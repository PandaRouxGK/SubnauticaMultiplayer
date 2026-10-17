using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using HarmonyLib;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Le ramassage d'un objet du monde demande l'accord du serveur (M5).
    ///
    /// Ce que ce garde retire au client : **la décision de prendre un objet**. Il ne décide plus
    /// « je le ramasse » ; il réclame, et le serveur répond. Trois chemins, et le journal dit lequel :
    ///
    /// 1. l'objet est **déjà accordé** à ce joueur → le ramassage passe (un objet relâché puis
    ///    ramassé de nouveau reste à lui) ;
    /// 2. l'objet est **refusé** (tenu par un autre, inconnu du serveur, hors de portée) → le
    ///    ramassage est **bloqué** avant que le jeu ne déplace l'objet dans l'inventaire ;
    /// 3. l'objet n'a **pas encore** de décision → la réclamation part, et le ramassage passe
    ///    **optimistement** pour ne pas geler le jeu sur un aller-retour. Si le refus arrive ensuite,
    ///    l'objet est **retiré de l'inventaire et relâché** (le « retour arrière »).
    ///
    /// Pourquoi l'optimisme : `Inventory.Pickup` est synchrone et s'exécute dans le geste du joueur.
    /// Attendre la réponse ferait une latence visible sur chaque ramassage ; ne rien faire perdrait la
    /// course dans tous les cas. L'optimisme déplace donc le problème d'une image, et le retour arrière
    /// est ce qui rend le refus **réel** au lieu d'être un simple message.
    ///
    /// Ce que ce garde ne couvre pas, et qui reste écrit dans `docs/authority.md` : les objets **sans
    /// clé** (ni identifiant, ni position d'ancrage exploitable) ne sont pas arbitrés et passent ; un
    /// objet lancé ou poussé peut changer de position — donc de clé — entre l'observation des deux
    /// instances.
    /// </summary>
    internal static class PickupGuard
    {
        private const string HarmonyId = "coop.subnautica.client.pickup";

        private static ManualLogSource _log;

        /// <summary>Réclamations parties par ce client, décisions reçues et ramassages bloqués.</summary>
        private static int _claimsSent;
        private static int _granted;
        private static int _blocked;
        private static int _rollbacks;
        private static int _unkeyed;
        private static int _failedRollbacks;

        /// <summary>
        /// Objets réclamés dont la décision n'est pas encore arrivée, par clé : c'est la table du retour
        /// arrière. Une entrée est retirée dès qu'une décision arrive (accordée ou refusée).
        /// </summary>
        private static readonly Dictionary<long, Pickupable> Pending = new Dictionary<long, Pickupable>();

        public static int ClaimsSent => _claimsSent;
        public static int GrantedCount => _granted;
        public static int BlockedCount => _blocked;
        public static int RollbackCount => _rollbacks;
        public static int UnkeyedCount => _unkeyed;
        public static int FailedRollbackCount => _failedRollbacks;

        /// <summary>Installe la garde. Renvoie <c>false</c> si la cible n'a pas pu être patchée.</summary>
        public static bool Install(ManualLogSource log)
        {
            _log = log;

            var target = AccessTools.Method(typeof(Pickupable), "OnHandClick");
            if (target == null)
            {
                log.LogError(
                    "[ramassage] cible introuvable : Pickupable.OnHandClick — le ramassage reste local et " +
                    "non arbitré. Ce n'est pas silencieux, mais ce n'est pas la règle visée.");

                // Non bloquant : un ramassage non arbitré n'abîme rien, contrairement à une sauvegarde
                // cliente écrite. On continue donc à jouer, en le disant.
                return false;
            }

            var prefix = AccessTools.Method(typeof(PickupGuard), nameof(AllowPickup));
            if (prefix == null)
            {
                log.LogError("[ramassage] méthode de préfixe introuvable (incohérence de build).");
                return false;
            }

            try
            {
                new Harmony(HarmonyId).Patch(target, new HarmonyMethod(prefix));
            }
            catch (Exception exception)
            {
                log.LogError($"[ramassage] échec du patch de {target.DeclaringType?.Name}.{target.Name} : {exception}");
                return false;
            }

            log.LogInfo(
                "[ramassage] garde installée : Pickupable.OnHandClick intercepté — un objet refusé par le " +
                "serveur ne peut plus entrer dans l'inventaire.");
            return true;
        }

        /// <summary>
        /// Préfixe de <c>Pickupable.OnHandClick</c> : renvoyer <c>false</c> empêche le jeu de ramasser.
        /// </summary>
        private static bool AllowPickup(Pickupable __instance)
        {
            var network = ClientNetworkBehaviour.Current;

            // Hors session (jeu solo, menu, ou avant le handshake) : le mod ne s'interpose pas.
            if (__instance == null || network == null || network.AssignedPlayerId == 0)
            {
                return true;
            }

            var key = ItemIdentity.KeyOf(__instance, out var mechanism);
            if (key == 0L)
            {
                _unkeyed++;
                _log.LogWarning(
                    $"[ramassage] objet sans clé ({ItemIdentity.Describe(__instance)}) — ramassé localement, " +
                    "aucune décision possible. C'est une limite du mod, pas un refus.");

                return true;
            }

            if (network.TryGetPickupResult(key, out var result))
            {
                if (result.Granted)
                {
                    _granted++;
                    return true;
                }

                _blocked++;
                _log.LogWarning(
                    $"[ramassage] ramassage BLOQUÉ : clé=0x{key:x16} '{ItemIdentity.TechName(__instance)}' " +
                    $"— le serveur l'a refusée (tenue par l'id {result.HolderId}). Objet laissé dans le monde.");

                return false;
            }

            // Aucune décision connue : on réclame et on laisse passer (voir l'en-tête). Une réclamation
            // déjà en vol pour la même clé n'est pas renvoyée : le joueur peut cliquer deux fois.
            if (!Pending.ContainsKey(key))
            {
                Pending[key] = __instance;
                if (network.SendPickupClaim(key, ItemIdentity.TechName(__instance)))
                {
                    _claimsSent++;
                    _log.LogInfo(
                        $"[ramassage] réclamation envoyée : clé=0x{key:x16} ({mechanism}) — ramassage local " +
                        "en attendant la décision.");
                }
                else
                {
                    Pending.Remove(key);
                }
            }

            return true;
        }

        /// <summary>
        /// Décision reçue du serveur. Appelée par la boucle réseau, sur le thread principal.
        ///
        /// Le refus d'un objet déjà ramassé optimistement déclenche le **retour arrière** : l'objet
        /// quitte l'inventaire et retourne dans le monde. Sans lui, un joueur garderait un objet que le
        /// serveur a refusé, et la décision ne serait qu'un message.
        /// </summary>
        public static void OnResult(PickupResultMessage result)
        {
            if (result == null)
            {
                return;
            }

            if (result.Granted)
            {
                Pending.Remove(result.ItemKey);
                return;
            }

            if (!Pending.TryGetValue(result.ItemKey, out var item))
            {
                // Refus d'un objet qui n'était pas en vol chez nous : rien à défaire.
                return;
            }

            Pending.Remove(result.ItemKey);

            if (item == null)
            {
                _log.LogWarning(
                    $"[ramassage] refus de 0x{result.ItemKey:x16} arrivé trop tard — l'objet n'existe plus, " +
                    "rien à retirer de l'inventaire.");

                return;
            }

            var inventory = Inventory.main;
            if (inventory == null || !inventory.TryRemoveItem(item))
            {
                _failedRollbacks++;
                _log.LogError(
                    $"[ramassage] RETOUR ARRIÈRE IMPOSSIBLE : 0x{result.ItemKey:x16} " +
                    $"'{ItemIdentity.TechName(item)}' n'a pas pu quitter l'inventaire — le joueur garde un " +
                    "objet que le serveur a refusé.");
                return;
            }

            // Relâché là où il est : le jeu le replace (le `Drop()` public passe par sa propre logique de
            // position, la même que la touche « lâcher »). Un objet refusé doit reapparaître, sinon il est
            // perdu pour tout le monde.
            item.Drop();
            _rollbacks++;

            _log.LogWarning(
                $"[ramassage] retour arrière : 0x{result.ItemKey:x16} '{ItemIdentity.TechName(item)}' retiré " +
                $"de l'inventaire et relâché (refus du serveur, tenu par l'id {result.HolderId}) — " +
                $"retours arrière={_rollbacks}.");
        }

        /// <summary>Résumé pour la sonde : ce que la garde a décidé, et combien de fois.</summary>
        public static string Summary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "réclamations={0} accords={1} blocages={2} retours arrière={3} échecs de retour={4} sans clé={5}",
                _claimsSent,
                _granted,
                _blocked,
                _rollbacks,
                _failedRollbacks,
                _unkeyed);
        }
    }
}
