using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Coop.Model;
using UWE;
using UnityEngine;
using UnityEngine.Rendering;

namespace Coop.Client
{
    /// <summary>
    /// Fait tenir à un avatar distant **l'objet de son joueur** : l'outil en main, et le PDA quand il
    /// est sorti.
    ///
    /// Deux problèmes distincts, traités séparément parce qu'ils se mesurent séparément :
    ///
    /// 1. **La pose.** Le jeu décide de la position des bras par des paramètres d'animation
    ///    (`holding_*`, `using_pda`, `using_tool`, `using_tool_alt`, `in_seamoth`, `in_exosuit`). Ces
    ///    paramètres sont transportés par le réseau tels que le jeu les a posés chez l'émetteur — on ne
    ///    les recalcule pas, on les recopie : `PlayerTool.animToolName` vaut
    ///    `TechTypeExtensions.AsString(pickupable.GetTechType(), true)`, et `ArmsController` expose une
    ///    règle dédiée pour `holding_welder`, donc le paramètre s'appelle `holding_` + le nom du
    ///    `TechType`. Recalculer cette règle côté récepteur, ce serait se donner deux sources pour la
    ///    même donnée.
    ///
    /// 2. **Le modèle.** La copie du corps emporte l'objet tenu **du joueur local** (`attach1`), et la
    ///    fabrique le masque : c'est la bonne chose à faire, puisque ce n'est pas le sien. S'il tient
    ///    le même `TechType`, on se contente de le réafficher ; sinon le modèle est demandé au
    ///    `PrefabDatabase` du jeu et instancié sous `attach1`.
    ///
    /// **Tout échoue fermé.** Un prefab qui ne se charge pas, un nom de `TechType` inconnu, un
    /// `attach1` introuvable : on journalise et l'avatar garde la pose sans modèle. Un avatar sans
    /// objet dans la main est un défaut visible ; un avatar qui fait tomber le jeu n'en est pas un.
    /// </summary>
    internal sealed class RemoteAvatarHeldItem
    {
        private static readonly ManualLogSource Log = RemoteAvatarFactory.Log;

        /// <summary>
        /// Délai au-delà duquel une demande de prefab est déclarée perdue, en secondes.
        ///
        /// Volontairement large : un avatar peut être créé **pendant** le chargement du monde (mesuré :
        /// la base de prefabs du monde s'initialise plusieurs secondes après le premier avatar), et une
        /// échéance courte condamnerait un modèle qui allait arriver.
        /// </summary>
        private const float PrefabTimeoutSeconds = 20f;

        /// <summary>Fragment de nom qui désigne le PDA dans la copie du corps (relevé du §16).</summary>
        private const string PdaNameHint = "pda";

        /// <summary>
        /// Durée du geste de sortie du PDA, en secondes.
        ///
        /// Ce n'est pas une valeur choisie : `PDA.Open` pose `sequence.Set(0.5f, true, ...)` (IL relevé au
        /// §25). Le nœud, lui, s'allume immédiatement dans le jeu — c'est le bras qui monte.
        /// </summary>
        private const float PdaDrawSeconds = 0.5f;

        /// <summary>
        /// Durée du geste de rangement, en secondes.
        ///
        /// Relevé au §25 : `PDA.Close` pose `sequence.Set(0.3f, false, PDA.Deactivated)`, et c'est ce rappel
        /// de fin qui fait le `SetActive(false)`. Le PDA reste donc dans la main pendant que le bras
        /// redescend, et ne disparaît **pas** à l'instant où le joueur le relâche.
        /// </summary>
        private const float PdaHolsterSeconds = 0.3f;

        private readonly GameObject _clone;
        private readonly Transform _attach;
        private readonly Animator _animator;

