using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de **disparition d'entité** (M6, protocole v11), côté client.
    ///
    /// Elle n'envoie rien : elle constate qu'un véhicule **créé** par l'annonce du serveur (v10) a ensuite
    /// **disparu** parce que le serveur l'a détruit et l'a annoncé (v11). Le critère n'est pas « un message
    /// est arrivé » mais « l'objet n'est plus dans le monde local, et il y était avant » — c'est la seule
    /// formulation qui distingue un retrait appliqué d'un message simplement reçu.
    /// </summary>
    internal sealed class EntityDespawnProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Disparition Sonde");

        private const float WorldTimeoutSeconds = 180f;
        private const float TimeoutSeconds = 180f;

        private IEnumerator Start()
        {
            Log.LogWarning("[sonde] disparition activée (-coopdebug=despawn) — attente d'un retrait du serveur...");

            var worldDeadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (!AutoLoadBehaviour.WorldLoaded && Time.realtimeSinceStartup < worldDeadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            if (!AutoLoadBehaviour.WorldLoaded)
            {
                Log.LogError(
                    $"[sonde] disparition : verdict=False — aucun monde chargé après {WorldTimeoutSeconds:F0}s.");
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (EntityDespawner.VehiclesRemoved == 0 &&
                   EntityDespawner.AbsentCount == 0 &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSeconds(0.5f);
            }

            if (EntityDespawner.VehiclesRemoved == 0 && EntityDespawner.AbsentCount == 0)
            {
                Log.LogError(
                    $"[sonde] disparition : verdict=False — aucune annonce de retrait en {TimeoutSeconds:F0}s " +
                    $"({EntityDespawner.Summary()}). Le serveur a détruit une entité sans l'annoncer.");
                yield break;
            }

            // Deux issues sont acceptables, et la seconde est une vraie propriété, pas une tolérance :
            //   - ce client avait créé l'objet : il doit avoir été détruit ici ;
            //   - ce client ne l'a **jamais** eu (retrait reçu avant son chargement de monde) : il ne doit
            //     pas l'avoir fabriqué après coup. C'est la course que `EntitySpawner` évite via `IsGone`.
            var key = EntityDespawner.LastRemovedKey;
            var stillPresent = EntityDespawner.StillPresent(key, EntityKind.Vehicle);
            var hadIt = EntityDespawner.VehiclesRemoved > 0;

            // Laisser à la file d'apparitions le temps de se vider : un fantôme créé après le retrait
            // apparaîtrait ici, et c'est précisément ce que la seconde branche affirme impossible.
            yield return new WaitForSeconds(5f);

            var ghost = EntitySpawner.SpawnedCount > 0 && stillPresent;
            var verdict = !stillPresent && key != 0L && !ghost;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] disparition : verdict={0} — {1} ; cette instance {2}, dernière clé retirée=0x{3:x16} " +
                    "encore présente={4}, entités créées ici={5} (un fantôme après le retrait serait un échec)",
                    verdict,
                    EntityDespawner.Summary(),
                    hadIt ? "avait l'objet et l'a détruit" : "ne l'a jamais eu, et ne l'a pas recréé",
                    key,
                    stillPresent,
                    EntitySpawner.SpawnedCount));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] disparition : l'entité annoncée disparue est **encore** dans le monde local — " +
                    "le retrait n'est qu'un message, pas un effet.");
            }
        }
    }
}
