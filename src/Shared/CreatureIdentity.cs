using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Coop.Model;
using UnityEngine;

namespace Coop.Shared
{
    /// <summary>
    /// Identité d'une créature, **calculée de la même façon par le serveur et par le client**, et état
    /// de son IA.
    ///
    /// Pourquoi une clé partagée : les deux instances chargent la même sauvegarde, donc elles ont
    /// chacune leur propre exemplaire de la même créature. Pour que le client puisse dire « cette
    /// créature-là, c'est celle que le serveur simule », il faut un identifiant qui ne dépende ni du
    /// pointeur, ni de l'ordre de création, ni du hasard du chargement.
    ///
    /// Deux mécanismes, dans cet ordre, et **le journal dit lequel a servi** :
    ///
    /// 1. la position d'apparition — `Creature.leashPosition` (champ **public**), relevé dans l'IL :
    ///    `Creature.InitializeOnce` fait exactement `leashPosition = transform.position`, une seule
    ///    fois, à l'initialisation. C'est donc la position de spawn, et non la position courante (qui,
    ///    elle, diverge dès que la créature nage). C'est le mécanisme **par défaut**, parce que c'est
    ///    le seul dont la mesure ait montré qu'il donne la même clé des deux côtés.
    /// 2. `UniqueIdentifier.Id` — l'identifiant que le jeu pose sur les objets du monde. Il ne passe
    ///    qu'en repli : mesuré, il **diffère** entre deux instances pour les créatures créées à
    ///    l'exécution, qui sont la majorité (les bancs de poissons).
    ///
    /// Si aucun des deux n'est disponible, la créature n'a **pas** de clé : elle n'est pas diffusée
    /// et elle est nommée dans le journal. Une clé inventée ferait correspondre deux créatures
    /// différentes, ce qui est bien pire qu'une créature non synchronisée.
    ///
    /// Le même fichier porte l'état de l'IA, parce que c'est la contrepartie de la clé : le serveur
    /// veut savoir si sa créature simule, le client veut pouvoir l'arrêter chez lui. Les deux
    /// questions se posent sur le même objet, et les séparer dans deux fichiers ferait diverger la
    /// lecture de l'index.
    ///
    /// Compilé dans les deux plugins via un &lt;Compile Include&gt; partagé : c'est la seule façon
    /// d'être sûr que les deux côtés calculent la même clé, plutôt que de compter sur une copie
    /// fidèle.
    /// </summary>
    internal static class CreatureIdentity
    {
        /// <summary>
        /// Mécanisme « position d'apparition » — **le mécanisme par défaut**, parce qu'il est le seul
        /// dont on ait mesuré qu'il est identique d'une instance à l'autre. Voir
        /// <see cref="KeyOf"/>.
        /// </summary>
        public const string MechanismSpawnPosition = "position d'apparition";

        /// <summary>
        /// Mécanisme « identifiant du jeu » — conservé comme repli, mais il n'est **pas** stable
        /// entre deux instances (mesuré), donc il ne passe qu'en dernier.
        /// </summary>
        public const string MechanismUniqueId = "identifiant (repli)";

        /// <summary>Aucun des deux : la créature n'est pas diffusée.</summary>
        public const string MechanismNone = "aucune";

        /// <summary>Précision (m) à laquelle la position d'apparition est quantifiée pour la clé.</summary>
        private const float SpawnQuantizationMeters = 0.05f;

        private static readonly Dictionary<Type, FieldInfo> LeashFields = new Dictionary<Type, FieldInfo>();

        private static readonly Dictionary<Type, MethodInfo> AllowUpdatesMethods =
            new Dictionary<Type, MethodInfo>();

