using System;
using System.Collections.Generic;
using Coop.Model;
using UnityEngine;

namespace Coop.Shared
{
    /// <summary>
    /// Lecture des **modules installés** sur un véhicule (M4, v15), d'une seule façon des deux côtés.
    ///
    /// Pourquoi une seule implémentation : le serveur décide de ce qui est installé, et le client compare
    /// ce qu'il lit chez lui à ce que le serveur annonce. Deux lectures divergentes produiraient un écart
    /// permanent qui ne veut rien dire — le compteur de divergence mesurerait la différence entre deux
    /// morceaux de code, pas entre deux mondes.
    ///
    /// L'identité d'un module est l'entier de `TechType` : les deux instances compilent le même jeu, donc
    /// la même énumération. Un nom aurait ajouté une chaîne variable au message.
    ///
    /// La lecture passe par les **slots** de l'équipement, pas par une énumération : mesuré au run du
    /// 2026-10-20, `(IEnumerable&lt;InventoryItem&gt;)vehicle.modules` lève `InvalidCastException` — `Equipment`
    /// n'expose que `IItemsContainer`. `GetSlots` + `GetTechTypeInSlot` sont l'API qui répond.
    /// </summary>
    public static class VehicleModules
    {
        /// <summary>
        /// Entiers `TechType` des modules installés, **triés et dédupliqués** (forme canonique du message).
        /// Un véhicule inconnu, détruit, ou sans console de modules rend un ensemble vide — pas une erreur :
        /// « pas de module » est un état normal, et un véhicule en cours de destruction ne doit pas faire
        /// perdre tout le balayage.
        /// </summary>
        public static ushort[] Read(Vehicle vehicle)
        {
            if (vehicle == null)
            {
                return Array.Empty<ushort>();
            }

            try
            {
                var modules = vehicle.modules;
                if (modules == null)
                {
                    return Array.Empty<ushort>();
                }

                var slots = new List<string>();
                modules.GetSlots(EquipmentType.VehicleModule, slots);

                var types = new List<ushort>(slots.Count);
                for (var i = 0; i < slots.Count; i++)
                {
                    var tech = modules.GetTechTypeInSlot(slots[i]);
                    if (tech == TechType.None)
                    {
                        continue;
                    }

                    var raw = (int)tech;
                    if (raw < 0 || raw > ushort.MaxValue)
                    {
                        // Une énumération hors de la borne du protocole ne peut pas voyager : l'ignorer
                        // en le disant vaut mieux que la tronquer en silence.
                        Debug.LogWarning($"[véhicule] module hors borne ignoré : {tech} ({raw})");
                        continue;
                    }

                    types.Add((ushort)raw);
                }

                return VehicleUpgradesMessage.Canonicalize(types.ToArray());
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[véhicule] modules illisibles : {exception.GetType().Name} — {exception.Message}");
                return Array.Empty<ushort>();
            }
        }

        /// <summary>Résumé lisible d'un ensemble de modules, pour les journaux et les sondes.</summary>
        public static string Describe(ushort[] types, out int count)
        {
            var list = types ?? Array.Empty<ushort>();
            count = list.Length;
            if (list.Length == 0)
            {
                return "aucun";
            }

            var names = new string[list.Length];
            for (var i = 0; i < list.Length; i++)
            {
                var tech = (TechType)list[i];
                names[i] = tech == TechType.None ? list[i].ToString() : tech.ToString();
            }

            return string.Join(",", names);
        }

        /// <summary>
        /// Charge normalisée `0..1` d'un véhicule, ou `-1` (inconnue) si aucun accumulateur n'est lisible.
        ///
        /// Mesuré au run du 2026-10-20 : `EnergyMixin.charge` d'un Seamoth vaut `200`, pas `1`. La v14
        /// comparait donc une charge absolue à une valeur normalisée et le serveur annonçait `1` en
        /// permanence — la borne `> 1` masquait l'unité. C'est `EnergyInterface.GetValues(charge, capacity)`
        /// qui donne l'échelle, et c'est elle qui fait la conversion.
        /// </summary>
        public static float ReadNormalized(Vehicle vehicle)
        {
            if (!TryRead(vehicle, out var charge, out var capacity))
            {
                return -1f;
            }

            if (capacity <= 0.001f)
            {
                return -1f;
            }

            var normalized = charge / capacity;
            return normalized < 0f ? 0f : (normalized > 1f ? 1f : normalized);
        }

