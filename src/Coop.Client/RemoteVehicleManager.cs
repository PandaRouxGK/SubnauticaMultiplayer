using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Fait suivre au client les véhicules que le serveur déplace (M4), et **interpole** leur position
    /// entre deux annonces.
    ///
    /// Pourquoi l'interpolation n'est pas un raffinement : la mesure du §30 a relevé qu'un Seamoth à
    /// `20 m/s` produit, à `10 Hz`, des pas de **2,15 m en moyenne** (tous dans la bande `≥ 2 m`). Appliquer
    /// ces positions telles quelles donne un objet qui saute de deux mètres dix fois par seconde. Entre
    /// deux annonces, le client glisse donc de la pose courante vers la pose annoncée sur la durée
    /// **réellement mesurée** entre les deux annonces — à 60 fps, cela fait ~0,33 m par image.
    ///
    /// Ce que le gestionnaire fait, et pourquoi :
    ///
    /// - **identifie par clé**, pas par nom : deux Seamoth de la même sauvegarde portent le même nom de
    ///   prefab. La clé est calculée des deux côtés par <see cref="VehicleIdentity"/>, compilé partagé.
    /// - **gèle la physique du véhicule suivi** (`Rigidbody.isKinematic`) : sinon la flottabilité et la
    ///   traînée du jeu déplacent la copie entre deux annonces, et l'interpolation se battrait contre
    ///   PhysX. L'état d'origine est mémorisé et restauré au relâchement.
    /// - **laisse la main au joueur local** : un véhicule conduit par ce client n'est ni déplacé ni gelé.
    ///   M4 ne transporte que la position ; l'autorité de conduite n'est pas dans ce jalon, et geler le
    ///   véhicule sous le joueur serait pire que l'absence de synchronisation.
    /// - **relâche** un véhicule dont l'état n'arrive plus (sortie du rayon, serveur arrêté) : la
    ///   simulation locale reprend, plutôt qu'un Seamoth figé sur une position périmée.
    ///
    /// Ce qu'il **refuse** de faire : deviner une clé inconnue. Une clé absente du registre est comptée
    /// et nommée. La seule exception est nommée au journal et bornée : quand la clé de repli (position
    /// d'apparition) a été calculée à un autre instant côté client, le véhicule annoncé est rattaché au
    /// seul véhicule local **du même type** à moins de `UnknownKeyBindingMeters` de la position annoncée.
    /// </summary>
    internal sealed class RemoteVehicleManager
    {
        internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Véhicule");

        /// <summary>
        /// Distance (m) au-delà de laquelle un écart avant lissage est **signalé** — il n'est pas refusé :
        /// un écart de quelques mètres sur une annonce peut être un vrai rattrapage après une perte de
        /// paquet, et le journal doit le montrer plutôt que le lisser en silence.
        /// </summary>
        private const float DriftAnomalyMeters = 5f;

        /// <summary>
        /// Rayon (m) de rattachement d'une clé inconnue, **nommé dans le journal**.
        ///
        /// Le cas qu'il couvre est celui d'un véhicule sans `UniqueIdentifier` : la clé de repli dépend
        /// de l'instant où chaque instance l'a vu pour la première fois. Un client arrivé en cours de
        /// route calcule donc une autre ancre, et la clé ne correspond plus. Sans ce rattachement, un
        /// Seamoth sans identifiant ne serait tout simplement jamais synchronisé. Le rayon est petit
        /// (30 m) et le type doit correspondre : deux véhicules du même type à moins de 30 m l'un de
        /// l'autre, dont un seul non suivi, désignent sans ambiguïté.
        /// </summary>
        private const float UnknownKeyBindingMeters = 30f;

        /// <summary>Intervalle (s) de reconstruction du registre local des clés.</summary>
        private const float RegistryRefreshSeconds = 2f;

        /// <summary>Intervalle (s) entre deux reconstructions imposées par une clé inconnue.</summary>
        private const float ForcedRebuildSeconds = 1f;

        /// <summary>
        /// Durée maximale (s) d'un segment d'interpolation. Une annonce manquante allonge l'intervalle
        /// mesuré : au-delà de cette borne, mieux vaut un rattrapage court qu'un glissement de plusieurs
        /// secondes qui laisserait le véhicule en retard sur le serveur.
        /// </summary>
        private const float MaxSegmentSeconds = VehicleInterpolation.MaxSegmentSeconds;

        /// <summary>
        /// Vitesse (m/s) au-delà de laquelle un segment est **allongé** pour éviter un saut d'image.
        ///
        /// La durée d'un segment est l'intervalle mesuré entre deux annonces — sauf quand les annonces
        /// arrivent **groupées** (une frame bloquée par le chargement d'assets les fait toutes livrer
        /// d'un coup, avec un intervalle mesuré presque nul). Dans ce cas, `durée = distance / 40 m/s` :
        /// un rattrapage de 21 m est étalé sur 0,5 s (~0,3 m par image à 143 fps) au lieu de se faire en
        /// une image. Mesuré sans cette borne : `pas par image max=21.11 m` alors que la fenêtre de sonde
        /// (12 s propres) n'en voyait aucun — c'est exactement le pic que la borne supprime.
        ///
        /// 40 m/s est le double de la vitesse d'un Seamoth ; une borne plus basse ajouterait du retard sur
        /// un mouvement normal, une borne plus haute laisserait repasser un saut visible.
        /// </summary>
        private const float MaxCatchUpSpeedMetersPerSecond = VehicleInterpolation.MaxCatchUpSpeedMetersPerSecond;

        /// <summary>Durée minimale (s) d'un segment : évite une division par zéro sur deux états rapprochés.</summary>
        private const float MinSegmentSeconds = VehicleInterpolation.MinSegmentSeconds;

        /// <summary>
        /// Intervalle (s) au-delà duquel deux états ne se suivent plus : le véhicule a quitté le flux puis
        /// y est revenu. Un écart mesuré sur une interruption ne dit rien de la synchronisation.
        /// </summary>
        private const float ConsecutiveStateGapSeconds = 0.5f;

        private sealed class Tracked
        {
            public long Key;
            public string Label;
            public Vehicle Vehicle;

            public Vector3 From;
            public Vector3 To;
            public Quaternion FromRotation;
            public Quaternion ToRotation;
            public float SegmentStart;
            public float SegmentDuration;

            public float LastStateRealtime;
            public long StatesReceived;

            /// <summary>Écart mesuré **avant** lissage, sur deux états consécutifs.</summary>
            public float MaxDriftMeters;

            public Rigidbody Body;
            public bool BodyWasKinematic;
            public bool BodyForcedKinematic;

            public bool LocallyDrivenLogged;
        }

        private readonly Dictionary<long, Tracked> _tracked = new Dictionary<long, Tracked>();
        private readonly Dictionary<long, Vehicle> _registry = new Dictionary<long, Vehicle>();
        private readonly ManualLogSource _log;

        private float _registryRealtime = float.NegativeInfinity;
        private float _forcedRegistryRealtime = float.NegativeInfinity;
        private float _nextReport;

        private long _snapshotsReceived;
        private long _statesReceived;
        private long _statesMatched;
        private long _statesUnknownKey;
        private long _bindingsByProximity;
        private long _releases;
        private long _locallyDrivenSkips;

        // — Modules installés (v15, M4) ————————————————————————————————————————
        //
        // Le dernier ensemble annoncé par véhicule est **mémorisé même si le véhicule n'est pas encore
        // suivi** : l'annonce de modules peut précéder la première position (elle est fiable, la position
        // ne l'est pas), et la jeter rendrait la copie divergente jusqu'à la prochaine installation.
        private readonly Dictionary<long, ushort[]> _pendingUpgrades = new Dictionary<long, ushort[]>();

        private long _upgradesMessages;
        private long _upgradesDivergences;
        private string _lastUpgradesKey = "aucun";
        private string _lastUpgradesServer = "aucun";
        private string _lastUpgradesLocal = "aucun";

        /// <summary>Dernier ensemble de modules reçu du serveur, pour la sonde (v15).</summary>
        public string LastUpgradesServer => _lastUpgradesServer;

        /// <summary>Ensemble lu localement sur le même véhicule au moment de la comparaison.</summary>
        public string LastUpgradesLocal => _lastUpgradesLocal;

        /// <summary>Charges écrites sur une copie locale (v14) : 0 significa « reçues mais identiques ».</summary>
        public long EnergiesApplied => _energiesApplied;

        public long UpgradesMessages => _upgradesMessages;
        public long UpgradesDivergences => _upgradesDivergences;

        // Mesure par image : c'est elle qui prouve que le saut de 2,15 m a disparu.
        private long _frameSteps;
        private long _framesObserved;
        private double _frameStepSum;
        private float _maxFrameStepMeters;
        private long _frameStepsAboveOneMeter;

        /// <summary>
        /// Nombre de pas par image déjà signalés comme plus rapides que le plafond de rattrapage. Borné :
        /// un défaut qui se répète ne doit pas noyer le journal, mais la première occurrence doit être
        /// lisible avec ses valeurs (durée du segment, alpha, trajet) pour être corrigée, pas seulement
        /// constatée.
        /// </summary>
        private int _fastStepWarnings;

        private float _measureStartRealtime = -1f;
        private float _measureSeconds;
        private float _maxDriftMeters;
        private string _maxDriftLabel = "aucun";

        public RemoteVehicleManager(ManualLogSource log)
        {
            _log = log;
        }

        public int TrackedCount => _tracked.Count;

        /// <summary>Véhicule suivi le plus proche du joueur local, ou <c>null</c> — pour les sondes.</summary>
        public Vehicle NearestTrackedVehicle
        {
            get
            {
                Vehicle nearest = null;
                var best = float.MaxValue;
                var origin = Player.main != null && Player.mainObject != null
                    ? Player.mainObject.transform.position
                    : Vector3.zero;

                foreach (var entry in _tracked.Values)
                {
                    if (entry.Vehicle == null)
                    {
                        continue;
                    }

                    var distance = Vector3.Distance(origin, entry.Vehicle.transform.position);
                    if (distance < best)
                    {
                        best = distance;
                        nearest = entry.Vehicle;
                    }
                }

                return nearest;
            }
        }

        /// <summary>
        /// Reçoit l'ensemble de modules installés sur un véhicule (v15, M4).
        ///
        /// Ce n'est pas un état périodique : le serveur ne l'envoie que lorsque l'ensemble change (ou à
        /// la connexion). Le client compare, journalise la divergence **nommée**, puis demande
        /// l'application locale — le prefab d'un module se charge de façon asynchrone, donc via
        /// <see cref="VehicleModuleApplier"/>.
        /// </summary>
        public int OnUpgrades(VehicleUpgradesMessage message)
        {
            if (message == null || message.VehicleKey == 0L)
            {
                return 0;
            }

            _upgradesMessages++;
            var serverTypes = message.TechTypes ?? Array.Empty<ushort>();
            _pendingUpgrades[message.VehicleKey] = serverTypes;

            if (!_tracked.TryGetValue(message.VehicleKey, out var entry) || entry.Vehicle == null)
            {
                VehicleModules.Describe(serverTypes, out var pendingCount);
                _log.LogInfo(
                    $"[véhicule] modules : clé=0x{message.VehicleKey:x16} {pendingCount} module(s) reçus " +
                    "avant le suivi — mémorisés pour la prochaine adoption.");
                return 0;
            }

            return ApplyUpgrades(entry, serverTypes);
        }

        /// <summary>
        /// Compare l'ensemble annoncé à celui lu localement, puis demande les ajouts et retraits manquants.
        /// Rend le nombre d'écarts traités (0 si les deux mondes sont d'accord).
        /// </summary>
        private int ApplyUpgrades(Tracked entry, ushort[] serverTypes)
        {
            var localTypes = VehicleModules.Read(entry.Vehicle);
            _lastUpgradesKey = entry.Label;
            _lastUpgradesServer = VehicleModules.Describe(serverTypes, out var serverCount);
            _lastUpgradesLocal = VehicleModules.Describe(localTypes, out var localCount);

            if (VehicleUpgradesMessage.SameSet(serverTypes, localTypes))
            {
                _pendingUpgrades.Remove(entry.Key);
                return 0;
            }

            _upgradesDivergences++;

            var changes = 0;
            for (var i = 0; i < serverTypes.Length; i++)
            {
                if (Array.IndexOf(localTypes, serverTypes[i]) >= 0)
                {
                    continue;
                }

                VehicleModuleApplier.RequestAdd(entry.Vehicle, serverTypes[i]);
                changes++;
            }

            for (var i = 0; i < localTypes.Length; i++)
            {
                if (Array.IndexOf(serverTypes, localTypes[i]) >= 0)
                {
                    continue;
                }

                VehicleModuleApplier.RequestRemove(entry.Vehicle, localTypes[i]);
                changes++;
            }

            _log.LogInfo(
                $"[véhicule] modules : {entry.Label} — serveur=[{_lastUpgradesServer}] ({serverCount}) " +
                $"local=[{_lastUpgradesLocal}] ({localCount}) -> {changes} écart(s) demandé(s) au client.");

            _pendingUpgrades.Remove(entry.Key);
            return changes;
        }

        /// <summary>Applique les ensembles mémorisés aux véhicules qui viennent d'être adoptés.</summary>
        private void FlushPendingUpgrades()
        {
            if (_pendingUpgrades.Count == 0)
            {
                return;
            }

            List<long> resolved = null;
            foreach (var pair in _pendingUpgrades)
            {
                if (!_tracked.TryGetValue(pair.Key, out var entry) || entry.Vehicle == null)
                {
                    continue;
                }

                (resolved ?? (resolved = new List<long>())).Add(pair.Key);
                ApplyUpgrades(entry, pair.Value);
            }

            if (resolved == null)
            {
                return;
            }

            for (var i = 0; i < resolved.Count; i++)
            {
                _pendingUpgrades.Remove(resolved[i]);
            }
        }

        /// <summary>Reçoit une annonce de véhicules : apparie, mesure l'écart, arme l'interpolation.</summary>
        public int OnSnapshot(VehicleSnapshotMessage message)
        {
            if (message == null || message.Vehicles == null || message.Vehicles.Length == 0)
            {
                return 0;
            }

            _snapshotsReceived++;
            EnsureRegistry();
            var applied = 0;
            var now = Time.realtimeSinceStartup;
            var localVehicle = Player.main != null ? Player.main.GetVehicle() : null;

            for (var i = 0; i < message.Vehicles.Length; i++)
            {
                var state = message.Vehicles[i];
                _statesReceived++;

                if (state.Key == 0L)
                {
                    _statesUnknownKey++;
                    continue;
                }

                if (!_tracked.TryGetValue(state.Key, out var entry))
                {
                    if (!TryAdopt(state, localVehicle, now, out entry))
                    {
                        continue;
                    }
                }
                else if (entry.Vehicle == null)
                {
                    // Le véhicule a été détruit (déchargement de cellule) : `Update` le relâchera.
                    continue;
                }

                if (entry.Vehicle == localVehicle)
                {
                    // Le joueur local conduit ce véhicule : sa simulation est locale, l'annonce ne doit
                    // pas la contredire. On le compte, une fois par véhicule, au lieu de le taire.
                    if (!entry.LocallyDrivenLogged)
                    {
                        entry.LocallyDrivenLogged = true;
                        _locallyDrivenSkips++;
                        _log.LogInfo(
                            $"[véhicule] {entry.Label} est conduit par ce client — position du serveur " +
                            "ignorée pour lui (M4 ne transporte pas encore la conduite).");
                    }

                    entry.LastStateRealtime = now;
                    continue;
                }

                var position = new Vector3(state.X, state.Y, state.Z);
                var rotation = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);

                if (entry.StatesReceived > 0)
                {
                    var gap = now - entry.LastStateRealtime;
                    var drift = Vector3.Distance(entry.Vehicle.transform.position, position);

                    if (gap <= ConsecutiveStateGapSeconds)
                    {
                        // Mesure **avant** lissage : ce que la copie locale avait pris de retard sur
                        // l'annonce précédente. C'est la dérive de synchronisation, pas le pas annoncé.
                        entry.MaxDriftMeters = Mathf.Max(entry.MaxDriftMeters, drift);
                        if (drift > _maxDriftMeters)
                        {
                            _maxDriftMeters = drift;
                            _maxDriftLabel = entry.Label;
                        }

                        if (drift > DriftAnomalyMeters)
                        {
                            _log.LogWarning(
                                $"[véhicule] écart de {drift:F2} m avant lissage sur {entry.Label} " +
                                $"(intervalle {gap:F3} s) — c'est un rattrapage, la position annoncée reste autoritaire.");
                        }

                        // Un segment par intervalle annoncé : le client arrive sur la cible au moment où
                        // l'annonce suivante arrive. Pas de tampon de latence supplémentaire, donc pas de
                        // retard ajouté à celui du réseau.
                        //
                        // La durée ne peut pas être plus courte que `distance / 40 m/s` : des annonces
                        // livrées en rafale (frame bloquée) ont un intervalle mesuré presque nul, et s'y
                        // fier ferait passer `alpha` à 1 en une image — le saut reviendrait, entier, au
                        // lieu d'être lissé.
                        var distance = Vector3.Distance(entry.Vehicle.transform.position, position);

                        // Enregistrement du flux **réel** (P3) : c'est cette suite d'états qui est rejouée
                        // hors du jeu par les tests, avec la même règle de durée.
                        RecordState(message.ServerTick, state.Key, gap, position);

                        entry.From = entry.Vehicle.transform.position;
                        entry.FromRotation = entry.Vehicle.transform.rotation;
                        entry.SegmentStart = now;
                        // La règle de durée vit dans `Coop.Model` (P3) : elle se rejoue hors du jeu, avec
                        // les mêmes bornes que celles qui protègent l'interpolation mesurée à l'étape J.
                        entry.SegmentDuration = VehicleInterpolation.SegmentDuration(gap, distance);
                    }
                    else
                    {
                        // Interruption (relevancy, paquet perdu pendant > 0,5 s) : on ne glisse pas sur
                        // une durée qui n'a plus de sens, on se replace et on repart de là.
                        entry.From = position;
                        entry.FromRotation = rotation;
                        entry.SegmentStart = now;
                        entry.SegmentDuration = MinSegmentSeconds;
                    }
                }
                else
                {
                    // Première annonce : application immédiate, puis interpolation à partir d'ici.
                    entry.Vehicle.transform.position = position;
                    entry.Vehicle.transform.rotation = rotation;
                    entry.From = position;
                    entry.FromRotation = rotation;
                    entry.SegmentStart = now;
                    entry.SegmentDuration = MinSegmentSeconds;
                }

                ApplyEnergy(entry, state.Energy);

                entry.To = position;
                entry.ToRotation = rotation;
                entry.LastStateRealtime = now;
                entry.StatesReceived++;
                _statesMatched++;
                applied++;
            }

            // Les modules reçus avant le suivi sont appliqués dès que la copie existe (v15).
            FlushPendingUpgrades();

            return applied;
        }

        /// <summary>
        /// Avance l'interpolation d'une image. Appelé à chaque frame par <see cref="ClientNetworkBehaviour"/>,
        /// après la mise à jour des avatars : l'ordre n'importe pas pour les véhicules, mais une seule
        /// boucle par frame évite de mesurer deux fois le même pas.
        /// </summary>
        public void UpdateInterpolation()
        {
            if (_tracked.Count == 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            // Durée de l'image en cours : c'est elle qui explique un pas plus grand qu'à l'ordinaire, et
            // c'est elle qui sert à juger si le pas est un glissement ou un saut.
            var frameDelta = Time.unscaledDeltaTime;
            var localVehicle = Player.main != null ? Player.main.GetVehicle() : null;

            if (_measureStartRealtime < 0f)
            {
                _measureStartRealtime = now;
            }

            _framesObserved++;

            List<Tracked> stale = null;

            foreach (var entry in _tracked.Values)
            {
                if (entry.Vehicle == null)
                {
                    if (stale == null)
                    {
                        stale = new List<Tracked>();
                    }

                    stale.Add(entry);
                    continue;
                }

                if (entry.Vehicle == localVehicle)
                {
                    if (!entry.LocallyDrivenLogged)
                    {
                        entry.LocallyDrivenLogged = true;
                        _locallyDrivenSkips++;
                        _log.LogInfo(
                            $"[véhicule] {entry.Label} est conduit par ce client — interpolation suspendue " +
                            "et physique rendue au jeu.");
                    }

                    RestoreBody(entry);
                    continue;
                }

                if (now - entry.LastStateRealtime > Protocol.VehicleTrackingTimeoutSeconds)
                {
                    if (stale == null)
                    {
                        stale = new List<Tracked>();
                    }

                    stale.Add(entry);
                    _log.LogInfo(
                        $"[véhicule] relâché : {entry.Label} (plus d'annonce depuis " +
                        $"{now - entry.LastStateRealtime:F1} s) — la simulation locale reprend.");
                    continue;
                }

                var elapsed = now - entry.SegmentStart;
                var alpha = entry.SegmentDuration > 0f
                    ? Mathf.Clamp01(elapsed / entry.SegmentDuration)
                    : 1f;

                var target = alpha >= 1f
                    ? entry.To
                    : Vector3.Lerp(entry.From, entry.To, alpha);
                var targetRotation = alpha >= 1f
                    ? entry.ToRotation
                    : Quaternion.Slerp(entry.FromRotation, entry.ToRotation, alpha);

                var current = entry.Vehicle.transform.position;
                var step = Vector3.Distance(current, target);

                entry.Vehicle.transform.position = target;
                entry.Vehicle.transform.rotation = targetRotation;

                // Le pas par image n'est compté que pendant un segment : au-delà, la position ne bouge
                // plus (le véhicule attend l'annonce suivante), et inclure ces zéros ferait baisser une
                // moyenne qui doit décrire le mouvement.
                if (step > 0f && frameDelta > 0f &&
                    step / frameDelta > MaxCatchUpSpeedMetersPerSecond * 1.25f &&
                    _fastStepWarnings < 5)
                {
                    _fastStepWarnings++;
                    _log.LogWarning(
                        $"[véhicule] pas rapide : {step:F2} m en {frameDelta * 1000f:F0} ms " +
                        $"({step / frameDelta:F1} m/s) sur {entry.Label} — segment {entry.SegmentDuration:F3} s, " +
                        $"écoulé={elapsed:F3} s, alpha={alpha:F2}, trajet={Vector3.Distance(entry.From, entry.To):F2} m, " +
                        $"reste={Vector3.Distance(current, entry.To):F2} m.");
                }

                if (alpha < 1f && step > 0f)
                {
                    _frameSteps++;
                    _frameStepSum += step;
                    _maxFrameStepMeters = Mathf.Max(_maxFrameStepMeters, step);
                    if (step >= 1f)
                    {
                        _frameStepsAboveOneMeter++;
                    }
                }
            }

            if (stale != null)
            {
                foreach (var entry in stale)
                {
                    RestoreBody(entry);
                    _tracked.Remove(entry.Key);
                    _releases++;
                }
            }

            _measureSeconds = now - _measureStartRealtime;
        }

        /// <summary>
        /// Rend tous les véhicules suivis à leur simulation locale. Appelé à la déconnexion : sans ça, un
        /// serveur disparu laisserait des véhicules gelés un peu partout dans le monde du client.
        /// </summary>
        public void ReleaseAll(string reason)
        {
            if (_tracked.Count == 0)
            {
                return;
            }

            var restored = 0;
            foreach (var entry in _tracked.Values)
            {
                if (entry.BodyForcedKinematic)
                {
                    restored++;
                }

                RestoreBody(entry);
            }

            _log.LogInfo(
                $"[véhicule] {_tracked.Count} véhicule(s) suivi(s) relâché(s) ({reason}) — " +
                $"{restored} physique(s) rendue(s).");
            _tracked.Clear();
        }


        private int _energiesApplied;

        /// <summary>
        /// Applique la charge annoncée (v14). `-1` veut dire « inconnue » (pas de batterie) : on ne
        /// touche à rien. L'écriture n'a lieu que si l'écart dépasse 1 %, pour ne pas réécrire la même
        /// valeur dix fois par seconde.
        /// </summary>
        private void ApplyEnergy(Tracked entry, float energy)
        {
            if (energy < 0f || entry.Vehicle == null)
            {
                return;
            }

            // La conversion normalisé -> absolu vit dans `VehicleModules` (Shared) : la v14 comparait une
            // charge absolue (`200` sur un Seamoth) à une valeur normalisée, et écrivait donc un écart
            // faux. Mesuré au run du 2026-10-20 : 125 « applications » sans qu'aucune charge ne change.
            if (VehicleModules.WriteNormalized(entry.Vehicle, energy))
            {
                _energiesApplied++;
            }
        }

        // — Enregistrement du flux (P3) ————————————————————————————————————————————

        private static bool _recordChecked;
        private static bool _recordEnabled;
        private static string _recordPath;
        private long _recordedStates;

        /// <summary>États écrits dans l'enregistrement (P3) : 0 signifie que `COOP_RECORD` n'était pas posé.</summary>
        public long RecordedStates => _recordedStates;

        /// <summary>
        /// Écrit un état reçu dans un fichier, si `COOP_RECORD=1`. Une ligne par état, en culture
        /// invariante : tick, intervalle mesuré, position. C'est le format que `VehicleReplay` relit.
        ///
        /// L'écriture est **tolérante** : un disque plein ou un fichier verrouillé ne doit pas interrompre
        /// la synchronisation d'un véhicule. L'échec est donc silencieux côté jeu, mais il arrête le
        /// compteur — un fichier absent se lit dans le journal, pas dans une exception.
        /// </summary>
        private void RecordState(long serverTick, long key, float gap, Vector3 position)
        {
            if (!_recordChecked)
            {
                _recordChecked = true;
                _recordEnabled = System.Environment.GetEnvironmentVariable("COOP_RECORD") == "1";
                if (_recordEnabled)
                {
                    try
                    {
                        var suffix = System.Environment.GetEnvironmentVariable("COOP_LOG_SUFFIX") ?? string.Empty;
                        var directory = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "logs");
                        System.IO.Directory.CreateDirectory(directory);
                        _recordPath = System.IO.Path.Combine(directory, "vehicles" + suffix + ".csv");
                        System.IO.File.WriteAllText(
                            _recordPath,
                            "# clé=" + key.ToString("x16") + " — tick;intervalle;x;y;z\n");
                    }
                    catch (Exception exception)
                    {
                        _recordEnabled = false;
                        _log.LogWarning($"[véhicule] enregistrement impossible : {exception.Message}");
                    }
                }
            }

            if (!_recordEnabled || _recordPath == null)
            {
                return;
            }

            try
            {
                System.IO.File.AppendAllText(
                    _recordPath,
                    VehicleReplay.Format(serverTick, gap, position.x, position.y, position.z) + "\n");
                _recordedStates++;
            }
            catch (Exception)
            {
                _recordEnabled = false;
            }
        }

        /// <summary>Résumé d'une ligne, pour le journal périodique et pour les sondes.</summary>
        public string Describe()
        {
            var average = _frameSteps > 0 ? _frameStepSum / _frameSteps : 0;
            var fps = _measureSeconds > 0.001f ? _framesObserved / _measureSeconds : 0;

            return string.Format(
                CultureInfo.InvariantCulture,
                "suivis={0} snapshots={1} états={2} appariés={3} clé inconnue={4} rattachés par proximité={5} " +
                "relâchés={6} conduits localement={7} écart avant lissage max={8:F2}m ({9}) " +
                "pas par image max={10:F2}m moy={11:F2}m sur {12} image(s) en mouvement (fps≈{13:F0}) " +
                "dont ≥1m={14}, vitesse implicite≈{15:F1} m/s, énergies appliquées={16}, " +
                "modules reçus={17} divergences={18} appliqués={19} retirés={20}",
                _tracked.Count,
                _snapshotsReceived,
                _statesReceived,
                _statesMatched,
                _statesUnknownKey,
                _bindingsByProximity,
                _releases,
                _locallyDrivenSkips,
                _maxDriftMeters,
                _maxDriftLabel,
                _maxFrameStepMeters,
                average,
                _frameSteps,
                fps,
                _frameStepsAboveOneMeter,
                fps * (float)average,
                _energiesApplied,
                _upgradesMessages,
                _upgradesDivergences,
                VehicleModuleApplier.AppliedCount,
                VehicleModuleApplier.RemovedCount);
        }

        /// <summary>Journalise le résumé toutes les 5 s : un run doit se lire, pas se déduire.</summary>
        public void ReportPeriodically()
        {
            var now = Time.realtimeSinceStartup;
            if (now < _nextReport)
            {
                return;
            }

            _nextReport = now + 5f;

            if (_snapshotsReceived == 0)
            {
                return;
            }

            _log.LogInfo($"[véhicule] état : {Describe()}");
        }

        /// <summary>
        /// Cherche le véhicule annoncé dans le monde local. Une clé inconnue n'est jamais inventée ; le
        /// seul rattachement autorisé est celui décrit par <see cref="UnknownKeyBindingMeters"/>, et il
        /// est compté et journalisé.
        /// </summary>
        private bool TryAdopt(VehicleStateEntry state, Vehicle localVehicle, float now, out Tracked entry)
        {
            entry = null;

            if (!_registry.TryGetValue(state.Key, out var vehicle) || vehicle == null)
            {
                EnsureRegistryIfAllowed(now);
                if (_registry.TryGetValue(state.Key, out vehicle) && vehicle != null)
                {
                    // La clé vient d'apparaître (cellule chargée entre-temps) : on continue.
                }
                else
                {
                    vehicle = TryBindByProximity(state, localVehicle);
                    if (vehicle == null)
                    {
                        _statesUnknownKey++;
                        if (_statesUnknownKey <= 5 || _statesUnknownKey % 100 == 0)
                        {
                            _log.LogWarning(
                                $"[véhicule] clé inconnue 0x{state.Key:x16} à " +
                                $"({state.X:F1}, {state.Y:F1}, {state.Z:F1}) — aucun véhicule local du même " +
                                $"type à moins de {UnknownKeyBindingMeters:F0} m ; entrée ignorée " +
                                $"({_statesUnknownKey} au total).");
                        }

                        return false;
                    }
                }
            }

            VehicleIdentity.KeyOf(vehicle, out var mechanism);
            entry = new Tracked
            {
                Key = state.Key,
                Vehicle = vehicle,
                Label = VehicleIdentity.Describe(vehicle),
                From = vehicle.transform.position,
                To = vehicle.transform.position,
                FromRotation = vehicle.transform.rotation,
                ToRotation = vehicle.transform.rotation,
                SegmentStart = now,
                SegmentDuration = MinSegmentSeconds,
                LastStateRealtime = now,
                StatesReceived = 0,

                // Remis à `false` même si le véhicule est déjà conduit : c'est la première annonce qui doit
                // écrire la ligne « conduit par ce client », pas le silence d'un état initial.
                LocallyDrivenLogged = false,
            };

            // Un véhicule conduit par le joueur local n'est pas gelé : sa physique est la sienne.
            if (vehicle != localVehicle)
            {
                FreezeBody(entry);
            }

            _tracked[state.Key] = entry;
            _log.LogInfo(
                $"[véhicule] suivi adopté : clé=0x{state.Key:x16} -> {entry.Label} " +
                $"(clé locale calculée par {mechanism}, " +
                $"{(entry.BodyForcedKinematic ? "physique gelée" : "physique déjà cinématique/absente")}, " +
                $"écart initial={Vector3.Distance(vehicle.transform.position, new Vector3(state.X, state.Y, state.Z)):F2} m).");
            return true;
        }

        /// <summary>
        /// Rattache une clé inconnue au seul véhicule local non suivi du **même type** dans le rayon.
        /// Compté comme un rattachement par proximité, jamais silencieux.
        /// </summary>
        private Vehicle TryBindByProximity(VehicleStateEntry state, Vehicle localVehicle)
        {
            var announced = new Vector3(state.X, state.Y, state.Z);
            Vehicle candidate = null;
            var candidates = 0;
            var vehicles = UnityEngine.Object.FindObjectsOfType<Vehicle>();

            for (var i = 0; i < vehicles.Length; i++)
            {
                var vehicle = vehicles[i];
                if (vehicle == null || vehicle.transform == null || vehicle == localVehicle)
                {
                    continue;
                }

                if (Vector3.Distance(vehicle.transform.position, announced) > UnknownKeyBindingMeters)
                {
                    continue;
                }

                if (IsTracked(vehicle))
                {
                    continue;
                }

                candidates++;
                if (candidate == null)
                {
                    candidate = vehicle;
                }
                else if (candidate.GetType() != vehicle.GetType())
                {
                    // Deux types différents dans le rayon : le rattachement n'est pas démontrable.
                    return null;
                }
            }

            if (candidates != 1 || candidate == null)
            {
                return null;
            }

            _bindingsByProximity++;
            _log.LogWarning(
                $"[véhicule] clé 0x{state.Key:x16} inconnue — rattachée par proximité à " +
                $"{VehicleIdentity.Describe(candidate)} (seul véhicule local non suivi à moins de " +
                $"{UnknownKeyBindingMeters:F0} m de la position annoncée). La clé de repli a été calculée " +
                "à un autre instant côté client.");

            _registry[state.Key] = candidate;
            return candidate;
        }

        private bool IsTracked(Vehicle vehicle)
        {
            foreach (var entry in _tracked.Values)
            {
                if (entry.Vehicle == vehicle)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reconstruit le registre local des clés. Il est rafraîchi périodiquement parce qu'une cellule
        /// peut se charger ou se décharger ; une clé inconnue déclenche en plus une reconstruction, mais
        /// au plus une fois par seconde pour ne pas transformer une clé définitivement inconnue en
        /// balayage permanent de la scène.
        /// </summary>
        private void EnsureRegistry()
        {
            var now = Time.realtimeSinceStartup;
            if (now - _registryRealtime < RegistryRefreshSeconds && _registry.Count > 0)
            {
                return;
            }

            _registryRealtime = now;
            _registry.Clear();
            var vehicles = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            var unkeyed = 0;

            for (var i = 0; i < vehicles.Length; i++)
            {
                var vehicle = vehicles[i];
                if (vehicle == null)
                {
                    continue;
                }

                var key = VehicleIdentity.KeyOf(vehicle, out _);
                if (key == 0L)
                {
                    unkeyed++;
                    continue;
                }

                if (_registry.ContainsKey(key))
                {
                    _log.LogWarning(
                        $"[véhicule] deux véhicules locaux partagent la clé 0x{key:x16} — " +
                        $"{VehicleIdentity.Describe(vehicles[i])} ; l'un des deux sera mal suivi.");
                }

                _registry[key] = vehicle;
            }

            if (unkeyed > 0)
            {
                _log.LogWarning($"[véhicule] {unkeyed} véhicule(s) local(aux) sans clé : il(s) ne peu(ven)t pas être suivi(s).");
            }
        }

        private void EnsureRegistryIfAllowed(float now)
        {
            if (now - _forcedRegistryRealtime < ForcedRebuildSeconds)
            {
                return;
            }

            _forcedRegistryRealtime = now;
            _registryRealtime = float.NegativeInfinity;
            EnsureRegistry();
        }

        /// <summary>
        /// Gèle la physique du véhicule suivi. Sans ça, la flottabilité et la traînée du jeu déplacent la
        /// copie entre deux annonces ; l'état d'origine est mémorisé pour être rendu au relâchement.
        /// </summary>
        private void FreezeBody(Tracked entry)
        {
            var body = entry.Vehicle.useRigidbody != null
                ? entry.Vehicle.useRigidbody
                : entry.Vehicle.GetComponent<Rigidbody>();

            if (body == null)
            {
                return;
            }

            entry.Body = body;
            entry.BodyWasKinematic = body.isKinematic;

            if (!body.isKinematic)
            {
                body.isKinematic = true;
                entry.BodyForcedKinematic = true;
            }

            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        private void RestoreBody(Tracked entry)
        {
            if (entry.Body == null || !entry.BodyForcedKinematic)
            {
                return;
            }

            entry.Body.isKinematic = entry.BodyWasKinematic;
            entry.BodyForcedKinematic = false;
        }
    }
}
