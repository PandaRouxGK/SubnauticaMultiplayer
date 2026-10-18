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
    /// Sonde de **création d'entité** (M6, protocole v10), côté hôte.
    ///
    /// Elle fabrique un véhicule **qui n'existait pas dans la sauvegarde** — par la commande console du
    /// jeu, comme le ferait un joueur — et laisse le serveur annoncer son apparition. C'est le seul moyen
    /// d'obtenir une entité que les clients n'ont pas : un véhicule de la sauvegarde est déjà chez tout le
    /// monde, et ne prouverait rien de la création.
    ///
    /// Elle ne détruit pas ce qu'elle fabrique : le message de retrait n'existe pas encore (M6), et une
    /// destruction locale laisserait les clients avec un objet fantôme. C'est dit dans `PLAN.md` §41.
    /// </summary>
    internal sealed class EntitySpawnProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Apparition Hôte");

        private const float WorldTimeoutSeconds = 180f;
        private const float FirstAttemptSeconds = 15f;
        private const float ClientWaitSeconds = 120f;
        private const float RetrySeconds = 5f;
        private const float SpawnTimeoutSeconds = 60f;

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
                    $"[sonde] apparition : verdict=False — aucun monde chargé après {WorldTimeoutSeconds:F0}s.");
                yield break;
            }

            // Les clés présentes **avant** la fabrication : c'est la référence pour reconnaître la nouvelle.
            var before = SnapshotKeys();
            Log.LogInfo($"[sonde] apparition : {before.Count} véhicule(s) avant la fabrication.");

            // Fabriquer **après** qu'un client soit entré dans le monde, et non à l'aveugle : c'est ce qui
            // rend le cas intéressant reproductible. Le premier client voit l'annonce en direct, le second —
            // lancé ensuite par le harnais — se connecte après coup et ne peut la recevoir que si le serveur
            // la rejoue. Fabriquer avant toute connexion testerait la diffusion à personne.
            var clientDeadline = Time.realtimeSinceStartup + ClientWaitSeconds;
            while (CoopServerPlugin.Network != null &&
                   CoopServerPlugin.Network.AcceptedClients == 0 &&
                   Time.realtimeSinceStartup < clientDeadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            var clients = CoopServerPlugin.Network != null ? CoopServerPlugin.Network.AcceptedClients : 0;
            Log.LogInfo($"[sonde] apparition : {clients} client(s) connecté(s) avant la fabrication.");

            yield return new WaitForSecondsRealtime(FirstAttemptSeconds);

            var command = "spawn " + TechType.Seamoth.AsString(false);
            var spawnDeadline = Time.realtimeSinceStartup + SpawnTimeoutSeconds;
            var attempts = 0;
            var nextAttempt = 0f;
            Vehicle created = null;

            while (created == null && Time.realtimeSinceStartup < spawnDeadline)
            {
                if (attempts == 0 || Time.realtimeSinceStartup >= nextAttempt)
                {
                    attempts++;
                    nextAttempt = Time.realtimeSinceStartup + RetrySeconds;

                    try
                    {
                        DevConsole.SendConsoleCommand(command);
                        Log.LogWarning($"[sonde] apparition : commande console « {command} » (tentative {attempts}).");
                    }
                    catch (Exception exception)
                    {
                        Log.LogError(
                            $"[sonde] apparition : la commande console a échoué ({exception.GetType().Name}: " +
                            $"{exception.Message}) — tentative {attempts}.");
                        yield break;
                    }
                }

                yield return new WaitForSecondsRealtime(1f);
                created = FindNewVehicle(before);
            }

            if (created == null)
            {
                Log.LogError(
                    "[sonde] apparition : verdict=False — aucun véhicule nouveau après " +
                    $"{attempts} tentative(s) de commande console.");
                yield break;
            }

            var key = VehicleIdentity.KeyOf(created, out var mechanism);

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] apparition : véhicule fabriqué — '{0}' (clé=0x{1:x16}, {2}) après {3} tentative(s) ; " +
                    "le serveur doit l'annoncer aux clients.",
                    created.gameObject.name,
                    key,
                    mechanism,
                    attempts));
        }

        private static HashSet<long> SnapshotKeys()
        {
            var keys = new HashSet<long>();
            foreach (var vehicle in UnityEngine.Object.FindObjectsOfType<Vehicle>())
            {
                if (vehicle == null || vehicle.gameObject == null || !vehicle.gameObject.scene.IsValid())
                {
                    continue;
                }

                var key = VehicleIdentity.KeyOf(vehicle, out _);
                if (key != 0L)
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private static Vehicle FindNewVehicle(HashSet<long> before)
        {
            foreach (var vehicle in UnityEngine.Object.FindObjectsOfType<Vehicle>())
            {
                if (vehicle == null || vehicle.gameObject == null || !vehicle.gameObject.scene.IsValid())
                {
                    continue;
                }

                var key = VehicleIdentity.KeyOf(vehicle, out _);
                if (key != 0L && !before.Contains(key))
                {
                    return vehicle;
                }
            }

            return null;
        }
    }
}