        private string _techTypeName = string.Empty;
        private string[] _holdingParams = Array.Empty<string>();
        private string _modelTechType;
        private GameObject _model;
        private GameObject _shownLocalModel;
        private IPrefabRequest _request;
        private string _requestTechType;
        private float _requestDeadline;
        private bool _requestWarned;
        private bool _requestServed;
        private bool _pdaSearched;
        private GameObject _pdaNode;
        private bool _pdaShown;
        private bool _pdaTransitioning;
        private bool _pdaTransitionTarget;
        private float _pdaTransitionSeconds;
        private float _pdaTransitionT;

        public RemoteAvatarHeldItem(GameObject clone, Animator animator)
        {
            _clone = clone;
            _animator = animator;

            var model = RemoteAvatarEquipment.FindModelRoot(clone);
            _attach = model != null ? model.Find(RemoteAvatarEquipment.AttachPoint) : null;

            if (_attach == null)
            {
                Log.LogWarning(
                    "[avatar] objet tenu : point d'accroche introuvable sur la copie " +
                    $"('{RemoteAvatarEquipment.AttachPoint}') — la pose suivra, le modèle non.");
            }
        }

        /// <summary>Nom technique de l'objet tenu, tel que reçu (« » = rien en main).</summary>
        public string TechTypeName => _techTypeName;

        /// <summary>Vrai si un modèle est actuellement montré sous `attach1` (instancié ou réutilisé).</summary>
        public bool HasVisibleModel => _model != null || _shownLocalModel != null;

        /// <summary>
        /// Applique l'objet tenu reçu : pose d'abord, modèle ensuite.
        ///
        /// Les paramètres d'animation sont posés à chaque appel (ils changent avec l'outil **et** avec
        /// le geste), alors que le modèle n'est refait que si le `TechType` change : instancier un
        /// prefab à chaque paquet de position ferait tomber le jeu.
        /// </summary>
        public void Apply(string techTypeName, IReadOnlyList<string> holdingParams)
        {
            _techTypeName = techTypeName ?? string.Empty;
            _holdingParams = holdingParams as string[] ?? ToArray(holdingParams);

            ApplyHoldingParams();
            SetModel(_techTypeName);
        }

        /// <summary>Pose les booléens `holding_*` : un seul vrai à la fois, comme chez le jeu.</summary>
        private void ApplyHoldingParams()
        {
            if (_animator == null)
            {
                return;
            }

            var active = new List<string>();
            var parameters = _animator.parameters;

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter.type != AnimatorControllerParameterType.Bool ||
                    !parameter.name.StartsWith(Protocol.HoldingParamPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var shouldBeTrue = Contains(_holdingParams, parameter.name);
                if (shouldBeTrue)
                {
                    active.Add(parameter.name);
                }

                SafeAnimator.SetBool(_animator, parameter.name, shouldBeTrue);
            }

            Log.LogInfo(
                $"[avatar] objet tenu : '{HeldLabel()}' -> {active.Count} paramètre(s) `holding_*` actif(s) " +
                (active.Count == 0 ? "[]" : $"[{string.Join(", ", active)}]"));
        }

