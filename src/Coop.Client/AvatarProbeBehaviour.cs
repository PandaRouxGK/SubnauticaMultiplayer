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
            var shots = new Texture2D[5];
            for (var i = 0; i < shots.Length; i++)
            {
                // Masquée, masquée, visible, visible, masquée : le bruit se mesure entre deux clichés du
                // **même** état, et le signal entre deux clichés d'états différents. Alterner à chaque
                // frame — l'erreur de la version précédente — met l'avatar dans chaque écart : les
                // quatre mesures disent alors la même chose (11 346, 11 797, 11 505, 11 434 px), et le
                // « bruit » annoncé est en réalité le signal, ce qui faisait échouer un avatar qui rend.
                clone.SetActive(i == 2 || i == 3);
                yield return new WaitForEndOfFrame();
                shots[i] = Capture();
            }

            clone.SetActive(true);

            // Signal : on prend le plus faible des deux (conservateur).
            // Bruit : on prend le plus fort des deux (conservateur aussi).
            var noise1 = PixelDifference(shots[0], shots[1]);   // masquée -> masquée
            var signal1 = PixelDifference(shots[1], shots[2]);  // masquée -> visible
            var noise2 = PixelDifference(shots[2], shots[3]);   // visible -> visible
            var signal2 = PixelDifference(shots[3], shots[4]);  // visible -> masquée

            var signal = System.Math.Min(signal1, signal2);
            var noise = System.Math.Max(noise1, noise2);
            var pixels = shots[0].width * shots[0].height;

            Log.LogInfo(
                $"[sonde] pixels de l'écran={pixels} — signal (changement d'état, une frame d'écart)={signal1} et {signal2} px, " +
                $"bruit (même état, même écart d'une frame)={noise1} et {noise2} px");

            // Deux exigences : un rapport franc au bruit **et** un plancher absolu (un avatar ne peut pas
            // tenir dans une poignée de pixels, même à distance).
            var threshold = System.Math.Max(noise * 4, 2000);
            var renders = signal > threshold;

            Log.LogInfo(
                $"[sonde] décision : signal retenu={signal} px, bruit retenu={noise} px, seuil={threshold} px " +
                $"(4x le bruit, plancher 2000) -> la copie rend={renders}");

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
            yield return MeasurePerRenderer(clone, probeCamera, renderTexture, coverage);

            // La copie repasse sur le calque du monde : c'est l'état qu'aurait un vrai avatar distant.
            RemoteAvatarFactory.MoveRenderersToWorldLayer(clone);

            probeCamera.targetTexture = null;
            Destroy(cameraObject);
            renderTexture.Release();
            Destroy(renderTexture);
        }

        /// <summary>
        /// Contribution de chaque renderer à l'image : on l'éteint, on redessine, on mesure la perte.
        ///
        /// C'est le seul moyen de savoir ce que « le corps rend » recouvre réellement. La boîte
        /// englobante de la copie mesure 5 m pour un corps de moins de 2 m, ce qui trahit des maillages
        /// qui partent à la dérive : il faut savoir combien dessinent vraiment, et lesquels.
        /// </summary>
        private IEnumerator MeasurePerRenderer(GameObject clone, Camera probeCamera, RenderTexture renderTexture, long fullCoverage)
        {
            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            var details = new StringBuilder();
            var contributing = 0;

            foreach (var renderer in renderers)
            {
                if (!renderer.enabled)
                {
                    continue;
                }

                renderer.enabled = false;
                probeCamera.Render();
                var withoutIt = ReadCoverage(renderTexture, out var texture, out _, out _, out _);
                renderer.enabled = true;

                if (texture != null)
                {
                    Destroy(texture);
                }

                var contribution = fullCoverage - withoutIt;
                if (contribution <= 50)
                {
                    continue;
                }

                contributing++;
                details.Append($"\n      {contribution,6} px  {MeshName(renderer)}");

                // Redessiner sans laisser la copie dans un état incohérent si une exception survient.
                yield return null;
            }

            Log.LogInfo(
                $"[sonde] maillages contribuant à l'image : {contributing}/{renderers.Length} " +
                $"(contribution > 50 px)" + details);
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
