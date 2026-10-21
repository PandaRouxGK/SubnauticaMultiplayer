using Coop.Model;
using LiteNetLib;

namespace Coop.Server
{
    public sealed partial class ServerNetwork
    {
        /// <summary>Instantanés d'inventaire envoyés depuis le démarrage (journal, diagnostics).</summary>
        private long _inventorySnapshotsSent;

        /// <summary>Instantanés d'inventaire envoyés — pour qu'une sonde puisse lire ce que le serveur a fait.</summary>
        public long InventorySnapshotsSent => _inventorySnapshotsSent;

        /// <summary>
        /// Annonce à un joueur **son** inventaire, tel que le serveur le tient (v19).
        ///
        /// Pourquoi à l'admission, même vide : un client doit savoir que le registre existe et à quelle
        /// révision il en est avant le premier ramassage. Un instantané envoyé seulement après un ramassage
        /// laissait le client deviner s'il avait manqué un message pendant son chargement.
        ///
        /// Un seul destinataire : l'inventaire d'un joueur n'appartient qu'à lui. Le diffuser à tous
        /// reviendrait à publier ce que chacun possède, et laissait un client lire l'inventaire d'un autre.
        /// </summary>
        private void SendInventorySnapshot(NetPeer peer, ClientSession session, string reason)
        {
            var message = new InventorySnapshotMessage
            {
                Revision = session.Inventory.Revision,
                Entries = session.Inventory.Snapshot(),
            };

            Send(peer, message.Write, DeliveryMethod.ReliableOrdered);
            _inventorySnapshotsSent++;

            _log.LogInfo(
                $"[net] inventaire de '{session.Username}'#{session.PlayerId} : révision {message.Revision}, " +
                $"{message.Entries.Length} objet(s) ({session.Inventory.TotalCount} unité(s)) — {reason}");
        }

        /// <summary>
        /// Enregistre un objet **réellement accordé** dans le registre du joueur, puis le lui annonce.
        ///
        /// Appelée seulement sur un ramassage **nouveau** et accordé : une réclamation répétée du même
        /// joueur est idempotente, et l'ajouter deux fois compterait deux objets pour un seul ramassage.
        /// C'est ici, et pas dans `HandlePickupClaim` directement, que le registre grossit : la règle
        /// « seul un ramassage accordé ajoute » est lisible en un seul endroit.
        /// </summary>
        private void RecordGrantedPickup(NetPeer peer, ClientSession session, string techTypeName)
        {
            if (string.IsNullOrEmpty(techTypeName))
            {
                // La clé était valide mais l'objet n'a pas de nom : il n'y a rien à enregistrer, et
                // l'inventer sous un nom générique créerait une ligne que le jeu ne saurait pas résoudre.
                _log.LogWarning(
                    $"[net] inventaire : ramassage accordé à '{session.Username}'#{session.PlayerId} " +
                    "sans nom d'objet — non enregistré (l'instantané resterait faux).");
                return;
            }

            var outcome = session.Inventory.Add(techTypeName, 1);

            if (outcome != InventoryChangeOutcome.Applied)
            {
                _log.LogWarning(
                    $"[net] inventaire : ajout de '{techTypeName}' refusé pour " +
                    $"'{session.Username}'#{session.PlayerId} ({outcome}) — l'objet ramassé n'est pas compté.");
                return;
            }

            SendInventorySnapshot(peer, session, $"ramassage accordé de '{techTypeName}'");
        }
    }
}
