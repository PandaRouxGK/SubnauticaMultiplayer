using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Coop.Model;
using LiteNetLib;
using UnityEngine;

namespace Coop.Server
{
    public sealed partial class ServerNetwork
    {
        /// <summary>Instantanés d'inventaire envoyés depuis le démarrage (journal, diagnostics).</summary>
        private long _inventorySnapshotsSent;

        /// <summary>Instantanés d'inventaire envoyés — pour qu'une sonde puisse lire ce que le serveur a fait.</summary>
        public long InventorySnapshotsSent => _inventorySnapshotsSent;

        /// <summary>Débits accordés / refusés, et demandes faites sous un id qui n'est pas celui de la session.</summary>
        private long _inventoryDebitsGranted;
        private long _inventoryDebitsRefused;
        private long _inventoryDebitForgeries;

        public long InventoryDebitsGranted => _inventoryDebitsGranted;
        public long InventoryDebitsRefused => _inventoryDebitsRefused;
        public long InventoryDebitForgeries => _inventoryDebitForgeries;

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

        /// <summary>
        /// Applique une demande de **débit** (v20), puis répond au demandeur.
        ///
        /// L'identité vient de la **session**, jamais du message : un client qui revendique l'id d'un autre
        /// joueur ne débite que son propre registre, et la falsification est comptée. Sans cela, un débit
        /// — la première opération cliente qui modifie l'inventaire — aurait rouvert exactement le trou
        /// que le registre serveur de la v19 avait fermé.
        ///
        /// Le refus est **nommé** (`UnknownItem`, `NotEnough`, `Invalid`) et n'avance pas la révision : une
        /// sonde peut donc distinguer « refusé » de « appliqué », et un client sait qu'il n'a rien consommé.
        /// </summary>
        private void HandleInventoryDebit(NetPeer peer, ClientSession session, InventoryDebitMessage message)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] débit d'inventaire avant handshake ignoré de {peer}");
                return;
            }

            if (message.PlayerId != session.PlayerId)
            {
                _inventoryDebitForgeries++;
                _log.LogWarning(
                    $"[net] inventaire : débit revendiqué sous l'id {message.PlayerId} par " +
                    $"'{session.Username}'#{session.PlayerId} — débité sur **sa** session (falsifications={_inventoryDebitForgeries}).");
            }

            var before = session.Inventory.Revision;
            var outcome = session.Inventory.Remove(message.TechTypeName, message.Count);
            var granted = outcome == InventoryChangeOutcome.Applied;

            Send(peer, w => new InventoryDebitResultMessage
            {
                TechTypeName = message.TechTypeName,
                Count = message.Count,
                Granted = granted,
                Reason = outcome,
                Revision = session.Inventory.Revision,
                ClaimedBy = message.PlayerId,
            }.Write(w), DeliveryMethod.ReliableOrdered);

            if (!granted)
            {
                _inventoryDebitsRefused++;
                _log.LogWarning(
                    $"[net] inventaire : débit de '{message.TechTypeName}' ×{message.Count} REFUSÉ pour " +
                    $"'{session.Username}'#{session.PlayerId} ({outcome}) — révision {session.Inventory.Revision} inchangée.");
                return;
            }

            _inventoryDebitsGranted++;
            _log.LogInfo(
                $"[net] inventaire : débit de '{message.TechTypeName}' ×{message.Count} accordé pour " +
                $"'{session.Username}'#{session.PlayerId} (révision {before} -> {session.Inventory.Revision})");

            // L'instantané part après la décision, sur le même canal fiable et ordonné : le client applique
            // le résultat puis le nouvel état, jamais l'inverse.
            SendInventorySnapshot(peer, session, $"débit de '{message.TechTypeName}' ×{message.Count}");
        }

        /// <summary>Racine des fichiers du serveur : `BepInEx/coop` (même dossier que le canal du launcher).</summary>
        private static string InventoryRoot => Path.Combine(BepInEx.Paths.BepInExRootPath, "coop");

        private static string InventoryPathFor(ClientSession session)
        {
            var slot = session.Save != null ? session.Save.Slot : null;
            return InventoryStore.PathFor(InventoryRoot, slot, session.Username);
        }

        /// <summary>
        /// Écrit le registre du joueur sur le disque du serveur (v21), après chaque changement appliqué.
        ///
        /// Pourquoi écrire à chaque mutation plutôt qu'à la déconnexion : un arrêt brutal ne doit pas
        /// emporter les ramassages et les crafts de la session. Un échec est **nommé** et n'interrompt pas
        /// le jeu : le registre continue de vivre en mémoire.
        /// </summary>
        private void PersistInventory(ClientSession session, string reason)
        {
            var path = InventoryPathFor(session);

            if (InventoryStore.Save(path, session.Inventory.Snapshot(), out var error))
            {
                _log.LogInfo(
                    $"[net] inventaire : '{session.Username}'#{session.PlayerId} conservé sur disque " +
                    $"({session.Inventory.Count} objet(s), {session.Inventory.TotalCount} unité(s), révision {session.Inventory.Revision}) — {reason}.");
                return;
            }

            _log.LogWarning(
                $"[net] inventaire : écriture impossible pour '{session.Username}'#{session.PlayerId} " +
                $"({error}) — le registre vit en mémoire seulement ({reason}).");
        }

        /// <summary>
        /// Relit le registre conservé d'un joueur, s'il existe, **avant** de demander l'adoption.
        ///
        /// Ce fichier n'est pas la vérité du joueur — le serveur ne peut pas lire sa sauvegarde — mais il
        /// couvre le cas où le client ne répondrait pas à la demande d'adoption : le registre précédent
        /// vaut mieux qu'un inventaire vide.
        /// </summary>
        private void LoadStoredInventory(ClientSession session)
        {
            var path = InventoryPathFor(session);

            if (!InventoryStore.TryLoad(path, out var entries, out var error, out var missing))
            {
                if (!missing)
                {
                    _log.LogWarning(
                        $"[net] inventaire : fichier illisible pour '{session.Username}'#{session.PlayerId} " +
                        $"({error}) — registre vide, l'adoption du client fera foi.");
                }

                return;
            }

            if (!session.Inventory.TryReplace(entries, out var replaceError))
            {
                _log.LogWarning(
                    $"[net] inventaire : fichier incohérent pour '{session.Username}'#{session.PlayerId} " +
                    $"({replaceError}) — ignoré, l'adoption du client fera foi.");
                return;
            }

            _log.LogInfo(
                $"[net] inventaire : '{session.Username}'#{session.PlayerId} restauré depuis le disque " +
                $"({entries.Length} objet(s), {session.Inventory.TotalCount} unité(s)) — en attente de l'adoption.");
        }

        /// <summary>
        /// Admission (v21) : demande au client **son** inventaire actuel, une fois.
        ///
        /// L'ordre compte : on relit le fichier d'abord (filet de sécurité), puis on demande l'adoption, qui
        /// **remplace** ce contenu. L'adoption est la seule déclaration cliente du protocole, et elle est
        /// bornée, triée et unique par session.
        /// </summary>
        private void RequestInventoryAdoption(NetPeer peer, ClientSession session)
        {
            LoadStoredInventory(session);
            session.AdoptionReceived = false;

            Send(
                peer,
                writer => new InventoryAdoptionRequestMessage { Sequence = 1 }.Write(writer),
                DeliveryMethod.ReliableOrdered);

            _log.LogInfo(
                $"[net] inventaire : adoption demandée à '{session.Username}'#{session.PlayerId} " +
                $"(registre initial {session.Inventory.Count} objet(s), {session.Inventory.TotalCount} unité(s)).");
        }

        /// <summary>
        /// Reçoit l'inventaire initial déclaré par le client et remplace le registre (v21).
        ///
        /// Un contenu invalide est refusé **en entier** et le registre reste tel quel : une adoption
        /// partielle vaudrait moins qu'aucune adoption, parce qu'elle ferait refuser des crafts légitimes
        /// ou en accorderait à tort.
        /// </summary>
        private void HandleInventoryAdopt(NetPeer peer, ClientSession session, InventoryAdoptMessage message)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] adoption d'inventaire avant handshake ignorée de {peer}");
                return;
            }

            if (session.AdoptionReceived)
            {
                _log.LogWarning(
                    $"[net] inventaire : seconde adoption refusée pour '{session.Username}'#{session.PlayerId} " +
                    "— le registre ne se remplace qu'une fois par session.");
                return;
            }

            if (!session.Inventory.TryReplace(message.Entries, out var error))
            {
                _log.LogError(
                    $"[net] inventaire : adoption REFUSÉE pour '{session.Username}'#{session.PlayerId} " +
                    $"({error}) — registre inchangé ({session.Inventory.Count} objet(s)).");
                return;
            }

            session.AdoptionReceived = true;
            PersistInventory(session, "adoption");
            SendInventorySnapshot(peer, session, "adoption");
        }

        /// <summary>
        /// Vérifie et prélève le **coût de craft** depuis les recettes du jeu (v21), puis rend la décision.
        ///
        /// La recette est lue côté serveur (`CraftData.GetRecipe`) : un client ne fournit ni le coût ni la
        /// liste des matériaux. Le prélèvement est atomique (`PlayerInventory.RemoveMany`), et un manque est
        /// rendu **nommé** (`MissingTechType`/`MissingAmount`) — un refus sans nom obligerait le joueur à
        /// deviner ce qui bloque.
        ///
        /// Le produit n'est pas instancié ici : ce lot arbitre le **coût**. Le client fabrique après
        /// l'accord, et l'instantané qui suit retire du monde les matériaux réellement prélevés.
        /// </summary>
        private void HandleCraftRequest(NetPeer peer, ClientSession session, CraftRequestMessage message)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] demande de craft avant handshake ignorée de {peer}");
                return;
            }

            session.CraftRequests++;
            var tech = (TechType)message.TechType;

            // Cadence avant tout le reste : une boucle de demandes ne doit pas coûter une lecture de recette
            // et une ligne de journal par message. Le refus est nommé et daté, la révision ne bouge pas.
            if (!session.CraftBudget.TryTake(ServerClock.Milliseconds, out var retryAfterMs))
            {
                RefuseCraft(
                    peer,
                    session,
                    message,
                    tech,
                    CraftOutcome.RateLimited,
                    0,
                    0,
                    $"cadence : {retryAfterMs} ms avant le prochain jeton (rafale {session.CraftBudget.Burst}, " +
                    $"1 par {session.CraftBudget.RefillMs} ms)");
                return;
            }

            // La recette vient du **jeu**, pas du client : `TechData.GetIngredients` est la source que le
            // fabricator utilise lui-même (vérifié sur `Assembly-CSharp` : `ReadOnlyCollection<Ingredient>`,
            // chaque `Ingredient` portant `techType` et `amount`).
            System.Collections.ObjectModel.ReadOnlyCollection<Ingredient> recipe = null;
            try
            {
                recipe = TechData.GetIngredients(tech);
            }
            catch (Exception exception)
            {
                _log.LogWarning($"[net] craft : recette de '{tech}' illisible ({exception.GetType().Name}).");
            }

            if (recipe == null || recipe.Count == 0)
            {
                RefuseCraft(peer, session, message, tech, CraftOutcome.UnknownRecipe, 0, 0, "aucune recette");
                return;
            }

            var costs = new List<InventoryEntry>(recipe.Count);
            var costTechs = new List<TechType>(recipe.Count);

            foreach (var ingredient in recipe)
            {
                if (ingredient == null || ingredient.amount <= 0)
                {
                    continue;
                }

                var name = ingredient.techType.ToString();
                if (string.IsNullOrEmpty(name) || name.Length > Protocol.MaxTechTypeNameLength)
                {
                    RefuseCraft(
                        peer,
                        session,
                        message,
                        tech,
                        CraftOutcome.Invalid,
                        0,
                        0,
                        $"recette illisible pour '{tech}'");
                    return;
                }

                costs.Add(new InventoryEntry(name, ingredient.amount));
                costTechs.Add(ingredient.techType);
            }

            if (costs.Count == 0)
            {
                RefuseCraft(peer, session, message, tech, CraftOutcome.UnknownRecipe, 0, 0, "recette sans matériau");
                return;
            }

            var revisionBefore = session.Inventory.Revision;
            var outcome = session.Inventory.RemoveMany(
                costs.ToArray(),
                out var missingName,
                out var needed,
                out var have);

            if (outcome != InventoryChangeOutcome.Applied)
            {
                var missingTech = TechType.None;
                for (var index = 0; index < costs.Count; index++)
                {
                    if (string.Equals(costs[index].TechTypeName, missingName, StringComparison.Ordinal))
                    {
                        missingTech = costTechs[index];
                        break;
                    }
                }

                var craftOutcome = outcome == InventoryChangeOutcome.Invalid
                    ? CraftOutcome.Invalid
                    : (have == 0 ? CraftOutcome.MissingMaterial : CraftOutcome.NotEnoughMaterial);

                var amount = Math.Min(Math.Max(needed - have, 0), ushort.MaxValue);

                RefuseCraft(
                    peer,
                    session,
                    message,
                    tech,
                    craftOutcome,
                    (ushort)missingTech,
                    (ushort)amount,
                    $"{missingName} ×{needed} (registre {have})");
                return;
            }

            session.CraftsGranted++;

            Send(peer, writer => new CraftResultMessage
            {
                RequestId = message.RequestId,
                TechType = message.TechType,
                Granted = true,
                Outcome = CraftOutcome.Applied,
                Revision = session.Inventory.Revision,
            }.Write(writer), DeliveryMethod.ReliableOrdered);

            _log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[net] craft : '{0}'#{1} -> '{2}' ACCORDÉ (coût : {3}) — révision {4} -> {5}.",
                    session.Username,
                    session.PlayerId,
                    tech,
                    DescribeCost(costs),
                    revisionBefore,
                    session.Inventory.Revision));

            PersistInventory(session, $"craft de '{tech}'");
            SendInventorySnapshot(peer, session, $"craft de '{tech}'");
        }

        private void RefuseCraft(
            NetPeer peer,
            ClientSession session,
            CraftRequestMessage message,
            TechType tech,
            CraftOutcome outcome,
            ushort missingTechType,
            ushort missingAmount,
            string detail)
        {
            session.CraftsRefused++;

            Send(peer, writer => new CraftResultMessage
            {
                RequestId = message.RequestId,
                TechType = message.TechType,
                Granted = false,
                Outcome = outcome,
                MissingTechType = missingTechType,
                MissingAmount = missingAmount,
                Revision = session.Inventory.Revision,
            }.Write(writer), DeliveryMethod.ReliableOrdered);

            _log.LogWarning(
                $"[net] craft : '{session.Username}'#{session.PlayerId} -> '{tech}' REFUSÉ " +
                $"({outcome} : {detail}) — révision {session.Inventory.Revision} inchangée.");
        }

        private static string DescribeCost(List<InventoryEntry> costs)
        {
            var parts = new string[costs.Count];
            for (var index = 0; index < costs.Count; index++)
            {
                parts[index] = $"{costs[index].TechTypeName} ×{costs[index].Count}";
            }

            return string.Join(", ", parts);
        }
    }
}
