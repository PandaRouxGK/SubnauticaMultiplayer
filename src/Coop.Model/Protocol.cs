using System;

namespace Coop.Model
{
    /// <summary>Constantes partagées du protocole. Toute modif casse la compatibilité client/serveur.</summary>
    public static class Protocol
    {
        /// <summary>
        /// Version du protocole. Un client d'une autre version est refusé au handshake.
        ///
        /// **9** : ajout de la **conduite autoritaire** d'un véhicule (`VehicleControl` /
        /// `VehicleControlResult`, M4). Un client v8 ne saurait pas envoyer son état de conduite : le
        /// serveur garderait le véhicule immobile chez les autres pendant que son conducteur le simule.
        /// **8** : ajout de la réclamation autoritaire d'un objet (`PickupClaim` / `PickupResult`, M5).
        /// Un client v7 refuserait de lire l'opcode 15 comme un message connu — le handshake le refuse
        /// avant, mais la version est la seule chose qui empêche un mélange silencieux.
        /// **7** : ajout du flux de véhicules (`VehicleSnapshot`). Un client v6 ne connaît pas l'opcode 14
        /// et laisserait les Seamoth du serveur chez lui, immobiles et simulés localement — le refus au
        /// handshake est préférable à cette divergence silencieuse.
        /// </summary>
        public const int Version = 9;

        /// <summary>Port UDP par défaut du serveur.</summary>
        public const int DefaultPort = 7777;

        /// <summary>Clé de connexion LiteNetLib : filtre les paquets parasites avant même le handshake.</summary>
        public const string ConnectionKey = "coop-subnautica-v9";

        public const int MaxUsernameLength = 32;
        public const int MaxClients = 10;

        /// <summary>Nombre maximum d'emplacements d'équipement dans un paquet <see cref="PlayerEquipmentMessage"/>.</summary>
        public const int MaxEquipmentSlots = 32;

        /// <summary>Longueur maximale d'un nom de <c>TechType</c> dans <see cref="PlayerEquipmentMessage"/>.</summary>
        public const int MaxTechTypeNameLength = 64;

        /// <summary>
        /// Portée maximale (m) à laquelle une réclamation de ramassage est acceptée (M5).
        ///
        /// Le serveur compare la **dernière position reçue** du joueur (20 Hz) à celle de l'objet. 6 m
        /// couvre la portée d'interaction du jeu (de l'ordre de 3 m) et le retard d'un ou deux messages de
        /// position pendant une nage, sans autoriser un ramassage à distance. Ce n'est pas un champ du
        /// protocole : c'est une règle du serveur, donc la v8 n'est pas rouverte.
        /// </summary>
        public const float MaxPickupRangeMeters = 6f;

        /// <summary>
        /// Portée maximale (m) à laquelle un client peut **conduire** un véhicule (M4, v9).
        ///
        /// Le serveur compare la dernière position reçue du joueur à celle du véhicule. 10 m couvre la
        /// montée/décente et le retard d'un message de position, sans permettre de piloter un Seamoth
        /// depuis l'autre bout de la carte.
        /// </summary>
        public const float MaxVehicleControlRangeMeters = 10f;

        /// <summary>
        /// Cadence d'envoi de l'état de conduite : c'est aussi la cadence de diffusion des véhicules
        /// (`VehicleSnapshot`), inutile de faire plus vite que ce que le serveur rediffuse.
        /// </summary>
        public const int VehicleControlIntervalMs = 100;

        /// <summary>Intervalle de vérification des changements d'équipement local côté client, en millisecondes.</summary>
        public const int EquipmentCheckIntervalMs = 250;

        /// <summary>Intervalle de vérification de l'objet tenu (outil / PDA) côté client, en millisecondes.</summary>
        public const int HeldItemCheckIntervalMs = 250;

        /// <summary>Préfixe des paramètres d'animation que le jeu pose pour chaque outil tenu.</summary>
        ///
        /// Vérifié dans le code du jeu, pas deviné : `PlayerTool.animToolName` renvoie
        /// `TechTypeExtensions.AsString(pickupable.GetTechType(), true)`, et `ArmsController` expose
        /// une règle dédiée pour `holding_welder`. Le paramètre est donc `holding_` + le nom technique
        /// du `TechType` en minuscules.
        public const string HoldingParamPrefix = "holding_";

