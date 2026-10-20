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
    /// Applique localement les modules qu'un véhicule porte **chez le serveur** (v15, M4).
    ///
    /// Pourquoi un composant et pas une méthode : le prefab d'un module se charge par une requête
    /// **asynchrone** du jeu (`CraftData.GetPrefabForTechTypeAsync`), donc par une coroutine. Une classe
    /// statique ne peut pas en démarrer une, et bloquer la boucle réseau pour attendre un prefab ferait
    /// tomber le client.
    ///
    /// Pourquoi l'application est **nommée et comptée** : chaque refus (pas de prefab, pas de slot libre,
    /// module déjà présent) laisse une ligne avec sa raison. C'est ce qui rend la sonde capable de dire
    /// « transporté mais non appliqué » au lieu de faire croire à une synchronisation qui n'a pas eu lieu.
    /// </summary>
    internal sealed class VehicleModuleApplier : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Modules");

        private struct Request
        {
            public Vehicle Vehicle;
            public ushort TechType;
            public bool Add;
        }

        private static readonly Queue<Request> Pending = new Queue<Request>();

        private static int _applied;
        private static int _removed;
        private static int _failed;
        private static int _refused;

        internal static int AppliedCount => _applied;
        internal static int RemovedCount => _removed;
        internal static int FailedCount => _failed;
        internal static int RefusedCount => _refused;

        /// <summary>Demande l'installation d'un module (une seule fois par véhicule et par module).</summary>
        internal static void RequestAdd(Vehicle vehicle, ushort techType)
        {
            if (vehicle == null)
            {
                return;
            }

            Pending.Enqueue(new Request { Vehicle = vehicle, TechType = techType, Add = true });
        }

        /// <summary>Demande le retrait d'un module devenu absent côté serveur.</summary>
        internal static void RequestRemove(Vehicle vehicle, ushort techType)
        {
            if (vehicle == null)
            {
                return;
            }

            Pending.Enqueue(new Request { Vehicle = vehicle, TechType = techType, Add = false });
        }

        private void Update()
        {
            // Rien avant le monde chargé : un module ajouté à la copie d'un véhicule de la scène de menu
            // disparaîtrait au chargement, exactement comme une entité fabriquée trop tôt.
            if (!AutoLoadBehaviour.WorldLoaded || Pending.Count == 0)
            {
                return;
            }

            while (Pending.Count > 0)
            {
                StartCoroutine(Apply(Pending.Dequeue()));
            }
        }

        private IEnumerator Apply(Request request)
        {
            if (request.Vehicle == null)
            {
                _failed++;
                yield break;
            }

            var tech = (TechType)request.TechType;
            if (tech == TechType.None)
            {
                _refused++;
                Log.LogWarning("[modules] un module annoncé se résout en TechType.None — ignoré.");
                yield break;
            }

            if (request.Add)
            {
                yield return Add(request.Vehicle, tech);
            }
            else
            {
                Remove(request.Vehicle, tech);
            }
        }

        private IEnumerator Add(Vehicle vehicle, TechType tech)
        {
            var modules = SafeModules(vehicle);
            if (modules == null)
            {
                _refused++;
                Log.LogWarning(
                    $"[modules] '{vehicle.name}' n'a pas de console de modules — '{tech}' non appliqué.");
                yield break;
            }

            if (HasModule(modules, tech))
            {
                // L'état a pu être appliqué entre-temps (deux annonces, ou annonce + rejeu) : ce n'est
                // pas un échec, et le compter comme tel ferait échouer la sonde sur un doublon bénin.
                yield break;
            }

            if (!modules.GetFreeSlot(EquipmentType.VehicleModule, out var slot))
            {
                _refused++;
                Log.LogWarning(
                    $"[modules] '{vehicle.name}' n'a pas de slot libre pour '{tech}' — non appliqué " +
                    "(le véhicule du serveur en porte plus que cette copie).");
                yield break;
            }

            CoroutineTask<GameObject> request;
            try
            {
                request = CraftData.GetPrefabForTechTypeAsync(tech, false);
            }
            catch (Exception exception)
            {
                _failed++;
                Log.LogError(
                    $"[modules] demande de prefab '{tech}' rejetée : {exception.GetType().Name} — {exception.Message}");
                yield break;
            }

            yield return request;

            var prefab = request.GetResult();
            if (prefab == null)
            {
                _failed++;
                Log.LogError($"[modules] prefab introuvable pour '{tech}' — non appliqué.");
                yield break;
            }

            GameObject created;
            try
            {
                created = CraftData.InstantiateFromPrefab(prefab, tech, false);
            }
            catch (Exception exception)
            {
                _failed++;
                Log.LogError(
                    $"[modules] instanciation de '{tech}' échouée : {exception.GetType().Name} — {exception.Message}");
                yield break;
            }

            var pickupable = created != null ? created.GetComponent<Pickupable>() : null;
            if (pickupable == null)
            {
                _failed++;
                Log.LogError($"[modules] '{tech}' n'a pas de Pickupable — non appliqué.");
                yield break;
            }

            bool added;
            try
            {
                added = modules.AddItem(slot, new InventoryItem(pickupable), false);
            }
            catch (Exception exception)
            {
                _failed++;
                Log.LogError(
                    $"[modules] ajout de '{tech}' dans le slot '{slot}' levé : {exception.GetType().Name} — {exception.Message}");
                yield break;
            }

            if (!added)
            {
                _refused++;
                Log.LogWarning($"[modules] '{tech}' refusé par l'équipement du véhicule (slot '{slot}') — non appliqué.");
                yield break;
            }

            // L'objet instancié ne doit pas rester visible dans le monde : il est **dans** le véhicule.
            created.SetActive(false);
            _applied++;

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[modules] appliqué : '{0}' sur '{1}' (slot '{2}').",
                    tech,
                    vehicle.name,
                    slot));
        }

        private static void Remove(Vehicle vehicle, TechType tech)
        {
            var modules = SafeModules(vehicle);
            if (modules == null)
            {
                _refused++;
                return;
            }

            var slots = new List<string>();
            try
            {
                modules.GetSlots(EquipmentType.VehicleModule, slots);
            }
            catch (Exception exception)
            {
                _failed++;
                Log.LogError(
                    $"[modules] slots illisibles sur '{vehicle.name}' : {exception.GetType().Name} — {exception.Message}");
                return;
            }

            for (var i = 0; i < slots.Count; i++)
            {
                if (modules.GetTechTypeInSlot(slots[i]) != tech)
                {
                    continue;
                }

                try
                {
                    modules.RemoveItem(slots[i], true, false);
                    _removed++;
                    Log.LogInfo($"[modules] retiré : '{tech}' de '{vehicle.name}' (slot '{slots[i]}').");
                }
                catch (Exception exception)
                {
                    _failed++;
                    Log.LogError(
                        $"[modules] retrait de '{tech}' levé : {exception.GetType().Name} — {exception.Message}");
                }

                return;
            }

            // Le module n'était pas là : rien à faire, ce n'est pas un échec.
        }

        private static Equipment SafeModules(Vehicle vehicle)
        {
            try
            {
                return vehicle != null ? vehicle.modules : null;
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[modules] équipement illisible : {exception.GetType().Name} — {exception.Message}");
                return null;
            }
        }

        private static bool HasModule(Equipment modules, TechType tech)
        {
            var slots = new List<string>();
            try
            {
                modules.GetSlots(EquipmentType.VehicleModule, slots);
                for (var i = 0; i < slots.Count; i++)
                {
                    if (modules.GetTechTypeInSlot(slots[i]) == tech)
                    {
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[modules] lecture des slots levée : {exception.GetType().Name} — {exception.Message}");
            }

            return false;
        }
    }
}
