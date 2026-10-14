using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Coop.Model;
using LiteNetLib;

namespace Coop.Server
{
    /// <summary>
    /// Serveur réseau du jalon M1 : accepte les connexions, valide le handshake et répond aux pings.
    ///
    /// Répartition des responsabilités :
    ///  - LiteNetLib gère le transport (UDP, fiabilité, fragmentation) ;
    ///  - <see cref="Coop.Model"/> gère le format des messages ;
    ///  - cette classe ne fait que la logique d'admission et le routage.
    ///
    /// Les callbacks sont invoqués depuis <see cref="Poll"/>, donc sur le thread principal :
    /// aucun état partagé n'a besoin d'être verrouillé tant que Poll n'est appelé que là.
    /// </summary>
    public sealed class ServerNetwork : IDisposable
    {
        private sealed class ClientSession
        {
            public string Username;
            public bool Accepted;
            public long PingsReceived;

            /// <summary>Instant (ms) au-delà duquel un peer qui n'a pas fini son handshake est éjecté.</summary>
            public long HandshakeDeadline;
        }

        private readonly NetManager _net;
        private readonly EventBasedNetListener _listener;
        private readonly ManualLogSource _log;
        private readonly string _gameVersion;
        private readonly string _modListHash;
        private readonly Dictionary<NetPeer, ClientSession> _sessions = new Dictionary<NetPeer, ClientSession>();
        private readonly PacketWriter _writer = new PacketWriter();

        public ServerNetwork(string gameVersion, string modListHash, ManualLogSource log)
        {
            _log = log;
            _gameVersion = gameVersion;
            _modListHash = modListHash;

            _listener = new EventBasedNetListener();
            // LiteNetLib n'expose pas de constructeur à un seul argument : on passe null
            // pour la couche de paquets supplémentaire (paramètre optionnel à l'origine).
            _net = new NetManager(_listener, null)
            {
                AutoRecycle = true,
                UnconnectedMessagesEnabled = false,
                NatPunchEnabled = false,
            };

            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += OnNetworkError;
        }

        public int Port { get; private set; }

