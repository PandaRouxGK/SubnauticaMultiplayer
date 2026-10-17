using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Shared;
using UnityEngine;

namespace Coop.Server
{
    /// <summary>
    /// Sonde de **M4 (a)** : faire exister un véhicule en mouvement, puis mesurer ce qu'il coûte à
    /// l'annonce.
    ///
    /// Pourquoi elle existe : le relevé des véhicules du monde (`ServerNetwork.ReportVehicles`) a mesuré que
    /// la sauvegarde de test **n'en contient aucun**, et que le plus grand pas observé pour une créature vaut
    /// `0,99 m` par annonce. Un Seamoth à `20 m/s` parcourt `2 m` entre deux annonces à `10 Hz` — une bande
    /// que les créatures n'ont **jamais** atteinte. La question de M4 — cadence d'envoi ou interpolation —
    /// ne pouvait donc pas être tranchée : il manquait le véhicule, pas le message.
    ///
    /// Ce que la sonde fait, dans l'ordre, et pourquoi :
    /// 1. **Faire exister le véhicule** par le chemin du jeu (`spawn seamoth`, la commande console que le
    ///    jeu traite comme une action de joueur), puis vérifier qu'un `Vehicle` est bien apparu. Si le monde
    ///    en contient déjà un, il est utilisé tel quel — c'est un état réel, pas une fabrication.
    /// 2. **Le déplacer pour de vrai** : sa position avance à `20 m/s` dans `Update`, avec son Rigidbody
    ///    rendu cinématique pour que la physique ne se dispute pas le transform. Ce n'est pas un compteur :
    ///    la vitesse annoncée est recalculée depuis le déplacement mesuré et la durée mesurée.
    /// 3. **Échantillonner à la cadence réelle des annonces** — `10 Hz` (celle des créatures,
    ///    `Protocol.CreatureStreamIntervalTicks`) et `20 Hz` (celle des joueurs) — et **répartir les pas**
    ///    entre deux échantillons dans les mêmes bandes que la sonde de créatures. Une bande `≥ 2 m` peuplée
    ///    est la mesure qui manquait.
    ///
    /// Ce que la sonde ne fait pas : elle n'envoie **aucun** message de véhicule (le protocole n'en a pas).
    /// Elle mesure donc ce qu'une annonce à cette cadence produirait, pas ce qu'un client reçoit. La
    /// distinction est écrite dans la ligne de verdict, et c'est elle qui décide entre cadence et
    /// interpolation.
    ///
    /// Activée par `-coopdebug=vehicle` côté **hôte** (voir <see cref="CoopServerPlugin"/>). Le véhicule
    /// fabriqué est détruit à la fin : un run de mesure ne doit pas laisser un Seamoth dans la sauvegarde.
    /// </summary>
    public class VehicleProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Véhicule");

        /// <summary>Vitesse visée (m/s) : celle du Seamoth de référence, et celle qui balaie `2 m` par annonce à 10 Hz.</summary>
        private const float SpeedMetersPerSecond = 20f;

        /// <summary>
        /// Durée (s) du déplacement mesuré, **surchargeable par `COOP_VEHICLE_SECONDS`**.
        ///
        /// 10 s suffisent à peupler la bande `≥ 2 m` par annonce (cent annonces à 10 Hz). Une durée plus
        /// longue existe pour une seule raison : laisser à un client le temps d'entrer dans le monde et
        /// d'observer le véhicule en mouvement (étape J de `tools/verify.sh`). Sans cette surcharge, la
        /// sonde de l'hôte aurait fini de déplacer — et détruit — le Seamoth avant que le client ne le voie.
        /// </summary>
        private static readonly float MotionSeconds = ParseMotionSeconds();

        private static float ParseMotionSeconds()
        {
            var raw = Environment.GetEnvironmentVariable("COOP_VEHICLE_SECONDS");
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                value >= 5f && value <= 600f)
            {
                return value;
            }

