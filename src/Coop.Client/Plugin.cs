using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

        /// <summary>
        /// Instance du plugin, pour écrire le fichier de configuration depuis le panneau de connexion.
        /// </summary>
        internal static CoopClientPlugin Instance;

        /// <summary>Entrée `[Reseau] Hote` : le panneau y écrit l'adresse saisie.</summary>
        internal static ConfigEntry<string> HostEntry;

        /// <summary>Entrée `[Reseau] Port`.</summary>
        internal static ConfigEntry<int> PortEntry;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

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

            // v21 : le **vrai** craft du jeu (fabricator, menu) demande l'accord du serveur. Même règle que
            // le ramassage : si la cible n'existe pas dans cette version, le craft reste local et le journal
            // le dit — un craft non arbitré n'abîme rien, il laisse seulement passer un coût non vérifié.
            var craftGuardInstalled = CraftGuard.Install(Log);

            // Plus de défaut `127.0.0.1` : deux joueurs séparés par Internet ne sont pas sur la même
            // boucle locale, et un client qui se connecte silencieusement à lui-même donnerait une panne
            // incompréhensible. Vide = pas d'adresse, donc **pas de connexion** tant que le joueur n'a pas
            // saisi la sienne (`-coopserver`, `COOP_SERVER`, panneau du menu, ou fichier d'une session
            // précédente).
            var host = Config.Bind("Reseau", "Hote", string.Empty,
                "Adresse du serveur (IP ou nom d'hôte), port facultatif. Vide = saisie dans le panneau du menu.");
            var port = Config.Bind("Reseau", "Port", Protocol.DefaultPort, "Port UDP du serveur.");
            var username = Config.Bind("Joueur", "Pseudo", Environment.UserName, "Pseudo envoyé au serveur.");
            HostEntry = host;
            PortEntry = port;

            var startHost = host.Value?.Trim() ?? string.Empty;
            var startPort = (ushort)Mathf.Clamp(port.Value, ServerAddressPreference.MinPort, ServerAddressPreference.MaxPort);

            // Argument / environnement prioritaire : c'est ce qui rend un client lançable et vérifiable
            // sans passer par le menu (harnais, serveur dédié, raccourci Windows). Une valeur explicite
            // mais invalide est un **refus nommé**, jamais un repli.
            var requestedAddress = ServerAddressPreference.Raw(Environment.GetCommandLineArgs());
            if (requestedAddress != null)
            {
                if (ServerAddressPreference.TryParse(requestedAddress, Protocol.DefaultPort, out var parsedAddress, out var addressError))
                {
                    startHost = parsedAddress.Host;
                    startPort = parsedAddress.Port;
                    Log.LogInfo(
                        $"[net] adresse imposée : {parsedAddress} ({ServerAddressPreference.ArgumentName} / " +
                        $"{ServerAddressPreference.EnvironmentVariableName}).");
                }
                else
                {
                    startHost = string.Empty;
                    Log.LogError(
                        $"[net] adresse REFUSÉE : « {ServerAddressPreference.Describe(requestedAddress)} » — {addressError}. " +
                        "Aucune connexion ; une saisie est attendue dans le panneau du menu.");
                }
            }

            var go = new GameObject("Coop.Client.Bootstrap");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var behaviour = go.AddComponent<ClientNetworkBehaviour>();
            behaviour.Configure(startHost, startPort, username.Value);

            // Le monde n'est chargé qu'une fois une adresse connue : sans cette porte, `AutoLoadBehaviour`
            // ouvre la partie au bout de trois secondes, bien avant qu'un joueur ait pu taper une IP.
            AutoLoadBehaviour.BeforeLoad = () =>
                ClientNetworkBehaviour.Current != null && ClientNetworkBehaviour.Current.HasEndpoint;

            // Panneau de connexion du menu : saisie de l'adresse, validation, mémorisation. Toujours
            // présent, il se masque dès que le serveur a accepté la session.
            go.AddComponent<JoinPanelBehaviour>();

            // Conduite autoritaire (v9) : la boucle qui envoie l'état du véhicule piloté. Elle ne fait
            // rien tant que le joueur ne pilote pas, donc elle vit avec le client et pas avec une sonde.
            go.AddComponent<VehicleDriverBehaviour>();

            // Création d'entité (v10) : instancie ce que le serveur annonce. Toujours actif : un client qui
            // ne crée pas une entité annoncée la voit dans le flux de positions sans objet à déplacer.
            go.AddComponent<EntitySpawner>();

            // Modules de véhicule (v15) : applique à la copie locale ce que le serveur annonce comme
            // installé. Composant, pas classe statique : le prefab d'un module se charge par coroutine.
            go.AddComponent<VehicleModuleApplier>();

            // Retrait d'entité (v11) : composant et non classe statique, parce qu'un retrait reçu avant le
            // chargement du monde doit être **gardé** puis appliqué — sans quoi la sauvegarde le fait revenir.
            go.AddComponent<EntityDespawner>();

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

            if (IsDebugFlagSet("join"))
            {
                Log.LogWarning("[boot] sonde de connexion activée (-coopdebug=join).");
                go.AddComponent<JoinProbeBehaviour>();
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

            if (IsDebugFlagSet("spawn"))
            {
                // M6 / v10 : attend qu'une entité annoncée par l'hôte soit créée localement, clé comprise.
                Log.LogWarning("[boot] sonde d'apparition activée (-coopdebug=spawn).");
                go.AddComponent<EntitySpawnProbeBehaviour>();
            }

            if (IsDebugFlagSet("craft"))
            {
                // v21 : le serveur lit la recette du jeu et prélève le coût sur son registre ; la sonde
                // choisit une recette réellement satisfaite par l'inventaire local, puis la rejoue pour
                // obtenir le refus nommé.
                Log.LogWarning("[boot] sonde de craft activée (-coopdebug=craft).");
                go.AddComponent<CraftProbeBehaviour>();
            }

            if (IsDebugFlagSet("inventory"))
            {
                // v19 : vérifie que le registre du serveur correspond à un ramassage réel (`-coopdebug=pickup`
                // doit tourner en même temps : c'est lui qui a l'objet et le geste).
                Log.LogWarning("[boot] sonde d'inventaire activée (-coopdebug=inventory).");
                go.AddComponent<InventoryProbeBehaviour>();
            }

            if (IsDebugFlagSet("time"))
            {
                // v18 : constate la réception de l'heure du serveur et le recalage effectivement écrit.
                Log.LogWarning("[boot] sonde d'heure du monde activée (-coopdebug=time).");
                go.AddComponent<TimeSyncProbeBehaviour>();
            }

            if (IsDebugFlagSet("creaturehealth"))
            {
                // v17 : constate la fraction de vie reçue sur une copie, puis le retrait décidé par le
                // serveur quand l'hôte tue la créature.
                Log.LogWarning("[boot] sonde de santé de créature activée (-coopdebug=creaturehealth).");
                go.AddComponent<CreatureHealthProbeBehaviour>();
            }

            if (IsDebugFlagSet("requestspawn"))
            {
                // v16 : le **client** demande une fabrication, le serveur décide et annonce.
                Log.LogWarning("[boot] sonde de demande d'apparition activée (-coopdebug=requestspawn).");
                go.AddComponent<ClientSpawnProbeBehaviour>();
            }

            if (IsDebugFlagSet("despawn"))
            {
                // M6 / v11 : attend qu'un véhicule annoncé par l'hôte soit **retiré** localement.
                Log.LogWarning("[boot] sonde de disparition activée (-coopdebug=despawn).");
                go.AddComponent<EntityDespawnProbeBehaviour>();
            }

            if (IsDebugFlagSet("drive"))
            {
                // M4 / v9 : un client conduit le véhicule, l'autre tente de le lui prendre.
                Log.LogWarning("[boot] sonde de conduite activée (-coopdebug=drive).");
                go.AddComponent<VehicleDriveProbeBehaviour>();
            }

            if (IsDebugFlagSet("pickup"))
            {
                // M5 : la première action autoritaire. Les deux clients réclament la même clé ; le serveur
                // accorde au premier et refuse au second, et l'id falsifié est réécrit.
                Log.LogWarning("[boot] sonde de ramassage activée (-coopdebug=pickup).");
                go.AddComponent<PickupProbeBehaviour>();
            }

            if (IsDebugFlagSet("position"))
            {
                // P1 / v13 : la garde de position doit refuser un téléport **et** ne pas corriger une
                // nage normale. Les deux critères sont nécessaires : l'un sans l'autre valide un garde
                // qui refuse tout, ou un garde qui ne refuse rien.
                Log.LogWarning("[boot] sonde de position activée (-coopdebug=position).");
                go.AddComponent<PositionProbeBehaviour>();
            }

            Log.LogInfo(
                $"[boot] GameObject 'Coop.Client.Bootstrap' créé (DontDestroyOnLoad, garde={saveGuardInstalled}, " +
                $"pauseCliente={(pauseGuardInstalled ? "désactivée" : "NON patchée")}, " +
                $"ramassage={(pickupGuardInstalled ? "arbitré" : "NON patché")}, " +
                $"craft={(craftGuardInstalled ? "arbitré" : "NON patché")}).");
        }

        /// <summary>
        /// Applique une adresse saisie dans le panneau : écriture dans le fichier de configuration (pour
        /// la session suivante) puis bascule immédiatement la connexion.
        ///
        /// L'écriture est **non bloquante** pour la connexion : un fichier de configuration en lecture seule
        /// ne doit pas empêcher de jouer, il doit seulement être signalé.
        /// </summary>
        internal static void ApplyEndpoint(string host, ushort port)
        {
            if (HostEntry != null)
            {
                HostEntry.Value = host;
            }

            if (PortEntry != null)
            {
                PortEntry.Value = port;
            }

            try
            {
                Instance?.Config.Save();
            }
            catch (Exception exception)
            {
                Log.LogWarning(
                    $"[menu] adresse non enregistrée dans le fichier de configuration ({exception.Message}) — " +
                    "elle vaut pour cette session.");
            }

            ClientNetworkBehaviour.Current?.SetEndpoint(host, port);
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

        // — Empreinte de sauvegarde (v13, P0) ————————————————————————————————————————
        // Le client ne se connecte pas avant de savoir **quelle partie** il a chargée : un handshake
        // sans empreinte ne peut pas être honoré, et le serveur ne saurait pas distinguer « même
        // sauvegarde » de « je ne sais pas encore ». L'attente est bornée, et son échec est nommé.
        private double _saveWaitStarted = -1.0;
        private bool _loggedSaveWait;
        private bool _loggedSaveTimeout;

        // — Corrections de position (v13, P1) ————————————————————————————————————————
        private int _correctionsReceived;
        private int _correctionsApplied;
        private int _correctionsSkipped;

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

        // — Conduite autoritaire (v9, M4) : l'état du véhicule que ce client pilote ————————
        private int _vehicleControlSequence;
        private int _vehicleControlSent;
        private int _vehicleControlGranted;
        private int _vehicleControlRefused;

        internal int VehicleControlSent => _vehicleControlSent;
        internal int VehicleControlGranted => _vehicleControlGranted;
        internal int VehicleControlRefused => _vehicleControlRefused;

        // — Lecture seule des compteurs de correction (P1) ————————————————————————————
        // Exposés pour la sonde `-coopdebug=position`, qui doit lire ce que la boucle réseau a
        // réellement reçu et appliqué, et non réimplémenter la règle.
        internal int CorrectionsReceived => _correctionsReceived;
        internal int CorrectionsApplied => _correctionsApplied;
        internal int CorrectionsSkipped => _correctionsSkipped;
        internal bool Accepted => _accepted;

        /// <summary>
        /// Envoie l'état du véhicule piloté (v9). Unreliable : un état perdu est remplacé par le suivant
        /// 100 ms plus tard, et le serveur ne décide rien de durable avec.
        /// </summary>
        internal bool SendVehicleControl(long vehicleKey, Vector3 position, Quaternion rotation, Vector3 speed)
        {
            if (_server == null || _server.ConnectionState != ConnectionState.Connected)
            {
                return false;
            }

            _writer.Reset();
            new VehicleControlMessage
            {
                PlayerId = _assignedPlayerId,
                VehicleKey = vehicleKey,
                Sequence = ++_vehicleControlSequence,
                X = position.x,
                Y = position.y,
                Z = position.z,
                RotX = rotation.x,
                RotY = rotation.y,
                RotZ = rotation.z,
                RotW = rotation.w,
                SpeedX = speed.x,
                SpeedY = speed.y,
                SpeedZ = speed.z,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.Unreliable);
            _vehicleControlSent++;
            return true;
        }

        private int _vehicleReleasesSent;

        internal int VehicleReleasesSent => _vehicleReleasesSent;

        /// <summary>
        /// Libère le volant (v12) : « je ne conduis plus ce véhicule ». ReliableOrdered, parce qu'un
        /// message perdu laisserait le véhicule attribué à un joueur qui n'est plus dedans.
        /// </summary>
        internal bool SendVehicleRelease(long vehicleKey)
        {
            if (vehicleKey == 0L || _server == null || _server.ConnectionState != ConnectionState.Connected)
            {
                return false;
            }

            _writer.Reset();
            new VehicleReleaseMessage
            {
                VehicleKey = vehicleKey,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
            _vehicleReleasesSent++;
            return true;
        }

        // — Demandes de fabrication (v16, M4/M6) ——————————————————————————————————————
        //
        // Le client ne fabrique **rien** : il demande. L'entité apparaîtra quand le serveur l'annoncera
        // (`EntitySpawn`), demandeur compris — c'est ce qui évite un doublon entre la copie locale et
        // l'annonce.
        private uint _entitySpawnRequestId;
        private int _entitySpawnsGranted;
        private int _entitySpawnsRefused;
        private long _lastSpawnKey;
        private string _lastSpawnReason = string.Empty;

        // — Heure du monde (v18) ————————————————————————————————————————————————————
        // Compteurs lus par la sonde `-coopdebug=time` : ce que la boucle réseau a reçu, ce qu'elle a
        // réellement écrit, et l'écart mesuré **avant** écriture (pas après, qui vaudrait toujours 0).
        private int _worldTimeMessages;
        private int _worldTimeApplied;
        private float _lastWorldTimeError;
        private float _lastWorldTimeAppliedError;
        private float _maxWorldTimeAppliedError;
        private float _lastWorldTimeServer;

        internal int WorldTimeMessages => _worldTimeMessages;
        internal int WorldTimeApplied => _worldTimeApplied;
        internal float LastWorldTimeError => _lastWorldTimeError;

        /// <summary>
        /// Écart mesuré lors du **dernier recalage écrit** — et non l'écart du dernier message reçu, qui
        /// vaut quelques millisecondes une seconde plus tard.
        /// </summary>
        internal float LastWorldTimeAppliedError => _lastWorldTimeAppliedError;

        /// <summary>
        /// Plus grand écart **effectivement corrigé** depuis le démarrage. La sonde doit lire celui-ci et
        /// non le dernier : mesuré à l'étape AA, le client se recale aussi plusieurs fois de quelques
        /// secondes pendant son chargement, et un saut de 3 600 s corrigé une fois serait masqué par le
        /// recalage suivant.
        /// </summary>
        internal float MaxWorldTimeAppliedError => _maxWorldTimeAppliedError;
        internal float LastWorldTimeServer => _lastWorldTimeServer;

        internal int EntitySpawnsGranted => _entitySpawnsGranted;
        internal int EntitySpawnsRefused => _entitySpawnsRefused;
        internal long LastSpawnGrantedKey => _lastSpawnKey;
        internal string LastSpawnRefusal => _lastSpawnReason;

        /// <summary>
        /// Envoie une demande de fabrication au serveur (ReliableOrdered). Rend l'identifiant de requête,
        /// ou 0 si rien n'est parti (pas de session acceptée).
        /// </summary>
        internal uint RequestEntitySpawn(TechType tech, Vector3 position, Quaternion rotation)
        {
            if (_server == null || _server.ConnectionState != ConnectionState.Connected || !_accepted)
            {
                CoopClientPlugin.Log.LogWarning(
                    $"[apparition] demande non envoyée : session non acceptée (état={_server?.ConnectionState.ToString() ?? "aucun peer"}).");
                return 0u;
            }

            var requestId = ++_entitySpawnRequestId;
            _writer.Reset();
            new EntitySpawnRequestMessage
            {
                RequestId = requestId,
                TechType = (ushort)tech,
                X = position.x,
                Y = position.y,
                Z = position.z,
                RotX = rotation.x,
                RotY = rotation.y,
                RotZ = rotation.z,
                RotW = rotation.w,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);

            CoopClientPlugin.Log.LogInfo(
                $"[apparition] demande envoyée : requête={requestId}, espèce={tech}, " +
                $"pos=({position.x:F1}, {position.y:F1}, {position.z:F1}) — le serveur décide.");
            return requestId;
        }

        private void HandleEntitySpawnResult(EntitySpawnResultMessage result)
        {
            if (result.Granted)
            {
                _entitySpawnsGranted++;
                _lastSpawnKey = result.Key;
                CoopClientPlugin.Log.LogInfo(
                    $"[apparition] demande ACCORDÉE : requête={result.RequestId}, clé=0x{result.Key:x16} — " +
                    "la copie sera créée par l'annonce du serveur (pas par le client).");
                return;
            }

            _entitySpawnsRefused++;
            _lastSpawnReason = result.Reason ?? string.Empty;
            CoopClientPlugin.Log.LogWarning(
                $"[apparition] demande REFUSÉE : requête={result.RequestId} — {_lastSpawnReason}");
        }

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
        private string _lastPickupGrantedName = string.Empty;

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

        /// <summary>Nom du dernier objet dont le ramassage a été **accordé** par le serveur (v19 : c'est ce nom que le registre serveur doit contenir).</summary>
        internal string LastPickupGrantedName => _lastPickupGrantedName;

        // — Inventaire serveur (v19) ————————————————————————————————————————————————
        //
        // Miroir **du registre serveur**, pas une copie de l'inventaire du jeu : la v19 ne fait que
        // transporter et vérifier le registre. La réconciliation avec `Inventory.main` (retirer chez le
        // client ce que le serveur ne contient pas, et inversement) est le lot suivant, et cette limite
        // est écrite dans `PLAN.md` §67 plutôt que masquée par un nom trompeur.
        private readonly Dictionary<string, int> _inventory = new Dictionary<string, int>(StringComparer.Ordinal);
        private int _inventoryMessages;
        private int _inventoryRevision = -1;
        private int _inventoryStale;

        /// <summary>Contenu du dernier instantané **appliqué** : la réconciliation ne regarde que les noms qu'il cite.</summary>
        private InventoryEntry[] _inventoryPrevious = Array.Empty<InventoryEntry>();
        private int _inventoryDebitResults;
        private int _inventoryDebitsGranted;
        private int _inventoryDebitsRefused;
        private string _inventoryLastDebitRefusal = string.Empty;

        internal int InventoryMessages => _inventoryMessages;

        /// <summary>Révision du dernier instantané appliqué ; `-1` = aucun instantané reçu.</summary>
        internal int InventoryRevision => _inventoryRevision;

        /// <summary>Instantanés arrivés dans le désordre et écartés (le contenu le plus récent est gardé).</summary>
        internal int InventoryStaleSnapshots => _inventoryStale;

        internal int InventoryDebitResults => _inventoryDebitResults;
        internal int InventoryDebitsGranted => _inventoryDebitsGranted;
        internal int InventoryDebitsRefused => _inventoryDebitsRefused;

        /// <summary>Motif du dernier refus de débit (`UnknownItem`, `NotEnough`, `Invalid`), ou vide.</summary>
        internal string InventoryLastDebitRefusal => _inventoryLastDebitRefusal;

        /// <summary>Unités réellement retirées de `Inventory.main` par la réconciliation (v20).</summary>
        internal int InventoryRemovedLocally => InventoryReconciler.RemovedItems;

        /// <summary>Retraits que la réconciliation n'a pas pu appliquer — une divergence reste visible.</summary>
        internal int InventoryReconciliationFailures => InventoryReconciler.FailedRemovals;

        /// <summary>Quantité annoncée par le serveur pour un objet (0 s'il n'y est pas).</summary>
        internal int InventoryCountOf(string techTypeName) =>
            techTypeName != null && _inventory.TryGetValue(techTypeName, out var count) ? count : 0;

        /// <summary>Somme des quantités de l'instantané courant.</summary>
        internal int InventoryTotalCount
        {
            get
            {
                var total = 0;
                foreach (var pair in _inventory)
                {
                    total += pair.Value;
                }

                return total;
            }
        }

        // — Adoption et craft (v21) ————————————————————————————————————————————————
        private int _inventoryAdoptionsRequested;
        private int _inventoryAdoptionsSent;

        /// <summary>
        /// Adoption demandée par le serveur mais **différée** : `Inventory.main` n'existe qu'une fois le
        /// monde chargé, et le handshake arrive avant. Mesuré au premier run AD : l'adoption était partie
        /// avec `0 objet(s)` alors que le joueur avait du titane — le coût du craft était donc refusé pour
        /// une raison qui n'avait rien à voir avec le serveur.
        /// </summary>
        private bool _adoptionPending;
        private int _craftRequestsSent;
        private int _craftResults;
        private int _craftsGranted;
        private int _craftsRefused;
        private int _craftsRateLimited;
        private CraftOutcome _lastCraftOutcome;
        private ushort _lastCraftTech;
        private ushort _lastCraftMissingTech;
        private int _lastCraftRevision = -1;
        private int _nextCraftRequestId = 1;

        internal int InventoryAdoptionsRequested => _inventoryAdoptionsRequested;
        internal int InventoryAdoptionsSent => _inventoryAdoptionsSent;
        internal int CraftRequestsSent => _craftRequestsSent;
        internal int CraftResults => _craftResults;
        internal int CraftsGranted => _craftsGranted;
        internal int CraftsRefused => _craftsRefused;

        /// <summary>Refus de **cadence** reçus (v21) : le serveur borne le rythme des demandes.</summary>
        internal int CraftsRateLimited => _craftsRateLimited;
        internal CraftOutcome LastCraftOutcome => _lastCraftOutcome;
        internal ushort LastCraftTech => _lastCraftTech;
        internal ushort LastCraftMissingTech => _lastCraftMissingTech;
        internal int LastCraftRevision => _lastCraftRevision;

        /// <summary>
        /// Envoie au serveur l'inventaire **réel** du joueur, une fois, en réponse à sa demande d'adoption
        /// (v21). C'est la seule déclaration que le protocole accepte : bornée, triée, et le serveur la
        /// remplace ensuite par ses propres décisions.
        /// </summary>
        internal bool SendInventoryAdopt()
        {
            if (_server == null || _server.ConnectionState != ConnectionState.Connected)
            {
                CoopClientPlugin.Log.LogWarning("[inventaire] adoption non envoyée : pas de connexion au serveur.");
                return false;
            }

            var entries = InventoryReconciler.CollectLocal(out var distinct, out var truncated);

            if (truncated)
            {
                CoopClientPlugin.Log.LogWarning(
                    $"[inventaire] adoption tronquée : {distinct} objets distincts > {Protocol.MaxInventoryEntries} " +
                    "— les suivants ne seront pas connus du serveur.");
            }

            _writer.Reset();
            new InventoryAdoptMessage { Entries = entries }.Write(_writer);
            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);

            _inventoryAdoptionsSent++;

            var names = entries.Length == 0
                ? "vide"
                : string.Join(", ", Array.ConvertAll(entries, entry => $"{entry.TechTypeName} ×{entry.Count}"));

            CoopClientPlugin.Log.LogInfo(
                $"[inventaire] adoption envoyée : {entries.Length} objet(s) [{names}] " +
                $"— le serveur les remplace ensuite par ses décisions.");
            return true;
        }

        /// <summary>
        /// Demande un **craft** au serveur (v21). Le client ne prélève rien et ne fabrique rien avant la
        /// réponse : c'est le serveur qui lit la recette et paie le coût depuis son registre.
        ///
        /// Rend l'identifiant de demande (`0` si rien n'est parti) pour que l'appelant puisse corréler.
        /// </summary>
        internal int SendCraftRequest(ushort techType)
        {
            if (techType == 0)
            {
                CoopClientPlugin.Log.LogWarning("[craft] demande non envoyée : TechType 0.");
                return 0;
            }

            if (_server == null || _server.ConnectionState != ConnectionState.Connected)
            {
                CoopClientPlugin.Log.LogWarning("[craft] demande non envoyée : pas de connexion au serveur.");
                return 0;
            }

            var requestId = _nextCraftRequestId++;
            if (_nextCraftRequestId <= 0)
            {
                _nextCraftRequestId = 1;
            }

            _writer.Reset();
            new CraftRequestMessage
            {
                RequestId = requestId,
                TechType = techType,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
            _craftRequestsSent++;

            CoopClientPlugin.Log.LogInfo(
                $"[craft] demande envoyée : TechType {techType} (requête {requestId}) — le serveur arbitre le coût.");
            return requestId;
        }

        /// <summary>
        /// Demande au serveur de retirer des unités du registre (v20). C'est une **demande** : le client ne
        /// retire rien lui-même, et c'est l'instantané qui suit la décision qui déclenche la réconciliation
        /// locale. Un refus ne change donc jamais `Inventory.main`.
        /// </summary>
        internal bool SendInventoryDebit(string techTypeName, int count)
        {
            if (string.IsNullOrEmpty(techTypeName) || count <= 0 || count > Protocol.MaxInventoryStack)
            {
                CoopClientPlugin.Log.LogWarning(
                    $"[inventaire] débit non envoyé : quantité ou nom invalide ('{techTypeName}' ×{count}).");
                return false;
            }

            if (_server == null || _server.ConnectionState != ConnectionState.Connected)
            {
                CoopClientPlugin.Log.LogWarning(
                    "[inventaire] débit non envoyé : pas de connexion au serveur.");
                return false;
            }

            _writer.Reset();
            new InventoryDebitMessage
            {
                PlayerId = _assignedPlayerId,
                TechTypeName = techTypeName,
                Count = count,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);

            CoopClientPlugin.Log.LogInfo(
                $"[inventaire] débit demandé : '{techTypeName}' ×{count} (révision locale {_inventoryRevision})");
            return true;
        }
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
            // Une adresse absente est une situation **normale** (le joueur ne l'a pas encore saisie) :
            // elle n'est ni remplacée par la boucle locale, ni traitée comme une erreur ici.
            _host = host ?? string.Empty;
            _port = port;
            _username = NormalizeUsername(username);
        }

        /// <summary>
        /// Vrai une fois qu'une adresse est connue (argument, fichier ou saisie). Tant qu'elle est fausse,
        /// le client ne tente rien et le monde n'est pas chargé.
        /// </summary>
        internal bool HasEndpoint => !string.IsNullOrEmpty(_host);

        /// <summary>Adresse en cours, pour le panneau et pour la sonde `-coopdebug=join`.</summary>
        internal string EndpointHost => _host;

        internal int EndpointPort => _port;

        /// <summary>
        /// Change d'adresse **à chaud** : coupe la session en cours, oublie l'acceptation, et relance la
        /// connexion vers la nouvelle adresse. C'est le chemin du bouton « Rejoindre » — sans lui, saisir
        /// une IP après un échec obligerait à relancer le jeu.
        /// </summary>
        internal void SetEndpoint(string host, ushort port)
        {
            if (string.IsNullOrEmpty(host))
            {
                return;
            }

            var changed = !string.Equals(_host, host, StringComparison.Ordinal) || _port != port;
            _host = host;
            _port = port;

            CoopClientPlugin.Log.LogInfo($"[net] nouvelle adresse : {_host}:{_port}" + (changed ? " (changement)" : ""));

            if (_server != null)
            {
                try
                {
                    _server.Disconnect();
                }
                catch (Exception exception)
                {
                    // Une session déjà fermée n'est pas une panne : la relance juste après la remplace.
                    CoopClientPlugin.Log.LogDebug($"[net] déconnexion avant changement d'adresse : {exception.Message}");
                }

                _server = null;
            }

            // Une nouvelle session ne doit pas hériter des demandes de craft de la précédente.
            CraftGuard.Clear();

            _accepted = false;
            _assignedPlayerId = 0;
            _saveWaitStarted = -1.0;
            _loggedSaveWait = false;
            _loggedSaveTimeout = false;
            _nextReconnectTime = 0;
            Connect();
        }

        /// <summary>
        /// Ramène le pseudo dans la forme admise par le protocole au lieu de le laisser partir tel quel.
        ///
        /// La règle vit dans <see cref="Usernames.Sanitize"/> — testée hors jeu — et n'est plus
        /// réimplémentée ici : un pseudo Steam de 40 caractères faisait refuser le handshake
        /// (« pseudo trop long »), et un pseudo avec un saut de ligne cassait les journaux. On garde
        /// seulement le journal de ce qui a changé, pour que l'utilisateur comprenne le nom affiché.
        /// </summary>
        private static string NormalizeUsername(string username)
        {
            var sanitized = Usernames.Sanitize(username);
            if (!string.Equals(sanitized, username, StringComparison.Ordinal))
            {
                CoopClientPlugin.Log.LogWarning(
                    $"[net] pseudo normalisé : '{username}' -> '{sanitized}' " +
                    $"(borne {Protocol.MaxUsernameLength}, caractères de contrôle retirés).");
            }

            return sanitized;
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

            if (!HasEndpoint)
            {
                CoopClientPlugin.Log.LogInfo(
                    "[net] aucune adresse de serveur — en attente d'une saisie (panneau du menu, " +
                    $"{ServerAddressPreference.ArgumentName}=IP:port, ou {ServerAddressPreference.EnvironmentVariableName}). " +
                    "Le monde ne sera pas chargé avant.");
                return;
            }

            Connect();
        }

        private void Connect()
        {
            // L'empreinte de la partie n'est connue qu'une fois le menu résolu (`AutoLoadBehaviour`).
            // Se connecter avant enverrait un handshake incapable de nommer la sauvegarde : le refus
            // du serveur serait exact, mais il accuserait le client de ne pas avoir de partie alors
            // qu'il n'a simplement pas encore lu son menu. On attend donc, avec une borne : au-delà,
            // on se connecte quand même et le refus reste **nommé**.
            var now = _clock.Elapsed.TotalSeconds;
            var save = SaveIdentitySource.Current;
            if (save == null)
            {
                if (_saveWaitStarted < 0)
                {
                    _saveWaitStarted = now;
                }

                var waited = now - _saveWaitStarted;
                if (waited < Protocol.SaveIdentityResolveTimeoutSeconds)
                {
                    if (!_loggedSaveWait)
                    {
                        _loggedSaveWait = true;
                        CoopClientPlugin.Log.LogInfo(
                            $"[net] attente de l'empreinte de sauvegarde avant connexion " +
                            $"({Protocol.SaveIdentityResolveTimeoutSeconds:F0} s max) — le menu doit d'abord résoudre la partie.");
                    }

                    // Repli toutes les 500 ms : c'est la boucle de reconnexion existante qui porte
                    // l'attente, pas une seconde boucle.
                    _nextReconnectTime = now + 0.5;
                    return;
                }

                if (!_loggedSaveTimeout)
                {
                    _loggedSaveTimeout = true;
                    CoopClientPlugin.Log.LogWarning(
                        $"[net] empreinte de sauvegarde toujours inconnue après {waited:F0} s — " +
                        "connexion quand même (le serveur la refusera nommément).");
                }
            }

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

            // Adoption (v21) : envoyée dès que le monde est chargé, si le serveur l'avait demandée avant.
            if (_adoptionPending
                && AutoLoadBehaviour.WorldLoaded
                && Player.main != null
                && Inventory.main != null)
            {
                _adoptionPending = false;
                CoopClientPlugin.Log.LogInfo("[inventaire] adoption différée : le monde est chargé, envoi de l'inventaire réel.");
                SendInventoryAdopt();
            }

            // Sans adresse, il n'y a rien à tenter : la boucle de reconnexion resterait sinon à écrire
            // un échec toutes les 500 ms en accusant le réseau alors que l'adresse manque.
            if (!HasEndpoint)
            {
                return;
            }

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

        /// <summary>
        /// Applique une position autoritaire reçue du serveur (v13, P1).
        ///
        /// Trois gardes, chacune pour une raison différente :
        /// - **monde chargé** : téléporter le joueur dans la scène du menu n'a aucun sens ;
        /// - **pas en véhicule** : le siège appartient au véhicule, et `SetPosition` sur le joueur
        ///   pendant qu'il est monté le désolidarise du siège ;
        /// - **seuil** : une correction de quelques centimètres est du bruit d'interpolation, pas une
        ///   divergence ; l'appliquer ferait trembler le joueur.
        ///
        /// Une correction reçue mais **non appliquée** est comptée séparément : c'est elle qui dit
        /// si le serveur corrige trop souvent (le critère d'arrêt à 5 % du §43.6 se lit sur ces
        /// compteurs, pas sur une impression).
        /// </summary>
        private void ApplyPositionCorrection(PlayerCorrectionMessage correction)
        {
            var player = Player.main;
            var root = Player.mainObject;
            var applied = false;
            var distance = 0f;

            if (player != null && root != null)
            {
                var target = new Vector3(correction.X, correction.Y, correction.Z);
                distance = Vector3.Distance(root.transform.position, target);

                var inVehicle = player.inSeamoth || player.inExosuit;
                applied = AutoLoadBehaviour.WorldLoaded
                          && !inVehicle
                          && distance > Protocol.CorrectionApplyThresholdMeters;

                if (applied)
                {
                    player.SetPosition(target, root.transform.rotation);
                    _correctionsApplied++;
                }
                else
                {
                    _correctionsSkipped++;
                }
            }
            else
            {
                _correctionsSkipped++;
            }

            // Même régime que les autres diagnostics : les premiers, puis un sur trente. Un log par
            // correction à 20 Hz noierait le journal, et un log muet ne prouverait rien.
            if (_correctionsReceived <= 3 || _correctionsReceived % 30 == 0)
            {
                string why;
                if (player == null || root == null)
                {
                    why = "joueur local indisponible";
                }
                else if (!AutoLoadBehaviour.WorldLoaded)
                {
                    why = "monde pas encore chargé";
                }
                else if (player.inSeamoth || player.inExosuit)
                {
                    why = "joueur en véhicule";
                }
                else if (!applied)
                {
                    why = string.Format(
                        CultureInfo.InvariantCulture,
                        "sous le seuil ({0:F2} m)",
                        Protocol.CorrectionApplyThresholdMeters);
                }
                else
                {
                    why = "appliquée";
                }

                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "[position] correction reçue (n°{0}) : écart={1:F2} m, appliquée={2} — {3} " +
                    "(tick serveur={4}, motif : {5})",
                    _correctionsReceived,
                    distance,
                    applied ? "oui" : "non",
                    why,
                    correction.ServerTick,
                    correction.Reason);

                if (applied)
                {
                    CoopClientPlugin.Log.LogInfo(line);
                }
                else
                {
                    CoopClientPlugin.Log.LogWarning(line);
                }
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
                $"correctionsRecues={_correctionsReceived} correctionsAppliquees={_correctionsApplied} " +
                $"correctionsIgnorees={_correctionsSkipped} " +
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
            CraftGuard.Clear();
            _remotes?.ClearAll();
            _vehicles?.ReleaseAll("déconnexion du serveur");
            _nextReconnectTime = _clock.Elapsed.TotalSeconds + ReconnectDelaySeconds;
        }

        private void SendHandshake()
        {
            // L'empreinte part avec le handshake : c'est elle qui transforme « les deux instances
            // chargent la même sauvegarde » d'hypothèse en contrat vérifié (P0). Si elle n'est pas
            // encore résolue, elle part vide — et le serveur la refuse nommément plutôt que de
            // laisser une divergence silencieuse.
            var save = SaveIdentitySource.Current
                       ?? SaveIdentity.Create(string.Empty, string.Empty, -1, string.Empty);

            _writer.Reset();
            new HandshakeRequest
            {
                ProtocolVersion = Protocol.Version,
                GameVersion = _gameVersion,
                ModListHash = _modListHash,
                Username = _username,
                Save = save,
            }.Write(_writer);

            _server.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
            CoopClientPlugin.Log.LogInfo(
                $"[net] handshake envoyé (protocole v{Protocol.Version}, jeu={_gameVersion}, save={save.Describe()})");
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

                case MessageType.PlayerCorrection:
                {
                    var correction = PlayerCorrectionMessage.Read(reader);
                    _correctionsReceived++;
                    ApplyPositionCorrection(correction);
                    break;
                }

                case MessageType.Disconnect:
                {
                    var message = DisconnectMessage.Read(reader);
                    CoopClientPlugin.Log.LogWarning($"[net] le serveur a fermé la connexion : {message.Reason}");
                    _accepted = false;
                    _assignedPlayerId = 0;
                    _server = null;
                    CraftGuard.Clear();
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

                case MessageType.VehicleUpgrades:
                {
                    var upgrades = VehicleUpgradesMessage.Read(reader);
                    _vehicles?.OnUpgrades(upgrades);
                    break;
                }

                case MessageType.EntitySpawnResult:
                {
                    HandleEntitySpawnResult(EntitySpawnResultMessage.Read(reader));
                    break;
                }

                case MessageType.EntitySpawn:
                {
                    var spawn = EntitySpawnMessage.Read(reader);
                    CoopClientPlugin.Log.LogInfo(
                        $"[net] apparition reçue : clé=0x{spawn.Key:x16} '{spawn.TechTypeName}' " +
                        $"identité '{spawn.UniqueId}'");
                    EntitySpawner.Enqueue(spawn);
                    break;
                }

                case MessageType.EntityDespawn:
                {
                    var despawn = EntityDespawnMessage.Read(reader);
                    var kind = despawn.Kind == EntityKind.Vehicle
                        ? "véhicule"
                        : despawn.Kind == EntityKind.Creature
                            ? "créature"
                            : "objet";
                    CoopClientPlugin.Log.LogInfo(
                        $"[net] disparition reçue : clé=0x{despawn.Key:x16} ({kind}) — motif « {despawn.Reason} »");
                    EntityDespawner.Handle(despawn);
                    break;
                }

                case MessageType.WorldTime:
                {
                    var worldTime = WorldTimeMessage.Read(reader);
                    _worldTimeMessages++;
                    _lastWorldTimeServer = worldTime.TimePassed;

                    var cycle = DayNightCycle.main;
                    if (cycle != null)
                    {
                        // L'écart est lu **avant** d'écrire : après, il vaudrait zéro par construction et
                        // la sonde ne mesurerait plus rien. En dessous du seuil, on laisse l'horloge locale
                        // avancer : la réécrire chaque seconde ferait sauter le soleil pour un aller-retour.
                        var error = Mathf.Abs(cycle.timePassedAsFloat - worldTime.TimePassed);
                        _lastWorldTimeError = error;
                        if (error > Protocol.WorldTimeApplyThresholdSeconds)
                        {
                            // `SetTimePassed` existe mais est marquée obsolète dans le jeu (« Do not mess
                            // with timePassed! ») : on écrit le champ public qu'elle écrit elle-même,
                            // pour ne pas dépendre d'une méthode que le jeu déconseille (et pour ne pas
                            // laisser un avertissement de compilation dans le build).
                            cycle.timePassedAsDouble = worldTime.TimePassed;
                            _worldTimeApplied++;
                            _lastWorldTimeAppliedError = error;
                            if (error > _maxWorldTimeAppliedError)
                            {
                                _maxWorldTimeAppliedError = error;
                            }
                            CoopClientPlugin.Log.LogInfo(
                                $"[temps] recalé sur le serveur : écart de {error:F1} s corrigé " +
                                $"({_worldTimeApplied} application(s) sur {_worldTimeMessages} message(s)).");
                        }
                    }

                    break;
                }

                case MessageType.VehicleControlResult:
                {
                    var result = VehicleControlResultMessage.Read(reader);
                    if (result.Granted)
                    {
                        _vehicleControlGranted++;
                    }
                    else
                    {
                        _vehicleControlRefused++;
                    }

                    CoopClientPlugin.Log.LogInfo(
                        $"[conduite] résultat : clé=0x{result.VehicleKey:x16} accordée={result.Granted} " +
                        $"conduite par {result.DriverId}");

                    VehicleDriverBehaviour.OnControlResult(result);
                    break;
                }

                case MessageType.InventorySnapshot:
                {
                    var snapshot = InventorySnapshotMessage.Read(reader);
                    _inventoryMessages++;

                    // Un instantané plus ancien que l'état courant est **écarté** et compté : le réseau
                    // fiable livre dans l'ordre, donc ce cas signale une révision serveur incohérente, pas
                    // une latence. L'appliquer ferait reculer l'inventaire du joueur.
                    if (snapshot.Revision < _inventoryRevision)
                    {
                        _inventoryStale++;
                        CoopClientPlugin.Log.LogWarning(
                            $"[inventaire] instantané périmé écarté : révision {snapshot.Revision} " +
                            $"< {_inventoryRevision} ({_inventoryStale} écarté(s)).");
                        break;
                    }

                    // La réconciliation se calcule **avant** de remplacer le miroir, et seulement sur les
                    // noms que les instantanés citent : l'admission (vide) ne vide donc pas l'inventaire
                    // sauvegardé du joueur (v20, `InventoryReconciliation.Compute`).
                    var reconciliation = InventoryReconciliation.Compute(
                        _inventoryPrevious,
                        snapshot.Entries,
                        InventoryReconciler.CountLocal);

                    _inventory.Clear();
                    foreach (var entry in snapshot.Entries)
                    {
                        _inventory[entry.TechTypeName] = entry.Count;
                    }

                    _inventoryRevision = snapshot.Revision;
                    _inventoryPrevious = snapshot.Entries;

                    // Référence de la reprise : un retrait reporté (craft en cours) sera réévalué contre
                    // cet inventaire dès que la protection de son matériau sera levée.
                    InventoryReconciler.RememberServerState(snapshot.Entries);

                    var names = snapshot.Entries.Length == 0
                        ? "vide"
                        : string.Join(", ", Array.ConvertAll(
                            snapshot.Entries,
                            entry => $"{entry.TechTypeName} ×{entry.Count}"));

                    CoopClientPlugin.Log.LogInfo(
                        $"[inventaire] instantané du serveur : révision {snapshot.Revision}, " +
                        $"{snapshot.Entries.Length} objet(s), {InventoryTotalCount} unité(s) [{names}]");

                    if (reconciliation.MissingLocally > 0)
                    {
                        // Rien n'est créé : un écart où le serveur possède davantage est signalé, il ne
                        // se « corrige » pas en fabriquant un objet dans le monde.
                        CoopClientPlugin.Log.LogWarning(
                            $"[inventaire] réconciliation : {reconciliation.MissingLocally} objet(s) que le " +
                            "serveur compte et que le jeu n'a pas encore — aucune création locale (limite connue).");
                    }

                    InventoryReconciler.Apply(reconciliation.Removals, CoopClientPlugin.Log);
                    break;
                }

                case MessageType.InventoryAdoptionRequest:
                {
                    var request = InventoryAdoptionRequestMessage.Read(reader);
                    _inventoryAdoptionsRequested++;

                    if (AutoLoadBehaviour.WorldLoaded && Player.main != null && Inventory.main != null)
                    {
                        CoopClientPlugin.Log.LogInfo(
                            $"[inventaire] adoption demandée par le serveur (n°{request.Sequence}) — envoi de l'inventaire local.");

                        SendInventoryAdopt();
                    }
                    else
                    {
                        _adoptionPending = true;
                        CoopClientPlugin.Log.LogInfo(
                            $"[inventaire] adoption demandée par le serveur (n°{request.Sequence}) — différée jusqu'au chargement du monde " +
                            "(Inventory.main n'existe pas encore).");
                    }

                    break;
                }

                case MessageType.CraftResult:
                {
                    var result = CraftResultMessage.Read(reader);
                    _craftResults++;
                    _lastCraftOutcome = result.Outcome;
                    _lastCraftTech = result.TechType;
                    _lastCraftMissingTech = result.MissingTechType;
                    _lastCraftRevision = result.Revision;

                    if (result.Granted)
                    {
                        _craftsGranted++;
                    }
                    else
                    {
                        _craftsRefused++;

                        if (result.Outcome == CraftOutcome.RateLimited)
                        {
                            _craftsRateLimited++;
                        }
                    }

                    // La reprise du craft local est **faite ici**, sur le thread principal, avant que
                    // l'instantané du coût ne soit traité : le jeu consomme ses matériaux lui-même.
                    CraftGuard.OnResult(result);

                    CoopClientPlugin.Log.LogInfo(
                        $"[craft] résultat : TechType {result.TechType} {(result.Granted ? "ACCORDÉ" : "REFUSÉ")} " +
                        $"({result.Outcome}" +
                        (result.MissingTechType != 0
                            ? $", manque TechType {result.MissingTechType} ×{result.MissingAmount}"
                            : string.Empty) +
                        $") — révision {result.Revision}, requête {result.RequestId}");
                    break;
                }

                case MessageType.InventoryDebitResult:
                {
                    var result = InventoryDebitResultMessage.Read(reader);
                    _inventoryDebitResults++;

                    if (result.Granted)
                    {
                        _inventoryDebitsGranted++;
                    }
                    else
                    {
                        _inventoryDebitsRefused++;
                        _inventoryLastDebitRefusal = result.Reason.ToString();
                    }

                    CoopClientPlugin.Log.LogInfo(
                        $"[inventaire] débit {(result.Granted ? "accordé" : "REFUSÉ")} : " +
                        $"'{result.TechTypeName}' ×{result.Count} ({result.Reason}) — révision " +
                        $"{result.Revision}, revendiqué sous l'id {result.ClaimedBy}");
                    break;
                }

                case MessageType.PickupResult:
                {
                    var result = PickupResultMessage.Read(reader);
                    _pickupResults[result.ItemKey] = result;
                    if (result.Granted)
                    {
                        _pickupGranted++;
                        _lastPickupGrantedName = result.TechTypeName;
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
