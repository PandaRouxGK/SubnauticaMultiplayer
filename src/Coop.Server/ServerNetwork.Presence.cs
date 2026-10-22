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

        private void ExpireStalledHandshakes()
        {
            if (_sessions.Count == 0)
            {
                return;
            }

            var now = ServerClock.Milliseconds;
            List<NetPeer> stalled = null;

            foreach (var pair in _sessions)
            {
                if (pair.Value.Accepted || now < pair.Value.HandshakeDeadline)
                {
                    continue;
                }

                stalled = stalled ?? new List<NetPeer>();
                stalled.Add(pair.Key);
            }

            if (stalled == null)
            {
                return;
            }

            foreach (var peer in stalled)
            {
                _log.LogWarning($"[net] handshake non terminé après {Protocol.HandshakeTimeoutSeconds}s : {peer} éjecté");
                peer.Disconnect();
            }
        }
        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            var known = _sessions.TryGetValue(peer, out var session);
            _sessions.Remove(peer);

            var who = known && session.Username != null ? $"{session.Username}#{session.PlayerId}" : peer.ToString();
            _log.LogInfo(
                $"[net] '{who}' déconnecté (raison={info.Reason}, pings={session?.PingsReceived ?? 0}, " +
                $"positions={session?.PositionsReceived ?? 0}, objetsTenus={session?.HeldItemsReceived ?? 0})");

            if (known && session.Accepted)
            {
                var leftMsg = new PlayerLeftMessage
                {
                    PlayerId = session.PlayerId,
                    Username = session.Username,
                    Reason = info.Reason.ToString(),
                };

                foreach (var pair in _sessions)
                {
                    if (!pair.Value.Accepted)
                    {
                        continue;
                    }

                    Send(pair.Key, leftMsg.Write, DeliveryMethod.ReliableOrdered);
                }

                _log.LogInfo($"[net] PlayerLeft diffusé pour '{session.Username}' (id={session.PlayerId})");

                // Ses volants aussi : un client parti ne les rendra jamais lui-même (v12).
                List<long> owned = null;
                foreach (var pair in _vehicleDrivers)
                {
                    if (pair.Value != session.PlayerId)
                    {
                        continue;
                    }

                    if (owned == null)
                    {
                        owned = new List<long>();
                    }

                    owned.Add(pair.Key);
                }

                if (owned != null)
                {
                    foreach (var key in owned)
                    {
                        _vehicleDrivers.Remove(key);
                        _vehicleDriverLastState.Remove(key);
                        VehicleReleases++;
                    }

                    _log.LogInfo(
                        $"[net] conduite : {owned.Count} volant(s) libéré(s) par la déconnexion de " +
                        $"'{session.Username}'#{session.PlayerId}.");
                }

                // Ses réclamations aussi. Mesuré le 2026-10-20 (reconnexion P2) : un objet réclamé par
                // le client tué restait refusé aux autres **avec l'id du mort** —
                // `REFUSÉE (déjà tenue par id 2)` — donc perdu pour tout le monde après une simple
                // déconnexion. L'objet accordé est déjà retiré du monde serveur ; la réclamation ne
                // protège plus rien une fois son détenteur parti.
                var releasedClaims = _pickupClaims.Release(session.PlayerId);

                if (releasedClaims > 0)
                {
                    _log.LogInfo(
                        $"[net] pickup : {releasedClaims} réclamation(s) libérée(s) par la déconnexion de " +
                        $"'{session.Username}'#{session.PlayerId} — les clés redeviennent réclamables.");
                }
            }
        }
        private void HandleHandshake(NetPeer peer, ClientSession session, HandshakeRequest request)
        {
            if (session.Accepted)
            {
                _log.LogWarning($"[net] handshake en double ignoré de {peer}");
                return;
            }

            var reason = Validate(request);
            if (reason != null)
            {
                _log.LogWarning($"[net] handshake REFUSÉ pour '{request.Username}' : {reason}");
                // On envoie le motif en fiable et on LAISSE le client fermer. Couper juste après
                // l'envoi faisait perdre le motif (la coupure dépassait le message) : le client
                // ne voyait plus qu'un « RemoteConnectionClose » sans explication.
                Send(peer, w => new HandshakeResponse
                {
                    Accepted = false,
                    Reason = reason,
                    ServerTick = ServerTickBehaviour.Ticks,
                    AssignedPlayerId = 0,
                }.Write(w), DeliveryMethod.ReliableOrdered);
                session.HandshakeDeadline = ServerClock.Milliseconds + Protocol.HandshakeTimeoutSeconds * 1000L;
                return;
            }

            _nextPlayerId++;
            if (_nextPlayerId == 0)
            {
                _nextPlayerId = 1;
            }

            session.PlayerId = _nextPlayerId;
            // Le pseudo d'un pair n'est pas une identité : il est affiché, journalisé et recopié dans
            // le nom du clone d'avatar. On le normalise ici pour qu'un client tiers ne puisse pas
            // injecter un saut de ligne dans une ligne de journal ni un caractère de contrôle dans un
            // nom d'objet. Le refus « pseudo trop long » de `Validate` reste pour l'annonce faite.
            session.Username = Usernames.Sanitize(request.Username);
            session.Accepted = true;
            session.RotW = 1f;

            // Empreinte admise (P0). Si le serveur n'a pas encore résolu la sienne, la vérification
            // est différée au tick suivant plutôt que de refuser un client correct sur une course de
            // démarrage — mais elle aura lieu **avant** que le client n'ait chargé son monde.
            session.Save = request.Save;
            session.SavePending = SaveIdentitySource.Current == null || SaveIdentitySource.Current.IsUnknown;

            if (session.SavePending)
            {
                _log.LogInfo(
                    $"[net] empreinte de sauvegarde en attente (serveur pas encore résolu) : " +
                    $"client={request.Save?.Describe()} — vérifiée au prochain tick.");
            }
            else
            {
                _log.LogInfo(
                    $"[net] empreinte de sauvegarde acceptée : client={request.Save.Describe()} · " +
                    $"serveur={SaveIdentitySource.Current.Describe()}");
            }

            // La couleur est décidée **ici**, une fois, et annoncée ensuite à tout le monde. L'hôte
            // occupe l'indice 0 quand son personnage est dans le monde : on cherche donc sa
            // présence avant de distribuer quoi que ce soit, et on réutilise la même référence plus
            // bas plutôt que de refaire la recherche.
            // **Sauf en serveur dédié** : l'admission est l'autre chemin qui fabriquait un avatar id=9999 —
            // celui que le client reçoit juste après son handshake, avant tout snapshot. Le mesurer a montré
            // que bloquer seulement la diffusion périodique laissait un « HoteServeur » apparaître 10 s plus
            // tard : il venait d'ici.
            var hostPlayer = CoopServerPlugin.Dedicated ? null : UnityEngine.Object.FindObjectOfType<Player>();
            var hostInWorld = hostPlayer != null && hostPlayer.gameObject != null;
            session.ColorIndex = AllocateColorIndex(hostInWorld);

            Protocol.PlayerColorAt(session.ColorIndex, out var assignedR, out var assignedG, out var assignedB);

            _log.LogInfo(
                $"[net] handshake ACCEPTÉ : '{request.Username}' (id={session.PlayerId}, jeu={request.GameVersion}, modHash={request.ModListHash}) " +
                $"depuis {peer} — {AcceptedClients}/{Protocol.MaxClients} client(s)");

            Send(peer, w => new HandshakeResponse
            {
                Accepted = true,
                Reason = string.Empty,
                ServerTick = ServerTickBehaviour.Ticks,
                AssignedPlayerId = session.PlayerId,
            }.Write(w), DeliveryMethod.ReliableOrdered);

            _log.LogInfo(
                $"[net] couleur assignée à '{session.Username}'#{session.PlayerId} : indice {session.ColorIndex} " +
                $"= rgb({assignedR},{assignedG},{assignedB})");

            // Sa propre couleur d'abord : un client qui ignore la sienne ne peut ni l'afficher, ni la
            // signaler en cas de doute.
            Send(peer, w => new PlayerColorMessage
            {
                PlayerId = session.PlayerId,
                ColorIndex = session.ColorIndex,
            }.Write(w), DeliveryMethod.ReliableOrdered);

            // 1. Informer le nouvel arrivant de tous les joueurs déjà présents (ainsi que l'hôte id=9999 s'il est dans le monde).
            if (hostInWorld)
            {
                var htr = hostPlayer.transform;
                var hpos = htr.position;
                // Même source que le client : le lacet du regard, pas la racine du joueur (sinon
                // l'avatar de l'hôte ne tourne jamais, et le receveur lui prêtait son propre lacet).
                var hrot = ViewDirection.Yaw(htr);
                Send(peer, w => new PlayerJoinedMessage
                {
                    PlayerId = 9999,
                    Username = "HoteServeur",
                    X = hpos.x,
                    Y = hpos.y,
                    Z = hpos.z,
                    RotX = hrot.x,
                    RotY = hrot.y,
                    RotZ = hrot.z,
                    RotW = hrot.w,
                }.Write(w), DeliveryMethod.ReliableOrdered);

                var hostEquip = ReadHostEquipment();
                if (hostEquip != null)
                {
                    Send(peer, w => new PlayerEquipmentMessage
                    {
                        PlayerId = 9999,
                        TechTypeNames = hostEquip,
                    }.Write(w), DeliveryMethod.ReliableOrdered);
                }

                var hostHeldItem = ReadHostHeldItemName();
                var hostHeldItemParams = ReadHostHoldingParams();
                if (hostHeldItem != null && hostHeldItemParams != null)
                {
                    Send(peer, w => new PlayerHeldItemMessage
                    {
                        PlayerId = 9999,
                        TechTypeName = hostHeldItem,
                        HoldingParams = hostHeldItemParams,
                    }.Write(w), DeliveryMethod.ReliableOrdered);
                }

                // La couleur de l'hôte : c'est elle qui rend le personnage de l'instance serveur
                // reconnaissable parmi les joueurs, au même titre que sa tenue.
                Send(peer, w => new PlayerColorMessage
                {
                    PlayerId = 9999,
                    ColorIndex = HostColorIndex,
                }.Write(w), DeliveryMethod.ReliableOrdered);
            }

            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted || pair.Key == peer)
                {
                    continue;
                }

                var other = pair.Value;
                Send(peer, w => new PlayerJoinedMessage
                {
                    PlayerId = other.PlayerId,
                    Username = other.Username,
                    X = other.X,
                    Y = other.Y,
                    Z = other.Z,
                    RotX = other.RotX,
                    RotY = other.RotY,
                    RotZ = other.RotZ,
                    RotW = other.RotW,
                }.Write(w), DeliveryMethod.ReliableOrdered);

                if (other.Equipment != null)
                {
                    Send(peer, w => new PlayerEquipmentMessage
                    {
                        PlayerId = other.PlayerId,
                        TechTypeNames = other.Equipment,
                    }.Write(w), DeliveryMethod.ReliableOrdered);
                }

                if (other.HeldItemName != null)
                {
                    Send(peer, w => new PlayerHeldItemMessage
                    {
                        PlayerId = other.PlayerId,
                        TechTypeName = other.HeldItemName,
                        HoldingParams = other.HoldingParams ?? Array.Empty<string>(),
                    }.Write(w), DeliveryMethod.ReliableOrdered);
                }

                Send(peer, w => new PlayerColorMessage
                {
                    PlayerId = other.PlayerId,
                    ColorIndex = other.ColorIndex,
                }.Write(w), DeliveryMethod.ReliableOrdered);
            }

            // 2. Informer tous les joueurs déjà présents de l'arrivée de ce joueur.
            var joinedMsg = new PlayerJoinedMessage
            {
                PlayerId = session.PlayerId,
                Username = session.Username,
                X = session.X,
                Y = session.Y,
                Z = session.Z,
                RotX = session.RotX,
                RotY = session.RotY,
                RotZ = session.RotZ,
                RotW = session.RotW,
            };

            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted || pair.Key == peer)
                {
                    continue;
                }

                Send(pair.Key, joinedMsg.Write, DeliveryMethod.ReliableOrdered);
                Send(pair.Key, w => new PlayerColorMessage
                {
                    PlayerId = session.PlayerId,
                    ColorIndex = session.ColorIndex,
                }.Write(w), DeliveryMethod.ReliableOrdered);
            }

            // 3. Ce qui a été fabriqué avant son arrivée. Après les joueurs : un client qui reçoit l'annonce
            //    d'une entité doit déjà connaître l'état du monde auquel elle se rattache.
            SendSpawnedEntities(peer, session);

            // 4. Son inventaire, **à lui seul** (v19), mais d'abord son **adoption** (v21) : le serveur ne
            //    peut pas lire la sauvegarde d'un client, donc il demande une fois ce que `Inventory.main`
            //    contient. Le registre restauré du disque est déjà chargé ; l'adoption le remplace. Sans
            //    elle, aucun craft ne pourrait être payé avec des matériaux sauvegardés (PLAN.md §69).
            RequestInventoryAdoption(peer, session);
        }
        private byte AllocateColorIndex(bool hostInWorld)
        {
            // Sans hôte dans le monde, l'indice 0 est libre : les clients le prennent à leur tour.
            var start = hostInWorld ? HostColorIndex + 1 : 0;
            var index = (byte)((_nextClientColorIndex + start) % Protocol.PlayerColorCount);
            _nextClientColorIndex = (byte)((_nextClientColorIndex + 1) % Protocol.PlayerColorCount);
            return index;
        }
        private void HandlePing(NetPeer peer, ClientSession session, PingMessage ping)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] ping avant handshake ignoré de {peer}");
                return;
            }

            session.PingsReceived++;

            Send(peer, w => new PongMessage
            {
                Sequence = ping.Sequence,
                ClientTimeMs = ping.ClientTimeMs,
                ServerTick = ServerTickBehaviour.Ticks,
                ServerTimeMs = ServerClock.Milliseconds,
            }.Write(w), DeliveryMethod.Unreliable);
        }
        private void HandlePlayerPosition(NetPeer peer, ClientSession session, PlayerPositionMessage pos)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] position avant handshake ignorée de {peer}");
                return;
            }

            // Rejeter les paquets UDP arrivés dans le désordre.
            if (session.PositionsReceived > 0 && pos.Sequence <= session.LastPositionSequence)
            {
                return;
            }

            // Garde-fou contre les flottants dégénérés (NaN / Inf).
            if (float.IsNaN(pos.X) || float.IsInfinity(pos.X) ||
                float.IsNaN(pos.Y) || float.IsInfinity(pos.Y) ||
                float.IsNaN(pos.Z) || float.IsInfinity(pos.Z) ||
                float.IsNaN(pos.RotX) || float.IsInfinity(pos.RotX) ||
                float.IsNaN(pos.RotY) || float.IsInfinity(pos.RotY) ||
                float.IsNaN(pos.RotZ) || float.IsInfinity(pos.RotZ) ||
                float.IsNaN(pos.RotW) || float.IsInfinity(pos.RotW))
            {
                _log.LogWarning($"[net] position NaN/Infinity ignorée de '{session.Username}'#{session.PlayerId}");
                return;
            }

            // La séquence et le compteur avancent **même si la position est refusée** : un message
            // rejeté ne doit pas être rejoué, et le taux de refus se calcule sur les messages reçus.
            session.LastPositionSequence = pos.Sequence;
            session.PositionsReceived++;

            var wallNow = UnityEngine.Time.realtimeSinceStartup;

            if (!session.HasAcceptedPosition)
            {
                StoreAcceptedPosition(session, pos, wallNow);
                session.NextPositionReportAt = wallNow + PositionReportSeconds;

                _log.LogInfo(
                    $"[net] première position reçue de '{session.Username}'#{session.PlayerId} : " +
                    $"({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}) v=({pos.MoveSpeedX:F1},{pos.MoveSpeedY:F1},{pos.MoveSpeedZ:F1}) " +
                    $"pitch={pos.ViewPitch:F1} sousEau={pos.IsUnderwater} actions={pos.ActionFlags}");
                ReportPositionRate(session, wallNow);
                return;
            }

            // P1 : la position est jugée contre la dernière position **acceptée**, pas contre la
            // précédente reçue. Un refus ne déplace donc pas la référence, sinon un client pourrait
            // avancer par petits sauts refusés sans jamais être corrigé.
            var deltaSeconds = wallNow - session.LastAcceptedAt;
            var verdict = PlayerPlausibility.Evaluate(
                session.LastAcceptedX,
                session.LastAcceptedY,
                session.LastAcceptedZ,
                pos.X,
                pos.Y,
                pos.Z,
                deltaSeconds,
                session.LastAcceptedFlags,
                pos.ActionFlags);

            if (verdict.Accepted)
            {
                session.RejectedStreak = 0;
                StoreAcceptedPosition(session, pos, wallNow);
            }
            else if (session.RejectedStreak + 1 >= PlayerPlausibility.MaxConsecutiveRejections)
            {
                // Une téléportation légitime (console, téléporteur, réapparition) ne doit pas
                // condamner la session : après trois refus consécutifs, la position reçue est
                // adoptée comme resynchronisation, et c'est **écrit** comme tel. Le troisième refus
                // est compté comme les deux premiers : le taux publié doit refléter tous les refus,
                // sinon une session qui resynchronise sans arrêt paraîtrait propre.
                session.PositionsRejected++;
                session.PositionResyncs++;
                session.RejectedStreak = 0;
                StoreAcceptedPosition(session, pos, wallNow);

                _log.LogWarning(
                    $"[net] position RESYNCHRONISÉE de '{session.Username}'#{session.PlayerId} après " +
                    $"{PlayerPlausibility.MaxConsecutiveRejections} refus consécutifs : {verdict.Reason} — la position reçue est adoptée.");

                // Budget : au-delà de cinq resynchronisations en une minute, ce n'est plus un
                // téléporteur du jeu, c'est un client qui saute. La session est éjectée avec un motif.
                if (session.ResyncWindowStart < 0f || wallNow - session.ResyncWindowStart > 60f)
                {
                    session.ResyncWindowStart = wallNow;
                    session.ResyncsInWindow = 0;
                }

                session.ResyncsInWindow++;
                if (session.ResyncsInWindow > 5)
                {
                    var budgetReason =
                        $"trop de resynchronisations de position ({session.ResyncsInWindow} en moins de 60 s) — " +
                        "une téléportation légitime est rare, une téléportation répétée n'en est pas une";

                    _log.LogWarning(
                        $"[net] position : '{session.Username}'#{session.PlayerId} éjecté — {budgetReason}");

                    Send(peer, w => new DisconnectMessage { Reason = budgetReason }.Write(w), DeliveryMethod.ReliableOrdered);
                    session.Accepted = false;
                    session.HandshakeDeadline = ServerClock.Milliseconds + 2000L;
                    return;
                }
            }
            else
            {
                session.RejectedStreak++;
                session.PositionsRejected++;

                // Le serveur garde la dernière position acceptée : les autres joueurs ne voient
                // jamais le saut, et le client reçoit de quoi revenir.
                if (session.PositionsRejected <= 3 || session.PositionsRejected % 100 == 0)
                {
                    _log.LogWarning(
                        $"[net] position REFUSÉE de '{session.Username}'#{session.PlayerId} : {verdict.Reason} — " +
                        $"correction renvoyée (refus={session.PositionsRejected})");
                }

                Send(peer, w => new PlayerCorrectionMessage
                {
                    ServerTick = ServerTickBehaviour.Ticks,
                    X = session.LastAcceptedX,
                    Y = session.LastAcceptedY,
                    Z = session.LastAcceptedZ,
                    Reason = WireReason(verdict.Reason),
                }.Write(w), DeliveryMethod.ReliableOrdered);
            }

            ReportPositionRate(session, wallNow);
        }
        private static void StoreAcceptedPosition(ClientSession session, PlayerPositionMessage pos, float now)
        {
            session.HasAcceptedPosition = true;
            session.LastAcceptedX = pos.X;
            session.LastAcceptedY = pos.Y;
            session.LastAcceptedZ = pos.Z;
            session.LastAcceptedFlags = pos.ActionFlags;
            session.LastAcceptedAt = now;

            session.X = pos.X;
            session.Y = pos.Y;
            session.Z = pos.Z;
            session.RotX = pos.RotX;
            session.RotY = pos.RotY;
            session.RotZ = pos.RotZ;
            session.RotW = pos.RotW;
            session.MoveSpeedX = pos.MoveSpeedX;
            session.MoveSpeedY = pos.MoveSpeedY;
            session.MoveSpeedZ = pos.MoveSpeedZ;
            session.ViewPitch = pos.ViewPitch;
            session.IsUnderwater = pos.IsUnderwater;
            session.OnSurface = pos.OnSurface;
            session.ActionFlags = pos.ActionFlags;
        }
        private void ReportPositionRate(ClientSession session, float now)
        {
            if (now < session.NextPositionReportAt)
            {
                return;
            }

            session.NextPositionReportAt = now + PositionReportSeconds;

            if (session.PositionsReceived <= 0)
            {
                return;
            }

            var rate = 100.0 * session.PositionsRejected / session.PositionsReceived;
            _log.LogInfo(
                $"[net] position : '{session.Username}'#{session.PlayerId} {session.PositionsReceived} reçue(s), " +
                $"{session.PositionsRejected} refusée(s) ({rate:F1}%), {session.PositionResyncs} resynchronisée(s)");
        }
        private void HandlePlayerEquipment(NetPeer peer, ClientSession session, PlayerEquipmentMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] équipement avant handshake ignoré de {peer}");
                return;
            }

            // Le serveur est autoritaire sur l'identité de l'émetteur : un client ne peut pas
            // prétendre modifier la tenue d'un autre joueur. Le champ `PlayerId` reçu n'est donc
            // **jamais** recopié — il sert seulement à constater la falsification, et c'est cette
            // constatation qui rend la règle mesurable au lieu de lisible.
            var forgedEquipment = msg.PlayerId != 0 && msg.PlayerId != session.PlayerId;
            if (forgedEquipment)
            {
                ForgedPlayerIds++;
                _log.LogWarning(
                    $"[net] PlayerId falsifié sur l'équipement : '{session.Username}' a annoncé sous l'id " +
                    $"{msg.PlayerId} (le sien est {session.PlayerId}) — le relais réécrit avec l'id de la session.");
                _log.LogInfo(
                    $"[net] vérification PlayerId (équipement) : '{session.Username}' a annoncé l'id " +
                    $"{msg.PlayerId}, effectif={session.PlayerId} -> verdict=True");
            }

            session.Equipment = msg.TechTypeNames ?? Array.Empty<string>();
            session.EquipmentsReceived++;

            var summary = session.Equipment.Length == 0
                ? "(sans équipement)"
                : string.Join(", ", session.Equipment);
            _log.LogInfo(
                $"[net] équipement reçu de '{session.Username}'#{session.PlayerId} " +
                $"({session.Equipment.Length} pièce(s)) : {summary}");

            var outgoing = new PlayerEquipmentMessage
            {
                PlayerId = session.PlayerId,
                TechTypeNames = session.Equipment,
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
                $"[net] PlayerEquipment de '{session.Username}'#{session.PlayerId} diffusé à {forwarded} autre(s) client(s).");
            if (forgedEquipment)
            {
                _log.LogInfo(
                    $"[net] vérification PlayerId (équipement) : diffusé sous l'id {session.PlayerId} " +
                    $"(revendiqué {msg.PlayerId}) pour {forwarded} client(s) -> verdict=True");
            }
        }
        private static string[] ReadHostEquipment()
        {
            var inventory = Inventory.main;
            var equipment = inventory != null ? inventory.equipment : null;
            if (equipment == null)
            {
                return null;
            }

            var names = new List<string>();
            var slots = equipment.GetEquipment();
            while (slots.MoveNext())
            {
                var item = slots.Current.Value;
                var pickupable = item != null ? item.item : null;
                if (pickupable == null)
                {
                    continue;
                }

                names.Add(pickupable.GetTechType().ToString());
            }

            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }
        private void BroadcastHostEquipmentIfChanged()
        {
            var hostEquip = ReadHostEquipment();
            if (hostEquip == null)
            {
                return;
            }

            var signature = string.Join("|", hostEquip);
            if (string.Equals(signature, _lastHostEquipmentSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastHostEquipmentSignature = signature;
            _lastHostEquipment = hostEquip;

            var outgoing = new PlayerEquipmentMessage
            {
                PlayerId = 9999,
                TechTypeNames = hostEquip,
            };

            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted)
                {
                    continue;
                }

                Send(pair.Key, outgoing.Write, DeliveryMethod.ReliableOrdered);
            }
        }
    }
}
