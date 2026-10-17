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
    /// Deux points de blocage, tous deux choisis **sur preuve** (scan IL de `Assembly-CSharp.dll` par
    /// `tools/ApiDump callers`), et superposés parce qu'ils ne protègent pas la même chose :
    ///
    /// 1. **L'entrée** — `IngameMenu.SaveGameAsync` (itérateur privé, sans paramètre) n'a que deux
    ///    appelants : `IngameMenu.SaveGame` (le bouton du menu pause, lui-même rappelé par
    ///    `OnSaveErrorConfirmed`) et `IngameMenu/&lt;QuitGameAsync&gt;d__65.MoveNext` (sauvegarde à la
    ///    fermeture). Le bloquer arrête l'intention : rien de la sauvegarde ne commence.
    /// 2. **L'écriture** — `SaveLoadManager/GameInfo.SaveFile(string, byte[])` est la dernière chose
    ///    avant le disque (`Platform.IO.File.WriteAllBytes`), et son **unique** appelant est
    ///    `GameInfo.SaveIntoCurrentSlot`, lui-même appelé par le seul itérateur de
    ///    `SaveToTemporaryStorageAsync`. Bloquer ici protège d'un chemin qui contournerait le menu —
    ///    y compris un chemin qui n'existe pas encore dans cette version du jeu.
    ///
    /// Ce que la garde ne couvre pas, et il faut le dire : `SaveLoadManager.CreateSlotAsync` (menu
    /// principal → « nouvelle partie ») écrit une partie **neuve** — jamais la session du serveur — et
    /// reste volontairement autorisé, sinon le mod casserait le jeu solo ; `ScreenshotManager` écrit des
    /// PNG de captures d'écran, pas des données de sauvegarde. Les deux sont hors du risque visé.
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

        /// <summary>Type imbriqué du dernier rempart : la méthode qui écrit réellement les octets.</summary>
        private const string WriteTargetTypeName = "SaveLoadManager+GameInfo";
        private const string WriteTargetMethodName = "SaveFile";

        private static ManualLogSource _log;
        private static int _blockedCount;
        private static int _blockedWriteCount;

        /// <summary>Nombre de sauvegardes interceptées à l'entrée : sert d'assertion pour l'autotest.</summary>
        public static int BlockedCount => _blockedCount;

        /// <summary>
        /// Nombre d'écritures interceptées au dernier rempart (avant le disque) : c'est la mesure qui
        /// prouve que la garde tient même quand on n'entre pas par le menu — l'autotest appelle
        /// `SaveFile` directement.
        /// </summary>
        public static int BlockedWriteCount => _blockedWriteCount;

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

            // Dernier rempart : la méthode qui appelle `File.WriteAllBytes`. Si le type imbriqué n'est pas
            // résolu, on échoue **fermé** comme pour l'entrée : un client qui écrit dans la sauvegarde du
            // serveur est un dégât irréversible, un coop qui refuse de démarrer ne l'est pas.
            var writeTargetType = typeof(SaveLoadManager).GetNestedType("GameInfo", BindingFlags.Public | BindingFlags.NonPublic);
            var writeTarget = writeTargetType == null
                ? null
                : writeTargetType.GetMethod(
                    WriteTargetMethodName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (writeTarget == null)
            {
                log.LogError(
                    $"[garde] cible d'écriture introuvable : {WriteTargetTypeName}.{WriteTargetMethodName} — " +
                    "aucun rempart avant le disque, chargement du monde refusé.");
                return false;
            }

            var writePrefix = AccessTools.Method(typeof(SaveGuard), nameof(BlockWrite));
            if (writePrefix == null)
            {
                log.LogError("[garde] préfixe d'écriture introuvable (incohérence de build).");
                return false;
            }

            var harmony = new Harmony(HarmonyId);
            try
            {
                harmony.Patch(target, new HarmonyMethod(prefix));
                harmony.Patch(writeTarget, new HarmonyMethod(writePrefix));
            }
            catch (Exception exception)
            {
                log.LogError(
                    $"[garde] échec du patch de {Describe(target)} ou de {Describe(writeTarget)} : {exception}");
                return false;
            }

            // On journalise les signatures réellement patchées : c'est la seule façon de savoir
            // qu'on a bien visé les bonnes méthodes et pas une surcharge inattendue.
            log.LogInfo(
                $"[garde] sauvegarde cliente désactivée — patché : {Describe(target)} (entrée) et " +
                $"{Describe(writeTarget)} (écriture sur disque)");
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

        /// <summary>
        /// Prefix du dernier rempart. Renvoyer <c>false</c> saute l'écriture : les octets ne touchent jamais
        /// le disque, quelle que soit la porte d'entrée.
        ///
        /// Volontairement **sans** remplacement de résultat (contrairement à <see cref="BlockSave"/> : la
        /// méthode renvoie <c>void</c>) et sans exception : un chemin de sauvegarde qui se poursuivrait
        /// verrait simplement un fichier absent, ce qui est le comportement d'un client sans droit d'écriture.
        /// </summary>
        private static bool BlockWrite(object[] __args)
        {
            _blockedWriteCount++;

            var fileName = __args != null && __args.Length > 0 ? __args[0] as string : null;
            _log?.LogWarning(
                $"[garde] écriture de sauvegarde interceptée avant le disque (n°{_blockedWriteCount}, fichier " +
                $"« {fileName ?? "(null)"} ») : cette instance est un client, le serveur est seul propriétaire du monde.");

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

            // — 1. L'entrée : le chemin qu'un joueur déclenche vraiment (bouton « Sauvegarder »).
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
            var entryBlocked = after > before;
            Log.LogInfo(
                entryBlocked
                    ? $"[autotest] entrée bloquée : IngameMenu.SaveGameAsync intercepté (compteur {before} -> {after})."
                    : $"[autotest] entrée NON bloquée : aucune interception (compteur resté à {after}).");

            // — 2. L'écriture : appelée **directement**, en contournant le menu. C'est la mesure qui
            //      compte : elle prouve qu'un chemin qui n'entre pas par `IngameMenu` ne peut pas écrire.
            //      Arguments nuls exprès — avec la garde la méthode n'exécute rien ; sans elle, elle lève,
            //      et l'exception est la preuve que le dernier rempart est absent. Aucun octet n'est écrit
            //      dans les deux cas.
            var writeBefore = SaveGuard.BlockedWriteCount;
            var writer = typeof(SaveLoadManager)
                .GetNestedType("GameInfo", BindingFlags.Public | BindingFlags.NonPublic)?
                .GetMethod("SaveFile", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (writer == null)
            {
                Log.LogError(
                    "[autotest] SaveLoadManager/GameInfo.SaveFile introuvable : le dernier rempart n'est " +
                    "pas vérifiable sur cette version du jeu.");
            }
            else
            {
                try
                {
                    writer.Invoke(null, new object[] { null, null });
                }
                catch (Exception exception)
                {
                    Log.LogError(
                        $"[autotest] l'écriture directe a LEVÉ ({exception.GetType().Name}: {exception.Message}) " +
                        "— le dernier rempart n'a pas intercepté l'appel.");
                }
            }

            var writeAfter = SaveGuard.BlockedWriteCount;
            var writeBlocked = writer != null && writeAfter > writeBefore;
            Log.LogInfo(
                writeBlocked
                    ? $"[autotest] écriture bloquée : SaveFile intercepté hors du menu (compteur {writeBefore} -> {writeAfter})."
                    : $"[autotest] écriture NON bloquée : SaveFile n'a pas été intercepté (compteur resté à {writeAfter}).");

            var verdict = entryBlocked && writeBlocked;
            if (verdict)
            {
                Log.LogWarning($"[autotest] RÉSULTAT OK : les deux remparts ont parlé (entrée {before}->{after}, écriture {writeBefore}->{writeAfter}).");
            }
            else
            {
                Log.LogError(
                    $"[autotest] RÉSULTAT ÉCHEC : entrée={entryBlocked}, écriture={writeBlocked} — la garde est " +
                    "INOPÉRANTE sur au moins un chemin, et cette instance écrirait dans la sauvegarde du serveur.");
            }

            Log.LogInfo($"[autotest] sauvegarde cliente bloquée (entrée+écriture) -> verdict={verdict}");

            menu.Close();
        }
    }
}
