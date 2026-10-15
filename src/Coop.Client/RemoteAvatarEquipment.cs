using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Fait porter à l'avatar la tenue de **son** joueur, au lieu de celle du joueur local.
    ///
    /// Le corps du joueur contient toutes les tenues à la fois, et c'est le jeu qui n'en laisse voir
    /// qu'une. La copie héritait donc de l'équipement du joueur local : tant qu'on jouait seul avec la
    /// même tenue, personne ne le voyait — dès que deux joueurs diffèrent, l'avatar est faux. Ce n'est
    /// pas un détail de finition, c'est la première chose qu'un joueur remarquera.
    ///
    /// Les règles ne sont **pas devinées** : elles sont transcrites des gestionnaires de visibilité de
    /// Nitrox (`NitroxClient/GameLogic/PlayerLogic/PlayerModel/Equipment/*VisibilityHandler.cs`,
    /// commit `7815b4e`, v1.9.0.0), y compris les chemins de nœuds
    /// (`PlayerEquipmentConstants.cs`) et les dépendances croisées — une bouteille ne se montre que si
    /// la combinaison correspondante est portée, le casque anti-radiations remplace la tête, etc.
    ///
    /// Deux écarts assumés par rapport à Nitrox :
    ///
    /// - Nitrox construit six objets gestionnaires par joueur distant, avec une liste qu'il faut penser
    ///   à ne pas partager entre joueurs (le commentaire de `PlayerModelManager` mentionne un bug causé
    ///   exactement par ça). Ici les règles sont **une fonction pure** de l'équipement vers l'état des
    ///   nœuds : rien à partager, rien à réinitialiser, et on peut l'appeler autant de fois qu'on veut.
    /// - Nitrox appelle `Transform.Find` sans garde : un nœud absent lève une `NullReferenceException`.
    ///   Ici un nœud absent est **compté et nommé**, jamais fatal — c'est la leçon de §16, où recréer un
    ///   renderer a fait tomber le jeu. Une tenue partiellement appliquée doit se lire dans le journal.
    /// </summary>
    internal static class RemoteAvatarEquipment
    {
        /// <summary>Source de log partagée avec la fabrique : un seul préfixe pour tout l'avatar.</summary>
        private static readonly ManualLogSource Log = RemoteAvatarFactory.Log;

        // Chemins des nœuds, relatifs à la racine du modèle (`player_view`, le parent de `male_geo`).
        // Recopiés tels quels de `PlayerEquipmentConstants` de Nitrox : ce sont des noms d'objets du jeu,
        // pas un choix de notre part, et une faute de frappe se traduirait par un nœud « absent ».
        public const string DiveSuitHead = "male_geo/diveSuit/diveSuit_head_geo";
        public const string DiveSuitHands = "male_geo/diveSuit/diveSuit_hands_geo";
        public const string DiveSuitBody = "male_geo/diveSuit/diveSuit_body_geo";

        public const string FinsRoot = "male_geo/generalSuit";
        public const string Fins = "male_geo/generalSuit/flippers_basic_geo";
        public const string ChargedFinsRoot = "male_geo/SwimChargeFins";
        public const string ChargedFins = "male_geo/SwimChargeFins/flippers_basic_geo";
        public const string GlideFinsRoot = "male_geo/UltraGlideFins";
        public const string GlideFins = "male_geo/UltraGlideFins/flippers_basic_geo";

        public const string StillSuit = "male_geo/stillSuit/still_suit_01_body_geo";

        public const string ScubaRoot = "male_geo/scubaSuit";
        public const string Rebreather = "male_geo/scubaSuit/scuba_head";
        public const string ScubaTank = "male_geo/scubaSuit/scuba_vest";
        public const string ScubaTankTubes = "male_geo/scubaSuit/scuba_vest/scuba_breatherTubes";

        public const string ReinforcedSuit = "male_geo/reinforcedSuit/reinforced_suit_01_body_geo";
        public const string ReinforcedGloves = "male_geo/reinforcedSuit/reinforced_suit_01_glove_geo";

        public const string RadiationSuit = "male_geo/radiationSuit/radiationSuit_body_geo";
        public const string RadiationGloves = "male_geo/radiationSuit/radiationSuit_gloves_geo";
        public const string RadiationSuitVest = "male_geo/radiationSuit/radiationSuit_vest_reducedNeck_geo";
        public const string RadiationHead = "male_geo/radiationSuit/radiationSuit_head_geo";
        public const string RadiationHelmet =
            "male_geo/radiationSuit/radiationSuit_vest_reducedNeck_geo/radiationSuit_vest_neckExtension_geo/radiationSuit_helmet_geo 1";
        public const string RadiationSuitNeckClasp =
            "male_geo/radiationSuit/radiationSuit_vest_reducedNeck_geo/radiationSuit_vest_neckExtension_geo";
        public const string RadiationTank =
            "male_geo/radiationSuit/radiationSuit_vest_reducedNeck_geo/radiationSuit_tank_geo 1";
        public const string RadiationTankTubes =
            "male_geo/radiationSuit/radiationSuit_vest_reducedNeck_geo/radiationSuit_tank_geo 1/radiationSuit_tubes_geo 1";

        /// <summary>
        /// Point d'accroche des objets tenus. Nitrox retire du modèle distant ce que le joueur tient
        /// en main ; l'objet n'a rien à y faire, c'est celui du joueur local.
        /// </summary>
        public const string AttachPoint =
            "export_skeleton/head_rig/neck/chest/clav_R/clav_R_aim/shoulder_R/elbow_R/hand_R/attach1";

        /// <summary>Nœuds que ces règles pilotent : ce que le journal doit pouvoir lister sans les lire.</summary>
        private static readonly string[] Governed =
        {
            DiveSuitHead, DiveSuitBody, DiveSuitHands,
            FinsRoot, Fins, ChargedFinsRoot, ChargedFins, GlideFinsRoot, GlideFins,
            StillSuit,
            ScubaRoot, Rebreather, ScubaTank, ScubaTankTubes,
            ReinforcedSuit, ReinforcedGloves,
            RadiationSuit, RadiationGloves, RadiationSuitVest, RadiationHead,
            RadiationHelmet, RadiationSuitNeckClasp, RadiationTank, RadiationTankTubes,
        };

        /// <summary>
        /// Applique l'équipement d'un joueur distant à sa copie.
        ///
        /// <paramref name="equipment"/> est ce que le réseau devra transporter : les `TechType` portés.
        /// Une liste vide décrit le joueur **sans équipement**, c'est-à-dire en scaphandre de base — et
        /// c'est le bon état de départ, parce que ces règles sont complètes : chaque nœud est décidé,
        /// donc aucun état hérité du joueur local ne peut subsister.
        /// </summary>
        /// <returns>Un compte rendu d'une ligne, journalisable et comparable entre deux exécutions.</returns>
        public static string Apply(GameObject clone, IReadOnlyCollection<TechType> equipment)
        {
            var model = FindModelRoot(clone);
            if (model == null)
            {
                Log.LogWarning("[avatar] équipement non appliqué : racine du modèle introuvable (parent de `male_geo`).");
                return "racine du modèle introuvable";
            }

            var has = new Func<TechType, bool>(type => equipment != null && equipment.Contains(type));

            var missing = new List<string>();
            var visible = 0;
            var hidden = 0;
            var unchanged = 0;

            void Set(string path, bool show)
            {
                var node = model.Find(path);
                if (node == null)
                {
                    missing.Add(path);
                    return;
                }

                var was = node.gameObject.activeSelf;
                if (was == show)
                {
                    unchanged++;
                    return;
                }

                node.gameObject.SetActive(show);
                if (show)
                {
                    visible++;
                }
                else
                {
                    hidden++;
                }
            }

            // — Scaphandre de base (`DiveSuitVisibilityHandler`) : c'est le corps nu du modèle, celui qui
            //   porte aussi la tête visible. Il s'efface devant toute combinaison, et la tête devant tout
            //   casque.
            var radiationHelmet = has(TechType.RadiationHelmet);
            var rebreather = has(TechType.Rebreather);
            Set(DiveSuitHead, !radiationHelmet && !rebreather);
            Set(DiveSuitBody,
                !has(TechType.RadiationSuit) && !has(TechType.WaterFiltrationSuit) && !has(TechType.ReinforcedDiveSuit));
            Set(DiveSuitHands, !has(TechType.RadiationGloves) && !has(TechType.ReinforcedGloves));

            // — Palmes (`FinsVisibilityHandler`) : trois paires distinctes, chacune avec sa racine.
            var basicFins = has(TechType.Fins);
            var chargedFins = has(TechType.SwimChargeFins);
            var glideFins = has(TechType.UltraGlideFins);
            Set(FinsRoot, basicFins);
            Set(Fins, basicFins);
            Set(ChargedFinsRoot, chargedFins);
            Set(ChargedFins, chargedFins);
            Set(GlideFinsRoot, glideFins);
            Set(GlideFins, glideFins);

            // — Combinaison anti-radiations (`RadiationSuitVisibilityHandler`) : la seule dont les pièces
            //   dépendent les unes des autres. Bouteille et tuyaux ne se montrent que si le gilet est
            //   visible, et les tuyaux seulement avec le casque.
            var radiationSuit = has(TechType.RadiationSuit);
            var radiationGloves = has(TechType.RadiationGloves);
            var vestVisible = radiationSuit || radiationHelmet;
            var radiationTankVisible = TankEquipped(has) && vestVisible;
            Set(RadiationHead, radiationHelmet);
            Set(RadiationHelmet, radiationHelmet);
            Set(RadiationGloves, radiationGloves);
            Set(RadiationSuit, radiationSuit);
            Set(RadiationSuitNeckClasp, radiationHelmet);
            Set(RadiationSuitVest, vestVisible);
            Set(RadiationTank, radiationTankVisible);
            Set(RadiationTankTubes, radiationTankVisible && radiationHelmet);

            // — Combinaison renforcée (`ReinforcedSuitVisibilityHandler`).
            Set(ReinforcedSuit, has(TechType.ReinforcedDiveSuit));
            Set(ReinforcedGloves, has(TechType.ReinforcedGloves));

            // — Détendeur et bouteille (`ScubaSuitVisibilityHandler`) : le gilet du scuba **est** la
            //   bouteille du jeu. Elle s'efface quand la combinaison anti-radiations, qui a la sienne,
            //   est portée.
            var scubaTankVisible = TankEquipped(has) && !radiationSuit;
            var scubaTubesVisible = (rebreather || radiationHelmet) && scubaTankVisible;
            Set(Rebreather, rebreather);
            Set(ScubaRoot, rebreather || scubaTankVisible);
            Set(ScubaTank, scubaTankVisible);
            Set(ScubaTankTubes, scubaTubesVisible);

            // — Combinaison de survie (`StillSuitVisibilityHandler`).
            Set(StillSuit, has(TechType.WaterFiltrationSuit));

            var report = new StringBuilder();
            report.Append($"{equipment?.Count ?? 0} pièce(s) -> {visible} nœud(s) montré(s), {hidden} masqué(s), " +
                          $"{unchanged} déjà dans le bon état");
            if (missing.Count > 0)
            {
                // Jamais silencieux : un chemin qui ne correspond plus est le signe d'un modèle de jeu qui
                // a changé, et c'est exactement ce qu'on veut voir dans le journal plutôt qu'à l'œil.
                report.Append($", {missing.Count} nœud(s) INTROUVABLE(S) : {string.Join(", ", missing)}");
                Log.LogWarning($"[avatar] équipement : {missing.Count} nœud(s) introuvable(s) dans le modèle : " +
                               $"{string.Join(", ", missing)}");
            }

            Log.LogInfo($"[avatar] équipement appliqué : {report}");
            return report.ToString();
        }

        /// <summary>
        /// Même chose, à partir de **noms** de `TechType` — la forme sous laquelle l'équipement arrivera
        /// du réseau, où l'on ne veut pas dépendre de la valeur numérique d'une énumération du jeu.
        /// </summary>
        /// <returns>Les noms qui n'ont pas pu être reconnus (liste vide si tout va bien).</returns>
        public static IReadOnlyList<string> ApplyNames(GameObject clone, IEnumerable<string> techTypeNames)
        {
            var equipment = new List<TechType>();
            var unknown = new List<string>();

            foreach (var name in techTypeNames ?? Array.Empty<string>())
            {
                if (Enum.TryParse(name, true, out TechType type) && Enum.IsDefined(typeof(TechType), type))
                {
                    equipment.Add(type);
                }
                else
                {
                    unknown.Add(name);
                }
            }

            Apply(clone, equipment);

            if (unknown.Count > 0)
            {
                Log.LogWarning($"[avatar] équipement : {unknown.Count} nom(s) de TechType inconnu(s) ignoré(s) : " +
                               $"{string.Join(", ", unknown)}");
            }

            return unknown;
        }

        /// <summary>
        /// Retire du modèle distant l'objet que le joueur local tient en main.
        ///
        /// Voir <see cref="AttachPoint"/> pour ce que Nitrox appelle `ITEM_ATTACH_POINT_GAME_OBJECT_NAME`.
        ///
        /// Il n'est pas dans `male_geo` : le jeu l'accroche sur l'os `attach1` du squelette. Une copie du
        /// corps l'emporte donc avec elle, et le joueur distant se retrouve à brandir l'outil de
        /// quelqu'un d'autre — ce que Nitrox corrige explicitement. On désactive au lieu de détruire :
        /// c'est réversible, et une copie n'a aucune raison de supprimer des objets du jeu.
        /// </summary>
        public static string HideAttachedItems(GameObject clone)
        {
            var model = FindModelRoot(clone);
            var attach = model != null ? model.Find(AttachPoint) : null;
            if (attach == null)
            {
                Log.LogWarning($"[avatar] objet tenu : point d'accroche introuvable ('{AttachPoint}').");
                return "point d'accroche introuvable";
            }

            var hidden = 0;
            var items = new List<string>();
            var structural = new List<string>();

            for (var index = 0; index < attach.childCount; index++)
            {
                var child = attach.GetChild(index);

                // Un objet tenu est **dessinable** : c'est ce qui le distingue des nœuds du jeu qui
                // portent l'orientation (`attach1_forward`, `attach1_up`), mesurés présents en
                // permanence sur `attach1`. Le premier essai masquait ces deux nœuds et annonçait
                // « 2 objet(s) » : un compte rendu qui décrit un travail qui n'a pas eu lieu est pire
                // qu'une absence de compte rendu.
                var renderers = child.GetComponentsInChildren<Renderer>(true).Length;
                if (renderers == 0)
                {
                    structural.Add(child.name);
                    continue;
                }

                items.Add($"{child.name} ({renderers} renderer(s))");
                if (child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(false);
                    hidden++;
                }
            }

            var report = $"{hidden} objet(s) tenu(s) masqué(s)" +
                         (items.Count == 0 ? " (rien de dessinable sur attach1)" : $" [{string.Join(", ", items)}]") +
                         $", {structural.Count} nœud(s) structurel(s) laissé(s) tel quel" +
                         (structural.Count == 0 ? string.Empty : $" [{string.Join(", ", structural)}]");
            Log.LogInfo($"[avatar] objet tenu : {report}");
            return report;
        }

        /// <summary>
        /// Lit l'équipement du **joueur local**, sous forme de noms de `TechType` — c'est la moitié
        /// « émission » du travail : ce que le client devra mettre dans un paquet pour que les autres
        /// affichent la bonne tenue.
        ///
        /// L'API n'est pas devinée, elle a été relevée dans `Assembly-CSharp.dll` par `tools/ApiDump` :
        /// `Inventory.main` (champ statique public), `Inventory.equipment` (propriété),
        /// `Equipment.GetEquipment()` (énumérateur de `Dictionary<string, InventoryItem>`) et
        /// `Pickupable.GetTechType()`. C'est aussi ce que fait Nitrox (`EquipmentHelper` :
        /// `o.IsLocalPlayer ? Inventory.main.equipment : null`).
        ///
        /// Renvoie une liste vide et <paramref name="failure"/> renseigné si l'équipement n'est pas
        /// lisible : mieux vaut un paquet sans équipement qu'une exception dans la boucle réseau.
        /// </summary>
        public static IReadOnlyList<string> ReadLocalEquipment(out string failure)
        {
            failure = null;
            var names = new List<string>();

            var inventory = Inventory.main;
            var equipment = inventory != null ? inventory.equipment : null;
            if (equipment == null)
            {
                failure = inventory == null ? "Inventory.main absent" : "Inventory.equipment absent";
                Log.LogWarning($"[avatar] équipement local illisible ({failure}) : rien à envoyer.");
                return names;
            }

            try
            {
                // `GetEquipment()` renvoie l'énumérateur d'un `Dictionary`, pas une collection : il se
                // parcourt avec `MoveNext()`/`Current`, ce que la mesure a montré (le `foreach` direct
                // ne compile pas, et c'est une bonne chose : la différence est explicite).
                var slots = equipment.GetEquipment();
                while (slots.MoveNext())
                {
                    var item = slots.Current.Value;
                    var pickupable = item != null ? item.item : null;
                    if (pickupable == null)
                    {
                        continue;
                    }

                    names.Add(pickupable.GetTechType().ToString());
                }
            }
            catch (Exception exception)
            {
                failure = $"{exception.GetType().Name} pendant l'énumération de Equipment";
                Log.LogError($"[avatar] équipement local : énumération interrompue ({exception.Message}).");
            }

            Log.LogInfo(names.Count == 0
                ? "[avatar] équipement local : rien de porté."
                : $"[avatar] équipement local : {names.Count} pièce(s) : {string.Join(", ", names)}");
            return names;
        }

        /// <summary>Les nœuds que ces règles pilotent, dans l'ordre des gestionnaires ci-dessus.</summary>
        public static IEnumerable<string> GovernedNodes => Governed;

        /// <summary>
        /// État d'un nœud piloté : vrai (actif), faux (masqué), ou <c>null</c> si le nœud n'existe pas.
        /// Le <c>null</c> est distinct du faux à dessein : « masqué » et « introuvable » ne sont pas le
        /// même défaut, et les confondre ferait passer un chemin de travers pour une tenue correcte.
        /// </summary>
        public static bool? NodeActive(GameObject clone, string path)
        {
            var model = FindModelRoot(clone);
            var node = model != null ? model.Find(path) : null;
            return node == null ? (bool?)null : node.gameObject.activeSelf;
        }

        /// <summary>Les nœuds pilotés qui sont actuellement actifs, en nom court — pour lire une tenue.</summary>
        public static IReadOnlyList<string> ActiveGovernedNodes(GameObject clone)
        {
            var model = FindModelRoot(clone);
            var active = new List<string>();

            foreach (var path in Governed)
            {
                var node = model != null ? model.Find(path) : null;
                if (node != null && node.gameObject.activeSelf)
                {
                    active.Add(node.name);
                }
            }

            return active;
        }

        /// <summary>Vrai si le joueur porte une bouteille, quelle qu'elle soit.</summary>
        private static bool TankEquipped(Func<TechType, bool> has) =>
            has(TechType.Tank) || has(TechType.DoubleTank) ||
            has(TechType.HighCapacityTank) || has(TechType.PlasteelTank);

        /// <summary>
        /// La racine sur laquelle les chemins ci-dessus s'appliquent : le parent de `male_geo`.
        ///
        /// Elle est trouvée par le nom de `male_geo` plutôt que par `player_view` : c'est ce segment qui
        /// figure dans les chemins transcrits, donc c'est lui qui doit définir l'origine, et les deux
        /// hypothèses ne peuvent pas diverger en silence.
        /// </summary>
        private static Transform FindModelRoot(GameObject clone)
        {
            foreach (var transform in clone.GetComponentsInChildren<Transform>(true))
            {
                if (transform.name == "male_geo")
                {
                    return transform.parent;
                }
            }

            return null;
        }
    }
}