        /// <summary>
        /// Affiche le modèle de <paramref name="techTypeName"/>, en réutilisant celui que la copie
        /// porte déjà s'il correspond — sinon en demandant le prefab au jeu.
        /// </summary>
        private void SetModel(string techTypeName)
        {
            if (string.Equals(techTypeName, _modelTechType, StringComparison.Ordinal))
            {
                return;
            }

            HideEverythingAttached();
            DestroyModel();
            ReleaseRequest();

            _modelTechType = techTypeName;

            if (string.IsNullOrEmpty(techTypeName))
            {
                _shownLocalModel = null;
                return;
            }

            if (_attach == null)
            {
                return;
            }

            // 1. Le modèle est peut-être déjà là : la copie emporte l'objet tenu du joueur local, donc
            //    si les deux joueurs tiennent le même objet, il n'y a rien à instancier.
            var existing = FindAttachedModel(techTypeName);
            if (existing != null)
            {
                existing.SetActive(true);
                _shownLocalModel = existing;
                Log.LogInfo(
                    $"[avatar] objet tenu : '{existing.name}' déjà présent sous `attach1` " +
                    "(même objet que le joueur local) -> réaffiché, aucun prefab demandé.");
                return;
            }

            _shownLocalModel = null;

            // 2. Sinon, le prefab du jeu. `PrefabDatabase` est asynchrone : la demande est posée ici et
            //    avancée par `Tick`, pour ne jamais bloquer la boucle réseau.
            if (!Enum.TryParse(techTypeName, true, out TechType type) || !Enum.IsDefined(typeof(TechType), type))
            {
                Log.LogWarning($"[avatar] objet tenu : '{techTypeName}' n'est pas un TechType connu — aucun modèle montré.");
                return;
            }

            string classId;
            try
            {
                classId = CraftData.GetClassIdForTechType(type);
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[avatar] objet tenu : identifiant de classe illisible pour '{techTypeName}' ({exception.GetType().Name}).");
                return;
            }

            if (string.IsNullOrEmpty(classId))
            {
                Log.LogWarning($"[avatar] objet tenu : aucune classe connue pour '{techTypeName}' — aucun modèle montré.");
                return;
            }

            try
            {
                _request = PrefabDatabase.GetPrefabAsync(classId);
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[avatar] objet tenu : demande de prefab refusée pour '{classId}' ({exception.GetType().Name}).");
                _request = null;
                return;
            }

            _requestTechType = techTypeName;
            _requestDeadline = Time.realtimeSinceStartup + PrefabTimeoutSeconds;
            _requestWarned = false;
            Log.LogInfo($"[avatar] objet tenu : prefab '{classId}' demandé pour '{techTypeName}' (chargement asynchrone).");
        }

        /// <summary>
        /// Avance les demandes de prefab en cours. Appelée à chaque frame pour chaque avatar : c'est ce
        /// qui rend le chargement asynchrone utilisable sans coroutine ni blocage.
        /// </summary>
        public void Tick()
        {
            // Le geste du PDA est avancé **avant** la garde du prefab : il ne dépend pas de la demande.
            AdvancePdaTransition(Time.deltaTime);

            if (_request == null || _requestServed)
            {
                return;
            }

            var prefab = default(GameObject);
            var ready = false;

            try
            {
                // Le pas du jeu, refait ici. La coroutine `CraftData.GetPrefabForTechTypeAsync` ne lit
                // jamais une demande neuve : elle « yield » la demande elle-même, et Unity appelle
                // alors son `MoveNext()`, qui initialise le chargement Addressables
                // (`LazyInitializeAsyncRequest`) puis le fait avancer. Interroger `TryGetPrefab` sans
                // ce pas lève « Attempting to use an invalid operation handle » — l'opération interne
                // de la poignée est encore nulle — et la panne est définitive tant que personne
                // n'initialise la demande : c'est la cause mesurée au §24. Faute de coroutine ici, on
                // fait ce pas à la main, une fois par frame, exactement comme le jeu.
                if (_request is IEnumerator driver)
                {
                    driver.MoveNext();
                }

                ready = _request.TryGetPrefab(out prefab);
            }
            catch (Exception exception)
            {
                // Une demande peut légitimement lever tant que la base de prefabs du monde n'est pas
                // prête : on réessaie jusqu'à l'échéance au lieu d'abandonner au premier refus, et on
                // journalise le **message** réel — le type seul ne dit pas si la table est encore vide
                // ou si l'identifiant de classe est introuvable.
                if (!_requestWarned)
                {
                    _requestWarned = true;
                    Log.LogWarning(
                        $"[avatar] objet tenu : prefab de '{_requestTechType}' pas encore lisible " +
                        $"({exception.GetType().Name}: {exception.Message}) — nouvel essai jusqu'à l'échéance.");
                }

                if (Time.realtimeSinceStartup > _requestDeadline)
                {
                    Log.LogWarning(
                        $"[avatar] objet tenu : prefab de '{_requestTechType}' toujours illisible après " +
                        $"{PrefabTimeoutSeconds:F0}s — avatar sans modèle (la pose reste correcte).");
                    ReleaseRequest();
                }

                return;
            }

            if (!ready)
            {
                if (!_requestWarned && Time.realtimeSinceStartup > _requestDeadline)
                {
                    _requestWarned = true;
                    Log.LogWarning(
                        $"[avatar] objet tenu : prefab de '{_requestTechType}' non chargé après " +
                        $"{PrefabTimeoutSeconds:F0}s — avatar sans modèle (la pose reste correcte).");
                    ReleaseRequest();
                }

                return;
            }

            // La demande ne se rend plus : elle devient le maintien en vie du modèle, car Addressables
            // décharge l'asset quand la dernière poignée est rendue. Elle suit donc le modèle affiché
            // et n'est libérée que lorsqu'il est remplacé ou que l'avatar disparaît (`SetModel`,
            // `Dispose`).
            _requestServed = true;
            InstantiateModel(prefab, _requestTechType);
        }

