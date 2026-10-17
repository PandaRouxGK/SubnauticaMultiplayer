using System;
using System.Collections;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace Coop.Server
{
    /// <summary>
    /// Canal de contrôle **fichier** entre le launcher et le serveur : il n'y a pas d'autre moyen pour un
    /// programme extérieur de demander une action à une instance de jeu.
    ///
    /// Pourquoi un fichier et pas un socket : le serveur possède déjà un socket UDP, mais il parle le
    /// protocole du jeu et n'accepte que des clients authentifiés. Ajouter une commande d'administration
    /// dans ce protocole ouvrirait une surface réseau pour un bouton local. Un fichier dans `BepInEx/coop/`
    /// ne peut être écrit que par un processus qui a déjà accès au dossier du jeu — c'est exactement le
    /// périmètre du launcher.
    ///
    /// Le contrat, volontairement minuscule :
    ///   - `save.request` : le launcher écrit un **jeton** (une ligne). Un jeton déjà vu n'est jamais rejoué,
    ///     donc cliquer deux fois sur « Sauvegarder » demande bien deux sauvegardes.
    ///   - `save.ack`     : `jeton|code|message`, écrit **après** l'appel. `ok` veut dire « `IngameMenu.SaveGame()`
    ///     a été appelé » — pas « le fichier du slot est déjà remplacé » : le jeu écrit en arrière-plan, et
    ///     le launcher ne doit pas prétendre le contraire.
    ///
    /// Le fichier est relu toutes les demi-secondes : c'est un bouton, pas une boucle de jeu, et un scan de
    /// disque à 20 Hz serait un gaspillage visible. `WaitForSecondsRealtime` parce que l'hôte peut être en
    /// pause (menu ouvert), et `WaitForSeconds` s'arrête alors avec le temps.
    /// </summary>
    public class ServerControlBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Contrôle");

        private const float PollSeconds = 0.5f;

        private static string ControlDirectory => Path.Combine(Paths.BepInExRootPath, "coop");

        public static string RequestPath => Path.Combine(ControlDirectory, "save.request");

        public static string AckPath => Path.Combine(ControlDirectory, "save.ack");

        public static string QuitRequestPath => Path.Combine(ControlDirectory, "quit.request");

        public static string QuitAckPath => Path.Combine(ControlDirectory, "quit.ack");

        private string _lastToken = string.Empty;
        private string _lastQuitToken = string.Empty;

        private IEnumerator Start()
        {
            // Le canal existe même si le monde n'est pas encore chargé : un clic trop tôt doit répondre
            // « pas encore », pas disparaître.
            try
            {
                Directory.CreateDirectory(ControlDirectory);
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[contrôle] dossier de contrôle impossible à créer ({exception.GetType().Name}: {exception.Message}) — le bouton Sauvegarder du launcher restera sans effet.");
            }

            // Une demande **antérieure au démarrage** ne doit pas être rejouée : le launcher peut avoir écrit
            // un jeton lors d'un run précédent et avoir été fermé sans nettoyage. Sans cette base, chaque
            // démarrage du serveur déclencherait une sauvegarde que personne n'a demandée — constaté après
            // les essais du launcher, où `save.request` restait sur le disque.
            try
            {
                if (File.Exists(RequestPath))
                {
                    var stale = File.ReadAllText(RequestPath).Trim();
                    if (stale.Length > 0)
                    {
                        _lastToken = stale;
                        Log.LogInfo($"[contrôle] demande de sauvegarde antérieure ignorée (jeton {stale}) : elle n'a pas été émise pendant ce run.");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[contrôle] lecture de la demande antérieure impossible ({exception.GetType().Name}) — elle sera traitée comme une nouvelle demande si elle est encore là.");
            }

            // Même règle pour l'arrêt : une demande antérieure au démarrage ne doit pas tuer l'instance.
            try
            {
                if (File.Exists(QuitRequestPath))
                {
                    var staleQuit = File.ReadAllText(QuitRequestPath).Trim();
                    if (staleQuit.Length > 0)
                    {
                        _lastQuitToken = staleQuit;
                    }
                }
            }
            catch (Exception)
            {
                // Même raison que pour la sauvegarde : on réessaiera au prochain sondage.
            }

            yield return null;

            while (true)
            {
                yield return new WaitForSecondsRealtime(PollSeconds);
                Poll();
            }
        }

        private void Poll()
        {
            if (PollQuit())
            {
                return;
            }

            string token;
            try
            {
                if (!File.Exists(RequestPath))
                {
                    return;
                }

                token = File.ReadAllText(RequestPath).Trim();
            }
            catch (Exception)
            {
                // Fichier en cours d'écriture par le launcher : on réessaiera au prochain sondage.
                return;
            }

            if (token.Length == 0 || string.Equals(token, _lastToken, StringComparison.Ordinal))
            {
                return;
            }

            _lastToken = token;

            // En serveur dédié, `Player.main` est **volontairement** absent (personnage retiré) : exiger un
            // joueur rendrait le bouton Sauvegarder définitivement muet. Le monde, lui, est chargé — c'est
            // la scène `Main` qui le dit.
            var worldReady = Player.main != null
                || (CoopServerPlugin.Dedicated && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Main");
            if (!worldReady)
            {
                WriteAck(token, "attente", "le monde du serveur n'est pas encore chargé");
                return;
            }

            var menu = IngameMenu.main;
            if (menu == null)
            {
                menu = FindObjectOfType<IngameMenu>();
            }

            if (menu == null)
            {
                WriteAck(token, "erreur", "aucun IngameMenu dans la scène");
                return;
            }

            try
            {
                menu.SaveGame();
            }
            catch (Exception exception)
            {
                Log.LogError($"[contrôle] sauvegarde demandée par le launcher : appel en échec ({exception.GetType().Name}: {exception.Message}).");
                WriteAck(token, "erreur", $"{exception.GetType().Name}: {exception.Message}");
                return;
            }

            Log.LogInfo($"[contrôle] sauvegarde demandée par le launcher (jeton {token}) : IngameMenu.SaveGame() appelé.");
            WriteAck(token, "ok", "IngameMenu.SaveGame() appelé ; le jeu écrit la sauvegarde en arrière-plan");
        }

        /// <summary>
        /// Arrêt demandé par le launcher : en `-batchmode` il n'y a **aucune fenêtre** à fermer, donc
        /// `CloseMainWindow` ne peut pas arrêter le serveur. Ce canal est le seul arrêt propre possible : il
        /// laisse Unity dérouler `OnDestroy`, qui coupe le réseau et ferme le journal.
        ///
        /// Rend <c>true</c> si une demande d'arrêt vient d'être traitée (la boucle s'arrête alors).
        /// </summary>
        private bool PollQuit()
        {
            string token;
            try
            {
                if (!File.Exists(QuitRequestPath))
                {
                    return false;
                }

                token = File.ReadAllText(QuitRequestPath).Trim();
            }
            catch (Exception)
            {
                return false;
            }

            if (token.Length == 0 || string.Equals(token, _lastQuitToken, StringComparison.Ordinal))
            {
                return false;
            }

            _lastQuitToken = token;
            Log.LogWarning($"[contrôle] arrêt demandé par le launcher (jeton {token}) : Application.Quit().");

            try
            {
                Directory.CreateDirectory(ControlDirectory);
                File.WriteAllText(QuitAckPath, $"{token}|ok|arrêt demandé", new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // L'arrêt prime sur l'accusé de réception.
            }

            Application.Quit();
            return true;
        }

        private static void WriteAck(string token, string code, string message)
        {
            try
            {
                Directory.CreateDirectory(ControlDirectory);
                var content = $"{token}|{code}|{message}";
                File.WriteAllText(AckPath, content, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Log.LogWarning($"[contrôle] accusé de réception impossible à écrire ({exception.GetType().Name}: {exception.Message}).");
            }
        }
    }
}
