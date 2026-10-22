using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using HarmonyLib;

namespace Coop.Client
{
    /// <summary>
    /// Le **vrai** craft du jeu demande l'accord du serveur (v21).
    ///
    /// Jusqu'ici, seul un message explicite (`CraftRequest`) savait poser la question : le fabricator
    /// continuait de consommer localement, sans coût arbitré. Ce garde intercepte le point d'entrée que le
    /// menu du fabricator utilise réellement — `ITreeActionReceiver.PerformAction(TechType)`, que `Crafter`
    /// implémente explicitement et qui appelle `Crafter.Craft` (redéfini par `GhostCrafter`) — et **bloque**
    /// l'appel jusqu'à la décision.
    ///
    /// Mesuré en Survie le 2026-10-22, deux fois plutôt qu'une : `CrafterLogic.Craft` ne consomme **rien**
    /// (il ne fait que planifier le produit), et `GhostCrafter.Craft` — qui appelle `CrafterLogic.ConsumeEnergy`
    /// puis `CrafterLogic.ConsumeResources` — est privé et virtual, appelé **par** l'action du menu. Un garde
    /// posé sur `CrafterLogic.Craft` laissait donc le fabricator d'un vrai joueur consommer ses matériaux,
    /// même refusé ; le seul point qui précède toute consommation et que le bouton emprunte est
    /// `PerformAction`.
    ///
    /// Pourquoi bloquer plutôt que laisser passer puis corriger : un craft déjà lancé a consommé ses
    /// matériaux et planifié son produit ; le défaire laisserait un objet fabriqué pour rien. Bloquer avant
    /// la consommation est la seule façon de rendre le refus **réel**.
    ///
    /// Le prix de ce choix est une latence d'un aller-retour avant que le fabricator ne démarre : c'est
    /// visible, borné, et préférable à un craft gratuit. L'appel est rejoué **sur la même instance** dès
    /// l'accord, avec un jeton d'approbation consommé une seule fois — sans lui, le préfixe bloquerait sa
    /// propre reprise.
    ///
    /// **Ce que ce garde ne fait pas** : il ne crée pas le produit (le jeu le fait, comme d'habitude), il
    /// ne vérifie pas que le client possède réellement les matériaux — c'est le registre du serveur qui
    /// fait foi — et il laisse passer tout appel hors session (solo, menu, avant le handshake).
    /// </summary>
    internal static class CraftGuard
    {
        private const string HarmonyId = "coop.subnautica.client.craft";

        /// <summary>
        /// Demandes en attente, indexées par **id de requête**, avec un jeton d'approbation par instance.
        /// Pur, donc testé hors jeu : deux crafts de la même recette ne se recouvrent plus.
        /// </summary>
        private static readonly CraftRequestLedger Ledger = new CraftRequestLedger();

        /// <summary>Instances par id, pour rejouer l'action sur celle qui a demandé (références Unity).</summary>
        private static readonly Dictionary<int, Crafter> Instances = new Dictionary<int, Crafter>();

        /// <summary>Instant de l'interception par id de requête : mesure la latence ajoutée par l'accord.</summary>
        private static readonly Dictionary<int, float> SentAt = new Dictionary<int, float>();

        private static float _lastLatencyMs = -1f;
        private static float _minLatencyMs = -1f;
        private static float _maxLatencyMs = -1f;

        /// <summary>
        /// Matériaux dont le prélèvement local est **en cours** : la réconciliation ne doit pas les retirer
        /// avant que le jeu ne les consomme lui-même, sinon le craft approuvé échouerait faute de matériaux et
        /// le registre serait déjà débité. La comptabilité (références partagées, fenêtre) est **pure** et
        /// testée hors jeu : `Coop.Model/MaterialProtection.cs`.
        /// </summary>
        private static readonly MaterialProtection InFlight = new MaterialProtection();

        private static ManualLogSource _log;
        private static int _blocked;
        private static int _replayed;
        private static int _refused;
        private static int _replayFailed;

        /// <summary>Dernière latence mesurée entre l'interception et la décision du serveur (ms).</summary>
        public static float LastLatencyMs => _lastLatencyMs;

        /// <summary>Latence minimale observée sur la session (ms), `-1` si aucune.</summary>
        public static float MinLatencyMs => _minLatencyMs;

        /// <summary>Latence maximale observée sur la session (ms), `-1` si aucune.</summary>
        public static float MaxLatencyMs => _maxLatencyMs;

