using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Coop.Client
{
    /// <summary>
    /// Panneau de connexion du menu : le joueur **tape l'adresse** du serveur au lieu de la chercher
    /// dans `BepInEx/config`.
    ///
    /// Pourquoi ce panneau existe : l'adresse venait d'un fichier lu au démarrage, avec `127.0.0.1` par
    /// défaut. Tant que les deux instances étaient sur la même machine ça passait ; dès qu'un ami est
    /// derrière un autre réseau, il n'y avait **aucun** endroit où saisir son IP publique. Le panneau est
    /// donc la porte d'entrée normale, le fichier et `-coopserver` restant les chemins de script.
    ///
    /// Ce qu'il ne fait pas : il ne devine rien. Une adresse invalide est refusée avec sa raison, et
    /// aucune connexion n'est tentée ailleurs — un repli silencieux sur la boucle locale ferait chercher
    /// la panne au mauvais endroit (`ServerAddressPreference.TryParse` porte la règle, testée hors jeu).
    ///
    /// Le panneau se masque dès que le serveur a **accepté** la session, et réapparaît au menu si la
    /// connexion échoue — c'est ce qui permet de corriger une IP sans relancer le jeu.
    /// </summary>
    public class JoinPanelBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Join");

        /// <summary>
        /// Instance courante, pour la sonde `-coopdebug=join` : elle remplit les **vrais** champs et
        /// appelle le **même** `Submit()` que le bouton, au lieu de réimplémenter la validation.
        /// </summary>
        internal static JoinPanelBehaviour Current { get; private set; }

        private static Font _uiFont;

        private GameObject _panel;
        private InputField _address;
        private InputField _port;
        private Text _status;
        private bool _built;

        /// <summary>Dernière raison de refus, lue par la sonde (vide après une saisie acceptée).</summary>
        internal string LastError { get; private set; } = string.Empty;

        /// <summary>Dernière saisie soumise, telle quelle, pour le journal de la sonde.</summary>
        internal string LastSubmitted { get; private set; } = string.Empty;

        internal bool Ready => _built;

        internal InputField AddressField => _address;

        internal InputField PortField => _port;

        private static Font UIFont =>
            _uiFont != null ? _uiFont : (_uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf"));

        private void Awake()
        {
            Current = this;
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }
        }

        private void Update()
        {
            var client = ClientNetworkBehaviour.Current;
            if (client == null)
            {
                // Avant `Start` du client réseau : rien à afficher, et surtout aucune adresse à préremplir.
                return;
            }

            if (client.Accepted)
            {
                Hide();
                return;
            }

            if (!_built)
            {
                // Le menu doit exister : le panneau s'affiche par-dessus lui, jamais dans le monde.
                if (uGUI_MainMenu.main == null || AutoLoadBehaviour.WorldLoaded)
                {
                    return;
                }

                Build(client);
            }

            if (!_built)
            {
                return;
            }

            Show();

            // Entrée valide comme le bouton : après avoir tapé l'IP, on ne cherche pas la souris.
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                Submit();
            }
        }

        private void Show()
        {
            if (_panel != null && !_panel.activeSelf)
            {
                _panel.SetActive(true);
            }
        }

        private void Hide()
        {
            if (_panel != null && _panel.activeSelf)
            {
                _panel.SetActive(false);
                Log.LogInfo("[menu] panneau de connexion masqué — session acceptée.");
            }
        }

        /// <summary>
        /// Valide la saisie puis bascule la connexion. Appelé par le bouton, par Entrée, et par la sonde :
        /// une seule règle pour les trois, sinon le bouton et la mesure pourraient diverger.
        /// </summary>
        internal void Submit()
        {
            var addressText = _address != null ? (_address.text ?? string.Empty).Trim() : string.Empty;
            var portText = _port != null ? (_port.text ?? string.Empty).Trim() : string.Empty;

            // Le port a son propre champ : on recompose la forme que le parseur comprend, plutôt que
            // d'avoir deux validateurs. Un port vide laisse le défaut du protocole.
            var combined = string.IsNullOrEmpty(portText) ? addressText : addressText + ":" + portText;
            LastSubmitted = combined;

            if (!ServerAddressPreference.TryParse(combined, Protocol.DefaultPort, out var address, out var error))
            {
                LastError = error;
                SetStatus($"Adresse refusée : {error}", true);
                Log.LogWarning(
                    $"[menu] adresse refusée : « {ServerAddressPreference.Describe(combined)} » — {error}");
                return;
            }

            LastError = string.Empty;
            SetStatus($"connexion à {address}…", false);
            Log.LogInfo($"[menu] connexion demandée : {address}");
            CoopClientPlugin.ApplyEndpoint(address.Host, address.Port);
        }

        private void SetStatus(string message, bool isError)
        {
            if (_status == null)
            {
                return;
            }

            _status.text = message;
            _status.color = isError
                ? new Color(1f, 0.45f, 0.42f)
                : new Color(0.75f, 0.85f, 0.95f);
        }

        private void Build(ClientNetworkBehaviour client)
        {
            try
            {
                if (FindObjectOfType<EventSystem>() == null)
                {
                    var eventSystem = new GameObject(
                        "Coop.JoinPanel.EventSystem",
                        typeof(EventSystem),
                        typeof(StandaloneInputModule));
                    DontDestroyOnLoad(eventSystem);
                }

                var canvasGo = new GameObject(
                    "Coop.JoinPanel",
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster));
                DontDestroyOnLoad(canvasGo);

                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 500;

                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1280f, 720f);
                scaler.matchWidthOrHeight = 0.5f;

                _panel = new GameObject("Panneau", typeof(RectTransform), typeof(Image));
                _panel.transform.SetParent(canvasGo.transform, false);

                var panelRect = (RectTransform)_panel.transform;
                panelRect.anchorMin = new Vector2(0.5f, 0.5f);
                panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                panelRect.pivot = new Vector2(0.5f, 0.5f);
                panelRect.sizeDelta = new Vector2(560f, 300f);
                panelRect.anchoredPosition = Vector2.zero;
                _panel.GetComponent<Image>().color = new Color(0.055f, 0.075f, 0.10f, 0.95f);

                CreateLabel("Titre", "Coop — rejoindre un serveur", 22, FontStyle.Bold, new Vector2(0f, 118f), new Vector2(500f, 32f));
                CreateLabel("InviteAdresse", "Adresse du serveur (IP ou nom d'hôte)", 14, FontStyle.Normal, new Vector2(0f, 80f), new Vector2(500f, 22f));

                // Préremplissage : la dernière adresse connue (argument, fichier, session précédente).
                var knownHost = client.EndpointHost ?? string.Empty;
                var knownPort = client.EndpointPort > 0 ? client.EndpointPort : Protocol.DefaultPort;
                _address = CreateInput("Adresse", "203.0.113.10 ou mon-serveur.exemple.fr", new Vector2(0f, 48f), new Vector2(500f, 36f), knownHost, 253);
                _port = CreateInput(
                    "Port",
                    Protocol.DefaultPort.ToString(CultureInfo.InvariantCulture),
                    new Vector2(-190f, 4f),
                    new Vector2(120f, 32f),
                    knownPort.ToString(CultureInfo.InvariantCulture),
                    5);
                _port.contentType = InputField.ContentType.IntegerNumber;
                CreateLabel("InvitePort", "Port UDP", 14, FontStyle.Normal, new Vector2(-190f, 30f), new Vector2(120f, 22f));

                CreateButton("Rejoindre", new Vector2(0f, -52f), new Vector2(220f, 42f));
                _status = CreateLabel("Statut", "Adresse manquante : saisissez celle du serveur.", 14, FontStyle.Normal, new Vector2(0f, -112f), new Vector2(500f, 40f));

                _built = true;
                _panel.SetActive(true);
                Log.LogInfo(
                    $"[menu] panneau de connexion affiché — adresse connue " +
                    $"« {ServerAddressPreference.Describe(knownHost)}:{knownPort} ».");
            }
            catch (System.Exception exception)
            {
                // Une interface qui casse ne doit pas empêcher de jouer : le client reste utilisable par
                // `-coopserver` / le fichier de configuration, et l'erreur est nommée.
                _built = false;
                Log.LogError($"[menu] panneau de connexion impossible à construire : {exception}");
            }
        }

        private Text CreateLabel(string name, string value, int fontSize, FontStyle style, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(_panel.transform, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            var text = go.GetComponent<Text>();
            text.font = UIFont;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.86f, 0.90f, 0.95f);
            text.supportRichText = false;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private InputField CreateInput(string name, string hint, Vector2 position, Vector2 size, string value, int characterLimit)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(_panel.transform, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            var background = go.GetComponent<Image>();
            background.color = new Color(0.97f, 0.98f, 1f, 0.94f);

            var text = CreateFieldText(go.transform, "Texte", new Color(0.05f, 0.06f, 0.08f));
            var placeholder = CreateFieldText(go.transform, "Invite", new Color(0.35f, 0.38f, 0.42f));
            placeholder.text = hint;

            var field = go.GetComponent<InputField>();
            field.targetGraphic = background;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.characterLimit = characterLimit;
            field.lineType = InputField.LineType.SingleLine;
            field.text = value ?? string.Empty;
            return field;
        }

        private Text CreateFieldText(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(8f, 2f);
            rect.offsetMax = new Vector2(-8f, -2f);

            var text = go.GetComponent<Text>();
            text.font = UIFont;
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = color;
            text.supportRichText = false;
            return text;
        }

        private void CreateButton(string label, Vector2 position, Vector2 size)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(_panel.transform, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            go.GetComponent<Image>().color = new Color(0.15f, 0.52f, 0.82f, 1f);

            var textGo = new GameObject("Texte", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGo.GetComponent<Text>();
            text.font = UIFont;
            text.fontSize = 18;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = label;

            go.GetComponent<Button>().onClick.AddListener(Submit);
        }
    }
}
