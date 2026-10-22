using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde du **vrai chemin de craft** (v21, `-coopdebug=craft`).
    ///
    /// Elle n'envoie **aucune** demande elle-même : elle appelle `ITreeActionReceiver.PerformAction` —
    /// l'action du bouton du fabricator, celle que le menu déclenche, avant toute consommation — et mesure
    /// ce que le garde en fait. Quatre affirmations :
    ///
    ///  1. l'appel est **bloqué avant consommation** : il rend `false` et les matériaux locaux ne bougent pas ;
    ///  2. le serveur **accorde** et le jeu est **rejoué** : le produit apparaît dans `Inventory.main` et les
    ///     matériaux y sont consommés ;
    ///  3. la même demande rejouée est **refusée** (matériaux partis) ;
    ///  4. le refus ne consomme **rien** et ne fabrique **rien**.
    ///
    /// La recette est choisie parmi celles que l'inventaire réel satisfait, et dont le **produit est absent**
    /// — sans quoi « le produit est apparu » ne serait pas mesurable. Elle n'est jamais déclarée au serveur :
    /// c'est lui qui lit la recette du jeu.
    /// </summary>
    internal sealed class CraftProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Craft");

        private const float WorldTimeoutSeconds = 120f;
        private const float AdoptionTimeoutSeconds = 90f;
        private const float ProductTimeoutSeconds = 30f;
        private const float RefusalSettleSeconds = 2.5f;
        private const int MaxIngredients = 3;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while ((ClientNetworkBehaviour.Current == null
                    || !ClientNetworkBehaviour.Current.Accepted
                    || !AutoLoadBehaviour.WorldLoaded
                    || Player.main == null)
                   && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            var client = ClientNetworkBehaviour.Current;
            if (client == null || !client.Accepted || !AutoLoadBehaviour.WorldLoaded || Player.main == null)
            {
                Fail($"pas de session acceptée / monde chargé après {WorldTimeoutSeconds:F0}s");
                yield break;
            }

            var adoptionDeadline = Time.realtimeSinceStartup + AdoptionTimeoutSeconds;
            while (client.InventoryAdoptionsSent == 0 && Time.realtimeSinceStartup < adoptionDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            if (client.InventoryAdoptionsSent == 0)
            {
                Fail($"aucune adoption envoyée en {AdoptionTimeoutSeconds:F0}s — le serveur ne l'a pas demandée");
                yield break;
            }

            yield return new WaitForSecondsRealtime(1f);

            // Le point d'entrée du bouton est une action de `Crafter` (`Fabricator` en dérive), pas le
            // `CrafterLogic` qu'elle anime : c'est l'action qui consomme énergie et matériaux.
            var crafters = UnityEngine.Object.FindObjectsOfType<Crafter>();
            Log.LogInfo($"[sonde] craft : {crafters.Length} Crafter trouvé(s) dans le monde.");

            if (crafters.Length == 0)
            {
                Fail("aucun Crafter dans le monde — le chemin réel du fabricator ne peut pas être exercé");
                yield break;
            }

            var crafter = crafters[0];
            var logic = crafter.logic;

            if (logic == null)
            {
                Fail("le Crafter trouvé n'a pas de CrafterLogic — l'action du bouton ne pourrait pas aboutir");
                yield break;
            }

            // Le `CrafterLogic` du caisson est **désactivé** quand le fabricator est fermé : sa coroutine ne
            // tourne alors pas, et une reprise approuvée resterait sans effet. La sonde l'active pour la
            // durée de la mesure (c'est un état de test, pas une règle de produit).
            var activeBefore = logic.isActiveAndEnabled;
            if (!activeBefore)
            {
                logic.enabled = true;
                if (!logic.gameObject.activeSelf)
                {
                    logic.gameObject.SetActive(true);
                }

                Log.LogInfo(
                    $"[sonde] craft : CrafterLogic réactivé pour la mesure (actif avant={activeBefore}, " +
                    $"après={logic.isActiveAndEnabled}).");
            }

            if (!TryChooseRecipe(out var tech, out var cost, out var why))
            {
                Fail($"aucune recette mesurable avec l'inventaire réel : {why}");
                yield break;
            }

            var product = tech.ToString();
            var costText = Describe(cost);

            var beforeCost = new int[cost.Length];
            for (var index = 0; index < cost.Length; index++)
            {
                beforeCost[index] = InventoryReconciler.CountLocal(cost[index].TechTypeName);
            }

            var beforeProduct = InventoryReconciler.CountLocal(product);
            var grantedBefore = client.CraftsGranted;
            var refusedBefore = client.CraftsRefused;

            // En **Survie**, le jeu consomme les ingrédients lui-même : c'est le seul régime où « matériaux
            // consommés » prouve autre chose qu'un retrait fait par la réconciliation. On compte donc les
            // retraits de la réconciliation avant/après, et la ligne le dit.
            var requiresIngredients = RequiresIngredients();
            var reconciledBefore = InventoryReconciler.RemovedItems;

            // 1. L'appel réel du jeu — exactement ce que fait le bouton du fabricator : il doit être bloqué
            //    **avant** toute consommation.
            var firstStart = ((ITreeActionReceiver)crafter).PerformAction(tech);
            var untouchedBeforeApproval = true;

            for (var index = 0; index < cost.Length; index++)
            {
                if (InventoryReconciler.CountLocal(cost[index].TechTypeName) != beforeCost[index])
                {
                    untouchedBeforeApproval = false;
                }
            }

            // 2. L'accord du serveur doit rejouer le craft et créer le produit.
            var productDeadline = Time.realtimeSinceStartup + ProductTimeoutSeconds;
            var productAppeared = false;
            var progressSeen = 0f;
            var inProgressSeen = false;
            var pickupAttempts = 0;

            while (Time.realtimeSinceStartup < productDeadline)
            {
                if (logic.inProgress)
                {
                    inProgressSeen = true;
                }

                if (logic.progress > progressSeen)
                {
                    progressSeen = logic.progress;
                }

                // Le fabricator ne crée l'objet qu'au **ramassage** du produit fini : c'est le bouton de
                // l'interface qui appelle `TryPickup`, et la mesure fait ce que ce bouton ferait. Sans lui,
                // la progression atteint 1 et reste en attente indéfiniment (mesuré au run du 2026-10-22).
                // L'appel est **retenté** : `TryPickupAsync` abandonne sans rien faire si la progression
                // n'est pas encore exactement à 1 (mesuré au run du 2026-10-22, seuil de 0.99 atteint avant
                // la fin réelle : un seul appel restait sans effet).
                if (logic.progress >= 0.99f)
                {
                    pickupAttempts++;
                    if (pickupAttempts == 1)
                    {
                        Log.LogInfo("[sonde] craft : progression terminée — ramassage du produit (TryPickup).");
                    }

                    logic.TryPickup();
                }

                if (client.CraftsGranted > grantedBefore
                    && InventoryReconciler.CountLocal(product) > beforeProduct)
                {
                    productAppeared = true;
                    break;
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            if (!productAppeared)
            {
                Log.LogWarning(
                    $"[sonde] craft : le produit n'est pas apparu — CrafterLogic en cours={inProgressSeen}, " +
                    $"progression maximale={progressSeen:F2}, actif={logic.isActiveAndEnabled}, " +
                    $"tentatives de ramassage={pickupAttempts}.");
            }

            var granted = client.CraftsGranted > grantedBefore;
            var consumed = true;

            for (var index = 0; index < cost.Length; index++)
            {
                var expected = beforeCost[index] - cost[index].Count;
                if (expected < 0)
                {
                    expected = 0;
                }

                if (InventoryReconciler.CountLocal(cost[index].TechTypeName) != expected)
                {
                    consumed = false;
                }
            }

            var productAfterFirst = InventoryReconciler.CountLocal(product);
            var reconciledAfterFirst = InventoryReconciler.RemovedItems;
            var reconciledForCost = reconciledAfterFirst - reconciledBefore;
            var blockedFirst = !firstStart;

            // Hors mode Créatif, le coût doit avoir disparu **sans** la réconciliation : c'est le jeu qui l'a
            // pris, à la reprise approuvée. En Créatif le jeu ne consomme rien, et c'est la réconciliation
            // qui applique le coût décidé par le serveur — l'exiger à zéro y serait faux.
            var localCostPaidByGame = !requiresIngredients || reconciledForCost == 0;

            // 3. La même demande, les matériaux partis : refus nommé, sans effet.
            var secondStart = ((ITreeActionReceiver)crafter).PerformAction(tech);
            yield return new WaitForSecondsRealtime(RefusalSettleSeconds);

            var refusedSecond = client.CraftsRefused > refusedBefore
                                && (client.LastCraftOutcome == CraftOutcome.MissingMaterial
                                    || client.LastCraftOutcome == CraftOutcome.NotEnoughMaterial);

            var untouchedAfterRefusal = InventoryReconciler.CountLocal(product) == productAfterFirst;

            for (var index = 0; index < cost.Length; index++)
            {
                if (InventoryReconciler.CountLocal(cost[index].TechTypeName) != Math.Max(beforeCost[index] - cost[index].Count, 0))
                {
                    untouchedAfterRefusal = false;
                }
            }

            var blockedSecond = !secondStart;
            var replayed = CraftGuard.ReplayedCount > 0;

            var verdict = blockedFirst
                          && untouchedBeforeApproval
                          && granted
                          && replayed
                          && productAppeared
                          && consumed
                          && localCostPaidByGame
                          && blockedSecond
                          && refusedSecond
                          && untouchedAfterRefusal
                          && client.InventoryReconciliationFailures == 0;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] craft : recette='{0}' coût=[{1}] produit='{2}' mode='{3}', " +
                    "bloqué={4}/{5}, matériaux intacts avant accord={6}, accordé={7}, rejoué={8}, " +
                    "produit apparu={9} ({10} -> {11}), matériaux consommés={12}, " +
                    "coût retiré par la réconciliation={13}, refus nommé={14} ({15}), " +
                    "rien consommé après refus={16}, échecs de réconciliation={17} — verdict={18}",
                    tech,
                    costText,
                    product,
                    requiresIngredients ? "Survie" : "Créatif",
                    blockedFirst,
                    blockedSecond,
                    untouchedBeforeApproval,
                    granted,
                    replayed,
                    productAppeared,
                    beforeProduct,
                    productAfterFirst,
                    consumed,
                    reconciledForCost,
                    refusedSecond,
                    client.LastCraftOutcome,
                    untouchedAfterRefusal,
                    client.InventoryReconciliationFailures,
                    verdict));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] craft : verdict=False — " +
                    $"bloqué avant consommation={blockedFirst}, matériaux intacts={untouchedBeforeApproval}, " +
                    $"accordé={granted}, rejoué={replayed}, produit apparu={productAppeared} " +
                    $"({beforeProduct} -> {productAfterFirst}), consommés={consumed}, " +
                    $"payé par le jeu={localCostPaidByGame} (retraits de la réconciliation={reconciledForCost}), " +
                    $"refus nommé={refusedSecond} ({client.LastCraftOutcome}), " +
                    $"rien après refus={untouchedAfterRefusal}, " +
                    $"échecs de réconciliation={client.InventoryReconciliationFailures}.");
            }

            // 5. **Cadence** (v21) : une rafale de demandes brutes doit être refusée **nommément** par le
            //    serveur. Ces demandes ne passent pas par le garde — c'est justement le serveur qui borne,
            //    et le refus doit être lisible (`RateLimited`), pas une coupure.
            var rateLimitedBefore = client.CraftsRateLimited;
            var requestsSent = 0;

            for (var index = 0; index < 12; index++)
            {
                if (client.SendCraftRequest((ushort)tech) != 0)
                {
                    requestsSent++;
                }
            }

            var burstDeadline = Time.realtimeSinceStartup + 3f;
            while (client.CraftsRateLimited == rateLimitedBefore && Time.realtimeSinceStartup < burstDeadline)
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }

            var rateLimited = client.CraftsRateLimited - rateLimitedBefore;
            var cadenceVerdict = requestsSent > 0 && rateLimited > 0;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] cadence : demandes={0} refus de cadence={1} (rafale {2}, 1 par {3} ms) — verdict={4}",
                    requestsSent,
                    rateLimited,
                    CraftBudget.DefaultBurst,
                    CraftBudget.DefaultRefillMs,
                    cadenceVerdict));

            if (!cadenceVerdict)
            {
                Log.LogError(
                    $"[sonde] cadence : verdict=False — {requestsSent} demande(s) partie(s), " +
                    $"{rateLimited} refus de cadence reçu(s) (attendu ≥ 1).");
            }
        }

        /// <summary>
        /// Retient la recette la moins chère que l'inventaire local satisfait **et** dont le produit est
        /// absent : sans cette dernière condition, l'apparition du produit ne prouverait rien.
        /// </summary>
        private static bool TryChooseRecipe(out TechType tech, out InventoryEntry[] cost, out string why)
        {
            tech = TechType.None;
            cost = Array.Empty<InventoryEntry>();
            why = string.Empty;

            var bestTotal = int.MaxValue;
            var craftable = 0;
            var satisfied = 0;

            foreach (TechType candidate in Enum.GetValues(typeof(TechType)))
            {
                if (candidate == TechType.None)
                {
                    continue;
                }

                System.Collections.ObjectModel.ReadOnlyCollection<Ingredient> recipe = null;
                try
                {
                    recipe = TechData.GetIngredients(candidate);
                }
                catch (Exception)
                {
                    continue;
                }

                if (recipe == null || recipe.Count <= 0 || recipe.Count > MaxIngredients)
                {
                    continue;
                }

                craftable++;

                if (InventoryReconciler.CountLocal(candidate.ToString()) > 0)
                {
                    continue;
                }

                var entries = new List<InventoryEntry>(recipe.Count);
                var total = 0;
                var complete = true;

                foreach (var ingredient in recipe)
                {
                    if (ingredient == null || ingredient.amount <= 0)
                    {
                        complete = false;
                        break;
                    }

                    var name = ingredient.techType.ToString();
                    if (string.IsNullOrEmpty(name) || name.Length > Protocol.MaxTechTypeNameLength)
                    {
                        complete = false;
                        break;
                    }

                    if (InventoryReconciler.CountLocal(name) < ingredient.amount)
                    {
                        complete = false;
                        break;
                    }

                    entries.Add(new InventoryEntry(name, ingredient.amount));
                    total += ingredient.amount;
                }

                if (!complete || entries.Count == 0)
                {
                    continue;
                }

                satisfied++;

                if (total >= bestTotal)
                {
                    continue;
                }

                entries.Sort((left, right) => string.CompareOrdinal(left.TechTypeName, right.TechTypeName));
                tech = candidate;
                cost = entries.ToArray();
                bestTotal = total;
            }

            if (tech != TechType.None)
            {
                return true;
            }

            why = craftable == 0
                ? "aucune recette lisible dans TechData"
                : $"{craftable} recette(s) lue(s), {satisfied} satisfaite(s) dont le produit est absent";

            return false;
        }

        /// <summary>
        /// Le mode exige-t-il des ingrédients ? (Survie / Hardcore : oui ; Créatif / Freedom : non.)
        /// Indisponible, on répond « non » : le verdict retombe alors sur le comportement Créatif, mesuré.
        /// </summary>
        private static bool RequiresIngredients()
        {
            try
            {
                return GameModeUtils.RequiresIngredients();
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[sonde] craft : mode de jeu illisible ({exception.GetType().Name}) — supposé sans ingrédients.");
                return false;
            }
        }

        private static string Describe(InventoryEntry[] entries)
        {
            var parts = new string[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                parts[index] = $"{entries[index].TechTypeName} ×{entries[index].Count}";
            }

            return string.Join(", ", parts);
        }

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] craft : verdict=False — {reason}.");
        }
    }
}
