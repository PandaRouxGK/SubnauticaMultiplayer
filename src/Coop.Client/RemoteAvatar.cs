using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

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
            Log.LogInfo(
                $"[avatar] corps candidat : '{Path(candidate)}' — {skinnedCount} SkinnedMeshRenderer, " +
                $"localPosition={candidate.localPosition.ToString("F3")}, localEuler={candidate.localEulerAngles.ToString("F1")}, " +
                $"deltaWorld={(candidate.position - playerRoot.position).ToString("F3")}");

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
            AllowRenderingInsteadOfShadowsOnly(clone);

            // La copie hérite de la tenue **du joueur local** : on la remet d'abord dans l'état d'un
            // joueur sans équipement, puis c'est le réseau qui dira ce que porte le joueur distant.
            // Sans cette remise à zéro, deux joueurs en tenues différentes se ressembleraient.
            RemoteAvatarEquipment.HideAttachedItems(clone);
            RemoteAvatarEquipment.Apply(clone, Array.Empty<TechType>());

            // Les calques ne sont **pas** touchés ici : le corps du joueur vit sur le calque
            // `Viewmodel`, propre à la vue première personne. Un avatar mondial doit être ailleurs,
            // mais ce choix se mesure (voir AvatarProbeBehaviour) plutôt que de se décider à l'aveugle.
            return clone;
        }

        /// <summary>
        /// Calcule la pose mondiale exacte que doit prendre la racine du corps copié (<c>Player/body</c>)
        /// pour un joueur dont le <c>Player.mainObject</c> est à <paramref name="networkRootPosition"/>
        /// et <paramref name="networkRootRotation"/>.
        ///
        /// Sur le joueur local, <c>Player/body</c> est un enfant de <c>Player.mainObject</c> et possède
        /// son propre décalage local (<c>localPosition</c>) par rapport au centre de la capsule physique.
        /// Appliquer directement <c>Player.mainObject.transform.position</c> à
        /// <c>clone.transform.position</c> supprimait ce décalage local et faisait flotter ou s'enfoncer
        /// l'avatar par rapport aux pieds du joueur local.
        ///
        /// En revanche, la **rotation locale du corps n'est pas réappliquée** : elle porte le lacet de la
        /// vue du joueur local, et la multiplier par la rotation réseau réinjectait ce lacet dans tous les
        /// avatars distants (constaté en jeu : les corps tournaient avec la caméra du receveur). La
        /// rotation réseau est désormais le lacet du regard de l'émetteur (`Shared/ViewDirection.cs`),
        /// donc elle s'applique telle quelle.
        /// </summary>
        public static void ComputeBodyWorldPose(
            Transform localPlayerRoot,
            Vector3 networkRootPosition,
            Quaternion networkRootRotation,
            out Vector3 bodyWorldPosition,
            out Quaternion bodyWorldRotation,
            out Vector3 bodyLocalPosition)
        {
            bodyLocalPosition = Vector3.zero;
            if (localPlayerRoot != null)
            {
                var candidate = FindBodyRoot(localPlayerRoot);
                if (candidate != null)
                {
                    bodyLocalPosition = candidate.localPosition;
                    bodyWorldPosition = networkRootPosition + networkRootRotation * candidate.localPosition;
                    bodyWorldRotation = networkRootRotation;
                    return;
                }
            }

            bodyWorldPosition = networkRootPosition;
            bodyWorldRotation = networkRootRotation;
        }

        /// <summary>
        /// Mesure en coordonnées mondiales la hauteur minimale (pieds) et maximale (sommet de la tête)
        /// des sommets cuits (<c>BakeMesh</c>) des <see cref="SkinnedMeshRenderer"/> actifs d'une racine.
        /// Permet de comparer directement la hauteur des pieds de l'avatar distant à celle du joueur local.
        /// </summary>
        public static bool TryMeasureWorldVerticalSpan(Transform root, out float minY, out float maxY)
        {
            minY = float.MaxValue;
            maxY = float.MinValue;
            if (root == null)
            {
                return false;
            }

            var found = false;
            var mesh = new Mesh();
            try
            {
                foreach (var skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (skinned == null || !skinned.enabled || !skinned.gameObject.activeInHierarchy || skinned.sharedMesh == null)
                    {
                        continue;
                    }

                    // Ignorer les maillages très petits (yeux, accessoires) pour mesurer le corps/pieds.
                    if (skinned.sharedMesh.vertexCount < 200)
                    {
                        continue;
                    }

                    skinned.BakeMesh(mesh);
                    var verts = mesh.vertices;
                    var tr = skinned.transform;
                    for (var i = 0; i < verts.Length; i++)
                    {
                        var wy = tr.TransformPoint(verts[i]).y;
                        if (wy < minY)
                        {
                            minY = wy;
                        }

                        if (wy > maxY)
                        {
                            maxY = wy;
                        }

                        found = true;
                    }
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                UnityEngine.Object.Destroy(mesh);
            }

            return found;
        }

        /// <summary>
        /// Applique l'équipement d'un joueur distant à sa copie — le point d'entrée que le réseau
        /// appellera quand l'équipement d'un joueur changera.
        ///
        /// Prend des **noms** de `TechType` : c'est la forme la plus stable pour transporter une pièce
        /// d'équipement (une valeur numérique d'énumération peut bouger d'une version du jeu à l'autre),
        /// et les noms non reconnus sont renvoyés au lieu d'être avalés.
        /// </summary>
        public static IReadOnlyList<string> ApplyEquipment(GameObject clone, IEnumerable<string> techTypeNames)
        {
            EnsureCloneBelongsToUs(clone);
            return RemoteAvatarEquipment.ApplyNames(clone, techTypeNames);
        }

        /// <summary>
        /// Refuse d'agir sur autre chose que nos copies.
        ///
        /// Ces méthodes sont publiques et prendront des objets venus de l'extérieur : les appliquer au
        /// corps du joueur local désactiverait ses propres maillages, et il disparaîtrait de sa propre
        /// vue. Un garde-fou qui nomme l'objet fautif vaut mieux qu'un commentaire qui demande de faire
        /// attention.
        /// </summary>
        private static void EnsureCloneBelongsToUs(GameObject clone)
        {
            if (clone == null)
            {
                throw new ArgumentNullException(nameof(clone));
            }

            if (!clone.name.StartsWith(CloneName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"'{Path(clone.transform)}' n'est pas une copie d'avatar ('{CloneName}') : la modifier " +
                    "désactiverait les maillages du joueur local.", nameof(clone));
            }
        }

        /// <summary>
        /// Redonne un rendu visible aux renderers que le jeu a passés en `ShadowsOnly`.
        ///
        /// **C'est la cause de la tête manquante**, et elle est plus vicieuse que les sept hypothèses
        /// écartées avant elle : `ShadowCastingMode.ShadowsOnly` signifie « projette une ombre, mais n'est
        /// pas dessiné par les caméras ». Le renderer reste donc actif, activé, avec un maillage valide,
        /// des bornes justes, des os présents, un matériau opaque, aucun `forceRenderingOff` — et zéro
        /// pixel. Rien de ce qu'on peut vérifier sur lui ne dit qu'il est masqué.
        ///
        /// Le jeu s'en sert pour cacher la tête du joueur en vue première personne, et Nitrox le contourne
        /// de façon explicite (`NitroxClient/GameLogic/LocalPlayer.cs`, ligne 151, commit `7815b4e`,
        /// commenté « Cheap fix for showing head ») : il passe `Player.head.shadowCastingMode` à `On`, copie le
        /// corps, puis remet `ShadowsOnly` sur l'original. On fait ici l'équivalent **sur la copie
        /// seulement**, sans toucher au joueur local, et on journalise chaque renderer concerné : c'est la
        /// différence entre réparer et espérer.
        /// </summary>
        private static void AllowRenderingInsteadOfShadowsOnly(GameObject clone)
        {
            var restored = 0;

            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
                {
                    continue;
                }

                renderer.shadowCastingMode = ShadowCastingMode.On;
                restored++;
                Log.LogInfo(
                    $"[avatar] renderer '{Path(renderer.transform)}' était en ShadowsOnly (projette une ombre, " +
                    "jamais dessiné) -> rendu réactivé.");
            }

            Log.LogInfo($"[avatar] rendu restauré : {restored} renderer(s) sortis de ShadowsOnly.");
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
        /// Retire de la copie tout ce qui n'est pas visuel, **dépendants d'abord**.
        ///
        /// L'ordre vient de `[RequireComponent]` : c'est lui qui fait disparaître l'erreur native
        /// `Can't remove ConditionRules because ArmsController depends on it`, qu'Unity écrivait quand un
        /// composant requis tombait avant celui qui l'exige (voir <see cref="RemoteAvatarComponents"/> et le
        /// relevé du §26). Le corps copié garde son `Animator` — sans lui les os restent figés.
        ///
        /// Le compte rendu est **vérifié** : il est établi en relisant la copie après coup, donc ce qui est
        /// écrit correspond à ce qui reste, pas à ce qu'on a tenté.
        /// </summary>
        private static void StripNonVisualComponents(GameObject clone)
        {
            var report = RemoteAvatarComponents.Strip(clone, keepAnimator: true);

            Log.LogInfo(report.Detected == 0
                ? "[avatar] aucun composant de gameplay à retirer de la copie."
                : $"[avatar] composants de gameplay : {report.Detected} détecté(s) -> {report.Removed} retiré(s)" +
                  (report.Remaining.Count == 0 ? ", 0 restant" : $", restants : {string.Join(", ", report.Remaining)}"));
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

    /// <summary>
    /// Gère le cycle de vie des avatars distants en fonction des événements réseau
    /// (<see cref="Coop.Model.PlayerJoinedMessage"/>, <see cref="Coop.Model.PlayerLeftMessage"/>,
    /// <see cref="Coop.Model.PlayersSnapshotMessage"/>) et interpole leurs positions/rotations
    /// entre deux ticks serveur (20 Hz).
    ///
    /// Dépend de la présence du joueur local dans le monde (<c>Player.main</c> et <c>Player.main.head</c>)
    /// puisque la fabrique copie le corps local. Tant que le monde n'est pas chargé, les joueurs
    /// distants connus sont conservés en mémoire et leurs avatars sont instanciés dès que le
    /// corps local devient disponible.
    /// </summary>
    internal sealed class RemotePlayerManager
    {
        /// <summary>
        /// Id du personnage virtuel de l'hôte (le serveur lui-même) dans les messages de joueur.
        /// Recopié de `ServerNetwork` (qui l'écrit en clair à plusieurs endroits) : c'est une constante du
        /// protocole, pas une valeur de sonde.
        /// </summary>
        private const ushort HostAvatarPlayerId = 9999;
        private sealed class RemoteEntry
        {
            public ushort PlayerId;
            public string Username;
            public long LastServerTick;
            public bool HasPosition;
            public Vector3 PreviousPosition;
            public Quaternion PreviousRotation = Quaternion.identity;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation = Quaternion.identity;
            public Vector3 CurrentRootPosition;
            public Quaternion CurrentRootRotation = Quaternion.identity;
            public Vector3 PreviousMoveSpeed;
            public Vector3 TargetMoveSpeed;
            public float PreviousViewPitch;
            public float TargetViewPitch;
            public bool IsUnderwater;
            public bool OnSurface;
            public Coop.Model.PlayerActionFlags ActionFlags;
            public float LastSnapshotRealtime;
            public string[] Equipment;

            /// <summary>Couleur assignée par le serveur ; <see cref="HasColor"/> dit si elle est arrivée.</summary>
            public byte ColorIndex;
            public bool HasColor;

            public bool HasHeldItem;
            public string HeldItemName = string.Empty;
            public string[] HoldingParams = Array.Empty<string>();
            public RemoteAvatarHeldItem HeldItem;
            public GameObject Avatar;
            public Animator Animator;
            public SkinnedMeshRenderer BodyRenderer;
            public Vector3[] BaselineVertices;
            public bool LoggedFirstAnimatedMovement;
            public bool LoggedBoneDisplacement;
            public float FirstAnimatedMovementTime;
        }

        /// <summary>Durée nominale d'interpolation (un intervalle de tick à 20 Hz = 50 ms).</summary>
        private const float InterpolationWindowSeconds = 0.05f;

        /// <summary>Au-delà de cette distance (m), on téléporte au lieu d'interpoler (ex. spawn initial).</summary>
        private const float TeleportThresholdMeters = 10f;

        private readonly Dictionary<ushort, RemoteEntry> _remotes = new Dictionary<ushort, RemoteEntry>();
        private readonly ManualLogSource _log;

        // — Relevés pour les sondes (`-coopdebug=presence`) ————————————————————
        //
        // Ils ne servent à **rien** dans le jeu : aucun de ces champs n'est lu par le chemin normal. Ils
        // existent parce qu'une sonde doit mesurer ce que le client a réellement reçu, pas ce qu'il aurait
        // dû recevoir. Un relevé reconstruit par la sonde mesurerait la sonde.
        private int _equipmentRevision;
        private ushort _lastEquipmentId;
        private string[] _lastEquipmentTypes = Array.Empty<string>();
        private int _heldItemRevision;
        private ushort _lastHeldItemId;
        private string _lastHeldItemName = string.Empty;
        private string[] _lastHoldingParams = Array.Empty<string>();

        public RemotePlayerManager(ManualLogSource log)
        {
            _log = log;
        }

        public int RemoteCount => _remotes.Count;

        public int SpawnedAvatarCount
        {
            get
            {
                var count = 0;
                foreach (var entry in _remotes.Values)
                {
                    if (entry.Avatar != null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public void OnPlayerJoined(Coop.Model.PlayerJoinedMessage msg)
        {
            if (!_remotes.TryGetValue(msg.PlayerId, out var entry))
            {
                entry = new RemoteEntry
                {
                    PlayerId = msg.PlayerId,
                    Username = msg.Username ?? $"Joueur#{msg.PlayerId}",
                };
                _remotes[msg.PlayerId] = entry;
            }
            else
            {
                entry.Username = msg.Username ?? entry.Username;
            }

            var pos = new Vector3(msg.X, msg.Y, msg.Z);
            var rot = NormalizeQuaternion(new Quaternion(msg.RotX, msg.RotY, msg.RotZ, msg.RotW));
            if (pos.sqrMagnitude > 0.0001f)
            {
                entry.PreviousPosition = pos;
                entry.TargetPosition = pos;
                entry.PreviousRotation = rot;
                entry.TargetRotation = rot;
                entry.HasPosition = true;
                entry.LastSnapshotRealtime = Time.realtimeSinceStartup;
            }

            _log.LogInfo(
                $"[presence] joueur rejoint : '{entry.Username}' (id={entry.PlayerId}) " +
                $"pos=({msg.X:F2}, {msg.Y:F2}, {msg.Z:F2}) — total distants={_remotes.Count}");
        }

        public void OnPlayerLeft(Coop.Model.PlayerLeftMessage msg)
        {
            if (!_remotes.TryGetValue(msg.PlayerId, out var entry))
            {
                return;
            }

            _remotes.Remove(msg.PlayerId);
            entry.HeldItem?.Dispose();
            entry.HeldItem = null;
            if (entry.Avatar != null)
            {
                UnityEngine.Object.Destroy(entry.Avatar);
                entry.Avatar = null;
            }

            _log.LogInfo(
                $"[presence] joueur parti : '{entry.Username}' (id={msg.PlayerId}, raison={msg.Reason}) " +
                $"— avatar détruit, total distants={_remotes.Count}");
        }

        public void OnSnapshot(Coop.Model.PlayersSnapshotMessage snapshot)
        {
            if (snapshot.Players == null)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            for (var i = 0; i < snapshot.Players.Length; i++)
            {
                var state = snapshot.Players[i];
                if (!_remotes.TryGetValue(state.PlayerId, out var entry))
                {
                    // Filet de sécurité si un snapshot arrive avant ou sans PlayerJoined.
                    entry = new RemoteEntry
                    {
                        PlayerId = state.PlayerId,
                        Username = $"Joueur#{state.PlayerId}",
                    };
                    _remotes[state.PlayerId] = entry;
                    _log.LogInfo($"[presence] joueur découvert via snapshot : id={state.PlayerId}");
                }

                if (entry.HasPosition && snapshot.ServerTick < entry.LastServerTick)
                {
                    continue;
                }

                entry.LastServerTick = snapshot.ServerTick;
                var newPos = new Vector3(state.X, state.Y, state.Z);
                var newRot = NormalizeQuaternion(new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW));
                var newSpeed = new Vector3(state.MoveSpeedX, state.MoveSpeedY, state.MoveSpeedZ);

                if (!entry.HasPosition || Vector3.Distance(entry.TargetPosition, newPos) > TeleportThresholdMeters)
                {
                    entry.PreviousPosition = newPos;
                    entry.TargetPosition = newPos;
                    entry.CurrentRootPosition = newPos;
                    entry.PreviousRotation = newRot;
                    entry.TargetRotation = newRot;
                    entry.CurrentRootRotation = newRot;
                    entry.PreviousMoveSpeed = newSpeed;
                    entry.TargetMoveSpeed = newSpeed;
                    entry.PreviousViewPitch = state.ViewPitch;
                    entry.TargetViewPitch = state.ViewPitch;
                    entry.HasPosition = true;
                }
                else
                {
                    // Point de départ de la nouvelle fenêtre d'interpolation : la position racine
                    // interpolée courante (sans mélanger l'espace de Player.mainObject et celui de Player/body).
                    entry.PreviousPosition = entry.Avatar != null ? entry.CurrentRootPosition : entry.TargetPosition;
                    entry.PreviousRotation = entry.Avatar != null ? entry.CurrentRootRotation : entry.TargetRotation;
                    entry.TargetPosition = newPos;
                    entry.TargetRotation = newRot;
                    entry.PreviousMoveSpeed = entry.TargetMoveSpeed;
                    entry.TargetMoveSpeed = newSpeed;
                    entry.PreviousViewPitch = entry.TargetViewPitch;
                    entry.TargetViewPitch = state.ViewPitch;
                }

                entry.IsUnderwater = state.IsUnderwater;
                entry.OnSurface = state.OnSurface;
                entry.ActionFlags = state.ActionFlags;
                entry.LastSnapshotRealtime = now;
            }
        }

        /// <summary>
        /// Couleur d'un joueur distant, décidée par le serveur (§27).
        ///
        /// Même cycle que l'équipement : appliquée tout de suite si l'avatar existe, mémorisée sinon
        /// pour la création. Le client ne décide rien — c'est ce qui garantit que deux joueurs ne
        /// portent pas la même couleur.
        /// </summary>
        public void OnPlayerColor(Coop.Model.PlayerColorMessage msg)
        {
            if (!_remotes.TryGetValue(msg.PlayerId, out var entry))
            {
                entry = new RemoteEntry
                {
                    PlayerId = msg.PlayerId,
                    Username = $"Joueur#{msg.PlayerId}",
                };
                _remotes[msg.PlayerId] = entry;
            }

            entry.ColorIndex = msg.ColorIndex;
            entry.HasColor = true;
            Coop.Model.Protocol.PlayerColorAt(msg.ColorIndex, out var r, out var g, out var b);

            if (entry.Avatar != null)
            {
                var report = RemoteAvatarColor.Apply(entry.Avatar, msg.ColorIndex);
                _log.LogInfo(
                    $"[presence] couleur appliquée sur l'avatar de '{entry.Username}' (id={entry.PlayerId}) : " +
                    $"indice {msg.ColorIndex} rgb({r},{g},{b}) -> {report}");
            }
            else
            {
                _log.LogInfo(
                    $"[presence] couleur mémorisée pour '{entry.Username}' (id={msg.PlayerId}, avatar en attente) : " +
                    $"indice {msg.ColorIndex} rgb({r},{g},{b})");
            }
        }

        /// <summary>
        /// Nombre de joueurs distants connus — pour la sonde : c'est ce qui distingue « personne n'est là »
        /// (une mesure impossible) de « un joueur est là mais aucun avatar » (un défaut).
        /// </summary>
        internal int KnownRemoteCountForProbe => _remotes.Count;

        /// <summary>
        /// Nombre de **clients** distants connus, l'hôte (id 9999) exclu.
        ///
        /// La sonde de présence doit attendre celui-ci et non `KnownRemoteCountForProbe` : un hôte non
        /// dédié expose lui aussi un avatar, ce qui satisfait l'attente alors que les deux mesures
        /// mutuelles (falsification, nage) n'ont aucun observateur. Mesuré le 2026-10-19 : la sonde du
        /// client 1 a conclu `verdict=False` 13 s après son entrée dans le monde, en comptant l'avatar de
        /// l'hôte, pendant que le client 2 chargeait encore son monde — `PlayerId falsifié=False (défaut :
        /// aucun joueur distant)` alors que l'autre client existait, mais pas encore.
        /// </summary>
        internal int KnownPeerCountForProbe
        {
            get
            {
                var count = 0;
                foreach (var pair in _remotes)
                {
                    if (pair.Key != HostAvatarPlayerId)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Dernier équipement **reçu du serveur** : son identifiant effectif et sa liste.
        ///
        /// C'est ce qui rend la réécriture du `PlayerId` mesurable côté client : une sonde annonce un
        /// équipement sous l'id d'un autre joueur, et l'autre client relève ici **sous quel id** le relais
        /// l'a diffusé. La valeur vient du gestionnaire, pas de la sonde.
        /// </summary>
        internal bool TryGetLastEquipmentForProbe(out ushort playerId, out string[] types, out int revision)
        {
            playerId = _lastEquipmentId;
            types = _lastEquipmentTypes;
            revision = _equipmentRevision;
            return _equipmentRevision > 0;
        }

        /// <summary>Dernier objet tenu reçu du serveur, avec son identifiant effectif.</summary>
        internal bool TryGetLastHeldItemForProbe(
            out ushort playerId,
            out string techTypeName,
            out string[] holdingParams,
            out int revision)
        {
            playerId = _lastHeldItemId;
            techTypeName = _lastHeldItemName;
            holdingParams = _lastHoldingParams;
            revision = _heldItemRevision;
            return _heldItemRevision > 0;
        }

        /// <summary>
        /// Première entrée distante qui est un **autre client**, l'hôte (id 9999, avatar virtuel du serveur)
        /// exclu.
        ///
        /// Pourquoi l'exclusion est nécessaire et non cosmétique : avec un hôte et deux clients, le
        /// gestionnaire contient trois entrées, et `TryGetFirstAvatarForProbe` peut rendre celle de l'hôte.
        /// Une sonde qui enverrait sa falsification à l'hôte n'aurait aucun observateur (le serveur ne
        /// retourne jamais un message à son émetteur et ne lance pas la sonde), et une nage mesurée sur
        /// l'avatar de l'hôte serait toujours immobile — deux faux négatifs parfaitement silencieux.
        /// </summary>
        internal bool TryGetPeerAvatarForProbe(
            out ushort playerId,
            out string username,
            out GameObject avatar)
        {
            foreach (var pair in _remotes)
            {
                var entry = pair.Value;
                if (pair.Key == HostAvatarPlayerId || entry.Avatar == null)
                {
                    continue;
                }

                playerId = pair.Key;
                username = entry.Username;
                avatar = entry.Avatar;
                return true;
            }

            playerId = 0;
            username = null;
            avatar = null;
            return false;
        }

        /// <summary>
        /// Mouvement d'un joueur distant **précis** tel que le client l'applique : vitesse cible (celle qui
        /// pilote l'animateur) et position cible. La variante « premier trouvé » ne suffit pas dès qu'il y a
        /// un hôte dans le monde : elle rendrait la sienne, toujours immobile.
        /// </summary>
        internal bool TryGetMotionForProbe(
            ushort playerId,
            out float moveSpeed,
            out Vector3 position,
            out bool isUnderwater)
        {
            if (_remotes.TryGetValue(playerId, out var entry) && entry.HasPosition)
            {
                moveSpeed = entry.TargetMoveSpeed.magnitude;
                position = entry.TargetPosition;
                isUnderwater = entry.IsUnderwater;
                return true;
            }

            moveSpeed = 0f;
            position = Vector3.zero;
            isUnderwater = false;
            return false;
        }

        /// <summary>
        /// Mouvement d'un joueur distant tel que le client l'applique : vitesse cible (celle qui pilote
        /// l'animateur) et position cible. Sert à vérifier qu'un vrai déplacement **arrive** et qu'il
        /// s'arrête, au lieu de croire l'animateur sur parole.
        /// </summary>
        internal bool TryGetFirstMotionForProbe(
            out ushort playerId,
            out float moveSpeed,
            out Vector3 position,
            out bool isUnderwater)
        {
            foreach (var pair in _remotes)
            {
                var entry = pair.Value;
                if (!entry.HasPosition)
                {
                    continue;
                }

                playerId = pair.Key;
                moveSpeed = entry.TargetMoveSpeed.magnitude;
                position = entry.TargetPosition;
                isUnderwater = entry.IsUnderwater;
                return true;
            }

            playerId = 0;
            moveSpeed = 0f;
            position = Vector3.zero;
            isUnderwater = false;
            return false;
        }

        /// <summary>
        /// Premier avatar distant **réellement instancié et coloré** — et son indice, celui du serveur.
        ///
        /// Exposé à la sonde de présence (<c>-coopdebug=presence</c>) parce que la branche « couleur reçue
        /// alors que l'avatar existe déjà » est inatteignable par le cours normal des messages : la couleur
        /// part à l'admission, l'avatar naît plus tard (mesuré : `couleur mémorisée … avatar en attente`).
        /// </summary>
        internal bool TryGetFirstAvatarForProbe(
            out ushort playerId,
            out string username,
            out byte colorIndex,
            out GameObject avatar)
        {
            foreach (var pair in _remotes)
            {
                var entry = pair.Value;
                if (entry.Avatar == null || !entry.HasColor)
                {
                    continue;
                }

                playerId = pair.Key;
                username = entry.Username;
                colorIndex = entry.ColorIndex;
                avatar = entry.Avatar;
                return true;
            }

            playerId = 0;
            username = null;
            colorIndex = 0;
            avatar = null;
            return false;
        }

        /// <summary>
        /// Applique un équipement reçu au joueur local, en le faisant passer par le gestionnaire :
        /// la sonde mesure le même chemin qu'un message du serveur, elle ne le réimplémente pas.
        /// </summary>
        internal void ProbeApplyEquipment(ushort playerId, string[] types) =>
            OnPlayerEquipment(new Coop.Model.PlayerEquipmentMessage
            {
                PlayerId = playerId,
                TechTypeNames = types,
            });

        /// <summary>
        /// Rejoue **le chemin réel** d'un message de couleur pour la sonde, au lieu de le réimplémenter :
        /// c'est <see cref="OnPlayerColor"/> qui s'exécute, donc exactement le code que le serveur déclenche.
        ///
        /// Le client ne décide rien pour autant : c'est la sonde qui décide, elle le journalise, et elle
        /// remet ensuite l'indice assigné par le serveur (et le vérifie).
        /// </summary>
        internal void ProbeReplayColor(ushort playerId, byte colorIndex) =>
            OnPlayerColor(new Coop.Model.PlayerColorMessage
            {
                PlayerId = playerId,
                ColorIndex = colorIndex,
            });

        public void OnPlayerEquipment(Coop.Model.PlayerEquipmentMessage msg)
        {
            if (!_remotes.TryGetValue(msg.PlayerId, out var entry))
            {
                entry = new RemoteEntry
                {
                    PlayerId = msg.PlayerId,
                    Username = $"Joueur#{msg.PlayerId}",
                };
                _remotes[msg.PlayerId] = entry;
            }

            entry.Equipment = msg.TechTypeNames ?? Array.Empty<string>();
            _equipmentRevision++;
            _lastEquipmentId = msg.PlayerId;
            _lastEquipmentTypes = entry.Equipment;

            var desc = entry.Equipment.Length == 0
                ? "(sans équipement)"
                : string.Join(", ", entry.Equipment);

            if (entry.Avatar != null)
            {
                RemoteAvatarFactory.ApplyEquipment(entry.Avatar, entry.Equipment);
                var activeNodes = RemoteAvatarEquipment.ActiveGovernedNodes(entry.Avatar);
                _log.LogInfo(
                    $"[presence] équipement appliqué sur l'avatar de '{entry.Username}' (id={entry.PlayerId}) : " +
                    $"{entry.Equipment.Length} pièce(s) [{desc}] -> {activeNodes.Count} nœud(s) actif(s) [{string.Join(", ", activeNodes)}]");
            }
            else
            {
                _log.LogInfo(
                    $"[presence] équipement mémorisé pour '{entry.Username}' (id={entry.PlayerId}, avatar en attente) : " +
                    $"{entry.Equipment.Length} pièce(s) [{desc}]");
            }
        }

        /// <summary>
        /// Objet tenu d'un joueur distant : l'outil en main et les paramètres d'animation que le jeu a
        /// posés pour lui. Reçu en fiable (le changement d'outil est rare), appliqué immédiatement si
        /// l'avatar existe, mémorisé sinon pour la création de l'avatar.
        /// </summary>
        public void OnPlayerHeldItem(Coop.Model.PlayerHeldItemMessage msg)
        {
            if (!_remotes.TryGetValue(msg.PlayerId, out var entry))
            {
                entry = new RemoteEntry
                {
                    PlayerId = msg.PlayerId,
                    Username = $"Joueur#{msg.PlayerId}",
                };
                _remotes[msg.PlayerId] = entry;
            }

            entry.HasHeldItem = true;
            entry.HeldItemName = msg.TechTypeName ?? string.Empty;
            entry.HoldingParams = msg.HoldingParams ?? Array.Empty<string>();
            _heldItemRevision++;
            _lastHeldItemId = msg.PlayerId;
            _lastHeldItemName = entry.HeldItemName;
            _lastHoldingParams = entry.HoldingParams;

            var label = entry.HeldItemName.Length == 0 ? "(rien en main)" : entry.HeldItemName;

            if (entry.HeldItem != null)
            {
                entry.HeldItem.Apply(entry.HeldItemName, entry.HoldingParams);
                _log.LogInfo(
                    $"[presence] objet tenu appliqué sur l'avatar de '{entry.Username}' (id={entry.PlayerId}) : " +
                    $"{entry.HeldItem.Describe()}");
            }
            else
            {
                _log.LogInfo(
                    $"[presence] objet tenu mémorisé pour '{entry.Username}' (id={entry.PlayerId}, avatar en attente) : " +
                    $"{label} ({entry.HoldingParams.Length} paramètre(s) `holding_*`)");
            }
        }

        /// <summary>
        /// Appelé à chaque frame côté client : crée les avatars en attente dès que le joueur local
        /// est prêt dans le monde, et interpole les transformations de chaque avatar actif.
        /// </summary>
        public void UpdateAvatars()
        {
            if (_remotes.Count == 0)
            {
                return;
            }

            var localReady = Player.main != null && Player.mainObject != null && Player.main.head != null;
            var now = Time.realtimeSinceStartup;

            foreach (var entry in _remotes.Values)
            {
                if (!entry.HasPosition)
                {
                    continue;
                }

                if (entry.Avatar == null && localReady)
                {
                    var root = Player.mainObject.transform;
                    RemoteAvatarFactory.ComputeBodyWorldPose(
                        root,
                        entry.TargetPosition,
                        entry.TargetRotation,
                        out var initialBodyPos,
                        out var initialBodyRot,
                        out var bodyLocalOffset);

                    var clone = RemoteAvatarFactory.TryCreate(root, initialBodyPos, root, out var failure);
                    if (clone != null)
                    {
                        clone.name = $"{RemoteAvatarFactory.CloneName}.{entry.PlayerId}.{entry.Username}";
                        clone.transform.position = initialBodyPos;
                        clone.transform.rotation = initialBodyRot;
                        entry.CurrentRootPosition = entry.TargetPosition;
                        entry.CurrentRootRotation = entry.TargetRotation;

                        var moved = RemoteAvatarFactory.MoveRenderersToWorldLayer(clone);
                        if (entry.Equipment != null)
                        {
                            RemoteAvatarFactory.ApplyEquipment(clone, entry.Equipment);
                        }

                        // La couleur vient **après** la tenue : les règles de tenue activent ou masquent
                        // des nœuds, et teinter un renderer masqué n'a pas d'intérêt — c'est la
                        // combinaison visible qu'on colore.
                        var colorReport = string.Empty;
                        if (entry.HasColor)
                        {
                            colorReport = RemoteAvatarColor.Apply(clone, entry.ColorIndex);
                        }

                        var activeNodes = RemoteAvatarEquipment.ActiveGovernedNodes(clone);
                        entry.Avatar = clone;
                        entry.Animator = clone.GetComponentInChildren<Animator>(true);
                        entry.BodyRenderer = FindMainBodyRenderer(clone);
                        entry.BaselineVertices = BakeVertices(entry.BodyRenderer);

                        // L'objet tenu a sa propre vie : il pose la pose `holding_*`, monte le modèle
                        // de l'outil sous `attach1` et sort le PDA quand il faut. Créé même sans objet
                        // reçu, pour que le premier `PlayerHeldItem` qui arrive trouve quelqu'un.
                        entry.HeldItem = new RemoteAvatarHeldItem(clone, entry.Animator);
                        if (entry.HasHeldItem)
                        {
                            entry.HeldItem.Apply(entry.HeldItemName, entry.HoldingParams);
                        }

                        var alignmentReport = string.Empty;
                        var localBody = RemoteAvatarFactory.FindBodyRoot(root);
                        if (localBody != null &&
                            RemoteAvatarFactory.TryMeasureWorldVerticalSpan(localBody, out var localMinY, out var localMaxY) &&
                            RemoteAvatarFactory.TryMeasureWorldVerticalSpan(clone.transform, out var cloneMinY, out var cloneMaxY))
                        {
                            var localFeetRelRoot = localMinY - root.position.y;
                            var cloneFeetRelRoot = cloneMinY - entry.TargetPosition.y;
                            var feetError = cloneFeetRelRoot - localFeetRelRoot;
                            alignmentReport =
                                $", offsetCorpsLocal={bodyLocalOffset.ToString("F3")}, " +
                                $"piedsRelRoot: local={localFeetRelRoot:F3}m avatar={cloneFeetRelRoot:F3}m (écart={feetError:F4}m, taille={cloneMaxY - cloneMinY:F2}m)";
                        }

                        _log.LogInfo(
                            $"[presence] avatar instancié pour '{entry.Username}' (id={entry.PlayerId}) " +
                            $"root={entry.TargetPosition.ToString("F2")} -> body={initialBodyPos.ToString("F2")} " +
                            $"({moved} renderers sur Default, animator={(entry.Animator != null ? entry.Animator.name : "absent")}, " +
                            $"tenue={entry.Equipment?.Length ?? 0} pièce(s), {activeNodes.Count} nœud(s) actif(s) [{string.Join(", ", activeNodes)}]" +
                            (colorReport.Length == 0 ? string.Empty : $", {colorReport}") +
                            $"{alignmentReport})");
                    }
                    else
                    {
                        _log.LogWarning(
                            $"[presence] échec création avatar pour '{entry.Username}' (id={entry.PlayerId}) : {failure}");
                    }
                }

                if (entry.Avatar != null)
                {
                    var t = Mathf.Clamp01((now - entry.LastSnapshotRealtime) / InterpolationWindowSeconds);
                    entry.CurrentRootPosition = Vector3.Lerp(entry.PreviousPosition, entry.TargetPosition, t);
                    entry.CurrentRootRotation = Quaternion.Slerp(entry.PreviousRotation, entry.TargetRotation, t);

                    var root = Player.mainObject != null ? Player.mainObject.transform : null;
                    RemoteAvatarFactory.ComputeBodyWorldPose(
                        root,
                        entry.CurrentRootPosition,
                        entry.CurrentRootRotation,
                        out var bodyPos,
                        out var bodyRot,
                        out _);

                    entry.Avatar.transform.position = bodyPos;
                    entry.Avatar.transform.rotation = bodyRot;

                    if (entry.Animator != null)
                    {
                        var speed = Vector3.Lerp(entry.PreviousMoveSpeed, entry.TargetMoveSpeed, t);
                        var pitch = Mathf.Lerp(entry.PreviousViewPitch, entry.TargetViewPitch, t);
                        var mag = speed.magnitude;

                        SafeAnimator.SetFloat(entry.Animator, "move_speed", mag);
                        SafeAnimator.SetFloat(entry.Animator, "move_speed_x", speed.x);
                        SafeAnimator.SetFloat(entry.Animator, "move_speed_y", speed.y);
                        SafeAnimator.SetFloat(entry.Animator, "move_speed_z", speed.z);
                        SafeAnimator.SetFloat(entry.Animator, "view_pitch", pitch);
                        SafeAnimator.SetBool(entry.Animator, "is_underwater", entry.IsUnderwater);
                        SafeAnimator.SetBool(entry.Animator, "on_surface", entry.OnSurface);

                        // Actions instantanées : elles voyagent à 20 Hz avec la position, donc elles
                        // suivent le geste sans faire porter un aller-retour à chaque clic.
                        var flags = entry.ActionFlags;
                        SafeAnimator.SetBool(entry.Animator, "using_pda", (flags & Coop.Model.PlayerActionFlags.UsingPda) != 0);
                        SafeAnimator.SetBool(entry.Animator, "using_tool", (flags & Coop.Model.PlayerActionFlags.UsingTool) != 0);
                        SafeAnimator.SetBool(entry.Animator, "using_tool_alt", (flags & Coop.Model.PlayerActionFlags.UsingToolAlt) != 0);
                        SafeAnimator.SetBool(entry.Animator, "in_seamoth", (flags & Coop.Model.PlayerActionFlags.InSeamoth) != 0);
                        SafeAnimator.SetBool(entry.Animator, "in_exosuit", (flags & Coop.Model.PlayerActionFlags.InExosuit) != 0);

                        if (!entry.LoggedFirstAnimatedMovement && mag > 0.1f)
                        {
                            entry.LoggedFirstAnimatedMovement = true;
                            entry.FirstAnimatedMovementTime = now;
                            _log.LogInfo(
                                $"[presence] animation active sur '{entry.Username}' (id={entry.PlayerId}) : " +
                                $"move_speed={mag:F2} v=({speed.x:F2},{speed.y:F2},{speed.z:F2}) " +
                                $"view_pitch={pitch:F1} is_underwater={entry.IsUnderwater} on_surface={entry.OnSurface}");
                        }
                        else if (entry.LoggedFirstAnimatedMovement && !entry.LoggedBoneDisplacement &&
                                 now - entry.FirstAnimatedMovementTime >= 0.4f)
                        {
                            entry.LoggedBoneDisplacement = true;
                            var currentVertices = BakeVertices(entry.BodyRenderer);
                            var displacement = AverageVertexDistance(entry.BaselineVertices, currentVertices);
                            _log.LogInfo(
                                $"[presence] déformation squelettique mesurée sur '{entry.Username}' (id={entry.PlayerId}) : " +
                                $"déplacement moyen des sommets={displacement:F4} m (maillage='{entry.BodyRenderer?.name ?? "aucun"}')");
                        }
                    }

                    // L'objet tenu est avancé après la pose : `Tick` fait progresser les demandes de
                    // prefab (asynchrones) et `SetPdaVisible` suit le drapeau reçu à 20 Hz.
                    if (entry.HeldItem != null)
                    {
                        entry.HeldItem.SetPdaVisible(
                            (entry.ActionFlags & Coop.Model.PlayerActionFlags.UsingPda) != 0);
                        entry.HeldItem.Tick();
                    }
                }
            }
        }

        public void ClearAll()
        {
            foreach (var entry in _remotes.Values)
            {
                entry.HeldItem?.Dispose();
                entry.HeldItem = null;
                if (entry.Avatar != null)
                {
                    UnityEngine.Object.Destroy(entry.Avatar);
                    entry.Avatar = null;
                }
            }

            _remotes.Clear();
        }

        private static Quaternion NormalizeQuaternion(Quaternion q)
        {
            var magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (magSq < 0.0001f || float.IsNaN(magSq))
            {
                return Quaternion.identity;
            }

            var inv = 1f / Mathf.Sqrt(magSq);
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }

        private static SkinnedMeshRenderer FindMainBodyRenderer(GameObject clone)
        {
            SkinnedMeshRenderer best = null;
            foreach (var r in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.sharedMesh == null)
                {
                    continue;
                }

                if (best == null || r.sharedMesh.vertexCount > best.sharedMesh.vertexCount)
                {
                    best = r;
                }
            }

            return best;
        }

        private static Vector3[] BakeVertices(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null)
            {
                return null;
            }

            var mesh = new Mesh();
            try
            {
                renderer.BakeMesh(mesh);
                return mesh.vertices;
            }
            catch
            {
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(mesh);
            }
        }

        private static float AverageVertexDistance(Vector3[] a, Vector3[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0)
            {
                return -1f;
            }

            var sum = 0f;
            for (var i = 0; i < a.Length; i++)
            {
                sum += Vector3.Distance(a[i], b[i]);
            }

            return sum / a.Length;
        }
    }
}
