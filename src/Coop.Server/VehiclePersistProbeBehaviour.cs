using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Logging;
using Coop.Shared;
using UnityEngine;

namespace Coop.Server
{
    /// <summary>
    /// Fabrique un Seamoth **dans la sauvegarde de l'hôte**, activé par <c>-coopdebug=vpersist</c>.
    ///
    /// Pourquoi cette sonde existe : la preuve d'interpolation de M4 (étape J) utilise un véhicule
    /// fabriqué **des deux côtés** par la commande console. Un `spawn` créé à l'exécution reçoit un
    /// `UniqueIdentifier` neuf dans chaque instance : les clés diffèrent par construction, et le suivi
    /// passe par le rattachement par proximité (`1` fois, `29,25 m`, journalisé). Ce chemin est prévu et
    /// mesuré, mais ce n'est **pas** la clé nominale : sur une vraie partie, le véhicule est dans le
    /// fichier de sauvegarde, les deux instances lisent le même identifiant, et aucun rattachement n'est
    /// nécessaire. Cette sonde-ci met le monde dans cet état, une fois, pour que l'étape **K** le mesure.
    ///
    /// Ce qu'elle fait :
    /// 1. attend le monde (`AutoLoadBehaviour.WorldLoaded`, plus 3 s) — `Player.main` et
    ///    `LargeWorldStreamer.main` existent dans les scènes de menu, et s'y fier a fabriqué le Seamoth à
    ///    la position du joueur de menu `(2,08, -3,78, -0,69)`, sauvegardé à 400 m de la capsule : la mesure
    ///    à deux clients a alors rattaché le véhicule par proximité (défaut mesuré au §35) ;
    /// 2. cherche un véhicule proche ; s'il n'y en a pas, le fabrique par la commande console du jeu
    ///    (`spawn seamoth`, **réessayée** : une commande envoyée avant la fin du chargement ne crée rien) ;
    /// 3. le place à l'ancre déterministe `Player.main + (0, -8, 0)` et relève sa clé (`VehicleIdentity`) ;
    /// 4. déclenche la sauvegarde de l'hôte (`IngameMenu.SaveGame()`, le chemin du bouton) et attend que
    ///    le dossier temporaire change — même instrument que <see cref="HostSaveProbeBehaviour"/> ;
    /// 5. **ne détruit pas** le véhicule : c'est précisément ce qui doit rester dans la sauvegarde.
    ///
    /// Ce qu'elle ne prouve pas : que le fichier du **slot** a été remplacé (le jeu le fait après coup) ni
    /// que le véhicule sera relu à l'identique au prochain chargement — c'est l'étape K qui le mesure, en
    /// faisant tourner deux clients sur cette sauvegarde.
    /// </summary>
    public class VehiclePersistProbeBehaviour : MonoBehaviour
    {
        // Préfixe explicite : `using UnityEngine` apporte aussi un `Logger`.
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Véhicule Persist");

        /// <summary>Attente maximale (s) du monde.</summary>
        private const float WorldTimeoutSeconds = 240f;

        /// <summary>Attente maximale (s) de l'apparition du véhicule après la commande console.</summary>
        private const float SpawnTimeoutSeconds = 40f;

        /// <summary>
        /// Délai (s) après la dernière commande avant de considérer l'échec. Une commande envoyée trop tôt
        /// est acceptée sans rien fabriquer ; on réessaie toutes les 5 s.
        /// </summary>
        private const float SpawnRetrySeconds = 5f;

        /// <summary>Attente maximale (s) de l'écriture disque après le déclenchement de la sauvegarde.</summary>
        private const float SaveTimeoutSeconds = 90f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (!AutoLoadBehaviour.WorldLoaded && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (!AutoLoadBehaviour.WorldLoaded || Player.main == null)
            {
                Log.LogError(
                    $"[sonde] véhicule persistant : verdict=False — aucun monde chargé après {WorldTimeoutSeconds:F0}s, " +
                    "rien n'a été fabriqué ni sauvegardé.");
                yield break;
            }

            // Laisse le jeu placer le joueur à sa position sauvegardée : c'est là que l'ancre est prise.
            yield return new WaitForSecondsRealtime(3f);

            var host = Player.main;
            var anchor = host.transform.position + Vector3.down * 8f;

            var vehicle = FindNearestVehicle(anchor, out var distance);
            var spawned = false;
            var attempts = 0;

            if (vehicle == null)
            {
                var command = "spawn " + TechType.Seamoth.AsString(false);
                Log.LogWarning(
                    $"[sonde] véhicule persistant : aucun véhicule près de l'ancre {anchor.ToString("F2")} — " +
                    $"commande console « {command} ».");

                var spawnDeadline = Time.realtimeSinceStartup + SpawnTimeoutSeconds;
                var nextAttempt = 0f;

                while (vehicle == null && Time.realtimeSinceStartup < spawnDeadline)
                {
                    if (attempts == 0 || Time.realtimeSinceStartup >= nextAttempt)
                    {
                        attempts++;
                        nextAttempt = Time.realtimeSinceStartup + SpawnRetrySeconds;

                        try
                        {
                            DevConsole.SendConsoleCommand(command);
                        }
                        catch (Exception exception)
                        {
                            Log.LogError(
                                $"[sonde] véhicule persistant : la commande console a échoué " +
                                $"({exception.GetType().Name}: {exception.Message}) — tentative {attempts}.");
                            yield break;
                        }
                    }

                    yield return new WaitForSecondsRealtime(1f);
                    vehicle = FindNearestVehicle(anchor, out distance);
                }

                spawned = vehicle != null;
                Log.LogInfo(
                    $"[sonde] véhicule persistant : {attempts} tentative(s) de commande console, " +
                    $"{(spawned ? "véhicule apparu" : "aucun véhicule")}.");
            }
            else
            {
                Log.LogInfo(
                    $"[sonde] véhicule persistant : '{vehicle.name}' déjà présent à " +
                    $"{distance.ToString("F1", CultureInfo.InvariantCulture)} m de l'ancre — il sera sauvegardé " +
                    "tel quel (aucune fabrication).");
            }

            if (vehicle == null)
            {
                Log.LogError(
                    "[sonde] véhicule persistant : verdict=False — aucun véhicule à sauvegarder.");
                yield break;
            }

            // Place l'ancre **avant** la sauvegarde : c'est là que l'étape K retrouvera le véhicule, et
            // c'est la position sur laquelle la clé de repli serait calculée s'il n'avait pas d'identifiant.
            var rigidbody = vehicle.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.isKinematic = true;
                rigidbody.velocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
            }

