using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace Coop.Shared
{
    /// <summary>
    /// Écrit un fichier de log propre à l'instance.
    ///
    /// Pourquoi ce n'est pas juste <c>BepInEx/LogOutput.log</c> : ce fichier a un nom fixe, donc deux
    /// instances lancées depuis le même dossier de jeu (un serveur et un client sur la même machine)
    /// écrivent dedans en même temps. Résultat observé au jalon M1 : le second processus n'arrivait
    /// pas à ouvrir le fichier et **ses logs devenaient invisibles** — impossible de déboguer le
    /// client pendant qu'un serveur tournait. BepInEx n'expose pas le chemin du log en configuration,
    /// d'où ce listener.
    ///
    /// Un seul fichier par processus : le listener n'est branché que par le plugin dont le rôle
    /// correspond au lancement (l'autre plugin est inactif et n'écrit rien).
    ///
    /// Compilé dans plusieurs projets via un &lt;Compile Include&gt; partagé.
    /// </summary>
    internal sealed class InstanceLogFile : ILogListener, IDisposable
    {
        /// <summary>Nom du fichier par rôle, sous <c>BepInEx/logs/</c>.</summary>
        public static string PathFor(string role) =>
            Path.Combine(Paths.BepInExRootPath, "logs", $"coop-{role}.log");

        private readonly object _gate = new object();
        private readonly StreamWriter _writer;

        public string FilePath { get; }

        private InstanceLogFile(string filePath, StreamWriter writer)
        {
            FilePath = filePath;
            _writer = writer;
        }

        /// <summary>
        /// Branche le listener sur le pipeline global de BepInEx et renvoie l'instance, ou <c>null</c>
        /// si le fichier n'a pas pu être ouvert (on ne fait jamais échouer le jeu pour un log).
        /// </summary>
        public static InstanceLogFile Attach(ManualLogSource logger, string role)
        {
            var path = PathFor(role);

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // FileMode.Create : on veut voir l'exécution courante, pas un empilement de fichiers.
                // FileShare.Read : un éditeur peut lire le fichier pendant que le jeu tourne.
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var listener = new InstanceLogFile(path, writer);
                listener.WriteHeader(role);

                // Collection globale : le listener reçoit les messages de toutes les sources,
                // y compris ceux du jeu et de l'autre plugin du même processus.
                Logger.Listeners.Add(listener);

                // Journalisé APRÈS le branchement pour que le chemin apparaisse dans le fichier lui-même.
                logger.LogInfo($"[boot] log d'instance : {path}");
                return listener;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    $"[boot] impossible d'ouvrir le log d'instance '{path}' ({exception.GetType().Name}: {exception.Message}) — " +
                    "les logs restent dans BepInEx/LogOutput.log");
                return null;
            }
        }

        private void WriteHeader(string role)
        {
            var header = $"=== Coop {role} — {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {System.Diagnostics.Process.GetCurrentProcess().Id}) ===";
            lock (_gate)
            {
                try
                {
                    _writer.WriteLine(header);
                    _writer.WriteLine($"=== les messages antérieurs au chargement du plugin ne sont pas repris ici ===");
                }
                catch (Exception)
                {
                    // Un en-tête raté ne doit pas empêcher la suite.
                }
            }
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            var source = eventArgs.Source?.SourceName ?? "?";
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{eventArgs.Level,-7}] [{source}] {eventArgs.Data}";

            lock (_gate)
            {
                try
                {
                    _writer.WriteLine(line);
                }
                catch (Exception)
                {
                    // Le logging ne doit jamais faire tomber le jeu : on avale l'erreur d'écriture.
                }
            }
        }

        public void Dispose()
        {
            try
            {
                Logger.Listeners.Remove(this);
            }
            catch (Exception)
            {
                // détail sans importance à l'arrêt
            }

            lock (_gate)
            {
                try
                {
                    _writer.Dispose();
                }
                catch (Exception)
                {
                    // idem
                }
            }
        }
    }
}
