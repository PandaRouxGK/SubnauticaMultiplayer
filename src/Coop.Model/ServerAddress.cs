using System;
using System.Globalization;

namespace Coop.Model
{
    /// <summary>
    /// Adresse d'un serveur coop, telle qu'un joueur la tape : `IP` ou `nom d'hôte`, avec un port
    /// facultatif (`203.0.113.10:7777`).
    ///
    /// Pourquoi ce type existe : l'adresse vivait uniquement dans `BepInEx/config`, lue au démarrage
    /// (`[Reseau] Hote`, défaut `127.0.0.1`). Deux joueurs sur le même réseau s'en accommodaient ; dès
    /// qu'ils sont séparés par Internet, l'adresse doit être **saisie** — et une adresse saisie peut être
    /// fausse. La règle de lecture est donc ici, testable sans Unity, plutôt que répartie entre un
    /// `InputField` et un `Connect()`.
    /// </summary>
    public sealed class ServerAddress
    {
        public ServerAddress(string host, ushort port)
        {
            Host = host;
            Port = port;
        }

        /// <summary>Nom d'hôte ou IPv4, sans port.</summary>
        public string Host { get; }

        public ushort Port { get; }

        public override string ToString() =>
            Host + ":" + Port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Lit l'adresse du serveur depuis `-coopserver=…` ou `COOP_SERVER`, et valide une saisie.
    ///
    /// Une valeur **explicitement invalide** est un refus nommé, jamais un repli silencieux sur
    /// `127.0.0.1` : un client qui se rabattrait sur la boucle locale afficherait « connexion impossible »
    /// alors que le vrai problème est l'adresse tapée, et le joueur chercherait la panne ailleurs.
    /// </summary>
    public static class ServerAddressPreference
    {
        public const string ArgumentName = "-coopserver";
        public const string EnvironmentVariableName = "COOP_SERVER";

        /// <summary>Borne d'un nom d'hôte DNS (RFC 1035, point final exclu).</summary>
        public const int MaxHostLength = 253;

        public const ushort MinPort = 1;
        public const ushort MaxPort = 65535;

        /// <summary>
        /// Valeur brute fournie par la ligne de commande ou l'environnement, ou <c>null</c> si aucune.
        /// Le nom doit être **exact** : `-coopserverevil=x` appartient à un autre outil (même règle que
        /// `-coopmode` et `-coopsave`).
        /// </summary>
        public static string Raw(string[] args)
        {
            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg == null || !arg.StartsWith(ArgumentName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (arg.Length <= ArgumentName.Length || arg[ArgumentName.Length] != '=')
                    {
                        continue;
                    }

                    return arg.Substring(ArgumentName.Length + 1).Trim();
                }
            }

            return Environment.GetEnvironmentVariable(EnvironmentVariableName)?.Trim();
        }

        /// <summary>
        /// Valide et découpe une adresse. <paramref name="error"/> nomme précisément ce qui ne va pas :
        /// « port invalide », « IPv4 invalide », « IPv6 non pris en charge »… — un message générique
        /// « adresse invalide » ne dit pas quoi corriger.
        /// </summary>
        public static bool TryParse(string raw, ushort defaultPort, out ServerAddress address, out string error)
        {
            address = null;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "adresse vide";
                return false;
            }

            var text = raw.Trim();

            if (text.Contains("://"))
            {
                error = "adresse attendue sans schéma (ex. 203.0.113.10:7777)";
                return false;
            }

            // IPv6 : LiteNetLib résout en IPv4 dans ce projet, et une adresse `[::1]:7777` serait
            // acceptée par un parseur naïf puis échouerait à la connexion. On refuse en le nommant.
            var colons = 0;
            foreach (var character in text)
            {
                if (character == ':')
                {
                    colons++;
                }
            }

            if (colons > 1)
            {
                error = "IPv6 non pris en charge (IPv4 ou nom d'hôte attendu)";
                return false;
            }

            var host = text;
            var port = defaultPort;

            if (colons == 1)
            {
                var separator = text.LastIndexOf(':');
                host = text.Substring(0, separator).Trim();
                var portText = text.Substring(separator + 1).Trim();

                if (portText.Length == 0)
                {
                    error = "port manquant après « : »";
                    return false;
                }

                if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)
                    || parsedPort < MinPort
                    || parsedPort > MaxPort)
                {
                    error = $"port invalide : « {portText} » (entier {MinPort}..{MaxPort} attendu)";
                    return false;
                }

                port = (ushort)parsedPort;
            }

            if (host.Length == 0)
            {
                error = "adresse vide";
                return false;
            }

            if (host.Length > MaxHostLength)
            {
                error = $"adresse trop longue ({host.Length} > {MaxHostLength})";
                return false;
            }

            var ipv4Candidate = true;
            foreach (var character in host)
            {
                var digitOrDot = (character >= '0' && character <= '9') || character == '.';
                if (!digitOrDot)
                {
                    ipv4Candidate = false;
                    break;
                }
            }

            if (ipv4Candidate)
            {
                if (!IsIpv4(host, out var ipv4Error))
                {
                    error = ipv4Error;
                    return false;
                }
            }
            else if (!IsHostname(host))
            {
                error = $"nom d'hôte invalide : « {host} » (lettres, chiffres, « . » et « - » attendus)";
                return false;
            }

            address = new ServerAddress(host, port);
            return true;
        }

        /// <summary>Adresse imposée par l'argument ou l'environnement, ou <c>null</c> si aucune.</summary>
        public static ServerAddress Current(ushort defaultPort) =>
            TryParse(Raw(Environment.GetCommandLineArgs()), defaultPort, out var address, out _) ? address : null;

        /// <summary>
        /// Quatre octets séparés par des points, chacun 0..255. Un hôte fait uniquement de chiffres et de
        /// points est une tentative d'IPv4 : la refuser précisément vaut mieux que de la passer à un
        /// résolveur DNS qui rendrait une erreur de connexion opaque.
        /// </summary>
        private static bool IsIpv4(string host, out string error)
        {
            error = null;
            var parts = host.Split('.');
            if (parts.Length != 4)
            {
                error = $"adresse IPv4 invalide : « {host} » (4 octets attendus)";
                return false;
            }

            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 3)
                {
                    error = $"adresse IPv4 invalide : « {host} » (octet vide ou trop long)";
                    return false;
                }

                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet) || octet > 255)
                {
                    error = $"adresse IPv4 invalide : « {host} » (octet hors 0..255)";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Nom DNS plausible. On n'interroge pas le résolveur ici : un nom valide peut être indisponible,
        /// et cette méthode sert à refuser une **faute de frappe**, pas à prouver la joignabilité.
        /// </summary>
        private static bool IsHostname(string host)
        {
            if (host[0] == '-' || host[0] == '.' || host[host.Length - 1] == '-' || host[host.Length - 1] == '.')
            {
                return false;
            }

            foreach (var character in host)
            {
                var allowed = (character >= 'a' && character <= 'z')
                              || (character >= 'A' && character <= 'Z')
                              || (character >= '0' && character <= '9')
                              || character == '.'
                              || character == '-';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Forme affichable d'une saisie, sans jeter sur une valeur absente (journal, panneau).</summary>
        public static string Describe(string raw) => string.IsNullOrWhiteSpace(raw) ? "(vide)" : raw.Trim();
    }
}
