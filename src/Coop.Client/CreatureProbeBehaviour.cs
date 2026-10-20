using System;
using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde du jalon M3 : elle **rend un verdict** sur le suivi des créatures, au lieu de laisser
    /// l'opérateur lire des compteurs dans le journal et décider lui-même.
    ///
    /// Pourquoi une sonde, alors que le suivi s'annonce tout seul : les nombres du suivi sont cumulés
    /// depuis le démarrage du client, et le client se connecte **avant** que son monde soit chargé. Sur
    /// un relevé réel, `clé inconnue=9 299` sur 11 508 états fait peur — alors que la totalité vient de
    /// la fenêtre de chargement, où le registre local est vide (`0 clé(s) … scène 'PreStartScreen'`).
    /// Une sonde se place donc **après** le chargement et ne juge que ce qu'elle a vu : elle relève une
    /// base, observe une fenêtre, et compare.
    ///
    /// Ce qu'elle exige pour dire `True` — chaque critère est un fait mesuré, pas une intention :
    ///
    /// 1. **au moins une créature adoptée** : une fenêtre sans adoption est un échec, pas une réussite en
    ///    attente. Le suivi n'a rien fait, et c'est ça qui doit se voir ;
    /// 2. **l'IA coupée pour toutes les adoptées** : `IA arrêtée == adoptées`. L'index est relu après
    ///    l'appel, donc « coupée » veut dire « retirée du planificateur du jeu » ;
    /// 3. **des états réellement appariés pendant la fenêtre** : c'est la preuve que le client a reçu
    ///    *et appliqué* quelque chose, pas seulement compté des paquets ;
    /// 4. **aucune clé inconnue pendant la fenêtre** : après le chargement du monde, chaque clé annoncée
    ///    doit exister localement. Une clé inconnue ici n'est plus un artefact, c'est une divergence ;
    /// 5. **aucune clé partagée par deux créatures** : c'est l'hypothèse sur laquelle repose toute
    ///    l'identité (cf. PLAN.md §28). Un seul contre-exemple la casse ;
    /// 6. **aucune créature du registre sans clé** : une créature sans clé ne peut pas être suivie ;
    /// 7. **écart ≤ <see cref="MaxConsecutiveDriftMeters"/> entre états consécutifs** : c'est la seule
    ///    mesure qui teste la synchronisation (l'écart après une interruption de flux teste la
    ///    continuité, et il est compté à part par le suivi). Relevé du 2026-10-17 : `0,94 m` sur une
    ///    trentaine d'espèces ; `2 m` en 100 ms correspondrait à 20 m/s, au-delà de ce que nage une
    ///    créature de ce jeu ;
    /// 8. **la copie avance vraiment à chaque image, sans saut** : le pas par image est mesuré sur la
    ///    copie réelle pendant la fenêtre. `0` image animée veut dire que les positions sont appliquées
    ///    par paliers de 10 Hz (l'ancien comportement) ; un pas au-dessus de `0,5 m` (ou au-dessus de
    ///    `40 m/s` pour une image longue) est un saut visible. C'est le critère qui ferme la fluidité des
    ///    créatures, comme J et K l'ont fait pour les véhicules.
    ///
    /// Le seuil de dérive porte sur un maximum **cumulé depuis le démarrage du client**, pas sur la
    /// seule fenêtre : c'est volontairement plus sévère que nécessaire — une dérive qui a eu lieu avant
    /// la base ne doit pas devenir invisible parce qu'on a commencé à mesurer après.
    ///
    /// Activée par `-coopdebug=creature` (voir <c>CoopClientPlugin</c>). Le verdict est écrit en
    /// **dernière ligne**, avec le marqueur stable `[sonde] créatures : verdict=` que
    /// `tools/run-session.sh` peut attendre — sinon un run n'a pas de fin observable et il faut dormir.
    /// </summary>
    internal sealed class CreatureProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Créature");

        /// <summary>Attente maximale (s) du suivi des créatures, qui n'existe qu'après `Start` du client.</summary>
        private const float TrackerTimeoutSeconds = 240f;

        /// <summary>
        /// Attente maximale (s) du chargement du monde. Le critère est que le registre local cesse
        /// d'être vide : tant qu'il l'est, tout état reçu est compté « clé inconnue » et les nombres ne
        /// veulent rien dire.
        /// </summary>
        private const float WorldTimeoutSeconds = 240f;

        /// <summary>Durée (s) de la fenêtre observée avant le verdict.</summary>
        private const float WindowSeconds = 20f;

        /// <summary>
        /// Délai (s) pendant lequel le registre doit rester **stable** avant d'ouvrir la fenêtre.
        ///
        /// Le registre se reconstruit par paliers (toutes les 2 s), et son premier palier non vide est
        /// partiel : relevé du 2026-10-17, `3 clé(s)` puis `100 clé(s)` à 1,1 s d'intervalle. Une fenêtre
        /// ouverte dès le premier palier a compté `550 clé(s) inconnue(s)` dans ses deux premières secondes
        /// — puis zéro pendant les dix-huit suivantes. C'était la **construction du registre**, pas une
        /// divergence : mesurer avant que la mesure soit possible fait accuser le jeu à tort.
        /// </summary>
        private const float RegistrySettleSeconds = 2.5f;

        /// <summary>
        /// Attente maximale (s) de stabilisation du registre. Au-delà, on ouvre la fenêtre quand même **en
        /// le disant** : le verdict portera alors sur un registre encore mouvant, et ça doit se lire.
        /// </summary>
        private const float RegistrySettleTimeoutSeconds = 30f;

        /// <summary>Intervalle (s) des relevés intermédiaires dans la fenêtre.</summary>
        private const float ReportEverySeconds = 5f;

        /// <summary>Écart maximal (m) toléré entre deux états consécutifs (cf. le commentaire de classe).</summary>
        private const float MaxConsecutiveDriftMeters = 2f;

        /// <summary>
        /// Pas par image maximal (m) toléré : au-delà, la créature avance d'un coup visible.
        ///
        /// `0,5 m` est la limite de visibilité retenue pour un poisson (`PLAN.md` §28) et correspond au plus
        /// grand pas d'annonce mesuré (`0,99 m`) divisé par deux. Comme pour les véhicules, un pas plus grand
        /// reste acceptable s'il est produit par une image longue : c'est alors sa **vitesse** qui compte.
        /// </summary>
        private const float MaxFrameStepMeters = 0.5f;

        /// <summary>
        /// Vitesse (m/s) maximale que l'interpolation s'autorise pour rattraper une rafale — même plafond
        /// que `RemoteCreatureManager` et `RemoteVehicleManager`.
        /// </summary>
        private const float MaxCatchUpSpeedMetersPerSecond = 40f;

        private IEnumerator Start()
        {
            Log.LogWarning(
                "[sonde] sonde de créatures activée (-coopdebug=creature) — attente du suivi puis du chargement du monde...");

            var tracker = WaitForTracker();
            while (tracker.MoveNext())
            {
                yield return tracker.Current;
            }

            var creatures = ClientNetworkBehaviour.Current != null ? ClientNetworkBehaviour.Current.Creatures : null;
            if (creatures == null)
            {
                Log.LogError(
                    $"[sonde] créatures : verdict=False — le suivi des créatures n'est jamais apparu en " +
                    $"{TrackerTimeoutSeconds:F0}s. Rien n'a pu être mesuré ; ce n'est pas un succès.");
                yield break;
            }

            var worldClock = Time.realtimeSinceStartup;
            while (creatures.RegistryKeyCount == 0)
            {
                if (Time.realtimeSinceStartup - worldClock > WorldTimeoutSeconds)
                {
                    Log.LogError(
                        $"[sonde] créatures : verdict=False — le registre local est resté vide {WorldTimeoutSeconds:F0}s " +
                        "(monde non chargé ?). Les états reçus pendant ce temps sont comptés « clé inconnue » " +
                        "et ne mesurent rien : aucun verdict n'est rendu sur un monde absent.");
                    yield break;
                }

                yield return null;
            }

            // Le registre doit avoir fini de se construire : un compte stable entre deux relevés espacés
            // d'un intervalle de reconstruction vaut « il ne bouge plus ».
            var settleClock = Time.realtimeSinceStartup;
            var settled = false;
            while (!settled)
            {
                var before = creatures.RegistryKeyCount;
                yield return new WaitForSeconds(RegistrySettleSeconds);

                settled = creatures.RegistryKeyCount == before && before > 0;
                if (settled)
                {
                    break;
                }

                if (Time.realtimeSinceStartup - settleClock > RegistrySettleTimeoutSeconds)
                {
                    Log.LogWarning(
                        $"[sonde] créatures : le registre local bouge encore après {RegistrySettleTimeoutSeconds:F0}s " +
                        $"({before} -> {creatures.RegistryKeyCount} clés). La fenêtre s'ouvre quand même : le verdict " +
                        "portera sur un registre mouvant, et les clés inconnues compteront peut-être une construction " +
                        "en cours plutôt qu'une divergence.");
                    break;
                }
            }

            Log.LogInfo(
                $"[sonde] créatures : fenêtre de {WindowSeconds:F0}s ouverte sur {creatures.RegistryKeyCount} clé(s) " +
                $"locale(s) stabilisée(s), {creatures.AdoptedCount} créature(s) déjà suivie(s) — base : " +
                $"appariés={creatures.StatesMatched}, clé inconnue={creatures.StatesUnknownKey}");

            var matchedBaseline = creatures.StatesMatched;
            var unknownBaseline = creatures.StatesUnknownKey;

            // La mesure par image porte sur **cette** fenêtre : les compteurs cumulés du suivi diraient
            // aussi bien un glissement vieux de dix minutes qu'un saut de maintenant.
            creatures.ResetFrameWindow();

            var windowClock = Time.realtimeSinceStartup;
            var lastReport = -ReportEverySeconds;

            while (Time.realtimeSinceStartup - windowClock < WindowSeconds)
            {
                yield return new WaitForSeconds(0.5f);

                var elapsed = Time.realtimeSinceStartup - windowClock;
                if (elapsed - lastReport < ReportEverySeconds)
                {
                    continue;
                }

                lastReport = elapsed;
                Log.LogInfo(
                    $"[sonde] créatures : +{elapsed:F0}s — adoptées={creatures.AdoptedCount} " +
                    $"(IA arrêtée={creatures.AiDisabledCount}), appariés +{creatures.StatesMatched - matchedBaseline}, " +
                    $"clé inconnue +{creatures.StatesUnknownKey - unknownBaseline}, clés partagées={creatures.MaxSharedKeys}, " +
                    $"sans clé={creatures.UnkeyedRegistry}, écart max={creatures.MaxDriftMeters.ToString("F2", CultureInfo.InvariantCulture)}m");
            }

            var matched = creatures.StatesMatched - matchedBaseline;
            var unknown = creatures.StatesUnknownKey - unknownBaseline;

            var adopted = creatures.AdoptedCount;
            var aiCut = creatures.AiDisabledCount == adopted;
            var allKeyed = creatures.UnkeyedRegistry == 0;
            var noSharedKeys = creatures.MaxSharedKeys == 0;
            var noUnknownKeys = unknown == 0;
            var matchedSomething = matched > 0;
            var anyAdopted = adopted > 0;
            var driftBounded = creatures.MaxDriftMeters <= MaxConsecutiveDriftMeters;

            // Le critère d'interpolation : la copie doit avoir avancé **pendant la fenêtre** (sinon rien
            // n'est interpolé, c'est l'ancien comportement par paliers), et son plus grand pas d'image doit
            // rester sous le seuil de visibilité — ou, pour une image longue, sous le plafond de vitesse.
            var frameSteps = creatures.FrameSteps;
            var maxFrameStep = creatures.MaxFrameStepMeters;
            var maxFrameStepSpeed = creatures.MaxFrameStepDelta > 0f
                ? maxFrameStep / creatures.MaxFrameStepDelta
                : 0f;
            var interpolated = frameSteps > 0;
            var noFrameJump = maxFrameStep <= MaxFrameStepMeters ||
                maxFrameStepSpeed <= MaxCatchUpSpeedMetersPerSecond;

            // Les deux répartitions ci-dessous **ne concluent pas**, et c'est délibéré : elles mesurent où
            // placer un seuil, et ces seuils ne sont pas tranchés (un véhicule rapide pour M4 ; à partir de
            // quelle durée une interruption se voit). Les faire entrer dans le verdict ferait passer un
            // arbitrage pour une mesure — l'erreur exacte qu'on vient de corriger sur l'aller-retour
            // d'équipement.
            Log.LogInfo(
                $"[sonde] créatures : pas de temps du serveur entre deux annonces — {creatures.DescribeStepCurve()}");
            Log.LogInfo(
                $"[sonde] créatures : interruptions par durée — {creatures.DescribeGapHistogram()} " +
                "(une copie ré-annoncée à plus de ~0,5 m de l'endroit où elle était saute à l'image : c'est le clignotement)");

            var verdict = anyAdopted && aiCut && matchedSomething && noUnknownKeys && noSharedKeys && allKeyed &&
                driftBounded && interpolated && noFrameJump;

            // Le verdict est la **dernière** ligne, et il porte le marqueur que le harnais attend : un run
            // doit avoir une fin observable, sinon on dort et on espère.
            Log.LogInfo(
                $"[sonde] créatures : verdict={verdict} — adoptées={adopted} (IA arrêtée={creatures.AiDisabledCount}, " +
                $"adoptions de loin={creatures.AdoptionsFarFromServer}), appariés +{matched} sur la fenêtre, " +
                $"clé inconnue +{unknown}, clés locales={creatures.RegistryKeyCount} (sans clé={creatures.UnkeyedRegistry}), " +
                $"clés partagées={creatures.MaxSharedKeys}, écart max={creatures.MaxDriftMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"sur états consécutifs ({creatures.MaxDriftLabel}, sur un intervalle de " +
                $"{creatures.MaxDriftGapSeconds.ToString("F2", CultureInfo.InvariantCulture)}s), " +
                $"écart avant coupure vérifiée={creatures.MaxPendingCutDriftMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"({creatures.MaxPendingCutDriftLabel}, hors critère : pendant la mise en file d'attente de la " +
                "désinscription, la créature simule encore chez le client), " +
                $"dont mouvement local ≤{creatures.MaxLocalJitterMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"({creatures.MaxLocalJitterLabel}, le reste étant le déplacement du serveur entre les deux annonces), " +
                $"paires écartées faute de glissement fini={creatures.StillGliding}, " +
                $"interruption max={creatures.MaxGapSeconds.ToString("F1", CultureInfo.InvariantCulture)}s " +
                $"({creatures.MaxGapLabel}) avec écart {creatures.MaxGapDriftMeters.ToString("F2", CultureInfo.InvariantCulture)}m, " +
                $"1re distance max={creatures.MaxFirstDistanceMeters.ToString("F2", CultureInfo.InvariantCulture)}m, " +
                $"interpolation : {creatures.DescribeFrameSteps()}, " +
                $"seuils : dérive≤{MaxConsecutiveDriftMeters:F0}m, clé inconnue=0, clé partagée=0, " +
                $"pas par image≤{MaxFrameStepMeters:F1}m «ou» vitesse≤{MaxCatchUpSpeedMetersPerSecond:F0} m/s");

            if (!anyAdopted)
            {
                Log.LogError(
                    "[sonde] créatures : aucune créature adoptée pendant la fenêtre — le suivi n'a rien fait. " +
                    "C'est un échec : le serveur n'avait rien à diffuser, ou aucune de ses clés n'existe dans ce monde.");
            }

            if (!aiCut)
            {
                Log.LogError(
                    $"[sonde] créatures : IA arrêtée pour {creatures.AiDisabledCount} créature(s) sur {adopted} adoptée(s) — " +
                    "les autres sont simulées des deux côtés et divergeront.");
            }

            if (!matchedSomething)
            {
                Log.LogError(
                    "[sonde] créatures : aucun état apparié pendant la fenêtre — les paquets arrivent peut-être, " +
                    "mais rien n'est appliqué.");
            }

            if (!noUnknownKeys)
            {
                Log.LogError(
                    $"[sonde] créatures : {unknown} clé(s) inconnue(s) pendant la fenêtre — le monde était chargé, " +
                    "donc ce n'est plus l'artefact de chargement : le serveur annonce des créatures que ce client n'a pas.");
            }

            if (!noSharedKeys)
            {
                Log.LogError(
                    $"[sonde] créatures : {creatures.MaxSharedKeys} clé(s) partagée(s) par deux créatures — " +
                    "« une clé désigne une créature » ne tient pas (PLAN.md §28).");
            }

            if (!allKeyed)
            {
                Log.LogError(
                    $"[sonde] créatures : {creatures.UnkeyedRegistry} créature(s) du registre sans clé — elles ne " +
                    "peuvent pas être suivies, et le journal dit lesquelles.");
            }

            if (!driftBounded)
            {
                Log.LogError(
                    $"[sonde] créatures : écart de {creatures.MaxDriftMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                    $"entre deux états consécutifs ({creatures.MaxDriftLabel}) — au-dessus de {MaxConsecutiveDriftMeters:F0}m, " +
                    "la copie locale bouge encore entre deux états autoritaires.");
            }

            if (!interpolated)
            {
                Log.LogError(
                    "[sonde] créatures : aucune image d'interpolation pendant la fenêtre — les positions sont " +
                    "appliquées par paliers de 10 Hz, pas glissées. C'est l'ancien comportement, pas un succès.");
            }

            if (!noFrameJump)
            {
                Log.LogError(
                    $"[sonde] créatures : pas par image de {maxFrameStep.ToString("F2", CultureInfo.InvariantCulture)}m " +
                    $"en {creatures.MaxFrameStepDelta * 1000f:F0}ms ({maxFrameStepSpeed.ToString("F1", CultureInfo.InvariantCulture)} m/s, " +
                    $"{creatures.MaxFrameStepLabel}) — au-dessus de {MaxFrameStepMeters:F1}m et du plafond de " +
                    $"{MaxCatchUpSpeedMetersPerSecond:F0} m/s : la créature saute au lieu de glisser.");
            }
        }

        /// <summary>
        /// Attend que le client réseau ait créé son suivi. Le suivi est construit par le `Start` du
        /// gestionnaire réseau, donc la sonde ne peut pas le recevoir au moment de sa création.
        /// </summary>
        private IEnumerator WaitForTracker()
        {
            var clock = Time.realtimeSinceStartup;
            while (ClientNetworkBehaviour.Current == null || ClientNetworkBehaviour.Current.Creatures == null)
            {
                if (Time.realtimeSinceStartup - clock > TrackerTimeoutSeconds)
                {
                    yield break;
                }

                yield return null;
            }
        }
    }
}
