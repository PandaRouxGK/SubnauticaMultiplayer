using System;
using System.Collections.Generic;

namespace Coop.Model
{
    /// <summary>
    /// Matériaux qu'un craft **approuvé** consomme lui-même : pendant la reprise locale, la réconciliation
    /// ne doit pas les retirer avant que le jeu ne les consomme, sinon le craft échouerait faute
    /// d'ingrédients alors que le registre serveur est déjà débité.
    ///
    /// Deux propriétés que le client mesurait sans pouvoir les tester (elles vivaient dans `CraftGuard`,
    /// avec `UnityEngine.Time`) :
    /// - **des références** : deux recettes peuvent partager un ingrédient ; lever la protection de l'une ne
    ///   doit pas découvrir l'autre, c'est la **dernière** levée qui retire l'entrée ;
    /// - **une fenêtre** : une demande sans réponse ne protège pas indéfiniment un matériau, sinon la
    ///   réconciliation serait bloquée pour lui jusqu'à la fin de la session.
    ///
    /// Le temps est un argument (aucune horloge implicite), donc le comportement est testable hors jeu.
    /// </summary>
    public sealed class MaterialProtection
    {
        /// <summary>Fenêtre par défaut : 10 s, la durée mesurée d'un aller-retour d'accord (≈ 30 ms).</summary>
        public const long DefaultWindowMs = 10_000;

        private sealed class Entry
        {
            public long ExpiresAt;
            public int Refs;
        }

        private readonly long _windowMs;
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        public MaterialProtection(long windowMs = DefaultWindowMs)
        {
            _windowMs = windowMs < 1 ? 1 : windowMs;
        }

        /// <summary>Durée de protection appliquée à chaque armement (ms).</summary>
        public long WindowMs => _windowMs;

        /// <summary>Nombre de matériaux protégés (entrées, pas références).</summary>
        public int Count => _entries.Count;

        /// <summary>Nombre total de références armées, tous matériaux confondus.</summary>
        public int TotalRefs
        {
            get
            {
                var total = 0;
                foreach (var entry in _entries.Values)
                {
                    total += entry.Refs;
                }

                return total;
            }
        }

        /// <summary>
        /// Arme une référence sur chaque nom, pour `nowMs + WindowMs`. Un armement plus tardif **repousse**
        /// l'échéance (jamais ne la raccourcit). Rend les noms réellement retenus : les vides sont ignorés et
        /// les doublons d'un même appel ne comptent qu'une fois.
        /// </summary>
        public IReadOnlyList<string> Arm(long nowMs, IEnumerable<string> names)
        {
            var kept = new List<string>();

            if (names == null)
            {
                return kept;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var expiresAt = nowMs + _windowMs;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                {
                    continue;
                }

                if (_entries.TryGetValue(name, out var entry))
                {
                    entry.Refs++;
                    entry.ExpiresAt = Math.Max(entry.ExpiresAt, expiresAt);
                }
                else
                {
                    _entries[name] = new Entry { ExpiresAt = expiresAt, Refs = 1 };
                }

                kept.Add(name);
            }

            return kept;
        }

        /// <summary>
        /// Lève une référence par nom et rend les noms dont la **dernière** référence est partie — ceux que
        /// la réconciliation peut de nouveau traiter. Un nom inconnu est ignoré.
        /// </summary>
        public IReadOnlyList<string> Release(IEnumerable<string> names)
        {
            var released = new List<string>();

            if (names == null)
            {
                return released;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                {
                    continue;
                }

                if (!_entries.TryGetValue(name, out var entry))
                {
                    continue;
                }

                if (entry.Refs > 1)
                {
                    entry.Refs--;
                    continue;
                }

                _entries.Remove(name);
                released.Add(name);
            }

            return released;
        }

        /// <summary>
        /// Vrai si le matériau est protégé à `nowMs`. Une entrée échue est oubliée **avec ses références** :
        /// une fenêtre expirée ne doit pas continuer à retarder la réconciliation.
        /// </summary>
        public bool IsProtected(long nowMs, string name)
        {
            if (string.IsNullOrEmpty(name) || !_entries.TryGetValue(name, out var entry))
            {
                return false;
            }

            if (nowMs > entry.ExpiresAt)
            {
                _entries.Remove(name);
                return false;
            }

            return true;
        }

        /// <summary>Oublie les entrées échues à `nowMs` ; rend le nombre de matériaux oubliés.</summary>
        public int Prune(long nowMs)
        {
            if (_entries.Count == 0)
            {
                return 0;
            }

            var expired = new List<string>();
            foreach (var pair in _entries)
            {
                if (nowMs > pair.Value.ExpiresAt)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (var name in expired)
            {
                _entries.Remove(name);
            }

            return expired.Count;
        }

        /// <summary>Fin de session : rien de la précédente ne survit.</summary>
        public void Clear()
        {
            _entries.Clear();
        }
    }
}