        private void InstantiateModel(GameObject prefab, string techTypeName)
        {
            if (prefab == null || _attach == null)
            {
                return;
            }

            var wasActive = prefab.activeSelf;
            GameObject instance = null;

            try
            {
                // Instancier un prefab **inactif** : `Awake` n'est alors pas appelé, donc aucun script
                // de gameplay ne s'enregistre (c'est exactement le piège qui a fait refuser la copie du
                // root `Player`). Le prefab est remis dans son état dans le `finally` — on ne modifie
                // jamais durablement un objet du jeu.
                if (wasActive)
                {
                    prefab.SetActive(false);
                }

                instance = UnityEngine.Object.Instantiate(prefab);
            }
            catch (Exception exception)
            {
                Log.LogError($"[avatar] objet tenu : instanciation du prefab de '{techTypeName}' impossible ({exception.GetType().Name}).");
            }
            finally
            {
                if (wasActive && prefab != null)
                {
                    prefab.SetActive(true);
                }
            }

            if (instance == null)
            {
                return;
            }

            var strip = RemoteAvatarComponents.Strip(instance, keepAnimator: false);

            instance.transform.SetParent(_attach, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            var worldLayer = LayerMask.NameToLayer("Default");
            var drawable = 0;
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                renderer.gameObject.layer = worldLayer;
                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                {
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                }

                drawable++;
            }

            instance.SetActive(true);
            _model = instance;

            var bounds = ComputeBounds(instance);
            Log.LogInfo(
                $"[avatar] objet tenu : modèle de '{techTypeName}' monté sous `attach1` " +
                $"({drawable} renderer(s), {strip.Removed}/{strip.Detected} composant(s) de gameplay retiré(s), " +
                $"taille={bounds.size.ToString("F3")} m, technique='{techTypeName}').");

            if (strip.Remaining.Count > 0)
            {
                Log.LogWarning(
                    $"[avatar] objet tenu : composant(s) laissé(s) sur le modèle de '{techTypeName}' " +
                    $"(Unity a refusé le retrait) : {string.Join(", ", strip.Remaining)}.");
            }
        }

