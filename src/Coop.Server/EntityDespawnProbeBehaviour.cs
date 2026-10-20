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
    /// Sonde de **disparition d'entité** (M6, protocole v11), côté hôte.
    ///
    /// Elle fabrique un véhicule qui n'existe pas dans la sauvegarde — donc que les clients n'ont pas — puis
    /// le **détruit** une fois qu'ils ont eu le temps de le créer. C'est le scénario que la v10 ne savait pas
    /// terminer : l'objet existait partout, et le détruire ici ne changeait rien ailleurs.
    ///
    /// Le délai avant destruction n'est pas décoratif : détruire avant que les clients aient créé l'objet ne
    /// prouverait rien (ils recevraient un retrait pour une entité qu'ils n'ont jamais eue). La sonde attend
    /// donc explicitement, et dit combien de temps.
    /// </summary>
    internal sealed class EntityDespawnProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Disparition Hôte");

        private const float WorldTimeoutSeconds = 180f;
        private const float ClientWaitSeconds = 120f;

        /// <summary>
        /// Nombre de clients attendus avant de fabriquer : deux pour l'étape O (les deux doivent avoir le
        /// véhicule avant sa destruction). L'étape Q n'en attend **qu'un**, pour que le second arrive
        /// **après** le retrait et ne reçoive que le rejeu (P2) — `COOP_DESPAWN_CLIENTS=1`.
        /// </summary>
        private const int DefaultExpectedClients = 2;
        private const float FirstAttemptSeconds = 10f;
        private const float RetrySeconds = 5f;
        private const float SpawnTimeoutSeconds = 60f;

        /// <summary>
        /// Délai par défaut entre la fabrication et la destruction : assez pour qu'un client qui charge
        /// encore son monde ait créé l'objet (mesuré en N : ≈ 3 s après la réception, une fois le monde
        /// chargé), et assez court pour que l'étape reste bornée. `COOP_DESPAWN_DELAY` le raccourcit pour
        /// l'étape Q, où le client tardif ne doit surtout pas être là au moment de la destruction.
        /// </summary>
        private const float DefaultDestroyDelaySeconds = 40f;

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
                    $"[sonde] disparition : verdict=False — aucun monde chargé après {WorldTimeoutSeconds:F0}s.");
                yield break;
            }

            var before = SnapshotKeys();
            Log.LogInfo($"[sonde] disparition : {before.Count} véhicule(s) avant la fabrication.");

            // Fabriquer après que les clients soient entrés dans le monde, sinon le retrait s'adresserait à
            // des clients qui n'ont jamais reçu la création. On attend **deux** clients quand il y en a deux
            // à attendre : mesuré à l'étape O, un délai fixe de 30 s après le **premier** client ne laissait
            // que 13 s au second, qui recevait alors le retrait avant d'avoir chargé son monde.
            var target = ExpectedClients();
            var destroyDelay = DestroyDelaySeconds();
            var clientDeadline = Time.realtimeSinceStartup + ClientWaitSeconds;
            while (CoopServerPlugin.Network != null &&
                   CoopServerPlugin.Network.AcceptedClients < target &&
                   Time.realtimeSinceStartup < clientDeadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            var clients = CoopServerPlugin.Network != null ? CoopServerPlugin.Network.AcceptedClients : 0;
            Log.LogInfo($"[sonde] disparition : {clients} client(s) connecté(s) avant la fabrication (attendu {target}).");

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
                        Log.LogWarning($"[sonde] disparition : commande console « {command} » (tentative {attempts}).");
                    }
                    catch (Exception exception)
                    {
                        Log.LogError(
                            $"[sonde] disparition : la commande console a échoué ({exception.GetType().Name}: " +
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
                    "[sonde] disparition : verdict=False — aucun véhicule nouveau après " +
                    $"{attempts} tentative(s) de commande console.");
                yield break;
            }

            var key = VehicleIdentity.KeyOf(created, out var mechanism);
            var name = created.gameObject != null ? created.gameObject.name : "?";

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] disparition : véhicule fabriqué — '{0}' (clé=0x{1:x16}, {2}) ; destruction dans {3:F0}s.",
                    name,
                    key,
                    mechanism,
                    destroyDelay));

            yield return new WaitForSecondsRealtime(destroyDelay);

            if (created == null || created.gameObject == null)
            {
                Log.LogError(
                    $"[sonde] disparition : verdict=False — '{name}' (clé=0x{key:x16}) a disparu avant la " +
                    "destruction : la mesure porterait sur autre chose que ce que la sonde a créé.");
                yield break;
            }

            var position = created.transform != null ? created.transform.position : Vector3.zero;
            UnityEngine.Object.Destroy(created.gameObject);

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] disparition : véhicule '{0}' (clé=0x{1:x16}) détruit après {2:F0}s " +
                    "(pos=({3:F1}, {4:F1}, {5:F1})) — le serveur doit l'annoncer aux clients.",
                    name,
                    key,
                    destroyDelay,
                    position.x,
                    position.y,
                    position.z));
        }

        /// <summary>Clients à attendre avant de fabriquer (`COOP_DESPAWN_CLIENTS`, défaut 2).</summary>
        private static int ExpectedClients()
        {
            var raw = Environment.GetEnvironmentVariable("COOP_DESPAWN_CLIENTS");
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : DefaultExpectedClients;
        }

        /// <summary>Délai fabrication → destruction (`COOP_DESPAWN_DELAY`, défaut 40 s).</summary>
        private static float DestroyDelaySeconds()
        {
            var raw = Environment.GetEnvironmentVariable("COOP_DESPAWN_DELAY");
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 1f
                ? value
                : DefaultDestroyDelaySeconds;
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
