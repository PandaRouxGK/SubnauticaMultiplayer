using System;
using System.Diagnostics;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Coop.Server
{
    /// <summary>
    /// Plugin d'instance SERVEUR.
    ///
    /// Rappel M0 : le bootstrap de Subnautica détruit le GameObject de BepInEx, ce qui tue
    /// silencieusement tout Update() d'un BaseUnityPlugin. Toute boucle est donc hébergée sur
    /// notre propre objet persistant (DontDestroyOnLoad).
    ///
    /// Le rôle (serveur/client) vient de la ligne de commande et non d'un fichier de config :
    /// deux instances lancées depuis le même dossier de jeu partagent BepInEx/config, donc un
    /// réglage de fichier ne pourrait pas les distinguer.
    /// </summary>
    [BepInPlugin(Guid, "Coop Server", "0.1.0")]
    public class CoopServerPlugin : BaseUnityPlugin
    {
        public const string Guid = "coop.subnautica.server";

        internal static ManualLogSource Log;
        internal static ServerNetwork Network;
        internal static InstanceLogFile InstanceLog;

        /// <summary>
        /// Vrai si cette instance est un **serveur dédié** : lancée en `-batchmode` (sans fenêtre), elle ne
        /// doit pas exposer de personnage joueur. Unity expose ce drapeau lui-même, donc aucun argument
        /// supplémentaire à retenir côté launcher : `-batchmode` suffit.
        /// </summary>
        internal static bool Dedicated => Application.isBatchMode;

        private void Awake()
        {
            Log = Logger;

            var mode = LaunchModeParser.Current;
            Log.LogInfo($"[boot] CoopServer chargé — mode={mode} (attendu: {LaunchModeParser.ArgumentName}=server)");

            if (mode != LaunchMode.Server)
            {
                Log.LogInfo("[boot] cette instance n'est pas serveur : plugin serveur inactif.");
                return;
            }

            // Avant tout le reste : tout ce qui suit doit atterrir dans le log de cette instance,
            // et pas seulement dans le fichier partagé des deux processus.
            InstanceLog = InstanceLogFile.Attach(Log, "server");

            // Journalisé **après** le branchement du journal d'instance : sinon la ligne qui dit si le
            // serveur est dédié n'atterrit que dans le `LogOutput.log` partagé, et pas dans le journal
            // qu'on relit.
            Log.LogInfo($"[boot] batchmode={Application.isBatchMode} — " +
                (Dedicated
                    ? "serveur dédié : aucun personnage joueur ne sera exposé aux clients."
                    : "serveur fenêtré : le joueur de l'hôte est diffusé comme id=9999."));

            var port = Config.Bind("Reseau", "Port", Protocol.DefaultPort, "Port UDP d'écoute du serveur.");

            var gameVersion = Application.version;
            var modHash = BepInExModList.ComputeHash();
            Log.LogInfo($"[boot] jeu={gameVersion}, mods tiers={BepInExModList.ThirdPartyPluginCount()}, modHash={modHash}");

            var network = new ServerNetwork(gameVersion, modHash, Log);
            if (network.Start(port.Value))
            {
                Network = network;
            }
            else
            {
                // On continue quand même : le tick et le chargement du monde restent utiles,
                // et l'erreur est déjà journalisée.
                network.Dispose();
            }

            StartHeartbeat();

            var go = new GameObject("Coop.Server.Bootstrap");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<ServerTickBehaviour>();

            // AutoLoadBehaviour vit dans src/Shared : c'est exactement le même code qui amène le
            // client dans le monde. Le serveur reste seul propriétaire de la partie ; le client
            // voit sa sauvegarde protégée par SaveGuard.
            go.AddComponent<AutoLoadBehaviour>();

            // Autotest de la sauvegarde **côté hôte** : la garde est purement cliente, et le fait que
            // l'hôte puisse encore sauvegarder n'était prouvé par aucune tentative réelle.
            if (IsDebugFlagSet("save"))
            {
                Log.LogWarning("[boot] autotest de la sauvegarde serveur activé (-coopdebug=save).");
                go.AddComponent<HostSaveProbeBehaviour>();
            }

            // Canal de contrôle du launcher (bouton « Sauvegarder ») : un fichier dans BepInEx/coop/, relu
            // toutes les demi-secondes. Toujours branché — c'est une fonction du serveur, pas un autotest.
            go.AddComponent<ServerControlBehaviour>();

            // Serveur dédié : le joueur local de l'instance est retiré du monde (inactif) et n'est plus
            // diffusé. Sans ça, une instance sans fenêtre resterait un personnage immobile à la capsule.
            if (Dedicated)
            {
                go.AddComponent<DedicatedServerBehaviour>();
            }

            // Sonde de M4 (a) : le monde de test ne contient **aucun** véhicule, et c'est ce qui bloquait la
            // décision cadence/interpolation. La sonde fait apparaître un Seamoth, le déplace et mesure.
            if (IsDebugFlagSet("vehicle"))
            {
                Log.LogWarning("[boot] sonde de véhicule activée (-coopdebug=vehicle).");
                go.AddComponent<VehicleProbeBehaviour>();
            }

            // Met un Seamoth **dans la sauvegarde** de l'hôte, pour que l'étape K mesure la clé nominale
            // (identifiant sérialisé, identique des deux côtés) au lieu du rattachement par proximité.
            if (IsDebugFlagSet("vpersist"))
            {
                Log.LogWarning("[boot] sonde de véhicule persistant activée (-coopdebug=vpersist).");
                go.AddComponent<VehiclePersistProbeBehaviour>();
            }

            Log.LogInfo("[boot] GameObject 'Coop.Server.Bootstrap' créé (DontDestroyOnLoad).");
        }

        /// <summary>
        /// Vrai si <c>-coopdebug=&lt;nom&gt;</c> est présent sur la ligne de commande.
        ///
        /// Même convention que le plugin client : un autotest ne tourne que sur demande explicite, et
        /// jamais à cause d'un réglage partagé par les deux instances.
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

        // NOTE : surtout PAS de OnDestroy ici qui arrêterait le réseau. Subnautica détruit le
        // GameObject du manager BepInEx au chargement de son premier écran (constat du jalon M0) :
        // arrêter le socket ici le refermait quelques secondes après l'ouverture du port.
        // L'arrêt est pris en charge par ServerTickBehaviour, hébergé sur notre objet persistant.

        /// <summary>
        /// Thread CLR indépendant de tout objet Unity : sert de référence « le processus est vivant »
        /// même si un objet Unity est détruit ou qu'Unity cesse d'appeler Update().
        /// </summary>
        private static void StartHeartbeat()
        {
            var heartbeat = new Thread(() =>
            {
                var n = 0;
                while (true)
                {
                    Thread.Sleep(5000);
                    n++;
                    Log.LogInfo(
                        $"[heartbeat] #{n} ticks={ServerTickBehaviour.Ticks} abandonnes={ServerTickBehaviour.DroppedTicks} " +
                        $"clients={Network?.AcceptedClients ?? 0}");
                }
            })
            { IsBackground = true, Name = "Coop-Heartbeat" };
            heartbeat.Start();
        }
    }

    /// <summary>
    /// Boucle de simulation du serveur à pas fixe de 20 Hz, découplée du framerate.
    ///
    /// Garde-fous :
    ///  - <see cref="Time.unscaledDeltaTime"/> : le serveur doit ticker même si le jeu met le
    ///    temps en pause (timescale 0), sinon la simulation s'arrête toute seule.
    ///  - accumulateur en <c>double</c> : pas de dérive cumulée sur des heures.
    ///  - plafond de rattrapage : après un gros à-coup (chargement de monde, GC), on ne rejoue pas
    ///    2 secondes de simulation d'un coup — c'est la spirale de la mort. On abandonne le retard
    ///    et on le compte.
    /// </summary>
    public class ServerTickBehaviour : MonoBehaviour
    {
        /// <summary>Fréquence de simulation du serveur (Hz).</summary>
        public const double TickRate = 20.0;

        /// <summary>Durée d'un pas de simulation, en secondes (50 ms).</summary>
        public const double TickInterval = 1.0 / TickRate;

        private const int MaxCatchUpSteps = 5;
        private const double ReportInterval = 5.0;

        /// <summary>
        /// Nombre total de pas de simulation effectués. En <c>int</c> car C# n'autorise pas
        /// <c>volatile long</c> : à 20 Hz le débordement n'arrive qu'après ~3,4 ans.
        /// </summary>
        public static volatile int Ticks;

        /// <summary>Nombre de pas abandonnés faute de pouvoir rattraper le retard.</summary>
        public static volatile int DroppedTicks;

        /// <summary>Hook d'extension : les futurs systèmes de simulation s'abonneront ici.</summary>
        public static event Action<long> Tick;

        private double _accumulator;
        private long _tick;
        private long _dropped;
        private double _nextReport;
        private long _lastReportTicks;
        private double _lastReportWall;
        private readonly Stopwatch _wall = Stopwatch.StartNew();

        private void Start()
        {
            CoopServerPlugin.Log.LogInfo(
                $"[tick] boucle à pas fixe démarrée : {TickRate:F0} Hz ({TickInterval * 1000:F0} ms), rattrapage max {MaxCatchUpSteps} pas/frame");
        }

        private void Update()
        {
            // Le réseau est pompé à chaque frame, pas à chaque tick : LiteNetLib veut être
            // appelé souvent pour vider sa file de paquets sans ajouter de latence.
            CoopServerPlugin.Network?.Poll();

            _accumulator += Time.unscaledDeltaTime;

            var steps = 0;
            while (_accumulator >= TickInterval && steps < MaxCatchUpSteps)
            {
                _accumulator -= TickInterval;
                steps++;
                _tick++;
                Ticks = (int)_tick;
                CoopServerPlugin.Network?.BroadcastSnapshots(_tick);
                Tick?.Invoke(_tick);
            }

            // Retard trop important : on abandonne le surplus au lieu de l'accumuler.
            if (_accumulator >= TickInterval)
            {
                var abandoned = (long)(_accumulator / TickInterval);
                _accumulator -= abandoned * TickInterval;
                _dropped += abandoned;
                DroppedTicks = (int)_dropped;
            }

            if (_wall.Elapsed.TotalSeconds >= _nextReport)
            {
                Report();
                _nextReport += ReportInterval;
            }
        }

        private void OnDestroy()
        {
            // Seul propriétaire légitime du réseau : cet objet survit à la destruction du manager
            // BepInEx, donc il ne s'arrête qu'à la fermeture du processus.
            CoopServerPlugin.Log.LogInfo("[net] arrêt du serveur réseau (fin de vie de l'instance).");
            CoopServerPlugin.Network?.Dispose();
            CoopServerPlugin.Network = null;

            CoopServerPlugin.InstanceLog?.Dispose();
            CoopServerPlugin.InstanceLog = null;
        }

        private void Report()
        {
            var realSeconds = _wall.Elapsed.TotalSeconds;
            var simulatedSeconds = _tick * TickInterval;

            // Taux instantané sur la dernière fenêtre : c'est LUI qui doit valoir 20,00.
            // Le taux cumulé depuis le démarrage est plus bas, il traîne le retard du
            // chargement de monde (ticks abandonnés une fois pour toutes).
            var intervalReal = realSeconds - _lastReportWall;
            var intervalTicks = _tick - _lastReportTicks;
            var intervalRate = intervalReal > 0 ? intervalTicks / intervalReal : 0;

            CoopServerPlugin.Log.LogInfo(
                $"[tick] tick={_tick} tauxInstantane={intervalRate:F2}/s " +
                $"simule={simulatedSeconds:F2}s reel={realSeconds:F2}s " +
                $"derive={(simulatedSeconds - realSeconds) * 1000:F0}ms " +
                $"abandonnes={_dropped} reste={_accumulator * 1000:F1}ms scene='{SceneManager.GetActiveScene().name}'");

            _lastReportTicks = _tick;
            _lastReportWall = realSeconds;
        }
    }
}
