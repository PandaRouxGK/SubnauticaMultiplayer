using System;
using System.Collections.Generic;

namespace Coop.Model
{
    /// <summary>Retrait à appliquer dans l'inventaire du jeu : un nom d'objet et une quantité.</summary>
    public readonly struct InventoryRemoval
    {
        public InventoryRemoval(string techTypeName, int count)
        {
            TechTypeName = techTypeName;
            Count = count;
        }

        public string TechTypeName { get; }

        public int Count { get; }
    }

    /// <summary>
    /// Réconciliation entre un instantané d'inventaire **serveur** et l'inventaire **du jeu** chez le
    /// client (v20), sans Unity et donc testable hors du jeu.
    ///
    /// **Pourquoi un delta de noms suivis, et pas une copie de l'instantané.** Le registre du serveur ne
    /// contient que ce que la session a **acquis** (ramassages accordés, v19) : il ne connaît pas les
    /// objets déjà présents dans une sauvegarde. Réécrire `Inventory.main` pour lui faire égaler
    /// l'instantané d'admission (révision 0, vide) **viderait l'inventaire du joueur dès la connexion**.
    /// La réconciliation ne regarde donc que les noms que les instantanés **ont déjà cités** — ceux que
    /// le registre suit — et ne retire que ce que le local possède **en plus** du serveur.
    ///
    /// Conséquences, voulues :
    /// - un ramassage accordé (serveur +1, jeu +1) ne retire rien ;
    /// - un débit accordé (serveur −1, jeu encore +1) retire l'unité en trop ;
    /// - un objet du jeu jamais cité par un instantané n'est **jamais** touché ;
    /// - l'inverse (serveur > local) n'invente rien : il est **compté** (`MissingLocally`) et laissé au
    ///   journal, parce que créer un objet dans le monde est une décision de jeu, pas une réconciliation.
    ///
    /// L'appel est **idempotent** sur un même état : recalculer après l'application ne retire plus rien.
    /// </summary>
    public sealed class InventoryReconciliation
    {
        private static readonly InventoryRemoval[] NoRemovals = Array.Empty<InventoryRemoval>();

        private InventoryReconciliation(InventoryRemoval[] removals, int missingLocally, int examined)
        {
            Removals = removals;
            MissingLocally = missingLocally;
            Examined = examined;
        }

        /// <summary>Rien à retirer — l'état est déjà cohérent (ou aucun nom n'est suivi).</summary>
        public static InventoryReconciliation Empty { get; } =
            new InventoryReconciliation(NoRemovals, 0, 0);

        /// <summary>Retraits à appliquer, triés par nom ordinal.</summary>
        public InventoryRemoval[] Removals { get; }

        /// <summary>Noms que le serveur tient en **plus** que le jeu (aucune unité créée, seulement compté).</summary>
        public int MissingLocally { get; }

        /// <summary>Noms examinés — ceux cités par l'un des deux instantanés.</summary>
        public int Examined { get; }

        public bool IsEmpty => Removals.Length == 0;

        /// <summary>
        /// Calcule les retraits entre l'instantané précédemment appliqué et le nouveau.
        ///
        /// `previous` peut être `null` (premier instantané) : c'est le cas de l'admission, et le calcul ne
        /// doit alors rien retirer, sinon le monde sauvegardé du joueur serait vidé.
        /// </summary>
        public static InventoryReconciliation Compute(
            InventoryEntry[] previous,
            InventoryEntry[] next,
            Func<string, int> localCount)
        {
            if (localCount == null)
            {
                throw new ArgumentNullException(nameof(localCount));
            }

            var tracked = new HashSet<string>(StringComparer.Ordinal);
            var server = new Dictionary<string, int>(StringComparer.Ordinal);

            AddNames(previous, tracked);
            AddNames(next, tracked);

            if (next != null)
            {
                foreach (var entry in next)
                {
                    if (entry.TechTypeName != null)
                    {
                        server[entry.TechTypeName] = entry.Count;
                    }
                }
            }

            if (tracked.Count == 0)
            {
                return Empty;
            }

            var names = new string[tracked.Count];
            tracked.CopyTo(names);
            Array.Sort(names, StringComparer.Ordinal);

            var removals = new List<InventoryRemoval>();
            var missing = 0;

            foreach (var name in names)
            {
                var announced = server.TryGetValue(name, out var count) ? count : 0;
                var local = localCount(name);
                if (local < 0)
                {
                    local = 0;
                }

                if (local > announced)
                {
                    removals.Add(new InventoryRemoval(name, local - announced));
                }
                else if (local < announced)
                {
                    missing++;
                }
            }

            return new InventoryReconciliation(removals.ToArray(), missing, names.Length);
        }

        private static void AddNames(InventoryEntry[] entries, HashSet<string> names)
        {
            if (entries == null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.TechTypeName))
                {
                    names.Add(entry.TechTypeName);
                }
            }
        }
    }
}
