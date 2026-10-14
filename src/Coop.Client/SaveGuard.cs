using System;
using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Empêche le client d'écrire dans la sauvegarde. Le serveur est seul propriétaire du monde :
    /// deux instances qui écrivent la même partie la corrompraient (elles chargent la même sessionId).
    ///
    /// Point de blocage choisi **sur preuve**, pas au jugé. Un scan IL de `Assembly-CSharp.dll`
    /// (outil `tools/ApiDump`, mode `callers`) donne exactement deux appelants de
    /// `IngameMenu.SaveGameAsync` (itérateur privé, sans paramètre) :
    ///   - `IngameMenu.SaveGame` — le bouton « Sauvegarder » du menu pause ;
    ///   - `IngameMenu/&lt;QuitGameAsync&gt;d__65.MoveNext` — la sauvegarde au moment de quitter.
    /// Et `SaveToTemporaryStorageAsync` / `SaveToDeepStorageAsync` n'ont **aucun** autre appelant
    /// que `SaveGameAsync`. Un seul prefix ferme donc tous les chemins d'écriture.
    ///
    /// Contre-exemple utile : `SaveLoadManager.GetAllowWritingFiles()` a un nom qui donne envie,
    /// mais le scan montre qu'il n'est appelé que par `LargeWorldStreamer.TryUnloadBatch` — c'est un
    /// garde-fou de **déchargement de cellules**, pas de sauvegarde. Le patcher n'aurait rien protégé.
    /// </summary>
    internal static class SaveGuard
    {
        private const string HarmonyId = "coop.subnautica.client.saveguard";
        private const string TargetTypeName = "IngameMenu";
        private const string TargetMethodName = "SaveGameAsync";

        private static ManualLogSource _log;
        private static int _blockedCount;

        /// <summary>Nombre de sauvegardes interceptées : sert aussi d'assertion pour l'autotest.</summary>
        public static int BlockedCount => _blockedCount;

        /// <summary>Installe la garde. Renvoie <c>false</c> si la cible n'a pas pu être patchée.</summary>
        public static bool Install(ManualLogSource log)
        {
            _log = log;

            var target = AccessTools.Method(typeof(IngameMenu), TargetMethodName);
            if (target == null)
            {
                log.LogError(
                    $"[garde] cible introuvable : {TargetTypeName}.{TargetMethodName} — le client pourrait écrire " +
                    "dans la sauvegarde du serveur !");
                return false;
            }

            var prefix = AccessTools.Method(typeof(SaveGuard), nameof(BlockSave));
            if (prefix == null)
            {
                log.LogError("[garde] méthode de préfixe introuvable (incohérence de build).");
                return false;
            }

            try
            {
                new Harmony(HarmonyId).Patch(target, new HarmonyMethod(prefix));
            }
            catch (Exception exception)
            {
                log.LogError($"[garde] échec du patch de {Describe(target)} : {exception}");
                return false;
            }

            // On journalise la signature réellement patchée : c'est la seule façon de savoir
            // qu'on a bien visé la bonne méthode et pas une surcharge inattendue.
            log.LogInfo($"[garde] sauvegarde cliente désactivée — patché : {Describe(target)}");
            return true;
        }

        /// <summary>
        /// Prefix Harmony. Renvoyer <c>false</c> empêche l'exécution de la coroutine de sauvegarde.
        ///
        /// On **remplace aussi le résultat** par une coroutine vide plutôt que de le laisser à
        /// <c>null</c>. Les deux appelants écrivent <c>StartCoroutine(SaveGameAsync())</c> : rendre
        /// <c>null</c> ferait lever une exception Unity dans le flux de fermeture du jeu
        /// (`QuitGameAsync`), ce qui laisserait le jeu à moitié quitté. Un no-op immédiat laisse le
        /// déroulement de l'appelant intact, sans rien écrire.
        /// </summary>
        private static bool BlockSave(ref IEnumerator __result)
        {
            _blockedCount++;
            _log?.LogWarning(
                $"[garde] sauvegarde interceptée et bloquée (n°{_blockedCount}) : cette instance est un client, " +
                "le serveur est seul propriétaire du monde.");

            __result = NoOp();
            return false;
        }

        /// <summary>Coroutine qui se termine immédiatement, à la place de la sauvegarde bloquée.</summary>
        private static IEnumerator NoOp()
        {
            yield break;
        }

        /// <summary>
        /// Décrit la méthode visée, paramètres compris. On ne peut pas utiliser
        /// <c>MethodInfo.FullName</c> ici (il ne se résout pas sur le net472 du jeu), et le détail
        /// des paramètres est justement ce qui prouve qu'on a patché la bonne surcharge :
        /// <c>SaveGameAsync()</c> sans argument, et non une variante paramétrée.
        /// </summary>
        private static string Describe(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var signature = parameters.Length == 0
                ? "()"
                : "(" + string.Join(", ", Array.ConvertAll(parameters, p => p.ParameterType.Name)) + ")";

            return $"{method.DeclaringType?.FullName}.{method.Name}{signature}";
        }
    }

    /// <summary>
    /// Autotest de la garde, activé seulement par l'argument <c>-coopdebug=saveguard</c>.
    ///
    /// Il déclenche une **vraie** sauvegarde depuis le client et vérifie qu'elle est interceptée.
    /// Sans cela, constater « aucun fichier modifié » ne prouverait rien : il faut aussi montrer que
    /// le test est capable de détecter une écriture. C'est exactement le piège dans lequel on tombe
    /// en fermant simplement le jeu — ce chemin-là ne tente aucune sauvegarde, donc l'observer ne
    /// dit rien de la garde.
    /// </summary>
    public class SaveGuardSelfTestBehaviour : MonoBehaviour
    {
        // Préfixe explicite : `using UnityEngine` apporte aussi un `Logger`, la référence seule serait ambiguë.
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop SaveGuardTest");

        /// <summary>Délai maximum accordé au chargement du monde avant d'abandonner le test.</summary>
        private const float WorldLoadTimeoutSeconds = 240f;

        private IEnumerator Start()
        {
            // Recherche du menu pause, en coroutine : surtout pas de Thread.Sleep ici, il gèlerait
            // le thread principal (et donc le jeu entier) pendant toute la durée de l'attente.
            // Trois pistes dans l'ordre, car un objet inactif est invisible à FindObjectOfType et
            // IngameMenu.main n'est renseigné que si le composant a été éveillé.
            var deadline = Time.realtimeSinceStartup + WorldLoadTimeoutSeconds;
            IngameMenu menu = null;

            while (menu == null && Time.realtimeSinceStartup < deadline)
            {
                menu = IngameMenu.main;

                if (menu == null)
                {
                    menu = FindObjectOfType<IngameMenu>();
                }

                if (menu == null)
                {
                    // IncludeInactive : le menu pause peut être sur un objet désactivé menu fermé.
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
                    yield return new WaitForSeconds(2f);
                }
            }

            if (menu == null)
            {
                Log.LogError(
                    $"[autotest] aucun IngameMenu après {WorldLoadTimeoutSeconds:F0}s (ni IngameMenu.main, ni en scène, " +
                    "ni inactif) : test impossible, la garde reste NON PROUVÉE.");
                yield break;
            }

            Log.LogInfo(
                $"[autotest] IngameMenu trouvé : actifDansHierarchie={menu.gameObject.activeInHierarchy}, " +
                $"composantActif={menu.enabled}");

            // Le bouton « Sauvegarder » n'est cliquable que menu ouvert ; on se met donc dans cet état
            // plutôt que d'appeler la sauvegarde depuis un objet inactif (StartCoroutine y échouerait).
            if (!menu.gameObject.activeInHierarchy)
            {
                Log.LogInfo("[autotest] ouverture du menu pause pour rendre le chemin de sauvegarde réel...");
                menu.Open();
                yield return new WaitForSeconds(1f);
                Log.LogInfo($"[autotest] après Open() : actifDansHierarchie={menu.gameObject.activeInHierarchy}");
            }

            var before = SaveGuard.BlockedCount;
            Log.LogWarning($"[autotest] déclenchement volontaire de IngameMenu.SaveGame() — compteur avant={before}");

            try
            {
                menu.SaveGame();
            }
            catch (Exception exception)
            {
                // Une exception ici serait le signe que le chemin de sauvegarde est cassé : on la
                // remonte comme un échec, pas comme un détail.
                Log.LogError($"[autotest] exception pendant SaveGame() : {exception}");
            }

            yield return new WaitForSeconds(4f);

            var after = SaveGuard.BlockedCount;
            if (after > before)
            {
                Log.LogWarning($"[autotest] RÉSULTAT OK : tentative interceptée (compteur {before} -> {after}).");
            }
            else
            {
                Log.LogError($"[autotest] RÉSULTAT ÉCHEC : aucune interception (compteur resté à {after}) — la garde est INOPÉRANTE.");
            }

            menu.Close();
        }
    }
}
