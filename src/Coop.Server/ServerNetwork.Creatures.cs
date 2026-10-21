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
        /// <summary>
        /// `LiveMixin` de chaque créature mise en cache, et sa fraction de vie relue à chaque diffusion
        /// (v17). Les deux tableaux suivent <see cref="ServerNetwork._creatureCache"/> : un `GetComponent`
        /// par créature et par tick serait payé pour rien, et une créature sans `LiveMixin` serait
        /// recherchée indéfiniment.
        /// </summary>
        private LiveMixin[] _creatureMixins;

        private float[] _creatureHealth;

        private bool[] _creatureHealthProbed;

        /// <summary>
        /// Clés dont la mort a déjà été annoncée. Sans ce registre, un cadavre encore présent dans la
        /// scène serait annoncé à chaque diffusion (dix fois par seconde).
        /// </summary>
        private readonly HashSet<long> _announcedCreatureDeaths = new HashSet<long>();

        private readonly Queue<long> _announcedCreatureDeathOrder = new Queue<long>();

        private const int MaxRememberedCreatureDeaths = 512;

        private long _creatureDeathsAnnounced;

        private void BroadcastCreatureSnapshots(long serverTick, Player hostPlayer)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_creatureCache == null || now - _creatureCacheRealtime >= CreatureCacheSeconds)
            {
                _creatureCache = UnityEngine.Object.FindObjectsOfType<Creature>();
                _creatureCacheRealtime = now;
                RefreshCreatureHealth();
                ReportCreatureIdentities();
            }
            else
            {
                // La santé change sans que la liste des créatures change : elle se relit donc à chaque
                // diffusion, alors que les `LiveMixin` sont résolus une seule fois par cache.
                ReadCreatureHealth();
            }

            AnnounceCreatureDeaths();

            if (_creatureCache == null || _creatureCache.Length == 0)
            {
                return;
            }

            var radiusSquared = Protocol.CreatureRelevancyRadiusMeters * Protocol.CreatureRelevancyRadiusMeters;

            // Diagnostic : « 0 créature diffusée » a deux causes très différentes — le monde est vide
            // autour du joueur, ou le rayon est trop petit. Sans cette mesure, les deux se
            // ressemblent, et on réglerait un rayon au hasard.
            ReportVehicles(hostPlayer, now);

            if (now >= _nextCreatureDiagnostic)
            {
                _nextCreatureDiagnostic = now + 5f;
                var nearest = NearestCreatures(hostPlayer, 3);
                _log.LogInfo(
                    nearest.Count == 0
                        ? $"[net] créatures : {_creatureCache.Length} dans le monde, aucune position lisible"
                        : $"[net] créatures : {_creatureCache.Length} dans le monde, les plus proches du point " +
                          $"{nearest[0].Reference.ToString("F1")} : " +
                          $"{string.Join(", ", nearest.Select(entry => $"'{entry.Name}' à {entry.Meters:F1} m"))} " +
                          $"(rayon de diffusion {Protocol.CreatureRelevancyRadiusMeters:F0} m)");
            }

            foreach (var recipientPair in _sessions)
            {
                var recipientSession = recipientPair.Value;
                if (!recipientSession.Accepted)
                {
                    continue;
                }

                _creatureBuffer.Clear();
                _creatureCandidates.Clear();

                // Le relevancy est celui du **destinataire**, pas celui de l'hôte : un client ne peut
                // suivre que les créatures qui existent dans son propre monde, et son monde n'a de
                // cellules chargées qu'autour de son joueur à lui.
                //
                // La première version prenait la plus petite distance à l'un ou l'autre (client ou
                // hôte) : mesuré, cela ne diffusait que des créatures proches de l'hôte, dont le monde
                // du client n'avait qu'une poignée — 128 appariements sur 36 452 états, et un avatar de
                // créature adopté puis relâché aussitôt. Le repli sur la position de l'hôte ne sert
                // donc plus qu'à un client qui n'a pas encore annoncé la sienne (juste après le
                // handshake), pour qu'il reçoive quelque chose plutôt que rien.
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

                for (var i = 0; i < _creatureCache.Length; i++)
                {
                    var creature = _creatureCache[i];
                    if (creature == null || creature.transform == null)
                    {
                        continue;
                    }

                    // Une créature morte n'est plus diffusée : sa disparition a été annoncée, et
                    // continuer à envoyer sa position ferait réapparaître le cadavre chez un client
                    // qui l'a déjà retiré.
                    if (IsCreatureDead(i))
                    {
                        continue;
                    }

                    var position = creature.transform.position;
                    var dx = position.x - interestX;
                    var dy = position.y - interestY;
                    var dz = position.z - interestZ;
                    var distanceSquared = dx * dx + dy * dy + dz * dz;

                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    _creatureCandidates.Add(new KeyValuePair<float, int>(distanceSquared, i));
                }

                if (_creatureCandidates.Count == 0)
                {
                    continue;
                }

                // Les plus proches d'abord : quand le plafond mord, ce sont les créatures du bout du
                // rayon qui sont sacrifiées, pas celles devant le joueur.
                _creatureCandidates.Sort((left, right) => left.Key.CompareTo(right.Key));

                var asKeyed = 0;
                var asSpawn = 0;
                var refused = 0;
                var sentChunks = 0;
                var serverMetres = 0f;
                var serverSeconds = 0f;
                var maxServerMovedMeters = 0f;
                var maxServerMovedSeconds = 0f;
                var maxServerMovedLabel = "aucune";

                // Jusqu'où la diffusion est allée dans la liste (triée du plus proche au plus loin) : la
                // différence avec le nombre de candidates est le nombre de créatures **sacrifiées au
                // plafond**, c'est-à-dire jamais envoyées — leur IA continue de tourner chez le client.
                var reached = 0;

                // Un paquet ne peut pas porter plus de `MaxCreatureStates` créatures : la liste part
                // donc en plusieurs paquets, sinon la queue du rayon n'est jamais diffusée et les
                // créatures déjà suivies sortent de la liste puis y reviennent (IA coupée/rétablie en
                // boucle, mesuré une fois par seconde avant ce découpage).
                for (var chunk = 0; chunk < CreatureChunksPerTick; chunk++)
                {
                    var start = chunk * Protocol.MaxCreatureStates;
                    if (start >= _creatureCandidates.Count)
                    {
                        break;
                    }

                    _creatureBuffer.Clear();
                    var end = System.Math.Min(start + Protocol.MaxCreatureStates, _creatureCandidates.Count);

                    for (var i = start; i < end; i++)
                    {
                        var creature = _creatureCache[_creatureCandidates[i].Value];
                        var key = CreatureIdentity.KeyOf(creature, out var mechanism);
                        if (key == 0L)
                        {
                            // Sans clé, le client ne pourrait pas la reconnaître : la diffuser ferait
                            // correspondre n'importe quoi. Elle est comptée, jamais inventée.
                            refused++;
                            continue;
                        }

                        if (mechanism == CreatureIdentity.MechanismUniqueId)
                        {
                            asKeyed++;
                        }
                        else
                        {
                            asSpawn++;
                        }

                        var transform = creature.transform;
                        var position = transform.position;
                        var rotation = transform.rotation;

                        // Déplacement du serveur depuis la dernière diffusion de **cette** créature. Le rythme
                        // réel est mesuré, pas supposé : la diffusion passe tous les
                        // `CreatureStreamIntervalTicks` ticks, mais un paquet non fiable peut manquer.
                        if (_lastSentPositions.TryGetValue(key, out var previous))
                        {
                            var seconds = now - previous.Realtime;
                            if (seconds > 0.001f)
                            {
                                var metres = UnityEngine.Vector3.Distance(previous.Position, position);
                                serverMetres = metres;
                                serverSeconds = seconds;
                                if (metres > maxServerMovedMeters)
                                {
                                    maxServerMovedMeters = metres;
                                    maxServerMovedSeconds = seconds;
                                    maxServerMovedLabel = creature.gameObject != null ? creature.gameObject.name : "?";
                                }
                            }
                        }

                        _lastSentPositions[key] = new SentPosition { Position = position, Realtime = now };

                        _creatureBuffer.Add(new CreatureStateEntry
                        {
                            Key = key,
                            X = position.x,
                            Y = position.y,
                            Z = position.z,
                            RotX = rotation.x,
                            RotY = rotation.y,
                            RotZ = rotation.z,
                            RotW = rotation.w,
                            Health = _creatureHealth != null && _creatureHealth.Length == _creatureCache.Length
                                ? _creatureHealth[_creatureCandidates[i].Value]
                                : -1f,
                        });
                    }

                    if (_creatureBuffer.Count == 0)
                    {
                        continue;
                    }

                    var snapshot = new CreatureSnapshotMessage
                    {
                        ServerTick = serverTick,
                        Creatures = _creatureBuffer.ToArray(),
                    };

                    _writer.Reset();
                    snapshot.Write(_writer);
                    if (_writer.Length > Protocol.MaxUnreliablePayloadBytes)
                    {
                        _oversizedCreaturePackets++;
                        if (_oversizedCreaturePackets <= 3)
                        {
                            _log.LogError(
                                $"[net] paquet de créatures REFUSÉ : {_writer.Length} octets pour " +
                                $"{_creatureBuffer.Count} créature(s), au-dessus de la limite non fiable de " +
                                $"{Protocol.MaxUnreliablePayloadBytes} octets. LiteNetLib l'aurait jeté sans le dire " +
                                "dans nos journaux.");
                        }

                        break;
                    }

                    recipientPair.Key.Send(_writer.BackingBuffer, 0, _writer.Length, DeliveryMethod.Unreliable);
                    _creatureSnapshotsBroadcast++;
                    _creatureStatesBroadcast += _creatureBuffer.Count;
                    recipientSession.CreatureBytesSent += _writer.Length;
                    recipientSession.CreatureStatesSent += _creatureBuffer.Count;
                    recipientSession.CreaturePacketsSent++;
                    reached = end;
                    sentChunks++;
                }

                if (sentChunks == 0)
                {
                    continue;
                }

                if (now < recipientSession.NextCreatureReport)
                {
                    continue;
                }

                recipientSession.NextCreatureReport = now + CreatureReportSeconds;

                // Clés partagées parmi les candidates : l'identité est-elle injective ? Un seul
                // contre-exemple suffit à dire que « une clé = une créature » ne tient pas, et c'est
                // l'hypothèse sur laquelle repose tout le suivi (cf. PLAN.md §28).
                _creatureKeyTally.Clear();
                var sharedKeys = 0;
                for (var i = 0; i < _creatureCandidates.Count; i++)
                {
                    var candidate = _creatureCache[_creatureCandidates[i].Value];
                    var candidateKey = CreatureIdentity.KeyOf(candidate, out _);
                    if (candidateKey == 0L)
                    {
                        continue;
                    }

                    _creatureKeyTally.TryGetValue(candidateKey, out var already);
                    if (already > 0)
                    {
                        sharedKeys++;
                    }

                    _creatureKeyTally[candidateKey] = already + 1;
                }

                var sacrificed = _creatureCandidates.Count - reached;

                // La liste est triée du plus proche au plus loin, donc la première sacrifiée est la plus
                // proche de toutes — c'est elle qui dit si le plafond mord dans ce que le joueur voit.
                var closestSacrificed = sacrificed > 0
                    ? (float)System.Math.Sqrt(_creatureCandidates[reached].Key)
                    : 0f;

                var bytesPerSecond = recipientSession.CreatureBytesSent / CreatureReportSeconds;

                _log.LogInfo(
                    $"[net] créatures : {_creatureCache.Length} dans le monde, " +
                    $"{_creatureCandidates.Count} à moins de {Protocol.CreatureRelevancyRadiusMeters:F0} m " +
                    // Le point d'intérêt est écrit parce que deux destinataires qui reçoivent des ensembles
                    // différents ne disent rien tant qu'on ne sait pas **d'où** le rayon est compté :
                    // position du client, ou repli sur l'hôte quand il n'en a pas encore annoncé.
                    $"du point ({interestX:F1}, {interestY:F1}, {interestZ:F1}) " +
                    $"[{(fromSession ? "position du client" : "repli position de l'hôte")}], " +
                    $"{asSpawn + asKeyed} diffusée(s) en {sentChunks} paquet(s) à " +
                    $"'{recipientSession.Username}'#{recipientSession.PlayerId} " +
                    $"({asSpawn} par {CreatureIdentity.MechanismSpawnPosition}, " +
                    $"{asKeyed} par {CreatureIdentity.MechanismUniqueId}, {refused} sans clé écartée(s)), " +
                    $"{sacrificed} sacrifiée(s) au plafond de " +
                    $"{CreatureChunksPerTick * Protocol.MaxCreatureStates}" +
                    (CreatureChunksOverridden ? " (plafond ABAISSÉ pour éprouver la garde)" : string.Empty) +
                    (sacrificed > 0 ? $" (la plus proche à {closestSacrificed:F0} m)" : string.Empty) + ", " +
                    $"clés partagées={sharedKeys} (sur {_creatureCandidates.Count} candidates), " +
                    // « non mesuré » et « 0 m » ne sont pas la même chose : le maximum vaut 0 quand aucune clé
                    // n'a été diffusée deux fois dans la fenêtre (un destinataire qui vient de se connecter),
                    // et l'annoncer comme « 0 m » ferait croire à un serveur immobile qui n'a pas été observé.
                    (maxServerMovedSeconds > 0f
                        ? $"déplacement serveur max={maxServerMovedMeters:F2} m en {maxServerMovedSeconds:F2} s " +
                          $"({maxServerMovedMeters / maxServerMovedSeconds:F1} m/s, '{maxServerMovedLabel}'), "
                        : "déplacement serveur : non mesuré (aucune clé diffusée deux fois dans la fenêtre), ") +
                    $"débit={bytesPerSecond / 1024f:F1} Ko/s " +
                    $"({recipientSession.CreatureStatesSent} état(s) en {recipientSession.CreaturePacketsSent} paquet(s) " +
                    $"sur {CreatureReportSeconds:F0} s), " +
                    $"total envoyé={_creatureSnapshotsBroadcast} snapshot(s) / {_creatureStatesBroadcast} état(s)" +
                    (_oversizedCreaturePackets == 0
                        ? string.Empty
                        : $", {_oversizedCreaturePackets} paquet(s) REFUSÉ(S) pour dépassement de taille"));

                // Le sacrifice est assumé : ce sont les créatures du **bout** du rayon (le tri est du plus
                // proche au plus loin), dont l'IA continue de tourner chez le client. Mais si le plafond
                // mord dans une créature que le joueur a sous les yeux, ce n'est plus le même compromis —
                // et c'est exactement le genre de bascule qui ne doit pas se produire en silence.
                if (sacrificed > 0 && closestSacrificed < CloseSacrificeMeters && _closeSacrificeWarnings < 3)
                {
                    _closeSacrificeWarnings++;
                    _log.LogWarning(
                        $"[net] créatures : une créature à {closestSacrificed:F0} m n'est PAS diffusée à " +
                        $"'{recipientSession.Username}'#{recipientSession.PlayerId} — le plafond de " +
                        $"{CreatureChunksPerTick * Protocol.MaxCreatureStates} est atteint alors que " +
                        $"{_creatureCandidates.Count} créatures sont dans le rayon. Le compromis « on ne suit que " +
                        "le proche » ne tient plus : il faut changer le plafond ou le rayon.");
                }

                recipientSession.CreatureBytesSent = 0;
                recipientSession.CreatureStatesSent = 0;
                recipientSession.CreaturePacketsSent = 0;
            }
        }
        /// <summary>
        /// Résout les `LiveMixin` des créatures mises en cache et lit leur première fraction de vie.
        /// Appelé au (re)chargement du cache, pas à chaque tick : un `GetComponent` par créature et par
        /// diffusion serait payé dix fois par seconde pour un composant qui ne change pas.
        /// </summary>
        private void RefreshCreatureHealth()
        {
            var count = _creatureCache != null ? _creatureCache.Length : 0;
            if (_creatureMixins == null || _creatureMixins.Length != count)
            {
                _creatureMixins = new LiveMixin[count];
                _creatureHealth = new float[count];
                _creatureHealthProbed = new bool[count];
            }

            for (var i = 0; i < count; i++)
            {
                _creatureMixins[i] = null;
                _creatureHealthProbed[i] = false;
            }

            ReadCreatureHealth();
        }

        /// <summary>Relit la fraction de vie de chaque créature du cache (`-1` = pas de `LiveMixin`).</summary>
        private void ReadCreatureHealth()
        {
            if (_creatureCache == null || _creatureHealth == null || _creatureHealth.Length != _creatureCache.Length)
            {
                return;
            }

            for (var i = 0; i < _creatureCache.Length; i++)
            {
                var creature = _creatureCache[i];
                if (creature == null)
                {
                    _creatureHealth[i] = -1f;
                    continue;
                }

                if (!_creatureHealthProbed[i])
                {
                    _creatureMixins[i] = creature.GetComponent<LiveMixin>()
                                        ?? creature.GetComponentInChildren<LiveMixin>();
                    _creatureHealthProbed[i] = true;
                }

                var mixin = _creatureMixins[i];
                if (mixin == null || mixin.maxHealth <= 0f)
                {
                    _creatureHealth[i] = -1f;
                    continue;
                }

                var fraction = mixin.health / mixin.maxHealth;
                _creatureHealth[i] = fraction < 0f ? 0f : (fraction > 1f ? 1f : fraction);
            }
        }

        private bool IsCreatureDead(int index)
        {
            return _creatureHealth != null
                   && index >= 0
                   && index < _creatureHealth.Length
                   && _creatureHealth[index] == 0f;
        }

        /// <summary>
        /// Annonce une seule fois la mort de chaque créature dont la santé est tombée à zéro (v17).
        ///
        /// Pourquoi le serveur seul : le client applique la fraction **sans tuer** sa copie, donc sans
        /// cette annonce une créature morte resterait debout chez tout le monde, et une créature détruite
        /// par le jeu serait « relâchée » puis reprise par son IA locale — un mort qui nage.
        /// </summary>
        private void AnnounceCreatureDeaths()
        {
            if (_creatureCache == null || _creatureHealth == null)
            {
                return;
            }

            for (var i = 0; i < _creatureCache.Length; i++)
            {
                if (!IsCreatureDead(i))
                {
                    continue;
                }

                var creature = _creatureCache[i];
                if (creature == null)
                {
                    continue;
                }

                var key = CreatureIdentity.KeyOf(creature, out _);
                if (key == 0L || !_announcedCreatureDeaths.Add(key))
                {
                    continue;
                }

                _announcedCreatureDeathOrder.Enqueue(key);
                while (_announcedCreatureDeathOrder.Count > MaxRememberedCreatureDeaths)
                {
                    _announcedCreatureDeaths.Remove(_announcedCreatureDeathOrder.Dequeue());
                }

                _creatureDeathsAnnounced++;
                BroadcastDespawn(key, EntityKind.Creature, "créature morte (santé 0)");

                if (_creatureDeathsAnnounced <= 10)
                {
                    _log.LogInfo(
                        $"[net] créature morte : clé=0x{key:x16} '{creature.gameObject?.name}' — " +
                        $"{_creatureDeathsAnnounced} mort(s) annoncée(s) depuis le démarrage.");
                }
            }
        }

        private void ReportCreatureIdentities()
        {
            var cached = _creatureCache ?? Array.Empty<Creature>();
            var byUniqueId = 0;
            var bySpawn = 0;
            var none = 0;
            var withoutKeyNames = new List<string>();

            for (var i = 0; i < cached.Length; i++)
            {
                var creature = cached[i];
                if (creature == null)
                {
                    continue;
                }

                CreatureIdentity.KeyOf(creature, out var mechanism);
                if (mechanism == CreatureIdentity.MechanismUniqueId)
                {
                    byUniqueId++;
                }
                else if (mechanism == CreatureIdentity.MechanismSpawnPosition)
                {
                    bySpawn++;
                }
                else
                {
                    none++;
                    if (withoutKeyNames.Count < 5 && creature.gameObject != null)
                    {
                        withoutKeyNames.Add(creature.gameObject.name);
                    }
                }
            }

            var report =
                $"{cached.Length} créature(s) : {bySpawn} par {CreatureIdentity.MechanismSpawnPosition}, " +
                $"{byUniqueId} par {CreatureIdentity.MechanismUniqueId}, {none} sans clé" +
                (withoutKeyNames.Count == 0
                    ? string.Empty
                    : $" (ex. : {string.Join(", ", withoutKeyNames)})");

            if (string.Equals(report, _lastCreatureIdentityReport, StringComparison.Ordinal))
            {
                return;
            }

            _lastCreatureIdentityReport = report;
            _log.LogInfo($"[net] identités de créatures : {report}");
        }
    }
}