        /// <summary>Nombre maximum de paramètres d'action transportés dans un <see cref="PlayerHeldItemMessage"/>.</summary>
        public const int MaxActionParams = 8;

        /// <summary>Intervalle d'envoi d'un Ping par le client, en millisecondes.</summary>
        public const int PingIntervalMs = 1000;

        // — Couleur de joueur (§27) ————————————————————————————————————————————

        /// <summary>
        /// Nombre d'entrées de la palette. Le serveur assigne les couleurs en tournant sur cette
        /// taille : deux joueurs simultanés ne peuvent donc pas se voir de la même couleur tant
        /// qu'ils sont au plus <see cref="PlayerColorCount"/>.
        /// </summary>
        public const int PlayerColorCount = 8;

        /// <summary>
        /// Palette, en composantes RVB linéaires (0–255).
        ///
        /// Ce ne sont pas des teintes inventées : la combinaison de plongée du jeu est un gris clair,
        /// et Nitrox la distingue en décalant la teinte de sa texture dans la bande 5°–45°
        /// (`HsvSwapper.SetHueRange(5f, 45f)`, `DiveSuitColorSwapManager`). Les huit teintes ci-dessous
        /// couvrent la même idée — des couleurs franchement distinctes une fois multipliées par ce gris
        /// — sans réécrire les pixels de la texture (voir `RemoteAvatarColor`).
        /// </summary>
        public static readonly byte[] PlayerColorsRgb =
        {
            255, 82, 82,     // rouge
            255, 152, 0,     // orange
            255, 235, 59,    // jaune
            76, 175, 80,     // vert
            0, 188, 212,     // cyan
            33, 150, 243,    // bleu
            156, 39, 176,    // violet
            255, 64, 129,    // rose
        };

        /// <summary>Composantes de la couleur <paramref name="index"/> (ramené dans la palette).</summary>
        public static void PlayerColorAt(int index, out byte r, out byte g, out byte b)
        {
            var count = PlayerColorCount;
            var slot = ((index % count) + count) % count;
            r = PlayerColorsRgb[slot * 3];
            g = PlayerColorsRgb[slot * 3 + 1];
            b = PlayerColorsRgb[slot * 3 + 2];
        }

        // — Créatures (§28) ————————————————————————————————————————————————————

        /// <summary>
        /// Taille maximale d'un paquet non fiable, **mesurée** et non supposée.
        ///
        /// LiteNetLib refuse un paquet Unreliable au-delà de cette taille, et le refuse en écrivant
        /// une `TooBigPacketException` dans le journal Unity — donc **hors** de nos logs : le premier
        /// envoi de créatures a été jeté comme ça, et côté mod rien ne le disait (64 créatures ×
        /// 36 octets = 2 304 octets, plus du double de la limite). La valeur vient du message de
        /// LiteNetLib lui-même : « exceeded maximum of 1023 bytes ».
        /// </summary>
        public const int MaxUnreliablePayloadBytes = 1023;

        /// <summary>
        /// Taille d'une entrée de créature sur le fil : clé 64 bits (8) + position et quaternion
        /// (7 × 4). C'est ce chiffre qui décide du plafond ci-dessous, et il est calculé ici plutôt
        /// qu'écrit en dur ailleurs : le jour où une entrée grossit, le plafond suit.
        /// </summary>
        public const int CreatureStateEntryBytes = 8 + 7 * 4;

        /// <summary>
        /// Nombre maximum de créatures dans un <see cref="CreatureSnapshotMessage"/>.
        ///
        /// Dérivé de la limite de paquet : 16 × 36 = 576 octets, plus l'en-tête (opcode, tick, compte)
        /// — largement sous les <see cref="MaxUnreliablePayloadBytes"/>, sans avoir à découper en
        /// plusieurs paquets. Le plafond précédent (64) ne tenait pas dans un paquet, et le test du
        /// selftest le vérifie désormais pour que ça ne puisse pas revenir.
        /// </summary>
        public const int MaxCreatureStates = 16;

