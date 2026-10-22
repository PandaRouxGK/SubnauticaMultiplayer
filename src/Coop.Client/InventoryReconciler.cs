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
        private static int _deferred;

        /// <summary>
        /// Dernier inventaire **serveur** appliqué : la référence contre laquelle rejouer un retrait reporté.
        /// </summary>
        private static InventoryEntry[] _lastServer;

        /// <summary>Noms dont un retrait a été reporté et qui n'ont pas encore été repris.</summary>
        private static readonly List<string> DeferredNames = new List<string>();

        /// <summary>Unités réellement retirées de `Inventory.main` depuis le démarrage.</summary>
        internal static int RemovedItems => _removed;

        /// <summary>Retraits impossibles (objet déjà absent, ou sortie du conteneur refusée).</summary>
        internal static int FailedRemovals => _failed;

        /// <summary>Retraits reportés parce qu'un craft approuvé va consommer ces matériaux lui-même.</summary>
        internal static int DeferredRemovals => _deferred;

        /// <summary>Retraits encore en attente de reprise (reportés, jamais rejoués).</summary>
        internal static int PendingDeferrals => DeferredNames.Count;

        /// <summary>Mémorise l'inventaire serveur appliqué : référence de la reprise après un report.</summary>
        internal static void RememberServerState(InventoryEntry[] entries)
        {
            _lastServer = entries;
        }

        /// <summary>
        /// Rejoue les retraits **reportés** contre le dernier inventaire serveur connu, et rend le nombre de
        /// noms examinés. Sans cela, un report n'était repris qu'à l'instantané suivant : si aucun n'arrivait
        /// (reprise refusée par le jeu, par exemple), la divergence restait indéfiniment.
        ///
        /// Seuls les noms reportés sont réévalués — pas tout l'instantané : c'est ce qui permet d'ignorer les
        /// noms qui ne tiennent pas dans un instantané tronqué. L'appel est idempotent, et un matériau encore
        /// protégé est reporté de nouveau (sans boucle : la reprise n'est déclenchée que par une levée).
        /// </summary>
        internal static int RetryDeferred(ManualLogSource log)
        {
            if (DeferredNames.Count == 0 || _lastServer == null)
            {
                return 0;
            }

            var names = DeferredNames.ToArray();
            DeferredNames.Clear();

            var removals = new List<InventoryRemoval>(names.Length);

            foreach (var name in names)
            {
                var announced = 0;
                if (_lastServer != null)
                {
                    foreach (var entry in _lastServer)
                    {
                        if (string.Equals(entry.TechTypeName, name, StringComparison.Ordinal))
                        {
                            announced += entry.Count;
                        }
                    }
                }

                var local = CountLocal(name);
                if (local > announced)
                {
                    removals.Add(new InventoryRemoval(name, local - announced));
                }
            }

            if (removals.Count == 0)
            {
                log?.LogInfo(
                    $"[inventaire] réconciliation : {names.Length} report(s) repris — plus rien à retirer.");
                return names.Length;
            }

            log?.LogInfo(
                $"[inventaire] réconciliation : reprise de {removals.Count} retrait(s) reporté(s) " +
                $"[{string.Join(", ", names)}] — un craft vient de rendre la main.");

            Apply(removals.ToArray(), log);
            return names.Length;
        }

        /// <summary>Fin de session : rien de ce qui était reporté ne survit.</summary>
        internal static void Reset()
        {
            _lastServer = null;
            DeferredNames.Clear();
        }

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

        /// <summary>
        /// Contenu **actuel** de `Inventory.main`, prêt pour l'adoption (v21) : noms triés, quantités
        /// cumulées, bornés comme un instantané.
        ///
        /// `distinct` dit combien de noms existent réellement ; si la borne en coupe, `truncated` le dit —
        /// une adoption silencieusement amputée ferait refuser des crafts légitimes sans explication.
        /// </summary>
        internal static InventoryEntry[] CollectLocal(out int distinct, out bool truncated)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var inventory = Inventory.main;

            if (inventory != null && inventory.container != null)
            {
                foreach (var pickupable in Enumerate(inventory))
                {
                    var type = pickupable.GetTechType();
                    if (type == TechType.None)
                    {
                        continue;
                    }

                    var name = type.ToString();
                    if (string.IsNullOrEmpty(name) || name.Length > Protocol.MaxTechTypeNameLength)
                    {
                        continue;
                    }

                    counts.TryGetValue(name, out var current);
                    counts[name] = Math.Min(current + 1, Protocol.MaxInventoryStack);
                }
            }

            distinct = counts.Count;
            truncated = distinct > Protocol.MaxInventoryEntries;

            var names = new string[counts.Count];
            counts.Keys.CopyTo(names, 0);
            Array.Sort(names, StringComparer.Ordinal);

            var length = Math.Min(names.Length, Protocol.MaxInventoryEntries);
            var entries = new InventoryEntry[length];

            for (var index = 0; index < length; index++)
            {
                entries[index] = new InventoryEntry(names[index], counts[names[index]]);
            }

            return entries;
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
                // Un craft vient d'être approuvé : le jeu consomme ses propres matériaux dans la foulée.
                // Les retirer ici les ferait disparaître **avant** que le fabricator ne les lise, et le
                // craft approuvé échouerait alors que le registre est déjà débité.
                if (CraftGuard.IsCostInFlight(removal.TechTypeName))
                {
                    _deferred++;

                    if (!DeferredNames.Contains(removal.TechTypeName))
                    {
                        DeferredNames.Add(removal.TechTypeName);
                    }

                    log?.LogInfo(
                        $"[inventaire] réconciliation : retrait de '{removal.TechTypeName}' ×{removal.Count} " +
                        "reporté — un craft approuvé consomme ce matériau lui-même.");
                    continue;
                }

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