        public static int BlockedCount => _blocked;
        public static int ReplayedCount => _replayed;
        public static int RefusedCount => _refused;
        public static int ReplayFailedCount => _replayFailed;

        /// <summary>Vrai si ce matériau est en cours de consommation par un craft approuvé.</summary>
        public static bool IsCostInFlight(string techTypeName)
        {
            return InFlight.IsProtected(NowMs(), techTypeName);
        }

        /// <summary>Horloge en millisecondes : la comptabilité pure prend le temps en argument.</summary>
        private static long NowMs()
        {
            return (long)(UnityEngine.Time.realtimeSinceStartup * 1000f);
        }

        public static bool Install(ManualLogSource log)
        {
            _log = log;

            // `PerformAction` est une implémentation **explicite** d'interface : son nom dans les métadonnées
            // est décoré. On la résout par la table d'interface, pas par son nom, pour ne pas dépendre de
            // cette décoration.
            MethodInfo target = null;
            try
            {
                var interfaceMethod = AccessTools.Method(
                    typeof(ITreeActionReceiver),
                    nameof(ITreeActionReceiver.PerformAction),
                    new[] { typeof(TechType) });
                var map = typeof(Crafter).GetInterfaceMap(typeof(ITreeActionReceiver));
                var index = interfaceMethod == null ? -1 : Array.IndexOf(map.InterfaceMethods, interfaceMethod);
                if (index >= 0)
                {
                    target = map.TargetMethods[index];
                }
            }
            catch (Exception exception)
            {
                log.LogError($"[craft] résolution de l'action de craft impossible : {exception.Message}");
            }

            if (target == null)
            {
                log.LogError(
                    "[craft] cible introuvable : Crafter.PerformAction(TechType) — le fabricator reste " +
                    "local, aucun coût n'est arbitré. Ce n'est pas silencieux, mais ce n'est pas la règle visée.");
                return false;
            }

            var prefix = AccessTools.Method(typeof(CraftGuard), nameof(Intercept));
            if (prefix == null)
            {
                log.LogError("[craft] méthode de préfixe introuvable (incohérence de build).");
                return false;
            }

            try
            {
                new Harmony(HarmonyId).Patch(target, new HarmonyMethod(prefix));
            }
            catch (Exception exception)
            {
                log.LogError($"[craft] échec du patch de l'action de craft : {exception}");
                return false;
            }

            log.LogInfo(
                "[craft] garde installée : action du fabricator interceptée (Crafter.PerformAction) — le craft " +
                "local attend désormais l'accord du serveur, qui prélève le coût de la recette du jeu ; ni " +
                "l'énergie ni les matériaux ne partent avant la décision.");
            return true;
        }

        /// <summary>
        /// Préfixe. Renvoyer `false` empêche le jeu de démarrer le craft ; l'accord rejouera l'appel.
        /// </summary>
        private static bool Intercept(Crafter __instance, TechType techType, ref bool __result)
        {
            var instanceId = __instance.GetInstanceID();

            // Reprise approuvée : le jeton vaut pour **cette** instance, une seule fois.
            if (Ledger.ConsumeApproval(instanceId))
            {
                return true;
            }

            var network = ClientNetworkBehaviour.Current;

            // Hors session : le mod ne s'interpose pas (solo, menu, avant le handshake).
            if (network == null || network.AssignedPlayerId == 0)
            {
                return true;
            }

            var requestId = network.SendCraftRequest((ushort)techType);
            if (requestId == 0)
            {
                // Pas de connexion au moment du geste : laisser le jeu faire ce qu'il faisait avant le mod
                // vaut mieux qu'un fabricator muet. C'est dit, et compté comme un craft non arbitré.
                _log?.LogWarning(
                    $"[craft] '{techType}' non arbitré : la demande n'a pas pu partir (pas de connexion).");
                return true;
            }

            if (!Ledger.Add(requestId, instanceId, (ushort)techType))
            {
                // Bloquer sans pouvoir rejouer laisserait un fabricator muet : on laisse passer ce craft
                // **et on le dit**. La borne (64) rend ce cas inatteignable en pratique.
                _log?.LogError(
                    $"[craft] '{techType}' non compté (requête {requestId} : table pleine ou id inattendu) — " +
                    "ce craft passe sans arbitrage du serveur.");
                return true;
            }

            Instances[instanceId] = __instance;
            SentAt[requestId] = UnityEngine.Time.realtimeSinceStartup;

            RememberCost(techType);
            _blocked++;

            // Le bouton reçoit `false` : ni énergie, ni matériaux, ni animation de craft ne partent.
            __result = false;

            _log?.LogInfo(
                $"[craft] interception : '{techType}' bloqué avant consommation, en attente du serveur " +
                $"(requête {requestId}, bloqués={_blocked}).");
            return false;
        }

