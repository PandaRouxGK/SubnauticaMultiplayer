using System.Collections;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Coop.Shared
{
    /// <summary>
    /// Amène l'instance dans le monde sans intervention, en réutilisant le chemin de code du menu.
    ///
    /// Utilisé par le serveur **et** par le client : sans monde chargé côté client, il n'y a rien à
    /// répliquer (la présence des joueurs suppose que les deux instances soient dans la partie).
    ///
    /// Pourquoi passer par le bouton du menu plutôt que reconstruire les paramètres : le chargement
    /// attend `saveGame`, `sessionId`, `changeSet` et `gameMode`, valeurs que seul le menu sait
    /// composer depuis la sauvegarde. Appeler `MainMenuLoadButton.Load()` suit donc le format de
    /// sauvegarde sans qu'on ait à le connaître.
    ///
    /// Compilé dans plusieurs projets via un &lt;Compile Include&gt; partagé.
    /// </summary>
    public class AutoLoadBehaviour : MonoBehaviour
    {
        /// <summary>Source de log dédiée : lisible quel que soit le plugin qui l'utilise.</summary>
        // Préfixe explicite : `using UnityEngine` apporte aussi un `Logger`, la référence seule serait ambiguë.
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Autoload");

        /// <summary>Délai laissé au menu pour finir de se peupler avant de le solliciter.</summary>
        private const float MenuSettleSeconds = 3f;

        /// <summary>Délai maximum accordé au chargement du monde.</summary>
        private const float WorldLoadTimeoutSeconds = 60f;

        /// <summary>Scènes du menu : tant qu'on y est, le monde n'est pas chargé.</summary>
        private static bool IsMenuScene(string sceneName) =>
            sceneName == "XMenu" || sceneName == "MenuEnvironment" || sceneName == "StartScreen";

        /// <summary>
        /// Vrai une fois qu'une partie est **réellement** chargée : la scène active n'est plus une scène de
        /// menu. C'est le seul signal fiable pour les sondes qui doivent agir sur le monde.
        ///
        /// Pourquoi pas `Player.main` ni `LargeWorldStreamer.main` : les deux existent déjà dans les scènes
        /// de menu. Le premier run de l'étape K a fabriqué son Seamoth à `(2,08, -3,78, -0,69)` — la position
        /// du joueur de menu — et l'a sauvegardé là, à 400 m de la capsule ; la mesure à deux clients a alors
        /// dû rattacher le véhicule par proximité, ce qui prouvait exactement le contraire de l'étape. Ce
        /// drapeau-ci est posé par la même boucle qui journalise `MONDE CHARGÉ`, donc après le changement de
        /// scène (observation à 2 s près) : les sondes attendent celui-là, puis laissent le jeu placer le
        /// joueur à sa position sauvegardée.
        /// </summary>
        public static bool WorldLoaded { get; private set; }

        private IEnumerator Start()
        {
            Log.LogInfo("[monde] attente du menu principal...");
            yield return new WaitUntil(() => uGUI_MainMenu.main != null);
            Log.LogInfo("[monde] uGUI_MainMenu.main disponible.");

            yield return new WaitForSeconds(MenuSettleSeconds);

            var saveLoadManager = SaveLoadManager.main;
            if (saveLoadManager == null)
            {
                Log.LogError("[monde] SaveLoadManager.main est null -> abandon du chargement.");
                yield break;
            }

            var slots = saveLoadManager.GetActiveSlotNames();
            Log.LogInfo(slots == null || slots.Length == 0
                ? "[monde] GetActiveSlotNames: (aucun)"
                : $"[monde] GetActiveSlotNames: {slots.Length} -> [{string.Join(", ", slots)}]");

            // Le panneau "Charger" est peuplé à la demande : on l'ouvre comme le ferait un clic.
            Log.LogInfo("[monde] ouverture du panneau de chargement (OnButtonLoad)...");
            uGUI_MainMenu.main.OnButtonLoad();
            yield return new WaitForSeconds(MenuSettleSeconds);

            var buttons = Object.FindObjectsOfType<MainMenuLoadButton>();
            Log.LogInfo($"[monde] MainMenuLoadButton trouvés: {buttons.Length}");

            MainMenuLoadButton target = null;
            foreach (var button in buttons)
            {
                if (button == null || button.IsEmpty())
                {
                    continue;
                }

                Log.LogInfo(
                    $"[monde]   slot='{button.saveGame}' session='{button.sessionId}' " +
                    $"changeSet={button.changeSet} mode={button.gameMode}");

                if (target == null)
                {
                    target = button;
                }
            }

            if (target == null)
            {
                Log.LogWarning("[monde] aucune sauvegarde chargeable trouvée -> l'instance reste au menu.");
                yield break;
            }

            // L'empreinte est publiée **avant** le chargement : c'est la même sauvegarde que celle que
            // `Load()` va ouvrir, et le client doit l'envoyer dans son handshake (P0). Publier après
            // reviendrait à décrire une partie qu'on a déjà commencé à modifier.
            SaveIdentitySource.Publish(
                Coop.Model.SaveIdentity.Create(
                    target.saveGame,
                    target.sessionId,
                    target.changeSet,
                    target.gameMode.ToString()));

            Log.LogInfo($"[monde] >>> chargement de la sauvegarde '{target.saveGame}'");
            target.Load();

            var deadline = Time.realtimeSinceStartup + WorldLoadTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSeconds(2f);

                var scene = SceneManager.GetActiveScene();
                if (!IsMenuScene(scene.name))
                {
                    WorldLoaded = true;
                    Log.LogInfo($"[monde] *** MONDE CHARGÉ : scene '{scene.name}' ***");
                    yield break;
                }
            }

            Log.LogWarning("[monde] délai dépassé : le monde ne semble pas s'être chargé.");
        }
    }
}