        public int AcceptedClients
        {
            get
            {
                var count = 0;
                foreach (var session in _sessions.Values)
                {
                    if (session.Accepted)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public bool Start(int port)
        {
            if (!_net.Start(port))
            {
                _log.LogError($"[net] impossible d'ouvrir le port UDP {port} (déjà utilisé ?)");
                return false;
            }

            Port = port;
            // Le port effectivement lié est journalisé : c'est ce qui permet de détecter
            // tout de suite un socket fermé ou rebindé ailleurs.
            _log.LogInfo($"[net] serveur à l'écoute sur UDP {port} (port lié={_net.LocalPort}, actif={_net.IsRunning}) — protocole v{Protocol.Version}, modHash={_modListHash}");
            return true;
        }

        /// <summary>À appeler à chaque frame : c'est ici que LiteNetLib traite les paquets reçus.</summary>
        public void Poll()
        {
            _net.PollEvents();
            ExpireStalledHandshakes();
        }

        /// <summary>
        /// Éjecte les peers qui n'ont jamais terminé leur handshake. Indispensable : puisque le
        /// serveur ne coupe plus la connexion juste après un refus (sinon le motif envoyé en
        /// fiable serait perdu), il faut une borne pour qu'une connexion muette ne reste pas ouverte.
        /// </summary>
        private void ExpireStalledHandshakes()
        {
            if (_sessions.Count == 0)
            {
                return;
            }

            var now = ServerClock.Milliseconds;
            List<NetPeer> stalled = null;

            foreach (var pair in _sessions)
            {
                if (pair.Value.Accepted || now < pair.Value.HandshakeDeadline)
                {
                    continue;
                }

                stalled = stalled ?? new List<NetPeer>();
                stalled.Add(pair.Key);
            }

            if (stalled == null)
            {
                return;
            }

            foreach (var peer in stalled)
            {
                _log.LogWarning($"[net] handshake non terminé après {Protocol.HandshakeTimeoutSeconds}s : {peer} éjecté");
                peer.Disconnect();
            }
        }

        public void Dispose()
        {
            try
            {
                _log.LogInfo("[net] fermeture du socket serveur.");
                _net.Stop();
            }
            catch (Exception exception)
            {
                _log.LogWarning($"[net] erreur à l'arrêt : {exception.Message}");
            }

            _sessions.Clear();
        }

        // ---- Callbacks de connexion -------------------------------------------------------

        private void OnConnectionRequest(ConnectionRequest request)
        {
            // La clé filtre les paquets parasites avant même d'allouer une session.
            var peer = request.AcceptIfKey(Protocol.ConnectionKey);
            if (peer == null)
            {
                _log.LogWarning($"[net] connexion refusée (clé invalide) depuis {request.RemoteEndPoint}");
                return;
            }

            _log.LogInfo($"[net] demande de connexion acceptée depuis {request.RemoteEndPoint}");
        }

        private void OnPeerConnected(NetPeer peer)
        {
            _sessions[peer] = new ClientSession
            {
                HandshakeDeadline = ServerClock.Milliseconds + Protocol.HandshakeTimeoutSeconds * 1000L,
            };

            _log.LogInfo($"[net] peer id={peer.Id} connecté ({peer}) — attente du handshake");
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            var known = _sessions.TryGetValue(peer, out var session);
            _sessions.Remove(peer);

            var who = known && session.Username != null ? session.Username : peer.ToString();
            _log.LogInfo($"[net] '{who}' déconnecté (raison={info.Reason}, pings={session?.PingsReceived ?? 0})");
        }

        private void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError error)
            => _log.LogWarning($"[net] erreur socket depuis {endPoint} : {error}");

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            try
            {
                // LiteNetLib prête un reader recyclable : il ne survit pas à ce callback,
                // donc les octets sont d'abord copiés dans notre propre reader.
                var payload = reader.GetRemainingBytes();
                Handle(peer, new PacketReader(payload));
            }
            catch (ProtocolException exception)
            {
                _log.LogWarning($"[net] paquet invalide de {peer} : {exception.Message}");
                _sessions.Remove(peer);
                peer.Disconnect();
            }
            catch (Exception exception)
            {
                _log.LogError($"[net] erreur inattendue en traitant un paquet de {peer} : {exception}");
            }
            finally
            {
                reader.Recycle();
            }
        }

        // ---- Logique de protocole ---------------------------------------------------------

        private void Handle(NetPeer peer, PacketReader reader)
        {
            if (!_sessions.TryGetValue(peer, out var session))
            {
                // Un paquet d'un peer inconnu est anormal : on ne crée pas de session à la volée.
                _log.LogWarning($"[net] paquet reçu d'un peer non enregistré id={peer.Id}");
                return;
            }

            var type = (MessageType)reader.ReadByte();
            switch (type)
            {
                case MessageType.HandshakeRequest:
                    HandleHandshake(peer, session, HandshakeRequest.Read(reader));
                    break;

                case MessageType.Ping:
                    HandlePing(peer, session, PingMessage.Read(reader));
                    break;

                default:
                    _log.LogWarning($"[net] message inattendu {type} de {peer} (handshake accepté={session.Accepted})");
                    break;
            }
        }

        private void HandleHandshake(NetPeer peer, ClientSession session, HandshakeRequest request)
        {
            if (session.Accepted)
            {
                _log.LogWarning($"[net] handshake en double ignoré de {peer}");
                return;
            }

            var reason = Validate(request);
            if (reason != null)
            {
                _log.LogWarning($"[net] handshake REFUSÉ pour '{request.Username}' : {reason}");
                // On envoie le motif en fiable et on LAISSE le client fermer. Couper juste après
                // l'envoi faisait perdre le motif (la coupure dépassait le message) : le client
                // ne voyait plus qu'un « RemoteConnectionClose » sans explication.
                Send(peer, w => new HandshakeResponse { Accepted = false, Reason = reason, ServerTick = ServerTickBehaviour.Ticks }.Write(w), DeliveryMethod.ReliableOrdered);
                session.HandshakeDeadline = ServerClock.Milliseconds + Protocol.HandshakeTimeoutSeconds * 1000L;
                return;
            }

            session.Username = request.Username;
            session.Accepted = true;

            _log.LogInfo(
                $"[net] handshake ACCEPTÉ : '{request.Username}' (jeu={request.GameVersion}, modHash={request.ModListHash}) " +
                $"depuis {peer} — {AcceptedClients}/{Protocol.MaxClients} client(s)");

            Send(peer, w => new HandshakeResponse { Accepted = true, Reason = string.Empty, ServerTick = ServerTickBehaviour.Ticks }.Write(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Retourne la raison du refus, ou <c>null</c> si la connexion est valide.</summary>
        private string Validate(HandshakeRequest request)
        {
            if (request.ProtocolVersion != Protocol.Version)
            {
                return $"version de protocole incompatible (client={request.ProtocolVersion}, serveur={Protocol.Version})";
            }

            if (AcceptedClients >= Protocol.MaxClients)
            {
                return $"serveur plein ({Protocol.MaxClients} clients)";
            }

            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return "pseudo vide";
            }

            if (request.Username.Length > Protocol.MaxUsernameLength)
            {
                return $"pseudo trop long ({request.Username.Length} > {Protocol.MaxUsernameLength})";
            }

            if (!string.Equals(request.ModListHash, _modListHash, StringComparison.Ordinal))
            {
                return $"liste de mods différente (client={request.ModListHash}, serveur={_modListHash})";
            }

            if (!string.Equals(request.GameVersion, _gameVersion, StringComparison.Ordinal))
            {
                return $"version du jeu différente (client={request.GameVersion}, serveur={_gameVersion})";
            }

            return null;
        }

        private void HandlePing(NetPeer peer, ClientSession session, PingMessage ping)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] ping avant handshake ignoré de {peer}");
                return;
            }

            session.PingsReceived++;

            Send(peer, w => new PongMessage
            {
                Sequence = ping.Sequence,
                ClientTimeMs = ping.ClientTimeMs,
                ServerTick = ServerTickBehaviour.Ticks,
                ServerTimeMs = ServerClock.Milliseconds,
            }.Write(w), DeliveryMethod.Unreliable);
        }

        private void Send(NetPeer peer, Action<PacketWriter> write, DeliveryMethod method)
        {
            _writer.Reset();
            write(_writer);
            peer.Send(_writer.BackingBuffer, 0, _writer.Length, method);
        }
    }

    /// <summary>Horloge monotone du serveur, exprimée en millisecondes depuis le démarrage.</summary>
    internal static class ServerClock
    {
        private static readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();

        public static long Milliseconds => Watch.ElapsedMilliseconds;
    }
}
