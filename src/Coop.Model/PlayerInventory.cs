using System;
using System.Collections.Generic;

namespace Coop.Model
{
    /// <summary>Une ligne d'inventaire : un nom de `TechType` et une quantité positive.</summary>
    public readonly struct InventoryEntry
    {
        public InventoryEntry(string techTypeName, int count)
        {
            TechTypeName = techTypeName;
            Count = count;
        }

        public string TechTypeName { get; }

        public int Count { get; }
    }

    /// <summary>
    /// Issue d'une **demande de craft** (v21). Elle est distincte de <see cref="InventoryChangeOutcome"/> :
    /// un craft peut échouer parce que la recette n'existe pas, ce que le registre ne sait pas dire.
    /// </summary>
    public enum CraftOutcome : byte
    {
        /// <summary>La recette existe et le coût a été débité : la révision avance de un.</summary>
        Applied = 0,

        /// <summary>Aucune recette connue pour ce `TechType` (ou recette vide).</summary>
        UnknownRecipe = 1,

        /// <summary>Le registre ne contient pas un des matériaux (`MissingTechType` le nomme).</summary>
        MissingMaterial = 2,

        /// <summary>Les matériaux sont là, mais en quantité insuffisante (`MissingAmount` dit combien il manque).</summary>
        NotEnoughMaterial = 3,

        /// <summary>Recette illisible : nom trop long, quantité hors bornes.</summary>
        Invalid = 4,
    }

    /// <summary>Issue d'une mutation de l'inventaire serveur.</summary>
    public enum InventoryChangeOutcome
    {
        /// <summary>La mutation a été appliquée : la révision avance de un.</summary>
        Applied,

        /// <summary>Nom vide, trop long, ou quantité hors bornes.</summary>
        Invalid,

        /// <summary>L'objet n'est pas dans le registre (retrait impossible).</summary>
        UnknownItem,

        /// <summary>Le registre ne contient pas assez d'unités (retrait impossible).</summary>
        NotEnough,

        /// <summary>Plus de place : plafond d'objets distincts ou de pile atteint.</summary>
        InventoryFull,
    }

    /// <summary>
    /// Registre **serveur** de l'inventaire d'un joueur (v19), sans Unity et donc testable.
    ///
    /// Pourquoi un registre, et pas une copie de ce que le client annonce : un client qui déclare son
    /// inventaire peut déclarer n'importe quoi, et le coût de fabrication (le lot suivant) n'aurait alors
    /// aucune valeur opposable. Ici, la seule façon d'**ajouter** est un appel serveur — aujourd'hui le
    /// ramassage accordé — et aucun message du protocole ne demande un ajout. Le client reçoit des
    /// instantanés (`InventorySnapshot`, #27) ; il ne les dicte pas.
    ///
    /// **Ce que ce modèle ne fait pas** : il ne connaît ni les emplacements du jeu, ni les tailles de pile
    /// par objet (elles varient : une batterie n'est pas un poisson). Il tient des quantités par nom, avec
    /// une borne globale de pile (`Protocol.MaxInventoryStack`) — assez pour un coût de fabrication, pas
    /// pour remplacer l'inventaire du jeu. La réconciliation avec `Inventory.main` est le lot suivant.
    ///
    /// **Thread** : comme <see cref="PickupClaimLedger"/>, les appels viennent de la boucle réseau du
    /// serveur, qui les sérialise ; aucun verrou n'est pris, et partager l'objet entre threads demanderait
    /// une synchronisation explicite.
    /// </summary>
    public sealed class PlayerInventory
    {
        private readonly Dictionary<string, int> _items =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Numéro d'ordre des changements appliqués. Il ne bouge **pas** sur un refus : un client peut donc
        /// vérifier qu'un refus n'a rien changé, et une sonde peut affirmer qu'un instantané plus récent
        /// correspond bien à une mutation.
        /// </summary>
        public int Revision { get; private set; }

        /// <summary>Nombre d'objets distincts.</summary>
        public int Count => _items.Count;

        /// <summary>Somme des quantités — sert aux journaux et aux tests de fuite.</summary>
        public int TotalCount
        {
            get
            {
                var total = 0;
                foreach (var pair in _items)
                {
                    total += pair.Value;
                }

                return total;
            }
        }

        /// <summary>Quantité détenue d'un objet (0 s'il est absent).</summary>
        public int CountOf(string techTypeName) =>
            techTypeName != null && _items.TryGetValue(techTypeName, out var count) ? count : 0;

        /// <summary>
        /// Ajoute des unités. Refus **nommé** plutôt que troncature : une quantité perdue en silence serait
        /// invisible pour le joueur comme pour le serveur.
        /// </summary>
        public InventoryChangeOutcome Add(string techTypeName, int count)
        {
            if (!IsValidName(techTypeName) || count <= 0)
            {
                return InventoryChangeOutcome.Invalid;
            }

            if (!_items.TryGetValue(techTypeName, out var current))
            {
                if (_items.Count >= Protocol.MaxInventoryEntries)
                {
                    return InventoryChangeOutcome.InventoryFull;
                }

                current = 0;
            }

            if (current + count > Protocol.MaxInventoryStack)
            {
                return InventoryChangeOutcome.InventoryFull;
            }

            _items[techTypeName] = current + count;
            Revision++;
            return InventoryChangeOutcome.Applied;
        }