            vehicle.transform.position = anchor;
            vehicle.transform.rotation = Quaternion.identity;

            var key = VehicleIdentity.KeyOf(vehicle, out var mechanism);
            Log.LogInfo(
                $"[sonde] véhicule persistant : '{vehicle.name}' placé à {anchor.ToString("F2")}, " +
                $"clé={key:x16} ({mechanism}) — {VehicleIdentity.Describe(vehicle)}.");

            // Laisse la diffusion/le cache d'identités voir le véhicule à sa position finale, puis sauvegarde.
            yield return new WaitForSecondsRealtime(3f);

            var root = string.Empty;
            try
            {
                root = SaveLoadManager.GetTemporarySavePathRoot();
            }
            catch (Exception exception)
            {
                Log.LogError($"[sonde] véhicule persistant : dossier temporaire illisible ({exception.Message}).");
            }

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Log.LogError(
                    $"[sonde] véhicule persistant : verdict=False — dossier temporaire introuvable (« {root} »).");
                yield break;
            }

            var before = HostSaveProbeBehaviour.Snapshot(root);

            var menu = IngameMenu.main;
            if (menu == null)
            {
                menu = FindObjectOfType<IngameMenu>();
            }

            if (menu == null)
            {
                foreach (var candidate in Resources.FindObjectsOfTypeAll<IngameMenu>())
                {
                    if (candidate != null && candidate.gameObject.scene.IsValid())
                    {
                        menu = candidate;
                        break;
                    }
                }
            }

            if (menu == null)
            {
                Log.LogError(
                    "[sonde] véhicule persistant : verdict=False — aucun IngameMenu, la sauvegarde n'a pas été déclenchée.");
                yield break;
            }

            Log.LogWarning(
                $"[sonde] véhicule persistant : déclenchement de IngameMenu.SaveGame() " +
                $"({before.Count} fichier(s) dans le dossier temporaire avant, timeScale={Time.timeScale:F2}).");

            try
            {
                menu.SaveGame();
            }
            catch (Exception exception)
            {
                Log.LogError($"[sonde] véhicule persistant : exception pendant SaveGame() : {exception}");
            }

            var changed = new List<string>();
            var saveDeadline = Time.realtimeSinceStartup + SaveTimeoutSeconds;
            while (Time.realtimeSinceStartup < saveDeadline)
            {
                yield return new WaitForSecondsRealtime(2f);
                changed = HostSaveProbeBehaviour.Diff(before, HostSaveProbeBehaviour.Snapshot(root));
                if (changed.Count > 0)
                {
                    break;
                }
            }

            if (changed.Count > 0)
            {
                Log.LogInfo(
                    $"[sonde] véhicule persistant : {changed.Count} fichier(s) écrit(s) ou modifié(s) — " +
                    $"{string.Join(", ", changed.GetRange(0, Math.Min(5, changed.Count)))}");
            }

            var verdict = changed.Count > 0 && key != 0L;
            Log.LogInfo(
                $"[sonde] véhicule persistant : verdict={verdict} — '{vehicle.name}' " +
                $"({(spawned ? "fabriqué par la sonde" : "déjà présent")}), clé=0x{key:x16} ({mechanism}) " +
                $"conservée dans la sauvegarde (aucune destruction), écritures observées={changed.Count}, " +
                $"ancre={anchor.ToString("F2")}, joueur={host.transform.position.ToString("F2")}.");

            // Le Rigidbody reste cinématique : le véhicule ne doit pas dériver entre la sauvegarde et l'arrêt
            // de l'instance. L'étape K le retrouvera à l'ancre.
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
    }
}
