using System;
using BepInEx.Logging;
using HarmonyLib;
using UWE;

namespace Coop.Client
{
    /// <summary>
    /// Empêche le **client** de mettre le monde en pause quand il ouvre le menu Échap.
    ///
    /// Pourquoi c'est un problème en coop : le jeu est solo, donc `IngameMenu.OnSelect` appelle
    /// `UWE.FreezeTime.Begin(Id.IngameMenu)` — ce qui met `Time.timeScale` à 0. Le serveur, lui, continue
    /// de simuler et d'envoyer ; le client figé cesse d'envoyer ses positions et voit le monde s'arrêter.
    /// Un joueur qui consulte simplement son menu ne doit pas arrêter la partie des autres.
    ///
    /// Le point d'interception est `UWE.FreezeTime.Set(Id, value)`, pas `Begin` : `Begin` et `End` y
    /// délèguent tous les deux (vérifié par `tools/ApiDump il`), donc un seul patch couvre l'entrée et la
    /// sortie du menu sans laisser un gel résiduel. Les autres gels (`WaitScreen`, `PDA`, …) sont
    /// conservés : eux correspondent à des états où le jeu **doit** s'arrêter.
    ///
    /// Ce que ce patch ne fait pas : il ne rend pas les entrées au joueur pendant le menu (c'est le rôle
    /// de `uGUI_InputGroup`), et il ne retire pas le menu — il retire seulement son effet sur le temps.
    /// </summary>
    internal static class ClientPauseGuard
    {
        private const string HarmonyId = "coop.subnautica.client.pauseguard";

        private static ManualLogSource _log;
        private static int _refusedCount;

        /// <summary>Nombre de gels refusés : sert d'assertion (le menu a bien été ouvert au moins une fois).</summary>
        public static int RefusedCount => _refusedCount;

        /// <summary>Installe le garde. Renvoie <c>false</c> si la cible n'a pas pu être patchée.</summary>
        public static bool Install(ManualLogSource log)
        {
            _log = log;

            var target = AccessTools.Method(typeof(FreezeTime), nameof(FreezeTime.Set));
            if (target == null)
            {
                log.LogError(
                    "[pause] cible introuvable : UWE.FreezeTime.Set — ouvrir le menu mettrait la partie " +
                    "en pause pour tout le monde.");
                return false;
            }

            var prefix = AccessTools.Method(typeof(ClientPauseGuard), nameof(SkipIngameMenuFreeze));
            if (prefix == null)
            {
                log.LogError("[pause] préfixe introuvable (incohérence de build).");
                return false;
            }

            try
            {
                new Harmony(HarmonyId).Patch(target, new HarmonyMethod(prefix));
            }
            catch (Exception exception)
            {
                log.LogError($"[pause] échec du patch de UWE.FreezeTime.Set : {exception}");
                return false;
            }

            log.LogInfo(
                "[pause] menu Échap sans gel côté client — patché : UWE.FreezeTime.Set(Id, value), " +
                "seul Id.IngameMenu est ignoré (WaitScreen et PDA restent gelés).");
            return true;
        }

        /// <summary>
        /// Prefix Harmony. Renvoyer <c>false</c> saute le corps de `Set`, donc `Time.timeScale` n'est pas
        /// touché : le monde du client continue de tourner et de s'envoyer au serveur.
        /// </summary>
        private static bool SkipIngameMenuFreeze(FreezeTime.Id id)
        {
            if (id != FreezeTime.Id.IngameMenu)
            {
                return true;
            }

            _refusedCount++;
            if (_refusedCount == 1 || _refusedCount % 100 == 0)
            {
                _log?.LogInfo(
                    $"[pause] gel refusé (n°{_refusedCount}) : le menu Échap du client ne met pas la " +
                    "partie en pause (Id.IngameMenu ignoré).");
            }

            return false;
        }
    }
}
