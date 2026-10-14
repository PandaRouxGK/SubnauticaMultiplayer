using System;

namespace Coop.Model
{
    /// <summary>Constantes partagées du protocole. Toute modif casse la compatibilité client/serveur.</summary>
    public static class Protocol
    {
        /// <summary>Version du protocole. Un client d'une autre version est refusé au handshake.</summary>
        public const int Version = 1;

        /// <summary>Port UDP par défaut du serveur.</summary>
        public const int DefaultPort = 7777;

        /// <summary>Clé de connexion LiteNetLib : filtre les paquets parasites avant même le handshake.</summary>
        public const string ConnectionKey = "coop-subnautica-v1";

        public const int MaxUsernameLength = 32;
        public const int MaxClients = 10;

        /// <summary>Intervalle d'envoi d'un Ping par le client, en millisecondes.</summary>
        public const int PingIntervalMs = 1000;

        /// <summary>Délai maximum accordé pour franchir le handshake, en secondes.</summary>
        public const int HandshakeTimeoutSeconds = 10;
    }

    /// <summary>Premier octet de chaque paquet : indique comment lire la suite.</summary>
    public enum MessageType : byte
    {
        HandshakeRequest = 1,
        HandshakeResponse = 2,
        Ping = 3,
        Pong = 4,
        Disconnect = 5,
    }

    /// <summary>Rôle de l'instance de jeu. Déterminé au lancement, jamais par la config partagée.</summary>
    public enum LaunchMode
    {
        Off,
        Server,
        Client,
    }

    /// <summary>
    /// Lit le rôle depuis la ligne de commande. On ne peut pas passer par un fichier de config :
    /// serveur et client partagent le même dossier de jeu, donc le même BepInEx/config.
    /// </summary>
    public static class LaunchModeParser
    {
        public const string ArgumentName = "-coopmode";
        public const string EnvironmentVariableName = "COOP_MODE";

        public static LaunchMode Parse(string[] args)
        {
            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg == null || !arg.StartsWith(ArgumentName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var separator = arg.IndexOf('=');
                    if (separator >= 0 && TryParseName(arg.Substring(separator + 1), out var mode))
                    {
                        return mode;
                    }
                }
            }

            var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            return TryParseName(fromEnvironment, out var envMode) ? envMode : LaunchMode.Off;
        }

        public static LaunchMode Current => Parse(Environment.GetCommandLineArgs());

        private static bool TryParseName(string value, out LaunchMode mode)
        {
            mode = LaunchMode.Off;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "server":
                    mode = LaunchMode.Server;
                    return true;
                case "client":
                    mode = LaunchMode.Client;
                    return true;
                case "off":
                    mode = LaunchMode.Off;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Paquet malformé ou tronqué : on préfère échouer bruyamment que lire n'importe quoi.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message)
        {
        }
    }
}