        /// <summary>
        /// Décision reçue. Un accord rejoue l'appel **sur l'instance interceptée** ; un refus ne rejoue rien
        /// et le jeu n'a rien consommé.
        /// </summary>
        public static void OnResult(CraftResultMessage result)
        {
            if (result == null)
            {
                return;
            }

            var tech = (TechType)result.TechType;
            var latencyMs = TakeLatency(result.RequestId);

            // Une demande partie sans passer par le garde (sonde de cadence) n'a pas d'instant de départ :
            // on n'écrit pas « décision en -1 ms », qui se lirait comme une mesure.
            var latencyNote = latencyMs >= 0f ? $", décision en {latencyMs:F1} ms" : string.Empty;

            if (!result.Granted)
            {
                if (Ledger.TryRefuse(result.RequestId, out var refused))
                {
                    _refused++;

                    // Une demande refusée ne sera jamais rejouée : garder la référence au `Crafter` ne
                    // servirait plus, et la garderait en vie jusqu'à la fin de la session.
                    if (refused != null)
                    {
                        Instances.Remove(refused.InstanceId);
                    }

                    // Seule une demande passée par le garde a armé une protection : la lever pour un refus
                    // venu d'ailleurs (rafale brute de la sonde) découragerait un craft encore en attente.
                    ReleaseCost(tech);
                }

                _log?.LogWarning(
                    $"[craft] refus du serveur : '{tech}' ({result.Outcome}" +
                    (result.MissingTechType != 0
                        ? $", manque TechType {result.MissingTechType} ×{result.MissingAmount}"
                        : string.Empty) +
                    $") — rien n'a été consommé localement (requête {result.RequestId}{latencyNote}).");
                return;
            }

            if (!Ledger.TryApprove(result.RequestId, result.TechType, out var pending))
            {
                _log?.LogWarning(
                    $"[craft] accord de '{tech}' (requête {result.RequestId}) sans demande en attente ou " +
                    "recette incohérente — la reprise n'a rien à rejouer.");
                return;
            }

            if (!Instances.TryGetValue(pending.InstanceId, out var instance) || instance == null)
            {
                // Jeton armé pour une instance perdue (monde déchargé) : le retirer plutôt que le laisser
                // autoriser un craft ultérieur de cette instance recyclée par Unity.
                Ledger.ConsumeApproval(pending.InstanceId);
                ReleaseCost(tech);
                _log?.LogWarning(
                    $"[craft] accord de '{tech}' (requête {result.RequestId}) pour une instance perdue — " +
                    "la reprise n'a rien à rejouer.");
                return;
            }

            _replayed++;

            _log?.LogInfo(
                $"[craft] accord du serveur : '{tech}' — reprise du craft local sur le fabricator " +
                $"(requête {result.RequestId}{latencyNote}, reprises={_replayed}).");

            try
            {
                var started = ((ITreeActionReceiver)instance).PerformAction(tech);
                if (!started)
                {
                    _replayFailed++;
                    _log?.LogWarning(
                        $"[craft] la reprise de '{tech}' a été refusée par le jeu (recette non satisfaite " +
                        $"localement ?) — le registre est déjà débité (échecs={_replayFailed}).");
                }
                else
                {
                    // `PerformAction` consomme les ressources **avant** de démarrer (`GhostCrafter.Craft`),
                    // donc à ce point le jeu a payé : garder la protection 10 s de plus ne protégerait
                    // qu'un matériau déjà parti, et retarderait la réconciliation sans raison.
                    ReleaseCost(tech);

                    // La reprise est faite : l'instance n'a plus de raison d'être retenue (une référence
                    // Unity gardée pour rien empêcherait sa collecte jusqu'à la fin de la session).
                    Instances.Remove(pending.InstanceId);
                }
            }
            catch (Exception exception)
            {
                _replayFailed++;
                _log?.LogError($"[craft] la reprise de '{tech}' a levé {exception.GetType().Name} : {exception.Message}");
            }
        }

        /// <summary>Latence de la décision pour cet id, en ms (`-1` si l'id n'était pas suivi).</summary>
        private static float TakeLatency(int requestId)
        {
            if (!SentAt.TryGetValue(requestId, out var sent))
            {
                return -1f;
            }

            SentAt.Remove(requestId);
            var latency = (UnityEngine.Time.realtimeSinceStartup - sent) * 1000f;

            if (latency < 0f)
            {
                return -1f;
            }

            _lastLatencyMs = latency;
            _minLatencyMs = _minLatencyMs < 0f ? latency : Math.Min(_minLatencyMs, latency);
            _maxLatencyMs = Math.Max(_maxLatencyMs, latency);
            return latency;
        }

