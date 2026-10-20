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

        private void HandleVehicleControl(NetPeer peer, ClientSession session, VehicleControlMessage msg)
        {
            if (!session.Accepted)
            {
                _log.LogWarning($"[net] conduite avant handshake ignorée de {peer}");
                return;
            }

            if (msg.PlayerId != 0 && msg.PlayerId != session.PlayerId)
            {
                ForgedVehicleIds++;
                if (ForgedVehicleIds <= 3)
                {
                    _log.LogWarning(
                        $"[net] PlayerId falsifié sur une conduite : '{session.Username}' a annoncé l'id " +
                        $"{msg.PlayerId} (le sien est {session.PlayerId}) — la conduite est attribuée à la session.");
                }
            }

            if (!TryResolveVehicle(msg.VehicleKey, out var vehicle, out var indexed))
            {
                VehicleControlRefusedUnknown++;
                LogControlRefusal(msg.VehicleKey, session, $"véhicule inconnu du serveur ({indexed} véhicule(s) indexé(s))");
                ReplyControl(peer, msg.VehicleKey, false, 0);
                return;
            }

            var vehiclePosition = vehicle.transform != null
                ? vehicle.transform.position
                : UnityEngine.Vector3.zero;

            var distance = session.PositionsReceived == 0
                ? float.MaxValue
                : UnityEngine.Vector3.Distance(
                    new UnityEngine.Vector3(session.X, session.Y, session.Z), vehiclePosition);

            if (distance > Protocol.MaxVehicleControlRangeMeters)
            {
                VehicleControlRefusedRange++;
                LogControlRefusal(
                    msg.VehicleKey,
                    session,
                    $"hors portée : {distance:F1} m > {Protocol.MaxVehicleControlRangeMeters:F1} m");
                ReplyControl(peer, msg.VehicleKey, false, 0);
                return;
            }

            if (_vehicleDrivers.TryGetValue(msg.VehicleKey, out var driver) && driver != session.PlayerId)
            {
                VehicleControlRefusedTaken++;
                LogControlRefusal(
                    msg.VehicleKey,
                    session,
                    $"déjà conduite par {DescribeHolder(driver)}");
                ReplyControl(peer, msg.VehicleKey, false, driver);
                return;
            }

            var first = !_vehicleDrivers.ContainsKey(msg.VehicleKey);
            _vehicleDrivers[msg.VehicleKey] = session.PlayerId;
            _vehicleDriverLastState[msg.VehicleKey] = UnityEngine.Time.realtimeSinceStartup;

            ApplyVehicleControl(vehicle, msg);
            VehicleControlApplied++;

            if (first)
            {
                _log.LogInfo(
                    $"[net] conduite : clé=0x{msg.VehicleKey:x16} — prise par '{session.Username}'#{session.PlayerId} " +
                    $"({VehicleIdentity.Describe(vehicle)}, joueur à {distance:F1} m), état appliqué et rediffusé.");

                ReplyControl(peer, msg.VehicleKey, true, session.PlayerId);
            }
        }
        private void HandleVehicleRelease(ClientSession session, VehicleReleaseMessage msg)
        {
            if (!session.Accepted || msg.VehicleKey == 0L)
            {
                return;
            }

            if (!_vehicleDrivers.TryGetValue(msg.VehicleKey, out var driver) || driver != session.PlayerId)
            {
                return;
            }

            _vehicleDrivers.Remove(msg.VehicleKey);
            _vehicleDriverLastState.Remove(msg.VehicleKey);
            VehicleReleases++;

            _log.LogInfo(
                $"[net] conduite : clé=0x{msg.VehicleKey:x16} libérée par '{session.Username}'#{session.PlayerId} " +
                "— le volant est libre (libérations=1, total=" + VehicleReleases + ")");
        }
        private void ReleaseSilentDrivers()
        {
            if (_vehicleDrivers.Count == 0)
            {
                return;
            }

            var now = UnityEngine.Time.realtimeSinceStartup;
            List<long> silent = null;

            foreach (var pair in _vehicleDrivers)
            {
                if (!_vehicleDriverLastState.TryGetValue(pair.Key, out var last))
                {
                    last = now;
                    _vehicleDriverLastState[pair.Key] = now;
                }

                if (now - last <= Protocol.VehicleSilenceReleaseSeconds)
                {
                    continue;
                }

                if (silent == null)
                {
                    silent = new List<long>();
                }

                silent.Add(pair.Key);
            }

            if (silent == null)
            {
                return;
            }

            foreach (var key in silent)
            {
                _vehicleDrivers.Remove(key);
                _vehicleDriverLastState.Remove(key);
                VehicleSilenceReleases++;
                _log.LogInfo(
                    $"[net] conduite : clé=0x{key:x16} libérée (silence de {Protocol.VehicleSilenceReleaseSeconds:F1} s) " +
                    $"— le conducteur ne l'envoie plus (libérations par silence={VehicleSilenceReleases}).");
            }
        }
        private static void ApplyVehicleControl(Vehicle vehicle, VehicleControlMessage msg)
        {
            var transform = vehicle.transform;
            if (transform != null)
            {
                transform.position = new UnityEngine.Vector3(msg.X, msg.Y, msg.Z);
                transform.rotation = new UnityEngine.Quaternion(msg.RotX, msg.RotY, msg.RotZ, msg.RotW);
            }

            // Le transform est posé à chaque état : laisser la physique accélérer le Rigidbody ferait
            // osciller le véhicule entre deux corrections. On annule donc sa vitesse, comme pour les
            // créatures suivies.
            var body = vehicle.GetComponent<UnityEngine.Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.velocity = UnityEngine.Vector3.zero;
                body.angularVelocity = UnityEngine.Vector3.zero;
            }
        }
        private bool TryResolveVehicle(long key, out Vehicle vehicle, out int indexed)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;

            if (_vehicleIndex == null || now - _vehicleIndexBuiltAt > 1f)
            {
                var index = new Dictionary<long, Vehicle>();
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<Vehicle>();
                var count = 0;

                foreach (var candidate in all)
                {
                    if (candidate == null || candidate.gameObject == null || !candidate.gameObject.scene.IsValid())
                    {
                        continue;
                    }

                    count++;
                    var candidateKey = VehicleIdentity.KeyOf(candidate, out _);
                    if (candidateKey != 0L)
                    {
                        index[candidateKey] = candidate;
                    }
                }

                _vehicleIndex = index;
                _vehicleIndexBuiltAt = now;
                _vehicleIndexCount = count;
            }

            indexed = _vehicleIndexCount;
            return _vehicleIndex.TryGetValue(key, out vehicle);
        }
        private void BroadcastNewVehicles()
        {
            if (_vehicleCache == null)
            {
                return;
            }

            var baseline = !_vehicleBaselineDone;
            _vehicleBaselineDone = true;
            var announced = 0;
            var present = new HashSet<long>();

            foreach (var vehicle in _vehicleCache)
            {
                if (vehicle == null || vehicle.gameObject == null)
                {
                    continue;
                }

                var key = VehicleIdentity.KeyOf(vehicle, out _);
                if (key != 0L)
                {
                    present.Add(key);
                }

                if (key == 0L || !_knownVehicles.Add(key))
                {
                    continue;
                }

                if (baseline)
                {
                    continue;
                }

                if (!VehicleIdentity.TryGetUniqueId(vehicle, out var uniqueId))
                {
                    _log.LogWarning(
                        $"[net] apparition : '{vehicle.gameObject.name}' (clé=0x{key:x16}) sans identifiant " +
                        "d'entité — NON annoncée : le client ne pourrait pas la faire correspondre.");
                    continue;
                }

                // `CraftData.GetTechType` lit le `TechTag` du prefab : c'est l'espèce (`SeaMoth`), pas le nom
                // de l'instance (`SeaMoth(Clone)`), et c'est ce que le client sait résoudre pour instancier.
                var tech = CraftData.GetTechType(vehicle.gameObject).ToString();
                if (tech.Length > Protocol.MaxTechTypeNameLength)
                {
                    _log.LogWarning(
                        $"[net] apparition : nom d'espèce trop long pour '{vehicle.gameObject.name}' — non annoncée.");
                    continue;
                }

                var transform = vehicle.transform;
                var position = transform != null ? transform.position : UnityEngine.Vector3.zero;
                var rotation = transform != null ? transform.rotation : UnityEngine.Quaternion.identity;

                // P1bis : la définition voyage avec l'annonce, et son empreinte est vérifiable.
                // Un véhicule du jeu a une échelle neutre ; le contrat existe pour que le client
                // puisse **refuser** une définition divergente au lieu de la supposer.
                var definition = EntityDefinition.ForVehicle(tech);

                var message = new EntitySpawnMessage
                {
                    Key = key,
                    UniqueId = uniqueId,
                    TechTypeName = tech,
                    X = position.x,
                    Y = position.y,
                    Z = position.z,
                    RotX = rotation.x,
                    RotY = rotation.y,
                    RotZ = rotation.z,
                    RotW = rotation.w,
                    ScaleX = definition.ScaleX,
                    ScaleY = definition.ScaleY,
                    ScaleZ = definition.ScaleZ,
                    DefinitionHash = definition.ComputeHash(),
                };

                // Mémorisé **avant** l'envoi : un client qui se connecte pendant la diffusion doit trouver
                // l'entité dans le carnet, sinon il ne la verrait jamais.
                RememberSpawn(message);

                var sent = 0;
                foreach (var pair in _sessions)
                {
                    if (!pair.Value.Accepted)
                    {
                        continue;
                    }

                    Send(pair.Key, message.Write, DeliveryMethod.ReliableOrdered);
                    sent++;
                }

                _entitySpawnsSent++;
                announced++;

                _log.LogInfo(
                    $"[net] apparition : clé=0x{key:x16} '{vehicle.gameObject.name}' ({tech}) — " +
                    $"diffusée à {sent} client(s), pos=({position.x:F1}, {position.y:F1}, {position.z:F1}), " +
                    $"identité '{uniqueId}', définition=0x{message.DefinitionHash:x16}.");
            }

            if (announced > 0)
            {
                _log.LogInfo($"[net] apparition : {announced} entité(s) annoncée(s) ce balayage.");
            }

            // Les modules sont diffusés **avant** la détection de disparition : un module posé sur un
            // véhicule qui vient d'apparaître ce balayage doit être annoncé, et l'ensemble périmé d'un
            // véhicule détruit est oublié dans la boucle ci-dessous.
            BroadcastVehicleUpgrades();

            // Les clés connues qui ne sont plus dans le monde ont **disparu** : on les annonce (v11).
            // Sans ce balayage, un véhicule détruit chez l'hôte restait chez tous les clients, immobile.
            // Le premier passage ne peut rien annoncer : `_knownVehicles` vient d'être rempli.
            List<long> vanished = null;
            foreach (var known in _knownVehicles)
            {
                if (!present.Contains(known))
                {
                    (vanished ?? (vanished = new List<long>())).Add(known);
                }
            }

            if (vanished == null)
            {
                return;
            }

            foreach (var key in vanished)
            {
                _knownVehicles.Remove(key);
                _vehicleUpgrades.Remove(key);
                BroadcastDespawn(key, EntityKind.Vehicle, "véhicule détruit chez le serveur");
            }
        }
        private static float ReadVehicleEnergy(Vehicle vehicle)
        {
            // La conversion absolu -> normalisé vit dans `VehicleModules` (Shared), pour que le client
            // applique exactement la même échelle. La v14 la faisait ici, avec `EnergyMixin.charge` brut :
            // mesuré au run du 2026-10-20, un Seamoth y vaut `200`, donc la borne `> 1` transformait
            // toute batterie pleine en `1` et toute batterie à moitié en `1` aussi.
            return VehicleModules.ReadNormalized(vehicle);
        }
        private void BroadcastVehicleUpgrades()
        {
            if (_vehicleCache == null || _vehicleCache.Length == 0)
            {
                return;
            }

            foreach (var vehicle in _vehicleCache)
            {
                if (vehicle == null || vehicle.gameObject == null)
                {
                    continue;
                }

                var key = VehicleIdentity.KeyOf(vehicle, out _);
                if (key == 0L)
                {
                    // Même règle que pour la position : un véhicule sans identité partagée ne peut pas
                    // recevoir de modules, puisque le client ne saurait pas lesquels.
                    continue;
                }

                var types = VehicleModules.Read(vehicle);
                _vehicleUpgrades.TryGetValue(key, out var previous);
                if (previous != null && VehicleUpgradesMessage.SameSet(previous.TechTypes, types))
                {
                    continue;
                }

                var message = new VehicleUpgradesMessage
                {
                    VehicleKey = key,
                    TechTypes = types,
                };

                // Mémorisé **avant** l'envoi : un client qui se connecte pendant la diffusion doit
                // trouver l'ensemble dans le carnet, sinon il ne le recevrait jamais.
                _vehicleUpgrades[key] = message;

                if (previous == null && types.Length == 0)
                {
                    // Premier balayage d'un véhicule sans module : rien à annoncer, et l'annoncer à
                    // chaque démarrage userait le canal fiable pour un état vide qui est déjà le défaut.
                    continue;
                }

                var sent = 0;
                foreach (var pair in _sessions)
                {
                    if (!pair.Value.Accepted)
                    {
                        continue;
                    }

                    Send(pair.Key, message.Write, DeliveryMethod.ReliableOrdered);
                    sent++;
                }

                _vehicleUpgradesBroadcast++;

                var names = VehicleModules.Describe(types, out var count);
                _log.LogInfo(
                    $"[net] modules : clé=0x{key:x16} '{vehicle.gameObject.name}' — {count} module(s) " +
                    $"[{names}] {(previous == null ? "annoncés" : "mis à jour")} à {sent} client(s).");
            }
        }
        private void BroadcastVehicleSnapshots(long serverTick, Player hostPlayer)
        {
            // Un volant que plus personne n'envoie doit redevenir libre avant le calcul des diffusés :
            // sans ça, `conduits localement` resterait vrai chez les autres clients indéfiniment.
            ReleaseSilentDrivers();

            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_vehicleCache == null || now - _vehicleCacheRealtime >= VehicleCacheSeconds)
            {
                _vehicleCache = UnityEngine.Object.FindObjectsOfType<Vehicle>();
                _vehicleCacheRealtime = now;
                ReportVehicleIdentities();
                BroadcastNewVehicles();
            }

            if (_vehicleCache == null || _vehicleCache.Length == 0)
            {
                return;
            }

            var radiusSquared =
                Protocol.VehicleRelevancyRadiusMeters * Protocol.VehicleRelevancyRadiusMeters;

            foreach (var recipientPair in _sessions)
            {
                var recipientSession = recipientPair.Value;
                if (!recipientSession.Accepted)
                {
                    continue;
                }

                // Même règle que pour les créatures : le relevancy est celui du **destinataire**. Un
                // client ne peut suivre que les véhicules de son propre monde, et son monde n'a de
                // cellules chargées qu'autour de lui. Repli sur l'hôte tant qu'il n'a pas annoncé sa
                // position (juste après le handshake), pour qu'il reçoive quelque chose plutôt que rien.
                var interestX = recipientSession.PositionsReceived > 0 ? recipientSession.X : 0f;
                var interestY = recipientSession.PositionsReceived > 0 ? recipientSession.Y : 0f;
                var interestZ = recipientSession.PositionsReceived > 0 ? recipientSession.Z : 0f;
                var fromSession = recipientSession.PositionsReceived > 0;

                if (!fromSession && hostPlayer != null && hostPlayer.gameObject != null)
                {
                    var hostPosition = hostPlayer.transform.position;
                    interestX = hostPosition.x;
                    interestY = hostPosition.y;
                    interestZ = hostPosition.z;
                }

                _vehicleCandidates.Clear();
                for (var i = 0; i < _vehicleCache.Length; i++)
                {
                    var vehicle = _vehicleCache[i];
                    if (vehicle == null || vehicle.transform == null)
                    {
                        continue;
                    }

                    var position = vehicle.transform.position;
                    var dx = position.x - interestX;
                    var dy = position.y - interestY;
                    var dz = position.z - interestZ;
                    var distanceSquared = dx * dx + dy * dy + dz * dz;

                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    _vehicleCandidates.Add(new KeyValuePair<float, int>(distanceSquared, i));
                }

                if (_vehicleCandidates.Count == 0)
                {
                    continue;
                }

                // Les plus proches d'abord : si le plafond mord un jour, ce sont les véhicules du bout
                // du rayon qui sont écartés, pas celui devant le joueur.
                _vehicleCandidates.Sort((left, right) => left.Key.CompareTo(right.Key));

                _vehicleBuffer.Clear();
                var asUniqueId = 0;
                var asAnchor = 0;
                var withoutKey = 0;

                for (var i = 0; i < _vehicleCandidates.Count && i < Protocol.MaxVehicleStates; i++)
                {
                    var vehicle = _vehicleCache[_vehicleCandidates[i].Value];
                    var key = VehicleIdentity.KeyOf(vehicle, out var mechanism);
                    if (key == 0L)
                    {
                        withoutKey++;
                        _vehiclesWithoutKey++;
                        continue;
                    }

                    if (mechanism == VehicleIdentity.MechanismUniqueId)
                    {
                        asUniqueId++;
                    }
                    else
                    {
                        asAnchor++;
                    }

                    var transform = vehicle.transform;
                    var position = transform.position;
                    var rotation = transform.rotation;

                    _vehicleBuffer.Add(new VehicleStateEntry
                    {
                        Key = key,
                        Energy = ReadVehicleEnergy(vehicle),
                        X = position.x,
                        Y = position.y,
                        Z = position.z,
                        RotX = rotation.x,
                        RotY = rotation.y,
                        RotZ = rotation.z,
                        RotW = rotation.w,
                    });
                }

                if (_vehicleBuffer.Count == 0)
                {
                    if (withoutKey > 0 && now >= recipientSession.NextVehicleReport)
                    {
                        recipientSession.NextVehicleReport = now + VehicleStreamReportSeconds;
                        _log.LogWarning(
                            $"[net] véhicules : {withoutKey} véhicule(s) à moins de " +
                            $"{Protocol.VehicleRelevancyRadiusMeters:F0} m de " +
                            $"'{recipientSession.Username}'#{recipientSession.PlayerId} n'ont **aucune clé** — " +
                            "le protocole les a écartés plutôt que de les confondre avec un autre.");
                    }

                    continue;
                }

                var snapshot = new VehicleSnapshotMessage
                {
                    ServerTick = serverTick,
                    Vehicles = _vehicleBuffer.ToArray(),
                };

                _writer.Reset();
                snapshot.Write(_writer);
                if (_writer.Length > Protocol.MaxUnreliablePayloadBytes)
                {
                    _oversizedVehiclePackets++;
                    if (_oversizedVehiclePackets <= 3)
                    {
                        _log.LogError(
                            $"[net] paquet de véhicules REFUSÉ : {_writer.Length} octets pour " +
                            $"{_vehicleBuffer.Count} véhicule(s), au-dessus de la limite non fiable de " +
                            $"{Protocol.MaxUnreliablePayloadBytes} octets. LiteNetLib l'aurait jeté sans le dire " +
                            "dans nos journaux.");
                    }

                    continue;
                }

                recipientPair.Key.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.Unreliable);
                _vehicleSnapshotsBroadcast++;
                _vehicleStatesBroadcast += _vehicleBuffer.Count;
                recipientSession.VehicleBytesSent += _writer.Length;
                recipientSession.VehicleStatesSent += _vehicleBuffer.Count;
                recipientSession.VehiclePacketsSent++;

                if (now < recipientSession.NextVehicleReport)
                {
                    continue;
                }

                recipientSession.NextVehicleReport = now + VehicleStreamReportSeconds;

                var truncated = System.Math.Max(0, _vehicleCandidates.Count - Protocol.MaxVehicleStates);
                var bytesPerSecond = recipientSession.VehicleBytesSent / VehicleStreamReportSeconds;

                _log.LogInfo(
                    $"[net] véhicules : {_vehicleCache.Length} dans le monde, " +
                    $"{_vehicleCandidates.Count} à moins de {Protocol.VehicleRelevancyRadiusMeters:F0} m " +
                    $"du point ({interestX:F1}, {interestY:F1}, {interestZ:F1}) " +
                    $"[{(fromSession ? "position du client" : "repli position de l'hôte")}], " +
                    $"{_vehicleBuffer.Count} diffusé(s) à '{recipientSession.Username}'#{recipientSession.PlayerId} " +
                    $"({asUniqueId} par {VehicleIdentity.MechanismUniqueId}, {asAnchor} par " +
                    $"{VehicleIdentity.MechanismSpawnPosition}, {withoutKey} sans clé écarté(s))" +
                    (truncated > 0 ? $", {truncated} au-delà du plafond de {Protocol.MaxVehicleStates}" : string.Empty) +
                    $", débit={bytesPerSecond / 1024f:F1} Ko/s " +
                    $"({recipientSession.VehicleStatesSent} état(s) en {recipientSession.VehiclePacketsSent} paquet(s) " +
                    $"sur {VehicleStreamReportSeconds:F0} s), total envoyé={_vehicleSnapshotsBroadcast} snapshot(s) / " +
                    $"{_vehicleStatesBroadcast} état(s)" +
                    (_oversizedVehiclePackets == 0
                        ? string.Empty
                        : $", {_oversizedVehiclePackets} paquet(s) REFUSÉ(S) pour dépassement de taille"));

                recipientSession.VehicleBytesSent = 0;
                recipientSession.VehicleStatesSent = 0;
                recipientSession.VehiclePacketsSent = 0;
            }
        }
        private void ReportVehicleIdentities()
        {
            var cached = _vehicleCache ?? Array.Empty<Vehicle>();
            var byUniqueId = 0;
            var byAnchor = 0;
            var none = 0;
            var names = new List<string>();

            for (var i = 0; i < cached.Length; i++)
            {
                var vehicle = cached[i];
                if (vehicle == null)
                {
                    continue;
                }

                var key = VehicleIdentity.KeyOf(vehicle, out var mechanism);
                if (key == 0L || mechanism == VehicleIdentity.MechanismNone)
                {
                    none++;
                }
                else if (mechanism == VehicleIdentity.MechanismUniqueId)
                {
                    byUniqueId++;
                }
                else
                {
                    byAnchor++;
                }

                if (names.Count < 4)
                {
                    names.Add(VehicleIdentity.Describe(vehicle));
                }
            }

            var report =
                $"{cached.Length} véhicule(s) : {byUniqueId} par {VehicleIdentity.MechanismUniqueId}, " +
                $"{byAnchor} par {VehicleIdentity.MechanismSpawnPosition}, {none} sans clé" +
                (names.Count == 0 ? string.Empty : $" — {string.Join(" · ", names)}");

            if (cached.Length == 0)
            {
                report = "aucun véhicule dans le monde";
            }

            if (string.Equals(report, _lastVehicleIdentityReport, StringComparison.Ordinal))
            {
                return;
            }

            _lastVehicleIdentityReport = report;
            _log.LogInfo($"[net] identités de véhicules : {report}");
        }
        private void ReportVehicles(Player hostPlayer, float now)
        {
            if (now < _nextVehicleDiagnostic)
            {
                return;
            }

            _nextVehicleDiagnostic = now + VehicleReportSeconds;

            var vehicles = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            if (vehicles == null || vehicles.Length == 0)
            {
                _log.LogInfo(
                    "[net] véhicules : aucun dans le monde — M4 (Seamoth autoritaire) n'a donc **rien à mesurer** " +
                    "dans cette sauvegarde : le flux `VehicleSnapshot` n'enverra rien tant qu'un véhicule n'y " +
                    "apparaît pas (cadence et interpolation sont tranchées par la mesure du §30, appliquées au §33).");
                return;
            }

            // Les trois plus proches de l'hôte, nommées et situées : c'est assez pour savoir si M4 a de quoi
            // mesurer. Leur **vitesse** n'est pas relevée ici, et c'est délibéré : un véhicule immobile
            // donnerait `0 m/s` — un chiffre qui n'apprend rien et qu'on prendrait pour une mesure. Elle
            // viendra d'un état de véhicule, le jour où il y en aura un à suivre.
            var nearest = vehicles
                .Where(vehicle => vehicle != null && vehicle.transform != null)
                .Select(vehicle => new
                {
                    Name = vehicle.name,
                    Distance = hostPlayer != null && hostPlayer.transform != null
                        ? UnityEngine.Vector3.Distance(hostPlayer.transform.position, vehicle.transform.position)
                        : 0f,
                })
                .OrderBy(entry => entry.Distance)
                .Take(3)
                .Select(entry =>
                    $"'{entry.Name}' à {entry.Distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} m");

            _log.LogInfo(
                $"[net] véhicules : {vehicles.Length} dans le monde, les plus proches de l'hôte : " +
                $"{string.Join(", ", nearest)}. Un véhicule à 20 m/s balaie 2 m par annonce à 10 Hz : c'est le " +
                "pas que l'interpolation cliente doit absorber (mesuré au §33 : 0,27 m par image au maximum).");
        }
    }
}
