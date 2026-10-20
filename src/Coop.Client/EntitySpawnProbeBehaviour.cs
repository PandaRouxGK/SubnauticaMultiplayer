using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de **création d'entité** (M6, protocole v10), côté client.
    ///
    /// Elle n'envoie rien : elle **attend** une annonce du serveur (`EntitySpawn`), vérifie qu'un objet a
    /// réellement été créé, et qu'il porte la clé annoncée — c'est-à-dire que le suivi de véhicules peut
    /// l'apparier. Une entité créée mais dont la clé diffère serait invisible pour tout le reste du mod :
    /// c'est précisément ce que cette sonde refuse de laisser passer.
    /// </summary>
    internal sealed class EntitySpawnProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Apparition Sonde");

        private const float WorldTimeoutSeconds = 180f;
        private const float TimeoutSeconds = 150f;

        private IEnumerator Start()
        {
            Log.LogWarning("[sonde] apparition activée (-coopdebug=spawn) — attente d'une annonce du serveur...");

            // Le monde d'abord : une annonce reçue pendant le menu est **gardée** par `EntitySpawner`
            // (rien n'est créé avant `WorldLoaded`), donc attendre ici ne perd rien et évite de mesurer un
            // objet fabriqué dans la scène du menu, que le chargement décharge.
            var worldDeadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (!AutoLoadBehaviour.WorldLoaded && Time.realtimeSinceStartup < worldDeadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            if (!AutoLoadBehaviour.WorldLoaded)
            {
                Log.LogError(
                    $"[sonde] apparition : verdict=False — aucun monde chargé après {WorldTimeoutSeconds:F0}s.");
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (EntitySpawner.SpawnedCount == 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSeconds(0.5f);
            }

            if (EntitySpawner.SpawnedCount == 0)
            {
                Log.LogError(
                    $"[sonde] apparition : verdict=False — aucune entité créée en {TimeoutSeconds:F0}s " +
                    $"(annonces reçues : {EntitySpawner.SkippedCount + EntitySpawner.FailedCount} écartée(s) " +
                    $"ou échouée(s), dont {EntitySpawner.FailedCount} échec(s) et {EntitySpawner.BadKeyCount} " +
                    "clé(s) divergente(s)).");
                yield break;
            }

            // Laisser au suivi le temps d'apparier l'objet, puis vérifier la clé **sur l'objet**.
            yield return new WaitForSeconds(2f);

            var key = EntitySpawner.LastSpawnedKey;
            var vehicle = FindLocalVehicle(key);

            // — Contrat de contenu (P1bis) ————————————————————————————————————————————
            // Deux contrôles, et le second est celui qui compte : la définition annoncée doit se
            // vérifier, et une définition **falsifiée** doit être refusée contre le même hash. Sans
            // le second, « le hash se vérifie » serait vrai même si la vérification renvoyait
            // toujours vrai — exactement le faux vert que la preuve discriminante du §28 a corrigé.
            var last = EntitySpawner.LastMessage;
            var nominal = false;
            var falsifiedRefused = false;
            long declared = 0L;
            long computed = 0L;

            if (last != null)
            {
                var definition = last.BuildDefinition();
                nominal = EntitySpawner.ProbeDefinitionContract(definition, last.DefinitionHash, out computed);
                declared = last.DefinitionHash;

                var tampered = new EntityDefinition
                {
                    TechTypeName = definition.TechTypeName + "X",
                    ScaleX = definition.ScaleX + 1f,
                    ScaleY = definition.ScaleY,
                    ScaleZ = definition.ScaleZ,
                };

                falsifiedRefused = !EntitySpawner.ProbeDefinitionContract(tampered, last.DefinitionHash, out _);
            }

            var verdict = vehicle != null
                          && key != 0L
                          && EntitySpawner.BadKeyCount == 0
                          && nominal
                          && falsifiedRefused
                          && EntitySpawner.RefusedDefinitionCount == 0;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] apparition : verdict={0} — entités créées={1}, écartées (déjà présentes)={2}, " +
                    "échecs={3}, clés divergentes={4} ; dernière clé=0x{5:x16} '" +
                    "{6}' — objet local retrouvé par sa clé={7} ; définition vérifiée={8} " +
                    "(hash=0x{9:x16}), définition falsifiée refusée={10}, définitions refusées={11}",
                    verdict,
                    EntitySpawner.SpawnedCount,
                    EntitySpawner.SkippedCount,
                    EntitySpawner.FailedCount,
                    EntitySpawner.BadKeyCount,
                    key,
                    EntitySpawner.LastSpawnedName,
                    vehicle != null,
                    nominal,
                    declared == 0L ? computed : declared,
                    falsifiedRefused,
                    EntitySpawner.RefusedDefinitionCount));

            if (!verdict)
            {
                if (vehicle == null || key == 0L)
                {
                    Log.LogError(
                        "[sonde] apparition : l'entité créée n'est pas retrouvée par la clé annoncée — le flux de " +
                        "positions ne pourra pas l'apparier (identifiant du serveur non écrit ?).");
                }

                if (!nominal || !falsifiedRefused)
                {
                    Log.LogError(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "[sonde] apparition : contrat de contenu non vérifiable — définition vérifiée={0}, " +
                            "falsifiée refusée={1}, refusées={2}, dernière définition '{3}'.",
                            nominal,
                            falsifiedRefused,
                            EntitySpawner.RefusedDefinitionCount,
                            EntitySpawner.LastDefinitionSummary));
                }
            }
        }

        private static Vehicle FindLocalVehicle(long key)
        {
            var all = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            foreach (var candidate in all)
            {
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (VehicleIdentity.KeyOf(candidate, out _) == key)
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
