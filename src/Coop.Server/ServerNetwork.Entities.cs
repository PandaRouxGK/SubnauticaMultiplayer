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

        private void ValidatePendingSaveIdentities()
        {
            var server = SaveIdentitySource.Current;
            if (server == null || server.IsUnknown || _sessions.Count == 0)
            {
                return;
            }

            List<KeyValuePair<NetPeer, string>> refused = null;

            foreach (var pair in _sessions)
            {
                var session = pair.Value;
                if (!session.Accepted || !session.SavePending)
                {
                    continue;
                }

                var reason = SaveCompatibility.Explain(server, session.Save);
                if (reason == null)
                {
                    session.SavePending = false;
                    _log.LogInfo(
                        $"[net] empreinte de sauvegarde acceptée (vérifiée après coup) : " +
                        $"client={session.Save?.Describe()} · serveur={server.Describe()}");
                    continue;
                }

                refused = refused ?? new List<KeyValuePair<NetPeer, string>>();
                refused.Add(new KeyValuePair<NetPeer, string>(pair.Key, reason));
            }

            if (refused == null)
            {
                return;
            }

            foreach (var item in refused)
            {
                if (!_sessions.TryGetValue(item.Key, out var session))
                {
                    continue;
                }

                _log.LogWarning(
                    $"[net] handshake REFUSÉ (empreinte) : '{session.Username}'#{session.PlayerId} : {item.Value}");

                // Même leçon qu'au §12 : envoyer le motif en fiable, **puis laisser le client fermer**.
                // Couper juste après l'envoi ferait perdre le message, et le client ne verrait qu'une
                // coupure sans explication. `ExpireStalledHandshakes` ferme le peer 2 s plus tard.
                Send(item.Key, w => new DisconnectMessage { Reason = item.Value }.Write(w), DeliveryMethod.ReliableOrdered);

                session.Accepted = false;
                session.SaveMismatch = true;
                session.HandshakeDeadline = ServerClock.Milliseconds + 2000L;
            }
        }
        private void BroadcastDespawn(long key, byte kind, string reason, ushort exceptPlayerId = 0)
        {
            if (key == 0L)
            {
                return;
            }

            // Une entité disparue ne doit plus être rejouée à un client qui se connecte après coup.
            _spawnedEntities.Remove(key);

            var message = new EntityDespawnMessage
            {
                Key = key,
                Kind = kind,
                Reason = reason,
            };

            RememberDespawn(message);

            var sent = 0;
            foreach (var pair in _sessions)
            {
                if (!pair.Value.Accepted)
                {
                    continue;
                }

                if (exceptPlayerId != 0 && pair.Value.PlayerId == exceptPlayerId)
                {
                    continue;
                }

                Send(pair.Key, message.Write, DeliveryMethod.ReliableOrdered);
                sent++;
            }

            _entityDespawnsSent++;

            var what = kind == EntityKind.Vehicle ? "véhicule" : "objet";
            _log.LogInfo(
                $"[net] disparition : clé=0x{key:x16} ({what}) — annoncée à {sent} client(s), motif « {reason} »" +
                (exceptPlayerId != 0 ? $", sauf au détenteur (id {exceptPlayerId})" : string.Empty) + ".");
        }
        private void RememberSpawn(EntitySpawnMessage message)
        {
            if (!_spawnedEntities.ContainsKey(message.Key))
            {
                if (_spawnOrder.Count >= MaxRememberedSpawns)
                {
                    var oldest = _spawnOrder.Dequeue();
                    _spawnedEntities.Remove(oldest);
                    _log.LogWarning(
                        $"[net] apparition : carnet plein ({MaxRememberedSpawns}) — clé=0x{oldest:x16} oubliée " +
                        "(un client qui se connecte maintenant ne la recevra pas).");
                }

                _spawnOrder.Enqueue(message.Key);
            }

            _spawnedEntities[message.Key] = message;

            // Une clé qui réapparaît n'est plus « disparue » : le carnet de retraits doit oublier
            // l'ancienne annonce, sinon le client recréerait puis retirerait la même entité.
            _despawnedEntities.Remove(message.Key);
        }
        private void RememberDespawn(EntityDespawnMessage message)
        {
            if (message == null || message.Key == 0L)
            {
                return;
            }

            // Une clé peut réapparaître puis disparaître de nouveau : la file ne doit pas la contenir
            // deux fois, sinon une éviction consommerait un créneau sans rien retirer et le carnet
            // grossirait au-delà de sa borne. 256 entrées au plus, donc `Contains` est sans coût notable.
            if (!_despawnedEntities.ContainsKey(message.Key) && !_despawnOrder.Contains(message.Key))
            {
                if (_despawnOrder.Count >= MaxRememberedDespawns)
                {
                    var oldest = _despawnOrder.Dequeue();
                    _despawnedEntities.Remove(oldest);
                    _log.LogWarning(
                        $"[net] disparition : carnet plein ({MaxRememberedDespawns}) — clé=0x{oldest:x16} oubliée " +
                        "(un client qui se connecte maintenant ne la recevra pas).");
                }

                _despawnOrder.Enqueue(message.Key);
            }

            _despawnedEntities[message.Key] = message;
        }
        private void SendSpawnedEntities(NetPeer peer, ClientSession session)
        {
            if (_spawnedEntities.Count > 0)
            {
                foreach (var message in _spawnedEntities.Values)
                {
                    Send(peer, message.Write, DeliveryMethod.ReliableOrdered);
                    _entitySpawnsReplayed++;
                }

                _log.LogInfo(
                    $"[net] apparition : {_spawnedEntities.Count} entité(s) fabriquée(s) rejouée(s) pour " +
                    $"'{session.Username}'#{session.PlayerId} (arrivé après leur création).");
            }

            // Modules installés depuis le démarrage du serveur : le client qui arrive charge la même
            // sauvegarde, donc il voit les modules **écrits dans la partie**, mais pas ceux installés en
            // jeu après la dernière sauvegarde. Ils sont rejoués avant les retraits, comme le carnet
            // d'apparitions avant celui des retraits : le client qui reçoit une entité doit déjà savoir
            // dans quel état elle est (v15).
            if (_vehicleUpgrades.Count > 0)
            {
                foreach (var message in _vehicleUpgrades.Values)
                {
                    Send(peer, message.Write, DeliveryMethod.ReliableOrdered);
                    _vehicleUpgradesReplayed++;
                }

                _log.LogInfo(
                    $"[net] modules : {_vehicleUpgrades.Count} ensemble(s) rejoué(s) pour " +
                    $"'{session.Username}'#{session.PlayerId} (installés avant son arrivée).");
            }

            // P2 : le pendant des retraits. Il est indépendant du carnet d'apparitions — une session
            // peut n'avoir que des retraits à rejouer (objet ramassé avant qu'elle n'arrive).
            if (_despawnedEntities.Count == 0)
            {
                return;
            }

            foreach (var message in _despawnedEntities.Values)
            {
                Send(peer, message.Write, DeliveryMethod.ReliableOrdered);
                _entityDespawnsReplayed++;
            }

            _log.LogInfo(
                $"[net] disparition : {_despawnedEntities.Count} entité(s) disparue(s) rejouée(s) pour " +
                $"'{session.Username}'#{session.PlayerId} (arrivé après leur retrait).");
        }
    }
}
