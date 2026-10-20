using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de la garde de position (P1, v13) — <c>-coopdebug=position</c>.
    ///
    /// Ce qu'elle mesure, et pourquoi deux phases : la garde du serveur a deux promesses opposées.
    /// Elle doit **refuser** un déplacement qu'aucune vitesse humaine n'explique (sinon elle ne sert à
    /// rien), et elle doit **ne pas corriger** une nage normale (sinon elle rend le jeu injouable).
    /// Une sonde qui ne testerait que le refus validerait un garde qui refuse tout ; une sonde qui ne
    /// testerait que le nominal validerait un garde qui ne refuse rien. Les deux phases sont donc
    /// nécessaires, et chacune a son critère.
    ///
    /// Ce que la sonde fait réellement : elle déplace le joueur local par le chemin du jeu
    /// (`Player.main.SetPosition`), en petits pas pour la phase nominale, puis d'un saut de 100 m
    /// qu'elle **maintient** quelques images pour que la boucle d'envoi à 20 Hz le transmette vraiment.
    /// Elle lit ensuite les compteurs du client (`CorrectionsReceived` / `CorrectionsApplied`), qui ne
    /// peuvent être alimentés que par un vrai `PlayerCorrection` reçu du serveur.
    ///
    /// Ce qu'elle ne prouve pas : le serveur peut avoir resynchronisé la position après trois refus
    /// consécutifs (comportement voulu, §44.3). Le critère est donc « au moins une correction reçue et
    /// appliquée », pas « le joueur est resté bloqué à l'ancienne position ».
    /// </summary>
    public class PositionProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Position");

        private const float WorldTimeoutSeconds = 180f;
        private const float HandshakeTimeoutSeconds = 60f;
        private const float SettleSeconds = 2f;

        // Phase 1 — nage nominale : 0,25 m toutes les 0,2 s = 1,25 m/s, très en dessous des 15 m/s.
        private const float NormalSeconds = 6f;
        private const float StepSeconds = 0.2f;
        private const float StepMeters = 0.25f;

        // Phase 2 — téléport : 100 m, maintenu 0,6 s pour que l'envoi à 20 Hz l'émette.
        private const float TeleportMeters = 100f;
        private const float TeleportHoldSeconds = 0.6f;
        private const float CorrectionTimeoutSeconds = 10f;

        private IEnumerator Start()
        {
            var worldDeadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (!AutoLoadBehaviour.WorldLoaded && Time.realtimeSinceStartup < worldDeadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            if (!AutoLoadBehaviour.WorldLoaded)
            {
                Fail($"monde non chargé après {WorldTimeoutSeconds:F0}s");
                yield break;
            }

            var network = ClientNetworkBehaviour.Current;
            var handshakeDeadline = Time.realtimeSinceStartup + HandshakeTimeoutSeconds;
            while ((network == null || !network.Accepted) && Time.realtimeSinceStartup < handshakeDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                network = ClientNetworkBehaviour.Current;
            }

            if (network == null || !network.Accepted)
            {
                Fail($"handshake non accepté après {HandshakeTimeoutSeconds:F0}s");
                yield break;
            }

            if (Player.main == null || Player.mainObject == null)
            {
                Fail("joueur local indisponible");
                yield break;
            }

            // Une correction n'est pas appliquée en véhicule (le siège appartient au véhicule) : la
            // phase 2 ne pourrait pas conclure, et un faux négatif n'apprendrait rien.
            if (Player.main.inSeamoth || Player.main.inExosuit)
            {
                Fail("joueur en véhicule — la sonde exige un joueur à pied");
                yield break;
            }

            yield return new WaitForSecondsRealtime(SettleSeconds);

            var root = Player.mainObject.transform;
            var origin = root.position;
            var rotation = root.rotation;

            // — Phase 1 : déplacement légitime, aucune correction attendue ————————————————
            var receivedBeforeNormal = network.CorrectionsReceived;
            var normalStart = Time.realtimeSinceStartup;
            var travelled = 0f;

            while (Time.realtimeSinceStartup - normalStart < NormalSeconds)
            {
                travelled += StepMeters;
                Player.main.SetPosition(origin + Vector3.right * travelled, rotation);
                yield return new WaitForSecondsRealtime(StepSeconds);
            }

            var normalCorrections = network.CorrectionsReceived - receivedBeforeNormal;

            // — Phase 2 : téléport de 100 m, une correction est attendue ————————————————————
            var receivedBeforeTeleport = network.CorrectionsReceived;
            var appliedBeforeTeleport = network.CorrectionsApplied;
            var teleportTarget = origin + Vector3.right * TeleportMeters;

            var teleportStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - teleportStart < TeleportHoldSeconds)
            {
                // Maintenu à chaque image : sinon la physique du jeu peut ramener le joueur avant que
                // la boucle d'envoi (20 Hz) n'ait lu la position, et la sonde mesurerait un saut qui
                // n'a jamais quitté le client.
                Player.main.SetPosition(teleportTarget, rotation);
                yield return null;
            }

            var correctionDeadline = Time.realtimeSinceStartup + CorrectionTimeoutSeconds;
            while (network.CorrectionsApplied - appliedBeforeTeleport < 1 &&
                   Time.realtimeSinceStartup < correctionDeadline)
            {
                yield return new WaitForSecondsRealtime(0.1f);
            }

            var teleportReceived = network.CorrectionsReceived - receivedBeforeTeleport;
            var teleportApplied = network.CorrectionsApplied - appliedBeforeTeleport;

            // — Remise en place, puis verdict ————————————————————————————————————————————
            Player.main.SetPosition(origin, rotation);
            yield return new WaitForSecondsRealtime(1f);

            var verdict = normalCorrections == 0 && teleportReceived > 0 && teleportApplied >= 1;

            if (normalCorrections != 0)
            {
                Log.LogError(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "[sonde] position : critère manqué — la phase nominale a reçu {0} correction(s) " +
                        "pour {1:F1} m en {2:F1}s (seuil 0) : la garde corrige un déplacement légitime.",
                        normalCorrections,
                        travelled,
                        NormalSeconds));
            }

            if (teleportReceived <= 0)
            {
                Log.LogError(
                    "[sonde] position : critère manqué — aucune correction reçue après un téléport de " +
                    $"{TeleportMeters:F0} m : le serveur ne refuse pas (ou la correction ne revient pas).");
            }

            if (teleportApplied < 1)
            {
                Log.LogError(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "[sonde] position : critère manqué — {0} correction(s) reçue(s) mais {1} appliquée(s) " +
                        "après le téléport (le client n'a pas repris la position acceptée).",
                        teleportReceived,
                        teleportApplied));
            }

            Log.LogWarning(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] position : verdict={0} — nage normale : {1} correction sur {2:F1}s (seuil 0), " +
                    "téléport de {3:F0} m : correction reçue={4}, appliquée={5} ({6} appliquée(s)) — " +
                    "seuils : 0 en nage, ≥1 appliquée après téléport",
                    verdict,
                    normalCorrections,
                    NormalSeconds,
                    TeleportMeters,
                    teleportReceived > 0,
                    teleportApplied >= 1,
                    teleportApplied));
        }

        /// <summary>Échec **nommé** : une sonde qui ne peut pas conclure doit le dire, pas rester muette.</summary>
        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] position : verdict=False — {reason}.");
        }
    }
}
