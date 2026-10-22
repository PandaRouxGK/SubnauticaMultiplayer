using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// menu et le fabricator utilisent réellement — `CrafterLogic.Craft(TechType, float)` — et **bloque**
    /// l'appel jusqu'à la décision.
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

        private static readonly Dictionary<ushort, PendingCraft> Pending =
            new Dictionary<ushort, PendingCraft>();

        /// <summary>Technologies approuvées dont la reprise n'a pas encore été jouée.</summary>
        private static readonly HashSet<ushort> Approved = new HashSet<ushort>();

        /// <summary>
        /// Noms de matériaux dont le prélèvement local est **en cours** : la réconciliation ne doit pas les
        /// retirer avant que le jeu ne les consomme lui-même, sinon le craft approuvé échouerait faute de
        /// matériaux et le registre serait déjà débité.
        /// </summary>
        private static readonly Dictionary<string, float> InFlightCosts =
            new Dictionary<string, float>(StringComparer.Ordinal);

        private const float InFlightSeconds = 10f;

        private static ManualLogSource _log;
        private static int _blocked;
        private static int _replayed;
        private static int _refused;
        private static int _replayFailed;

        private sealed class PendingCraft
        {
            public CrafterLogic Instance;
            public float CraftTime;
        }

        public static int BlockedCount => _blocked;
        public static int ReplayedCount => _replayed;
        public static int RefusedCount => _refused;
        public static int ReplayFailedCount => _replayFailed;

        /// <summary>Vrai si ce matériau est en cours de consommation par un craft approuvé.</summary>
        public static bool IsCostInFlight(string techTypeName)
        {
            if (string.IsNullOrEmpty(techTypeName))
            {
                return false;
            }

            if (!InFlightCosts.TryGetValue(techTypeName, out var expiresAt))
            {
                return false;
            }

            if (UnityEngine.Time.realtimeSinceStartup > expiresAt)
            {
                InFlightCosts.Remove(techTypeName);
                return false;
            }

            return true;
        }

        public static bool Install(ManualLogSource log)
        {
            _log = log;

            var target = AccessTools.Method(
                typeof(CrafterLogic),
                "Craft",
                new[] { typeof(TechType), typeof(float) });

            if (target == null)
            {
                log.LogError(
                    "[craft] cible introuvable : CrafterLogic.Craft(TechType, float) — le fabricator reste " +
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
                log.LogError($"[craft] échec du patch de CrafterLogic.Craft : {exception}");
                return false;
            }

            log.LogInfo(
                "[craft] garde installée : CrafterLogic.Craft intercepté — un craft local attend désormais " +
                "l'accord du serveur, qui prélève le coût de la recette du jeu.");
            return true;
        }

        /// <summary>
        /// Préfixe. Renvoyer `false` empêche le jeu de démarrer le craft ; l'accord rejouera l'appel.
        /// </summary>
        private static bool Intercept(CrafterLogic __instance, TechType techType, float craftTime)
        {
            if (Approved.Remove((ushort)techType))
            {
                return true;
            }

            var network = ClientNetworkBehaviour.Current;

            // Hors session : le mod ne s'interpose pas (solo, menu, avant le handshake).
            if (network == null || network.AssignedPlayerId == 0)
            {
                return true;
            }

            if (network.SendCraftRequest((ushort)techType) == 0)
            {
                // Pas de connexion au moment du geste : laisser le jeu faire ce qu'il faisait avant le mod
                // vaut mieux qu'un fabricator muet. C'est dit, et compté comme un craft non arbitré.
                _log?.LogWarning(
                    $"[craft] '{techType}' non arbitré : la demande n'a pas pu partir (pas de connexion).");
                return true;
            }

            Pending[(ushort)techType] = new PendingCraft
            {
                Instance = __instance,
                CraftTime = craftTime,
            };

            RememberCost(techType);
            _blocked++;

            _log?.LogInfo(
                $"[craft] interception : '{techType}' bloqué avant consommation, en attente du serveur " +
                $"(bloqués={_blocked}).");
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

            if (!result.Granted)
            {
                Pending.Remove(result.TechType);
                _refused++;
                _log?.LogWarning(
                    $"[craft] refus du serveur : '{tech}' ({result.Outcome}" +
                    (result.MissingTechType != 0
                        ? $", manque TechType {result.MissingTechType} ×{result.MissingAmount}"
                        : string.Empty) +
                    ") — rien n'a été consommé localement.");
                return;
            }

            if (!Pending.TryGetValue(result.TechType, out var pending) || pending.Instance == null)
            {
                _log?.LogWarning(
                    $"[craft] accord de '{tech}' sans appel en attente — la reprise n'a rien à rejouer.");
                return;
            }

            Pending.Remove(result.TechType);
            Approved.Add(result.TechType);
            _replayed++;

            _log?.LogInfo(
                $"[craft] accord du serveur : '{tech}' — reprise du craft local sur le fabricator " +
                $"(reprises={_replayed}).");

            try
            {
                var started = pending.Instance.Craft(tech, pending.CraftTime);
                if (!started)
                {
                    _replayFailed++;
                    _log?.LogWarning(
                        $"[craft] la reprise de '{tech}' a été refusée par le jeu (recette non satisfaite " +
                        $"localement ?) — le registre est déjà débité (échecs={_replayFailed}).");
                }
            }
            catch (Exception exception)
            {
                _replayFailed++;
                _log?.LogError($"[craft] la reprise de '{tech}' a levé {exception.GetType().Name} : {exception.Message}");
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

                var expiresAt = UnityEngine.Time.realtimeSinceStartup + InFlightSeconds;

                foreach (var ingredient in ingredients)
                {
                    var name = ingredient.techType.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        InFlightCosts[name] = expiresAt;
                    }
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