        /// <summary>
        /// Rayon (m) autour d'un joueur connu dans lequel une créature est jugée « pertinente » et
        /// donc diffusée. Un client n'a rien à faire de la position d'une créature à 800 m : c'est la
        /// relevancy du §7, appliquée dès le premier envoi plutôt qu'après.
        /// </summary>
        public const float CreatureRelevancyRadiusMeters = 120f;

        /// <summary>
        /// Un envoi de créatures tous les <c>n</c> ticks : 20 Hz de positions de joueur suffisent,
        /// mais 20 Hz de créatures coûteraient le double de bande passante pour rien, elles bougent
        /// plus lentement qu'un joueur.
        /// </summary>
        public const int CreatureStreamIntervalTicks = 2;

        /// <summary>
        /// Nombre de paquets de créatures envoyés au maximum par tick et par client.
        ///
        /// Un paquet ne peut pas en porter plus de <see cref="MaxCreatureStates"/> (limite de taille
        /// non fiable), et n'en envoyer qu'un seul faisait tourner la liste des créatures suivies :
        /// mesuré, l'IA de la même créature était coupée puis rétablie **une fois par seconde**, ce qui
        /// est visible en jeu et absurde. Quatre paquets couvrent 64 créatures, l'essentiel de ce qui
        /// est à portée.
        /// </summary>
        public const int MaxCreatureChunksPerTick = 4;

        /// <summary>
        /// Durée (s) pendant laquelle une créature reste adoptée après le dernier état reçu. Passé ce
        /// délai, le client la relâche : l'IA reprend chez lui plutôt que de laisser une créature
        /// figée sur une position périmée.
        ///
        /// 2 s et non 1 s : une seconde suffit à ce qu'une créature momentanément hors de la liste
        /// diffusée soit relâchée puis adoptée à nouveau, et c'est exactement le va-et-vient mesuré.
        /// </summary>
        public const float CreatureAdoptionTimeoutSeconds = 2.0f;

        // — Véhicules (M4) ————————————————————————————————————————————————————————

        /// <summary>
        /// Un envoi de véhicules tous les <c>n</c> ticks, soit **10 Hz** comme les créatures.
        ///
        /// La mesure du §30 a tranché : à 10 Hz, un Seamoth à 20 m/s produit des pas de 2,15 m en
        /// moyenne — la bande que les créatures n'atteignent jamais. Monter à 20 Hz laissait encore
        /// 175 pas sur 177 dans la bande 1–2 m, et doubler à 40 Hz doublerait la bande passante pour un
        /// objet qui n'en a pas besoin. La cadence reste donc à 10 Hz, et c'est **l'interpolation côté
        /// client** qui ramène le pas visible à ~0,33 m par image à 60 fps.
        /// </summary>
        public const int VehicleStreamIntervalTicks = 2;

        /// <summary>
        /// Taille d'une entrée de véhicule sur le fil : clé 64 bits (8) + position et quaternion
        /// (7 × 4) — même forme que <see cref="CreatureStateEntryBytes"/>, et calculée de la même façon
        /// pour que le plafond suive le jour où l'entrée grossit.
        /// </summary>
        public const int VehicleStateEntryBytes = 8 + 7 * 4;

        /// <summary>
        /// Nombre maximum de véhicules dans un <see cref="VehicleSnapshotMessage"/>.
        ///
        /// Un monde contient quelques véhicules, pas des centaines : 16 × 36 = 576 octets, plus l'en-tête
        /// (opcode, tick, compte) restent largement sous <see cref="MaxUnreliablePayloadBytes"/>. Le
        /// plafond est là pour la lecture, pas pour découper un flux qui n'existe pas.
        /// </summary>
        public const int MaxVehicleStates = 16;

        /// <summary>
        /// Rayon (m) autour d'un joueur dans lequel un véhicule est jugé pertinent.
        ///
        /// Plus large que celui des créatures (120 m) : un Seamoth se voit de loin, et surtout il se
        /// **conduit** de loin — le perdre à 121 m ferait sauter sa position à chaque passage de la
        /// frontière. La valeur reste bornée pour ne pas diffuser tout le monde à tout le monde.
        /// </summary>
        public const float VehicleRelevancyRadiusMeters = 150f;

