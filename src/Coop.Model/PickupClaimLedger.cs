using System.Collections.Generic;

namespace Coop.Model
{
    /// <summary>Issue d'une réclamation sur une clé d'objet.</summary>
    public enum PickupClaimOutcome
    {
        /// <summary>La clé était libre : elle appartient maintenant au demandeur.</summary>
        GrantedFirst,

        /// <summary>Le demandeur détenait déjà la clé : la réclamation est accordée à nouveau.</summary>
        GrantedSameHolder,

        /// <summary>Un autre joueur détient la clé.</summary>
        RefusedOtherHolder,
    }

    /// <summary>
    /// Registre des réclamations de ramassage (M5) : clé d'objet du monde → joueur qui l'a obtenue.
    ///
    /// Il vivait dans <c>ServerNetwork</c> sous forme de `Dictionary&lt;long, ushort&gt;`, sans test
    /// possible : le défaut trouvé le 2026-10-20 (un objet réclamé par un client tué restait refusé
    /// **au nom du mort**, `REFUSÉE (déjà tenue par id 2)`) n'était couvert par aucune vérification.
    /// Cette classe ne fait que la décision ; le serveur garde la résolution du monde, la portée et
    /// les journaux.
    ///
    /// La clé `0` n'est pas un objet : c'est au serveur de la refuser avant d'arriver ici, parce que
    /// le motif (« aucune identité d'objet ») lui appartient.
    ///
    /// **Thread** : les appels viennent de la boucle réseau du serveur, qui les sérialise ; la classe ne
    /// prend aucun verrou. La partager entre threads demanderait une synchronisation, pas un nouveau
    /// dictionnaire — la contrainte est nommée ici pour être vue avant d'être violée.
    /// </summary>
    public sealed class PickupClaimLedger
    {
        private readonly Dictionary<long, ushort> _claims = new Dictionary<long, ushort>();

        /// <summary>Nombre de clés détenues — sert à vérifier qu'une libération ne fuit pas.</summary>
        public int Count => _claims.Count;

        /// <summary>Détenteur de la clé, s'il y en a un.</summary>
        public bool TryGetHolder(long key, out ushort holder) => _claims.TryGetValue(key, out holder);

        /// <summary>
        /// Réclame une clé. La première réclamation l'emporte et devient durable : c'est la décision
        /// du serveur, pas une intention du client.
        /// </summary>
        public PickupClaimOutcome Claim(long key, ushort playerId)
        {
            if (_claims.TryGetValue(key, out var existing))
            {
                return existing == playerId
                    ? PickupClaimOutcome.GrantedSameHolder
                    : PickupClaimOutcome.RefusedOtherHolder;
            }

            _claims[key] = playerId;
            return PickupClaimOutcome.GrantedFirst;
        }

        /// <summary>
        /// Libère toutes les clés d'un joueur parti et rend leur nombre.
        ///
        /// Sans cela, la clé reste bloquée pour toute la session alors que son détenteur n'existe
        /// plus : un simple Alt+F4 rendait un objet définitivement inaccessible (mesuré le
        /// 2026-10-20), et le registre grossissait d'une entrée par ramassage sans jamais se vider.
        /// </summary>
        public int Release(ushort playerId)
        {
            List<long> owned = null;

            foreach (var pair in _claims)
            {
                if (pair.Value != playerId)
                {
                    continue;
                }

                if (owned == null)
                {
                    owned = new List<long>();
                }

                owned.Add(pair.Key);
            }

            if (owned == null)
            {
                return 0;
            }

            foreach (var key in owned)
            {
                _claims.Remove(key);
            }

            return owned.Count;
        }

        /// <summary>Clés détenues par un joueur (journal, diagnostics, tests).</summary>
        public List<long> KeysOf(ushort playerId)
        {
            var keys = new List<long>();

            foreach (var pair in _claims)
            {
                if (pair.Value == playerId)
                {
                    keys.Add(pair.Key);
                }
            }

            return keys;
        }
    }
}
