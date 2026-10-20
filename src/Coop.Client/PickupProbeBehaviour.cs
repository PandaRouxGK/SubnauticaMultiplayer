using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de **M5**, sur un **objet réel** : la décision de ramassage porte sur un objet que les deux
    /// instances voient dans le monde, et le **geste** de ramassage est exercé.
    ///
    /// Ce que la sonde prouve, sur le **même** objet des deux côtés :
    ///
    /// 1. les deux clients trouvent l'objet de la même façon (le plus proche du point d'apparition, parmi
    ///    ceux qui ont une clé) et **journalisent leur clé** — deux clés différentes signifieraient que le
    ///    mécanisme d'identité est en cause, pas la décision ;
    /// 2. le premier réclame la clé → accordée (l'objet existe chez le serveur, le joueur est à portée) ;
    /// 3. le second réclame la même clé → refus, détenteur nommé ;
    /// 4. le second réclame une clé **inventée** → refus « objet inconnu du serveur ». C'est la valeur
    ///    falsifiée : avant ce contrôle, le serveur accordait n'importe quelle clé ;
    /// 5. le second réclame un **second objet réel sous l'id du premier** → accordé, et le résultat porte
    ///    l'id de **sa** session. Un client ne peut donc pas réclamer pour un autre ;
    /// 6. les deux **ramassent** (vrai `Pickupable.OnHandClick`) : le second est **bloqué** et l'objet
    ///    n'entre pas dans son inventaire, le premier le prend.
    ///
    /// Le point 6 est la preuve qui manquait : une décision qu'on n'a jamais vue bloquer un geste n'est
    /// qu'un message. Le compteur de la garde et le contenu de l'inventaire sont lus **séparément**, pour
    /// qu'un geste refusé par le jeu (main occupée) ne passe pas pour une garde qui fonctionne.
    /// </summary>
    internal sealed class PickupProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Ramassage");

        /// <summary>Clé inventée : elle ne doit correspondre à aucun objet du monde (valeur falsifiée).</summary>
        private const long UnknownKey = 0x5049434B55503232L; // "PICKUP22"

        private const float ConnectionTimeoutSeconds = 120f;

        /// <summary>
        /// Attente (s) de l'autre client **entré dans le monde** avant la première réclamation.
        ///
        /// Connecté ne suffit pas à rendre la mesure vraie, mais c'est le minimum : la réclamation du
        /// client 1 est annoncée aux clients présents, et le serveur écrit « annoncée à 0 client(s) » si le
        /// second n'a pas encore fait son handshake (mesuré le 2026-10-19 : client 2 connecté 9 s après la
        /// réclamation de client 1, run rouge alors que la décision elle-même était bonne). On attend donc
        /// son **avatar**, c'est-à-dire son entrée dans le monde, pour que le retrait qu'il reçoit soit
        /// celui qu'il doit appliquer.
        /// </summary>
        private const float PeerWaitSeconds = 90f;

        /// <summary>Décalage (m) appliqué au joueur pour se placer devant l'objet.</summary>
        private static readonly Vector3 StandOffset = new Vector3(1.2f, 0.4f, 1.2f);

        // Calendrier, en secondes depuis que la sonde a son objet. Absolu et non relatif : les étapes du
        // client 2 (second objet, id falsifié) décaleraient tout le reste.
        private const float TargetClaimSeconds = 8f;
        private const float ForgedClaimSeconds = 5f;
        private const float UnknownClaimSeconds = 12f;
        private const float ClickSeconds = 15f;
        private const float VerdictSeconds = 20f;

        private IEnumerator Start()
        {
            Log.LogWarning("[sonde] ramassage activée (-coopdebug=pickup) — attente de la connexion...");

            var clock = Time.realtimeSinceStartup;
            var network = ClientNetworkBehaviour.Current;
            while (network == null || network.AssignedPlayerId == 0)
            {
                if (Time.realtimeSinceStartup - clock > ConnectionTimeoutSeconds)
                {
                    Log.LogError(
                        $"[sonde] ramassage : verdict=False — aucune connexion acceptée en " +
                        $"{ConnectionTimeoutSeconds:F0}s, rien n'a été réclamé.");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
                network = ClientNetworkBehaviour.Current;
            }

            var me = network.AssignedPlayerId;
            var isFirst = me == 1;

            // Le monde doit être chargé pour qu'un objet existe : `Player.mainObject` est le marqueur
            // commun aux deux instances (la sonde démarre avant la fin du chargement).
            clock = Time.realtimeSinceStartup;
            while (Player.main == null || Player.mainObject == null)
            {
                if (Time.realtimeSinceStartup - clock > ConnectionTimeoutSeconds)
                {
                    Log.LogError(
                        "[sonde] ramassage : verdict=False — le monde n'est pas chargé, aucun objet réel " +
                        "n'a pu être choisi.");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }

            if (isFirst && network.Remotes != null)
            {
                var peerDeadline = Time.realtimeSinceStartup + PeerWaitSeconds;
                string peerName = null;
                while (Time.realtimeSinceStartup < peerDeadline &&
                       !network.Remotes.TryGetPeerAvatarForProbe(out _, out peerName, out _))
                {
                    yield return new WaitForSeconds(0.5f);
                }

                Log.LogInfo(
                    network.Remotes.TryGetPeerAvatarForProbe(out _, out peerName, out _)
                        ? $"[sonde] ramassage : l'autre client ('{peerName}') est entré dans le monde — la " +
                          "réclamation sera annoncée à un client qui peut l'appliquer."
                        : $"[sonde] ramassage : aucun autre client entré dans le monde après " +
                          $"{PeerWaitSeconds:F0}s — la réclamation sera annoncée à personne, et la moitié " +
                          "« l'autre voit le retrait » ne sera pas mesurée.");
            }

            var target = FindTarget(0L, out _, out var targetKey, out var mechanism);
            if (target == null || targetKey == 0L)
            {
                Log.LogError(
                    "[sonde] ramassage : verdict=False — aucun objet ramassable avec une clé n'a été trouvé " +
                    "dans le monde. La décision porte sur un objet réel : sans objet, il n'y a rien à décider.");
                yield break;
            }

            Log.LogInfo(
                $"[sonde] ramassage : objet choisi — {ItemIdentity.Describe(target)} (mécanisme : {mechanism})");

            yield return PlaceNear(target);

            // — Client 2 : second objet réel, pour la réclamation à id falsifié.
            //
            // Elle doit être **accordée** : la clé existe chez le serveur, donc un accord prouve que
            // l'identité utilisée est celle de la session (id 2) et non celle annoncée (id 1). Un refus
            // pour « objet inconnu » ressemblerait à la règle sans en être une.
            var forgedTargetKey = 0L;
            var forgedTargetTech = string.Empty;
            var forgedTargetFound = false;
            var start = Time.realtimeSinceStartup;

            if (!isFirst)
            {
                FindTarget(targetKey, out var second, out forgedTargetKey, out _);
                if (second != null)
                {
                    forgedTargetFound = true;
                    forgedTargetTech = ItemIdentity.TechName(second);
                    Log.LogInfo($"[sonde] ramassage : second objet (id falsifié) — {ItemIdentity.Describe(second)}");

                    yield return PlaceNear(second);

                    while (Time.realtimeSinceStartup - start < ForgedClaimSeconds)
                    {
                        yield return null;
                    }

                    // `PlayerId = 1`, alors que le serveur sait que je suis le 2.
                    network.ProbeSendPickupClaim(forgedTargetKey, forgedTargetTech, 1);
                    yield return new WaitForSeconds(1f);
                    yield return PlaceNear(target);
                }
                else
                {
                    Log.LogWarning(
                        "[sonde] ramassage : aucun second objet avec une clé — la réclamation à id falsifié " +
                        "n'est pas exercée.");
                }
            }

            while (Time.realtimeSinceStartup - start < TargetClaimSeconds)
            {
                yield return null;
            }

            network.SendPickupClaim(targetKey, ItemIdentity.TechName(target));

            if (!isFirst)
            {
                while (Time.realtimeSinceStartup - start < UnknownClaimSeconds)
                {
                    yield return null;
                }

                network.SendPickupClaim(UnknownKey, "CléFalsifiée");
            }

            while (Time.realtimeSinceStartup - start < ClickSeconds)
            {
                yield return null;
            }

            var blockedBefore = PickupGuard.BlockedCount;
            var grantedBefore = PickupGuard.GrantedCount;

            // Le geste du joueur : c'est `Pickupable.OnHandClick` que le jeu invoque quand on clique sur
            // l'objet, donc c'est la garde qui décide.
            var interactable = Player.main != null && Player.main.guiHand != null &&
                               Player.main.guiHand.IsFreeToInteract();

            try
            {
                target.OnHandClick(Player.main != null ? Player.main.guiHand : null);
            }
            catch (System.Exception exception)
            {
                Log.LogWarning(
                    $"[sonde] ramassage : le geste de ramassage a levé {exception.GetType().Name} — " +
                    $"{exception.Message}");
            }

            yield return new WaitForSeconds(1f);

            var inInventory = target != null && Inventory.main != null && Inventory.main.Contains(target);
            var blockedNow = PickupGuard.BlockedCount;
            var grantedNow = PickupGuard.GrantedCount;

            // Depuis la v11, un objet accordé à quelqu'un d'autre est **retiré du monde** et annoncé : chez
            // le refusé, il peut donc disparaître avant son propre clic. Ce n'est pas la garde qui a parlé,
            // c'est l'objet qui n'est plus là — deux issues distinctes, et la sonde les nomme plutôt que
            // de faire passer l'une pour l'autre.
            var objectGone = target == null;

            while (Time.realtimeSinceStartup - start < VerdictSeconds)
            {
                yield return null;
            }

            var hasShared = network.TryGetPickupResult(targetKey, out var shared);
            var hasUnknown = network.TryGetPickupResult(UnknownKey, out var unknown);
            PickupResultMessage forged = null;
            var hasForged = forgedTargetFound && network.TryGetPickupResult(forgedTargetKey, out forged);

            var sharedGood = hasShared &&
                (isFirst
                    ? shared.Granted && shared.HolderId == 1
                    : !shared.Granted && shared.HolderId == 1);

            // Le contrôle d'existence : la clé inventée doit être refusée (le motif exact n'est pas dans le
            // message de v8 ; c'est la ligne serveur qui le dit).
            var unknownGood = isFirst || (hasUnknown && !unknown.Granted);

            // L'id falsifié : accordé **sous l'id de la session**, et le message dit la revendication.
            var forgedGood = isFirst || !forgedTargetFound ||
                             (hasForged && forged.Granted && forged.HolderId == me && forged.ClaimedBy == 1);

            var guardDecided = isFirst
                ? grantedNow > grantedBefore
                : blockedNow > blockedBefore || objectGone;

            var pickupOutcomeGood = isFirst ? inInventory : !inInventory;

            var verdict = sharedGood && unknownGood && forgedGood && guardDecided && pickupOutcomeGood;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] ramassage : verdict={0} — id={1}, clé partagée 0x{2:x16} reçue={3} accordée={4} " +
                    "tenue par {5} (attendu : {6}) ; clé inventée 0x{7:x16} reçue={8} accordée={9} " +
                    "(attendu : refusée) ; id falsifié 0x{10:x16} reçue={11} accordée={12} tenue par {13} " +
                    "revendiquée sous l'id {14} (attendu : accordée, tenue par {1}) ; geste de ramassage : " +
                    "main libre={15}, garde={16} (blocages {17}->{18}, accords {19}->{20}), objet dans " +
                    "l'inventaire={21} (attendu : {22}), objet retiré par disparition={24} ; garde : {23}",
                    verdict,
                    me,
                    targetKey,
                    hasShared,
                    hasShared && shared.Granted,
                    hasShared ? shared.HolderId.ToString(CultureInfo.InvariantCulture) : "-",
                    isFirst ? "accordée, tenue par 1" : "refusée, tenue par 1",
                    UnknownKey,
                    hasUnknown,
                    hasUnknown && unknown.Granted,
                    forgedTargetKey,
                    hasForged,
                    hasForged && forged.Granted,
                    hasForged ? forged.HolderId.ToString(CultureInfo.InvariantCulture) : "-",
                    hasForged ? forged.ClaimedBy.ToString(CultureInfo.InvariantCulture) : "-",
                    interactable,
                    isFirst ? "accord" : "blocage",
                    blockedBefore,
                    blockedNow,
                    grantedBefore,
                    grantedNow,
                    inInventory,
                    isFirst ? "oui" : "non",
                    PickupGuard.Summary(),
                    objectGone));

            if (!sharedGood)
            {
                Log.LogError(
                    isFirst
                        ? "[sonde] ramassage : ma première réclamation sur un objet réel n'a pas été accordée " +
                          "(objet absent du monde serveur, ou hors de portée ?) — la ligne serveur dit le motif."
                        : "[sonde] ramassage : la clé partagée ne m'a pas été refusée avec le bon détenteur — " +
                          "deux joueurs pourraient ramasser le même objet.");
            }

            if (!unknownGood)
            {
                Log.LogError(
                    "[sonde] ramassage : une clé qui ne désigne aucun objet a été accordée — le serveur ne " +
                    "vérifie pas l'existence de ce qu'on lui demande.");
            }

            if (!forgedGood)
            {
                Log.LogError(
                    "[sonde] ramassage : la réclamation à id falsifié n'a pas été attribuée à ma session — " +
                    "un client pourrait réclamer pour un autre.");
            }

            if (!guardDecided || !pickupOutcomeGood)
            {
                Log.LogError(
                    "[sonde] ramassage : le geste de ramassage n'a pas suivi la décision — " +
                    (isFirst
                        ? "l'objet accordé n'est pas dans l'inventaire."
                        : "l'objet refusé a pu être ramassé (garde absente ?).") +
                    $" (main libre={interactable})");
            }
        }

        /// <summary>Place le joueur devant l'objet, puis laisse le temps aux positions d'arriver (20 Hz).</summary>
        private static IEnumerator PlaceNear(Pickupable item)
        {
            if (item == null || Player.main == null || Player.mainObject == null)
            {
                yield break;
            }

            var position = item.transform.position;
            var destination = position + StandOffset;

            // Le serveur refuse un déplacement de plus de 15 m/s : une téléportation de 16 m en une image
            // est rejetée, puis le client est resynchronisé et se retrouve loin de l'objet (mesuré le
            // 2026-10-19 : « joueur placé à 15.99 m », puis réclamation refusée « hors portée »). On
            // approche donc par petits pas sous la vitesse admise — 1,5 m toutes les 0,15 s, soit
            // 10 m/s — au lieu d'un saut que la garde P1 a raison de refuser.
            var origin = Player.mainObject.transform.position;
            var distance = Vector3.Distance(origin, destination);
            var steps = Mathf.Max(1, Mathf.CeilToInt(distance / 1.5f));

            for (var step = 1; step <= steps; step++)
            {
                var next = Vector3.Lerp(origin, destination, (float)step / steps);
                Player.main.SetPosition(next, Player.mainObject.transform.rotation);
                yield return new WaitForSeconds(0.15f);
            }

            yield return new WaitForSeconds(0.5f);

            var placed = Player.mainObject != null
                ? Vector3.Distance(Player.mainObject.transform.position, position)
                : float.MaxValue;

            Log.LogInfo(
                $"[sonde] ramassage : joueur placé à {placed:F2} m de l'objet (portée admise par le serveur : " +
                $"{Coop.Model.Protocol.MaxPickupRangeMeters:F1} m ; approche en {steps} pas)");

            if (placed > Coop.Model.Protocol.MaxPickupRangeMeters)
            {
                // Une réclamation partirait hors portée et le refus ressemblerait à une règle d'identité
                // respectée : le dire ici évite de lire un refus de distance comme un accord déguisé.
                Log.LogWarning(
                    $"[sonde] ramassage : le joueur n'est pas à portée après l'approche ({placed:F2} m > " +
                    $"{Coop.Model.Protocol.MaxPickupRangeMeters:F1} m) — la réclamation sera refusée pour la " +
                    "distance, pas pour l'identité.");
            }
        }

        /// <summary>
        /// Objet de la sonde : le plus proche du joueur parmi ceux qui ont une clé, en excluant une clé
        /// déjà choisie. Les deux instances partent du même point de la même sauvegarde, donc elles
        /// doivent désigner le même objet — et leurs journaux le disent, clé à l'appui.
        /// </summary>
        private static Pickupable FindTarget(long exceptKey, out Pickupable found, out long key, out string mechanism)
        {
            key = 0L;
            mechanism = ItemIdentity.MechanismNone;

            var origin = Player.mainObject != null ? Player.mainObject.transform.position : Vector3.zero;
            var items = ItemIdentity.CollectWorldItems();
            Pickupable best = null;
            var bestDistance = float.MaxValue;

            foreach (var item in items)
            {
                var itemKey = ItemIdentity.KeyOf(item, out var itemMechanism);
                if (itemKey == 0L || itemKey == exceptKey)
                {
                    continue;
                }

                // Objet **immobile** seulement. Mesuré le 2026-10-17 : un `Pickupable` porté par une
                // créature (`GarryFish`) a une clé stable mais une position qui nage — le serveur a refusé
                // la réclamation pour `hors portée : 6,8 m > 6,0 m` alors que le client, lui, était à
                // `1,74 m` de **son** poisson. Ce n'était pas la portée qui était fausse : c'était l'objet
                // choisi. La décision de M5 porte sur un objet posé, pas sur un butin vivant.
                if (item.GetComponentInParent<Creature>() != null)
                {
                    continue;
                }

                var distance = item.transform != null
                    ? Vector3.Distance(origin, item.transform.position)
                    : float.MaxValue;

                if (distance >= bestDistance)
                {
                    continue;
                }

                best = item;
                bestDistance = distance;
                key = itemKey;
                mechanism = itemMechanism;
            }

            found = best;
            return best;
        }
    }
}
