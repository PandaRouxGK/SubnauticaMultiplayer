using System;
using System.Collections.Generic;
using System.Globalization;
using Coop.Model;
using Coop.Shared;
using LiteNetLib;
using UnityEngine;

namespace Coop.Server
{
    /// <summary>
    /// Demandes de fabrication **émises par un client** (v16, M4/M6).
    ///
    /// Le principe est le même que pour le ramassage (v8) et la conduite (v9) : le client ne fabrique
    /// rien, il demande ; le serveur décide, fabrique, et l'annonce existante
    /// (<see cref="EntitySpawnMessage"/>) crée la copie chez tout le monde — demandeur compris. Ce dernier
    /// point est ce qui évite le doublon : le demandeur n'instancie pas sa propre demande, il attend
    /// l'annonce comme les autres.
    ///
    /// Quatre règles, chacune pour une raison mesurable :
    ///  1. **liste blanche d'espèces** — une valeur reçue n'est pas une permission ; seuls les véhicules
    ///     que le projet sait déjà synchroniser sont acceptés ;
    ///  2. **portée** — la position demandée doit être à moins de
    ///     <see cref="Protocol.MaxEntitySpawnDistanceMeters"/> de la dernière position **acceptée** du
    ///     joueur (celle de la garde P1, pas celle qu'il prétend avoir), sinon le client fabriquerait
    ///     des entités à l'autre bout de la carte ;
    ///  3. **cadence et plafond** — un client ne doit pas peupler le monde du serveur ; le refus est
    ///     nommé et arrive au demandeur ;
    ///  4. **clé nulle = échec** — une entité que personne ne peut désigner n'est jamais annoncée.
    /// </summary>
    public sealed partial class ServerNetwork
    {
        /// <summary>
        /// Espèces qu'un client peut demander. Volontairement court : ce sont les véhicules dont le suivi
        /// (position, énergie, santé, modules) est déjà prouvé. Élargir cette liste est une décision, pas
        /// un effet de bord.
        /// </summary>
        private static readonly HashSet<TechType> SpawnableTechTypes = new HashSet<TechType>
        {
            TechType.Seamoth,
            TechType.Exosuit,
        };

        private void HandleEntitySpawnRequest(NetPeer peer, ClientSession session, EntitySpawnRequestMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] demande d'apparition avant handshake ignorée de {peer}");
                return;
            }

            session.EntitySpawnRequests++;
            var tech = (TechType)msg.TechType;
            var now = Time.realtimeSinceStartup;

            if (!SpawnableTechTypes.Contains(tech))
            {
                RefuseSpawn(peer, session, msg, $"espèce non autorisée : {(int)msg.TechType}");
                return;
            }

            if (!session.HasAcceptedPosition)
            {
                RefuseSpawn(peer, session, msg, "position du joueur inconnue (aucune position acceptée)");
                return;
            }

            if (now < session.NextEntitySpawnAt)
            {
                var wait = session.NextEntitySpawnAt - now;
                RefuseSpawn(
                    peer,
                    session,
                    msg,
                    string.Format(CultureInfo.InvariantCulture, "cadence : encore {0:F1} s avant la prochaine", wait));
                return;
            }

            if (session.EntitySpawnsGranted >= Protocol.MaxEntitySpawnsPerSession)
            {
                RefuseSpawn(
                    peer,
                    session,
                    msg,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "plafond atteint : {0} fabrication(s) pour cette session",
                        session.EntitySpawnsGranted));
                return;
            }

            var requested = new Vector3(msg.X, msg.Y, msg.Z);
            var player = new Vector3(session.LastAcceptedX, session.LastAcceptedY, session.LastAcceptedZ);
            var distance = Vector3.Distance(player, requested);

            if (distance > Protocol.MaxEntitySpawnDistanceMeters)
            {
                RefuseSpawn(
                    peer,
                    session,
                    msg,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "hors portée : {0:F1} m > {1:F1} m",
                        distance,
                        Protocol.MaxEntitySpawnDistanceMeters));
                return;
            }

            var factory = ServerEntityFactory.Current;
            if (factory == null)
            {
                // Cas réel d'un serveur démarré sans le composant (build ancien) : refus nommé plutôt
                // qu'une demande qui reste sans réponse.
                RefuseSpawn(peer, session, msg, "fabrique d'entités indisponible sur le serveur");
                return;
            }

            var rotation = new Quaternion(msg.RotX, msg.RotY, msg.RotZ, msg.RotW);
            _log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[net] apparition demandée : '{0}'#{1} -> '{2}' à ({3:F1}, {4:F1}, {5:F1}) " +
                    "(joueur à {6:F1} m), requête={7} — fabrication en cours.",
                    session.Username,
                    session.PlayerId,
                    tech,
                    requested.x,
                    requested.y,
                    requested.z,
                    distance,
                    msg.RequestId));

            factory.Request(tech, requested, rotation, (key, error) =>
                CompleteSpawn(peer, session, msg, tech, distance, key, error));
        }

        /// <summary>
        /// Rend la décision au demandeur. Appelée **plus tard** par la fabrique : entre la demande et la
        /// réponse, la session a pu se déconnecter — le message part alors dans le vide, ce qui est le
        /// comportement correct (personne à qui répondre).
        /// </summary>
        private void CompleteSpawn(
            NetPeer peer,
            ClientSession session,
            EntitySpawnRequestMessage msg,
            TechType tech,
            float distance,
            long key,
            string error)
        {
            if (error != null || key == 0L)
            {
                RefuseSpawn(peer, session, msg, error ?? "fabrication sans clé");
                return;
            }

            session.EntitySpawnsGranted++;
            session.NextEntitySpawnAt = Time.realtimeSinceStartup + Protocol.EntitySpawnCooldownSeconds;

            Send(
                peer,
                writer => new EntitySpawnResultMessage
                {
                    RequestId = msg.RequestId,
                    Granted = true,
                    Key = key,
                    Reason = string.Empty,
                }.Write(writer),
                DeliveryMethod.ReliableOrdered);

            _log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[net] apparition demandée : '{0}'#{1} -> '{2}' ACCORDÉE (clé=0x{3:x16}, " +
                    "joueur à {4:F1} m, requête={5}) — l'annonce part au prochain balayage, à tous les clients.",
                    session.Username,
                    session.PlayerId,
                    tech,
                    key,
                    distance,
                    msg.RequestId));
        }

        private void RefuseSpawn(NetPeer peer, ClientSession session, EntitySpawnRequestMessage msg, string reason)
        {
            // Le motif est borné par le protocole : un refus ne doit pas fabriquer un paquet que nos
            // propres clients rejetteraient.
            if (reason != null && reason.Length > Protocol.MaxSpawnReasonLength)
            {
                reason = reason.Substring(0, Protocol.MaxSpawnReasonLength);
            }

            session.EntitySpawnsRefused++;

            Send(
                peer,
                writer => new EntitySpawnResultMessage
                {
                    RequestId = msg.RequestId,
                    Granted = false,
                    Key = 0L,
                    Reason = reason ?? "refus sans motif",
                }.Write(writer),
                DeliveryMethod.ReliableOrdered);

            _log.LogWarning(
                $"[net] apparition demandée : '{session.Username}'#{session.PlayerId} -> " +
                $"'(TechType {msg.TechType})' REFUSÉE — {reason} (requête={msg.RequestId}).");
        }
    }
}