        /// <summary>
        /// Clé stable de la créature, ou 0 si elle n'en a pas. <paramref name="mechanism"/> dit lequel
        /// des deux mécanismes a répondu.
        ///
        /// **L'ordre a été inversé par la mesure, pas par goût.** La première version préférait
        /// l'identifiant du jeu (`UniqueIdentifier.Id`), sur l'idée qu'un identifiant du monde est
        /// stable par construction. Le premier run en conditions réelles l'a démenti : 34 808 états de
        /// créature envoyés par le serveur, **0 apparié** côté client — et sur les 100 créatures que le
        /// client voyait, 26 n'avaient même pas d'identifiant. Les poissons du jeu sont pour une bonne
        /// part créés à l'exécution (les bancs), et leur identifiant est alors tiré sur chaque instance :
        /// le même poisson porte deux identifiants différents chez le serveur et chez le client.
        ///
        /// La position d'apparition, elle, ne peut pas diverger : les deux instances chargent la même
        /// sauvegarde et le jeu place chaque créature au même endroit dans les deux. C'est donc elle qui
        /// porte désormais la clé, et l'identifiant ne reste qu'en repli — utile pour une créature qui
        /// viendrait d'un objet du monde sérialisé, et signalé comme tel dans le journal.
        /// </summary>
        public static long KeyOf(Creature creature, out string mechanism)
        {
            mechanism = MechanismNone;
            if (creature == null)
            {
                return 0L;
            }

            if (TryGetSpawnPosition(creature, out var spawn))
            {
                mechanism = MechanismSpawnPosition;
                // Le nom du prefab fait partie de la matière : deux créatures de la même espèce ne
                // partagent pas la même position d'apparition, mais l'inclure rend une collision de
                // position entre deux espèces impossible à confondre avec une vraie correspondance.
                var name = creature.gameObject != null ? creature.gameObject.name : string.Empty;
                return StableHash.Of(
                    "spawn|" + name + "|" +
                    Quantize(spawn.x) + "|" + Quantize(spawn.y) + "|" + Quantize(spawn.z));
            }

            if (TryGetUniqueId(creature, out var uniqueId))
            {
                mechanism = MechanismUniqueId;
                return StableHash.Of("uid|" + uniqueId);
            }

            return 0L;
        }

        /// <summary>Identifiant du monde, s'il existe et n'est pas vide.</summary>
        public static bool TryGetUniqueId(Creature creature, out string id)
        {
            id = null;

            // `GetComponentInParent` et non `GetComponent` : l'identifiant est posé sur la racine de
            // l'objet du monde, alors que le composant `Creature` peut vivre sur un enfant.
            var identifier = creature.GetComponentInParent<UniqueIdentifier>();
            if (identifier == null)
            {
                return false;
            }

            id = identifier.Id;
            return !string.IsNullOrEmpty(id);
        }

        /// <summary>
        /// Position d'apparition, lue dans `Creature.leashPosition` : c'est la valeur que
        /// `Creature.InitializeOnce` a copiée de `transform.position` au moment de l'initialisation.
        /// Le champ est mis en cache par type : la réflexion n'est faite qu'une fois.
        ///
        /// **Le champ est public**, et l'oublier a coûté deux exécutions. Le premier relevé a cherché
        /// `leashPosition` avec `BindingFlags.Instance | BindingFlags.NonPublic` — un choix guidé par
        /// l'idée que seuls les champs internes du jeu ont besoin de réflexion — et `GetField` a donc
        /// renvoyé `null` pour **toutes** les créatures, en silence, ce qui faisait basculer chaque clé
        /// sur le repli par identifiant (celui qui ne correspond pas d'une instance à l'autre). Le
        /// doute a été levé hors du jeu, par deux mesures de trente secondes sur la vraie assembly :
        /// `ApiDump Assembly-CSharp.dll Creature` affiche `public Vector3 leashPosition`, et une sonde
        /// de réflexion jetable a montré que `GetFields(Instance|NonPublic|Public)` le liste pendant que
        /// `GetField(Instance|NonPublic)` le manque. Les drapeaux incluent donc `Public`, et la marche
        /// dans la hiérarchie reste là par prudence (un champ déplacé dans une classe de base ne doit
        /// pas casser la clé sans le dire).
        /// </summary>
        public static bool TryGetSpawnPosition(Creature creature, out Vector3 spawn)
        {
            spawn = Vector3.zero;

            var type = creature.GetType();
            if (!LeashFields.TryGetValue(type, out var field))
            {
                // `Creature` déclare le champ, mais l'instance est d'un type dérivé (`Peeper`,
                // `Boomerang`...) : on cherche donc aussi dans les classes de base.
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                field = null;
                for (var current = type; current != null && field == null; current = current.BaseType)
                {
                    field = current.GetField("leashPosition", flags);
                }

                LeashFields[type] = field;
            }

            if (field == null)
            {
                return false;
            }

            try
            {
                spawn = (Vector3)field.GetValue(creature);
                return true;
            }
            catch (Exception)
            {
                // Un champ illisible n'est pas une raison de tomber : la créature sera simplement
                // sans clé, et le journal le dira.
                return false;
            }
        }

