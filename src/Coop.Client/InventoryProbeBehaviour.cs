using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
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
    ///     **réellement** chez le client pour ce `TechType` ;
    ///  4. **(v20)** un **débit** accordé par le serveur vide le registre, et la réconciliation retire
    ///     l'objet de `Inventory.main` — c'est le serveur qui décide, pas le client ;
    ///  5. **(v20)** un second débit du même objet est **refusé nommément** (`UnknownItem`), sans avancer
    ///     la révision ni toucher à l'inventaire du jeu.
    ///
    /// Le troisième point est celui qui compte pour le registre : un registre qui ne correspondrait pas au
    /// monde serait pire qu'aucun registre, puisqu'il servirait de coût de fabrication au lot suivant. Les
    /// deux derniers mesurent la **réconciliation** : sans elle, un débit ne serait qu'une ligne de journal.
    ///
    /// **Ce que la sonde ne prouve pas** : que le jeu consomme réellement l'objet par ses propres chemins
    /// (craft, nourriture) et appelle le débit — la sonde demande le débit elle-même, à la place d'un
    /// consommateur ; ni que les objets **équipés** soient réconciliés (hors `Inventory.main.container`).
    /// </summary>
    internal sealed class InventoryProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Coop Inventaire");

        private const float SessionTimeoutSeconds = 240f;
        private const float FirstSnapshotSeconds = 60f;
        private const float PickupSeconds = 180f;
        private const float MatchingSnapshotSeconds = 30f;
        private const float DebitSeconds = 90f;
        private const float RefusalSeconds = 45f;

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
                yield break;
            }

            // — v20 : le débit décidé par le serveur, puis la réconciliation locale —————————————————
            //
            // La sonde **ne retire rien elle-même** : elle demande le débit et attend que `Inventory.main`
            // ait changé. Si le client retirait l'objet de son côté, la mesure ne dirait rien du serveur.
            var debitStartRevision = client.InventoryRevision;

            if (!client.SendInventoryDebit(pickedName, 1))
            {
                Fail("la demande de débit n'a pas pu partir");
                yield break;
            }

            var debitDeadline = Time.realtimeSinceStartup + DebitSeconds;
            while (Time.realtimeSinceStartup < debitDeadline)
            {
                if (client.InventoryDebitsGranted > 0
                    && client.InventoryCountOf(pickedName) == 0
                    && CountInLocalInventory(pickedName) == 0)
                {
                    break;
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            var debitGranted = client.InventoryDebitsGranted > 0;
            var serverAfterDebit = client.InventoryCountOf(pickedName);
            var localAfterDebit = CountInLocalInventory(pickedName);
            var removedLocally = client.InventoryRemovedLocally;
            var debitRevision = client.InventoryRevision;
            var revisionAdvancedByOne = debitRevision == debitStartRevision + 1;

            // Second débit : l'objet n'est plus dans le registre, le refus doit être **nommé** — sinon un
            // client ne saurait pas distinguer « consommé » de « refusé » et le registre resterait cru.
            if (!client.SendInventoryDebit(pickedName, 1))
            {
                Fail("la seconde demande de débit (celle qui doit être refusée) n'a pas pu partir");
                yield break;
            }

            var refusalDeadline = Time.realtimeSinceStartup + RefusalSeconds;
            while (client.InventoryDebitsRefused == 0 && Time.realtimeSinceStartup < refusalDeadline)
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }

            var refusedNamed = client.InventoryDebitsRefused > 0
                && string.Equals(
                    client.InventoryLastDebitRefusal,
                    nameof(InventoryChangeOutcome.UnknownItem),
                    StringComparison.Ordinal);
            var refusalKeptRevision = client.InventoryRevision == debitRevision;
            var refusalMovedNothing = client.InventoryCountOf(pickedName) == 0 && CountInLocalInventory(pickedName) == 0;

            var debitVerdict = debitGranted
                && revisionAdvancedByOne
                && serverAfterDebit == 0
                && localAfterDebit == 0
                && removedLocally >= 1
                && client.InventoryReconciliationFailures == 0
                && refusedNamed
                && refusalKeptRevision
                && refusalMovedNothing;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[sonde] débit : accordés={0} refusés={1} retirés localement={2} échecs={3}, '{4}' " +
                    "serveur={5} local={6}, révision {7} -> {8}, refus nommé={9} — verdict={10}",
                    client.InventoryDebitsGranted,
                    client.InventoryDebitsRefused,
                    removedLocally,
                    client.InventoryReconciliationFailures,
                    pickedName,
                    serverAfterDebit,
                    localAfterDebit,
                    debitStartRevision,
                    debitRevision,
                    client.InventoryLastDebitRefusal,
                    debitVerdict));

            if (!debitVerdict)
            {
                Log.LogError(
                    "[sonde] débit : verdict=False — " +
                    $"accordé={debitGranted}, révision avancée de 1={revisionAdvancedByOne} " +
                    $"({debitStartRevision} -> {debitRevision}), serveur={serverAfterDebit}, " +
                    $"local={localAfterDebit}, retirés localement={removedLocally}, " +
                    $"échecs={client.InventoryReconciliationFailures}, refus nommé={refusedNamed} " +
                    $"({client.InventoryLastDebitRefusal}), refus sans effet={refusalKeptRevision && refusalMovedNothing}.");
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
        private static int CountInLocalInventory(string techTypeName) =>
            InventoryReconciler.CountLocal(techTypeName);

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] inventaire : verdict=False — {reason}.");
        }
    }
}