        /// <summary>
        /// Écrit une charge normalisée sur un véhicule. Rend <c>true</c> si l'écriture a eu lieu, et
        /// <c>false</c> si la valeur était déjà là ou si aucun accumulateur n'est lisible — jamais une
        /// exception silencieuse : l'appelant compte ce qu'il a réellement appliqué.
        /// </summary>
        public static bool WriteNormalized(Vehicle vehicle, float normalized)
        {
            if (vehicle == null || normalized < 0f)
            {
                return false;
            }

            if (!TryRead(vehicle, out var charge, out var capacity) || capacity <= 0.001f)
            {
                return false;
            }

            var target = (normalized > 1f ? 1f : normalized) * capacity;
            var delta = target - charge;

            // 1 % de l'échelle : en dessous, l'écriture ne dirait rien de nouveau et réveillerait
            // l'interface d'énergie dix fois par seconde pour rien.
            if (Mathf.Abs(delta) < 0.01f * capacity)
            {
                return false;
            }

            try
            {
                // L'écriture passe par la **batterie** (`IBattery.charge` est accessible en écriture),
                // pas par `ModifyCharge` : mesuré au run du 2026-10-20, `EnergyInterface.ModifyCharge`
                // commence par `GameModeUtils.RequiresPower()` et ne fait rien quand le mode de jeu ne
                // consomme pas d'énergie — la charge restait à `200` après un drain de 50 %.
                if (TryFindBattery(vehicle, out var battery))
                {
                    battery.charge = target;
                    return true;
                }

                var mixin = vehicle.GetComponent<EnergyMixin>();
                if (mixin != null)
                {
                    mixin.ModifyCharge(delta);
                    return true;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[véhicule] écriture d'énergie refusée : {exception.GetType().Name} — {exception.Message}");
            }

            return false;
        }

        /// <summary>
        /// Batterie réellement écrite : celle de la cellule du véhicule, trouvée par les sources de son
        /// `EnergyInterface`, puis par le `EnergyMixin` du véhicule lui-même. `null` si le véhicule n'en
        /// porte aucune (un véhicule sans pile n'a pas de charge à synchroniser, et ce n'est pas une faute).
        /// </summary>
        public static bool TryFindBattery(Vehicle vehicle, out IBattery battery)
        {
            battery = null;
            if (vehicle == null)
            {
                return false;
            }

            var energy = vehicle.GetComponent<EnergyInterface>();
            if (energy != null && energy.sources != null)
            {
                for (var i = 0; i < energy.sources.Length; i++)
                {
                    var source = energy.sources[i];
                    if (source == null)
                    {
                        continue;
                    }

                    var found = source.GetBattery();
                    if (found != null)
                    {
                        battery = found;
                        return true;
                    }
                }
            }

            var mixin = vehicle.GetComponent<EnergyMixin>();
            if (mixin != null)
            {
                var found = mixin.GetBattery();
                if (found != null)
                {
                    battery = found;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Lit la charge absolue et la capacité du véhicule, par la même porte des deux côtés.</summary>
        public static bool TryRead(Vehicle vehicle, out float charge, out float capacity)
        {
            charge = 0f;
            capacity = 0f;

            if (vehicle == null)
            {
                return false;
            }

            try
            {
                var energy = vehicle.GetComponent<EnergyInterface>();
                if (energy != null)
                {
                    energy.GetValues(out charge, out capacity);
                    if (capacity > 0.001f)
                    {
                        return true;
                    }
                }

                var mixin = vehicle.GetComponent<EnergyMixin>();
                if (mixin != null)
                {
                    charge = mixin.charge;
                    capacity = mixin.capacity;
                    return capacity > 0.001f;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[véhicule] énergie illisible : {exception.GetType().Name} — {exception.Message}");
            }

            return false;
        }
    }
}
