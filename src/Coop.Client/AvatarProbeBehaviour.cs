using System.Collections;
using System.IO;
using System.Linq;
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
    ///
    /// Cette mesure-là dépend de la scène, donc elle se tait quand le bruit monte (mesuré : bruit médian
    /// dépassant le tiers du signal). Ce qui fait autorité sur « la copie est-elle dessinée », c'est le
    /// **rendu isolé** — la copie seule, sur fond magenta, avec notre caméra propre (voir
    /// <see cref="LogVisibilityVerdict"/>). Le témoin de scène corrobore ou reste muet, il ne conclut pas.
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

        /// <summary>
        /// Écart minimal (0-255) entre les couleurs relues de deux indices pour les compter comme
        /// différentes.
        ///
        /// 8/255 est au-dessus de l'arrondi d'un aller-retour couleur -> matériau -> rendu, et très en
        /// dessous de ce que la palette produit entre deux de ses entrées (le rouge et le cyan diffèrent
        /// de plus de 170). Un seuil plus haut laisserait passer deux teintes voisines pour identiques ;
        /// plus bas, il ferait échouer la mesure sur du bruit d'arrondi.
        /// </summary>
        private const int ColorDifferenceThreshold = 8;

        /// <summary>
        /// Écart (m) entre les deux corps de la preuve « deux avatars, deux couleurs ».
        ///
        /// La caméra du rendu isolé voit 3,36 m de large à la distance du corps (±1,68 m) : à 1,10 m du
        /// centre, le second corps reste entier dans l'image, sans recouvrir le premier.
        /// </summary>
        private const float TwoBodiesSpacingMeters = 1.1f;

        /// <summary>
        /// Demi-taille (px) de la région échantillonnée autour du torse d'un corps.
        ///
        /// Le torse fait ~76 px de large dans ce rendu ; ±24 px tombe donc dans le corps, ce qui est le
        /// but — mesurer **une** région par corps, pas la moyenne de toute l'image.
        /// </summary>
        private const int BodyRegionHalfSizePixels = 24;

        // — Résultat des deux mesures de visibilité, assemblées en un seul verdict —————————————
        //
        // Mesuré au run du 2026-10-17 : la sonde concluait « la copie ne change pas assez de pixels pour
        // être considérée comme rendue » alors que son propre rendu isolé, 17 ms plus tard, montrait la
        // copie dessinée sur 4,5 % de l'image. Un verdict qui nie ce qui vient d'être mesuré vaut moins
        // que pas de verdict : les deux mesures sont donc conservées, puis assemblées une fois connues.
        private bool _frameDifferenceRenders;
        private double _frameDifferenceSignal;
        private double _frameDifferenceNoise;
        private double _frameDifferenceThreshold;

        private bool _isolatedMeasured;
        private long _isolatedCoverage;
        private long _isolatedPixels;

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
        ///
        /// Cette mesure **ne conclut pas** que la copie ne s'affiche pas : elle dépend du bruit de la
        /// scène, et quand ce bruit monte elle devient muette. Elle est conservée telle quelle et c'est
        /// <see cref="LogVisibilityVerdict"/> qui tranche, une fois le rendu isolé connu.
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

            _frameDifferenceRenders = renders;
            _frameDifferenceSignal = signal;
            _frameDifferenceNoise = noise;
            _frameDifferenceThreshold = threshold;

            Log.LogInfo(
                $"[sonde] décision (différence d'images) : signal médian={signal:F0} px, bruit médian={noise:F0} px, " +
                $"seuil={threshold:F0} px (3x le bruit, plancher 2000) -> signal au-dessus du seuil={renders}");

            if (!renders)
            {
                // Pas un échec, une **mesure muette** : le bruit de la scène (eau, HUD) impose un seuil que le
                // signal ne dépasse pas. Cette mesure ne peut donc pas dire que rien ne s'affiche — elle dit
                // qu'ici elle ne voit pas la copie. Le rendu isolé, lui, ne dépend pas de la scène.
                Log.LogWarning(
                    $"[sonde] mesure par différence d'images NON CONCLUANTE : bruit médian de la scène={noise:F0} px, " +
                    $"signal médian={signal:F0} px, seuil={threshold:F0} px — le bruit de la scène masque le signal, " +
                    "donc cette mesure ne dit rien de la copie. Le rendu isolé tranche (voir le verdict de rendu).");
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

            // C'est **cette** mesure qui fait autorité sur « la copie est-elle dessinée » : elle ne dépend ni
            // de l'orientation du joueur, ni du bruit de la scène. Le verdict est assemblé ici, quand les deux
            // mesures sont connues, et non au moment où la première tombe.
            _isolatedMeasured = true;
            _isolatedCoverage = coverage;
            _isolatedPixels = pixels;
            LogVisibilityVerdict();

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

            // La copie doit pouvoir porter **la** tenue de son joueur et pas celle du joueur local :
            // c'est la différence la plus visible entre deux joueurs, et elle se mesure (silhouette,
            // pixels) au lieu de se croire.
            yield return MeasureEquipmentScenarios(clone, probeCamera, renderTexture);

            // Deux joueurs en scaphandre de base sont identiques : la couleur est ce qui les distingue,
            // et elle se mesure sur le rendu, pas sur le setter qu'on vient d'appeler.
            yield return MeasureColorScenarios(clone, probeCamera, renderTexture);

            // Et la question que se pose vraiment un joueur : deux avatars **côte à côte**, deux couleurs,
            // dans une seule image. La phase ci-dessus ne la pose pas (elle mesure la même copie deux fois).
            yield return MeasureTwoColorProof(clone, probeCamera, renderTexture);

            // L'aller-retour complet : lire l'équipement local, le transmettre sous forme de noms, puis
            // l'appliquer à la copie — et vérifier que le voyage en noms ne perd rien.
            yield return MeasureLocalEquipmentRoundTrip(clone);

            // La copie repasse sur le calque du monde : c'est l'état qu'aurait un vrai avatar distant.
            RemoteAvatarFactory.MoveRenderersToWorldLayer(clone);

            probeCamera.targetTexture = null;
            Destroy(cameraObject);
            renderTexture.Release();
            Destroy(renderTexture);
        }

        /// <summary>
        /// Fait le tour complet du chemin que le réseau empruntera : **lire** l'équipement du joueur
        /// local, le passer en noms, l'appliquer à la copie — et vérifier que ce voyage en noms ne perd
        /// rien.
        ///
        /// **La référence a changé, et c'est une mesure qui l'a imposé.** La première version comparait la
        /// copie à l'**état brut du modèle local**, et elle rendait `verdict=False` à chaque run : relevé du
        /// 2026-10-17, `0 pièce(s) lue(s), 10 nœud(s) actif(s) chez le joueur local contre 3 sur la copie`,
        /// 7 nœuds « manquants » dont les porteurs de palmes, de bouteille et de détendeur. Ce n'était pas
        /// un défaut des règles : le jeu pilote une partie des nœuds du modèle local par d'autres moyens que
        /// l'équipement (`ConditionRules`, `ArmsController`). Comparer à cet état ne testait donc rien — et
        /// un verdict toujours faux est pire qu'aucun verdict : il fait ignorer les vrais (c'est le harnais
        /// `tools/run-session.sh`, qui fait échouer un run sur `verdict=False`, qui l'a démasqué).
        ///
        /// L'état brut du modèle local reste **journalisé comme observation**, avec sa raison de ne pas
        /// conclure : les porteurs actifs alors que rien n'est porté restent une information à suivre.
        ///
        /// Trois contrôles, chacun falsifiable :
        ///
        /// 1. **le canal des noms ne perd rien** : sur l'ensemble des pièces que ces règles connaissent,
        ///    `ApplyNames(noms)` doit donner exactement l'état de `Apply(types)`. Un `Enum.TryParse` qui
        ///    échouerait sur un nom du jeu ferait disparaître une pièce du voyage, et c'est exactement ce
        ///    que ce contrôle attrape ;
        /// 2. **le contrôle lui-même peut échouer** : la même liste avec un nom corrompu (`"X" + nom`) doit
        ///    donner **autre chose**. Sans ça, l'égalité du point 1 pourrait être vraie pour tout et ne rien
        ///    prouver — un contrôle qu'on n'a jamais vu broncher n'est pas un contrôle ;
        /// 3. **la lecture locale ne perd rien** : autant de noms que de `TechType` lus, et une lecture qui
        ///    a échoué ne passe pas pour un succès.
        /// </summary>
        private IEnumerator MeasureLocalEquipmentRoundTrip(GameObject clone)
        {
            Log.LogInfo("[sonde] --- aller-retour : le voyage en noms ne doit rien perdre ---");

            // Ce que le réseau transportera vraiment (les noms, tels que le plugin les produit) et la
            // référence correspondante, lue dans la même passe silencieuse pour ne pas journaliser deux
            // fois la même ligne.
            var names = RemoteAvatarEquipment.ReadLocalEquipment(out var failure, logResult: false);
            var types = RemoteAvatarEquipment.ReadLocalEquipmentTypes(out var typesFailure, logResult: true);
            if (failure != null)
            {
                Log.LogWarning($"[sonde] aller-retour d'équipement : lecture des noms impossible ({failure}).");
            }

            // Observation, pas critère : ce que le jeu laisse actif sur le modèle local.
            var sourceBody = Player.mainObject != null
                ? RemoteAvatarFactory.FindBodyRoot(Player.mainObject.transform)
                : null;
            if (sourceBody != null)
            {
                var sourceNodes = RemoteAvatarEquipment.ActiveGovernedNodes(sourceBody.gameObject);
                Log.LogInfo(
                    $"[sonde] aller-retour d'équipement : observation (hors critère) — {sourceNodes.Count} nœud(s) " +
                    $"actif(s) sur le modèle local pour {(types.Count == 0 ? "rien de porté" : $"{types.Count} pièce(s)")} : " +
                    $"{string.Join(", ", sourceNodes)}. Le jeu en pilote une partie hors équipement " +
                    "(ConditionRules, ArmsController) : ce n'est pas une référence.");
            }

            // — 1. Le canal des noms, éprouvé sur l'ensemble des pièces que les règles savent appliquer.
            //     Un `ToString()` suivi d'un `Enum.TryParse` est exactement ce que fait le voyage réseau ;
            //     la liste est volontairement complète pour couvrir toutes les branches des règles.
            var probeTypes = new[]
            {
                TechType.RadiationSuit, TechType.RadiationHelmet, TechType.RadiationGloves,
                TechType.Tank, TechType.DoubleTank, TechType.HighCapacityTank, TechType.PlasteelTank,
                TechType.Fins, TechType.SwimChargeFins, TechType.UltraGlideFins, TechType.Rebreather,
                TechType.WaterFiltrationSuit,
                TechType.ReinforcedDiveSuit, TechType.ReinforcedGloves,
            };
            var probeNames = probeTypes.Select(type => type.ToString()).ToList();

            RemoteAvatarEquipment.Apply(clone, probeTypes);
            yield return new WaitForEndOfFrame();
            var viaTypes = RemoteAvatarEquipment.ActiveGovernedNodes(clone);

            RemoteAvatarEquipment.ApplyNames(clone, probeNames);
            yield return new WaitForEndOfFrame();
            var viaNames = RemoteAvatarEquipment.ActiveGovernedNodes(clone);

            var channelMissing = viaTypes.Where(node => !viaNames.Contains(node)).ToList();
            var channelExtra = viaNames.Where(node => !viaTypes.Contains(node)).ToList();
            var channelIntact = channelMissing.Count == 0 && channelExtra.Count == 0;

            Log.LogInfo(
                $"[sonde] aller-retour d'équipement : {probeTypes.Length} pièce(s) passée(s) en noms -> " +
                $"{probeNames.Count} nom(s), {viaTypes.Count} nœud(s) actif(s) par les types contre {viaNames.Count} " +
                $"par les noms" +
                (channelIntact
                    ? " (mêmes nœuds)"
                    : $" — {channelMissing.Count} manquant(s) [{string.Join(", ", channelMissing)}], " +
                      $"{channelExtra.Count} en trop [{string.Join(", ", channelExtra)}]") +
                $" -> verdict={channelIntact}");

            // — 2. Le contrôle doit pouvoir échouer : les mêmes noms, tous corrompus, doivent donner
            //     l'état sans équipement — sinon l'égalité ci-dessus est vraie par construction.
            //     Les avertissements « nom de TechType inconnu » de cet appel sont **attendus** ici.
            var corrupted = probeNames.Select(name => "X" + name).ToList();
            RemoteAvatarEquipment.ApplyNames(clone, corrupted);
            yield return new WaitForEndOfFrame();
            var viaCorrupted = RemoteAvatarEquipment.ActiveGovernedNodes(clone);
            var discriminates = !channelIntact
                || viaCorrupted.Count != viaTypes.Count
                || viaCorrupted.Any(node => !viaTypes.Contains(node));

            Log.LogInfo(
                $"[sonde] aller-retour d'équipement : discrimination du contrôle — {corrupted.Count} nom(s) " +
                $"corrompu(s) donnent {viaCorrupted.Count} nœud(s) actif(s) contre {viaTypes.Count} pour les noms " +
                $"intacts -> verdict={discriminates} (un contrôle qui ne peut pas échouer ne prouve rien)");

            // — 3. La lecture locale : la conversion en noms ne doit rien perdre, et un échec de lecture ne
            //     doit pas passer pour un succès (une liste vide des deux côtés serait « égale »).
            var readLossless = typesFailure == null && names.Count == types.Count;

            Log.LogInfo(
                $"[sonde] aller-retour d'équipement : lecture locale — {names.Count} nom(s) pour {types.Count} " +
                $"TechType lu(s)" + (typesFailure == null ? string.Empty : $", lecture en échec ({typesFailure})") +
                $" -> verdict={readLossless}");

            var verdict = channelIntact && discriminates && readLossless;
            Log.LogInfo($"[sonde] aller-retour d'équipement : verdict={verdict}");

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] aller-retour d'équipement : le voyage en noms ne reproduit pas l'équipement appliqué en " +
                    "types, ou le contrôle ne discrimine pas, ou la lecture locale a échoué — un avatar distant " +
                    "porterait une tenue fausse.");
            }

            RemoteAvatarEquipment.Apply(clone, new TechType[0]);
            yield return new WaitForEndOfFrame();
        }

        /// <summary>
        /// Mesure la tenue de l'avatar sous plusieurs équipements, en appliquant les règles du jeu.
        ///
        /// Ce que ce test doit établir, et qu'aucune capture ne montrerait seule :
        ///
        /// - la tenue **change** de composition selon l'équipement reçu (nombre de nœuds actifs, pixels) ;
        /// - un nœud piloté qui doit être masqué l'est **vraiment**, et un nœud attendu visible l'est
        ///   aussi — un verdict explicite par scénario, pas une impression de capture ;
        /// - rien ne lève d'exception sur ces chemins-là, y compris pour un nœud absent.
        ///
        /// C'est aussi la répétition générale de ce que le réseau fera : chaque scénario appelle la même
        /// méthode que celle qu'un paquet d'équipement déclenchera.
        /// </summary>
        private IEnumerator MeasureEquipmentScenarios(GameObject clone, Camera probeCamera, RenderTexture renderTexture)
        {
            Log.LogInfo("[sonde] --- tenue : la copie doit porter l'équipement de son joueur ---");

            var scenarios = new (string Key, TechType[] Equipment, string Visible, string Hidden)[]
            {
                ("sans-equipement", new TechType[0],
                    RemoteAvatarEquipment.DiveSuitBody, RemoteAvatarEquipment.RadiationSuit),
                ("combinaison-anti-radiations",
                    new[] { TechType.RadiationSuit, TechType.RadiationHelmet, TechType.RadiationGloves, TechType.Tank },
                    RemoteAvatarEquipment.RadiationSuit, RemoteAvatarEquipment.DiveSuitBody),
                ("combinaison-de-survie",
                    new[] { TechType.WaterFiltrationSuit },
                    RemoteAvatarEquipment.StillSuit, RemoteAvatarEquipment.DiveSuitBody),
                ("combinaison-renforcee",
                    new[] { TechType.ReinforcedDiveSuit, TechType.ReinforcedGloves },
                    RemoteAvatarEquipment.ReinforcedSuit, RemoteAvatarEquipment.DiveSuitHands),
                ("palmes-detendeur-bouteille",
                    new[] { TechType.Fins, TechType.Rebreather, TechType.Tank },
                    RemoteAvatarEquipment.Fins, RemoteAvatarEquipment.DiveSuitHead),
            };

            foreach (var scenario in scenarios)
            {
                RemoteAvatarEquipment.Apply(clone, scenario.Equipment);

                // Une frame au moins : les bornes des maillages squelettiques sont recalculées depuis la
                // pose réelle, et une tenue qu'on vient de montrer n'est dessinable qu'après.
                yield return new WaitForEndOfFrame();

                probeCamera.Render();
                var coverage = ReadCoverage(renderTexture, out var texture, out var luminance, out var width, out var height);

                var expectedVisible = RemoteAvatarEquipment.NodeActive(clone, scenario.Visible);
                var expectedHidden = RemoteAvatarEquipment.NodeActive(clone, scenario.Hidden);
                var active = RemoteAvatarEquipment.ActiveGovernedNodes(clone);

                // `null` (nœud introuvable) n'est pas `false` : on refuse de compter un chemin perdu
                // comme une tenue correcte.
                var verdict = expectedVisible == true && expectedHidden == false;

                Log.LogInfo(
                    $"[sonde] tenue '{scenario.Key}' : {coverage} px ({width}x{height} px sur " +
                    $"{renderTexture.width}x{renderTexture.height}, luminance {luminance:F0}/255), " +
                    $"{active.Count} nœud(s) actif(s) : {string.Join(", ", active)}");
                Log.LogInfo(
                    $"[sonde] tenue '{scenario.Key}' : attendu visible '{Leaf(scenario.Visible)}'=" +
                    $"{Format(expectedVisible)}, attendu masqué '{Leaf(scenario.Hidden)}'={Format(expectedHidden)} " +
                    $"-> verdict={verdict}");

                if (!verdict)
                {
                    Log.LogError($"[sonde] tenue '{scenario.Key}' : composition incorrecte après application des règles.");
                }

                if (texture != null)
                {
                    var path = EquipmentProofPath(scenario.Key);
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                    Log.LogInfo($"[sonde] tenue '{scenario.Key}' : image écrite -> {path}");
                    Destroy(texture);
                }
            }

            // On laisse l'avatar dans l'état par défaut : la dernière tenue mesurée ne doit pas rester
            // affichée comme si elle avait été décidée par le réseau.
            RemoteAvatarEquipment.Apply(clone, new TechType[0]);
            yield return new WaitForEndOfFrame();

            // Les nœuds gouvernés hors `male_geo` (l'objet tenu) ont leur propre relevé, plus haut.
            Log.LogInfo($"[sonde] tenue : règles appliquées sans exception sur {scenarios.Length} scénario(s).");
        }

        /// <summary>
        /// Mesure la couleur de joueur sur la copie, et **prouve** que deux indices différents donnent
        /// deux apparences différentes.
        ///
        /// Le sujet a été relevé avant d'écrire la moindre ligne de coloration : Subnautica n'a pas de
        /// couleur de joueur (aucun champ ni propriété de couleur dans `Player`), c'est Nitrox qui
        /// l'invente. C'est pourquoi « j'ai appelé un setter » ne doit jamais être confondu avec « la
        /// copie a changé » : un nom de propriété de shader qui n'existerait pas laisserait le matériau
        /// intact, et l'avatar distant resterait gris. D'où deux lectures indépendantes :
        ///
        /// - ce que les **matériaux** portent après application (`RemoteAvatarColor.Describe`) ;
        /// - ce que le **rendu** montre (moyenne RVB des pixels du sujet, fond exclu).
        ///
        /// Le verdict porte sur le rendu quand il est mesurable, parce que c'est la seule des deux qui
        /// dit ce qu'un joueur verrait ; il retombe sur les matériaux sinon, **en le disant** plutôt
        /// qu'en faisant passer une mesure manquée pour un succès.
        /// </summary>
        private IEnumerator MeasureColorScenarios(GameObject clone, Camera probeCamera, RenderTexture renderTexture)
        {
            Log.LogInfo("[sonde] --- couleur : deux joueurs en scaphandre de base doivent se distinguer ---");

            byte[] indices = { 0, 1 };
            var reports = new string[indices.Length];
            var readBacks = new string[indices.Length];
            var materialColors = new Color?[indices.Length];
            var averages = new Color[indices.Length];
            var coverages = new long[indices.Length];
            var luminances = new float[indices.Length];

            for (var i = 0; i < indices.Length; i++)
            {
                var index = indices[i];
                reports[i] = RemoteAvatarColor.Apply(clone, index);

                // Une frame au moins : changer la couleur d'un matériau ne prend effet qu'au rendu qui
                // suit, et mesurer avant reviendrait à mesurer la couleur précédente.
                yield return new WaitForEndOfFrame();

                readBacks[i] = RemoteAvatarColor.Describe(clone);
                materialColors[i] = RemoteAvatarColor.AverageTint(clone);

                probeCamera.Render();
                coverages[i] = ReadSubjectColor(renderTexture, out averages[i], out luminances[i], out var texture);

                Log.LogInfo($"[sonde] couleur {index} : {reports[i]}");
                Log.LogInfo($"[sonde] couleur {index} : matériaux relus -> {readBacks[i]}");
                Log.LogInfo(
                    $"[sonde] couleur {index} : rendu isolé {coverages[i]} px, moyenne RVB du sujet=" +
                    $"({averages[i].r * 255f:F0}, {averages[i].g * 255f:F0}, {averages[i].b * 255f:F0}), " +
                    $"luminance={luminances[i]:F0}/255");

                if (texture != null)
                {
                    var path = ColorProofPath(index);
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                    Log.LogInfo($"[sonde] couleur {index} : image écrite -> {path}");
                    Destroy(texture);
                }
            }

            var pixelDelta = ComponentDelta(averages[0], averages[1]);
            var hueDelta = HueDelta(averages[0], averages[1]);
            var materialDelta = materialColors[0].HasValue && materialColors[1].HasValue
                ? ComponentDelta(materialColors[0].Value, materialColors[1].Value)
                : -1;
            var rendered = coverages[0] > 0 && coverages[1] > 0;

            // Deux mesures, parce qu'une seule ne suffit pas : sur les moyennes brutes, un sujet
            // sombre écrase l'écart de teinte jusqu'à le rendre invisible (voir `HueDelta`).
            var verdict = rendered
                ? pixelDelta >= ColorDifferenceThreshold || hueDelta >= ColorDifferenceThreshold
                : materialDelta >= ColorDifferenceThreshold;

            Log.LogInfo(
                $"[sonde] couleur : écart maximal entre l'indice 0 et l'indice 1 = {pixelDelta}/255 sur les " +
                $"moyennes RVB du rendu, {hueDelta}/255 de teinte à luminance égale" +
                (materialDelta >= 0 ? $", {materialDelta}/255 sur les matériaux" : ", matériaux illisibles") +
                $" (seuil {ColorDifferenceThreshold}) -> verdict={verdict}");

            if (!rendered)
            {
                Log.LogWarning(
                    "[sonde] couleur : au moins un des deux rendus ne produit aucun pixel — le verdict porte " +
                    "donc sur les matériaux, pas sur ce qu'un joueur verrait.");
            }

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] couleur : les indices 0 et 1 donnent la même apparence — deux joueurs en " +
                    "scaphandre de base resteraient indiscernables.");
            }

            // L'avatar revient à un état défini (indice 0) : la dernière couleur mesurée ne doit pas
            // rester affichée comme si le réseau l'avait choisie.
            RemoteAvatarColor.Apply(clone, 0);
            yield return new WaitForEndOfFrame();
        }

        /// <summary>
        /// Écart maximal (0-255) entre deux couleurs, composante par composante.
        ///
        /// Pourquoi un maximum et pas une moyenne : la palette est faite de teintes franches, et c'est
        /// la composante la plus éloignée qui dit si l'œil verra la différence. Une moyenne diluerait
        /// par exemple un rouge (255, 82, 82) et un rose (255, 64, 129) en un écart moyen modeste alors
        /// que les deux tons se distinguent nettement.
        /// </summary>
        private static int ComponentDelta(Color a, Color b) =>
            Mathf.Max(
                Mathf.Abs(Mathf.RoundToInt(a.r * 255f) - Mathf.RoundToInt(b.r * 255f)),
                Mathf.Max(
                    Mathf.Abs(Mathf.RoundToInt(a.g * 255f) - Mathf.RoundToInt(b.g * 255f)),
                    Mathf.Abs(Mathf.RoundToInt(a.b * 255f) - Mathf.RoundToInt(b.b * 255f))));

        /// <summary>
        /// Écart maximal (0-255) entre deux couleurs **ramenées à la même luminance** : la teinte
        /// seule, débarrassée de l'exposition du rendu.
        ///
        /// Pourquoi cette seconde mesure existe : la sonde rend la copie dans l'éclairage du monde, et
        /// le relevé du 2026-10-17 mesurait un sujet à `14/255` de luminance. Sur un sujet aussi sombre,
        /// la teinte est **multipliée** par l'éclairage : le rouge `(255,82,82)` et l'orange
        /// `(255,152,0)` de la palette ne se séparent plus que de `4,5/255` en moyenne brute, alors
        /// qu'à luminance égale ils se séparent de `23/255` — et, sur les mêmes pixels, 15,2 % d'entre
        /// eux (1 704 sur 11 188) changent de plus de `8/255`, jusqu'à `32/255`. Juger sur les moyennes
        /// brutes seules conclurait « même apparence » sur deux teintes que l'œil distingue.
        ///
        /// Elle reste falsifiable : si la propriété de couleur n'existait pas sur le shader, les deux
        /// rendus seraient identiques et cet écart vaudrait `0`.
        /// </summary>
        private static int HueDelta(Color a, Color b) => ComponentDelta(NormalizeHue(a), NormalizeHue(b));

        /// <summary>
        /// Ramène une couleur à une luminance unité : garde les rapports entre canaux (la teinte),
        /// jette le niveau (l'exposition). Une couleur éteinte n'a pas de teinte : elle est rendue telle
        /// quelle, sans division par zéro.
        /// </summary>
        private static Color NormalizeHue(Color color)
        {
            var total = color.r + color.g + color.b;
            if (total <= 0f)
            {
                return color;
            }

            return new Color(color.r / total, color.g / total, color.b / total, 1f);
        }

        /// <summary>
        /// Verdict de visibilité, assemblé à partir des **deux** mesures — parce qu'aucune des deux ne
        /// suffit, et que les confondre a déjà produit un faux échec.
        ///
        /// Le rendu isolé fait autorité : la copie seule, sur fond magenta, par notre propre caméra. Il ne
        /// dépend ni de l'endroit où le joueur regarde, ni du bruit de la scène. La mesure par différence
        /// d'images, elle, dit ce que la copie change **à l'écran du joueur** quand elle est concluante,
        /// et se tait quand le bruit monte.
        ///
        /// Mesuré au run du 2026-10-17 : la sonde concluait « la copie ne change pas assez de pixels pour
        /// être considérée comme rendue » alors que son propre rendu isolé, 17 ms plus tard, montrait la
        /// copie dessinée (`11742/262144 px`, 4,5 %). Le faux échec venait de la mesure de scène seule, qui
        /// n'avait pas le droit de conclure cela.
        /// </summary>
        private void LogVisibilityVerdict()
        {
            if (!_isolatedMeasured)
            {
                Log.LogError(
                    "[sonde] RÉSULTAT ÉCHEC : aucune mesure de rendu isolé — la visibilité de la copie reste " +
                    "NON PROUVÉE (le rendu isolé n'a pas pu être fait).");
                return;
            }

            if (_isolatedCoverage <= 0)
            {
                var frameDifference = _frameDifferenceRenders
                    ? $"la mesure par différence d'images donnait, elle, un signal de {_frameDifferenceSignal:F0} px " +
                      $"(bruit médian {_frameDifferenceNoise:F0} px) : les deux mesures se CONTREDISENT"
                    : $"la mesure par différence d'images ne la voyait pas non plus (signal {_frameDifferenceSignal:F0} px, " +
                      $"bruit médian {_frameDifferenceNoise:F0} px)";

                Log.LogError(
                    $"[sonde] RÉSULTAT ÉCHEC : le rendu isolé ne montre aucun pixel de la copie (0/{_isolatedPixels}) et " +
                    $"{frameDifference} — la copie existe, mais rien ne s'affiche dans un rendu qui ne dépend que d'elle.");
                return;
            }

            var coveragePercent = _isolatedCoverage * 100.0 / _isolatedPixels;
            var corroboration = _frameDifferenceRenders
                ? $"corrobore (signal {_frameDifferenceSignal:F0} px pour un seuil de {_frameDifferenceThreshold:F0} px)"
                : $"reste muette, bruit médian={_frameDifferenceNoise:F0} px contre un signal de {_frameDifferenceSignal:F0} px " +
                  $"(seuil {_frameDifferenceThreshold:F0} px) — muette, pas contradictoire";

            Log.LogInfo(
                $"[sonde] verdict de rendu : la copie EST dessinée — rendu isolé {_isolatedCoverage}/{_isolatedPixels} px " +
                $"({coveragePercent:F1} %), mesuré seule sur fond magenta par notre caméra. Mesure par différence d'images : " +
                $"{corroboration}.");
        }

        /// <summary>
        /// Preuve à **deux corps** : deux avatars côte à côte, teintés par deux entrées différentes de la
        /// palette, mesurés dans **une seule image**.
        ///
        /// Ce que la phase de couleur seule ne pouvait pas dire : elle mesure la même copie avec deux
        /// indices, donc elle prouve qu'une teinte change — pas que **deux joueurs se distinguent côte à
        /// côte**, qui est la seule question que se pose un joueur devant l'écran. Ici les deux corps sont
        /// dans le même rendu, sur le même fond magenta, et chacun est mesuré dans **sa** région de l'image.
        ///
        /// Si un des deux corps n'est pas mesurable, le verdict est faux et il le dit : une preuve qui
        /// suppose visible ce qu'elle n'a pas compté ne prouve rien.
        /// </summary>
        private IEnumerator MeasureTwoColorProof(GameObject clone, Camera probeCamera, RenderTexture renderTexture)
        {
            var probeLayer = FindProbeLayer();
            if (probeLayer < 0)
            {
                Log.LogError("[sonde] deux avatars : aucun calque libre pour le rendu isolé — preuve impossible.");
                yield break;
            }

            // Même fabrique que la copie mesurée plus haut : deux chemins de création divergeraient, et une
            // preuve sur un corps qui ne serait pas celui des avatars réels ne prouverait rien du jeu.
            var playerRoot = Player.mainObject != null ? Player.mainObject.transform : clone.transform;
            var position = clone.transform.position + probeCamera.transform.right * TwoBodiesSpacingMeters;
            var second = RemoteAvatarFactory.TryCreate(playerRoot, position, playerRoot, out var failure);

            if (second == null)
            {
                Log.LogError(
                    $"[sonde] deux avatars : le second corps n'a pas pu être créé ({failure}) — la distinction de " +
                    "deux joueurs côte à côte reste NON PROUVÉE.");
                yield break;
            }

            // Le calque de la sonde est le seul que sa caméra dessine ; sans ça, le second corps serait
            // invisible dans l'image et la mesure le dirait pour la mauvaise raison.
            foreach (var renderer in second.GetComponentsInChildren<Renderer>(true))
            {
                renderer.gameObject.layer = probeLayer;
            }

            RemoteAvatarColor.Apply(clone, 0);
            RemoteAvatarColor.Apply(second, 1);

            // Deux frames : changer la couleur d'un matériau ne prend effet qu'au rendu qui suit.
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            probeCamera.Render();
            var image = ReadImage(renderTexture);

            var countA = ReadRegionColor(image, probeCamera, clone, out var colorA);
            var countB = ReadRegionColor(image, probeCamera, second, out var colorB);
            var delta = countA > 0 && countB > 0 ? ComponentDelta(colorA, colorB) : -1;
            var verdict = countA > 0 && countB > 0 && delta >= ColorDifferenceThreshold;

            Log.LogInfo(
                $"[sonde] couleur : deux avatars dans la même image — corps A (indice 0)={countA} px " +
                $"moyenne=({colorA.r * 255f:F0}, {colorA.g * 255f:F0}, {colorA.b * 255f:F0}), " +
                $"corps B (indice 1)={countB} px moyenne=({colorB.r * 255f:F0}, {colorB.g * 255f:F0}, {colorB.b * 255f:F0}), " +
                $"écart={delta}/255 (seuil {ColorDifferenceThreshold}) -> verdict={verdict}");

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] deux avatars : un des deux corps n'est pas mesurable dans l'image, ou les deux teintes " +
                    "ne se distinguent pas — deux joueurs côte à côte resteraient indiscernables.");
            }

            if (image != null)
            {
                var path = TwoColorProofPath();
                File.WriteAllBytes(path, image.EncodeToPNG());
                Log.LogInfo($"[sonde] deux avatars : image écrite -> {path}");
                Destroy(image);
            }

            // Le second corps n'est là que pour la mesure : le laisser en place ferait croire, à la phase
            // suivante, à un joueur distant qui n'existe pas.
            Destroy(second);
        }

        /// <summary>Lit un rendu hors écran dans une image, pour la mesurer **et** la garder.</summary>
        private static Texture2D ReadImage(RenderTexture renderTexture)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = renderTexture;

            var image = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            image.Apply();

            RenderTexture.active = previous;
            return image;
        }

        /// <summary>
        /// Couleur moyenne d'**un** corps dans sa région de l'image, et nombre de pixels ainsi mesurés.
        ///
        /// La région est le carré de ±<see cref="BodyRegionHalfSizePixels"/> pixels autour de la projection
        /// du torse (`-0,9 m` sous l'origine de la copie : c'est le cadrage déjà utilisé par le rendu isolé,
        /// l'origine étant en haut du corps). Compter les pixels non-fond **de cette région** — et non de
        /// toute l'image — est ce qui permet de dire « celui-là est rouge, celui-là est orange » dans une
        /// même image.
        ///
        /// Composantes en 0–1, comme celles d'un matériau : les pixels lus sont en 0–255, l'appelant les
        /// repasse en 0–255 pour l'affichage et pour les écarts.
        /// </summary>
        private static long ReadRegionColor(Texture2D image, Camera camera, GameObject body, out Color average)
        {
            average = Color.clear;

            if (image == null || camera == null || body == null)
            {
                return 0;
            }

            var torso = body.transform.position - Vector3.up * 0.9f;
            var viewport = camera.WorldToViewportPoint(torso);
            if (viewport.z <= 0f)
            {
                // Derrière la caméra : rien à mesurer, et surtout pas « mesuré noir ».
                return 0;
            }

            var centreX = (int)System.Math.Round(viewport.x * image.width);
            var centreY = (int)System.Math.Round(viewport.y * image.height);
            var pixels = image.GetPixels32();

            long coverage = 0;
            long red = 0;
            long green = 0;
            long blue = 0;

            for (var y = centreY - BodyRegionHalfSizePixels; y <= centreY + BodyRegionHalfSizePixels; y++)
            {
                if (y < 0 || y >= image.height)
                {
                    continue;
                }

                for (var x = centreX - BodyRegionHalfSizePixels; x <= centreX + BodyRegionHalfSizePixels; x++)
                {
                    if (x < 0 || x >= image.width)
                    {
                        continue;
                    }

                    var pixel = pixels[y * image.width + x];
                    var difference =
                        System.Math.Abs(pixel.r - 255) +
                        System.Math.Abs(pixel.g - 0) +
                        System.Math.Abs(pixel.b - 255);

                    if (difference <= PixelDifferenceThreshold)
                    {
                        // Fond magenta : le compter reviendrait à mesurer le fond, pas le corps.
                        continue;
                    }

                    coverage++;
                    red += pixel.r;
                    green += pixel.g;
                    blue += pixel.b;
                }
            }

            if (coverage == 0)
            {
                return 0;
            }

            average = new Color(
                red / (255f * coverage),
                green / (255f * coverage),
                blue / (255f * coverage),
                1f);
            return coverage;
        }

        /// <summary>Rend lisible un état de nœud à trois valeurs, en gardant `introuvable` distinct.</summary>
        private static string Format(bool? active) =>
            active == null ? "INTROUVABLE" : active.Value ? "actif" : "masqué";

        /// <summary>Dernier segment d'un chemin de nœud : les journaux restent lisibles.</summary>
        private static string Leaf(string path)
        {
            var separator = path.LastIndexOf('/');
            return separator < 0 ? path : path.Substring(separator + 1);
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
                        $"ombres={target.shadowCastingMode}, shader={ShaderNames(target)}" +
                        BoneExtent(target, clone) + BakedBounds(target) + TestWithPlainMaterial(target, probeCamera, renderTexture));
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

        /// <summary>
        /// Lit le rendu hors écran et donne la **couleur moyenne du sujet**, pas seulement sa luminance :
        /// c'est la mesure qui distingue « la copie est teintée » de « la copie est là ». Même lecture et
        /// même seuil de fond magenta que <see cref="ReadCoverage"/> : les pixels de fond sont donc
        /// exclus du calcul, sinon la teinte mesurée serait celle du fond.
        /// </summary>
        private static long ReadSubjectColor(
            RenderTexture renderTexture,
            out Color average,
            out float averageLuminance,
            out Texture2D texture)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = renderTexture;

            texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture.Apply();

            RenderTexture.active = previous;

            var pixels = texture.GetPixels32();
            long coverage = 0;
            long red = 0;
            long green = 0;
            long blue = 0;

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
                red += pixels[i].r;
                green += pixels[i].g;
                blue += pixels[i].b;
            }

            if (coverage == 0)
            {
                average = Color.clear;
                averageLuminance = 0f;
                return 0;
            }

            // Composantes en **0–1**, comme celles que rend un matériau (`Color` d'Unity) : les pixels
            // lus sont en 0–255, et les garder tels quels faisait afficher « moyenne RVB =(7793, …) »
            // — une valeur qui n'existe pas — puis un écart de « 1157/255 ». Diviser ici met les deux
            // sources de couleur (rendu et matériaux) dans la même unité, et rend le seuil de 8/255
            // comparable entre les deux.
            average = new Color(
                red / (255f * coverage),
                green / (255f * coverage),
                blue / (255f * coverage),
                1f);
            averageLuminance = (red + green + blue) / (3f * coverage);
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
        /// Chemin d'une image de preuve, dans le dossier de logs, à côté du log de l'instance : c'est là
        /// que tout ce qui concerne une exécution est déjà rangé, et c'est un dossier qu'on sait retrouver.
        ///
        /// Le suffixe <c>COOP_LOG_SUFFIX</c> (vide en usage courant) est celui du **log de l'instance**,
        /// cf. <c>InstanceLogFile</c> : deux clients lancés depuis le même dossier de jeu partagent ce
        /// dossier, et ces images ont des noms fixes — le second écraserait les preuves du premier. Une
        /// preuve qui s'écrase toute seule est une preuve qu'on ne peut pas comparer. Sans la variable, les
        /// noms sont exactement ceux d'avant, donc un run à un seul client ne change pas.
        /// </summary>
        private static string ProofPath(string name)
        {
            var suffix = System.Environment.GetEnvironmentVariable("COOP_LOG_SUFFIX");
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "BepInEx", "logs", $"{name}{suffix}.png"));
        }

        /// <summary>Capture d'écran du rendu du joueur, avec la copie dedans.</summary>
        private static string ScreenshotPath() => ProofPath("coop-avatar-proof");

        /// <summary>Image de la copie seule, sur fond magenta, prise par notre propre caméra.</summary>
        private static string IsolatedProofPath() => ProofPath("coop-avatar-isolated");

        /// <summary>Une image par couleur mesurée : deux teintes valent mieux qu'un chiffre seul.</summary>
        private static string ColorProofPath(byte index) => ProofPath($"coop-avatar-couleur-{index}");

        /// <summary>Une image par tenue mesurée : quatre silhouettes différentes valent mieux qu'une.</summary>
        private static string EquipmentProofPath(string key) => ProofPath($"coop-avatar-tenue-{key}");

        /// <summary>
        /// Deux avatars de deux couleurs, dans **une** image : la preuve qu'un joueur peut les distinguer
        /// côte à côte, là où une image par couleur ne montre qu'une teinte à la fois.
        /// </summary>
        private static string TwoColorProofPath() => ProofPath("coop-avatar-couleurs-deux-corps");
    }
}
