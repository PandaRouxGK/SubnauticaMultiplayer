using System;
using System.Collections.Generic;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Retrait des composants non visuels d'une copie d'objet, **dépendants d'abord**.
    ///
    /// Le sujet est l'ordre, pas la liste. Unity refuse de retirer un composant qu'un autre composant du même
    /// objet déclare exiger (`[RequireComponent]`), et il refuse en **écrivant une erreur** dans le journal :
    /// `Can't remove ConditionRules (Script) because ArmsController (Script) depends on it`. Retirer dans
    /// l'ordre du tableau rendu par `GetComponentsInChildren` revient donc à tirer au sort : quand le
    /// composant requis tombe avant celui qui l'exige, Unity l'écrit et le laisse en place — il fallait alors
    /// un second tour pour finir le travail, et l'erreur restait au journal (§24, §26).
    ///
    /// On trie donc les candidats pour que **chaque dépendant parte avant ce dont il dépend**, puis on retire
    /// du premier au dernier. Les types exigés sont **lus**, pas devinés : l'IL de `UnityEngine.RequireComponent`
    /// (`UnityEngine.CoreModule`) montre trois champs **publics**, `m_Type0`, `m_Type1` et `m_Type2`. Un cycle
    /// (`A` exige `B`, `B` exige `A`) est indéracinable par Unity : la visite le casse, et ce qui survit est
    /// **annoncé** au lieu d'être compté comme retiré.
    /// </summary>
    internal static class RemoteAvatarComponents
    {
        /// <summary>Bilan de retrait, établi par **relecture** de la copie après coup.</summary>
        public sealed class Report
        {
            /// <summary>Composants non visuels trouvés avant retrait.</summary>
            public int Detected;

            /// <summary>Composants réellement partis (détectés moins survivants).</summary>
            public int Removed;

            /// <summary>Composants que Unity a laissés, nommés pour le journal.</summary>
            public readonly List<string> Remaining = new List<string>();
        }

        /// <summary>
        /// Retire tout ce qui n'est pas visuel.
        ///
        /// <paramref name="keepAnimator"/> : la copie du corps **doit** garder son `Animator`, sans quoi les os
        /// restent figés ; un modèle d'outil instancié, lui, n'en a pas besoin.
        /// </summary>
        public static Report Strip(GameObject root, bool keepAnimator)
        {
            var report = new Report();
            var candidates = new List<Component>();

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || IsVisual(component.GetType(), keepAnimator))
                {
                    continue;
                }

                report.Detected++;
                candidates.Add(component);
            }

            foreach (var component in OrderDependentsFirst(candidates))
            {
                if (component == null)
                {
                    continue;
                }

                try
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
                catch (Exception)
                {
                    // Un refus d'Unity n'est pas une raison d'arrêter : les autres candidats partent quand
                    // même, et la relecture ci-dessous nommera celui qui est resté.
                }
            }

            // `DestroyImmediate` peut aussi échouer sans lever (Unity écrit alors son erreur nativement) : le
            // compte est donc établi en relisant les références, pas en comptant les appels.
            foreach (var component in candidates)
            {
                if (component != null)
                {
                    report.Remaining.Add(component.GetType().Name);
                }
            }

            report.Removed = report.Detected - report.Remaining.Count;
            return report;
        }

        /// <summary>
        /// Composants conservés : de quoi être dessiné, et rien de plus. `Transform` en fait partie car il
        /// ressort de `GetComponents` comme les autres.
        /// </summary>
        private static bool IsVisual(Type type, bool keepAnimator) =>
            type == typeof(Transform) ||
            typeof(Renderer).IsAssignableFrom(type) ||
            type == typeof(MeshFilter) ||
            (keepAnimator && type == typeof(Animator));

        /// <summary>
        /// Ordre de retrait : chaque dépendant avant ce dont il dépend. L'arête vient de `[RequireComponent]`,
        /// et la marque est posée dès l'entrée de la visite — c'est ce qui casse un cycle, de toute façon
        /// inséparable par Unity.
        /// </summary>
        private static List<Component> OrderDependentsFirst(List<Component> candidates)
        {
            var ordered = new List<Component>(candidates.Count);
            var visited = new HashSet<Component>();

            foreach (var candidate in candidates)
            {
                Visit(candidate, candidates, visited, ordered);
            }

            return ordered;
        }

        private static void Visit(
            Component candidate,
            List<Component> candidates,
            HashSet<Component> visited,
            List<Component> ordered)
        {
            if (candidate == null || !visited.Add(candidate))
            {
                return;
            }

            var required = candidate.GetType();
            var host = candidate.gameObject;

            foreach (var other in candidates)
            {
                // Unity ne regarde que le même objet : un composant d'un autre nœud ne bloque rien.
                if (other == null || other == candidate || other.gameObject != host)
                {
                    continue;
                }

                if (Requires(other.GetType(), required))
                {
                    Visit(other, candidates, visited, ordered);
                }
            }

            ordered.Add(candidate);
        }

        /// <summary>Vrai si <paramref name="type"/> déclare exiger <paramref name="required"/>.</summary>
        private static bool Requires(Type type, Type required)
        {
            foreach (var attribute in type.GetCustomAttributes(typeof(RequireComponent), true))
            {
                if (attribute is RequireComponent require)
                {
                    if (require.m_Type0 == required || require.m_Type1 == required || require.m_Type2 == required)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
