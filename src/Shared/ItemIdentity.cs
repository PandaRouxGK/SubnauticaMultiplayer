using System;
using System.Collections.Generic;
using System.Globalization;
using Coop.Model;
using UnityEngine;

namespace Coop.Shared
{
    /// <summary>
    /// Identité d'un **objet ramassable du monde**, calculée de la même façon par le serveur et par le
    /// client.
    ///
    /// Pourquoi une clé partagée : la décision de ramassage (M5) porte sur un objet précis — « ce
    /// Titanium-là est déjà réclamé » — et non sur son espèce. Les deux instances chargent la même
    /// sauvegarde, donc elles ont chacune leur exemplaire du même objet ; il faut un identifiant qui ne
    /// dépende ni du pointeur, ni de l'ordre de création, ni du hasard du chargement.
    ///
    /// Deux mécanismes, dans cet ordre, et **le journal dit lequel a servi** :
    ///
    /// 1. **`UniqueIdentifier.Id`** — l'identifiant que le jeu pose sur les objets du monde. Un objet
    ///    **sérialisé** (posé dans la sauvegarde, ou lâché puis sauvegardé) le porte, et les deux
    ///    instances lisent alors la même valeur — mesuré pour les véhicules (`VehicleIdentity`).
    /// 2. **Position d'ancrage (repli)** — position de l'objet **la première fois qu'il est observé**,
    ///    quantifiée à 5 cm, plus le nom du prefab et le `TechType`. C'est le mécanisme qui couvre un
    ///    objet **fabriqué à l'exécution** (butin de banc de poissons, `spawn`), dont l'identifiant est
    ///    tiré séparément dans chaque instance.
    ///
    /// Limite du repli, à dire : il suppose que les deux instances observent l'objet **avant qu'il ne
    /// bouge**. Un objet statique de la sauvegarde est dans ce cas ; un objet qui tombe ou qu'on pousse
    /// ne l'est pas, et sa clé peut alors différer d'un côté à l'autre. C'est pourquoi le serveur
    /// **refuse** une clé qu'il ne sait pas résoudre au lieu de la deviner, et que la sonde dit laquelle
    /// des deux clés elle a obtenue.
    ///
    /// Aucun des deux mécanismes disponible : la clé vaut `0`, et le ramassage n'est pas arbitré (il
    /// reste local, et c'est compté). Une clé inventée ferait refuser un objet légitime.
    ///
    /// Compilé dans les deux plugins via un &lt;Compile Include&gt; partagé : c'est la seule façon
    /// d'être sûr que les deux côtés calculent la même clé.
    /// </summary>
    internal static class ItemIdentity
    {
        /// <summary>Mécanisme « identifiant du jeu » — le mécanisme par défaut.</summary>
        public const string MechanismUniqueId = "identifiant";

        /// <summary>Mécanisme « position à la première observation » — repli pour un objet fabriqué.</summary>
        public const string MechanismAnchor = "position d'ancrage (repli)";

        /// <summary>Aucun des deux : l'objet n'est pas arbitré.</summary>
        public const string MechanismNone = "aucune";

        /// <summary>Précision (m) à laquelle la position d'ancrage est quantifiée pour la clé.</summary>
        private const float AnchorQuantizationMeters = 0.05f;

        /// <summary>
        /// Nombre maximum d'ancres retenues. Un monde contient beaucoup d'objets ramassables ; le
        /// plafond existe pour qu'une longue session ne fasse pas grossir la table indéfiniment. Le
        /// vider n'est pas grave : l'ancre est la position observée, et un objet statique la redonne.
        /// </summary>
        private const int MaxAnchors = 4096;

        private struct Anchor
        {
            public Vector3 Position;
        }

        private static readonly Dictionary<int, Anchor> Anchors = new Dictionary<int, Anchor>();

        /// <summary>
        /// Clé stable de l'objet, ou `0` s'il n'en a pas. <paramref name="mechanism"/> dit lequel des
        /// deux mécanismes a répondu.
        /// </summary>
        public static long KeyOf(Pickupable pickupable, out string mechanism)
        {
            mechanism = MechanismNone;
            if (pickupable == null)
            {
                return 0L;
            }

            if (TryGetUniqueId(pickupable, out var uniqueId))
            {
                mechanism = MechanismUniqueId;
                return StableHash.Of("item-uid|" + uniqueId);
            }

            var anchor = AnchorOf(pickupable);
            mechanism = MechanismAnchor;

            return StableHash.Of(
                "item-anchor|" + TechName(pickupable) + "|" + PrefabName(pickupable) + "|" +
                Quantize(anchor.x) + "|" + Quantize(anchor.y) + "|" + Quantize(anchor.z));
        }

