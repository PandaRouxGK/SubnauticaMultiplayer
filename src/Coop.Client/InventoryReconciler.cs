using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;

namespace Coop.Client
{
    /// <summary>
    /// Applique à `Inventory.main` ce que le registre **serveur** ne contient plus (v20).
    ///
    /// La décision de ce qu'il faut retirer est prise dans `Coop.Model/InventoryReconciliation.cs`, sans
    /// Unity et donc testable hors du jeu. Ici, il ne reste que le geste : retrouver l'objet du jeu par son
    /// nom de `TechType`, le sortir du conteneur, puis le **détruire** — un objet consommé ne doit pas
    /// retourner dans le monde (contrairement au retour arrière d'un ramassage refusé, qui le relâche).
    ///
    /// **Pourquoi détruire, et pas relâcher.** Le débit naît d'une consommation (nourriture, pile, coût de
    /// fabrication) : le serveur a déjà décidé que l'unité n'existe plus. La relâcher ferait réapparaître
    /// un objet que le registre ne compte plus — deux sources de vérité contraires dans le même monde.
    ///
    /// **Ce que ce geste ne couvre pas** : les objets équipés (hors de `Inventory.main.container`), et les
    /// objets dont le nom transporté ne correspond pas au `ToString()` de leur `TechType` — même limite de
    /// rapprochement que la sonde, mesurée sur `PowerCell` seulement.
    /// </summary>
    internal static class InventoryReconciler
    {
        private static int _removed;
        private static int _failed;

        /// <summary>Unités réellement retirées de `Inventory.main` depuis le démarrage.</summary>
        internal static int RemovedItems => _removed;

        /// <summary>Retraits impossibles (objet déjà absent, ou sortie du conteneur refusée).</summary>
        internal static int FailedRemovals => _failed;

        /// <summary>Nombre d'unités de ce `TechType` réellement présentes dans l'inventaire du jeu.</summary>
        internal static int CountLocal(string techTypeName)
        {
            var inventory = Inventory.main;
            if (string.IsNullOrEmpty(techTypeName) || inventory == null || inventory.container == null)
            {
                return 0;
            }

            var count = 0;

            foreach (var item in Enumerate(inventory))
            {
                if (item != null && string.Equals(item.GetTechType().ToString(), techTypeName, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Retire les unités demandées. Chaque échec est nommé, jamais silencieux.</summary>
        internal static void Apply(InventoryRemoval[] removals, ManualLogSource log)
        {
            if (removals == null || removals.Length == 0)
            {
                return;
            }

            var inventory = Inventory.main;
            if (inventory == null || inventory.container == null)
            {
                _failed += removals.Length;
                log?.LogError(
                    "[inventaire] réconciliation impossible : `Inventory.main` n'existe pas — " +
                    "le registre et le jeu divergent jusqu'au prochain instantané.");
                return;
            }

            var removedNow = 0;

            foreach (var removal in removals)
            {
                var remaining = removal.Count;

                while (remaining > 0)
                {
                    var pickupable = FindLocal(inventory, removal.TechTypeName);
                    if (pickupable == null)
                    {
                        _failed++;
                        log?.LogWarning(
                            $"[inventaire] réconciliation : '{removal.TechTypeName}' ×{remaining} " +
                            "introuvable dans `Inventory.main` — le registre le retire, le jeu ne l'a déjà plus.");
                        break;
                    }

                    if (!inventory.TryRemoveItem(pickupable))
                    {
                        _failed++;
                        log?.LogError(
                            $"[inventaire] réconciliation : '{removal.TechTypeName}' n'a pas pu quitter " +
                            "`Inventory.main` — le joueur garde un objet que le serveur ne compte plus.");
                        break;
                    }

                    UnityEngine.Object.Destroy(pickupable.gameObject);
                    _removed++;
                    removedNow++;
                    remaining--;
                }
            }

            log?.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[inventaire] réconciliation : {0} unité(s) retirée(s) de Inventory.main, échecs={1} (total retiré={2}).",
                    removedNow,
                    FailedRemovals,
                    _removed));
        }

        private static Pickupable FindLocal(Inventory inventory, string techTypeName)
        {
            foreach (var item in Enumerate(inventory))
            {
                if (item != null && string.Equals(item.GetTechType().ToString(), techTypeName, StringComparison.Ordinal))
                {
                    return item;
                }
            }

            return null;
        }

        private static IEnumerable<Pickupable> Enumerate(Inventory inventory)
        {
            var container = inventory.container as IEnumerable<InventoryItem>;
            if (container == null)
            {
                yield break;
            }

            foreach (var slot in container)
            {
                if (slot != null && slot.item != null)
                {
                    yield return slot.item;
                }
            }
        }
    }
}
