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

            // Le menu Échap d'un client ne doit pas geler la partie : le serveur continue de simuler,
            // et un client figé cesse d'envoyer ses positions. Non bloquant volontairement : un menu qui
            // met en pause est gênant, une garde de sauvegarde absente est dangereuse.
            var pauseGuardInstalled = ClientPauseGuard.Install(Log);

            // M5 : le ramassage d'un objet du monde demande l'accord du serveur. Non bloquant : si la
            // cible n'existe pas dans cette version du jeu, le joueur ramasse encore localement, et le
            // journal le dit — un ramassage non arbitré n'abîme rien.
            var pickupGuardInstalled = PickupGuard.Install(Log);

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

            if (IsDebugFlagSet("avatar"))
            {
                Log.LogWarning("[boot] sonde d'avatar activée (-coopdebug=avatar).");
                go.AddComponent<AvatarProbeBehaviour>();
            }

            if (IsDebugFlagSet("creature"))
            {
                // Elle a besoin du suivi des créatures, qui n'existe qu'après `Start` du client réseau :
                // la sonde attend donc qu'il soit là au lieu de le recevoir ici (elle lit
                // `ClientNetworkBehaviour.Current`).
                Log.LogWarning("[boot] sonde de créatures activée (-coopdebug=creature).");
                go.AddComponent<CreatureProbeBehaviour>();
            }

            if (IsDebugFlagSet("pause"))
            {
                Log.LogWarning("[boot] sonde de pause activée (-coopdebug=pause).");
                go.AddComponent<ClientPauseProbeBehaviour>();
            }

            if (IsDebugFlagSet("presence"))
            {
                // Même raison : elle lit le gestionnaire des avatars distants
                // (`ClientNetworkBehaviour.Current.Remotes`), qui n'existe qu'après `Start`.
                Log.LogWarning("[boot] sonde de présence activée (-coopdebug=presence).");
                go.AddComponent<PresenceProbeBehaviour>();
            }

            if (IsDebugFlagSet("vehicle"))
            {
                // Elle mesure l'interpolation du véhicule que l'hôte déplace (M4). Le même drapeau
                // active la sonde de l'hôte : c'est le couple qui produit la preuve de bout en bout.
                Log.LogWarning("[boot] sonde de véhicule cliente activée (-coopdebug=vehicle).");
                go.AddComponent<VehicleClientProbeBehaviour>();
            }

            if (IsDebugFlagSet("pickup"))
            {
                // M5 : la première action autoritaire. Les deux clients réclament la même clé ; le serveur
                // accorde au premier et refuse au second, et l'id falsifié est réécrit.
                Log.LogWarning("[boot] sonde de ramassage activée (-coopdebug=pickup).");
                go.AddComponent<PickupProbeBehaviour>();
            }

            Log.LogInfo(
                $"[boot] GameObject 'Coop.Client.Bootstrap' créé (DontDestroyOnLoad, garde={saveGuardInstalled}, " +
                $"pauseCliente={(pauseGuardInstalled ? "désactivée" : "NON patchée")}, " +
                $"ramassage={(pickupGuardInstalled ? "arbitré" : "NON patché")}).");
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
        private ushort _assignedPlayerId;
        private long _sequence;
        private uint _positionSequence;
        private double _nextPingTime;
        private double _nextPositionTime;
        private double _nextEquipmentCheckTime;
        private double _nextHeldItemCheckTime;
        private double _nextReconnectTime;
        private double _nextStatsTime;

        private RemotePlayerManager _remotes;

        /// <summary>
        /// Suivi des créatures que le serveur simule (M3). Créé au démarrage, jamais en cours de
        /// route : la seule chose qui change, ce sont les créatures qu'il adopte.
        /// </summary>
        private RemoteCreatureManager _creatures;

        /// <summary>
        /// Suivi des véhicules du serveur (M4) : registre des clés locales et interpolation entre deux
        /// annonces. Créé au démarrage, comme le suivi des créatures.
        /// </summary>
        private RemoteVehicleManager _vehicles;

        /// <summary>
        /// Instance en cours, pour que les sondes puissent lire l'état réel du suivi (M3) au lieu de
        /// recevoir une copie figée au démarrage.
        /// </summary>
        internal static ClientNetworkBehaviour Current { get; private set; }

        /// <summary>Suivi des créatures de cette instance ; <c>null</c> avant <see cref="Start"/>.</summary>
        internal RemoteCreatureManager Creatures => _creatures;

        /// <summary>Suivi des véhicules de cette instance ; <c>null</c> avant <see cref="Start"/>.</summary>
        internal RemoteVehicleManager Vehicles => _vehicles;

        /// <summary>
        /// Gestionnaire des avatars distants de cette instance ; <c>null</c> avant <see cref="Start"/>.
        ///
        /// Exposé pour la sonde de présence (<c>-coopdebug=presence</c>), qui doit exercer le chemin
        /// « couleur appliquée à un avatar **déjà instancié** » : l'ordre réel des messages fait toujours
        /// arriver la couleur avant l'avatar, donc cette branche ne s'exécuterait jamais autrement.
        /// </summary>
        internal RemotePlayerManager Remotes => _remotes;

        /// <summary>Id que le serveur a assigné à ce joueur (0 avant le handshake) — pour les sondes.</summary>
        internal ushort AssignedPlayerIdForProbe => _assignedPlayerId;

        /// <summary>Nombre d'annonces d'objet tenu réellement émises — pour la sonde.</summary>
        internal int HeldItemsSentForProbe => _heldItemsSent;

        /// <summary>
        /// Envoie un équipement en **revendiquant un id qui n'est peut-être pas le sien**.
        ///
        /// C'est la seule façon de mesurer la règle « le serveur force `PlayerId` » : tant qu'aucune sonde
        /// n'envoie un id étranger, l'affirmation se lit dans le code et rien ne la contredit. Le message
        /// part par le **vrai** chemin d'envoi (même tampon, même canal fiable) — seule la valeur du champ
        /// est falsifiée, et c'est exactement ce que la règle doit couvrir.
        /// </summary>
        internal bool ProbeSendEquipment(ushort claimedPlayerId, string[] types, string label)
        {
            if (_server == null || _server.ConnectionState != ConnectionState.Connected)
            {
                CoopClientPlugin.Log.LogWarning(
                    "[sonde] PlayerId falsifié : pas de connexion au serveur — le message n'a pas pu partir.");
                return false;
            }

            _writer.Reset();
            new PlayerEquipmentMessage
            {
                PlayerId = claimedPlayerId,
                TechTypeNames = types,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);

            var desc = types.Length == 0 ? "(vide)" : string.Join(", ", types);
            CoopClientPlugin.Log.LogWarning(
                $"[sonde] PlayerId falsifié : {label} — équipement annoncé sous l'id {claimedPlayerId} " +
                $"alors que le mien est {_assignedPlayerId} ({types.Length} pièce(s) : {desc}).");
            return true;
        }

        /// <summary>
        /// Renvoie l'équipement local **sans attendre un changement** : la sonde doit pouvoir remettre
        /// l'état réel après avoir falsifié l'id, sinon l'avatar de l'autre client garderait la liste de
        /// la falsification et la mesure suivante partirait d'un état faux.
        /// </summary>
        internal bool ProbeSendLocalEquipmentNow()
        {
            var items = RemoteAvatarEquipment.ReadLocalEquipment(out var failure, logResult: false);
            if (failure != null)
            {
                CoopClientPlugin.Log.LogWarning(
                    $"[sonde] PlayerId falsifié : équipement local illisible ({failure}) — pas de restauration.");
                return false;
            }

            var sorted = new List<string>(items).ToArray();
            Array.Sort(sorted, StringComparer.Ordinal);

            _lastLocalEquipmentSignature = string.Join("|", sorted);
            return ProbeSendEquipment(_assignedPlayerId, sorted, "restauration (id du serveur)");
        }

        private int _positionsSent;
        private int _snapshotsReceived;
        private int _equipmentsSent;
        private string _lastLocalEquipmentSignature;
        private int _heldItemsSent;

        // — Réclamations de ramassage (M5) : la décision du serveur et son résultat local —
        private readonly Dictionary<long, PickupResultMessage> _pickupResults = new Dictionary<long, PickupResultMessage>();
        private int _pickupGranted;
        private int _pickupDenied;
        private int _pickupForgeriesSent;

        /// <summary>Identifiant attribué par le serveur à ce client (<c>0</c> tant que le handshake n'est pas passé).</summary>
        internal ushort AssignedPlayerId => _assignedPlayerId;

        /// <summary>
        /// Réclame un objet au serveur (M5). Le message part par le vrai chemin d'envoi, avec un
        /// <c>PlayerId</c> **revendiqué** que le serveur ne recopie pas : c'est ce champ que la sonde
        /// falsifie pour vérifier que l'identité vient de la session.
        /// </summary>
        internal bool ProbeSendPickupClaim(long itemKey, string techType, ushort claimedPlayerId)
        {
            if (_server == null || _server.ConnectionState != ConnectionState.Connected)
            {
                CoopClientPlugin.Log.LogWarning(
                    "[sonde] ramassage : pas de connexion au serveur — la réclamation n'a pas pu partir.");
                return false;
            }

            _writer.Reset();
            new PickupClaimMessage
            {
                PlayerId = claimedPlayerId,
                ItemKey = itemKey,
                TechTypeName = techType,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);

            if (claimedPlayerId != 0 && claimedPlayerId != _assignedPlayerId)
            {
                _pickupForgeriesSent++;
            }

            CoopClientPlugin.Log.LogInfo(
                $"[pickup] réclamation envoyée : clé=0x{itemKey:x16} '{techType}' sous l'id " +
                $"{claimedPlayerId} (le mien est {_assignedPlayerId})");
            return true;
        }

        /// <summary>
        /// Réclame un objet **réel** au serveur (M5), sous l'identité de ce client. Le chemin est le
        /// même que <see cref="ProbeSendPickupClaim"/> : seule l'identité revendiquée change, et c'est
        /// justement ce que la sonde falsifie.
        /// </summary>
        internal bool SendPickupClaim(long itemKey, string techType) =>
            ProbeSendPickupClaim(itemKey, techType, _assignedPlayerId);

        /// <summary>Résultat reçu du serveur pour une clé, ou <c>false</c> si la décision n'est pas encore arrivée.</summary>
        internal bool TryGetPickupResult(long itemKey, out PickupResultMessage result) =>
            _pickupResults.TryGetValue(itemKey, out result);

        internal int PickupGrantedCount => _pickupGranted;
        internal int PickupDeniedCount => _pickupDenied;
        internal int PickupForgeriesSent => _pickupForgeriesSent;
        private string _lastHeldItemSignature;

        /// <summary>Couleur que le serveur a assignée à ce joueur (indice de palette, §27).</summary>
        private int _assignedColorIndex = -1;

        private int _creatureSnapshotsReceived;
        private int _creatureStatesReceived;
        private int _vehicleSnapshotsReceived;
        private int _vehicleStatesReceived;

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
            _remotes = new RemotePlayerManager(CoopClientPlugin.Log);
            _creatures = new RemoteCreatureManager(CoopClientPlugin.Log);
            _vehicles = new RemoteVehicleManager(CoopClientPlugin.Log);
            Current = this;
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

            if (_accepted && now >= _nextPositionTime)
            {
                _nextPositionTime = now + Protocol.PositionSendIntervalMs / 1000.0;
                SendLocalPositionIfReady();
            }

            if (_accepted && now >= _nextEquipmentCheckTime)
            {
                _nextEquipmentCheckTime = now + Protocol.EquipmentCheckIntervalMs / 1000.0;
                SendLocalEquipmentIfChanged();
            }

            if (_accepted && now >= _nextHeldItemCheckTime)
            {
                _nextHeldItemCheckTime = now + Protocol.HeldItemCheckIntervalMs / 1000.0;
                SendLocalHeldItemIfChanged();
            }

            _remotes?.UpdateAvatars();

            // Le suivi des créatures se fait à chaque frame : c'est lui qui relâche celles dont
            // l'état n'arrive plus (leur IA doit reprendre, pas rester figée).
            _creatures?.Update();

            // Les véhicules, eux, avancent par interpolation à chaque image : c'est ce qui remplace le
            // saut de 2,15 m par annonce par un glissement de ~0,33 m à 60 fps.
            _vehicles?.UpdateInterpolation();

            if (now >= _nextStatsTime)
            {
                _nextStatsTime = now + StatsIntervalSeconds;
                _creatures?.ReportPeriodically();
                _vehicles?.ReportPeriodically();
                ReportStats();
            }
        }

        private void SendLocalPositionIfReady()
        {
            var player = Player.main;
            if (player == null || Player.mainObject == null)
            {
                return;
            }

            var tr = Player.mainObject.transform;
            var pos = tr.position;

            // La rotation envoyée est le **lacet du regard**, pas celui de la racine du joueur : dans
            // Subnautica, `Player.mainObject` ne porte pas ce lacet (c'est la caméra qui le porte, et
            // `Player/body` le compense en local). Envoyer `tr.rotation` envoyait une orientation quasi
            // fixe ; l'avatar distant était alors orienté par le corps **du receveur**, donc tous les
            // corps tournaient avec la caméra de celui qui regarde. Mesuré en jeu, puis corrigé ici et
            // dans `RemoteAvatarFactory.ComputeBodyWorldPose`.
            var rot = ViewDirection.Yaw(tr);

            // Paramètres d'animation relevés dans ArmsController.SetPlayerSpeedParameters,
            // ArmsController.InstallAnimationRules et ArmsController.Update via tools/ApiDump.
            var arms = player.armsController;
            var vel = arms != null ? arms.GetSmoothedVelocity() : Vector3.zero;
            var pitch = MainCameraControl.main != null ? MainCameraControl.main.GetCameraPitch() : 0f;
            var isUnderwater = player.IsUnderwater() && player.motorMode != Player.MotorMode.Vehicle;
            var waterLevel = player.GetWaterLevel();
            var onSurface = pos.y > waterLevel - 1f && pos.y < waterLevel + 1f
                            && !player.IsInside() && player.IsUnderwaterForSwimming();

            // Drapeaux d'action : mêmes sources que ArmsController.Update, relevées dans l'IL du jeu.
            // Ils voyagent à 20 Hz (flux rapide) et non en fiable : un clic ne doit pas coûter
            // un aller-retour, et perdre un paquet n'a aucune conséquence (le suivant le corrige).
            var actionFlags = PlayerActionFlags.None;
            var pda = player.GetPDA();
            if (pda != null && pda.isInUse)
            {
                actionFlags |= PlayerActionFlags.UsingPda;
            }

            var guiHand = player.guiHand;
            if (guiHand != null)
            {
                if (guiHand.GetUsingTool())
                {
                    actionFlags |= PlayerActionFlags.UsingTool;
                }

                if (guiHand.GetAltAttacking())
                {
                    actionFlags |= PlayerActionFlags.UsingToolAlt;
                }
            }

            if (player.inSeamoth)
            {
                actionFlags |= PlayerActionFlags.InSeamoth;
            }

            if (player.inExosuit)
            {
                actionFlags |= PlayerActionFlags.InExosuit;
            }

            _positionSequence++;
            _positionsSent++;

            _writer.Reset();
            new PlayerPositionMessage
            {
                Sequence = _positionSequence,
                X = pos.x,
                Y = pos.y,
                Z = pos.z,
                RotX = rot.x,
                RotY = rot.y,
                RotZ = rot.z,
                RotW = rot.w,
                MoveSpeedX = vel.x,
                MoveSpeedY = vel.y,
                MoveSpeedZ = vel.z,
                ViewPitch = pitch,
                IsUnderwater = isUnderwater,
                OnSurface = onSurface,
                ActionFlags = actionFlags,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.Unreliable);

            if (_positionsSent == 1)
            {
                CoopClientPlugin.Log.LogInfo(
                    $"[presence] première position locale émise : seq={_positionSequence} pos={pos.ToString("F2")} " +
                    $"v=({vel.x:F1},{vel.y:F1},{vel.z:F1}) pitch={pitch:F1} sousEau={isUnderwater} surface={onSurface} " +
                    $"lacetVue={ViewDirection.YawDegrees(rot):F1}° lacetRacine={ViewDirection.YawDegrees(tr.rotation):F1}°");
            }
        }

        private void SendLocalEquipmentIfChanged()
        {
            if (Player.main == null || Player.mainObject == null)
            {
                return;
            }

            var items = RemoteAvatarEquipment.ReadLocalEquipment(out var failure, logResult: false);
            if (failure != null)
            {
                return;
            }

            var sorted = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                sorted[i] = items[i];
            }

            Array.Sort(sorted, StringComparer.Ordinal);
            var signature = string.Join("|", sorted);
            if (_lastLocalEquipmentSignature != null &&
                string.Equals(signature, _lastLocalEquipmentSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastLocalEquipmentSignature = signature;
            _equipmentsSent++;

            _writer.Reset();
            new PlayerEquipmentMessage
            {
                PlayerId = _assignedPlayerId,
                TechTypeNames = sorted,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);

            var desc = sorted.Length == 0 ? "(sans équipement)" : string.Join(", ", sorted);
            CoopClientPlugin.Log.LogInfo(
                $"[presence] équipement local émis (#{_equipmentsSent}, {sorted.Length} pièce(s)) : {desc}");
        }

        /// <summary>
        /// Émet l'objet tenu (outil en main) et les paramètres d'animation `holding_*` actifs.
        ///
        /// Les paramètres `holding_*` sont **lus sur l'`Animator` du joueur local**, pas re-dérivés :
        /// c'est le jeu qui les a posés pour l'outil équipé (`PlayerTool.animToolName` vaut
        /// `TechTypeExtensions.AsString(TechType, true)`, donc le paramètre s'appelle
        /// `holding_` + nom du `TechType`), et il n'y a donc rien à deviner ni table à maintenir.
        ///
        /// N'émet que sur changement : changer d'outil est rare, le paquet est fiable.
        /// </summary>
        private void SendLocalHeldItemIfChanged()
        {
            if (Player.main == null || Player.mainObject == null)
            {
                return;
            }

            var name = string.Empty;
            try
            {
                var inventory = Inventory.main;
                var tool = inventory != null ? inventory.GetHeldTool() : null;
                if (tool != null)
                {
                    var pickupable = tool.GetComponent<Pickupable>();
                    name = pickupable != null ? pickupable.GetTechType().ToString() : tool.gameObject.name;
                }
            }
            catch (Exception exception)
            {
                // La boucle réseau ne doit jamais mourir pour un outil illisible : on émet « rien ».
                CoopClientPlugin.Log.LogWarning($"[presence] objet tenu illisible ({exception.GetType().Name}) : rien envoyé.");
                return;
            }

            var holding = new List<string>();
            var animator = Player.mainObject.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                var parameters = animator.parameters;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    if (parameter.type != AnimatorControllerParameterType.Bool ||
                        !parameter.name.StartsWith(Protocol.HoldingParamPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (animator.GetBool(parameter.name))
                    {
                        holding.Add(parameter.name);
                    }
                }
            }

            holding.Sort(StringComparer.Ordinal);
            var signature = name + "|" + string.Join("|", holding);
            if (_lastHeldItemSignature != null &&
                string.Equals(signature, _lastHeldItemSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastHeldItemSignature = signature;
            _heldItemsSent++;

            _writer.Reset();
            new PlayerHeldItemMessage
            {
                PlayerId = _assignedPlayerId,
                TechTypeName = name,
                HoldingParams = holding.ToArray(),
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);

            var held = name.Length == 0 ? "(rien en main)" : name;
            var paramsText = holding.Count == 0 ? "(aucun)" : string.Join(", ", holding);
            CoopClientPlugin.Log.LogInfo(
                $"[presence] objet tenu émis (#{_heldItemsSent}) : '{held}' holding=[{paramsText}]");
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
                $"[ping] id={_assignedPlayerId} pongs={_pongsReceived} rtt dernier={_lastRtt}ms min={minimum}ms moy={average}ms max={_maxRtt}ms " +
                $"dernierTickServeur={_lastServerTick} posEnvoyees={_positionsSent} equipEnvoyes={_equipmentsSent} outilsEnvoyes={_heldItemsSent} snapshotsRecus={_snapshotsReceived} " +
                $"joueursDistants={_remotes?.RemoteCount ?? 0} avatarsActifs={_remotes?.SpawnedAvatarCount ?? 0} " +
                $"couleurPerso={_assignedColorIndex} snapshotsCreatures={_creatureSnapshotsReceived} etatsCreatures={_creatureStatesReceived} " +
                $"snapshotsVehicules={_vehicleSnapshotsReceived} etatsVehicules={_vehicleStatesReceived} " +
                $"vehicules={(_vehicles != null ? _vehicles.Describe() : "(aucun suivi)")} " +
                $"creatures={(_creatures != null ? _creatures.Describe() : "(aucun suivi)")}");
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
            _assignedPlayerId = 0;
            _lastLocalEquipmentSignature = null;
            _lastHeldItemSignature = null;
            _server = null;
            _pingSentAt.Clear();
            _remotes?.ClearAll();
            _vehicles?.ReleaseAll("déconnexion du serveur");
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
                    _assignedPlayerId = response.AssignedPlayerId;
                    _lastLocalEquipmentSignature = null;
                    _lastHeldItemSignature = null;
                    _assignedColorIndex = -1;
                    _nextPingTime = _clock.Elapsed.TotalSeconds;
                    _nextPositionTime = _clock.Elapsed.TotalSeconds;
                    _nextEquipmentCheckTime = _clock.Elapsed.TotalSeconds;
                    _nextHeldItemCheckTime = _clock.Elapsed.TotalSeconds;
                    CoopClientPlugin.Log.LogInfo(
                        $"[net] handshake ACCEPTÉ (id={response.AssignedPlayerId}, tick serveur={response.ServerTick}) — début des pings et positions");
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
                    _assignedPlayerId = 0;
                    _server = null;
                    _remotes?.ClearAll();

                    // Un serveur qui disparaît ne doit pas laisser le monde du client à moitié figé :
                    // les créatures qu'il simulait pour nous reprennent leur IA ici.
                    _creatures?.ReleaseAll("le serveur a fermé la connexion");
                    _vehicles?.ReleaseAll("le serveur a fermé la connexion");
                    break;
                }

                case MessageType.PlayerJoined:
                {
                    var joined = PlayerJoinedMessage.Read(reader);
                    if (joined.PlayerId != _assignedPlayerId)
                    {
                        _remotes?.OnPlayerJoined(joined);
                    }

                    break;
                }

                case MessageType.PlayerLeft:
                {
                    var left = PlayerLeftMessage.Read(reader);
                    _remotes?.OnPlayerLeft(left);
                    break;
                }

                case MessageType.PlayersSnapshot:
                {
                    var snapshot = PlayersSnapshotMessage.Read(reader);
                    _snapshotsReceived++;
                    _lastServerTick = snapshot.ServerTick;
                    _remotes?.OnSnapshot(snapshot);
                    break;
                }

                case MessageType.PlayerEquipment:
                {
                    var equip = PlayerEquipmentMessage.Read(reader);
                    if (equip.PlayerId != _assignedPlayerId)
                    {
                        _remotes?.OnPlayerEquipment(equip);
                    }

                    break;
                }

                case MessageType.PlayerHeldItem:
                {
                    var held = PlayerHeldItemMessage.Read(reader);
                    if (held.PlayerId != _assignedPlayerId)
                    {
                        _remotes?.OnPlayerHeldItem(held);
                    }

                    break;
                }

                case MessageType.PlayerColor:
                {
                    var color = PlayerColorMessage.Read(reader);
                    if (color.PlayerId == _assignedPlayerId)
                    {
                        // Sa propre couleur : elle ne sert pas à grand-chose tant qu'on ne colore que
                        // les avatars distants, mais la connaître évite de croire à une couleur
                        // manquante quand c'est la sienne qui a été attribuée.
                        _assignedColorIndex = color.ColorIndex;
                        Protocol.PlayerColorAt(color.ColorIndex, out var sr, out var sg, out var sb);
                        CoopClientPlugin.Log.LogInfo(
                            $"[presence] couleur assignée à ce joueur : indice {color.ColorIndex} rgb({sr},{sg},{sb})");
                    }
                    else
                    {
                        _remotes?.OnPlayerColor(color);
                    }

                    break;
                }

                case MessageType.CreatureSnapshot:
                {
                    var creatures = CreatureSnapshotMessage.Read(reader);
                    _creatureSnapshotsReceived++;
                    _creatureStatesReceived += creatures.Creatures != null ? creatures.Creatures.Length : 0;
                    _creatures?.OnSnapshot(creatures);
                    break;
                }

                case MessageType.VehicleSnapshot:
                {
                    var vehicles = VehicleSnapshotMessage.Read(reader);
                    _vehicleSnapshotsReceived++;
                    _vehicleStatesReceived += vehicles.Vehicles != null ? vehicles.Vehicles.Length : 0;
                    _vehicles?.OnSnapshot(vehicles);
                    break;
                }

                case MessageType.PickupResult:
                {
                    var result = PickupResultMessage.Read(reader);
                    _pickupResults[result.ItemKey] = result;
                    if (result.Granted)
                    {
                        _pickupGranted++;
                    }
                    else
                    {
                        _pickupDenied++;
                    }

                    CoopClientPlugin.Log.LogInfo(
                        $"[pickup] résultat : clé=0x{result.ItemKey:x16} '{result.TechTypeName}' " +
                        $"accordée={result.Granted} tenue par {result.HolderId} " +
                        $"(revendiquée sous l'id {result.ClaimedBy})");

                    // Le refus est **appliqué** ici : un objet ramassé optimistement repart dans le
                    // monde. Sans cet appel, la décision du serveur ne serait qu'un message.
                    PickupGuard.OnResult(result);
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
                _remotes?.ClearAll();
                _creatures?.ReleaseAll("arrêt du client");
                _vehicles?.ReleaseAll("arrêt du client");
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
