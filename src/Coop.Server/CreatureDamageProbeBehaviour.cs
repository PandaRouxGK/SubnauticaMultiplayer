using System;
using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Server
{
    /// <summary>
    /// Sonde des **dégâts des créatures** (v17, `-coopdebug=creaturehit`).
    ///
    /// Pourquoi elle existe : la v17 transporte la fraction de vie d'une créature et annonce sa mort, mais
    /// sans une créature réellement blessée puis tuée sur le serveur, la chaîne n'est qu'une lecture de
    /// codec. Cette sonde **change l'état** de la créature que le serveur simule : elle la blesse à 40 % de
    /// sa vie, laisse le flux de créatures porter la valeur, puis la tue.
    ///
    /// Ce qu'elle fait, et pourquoi dans cet ordre :
    ///  1. attend le monde chargé (`AutoLoadBehaviour.WorldLoaded`) et un joueur — la même condition que
    ///     les autres sondes, sans quoi elle écrirait dans la scène de menu ;
    ///  2. prend la créature la plus proche de l'hôte, **avec sa clé** (`CreatureIdentity`), parce que
    ///     c'est cette clé que le client doit retrouver ;
    ///  3. écrit la fraction `0,4` **directement sur `LiveMixin.health`**, sans passer par `TakeDamage` :
    ///     on mesure le transport de la valeur, pas le système de dégâts du jeu ;
    ///  4. attend que le flux parte (le client reçoit à 10 Hz), puis écrit `0` — la mort est constatée par
    ///     le serveur (`santé 0`), qui annonce le retrait.
    ///
    /// Ce qu'elle ne prouve pas : qu'un **vrai** dégât du jeu (morsure, collision) produit ces valeurs.
    /// C'est le transport et la décision de mort qui sont mesurés, pas la physique du jeu.
    /// </summary>
    internal sealed class CreatureDamageProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Créature Dégâts");

        private const float WorldTimeoutSeconds = 240f;
        private const float FindTimeoutSeconds = 40f;
        private const float SearchRadiusMeters = 60f;
        private const float HurtFraction = 0.4f;
        private const float PropagationSeconds = 6f;

        /// <summary>Attente maximale (s) d'un client **dans le monde** avant de blesser quoi que ce soit.</summary>
        private const float ClientWaitSeconds = 180f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (!AutoLoadBehaviour.WorldLoaded && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (!AutoLoadBehaviour.WorldLoaded || Player.main == null)
            {
                Fail($"aucun monde chargé après {WorldTimeoutSeconds:F0}s");
                yield break;
            }

            // Attend qu'un client soit **dans le monde**, pas seulement admis : au premier run de cette
            // sonde (2026-10-21), la créature a été tuée pendant que le client chargeait encore sa scène ;
            // il a reçu le retrait avant de connaître la créature, puis a chargé le poisson vivant — la
            // sonde mesurait le chemin « retrait en attente », pas le transport et le retrait nominaux.
            var clientDeadline = Time.realtimeSinceStartup + ClientWaitSeconds;
            while ((ServerNetwork.Current == null || ServerNetwork.Current.ClientsInWorld == 0)
                   && Time.realtimeSinceStartup < clientDeadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (ServerNetwork.Current == null || ServerNetwork.Current.ClientsInWorld == 0)
            {
                Fail($"aucun client dans le monde après {ClientWaitSeconds:F0}s — la mort ne serait pas observable");
                yield break;
            }

            // Laisse le temps au client d'adopter (le flux de créatures part à 10 Hz) avant de blesser.
            yield return new WaitForSecondsRealtime(6f);

            Creature target = null;
            LiveMixin mixin = null;
            long key = 0L;
            var findDeadline = Time.realtimeSinceStartup + FindTimeoutSeconds;

            while (Time.realtimeSinceStartup < findDeadline && target == null)
            {
                target = FindNearestCreature(Player.main.transform.position, out mixin, out key);
                if (target == null)
                {
                    yield return new WaitForSecondsRealtime(1f);
                }
            }

            if (target == null || mixin == null || key == 0L)
            {
                Fail(
                    $"aucune créature avec clé et LiveMixin à moins de {SearchRadiusMeters:F0} m " +
                    $"après {FindTimeoutSeconds:F0}s — rien n'a été blessé");
                yield break;
            }

            var max = mixin.maxHealth;
            var before = mixin.health;
            var wounded = max * HurtFraction;

            // Écriture directe du champ : `TakeDamage` déclencherait les événements de mort du jeu, et
            // c'est la **valeur transportée** que cette sonde mesure.
            mixin.health = wounded;
            var afterWrite = mixin.health;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] créature santé : clé=0x{0:x16} '{1}' {2:F2} -> {3:F2} (max {4:F1}, " +
                    "écriture effectuée={5}) — le client doit porter la même fraction.",
                    key,
                    target.gameObject != null ? target.gameObject.name : "?",
                    before,
                    afterWrite,
                    max,
                    Math.Abs(afterWrite - wounded) < 0.01f));

            // Laisse le flux de créatures (10 Hz) porter la valeur, puis tue : la mort doit être une
            // annonce du serveur, pas une déduction du client sur une fraction à zéro.
            yield return new WaitForSecondsRealtime(PropagationSeconds);

            mixin.health = 0f;
            var afterKill = mixin.health;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] créature santé : clé=0x{0:x16} -> {1:F2} (mort écrite={2}) — le serveur doit " +
                    "annoncer le retrait, le client doit retirer sa copie.",
                    key,
                    afterKill,
                    afterKill <= 0f));

            Log.LogInfo(
                $"[sonde] créature santé : verdict=True — '{target.gameObject?.name}' blessée à " +
                $"{afterWrite / max:F2} puis tuée, clé=0x{key:x16} (transport et mort à vérifier côté client).");
        }

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] créature santé : verdict=False — {reason}.");
        }

        private static Creature FindNearestCreature(Vector3 origin, out LiveMixin mixin, out long key)
        {
            mixin = null;
            key = 0L;

            var creatures = UnityEngine.Object.FindObjectsOfType<Creature>();
            var nearest = (Creature)null;
            var nearestDistance = SearchRadiusMeters;
            var nearestMixin = (LiveMixin)null;
            var nearestKey = 0L;

            for (var i = 0; i < creatures.Length; i++)
            {
                var candidate = creatures[i];
                if (candidate == null || candidate.transform == null)
                {
                    continue;
                }

                var distance = Vector3.Distance(origin, candidate.transform.position);
                if (distance > nearestDistance)
                {
                    continue;
                }

                var candidateKey = CreatureIdentity.KeyOf(candidate, out _);
                if (candidateKey == 0L)
                {
                    continue;
                }

                var candidateMixin = candidate.GetComponent<LiveMixin>()
                                     ?? candidate.GetComponentInChildren<LiveMixin>();
                if (candidateMixin == null || candidateMixin.maxHealth <= 0f)
                {
                    continue;
                }

                // Une créature déjà morte n'apprend rien : la sonde veut une écriture observable.
                if (candidateMixin.health <= 0f)
                {
                    continue;
                }

                nearest = candidate;
                nearestDistance = distance;
                nearestMixin = candidateMixin;
                nearestKey = candidateKey;
            }

            mixin = nearestMixin;
            key = nearestKey;
            return nearest;
        }
    }
}