            return 10f;
        }

        /// <summary>
        /// Demi-longueur (m) de l'aller-retour du véhicule.
        ///
        /// Le déplacement était autrefois une ligne droite de `durée × 20 m/s` : sur 10 s, 200 m. Une
        /// durée longue sortirait du rayon de pertinence (150 m) et le client cesserait de recevoir le
        /// véhicule au bout de quelques secondes — la mesure d'interpolation porterait alors sur un objet
        /// immobile. Le véhicule fait donc des allers-retours dans une bande de ±50 m : il reste pertinent,
        /// et chaque demi-tour est un changement de cap que l'interpolation doit encaisser.
        /// </summary>
        private const float AmplitudeMeters = 50f;

        /// <summary>Attente maximale (s) du monde et du joueur.</summary>
        private const float WorldTimeoutSeconds = 240f;

        /// <summary>Attente maximale (s) de l'apparition du véhicule après la commande console.</summary>
        private const float SpawnTimeoutSeconds = 25f;

        private const float CreatureCadenceSeconds = 0.1f;
        private const float PlayerCadenceSeconds = 0.05f;

        private IEnumerator Start()
        {
            // `Player.main` et `LargeWorldStreamer.main` existent **aussi dans les scènes de menu**, et le
            // streamer est même ré-initialisé pendant le chargement (mesuré : sonde lancée sur un
            // `Deinitialize`/`Initialize`, Seamoth fabriqué avant que le monde ne soit prêt). Le seul signal
            // qui vaut est celui d'`AutoLoadBehaviour`, posé quand la scène active n'est plus une scène de
            // menu ; le délai qui suit laisse le jeu placer le joueur à sa position sauvegardée. Au premier
            // run de l'étape K, s'y fier par le streamer a fait fabriquer un second Seamoth et perdre le
            // véhicule de la sauvegarde (défaut mesuré au §35).
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (!AutoLoadBehaviour.WorldLoaded && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (!AutoLoadBehaviour.WorldLoaded || Player.main == null)
            {
                Log.LogError(
                    $"[sonde] véhicule : verdict=False — aucun monde chargé après {WorldTimeoutSeconds:F0}s, rien n'a été déplacé.");
                yield break;
            }

            yield return new WaitForSecondsRealtime(3f);
            var host = Player.main;

            // Un véhicule déjà présent est préféré : c'est un état du monde réel, et le déplacer ne demande
            // aucune fabrication. Sinon, on passe par la commande console du jeu.
            var vehicle = FindNearestVehicle(host.transform.position, out var distance);
            var spawned = false;

            if (vehicle == null)
            {
                var command = "spawn " + TechType.Seamoth.AsString(false);
                Log.LogWarning($"[sonde] véhicule : aucun véhicule dans le monde — commande console « {command} ».");

                var spawnDeadline = Time.realtimeSinceStartup + SpawnTimeoutSeconds;
                var attempts = 0;
                var nextAttempt = 0f;

                // La commande est **réessayée** : `Player.main` existe avant que le monde ne soit
                // complètement chargé, et une commande console envoyée trop tôt est acceptée sans rien
                // fabriquer (mesuré : une seule tentative à 10 s du chargement, aucun véhicule 25 s plus
                // tard). Le nombre de tentatives est journalisé pour que « la console ne répond pas » soit
                // distingué de « on n'a essayé qu'une fois ».
                while (vehicle == null && Time.realtimeSinceStartup < spawnDeadline)
                {
                    if (attempts == 0 || Time.realtimeSinceStartup >= nextAttempt)
                    {
                        attempts++;
                        nextAttempt = Time.realtimeSinceStartup + 5f;

                        try
                        {
                            DevConsole.SendConsoleCommand(command);
                        }
                        catch (Exception exception)
                        {
                            Log.LogError(
                                $"[sonde] véhicule : la commande console a échoué ({exception.GetType().Name}: " +
                                $"{exception.Message}) — tentative {attempts}.");
                            yield break;
                        }
                    }

                    yield return new WaitForSecondsRealtime(1f);
                    vehicle = FindNearestVehicle(host.transform.position, out distance);
                }

                spawned = vehicle != null;
                Log.LogInfo(
                    $"[sonde] véhicule : {attempts} tentative(s) de commande console, " +
                    $"{(spawned ? "véhicule apparu" : "aucun véhicule")}.");
            }
            else
            {
                Log.LogInfo(
                    $"[sonde] véhicule : '{vehicle.name}' déjà présent à {distance.ToString("F1", CultureInfo.InvariantCulture)} m de l'hôte — déplacé tel quel.");
            }

            if (vehicle == null)
            {
                Log.LogError(
                    $"[sonde] véhicule : verdict=False — aucun Vehicle {SpawnTimeoutSeconds:F0}s après « spawn seamoth ». " +
                    "La commande a peut-être échoué (console indisponible) : rien n'a été mesuré.");
                yield break;
            }

            // Le nom est lu **avant** la destruction du véhicule fabriqué : `Destroy` est différé à la fin
            // de la frame, mais s'appuyer dessus serait un détail de timing, pas une garantie.
            var vehicleName = vehicle.name;

            Log.LogInfo(
                $"[sonde] véhicule : '{vehicleName}' retenu ({(spawned ? "fabriqué par la sonde" : "déjà dans le monde")}), " +
                $"position initiale {vehicle.transform.position.ToString("F1")} — déplacement de {MotionSeconds:F0}s à " +
                $"{SpeedMetersPerSecond.ToString("F1", CultureInfo.InvariantCulture)} m/s.");

            // Point de départ : sous l'hôte, dans l'eau libre. Le transform est piloté directement (le
            // véhicule n'est pas conduit par un joueur), et le Rigidbody est neutralisé pour que la physique
            // ne reprenne pas la main entre deux frames — sinon la vitesse mesurée ne serait plus celle
            // qu'on impose, et la sonde mesurerait sa propre bagarre avec PhysX.
            var start = host.transform.position + new Vector3(0f, -8f, 0f);
            var direction = Vector3.right;

            // Un véhicule **du monde** est rendu à sa place à la fin : une sonde qui laisse un Seamoth
            // 200 m plus loin aurait modifié la sauvegarde qu'elle devait seulement mesurer.
            var originalPosition = vehicle.transform.position;
            var originalRotation = vehicle.transform.rotation;

            var rigidbody = vehicle.GetComponent<Rigidbody>();
            var wasKinematic = false;
            if (rigidbody != null)
            {
                wasKinematic = rigidbody.isKinematic;
                rigidbody.isKinematic = true;
                rigidbody.velocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
            }

            vehicle.transform.position = start;
            vehicle.transform.rotation = Quaternion.LookRotation(direction);

            // L'identité de repli (véhicule sans `UniqueIdentifier`) est ancrée sur la position **à la
            // première observation**. La diffusion calcule cette clé au plus tard deux secondes après
            // l'apparition (`VehicleCacheSeconds`) : on laisse donc le véhicule immobile ce temps-là, sinon
            // la clé serait calculée sur une position déjà déplacée et ne coïnciderait plus avec celle que
            // le client calcule de son côté. Sur un véhicule qui a un `UniqueIdentifier`, cette attente ne
            // change rien — elle ne coûte que deux secondes.
            yield return new WaitForSecondsRealtime(2.5f);

            var creatureSteps = new Steps();
            var playerSteps = new Steps();

            var position = start;
            var elapsed = 0f;
            var creatureClock = 0f;
            var playerClock = 0f;
            var creaturePrevious = position;
            var playerPrevious = position;

            // Distance **parcourue** et non déplacement net : avec l'aller-retour, le déplacement net
            // revient près de zéro alors que le véhicule a bel et bien roulé.
            var travelled = 0f;

            while (elapsed < MotionSeconds)
            {
                yield return null;

                var delta = Time.unscaledDeltaTime;
                if (delta <= 0f)
                {
                    continue;
                }

                elapsed += delta;
                var advance = SpeedMetersPerSecond * delta;
                position += direction * advance;
                travelled += advance;

                // Demi-tour borné : le véhicule reste dans le rayon de pertinence du client.
                var offset = position - start;
                if (offset.magnitude > AmplitudeMeters)
                {
                    position = start + offset.normalized * AmplitudeMeters;
                    direction = -direction;
                    vehicle.transform.rotation = Quaternion.LookRotation(direction);
                }

                vehicle.transform.position = position;

                creatureClock += delta;
                if (creatureClock >= CreatureCadenceSeconds)
                {
                    creatureSteps.Add(Vector3.Distance(creaturePrevious, position), creatureClock);
                    creaturePrevious = position;
                    creatureClock = 0f;
                }

                playerClock += delta;
                if (playerClock >= PlayerCadenceSeconds)
                {
                    playerSteps.Add(Vector3.Distance(playerPrevious, position), playerClock);
                    playerPrevious = position;
                    playerClock = 0f;
                }
            }

            var moved = Vector3.Distance(start, position);
            var speed = elapsed > 0f ? travelled / elapsed : 0f;

            if (spawned)
            {
                // Le monde doit rester tel qu'on l'a trouvé : sinon la mesure suivante verrait un véhicule
                // que c'est *elle* qui aurait fabriqué.
                UnityEngine.Object.Destroy(vehicle.gameObject);
            }
            else
            {
                vehicle.transform.position = originalPosition;
                vehicle.transform.rotation = originalRotation;
                if (rigidbody != null)
                {
                    rigidbody.isKinematic = wasKinematic;
                }
            }

            Log.LogInfo(
                $"[sonde] véhicule : pas entre deux annonces — à 10 Hz (créatures) : {creatureSteps.Describe()} " +
                $"· à 20 Hz (joueurs) : {playerSteps.Describe()} (mesurés sur un déplacement réel, avant tout message : " +
                "le protocole ne transporte encore aucun véhicule).");

            var enoughMoved = travelled >= 0.9f * SpeedMetersPerSecond * MotionSeconds;
            var speedPlausible = speed >= 0.9f * SpeedMetersPerSecond && speed <= 1.1f * SpeedMetersPerSecond;
            var fastBandPopulated = creatureSteps.AtLeastTwoMeters > 0;
            var verdict = enoughMoved && speedPlausible && fastBandPopulated;

            Log.LogInfo(
                $"[sonde] véhicule : verdict={verdict} — '{vehicleName}' déplacé de " +
                $"{moved.ToString("F1", CultureInfo.InvariantCulture)} m (parcouru " +
                $"{travelled.ToString("F1", CultureInfo.InvariantCulture)} m) en " +
                $"{elapsed.ToString("F1", CultureInfo.InvariantCulture)} s " +
                $"(vitesse mesurée {speed.ToString("F1", CultureInfo.InvariantCulture)} m/s), pas max à 10 Hz=" +
                $"{creatureSteps.Max.ToString("F2", CultureInfo.InvariantCulture)} m / à 20 Hz=" +
                $"{playerSteps.Max.ToString("F2", CultureInfo.InvariantCulture)} m, bande ≥2 m à 10 Hz=" +
                $"{creatureSteps.AtLeastTwoMeters} annonce(s) — seuils : parcouru≥{0.9f * SpeedMetersPerSecond * MotionSeconds:F0} m, " +
                $"vitesse≈{SpeedMetersPerSecond:F0} m/s, bande ≥2 m non vide");
        }

        private static Vehicle FindNearestVehicle(Vector3 origin, out float distance)
        {
            var nearest = (Vehicle)null;
            var best = float.MaxValue;

            var vehicles = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            if (vehicles != null)
            {
                for (var i = 0; i < vehicles.Length; i++)
                {
                    var candidate = vehicles[i];
                    if (candidate == null || candidate.transform == null)
                    {
                        continue;
                    }

                    var d = Vector3.Distance(origin, candidate.transform.position);
                    if (d < best)
                    {
                        best = d;
                        nearest = candidate;
                    }
                }
            }

            distance = nearest != null ? best : 0f;
            return nearest;
        }

        /// <summary>
        /// Répartition des pas entre deux annonces, dans les bandes déjà utilisées par la sonde de
        /// créatures — les mêmes bandes rendent les deux mesures comparables, et `≥ 2 m` est la bande que
        /// les créatures n'ont jamais atteinte.
        /// </summary>
        private sealed class Steps
        {
            private int _underQuarter;
            private int _quarterToHalf;
            private int _halfToOne;
            private int _oneToTwo;
            private int _atLeastTwo;
            private double _sumDistance;
            private double _sumInterval;

            public float Max { get; private set; }

            public int AtLeastTwoMeters => _atLeastTwo;

            public void Add(float distance, float interval)
            {
                if (distance > Max)
                {
                    Max = distance;
                }

                _sumDistance += distance;
                _sumInterval += interval;

                if (distance < 0.25f)
                {
                    _underQuarter++;
                }
                else if (distance < 0.5f)
                {
                    _quarterToHalf++;
                }
                else if (distance < 1f)
                {
                    _halfToOne++;
                }
                else if (distance < 2f)
                {
                    _oneToTwo++;
                }
                else
                {
                    _atLeastTwo++;
                }
            }

            public string Describe()
            {
                var count = _underQuarter + _quarterToHalf + _halfToOne + _oneToTwo + _atLeastTwo;
                var implied = _sumInterval > 0 ? _sumDistance / _sumInterval : 0;
                var average = count > 0 ? _sumDistance / count : 0;

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "<0,25 m : {0} · 0,25–0,5 m : {1} · 0,5–1 m : {2} · 1–2 m : {3} · ≥2 m : {4} " +
                    "({5} annonce(s), pas moyen {6:F2} m, max {7:F2} m, vitesse implicite {8:F1} m/s)",
                    _underQuarter,
                    _quarterToHalf,
                    _halfToOne,
                    _oneToTwo,
                    _atLeastTwo,
                    count,
                    average,
                    Max,
                    implied);
            }
        }
    }
}
