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
    /// Sonde du **craft payé** (v21, `-coopdebug=craft`).
    ///
    /// Ce qui est mesuré : le serveur lit la **recette du jeu**, prélève les matériaux sur **son** registre,
    /// et le monde du client s'aligne (la réconciliation retire les unités prélevées). Puis la même demande
    /// est rejouée : elle doit être **refusée en nommant le matériau manquant**, sans avancer la révision.
    ///
    /// Pourquoi deux demandes : un serveur qui accorderait tout passerait la première, un serveur qui
    /// refuserait tout passerait la seconde. La preuve est le couple.
    ///
    /// **La recette est choisie, pas inventée** : la sonde parcourt les `TechType` du jeu, lit leur recette
    /// par `CraftData.GetRecipe`, et retient la moins chère que `Inventory.main` satisfait réellement. Elle
    /// ne déclare donc jamais de matériaux qu'elle n'a pas — l'adoption envoyée à l'admission est le vrai
    /// contenu du joueur.
    ///
    /// **Ce que la sonde ne fait pas** : elle ne fabrique pas le produit. L'accord du serveur porte sur le
    /// **coût** (ce lot) ; le branchement du menu de craft et l'instanciation du produit restent à faire, et
    /// c'est écrit dans `PLAN.md` §69.4.
    /// </summary>
    internal sealed class CraftProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Craft");

        private const float WorldTimeoutSeconds = 120f;
        private const float AdoptionTimeoutSeconds = 90f;
        private const float ResultTimeoutSeconds = 30f;
        private const float CostSyncTimeoutSeconds = 30f;
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

            // Sans adoption, le registre serveur ne connaît pas les matériaux sauvegardés : le craft
            // refuserait pour une raison qui n'a rien à voir avec le coût.
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

            // Laisse l'instantané d'adoption arriver : la révision est la base de la comparaison.
            yield return new WaitForSecondsRealtime(1f);

            if (!TryChooseRecipe(out var tech, out var cost, out var why))
            {
                Fail($"aucune recette réalisable avec l'inventaire réel : {why}");
                yield break;
            }

            var costText = Describe(cost);
            var revisionBefore = client.InventoryRevision;

            var beforeServer = new int[cost.Length];
            var beforeLocal = new int[cost.Length];

            for (var index = 0; index < cost.Length; index++)
            {
                beforeServer[index] = client.InventoryCountOf(cost[index].TechTypeName);
                beforeLocal[index] = InventoryReconciler.CountLocal(cost[index].TechTypeName);
            }

            var removedBefore = client.InventoryRemovedLocally;
            var requestId = client.SendCraftRequest((ushort)tech);

            if (requestId == 0)
            {
                Fail("la demande de craft n'est pas partie (session non acceptée)");
                yield break;
            }

            var resultDeadline = Time.realtimeSinceStartup + ResultTimeoutSeconds;
            while (client.CraftResults == 0 && Time.realtimeSinceStartup < resultDeadline)
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }

            var granted = client.CraftsGranted > 0 && client.LastCraftOutcome == CraftOutcome.Applied;
            var revisionAfter = client.InventoryRevision;
            var revisionAdvanced = revisionAfter > revisionBefore;

            // Le serveur a débité : la réconciliation doit retirer les unités du monde chez le client.
            var syncDeadline = Time.realtimeSinceStartup + CostSyncTimeoutSeconds;
            var costSynced = false;

            while (Time.realtimeSinceStartup < syncDeadline)
            {
                costSynced = true;

                for (var index = 0; index < cost.Length; index++)
                {
                    var expected = beforeServer[index] - cost[index].Count;
                    if (expected < 0)
                    {
                        expected = 0;
                    }

                    if (client.InventoryCountOf(cost[index].TechTypeName) != expected
                        || InventoryReconciler.CountLocal(cost[index].TechTypeName) != expected)
                    {
                        costSynced = false;
                        break;
                    }
                }

                if (costSynced)
                {
                    break;
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            var removedLocally = client.InventoryRemovedLocally - removedBefore;
            var removedTotal = 0;
            foreach (var entry in cost)
            {
                removedTotal += entry.Count;
            }

            // Seconde demande : les matériaux sont partis, le refus doit être nommé et sans effet.
            var refusalRevision = client.InventoryRevision;
            var secondId = client.SendCraftRequest((ushort)tech);
            var resultsBefore = client.CraftResults;

            if (secondId == 0)
            {
                Fail("la seconde demande de craft (celle qui doit être refusée) n'est pas partie");
                yield break;
            }

            var refusalDeadline = Time.realtimeSinceStartup + ResultTimeoutSeconds;
            while (client.CraftResults == resultsBefore && Time.realtimeSinceStartup < refusalDeadline)
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }

            var refusedNamed = client.CraftsRefused > 0
                               && (client.LastCraftOutcome == CraftOutcome.MissingMaterial
                                   || client.LastCraftOutcome == CraftOutcome.NotEnoughMaterial)
                               && client.LastCraftMissingTech != 0;
            var refusalKeptRevision = client.InventoryRevision == refusalRevision;

            var verdict = granted
                          && revisionAdvanced
                          && costSynced
                          && removedLocally == removedTotal
                          && client.InventoryReconciliationFailures == 0
                          && refusedNamed
                          && refusalKeptRevision;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] craft : recette='{0}' coût=[{1}] accordé={2} révision {3} -> {4}, " +
                    "matériaux retirés localement={5}/{6}, refus nommé={7} ({8}) — verdict={9}",
                    tech,
                    costText,
                    granted,
                    revisionBefore,
                    revisionAfter,
                    removedLocally,
                    removedTotal,
                    refusedNamed,
                    client.LastCraftOutcome,
                    verdict));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] craft : verdict=False — " +
                    $"accordé={granted}, révision avancée={revisionAdvanced} ({revisionBefore} -> {revisionAfter}), " +
                    $"coût synchronisé={costSynced}, retirés {removedLocally}/{removedTotal}, " +
                    $"échecs de réconciliation={client.InventoryReconciliationFailures}, " +
                    $"refus nommé={refusedNamed} ({client.LastCraftOutcome}), " +
                    $"révision du refus inchangée={refusalKeptRevision}.");
            }
        }

        /// <summary>
        /// Retient la recette la **moins chère** que l'inventaire local satisfait réellement.
        ///
        /// Le parcours des `TechType` évite de coder en dur une recette qui changerait avec la version du
        /// jeu : la sonde mesure le serveur, pas une table recopiée.
        /// </summary>
        private static bool TryChooseRecipe(out TechType tech, out InventoryEntry[] cost, out string why)
        {
            tech = TechType.None;
            cost = Array.Empty<InventoryEntry>();
            why = string.Empty;

            var bestTotal = int.MaxValue;
            var craftable = 0;

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

                var entries = new List<InventoryEntry>(recipe.Count);
                var total = 0;
                var satisfied = true;

                foreach (var ingredient in recipe)
                {
                    if (ingredient == null || ingredient.amount <= 0)
                    {
                        satisfied = false;
                        break;
                    }

                    var name = ingredient.techType.ToString();
                    if (string.IsNullOrEmpty(name) || name.Length > Protocol.MaxTechTypeNameLength)
                    {
                        satisfied = false;
                        break;
                    }

                    if (InventoryReconciler.CountLocal(name) < ingredient.amount)
                    {
                        satisfied = false;
                        break;
                    }

                    entries.Add(new InventoryEntry(name, ingredient.amount));
                    total += ingredient.amount;
                }

                if (!satisfied || entries.Count == 0 || total >= bestTotal)
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
                ? "aucune recette lisible dans CraftData"
                : $"{craftable} recette(s) lue(s), aucune entièrement satisfaite par Inventory.main";

            return false;
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
