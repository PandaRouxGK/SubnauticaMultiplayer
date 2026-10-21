using System;
using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Shared;
using UnityEngine;

namespace Coop.Server
{
    /// <summary>
    /// Déplace l'heure du monde de **+1 h** pour que la synchronisation de la v18 ait quelque chose à
    /// corriger (`-coopdebug=timeshift`).
    ///
    /// Pourquoi un décalage plutôt qu'attendre : les deux instances chargent la même sauvegarde et leurs
    /// horloges avancent au même rythme. Sans un changement côté serveur, l'écart resterait sous le seuil
    /// de `0.5 s` et le client n'écrirait jamais rien — on ne mesurerait que « aucun message ne casse
    /// rien ». Le saut d'une heure est **observable** : le soleil se déplace, et la sonde cliente peut
    /// nommer l'écart qu'elle a corrigé.
    ///
    /// Ce que la sonde ne prouve pas : que le jeu rend le décalage visuellement (aucune capture), ni que
    /// la météo suit (elle n'est pas transportée).
    /// </summary>
    internal sealed class TimeShiftProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Temps");

        private const float WorldTimeoutSeconds = 240f;
        private const float ClientWaitSeconds = 180f;
        private const float ShiftSeconds = 3600f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (!AutoLoadBehaviour.WorldLoaded && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (!AutoLoadBehaviour.WorldLoaded || Player.main == null)
            {
                Fail($"aucun monde chargé après {WorldTimeoutSeconds:F0}s");
                yield break;
            }

            // Le décalage n'a de sens que si un client est **dans le monde** pour le recevoir : sinon il
            // chargerait sa scène après coup, avec l'heure de la sauvegarde, et ne verrait jamais le saut.
            var clientDeadline = Time.realtimeSinceStartup + ClientWaitSeconds;
            while ((ServerNetwork.Current == null || ServerNetwork.Current.ClientsInWorld == 0)
                   && Time.realtimeSinceStartup < clientDeadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (ServerNetwork.Current == null || ServerNetwork.Current.ClientsInWorld == 0)
            {
                Fail($"aucun client dans le monde après {ClientWaitSeconds:F0}s — le décalage ne serait pas observable");
                yield break;
            }

            // Laisse la diffusion d'heure (1 Hz) et l'admission se poser.
            yield return new WaitForSecondsRealtime(6f);

            var cycle = DayNightCycle.main;
            if (cycle == null)
            {
                Fail("aucun DayNightCycle — l'heure du monde n'est pas lisible");
                yield break;
            }

            var before = cycle.timePassedAsFloat;
            cycle.timePassedAsDouble = before + ShiftSeconds;
            var after = cycle.timePassedAsFloat;

            var written = Math.Abs(after - (before + ShiftSeconds)) < 1f;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] temps : décalage +{0:F0} s ({1:F1} -> {2:F1}, écriture effectuée={3}) — le client " +
                    "doit recevoir l'heure et se recaler.",
                    ShiftSeconds,
                    before,
                    after,
                    written));

            if (!written)
            {
                Fail("le décalage n'a pas été écrit (l'heure n'a pas bougé)");
                yield break;
            }

            Log.LogInfo("[sonde] temps : verdict=True — heure du monde décalée côté serveur.");
        }

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] temps : verdict=False — {reason}.");
        }
    }
}
