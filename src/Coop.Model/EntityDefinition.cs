using System;
using System.Globalization;

namespace Coop.Model
{
    /// <summary>
    /// Définition d'une entité **telle que le serveur la décrit** (P1bis, protocole v13).
    ///
    /// Pourquoi ce type : la v10 annonçait une entité par son espèce et son identité, sans dire
    /// *selon quelle définition* elle existe. Un client ne pouvait donc pas distinguer « le serveur
    /// m'annonce un Seamoth » de « le serveur m'annonce autre chose que ce que je crois ». Le contrat
    /// minimal est : le serveur transporte sa définition, son empreinte, et le client **recalcule**
    /// l'empreinte avant d'instancier. Une divergence est refusée et nommée — c'est la même méthode
    /// que la preuve discriminante de l'équipement (§28) : un contrôle qui ne peut pas échouer ne
    /// prouve rien.
    ///
    /// Ce que ce n'est pas : un pipeline de contenu custom (AssetBundles). Aucun prefab inconnu du
    /// client ne peut être instancié ici — la définition ne transporte que ce que le client sait
    /// déjà résoudre, et c'est écrit dans `PLAN.md` §43.5 (P1bis = contrat, pas pipeline).
    /// </summary>
    public sealed class EntityDefinition
    {
        /// <summary>Version de la définition. Un champ ajouté plus tard change le hash de tout le monde.</summary>
        public const byte CurrentRevision = 1;

        public byte Revision = CurrentRevision;

        /// <summary>Nom d'espèce (`SeaMoth`), pas le nom du prefab.</summary>
        public string TechTypeName = string.Empty;

        public float ScaleX = 1f;
        public float ScaleY = 1f;
        public float ScaleZ = 1f;

        /// <summary>Réservé aux futurs attributs de définition (aucun bit utilisé aujourd'hui).</summary>
        public byte Flags;

        public static EntityDefinition ForVehicle(string techTypeName)
        {
            return new EntityDefinition
            {
                TechTypeName = techTypeName ?? string.Empty,
                ScaleX = 1f,
                ScaleY = 1f,
                ScaleZ = 1f,
            };
        }

        /// <summary>
        /// Forme canonique et **déterministe** : les échelles sont quantifiées au millimètre et
        /// formatées comme des entiers, donc deux runtimes (Mono du jeu, .NET des tests) produisent
        /// la même chaîne. Un `float.ToString` dépendant de la culture ou de l'arrondi ferait
        /// échouer un hash correct.
        /// </summary>
        public string Canonical()
        {
            return string.Join(
                "|",
                "content-v1",
                Revision.ToString(CultureInfo.InvariantCulture),
                TechTypeName ?? string.Empty,
                Milli(ScaleX).ToString(CultureInfo.InvariantCulture),
                Milli(ScaleY).ToString(CultureInfo.InvariantCulture),
                Milli(ScaleZ).ToString(CultureInfo.InvariantCulture),
                Flags.ToString(CultureInfo.InvariantCulture));
        }

        public long ComputeHash()
        {
            return StableHash.Of(Canonical());
        }

        /// <summary>
        /// Recalcule l'empreinte de la définition reçue et la compare à celle annoncée.
        /// Retourne <c>false</c> et le hash recalculé si elles diffèrent — l'appelant refuse alors.
        /// </summary>
        public static bool Verify(EntityDefinition definition, long declaredHash, out long computedHash)
        {
            computedHash = definition == null ? 0L : definition.ComputeHash();
            return definition != null && computedHash == declaredHash;
        }

        private static long Milli(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return 0;
            }

            // `int` débordait dès 2 147 km d'échelle (valeur * 1000), et deux échelles opposées
            // au-delà de cette borne partageaient alors le même canonique. `long` recule la limite
            // à ~9,2 × 10^15 m ; au-delà (absurde), le signe reste distingué par saturation.
            var scaled = value * 1000.0;
            if (scaled >= long.MaxValue)
            {
                return long.MaxValue;
            }

            if (scaled <= long.MinValue)
            {
                return long.MinValue;
            }

            return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }
    }
}
