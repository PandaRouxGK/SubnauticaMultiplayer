using System;
using System.Collections.Generic;
using BepInEx.Logging;
using System.Linq;
using Coop.Model;
using Coop.Shared;
using LiteNetLib;

namespace Coop.Server
{
    /// <summary>
    /// Serveur réseau du jalon M1 : accepte les connexions, valide le handshake et répond aux pings.
    ///
    /// Répartition des responsabilités :
    ///  - LiteNetLib gère le transport (UDP, fiabilité, fragmentation) ;
    ///  - <see cref="Coop.Model"/> gère le format des messages ;
    ///  - cette classe ne fait que la logique d'admission et le routage.
    ///
    /// Les callbacks sont invoqués depuis <see cref="Poll"/>, donc sur le thread principal :
    /// aucun état partagé n'a besoin d'être verrouillé tant que Poll n'est appelé que là.
    /// </summary>
    public sealed partial class ServerNetwork
    {

        private void HandleHeldItem(NetPeer peer, ClientSession session, PlayerHeldItemMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] objet tenu avant handshake ignoré de {peer}");
                return;
            }

            // Le client peut prétendre tenir l'objet d'un autre : l'identité qui compte est celle de la
            // session, jamais celle du message. Un écart est une falsification, et elle se dit — une
            // réécriture silencieuse laisserait croire à une confiance accordée au champ.
            var forgedHeld = msg.PlayerId != 0 && msg.PlayerId != session.PlayerId;
            if (forgedHeld)
            {
                ForgedPlayerIds++;
                _log.LogWarning(
                    $"[net] PlayerId falsifié sur l'objet tenu : '{session.Username}' a annoncé sous l'id " +
                    $"{msg.PlayerId} (le sien est {session.PlayerId}) — le relais réécrit avec l'id de la session.");
                _log.LogInfo(
                    $"[net] vérification PlayerId (objet tenu) : '{session.Username}' a annoncé l'id " +
                    $"{msg.PlayerId}, effectif={session.PlayerId} -> verdict=True");
            }

            session.HeldItemName = msg.TechTypeName ?? string.Empty;
            session.HoldingParams = msg.HoldingParams ?? Array.Empty<string>();
            session.HeldItemsReceived++;

            var held = session.HeldItemName.Length == 0 ? "(rien en main)" : session.HeldItemName;
            var parameters = session.HoldingParams.Length == 0
                ? "(aucun paramètre)"
                : string.Join(", ", session.HoldingParams);
            _log.LogInfo(
                $"[net] objet tenu reçu de '{session.Username}'#{session.PlayerId} : {held} " +
                $"— paramètres : {parameters}");

            var outgoing = new PlayerHeldItemMessage
            {
                PlayerId = session.PlayerId,
                TechTypeName = session.HeldItemName,
                HoldingParams = session.HoldingParams,
            };

