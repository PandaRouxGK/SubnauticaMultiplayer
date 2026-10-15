using System.Collections;
using System.IO;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Coop.Client
{
    /// <summary>
    /// Sonde M2.0, activée seulement par <c>-coopdebug=avatar</c> : **peut-on fabriquer un second
    /// corps de joueur, et rend-il vraiment ?**
    ///
    /// C'est la seule inconnue de M2, et elle vaut d'être traitée seule. Rien dans l'API du jeu
    /// n'expose de prefab de joueur (`tools/ApiDump` : `Player` n'a ni propriété ni champ de modèle,
    /// privé compris) : le corps n'existe qu'à l'exécution, dans la hiérarchie de
    /// <c>Player.mainObject</c>. Écrire la synchronisation des joueurs avant d'avoir la réponse,
    /// c'est risquer de jeter le travail si la copie ne rend pas.
    ///
    /// Ce n'est pas une question d'impression. « Verdict : ça a l'air de rendre » ne vaut rien, parce
    /// qu'un objet parfaitement construit peut n'apparaître nulle part : un calque absent du
    /// `cullingMask`, un renderer désactivé, un maillage nul, et il n'y a rien à l'écran. La sonde
    /// **compte donc les pixels que la copie change** : deux clichés dos à dos sans la copie donnent
    /// le bruit de la scène (eau, HUD), puis un cliché avec la copie donne l'écart. Sans témoin de
    /// bruit, un écart de pixels ne prouve rien non plus.
    /// </summary>
    public class AvatarProbeBehaviour : MonoBehaviour
    {
        private const float WorldTimeoutSeconds = 240f;

        /// <summary>
        /// Distance à laquelle la copie est posée devant la caméra, en mètres.
        ///
        /// Devant la **caméra**, et pas devant le joueur : la première mesure a placé la copie 3 m
        /// devant `Player.main`, c'est-à-dire dans la coque du module de survie, et le résultat était
        /// sans ambiguïté — 3 renderers sur 25 dans le frustum, aucun pixel changé. Le placement est
        /// donc la variable à fixer avant de conclure quoi que ce soit sur le rendu.
        /// </summary>
        private const float AvatarDistanceMeters = 1.5f;

        private const int MaxDumpDepth = 3;
        private const int MaxDumpNodes = 150;

        /// <summary>Écart minimal de composante RVB pour compter un pixel comme changé (0-255).</summary>
        private const int PixelDifferenceThreshold = 30;

        private static readonly ManualLogSource Log = RemoteAvatarFactory.Log;

        private IEnumerator Start()
        {
            Log.LogWarning("[sonde] autotest d'avatar activé (-coopdebug=avatar) — attente du monde...");

            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (Player.main == null && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSeconds(2f);
            }

            if (Player.main == null || Player.mainObject == null)
            {
                Log.LogError(
                    $"[sonde] aucun joueur après {WorldTimeoutSeconds:F0}s (Player.main ou mainObject null) : " +
                    "test impossible, l'avatar reste NON PROUVÉ.");
                yield break;
            }

            // `Player.head` est l'ancre de la fabrique : tant qu'il est null, le corps n'est pas
            // construit et une copie ne prouverait rien de plus que « le modèle n'existe pas encore ».
            while (Player.main.head == null && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSeconds(1f);
            }

            var root = Player.mainObject.transform;
            Log.LogInfo(
                $"[sonde] joueur prêt : scene='{SceneManager.GetActiveScene().name}', root='{root.name}', " +
                $"head={(Player.main.head != null ? "présent" : "ABSENT")}, position={root.position.ToString("F2")}");

            yield return new WaitForSeconds(3f);

            DumpHierarchy(root);

            var camera = Camera.main;
            Log.LogInfo(
                camera == null
                    ? "[sonde] pas de Camera.main."
                    : $"[sonde] caméra principale '{camera.name}' à {camera.transform.position.ToString("F2")} — " +
                      $"cullingMask=0x{camera.cullingMask:X}, cible={camera.targetTexture}");

            var anchor = camera != null ? camera.transform : root;
            var position = anchor.position + anchor.forward * AvatarDistanceMeters;

            if (camera != null)
            {
                var toAvatar = (position - camera.transform.position).normalized;
                Log.LogInfo(
                    $"[sonde] placement devant la caméra : avant={camera.transform.forward.ToString("F2")} " +
                    $"produit scalaire avec la direction de la copie={Vector3.Dot(camera.transform.forward, toAvatar):F3} " +
                    $"(1 = pile dans l'axe).");
            }

            var clone = RemoteAvatarFactory.TryCreate(root, position, root, out var failure);

            if (clone == null)
            {
                Log.LogError($"[sonde] RÉSULTAT ÉCHEC : {failure}");
                yield break;
            }

            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            var summary = RemoteAvatarFactory.Describe(clone, out var inFrustum);
            Log.LogInfo($"[sonde] copie à {clone.transform.position.ToString("F2")} : {summary}");
            Log.LogInfo($"[sonde] calques d'origine de la copie : {RemoteAvatarFactory.DescribeLayers(clone, camera)}");
            LogRendererAnatomy(clone);
            Log.LogInfo($"[sonde] branches de maillage visibles : {RemoteAvatarFactory.DescribeSuitBranches(clone)}");
            LogRendererVisibility(clone);

            yield return MeasureVisibility(clone, camera);

            // Mesure complémentaire : la précédente mêle trois causes possibles (hors champ, caché par
            // une paroi, éliminé par le rendu du jeu). Le rendu isolé n'en garde qu'une.
            yield return IsolatedRender(clone);

            // État final : c'est celui que la capture d'écran doit montrer, et celui qu'aurait un vrai
            // avatar distant (objet du monde, donc calque `Default`).
            clone.SetActive(true);

            yield return CaptureProof();
        }

        /// <summary>
        /// Mesure ce que la copie change à l'écran, avec son témoin.
        ///
        /// L'ordre compte : `off1` et `off2` encadrent toute la mesure, donc leur écart donne le bruit
        /// maximal de la scène sur la durée totale — c'est le mètre-étalon, pas l'écart entre deux
        /// images voisines qui sous-estimerait le bruit. Chaque mesure est ensuite comparée à **ce**
        /// bruit-là, ce qui rend le verdict difficile à obtenir par accident.
        /// </summary>
        private IEnumerator MeasureVisibility(GameObject clone, Camera camera)
        {
            var moved = RemoteAvatarFactory.MoveRenderersToWorldLayer(clone);
            Log.LogInfo($"[sonde] {moved} renderer(s) basculé(s) sur le calque Default (calque d'un objet du monde).");

            // Alternance masquée / visible, **à un intervalle d'une frame**, cinq clichés :
            //
            //   o1  n1  o2  n2  o3
            //
            // Le signal se mesure sur o->n et le bruit sur n->o : **même intervalle**, donc comparaison
            // honnête. La première version comparait un signal de 3 frames à un bruit de 7 frames, et le
            // verdict basculait d'un run à l'autre sans que rien ne change dans le rendu (bruit 456 px
            // dans un run, 2111 px dans l'autre, pour un signal stable autour de 11 000 px). Un test qui
            // change de réponse sans que le monde change ne teste rien.
            // Masquée x4 puis visible x4, à une frame d'intervalle : le bruit se mesure entre deux
            // clichés du **même** état, le signal entre deux états différents.
            //
            // Quatre échantillons de chaque, et on compare les **médianes**, parce que le bruit de la
            // scène est erratique : une vague ou une lueur de HUD fait changer 2 381 px sur une frame
            // isolée, contre 214 px sur une autre. Comparer un signal à un unique pire cas de bruit
            // faisait échouer un avatar qui rend (9 198 px de signal contre un seuil de 9 524 px calculé
            // sur cette frame-là). La médiane ignore l'échantillon aberrant, des deux côtés.
            // Deux paires de chaque état : [masquée, masquée], [visible, visible], et on recommence.
            // Le bruit se mesure **dans** une paire, le signal **entre** deux paires, toujours à une frame
            // d'écart. Un premier motif « quatre d'un état puis quatre de l'autre » ne laissait qu'un seul
            // vrai changement d'état : les trois autres « signaux » étaient des paires du même état
            // (426, 295, 272 px), et leur médiane ne mesurait plus rien du tout.
            var shots = new Texture2D[8];
            for (var i = 0; i < shots.Length; i++)
            {
                clone.SetActive(i % 4 >= 2);
                yield return new WaitForEndOfFrame();
                shots[i] = Capture();
            }

            clone.SetActive(true);

            // Signal : on prend le plus faible des deux (conservateur).
            // Bruit : on prend le plus fort des deux (conservateur aussi).
            var noiseSamples = new[]
            {
                PixelDifference(shots[0], shots[1]),   // masquée -> masquée
                PixelDifference(shots[2], shots[3]),   // visible -> visible
                PixelDifference(shots[4], shots[5]),   // masquée -> masquée
                PixelDifference(shots[6], shots[7]),   // visible -> visible
            };

            var signalSamples = new[]
            {
                PixelDifference(shots[1], shots[2]),   // masquée -> visible
                PixelDifference(shots[3], shots[4]),   // visible -> masquée
                PixelDifference(shots[5], shots[6]),   // masquée -> visible
            };

            var noise = Median(noiseSamples);
            var signal = Median(signalSamples);
            var pixels = shots[0].width * shots[0].height;

            Log.LogInfo(
                $"[sonde] pixels de l'écran={pixels} — signal (changement d'état, une frame d'écart) : " +
                $"{string.Join(", ", signalSamples)} px -> médiane {signal:F0} ; bruit (même état, même écart) : " +
                $"{string.Join(", ", noiseSamples)} px -> médiane {noise:F0}");

            // Deux exigences : un rapport franc au bruit **et** un plancher absolu (un avatar ne peut pas
            // tenir dans une poignée de pixels, même à distance).
            var threshold = System.Math.Max(noise * 3, 2000);
            var renders = signal > threshold;

            Log.LogInfo(
                $"[sonde] décision : signal médian={signal:F0} px, bruit médian={noise:F0} px, seuil={threshold:F0} px " +
                $"(3x le bruit, plancher 2000) -> la copie rend={renders}");

            if (!renders)
            {
                Log.LogError(
                    "[sonde] la copie ne change pas assez de pixels pour être considérée comme rendue : " +
                    "elle existe, mais rien ne s'affiche.");
            }

            foreach (var shot in shots)
            {
                Destroy(shot);
            }
        }

        /// <summary>
        /// Capture d'écran de preuve, et **vérification qu'elle existe vraiment**.
        ///
        /// Le fichier n'est pas écrit au moment de l'appel : Unity l'écrit en fin de frame, et lors du
        /// premier essai il manquait encore après deux frames alors qu'il est bien arrivé ensuite. Le
        /// test attend donc le fichier au lieu de conclure trop tôt — sinon il aurait annoncé un échec
        /// alors que la preuve existait.
        /// </summary>
        private IEnumerator CaptureProof()
        {
            var proofPath = ScreenshotPath();
            ScreenCapture.CaptureScreenshot(proofPath);

            var deadline = Time.realtimeSinceStartup + 10f;
            long bytes = -1;

            while (Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSeconds(0.5f);

                if (File.Exists(proofPath))
                {
                    bytes = new FileInfo(proofPath).Length;
                    break;
                }
            }

            Log.LogInfo(bytes > 0
                ? $"[sonde] capture écrite : {bytes} octets -> {proofPath}"
                : $"[sonde] capture ABSENTE après 10 s -> {proofPath}");
        }

        /// <summary>
        /// Combien de renderers de la copie sont vus par une caméra, et à quoi ressemblent ces caméras.
        ///
        /// `Renderer.isVisible` **ne tient pas compte de l'occultation** : c'est exactement ce qu'on veut
        /// séparer ici. « Hors champ » et « caché par une paroi » ne se corrigent pas de la même façon,
        /// et sans cette distinction on réparerait la mauvaise chose.
        /// </summary>
        private static void LogRendererVisibility(GameObject clone)
        {
            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            var visible = 0;

            foreach (var renderer in renderers)
            {
                if (renderer.isVisible)
                {
                    visible++;
                }
            }

            Log.LogInfo($"[sonde] Renderer.isVisible (frustum seul, occultation exclue) : {visible}/{renderers.Length}");

            var builder = new StringBuilder();
            foreach (var cam in Camera.allCameras)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(
                    $"'{cam.name}' (depth={cam.depth}, masque=0x{cam.cullingMask:X}, near={cam.nearClipPlane:F2}, " +
                    $"far={cam.farClipPlane:F0}, cible={(cam.targetTexture == null ? "écran" : cam.targetTexture.name)})");
            }

            Log.LogInfo($"[sonde] caméras actives : {(builder.Length == 0 ? "(aucune)" : builder.ToString())}");
        }

        /// <summary>
        /// Dessine la copie **seule**, avec notre propre caméra, sur un fond magenta.
        ///
        /// Deux garanties rendent cette mesure concluante là où la mesure dans le monde ne peut pas
        /// l'être : un calque dédié (aucune caméra du jeu ne le rend, donc rien ne peut l'occulter) et
        /// un fond magenta (un maillage sombre reste comptable — noir sur noir ne se mesure pas).
        /// Si la copie apparaît ici, alors sa géométrie, ses matériaux et son skinning fonctionnent, et
        /// le problème restant n'est plus qu'une question de placement et de réglage de caméra.
        /// </summary>
        private IEnumerator IsolatedRender(GameObject clone)
        {
            var probeLayer = FindProbeLayer();
            if (probeLayer < 0)
            {
                Log.LogError("[sonde] aucun calque libre pour le rendu isolé : mesure impossible.");
                yield break;
            }

            var moved = 0;
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                renderer.gameObject.layer = probeLayer;
                moved++;
            }

            Log.LogInfo($"[sonde] rendu isolé : {moved} renderer(s) sur le calque {probeLayer} ({LayerMask.LayerToName(probeLayer)}).");

            // Cadrage sur le **corps**, pas sur la boîte englobante : celle-ci est faussée (elle
            // annonce 4 m pour ce corps), et viser son centre plaçait le corps hors du cadre — ce qui
            // expliquait une couverture de 3,9 % au lieu de la dizaine attendue. Le relevé par maillage
            // montre que l'origine de la copie est à hauteur du haut du corps : le torse est 0,9 m
            // plus bas.
            var target = clone.transform.position - Vector3.up * 0.9f;

            var cameraObject = new GameObject("Coop.AvatarProbe.Camera");
            var probeCamera = cameraObject.AddComponent<Camera>();
            probeCamera.cullingMask = 1 << probeLayer;
            probeCamera.clearFlags = CameraClearFlags.SolidColor;
            probeCamera.backgroundColor = Color.magenta;
            probeCamera.nearClipPlane = 0.05f;
            probeCamera.farClipPlane = 200f;
            probeCamera.fieldOfView = 50f;
            cameraObject.transform.position = target + clone.transform.rotation * new Vector3(1.6f, 0.5f, 3.2f);
            cameraObject.transform.LookAt(target);

            var renderTexture = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32);
            renderTexture.Create();
            probeCamera.targetTexture = renderTexture;
            probeCamera.enabled = false;

            // Premier essai en rendu manuel (caméra désactivée : elle ne perturbe rien d'autre).
            probeCamera.Render();
            var coverage = ReadCoverage(renderTexture, out var texture, out var averageLuminance, out var blobWidth, out var blobHeight);

            if (coverage == 0)
            {
                // Si la caméra désactivée ne rend rien, on réessaie en la laissant rendre d'elle-même :
                // c'est le seul doute de cette méthode, autant le lever dans le même run.
                Log.LogWarning("[sonde] rendu manuel vide -> nouvelle tentative avec la caméra activée.");
                probeCamera.enabled = true;
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();
                coverage = ReadCoverage(renderTexture, out texture, out averageLuminance, out blobWidth, out blobHeight);
            }

            var pixels = renderTexture.width * renderTexture.height;
            Log.LogInfo(
                $"[sonde] rendu isolé : {coverage}/{pixels} pixels non-fond " +
                $"({(coverage * 100.0 / pixels):F1} %), luminance moyenne hors fond={averageLuminance:F0}/255, " +
                $"étendue du sujet={blobWidth}x{blobHeight} px sur {renderTexture.width}x{renderTexture.height}");

            if (texture != null)
            {
                var path = IsolatedProofPath();
                File.WriteAllBytes(path, texture.EncodeToPNG());
                Log.LogInfo($"[sonde] image isolée écrite : {new FileInfo(path).Length} octets -> {path}");
                Destroy(texture);
            }

            // Une couverture globale ne dit pas **ce qui** la produit : le corps entier vu petit, ou
            // trois maillages sur vingt-quatre. Les deux cas n'appellent pas le même travail, donc on
            // éteint chaque renderer à tour de rôle et on mesure ce qu'on perd.
            yield return MeasurePerRenderer(clone, probeCamera, renderTexture);

            // L'avatar doit pouvoir prendre une pose (nager, marcher, rester immobile) : si l'`Animator`
            // conservé sur la copie ne répond pas, il faut le savoir maintenant et pas au moment où le
            // réseau enverra un état.
            yield return AnimatorPoseTest(clone);

            // La copie repasse sur le calque du monde : c'est l'état qu'aurait un vrai avatar distant.
            RemoteAvatarFactory.MoveRenderersToWorldLayer(clone);

            probeCamera.targetTexture = null;
            Destroy(cameraObject);
            renderTexture.Release();
            Destroy(renderTexture);
        }

        /// <summary>
        /// Dessine **chaque maillage tout seul**, l'un après l'autre, et relève ce qu'il donne.
        ///
        /// Mesurer la contribution d'un maillage dans la pile ne dit pas s'il se dessine : un maillage
        /// peut être caché par celui qui est devant. Ici, rien d'autre n'est dessiné, donc « 0 pixel »
        /// veut dire « ne se dessine pas », sans ambiguïté — et c'est exactement la question, puisque le
        /// corps rendu s'arrête au cou : les maillages de la tête sont-ils absents ou cachés ?
        /// </summary>
        private IEnumerator MeasurePerRenderer(GameObject clone, Camera probeCamera, RenderTexture renderTexture)
        {
            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            var details = new StringBuilder();
            var drawn = 0;

            for (var index = 0; index < renderers.Length; index++)
            {
                var target = renderers[index];
                if (target == null)
                {
                    continue;
                }
                target.enabled = true;

                foreach (var other in renderers)
                {
                    other.enabled = other == target;
                }

                probeCamera.Render();
                var alone = ReadCoverage(renderTexture, out var texture, out _, out var width, out var height);

                if (texture != null)
                {
                    Destroy(texture);
                }

                var centre = target.bounds.center - clone.transform.position;

                if (alone > 20)
                {
                    drawn++;
                    details.Append($"\n      {alone,6} px  {width,3}x{height,3}  {MeshName(target)}" + BakedBounds(target));
                }
                else
                {
                    // Note : recréer le `SkinnedMeshRenderer` pour lever un état interne douteux a été
                    // essayé, et **fait tomber le jeu** (`NullReferenceException` pendant la remise en
                    // place). Cette piste est donc fermée, pas seulement infructueuse.
                    // Le détail est indispensable : « 0 pixel » a trois causes très différentes — le
                    // maillage est ailleurs, il est dégénéré, ou le moteur refuse de le dessiner. Sans
                    // ces valeurs on réparerait au hasard (la correction des bornes a déjà été faite
                    // pour rien, sur une hypothèse qui n'a pas survécu à la mesure).
                    details.Append(
                        $"\n            -  {MeshName(target)} : centre={centre.ToString("F2")}, taille={target.bounds.size.ToString("F2")}, " +
                        $"isVisible={target.isVisible}, actifHierarchie={target.gameObject.activeInHierarchy}, " +
                        $"shader={ShaderNames(target)}" + BoneExtent(target, clone) + BakedBounds(target) + TestWithPlainMaterial(target, probeCamera, renderTexture));
                }

                yield return null;
            }

            foreach (var renderer in renderers)
            {
                renderer.enabled = true;
            }

            Log.LogInfo(
                $"[sonde] chaque maillage seul : {drawn}/{renderers.Length} se dessinent" + details);
        }

        /// <summary>
        /// Ouvre chaque renderer de la copie : boîte englobante par rapport à la copie, nombre d'os, et
        /// **où pointent ces os**.
        ///
        /// C'est la question que la couverture globale ne peut pas trancher. Un `SkinnedMeshRenderer`
        /// déforme son maillage avec ses os (`bones`, `rootBone`) : si ces références pointent encore
        /// vers le squelette du joueur d'origine et non vers ceux de la copie, les maillages concernés ne
        /// suivent plus la copie du tout. Mesurer leur nombre et leur emplacement évite de réparer à
        /// l'aveugle ce qui n'est peut-être pas cassé.
        /// </summary>
        private static void LogRendererAnatomy(GameObject clone)
        {
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                var centre = renderer.bounds.center - clone.transform.position;
                var line = $"[sonde] anatomie {MeshName(renderer)} : actif={renderer.enabled}, " +
                           $"centre={centre.ToString("F2")}, taille={renderer.bounds.size.ToString("F2")}";

                if (renderer is SkinnedMeshRenderer skinned)
                {
                    var bones = skinned.bones;
                    var inside = 0;
                    if (bones != null)
                    {
                        foreach (var bone in bones)
                        {
                            if (bone != null && bone.IsChildOf(clone.transform))
                            {
                                inside++;
                            }
                        }
                    }

                    var root = skinned.rootBone;
                    var rootState = root == null
                        ? "aucun"
                        : (root.IsChildOf(clone.transform) ? "dans la copie" : $"AILLEURS ({RemoteAvatarFactory.Path(root)})");

                    line += $", os={(bones == null ? 0 : bones.Length)} dont {inside} dans la copie, rootBone={rootState}";
                }

                Log.LogInfo(line);
            }
        }

        /// <summary>
        /// Redessine le maillage **avec un matériau simple**, et dit s'il apparaît.
        ///
        /// Dernier recours quand tout le reste est en règle : le maillage est bon (vérifié en le faisant
        /// cuire), le renderer est actif, ses bornes sont saines, son calque est dans le masque de la
        /// caméra — et il ne donne rien. Il ne reste alors que la façon dont il est dessiné. Si le
        /// maillage apparaît avec un matériau neuf, le problème est dans le matériau d'origine ; s'il
        /// n'apparaît pas non plus, c'est le renderer lui-même, et il faut chercher ailleurs.
        /// </summary>
        private static string TestWithPlainMaterial(Renderer renderer, Camera probeCamera, RenderTexture renderTexture)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (shader == null)
            {
                return ", matériau de test impossible (aucun shader simple trouvé)";
            }

            var saved = renderer.sharedMaterials;
            var plain = new Material(shader) { color = Color.green };
            var count = 1;

            if (saved != null && saved.Length > 1)
            {
                count = saved.Length;
            }

            var replacement = new Material[count];
            for (var i = 0; i < count; i++)
            {
                replacement[i] = plain;
            }

            try
            {
                renderer.sharedMaterials = replacement;
                probeCamera.Render();
                var withPlain = ReadCoverage(renderTexture, out var texture, out _, out _, out _);

                if (texture != null)
                {
                    Destroy(texture);
                }

                return $", avec un matériau simple : {withPlain} px";
            }
            finally
            {
                renderer.sharedMaterials = saved;
                Destroy(plain);
            }
        }

        /// <summary>
        /// Ce que le skinning produit réellement, en demandant à Unity de cuire le maillage.
        ///
        /// Les bornes déclarées mesurent ce que le moteur *croit*, `BakeMesh` mesure ce qu'il *calcule*.
        /// Un maillage dont les sommets cuits se réduisent à un point ne se dessinera jamais, quels que
        /// soient ses bornes, son matériau ou son activation : c'est la seule mesure qui sépare
        /// « dégénéré » de « dessiné ailleurs ».
        /// </summary>
        private static string BakedBounds(Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer skinned) || skinned.sharedMesh == null)
            {
                return string.Empty;
            }

            var baked = new Mesh();
            try
            {
                skinned.BakeMesh(baked);
            }
            catch (System.Exception exception)
            {
                Object.Destroy(baked);
                return $", cuisson impossible ({exception.GetType().Name})";
            }

            var size = baked.bounds.size;
            var center = baked.bounds.center;

            // Le nombre de triangles est décisif : un maillage avec des sommets mais **aucun triangle**
            // a des bornes justes et ne se dessine jamais, quel que soit son matériau, ses os, son calque
            // ou son activation. `Mesh.triangles` est interdit sur le maillage du jeu, mais le maillage
            // cuit est à nous — c'est donc ici, et seulement ici, qu'on peut le compter.
            var indices = new StringBuilder();
            var total = 0;

            for (var i = 0; i < baked.subMeshCount; i++)
            {
                var count = (int)baked.GetIndexCount(i);
                total += count;
                indices.Append(i == 0 ? $" {count}" : $"+{count}");
            }

            var vertices = baked.vertexCount;
            var subMeshes = baked.subMeshCount;
            Object.Destroy(baked);

            return $", cuit : centre={center.ToString("F3")}, taille={size.ToString("F3")}, " +
                   $"sommets={vertices}, sous-maillages={subMeshes}, indices={total} ({indices.ToString().Trim()})";
        }

        /// <summary>
        /// Où sont les os d'un maillage, et sur quelle étendue.
        ///
        /// Un maillage squelettique dont les os sont tous au même point se réduit à rien : il est
        /// actif, il a un maillage, un matériau, des bornes correctes, et il ne dessine aucun pixel.
        /// C'est la seule différence mesurable entre « il ne peut pas se dessiner » et « il est
        /// dessiné ailleurs ».
        /// </summary>
        private static string BoneExtent(Renderer renderer, GameObject clone)
        {
            if (!(renderer is SkinnedMeshRenderer skinned) || skinned.bones == null || skinned.bones.Length == 0)
            {
                return string.Empty;
            }

            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var count = 0;

            foreach (var bone in skinned.bones)
            {
                if (bone == null)
                {
                    continue;
                }

                var local = bone.position - clone.transform.position;
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
                count++;
            }

            if (count == 0)
            {
                return ", aucun os valide";
            }

            return $", {count} os de {min.ToString("F2")} à {max.ToString("F2")}, étendue={(max - min).ToString("F2")}";
        }

        private static string ShaderNames(Renderer renderer)
        {
            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                return "aucun materiau";
            }

            var builder = new StringBuilder();
            foreach (var material in materials)
            {
                if (builder.Length > 0)
                {
                    builder.Append("/");
                }

                builder.Append(material == null ? "null" : (material.shader == null ? "sans shader" : material.shader.name));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Vérifie que l'`Animator` de la copie pilote réellement le squelette, et relève les paramètres
        /// exacts qu'il écoute.
        ///
        /// La mesure est directe : on cuit le maillage du corps (les sommets déformés par le skinning) et
        /// on calcule **le déplacement moyen des sommets** entre deux états. Un `Animator` inerte laisse ce
        /// chiffre à zéro, quels que soient les paramètres qu'on lui donne ; un `Animator` vivant le fait
        /// bouger. Relever les noms des paramètres est le deuxième livrable : c'est exactement ce que le
        /// réseau devra pousser pour donner une pose à un joueur distant.
        /// </summary>
        private IEnumerator AnimatorPoseTest(GameObject clone)
        {
            var animator = clone.GetComponentInChildren<Animator>();
            if (animator == null)
            {
                Log.LogError("[sonde] aucun Animator dans la copie : la pose ne sera pas pilotable.");
                yield break;
            }

            var controller = animator.runtimeAnimatorController;
            Log.LogInfo(
                $"[sonde] animator '{animator.name}', controller={(controller == null ? "aucun" : controller.name)}, " +
                $"{animator.parameters.Length} paramètre(s), mise à jour={(animator.updateMode)}");

            var body = FindBodyRenderer(clone);
            if (body == null)
            {
                Log.LogError("[sonde] aucun maillage de corps cuit : pose non mesurable.");
                yield break;
            }

            var reference = BakePose(body);
            yield return new WaitForSeconds(0.5f);
            var spontaneous = PoseDistance(reference, BakePose(body));

            Log.LogInfo(
                spontaneous < 0f
                    ? "[sonde] pose : mesure impossible (cuisson échouée)."
                    : $"[sonde] pose spontanée : {spontaneous:F4} m de déplacement moyen des sommets en 0,5 s " +
                      "(0 = squelette figé, donc animator qui ne tourne pas)");

            var reference2 = BakePose(body);
            var moved = 0;

            foreach (var parameter in animator.parameters)
            {
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        animator.SetBool(parameter.name, true);
                        break;
                    case AnimatorControllerParameterType.Float:
                        animator.SetFloat(parameter.name, 1f);
                        break;
                    case AnimatorControllerParameterType.Int:
                        animator.SetInteger(parameter.name, 1);
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        animator.SetTrigger(parameter.name);
                        break;
                    default:
                        continue;
                }

                animator.Update(0.25f);
                var distance = PoseDistance(reference2, BakePose(body));
                moved += distance > 0.002f ? 1 : 0;

                Log.LogInfo($"[sonde]   '{parameter.name}' ({parameter.type}) -> déplacement des sommets {distance:F4} m");
                yield return null;
            }

            Log.LogInfo(
                $"[sonde] pose : {moved}/{animator.parameters.Length} paramètre(s) déplacent réellement le squelette");
        }

        /// <summary>Le maillage qui porte le corps visible (le plus de sommets), pour mesurer la pose.</summary>
        private static SkinnedMeshRenderer FindBodyRenderer(GameObject clone)
        {
            SkinnedMeshRenderer best = null;

            foreach (var renderer in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null)
                {
                    continue;
                }

                if (best == null || renderer.sharedMesh.vertexCount > best.sharedMesh.vertexCount)
                {
                    best = renderer;
                }
            }

            return best;
        }

        /// <summary>Sommets déformés par le skinning, tels qu'Unity les calcule à cet instant.</summary>
        private static Vector3[] BakePose(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();

            try
            {
                renderer.BakeMesh(baked);
                return baked.vertices;
            }
            catch (System.Exception exception)
            {
                Log.LogWarning($"[sonde] cuisson impossible pour la pose : {exception.Message}");
                return null;
            }
            finally
            {
                Object.Destroy(baked);
            }
        }

        /// <summary>Déplacement moyen des sommets entre deux poses, en mètres.</summary>
        private static float PoseDistance(Vector3[] a, Vector3[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return -1f;
            }

            var total = 0f;
            for (var i = 0; i < a.Length; i++)
            {
                total += Vector3.Distance(a[i], b[i]);
            }

            return total / a.Length;
        }

        private static string MeshName(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                return skinned.sharedMesh.name;
            }

            if (renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
            {
                return filter.sharedMesh.name;
            }

            return renderer.GetType().Name;
        }

        /// <summary>
        /// Calque libre : ni nommé (les calques nommés appartiennent au jeu), ni présent dans le
        /// `cullingMask` d'une caméra du jeu — donc rien d'autre que notre caméra ne le dessine.
        /// </summary>
        private static int FindProbeLayer()
        {
            var main = Camera.main;

            for (var layer = 31; layer >= 3; layer--)
            {
                if (LayerMask.LayerToName(layer).Length != 0)
                {
                    continue;
                }

                if (main != null && (main.cullingMask & (1 << layer)) != 0)
                {
                    continue;
                }

                return layer;
            }

            return -1;
        }

        /// <summary>Boîte englobante de la copie, pour cadrer la caméra de mesure.</summary>
        private static Bounds ComputeBounds(GameObject clone)
        {
            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            var bounds = new Bounds(clone.transform.position, Vector3.zero);

            for (var i = 0; i < renderers.Length; i++)
            {
                if (i == 0)
                {
                    bounds = renderers[i].bounds;
                }
                else
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }

            return bounds;
        }

        /// <summary>
        /// Lit le rendu hors écran et compte les pixels qui ne sont pas du fond magenta.
        ///
        /// Renvoie aussi l'étendue du sujet, parce que la couverture seule ne distingue pas « tout le
        /// corps, petit » de « un morceau de corps, grand » — et ces deux cas appellent des corrections
        /// opposées. Un corps d'1,90 m vu à 3,6 m avec 50° de champ doit faire environ 290 px de haut :
        /// c'est cette valeur-là qu'on compare.
        /// </summary>
        private static long ReadCoverage(
            RenderTexture renderTexture,
            out Texture2D texture,
            out float averageLuminance,
            out int blobWidth,
            out int blobHeight)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = renderTexture;

            texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture.Apply();

            RenderTexture.active = previous;

            var pixels = texture.GetPixels32();
            long coverage = 0;
            long luminanceSum = 0;
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < pixels.Length; i++)
            {
                var difference =
                    System.Math.Abs(pixels[i].r - 255) +
                    System.Math.Abs(pixels[i].g - 0) +
                    System.Math.Abs(pixels[i].b - 255);

                if (difference <= PixelDifferenceThreshold)
                {
                    continue;
                }

                coverage++;
                luminanceSum += (pixels[i].r + pixels[i].g + pixels[i].b) / 3;

                var x = i % renderTexture.width;
                var y = i / renderTexture.width;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            averageLuminance = coverage == 0 ? 0f : luminanceSum / (float)coverage;
            blobWidth = coverage == 0 ? 0 : maxX - minX + 1;
            blobHeight = coverage == 0 ? 0 : maxY - minY + 1;

            return coverage;
        }

        private static Texture2D Capture() => ScreenCapture.CaptureScreenshotAsTexture();

        /// <summary>Médiane : robuste à un échantillon aberrant, contrairement à la moyenne et au maximum.</summary>
        private static double Median(long[] values)
        {
            var sorted = (long[])values.Clone();
            System.Array.Sort(sorted);

            var middle = sorted.Length / 2;
            return sorted.Length % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }

        /// <summary>Nombre de pixels dont la couleur a changé au-delà du seuil.</summary>
        private static long PixelDifference(Texture2D a, Texture2D b)
        {
            if (a == null || b == null || a.width != b.width || a.height != b.height)
            {
                Log.LogError("[sonde] clichés incomparables (taille différente ou capture nulle).");
                return -1;
            }

            if (a.format != TextureFormat.RGBA32 || b.format != TextureFormat.RGBA32)
            {
                Log.LogWarning($"[sonde] formats de clichés : {a.format} / {b.format} (attendus RGBA32).");
            }

            var pixelsA = a.GetPixels32();
            var pixelsB = b.GetPixels32();
            long changed = 0;

            for (var i = 0; i < pixelsA.Length; i++)
            {
                var difference =
                    System.Math.Abs(pixelsA[i].r - pixelsB[i].r) +
                    System.Math.Abs(pixelsA[i].g - pixelsB[i].g) +
                    System.Math.Abs(pixelsA[i].b - pixelsB[i].b);

                if (difference > PixelDifferenceThreshold)
                {
                    changed++;
                }
            }

            return changed;
        }

        /// <summary>
        /// Décrit la hiérarchie réelle du joueur : c'est ce qui manque au raisonnement par IL seul,
        /// car la structure d'un GameObject n'est écrite nulle part dans le code.
        /// </summary>
        private void DumpHierarchy(Transform root)
        {
            var output = new StringBuilder();
            var lines = 0;

            output.AppendLine($"[sonde] hiérarchie de '{root.name}' (profondeur max {MaxDumpDepth}) :");
            DumpNode(root, 0, output, ref lines);

            // Les renderers sont listés à part, sans limite de profondeur : ce sont eux qui décident
            // de ce qui est visible, et ils peuvent vivre loin sous le root.
            output.AppendLine("[sonde] renderers de tout le sous-arbre (toutes profondeurs) :");

            var found = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                found++;
                var mesh = MeshOf(renderer);
                output.AppendLine(
                    $"    {RemoteAvatarFactory.Path(renderer.transform)} : {renderer.GetType().Name} " +
                    $"actif={renderer.enabled} calque={LayerMask.LayerToName(renderer.gameObject.layer)} " +
                    $"mesh={(mesh == null ? "aucun" : mesh.name)} sommets={(mesh == null ? 0 : mesh.vertexCount)}");
            }

            if (found == 0)
            {
                output.AppendLine("    (aucun)");
            }

            Log.LogInfo(output.ToString());
        }

        private static void DumpNode(Transform node, int depth, StringBuilder output, ref int lines)
        {
            if (lines >= MaxDumpNodes)
            {
                if (lines == MaxDumpNodes)
                {
                    output.AppendLine("    ... (tronqué : trop de nœuds)");
                    lines++;
                }

                return;
            }

            lines++;

            var state = !node.gameObject.activeSelf
                ? "inactif"
                : (node.gameObject.activeInHierarchy ? "actif" : "actif mais parent inactif");

            output.AppendLine(
                $"    {new string(' ', depth * 4)}{node.name} [{state}, calque={LayerMask.LayerToName(node.gameObject.layer)}, " +
                $"localPos={node.localPosition.ToString("F2")}] composants:{DescribeComponents(node.gameObject)}");

            if (depth >= MaxDumpDepth)
            {
                return;
            }

            foreach (Transform child in node)
            {
                DumpNode(child, depth + 1, output, ref lines);
            }
        }

        private static string DescribeComponents(GameObject gameObject)
        {
            var builder = new StringBuilder();

            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    builder.Append(" <script manquant>");
                    continue;
                }

                var type = component.GetType();
                string detail;

                if (component is SkinnedMeshRenderer skinned)
                {
                    detail = $" mesh={(skinned.sharedMesh == null ? "aucun" : skinned.sharedMesh.name)}" +
                             $" matériaux={(skinned.sharedMaterials == null ? 0 : skinned.sharedMaterials.Length)}";
                }
                else if (component is MeshRenderer meshRenderer)
                {
                    detail = $" matériaux={(meshRenderer.sharedMaterials == null ? 0 : meshRenderer.sharedMaterials.Length)}";
                }
                else if (component is MeshFilter filter)
                {
                    detail = $" mesh={(filter.sharedMesh == null ? "aucun" : filter.sharedMesh.name)}";
                }
                else if (component is Animator animator)
                {
                    detail = $" controller={(animator.runtimeAnimatorController == null ? "aucun" : animator.runtimeAnimatorController.name)}" +
                             $" avatar={(animator.avatar == null ? "aucun" : animator.avatar.name)}";
                }
                else if (component is Renderer renderer)
                {
                    detail = $" actif={renderer.enabled}";
                }
                else if (component is Collider collider)
                {
                    detail = $" trigger={collider.isTrigger}";
                }
                else if (component is Rigidbody rigidbody)
                {
                    detail = $" kinematic={rigidbody.isKinematic}";
                }
                else
                {
                    detail = string.Empty;
                }

                builder.Append($" [{type.Name}{detail}]");
            }

            return builder.ToString();
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                return skinned.sharedMesh;
            }

            return renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
        }

        /// <summary>
        /// La capture va dans le dossier de logs, à côté du log de l'instance : c'est là que tout ce
        /// qui concerne une exécution est déjà rangé, et c'est un dossier qu'on sait retrouver.
        /// </summary>
        private static string ScreenshotPath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BepInEx", "logs", "coop-avatar-proof.png"));

        /// <summary>Image de la copie seule, sur fond magenta, prise par notre propre caméra.</summary>
        private static string IsolatedProofPath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BepInEx", "logs", "coop-avatar-isolated.png"));
    }
}
