using System;
using System.Collections.Generic;
using BepInEx.Logging;
using System.Linq;
using Coop.Model;
using Coop.Shared;
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
            public ushort PlayerId;
            public string Username;
            public bool Accepted;
            public long PingsReceived;
            public long PositionsReceived;
            public long EquipmentsReceived;
            public long HeldItemsReceived;

            // Dernier équipement connu du joueur (noms de TechType).
            public string[] Equipment;

            // Dernier objet tenu connu du joueur : null = jamais annoncé, chaîne vide = rien en main.
            public string HeldItemName;

            // Paramètres d'animation `holding_*` que l'émetteur a annoncés comme actifs.
            public string[] HoldingParams;

            // Dernier état de position reçu du joueur (ou position de spawn initiale).
            public uint LastPositionSequence;
            public float X;
            public float Y;
            public float Z;
            public float RotX;
            public float RotY;
            public float RotZ;
            public float RotW = 1f;
            public float MoveSpeedX;
            public float MoveSpeedY;
            public float MoveSpeedZ;
            public float ViewPitch;
            public bool IsUnderwater;
            public bool OnSurface;

            // Dernières actions instantanées reçues (PDA, outil, véhicule).
            public PlayerActionFlags ActionFlags;

            /// <summary>Couleur assignée à ce joueur par le serveur (indice dans la palette).</summary>
            public byte ColorIndex;

            // — Débit de créatures vers ce client, cumulé depuis le dernier rapport (5 s) ————
            //
            // Compté **au moment de l'envoi**, avec la taille réelle du paquet : c'est la seule façon de
            // chiffrer ce que le relevancy et le plafond coûtent vraiment, plutôt que de multiplier des
            // constantes. Remis à zéro après chaque rapport.
            public long CreatureBytesSent;
            public long CreatureStatesSent;
            public long CreaturePacketsSent;

            // — Débit de véhicules vers ce client, cumulé depuis le dernier rapport (5 s) ————
            public long VehicleBytesSent;
            public long VehicleStatesSent;
            public long VehiclePacketsSent;

            /// <summary>Instant du prochain rapport de véhicules **pour ce client** (même raison que les créatures).</summary>
            public float NextVehicleReport;

            /// <summary>
            /// Instant du prochain rapport de créatures **pour ce client**.
            ///
            /// Le rapport était auparavant gardé par un unique compteur global : le premier client de la
            /// boucle le faisait avancer, et le second ne recevait donc jamais son rapport. Avec un débit
            /// à mesurer par client, ça ne pouvait plus durer.
            /// </summary>
            public float NextCreatureReport;

            /// <summary>Instant (ms) au-delà duquel un peer qui n'a pas fini son handshake est éjecté.</summary>
            public long HandshakeDeadline;

            // — Empreinte de sauvegarde (P0, v13) ————————————————————————————————
            //
            // `SavePending` est le cas de démarrage : le réseau écoute avant qu'AutoLoad n'ait résolu la
            // partie (côté serveur comme côté client). L'empreinte est alors vérifiée au tick suivant,
            // au lieu de refuser un client correct sur une simple course de démarrage.
            public SaveIdentity Save;
            public bool SavePending;
            public bool SaveMismatch;

            // — Position acceptée et plausibilité (P1, v13) ————————————————————————
            //
            // La session garde **deux** états : la dernière position acceptée (qui est diffusée aux
            // autres) et la dernière position reçue (qui peut avoir été refusée). Les confondre
            // reviendrait à diffuser un saut qu'on vient de refuser.
            public bool HasAcceptedPosition;
            public float LastAcceptedX;
            public float LastAcceptedY;
            public float LastAcceptedZ;
            public float LastAcceptedAt;
            public PlayerActionFlags LastAcceptedFlags;
            public int RejectedStreak;
            public long PositionsRejected;
            public long PositionResyncs;
            public float NextPositionReportAt;

            // Fenêtre de resynchronisations (P1). Une téléportation légitime est rare ; une session qui
            // resynchronise en boucle ne joue pas, elle saute. Le budget rend la garde opposable au
            // lieu de laisser un client se téléporter toutes les 150 ms.
            public float ResyncWindowStart = -1f;
            public int ResyncsInWindow;
        }

        private readonly NetManager _net;
        private readonly EventBasedNetListener _listener;
        private readonly ManualLogSource _log;
        private readonly string _gameVersion;
        private readonly string _modListHash;
        private readonly Dictionary<NetPeer, ClientSession> _sessions = new Dictionary<NetPeer, ClientSession>();
        private readonly PacketWriter _writer = new PacketWriter();

        /// <summary>
        /// Compteur réutilisé des clés de créatures, pour relever les clés **partagées par deux
        /// créatures** côté serveur.
        ///
        /// Le client a le même compteur sur son propre registre ; les deux mesures sont nécessaires, parce
        /// que les deux ensembles ne sont pas les mêmes (le serveur voit tout son monde, le client ne voit
        /// que ses cellules chargées). Une clé partagée ici veut dire que l'identité n'est pas injective —
        /// et c'est la seule hypothèse sur laquelle repose tout le suivi.
        /// </summary>
        private readonly Dictionary<long, int> _creatureKeyTally = new Dictionary<long, int>();

        /// <summary>
        /// Dernière position **envoyée** pour chaque clé de créature, pour mesurer le déplacement du serveur
        /// entre deux diffusions.
        ///
        /// À quoi ça sert : le client mesure l'écart entre sa copie et la position annoncée, et ce chiffre
        /// seul est ambigu — un serveur qui va vite ressemble à un client qui traîne (mesuré au run du
        /// 2026-10-17 : `1.69m` en `0.10s`, soit 16,9 m/s pour un `Peeper` qui nage à 5). En relevant le
        /// déplacement **côté serveur**, les deux nombres se comparent : si le serveur a lui-même bougé de
        /// 1,6 m, l'écart du client ne dit rien contre la synchronisation.
        /// </summary>
        private readonly Dictionary<long, SentPosition> _lastSentPositions = new Dictionary<long, SentPosition>();

        private struct SentPosition
        {
            public UnityEngine.Vector3 Position;
            public float Realtime;
        }

        /// <summary>
        /// Distance (m) en deçà de laquelle une créature sacrifiée au plafond n'est plus un compromis
        /// acceptable : elle est **sous les yeux du joueur**. La mesure de référence (2026-10-17) : 19 à 20
        /// créatures sacrifiées sur 83 candidates, la plus proche bien au-delà — le plafond mordait alors
        /// le bout du rayon, ce qui est le comportement voulu.
        /// </summary>
        private const float CloseSacrificeMeters = 30f;

        /// <summary>Période du rapport de taux de refus de position (P1), par session.</summary>
        private const float PositionReportSeconds = 5f;

        private int _closeSacrificeWarnings;

        /// <summary>
        /// Paquets de créatures par tick et par client, tel que le protocole le fixe — **surchargeable par
        /// `COOP_CREATURE_CHUNKS`**.
        ///
        /// Pourquoi une surcharge pour une constante : la garde de sacrifice (« une créature non diffusée à
        /// moins de `CloseSacrificeMeters` doit être signalée ») n'a jamais pu s'exercer, parce que dans nos
        /// zones la plus proche sacrifiée était à 68–72 m. Or un contrôle qu'on n'a jamais vu broncher n'est
        /// pas un contrôle : en abaissant le plafond le temps d'un run, l'avertissement **doit** sortir. Un
        /// test qu'on ne peut pas déclencher ne prouve rien.
        /// </summary>
        private static readonly int CreatureChunksPerTick = ParseChunksOverride();

        /// <summary>Vrai si le plafond de diffusion vient de la variable d'environnement et non du protocole.</summary>
        private static readonly bool CreatureChunksOverridden =
            CreatureChunksPerTick != Protocol.MaxCreatureChunksPerTick;

        private static int ParseChunksOverride()
        {
            var raw = Environment.GetEnvironmentVariable("COOP_CREATURE_CHUNKS");
            if (int.TryParse(raw, out var value) && value >= 1)
            {
                return value;
            }

            return Protocol.MaxCreatureChunksPerTick;
        }

        private ushort _nextPlayerId;
        private long _snapshotsBroadcast;

        /// <summary>
        /// Nombre de messages dont le <c>PlayerId</c> annoncé ne correspondait pas à la session qui les a
        /// envoyés — tous réécrits avec l'id de la session. Ce compteur existe pour que la règle
        /// « le serveur force <c>PlayerId</c> » soit **mesurée** (une sonde envoie un id étranger) et non
        /// seulement lue dans le code.
        /// </summary>
        public int ForgedPlayerIds { get; private set; }

        /// <summary>
        /// Objet du monde → joueur qui l'a réclamé le premier (M5). C'est la **décision** du serveur :
        /// une clé déjà présente ne peut plus être accordée à quelqu'un d'autre. Le registre vit aussi
        /// longtemps que la session serveur — un objet ramassé ne réapparaît pas — et libère les clés
        /// d'un joueur qui part (<see cref="PickupClaimLedger.Release"/>), sinon elles resteraient
        /// tenues au nom d'un joueur qui n'existe plus.
        /// </summary>
        private readonly PickupClaimLedger _pickupClaims = new PickupClaimLedger();

        /// <summary>Clés d'objets **déjà retirées** du monde simulé après un ramassage accordé (v11).</summary>
        private readonly HashSet<long> _pickupRemoved = new HashSet<long>();

        public int PickupClaimsGranted { get; private set; }

        public int PickupClaimsRefused { get; private set; }

        /// <summary>Réclamations dont le <c>PlayerId</c> annoncé n'était pas celui de la session.</summary>
        public int ForgedPickupIds { get; private set; }

        /// <summary>Réclamations refusées parce que la clé ne correspond à aucun objet du monde serveur.</summary>
        public int PickupRefusedUnknown { get; private set; }

        /// <summary>Réclamations refusées parce que le joueur est trop loin de l'objet.</summary>
        public int PickupRefusedRange { get; private set; }

        /// <summary>Réclamations refusées faute de position connue pour le joueur.</summary>
        public int PickupRefusedNoPosition { get; private set; }

        // — Conduite autoritaire (v9, M4) ——————————————————————————————————————

        /// <summary>Véhicule → joueur qui le conduit. Le premier qui prend la main la garde jusqu'à libération.</summary>
        private readonly Dictionary<long, ushort> _vehicleDrivers = new Dictionary<long, ushort>();

        /// <summary>Dernier état reçu par véhicule conduit : sert à libérer un volant devenu silencieux (v12).</summary>
        private readonly Dictionary<long, float> _vehicleDriverLastState = new Dictionary<long, float>();

        /// <summary>Volants libérés par leur conducteur / par silence — comptés, pour le dire au journal.</summary>
        public int VehicleReleases { get; private set; }

        public int VehicleSilenceReleases { get; private set; }

        /// <summary>Index clé → véhicule du monde serveur, reconstruit au plus une fois par seconde.</summary>
        private Dictionary<long, Vehicle> _vehicleIndex;

        private float _vehicleIndexBuiltAt = -1f;
        private int _vehicleIndexCount;

        /// <summary>Refus déjà journalisés (une fois par clé) : à 10 Hz, un refus répété noierait le journal.</summary>
        private readonly HashSet<long> _controlRefusalsLogged = new HashSet<long>();

        private readonly Dictionary<long, float> _lastControlReplyAt = new Dictionary<long, float>();

        public int VehicleControlApplied { get; private set; }
        public int VehicleControlRefusedUnknown { get; private set; }
        public int VehicleControlRefusedRange { get; private set; }
        public int VehicleControlRefusedTaken { get; private set; }

        /// <summary>Conduites dont le <c>PlayerId</c> annoncé n'était pas celui de la session.</summary>
        public int ForgedVehicleIds { get; private set; }

        /// <summary>
        /// Index clé → position des objets ramassables du monde, reconstruit **au plus une fois par
        /// seconde** : un ramassage est rare, mais un index refait à chaque réclamation coûterait un
        /// parcours complet du monde par clic.
        /// </summary>
        private Dictionary<long, UnityEngine.Vector3> _itemIndex;

        private float _itemIndexBuiltAt = -1f;
        private int _itemIndexObjects;
        private int _itemIndexSharedKeys;

        private string[] _lastHostEquipment;
        private string _lastHostEquipmentSignature;
        private string _lastHostHeldItemSignature;

        /// <summary>
        /// Indice de couleur distribué au prochain client. Avance à chaque admission, donc deux
        /// joueurs connectés ne peuvent pas recevoir le même tant qu'ils sont au plus
        /// <see cref="Protocol.PlayerColorCount"/>. Au-delà, la palette tourne — c'est dit au §27.
        /// </summary>
        private byte _nextClientColorIndex;

        /// <summary>
        /// Couleur du personnage de l'instance serveur. Fixe : c'est le repère visuel stable quand
        /// un client rejoint, et il faut de toute façon lui réserver un indice pour ne pas le
        /// distribuer à un client.
        /// </summary>
        private const byte HostColorIndex = 0;

        // — Créatures (M3) ————————————————————————————————————————————————————

        /// <summary>
        /// Cache des créatures du monde. `FindObjectsOfType` parcourt toute la scène : à 10 Hz ce
        /// serait absurde, alors qu'une créature apparaît ou disparaît rarement. Le cache est
        /// rafraîchi toutes les <see cref="CreatureCacheSeconds"/> et suffit largement pour un
        /// relevancy à 120 m.
        /// </summary>
        private Creature[] _creatureCache;
        private float _creatureCacheRealtime = float.NegativeInfinity;
        private const float CreatureCacheSeconds = 2f;

        private long _creatureSnapshotsBroadcast;
        private long _creatureStatesBroadcast;

        /// <summary>
        /// Paquets de créatures refusés parce qu'ils dépassaient la taille acceptée par LiteNetLib.
        ///
        /// Ce compteur existe parce que ce cas est **invisible** autrement : LiteNetLib jette le
        /// paquet en écrivant dans le journal Unity, pas dans le nôtre. Le premier envoi de créatures
        /// est tombé pile dans ce trou.
        /// </summary>
        private long _oversizedCreaturePackets;
        private string _lastCreatureIdentityReport;

        /// <summary>Prochain instant (s) où un résumé des créatures doit être journalisé.</summary>
        /// <summary>
        /// Intervalle (s) du rapport de créatures, **par client** : c'est la fenêtre sur laquelle le débit
        /// envoyé est cumulé puis remis à zéro. La même valeur sert de diviseur au débit, donc elle doit
        /// rester la durée réellement écoulée — d'où une constante et non un littéral recopié.
        /// </summary>
        private const float CreatureReportSeconds = 5f;

        /// <summary>
        /// Prochain instant (s) du relevé de distance. Séparé du résumé d'envoi : sinon le premier
        /// à écrire consommerait le créneau de l'autre, et l'un des deux disparaîtrait du journal.
        /// </summary>
        private float _nextCreatureDiagnostic;

        /// <summary>
        /// Intervalle (s) du relevé des véhicules, et prochain instant.
        ///
        /// Il existe pour **M4** : la première question d'un Seamoth autoritaire n'est pas « comment
        /// l'écrire » mais « y a-t-il un véhicule à mesurer dans cette sauvegarde ». Un scan de scène est
        /// trop lourd pour la boucle à 10 Hz : trente secondes suffisent, un véhicule ne se déplace pas
        /// d'un monde à l'autre entre deux relevés.
        /// </summary>
        private const float VehicleReportSeconds = 30f;

        private float _nextVehicleDiagnostic;

        // — Véhicules (M4) ————————————————————————————————————————————————————————

        /// <summary>
        /// Cache des véhicules du monde, rafraîchi toutes les <see cref="VehicleCacheSeconds"/>.
        /// `FindObjectsOfType<Vehicle>` parcourt la scène : à 10 Hz ce serait absurde, et un véhicule
        /// apparaît ou disparaît rarement.
        /// </summary>
        private Vehicle[] _vehicleCache;
        private float _vehicleCacheRealtime = float.NegativeInfinity;
        private const float VehicleCacheSeconds = 2f;

        // — Création d'entité (v10, M6) ————————————————————————————————————————

        /// <summary>Clés de véhicules déjà annoncées (ou déjà présentes au premier balayage).</summary>
        private readonly HashSet<long> _knownVehicles = new HashSet<long>();

        /// <summary>Faux tant que le premier balayage n'a pas eu lieu : il **enregistre** sans annoncer.</summary>
        private bool _vehicleBaselineDone;

        /// <summary>
        /// Entités **fabriquées depuis le dernier démarrage**, par clé. Sert à rejouer l'annonce auprès d'un
        /// client qui se connecte après coup : sans ça, une entité créée pendant que personne n'était là (ou
        /// avant l'arrivée du deuxième client) n'existerait que chez ceux qui étaient connectés à cet instant.
        /// </summary>
        private readonly Dictionary<long, EntitySpawnMessage> _spawnedEntities = new Dictionary<long, EntitySpawnMessage>();

        // — Carnet des retraits (P2) ————————————————————————————————————————————————
        //
        // Le carnet d'apparitions rejoue ce qui a été **créé** avant l'arrivée d'un client. Sans le
        // pendant pour les retraits, un client qui rejoint après un ramassage ou une destruction
        // recharge la sauvegarde et voit réapparaître ce que le serveur a déjà retiré — le fantôme
        // que la v11 avait corrigé pour les clients **déjà connectés** seulement.
        private readonly Dictionary<long, EntityDespawnMessage> _despawnedEntities =
            new Dictionary<long, EntityDespawnMessage>();

        private readonly Queue<long> _despawnOrder = new Queue<long>();

        private const int MaxRememberedDespawns = 256;
        private int _entityDespawnsReplayed;

        /// <summary>Ordre d'ajout dans le carnet, pour que l'éviction retire bien la **plus ancienne**.</summary>
        private readonly Queue<long> _spawnOrder = new Queue<long>();

        /// <summary>Borne du carnet d'entités fabriquées : une partie longue ne doit pas le faire grossir sans fin.</summary>
        private const int MaxRememberedSpawns = 256;

        private int _entitySpawnsSent;
        private int _entitySpawnsReplayed;
        private int _entityDespawnsSent;

        public int EntitySpawnsSent => _entitySpawnsSent;

        public int EntityDespawnsSent => _entityDespawnsSent;

        /// <summary>Retraits rejoués à des clients arrivés après coup (P2).</summary>
        public int EntityDespawnsReplayed => _entityDespawnsReplayed;

        /// <summary>Intervalle (s) du rapport de flux de véhicules, par client.</summary>
        private const float VehicleStreamReportSeconds = 5f;

        private long _vehicleSnapshotsBroadcast;
        private long _vehicleStatesBroadcast;

        /// <summary>Véhicules écartés faute de clé : une identité inventée vaudrait moins qu'un véhicule non diffusé.</summary>
        private long _vehiclesWithoutKey;

        /// <summary>Paquets de véhicules refusés pour dépassement de la limite non fiable (invisibles autrement).</summary>
        private long _oversizedVehiclePackets;

        private string _lastVehicleIdentityReport;

        private readonly List<VehicleStateEntry> _vehicleBuffer = new List<VehicleStateEntry>();
        private readonly List<KeyValuePair<float, int>> _vehicleCandidates = new List<KeyValuePair<float, int>>();

        private readonly List<CreatureStateEntry> _creatureBuffer = new List<CreatureStateEntry>();
        private readonly List<KeyValuePair<float, int>> _creatureCandidates = new List<KeyValuePair<float, int>>();

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
            ValidatePendingSaveIdentities();
        }

        /// <summary>
        /// Vérifie les empreintes des sessions admises **avant** que le serveur n'ait résolu sa propre
        /// partie (P0).
        ///
        /// Pourquoi ce n'est pas fait au handshake : `ServerNetwork.Start` est appelé dans `Awake`, alors
        /// qu'`AutoLoadBehaviour` ne résout le slot que quelques secondes plus tard. Refuser un client
        /// correct sur cette course rendrait le démarrage dépendant d'un ordre que rien ne garantit.
        /// Le client est donc admis sous réserve, et la vérification tombe dès que l'empreinte serveur
        /// existe — avant qu'il n'ait pu jouer (le monde n'est pas encore chargé de son côté).
        /// </summary>
        private void ValidatePendingSaveIdentities()
        {
            var server = SaveIdentitySource.Current;
            if (server == null || server.IsUnknown || _sessions.Count == 0)
            {
                return;
            }

            List<KeyValuePair<NetPeer, string>> refused = null;

            foreach (var pair in _sessions)
            {
                var session = pair.Value;
                if (!session.Accepted || !session.SavePending)
                {
                    continue;
                }

                var reason = SaveCompatibility.Explain(server, session.Save);
                if (reason == null)
                {
                    session.SavePending = false;
                    _log.LogInfo(
                        $"[net] empreinte de sauvegarde acceptée (vérifiée après coup) : " +
                        $"client={session.Save?.Describe()} · serveur={server.Describe()}");
                    continue;
                }

                refused = refused ?? new List<KeyValuePair<NetPeer, string>>();
                refused.Add(new KeyValuePair<NetPeer, string>(pair.Key, reason));
            }

            if (refused == null)
            {
                return;
            }

            foreach (var item in refused)
            {
                if (!_sessions.TryGetValue(item.Key, out var session))
                {
                    continue;
                }

                _log.LogWarning(
                    $"[net] handshake REFUSÉ (empreinte) : '{session.Username}'#{session.PlayerId} : {item.Value}");

                // Même leçon qu'au §12 : envoyer le motif en fiable, **puis laisser le client fermer**.
                // Couper juste après l'envoi ferait perdre le message, et le client ne verrait qu'une
                // coupure sans explication. `ExpireStalledHandshakes` ferme le peer 2 s plus tard.
                Send(item.Key, w => new DisconnectMessage { Reason = item.Value }.Write(w), DeliveryMethod.ReliableOrdered);

                session.Accepted = false;
                session.SaveMismatch = true;
                session.HandshakeDeadline = ServerClock.Milliseconds + 2000L;
            }
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

            var who = known && session.Username != null ? $"{session.Username}#{session.PlayerId}" : peer.ToString();
            _log.LogInfo(
                $"[net] '{who}' déconnecté (raison={info.Reason}, pings={session?.PingsReceived ?? 0}, " +
                $"positions={session?.PositionsReceived ?? 0}, objetsTenus={session?.HeldItemsReceived ?? 0})");

            if (known && session.Accepted)
            {
                var leftMsg = new PlayerLeftMessage
                {
                    PlayerId = session.PlayerId,
                    Username = session.Username,
                    Reason = info.Reason.ToString(),
                };

                foreach (var pair in _sessions)
                {
                    if (!pair.Value.Accepted)
                    {
                        continue;
                    }

                    Send(pair.Key, leftMsg.Write, DeliveryMethod.ReliableOrdered);
                }

                _log.LogInfo($"[net] PlayerLeft diffusé pour '{session.Username}' (id={session.PlayerId})");

                // Ses volants aussi : un client parti ne les rendra jamais lui-même (v12).
                List<long> owned = null;
                foreach (var pair in _vehicleDrivers)
                {
                    if (pair.Value != session.PlayerId)
                    {
                        continue;
                    }

                    if (owned == null)
                    {
                        owned = new List<long>();
                    }

                    owned.Add(pair.Key);
                }

                if (owned != null)
                {
                    foreach (var key in owned)
                    {
                        _vehicleDrivers.Remove(key);
                        _vehicleDriverLastState.Remove(key);
                        VehicleReleases++;
                    }

                    _log.LogInfo(
                        $"[net] conduite : {owned.Count} volant(s) libéré(s) par la déconnexion de " +
                        $"'{session.Username}'#{session.PlayerId}.");
                }

                // Ses réclamations aussi. Mesuré le 2026-10-20 (reconnexion P2) : un objet réclamé par
                // le client tué restait refusé aux autres **avec l'id du mort** —
                // `REFUSÉE (déjà tenue par id 2)` — donc perdu pour tout le monde après une simple
                // déconnexion. L'objet accordé est déjà retiré du monde serveur ; la réclamation ne
                // protège plus rien une fois son détenteur parti.
                var releasedClaims = _pickupClaims.Release(session.PlayerId);

                if (releasedClaims > 0)
                {
                    _log.LogInfo(
                        $"[net] pickup : {releasedClaims} réclamation(s) libérée(s) par la déconnexion de " +
                        $"'{session.Username}'#{session.PlayerId} — les clés redeviennent réclamables.");
                }
            }
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

                case MessageType.PlayerPosition:
                    HandlePlayerPosition(peer, session, PlayerPositionMessage.Read(reader));
                    break;

                case MessageType.PlayerEquipment:
                    HandlePlayerEquipment(peer, session, PlayerEquipmentMessage.Read(reader));
                    break;

                case MessageType.PlayerHeldItem:
                    HandleHeldItem(peer, session, PlayerHeldItemMessage.Read(reader));
                    break;

                case MessageType.PickupClaim:
                    HandlePickupClaim(peer, session, PickupClaimMessage.Read(reader));
                    break;

                case MessageType.VehicleControl:
                    HandleVehicleControl(peer, session, VehicleControlMessage.Read(reader));
                    break;

                case MessageType.VehicleRelease:
                    HandleVehicleRelease(session, VehicleReleaseMessage.Read(reader));
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
                Send(peer, w => new HandshakeResponse
                {
                    Accepted = false,
                    Reason = reason,
                    ServerTick = ServerTickBehaviour.Ticks,
                    AssignedPlayerId = 0,
                }.Write(w), DeliveryMethod.ReliableOrdered);
                session.HandshakeDeadline = ServerClock.Milliseconds + Protocol.HandshakeTimeoutSeconds * 1000L;
                return;
            }

            _nextPlayerId++;
            if (_nextPlayerId == 0)
            {
                _nextPlayerId = 1;
            }

            session.PlayerId = _nextPlayerId;
            // Le pseudo d'un pair n'est pas une identité : il est affiché, journalisé et recopié dans
            // le nom du clone d'avatar. On le normalise ici pour qu'un client tiers ne puisse pas
            // injecter un saut de ligne dans une ligne de journal ni un caractère de contrôle dans un
            // nom d'objet. Le refus « pseudo trop long » de `Validate` reste pour l'annonce faite.
            session.Username = Usernames.Sanitize(request.Username);
            session.Accepted = true;
            session.RotW = 1f;

            // Empreinte admise (P0). Si le serveur n'a pas encore résolu la sienne, la vérification
            // est différée au tick suivant plutôt que de refuser un client correct sur une course de
            // démarrage — mais elle aura lieu **avant** que le client n'ait chargé son monde.
            session.Save = request.Save;
            session.SavePending = SaveIdentitySource.Current == null || SaveIdentitySource.Current.IsUnknown;

            if (session.SavePending)
            {
                _log.LogInfo(
                    $"[net] empreinte de sauvegarde en attente (serveur pas encore résolu) : " +
                    $"client={request.Save?.Describe()} — vérifiée au prochain tick.");
            }
            else
            {
                _log.LogInfo(
                    $"[net] empreinte de sauvegarde acceptée : client={request.Save.Describe()} · " +
                    $"serveur={SaveIdentitySource.Current.Describe()}");
            }

            // La couleur est décidée **ici**, une fois, et annoncée ensuite à tout le monde. L'hôte
            // occupe l'indice 0 quand son personnage est dans le monde : on cherche donc sa
            // présence avant de distribuer quoi que ce soit, et on réutilise la même référence plus
            // bas plutôt que de refaire la recherche.
            // **Sauf en serveur dédié** : l'admission est l'autre chemin qui fabriquait un avatar id=9999 —
            // celui que le client reçoit juste après son handshake, avant tout snapshot. Le mesurer a montré
            // que bloquer seulement la diffusion périodique laissait un « HoteServeur » apparaître 10 s plus
            // tard : il venait d'ici.
            var hostPlayer = CoopServerPlugin.Dedicated ? null : UnityEngine.Object.FindObjectOfType<Player>();
            var hostInWorld = hostPlayer != null && hostPlayer.gameObject != null;
            session.ColorIndex = AllocateColorIndex(hostInWorld);

            Protocol.PlayerColorAt(session.ColorIndex, out var assignedR, out var assignedG, out var assignedB);

            _log.LogInfo(
                $"[net] handshake ACCEPTÉ : '{request.Username}' (id={session.PlayerId}, jeu={request.GameVersion}, modHash={request.ModListHash}) " +
                $"depuis {peer} — {AcceptedClients}/{Protocol.MaxClients} client(s)");

            Send(peer, w => new HandshakeResponse
            {
                Accepted = true,
                Reason = string.Empty,
                ServerTick = ServerTickBehaviour.Ticks,
                AssignedPlayerId = session.PlayerId,
            }.Write(w), DeliveryMethod.ReliableOrdered);

            _log.LogInfo(
                $"[net] couleur assignée à '{session.Username}'#{session.PlayerId} : indice {session.ColorIndex} " +
                $"= rgb({assignedR},{assignedG},{assignedB})");

            // Sa propre couleur d'abord : un client qui ignore la sienne ne peut ni l'afficher, ni la
            // signaler en cas de doute.
            Send(peer, w => new PlayerColorMessage
            {
                PlayerId = session.PlayerId,
                ColorIndex = session.ColorIndex,
            }.Write(w), DeliveryMethod.ReliableOrdered);

            // 1. Informer le nouvel arrivant de tous les joueurs déjà présents (ainsi que l'hôte id=9999 s'il est dans le monde).
            if (hostInWorld)
            {
                var htr = hostPlayer.transform;
                var hpos = htr.position;
                // Même source que le client : le lacet du regard, pas la racine du joueur (sinon
                // l'avatar de l'hôte ne tourne jamais, et le receveur lui prêtait son propre lacet).
                var hrot = ViewDirection.Yaw(htr);
                Send(peer, w => new PlayerJoinedMessage
                {
                    PlayerId = 9999,
                    Username = "HoteServeur",
                    X = hpos.x,
                    Y = hpos.y,
                    Z = hpos.z,
                    RotX = hrot.x,
                    RotY = hrot.y,
                    RotZ = hrot.z,
                    RotW = hrot.w,
                }.Write(w), DeliveryMethod.ReliableOrdered);

                var hostEquip = ReadHostEquipment();
                if (hostEquip != null)
                {
                    Send(peer, w => new PlayerEquipmentMessage
                    {
                        PlayerId = 9999,
                        TechTypeNames = hostEquip,
                    }.Write(w), DeliveryMethod.ReliableOrdered);
                }

                var hostHeldItem = ReadHostHeldItemName();
                var hostHeldItemParams = ReadHostHoldingParams();
                if (hostHeldItem != null && hostHeldItemParams != null)
                {
                    Send(peer, w => new PlayerHeldItemMessage
                    {
                        PlayerId = 9999,
                        TechTypeName = hostHeldItem,
                        HoldingParams = hostHeldItemParams,
                    }.Write(w), DeliveryMethod.ReliableOrdered);
                }

                // La couleur de l'hôte : c'est elle qui rend le personnage de l'instance serveur
                // reconnaissable parmi les joueurs, au même titre que sa tenue.
                Send(peer, w => new PlayerColorMessage
                {
                    PlayerId = 9999,
                    ColorIndex = HostColorIndex,
                }.Write(w), DeliveryMethod.ReliableOrdered);
            }

            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted || pair.Key == peer)
                {
                    continue;
                }

                var other = pair.Value;
                Send(peer, w => new PlayerJoinedMessage
                {
                    PlayerId = other.PlayerId,
                    Username = other.Username,
                    X = other.X,
                    Y = other.Y,
                    Z = other.Z,
                    RotX = other.RotX,
                    RotY = other.RotY,
                    RotZ = other.RotZ,
                    RotW = other.RotW,
                }.Write(w), DeliveryMethod.ReliableOrdered);

                if (other.Equipment != null)
                {
                    Send(peer, w => new PlayerEquipmentMessage
                    {
                        PlayerId = other.PlayerId,
                        TechTypeNames = other.Equipment,
                    }.Write(w), DeliveryMethod.ReliableOrdered);
                }

                if (other.HeldItemName != null)
                {
                    Send(peer, w => new PlayerHeldItemMessage
                    {
                        PlayerId = other.PlayerId,
                        TechTypeName = other.HeldItemName,
                        HoldingParams = other.HoldingParams ?? Array.Empty<string>(),
                    }.Write(w), DeliveryMethod.ReliableOrdered);
                }

                Send(peer, w => new PlayerColorMessage
                {
                    PlayerId = other.PlayerId,
                    ColorIndex = other.ColorIndex,
                }.Write(w), DeliveryMethod.ReliableOrdered);
            }

            // 2. Informer tous les joueurs déjà présents de l'arrivée de ce joueur.
            var joinedMsg = new PlayerJoinedMessage
            {
                PlayerId = session.PlayerId,
                Username = session.Username,
                X = session.X,
                Y = session.Y,
                Z = session.Z,
                RotX = session.RotX,
                RotY = session.RotY,
                RotZ = session.RotZ,
                RotW = session.RotW,
            };

            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted || pair.Key == peer)
                {
                    continue;
                }

                Send(pair.Key, joinedMsg.Write, DeliveryMethod.ReliableOrdered);
                Send(pair.Key, w => new PlayerColorMessage
                {
                    PlayerId = session.PlayerId,
                    ColorIndex = session.ColorIndex,
                }.Write(w), DeliveryMethod.ReliableOrdered);
            }

            // 3. Ce qui a été fabriqué avant son arrivée. Après les joueurs : un client qui reçoit l'annonce
            //    d'une entité doit déjà connaître l'état du monde auquel elle se rattache.
            SendSpawnedEntities(peer, session);
        }

        /// <summary>
        /// Indice de couleur du prochain client admis.
        ///
        /// La règle tient en une ligne : on avance d'un cran par client, en sautant l'indice de
        /// l'hôte quand son personnage est dans le monde. C'est ce qui garantit que deux joueurs ne
        /// reçoivent pas la même couleur, sans arbitrage à la connexion et sans que le client ait
        /// quelque chose à décider.
        /// </summary>
        private byte AllocateColorIndex(bool hostInWorld)
        {
            // Sans hôte dans le monde, l'indice 0 est libre : les clients le prennent à leur tour.
            var start = hostInWorld ? HostColorIndex + 1 : 0;
            var index = (byte)((_nextClientColorIndex + start) % Protocol.PlayerColorCount);
            _nextClientColorIndex = (byte)((_nextClientColorIndex + 1) % Protocol.PlayerColorCount);
            return index;
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

            // Empreinte de sauvegarde (P0). Le serveur ne peut juger que s'il a lui-même résolu sa
            // partie : sinon la session est admise sous réserve (voir `ValidatePendingSaveIdentities`).
            // Un client qui n'a pas résolu la sienne, en revanche, est refusé tout de suite : accepter
            // une empreinte inconnue reviendrait à accepter n'importe quelle partie.
            var serverSave = SaveIdentitySource.Current;
            if (serverSave != null && !serverSave.IsUnknown)
            {
                if (request.Save == null || request.Save.IsUnknown)
                {
                    return "empreinte de sauvegarde inconnue côté client (le client n'a pas encore résolu sa partie)";
                }

                var mismatch = SaveCompatibility.Explain(serverSave, request.Save);
                if (mismatch != null)
                {
                    return mismatch;
                }
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

        private void HandlePlayerPosition(NetPeer peer, ClientSession session, PlayerPositionMessage pos)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] position avant handshake ignorée de {peer}");
                return;
            }

            // Rejeter les paquets UDP arrivés dans le désordre.
            if (session.PositionsReceived > 0 && pos.Sequence <= session.LastPositionSequence)
            {
                return;
            }

            // Garde-fou contre les flottants dégénérés (NaN / Inf).
            if (float.IsNaN(pos.X) || float.IsInfinity(pos.X) ||
                float.IsNaN(pos.Y) || float.IsInfinity(pos.Y) ||
                float.IsNaN(pos.Z) || float.IsInfinity(pos.Z) ||
                float.IsNaN(pos.RotX) || float.IsInfinity(pos.RotX) ||
                float.IsNaN(pos.RotY) || float.IsInfinity(pos.RotY) ||
                float.IsNaN(pos.RotZ) || float.IsInfinity(pos.RotZ) ||
                float.IsNaN(pos.RotW) || float.IsInfinity(pos.RotW))
            {
                _log.LogWarning($"[net] position NaN/Infinity ignorée de '{session.Username}'#{session.PlayerId}");
                return;
            }

            // La séquence et le compteur avancent **même si la position est refusée** : un message
            // rejeté ne doit pas être rejoué, et le taux de refus se calcule sur les messages reçus.
            session.LastPositionSequence = pos.Sequence;
            session.PositionsReceived++;

            var wallNow = UnityEngine.Time.realtimeSinceStartup;

            if (!session.HasAcceptedPosition)
            {
                StoreAcceptedPosition(session, pos, wallNow);
                session.NextPositionReportAt = wallNow + PositionReportSeconds;

                _log.LogInfo(
                    $"[net] première position reçue de '{session.Username}'#{session.PlayerId} : " +
                    $"({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}) v=({pos.MoveSpeedX:F1},{pos.MoveSpeedY:F1},{pos.MoveSpeedZ:F1}) " +
                    $"pitch={pos.ViewPitch:F1} sousEau={pos.IsUnderwater} actions={pos.ActionFlags}");
                ReportPositionRate(session, wallNow);
                return;
            }

            // P1 : la position est jugée contre la dernière position **acceptée**, pas contre la
            // précédente reçue. Un refus ne déplace donc pas la référence, sinon un client pourrait
            // avancer par petits sauts refusés sans jamais être corrigé.
            var deltaSeconds = wallNow - session.LastAcceptedAt;
            var verdict = PlayerPlausibility.Evaluate(
                session.LastAcceptedX,
                session.LastAcceptedY,
                session.LastAcceptedZ,
                pos.X,
                pos.Y,
                pos.Z,
                deltaSeconds,
                session.LastAcceptedFlags,
                pos.ActionFlags);

            if (verdict.Accepted)
            {
                session.RejectedStreak = 0;
                StoreAcceptedPosition(session, pos, wallNow);
            }
            else if (session.RejectedStreak + 1 >= PlayerPlausibility.MaxConsecutiveRejections)
            {
                // Une téléportation légitime (console, téléporteur, réapparition) ne doit pas
                // condamner la session : après trois refus consécutifs, la position reçue est
                // adoptée comme resynchronisation, et c'est **écrit** comme tel. Le troisième refus
                // est compté comme les deux premiers : le taux publié doit refléter tous les refus,
                // sinon une session qui resynchronise sans arrêt paraîtrait propre.
                session.PositionsRejected++;
                session.PositionResyncs++;
                session.RejectedStreak = 0;
                StoreAcceptedPosition(session, pos, wallNow);

                _log.LogWarning(
                    $"[net] position RESYNCHRONISÉE de '{session.Username}'#{session.PlayerId} après " +
                    $"{PlayerPlausibility.MaxConsecutiveRejections} refus consécutifs : {verdict.Reason} — la position reçue est adoptée.");

                // Budget : au-delà de cinq resynchronisations en une minute, ce n'est plus un
                // téléporteur du jeu, c'est un client qui saute. La session est éjectée avec un motif.
                if (session.ResyncWindowStart < 0f || wallNow - session.ResyncWindowStart > 60f)
                {
                    session.ResyncWindowStart = wallNow;
                    session.ResyncsInWindow = 0;
                }

                session.ResyncsInWindow++;
                if (session.ResyncsInWindow > 5)
                {
                    var budgetReason =
                        $"trop de resynchronisations de position ({session.ResyncsInWindow} en moins de 60 s) — " +
                        "une téléportation légitime est rare, une téléportation répétée n'en est pas une";

                    _log.LogWarning(
                        $"[net] position : '{session.Username}'#{session.PlayerId} éjecté — {budgetReason}");

                    Send(peer, w => new DisconnectMessage { Reason = budgetReason }.Write(w), DeliveryMethod.ReliableOrdered);
                    session.Accepted = false;
                    session.HandshakeDeadline = ServerClock.Milliseconds + 2000L;
                    return;
                }
            }
            else
            {
                session.RejectedStreak++;
                session.PositionsRejected++;

                // Le serveur garde la dernière position acceptée : les autres joueurs ne voient
                // jamais le saut, et le client reçoit de quoi revenir.
                if (session.PositionsRejected <= 3 || session.PositionsRejected % 100 == 0)
                {
                    _log.LogWarning(
                        $"[net] position REFUSÉE de '{session.Username}'#{session.PlayerId} : {verdict.Reason} — " +
                        $"correction renvoyée (refus={session.PositionsRejected})");
                }

                Send(peer, w => new PlayerCorrectionMessage
                {
                    ServerTick = ServerTickBehaviour.Ticks,
                    X = session.LastAcceptedX,
                    Y = session.LastAcceptedY,
                    Z = session.LastAcceptedZ,
                    Reason = WireReason(verdict.Reason),
                }.Write(w), DeliveryMethod.ReliableOrdered);
            }

            ReportPositionRate(session, wallNow);
        }

        /// <summary>
        /// Mémorise une position comme **acceptée** : c'est le seul état que les autres joueurs voient.
        /// </summary>
        private static void StoreAcceptedPosition(ClientSession session, PlayerPositionMessage pos, float now)
        {
            session.HasAcceptedPosition = true;
            session.LastAcceptedX = pos.X;
            session.LastAcceptedY = pos.Y;
            session.LastAcceptedZ = pos.Z;
            session.LastAcceptedFlags = pos.ActionFlags;
            session.LastAcceptedAt = now;

            session.X = pos.X;
            session.Y = pos.Y;
            session.Z = pos.Z;
            session.RotX = pos.RotX;
            session.RotY = pos.RotY;
            session.RotZ = pos.RotZ;
            session.RotW = pos.RotW;
            session.MoveSpeedX = pos.MoveSpeedX;
            session.MoveSpeedY = pos.MoveSpeedY;
            session.MoveSpeedZ = pos.MoveSpeedZ;
            session.ViewPitch = pos.ViewPitch;
            session.IsUnderwater = pos.IsUnderwater;
            session.OnSurface = pos.OnSurface;
            session.ActionFlags = pos.ActionFlags;
        }

        /// <summary>
        /// Tronque un motif pour le fil : `Protocol.MaxCorrectionReasonLength` est une borne de
        /// message, et un motif de plausibilité complet la dépasse. Le journal garde la version
        /// entière ; seule la copie transportée est tronquée.
        /// </summary>
        private static string WireReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return string.Empty;
            }

            return reason.Length <= Protocol.MaxCorrectionReasonLength
                ? reason
                : reason.Substring(0, Protocol.MaxCorrectionReasonLength);
        }

        /// <summary>
        /// Rapport périodique du taux de refus (P1). C'est ce chiffre — et non le nombre brut — qui
        /// sert de critère d'arrêt : plus de 5 % de corrections sur une nage normale signifie que la
        /// validation a posteriori n'est pas viable.
        /// </summary>
        private void ReportPositionRate(ClientSession session, float now)
        {
            if (now < session.NextPositionReportAt)
            {
                return;
            }

            session.NextPositionReportAt = now + PositionReportSeconds;

            if (session.PositionsReceived <= 0)
            {
                return;
            }

            var rate = 100.0 * session.PositionsRejected / session.PositionsReceived;
            _log.LogInfo(
                $"[net] position : '{session.Username}'#{session.PlayerId} {session.PositionsReceived} reçue(s), " +
                $"{session.PositionsRejected} refusée(s) ({rate:F1}%), {session.PositionResyncs} resynchronisée(s)");
        }

        /// <summary>
        /// Objet tenu annoncé par un client (outil en main, ou rien).
        ///
        /// Le serveur ne fait que le mémoriser et le relayer : il ne le valide pas contre un inventaire
        /// qu'il ne détient pas pour les joueurs distants, et il **force** l'identité de l'émetteur —
        /// un client ne peut donc pas prétendre tenir un objet à la place d'un autre.
        /// </summary>
        private void HandleHeldItem(NetPeer peer, ClientSession session, PlayerHeldItemMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] objet tenu avant handshake ignoré de {peer}");
                return;
            }

            // Le client peut prétendre tenir l'objet d'un autre : l'identité qui compte est celle de la
            // session, jamais celle du message. Un écart est une falsification, et elle se dit — une
            // réécriture silencieuse laisserait croire à une confiance accordée au champ.
            var forgedHeld = msg.PlayerId != 0 && msg.PlayerId != session.PlayerId;
            if (forgedHeld)
            {
                ForgedPlayerIds++;
                _log.LogWarning(
                    $"[net] PlayerId falsifié sur l'objet tenu : '{session.Username}' a annoncé sous l'id " +
                    $"{msg.PlayerId} (le sien est {session.PlayerId}) — le relais réécrit avec l'id de la session.");
                _log.LogInfo(
                    $"[net] vérification PlayerId (objet tenu) : '{session.Username}' a annoncé l'id " +
                    $"{msg.PlayerId}, effectif={session.PlayerId} -> verdict=True");
            }

            session.HeldItemName = msg.TechTypeName ?? string.Empty;
            session.HoldingParams = msg.HoldingParams ?? Array.Empty<string>();
            session.HeldItemsReceived++;

            var held = session.HeldItemName.Length == 0 ? "(rien en main)" : session.HeldItemName;
            var parameters = session.HoldingParams.Length == 0
                ? "(aucun paramètre)"
                : string.Join(", ", session.HoldingParams);
            _log.LogInfo(
                $"[net] objet tenu reçu de '{session.Username}'#{session.PlayerId} : {held} " +
                $"— paramètres : {parameters}");

            var outgoing = new PlayerHeldItemMessage
            {
                PlayerId = session.PlayerId,
                TechTypeName = session.HeldItemName,
                HoldingParams = session.HoldingParams,
            };

            var forwarded = 0;
            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted || pair.Key == peer)
                {
                    continue;
                }

                Send(pair.Key, outgoing.Write, DeliveryMethod.ReliableOrdered);
                forwarded++;
            }

            _log.LogInfo(
                $"[net] PlayerHeldItem de '{session.Username}'#{session.PlayerId} diffusé à {forwarded} autre(s) client(s).");
        }

        /// <summary>
        /// Première action **autoritaire** (M5) : le serveur décide qui a le droit de ramasser un objet.
        ///
        /// La règle est minimale et entièrement côté serveur : la première réclamation d'une clé gagne, les
        /// suivantes sont refusées et **nommées** avec leur détenteur ; une réclamation du même joueur est
        /// idempotente. Le `PlayerId` du message n'est jamais recopié — l'identité vient de la session, et
        /// l'écart est compté, sans quoi un client pourrait réclamer pour le compte d'un autre.
        ///
        /// Ce que ce handler ne fait pas encore : vérifier que l'objet existe et que le demandeur est à
        /// portée. C'est la moitié « branchement sur le ramassage réel » de M5 (PLAN.md §38) ; ici, la clé
        /// est fournie par la sonde et la décision est réelle.
        /// </summary>
        private void HandlePickupClaim(NetPeer peer, ClientSession session, PickupClaimMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] réclamation de ramassage avant handshake ignorée de {peer}");
                return;
            }

            var tech = string.IsNullOrEmpty(msg.TechTypeName) ? "(sans nom)" : msg.TechTypeName;

            if (msg.PlayerId != 0 && msg.PlayerId != session.PlayerId)
            {
                ForgedPickupIds++;
                _log.LogWarning(
                    $"[net] PlayerId falsifié sur une réclamation : '{session.Username}' a annoncé sous l'id " +
                    $"{msg.PlayerId} (le sien est {session.PlayerId}) — la réclamation est attribuée à la session.");
                _log.LogInfo(
                    $"[net] vérification PlayerId (pickup) : '{session.Username}' a annoncé l'id " +
                    $"{msg.PlayerId}, effectif={session.PlayerId} -> verdict=True");
            }

            // — L'objet doit exister **chez le serveur**, et le joueur doit être à portée.
            //
            // Ce sont les deux gardes qui manquaient à la décision seule : une clé inconnue était
            // accordée, et un joueur pouvait réclamer un objet à l'autre bout de la carte. La portée se
            // mesure avec la **dernière position reçue** de la session (20 Hz), sans nouveau champ dans
            // le message : le format de la v8 suffit.
            // — Une clé **déjà attribuée** est refusée immédiatement, avant toute résolution du monde.
            //
            // L'ordre compte depuis la v11 : un objet accordé est retiré du monde simulé, donc la résolution
            // échouerait et le motif deviendrait « objet inconnu » — vrai, mais muet sur le fait qu'un
            // joueur le détient. La carte des réclamations est le registre durable ; elle passe en premier.
            if (msg.ItemKey != 0L &&
                _pickupClaims.TryGetHolder(msg.ItemKey, out var alreadyHeld) &&
                alreadyHeld != session.PlayerId)
            {
                PickupClaimsRefused++;
                _log.LogInfo(
                    $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                    $"'{session.Username}'#{session.PlayerId} -> REFUSÉE (déjà tenue par {DescribeHolder(alreadyHeld)})");

                Send(peer, new PickupResultMessage
                {
                    ItemKey = msg.ItemKey,
                    TechTypeName = tech,
                    Granted = false,
                    HolderId = alreadyHeld,
                    ClaimedBy = msg.PlayerId,
                }.Write, DeliveryMethod.ReliableOrdered);

                return;
            }

            UnityEngine.Vector3 itemPosition = UnityEngine.Vector3.zero;
            var distance = 0f;
            string refusal = null;

            if (!TryResolveWorldItem(msg.ItemKey, out itemPosition, out var indexedKeys))
            {
                PickupRefusedUnknown++;
                refusal = $"objet inconnu du serveur ({indexedKeys} objet(s) indexé(s)) — la clé ne désigne " +
                          "rien dans le monde simulé";
            }
            else if (session.PositionsReceived == 0)
            {
                PickupRefusedNoPosition++;
                refusal = "position du joueur inconnue (aucune position reçue) — la portée n'est pas vérifiable";
            }
            else
            {
                distance = UnityEngine.Vector3.Distance(
                    new UnityEngine.Vector3(session.X, session.Y, session.Z), itemPosition);

                if (distance > Protocol.MaxPickupRangeMeters)
                {
                    PickupRefusedRange++;
                    refusal = $"hors portée : {distance:F1} m > {Protocol.MaxPickupRangeMeters:F1} m";
                }
            }

            if (refusal != null)
            {
                PickupClaimsRefused++;
                _log.LogInfo(
                    $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                    $"'{session.Username}'#{session.PlayerId} -> REFUSÉE ({refusal})");

                Send(peer, new PickupResultMessage
                {
                    ItemKey = msg.ItemKey,
                    TechTypeName = tech,
                    Granted = false,
                    HolderId = 0,
                    ClaimedBy = msg.PlayerId,
                }.Write, DeliveryMethod.ReliableOrdered);

                return;
            }

            var granted = false;
            var holder = session.PlayerId;

            if (msg.ItemKey == 0L)
            {
                PickupClaimsRefused++;
                _log.LogWarning(
                    $"[net] pickup : clé nulle réclamée par '{session.Username}'#{session.PlayerId} " +
                    $"('{tech}') -> REFUSÉE (aucune identité d'objet)");
            }
            else if (_pickupClaims.TryGetHolder(msg.ItemKey, out var existing))
            {
                holder = existing;
                granted = existing == session.PlayerId;
                if (granted)
                {
                    PickupClaimsGranted++;
                    _log.LogInfo(
                        $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                        $"'{session.Username}'#{session.PlayerId} -> ACCORDÉE (déjà tenue par le même joueur, " +
                        $"objet à {distance:F1} m)");
                }
                else
                {
                    PickupClaimsRefused++;
                    _log.LogInfo(
                        $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                        $"'{session.Username}'#{session.PlayerId} -> REFUSÉE (déjà tenue par " +
                        $"{DescribeHolder(existing)})");
                }
            }
            else
            {
                _pickupClaims.Claim(msg.ItemKey, session.PlayerId);
                granted = true;
                PickupClaimsGranted++;
                _log.LogInfo(
                    $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                    $"'{session.Username}'#{session.PlayerId} -> ACCORDÉE (premier claim, objet à {distance:F1} m)");
            }

            Send(peer, new PickupResultMessage
            {
                ItemKey = msg.ItemKey,
                TechTypeName = tech,
                Granted = granted,
                HolderId = holder,
                ClaimedBy = msg.PlayerId,
            }.Write, DeliveryMethod.ReliableOrdered);

            // — La conséquence du geste (v11).
            //
            // Jusqu'ici, un accord ne changeait rien dans le monde du serveur : l'objet restait chez lui,
            // et un troisième joueur qui le réclamait recevait un **accord** pour un objet qui n'existait
            // plus pour personne. On retire donc l'objet du monde simulé et on l'annonce.
            //
            // Seulement sur un accord **nouveau** : une réclamation répétée du même joueur (idempotente)
            // ne doit pas retirer deux fois, et un refus ne retire rien.
            if (granted && msg.ItemKey != 0L && !_pickupRemoved.Contains(msg.ItemKey))
            {
                RemoveGrantedItem(msg.ItemKey, tech, session);
            }
        }

        /// <summary>
        /// Retire du monde du serveur l'objet dont la clé vient d'être accordée, puis annonce sa disparition.
        ///
        /// Ce que ça change et ce que ça ne change pas : l'objet cesse d'exister dans le monde simulé (il ne
        /// sera plus résolu par <see cref="TryResolveWorldItem"/>), et les clients le voient disparaître. Il
        /// n'est **pas** retiré de l'inventaire de celui qui l'a ramassé — il y est déjà depuis son geste
        /// local, et c'est son exemplaire à lui qui compte.
        /// </summary>
        private void RemoveGrantedItem(long key, string tech, ClientSession session)
        {
            if (!TryResolveWorldItemObject(key, out var item))
            {
                _log.LogWarning(
                    $"[net] pickup : clé=0x{key:x16} '{tech}' accordée à '{session.Username}'#{session.PlayerId} " +
                    "mais l'objet n'est plus dans le monde serveur — rien à retirer (il a peut-être déjà été " +
                    "consumé par le jeu).");
                return;
            }

            var described = ItemIdentity.Describe(item);

            _pickupRemoved.Add(key);
            UnityEngine.Object.Destroy(item.gameObject);

            // L'index est invalidé : sans ça, la clé resterait résoluble pendant une seconde, et une
            // réclamation concurrente retomberait sur une position morte.
            _itemIndex = null;

            _log.LogInfo(
                $"[net] pickup : clé=0x{key:x16} '{tech}' retirée du monde serveur ({described}) — " +
                $"ramassée par '{session.Username}'#{session.PlayerId}.");

            // Le détenteur est exclu : son exemplaire est déjà dans son inventaire, et c'est son geste à lui
            // qui le fait disparaître de son monde.
            BroadcastDespawn(
                key,
                EntityKind.Item,
                $"ramassée par '{session.Username}'#{session.PlayerId}",
                session.PlayerId);
        }

        /// <summary>
        /// Conduite d'un véhicule (v9, M4). Le client pilote : il envoie la position et l'orientation de
        /// **son** véhicule à 10 Hz, et le serveur les applique à **son** exemplaire du même véhicule, puis
        /// les rediffuse par le flux `VehicleSnapshot` déjà en place.
        ///
        /// Ce que le serveur ne croit pas sur parole, et qui est compté : la clé doit désigner un véhicule
        /// de **son** monde, le joueur doit être à moins de `MaxVehicleControlRangeMeters`, et un véhicule
        /// déjà conduit par un autre joueur n'est pas volé en silence — le refus est renvoyé au demandeur
        /// (`VehicleControlResult`) pour qu'il sache qu'il ne conduit pas.
        /// </summary>
        private void HandleVehicleControl(NetPeer peer, ClientSession session, VehicleControlMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] conduite avant handshake ignorée de {peer}");
                return;
            }

            if (msg.PlayerId != 0 && msg.PlayerId != session.PlayerId)
            {
                ForgedVehicleIds++;
                if (ForgedVehicleIds <= 3)
                {
                    _log.LogWarning(
                        $"[net] PlayerId falsifié sur une conduite : '{session.Username}' a annoncé l'id " +
                        $"{msg.PlayerId} (le sien est {session.PlayerId}) — la conduite est attribuée à la session.");
                }
            }

            if (!TryResolveVehicle(msg.VehicleKey, out var vehicle, out var indexed))
            {
                VehicleControlRefusedUnknown++;
                LogControlRefusal(msg.VehicleKey, session, $"véhicule inconnu du serveur ({indexed} véhicule(s) indexé(s))");
                ReplyControl(peer, msg.VehicleKey, false, 0);
                return;
            }

            var vehiclePosition = vehicle.transform != null
                ? vehicle.transform.position
                : UnityEngine.Vector3.zero;

            var distance = session.PositionsReceived == 0
                ? float.MaxValue
                : UnityEngine.Vector3.Distance(
                    new UnityEngine.Vector3(session.X, session.Y, session.Z), vehiclePosition);

            if (distance > Protocol.MaxVehicleControlRangeMeters)
            {
                VehicleControlRefusedRange++;
                LogControlRefusal(
                    msg.VehicleKey,
                    session,
                    $"hors portée : {distance:F1} m > {Protocol.MaxVehicleControlRangeMeters:F1} m");
                ReplyControl(peer, msg.VehicleKey, false, 0);
                return;
            }

            if (_vehicleDrivers.TryGetValue(msg.VehicleKey, out var driver) && driver != session.PlayerId)
            {
                VehicleControlRefusedTaken++;
                LogControlRefusal(
                    msg.VehicleKey,
                    session,
                    $"déjà conduite par {DescribeHolder(driver)}");
                ReplyControl(peer, msg.VehicleKey, false, driver);
                return;
            }

            var first = !_vehicleDrivers.ContainsKey(msg.VehicleKey);
            _vehicleDrivers[msg.VehicleKey] = session.PlayerId;
            _vehicleDriverLastState[msg.VehicleKey] = UnityEngine.Time.realtimeSinceStartup;

            ApplyVehicleControl(vehicle, msg);
            VehicleControlApplied++;

            if (first)
            {
                _log.LogInfo(
                    $"[net] conduite : clé=0x{msg.VehicleKey:x16} — prise par '{session.Username}'#{session.PlayerId} " +
                    $"({VehicleIdentity.Describe(vehicle)}, joueur à {distance:F1} m), état appliqué et rediffusé.");

                ReplyControl(peer, msg.VehicleKey, true, session.PlayerId);
            }
        }

        /// <summary>
        /// Libération du volant par son conducteur (v12, M4).
        ///
        /// Ce que ça change : jusqu'ici un véhicule restait attribué à son dernier conducteur **jusqu'à sa
        /// déconnexion**. Un joueur qui descendait d'un Seamoth le rendait donc inutilisable pour les autres
        /// — le refus les renvoyait à un conducteur qui n'était plus dedans. La libération est immédiate ; le
        /// silence reste le filet de sécurité (voir <see cref="ReleaseSilentDrivers"/>).
        ///
        /// Seul le conducteur **effectif** peut libérer : une demande pour un véhicule conduit par un autre
        /// est ignorée (sans réponse : ce n'est pas une décision à rendre, c'est un fait sans effet).
        /// </summary>
        private void HandleVehicleRelease(ClientSession session, VehicleReleaseMessage msg)
        {
            if (!session.Accepted || msg.VehicleKey == 0L)
            {
                return;
            }

            if (!_vehicleDrivers.TryGetValue(msg.VehicleKey, out var driver) || driver != session.PlayerId)
            {
                return;
            }

            _vehicleDrivers.Remove(msg.VehicleKey);
            _vehicleDriverLastState.Remove(msg.VehicleKey);
            VehicleReleases++;

            _log.LogInfo(
                $"[net] conduite : clé=0x{msg.VehicleKey:x16} libérée par '{session.Username}'#{session.PlayerId} " +
                "— le volant est libre (libérations=1, total=" + VehicleReleases + ")");
        }

        /// <summary>
        /// Libère les volants devenus **silencieux** (v12).
        ///
        /// Pourquoi le silence et pas seulement le message : un client qui plante, qui perd le réseau ou qui
        /// contourne `VehicleDriverBehaviour` ne libère rien. Le conducteur envoie un état toutes les 100 ms
        /// tant qu'il est au volant ; `VehicleSilenceReleaseSeconds` (3 s) vaut donc 30 états manqués — bien
        /// au-delà d'une rafale de latence.
        /// </summary>
        private void ReleaseSilentDrivers()
        {
            if (_vehicleDrivers.Count == 0)
            {
                return;
            }

            var now = UnityEngine.Time.realtimeSinceStartup;
            List<long> silent = null;

            foreach (var pair in _vehicleDrivers)
            {
                if (!_vehicleDriverLastState.TryGetValue(pair.Key, out var last))
                {
                    last = now;
                    _vehicleDriverLastState[pair.Key] = now;
                }

                if (now - last <= Protocol.VehicleSilenceReleaseSeconds)
                {
                    continue;
                }

                if (silent == null)
                {
                    silent = new List<long>();
                }

                silent.Add(pair.Key);
            }

            if (silent == null)
            {
                return;
            }

            foreach (var key in silent)
            {
                _vehicleDrivers.Remove(key);
                _vehicleDriverLastState.Remove(key);
                VehicleSilenceReleases++;
                _log.LogInfo(
                    $"[net] conduite : clé=0x{key:x16} libérée (silence de {Protocol.VehicleSilenceReleaseSeconds:F1} s) " +
                    $"— le conducteur ne l'envoie plus (libérations par silence={VehicleSilenceReleases}).");
            }
        }

        /// <summary>Refus journalisé **une fois par clé** : à 10 Hz, un refus répété noierait le journal.</summary>
        private void LogControlRefusal(long key, ClientSession session, string reason)
        {
            if (!_controlRefusalsLogged.Add(key))
            {
                return;
            }

            _log.LogInfo(
                $"[net] conduite : clé=0x{key:x16} réclamée par '{session.Username}'#{session.PlayerId} " +
                $"-> REFUSÉE ({reason})");
        }

        /// <summary>
        /// Réponse au demandeur : sur un accord, toujours ; sur un refus, au plus une fois par seconde et
        /// par clé (le client envoie 10 états par seconde, il n'a pas besoin de 10 refus).
        /// </summary>
        private void ReplyControl(NetPeer peer, long key, bool granted, ushort driverId)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (!granted && _lastControlReplyAt.TryGetValue(key, out var last) && now - last < 1f)
            {
                return;
            }

            _lastControlReplyAt[key] = now;

            Send(peer, new VehicleControlResultMessage
            {
                VehicleKey = key,
                Granted = granted,
                DriverId = driverId,
            }.Write, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Applique l'état du conducteur au véhicule du serveur (c'est lui qui est rediffusé).</summary>
        private static void ApplyVehicleControl(Vehicle vehicle, VehicleControlMessage msg)
        {
            var transform = vehicle.transform;
            if (transform != null)
            {
                transform.position = new UnityEngine.Vector3(msg.X, msg.Y, msg.Z);
                transform.rotation = new UnityEngine.Quaternion(msg.RotX, msg.RotY, msg.RotZ, msg.RotW);
            }

            // Le transform est posé à chaque état : laisser la physique accélérer le Rigidbody ferait
            // osciller le véhicule entre deux corrections. On annule donc sa vitesse, comme pour les
            // créatures suivies.
            var body = vehicle.GetComponent<UnityEngine.Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.velocity = UnityEngine.Vector3.zero;
                body.angularVelocity = UnityEngine.Vector3.zero;
            }
        }

        /// <summary>
        /// Vrai si la clé désigne un **véhicule du monde serveur**. Index reconstruit au plus une fois par
        /// seconde : un état de conduite arrive 10 fois par seconde, un parcours complet à chaque état
        /// coûterait cher pour rien.
        /// </summary>
        private bool TryResolveVehicle(long key, out Vehicle vehicle, out int indexed)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;

            if (_vehicleIndex == null || now - _vehicleIndexBuiltAt > 1f)
            {
                var index = new Dictionary<long, Vehicle>();
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<Vehicle>();
                var count = 0;

                foreach (var candidate in all)
                {
                    if (candidate == null || candidate.gameObject == null || !candidate.gameObject.scene.IsValid())
                    {
                        continue;
                    }

                    count++;
                    var candidateKey = VehicleIdentity.KeyOf(candidate, out _);
                    if (candidateKey != 0L)
                    {
                        index[candidateKey] = candidate;
                    }
                }

                _vehicleIndex = index;
                _vehicleIndexBuiltAt = now;
                _vehicleIndexCount = count;
            }

            indexed = _vehicleIndexCount;
            return _vehicleIndex.TryGetValue(key, out vehicle);
        }

        /// <summary>
        /// Vrai si la clé désigne un objet **du monde simulé**, et où il se trouve.
        ///
        /// Pourquoi le serveur doit résoudre la clé lui-même : sans cela, il accorderait n'importe quelle
        /// valeur — un client pourrait « réclamer » une clé inventée pour bloquer un objet qu'un autre
        /// client réclamerait ensuite. L'index est reconstruit au plus une fois par seconde, avec la
        /// **même** fonction de clé que le client (<see cref="ItemIdentity"/>, compilé dans les deux
        /// plugins) ; les clés partagées sont comptées et signalées plutôt que silencieuses.
        /// </summary>
        private bool TryResolveWorldItem(long key, out UnityEngine.Vector3 position, out int indexedKeys)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;

            if (_itemIndex == null || now - _itemIndexBuiltAt > 1f)
            {
                var index = new Dictionary<long, UnityEngine.Vector3>();
                var items = ItemIdentity.CollectWorldItems();
                var shared = 0;
                var sharedKey = 0L;

                foreach (var item in items)
                {
                    var itemKey = ItemIdentity.KeyOf(item, out _);
                    if (itemKey == 0L)
                    {
                        continue;
                    }

                    if (index.ContainsKey(itemKey))
                    {
                        shared++;
                        sharedKey = itemKey;
                    }

                    index[itemKey] = item.transform != null ? item.transform.position : UnityEngine.Vector3.zero;
                }

                _itemIndex = index;
                _itemIndexBuiltAt = now;
                _itemIndexObjects = items.Count;

                if (shared != _itemIndexSharedKeys)
                {
                    _itemIndexSharedKeys = shared;
                    _log.LogWarning(
                        $"[net] pickup : {shared} clé(s) d'objet partagée(s) dans l'index (ex. 0x{sharedKey:x16}) " +
                        "— deux objets du monde se confondent, la décision portera sur le dernier indexé. " +
                        "C'est une limite du repli par position, pas un refus.");
                }
            }

            indexedKeys = _itemIndex.Count;
            return _itemIndex.TryGetValue(key, out position);
        }

        /// <summary>Objet du monde portant cette clé, s'il existe encore.</summary>
        ///
        /// Distinct de <see cref="TryResolveWorldItem"/>, qui rend une position indexée : ici on veut
        /// l'objet lui-même, pour le retirer. Le parcours est refait à neuf plutôt que de mémoriser des
        /// références, parce qu'une référence à un objet détruit par le jeu ne se distingue pas d'une
        /// référence nulle et ferait croire à un retrait réussi.
        /// </summary>
        private bool TryResolveWorldItemObject(long key, out Pickupable item)
        {
            item = null;
            if (key == 0L)
            {
                return false;
            }

            foreach (var candidate in ItemIdentity.CollectWorldItems())
            {
                if (candidate == null || ItemIdentity.KeyOf(candidate, out _) != key)
                {
                    continue;
                }

                item = candidate;
                return true;
            }

            return false;
        }

        /// <summary>Nom lisible du détenteur, pour que le refus dise **qui** détient l'objet.</summary>
        private string DescribeHolder(ushort playerId)
        {
            foreach (var pair in _sessions)
            {
                if (pair.Value.PlayerId == playerId)
                {
                    return $"'{pair.Value.Username}'#{playerId}";
                }
            }

            return $"id {playerId}";
        }

        private void HandlePlayerEquipment(NetPeer peer, ClientSession session, PlayerEquipmentMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] équipement avant handshake ignoré de {peer}");
                return;
            }

            // Le serveur est autoritaire sur l'identité de l'émetteur : un client ne peut pas
            // prétendre modifier la tenue d'un autre joueur. Le champ `PlayerId` reçu n'est donc
            // **jamais** recopié — il sert seulement à constater la falsification, et c'est cette
            // constatation qui rend la règle mesurable au lieu de lisible.
            var forgedEquipment = msg.PlayerId != 0 && msg.PlayerId != session.PlayerId;
            if (forgedEquipment)
            {
                ForgedPlayerIds++;
                _log.LogWarning(
                    $"[net] PlayerId falsifié sur l'équipement : '{session.Username}' a annoncé sous l'id " +
                    $"{msg.PlayerId} (le sien est {session.PlayerId}) — le relais réécrit avec l'id de la session.");
                _log.LogInfo(
                    $"[net] vérification PlayerId (équipement) : '{session.Username}' a annoncé l'id " +
                    $"{msg.PlayerId}, effectif={session.PlayerId} -> verdict=True");
            }

            session.Equipment = msg.TechTypeNames ?? Array.Empty<string>();
            session.EquipmentsReceived++;

            var summary = session.Equipment.Length == 0
                ? "(sans équipement)"
                : string.Join(", ", session.Equipment);
            _log.LogInfo(
                $"[net] équipement reçu de '{session.Username}'#{session.PlayerId} " +
                $"({session.Equipment.Length} pièce(s)) : {summary}");

            var outgoing = new PlayerEquipmentMessage
            {
                PlayerId = session.PlayerId,
                TechTypeNames = session.Equipment,
            };

            var forwarded = 0;
            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted || pair.Key == peer)
                {
                    continue;
                }

                Send(pair.Key, outgoing.Write, DeliveryMethod.ReliableOrdered);
                forwarded++;
            }

            _log.LogInfo(
                $"[net] PlayerEquipment de '{session.Username}'#{session.PlayerId} diffusé à {forwarded} autre(s) client(s).");
            if (forgedEquipment)
            {
                _log.LogInfo(
                    $"[net] vérification PlayerId (équipement) : diffusé sous l'id {session.PlayerId} " +
                    $"(revendiqué {msg.PlayerId}) pour {forwarded} client(s) -> verdict=True");
            }
        }

        private static string[] ReadHostEquipment()
        {
            var inventory = Inventory.main;
            var equipment = inventory != null ? inventory.equipment : null;
            if (equipment == null)
            {
                return null;
            }

            var names = new List<string>();
            var slots = equipment.GetEquipment();
            while (slots.MoveNext())
            {
                var item = slots.Current.Value;
                var pickupable = item != null ? item.item : null;
                if (pickupable == null)
                {
                    continue;
                }

                names.Add(pickupable.GetTechType().ToString());
            }

            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        private void BroadcastHostEquipmentIfChanged()
        {
            var hostEquip = ReadHostEquipment();
            if (hostEquip == null)
            {
                return;
            }

            var signature = string.Join("|", hostEquip);
            if (string.Equals(signature, _lastHostEquipmentSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastHostEquipmentSignature = signature;
            _lastHostEquipment = hostEquip;

            var outgoing = new PlayerEquipmentMessage
            {
                PlayerId = 9999,
                TechTypeNames = hostEquip,
            };

            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted)
                {
                    continue;
                }

                Send(pair.Key, outgoing.Write, DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>
        /// Nom de l'objet tenu par le personnage de l'instance serveur, ou chaîne vide s'il n'a rien
        /// en main. Renvoie <c>null</c> si l'inventaire local n'est pas lisible (monde non chargé).
        /// </summary>
        private static string ReadHostHeldItemName()
        {
            var inventory = Inventory.main;
            if (inventory == null)
            {
                return null;
            }

            var tool = inventory.GetHeldTool();
            if (tool == null)
            {
                return string.Empty;
            }

            var pickupable = tool.GetComponent<Pickupable>();
            return pickupable != null ? pickupable.GetTechType().ToString() : string.Empty;
        }

        /// <summary>
        /// Paramètres `holding_*` du personnage hôte, **lus sur son propre `Animator`** — exactement
        /// comme un client le fait pour lui-même, et non plus re-dérivés du nom de l'objet tenu.
        ///
        /// Pourquoi lire et non dériver : le jeu pose ces booléens depuis les `ConditionRules` de
        /// chaque prefab d'outil (`ArmsController` en expose une pour `holding_welder`), et la règle
        /// « `holding_` + nom du `TechType` en minuscules » n'en est qu'une conséquence la plupart du
        /// temps. Deux façons de calculer la même donnée finiraient par diverger en silence : ici la
        /// source est unique pour l'hôte comme pour les clients.
        ///
        /// Renvoie <c>null</c> si l'état n'est pas lisible (pas de joueur, pas d'animator) : c'est
        /// différent d'un tableau vide, qui veut dire « rien en main ».
        /// </summary>
        private static string[] ReadHostHoldingParams()
        {
            var hostPlayer = UnityEngine.Object.FindObjectOfType<Player>();
            if (hostPlayer == null || hostPlayer.gameObject == null)
            {
                return null;
            }

            var animator = hostPlayer.gameObject.GetComponentInChildren<UnityEngine.Animator>(true);
            if (animator == null)
            {
                return null;
            }

            var holding = new List<string>();
            var parameters = animator.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter.type != UnityEngine.AnimatorControllerParameterType.Bool ||
                    !parameter.name.StartsWith(Protocol.HoldingParamPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (animator.GetBool(parameter.name))
                {
                    holding.Add(parameter.name);
                }
            }

            holding.Sort(StringComparer.Ordinal);
            return holding.ToArray();
        }

        private void BroadcastHostHeldItemIfChanged()
        {
            var name = ReadHostHeldItemName();
            var parameters = ReadHostHoldingParams();
            if (name == null || parameters == null)
            {
                return;
            }

            var signature = name + "|" + string.Join("|", parameters);
            if (string.Equals(signature, _lastHostHeldItemSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastHostHeldItemSignature = signature;

            var outgoing = new PlayerHeldItemMessage
            {
                PlayerId = 9999,
                TechTypeName = name,
                HoldingParams = parameters,
            };

            var sent = 0;
            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted)
                {
                    continue;
                }

                Send(pair.Key, outgoing.Write, DeliveryMethod.ReliableOrdered);
                sent++;
            }

            _log.LogInfo(
                $"[net] objet tenu de l'hôte modifié : {(name.Length == 0 ? "(rien en main)" : name)} " +
                $"-> diffusé à {sent} client(s) (paramètres lus sur son animator : " +
                (parameters.Length == 0 ? "(aucun)" : string.Join(", ", parameters)) + ")");
        }

        /// <summary>
        /// Diffuse à chaque tick (20 Hz) l'état de position de tous les autres joueurs connectés
        /// à chaque client accepté.
        /// </summary>
        public void BroadcastSnapshots(long serverTick)
        {
            if (_sessions.Count == 0)
            {
                return;
            }

            var acceptedCount = AcceptedClients;
            if (acceptedCount == 0)
            {
                return;
            }

            // Si l'hôte (le joueur local de l'instance serveur) est dans le monde, on l'inclut
            // comme joueur virtuel id=9999 afin qu'un client seul puisse aussi voir le personnage
            // de l'instance serveur dans le monde !
            //
            // **Sauf en serveur dédié** : la instance headless n'a pas de personnage joueur, et diffuser
            // quand même un id=9999 ferait apparaître à la capsule un avatar que personne ne pilote —
            // exactement ce que « le serveur n'est pas un personnage » interdit.
            var hostPlayer = CoopServerPlugin.Dedicated ? null : UnityEngine.Object.FindObjectOfType<Player>();
            var hasHostAvatar = hostPlayer != null && hostPlayer.gameObject != null;
            if (hasHostAvatar && serverTick % 5 == 0)
            {
                BroadcastHostEquipmentIfChanged();
                BroadcastHostHeldItemIfChanged();
            }

            // Les créatures voyagent moins souvent que les joueurs : elles bougent plus lentement, et
            // doubler la cadence doublerait la bande passante pour rien.
            if (serverTick % Protocol.CreatureStreamIntervalTicks == 0)
            {
                BroadcastCreatureSnapshots(serverTick, hostPlayer);
            }

            // Les véhicules voyagent à la même cadence que les créatures (10 Hz). La mesure du §30 a
            // montré qu'un Seamoth à 20 m/s produit alors des pas de 2,15 m : c'est **l'interpolation
            // côté client** qui rend le mouvement continu, pas une cadence plus élevée (voir
            // `RemoteVehicleManager`).
            if (serverTick % Protocol.VehicleStreamIntervalTicks == 0)
            {
                BroadcastVehicleSnapshots(serverTick, hostPlayer);
            }

            foreach (var recipientPair in _sessions)
            {
                var recipientSession = recipientPair.Value;
                if (!recipientSession.Accepted)
                {
                    continue;
                }

                var otherCount = acceptedCount - 1 + (hasHostAvatar ? 1 : 0);
                if (otherCount <= 0)
                {
                    continue;
                }

                var states = new PlayerStateEntry[otherCount];
                var idx = 0;

                foreach (var sourcePair in _sessions)
                {
                    var source = sourcePair.Value;
                    if (!source.Accepted || sourcePair.Key == recipientPair.Key)
                    {
                        continue;
                    }

                    states[idx++] = new PlayerStateEntry
                    {
                        PlayerId = source.PlayerId,
                        X = source.X,
                        Y = source.Y,
                        Z = source.Z,
                        RotX = source.RotX,
                        RotY = source.RotY,
                        RotZ = source.RotZ,
                        RotW = source.RotW,
                        MoveSpeedX = source.MoveSpeedX,
                        MoveSpeedY = source.MoveSpeedY,
                        MoveSpeedZ = source.MoveSpeedZ,
                        ViewPitch = source.ViewPitch,
                        IsUnderwater = source.IsUnderwater,
                        OnSurface = source.OnSurface,
                        ActionFlags = source.ActionFlags,
                    };
                }

                if (hasHostAvatar && idx < states.Length)
                {
                    var tr = hostPlayer.transform;
                    var pos = tr.position;
                    var rot = ViewDirection.Yaw(tr);
                    var arms = hostPlayer.armsController;
                    var vel = arms != null ? arms.GetSmoothedVelocity() : UnityEngine.Vector3.zero;
                    var pitch = MainCameraControl.main != null ? MainCameraControl.main.GetCameraPitch() : 0f;
                    var isUnderwater = hostPlayer.IsUnderwater() && hostPlayer.motorMode != Player.MotorMode.Vehicle;
                    var waterLevel = hostPlayer.GetWaterLevel();
                    var onSurface = pos.y > waterLevel - 1f && pos.y < waterLevel + 1f
                                    && !hostPlayer.IsInside() && hostPlayer.IsUnderwaterForSwimming();

                    // Mêmes sources que celles du client, relevées dans `ArmsController.Update` :
                    // `using_pda` vient de `Player.GetPDA().isInUse`, `using_tool` et `using_tool_alt` de
                    // `GUIHand.GetUsingTool()` / `GetAltAttacking()`, les véhicules de `inSeamoth` /
                    // `inExosuit`.
                    var hostFlags = PlayerActionFlags.None;
                    var pda = hostPlayer.GetPDA();
                    if (pda != null && pda.isInUse)
                    {
                        hostFlags |= PlayerActionFlags.UsingPda;
                    }

                    var guiHand = hostPlayer.guiHand;
                    if (guiHand != null)
                    {
                        if (guiHand.GetUsingTool())
                        {
                            hostFlags |= PlayerActionFlags.UsingTool;
                        }

                        if (guiHand.GetAltAttacking())
                        {
                            hostFlags |= PlayerActionFlags.UsingToolAlt;
                        }
                    }

                    if (hostPlayer.inSeamoth)
                    {
                        hostFlags |= PlayerActionFlags.InSeamoth;
                    }

                    if (hostPlayer.inExosuit)
                    {
                        hostFlags |= PlayerActionFlags.InExosuit;
                    }

                    states[idx++] = new PlayerStateEntry
                    {
                        PlayerId = 9999,
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
                        ActionFlags = hostFlags,
                    };
                }

                var snapshot = new PlayersSnapshotMessage
                {
                    ServerTick = serverTick,
                    Players = states,
                };

                Send(recipientPair.Key, snapshot.Write, DeliveryMethod.Unreliable);
                _snapshotsBroadcast++;
            }
        }

        /// <summary>
        /// Diffuse l'état des créatures que **le serveur** simule (M3).
        ///
        /// Le partage des rôles est celui du plan (§4) : le serveur simule, les clients suivent. Il
        /// n'y a donc rien à recevoir ici, et rien à négocier — seulement à lire puis à envoyer.
        ///
        /// Le filtrage est un vrai relevancy, pas une coupe arbitraire : une créature n'est envoyée
        /// que si elle est à moins de <see cref="Protocol.CreatureRelevancyRadiusMeters"/> d'un joueur
        /// concerné. Envoyer tout le monde à tout le monde ne passerait pas le 20 Hz du §3.
        /// </summary>
        private void BroadcastCreatureSnapshots(long serverTick, Player hostPlayer)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_creatureCache == null || now - _creatureCacheRealtime >= CreatureCacheSeconds)
            {
                _creatureCache = UnityEngine.Object.FindObjectsOfType<Creature>();
                _creatureCacheRealtime = now;
                ReportCreatureIdentities();
            }

            if (_creatureCache == null || _creatureCache.Length == 0)
            {
                return;
            }

            var radiusSquared = Protocol.CreatureRelevancyRadiusMeters * Protocol.CreatureRelevancyRadiusMeters;

            // Diagnostic : « 0 créature diffusée » a deux causes très différentes — le monde est vide
            // autour du joueur, ou le rayon est trop petit. Sans cette mesure, les deux se
            // ressemblent, et on réglerait un rayon au hasard.
            ReportVehicles(hostPlayer, now);

            if (now >= _nextCreatureDiagnostic)
            {
                _nextCreatureDiagnostic = now + 5f;
                var nearest = NearestCreatures(hostPlayer, 3);
                _log.LogInfo(
                    nearest.Count == 0
                        ? $"[net] créatures : {_creatureCache.Length} dans le monde, aucune position lisible"
                        : $"[net] créatures : {_creatureCache.Length} dans le monde, les plus proches du point " +
                          $"{nearest[0].Reference.ToString("F1")} : " +
                          $"{string.Join(", ", nearest.Select(entry => $"'{entry.Name}' à {entry.Meters:F1} m"))} " +
                          $"(rayon de diffusion {Protocol.CreatureRelevancyRadiusMeters:F0} m)");
            }

            foreach (var recipientPair in _sessions)
            {
                var recipientSession = recipientPair.Value;
                if (!recipientSession.Accepted)
                {
                    continue;
                }

                _creatureBuffer.Clear();
                _creatureCandidates.Clear();

                // Le relevancy est celui du **destinataire**, pas celui de l'hôte : un client ne peut
                // suivre que les créatures qui existent dans son propre monde, et son monde n'a de
                // cellules chargées qu'autour de son joueur à lui.
                //
                // La première version prenait la plus petite distance à l'un ou l'autre (client ou
                // hôte) : mesuré, cela ne diffusait que des créatures proches de l'hôte, dont le monde
                // du client n'avait qu'une poignée — 128 appariements sur 36 452 états, et un avatar de
                // créature adopté puis relâché aussitôt. Le repli sur la position de l'hôte ne sert
                // donc plus qu'à un client qui n'a pas encore annoncé la sienne (juste après le
                // handshake), pour qu'il reçoive quelque chose plutôt que rien.
                var interestX = recipientSession.PositionsReceived > 0 ? recipientSession.X : 0f;
                var interestY = recipientSession.PositionsReceived > 0 ? recipientSession.Y : 0f;
                var interestZ = recipientSession.PositionsReceived > 0 ? recipientSession.Z : 0f;
                var fromSession = recipientSession.PositionsReceived > 0;

                if (!fromSession && hostPlayer != null && hostPlayer.gameObject != null)
                {
                    var hostPosition = hostPlayer.transform.position;
                    interestX = hostPosition.x;
                    interestY = hostPosition.y;
                    interestZ = hostPosition.z;
                }

                for (var i = 0; i < _creatureCache.Length; i++)
                {
                    var creature = _creatureCache[i];
                    if (creature == null || creature.transform == null)
                    {
                        continue;
                    }

                    var position = creature.transform.position;
                    var dx = position.x - interestX;
                    var dy = position.y - interestY;
                    var dz = position.z - interestZ;
                    var distanceSquared = dx * dx + dy * dy + dz * dz;

                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    _creatureCandidates.Add(new KeyValuePair<float, int>(distanceSquared, i));
                }

                if (_creatureCandidates.Count == 0)
                {
                    continue;
                }

                // Les plus proches d'abord : quand le plafond mord, ce sont les créatures du bout du
                // rayon qui sont sacrifiées, pas celles devant le joueur.
                _creatureCandidates.Sort((left, right) => left.Key.CompareTo(right.Key));

                var asKeyed = 0;
                var asSpawn = 0;
                var refused = 0;
                var sentChunks = 0;
                var serverMetres = 0f;
                var serverSeconds = 0f;
                var maxServerMovedMeters = 0f;
                var maxServerMovedSeconds = 0f;
                var maxServerMovedLabel = "aucune";

                // Jusqu'où la diffusion est allée dans la liste (triée du plus proche au plus loin) : la
                // différence avec le nombre de candidates est le nombre de créatures **sacrifiées au
                // plafond**, c'est-à-dire jamais envoyées — leur IA continue de tourner chez le client.
                var reached = 0;

                // Un paquet ne peut pas porter plus de `MaxCreatureStates` créatures : la liste part
                // donc en plusieurs paquets, sinon la queue du rayon n'est jamais diffusée et les
                // créatures déjà suivies sortent de la liste puis y reviennent (IA coupée/rétablie en
                // boucle, mesuré une fois par seconde avant ce découpage).
                for (var chunk = 0; chunk < CreatureChunksPerTick; chunk++)
                {
                    var start = chunk * Protocol.MaxCreatureStates;
                    if (start >= _creatureCandidates.Count)
                    {
                        break;
                    }

                    _creatureBuffer.Clear();
                    var end = System.Math.Min(start + Protocol.MaxCreatureStates, _creatureCandidates.Count);

                    for (var i = start; i < end; i++)
                    {
                        var creature = _creatureCache[_creatureCandidates[i].Value];
                        var key = CreatureIdentity.KeyOf(creature, out var mechanism);
                        if (key == 0L)
                        {
                            // Sans clé, le client ne pourrait pas la reconnaître : la diffuser ferait
                            // correspondre n'importe quoi. Elle est comptée, jamais inventée.
                            refused++;
                            continue;
                        }

                        if (mechanism == CreatureIdentity.MechanismUniqueId)
                        {
                            asKeyed++;
                        }
                        else
                        {
                            asSpawn++;
                        }

                        var transform = creature.transform;
                        var position = transform.position;
                        var rotation = transform.rotation;

                        // Déplacement du serveur depuis la dernière diffusion de **cette** créature. Le rythme
                        // réel est mesuré, pas supposé : la diffusion passe tous les
                        // `CreatureStreamIntervalTicks` ticks, mais un paquet non fiable peut manquer.
                        if (_lastSentPositions.TryGetValue(key, out var previous))
                        {
                            var seconds = now - previous.Realtime;
                            if (seconds > 0.001f)
                            {
                                var metres = UnityEngine.Vector3.Distance(previous.Position, position);
                                serverMetres = metres;
                                serverSeconds = seconds;
                                if (metres > maxServerMovedMeters)
                                {
                                    maxServerMovedMeters = metres;
                                    maxServerMovedSeconds = seconds;
                                    maxServerMovedLabel = creature.gameObject != null ? creature.gameObject.name : "?";
                                }
                            }
                        }

                        _lastSentPositions[key] = new SentPosition { Position = position, Realtime = now };

                        _creatureBuffer.Add(new CreatureStateEntry
                        {
                            Key = key,
                            X = position.x,
                            Y = position.y,
                            Z = position.z,
                            RotX = rotation.x,
                            RotY = rotation.y,
                            RotZ = rotation.z,
                            RotW = rotation.w,
                        });
                    }

                    if (_creatureBuffer.Count == 0)
                    {
                        continue;
                    }

                    var snapshot = new CreatureSnapshotMessage
                    {
                        ServerTick = serverTick,
                        Creatures = _creatureBuffer.ToArray(),
                    };

                    _writer.Reset();
                    snapshot.Write(_writer);
                    if (_writer.Length > Protocol.MaxUnreliablePayloadBytes)
                    {
                        _oversizedCreaturePackets++;
                        if (_oversizedCreaturePackets <= 3)
                        {
                            _log.LogError(
                                $"[net] paquet de créatures REFUSÉ : {_writer.Length} octets pour " +
                                $"{_creatureBuffer.Count} créature(s), au-dessus de la limite non fiable de " +
                                $"{Protocol.MaxUnreliablePayloadBytes} octets. LiteNetLib l'aurait jeté sans le dire " +
                                "dans nos journaux.");
                        }

                        break;
                    }

                    recipientPair.Key.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.Unreliable);
                    _creatureSnapshotsBroadcast++;
                    _creatureStatesBroadcast += _creatureBuffer.Count;
                    recipientSession.CreatureBytesSent += _writer.Length;
                    recipientSession.CreatureStatesSent += _creatureBuffer.Count;
                    recipientSession.CreaturePacketsSent++;
                    reached = end;
                    sentChunks++;
                }

                if (sentChunks == 0)
                {
                    continue;
                }

                if (now < recipientSession.NextCreatureReport)
                {
                    continue;
                }

                recipientSession.NextCreatureReport = now + CreatureReportSeconds;

                // Clés partagées parmi les candidates : l'identité est-elle injective ? Un seul
                // contre-exemple suffit à dire que « une clé = une créature » ne tient pas, et c'est
                // l'hypothèse sur laquelle repose tout le suivi (cf. PLAN.md §28).
                _creatureKeyTally.Clear();
                var sharedKeys = 0;
                for (var i = 0; i < _creatureCandidates.Count; i++)
                {
                    var candidate = _creatureCache[_creatureCandidates[i].Value];
                    var candidateKey = CreatureIdentity.KeyOf(candidate, out _);
                    if (candidateKey == 0L)
                    {
                        continue;
                    }

                    _creatureKeyTally.TryGetValue(candidateKey, out var already);
                    if (already > 0)
                    {
                        sharedKeys++;
                    }

                    _creatureKeyTally[candidateKey] = already + 1;
                }

                var sacrificed = _creatureCandidates.Count - reached;

                // La liste est triée du plus proche au plus loin, donc la première sacrifiée est la plus
                // proche de toutes — c'est elle qui dit si le plafond mord dans ce que le joueur voit.
                var closestSacrificed = sacrificed > 0
                    ? (float)System.Math.Sqrt(_creatureCandidates[reached].Key)
                    : 0f;

                var bytesPerSecond = recipientSession.CreatureBytesSent / CreatureReportSeconds;

                _log.LogInfo(
                    $"[net] créatures : {_creatureCache.Length} dans le monde, " +
                    $"{_creatureCandidates.Count} à moins de {Protocol.CreatureRelevancyRadiusMeters:F0} m " +
                    // Le point d'intérêt est écrit parce que deux destinataires qui reçoivent des ensembles
                    // différents ne disent rien tant qu'on ne sait pas **d'où** le rayon est compté :
                    // position du client, ou repli sur l'hôte quand il n'en a pas encore annoncé.
                    $"du point ({interestX:F1}, {interestY:F1}, {interestZ:F1}) " +
                    $"[{(fromSession ? "position du client" : "repli position de l'hôte")}], " +
                    $"{asSpawn + asKeyed} diffusée(s) en {sentChunks} paquet(s) à " +
                    $"'{recipientSession.Username}'#{recipientSession.PlayerId} " +
                    $"({asSpawn} par {CreatureIdentity.MechanismSpawnPosition}, " +
                    $"{asKeyed} par {CreatureIdentity.MechanismUniqueId}, {refused} sans clé écartée(s)), " +
                    $"{sacrificed} sacrifiée(s) au plafond de " +
                    $"{CreatureChunksPerTick * Protocol.MaxCreatureStates}" +
                    (CreatureChunksOverridden ? " (plafond ABAISSÉ pour éprouver la garde)" : string.Empty) +
                    (sacrificed > 0 ? $" (la plus proche à {closestSacrificed:F0} m)" : string.Empty) + ", " +
                    $"clés partagées={sharedKeys} (sur {_creatureCandidates.Count} candidates), " +
                    // « non mesuré » et « 0 m » ne sont pas la même chose : le maximum vaut 0 quand aucune clé
                    // n'a été diffusée deux fois dans la fenêtre (un destinataire qui vient de se connecter),
                    // et l'annoncer comme « 0 m » ferait croire à un serveur immobile qui n'a pas été observé.
                    (maxServerMovedSeconds > 0f
                        ? $"déplacement serveur max={maxServerMovedMeters:F2} m en {maxServerMovedSeconds:F2} s " +
                          $"({maxServerMovedMeters / maxServerMovedSeconds:F1} m/s, '{maxServerMovedLabel}'), "
                        : "déplacement serveur : non mesuré (aucune clé diffusée deux fois dans la fenêtre), ") +
                    $"débit={bytesPerSecond / 1024f:F1} Ko/s " +
                    $"({recipientSession.CreatureStatesSent} état(s) en {recipientSession.CreaturePacketsSent} paquet(s) " +
                    $"sur {CreatureReportSeconds:F0} s), " +
                    $"total envoyé={_creatureSnapshotsBroadcast} snapshot(s) / {_creatureStatesBroadcast} état(s)" +
                    (_oversizedCreaturePackets == 0
                        ? string.Empty
                        : $", {_oversizedCreaturePackets} paquet(s) REFUSÉ(S) pour dépassement de taille"));

                // Le sacrifice est assumé : ce sont les créatures du **bout** du rayon (le tri est du plus
                // proche au plus loin), dont l'IA continue de tourner chez le client. Mais si le plafond
                // mord dans une créature que le joueur a sous les yeux, ce n'est plus le même compromis —
                // et c'est exactement le genre de bascule qui ne doit pas se produire en silence.
                if (sacrificed > 0 && closestSacrificed < CloseSacrificeMeters && _closeSacrificeWarnings < 3)
                {
                    _closeSacrificeWarnings++;
                    _log.LogWarning(
                        $"[net] créatures : une créature à {closestSacrificed:F0} m n'est PAS diffusée à " +
                        $"'{recipientSession.Username}'#{recipientSession.PlayerId} — le plafond de " +
                        $"{CreatureChunksPerTick * Protocol.MaxCreatureStates} est atteint alors que " +
                        $"{_creatureCandidates.Count} créatures sont dans le rayon. Le compromis « on ne suit que " +
                        "le proche » ne tient plus : il faut changer le plafond ou le rayon.");
                }

                recipientSession.CreatureBytesSent = 0;
                recipientSession.CreatureStatesSent = 0;
                recipientSession.CreaturePacketsSent = 0;
            }
        }

        /// <summary>
        /// Diffuse l'état des véhicules du monde serveur (M4), à la même cadence que les créatures.
        ///
        /// Ce que le message porte, et ce qu'il ne porte pas : **la position et l'orientation**, pas
        /// l'énergie ni les modules. C'est ce que la mesure du §30 a demandé — un véhicule qui se déplace
        /// — et c'est ce qui manquait au protocole. L'énergie et les upgrades restent hors de M4.
        ///
        /// Le filtrage est un relevancy par destinataire, comme les créatures : un véhicule n'est envoyé
        /// qu'à un client à moins de <see cref="Protocol.VehicleRelevancyRadiusMeters"/> de lui. Le rayon
        /// est plus large que celui des créatures (150 m contre 120 m), parce qu'un Seamoth se voit de
        /// loin et surtout se conduit de loin.
        ///
        /// La clé vient de <see cref="VehicleIdentity.KeyOf"/>, calculée des deux côtés. Un véhicule sans
        /// clé est **compté et nommé**, jamais diffusé avec une clé inventée : deux véhicules confondus
        /// vaudraient moins bien qu'un véhicule non synchronisé.
        /// </summary>
        /// <summary>
        /// Annonce les véhicules **apparus depuis le dernier balayage** (v10, M6).
        ///
        /// Un véhicule fabriqué à l'exécution (`spawn`, épave qui se détache, futur objet de contenu) n'est
        /// pas dans la sauvegarde : les clients ne l'ont pas, et le flux de positions ne leur donnait
        /// jusqu'ici qu'une clé sans objet à déplacer. Le premier balayage **enregistre** les véhicules
        /// déjà présents sans rien annoncer — sans quoi un client qui se connecte verrait arriver une
        /// annonce pour chaque Seamoth de la carte.
        ///
        /// Une entité sans identifiant n'est pas annoncée : le client ne pourrait pas la faire correspondre,
        /// et une entité en double est pire qu'une entité absente. C'est journalisé.
        /// </summary>
        private void BroadcastNewVehicles()
        {
            if (_vehicleCache == null)
            {
                return;
            }

            var baseline = !_vehicleBaselineDone;
            _vehicleBaselineDone = true;
            var announced = 0;
            var present = new HashSet<long>();

            foreach (var vehicle in _vehicleCache)
            {
                if (vehicle == null || vehicle.gameObject == null)
                {
                    continue;
                }

                var key = VehicleIdentity.KeyOf(vehicle, out _);
                if (key != 0L)
                {
                    present.Add(key);
                }

                if (key == 0L || !_knownVehicles.Add(key))
                {
                    continue;
                }

                if (baseline)
                {
                    continue;
                }

                if (!VehicleIdentity.TryGetUniqueId(vehicle, out var uniqueId))
                {
                    _log.LogWarning(
                        $"[net] apparition : '{vehicle.gameObject.name}' (clé=0x{key:x16}) sans identifiant " +
                        "d'entité — NON annoncée : le client ne pourrait pas la faire correspondre.");
                    continue;
                }

                // `CraftData.GetTechType` lit le `TechTag` du prefab : c'est l'espèce (`SeaMoth`), pas le nom
                // de l'instance (`SeaMoth(Clone)`), et c'est ce que le client sait résoudre pour instancier.
                var tech = CraftData.GetTechType(vehicle.gameObject).ToString();
                if (tech.Length > Protocol.MaxTechTypeNameLength)
                {
                    _log.LogWarning(
                        $"[net] apparition : nom d'espèce trop long pour '{vehicle.gameObject.name}' — non annoncée.");
                    continue;
                }

                var transform = vehicle.transform;
                var position = transform != null ? transform.position : UnityEngine.Vector3.zero;
                var rotation = transform != null ? transform.rotation : UnityEngine.Quaternion.identity;

                // P1bis : la définition voyage avec l'annonce, et son empreinte est vérifiable.
                // Un véhicule du jeu a une échelle neutre ; le contrat existe pour que le client
                // puisse **refuser** une définition divergente au lieu de la supposer.
                var definition = EntityDefinition.ForVehicle(tech);

                var message = new EntitySpawnMessage
                {
                    Key = key,
                    UniqueId = uniqueId,
                    TechTypeName = tech,
                    X = position.x,
                    Y = position.y,
                    Z = position.z,
                    RotX = rotation.x,
                    RotY = rotation.y,
                    RotZ = rotation.z,
                    RotW = rotation.w,
                    ScaleX = definition.ScaleX,
                    ScaleY = definition.ScaleY,
                    ScaleZ = definition.ScaleZ,
                    DefinitionHash = definition.ComputeHash(),
                };

                // Mémorisé **avant** l'envoi : un client qui se connecte pendant la diffusion doit trouver
                // l'entité dans le carnet, sinon il ne la verrait jamais.
                RememberSpawn(message);

                var sent = 0;
                foreach (var pair in _sessions)
                {
                    if (!pair.Value.Accepted)
                    {
                        continue;
                    }

                    Send(pair.Key, message.Write, DeliveryMethod.ReliableOrdered);
                    sent++;
                }

                _entitySpawnsSent++;
                announced++;

                _log.LogInfo(
                    $"[net] apparition : clé=0x{key:x16} '{vehicle.gameObject.name}' ({tech}) — " +
                    $"diffusée à {sent} client(s), pos=({position.x:F1}, {position.y:F1}, {position.z:F1}), " +
                    $"identité '{uniqueId}', définition=0x{message.DefinitionHash:x16}.");
            }

            if (announced > 0)
            {
                _log.LogInfo($"[net] apparition : {announced} entité(s) annoncée(s) ce balayage.");
            }

            // Les clés connues qui ne sont plus dans le monde ont **disparu** : on les annonce (v11).
            // Sans ce balayage, un véhicule détruit chez l'hôte restait chez tous les clients, immobile.
            // Le premier passage ne peut rien annoncer : `_knownVehicles` vient d'être rempli.
            List<long> vanished = null;
            foreach (var known in _knownVehicles)
            {
                if (!present.Contains(known))
                {
                    (vanished ?? (vanished = new List<long>())).Add(known);
                }
            }

            if (vanished == null)
            {
                return;
            }

            foreach (var key in vanished)
            {
                _knownVehicles.Remove(key);
                BroadcastDespawn(key, EntityKind.Vehicle, "véhicule détruit chez le serveur");
            }
        }

        /// <summary>
        /// Annonce une disparition à **tous** les clients admis (v11, M6), et oublie l'entité du carnet.
        ///
        /// Le retrait n'est jamais demandé par un client : c'est le serveur qui constate qu'une entité
        /// n'existe plus (véhicule détruit) ou qu'il vient de la retirer (objet ramassé). Le client, lui, ne
        /// fait que constater — détruire sa copie, ou ne rien faire si elle est déjà dans un inventaire.
        /// </summary>
        /// <param name="exceptPlayerId">
        /// Joueur à **exclure** de la diffusion, ou `0` pour aucun. Utile au ramassage : celui qui vient de
        /// ramasser est le seul à qui le retrait ne doit pas être annoncé — son objet est déjà dans son
        /// inventaire, et le retirer avant qu'il n'ait fini son geste l'empêcherait de le ramasser pour de
        /// vrai (mesuré à l'étape L : `accordée=True` côté serveur, `objet dans l'inventaire=False` côté
        /// client).
        /// </param>
        private void BroadcastDespawn(long key, byte kind, string reason, ushort exceptPlayerId = 0)
        {
            if (key == 0L)
            {
                return;
            }

            // Une entité disparue ne doit plus être rejouée à un client qui se connecte après coup.
            _spawnedEntities.Remove(key);

            var message = new EntityDespawnMessage
            {
                Key = key,
                Kind = kind,
                Reason = reason,
            };

            RememberDespawn(message);

            var sent = 0;
            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted)
                {
                    continue;
                }

                if (exceptPlayerId != 0 && pair.Value.PlayerId == exceptPlayerId)
                {
                    continue;
                }

                Send(pair.Key, message.Write, DeliveryMethod.ReliableOrdered);
                sent++;
            }

            _entityDespawnsSent++;

            var what = kind == EntityKind.Vehicle ? "véhicule" : "objet";
            _log.LogInfo(
                $"[net] disparition : clé=0x{key:x16} ({what}) — annoncée à {sent} client(s), motif « {reason} »" +
                (exceptPlayerId != 0 ? $", sauf au détenteur (id {exceptPlayerId})" : string.Empty) + ".");
        }


        /// <summary>Range une annonce dans le carnet, en évinçant la plus ancienne au-delà de la borne.</summary>
        private void RememberSpawn(EntitySpawnMessage message)
        {
            if (!_spawnedEntities.ContainsKey(message.Key))
            {
                if (_spawnOrder.Count >= MaxRememberedSpawns)
                {
                    var oldest = _spawnOrder.Dequeue();
                    _spawnedEntities.Remove(oldest);
                    _log.LogWarning(
                        $"[net] apparition : carnet plein ({MaxRememberedSpawns}) — clé=0x{oldest:x16} oubliée " +
                        "(un client qui se connecte maintenant ne la recevra pas).");
                }

                _spawnOrder.Enqueue(message.Key);
            }

            _spawnedEntities[message.Key] = message;

            // Une clé qui réapparaît n'est plus « disparue » : le carnet de retraits doit oublier
            // l'ancienne annonce, sinon le client recréerait puis retirerait la même entité.
            _despawnedEntities.Remove(message.Key);
        }

        /// <summary>
        /// Range un retrait dans le carnet, borné comme celui des apparitions.
        ///
        /// Un client qui rejoint après un ramassage charge la **même sauvegarde**, qui contient
        /// encore l'objet : sans ce rejeu, il le verrait et pourrait le réclamer, alors que le
        /// serveur l'a déjà accordé à quelqu'un. C'est le défaut que la v11 avait fermé pour les
        /// clients présents, et qui restait ouvert pour les suivants (P2).
        /// </summary>
        private void RememberDespawn(EntityDespawnMessage message)
        {
            if (message == null || message.Key == 0L)
            {
                return;
            }

            // Une clé peut réapparaître puis disparaître de nouveau : la file ne doit pas la contenir
            // deux fois, sinon une éviction consommerait un créneau sans rien retirer et le carnet
            // grossirait au-delà de sa borne. 256 entrées au plus, donc `Contains` est sans coût notable.
            if (!_despawnedEntities.ContainsKey(message.Key) && !_despawnOrder.Contains(message.Key))
            {
                if (_despawnOrder.Count >= MaxRememberedDespawns)
                {
                    var oldest = _despawnOrder.Dequeue();
                    _despawnedEntities.Remove(oldest);
                    _log.LogWarning(
                        $"[net] disparition : carnet plein ({MaxRememberedDespawns}) — clé=0x{oldest:x16} oubliée " +
                        "(un client qui se connecte maintenant ne la recevra pas).");
                }

                _despawnOrder.Enqueue(message.Key);
            }

            _despawnedEntities[message.Key] = message;
        }

        /// <summary>
        /// Rejoue à un client qui vient d'être admis les entités fabriquées avant son arrivée.
        ///
        /// Pourquoi c'est nécessaire : les entités de la **sauvegarde** existent déjà chez lui (il charge la
        /// même partie), une entité **fabriquée** non. Mesuré à l'étape N : le deuxième client se connecte
        /// après l'annonce et ne reçoit rien — verdict False à 150 s, alors que le premier a tout créé.
        /// </summary>
        private void SendSpawnedEntities(NetPeer peer, ClientSession session)
        {
            if (_spawnedEntities.Count > 0)
            {
                foreach (var message in _spawnedEntities.Values)
                {
                    Send(peer, message.Write, DeliveryMethod.ReliableOrdered);
                    _entitySpawnsReplayed++;
                }

                _log.LogInfo(
                    $"[net] apparition : {_spawnedEntities.Count} entité(s) fabriquée(s) rejouée(s) pour " +
                    $"'{session.Username}'#{session.PlayerId} (arrivé après leur création).");
            }

            // P2 : le pendant des retraits. Il est indépendant du carnet d'apparitions — une session
            // peut n'avoir que des retraits à rejouer (objet ramassé avant qu'elle n'arrive).
            if (_despawnedEntities.Count == 0)
            {
                return;
            }

            foreach (var message in _despawnedEntities.Values)
            {
                Send(peer, message.Write, DeliveryMethod.ReliableOrdered);
                _entityDespawnsReplayed++;
            }

            _log.LogInfo(
                $"[net] disparition : {_despawnedEntities.Count} entité(s) disparue(s) rejouée(s) pour " +
                $"'{session.Username}'#{session.PlayerId} (arrivé après leur retrait).");
        }


        /// <summary>
        /// Charge normalisée d'un véhicule (M4, v14), ou -1 si le composant est absent : un véhicule
        /// sans batterie ne doit pas être annoncé « à plat ». Le sentinelle -1 est écrit tel quel.
        /// </summary>
        private static float ReadVehicleEnergy(Vehicle vehicle)
        {
            if (vehicle == null)
            {
                return -1f;
            }

            try
            {
                var mixin = vehicle.GetComponent<EnergyMixin>();
                if (mixin == null)
                {
                    return -1f;
                }

                var charge = mixin.charge;
                return charge < 0f ? 0f : (charge > 1f ? 1f : charge);
            }
            catch (System.Exception exception)
            {
                // Un véhicule en cours de destruction peut lever : l'absence d'énergie est une
                // information valide (-1), pas une raison de perdre tout le snapshot.
                UnityEngine.Debug.LogWarning($"[net] énergie de véhicule illisible : {exception.Message}");
                return -1f;
            }
        }

        private void BroadcastVehicleSnapshots(long serverTick, Player hostPlayer)
        {
            // Un volant que plus personne n'envoie doit redevenir libre avant le calcul des diffusés :
            // sans ça, `conduits localement` resterait vrai chez les autres clients indéfiniment.
            ReleaseSilentDrivers();

            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_vehicleCache == null || now - _vehicleCacheRealtime >= VehicleCacheSeconds)
            {
                _vehicleCache = UnityEngine.Object.FindObjectsOfType<Vehicle>();
                _vehicleCacheRealtime = now;
                ReportVehicleIdentities();
                BroadcastNewVehicles();
            }

            if (_vehicleCache == null || _vehicleCache.Length == 0)
            {
                return;
            }

            var radiusSquared =
                Protocol.VehicleRelevancyRadiusMeters * Protocol.VehicleRelevancyRadiusMeters;

            foreach (var recipientPair in _sessions)
            {
                var recipientSession = recipientPair.Value;
                if (!recipientSession.Accepted)
                {
                    continue;
                }

                // Même règle que pour les créatures : le relevancy est celui du **destinataire**. Un
                // client ne peut suivre que les véhicules de son propre monde, et son monde n'a de
                // cellules chargées qu'autour de lui. Repli sur l'hôte tant qu'il n'a pas annoncé sa
                // position (juste après le handshake), pour qu'il reçoive quelque chose plutôt que rien.
                var interestX = recipientSession.PositionsReceived > 0 ? recipientSession.X : 0f;
                var interestY = recipientSession.PositionsReceived > 0 ? recipientSession.Y : 0f;
                var interestZ = recipientSession.PositionsReceived > 0 ? recipientSession.Z : 0f;
                var fromSession = recipientSession.PositionsReceived > 0;

                if (!fromSession && hostPlayer != null && hostPlayer.gameObject != null)
                {
                    var hostPosition = hostPlayer.transform.position;
                    interestX = hostPosition.x;
                    interestY = hostPosition.y;
                    interestZ = hostPosition.z;
                }

                _vehicleCandidates.Clear();
                for (var i = 0; i < _vehicleCache.Length; i++)
                {
                    var vehicle = _vehicleCache[i];
                    if (vehicle == null || vehicle.transform == null)
                    {
                        continue;
                    }

                    var position = vehicle.transform.position;
                    var dx = position.x - interestX;
                    var dy = position.y - interestY;
                    var dz = position.z - interestZ;
                    var distanceSquared = dx * dx + dy * dy + dz * dz;

                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    _vehicleCandidates.Add(new KeyValuePair<float, int>(distanceSquared, i));
                }

                if (_vehicleCandidates.Count == 0)
                {
                    continue;
                }

                // Les plus proches d'abord : si le plafond mord un jour, ce sont les véhicules du bout
                // du rayon qui sont écartés, pas celui devant le joueur.
                _vehicleCandidates.Sort((left, right) => left.Key.CompareTo(right.Key));

                _vehicleBuffer.Clear();
                var asUniqueId = 0;
                var asAnchor = 0;
                var withoutKey = 0;

                for (var i = 0; i < _vehicleCandidates.Count && i < Protocol.MaxVehicleStates; i++)
                {
                    var vehicle = _vehicleCache[_vehicleCandidates[i].Value];
                    var key = VehicleIdentity.KeyOf(vehicle, out var mechanism);
                    if (key == 0L)
                    {
                        withoutKey++;
                        _vehiclesWithoutKey++;
                        continue;
                    }

                    if (mechanism == VehicleIdentity.MechanismUniqueId)
                    {
                        asUniqueId++;
                    }
                    else
                    {
                        asAnchor++;
                    }

                    var transform = vehicle.transform;
                    var position = transform.position;
                    var rotation = transform.rotation;

                    _vehicleBuffer.Add(new VehicleStateEntry
                    {
                        Key = key,
                        Energy = ReadVehicleEnergy(vehicle),
                        X = position.x,
                        Y = position.y,
                        Z = position.z,
                        RotX = rotation.x,
                        RotY = rotation.y,
                        RotZ = rotation.z,
                        RotW = rotation.w,
                    });
                }

                if (_vehicleBuffer.Count == 0)
                {
                    if (withoutKey > 0 && now >= recipientSession.NextVehicleReport)
                    {
                        recipientSession.NextVehicleReport = now + VehicleStreamReportSeconds;
                        _log.LogWarning(
                            $"[net] véhicules : {withoutKey} véhicule(s) à moins de " +
                            $"{Protocol.VehicleRelevancyRadiusMeters:F0} m de " +
                            $"'{recipientSession.Username}'#{recipientSession.PlayerId} n'ont **aucune clé** — " +
                            "le protocole les a écartés plutôt que de les confondre avec un autre.");
                    }

                    continue;
                }

                var snapshot = new VehicleSnapshotMessage
                {
                    ServerTick = serverTick,
                    Vehicles = _vehicleBuffer.ToArray(),
                };

                _writer.Reset();
                snapshot.Write(_writer);
                if (_writer.Length > Protocol.MaxUnreliablePayloadBytes)
                {
                    _oversizedVehiclePackets++;
                    if (_oversizedVehiclePackets <= 3)
                    {
                        _log.LogError(
                            $"[net] paquet de véhicules REFUSÉ : {_writer.Length} octets pour " +
                            $"{_vehicleBuffer.Count} véhicule(s), au-dessus de la limite non fiable de " +
                            $"{Protocol.MaxUnreliablePayloadBytes} octets. LiteNetLib l'aurait jeté sans le dire " +
                            "dans nos journaux.");
                    }

                    continue;
                }

                recipientPair.Key.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.Unreliable);
                _vehicleSnapshotsBroadcast++;
                _vehicleStatesBroadcast += _vehicleBuffer.Count;
                recipientSession.VehicleBytesSent += _writer.Length;
                recipientSession.VehicleStatesSent += _vehicleBuffer.Count;
                recipientSession.VehiclePacketsSent++;

                if (now < recipientSession.NextVehicleReport)
                {
                    continue;
                }

                recipientSession.NextVehicleReport = now + VehicleStreamReportSeconds;

                var truncated = System.Math.Max(0, _vehicleCandidates.Count - Protocol.MaxVehicleStates);
                var bytesPerSecond = recipientSession.VehicleBytesSent / VehicleStreamReportSeconds;

                _log.LogInfo(
                    $"[net] véhicules : {_vehicleCache.Length} dans le monde, " +
                    $"{_vehicleCandidates.Count} à moins de {Protocol.VehicleRelevancyRadiusMeters:F0} m " +
                    $"du point ({interestX:F1}, {interestY:F1}, {interestZ:F1}) " +
                    $"[{(fromSession ? "position du client" : "repli position de l'hôte")}], " +
                    $"{_vehicleBuffer.Count} diffusé(s) à '{recipientSession.Username}'#{recipientSession.PlayerId} " +
                    $"({asUniqueId} par {VehicleIdentity.MechanismUniqueId}, {asAnchor} par " +
                    $"{VehicleIdentity.MechanismSpawnPosition}, {withoutKey} sans clé écarté(s))" +
                    (truncated > 0 ? $", {truncated} au-delà du plafond de {Protocol.MaxVehicleStates}" : string.Empty) +
                    $", débit={bytesPerSecond / 1024f:F1} Ko/s " +
                    $"({recipientSession.VehicleStatesSent} état(s) en {recipientSession.VehiclePacketsSent} paquet(s) " +
                    $"sur {VehicleStreamReportSeconds:F0} s), total envoyé={_vehicleSnapshotsBroadcast} snapshot(s) / " +
                    $"{_vehicleStatesBroadcast} état(s)" +
                    (_oversizedVehiclePackets == 0
                        ? string.Empty
                        : $", {_oversizedVehiclePackets} paquet(s) REFUSÉ(S) pour dépassement de taille"));

                recipientSession.VehicleBytesSent = 0;
                recipientSession.VehicleStatesSent = 0;
                recipientSession.VehiclePacketsSent = 0;
            }
        }

        /// <summary>
        /// Journalise **une fois** de quoi sont faites les identités des véhicules du monde.
        ///
        /// Le chiffre qui compte est la part du repli : si tous les véhicules passent par
        /// l'identifiant du jeu, la clé est stable par construction ; si une partie passe par la position
        /// d'apparition, la synchronisation dépend de l'instant où les deux instances les ont vus — et
        /// c'est écrit, pas supposé.
        /// </summary>
        private void ReportVehicleIdentities()
        {
            var cached = _vehicleCache ?? Array.Empty<Vehicle>();
            var byUniqueId = 0;
            var byAnchor = 0;
            var none = 0;
            var names = new List<string>();

            for (var i = 0; i < cached.Length; i++)
            {
                var vehicle = cached[i];
                if (vehicle == null)
                {
                    continue;
                }

                var key = VehicleIdentity.KeyOf(vehicle, out var mechanism);
                if (key == 0L || mechanism == VehicleIdentity.MechanismNone)
                {
                    none++;
                }
                else if (mechanism == VehicleIdentity.MechanismUniqueId)
                {
                    byUniqueId++;
                }
                else
                {
                    byAnchor++;
                }

                if (names.Count < 4)
                {
                    names.Add(VehicleIdentity.Describe(vehicle));
                }
            }

            var report =
                $"{cached.Length} véhicule(s) : {byUniqueId} par {VehicleIdentity.MechanismUniqueId}, " +
                $"{byAnchor} par {VehicleIdentity.MechanismSpawnPosition}, {none} sans clé" +
                (names.Count == 0 ? string.Empty : $" — {string.Join(" · ", names)}");

            if (cached.Length == 0)
            {
                report = "aucun véhicule dans le monde";
            }

            if (string.Equals(report, _lastVehicleIdentityReport, StringComparison.Ordinal))
            {
                return;
            }

            _lastVehicleIdentityReport = report;
            _log.LogInfo($"[net] identités de véhicules : {report}");
        }

        /// <summary>
        /// Distance (m) de la créature la plus proche d'un point de référence, et son nom.
        ///
        /// Renvoie une valeur avec <c>Exists = false</c> s'il n'y a rien à mesurer ou rien de
        /// lisible : c'est distinct de « 0 m », et confondre les deux ferait croire à une créature
        /// collée au joueur.
        /// </summary>
        private List<(string Name, float Meters, UnityEngine.Vector3 Reference)> NearestCreatures(Player hostPlayer, int count)
        {
            var result = new List<(string, float, UnityEngine.Vector3)>();
            var cached = _creatureCache ?? Array.Empty<Creature>();
            if (cached.Length == 0)
            {
                return result;
            }

            UnityEngine.Vector3? origin = null;
            if (hostPlayer != null && hostPlayer.gameObject != null)
            {
                origin = hostPlayer.transform.position;
            }
            else
            {
                foreach (var session in _sessions.Values)
                {
                    if (!session.Accepted)
                    {
                        continue;
                    }

                    origin = new UnityEngine.Vector3(session.X, session.Y, session.Z);
                    break;
                }
            }

            if (origin == null)
            {
                return result;
            }

            var reference = origin.Value;
            var measured = new List<(string Name, float Meters, UnityEngine.Vector3 Reference)>();

            for (var i = 0; i < cached.Length; i++)
            {
                var creature = cached[i];
                if (creature == null || creature.transform == null)
                {
                    continue;
                }

                measured.Add((
                    creature.gameObject != null ? creature.gameObject.name : "?",
                    UnityEngine.Vector3.Distance(creature.transform.position, reference),
                    reference));
            }

            measured.Sort((left, right) => left.Meters.CompareTo(right.Meters));
            for (var i = 0; i < measured.Count && i < count; i++)
            {
                result.Add(measured[i]);
            }

            return result;
        }

        /// <summary>
        /// Journalise **une fois** de quoi sont faites les identités des créatures du monde.
        ///
        /// C'est la mesure qui décide de la suite : si le jeu a posé un `UniqueIdentifier` sur
        /// toutes les créatures, la clé est stable par construction ; si une partie n'en a pas, le
        /// repli par position d'apparition prend le relais — et le compte dit lequel des deux porte
        /// réellement la synchronisation. Les créatures sans aucune clé sont **nommées** : sans ça,
        /// « 200 créatures non diffusées » ne dirait pas lesquelles, ni pourquoi.
        /// </summary>
        /// <summary>
        /// Dit ce que la sauvegarde contient comme **véhicules**, avec leur distance et leur vitesse.
        ///
        /// Pourquoi ce relevé précède le code de M4 au lieu de le suivre : la mesure qui décide de la forme
        /// de M4 — cadence d'envoi ou interpolation — porte sur un véhicule **en mouvement à 20 m/s**. Si la
        /// sauvegarde n'en contient aucun (le cas le plus probable ici), aucune ligne de M4 ne pourra être
        /// mesurée avant d'en avoir fait apparaître un, et mieux vaut l'apprendre par une ligne de journal
        /// que par un run qui ne mesure rien.
        /// </summary>
        private void ReportVehicles(Player hostPlayer, float now)
        {
            if (now < _nextVehicleDiagnostic)
            {
                return;
            }

            _nextVehicleDiagnostic = now + VehicleReportSeconds;

            var vehicles = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            if (vehicles == null || vehicles.Length == 0)
            {
                _log.LogInfo(
                    "[net] véhicules : aucun dans le monde — M4 (Seamoth autoritaire) n'a donc **rien à mesurer** " +
                    "dans cette sauvegarde : le flux `VehicleSnapshot` n'enverra rien tant qu'un véhicule n'y " +
                    "apparaît pas (cadence et interpolation sont tranchées par la mesure du §30, appliquées au §33).");
                return;
            }

            // Les trois plus proches de l'hôte, nommées et situées : c'est assez pour savoir si M4 a de quoi
            // mesurer. Leur **vitesse** n'est pas relevée ici, et c'est délibéré : un véhicule immobile
            // donnerait `0 m/s` — un chiffre qui n'apprend rien et qu'on prendrait pour une mesure. Elle
            // viendra d'un état de véhicule, le jour où il y en aura un à suivre.
            var nearest = vehicles
                .Where(vehicle => vehicle != null && vehicle.transform != null)
                .Select(vehicle => new
                {
                    Name = vehicle.name,
                    Distance = hostPlayer != null && hostPlayer.transform != null
                        ? UnityEngine.Vector3.Distance(hostPlayer.transform.position, vehicle.transform.position)
                        : 0f,
                })
                .OrderBy(entry => entry.Distance)
                .Take(3)
                .Select(entry =>
                    $"'{entry.Name}' à {entry.Distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} m");

            _log.LogInfo(
                $"[net] véhicules : {vehicles.Length} dans le monde, les plus proches de l'hôte : " +
                $"{string.Join(", ", nearest)}. Un véhicule à 20 m/s balaie 2 m par annonce à 10 Hz : c'est le " +
                "pas que l'interpolation cliente doit absorber (mesuré au §33 : 0,27 m par image au maximum).");
        }

        private void ReportCreatureIdentities()
        {
            var cached = _creatureCache ?? Array.Empty<Creature>();
            var byUniqueId = 0;
            var bySpawn = 0;
            var none = 0;
            var withoutKeyNames = new List<string>();

            for (var i = 0; i < cached.Length; i++)
            {
                var creature = cached[i];
                if (creature == null)
                {
                    continue;
                }

                CreatureIdentity.KeyOf(creature, out var mechanism);
                if (mechanism == CreatureIdentity.MechanismUniqueId)
                {
                    byUniqueId++;
                }
                else if (mechanism == CreatureIdentity.MechanismSpawnPosition)
                {
                    bySpawn++;
                }
                else
                {
                    none++;
                    if (withoutKeyNames.Count < 5 && creature.gameObject != null)
                    {
                        withoutKeyNames.Add(creature.gameObject.name);
                    }
                }
            }

            var report =
                $"{cached.Length} créature(s) : {bySpawn} par {CreatureIdentity.MechanismSpawnPosition}, " +
                $"{byUniqueId} par {CreatureIdentity.MechanismUniqueId}, {none} sans clé" +
                (withoutKeyNames.Count == 0
                    ? string.Empty
                    : $" (ex. : {string.Join(", ", withoutKeyNames)})");

            if (string.Equals(report, _lastCreatureIdentityReport, StringComparison.Ordinal))
            {
                return;
            }

            _lastCreatureIdentityReport = report;
            _log.LogInfo($"[net] identités de créatures : {report}");
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