        /// <summary>
        /// Fermeture de session : aucune demande ni jeton de la session précédente ne doit survivre. Une
        /// réponse arrivée après la coupure ne rejoue donc rien — et le jeu n'a rien consommé pour elle.
        /// </summary>
        public static void Clear()
        {
            var pending = Ledger.PendingCount;
            Ledger.Clear();
            Instances.Clear();
            SentAt.Clear();
            InFlight.Clear();

            if (pending > 0)
            {
                _log?.LogInfo(
                    $"[craft] {pending} demande(s) en attente oubliée(s) à la fermeture de session.");
            }
        }

        /// <summary>
        /// Lève la protection d'une recette dont la demande a été **refusée** : le préfixe a bloqué avant
        /// toute consommation, donc la fenêtre de 10 s ne protège plus rien et ne ferait que retarder la
        /// réconciliation sur ce matériau. Deux recettes peuvent partager un ingrédient : la protection ne
        /// part qu'à la dernière référence, sinon un refus découvrirait un craft encore en attente.
        /// </summary>
        private static void ReleaseCost(TechType techType)
        {
            IReadOnlyList<string> released;

            try
            {
                var ingredients = TechData.GetIngredients(techType);
                if (ingredients == null)
                {
                    return;
                }

                var names = new List<string>(ingredients.Count);
                foreach (var ingredient in ingredients)
                {
                    names.Add(ingredient.techType.ToString());
                }

                released = InFlight.Release(names);
            }
            catch (Exception exception)
            {
                _log?.LogWarning(
                    $"[craft] levée de protection de '{techType}' illisible ({exception.GetType().Name}) — " +
                    "les matériaux resteront protégés jusqu'à l'expiration.");
                return;
            }

            if (released.Count > 0)
            {
                _log?.LogInfo(
                    $"[craft] protection levée : {string.Join(", ", released)} (consommation locale faite ou " +
                    "refus : rien ne reste à protéger) — la réconciliation peut de nouveau agir sur ce matériau.");
            }
        }

        /// <summary>Garde les noms de la recette hors de la réconciliation pendant la reprise locale.</summary>
        private static void RememberCost(TechType techType)
        {
            // En mode **Créatif/Freedom**, le jeu ne consomme rien : rien à protéger, et c'est la
            // réconciliation qui appliquera le coût décidé par le serveur (mesuré le 2026-10-22 : la
            // sauvegarde de test est en mode Créatif, et le titane restait dans l'inventaire).
            try
            {
                if (!GameModeUtils.RequiresIngredients())
                {
                    _log?.LogInfo(
                        $"[craft] mode sans ingrédients : le coût de '{techType}' sera retiré par la réconciliation.");
                    return;
                }
            }
            catch (Exception exception)
            {
                _log?.LogWarning(
                    $"[craft] mode de jeu illisible ({exception.GetType().Name}) — les matériaux sont protégés " +
                    "par précaution jusqu'à la reprise.");
            }

            try
            {
                var ingredients = TechData.GetIngredients(techType);
                if (ingredients == null)
                {
                    return;
                }

                var names = new List<string>(ingredients.Count);
                foreach (var ingredient in ingredients)
                {
                    names.Add(ingredient.techType.ToString());
                }

                var protectedNames = InFlight.Arm(NowMs(), names);

                // Sans cette ligne, la branche de protection était muette en Survie : impossible de savoir,
                // dans un journal, si le coût avait été écarté de la réconciliation ou non.
                if (protectedNames.Count > 0)
                {
                    _log?.LogInfo(
                        $"[craft] matériaux protégés {InFlight.WindowMs / 1000.0:F0} s : " +
                        $"{string.Join(", ", protectedNames)} (le craft approuvé les consomme lui-même).");
                }
            }
            catch (Exception exception)
            {
                _log?.LogWarning(
                    $"[craft] matériaux de '{techType}' illisibles ({exception.GetType().Name}) — la " +
                    "réconciliation pourrait retirer le coût avant la reprise.");
            }
        }

        public static string Summary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "bloqués={0} reprises={1} refus={2} échecs de reprise={3}",
                _blocked,
                _replayed,
                _refused,
                _replayFailed);
        }
    }
}
