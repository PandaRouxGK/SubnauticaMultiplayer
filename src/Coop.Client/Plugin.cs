using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using LiteNetLib;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Plugin d'instance CLIENT.
    ///
    /// Comme pour le serveur, le rôle vient de la ligne de commande (deux instances partagent
    /// le même BepInEx/config, un réglage de fichier ne pourrait pas les distinguer).
    /// </summary>
    [BepInPlugin(Guid, "Coop Client", "0.1.0")]
    public class CoopClientPlugin : BaseUnityPlugin
    {
        public const string Guid = "coop.subnautica.client";

        internal static ManualLogSource Log;
        internal static InstanceLogFile InstanceLog;

        private void Awake()
        {
            Log = Logger;

            var mode = LaunchModeParser.Current;
            Log.LogInfo($"[boot] CoopClient chargé — mode={mode} (attendu: {LaunchModeParser.ArgumentName}=client)");

            if (mode != LaunchMode.Client)
            {
                Log.LogInfo("[boot] cette instance n'est pas client : plugin client inactif.");
                return;
            }

            // Avant tout le reste : tout ce qui suit doit atterrir dans le log de cette instance,
            // et pas seulement dans le fichier partagé des deux processus.
            InstanceLog = InstanceLogFile.Attach(Log, "client");

            // La garde de sauvegarde est installée AVANT de charger le monde, et le chargement
            // est conditionné à son succès : deux instances qui écrivent la même partie la
            // corrompraient. En cas d'échec du patch on échoue donc **fermé** — le client reste
            // au menu, ce qui est réparable, plutôt que de risquer la sauvegarde du serveur.
            var saveGuardInstalled = SaveGuard.Install(Log);

            var host = Config.Bind("Reseau", "Hote", "127.0.0.1", "Adresse du serveur.");
            var port = Config.Bind("Reseau", "Port", Protocol.DefaultPort, "Port UDP du serveur.");
            var username = Config.Bind("Joueur", "Pseudo", Environment.UserName, "Pseudo envoyé au serveur.");

            var go = new GameObject("Coop.Client.Bootstrap");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var behaviour = go.AddComponent<ClientNetworkBehaviour>();
            behaviour.Configure(host.Value, port.Value, username.Value);

            if (saveGuardInstalled)
            {
                // Même composant que côté serveur (src/Shared) : le client entre dans le monde.
                go.AddComponent<AutoLoadBehaviour>();
            }
            else
            {
                Log.LogError(
                    "[boot] garde de sauvegarde NON installée -> chargement du monde annulé : " +
                    "ce client écrirait dans la sauvegarde du serveur.");
            }

            if (IsDebugFlagSet("saveguard"))
            {
                Log.LogWarning("[boot] autotest de la garde activé (-coopdebug=saveguard).");
                go.AddComponent<SaveGuardSelfTestBehaviour>();
            }

            Log.LogInfo($"[boot] GameObject 'Coop.Client.Bootstrap' créé (DontDestroyOnLoad, garde={saveGuardInstalled}).");
        }

        /// <summary>
        /// Vrai si <c>-coopdebug=&lt;nom&gt;</c> est présent sur la ligne de commande.
        /// Sert à n'activer les autotests que sur demande explicite.
        /// </summary>
        internal static bool IsDebugFlagSet(string name)
        {
            var expected = "-coopdebug=" + name;

            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Client réseau du jalon M1 : connexion, handshake, puis un Ping par seconde dont on mesure
    /// l'aller-retour. Aucune simulation ici : le but est de prouver que la liaison tient.
    ///
    /// Hébergé sur notre propre objet persistant, car le GameObject du manager BepInEx est détruit
    /// par Subnautica au chargement de son premier écran (constat du jalon M0).
    /// </summary>
    public class ClientNetworkBehaviour : MonoBehaviour
    {
        private const double ReconnectDelaySeconds = 5.0;
        private const double StatsIntervalSeconds = 5.0;
        private const int MaxTrackedPings = 64;

        private readonly PacketWriter _writer = new PacketWriter();
        private readonly Dictionary<long, long> _pingSentAt = new Dictionary<long, long>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private EventBasedNetListener _listener;
        private NetManager _net;
        private NetPeer _server;

        private string _host;
        private int _port;
        private string _username;
        private string _modListHash;
        private string _gameVersion;

        private bool _accepted;
        private long _sequence;
        private double _nextPingTime;
        private double _nextReconnectTime;
        private double _nextStatsTime;

        // Statistiques de ping.
        private int _pongsReceived;
        private long _lastRtt;
        private long _minRtt = long.MaxValue;
        private long _maxRtt;
        private long _rttSum;
        private long _lastServerTick;

        public void Configure(string host, int port, string username)
        {
            _host = host;
            _port = port;
            _username = string.IsNullOrWhiteSpace(username) ? "joueur" : username.Trim();
        }

        private void Start()
        {
            _gameVersion = Application.version;
            _modListHash = BepInExModList.ComputeHash();
            CoopClientPlugin.Log.LogInfo($"[net] jeu={_gameVersion}, modHash={_modListHash}, pseudo='{_username}'");

            _listener = new EventBasedNetListener();
            _net = new NetManager(_listener, null) { AutoRecycle = true, UnconnectedMessagesEnabled = false };

            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += (endPoint, error) =>
                CoopClientPlugin.Log.LogWarning($"[net] erreur socket depuis {endPoint} : {error}");

            if (!_net.Start())
            {
                CoopClientPlugin.Log.LogError("[net] impossible de démarrer le client LiteNetLib.");
                return;
            }

            Connect();
        }

        private void Connect()
        {
            CoopClientPlugin.Log.LogInfo($"[net] connexion à {_host}:{_port}...");
            _server = _net.Connect(_host, _port, Protocol.ConnectionKey);
            _nextReconnectTime = _clock.Elapsed.TotalSeconds + ReconnectDelaySeconds;
        }

        private void Update()
        {
            if (_net == null)
            {
                return;
            }

            _net.PollEvents();

            var now = _clock.Elapsed.TotalSeconds;

            if (_server == null || _server.ConnectionState != ConnectionState.Connected)
            {
                if (now >= _nextReconnectTime)
                {
                    // LiteNetLib gère ses propres tentatives ; on relance seulement si rien n'est en cours.
                    if (_server == null || _server.ConnectionState == ConnectionState.Disconnected)
                    {
                        Connect();
                    }
                }

                return;
            }

            if (_accepted && now >= _nextPingTime)
            {
                _nextPingTime = now + Protocol.PingIntervalMs / 1000.0;
                SendPing();
            }

            if (now >= _nextStatsTime)
            {
                _nextStatsTime = now + StatsIntervalSeconds;
                ReportStats();
            }
        }

        private void SendPing()
        {
            _sequence++;
            _pingSentAt[_sequence] = _clock.ElapsedMilliseconds;

            if (_pingSentAt.Count > MaxTrackedPings)
            {
                // Évite de faire grossir la table si un pong se perd définitivement.
                _pingSentAt.Clear();
                _pingSentAt[_sequence] = _clock.ElapsedMilliseconds;
            }

            _writer.Reset();
            new PingMessage { Sequence = _sequence, ClientTimeMs = _clock.ElapsedMilliseconds }.Write(_writer);
            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.Unreliable);
        }

        private void ReportStats()
        {
            if (!_accepted)
            {
                return;
            }

            var average = _pongsReceived > 0 ? _rttSum / _pongsReceived : 0;
            var minimum = _minRtt == long.MaxValue ? 0 : _minRtt;

            CoopClientPlugin.Log.LogInfo(
                $"[ping] pongs={_pongsReceived} rtt dernier={_lastRtt}ms min={minimum}ms moy={average}ms max={_maxRtt}ms " +
                $"dernierTickServeur={_lastServerTick}");
        }

        private void OnPeerConnected(NetPeer peer)
        {
            CoopClientPlugin.Log.LogInfo($"[net] connecté au serveur {peer} — envoi du handshake");
            SendHandshake();
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            CoopClientPlugin.Log.LogWarning($"[net] déconnecté du serveur (raison={info.Reason})");
            _accepted = false;
            _server = null;
            _pingSentAt.Clear();
            _nextReconnectTime = _clock.Elapsed.TotalSeconds + ReconnectDelaySeconds;
        }

        private void SendHandshake()
        {
            _writer.Reset();
            new HandshakeRequest
            {
                ProtocolVersion = Protocol.Version,
                GameVersion = _gameVersion,
                ModListHash = _modListHash,
                Username = _username,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
            CoopClientPlugin.Log.LogInfo($"[net] handshake envoyé (protocole v{Protocol.Version}, jeu={_gameVersion})");
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            try
            {
                var payload = reader.GetRemainingBytes();
                Handle(new PacketReader(payload));
            }
            catch (ProtocolException exception)
            {
                CoopClientPlugin.Log.LogWarning($"[net] paquet invalide du serveur : {exception.Message}");
            }
            catch (Exception exception)
            {
                CoopClientPlugin.Log.LogError($"[net] erreur en traitant un paquet : {exception}");
            }
            finally
            {
                reader.Recycle();
            }
        }

        private void Handle(PacketReader reader)
        {
            switch ((MessageType)reader.ReadByte())
            {
                case MessageType.HandshakeResponse:
                {
                    var response = HandshakeResponse.Read(reader);
                    if (!response.Accepted)
                    {
                        CoopClientPlugin.Log.LogError($"[net] handshake REFUSÉ par le serveur : {response.Reason}");
                        _accepted = false;
                        _server?.Disconnect();
                        return;
                    }

                    _accepted = true;
                    _nextPingTime = _clock.Elapsed.TotalSeconds;
                    CoopClientPlugin.Log.LogInfo($"[net] handshake ACCEPTÉ (tick serveur={response.ServerTick}) — début des pings");
                    break;
                }

                case MessageType.Pong:
                {
                    var pong = PongMessage.Read(reader);
                    var now = _clock.ElapsedMilliseconds;

                    if (!_pingSentAt.TryGetValue(pong.Sequence, out var sentAt))
                    {
                        // Pong sans ping correspondant : on l'ignore plutôt que d'inventer une mesure.
                        return;
                    }

                    _pingSentAt.Remove(pong.Sequence);

                    var rtt = now - sentAt;
                    _pongsReceived++;
                    _lastRtt = rtt;
                    _rttSum += rtt;
                    _maxRtt = Math.Max(_maxRtt, rtt);
                    _minRtt = Math.Min(_minRtt, rtt);
                    _lastServerTick = pong.ServerTick;

                    if (_pongsReceived <= 3)
                    {
                        CoopClientPlugin.Log.LogInfo($"[ping] seq={pong.Sequence} rtt={rtt}ms tickServeur={pong.ServerTick}");
                    }

                    break;
                }

                case MessageType.Disconnect:
                {
                    var message = DisconnectMessage.Read(reader);
                    CoopClientPlugin.Log.LogWarning($"[net] le serveur a fermé la connexion : {message.Reason}");
                    _accepted = false;
                    _server = null;
                    break;
                }

                default:
                    CoopClientPlugin.Log.LogWarning($"[net] message inattendu du serveur");
                    break;
            }
        }

        private void OnDestroy()
        {
            try
            {
                _net?.Stop();
            }
            catch (Exception exception)
            {
                CoopClientPlugin.Log.LogWarning($"[net] erreur à l'arrêt : {exception.Message}");
            }

            CoopClientPlugin.InstanceLog?.Dispose();
            CoopClientPlugin.InstanceLog = null;
        }
    }
}
