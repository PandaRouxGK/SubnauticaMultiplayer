using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Fabrique un corps visible pour représenter un joueur distant.
    ///
    /// Pourquoi une copie du corps local et pas un prefab : Subnautica n'expose aucun prefab de
    /// joueur. `Player` n'a ni propriété ni champ public de modèle (vérifié par `tools/ApiDump`),
    /// et le corps n'existe qu'à l'exécution, construit par le jeu dans la hiérarchie de
    /// `Player.mainObject`. La seule source disponible est donc le corps du joueur local lui-même.
    ///
    /// Ce qui rend ça délicat, et pourquoi le code est si prudent :
    ///
    /// - **Le corps vit sous le GameObject qui porte le composant `Player`.** Copier ce GameObject
    ///   exécuterait l'`Awake` d'un second `Player` — un singleton statique (`Player.main`), donc
    ///   une copie du corps suffit, et une copie du root interdirait tout. D'où la refus catégorique
    ///   si le sous-arbre copié contient `Player`, `Camera` ou `AudioListener` : ces composants
    ///   détourneraient l'autorité, le rendu ou l'audio du client.
    /// - **Un corps de joueur est plein de composants de gameplay** (moteurs, entropie, sons,
    ///   respirations). Sur une copie, ils simuleraient un second joueur : ils sont donc retirés,
    ///   et chaque type retiré est journalisé (rien n'est supprimé en silence).
    /// - **Rien ne garantit qu'un maillage soit rendu** : un calque absent du `cullingMask` de la
    ///   caméra donne un objet parfaitement invisible. C'est vérifié et corrigé explicitement.
    /// </summary>
    internal static class RemoteAvatarFactory
    {
        /// <summary>Source dédiée : les lignes arrivent dans le log de l'instance (coop-client.log).</summary>
        public static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Avatar");

        public const string CloneName = "Coop.RemoteAvatar";

        /// <summary>
        /// Composants conservés sur la copie : de quoi être dessiné, et rien de plus.
        /// `Transform` en fait partie car il ressort de <c>GetComponents</c> comme les autres.
        /// </summary>
        private static bool IsVisual(Type type) =>
            type == typeof(Transform) ||
            typeof(Renderer).IsAssignableFrom(type) ||
            type == typeof(MeshFilter) ||
            type == typeof(Animator);

        /// <summary>
        /// Composants dont la seule présence dans le sous-arbre interdit la copie. Ce ne sont pas des
        /// précautions de style : chacun d'eux, dupliqué, casserait quelque chose de précis
        /// (autorité du joueur, rendu, audio du client).
        /// </summary>
        private static readonly Type[] Forbidden =
        {
            typeof(Player),
            typeof(Camera),
            typeof(AudioListener),
            typeof(PlayerMotor),
            typeof(GroundMotor),
            typeof(LiveMixin),
        };

        /// <summary>
        /// Trouve le plus haut nœud **strictement sous** le joueur qui contient le corps.
        ///
        /// L'ancre est `Player.head` (un `SkinnedMeshRenderer` public, donc stable) : on remonte
        /// depuis lui jusqu'à l'enfant direct du root. Si le jeu cesse de renseigner `head`, on
        /// retombe sur le plus gros maillage squelettique du sous-arbre, avec la même remontée.
        /// </summary>
        public static Transform FindBodyRoot(Transform playerRoot)
        {
            var head = Player.main != null ? Player.main.head : null;
            if (head != null)
            {
                var node = AncestorDirectlyUnder(playerRoot, head.transform);
                if (node != null)
                {
                    return node;
                }
            }

            SkinnedMeshRenderer largest = null;
            foreach (var renderer in playerRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer.sharedMesh == null)
                {
                    continue;
                }

                if (largest == null || renderer.sharedMesh.vertexCount > largest.sharedMesh.vertexCount)
                {
                    largest = renderer;
                }
            }

            return largest != null ? AncestorDirectlyUnder(playerRoot, largest.transform) : null;
        }

        /// <summary>Remonte jusqu'à l'enfant direct de <paramref name="root"/> sur la branche de <paramref name="node"/>.</summary>
        private static Transform AncestorDirectlyUnder(Transform root, Transform node)
        {
            while (node != null && node.parent != null && node.parent != root)
            {
                node = node.parent;
            }

            return node != null && node.parent == root ? node : null;
        }

        /// <summary>Vrai si le sous-arbre peut être copié sans détourner une ressource globale.</summary>
        public static bool IsSafeToClone(Transform candidate, out string reason)
        {
            foreach (var component in candidate.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    // Script manquant : Unity renvoie null ici, ce n'est pas une erreur de notre fait.
                    continue;
                }

                var type = component.GetType();
                foreach (var forbidden in Forbidden)
                {
                    if (forbidden.IsAssignableFrom(type))
                    {
                        reason = $"{type.Name} présent dans le sous-arbre";
                        return false;
                    }
                }
            }

            // Le candidat doit appartenir au joueur : sinon on ne sait plus ce qu'on copie.
            if (candidate.GetComponentInParent<Player>() == null)
            {
                reason = "le candidat n'est pas rattaché au joueur";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Crée la copie du corps à <paramref name="position"/>, tournée vers <paramref name="lookAt"/>.
        /// Renvoie <c>null</c> et <paramref name="failure"/> renseigné si la copie n'est pas possible ;
        /// dans ce cas rien n'a été créé (échec fermé : mieux vaut aucun avatar qu'un client cassé).
        /// </summary>
        public static GameObject TryCreate(Transform playerRoot, Vector3 position, Transform lookAt, out string failure)
        {
            failure = null;

            var candidate = FindBodyRoot(playerRoot);
            if (candidate == null)
            {
                failure = "aucun corps de joueur identifiable sous Player.mainObject";
                return null;
            }

            var skinnedCount = candidate.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            Log.LogInfo($"[avatar] corps candidat : '{Path(candidate)}' — {skinnedCount} SkinnedMeshRenderer");

            if (!IsSafeToClone(candidate, out var reason))
            {
                failure = $"copie refusée : {reason}";
                Log.LogError($"[avatar] copie refusée : {reason}");
                return null;
            }

            var forward = position - playerRoot.position;
            forward.y = 0f;
            var rotation = forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : playerRoot.rotation;

            GameObject clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(candidate.gameObject, position, rotation);
            }
            catch (Exception exception)
            {
                failure = $"Instantiate a levé {exception.GetType().Name}";
                Log.LogError($"[avatar] échec de la copie : {exception}");
                return null;
            }

            clone.name = CloneName;
            StripNonVisualComponents(clone);
            ForceRenderersEnabled(clone);
            FixSkinnedMeshCulling(clone);
            MakeAnimationIgnoreVisibility(clone);

            // Les calques ne sont **pas** touchés ici : le corps du joueur vit sur le calque
            // `Viewmodel`, propre à la vue première personne. Un avatar mondial doit être ailleurs,
            // mais ce choix se mesure (voir AvatarProbeBehaviour) plutôt que de se décider à l'aveugle.
            return clone;
        }

        /// <summary>
        /// Fait tourner l'animation de la copie même quand le moteur ne la « voit » pas.
        ///
        /// `Animator.cullingMode` vaut `CullUpdateTransforms` par défaut : Unity cesse alors de mettre à
        /// jour les os d'un maillage qu'il ne considère pas visible. Sur une copie, ce jugement est faux —
        /// mesuré : 3 renderers sur 25 « visibles » alors que la copie change 10 000 px à l'écran — et le
        /// résultat est un squelette **figé** : 0,0000 m de déplacement moyen des sommets en 0,5 s, et
        /// **201 paramètres d'animation sans le moindre effet** (`move_speed`, `is_underwater`,
        /// `swimming_fast`, `holding_*`...). `AlwaysAnimate` supprime cette dépendance à la visibilité, qui
        /// n'a aucun sens pour un avatar qu'on veut précisément voir bouger.
        /// </summary>
        private static void MakeAnimationIgnoreVisibility(GameObject clone)
        {
            var animators = clone.GetComponentsInChildren<Animator>(true);
            var changed = 0;

            foreach (var animator in animators)
            {
                var previous = animator.cullingMode;
                if (previous == AnimatorCullingMode.AlwaysAnimate)
                {
                    continue;
                }

                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                changed++;
                Log.LogInfo(
                    $"[avatar] animator '{Path(animator.transform)}' : cullingMode {previous} -> AlwaysAnimate " +
                    "(sinon les os restent figés).");
            }

            Log.LogInfo($"[avatar] animation : {changed}/{animators.Length} animator(s) mis en AlwaysAnimate.");
        }

        /// <summary>
        /// Retire de la copie tout ce qui n'est pas visuel.
        ///
        /// **En deux passes, et le compte rendu est vérifié.** Unity refuse de retirer un composant
        /// dont un autre dépend encore (`Can't remove ConditionRules because ArmsController depends
        /// on it`) : une seule passe laissait des composants en place tout en les annonçant retirés.
        /// Le journal est donc établi en relisant la copie après coup — ce qui est écrit correspond
        /// à ce qui reste, pas à ce qu'on a tenté.
        /// </summary>
        private static void StripNonVisualComponents(GameObject clone)
        {
            var before = new List<string>();
            foreach (var component in clone.GetComponentsInChildren<Component>(true))
            {
                if (component != null && !IsVisual(component.GetType()))
                {
                    before.Add(component.GetType().Name);
                }
            }

            for (var pass = 0; pass < 3; pass++)
            {
                var removedThisPass = 0;

                foreach (var component in clone.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || IsVisual(component.GetType()))
                    {
                        continue;
                    }

                    try
                    {
                        UnityEngine.Object.DestroyImmediate(component);
                        removedThisPass++;
                    }
                    catch (Exception exception)
                    {
                        Log.LogWarning($"[avatar] impossible de retirer {component.GetType().Name} : {exception.Message}");
                    }
                }

                if (removedThisPass == 0)
                {
                    break;
                }
            }

            var remaining = new List<string>();
            foreach (var component in clone.GetComponentsInChildren<Component>(true))
            {
                if (component != null && !IsVisual(component.GetType()))
                {
                    remaining.Add(component.GetType().Name);
                }
            }

            Log.LogInfo(before.Count == 0
                ? "[avatar] aucun composant de gameplay à retirer de la copie."
                : $"[avatar] composants de gameplay : {before.Count} détecté(s) -> {before.Count - remaining.Count} retiré(s)" +
                  (remaining.Count == 0 ? ", 0 restant" : $", restants : {string.Join(", ", remaining)}"));
        }

        /// <summary>
        /// Assainit l'état de dessin des maillages squelettiques copiés.
        ///
        /// **Ce correctif n'a rien changé au rendu, et la raison pour laquelle il a été écrit était
        /// fausse** : les maillages qui ne se dessinaient pas appartenaient aux tenues que le joueur ne
        /// porte pas, donc désactivées dans la hiérarchie, pas mal cullées à cause de bornes figées.
        /// Cela reste écrit ici parce que l'état est réellement douteux après une copie — les bornes
        /// annonçaient 4,01 m pour un corps d'1,90 m — et parce que le relevé de la cause réelle est une
        /// leçon : `Renderer.isVisible` et les bornes sont trompeurs sur ces copies, la mesure de pixels
        /// ne l'est pas. Les deux hypothèses suivantes (occlusion dynamique, ordre de non-dessin hérité)
        /// ont été mesurées puis écartées de la même façon.
        /// </summary>
        private static void FixSkinnedMeshCulling(GameObject clone)
        {
            var fixedCount = 0;

            foreach (var renderer in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null)
                {
                    continue;
                }

                renderer.updateWhenOffscreen = true;
                renderer.localBounds = renderer.sharedMesh.bounds;

                // `forceRenderingOff` se copie avec le reste : le jeu peut avoir demandé à ne pas dessiner
                // un morceau du corps (sa tête, par exemple, invisible en vue première personne) et la
                // copie hérite silencieusement de cet ordre. C'est un état qui ne se voit nulle part : le
                // renderer reste actif, avec son maillage, son matériau et des bornes justes.

                // L'occlusion dynamique est un candidat sérieux : elle se décide d'après des données de
                // visibilité cuites pour la scène, et une copie posée à un endroit imprévu peut être
                // déclarée cachée sans que rien ne le signale. Sur un avatar, elle n'apporte rien.
                renderer.allowOcclusionWhenDynamic = false;
                fixedCount++;
            }

            Log.LogInfo(
                $"[avatar] bornes recalculées pour {fixedCount} SkinnedMeshRenderer " +
                "(updateWhenOffscreen + bornes du maillage, au lieu de bornes figées de l'original).");
        }

        private static void ForceRenderersEnabled(GameObject clone)
        {
            var forcedOff = 0;

            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.forceRenderingOff)
                {
                    forcedOff++;
                    Log.LogWarning(
                        $"[avatar] renderer '{Path(renderer.transform)}' était en forceRenderingOff " +
                        "(ordre hérité de l'original) -> dessin réautorisé.");
                    renderer.forceRenderingOff = false;
                }

                if (!renderer.enabled)
                {
                    Log.LogInfo($"[avatar] renderer '{Path(renderer.transform)}' était désactivé -> réactivé.");
                    renderer.enabled = true;
                }

                if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh == null)
                {
                    Log.LogWarning($"[avatar] SkinnedMeshRenderer '{Path(skinned.transform)}' sans mesh : ne rendra rien.");
                }

                if (renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0)
                {
                    Log.LogWarning($"[avatar] renderer '{Path(renderer.transform)}' sans matériau : rendra en magenta ou rien.");
                }
            }

            Log.LogInfo($"[avatar] ordres de non-dessin hérités : {forcedOff} renderer(s) réautorisé(s).");
        }

        /// <summary>
        /// Bascule la copie sur le calque du monde (`Default`).
        ///
        /// Le corps du joueur vit sur `Viewmodel`, le calque que le jeu réserve à la vue première
        /// personne. Un avatar distant est un objet du monde : il doit être occulté par le terrain
        /// comme n'importe quel objet, donc ne pas rester sur ce calque.
        /// </summary>
        public static int MoveRenderersToWorldLayer(GameObject clone)
        {
            var worldLayer = LayerMask.NameToLayer("Default");
            var moved = 0;

            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.gameObject.layer == worldLayer)
                {
                    continue;
                }

                renderer.gameObject.layer = worldLayer;
                moved++;
            }

            return moved;
        }

        /// <summary>Calques réellement portés par la copie, et ce que la caméra en fait.</summary>
        public static string DescribeLayers(GameObject clone, Camera camera)
        {
            var counts = new Dictionary<int, int>();

            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                var layer = renderer.gameObject.layer;
                counts.TryGetValue(layer, out var current);
                counts[layer] = current + 1;
            }

            var builder = new StringBuilder();
            foreach (var pair in counts)
            {
                var name = LayerMask.LayerToName(pair.Key);
                var visible = camera == null ? (bool?)null : (camera.cullingMask & (1 << pair.Key)) != 0;

                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append($"{pair.Key} ({name}) x{pair.Value} " +
                    (visible == null ? "[caméra inconnue]" : visible.Value ? "[vu]" : "[HORS cullingMask]"));
            }

            return builder.Length == 0 ? "(aucun renderer)" : builder.ToString();
        }

        /// <summary>
        /// Quelles branches de maillage du corps sont visibles.
        ///
        /// Le corps du joueur contient **toutes** les tenues à la fois (scaphandre, combinaison
        /// blindée, anti-radiations, palmes, bouteilles...), et c'est le jeu qui n'en laisse voir
        /// qu'une, par `ConditionRules` / `ArmsController` — des composants qu'on retire justement de
        /// la copie parce qu'ils appartiennent au gameplay. Résultat : sur la copie, elles se
        /// superposent. Ce résumé chiffre ce qui reste à faire pour M2.1 au lieu de le laisser
        /// découvrir à l'œil sur une capture.
        /// </summary>
        public static string DescribeSuitBranches(GameObject clone)
        {
            const string marker = "/male_geo/";
            var counts = new Dictionary<string, int>();

            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                var path = Path(renderer.transform);
                var index = path.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                var rest = path.Substring(index + marker.Length);
                var separator = rest.IndexOf('/');
                var branch = separator < 0 ? rest : rest.Substring(0, separator);

                counts.TryGetValue(branch, out var current);
                counts[branch] = current + 1;
            }

            if (counts.Count == 0)
            {
                return "(aucune branche male_geo)";
            }

            var builder = new StringBuilder();
            foreach (var pair in counts)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append($"{pair.Key} x{pair.Value}");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Résumé factuel de ce que la copie donne à voir. Sert de verdict, pas d'impression :
        /// <paramref name="hasVisibleGeometry"/> n'est vrai que si des renderers actifs portent des
        /// triangles **et** que l'ensemble tombe dans le frustum de la caméra.
        /// </summary>
        public static string Describe(GameObject clone, out bool hasVisibleGeometry)
        {
            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            var enabled = 0;
            var vertices = 0;
            var materials = 0;
            var bounds = new Bounds(clone.transform.position, Vector3.zero);
            var first = true;

            foreach (var renderer in renderers)
            {
                if (renderer.enabled)
                {
                    enabled++;
                }

                materials += renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;

                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : null;
                if (mesh == null && renderer.TryGetComponent(out MeshFilter filter))
                {
                    mesh = filter.sharedMesh;
                }

                if (mesh != null)
                {
                    // On compte les **sommets**, pas les triangles : Unity refuse `mesh.triangles` sur
                    // un maillage non lisible (les maillages du jeu le sont), et ne renvoie alors que
                    // des erreurs et un zéro trompeur. `vertexCount` est lisible partout.
                    vertices += mesh.vertexCount;
                }

                if (first)
                {
                    bounds = renderer.bounds;
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            var camera = Camera.main;
            var inFrustum = false;
            if (camera != null)
            {
                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                inFrustum = GeometryUtility.TestPlanesAABB(planes, bounds);
            }

            var animator = clone.GetComponentInChildren<Animator>();
            var animatorState = animator == null
                ? "aucun"
                : (animator.runtimeAnimatorController == null ? "présent mais sans controller" : animator.runtimeAnimatorController.name);

            hasVisibleGeometry = enabled > 0 && vertices > 0 && inFrustum;

            return
                $"renderers={enabled}/{renderers.Length} actifs, sommets={vertices}, matériaux={materials}, " +
                $"taille={bounds.size.ToString("F2")}, dansFrustum={inFrustum}, animator={animatorState}";
        }

        /// <summary>Chemin lisible d'un nœud dans la hiérarchie (utile dans un log).</summary>
        public static string Path(Transform node)
        {
            var builder = new StringBuilder(node.name);
            var current = node.parent;

            while (current != null)
            {
                builder.Insert(0, current.name + "/");
                current = current.parent;
            }

            return builder.ToString();
        }
    }
}