        /// <summary>Nom technique de l'objet, tel que le jeu l'écrit (celui que le message transporte).</summary>
        public static string TechName(Pickupable pickupable)
        {
            if (pickupable == null)
            {
                return string.Empty;
            }

            try
            {
                return pickupable.GetTechType().ToString();
            }
            catch (Exception)
            {
                // Un `Pickupable` sans `TechTag` valide lève ici : l'objet n'a alors pas d'identité
                // d'espèce, donc pas de clé — on préfère une chaîne vide à une exception qui couperait
                // le ramassage du joueur.
                return string.Empty;
            }
        }

        /// <summary>Identifiant du monde, s'il existe et n'est pas vide.</summary>
        public static bool TryGetUniqueId(Pickupable pickupable, out string id)
        {
            id = null;
            if (pickupable == null)
            {
                return false;
            }

            // `GetComponentInParent` et non `GetComponent` : l'identifiant est posé sur la racine de
            // l'objet du monde, alors que le `Pickupable` peut vivre sur un enfant.
            var identifier = pickupable.GetComponentInParent<UniqueIdentifier>();
            if (identifier == null)
            {
                return false;
            }

            id = identifier.Id;
            return !string.IsNullOrEmpty(id);
        }

        /// <summary>
        /// Tous les objets ramassables **du monde**, hors inventaires.
        ///
        /// Pourquoi ce filtre : un objet ramassé continue d'exister (il est parenté au joueur), donc
        /// `FindObjectsOfTypeAll` le renvoie encore. L'inclure ferait croire au serveur qu'un objet
        /// déjà ramassé est encore dans le monde, et il accorderait sa clé à un second joueur.
        /// </summary>
        public static List<Pickupable> CollectWorldItems()
        {
            var items = new List<Pickupable>();
            var all = Resources.FindObjectsOfTypeAll<Pickupable>();

            for (var i = 0; i < all.Length; i++)
            {
                var item = all[i];
                if (item == null || item.gameObject == null)
                {
                    continue;
                }

                // Un prefab (asset) n'a pas de scène : ce n'est pas un objet du monde.
                if (!item.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (IsInLocalInventory(item))
                {
                    continue;
                }

                items.Add(item);
            }

            return items;
        }

        /// <summary>Vrai si l'objet est dans l'inventaire de **cette** instance.</summary>
        public static bool IsInLocalInventory(Pickupable pickupable)
        {
            if (pickupable == null)
            {
                return false;
            }

            var inventory = Inventory.main;
            return inventory != null && inventory.Contains(pickupable);
        }

        /// <summary>Résumé d'un objet, pour un journal : ce qu'il faut pour le reconnaître.</summary>
        public static string Describe(Pickupable pickupable)
        {
            if (pickupable == null)
            {
                return "(objet absent)";
            }

            var position = pickupable.transform != null ? pickupable.transform.position : Vector3.zero;
            var key = KeyOf(pickupable, out var mechanism);

            return string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' ({1}) pos=({2:F1}, {3:F1}, {4:F1}) clé={5:x16} ({6})",
                PrefabName(pickupable),
                TechName(pickupable),
                position.x,
                position.y,
                position.z,
                key,
                mechanism);
        }

        /// <summary>
        /// Position d'ancrage du repli : celle de l'objet **la première fois qu'il est vu**, mémorisée
        /// ensuite. Lire la position courante à chaque appel donnerait une clé qui change dès que
        /// l'objet bouge — c'est-à-dire une clé qui ne désigne plus rien.
        /// </summary>
        private static Vector3 AnchorOf(Pickupable pickupable)
        {
            var instanceId = pickupable.GetInstanceID();
            if (Anchors.TryGetValue(instanceId, out var stored))
            {
                return stored.Position;
            }

            if (Anchors.Count >= MaxAnchors)
            {
                Anchors.Clear();
            }

            var position = pickupable.transform != null ? pickupable.transform.position : Vector3.zero;
            Anchors[instanceId] = new Anchor { Position = position };
            return position;
        }

        private static string PrefabName(Pickupable pickupable)
        {
            return pickupable != null && pickupable.gameObject != null ? pickupable.gameObject.name : string.Empty;
        }

        private static string Quantize(float value)
        {
            var quantum = (long)Math.Round(value / AnchorQuantizationMeters);
            return quantum.ToString(CultureInfo.InvariantCulture);
        }
    }
}
