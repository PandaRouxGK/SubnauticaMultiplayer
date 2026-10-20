using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de **conduite autoritaire** (M4, protocole v9).
    ///
    /// Deux clients, un véhicule :
    ///
    /// - le premier **conduit** : il déplace son exemplaire et laisse `VehicleDriverBehaviour` envoyer son
    ///   état à 10 Hz — la même boucle que celle d'un vrai joueur au volant, seule la source du véhicule
    ///   est forcée (`ProbeTarget`) ;
    /// - le second **tente de prendre le volant** du même véhicule : il se place à côté, envoie son état,
    ///   et le serveur doit **refuser** en nommant le conducteur.
    ///
    /// Ce que la sonde ne fait pas : monter dans le véhicule avec l'interface du jeu. Le critère
    /// `Player.main.currentMountedVehicle` est une lecture de champ public, pas une mécanique ; ce que la
    /// sonde mesure est le chemin d'envoi, l'arbitrage du serveur et le refus rendu au demandeur.
    /// </summary>
    internal sealed class VehicleDriveProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Conduite Sonde");

        private const float ReadyTimeoutSeconds = 120f;
        private const float DriveSeconds = 8f;
        private const float DriveSpeed = 12f;
        private const float HijackStartSeconds = 12f;
        private const float HijackSeconds = 2f;
        private const float RetryTimeoutSeconds = 90f;

        /// <summary>
        /// Durée pendant laquelle le premier client **garde** le volant après son aller-retour.
        ///
        /// Pourquoi ce n'est pas du remplissage : depuis la v12, le conducteur libère le véhicule dès qu'il
        /// le quitte. Sans attente, il libérait avant que le second n'ait tenté sa prise en main — mesuré :
        /// `refus=0, accords=1` chez le second, qui n'a jamais vu de refus et ne mesurait donc plus
        /// l'exclusivité. Soixante secondes couvrent le lancement du second client, son chargement de monde
        /// et sa tentative (mesuré : ≈ 40 s après le début du premier dans le pire cas observé).
        /// </summary>
        private const float HoldAfterDriveSeconds = 60f;

        private IEnumerator Start()
        {
            Log.LogWarning("[sonde] conduite activée (-coopdebug=drive) — attente de la connexion...");

            var clock = Time.realtimeSinceStartup;
            var network = ClientNetworkBehaviour.Current;
            while (network == null || network.AssignedPlayerId == 0)
            {
                if (Time.realtimeSinceStartup - clock > ReadyTimeoutSeconds)
                {
                    Log.LogError("[sonde] conduite : verdict=False — aucune connexion acceptée, rien n'a été envoyé.");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
                network = ClientNetworkBehaviour.Current;
            }

            var me = network.AssignedPlayerId;

            clock = Time.realtimeSinceStartup;
            while (Player.main == null || Player.mainObject == null)
            {
                if (Time.realtimeSinceStartup - clock > ReadyTimeoutSeconds)
                {
                    Log.LogError("[sonde] conduite : verdict=False — le monde n'est pas chargé, aucun véhicule.");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }

            var vehicle = FindVehicle(out var key, out var mechanism);
            if (vehicle == null || key == 0L)
            {
                Log.LogError(
                    "[sonde] conduite : verdict=False — aucun véhicule avec une clé dans le monde. La " +
                    "conduite porte sur un véhicule réel : sans véhicule, il n'y a rien à arbitrer.");
                yield break;
            }

            Log.LogInfo($"[sonde] conduite : véhicule choisi — clé=0x{key:x16} ({mechanism}), " +
                        $"pos=({vehicle.transform.position.x:F1}, {vehicle.transform.position.y:F1}, " +
                        $"{vehicle.transform.position.z:F1})");

            if (me == 1)
            {
                yield return Drive(vehicle, network);
            }
            else
            {
                yield return TryHijack(vehicle, network);
            }
        }

        /// <summary>Le premier client déplace le véhicule ; la boucle réelle envoie son état.</summary>
        private IEnumerator Drive(Vehicle vehicle, ClientNetworkBehaviour network)
        {
            var origin = vehicle.transform.position;
            var direction = vehicle.transform.forward;
            if (direction.sqrMagnitude < 0.5f)
            {
                direction = Vector3.forward;
            }

            // Se placer **à côté** du véhicule avant d'envoyer : le serveur vérifie la portée (10 m), et
            // le Seamoth de la sauvegarde est à ~25 m du point d'apparition.
            Player.main.SetPosition(origin + new Vector3(1.5f, 0.5f, 1.5f), Player.mainObject.transform.rotation);
            yield return new WaitForSeconds(2f);

            var sentBefore = VehicleDriverBehaviour.SentCount;
            var grantedBefore = VehicleDriverBehaviour.GrantedCount;
            var refusedBefore = VehicleDriverBehaviour.RefusedCount;

            VehicleDriverBehaviour.ProbeTarget = vehicle;

            // Aller-retour **borné** (amplitude 4 m) plutôt qu'une ligne droite : le joueur reste devant le
            // véhicule, donc dans la portée admise, pendant toute la mesure. Une trajectoire fuyante ferait
            // refuser les derniers états pour « hors portée » — un échec qui accuserait l'arbitrage alors
            // que c'est la sonde qui s'éloignait.
            var start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < DriveSeconds)
            {
                var phase = (Time.realtimeSinceStartup - start) / DriveSeconds * Mathf.PI * 4f;
                vehicle.transform.position = origin + direction * (Mathf.Sin(phase) * 4f);
                yield return null;
            }

            vehicle.transform.position = origin;

            // Mode « volant tenu jusqu'au kill » (étape X, P2) : la sonde garde la main et ne libère rien,
            // pour que le harnais puisse tuer le client **pendant** qu'il conduit. Sans ce mode, le volant
            // était toujours rendu avant la fin de la sonde, et la libération à la déconnexion n'était
            // couverte que par le code (limite écrite au §52.3).
            if (System.Environment.GetEnvironmentVariable("COOP_DRIVE_KEEP") == "1")
            {
                Log.LogWarning(
                    "[sonde] conduite : maintien — volant gardé jusqu'à la mort du client (COOP_DRIVE_KEEP=1).");

                while (true)
                {
                    yield return null;
                }
            }

            // Garder le volant : c'est ce qui fait que le second client essuie un **refus** au lieu de
            // trouver le véhicule libre.
            var holdUntil = Time.realtimeSinceStartup + HoldAfterDriveSeconds;
            while (Time.realtimeSinceStartup < holdUntil)
            {
                yield return null;
            }

            VehicleDriverBehaviour.ProbeTarget = null;
            yield return new WaitForSeconds(1.5f);

            var sent = VehicleDriverBehaviour.SentCount - sentBefore;
            var granted = VehicleDriverBehaviour.GrantedCount - grantedBefore;
            var refused = VehicleDriverBehaviour.RefusedCount - refusedBefore;
            var moved = Vector3.Distance(origin, vehicle.transform.position);

            var verdict = sent > 0 && granted > 0 && refused == 0;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] conduite : verdict={0} — id={1}, conducteur : états envoyés={2} en {3:F0}s " +
                    "(+{10:F0}s de volant gardé, puis libéré), accords={4}, refus={5}, déplacement local={6:F1} m " +
                    "(les accords prouvent que le serveur a accepté la prise en main ; aucun refus n'est attendu " +
                    "pour le conducteur) ; compteurs réseau : conduites envoyées={7}, accords={8}, refus={9}",
                    verdict,
                    network.AssignedPlayerId,
                    sent,
                    DriveSeconds,
                    granted,
                    refused,
                    moved,
                    network.VehicleControlSent,
                    network.VehicleControlGranted,
                    network.VehicleControlRefused,
                    HoldAfterDriveSeconds));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] conduite : le serveur n'a pas accepté la prise en main (ou l'a refusée) — voir les " +
                    "lignes `[conduite]` et `[net] conduite` des deux journaux.");
            }
        }

        /// <summary>Le second client tente de conduire le véhicule déjà pris : le serveur doit refuser.</summary>
        private IEnumerator TryHijack(Vehicle vehicle, ClientNetworkBehaviour network)
        {
            // Attendre que le premier client ait pris la main : le calendrier des deux instances diffère
            // (chargement du monde), donc on vise large plutôt qu'un instant commun.
            yield return new WaitForSeconds(HijackStartSeconds);

            // Se placer **à côté du véhicule tel qu'on le voit** : il a bougé sous l'impulsion du premier
            // client, et le refus doit porter sur « déjà conduit », pas sur « hors portée ».
            if (vehicle != null && Player.main != null && Player.mainObject != null)
            {
                Player.main.SetPosition(
                    vehicle.transform.position + new Vector3(1.5f, 0.5f, 1.5f),
                    Player.mainObject.transform.rotation);
            }

            yield return new WaitForSeconds(2f);

            var grantedBefore = VehicleDriverBehaviour.GrantedCount;
            var refusedBefore = VehicleDriverBehaviour.RefusedCount;

            VehicleDriverBehaviour.ProbeTarget = vehicle;
            yield return new WaitForSeconds(HijackSeconds);
            VehicleDriverBehaviour.ProbeTarget = null;

            yield return new WaitForSeconds(1.5f);

            var refused = VehicleDriverBehaviour.RefusedCount - refusedBefore;
            var grantedOnFirstTry = VehicleDriverBehaviour.GrantedCount - grantedBefore;

            // — Puis on attend que le **premier** client libère le volant (v12) et on le reprend.
            //
            // Sans cette reprise, le refus seul ne distinguerait pas « un seul conducteur à la fois » de
            // « le véhicule est pris pour toujours » : les deux produisent exactement le même refus. C'est
            // la seconde moitié de M4, et elle a besoin d'un conducteur qui **descend** de son véhicule.
            var retryStart = Time.realtimeSinceStartup;
            var grantedAfterRelease = 0;
            var attempts = 0;

            while (Time.realtimeSinceStartup - retryStart < RetryTimeoutSeconds)
            {
                attempts++;
                VehicleDriverBehaviour.ProbeTarget = vehicle;
                yield return new WaitForSeconds(HijackSeconds);
                VehicleDriverBehaviour.ProbeTarget = null;
                yield return new WaitForSeconds(1f);

                grantedAfterRelease = VehicleDriverBehaviour.GrantedCount - grantedBefore;
                if (grantedAfterRelease > 0)
                {
                    break;
                }
            }

            var verdict = refused > 0 && grantedAfterRelease > 0 && grantedOnFirstTry == 0;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] conduite : verdict={0} — id={1}, tentative sur un véhicule déjà pris : " +
                    "refus={2}, accords={3} (attendu : refus ≥ 1, accords = 0) ; après libération par le " +
                    "conducteur : accords={4} en {5} tentative(s) (attendu : ≥ 1) ; compteurs réseau : " +
                    "conduites envoyées={6}, accords={7}, refus={8}, libérations envoyées={9}",
                    verdict,
                    network.AssignedPlayerId,
                    refused,
                    grantedOnFirstTry,
                    grantedAfterRelease,
                    attempts,
                    network.VehicleControlSent,
                    network.VehicleControlGranted,
                    network.VehicleControlRefused,
                    network.VehicleReleasesSent));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] conduite : soit le second client a pu prendre un véhicule déjà conduit, soit il " +
                    "n'a jamais pu le reprendre après la libération — le volant reste attribué à quelqu'un " +
                    "qui n'est plus dedans.");
            }
        }

        /// <summary>Véhicule le plus proche du joueur parmi ceux qui ont une clé.</summary>
        private static Vehicle FindVehicle(out long key, out string mechanism)
        {
            key = 0L;
            mechanism = VehicleIdentity.MechanismNone;

            var origin = Player.mainObject != null ? Player.mainObject.transform.position : Vector3.zero;
            var all = Resources.FindObjectsOfTypeAll<Vehicle>();
            Vehicle best = null;
            var bestDistance = float.MaxValue;

            foreach (var candidate in all)
            {
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.scene.IsValid())
                {
                    continue;
                }

                var candidateKey = VehicleIdentity.KeyOf(candidate, out var candidateMechanism);
                if (candidateKey == 0L)
                {
                    continue;
                }

                var distance = candidate.transform != null
                    ? Vector3.Distance(origin, candidate.transform.position)
                    : float.MaxValue;

                if (distance >= bestDistance)
                {
                    continue;
                }

                best = candidate;
                bestDistance = distance;
                key = candidateKey;
                mechanism = candidateMechanism;
            }

            return best;
        }
    }
}
