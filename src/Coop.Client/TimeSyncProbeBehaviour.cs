using System;
using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde **cliente** de l'heure du monde (v18, `-coopdebug=time`).
    ///
    /// Le pendant de `TimeShiftProbeBehaviour` : l'hôte décale son horloge d'une heure, et cette sonde
    /// constate chez le client deux choses distinctes :
    ///  1. des messages d'heure sont **reçus** (`WorldTimeMessages`) — le transport ;
    ///  2. un recalage a été **écrit** (`WorldTimeApplied`), et l'écart corrigé est du même ordre que le
    ///     décalage serveur (`LastWorldTimeError`) — l'application, pas seulement la réception.
    ///
    /// Le critère de grandeur compte : un écart de `0.5 s` serait un recalage de bruit ; seule une
    /// correction d'environ `3600 s` prouve que c'est bien le saut du serveur qui a été appliqué.
    ///
    /// Ce que la sonde ne prouve pas : que le soleil est visiblement déplacé (aucune capture), ni que la
    /// météo ou les événements suivent — ils ne sont pas transportés par la v18.
    /// </summary>
    internal sealed class TimeSyncProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Temps Client");

        private const float WorldTimeoutSeconds = 200f;
        private const float MessagesTimeoutSeconds = 30f;
        private const float ApplyTimeoutSeconds = 90f;
        private const float ExpectedShiftSeconds = 3600f;
        private const float ShiftToleranceSeconds = 30f;

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

            if (ClientNetworkBehaviour.Current == null
                || !ClientNetworkBehaviour.Current.Accepted
                || !AutoLoadBehaviour.WorldLoaded
                || Player.main == null)
            {
                Fail($"pas de session acceptée / monde chargé après {WorldTimeoutSeconds:F0}s");
                yield break;
            }

            var client = ClientNetworkBehaviour.Current;

            var messageDeadline = Time.realtimeSinceStartup + MessagesTimeoutSeconds;
            while (client.WorldTimeMessages == 0 && Time.realtimeSinceStartup < messageDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            var messagesReceived = client.WorldTimeMessages > 0;

            // On attend un recalage **du même ordre que le saut du serveur**, pas le premier recalage
            // venu : pendant son chargement, le client se recale déjà plusieurs fois de quelques secondes
            // (horloge gelée au menu), et à l'étape AA du 2026-10-21 la sonde a conclu False pour cette
            // raison — le 3 600 s corrigé était masqué par un recalage de 0,6 s arrivé après lui.
            var applyDeadline = Time.realtimeSinceStartup + ApplyTimeoutSeconds;
            while (client.MaxWorldTimeAppliedError < ExpectedShiftSeconds - ShiftToleranceSeconds
                   && Time.realtimeSinceStartup < applyDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            var applied = client.WorldTimeApplied > 0;
            var corrected = client.MaxWorldTimeAppliedError;
            var bigEnough = corrected >= ExpectedShiftSeconds - ShiftToleranceSeconds;

            var cycle = DayNightCycle.main;
            var local = cycle != null ? cycle.timePassedAsFloat : -1f;
            var server = client.LastWorldTimeServer;
            var verdict = messagesReceived && applied && bigEnough;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] temps : messages={0} appliqués={1} écart corrigé max={2:F1}s (attendu ≈{3:F0}s), " +
                    "heure locale={4:F1}s dernière heure serveur={5:F1}s — verdict={6}",
                    client.WorldTimeMessages,
                    client.WorldTimeApplied,
                    corrected,
                    ExpectedShiftSeconds,
                    local,
                    server,
                    verdict));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] temps : verdict=False — " +
                    $"messages reçus={messagesReceived}, recalage appliqué={applied}, " +
                    $"écart corrigé max={corrected:F1}s (le saut du serveur n'a pas été vu).");
            }
        }

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] temps : verdict=False — {reason}.");
        }
    }
}
