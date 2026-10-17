using System.Collections;
using BepInEx.Logging;
using Coop.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Coop.Client
{
    /// <summary>
    /// Autotest de la garde de pause (`-coopdebug=pause`).
    ///
    /// Il ne se contente pas de vérifier que le patch est installé : il **appelle exactement ce que le menu
    /// Échap appelle** (`UWE.FreezeTime.Begin(Id.IngameMenu)`, relevé dans `IngameMenu.OnSelect` par
    /// `tools/ApiDump il`), puis relit `Time.timeScale`. Un patch installé mais qui laisserait passer le
    /// gel passerait un simple test d'installation, pas celui-ci.
    /// </summary>
    public class ClientPauseProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Pause");

        private const float WorldTimeoutSeconds = 240f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (SceneManager.GetActiveScene().name != "Main" && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (SceneManager.GetActiveScene().name != "Main")
            {
                Log.LogError($"[sonde] pause : verdict=False — monde non chargé après {WorldTimeoutSeconds:F0}s.");
                yield break;
            }

            // La scène 'Main' devient active **avant** la fin du chargement du monde : `WaitScreen` gèle
            // encore le temps pendant que le terrain et les cellules se chargent. Mesurer ici donnerait
            // `timeScale=0` pour une raison qui n'a rien à voir avec le menu (constaté : verdict=False
            // avec 0,00 partout). On attend donc que le jeu ait réellement repris, puis on mesure.
            var resumeDeadline = Time.realtimeSinceStartup + 90f;
            while (Time.timeScale <= 0.01f && Time.realtimeSinceStartup < resumeDeadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            if (Time.timeScale <= 0.01f)
            {
                Log.LogError(
                    $"[sonde] pause : verdict=False — le monde n'a jamais repris le temps après 90s " +
                    $"(timeScale={Time.timeScale:F2}, gels actifs : {UWE.FreezeTime.Debug()}).");
                yield break;
            }

            // Repères de rotation : c'est la mesure qui dit si la racine du joueur porte le lacet de la vue.
            // Si `lacetRacine` diffère de `lacetVue` et que `lacetCorpsLocal` suit la vue, alors envoyer la
            // racine (ancien code) ne pouvait pas orienter un avatar distant.
            var root = Player.mainObject != null ? Player.mainObject.transform : null;
            var body = RemoteAvatarFactory.FindBodyRoot(root);
            Log.LogWarning(
                $"[sonde] repères de rotation : lacetVue={ViewDirection.YawDegrees(ViewDirection.Yaw(root)):F1}°, " +
                $"lacetRacine={(root != null ? ViewDirection.YawDegrees(root.rotation) : 0f):F1}°, " +
                $"lacetCorpsLocal={(body != null ? body.localEulerAngles.y : 0f):F1}° " +
                $"(caméra présente : {(MainCameraControl.main != null)}).");

            var before = Time.timeScale;
            var refusedBefore = ClientPauseGuard.RefusedCount;

            UWE.FreezeTime.Begin(UWE.FreezeTime.Id.IngameMenu);
            yield return null;
            var during = Time.timeScale;

            UWE.FreezeTime.End(UWE.FreezeTime.Id.IngameMenu);
            yield return null;
            var after = Time.timeScale;

            var refusedAfter = ClientPauseGuard.RefusedCount;
            var verdict = before > 0f && during > 0f && after > 0f && refusedAfter > refusedBefore;

            Log.LogWarning(
                $"[sonde] pause : verdict={verdict} — Time.timeScale avant={before:F2}, pendant le gel " +
                $"demandé={during:F2}, après={after:F2} ; gels refusés {refusedBefore} -> {refusedAfter} " +
                $"(appel réel du menu Échap : UWE.FreezeTime.Begin(Id.IngameMenu)).");
        }
    }
}
