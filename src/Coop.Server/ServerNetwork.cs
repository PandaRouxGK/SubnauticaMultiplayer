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
    ///
    /// **Découpage (P5)** : la classe est `partial`, et ses membres sont répartis par domaine dans
    /// `ServerNetwork.Presence.cs` (admission, joueurs, équipement, positions), `.Creatures.cs`,
    /// `.Vehicles.cs` (diffusion, conduite, modules, énergie), `.Pickup.cs` et `.Entities.cs`
    /// (apparition, retrait, carnets de rejeu). Les **champs** restent ici : ils sont partagés par tous
    /// les domaines, et les déplacer aurait changé l'ordre de déclaration sans rien séparer de plus.
    /// Ce découpage ne change ni les messages, ni l'ordre des traitements, ni `tools/verify.sh`.
    /// </summary>
    public sealed partial class ServerNetwork
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

        // — Modules de véhicule (v15, M4) ————————————————————————————————————————
        //
        // Dernier ensemble **annoncé** par véhicule, en forme canonique. Il sert à deux choses : ne
        // rediffuser que ce qui a changé (un module ne change pas dix fois par seconde), et rejouer
        // l'état installé à un client qui se connecte après l'installation.
        private readonly Dictionary<long, VehicleUpgradesMessage> _vehicleUpgrades =
            new Dictionary<long, VehicleUpgradesMessage>();
        private int _vehicleUpgradesBroadcast;
        private int _vehicleUpgradesReplayed;
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
        /// <summary>Annonces de modules émises (v15) : un module installé ou retiré, pas un état périodique.</summary>
        public int VehicleUpgradesBroadcast => _vehicleUpgradesBroadcast;
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
        /// <summary>
        /// Éjecte les peers qui n'ont jamais terminé leur handshake. Indispensable : puisque le
        /// serveur ne coupe plus la connexion juste après un refus (sinon le motif envoyé en
        /// fiable serait perdu), il faut une borne pour qu'une connexion muette ne reste pas ouverte.
        /// </summary>
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
        /// <summary>
        /// Indice de couleur du prochain client admis.
        ///
        /// La règle tient en une ligne : on avance d'un cran par client, en sautant l'indice de
        /// l'hôte quand son personnage est dans le monde. C'est ce qui garantit que deux joueurs ne
        /// reçoivent pas la même couleur, sans arbitrage à la connexion et sans que le client ait
        /// quelque chose à décider.
        /// </summary>
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
        /// <summary>
        /// Mémorise une position comme **acceptée** : c'est le seul état que les autres joueurs voient.
        /// </summary>
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
        /// <summary>
        /// Objet tenu annoncé par un client (outil en main, ou rien).
        ///
        /// Le serveur ne fait que le mémoriser et le relayer : il ne le valide pas contre un inventaire
        /// qu'il ne détient pas pour les joueurs distants, et il **force** l'identité de l'émetteur —
        /// un client ne peut donc pas prétendre tenir un objet à la place d'un autre.
        /// </summary>
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
        /// <summary>
        /// Retire du monde du serveur l'objet dont la clé vient d'être accordée, puis annonce sa disparition.
        ///
        /// Ce que ça change et ce que ça ne change pas : l'objet cesse d'exister dans le monde simulé (il ne
        /// sera plus résolu par <see cref="TryResolveWorldItem"/>), et les clients le voient disparaître. Il
        /// n'est **pas** retiré de l'inventaire de celui qui l'a ramassé — il y est déjà depuis son geste
        /// local, et c'est son exemplaire à lui qui compte.
        /// </summary>
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
        /// <summary>
        /// Libère les volants devenus **silencieux** (v12).
        ///
        /// Pourquoi le silence et pas seulement le message : un client qui plante, qui perd le réseau ou qui
        /// contourne `VehicleDriverBehaviour` ne libère rien. Le conducteur envoie un état toutes les 100 ms
        /// tant qu'il est au volant ; `VehicleSilenceReleaseSeconds` (3 s) vaut donc 30 états manqués — bien
        /// au-delà d'une rafale de latence.
        /// </summary>
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
        /// <summary>
        /// Vrai si la clé désigne un **véhicule du monde serveur**. Index reconstruit au plus une fois par
        /// seconde : un état de conduite arrive 10 fois par seconde, un parcours complet à chaque état
        /// coûterait cher pour rien.
        /// </summary>
        /// <summary>
        /// Vrai si la clé désigne un objet **du monde simulé**, et où il se trouve.
        ///
        /// Pourquoi le serveur doit résoudre la clé lui-même : sans cela, il accorderait n'importe quelle
        /// valeur — un client pourrait « réclamer » une clé inventée pour bloquer un objet qu'un autre
        /// client réclamerait ensuite. L'index est reconstruit au plus une fois par seconde, avec la
        /// **même** fonction de clé que le client (<see cref="ItemIdentity"/>, compilé dans les deux
        /// plugins) ; les clés partagées sont comptées et signalées plutôt que silencieuses.
        /// </summary>
        /// <summary>Objet du monde portant cette clé, s'il existe encore.</summary>
        ///
        /// Distinct de <see cref="TryResolveWorldItem"/>, qui rend une position indexée : ici on veut
        /// l'objet lui-même, pour le retirer. Le parcours est refait à neuf plutôt que de mémoriser des
        /// références, parce qu'une référence à un objet détruit par le jeu ne se distingue pas d'une
        /// référence nulle et ferait croire à un retrait réussi.
        /// </summary>
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
        /// <summary>
        /// Nom de l'objet tenu par le personnage de l'instance serveur, ou chaîne vide s'il n'a rien
        /// en main. Renvoie <c>null</c> si l'inventaire local n'est pas lisible (monde non chargé).
        /// </summary>
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
        /// <summary>Range une annonce dans le carnet, en évinçant la plus ancienne au-delà de la borne.</summary>
        /// <summary>
        /// Range un retrait dans le carnet, borné comme celui des apparitions.
        ///
        /// Un client qui rejoint après un ramassage charge la **même sauvegarde**, qui contient
        /// encore l'objet : sans ce rejeu, il le verrait et pourrait le réclamer, alors que le
        /// serveur l'a déjà accordé à quelqu'un. C'est le défaut que la v11 avait fermé pour les
        /// clients présents, et qui restait ouvert pour les suivants (P2).
        /// </summary>
        /// <summary>
        /// Rejoue à un client qui vient d'être admis les entités fabriquées avant son arrivée.
        ///
        /// Pourquoi c'est nécessaire : les entités de la **sauvegarde** existent déjà chez lui (il charge la
        /// même partie), une entité **fabriquée** non. Mesuré à l'étape N : le deuxième client se connecte
        /// après l'annonce et ne reçoit rien — verdict False à 150 s, alors que le premier a tout créé.
        /// </summary>
        /// <summary>
        /// Charge normalisée d'un véhicule (M4, v14), ou -1 si le composant est absent : un véhicule
        /// sans batterie ne doit pas être annoncé « à plat ». Le sentinelle -1 est écrit tel quel.
        /// </summary>
        /// <summary>
        /// Diffuse les modules **installés** sur chaque véhicule connu (v15, M4), et seulement quand
        /// l'ensemble a changé.
        ///
        /// Pourquoi sur changement et pas dix fois par seconde : un module s'installe en quelques secondes
        /// de jeu, et le rediffuser à chaque balayage ferait un message fiable par seconde et par véhicule
        /// pour un état immobile. La comparaison porte sur la forme **canonique** (triée, dédupliquée) :
        /// l'ordre d'itération de l'équipement du jeu n'est pas garanti, et deux ensembles identiques doivent
        /// produire le même message.
        /// </summary>
        /// <summary>
        /// Journalise **une fois** de quoi sont faites les identités des véhicules du monde.
        ///
        /// Le chiffre qui compte est la part du repli : si tous les véhicules passent par
        /// l'identifiant du jeu, la clé est stable par construction ; si une partie passe par la position
        /// d'apparition, la synchronisation dépend de l'instant où les deux instances les ont vus — et
        /// c'est écrit, pas supposé.
        /// </summary>
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