        /// <summary>
        /// Retire des unités. Une ligne qui tombe à zéro est **supprimée** : garder une ligne vide ferait
        /// apparaître un objet fantôme dans l'instantané et consommerait une place du plafond.
        /// </summary>
        public InventoryChangeOutcome Remove(string techTypeName, int count)
        {
            if (!IsValidName(techTypeName) || count <= 0)
            {
                return InventoryChangeOutcome.Invalid;
            }

            if (!_items.TryGetValue(techTypeName, out var current))
            {
                return InventoryChangeOutcome.UnknownItem;
            }

            if (current < count)
            {
                return InventoryChangeOutcome.NotEnough;
            }

            if (current == count)
            {
                _items.Remove(techTypeName);
            }
            else
            {
                _items[techTypeName] = current - count;
            }

            Revision++;
            return InventoryChangeOutcome.Applied;
        }

        /// <summary>
        /// Retire **plusieurs** matériaux d'un coup, ou aucun (v21, coût de craft).
        ///
        /// L'atomicité n'est pas un confort : un craft qui débiterait le titane puis découvrirait que le
        /// verre manque laisserait le joueur payé pour rien. Toutes les quantités sont donc vérifiées
        /// **avant** la première suppression, et un échec laisse le registre intact (révision comprise).
        ///
        /// Le premier manque est nommé dans `missingName`/`missingNeeded`/`missingHave` : un refus sans nom
        /// obligerait le client à deviner ce qui bloque.
        /// </summary>
        public InventoryChangeOutcome RemoveMany(
            InventoryEntry[] costs,
            out string missingName,
            out int missingNeeded,
            out int missingHave)
        {
            missingName = null;
            missingNeeded = 0;
            missingHave = 0;

            if (costs == null || costs.Length == 0 || costs.Length > Protocol.MaxInventoryEntries)
            {
                return InventoryChangeOutcome.Invalid;
            }

            // Une recette pourrait citer deux fois le même matériau (deux lignes du jeu) : les sommer
            // évite de vérifier chaque ligne séparément contre un registre qui ne verra que le total.
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var cost in costs)
            {
                if (!IsValidName(cost.TechTypeName) || cost.Count <= 0)
                {
                    return InventoryChangeOutcome.Invalid;
                }

                totals.TryGetValue(cost.TechTypeName, out var current);
                if (current + cost.Count > Protocol.MaxInventoryStack)
                {
                    return InventoryChangeOutcome.Invalid;
                }

                totals[cost.TechTypeName] = current + cost.Count;
            }

            foreach (var pair in totals)
            {
                var have = CountOf(pair.Key);
                if (have >= pair.Value)
                {
                    continue;
                }

                missingName = pair.Key;
                missingNeeded = pair.Value;
                missingHave = have;

                return have == 0 ? InventoryChangeOutcome.UnknownItem : InventoryChangeOutcome.NotEnough;
            }

            foreach (var pair in totals)
            {
                var remaining = CountOf(pair.Key) - pair.Value;
                if (remaining == 0)
                {
                    _items.Remove(pair.Key);
                }
                else
                {
                    _items[pair.Key] = remaining;
                }
            }

            Revision++;
            return InventoryChangeOutcome.Applied;
        }

        /// <summary>
        /// Remplace tout le contenu (v21, **adoption** du joueur à l'admission).
        ///
        /// C'est la seule écriture qui ne provient pas d'une décision du serveur, et elle est bornée comme
        /// un instantané : noms valides, quantités `1..MaxInventoryStack`, pas de doublon, tri ordinal. Le
        /// tri est exigé parce que le même contenu doit avoir **une** forme : deux adoptions du même
        /// inventaire ne doivent pas produire deux fichiers différents.
        /// </summary>
        public bool TryReplace(InventoryEntry[] entries, out string error)
        {
            error = null;

            if (entries == null)
            {
                error = "contenu nul";
                return false;
            }

            if (entries.Length > Protocol.MaxInventoryEntries)
            {
                error = $"trop d'objets ({entries.Length} > {Protocol.MaxInventoryEntries})";
                return false;
            }

            var rebuilt = new Dictionary<string, int>(StringComparer.Ordinal);
            string previous = null;

            foreach (var entry in entries)
            {
                if (!IsValidName(entry.TechTypeName))
                {
                    error = "nom d'objet vide ou trop long";
                    return false;
                }

                if (entry.Count <= 0 || entry.Count > Protocol.MaxInventoryStack)
                {
                    error = $"quantité hors bornes pour '{entry.TechTypeName}' ({entry.Count})";
                    return false;
                }

                if (previous != null && string.CompareOrdinal(previous, entry.TechTypeName) >= 0)
                {
                    error = $"objets non triés ou dupliqués ('{previous}' puis '{entry.TechTypeName}')";
                    return false;
                }

                previous = entry.TechTypeName;
                rebuilt[entry.TechTypeName] = entry.Count;
            }

            _items.Clear();
            foreach (var pair in rebuilt)
            {
                _items[pair.Key] = pair.Value;
            }

            Revision++;
            return true;
        }

        /// <summary>
        /// Contenu trié par nom (`StringComparer.Ordinal`), prêt pour l'instantané.
        ///
        /// Le tri n'est pas cosmétique : l'instantané voyage sur le réseau, et deux instances qui
        /// sérialisent le même contenu dans deux ordres produiraient deux paquets différents pour le même
        /// état — impossible à comparer dans un journal ou dans un test.
        /// </summary>
        public InventoryEntry[] Snapshot()
        {
            var entries = new InventoryEntry[_items.Count];
            var index = 0;

            foreach (var pair in _items)
            {
                entries[index++] = new InventoryEntry(pair.Key, pair.Value);
            }

            Array.Sort(entries, (left, right) =>
                string.CompareOrdinal(left.TechTypeName, right.TechTypeName));

            return entries;
        }

        private static bool IsValidName(string techTypeName) =>
            !string.IsNullOrEmpty(techTypeName) &&
            techTypeName.Length <= Protocol.MaxTechTypeNameLength;
    }
}
