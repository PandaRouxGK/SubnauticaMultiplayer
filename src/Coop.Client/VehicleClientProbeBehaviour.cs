using System;
using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de **M4 (b)** : mesure ce que le client fait d'un véhicule annoncé à 10 Hz — et donc si
    /// l'interpolation remplace bien le saut de 2,15 m par annonce.
    ///
    /// Pourquoi cette sonde existe : la mesure du §30 a établi ce qu'une annonce **coûterait** (2,15 m à
    /// 10 Hz pour un Seamoth à 20 m/s), pas ce qu'un client en fait. Une interpolation correcte doit
    /// produire un déplacement continu : un pas par image petit (≈ 0,33 m à 60 fps), jamais un saut de la
    /// taille d'une annonce. C'est exactement ce que cette sonde compte, image par image, sur le véhicule
    /// suivi le plus proche.
    ///
    /// Ce qu'elle n'affirme **pas** : que la conduite est synchronisée. M4 ne transporte que la position ;
    /// aucun message ne remonte du client vers le serveur pour piloter un véhicule.
    ///
    /// Activée par `-coopdebug=vehicle` côté **client** (voir <see cref="CoopClientPlugin"/>), en même
    /// temps que la sonde de l'hôte qui déplace le Seamoth.
    /// </summary>
    public class VehicleClientProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Véhicule Client");

        /// <summary>Attente maximale (s) du monde, de la connexion, puis d'un véhicule suivi.</summary>
        private const float WorldTimeoutSeconds = 240f;

        /// <summary>Attente maximale (s) d'un véhicule suivi après l'entrée dans le monde.</summary>
        private const float VehicleTimeoutSeconds = 180f;

        /// <summary>Durée (s) de la fenêtre d'échantillonnage.</summary>
        private const float WindowSeconds = 12f;

        /// <summary>Déplacement minimal (m) pour que la fenêtre décrive un véhicule en mouvement.</summary>
        private const float RequiredDisplacementMeters = 5f;

        /// <summary>
        /// Pas par image maximal accepté (m). À 60 fps et 20 m/s, l'interpolation produit ≈ 0,33 m ;
        /// une annonce appliquée d'un coup donnerait 2,15 m. Le seuil de 1 m laisse passer une baisse de
        /// framerate (30 fps → 0,66 m) et sépare nettement les deux comportements.
        /// </summary>
        private const float MaxFrameStepMeters = 1.0f;

        /// <summary>
        /// Vitesse (m/s) au-delà de laquelle un pas d'image n'est plus un glissement mais un saut.
        ///
        /// Même plafond que l'interpolation elle-même (`RemoteVehicleManager` ne rattrape jamais plus vite :
        /// la durée d'un segment vaut au moins `distance / 40 m/s`). Un seuil **absolu** en mètres dépend
        /// silencieusement du framerate : `1 m` suppose 0,025 s d'image à 40 m/s, alors que 137 fps laissent
        /// 0,007 s. Mesuré à l'étape K : une rafale de quatre annonces a créé un rattrapage de `8,15 m` en
        /// `0,2 s`, et une image de `45 ms` a donc avancé de `1,80 m` — à `40 m/s`, exactement la vitesse
        /// prévue. Le pas est donc jugé faux s'il dépasse **soit** le seuil absolu **soit** la vitesse
        /// maximale que l'interpolation s'autorise.
        /// </summary>
        private const float MaxCatchUpSpeedMetersPerSecond = 40f;

        /// <summary>Pas moyen maximal accepté (m) : même raison, en moyenne.</summary>
        private const float MaxMeanStepMeters = 0.6f;

        /// <summary>
        /// Délai (s) après lequel la sonde fabrique elle-même un Seamoth local si aucun véhicule n'est
        /// suivi.
        ///
        /// Pourquoi : le Seamoth de l'hôte est fabriqué **dans le monde du serveur** par la commande
        /// console. La sauvegarde de test n'en contient aucun, donc le monde du client n'en a pas la
        /// contrepartie — et un suivi sans objet local n'a rien à interpoler. La sonde fabrique donc la
        /// contrepartie par le même chemin (`spawn seamoth`), à la même position déterministe que l'hôte
        /// (`Player.main` est à la position sauvegardée des deux côtés, `-8 m` sous lui), ce qui donne la
        /// même clé de repli et fait porter la mesure sur le **vrai** chemin : clé, appariement, annonce,
        /// interpolation.
        /// </summary>
        private const float LocalSpawnAfterSeconds = 25f;

        /// <summary>Attente maximale (s) de la création du véhicule après la commande console.</summary>
        private const float SpawnWaitSeconds = 5f;

        /// <summary>
        /// Fabrique la contrepartie locale du véhicule de l'hôte, par la commande console du jeu, puis la
        /// place à la position déterministe où l'hôte a placé le sien. C'est du matériel de mesure, et
        /// c'est dit dans le journal.
        /// </summary>
        private static IEnumerator TrySpawnLocalCounterpart()
        {
            if (Player.main == null || Player.mainObject == null)
            {
                yield break;
            }

            var command = "spawn " + TechType.Seamoth.AsString(false);
            Log.LogWarning(
                $"[sonde] véhicule client : aucun véhicule suivi après {LocalSpawnAfterSeconds:F0}s — fabrication " +
                $"d'une contrepartie locale par la commande du jeu « {command} ».");

            try
            {
                DevConsole.SendConsoleCommand(command);
            }
            catch (System.Exception exception)
            {
                Log.LogError($"[sonde] véhicule client : la commande console a échoué ({exception.GetType().Name}: {exception.Message}).");
                yield break;
            }

            // La création est **différée** par le jeu : une lecture immédiate ne voit rien (mesuré :
            // « aucun Vehicle » à l'instant de la commande, puis un `SeaMoth(Clone)` une seconde plus tard).
            // On attend donc au lieu de conclure — sinon la contrepartie n'est pas placée à l'ancre et le
            // rattachement dépend du hasard d'un passage dans le rayon.
            var vehicles = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            var deadline = Time.realtimeSinceStartup + SpawnWaitSeconds;
            while ((vehicles == null || vehicles.Length == 0) && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                vehicles = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            }

            if (vehicles == null || vehicles.Length == 0)
            {
                Log.LogError(
                    $"[sonde] véhicule client : aucun Vehicle {SpawnWaitSeconds:F0}s après la commande — " +
                    "la contrepartie n'a pas pu être placée.");
                yield break;
            }

            var anchor = Player.main.transform.position + Vector3.down * 8f;
            var placed = 0;
            for (var i = 0; i < vehicles.Length; i++)
            {
                var vehicle = vehicles[i];
                if (vehicle == null || vehicle.transform == null)
                {
                    continue;
                }

                vehicle.transform.position = anchor;
                vehicle.transform.rotation = Quaternion.identity;
                placed++;
            }

            Log.LogInfo(
                $"[sonde] véhicule client : {placed} véhicule(s) local(aux) placé(s) à " +
                $"{anchor.ToString("F2")} (même ancre que l'hôte, pour que la clé de repli coïncide).");
        }

        private IEnumerator Start()
        {
            // Même précaution que côté hôte : `Player.main` existe dans les scènes de menu, et une
            // contrepartie fabriquée là ne serait pas dans le monde (elle serait même à des centaines de
            // mètres de l'ancre attendue). Le monde est attendu par son nom de scène.
            // `LargeWorldStreamer.main` est le signal « monde chargé » (la scène, elle, peut déjà s'appeler
            // `Main` alors que le joueur est encore à la position de la scène de menu). Le délai qui suit
            // laisse le jeu placer le joueur à sa position sauvegardée.
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while ((ClientNetworkBehaviour.Current == null || ClientNetworkBehaviour.Current.Vehicles == null ||
                    Player.main == null || LargeWorldStreamer.main == null) &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            yield return new WaitForSecondsRealtime(3f);

            var network = ClientNetworkBehaviour.Current;
            if (network == null || network.Vehicles == null || Player.main == null ||
                LargeWorldStreamer.main == null)
            {
                Log.LogError(
                    $"[sonde] véhicule client : verdict=False — pas de monde/connexion après {WorldTimeoutSeconds:F0}s, " +
                    "rien n'a été mesuré.");
                yield break;
            }

            var vehicles = network.Vehicles;
            var waitDeadline = Time.realtimeSinceStartup + VehicleTimeoutSeconds;
            var spawnAttempted = false;
            var spawnAt = Time.realtimeSinceStartup + LocalSpawnAfterSeconds;

            while (vehicles.TrackedCount == 0 && Time.realtimeSinceStartup < waitDeadline)
            {
                if (!spawnAttempted && Time.realtimeSinceStartup >= spawnAt)
                {
                    spawnAttempted = true;
                    yield return TrySpawnLocalCounterpart();
                }

                yield return new WaitForSecondsRealtime(1f);
            }

            if (vehicles.TrackedCount == 0)
            {
                Log.LogError(
                    $"[sonde] véhicule client : verdict=False — aucun véhicule suivi {VehicleTimeoutSeconds:F0}s après " +
                    $"l'entrée dans le monde (état : {vehicles.Describe()}).");
                yield break;
            }

            var vehicle = vehicles.NearestTrackedVehicle;
            if (vehicle == null)
            {
                Log.LogError("[sonde] véhicule client : verdict=False — un véhicule est compté comme suivi mais aucun objet n'est lisible.");
                yield break;
            }

            Log.LogInfo(
                $"[sonde] véhicule client : fenêtre de {WindowSeconds:F0}s sur '{vehicle.name}' (" +
                $"{vehicles.Describe()}).");

            var previous = vehicle.transform.position;
            var samples = 0;
            var sum = 0.0;
            var max = 0f;
            var maxSpeed = 0f;
            var maxStepDelta = 0f;
            var aboveOneMeter = 0;
            var displacement = 0f;
            var lost = 0;
            var started = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - started < WindowSeconds)
            {
                yield return null;

                vehicle = vehicles.NearestTrackedVehicle;
                if (vehicle == null)
                {
                    // Le véhicule est sorti du rayon ou a été détruit : la fenêtre ne décrira plus rien.
                    lost++;
                    if (lost > 60)
                    {
                        break;
                    }

                    continue;
                }

                var position = vehicle.transform.position;
                var step = Vector3.Distance(previous, position);
                previous = position;

                // Durée **de l'image** : c'est elle qui dit si un pas est un saut ou un glissement rapide.
                // Une image longue (chargement d'assets, GC) produit mécaniquement un pas plus grand sans que
                // la synchronisation soit en cause ; sa vitesse implicite, elle, reste dans le plafond.
                var delta = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
                var speed = step / delta;

                samples++;
                sum += step;
                displacement += step;
                if (step > max)
                {
                    max = step;
                    maxStepDelta = delta;
                }

                if (speed > maxSpeed)
                {
                    maxSpeed = speed;
                }

                if (step >= 1f)
                {
                    aboveOneMeter++;
                }
            }

            var elapsed = Time.realtimeSinceStartup - started;
            var mean = samples > 0 ? sum / samples : 0;
            var fps = elapsed > 0.001f ? samples / elapsed : 0;

            var enoughSamples = samples >= 120;
            var moved = displacement >= RequiredDisplacementMeters;
            // Le pas le plus grand est jugé avec **sa** durée d'image, pas avec la vitesse maximale d'une
            // autre image : c'est le rapport des deux qui dit si le véhicule a avancé plus vite qu'il ne
            // peut. Mesuré deux fois de suite : un pas de `1,06 m` sur une image de `41 ms` vaut `26 m/s`
            // (normal), tandis qu'une image de `7 ms` qui avance de `0,68 m` vaut `97 m/s` (un saut).
            var maxStepSpeed = maxStepDelta > 0f ? max / maxStepDelta : 0f;
            var noJump = max <= MaxFrameStepMeters || maxStepSpeed <= MaxCatchUpSpeedMetersPerSecond;
            var smoothMean = mean <= MaxMeanStepMeters && mean > 0.01f;
            var verdict = enoughSamples && moved && noJump && smoothMean;

            // — Énergie et modules (v15, M4) ————————————————————————————————————————
            //
            // Deux faits distincts, et c'est pour ça qu'il y a deux verdicts : le message a-t-il été
            // **reçu** (transport), et la copie locale porte-t-elle le **même** ensemble (application) ?
            // Les confondre ferait passer un message reçu pour un module appliqué.
            var serverSet = vehicles.LastUpgradesServer;
            var localSet = vehicles.LastUpgradesLocal;
            var modulesReceived = vehicles.UpgradesMessages > 0;
            var serverHasModules = !string.Equals(serverSet, "aucun", StringComparison.Ordinal);
            var transportVerdict = modulesReceived && serverHasModules;

            // L'ensemble **actuel** de la copie, relu ici : `localSet` est celui lu *avant* la demande
            // d'application, et le comparer au serveur dirait toujours « divergent » juste après un ajout —
            // c'est le défaut mesuré au run du 2026-10-20 à 17:42, où `appliqués=1` coexistait avec un
            // verdict d'application faux.
            var localNow = VehicleModules.Describe(VehicleModules.Read(vehicle), out _);
            var applicationVerdict = transportVerdict &&
                string.Equals(serverSet, localNow, StringComparison.Ordinal);

            Log.LogInfo(
                $"[sonde] véhicule modules : messages={vehicles.UpgradesMessages} serveur=[{serverSet}] " +
                $"local avant=[{localSet}] local maintenant=[{localNow}] " +
                $"divergences={vehicles.UpgradesDivergences} " +
                $"charges appliquées={vehicles.EnergiesApplied} — transport={transportVerdict} " +
                $"application={applicationVerdict} (le premier est la réception, le second l'état de la copie).");

            Log.LogInfo(
                $"[sonde] véhicule client : verdict={verdict} — '{vehicle.name}' suivi sur {samples} image(s) en " +
                $"{elapsed.ToString("F2", CultureInfo.InvariantCulture)}s (fps≈{fps:F0}), déplacement=" +
                $"{displacement.ToString("F2", CultureInfo.InvariantCulture)} m, pas par image max=" +
                $"{max.ToString("F2", CultureInfo.InvariantCulture)} m / moyen={mean.ToString("F2", CultureInfo.InvariantCulture)} m, " +
                $"pas max sur une image de {maxStepDelta * 1000f:F0} ms (soit " +
                $"{maxStepSpeed.ToString("F1", CultureInfo.InvariantCulture)} m/s ; vitesse max d'une image=" +
                $"{maxSpeed.ToString("F1", CultureInfo.InvariantCulture)} m/s), " +
                $"images ≥1 m={aboveOneMeter}, pertes de suivi={lost} — seuils : images≥120, déplacement≥" +
                $"{RequiredDisplacementMeters:F0} m, pas max≤{MaxFrameStepMeters:F1} m «ou» vitesse≤" +
                $"{MaxCatchUpSpeedMetersPerSecond:F0} m/s, pas moyen≤{MaxMeanStepMeters:F1} m. " +
                $"État du suivi : {vehicles.Describe()}");
        }
    }
}
