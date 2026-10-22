using System.Collections.Generic;

namespace Coop.Model
{
    /// <summary>Un craft intercepté chez le client, en attente de la décision du serveur.</summary>
    public sealed class PendingCraft
    {
        public int RequestId;
        public int InstanceId;
        public ushort TechType;
    }

    /// <summary>
    /// Comptabilité des crafts interceptés côté client.
    ///
    /// Deux crafts de la **même recette** demandés avant la première réponse se recouvraient : la table
    /// était indexée par `TechType`, donc la seconde demande remplaçait la première, et le jeton
    /// d'approbation était lui aussi par `TechType` — n'importe quelle instance de cette recette pouvait
    /// alors traverser le préfixe. Ici, une demande est indexée par son **id de requête** (celui que le
    /// serveur renvoie) et l'approbation est un **jeton par instance** : la réponse d'un id ne rejoue que
    /// l'instance qui l'a demandé, une seule fois.
    ///
    /// Pur (aucun Unity) : testé hors jeu dans `tests/Coop.Model.Tests`.
    /// </summary>
    public sealed class CraftRequestLedger
    {
        /// <summary>Borne de sécurité : une demande sans réponse ne doit pas croître sans fin.</summary>
        public const int MaxPending = 64;

        private readonly Dictionary<int, PendingCraft> _pending = new Dictionary<int, PendingCraft>();
        private readonly HashSet<int> _approved = new HashSet<int>();

        public int PendingCount => _pending.Count;

        public int ApprovedCount => _approved.Count;

        /// <summary>Retient une demande bloquée. Faux si l'id est invalide, déjà connu, ou la table pleine.</summary>
        public bool Add(int requestId, int instanceId, ushort techType)
        {
            if (requestId <= 0 || techType == 0 || instanceId == 0)
            {
                return false;
            }

            if (_pending.Count >= MaxPending || _pending.ContainsKey(requestId))
            {
                return false;
            }

            _pending[requestId] = new PendingCraft
            {
                RequestId = requestId,
                InstanceId = instanceId,
                TechType = techType,
            };

            return true;
        }

        /// <summary>
        /// Accord reçu pour cet id : retire la demande et arme un jeton pour **son** instance. Faux si
        /// l'id est inconnu (réponse périmée) ou si la recette annoncée ne correspond pas à la demande.
        /// </summary>
        public bool TryApprove(int requestId, ushort techType, out PendingCraft pending)
        {
            pending = null;

            if (!_pending.TryGetValue(requestId, out var found))
            {
                return false;
            }

            _pending.Remove(requestId);

            if (found.TechType != techType)
            {
                return false;
            }

            _approved.Add(found.InstanceId);
            pending = found;
            return true;
        }

        /// <summary>Refus reçu : retire la demande, sans armer de jeton.</summary>
        public bool TryRefuse(int requestId, out PendingCraft pending)
        {
            pending = null;

            if (!_pending.TryGetValue(requestId, out var found))
            {
                return false;
            }

            _pending.Remove(requestId);
            pending = found;
            return true;
        }

        /// <summary>Consomme le jeton d'une instance (appelé par le préfixe au moment de la reprise).</summary>
        public bool ConsumeApproval(int instanceId)
        {
            return _approved.Remove(instanceId);
        }

        /// <summary>Nouvelle session : rien ne doit survivre à une déconnexion.</summary>
        public void Clear()
        {
            _pending.Clear();
            _approved.Clear();
        }
    }
}
