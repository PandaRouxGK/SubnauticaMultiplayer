using System;
using System.Collections.Generic;
using System.Globalization;
using Coop.Model;
using UnityEngine;

namespace Coop.Shared
{
    /// <summary>
    /// Identité d'un véhicule, **calculée de la même façon par le serveur et par le client**.
    ///
    /// Pourquoi une clé : un véhicule n'a pas d'identifiant réseau, et son nom (`SeaMoth(Clone)`,
    /// `Exosuit(Clone)`) est celui du prefab, donc partagé par tous les exemplaires de la même espèce.
    /// Pour que le client puisse dire « ce Seamoth-là, c'est celui que le serveur déplace », il faut un
    /// identifiant qui ne dépende ni du pointeur, ni de l'ordre de chargement.
    ///
    /// Deux mécanismes, dans cet ordre, et **le journal dit lequel a servi** :
    ///
    /// 1. **`UniqueIdentifier.Id`** — l'identifiant que le jeu pose sur les objets du monde. Les
    ///    véhicules d'une sauvegarde sont des objets sérieux, instanciés depuis la sauvegarde : leur
    ///    identifiant y est écrit, donc les deux instances qui chargent la même partie lisent le même.
    ///    C'est le mécanisme par défaut, contrairement aux créatures — dont les bancs sont créés à
    ///    l'exécution et dont l'identifiant, mesuré, diffère d'une instance à l'autre (`CreatureIdentity`).
    /// 2. **Position d'apparition (repli)** — position du véhicule **à la première observation**,
    ///    quantifiée à 5 cm, plus le type et le nom du prefab. Ce repli couvre un véhicule fabriqué à
    ///    l'exécution (commande console, `spawn`), qui n'a pas d'identifiant sérieux.
    ///
    /// Le repli a une limite qui doit être dite : il suppose que les deux instances observent le
    /// véhicule **avant qu'il ait beaucoup bougé**. Un client qui rejoint une partie pendant que le
    /// Seamoth roule verrait une autre position d'ancrage, donc une autre clé — et le suivi ne
    /// l'apparierait pas. C'est pourquoi `RemoteVehicleManager` compte les clés inconnues au lieu de
    /// les deviner, et ne rattache un véhicule par proximité que dans un cas nommé au journal.
    ///
    /// Aucun des deux mécanismes disponible : la clé vaut `0`, le véhicule n'est pas diffusé. Une clé
    /// inventée ferait correspondre deux véhicules différents, ce qui est pire qu'un véhicule non
    /// synchronisé.
    ///
    /// Compilé dans les deux plugins via un &lt;Compile Include&gt; partagé : c'est la seule façon d'être
    /// sûr que les deux côtés calculent la même clé.
    /// </summary>
    internal static class VehicleIdentity
    {
        /// <summary>Mécanisme « identifiant du jeu » — le mécanisme par défaut pour un véhicule.</summary>
        public const string MechanismUniqueId = "identifiant";

        /// <summary>Mécanisme « position à la première observation » — repli pour un véhicule fabriqué.</summary>
        public const string MechanismSpawnPosition = "position d'apparition (repli)";

        /// <summary>Aucun des deux : le véhicule n'est pas diffusé.</summary>
        public const string MechanismNone = "aucune";

        /// <summary>Précision (m) à laquelle la position d'ancrage est quantifiée pour la clé.</summary>
        private const float AnchorQuantizationMeters = 0.05f;

        /// <summary>
        /// Nombre maximum d'ancres retenues. Un monde contient quelques véhicules ; le plafond n'existe
        /// que pour qu'un monde exotique ne fasse pas grossir la table indéfiniment.
        /// </summary>
        private const int MaxAnchors = 64;

        private struct Anchor
        {
            public int InstanceId;
            public Vector3 Position;
        }

        private static readonly Dictionary<int, Anchor> Anchors = new Dictionary<int, Anchor>();

        /// <summary>
        /// Clé stable du véhicule, ou 0 s'il n'en a pas. <paramref name="mechanism"/> dit lequel des
        /// deux mécanismes a répondu.
        /// </summary>
        public static long KeyOf(Vehicle vehicle, out string mechanism)
        {
            mechanism = MechanismNone;
            if (vehicle == null)
            {
                return 0L;
            }

            if (TryGetUniqueId(vehicle, out var uniqueId))
            {
                mechanism = MechanismUniqueId;
                return StableHash.Of("vehicle-uid|" + uniqueId);
            }

            var anchor = AnchorOf(vehicle);
            mechanism = MechanismSpawnPosition;

            var type = vehicle.GetType().Name;
            var name = vehicle.gameObject != null ? vehicle.gameObject.name : string.Empty;
            return StableHash.Of(
                "vehicle-spawn|" + type + "|" + name + "|" +
                Quantize(anchor.x) + "|" + Quantize(anchor.y) + "|" + Quantize(anchor.z));
        }

        /// <summary>
        /// Identifiant du monde, s'il existe et n'est pas vide.
        ///
        /// `GetComponentInParent` et non `GetComponent` : selon le prefab, l'identifiant peut être posé
        /// sur la racine de l'objet du monde plutôt que sur le composant `Vehicle` lui-même.
        /// </summary>
        public static bool TryGetUniqueId(Vehicle vehicle, out string id)
        {
            id = null;
            if (vehicle == null)
            {
                return false;
            }

            var identifier = vehicle.GetComponentInParent<UniqueIdentifier>();
            if (identifier == null)
            {
                return false;
            }

            id = identifier.Id;
            return !string.IsNullOrEmpty(id);
        }

        /// <summary>
        /// Position d'ancrage du repli : celle du véhicule **la première fois qu'il est vu**, mémorisée
        /// ensuite. Lire la position courante à chaque appel donnerait une clé qui change dès que le
        /// véhicule bouge — c'est-à-dire une clé qui ne désigne plus rien.
        /// </summary>
        private static Vector3 AnchorOf(Vehicle vehicle)
        {
            var instanceId = vehicle.GetInstanceID();
            if (Anchors.TryGetValue(instanceId, out var stored))
            {
                return stored.Position;
            }

            if (Anchors.Count >= MaxAnchors)
            {
                Anchors.Clear();
            }

            var position = vehicle.transform != null ? vehicle.transform.position : Vector3.zero;
            Anchors[instanceId] = new Anchor { InstanceId = instanceId, Position = position };
            return position;
        }

        /// <summary>Résumé d'un véhicule, pour un journal : ce qu'il faut pour le reconnaître.</summary>
        public static string Describe(Vehicle vehicle)
        {
            if (vehicle == null)
            {
                return "(véhicule absent)";
            }

            var position = vehicle.transform != null ? vehicle.transform.position : Vector3.zero;
            var key = KeyOf(vehicle, out var mechanism);

            return string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' ({1}) pos=({2:F1}, {3:F1}, {4:F1}) clé={5:x16} ({6})",
                vehicle.gameObject != null ? vehicle.gameObject.name : "?",
                vehicle.GetType().Name,
                position.x,
                position.y,
                position.z,
                key,
                mechanism);
        }

        private static string Quantize(float value)
        {
            var quantum = (long)Math.Round(value / AnchorQuantizationMeters);
            return quantum.ToString(CultureInfo.InvariantCulture);
        }
    }
}
