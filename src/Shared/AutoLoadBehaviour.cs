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

            // Sauvegarde imposée (`-coopsave=slot0001` ou `COOP_SAVE_SLOT`) : c'est ce qui permet au
            // harnais de faire parler un client d'une **autre** partie que l'hôte, et donc d'éprouver
            // le refus P0 « deux sauvegardes différentes » en jeu. Sans préférence, on garde le
            // comportement historique : la première sauvegarde chargeable.
            var requestedSlot = Coop.Model.SaveSlotPreference.Current;
            if (requestedSlot != null)
            {
                Log.LogInfo($"[monde] sauvegarde demandée : '{requestedSlot}'");
            }

            // `FindObjectsOfType` ne garantit aucun ordre : avec deux sauvegardes, « la première venue »
            // changeait d'un lancement à l'autre. Mesuré le 2026-10-20 : le serveur et le client ont chargé
            // `slot0001` alors que la fixture était `slot0000`, et le refus P0 n'a pas eu lieu. On trie donc
            // explicitement par nom de slot quand aucune préférence n'est donnée.
            var candidates = new System.Collections.Generic.List<MainMenuLoadButton>();
            foreach (var button in buttons)
            {
                if (button == null || button.IsEmpty())
                {
                    continue;
                }

                Log.LogInfo(
                    $"[monde]   slot='{button.saveGame}' session='{button.sessionId}' " +
                    $"changeSet={button.changeSet} mode={button.gameMode}");
                candidates.Add(button);
            }

            candidates.Sort((left, right) => string.CompareOrdinal(left.saveGame, right.saveGame));

            MainMenuLoadButton target = null;
            var requestedFound = false;
            if (requestedSlot != null)
            {
                foreach (var candidate in candidates)
                {
                    if (!string.Equals(candidate.saveGame, requestedSlot, System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    target = candidate;
                    requestedFound = true;
                    Log.LogInfo($"[monde] sauvegarde demandée '{requestedSlot}' trouvée");
                    break;
                }
            }
            else if (candidates.Count > 0)
            {
                target = candidates[0];
                Log.LogInfo(
                    $"[monde] sauvegarde par défaut : '{target.saveGame}' " +
                    $"(aucune préférence ; ordre déterministe par nom parmi {candidates.Count} slot(s))");
            }

            // Demander une sauvegarde absente ne doit **jamais** charger une autre partie : le harnais
            // croirait mesurer une divergence de sauvegarde alors que les deux instances auraient ouvert
            // la même. Le contrôle vient **avant** le cas général, sinon le message dirait « aucune
            // sauvegarde chargeable » alors que des sauvegardes existent — mais pas celle demandée.
            if (requestedSlot != null && !requestedFound)
            {
                Log.LogError(
                    $"[monde] sauvegarde demandée '{requestedSlot}' introuvable parmi les {buttons.Length} " +
                    "bouton(s) de chargement -> l'instance reste au menu (aucune autre partie n'est ouverte).");
                yield break;
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
