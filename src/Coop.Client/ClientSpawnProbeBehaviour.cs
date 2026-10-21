using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de **fabrication demandée par le client** (`-coopdebug=requestspawn`, v16, M4/M6).
    ///
    /// Ce qui est mesuré, dans l'ordre :
    ///  1. une demande **légitime** (un Seamoth à ses pieds) est accordée : le serveur répond avec une clé,
    ///     puis son annonce crée la copie localement — la sonde vérifie que la clé créée est **celle de la
    ///     décision**, donc que l'objet vu est bien celui demandé ;
    ///  2. une demande **hors portée** (500 m) est refusée, avec un motif nommé, et **aucune** entité
    ///     supplémentaire n'apparaît.
    ///
    /// Pourquoi les deux : un serveur qui accorderait tout passerait la première moitié, et un serveur qui
    /// refuserait tout passerait la seconde. La preuve est le couple.
    ///
    /// Ce que la sonde ne fait pas : elle n'instancie rien elle-même. Si la copie apparaît, c'est que
    /// l'annonce du serveur est arrivée et que `EntitySpawner` l'a traitée — c'est précisément le chemin
    /// qu'on veut éprouver.
    /// </summary>
    internal sealed class ClientSpawnProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Demande Apparition");

        private const float WorldTimeoutSeconds = 90f;
        private const float DecisionTimeoutSeconds = 25f;
        private const float CreationTimeoutSeconds = 30f;
        private const float OutOfRangeMeters = 500f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while ((ClientNetworkBehaviour.Current == null
                    || !ClientNetworkBehaviour.Current.Accepted
                    || !AutoLoadBehaviour.WorldLoaded
                    || Player.main == null)
                   && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            var client = ClientNetworkBehaviour.Current;
            if (client == null || !client.Accepted || !AutoLoadBehaviour.WorldLoaded || Player.main == null)
            {
                Fail($"pas de session acceptée / monde chargé après {WorldTimeoutSeconds:F0} s — rien n'a été demandé");
                yield break;
            }

            // Laisse le monde se poser : une demande envoyée pendant le placement du joueur partirait
            // d'une position que le serveur n'a pas encore acceptée.
            yield return new WaitForSecondsRealtime(3f);

            var origin = Player.main.transform.position;
            var spawnedBefore = EntitySpawner.SpawnedCount;

            // 1. Demande légitime, à ses pieds (dans la portée de 8 m).
            var rotation = Player.main.transform.rotation;
            var validRequest = client.RequestEntitySpawn(TechType.Seamoth, origin, rotation);
            if (validRequest == 0u)
            {
                Fail("la demande légitime n'est pas partie (session non acceptée)");
                yield break;
            }

            var decisionDeadline = Time.realtimeSinceStartup + DecisionTimeoutSeconds;
            while (client.EntitySpawnsGranted == 0 && client.EntitySpawnsRefused == 0 &&
                   Time.realtimeSinceStartup < decisionDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            var granted = client.EntitySpawnsGranted >= 1;
            var grantedKey = client.LastSpawnGrantedKey;

            // 2. La copie doit être créée par l'annonce, avec la clé de la décision.
            var created = false;
            var createdKey = 0L;
            if (granted)
            {
                var creationDeadline = Time.realtimeSinceStartup + CreationTimeoutSeconds;
                while (EntitySpawner.SpawnedCount == spawnedBefore && Time.realtimeSinceStartup < creationDeadline)
                {
                    yield return new WaitForSecondsRealtime(0.5f);
                }

                created = EntitySpawner.SpawnedCount > spawnedBefore;
                createdKey = EntitySpawner.LastSpawnedKey;
            }

            // 3. Demande hors portée : refusée, et aucune création de plus.
            //
            // La cadence passe **avant** la portée dans la décision du serveur (un refus bon marché
            // d'abord). Sans cette attente, la seconde demande était refusée pour cadence — mesuré au run
            // du 2026-10-21, où le motif rendu était « cadence : encore 4,5 s » : la garde de portée
            // n'était alors pas éprouvée du tout. On attend donc la fin du délai, plus une marge.
            yield return new WaitForSecondsRealtime(Protocol.EntitySpawnCooldownSeconds + 1.5f);

            var refusedFar = false;
            var refusalReason = string.Empty;
            var spawnedAfterValid = EntitySpawner.SpawnedCount;

            var farRequest = client.RequestEntitySpawn(
                TechType.Seamoth,
                origin + new Vector3(OutOfRangeMeters, 0f, 0f),
                rotation);

            if (farRequest != 0u)
            {
                var refusalDeadline = Time.realtimeSinceStartup + DecisionTimeoutSeconds;
                while (client.EntitySpawnsRefused == 0 && Time.realtimeSinceStartup < refusalDeadline)
                {
                    yield return new WaitForSecondsRealtime(0.5f);
                }

                refusedFar = client.EntitySpawnsRefused >= 1;
                refusalReason = client.LastSpawnRefusal ?? string.Empty;

                // Une seconde de marge : une création tardive de la demande valide ne doit pas être
                // comptée comme une création provoquée par la demande refusée.
                yield return new WaitForSecondsRealtime(1.5f);
            }

            var noExtraCreation = EntitySpawner.SpawnedCount == spawnedAfterValid;
            var keyMatches = grantedKey != 0L && createdKey == grantedKey;
            var verdict = granted && created && keyMatches && refusedFar && noExtraCreation;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] demandes d'apparition : verdict={0} — accordée={1} (clé=0x{2:x16}), " +
                    "créée localement={3} (clé=0x{4:x16}, clé identique={5}), hors portée refusée={6} " +
                    "(motif=« {7} »), aucune création supplémentaire={8}",
                    verdict,
                    granted,
                    grantedKey,
                    created,
                    createdKey,
                    keyMatches,
                    refusedFar,
                    refusalReason,
                    noExtraCreation));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] demandes d'apparition : verdict=False — " +
                    $"accordée={granted}, créée={created}, clé identique={keyMatches}, " +
                    $"hors portée refusée={refusedFar}, aucune création de plus={noExtraCreation}");
            }
        }

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] demandes d'apparition : verdict=False — {reason}.");
        }
    }
}
