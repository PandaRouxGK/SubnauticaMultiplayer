using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde **cliente** de l'inventaire serveur (v19, `-coopdebug=inventory`).
    ///
    /// Elle ne vérifie pas « un message est arrivé » : elle vérifie que le registre du serveur **dit la
    /// vérité sur le monde**. Pour cela elle a besoin d'un ramassage réel, que fait
    /// `-coopdebug=pickup` dans le même run :
    ///
    ///  1. l'instantané d'admission arrive (révision `0`, contenu vide) — le client sait où il en est ;
    ///  2. le ramassage est accordé, et un instantané de révision supérieure contient l'objet **×1** ;
    ///  3. la quantité annoncée par le serveur est comparée à ce que `Inventory.main` contient
    ///     **réellement** chez le client pour ce `TechType`.
    ///
    /// Le troisième point est celui qui compte : un registre qui ne correspondrait pas au monde serait
    /// pire qu'aucun registre, puisqu'il servirait de coût de fabrication au lot suivant.
    ///
    /// **Ce que la sonde ne prouve pas** : que le client **applique** l'instantané à son inventaire de jeu
    /// (il n'y a encore rien à appliquer — le serveur ne fait qu'enregistrer ce que le jeu a déjà accordé),
    /// ni qu'un retrait côté serveur se répercute sur l'objet en main. La réconciliation est le lot suivant.
    /// </summary>
    internal sealed class InventoryProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Inventaire");

        private const float SessionTimeoutSeconds = 240f;
        private const float FirstSnapshotSeconds = 60f;
        private const float PickupSeconds = 180f;
        private const float MatchingSnapshotSeconds = 30f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + SessionTimeoutSeconds;
            while ((ClientNetworkBehaviour.Current == null
                    || !ClientNetworkBehaviour.Current.Accepted
                    || !AutoLoadBehaviour.WorldLoaded
                    || Player.main == null)
                   && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(1f);
            }

            var client = ClientNetworkBehaviour.Current;
            if (client == null || !client.Accepted || !AutoLoadBehaviour.WorldLoaded || Player.main == null)
            {
                Fail($"pas de session acceptée / monde chargé après {SessionTimeoutSeconds:F0}s");
                yield break;
            }

            var snapshotDeadline = Time.realtimeSinceStartup + FirstSnapshotSeconds;
            while (client.InventoryMessages == 0 && Time.realtimeSinceStartup < snapshotDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            if (client.InventoryMessages == 0)
            {
                Fail($"aucun instantané d'inventaire en {FirstSnapshotSeconds:F0}s — le serveur ne l'a pas annoncé");
                yield break;
            }

            var admissionRevision = client.InventoryRevision;
            var admissionEntries = client.InventoryTotalCount;

            // Le ramassage réel est fait par la sonde de ramassage, dans le même run : elle seule a un
            // objet du monde et un geste du joueur. On attend sa décision sans la provoquer, sinon deux
            // sondes réclameraient le même objet.
            var pickupDeadline = Time.realtimeSinceStartup + PickupSeconds;
            while (client.PickupGrantedCount == 0 && Time.realtimeSinceStartup < pickupDeadline)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            if (client.PickupGrantedCount == 0)
            {
                // Sans ramassage, l'inventaire ne peut pas changer : conclure True ne mesurerait que le
                // transport de l'instantané vide. On échoue en nommant la cause.
                Fail($"aucun ramassage accordé en {PickupSeconds:F0}s — l'enregistrement n'a rien à vérifier " +
                     "(lancer `-coopdebug=pickup` en même temps)");
                yield break;
            }

            var pickedName = client.LastPickupGrantedName;

            // La décision du serveur arrive **avant** que le geste du jeu n'ait fini d'ajouter l'objet
            // localement : la réclamation part du préfixe de `OnHandClick`, et `Inventory.Pickup` n'a lieu
            // qu'après. Mesuré au premier run AB : `serveur=1 local=0` à l'instant du verdict, alors que
            // le ramassage local se produisait dans la même image. On attend donc que les deux quantités
            // se rejoignent, au lieu de lire une seule fois.
            var matchDeadline = Time.realtimeSinceStartup + MatchingSnapshotSeconds;
            var localCount = 0;

            while (Time.realtimeSinceStartup < matchDeadline)
            {
                localCount = CountInLocalInventory(pickedName);
                var announced = client.InventoryCountOf(pickedName);

                if (localCount > 0 && announced == localCount)
                {
                    break;
                }

                yield return new WaitForSecondsRealtime(0.5f);
            }

            var serverCount = client.InventoryCountOf(pickedName);
            var revisionAdvanced = client.InventoryRevision > admissionRevision;
            var matchesWorld = serverCount == localCount && serverCount > 0;
            var verdict = revisionAdvanced && matchesWorld && client.InventoryStaleSnapshots == 0;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] inventaire : messages={0}, révision {1} -> {2}, '{3}' serveur={4} local={5} " +
                    "(admission : révision {6}, {7} unité(s)), périmés écartés={8} — verdict={9}",
                    client.InventoryMessages,
                    admissionRevision,
                    client.InventoryRevision,
                    pickedName,
                    serverCount,
                    localCount,
                    admissionRevision,
                    admissionEntries,
                    client.InventoryStaleSnapshots,
                    verdict));

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] inventaire : verdict=False — " +
                    $"révision avancée={revisionAdvanced}, quantité du serveur={serverCount}, " +
                    $"quantité réelle={localCount} (elles doivent être égales et > 0), " +
                    $"périmés écartés={client.InventoryStaleSnapshots}.");
            }
        }

        /// <summary>
        /// Compte les objets réellement présents dans l'inventaire du jeu pour un `TechType`, par son nom.
        ///
        /// Pourquoi parcourir `Inventory.main.container` plutôt que `GetPickupCount` : cette dernière
        /// demande un `TechType`, et retrouver l'énumération depuis un nom transporté obligerait à
        /// parser une chaîne du protocole avec une API du jeu — deux sources d'erreur pour un comptage.
        /// Le nom de l'énumération est stable pour les objets de la sauvegarde.
        /// </summary>
        private static int CountInLocalInventory(string techTypeName)
        {
            if (string.IsNullOrEmpty(techTypeName) || Inventory.main == null || Inventory.main.container == null)
            {
                return 0;
            }

            var container = Inventory.main.container as IEnumerable<InventoryItem>;
            if (container == null)
            {
                return 0;
            }

            var count = 0;

            foreach (var item in container)
            {
                var pickupable = item != null ? item.item : null;
                if (pickupable == null)
                {
                    continue;
                }

                var type = pickupable.GetTechType();
                if (type == TechType.None)
                {
                    continue;
                }

                if (string.Equals(type.ToString(), techTypeName, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] inventaire : verdict=False — {reason}.");
        }
    }
}