            var forwarded = 0;
            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted || pair.Key == peer)
                {
                    continue;
                }

                Send(pair.Key, outgoing.Write, DeliveryMethod.ReliableOrdered);
                forwarded++;
            }

            _log.LogInfo(
                $"[net] PlayerHeldItem de '{session.Username}'#{session.PlayerId} diffusé à {forwarded} autre(s) client(s).");
        }
        private void HandlePickupClaim(NetPeer peer, ClientSession session, PickupClaimMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] réclamation de ramassage avant handshake ignorée de {peer}");
                return;
            }

            var tech = string.IsNullOrEmpty(msg.TechTypeName) ? "(sans nom)" : msg.TechTypeName;

            if (msg.PlayerId != 0 && msg.PlayerId != session.PlayerId)
            {
                ForgedPickupIds++;
                _log.LogWarning(
                    $"[net] PlayerId falsifié sur une réclamation : '{session.Username}' a annoncé sous l'id " +
                    $"{msg.PlayerId} (le sien est {session.PlayerId}) — la réclamation est attribuée à la session.");
                _log.LogInfo(
                    $"[net] vérification PlayerId (pickup) : '{session.Username}' a annoncé l'id " +
                    $"{msg.PlayerId}, effectif={session.PlayerId} -> verdict=True");
            }

            // — L'objet doit exister **chez le serveur**, et le joueur doit être à portée.
            //
            // Ce sont les deux gardes qui manquaient à la décision seule : une clé inconnue était
            // accordée, et un joueur pouvait réclamer un objet à l'autre bout de la carte. La portée se
            // mesure avec la **dernière position reçue** de la session (20 Hz), sans nouveau champ dans
            // le message : le format de la v8 suffit.
            // — Une clé **déjà attribuée** est refusée immédiatement, avant toute résolution du monde.
            //
            // L'ordre compte depuis la v11 : un objet accordé est retiré du monde simulé, donc la résolution
            // échouerait et le motif deviendrait « objet inconnu » — vrai, mais muet sur le fait qu'un
            // joueur le détient. La carte des réclamations est le registre durable ; elle passe en premier.
            if (msg.ItemKey != 0L &&
                _pickupClaims.TryGetHolder(msg.ItemKey, out var alreadyHeld) &&
                alreadyHeld != session.PlayerId)
            {
                PickupClaimsRefused++;
                _log.LogInfo(
                    $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                    $"'{session.Username}'#{session.PlayerId} -> REFUSÉE (déjà tenue par {DescribeHolder(alreadyHeld)})");

                Send(peer, new PickupResultMessage
                {
                    ItemKey = msg.ItemKey,
                    TechTypeName = tech,
                    Granted = false,
                    HolderId = alreadyHeld,
                    ClaimedBy = msg.PlayerId,
                }.Write, DeliveryMethod.ReliableOrdered);

                return;
            }

            UnityEngine.Vector3 itemPosition = UnityEngine.Vector3.zero;
            var distance = 0f;
            string refusal = null;

            if (!TryResolveWorldItem(msg.ItemKey, out itemPosition, out var indexedKeys))
            {
                PickupRefusedUnknown++;
                refusal = $"objet inconnu du serveur ({indexedKeys} objet(s) indexé(s)) — la clé ne désigne " +
                          "rien dans le monde simulé";
            }
            else if (session.PositionsReceived == 0)
            {
                PickupRefusedNoPosition++;
                refusal = "position du joueur inconnue (aucune position reçue) — la portée n'est pas vérifiable";
            }
            else
            {
                distance = UnityEngine.Vector3.Distance(
                    new UnityEngine.Vector3(session.X, session.Y, session.Z), itemPosition);

                if (distance > Protocol.MaxPickupRangeMeters)
                {
                    PickupRefusedRange++;
                    refusal = $"hors portée : {distance:F1} m > {Protocol.MaxPickupRangeMeters:F1} m";
                }
            }

            if (refusal != null)
            {
                PickupClaimsRefused++;
                _log.LogInfo(
                    $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                    $"'{session.Username}'#{session.PlayerId} -> REFUSÉE ({refusal})");

                Send(peer, new PickupResultMessage
                {
                    ItemKey = msg.ItemKey,
                    TechTypeName = tech,
                    Granted = false,
                    HolderId = 0,
                    ClaimedBy = msg.PlayerId,
                }.Write, DeliveryMethod.ReliableOrdered);

                return;
            }

            var granted = false;
            var holder = session.PlayerId;

            if (msg.ItemKey == 0L)
            {
                PickupClaimsRefused++;
                _log.LogWarning(
                    $"[net] pickup : clé nulle réclamée par '{session.Username}'#{session.PlayerId} " +
                    $"('{tech}') -> REFUSÉE (aucune identité d'objet)");
            }
            else if (_pickupClaims.TryGetHolder(msg.ItemKey, out var existing))
            {
                holder = existing;
                granted = existing == session.PlayerId;
                if (granted)
                {
                    PickupClaimsGranted++;
                    _log.LogInfo(
                        $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                        $"'{session.Username}'#{session.PlayerId} -> ACCORDÉE (déjà tenue par le même joueur, " +
                        $"objet à {distance:F1} m)");
                }
                else
                {
                    PickupClaimsRefused++;
                    _log.LogInfo(
                        $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                        $"'{session.Username}'#{session.PlayerId} -> REFUSÉE (déjà tenue par " +
                        $"{DescribeHolder(existing)})");
                }
            }
            else
            {
                _pickupClaims.Claim(msg.ItemKey, session.PlayerId);
                granted = true;
                PickupClaimsGranted++;
                _log.LogInfo(
                    $"[net] pickup : clé=0x{msg.ItemKey:x16} '{tech}' réclamée par " +
                    $"'{session.Username}'#{session.PlayerId} -> ACCORDÉE (premier claim, objet à {distance:F1} m)");
            }

            Send(peer, new PickupResultMessage
            {
                ItemKey = msg.ItemKey,
                TechTypeName = tech,
                Granted = granted,
                HolderId = holder,
                ClaimedBy = msg.PlayerId,
            }.Write, DeliveryMethod.ReliableOrdered);

            // — La conséquence du geste (v11).
            //
            // Jusqu'ici, un accord ne changeait rien dans le monde du serveur : l'objet restait chez lui,
            // et un troisième joueur qui le réclamait recevait un **accord** pour un objet qui n'existait
            // plus pour personne. On retire donc l'objet du monde simulé et on l'annonce.
            //
            // Seulement sur un accord **nouveau** : une réclamation répétée du même joueur (idempotente)
            // ne doit pas retirer deux fois, et un refus ne retire rien.
            if (granted && msg.ItemKey != 0L && !_pickupRemoved.Contains(msg.ItemKey))
            {
                RemoveGrantedItem(msg.ItemKey, tech, session);
            }
        }
        private void RemoveGrantedItem(long key, string tech, ClientSession session)
        {
            if (!TryResolveWorldItemObject(key, out var item))
            {
                _log.LogWarning(
                    $"[net] pickup : clé=0x{key:x16} '{tech}' accordée à '{session.Username}'#{session.PlayerId} " +
                    "mais l'objet n'est plus dans le monde serveur — rien à retirer (il a peut-être déjà été " +
                    "consumé par le jeu).");
                return;
            }

            var described = ItemIdentity.Describe(item);

            _pickupRemoved.Add(key);
            UnityEngine.Object.Destroy(item.gameObject);

            // L'index est invalidé : sans ça, la clé resterait résoluble pendant une seconde, et une
            // réclamation concurrente retomberait sur une position morte.
            _itemIndex = null;

            _log.LogInfo(
                $"[net] pickup : clé=0x{key:x16} '{tech}' retirée du monde serveur ({described}) — " +
                $"ramassée par '{session.Username}'#{session.PlayerId}.");

            // Le détenteur est exclu : son exemplaire est déjà dans son inventaire, et c'est son geste à lui
            // qui le fait disparaître de son monde.
            BroadcastDespawn(
                key,
                EntityKind.Item,
                $"ramassée par '{session.Username}'#{session.PlayerId}",
                session.PlayerId);
        }
        private bool TryResolveWorldItem(long key, out UnityEngine.Vector3 position, out int indexedKeys)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;

            if (_itemIndex == null || now - _itemIndexBuiltAt > 1f)
            {
                var index = new Dictionary<long, UnityEngine.Vector3>();
                var items = ItemIdentity.CollectWorldItems();
                var shared = 0;
                var sharedKey = 0L;

                foreach (var item in items)
                {
                    var itemKey = ItemIdentity.KeyOf(item, out _);
                    if (itemKey == 0L)
                    {
                        continue;
                    }

                    if (index.ContainsKey(itemKey))
                    {
                        shared++;
                        sharedKey = itemKey;
                    }

                    index[itemKey] = item.transform != null ? item.transform.position : UnityEngine.Vector3.zero;
                }

                _itemIndex = index;
                _itemIndexBuiltAt = now;
                _itemIndexObjects = items.Count;

                if (shared != _itemIndexSharedKeys)
                {
                    _itemIndexSharedKeys = shared;
                    _log.LogWarning(
                        $"[net] pickup : {shared} clé(s) d'objet partagée(s) dans l'index (ex. 0x{sharedKey:x16}) " +
                        "— deux objets du monde se confondent, la décision portera sur le dernier indexé. " +
                        "C'est une limite du repli par position, pas un refus.");
                }
            }

            indexedKeys = _itemIndex.Count;
            return _itemIndex.TryGetValue(key, out position);
        }
        private bool TryResolveWorldItemObject(long key, out Pickupable item)
        {
            item = null;
            if (key == 0L)
            {
                return false;
            }

            foreach (var candidate in ItemIdentity.CollectWorldItems())
            {
                if (candidate == null || ItemIdentity.KeyOf(candidate, out _) != key)
                {
                    continue;
                }

                item = candidate;
                return true;
            }

            return false;
        }
        private static string ReadHostHeldItemName()
        {
            var inventory = Inventory.main;
            if (inventory == null)
            {
                return null;
            }

            var tool = inventory.GetHeldTool();
            if (tool == null)
            {
                return string.Empty;
            }

            var pickupable = tool.GetComponent<Pickupable>();
            return pickupable != null ? pickupable.GetTechType().ToString() : string.Empty;
        }
        private void BroadcastHostHeldItemIfChanged()
        {
            var name = ReadHostHeldItemName();
            var parameters = ReadHostHoldingParams();
            if (name == null || parameters == null)
            {
                return;
            }

            var signature = name + "|" + string.Join("|", parameters);
            if (string.Equals(signature, _lastHostHeldItemSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastHostHeldItemSignature = signature;

            var outgoing = new PlayerHeldItemMessage
            {
                PlayerId = 9999,
                TechTypeName = name,
                HoldingParams = parameters,
            };

            var sent = 0;
            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted)
                {
                    continue;
                }

                Send(pair.Key, outgoing.Write, DeliveryMethod.ReliableOrdered);
                sent++;
            }

            _log.LogInfo(
                $"[net] objet tenu de l'hôte modifié : {(name.Length == 0 ? "(rien en main)" : name)} " +
                $"-> diffusé à {sent} client(s) (paramètres lus sur son animator : " +
                (parameters.Length == 0 ? "(aucun)" : string.Join(", ", parameters)) + ")");
        }
    }
}
