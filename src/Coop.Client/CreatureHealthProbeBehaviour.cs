using System;
using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde **cliente** des dégâts de créature (v17, `-coopdebug=creaturehealth`).
    ///
    /// Le pendant de `CreatureDamageProbeBehaviour` (serveur) : l'hôte blesse puis tue une créature, et
    /// cette sonde constate **chez le client** deux choses distinctes, qui ne se confondent pas :
    ///  1. la fraction de vie **reçue** a été écrite sur la copie locale (`HealthApplied`) — le transport
    ///     et l'application, pas seulement la réception d'un message ;
    ///  2. la créature **retirée** l'est réellement : la clé n'est plus connue du gestionnaire après
    ///     l'annonce de mort, et le client l'a comptée (`CreatureDespawns`).
    ///
    /// Pourquoi les deux : une santé appliquée sans retrait laisserait un cadavre debout chez le client,
    /// et un retrait sans santé ne prouverait pas que la valeur a voyagé.
    ///
    /// Ce que la sonde ne prouve pas : que la copie est **invisible** (aucune capture d'écran) ni qu'un
    /// dégât du jeu (morsure, collision) produit cette valeur — la sonde serveur écrit la fraction.
    /// </summary>
    internal sealed class CreatureHealthProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Créature Santé");

        private const float WorldTimeoutSeconds = 200f;
        private const float HealthTimeoutSeconds = 60f;
        private const float DespawnTimeoutSeconds = 90f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while ((ClientNetworkBehaviour.Current == null
                    || !ClientNetworkBehaviour.Current.Accepted
                    || !AutoLoadBehaviour.WorldLoaded
                    || Player.main == null)
                   && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            if (ClientNetworkBehaviour.Current == null
                || !ClientNetworkBehaviour.Current.Accepted
                || !AutoLoadBehaviour.WorldLoaded
                || Player.main == null)
            {
                Fail($"pas de session acceptée / monde chargé après {WorldTimeoutSeconds:F0}s");
                yield break;
            }

            var manager = RemoteCreatureManager.Active;
            if (manager == null)
            {
                Fail("aucun gestionnaire de créatures (le plugin n'a pas été initialisé)");
                yield break;
            }

            var healthDeadline = Time.realtimeSinceStartup + HealthTimeoutSeconds;
            while (manager.HealthApplied == 0 && Time.realtimeSinceStartup < healthDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            var healthApplied = manager.HealthApplied > 0;
            var lastHealth = manager.LastAppliedHealth;
            var lastHealthKey = manager.LastHealthKey;

            var despawnDeadline = Time.realtimeSinceStartup + DespawnTimeoutSeconds;
            while (manager.CreatureDespawns == 0 && Time.realtimeSinceStartup < despawnDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            // Une seconde de marge : la destruction est différée d'une image, et un état déjà en vol ne
            // doit pas faire croire que la copie est revenue.
            yield return new WaitForSecondsRealtime(1.5f);

            var despawned = manager.CreatureDespawns > 0;
            var despawnKey = manager.LastDespawnKey;
            var stillKnown = manager.IsKnown(despawnKey);
            var verdict = healthApplied && despawned && despawnKey != 0L && !stillKnown;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] créature santé : appliquée={0} dernière={1:F2} clé=0x{2:x16}, retirée={3} " +
                    "(clé=0x{4:x16}, encore connue={5}, absente à l'arrivée={6}), états de clé morte={7} " +
                    "— verdict={8}",
                    manager.HealthApplied,
                    lastHealth,
                    lastHealthKey,
                    despawned,
                    despawnKey,
                    stillKnown,
                    manager.CreatureDespawnAbsent,
                    manager.StatesDeadKey,
                    verdict));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] créature santé : verdict=False — " +
                    $"santé appliquée={healthApplied}, retrait annoncé={despawned}, " +
                    $"clé retirée encore connue={stillKnown}.");
            }
        }

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] créature santé : verdict=False — {reason}.");
        }
    }
}
