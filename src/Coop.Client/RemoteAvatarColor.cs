using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Logging;
using Coop.Model;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Donne à l'avatar distant une couleur propre à son joueur, pour qu'on puisse les distinguer.
    ///
    /// Le problème est concret : deux joueurs en scaphandre de base sont **identiques**. Subnautica
    /// n'a pas de couleur de joueur (le sujet a été mesuré : aucun champ, aucune propriété de couleur
    /// dans `Player`, et le modèle du joueur ne porte que ses textures) — c'est une invention de
    /// Nitrox, qui la réalise en réécrivant les pixels de la texture de la combinaison
    /// (`ColorSwap/DiveSuitColorSwapManager` : `HsvSwapper` avec `SetHueRange(5f, 45f)`, appliqué aux
    /// pixels des deux matériaux du `SkinnedMeshRenderer` de la combinaison).
    ///
    /// Ce qui est repris de Nitrox : **où** la couleur va (la combinaison de plongée, `diveSuit` —
    /// c'est la seule tenue que tout le monde porte par défaut, et Nitrox ne colore pas les autres),
    /// et l'intention (une teinte franchement distincte, pas un pastel).
    ///
    /// Ce qui n'est **pas** repris, et pourquoi : Nitrox clone la texture, la décode en tableau de
    /// pixels, parcourt chaque pixel et déplace sa teinte, puis reconstruit la texture — une
    /// opération coûteuse, asynchrone, qu'il fait une fois par joueur parce qu'il dessine lui-même
    /// ses avatars. Ici la copie est **la nôtre** : il suffit de teinter son matériau
    /// (`material.color`), ce qui multiplie la texture — un gris clair devient la couleur voulue —
    /// sans toucher un seul pixel. Le résultat est visiblement différent d'une teinte de base, et
    /// surtout il est instantané, réversible, et sans allocation de texture.
    ///
    /// Comme pour l'équipement, rien n'est supposé sur le nom de la propriété de couleur du shader :
    /// on essaie celles qui existent réellement sur le matériau, on **dit** laquelle a servi, et
    /// s'il n'y en a aucune on le signale au lieu de prétendre avoir colorié quelque chose.
    /// </summary>
    internal static class RemoteAvatarColor
    {
        private static readonly ManualLogSource Log = RemoteAvatarFactory.Log;

        /// <summary>
        /// Nœud de la combinaison de plongée. C'est le nom d'objet du jeu, recopié de Nitrox
        /// (`PlayerEquipmentConstants.DIVE_SUIT_GAME_OBJECT_NAME`), et le même chemin que celui déjà
        /// utilisé par les règles de tenue.
        /// </summary>
        public const string DiveSuitRoot = "male_geo/diveSuit";

        /// <summary>
        /// Propriétés de couleur essayées, dans l'ordre. Ce sont les noms réellement rencontrés sur
        /// les shaders de Subnautica : `_Color` sur les shaders de personnage, `_BaseColor` sur
        /// certains shaders URP-like, `_TintColor` sur les shaders à teinte additive.
        /// </summary>
        private static readonly string[] ColorProperties = { "_Color", "_BaseColor", "_TintColor" };

        /// <summary>
        /// Applique la couleur <paramref name="colorIndex"/> de la palette
        /// (<see cref="Protocol.PlayerColorAt"/>) à la copie.
        ///
        /// Ne touche qu'aux matériaux **de la copie** : `Renderer.material` instancie le matériau
        /// partagé, donc le joueur local garde le sien intact. C'est le même raisonnement que pour
        /// l'objet tenu (§24) : ce n'est pas le nôtre, on ne le modifie pas.
        /// </summary>
        /// <returns>Un compte rendu d'une ligne, journalisable et comparable entre deux exécutions.</returns>
        public static string Apply(GameObject clone, byte colorIndex)
        {
            Protocol.PlayerColorAt(colorIndex, out var r, out var g, out var b);
            var color = new Color(r / 255f, g / 255f, b / 255f, 1f);

            var model = RemoteAvatarEquipment.FindModelRoot(clone);
            if (model == null)
            {
                Log.LogWarning("[avatar] couleur non appliquée : racine du modèle introuvable (parent de `male_geo`).");
                return "racine du modèle introuvable";
            }

            var suit = model.Find(DiveSuitRoot);
            if (suit == null)
            {
                Log.LogWarning($"[avatar] couleur non appliquée : nœud '{DiveSuitRoot}' introuvable dans le modèle.");
                return $"nœud '{DiveSuitRoot}' introuvable";
            }

            var renderers = suit.GetComponentsInChildren<Renderer>(true);
            var tinted = 0;
            var withoutColorProperty = new List<string>();
            var usedProperties = new List<string>();
            var shaders = new List<string>();

            foreach (var renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                // `material` et non `sharedMaterial` : c'est ce qui isole la copie du joueur local.
                var material = renderer.material;
                if (material == null)
                {
                    continue;
                }

                var property = FindColorProperty(material);
                if (property == null)
                {
                    withoutColorProperty.Add(renderer.name);
                    continue;
                }

                material.SetColor(property, color);
                tinted++;

                if (!usedProperties.Contains(property))
                {
                    usedProperties.Add(property);
                }

                var shaderName = material.shader != null ? material.shader.name : "sans shader";
                var label = $"{shaderName}/({property})";
                if (!shaders.Contains(label))
                {
                    shaders.Add(label);
                }
            }

            var report = new StringBuilder();
            report.Append(
                $"couleur {colorIndex} = rgb({r},{g},{b}) -> {tinted}/{renderers.Length} renderer(s) teinté(s) " +
                $"de '{DiveSuitRoot}'");
            if (shaders.Count > 0)
            {
                report.Append($", propriétés utilisées : {string.Join(", ", shaders)}");
            }

            if (withoutColorProperty.Count > 0)
            {
                // Jamais silencieux : un matériau sans propriété de couleur est un fait à voir, pas
                // un détail — c'est exactement ce qui ferait croire à une couleur appliquée alors
                // que rien ne change à l'écran.
                report.Append(
                    $", {withoutColorProperty.Count} renderer(s) SANS propriété de couleur " +
                    $"[{string.Join(", ", withoutColorProperty)}]");
                Log.LogWarning(
                    $"[avatar] couleur : {withoutColorProperty.Count} renderer(s) de la combinaison sans " +
                    $"propriété de couleur parmi [{string.Join(", ", ColorProperties)}] : " +
                    $"[{string.Join(", ", withoutColorProperty)}] — ils resteront à leur couleur d'origine.");
            }

            Log.LogInfo($"[avatar] couleur appliquée : {report}");
            return report.ToString();
        }

        /// <summary>
        /// Couleur actuellement lue **sur les matériaux de la copie**, en `Color` Unity (composantes
        /// 0–1, comme tout matériau ; `Describe` et les écarts les repassent en 0–255) : c'est la
        /// seule preuve qu'une couleur est bien posée, plutôt que d'avoir appelé un setter. Renvoie
        /// <c>null</c> si rien n'est lisible.
        /// </summary>
        public static Color? AverageTint(GameObject clone)
        {
            var model = RemoteAvatarEquipment.FindModelRoot(clone);
            var suit = model != null ? model.Find(DiveSuitRoot) : null;
            if (suit == null)
            {
                return null;
            }

            var total = Vector3.zero;
            var count = 0;

            foreach (var renderer in suit.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                {
                    continue;
                }

                var material = renderer.material;
                var property = material != null ? FindColorProperty(material) : null;
                if (property == null)
                {
                    continue;
                }

                var current = material.GetColor(property);
                total += new Vector3(current.r, current.g, current.b);
                count++;
            }

            if (count == 0)
            {
                return null;
            }

            var average = total / count;
            return new Color(average.x, average.y, average.z, 1f);
        }

        /// <summary>Résumé lisible de la couleur lue, pour un journal ou une sonde.</summary>
        public static string Describe(GameObject clone)
        {
            var tint = AverageTint(clone);
            if (tint == null)
            {
                return "couleur illisible (aucun matériau de combinaison coloriable)";
            }

            var color = tint.Value;
            var r = Mathf.RoundToInt(color.r * 255f);
            var g = Mathf.RoundToInt(color.g * 255f);
            var b = Mathf.RoundToInt(color.b * 255f);
            return string.Format(
                CultureInfo.InvariantCulture,
                "moyenneRVG=({0},{1},{2})", r, g, b);
        }

        /// <summary>
        /// Première propriété de couleur réellement présente sur le matériau. Renvoie <c>null</c> si
        /// aucune des candidates n'existe : c'est une information, pas une erreur à avaler.
        /// </summary>
        private static string FindColorProperty(Material material)
        {
            foreach (var property in ColorProperties)
            {
                if (material.HasProperty(property))
                {
                    return property;
                }
            }

            return null;
        }
    }
}