        /// <summary>
        /// Durée (s) pendant laquelle un véhicule reste suivi après la dernière annonce reçue. Passé ce
        /// délai, le client le relâche : rendre la main à la simulation locale vaut mieux que laisser un
        /// Seamoth figé sur une position périmée.
        /// </summary>
        public const float VehicleTrackingTimeoutSeconds = 2.0f;

        /// <summary>Intervalle d'envoi de la position du joueur local par le client, en millisecondes (20 Hz).</summary>
        public const int PositionSendIntervalMs = 50;

        /// <summary>Identifiant réservé au personnage de l'instance serveur (hôte/simulation).</summary>
        public const ushort ServerHostPlayerId = 0;

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
        PlayerJoined = 6,
        PlayerLeft = 7,
        PlayerPosition = 8,
        PlayersSnapshot = 9,
        PlayerEquipment = 10,
        PlayerHeldItem = 11,

        /// <summary>Couleur assignée à un joueur par le serveur (ReliableOrdered, protocole v6).</summary>
        PlayerColor = 12,

        /// <summary>État des créatures simulées par le serveur (Unreliable, 10 Hz, protocole v6).</summary>
        CreatureSnapshot = 13,

        /// <summary>État des véhicules du monde serveur (Unreliable, 10 Hz, protocole v7).</summary>
        VehicleSnapshot = 14,

        /// <summary>Réclamation d'un objet du monde, émis par le client (ReliableOrdered, protocole v8, M5).</summary>
        PickupClaim = 15,

        /// <summary>Décision du serveur sur une réclamation, envoyée au seul demandeur (ReliableOrdered, v8).</summary>
        PickupResult = 16,

        /// <summary>État de conduite d'un véhicule, émis par le client qui le pilote (v9, M4).</summary>
        VehicleControl = 17,

        /// <summary>Décision du serveur sur une conduite, envoyée au seul demandeur (ReliableOrdered, v9).</summary>
        VehicleControlResult = 18,
    }

    /// <summary>
    /// État d'action instantané du joueur, transporté à 20 Hz dans les paquets de position.
    ///
    /// Ces booléens changent vite (un clic, un changement de véhicule) : ils voyagent donc dans le
    /// flux rapide et non dans un paquet fiable, sinon chaque coup porterait un aller-retour.
    /// </summary>
    [Flags]
    public enum PlayerActionFlags : byte
    {
        None = 0,

        /// <summary><c>using_pda</c> : le PDA est sorti et tenu.</summary>
        UsingPda = 1,

        /// <summary><c>using_tool</c> : l'outil tenu est en cours d'utilisation.</summary>
        UsingTool = 2,

        /// <summary><c>using_tool_alt</c> : utilisation alternative de l'outil.</summary>
        UsingToolAlt = 4,

        /// <summary><c>in_seamoth</c> : le joueur pilote un Seamoth.</summary>
        InSeamoth = 8,

        /// <summary><c>in_exosuit</c> : le joueur pilote un Exosuit.</summary>
        InExosuit = 16,
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

    /// <summary>
    /// Empreinte stable d'une chaîne, partagée par les deux plugins.
    ///
    /// Pourquoi pas <c>string.GetHashCode()</c> : il n'est **pas** garanti stable d'un processus à
    /// l'autre (et il ne l'est pas en pratique sur .NET Framework pour ce cas d'usage), alors que
    /// serveur et client doivent calculer la même clé pour la même créature. FNV-1a 64 bits est
    /// déterministe, sans dépendance, et suffisant pour un identifiant local — ce n'est pas une
    /// primitive cryptographique et ça ne prétend pas l'être.
    /// </summary>
    public static class StableHash
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static long Of(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            var hash = Offset;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                hash ^= (byte)(c & 0xFF);
                hash *= Prime;
                hash ^= (byte)(c >> 8);
                hash *= Prime;
            }

            return unchecked((long)hash);
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
