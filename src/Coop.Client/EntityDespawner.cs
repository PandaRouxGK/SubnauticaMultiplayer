using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Retire chez le client l'entité que le serveur vient de faire disparaître (protocole v11, M6).
    ///
    /// Pourquoi ce composant existe : la v10 savait créer, pas retirer. Un Seamoth détruit chez l'hôte
    /// restait donc chez tous les clients, immobile et suivi par <c>RemoteVehicleManager</c> (le flux de
    /// positions ne s'arrêtait pas, il était simplement périmé). Et un objet ramassé par un joueur restait
    /// **visible et réclamable** chez les autres.
    ///
    /// Ce que le client ne fait pas : décider. Aucun retrait n'est demandé par un client ; le message vient
    /// du serveur, qui seul constate qu'une entité n'existe plus, ou qui vient de la retirer lui-même.
    ///
    /// Trois précautions, et chacune vient d'une erreur **mesurée** :
    ///
    /// 1. **Un objet dans un inventaire n'est pas dans le monde.** On ne parcourt que
    ///    <see cref="ItemIdentity.CollectWorldItems"/>, qui exclut déjà les inventaires : sans ça, un
    ///    ramassage accordé détruirait l'objet de celui qui vient de le ramasser.
    /// 2. **Rien n'est retiré avant que le monde ne soit chargé.** Le client se connecte au menu ; un retrait
    ///    reçu à ce moment-là ne trouverait rien, et l'objet **revient** avec la sauvegarde : mesuré à
    ///    l'étape L, le second client recevait le retrait du `PowerCell` 10 s avant son chargement, ne
    ///    trouvait rien à retirer, puis chargeait son monde avec l'objet intact.
    /// 3. **Une entité absente n'est pas une erreur.** Un client qui se connecte après la disparition reçoit
    ///    quand même le retrait : il le compte et l'écrit, plutôt que de crier au défaut — mais il le garde
    ///    en mémoire (`Removed`) pour qu'une apparition arrivée après coup ne ressuscite pas l'objet.
    /// </summary>
    internal sealed class EntityDespawner : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Disparition");

        /// <summary>Retraits reçus avant le chargement du monde : appliqués dès que le monde est là.</summary>
        private static readonly Queue<EntityDespawnMessage> Pending = new Queue<EntityDespawnMessage>();

        /// <summary>
        /// Clés annoncées disparues, gardées même quand l'entité était absente localement.
        ///
        /// Sert à une course réelle : un client qui charge encore son monde reçoit l'apparition **et** la
        /// disparition avant d'avoir créé quoi que ce soit (`EntitySpawner` attend `WorldLoaded`). Sans ce
        /// registre, la file d'apparitions recréerait un objet que le serveur a déjà détruit — un fantôme.
        /// </summary>
        private static readonly HashSet<long> Removed = new HashSet<long>();

        private static int _vehiclesRemoved;
        private static int _itemsRemoved;
        private static int _absent;
        private static long _lastRemovedKey;
        private static long _lastVehicleKey;

        internal static int VehiclesRemoved => _vehiclesRemoved;
        internal static int ItemsRemoved => _itemsRemoved;
        internal static int AbsentCount => _absent;
        internal static long LastRemovedKey => _lastRemovedKey;
        internal static long LastVehicleKey => _lastVehicleKey;

        /// <summary>Reçoit une annonce de retrait. Appelée par la boucle réseau, sur le thread principal.</summary>
        internal static void Handle(EntityDespawnMessage msg)
        {
            if (msg == null || msg.Key == 0L)
            {
                return;
            }

            Removed.Add(msg.Key);

            if (!AutoLoadBehaviour.WorldLoaded)
            {
                Pending.Enqueue(msg);
                Log.LogInfo(
                    $"[disparition] retrait de 0x{msg.Key:x16} mis en attente — le monde n'est pas encore " +
                    "chargé (appliqué dès qu'il le sera, sinon la sauvegarde ferait revenir l'objet).");
                return;
            }

            Apply(msg);
        }

        private void Update()
        {
            if (!AutoLoadBehaviour.WorldLoaded || Pending.Count == 0)
            {
                return;
            }

            var applied = 0;
            while (Pending.Count > 0)
            {
                Apply(Pending.Dequeue());
                applied++;
            }

            if (applied > 0)
            {
                Log.LogInfo($"[disparition] {applied} retrait(s) en attente appliqué(s) après le chargement du monde.");
            }
        }

        private static void Apply(EntityDespawnMessage msg)
        {
            if (msg.Kind == EntityKind.Vehicle)
            {
                RemoveVehicle(msg);
                return;
            }

            if (msg.Kind == EntityKind.Item)
            {
                RemoveItem(msg);
            }
        }

        private static void RemoveVehicle(EntityDespawnMessage msg)
        {
            var vehicle = FindVehicle(msg.Key);
            if (vehicle == null)
            {
                _absent++;
                Log.LogInfo(
                    $"[disparition] véhicule 0x{msg.Key:x16} absent localement — rien à retirer " +
                    $"(motif serveur « {msg.Reason} »). Ce client ne l'avait pas.");
                return;
            }

            var name = vehicle.gameObject != null ? vehicle.gameObject.name : "?";

            // Détruit avant de compter : si la destruction levait, le compteur mentirait.
            Object.Destroy(vehicle.gameObject);

            _vehiclesRemoved++;
            _lastRemovedKey = msg.Key;
            _lastVehicleKey = msg.Key;

            Log.LogInfo(
                $"[disparition] véhicule retiré : clé=0x{msg.Key:x16} '{name}' — {_vehiclesRemoved} retrait(s) " +
                $"de ce type, motif serveur « {msg.Reason} »");
        }

        private static void RemoveItem(EntityDespawnMessage msg)
        {
            var item = FindItem(msg.Key);
            if (item == null)
            {
                _absent++;
                Log.LogInfo(
                    $"[disparition] objet 0x{msg.Key:x16} absent du monde local — rien à retirer " +
                    $"(motif serveur « {msg.Reason} »). Il est peut-être dans un inventaire, ici.");
                return;
            }

            var described = ItemIdentity.Describe(item);
            Object.Destroy(item.gameObject);

            _itemsRemoved++;
            _lastRemovedKey = msg.Key;

            Log.LogInfo(
                $"[disparition] objet retiré : clé=0x{msg.Key:x16} ({described}) — {_itemsRemoved} retrait(s) " +
                $"d'objet, motif serveur « {msg.Reason} »");
        }

        /// <summary>Véhicule du monde local portant cette clé, s'il y en a un.</summary>
        private static Vehicle FindVehicle(long key)
        {
            foreach (var candidate in Object.FindObjectsOfType<Vehicle>())
            {
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (VehicleIdentity.KeyOf(candidate, out _) == key)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Objet du monde local portant cette clé. Le parcours exclut les inventaires : c'est la seule
        /// différence avec un `FindObjectsOfType` nu, et c'est celle qui compte (voir l'en-tête).
        /// </summary>
        private static Pickupable FindItem(long key)
        {
            foreach (var candidate in ItemIdentity.CollectWorldItems())
            {
                if (candidate == null || candidate.gameObject == null)
                {
                    continue;
                }

                if (ItemIdentity.KeyOf(candidate, out _) == key)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Vrai si cette clé a été annoncée disparue. Utilisé par <see cref="EntitySpawner"/> pour ne pas
        /// créer un objet que le serveur a déjà retiré avant que ce client n'ait chargé son monde.
        /// </summary>
        internal static bool IsGone(long key) => Removed.Contains(key);

        /// <summary>Vrai si l'entité annoncée existe encore localement — pour la sonde.</summary>
        internal static bool StillPresent(long key, byte kind)
        {
            return kind == EntityKind.Vehicle ? FindVehicle(key) != null : FindItem(key) != null;
        }

        internal static string Summary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "véhicules retirés={0} objets retirés={1} absents localement={2} en attente={3}",
                _vehiclesRemoved,
                _itemsRemoved,
                _absent,
                Pending.Count);
        }
    }
}
