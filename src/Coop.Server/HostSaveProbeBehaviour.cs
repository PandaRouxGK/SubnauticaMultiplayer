using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace Coop.Server
{
    /// <summary>
    /// Autotest de la **sauvegarde côté serveur**, activé par <c>-coopdebug=save</c>.
    ///
    /// Pourquoi il existe : `SaveGuard` n'est installé que par le plugin client, et la batterie prouvait
    /// déjà que le client ne peut pas écrire. Mais personne n'avait jamais vu le **serveur** sauvegarder
    /// pendant un run — « la garde ne bloque que le client » reposait donc sur une lecture de code, pas sur
    /// une tentative réelle. Ce contrôle-ci renverse la question : il déclenche la sauvegarde de l'hôte, et
    /// vérifie que des octets ont bien atterri.
    ///
    /// Ce qui est observé, et pourquoi ce n'est pas un compteur inventé : le jeu écrit la sauvegarde dans des
    /// dossiers temporaires sous `SaveLoadManager.GetTemporarySavePathRoot()` (`tmpXXXXXX/` avec
    /// `gameinfo.json`, `scene-objects.bin`, `screenshot.jpg`, `CellsCache/`), puis les déplace vers le
    /// slot. Une sauvegarde réussie **crée donc un dossier temporaire** (ou en modifie le contenu) : c'est
    /// cette différence de contenu disque qui est mesurée, avant/après.
    ///
    /// Ce que ça ne prouve pas : que le fichier du slot a bien été remplacé (le jeu le fait après coup, et
    /// l'affirmer demanderait de lire l'état du slot). La ligne le dit : elle parle du dossier temporaire.
    /// </summary>
    public class HostSaveProbeBehaviour : MonoBehaviour
    {
        // Préfixe explicite : `using UnityEngine` apporte aussi un `Logger`.
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop SaveProbe");

        /// <summary>Attente maximale (s) du joueur et du menu pause.</summary>
        private const float WorldTimeoutSeconds = 240f;

        /// <summary>
        /// Délai (s) avant de déclencher la sauvegarde : le client lancé par le harnais se connecte pendant
        /// ce temps. Sans lui, le test prouverait « l'hôte sauvegarde seul », ce qui n'est pas la question —
        /// la question est « l'hôte sauvegarde pendant qu'un client est là ».
        /// </summary>
        private const float SettleBeforeSaveSeconds = 15f;

        /// <summary>Attente maximale (s) de l'écriture disque après le déclenchement.</summary>
        private const float SaveTimeoutSeconds = 90f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (Player.main == null && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (Player.main == null)
            {
                Log.LogError(
                    $"[autotest] sauvegarde serveur : verdict=False — aucun joueur après {WorldTimeoutSeconds:F0}s, " +
                    "rien n'a été déclenché.");
                yield break;
            }

            Log.LogInfo($"[autotest] sauvegarde serveur : monde prêt, déclenchement dans {SettleBeforeSaveSeconds:F0}s.");

            // `WaitForSecondsRealtime` et non `WaitForSeconds` : le jeu **met le temps en pause** pendant
            // l'enregistrement, et `WaitForSeconds` est mis à l'échelle par `Time.timeScale` — à 0, il ne
            // s'écoule plus. Constaté : le déclenchement a bien eu lieu et la sonde n'a plus jamais reparlé.
            yield return new WaitForSecondsRealtime(SettleBeforeSaveSeconds);

            var root = string.Empty;
            try
            {
                root = SaveLoadManager.GetTemporarySavePathRoot();
            }
            catch (Exception exception)
            {
                Log.LogError($"[autotest] sauvegarde serveur : dossier temporaire illisible ({exception.Message}).");
            }

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Log.LogError(
                    $"[autotest] sauvegarde serveur : verdict=False — dossier temporaire introuvable " +
                    $"(« {root} »), l'écriture ne peut pas être observée.");
                yield break;
            }

            var before = Snapshot(root);

            // Le menu pause est trouvé comme sur le client : `IngameMenu.main` peut être null et le
            // composant inactif dans la hiérarchie — l'appel direct reste le chemin que le bouton utilise.
            var menu = IngameMenu.main;
            if (menu == null)
            {
                menu = FindObjectOfType<IngameMenu>();
            }

            if (menu == null)
            {
                foreach (var candidate in Resources.FindObjectsOfTypeAll<IngameMenu>())
                {
                    if (candidate != null && candidate.gameObject.scene.IsValid())
                    {
                        menu = candidate;
                        break;
                    }
                }
            }

            if (menu == null)
            {
                Log.LogError("[autotest] sauvegarde serveur : verdict=False — aucun IngameMenu, rien n'a été déclenché.");
                yield break;
            }

            Log.LogWarning(
                $"[autotest] sauvegarde serveur : déclenchement volontaire de IngameMenu.SaveGame() " +
                $"({before.Count} fichier(s) dans le dossier temporaire avant, timeScale={Time.timeScale:F2}).");

            try
            {
                menu.SaveGame();
            }
            catch (Exception exception)
            {
                Log.LogError($"[autotest] sauvegarde serveur : exception pendant SaveGame() : {exception}");
            }

            var changed = new List<string>();
            var saveDeadline = Time.realtimeSinceStartup + SaveTimeoutSeconds;
            while (Time.realtimeSinceStartup < saveDeadline)
            {
                yield return new WaitForSecondsRealtime(2f);
                changed = Diff(before, Snapshot(root));
                if (changed.Count > 0)
                {
                    break;
                }
            }

            var verdict = changed.Count > 0;
            if (verdict)
            {
                Log.LogInfo(
                    $"[autotest] sauvegarde serveur : {changed.Count} fichier(s) écrit(s) ou modifié(s) dans le " +
                    $"dossier temporaire — {string.Join(", ", changed.GetRange(0, Math.Min(5, changed.Count)))}");
            }
            else
            {
                Log.LogError(
                    $"[autotest] sauvegarde serveur : AUCUNE écriture en {SaveTimeoutSeconds:F0}s — l'hôte a " +
                    $"déclenché une sauvegarde et rien n'a touché le disque (timeScale={Time.timeScale:F2}).");
            }

            Log.LogInfo($"[autotest] sauvegarde serveur pendant un run -> verdict={verdict}");
        }

        /// <summary>
        /// Contenu du dossier temporaire : chemin relatif → (taille, date d'écriture).
        ///
        /// La date **et** la taille : une sauvegarde réécrit les mêmes noms, et une taille identique avec une
        /// date identique ne dit rien ; les deux ensemble distinguent « réécrit » de « rien ».
        /// </summary>
        internal static Dictionary<string, string> Snapshot(string root)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(path);
                    files[path] = info.Length.ToString() + "@" + info.LastWriteTimeUtc.Ticks;
                }
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[autotest] sauvegarde serveur : lecture du dossier temporaire interrompue ({exception.Message}).");
            }

            return files;
        }

        /// <summary>Fichiers apparus ou modifiés entre deux relevés (chemins relatifs, lisibles).</summary>
        internal static List<string> Diff(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            var changed = new List<string>();

            foreach (var pair in after)
            {
                if (!before.TryGetValue(pair.Key, out var previous) ||
                    !string.Equals(previous, pair.Value, StringComparison.Ordinal))
                {
                    var name = pair.Key.Replace('\\', '/');
                    var marker = before.ContainsKey(pair.Key) ? "modifié" : "créé";
                    changed.Add($"{marker} {name}");
                }
            }

            return changed;
        }
    }
}