        /// <summary>
        /// Suit le geste du PDA du joueur distant, geste pour geste.
        ///
        /// Le PDA fait **partie du corps copié** (relevé au §16 : un nœud inactif dans la hiérarchie),
        /// donc il n'y a rien à instancier. Mais l'activer et le désactiver sèchement n'est pas le geste du
        /// jeu ; l'IL (§25) le dit en deux lignes :
        ///
        /// - `PDA.Open` allume le nœud **tout de suite** (`gameObject.SetActive(true)`), juste après avoir
        ///   posé `using_pda` sur l'`Animator` : c'est le bras qui monte, pas le modèle qui s'allume en fondu ;
        /// - `PDA.Close` ne l'éteint **pas** : il pose `sequence.Set(0.3f, false, PDA.Deactivated)` et laisse
        ///   ce rappel de fin faire le `SetActive(false)`. Le PDA reste donc en main pendant que le bras
        ///   redescend.
        ///
        /// Le nœud est cherché par son nom au premier appel, et le journal dit ce qui a été trouvé.
        /// </summary>
        public void SetPdaVisible(bool visible)
        {
            if (!_pdaSearched)
            {
                _pdaSearched = true;
                _pdaNode = FindPdaNode(_clone);
                Log.LogInfo(_pdaNode == null
                    ? "[avatar] PDA : aucun nœud contenant 'pda' avec un renderer dans la copie — PDA non montré."
                    : $"[avatar] PDA : nœud '{RemoteAvatarFactory.Path(_pdaNode.transform)}' trouvé dans la copie.");
            }

            if (_pdaNode == null)
            {
                return;
            }

            // Déjà dans le bon état, ou déjà en route vers lui : ne rien relancer (sinon le geste,
            // redémarré à chaque paquet de position, ne finirait jamais).
            if (_pdaTransitioning ? visible == _pdaTransitionTarget : visible == _pdaShown)
            {
                return;
            }

            if (visible)
            {
                if (!_pdaShown)
                {
                    // Le geste du jeu : le modèle s'allume tout de suite, le bras monte ensuite.
                    _pdaNode.SetActive(true);
                    _pdaShown = true;
                    Log.LogInfo(
                        $"[avatar] PDA sorti sur l'avatar ('{_pdaNode.name}') — geste de " +
                        $"{PdaDrawSeconds:F1} s engagé (le modèle suit la main).");
                }

                BeginPdaTransition(true, PdaDrawSeconds);
                return;
            }

            BeginPdaTransition(false, PdaHolsterSeconds);
            Log.LogInfo(
                $"[avatar] PDA : rangement engagé sur l'avatar ('{_pdaNode.name}') — le modèle reste en main " +
                $"pendant les {PdaHolsterSeconds:F1} s du geste, comme `PDA.Close`.");
        }

        /// <summary>
        /// Démarre un geste, ou l'inverse en cours de route en conservant la progression acquise
        /// (comme `Sequence.t`) : un joueur qui ressort son PDA pendant les 0,3 s de rangement ne doit pas
        /// voir le modèle sauter.
        /// </summary>
        private void BeginPdaTransition(bool target, float seconds)
        {
            _pdaTransitionT = _pdaTransitioning ? 1f - Mathf.Clamp01(_pdaTransitionT) : 0f;
            _pdaTransitioning = true;
            _pdaTransitionTarget = target;
            _pdaTransitionSeconds = Mathf.Max(seconds, 0.01f);
        }

        /// <summary>
        /// Avance le geste en cours. C'est ce que fait `PDA.sequence.Update` dans le jeu, et c'est à la
        /// **fin** du geste de rangement que le nœud s'éteint (`PDA.Deactivated`), pas à son début.
        /// </summary>
        private void AdvancePdaTransition(float deltaTime)
        {
            if (!_pdaTransitioning)
            {
                return;
            }

            _pdaTransitionT += deltaTime / _pdaTransitionSeconds;
            if (_pdaTransitionT < 1f)
            {
                return;
            }

            _pdaTransitionT = 1f;
            _pdaTransitioning = false;

            if (_pdaTransitionTarget)
            {
                Log.LogInfo(
                    $"[avatar] PDA : geste de sortie terminé sur l'avatar ('{NodeName()}') après " +
                    $"{PdaDrawSeconds:F1} s.");
                return;
            }

            if (_pdaNode != null && _pdaNode.activeSelf)
            {
                _pdaNode.SetActive(false);
            }

            _pdaShown = false;
            Log.LogInfo(
                $"[avatar] PDA rangé sur l'avatar ('{NodeName()}') — le modèle a suivi le bras pendant les " +
                $"{PdaHolsterSeconds:F1} s du geste.");
        }