        /// <summary>
        /// Indice de planification de la créature (`IScheduledUpdateBehaviour.scheduledUpdateIndex`).
        ///
        /// C'est **la** façon de savoir si une créature simule, et elle est relevée dans l'IL plutôt
        /// que supposée : `ScheduledUpdateBehaviourSet.Add` pose `index = count + 1` (donc
        /// strictement positif) et `Remove` refuse de retirer un index déjà `&lt;= 0`. Un index
        /// supérieur à zéro veut dire « planifiée, donc l'IA tourne » ; zéro veut dire « retirée,
        /// l'IA ne tourne pas ».
        ///
        /// Renvoie <c>int.MinValue</c> si la valeur n'est pas lisible — distinct de 0, qui est une
        /// information : « l'IA est arrêtée ».
        /// </summary>
        public static int ScheduledUpdateIndex(Creature creature)
        {
            if (creature == null)
            {
                return int.MinValue;
            }

            try
            {
                var scheduled = creature as IScheduledUpdateBehaviour;
                return scheduled != null ? scheduled.scheduledUpdateIndex : int.MinValue;
            }
            catch (Exception)
            {
                return int.MinValue;
            }
        }

        /// <summary>Vrai si la créature est planifiée, c'est-à-dire si son IA tourne.</summary>
        public static bool IsAiScheduled(Creature creature) => ScheduledUpdateIndex(creature) > 0;

        /// <summary>
        /// Allume ou éteint l'IA d'une créature en passant par **le chemin du jeu** :
        /// `Creature.AllowCreatureUpdates(bool)` (privée), qui se contente de
        /// `UpdateSchedulerUtils.Register`/`Deregister`.
        ///
        /// Pourquoi la méthode du jeu plutôt qu'un `enabled = false` ou la désactivation d'un
        /// comportement précis : elle existe déjà, elle est générique (les quatre appelants du jeu —
        /// `BirdBehaviour`, `CrabSnake`, `SeaTreader`, `GhostLeviatanVoid` — s'en servent exactement
        /// pour mettre une créature en pause), et surtout elle ne touche **que** la planification de
        /// l'IA : l'`Animator` continue de jouer, le maillage reste visible, les collisions restent.
        /// Une créature qu'on éteint par `enabled = false` disparaîtrait de la vue.
        ///
        /// Renvoie le nouvel indice de planification, ou <c>int.MinValue</c> si l'appel n'a pas pu
        /// être fait — le journal doit pouvoir dire « je n'ai pas pu », pas « c'est fait ».
        /// </summary>
        public static int SetAiScheduled(Creature creature, bool scheduled)
        {
            if (creature == null)
            {
                return int.MinValue;
            }

            var type = creature.GetType();
            if (!AllowUpdatesMethods.TryGetValue(type, out var method))
            {
                method = type.GetMethod(
                    "AllowCreatureUpdates",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                AllowUpdatesMethods[type] = method;
            }

            if (method == null)
            {
                return int.MinValue;
            }

            try
            {
                method.Invoke(creature, new object[] { scheduled });
            }
            catch (Exception)
            {
                return int.MinValue;
            }

            return ScheduledUpdateIndex(creature);
        }

        /// <summary>Résumé d'une créature, pour un journal : ce qu'il faut pour la reconnaître.</summary>
        public static string Describe(Creature creature)
        {
            if (creature == null)
            {
                return "(créature absente)";
            }

            var position = creature.transform != null ? creature.transform.position : Vector3.zero;
            var index = ScheduledUpdateIndex(creature);
            var indexText = index == int.MinValue ? "illisible" : index.ToString(CultureInfo.InvariantCulture);
            var key = KeyOf(creature, out var mechanism);

            return string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' pos=({1:F1}, {2:F1}, {3:F1}) indexIA={4} ({5}) clé={6:x16}",
                creature.gameObject != null ? creature.gameObject.name : "?",
                position.x,
                position.y,
                position.z,
                indexText,
                mechanism,
                key);
        }

        private static string Quantize(float value)
        {
            var quantum = (long)Math.Round(value / SpawnQuantizationMeters);
            return quantum.ToString(CultureInfo.InvariantCulture);
        }
    }
}
