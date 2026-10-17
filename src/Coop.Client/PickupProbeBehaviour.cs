using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de **M5** : la première action autoritaire. Deux clients réclament le **même** objet ; le
    /// serveur accorde au premier et refuse au second, en nommant le détenteur. Le second envoie ensuite une
    /// réclamation sous un **id falsifié** pour vérifier que l'identité vient de la session et non du champ.
    ///
    /// Pourquoi une clé synthétique : la clé d'un objet réel n'est pas encore lue dans le monde (c'est la
    /// moitié « branchement sur le ramassage » de M5, cf. PLAN.md §38). Ce que cette sonde prouve est la
    /// **décision** : carte des réclamations, refus, attribution par connexion, idempotence. Inventer une
    /// clé ne change rien à cette mécanique — elle ne dépend pas de l'objet, seulement de son identité.
    ///
    /// Le calendrier est déterministe par construction : le client dont l'id est `1` réclame à 10 s de sa
    /// connexion, le second à 22 s. Le second client se connecte plusieurs secondes après le premier, donc
    /// l'ordre « 1 puis 2 » ne dépend pas de la vitesse de chargement du monde.
    ///
    /// Le verdict est écrit en **dernière** ligne, avec le marqueur `[sonde] ramassage : verdict=` que
    /// `tools/run-session.sh` attend.
    /// </summary>
    internal sealed class PickupProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Ramassage");

        /// <summary>Clé partagée par les deux clients : le même objet, donc la même valeur des deux côtés.</summary>
        private const long SharedKey = 0x5049434B55503131L; // "PICKUP11"

        /// <summary>Clé distincte pour la falsification — elle ne doit pas être déjà réclamée.</summary>
        private const long ForgedKey = 0x5049434B55503232L; // "PICKUP22"

        private const string TechName = "Titanium";

        private const float ConnectionTimeoutSeconds = 120f;

        /// <summary>Délai (s) avant la réclamation partagée, pour le premier client.</summary>
        private const float FirstClaimSeconds = 10f;

        /// <summary>Délai (s) avant la réclamation partagée, pour le second client (après le premier).</summary>
        private const float SecondClaimSeconds = 22f;

        /// <summary>Délai (s) avant la réclamation à id falsifié, pour le second client.</summary>
        private const float ForgedClaimSeconds = 28f;

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

            Log.LogInfo(
                $"[sonde] ramassage : connecté (id={me}) — " +
                (isFirst
                    ? "je réclame la clé partagée en premier."
                    : "je réclame la même clé après le premier client, puis je forge un id."));

            yield return new WaitForSeconds(isFirst ? FirstClaimSeconds : SecondClaimSeconds);
            network.ProbeSendPickupClaim(SharedKey, TechName, me);

            if (!isFirst)
            {
                yield return new WaitForSeconds(ForgedClaimSeconds - SecondClaimSeconds);
                // Id falsifié : le serveur doit attribuer la réclamation à **ma** session (id 2), pas à
                // celle que j'annonce. C'est la même règle que l'équipement, appliquée à une décision.
                network.ProbeSendPickupClaim(ForgedKey, TechName, 1);
            }

            // Laisse arriver les deux décisions (aller-retour + marge).
            yield return new WaitForSeconds(2f);

            var hasShared = network.TryGetPickupResult(SharedKey, out var shared);
            var hasForged = network.TryGetPickupResult(ForgedKey, out var forged);

            var sharedGood = hasShared &&
                (isFirst
                    ? shared.Granted && shared.HolderId == 1
                    : !shared.Granted && shared.HolderId == 1);

            // Seul le second client envoie la clé forgée ; pour le premier, le critère est sans objet.
            var forgedGood = isFirst || (hasForged && forged.Granted && forged.HolderId == me);

            var verdict = sharedGood && forgedGood;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] ramassage : verdict={0} — id={1}, clé partagée 0x{2:x16} reçue={3} accordée={4} " +
                    "tenue par {5} (attendu : {6}) ; clé forgée 0x{7:x16} reçue={8} accordée={9} tenue par {10} " +
                    "(attendu : {11}) ; réclamations falsifiées envoyées={12}, accords={13}, refus={14}",
                    verdict,
                    me,
                    SharedKey,
                    hasShared,
                    hasShared && shared.Granted,
                    hasShared ? shared.HolderId.ToString(CultureInfo.InvariantCulture) : "-",
                    isFirst ? "accordée, tenue par 1" : "refusée, tenue par 1",
                    ForgedKey,
                    hasForged,
                    hasForged && forged.Granted,
                    hasForged ? forged.HolderId.ToString(CultureInfo.InvariantCulture) : "-",
                    isFirst ? "sans objet" : "accordée, tenue par 2",
                    network.PickupForgeriesSent,
                    network.PickupGrantedCount,
                    network.PickupDeniedCount));

            if (!sharedGood)
            {
                Log.LogError(
                    isFirst
                        ? "[sonde] ramassage : ma première réclamation n'a pas été accordée (ou pas par mon id) — " +
                          "la carte des objets réclamés ne fonctionne pas."
                        : "[sonde] ramassage : la clé partagée ne m'a pas été refusée avec le bon détenteur — " +
                          "deux joueurs pourraient ramasser le même objet.");
            }

            if (!forgedGood)
            {
                Log.LogError(
                    "[sonde] ramassage : la réclamation à id falsifié n'a pas été réattribuée à ma session — " +
                    "un client pourrait réclamer pour un autre.");
            }
        }
    }
}