        private string NodeName() => _pdaNode != null ? _pdaNode.name : "?";

        /// <summary>Refuse de détruire autre chose que nos propres objets, et rend la demande de prefab.</summary>
        public void Dispose()
        {
            _pdaTransitioning = false;
            ReleaseRequest();
            DestroyModel();

            if (_shownLocalModel != null)
            {
                _shownLocalModel.SetActive(false);
                _shownLocalModel = null;
            }
        }

        private void DestroyModel()
        {
            if (_model != null)
            {
                UnityEngine.Object.Destroy(_model);
                _model = null;
            }
        }

        private void ReleaseRequest()
        {
            if (_request == null)
            {
                return;
            }

            try
            {
                _request.Release();
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[avatar] objet tenu : libération du prefab en échec ({exception.GetType().Name}).");
            }

            _request = null;
            _requestTechType = null;
            _requestServed = false;
        }

        private void HideEverythingAttached()
        {
            if (_clone != null)
            {
                RemoteAvatarEquipment.HideAttachedItems(_clone);
            }
        }

        private GameObject FindAttachedModel(string techTypeName)
        {
            if (_attach == null)
            {
                return null;
            }

            for (var index = 0; index < _attach.childCount; index++)
            {
                var child = _attach.GetChild(index);
                if (child == null)
                {
                    continue;
                }

                if (child.GetComponentsInChildren<Renderer>(true).Length == 0)
                {
                    continue;
                }

                if (child.name.IndexOf(techTypeName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Nœud du PDA dans la copie : le nom contient « pda », et il porte au moins un renderer —
        /// sans quoi on activerait un conteneur vide en croyant avoir montré quelque chose.
        /// </summary>
        private static GameObject FindPdaNode(GameObject clone)
        {
            GameObject best = null;
            var transforms = clone.GetComponentsInChildren<Transform>(true);

            for (var i = 0; i < transforms.Length; i++)
            {
                var transform = transforms[i];
                if (transform == null || transform.name.IndexOf(PdaNameHint, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (transform.GetComponentsInChildren<Renderer>(true).Length == 0)
                {
                    continue;
                }

                // Le plus profond gagne : c'est le modèle, pas le dossier qui le contient.
                if (best == null || Depth(transform) > Depth(best.transform))
                {
                    best = transform.gameObject;
                }
            }

            return best;
        }

        private static int Depth(Transform node)
        {
            var depth = 0;
            while (node.parent != null)
            {
                depth++;
                node = node.parent;
            }

            return depth;
        }

        private static Bounds ComputeBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return new Bounds(root.transform.position, Vector3.zero);
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        private string HeldLabel() => _techTypeName.Length == 0 ? "rien en main" : _techTypeName;

        private static bool Contains(string[] values, string value)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] ToArray(IReadOnlyList<string> values)
        {
            if (values == null)
            {
                return Array.Empty<string>();
            }

            var array = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                array[i] = values[i];
            }

            return array;
        }

        /// <summary>Description courte de l'état, pour les lignes de journal.</summary>
        public string Describe()
        {
            var builder = new StringBuilder();
            builder.Append(_techTypeName.Length == 0 ? "(rien en main)" : _techTypeName);
            builder.Append(_holdingParams.Length == 0
                ? " sans paramètre `holding_*`"
                : $" [{string.Join(", ", _holdingParams)}]");
            builder.Append(_model != null
                ? " modèle instancié"
                : _shownLocalModel != null ? " modèle de la copie réutilisé" : " aucun modèle");
            if (_request != null && !_requestServed)
            {
                builder.Append(" (prefab en cours de chargement)");
            }

            return builder.ToString();
        }
    }
}
