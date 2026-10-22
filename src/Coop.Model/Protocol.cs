using System;

namespace Coop.Model
{
    /// <summary>Constantes partagées du protocole. Toute modif casse la compatibilité client/serveur.</summary>
    public static class Protocol
    {
        /// <summary>
        /// Version du protocole. Un client d'une autre version est refusé au handshake.
        ///
        /// **21** : l'**inventaire persistant** et le **craft payé**. Le serveur adopte une fois l'inventaire
        /// réel du joueur (`InventoryAdopt` #31), le conserve sur disque pour la session, et vérifie le
        /// coût d'un craft (`CraftRequest` #32 / `CraftResult` #33) sur **les recettes du jeu** avant de
        /// débiter le registre ; un matériau manquant est nommé. Le produit reste fabriqué par le client :
        /// ce lot arbitre le **coût**, pas encore l'instanciation.
        ///
        /// **20** : le **débit** de l'inventaire serveur (`InventoryDebit` #28 / `InventoryDebitResult` #29),
        /// et la **réconciliation** chez le client. Un client peut désormais demander qu'une quantité
        /// quitte **son** registre (consommation, dépôt) — le serveur refuse nommément ce qu'il ne tient
        /// pas — et l'instantané qui suit fait **retirer de `Inventory.main`** l'objet que le registre ne
        /// contient plus. Le débit ne peut que **diminuer** : aucun message ne permet d'ajouter un objet.
        /// **19** : l'**inventaire du joueur** côté serveur (`InventorySnapshot`, #27) : le serveur tient le
        /// registre de ce que chaque joueur possède et l'annonce à son propriétaire après chaque ramassage
        /// accordé — c'est le socle du coût de fabrication. Aucun message ne permet à un client d'**ajouter**
        /// un objet à ce registre : un ajout ne vient que d'une décision du serveur.
        /// **18** : l'**heure du monde** (`WorldTime`, #26) : le serveur diffuse son `DayNightCycle` à
        /// 1 Hz, et un client qui rejoint (ou dont l'horloge a dérivé) se recale. Sans ce message, deux
        /// instances du même monde pouvaient vivre deux moments différents de la journée.
        /// **17** : les **dégâts des créatures** (M3/M4) : la fraction de vie voyage dans l'entrée de
        /// créature (#13), et la mort est une **décision du serveur** annoncée par `EntityDespawn` avec
        /// `EntityKind.Creature` — un client ne peut pas tuer une créature que le serveur simule encore.
        /// Sans ça, un poisson abattu chez l'hôte restait vivant chez les autres, et un poisson mort
        /// côté serveur était relâché chez le client avec son IA rétablie.
        /// **16** : deux manques nommés de M4, fermés ensemble parce qu'ils touchent tous les deux un
        /// véhicule qui n'existe pas encore partout :
        /// (a) **santé** dans `VehicleStateEntry` — un véhicule endommagé chez l'hôte restait intact chez
        /// les autres ; la fraction voyage dans le flux déjà en place, avec la sentinelle `-1` de
        /// l'énergie (`0` = détruit, pas « inconnu ») ;
        /// (b) **fabrication demandée par un client** (`EntitySpawnRequest` / `EntitySpawnResult`) —
        /// le client ne fabrique rien : il demande, le serveur arbitre puis fabrique, et l'annonce
        /// existante crée la copie chez tout le monde, demandeur compris.
        /// **15** : ajout des **modules** (`VehicleUpgrades`, M4) : l'ensemble installé voyage à part de
        /// la position, en fiable, parce qu'un module manquant change la profondeur et la charge utile
        /// du véhicule.
        /// **14** : ajout de l'**énergie** dans `VehicleStateEntry` (M4) — la charge normalisée voyage
        /// avec la position, au même rythme, plutôt que dans un message séparé.
        /// **13** : trois ajouts d'un seul coup, parce qu'ils changent tous les trois l'admission ou
        /// l'apparition d'entités, et qu'un client v12 ne saurait lire aucun des trois :
        /// (a) **empreinte de sauvegarde** dans le `HandshakeRequest` (slot, session, changeSet, mode) —
        /// sans elle, un client sur une vieille partie désynchronisait silencieusement (§43.2) ;
        /// (b) **`PlayerCorrection`** (#22) — le serveur peut enfin opposer un refus de position au
        /// client au lieu de relayer n'importe quel saut (§43.3) ;
        /// (c) **définition + `definitionHash`** dans `EntitySpawn` — le client recalcule l'empreinte
        /// avant d'instancier et refuse une définition divergente (P1bis).
        /// **12** : ajout de la **libération du volant** (`VehicleRelease`, M4) : le conducteur rend le
        /// véhicule quand il le quitte, et le serveur le libère aussi après un silence de
        /// `VehicleSilenceReleaseSeconds`. Sans ce message, un véhicule restait attribué à son dernier
        /// conducteur jusqu'à sa déconnexion — il fallait relancer le serveur pour le reprendre.
        /// **11** : ajout du **retrait d'entité** (`EntityDespawn`, M6) : ce que le serveur fait disparaître
        /// — un véhicule détruit, un objet ramassé — est annoncé aux clients. Sans ce message, une entité
        /// créée par la v10 restait chez tout le monde après sa destruction chez l'hôte, et un objet
        /// ramassé restait visible (et réclamable) pour les autres joueurs.
        /// **10** : ajout de la **création d'entité** (`EntitySpawn`, M6) : le serveur annonce le véhicule
        /// qu'il vient de fabriquer, et le client l'instancie au même endroit avec la **même** identité.
        /// Sans ce message, un `spawn` côté serveur n'existe pas chez les clients — ils le voient dans le
        /// flux de positions (clé inconnue) mais n'ont aucun objet à déplacer.
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
        public const int Version = 21;

        /// <summary>Port UDP par défaut du serveur.</summary>
        public const int DefaultPort = 7777;

        /// <summary>Clé de connexion LiteNetLib : filtre les paquets parasites avant même le handshake.</summary>
        public const string ConnectionKey = "coop-subnautica-v21";

        public const int MaxUsernameLength = 32;
        public const int MaxClients = 10;

        /// <summary>
        /// Longueur maximale annoncée pour la version du jeu (`2019.4.36f1`, `71288`). Le handshake
        /// n'est pas un canal de texte libre : sans borne, un client pouvait envoyer 64 Ko dans ce
        /// champ, que le serveur comparait puis refusait — un paquet valide mais inutilement lourd.
        /// </summary>
        public const int MaxGameVersionLength = 32;

        /// <summary>
        /// Longueur maximale de l'empreinte de la liste de mods : 16 caractères hexadécimaux produits
        /// par <see cref="ModListHash.Compute"/> ; la borne laisse de la marge pour un futur schéma.
        /// </summary>
        public const int MaxModListHashLength = 64;

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

        /// <summary>
        /// Silence au bout duquel le serveur **libère** le volant d'un conducteur (v12).
        ///
        /// Dimensionné sur la cadence d'envoi : le client envoie un état toutes les 100 ms tant qu'il est au
        /// volant, donc 3 s représentent 30 états perdus — bien au-delà d'une rafale de latence, et bien en
        /// dessous du délai qu'un joueur attendrait pour reprendre un véhicule laissé.
        /// </summary>
        public const float VehicleSilenceReleaseSeconds = 3f;

        /// <summary>Longueur maximale d'un identifiant d'entité transporté dans un <see cref="EntitySpawnMessage"/>.</summary>
        public const int MaxEntityIdLength = 64;

        /// <summary>Longueur maximale du motif d'un <see cref="EntityDespawnMessage"/> (journal, pas décision).</summary>
        public const int MaxDespawnReasonLength = 96;

        /// <summary>
        /// Longueur maximale d'un motif **lisible** transporté par le protocole (déconnexion, départ,
        /// refus de handshake). Un motif est journalisé et affiché tel quel ; sans borne, un pair pouvait
        /// faire transporter 64 Ko de texte dans un champ qui finit dans un journal.
        /// </summary>
        public const int MaxReasonTextLength = 160;

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
        /// (7 × 4) + santé (v17, 4). C'est ce chiffre qui décide du plafond ci-dessous, et il est calculé
        /// ici plutôt qu'écrit en dur ailleurs : le jour où une entrée grossit, le plafond suit.
        /// </summary>
        public const int CreatureStateEntryBytes = 8 + 8 * 4;

        /// <summary>
        /// Nombre maximum de créatures dans un <see cref="CreatureSnapshotMessage"/>.
        ///
        /// Dérivé de la limite de paquet : 16 × 40 = 640 octets, plus l'en-tête (opcode, tick, compte)
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
        /// (7 × 4) + énergie (v14) + santé (v16) — soit 44 octets, même forme que
        /// <see cref="CreatureStateEntryBytes"/>, et calculée de la même façon pour que le plafond suive le
        /// jour où l'entrée grossit.
        /// </summary>
        public const int VehicleStateEntryBytes = 8 + 9 * 4;

        /// <summary>
        /// Nombre maximum de véhicules dans un <see cref="VehicleSnapshotMessage"/>.
        ///
        /// Un monde contient quelques véhicules, pas des centaines : 16 × 36 = 576 octets, plus l'en-tête
        /// (opcode, tick, compte) restent largement sous <see cref="MaxUnreliablePayloadBytes"/>. Le
        /// plafond est là pour la lecture, pas pour découper un flux qui n'existe pas.
        /// </summary>
        public const int MaxVehicleStates = 16;

        /// <summary>
        /// Nombre maximal de modules annoncés pour un véhicule. Un Seamoth en porte 4, un Prawn 4, un
        /// Cyclops 6 : la borne est au-dessus du plus gros véhicule du jeu, et un message qui la dépasse
        /// est refusé plutôt que tronqué en silence.
        /// </summary>
        public const int MaxVehicleUpgrades = 8;

        /// <summary>
        /// Nombre maximal d'**objets distincts** dans l'inventaire serveur d'un joueur.
        ///
        /// Ce n'est pas la taille de l'inventaire du jeu (24 emplacements) : c'est la borne du conteneur qui
        /// décide, pour qu'un client ne puisse pas faire grossir un dictionnaire sans limite. La valeur est
        /// le double de la place réelle, donc elle ne bloque aucun joueur légitime.
        /// </summary>
        public const int MaxInventoryEntries = 48;

        /// <summary>
        /// Quantité maximale d'un même objet dans le registre serveur. Au-delà, l'ajout est refusé
        /// (`InventoryFull`) plutôt que tronqué : une quantité tronquée silencieusement serait un objet
        /// perdu sans que personne ne le voie.
        /// </summary>
        public const int MaxInventoryStack = 9999;

        /// <summary>
        /// Un envoi d'heure du monde tous les <c>n</c> ticks, soit **1 Hz**. L'heure avance lentement à
        /// l'échelle d'une session : vingt envois par seconde seraient du gaspillage, un par seconde suffit
        /// à recaler un client dont l'horloge a dérivé de quelques secondes.
        /// </summary>
        public const int WorldTimeIntervalTicks = 20;

        /// <summary>
        /// Écart (s) au-delà duquel le client écrit l'heure du serveur. En dessous, il laisse son horloge
        /// locale avancer : réécrire à chaque paquet ferait sauter le soleil de la même quantité, et
        /// l'écart infime d'un aller-retour n'a aucune valeur de jeu.
        ///
        /// **5 s, pas 0,5 s** : mesuré à l'étape AA le 2026-10-21, deux instances du même monde gardent
        /// un écart **stable** de `0,6 à 1,1 s` (leur horloge n'avance pas exactement au même rythme).
        /// Avec un seuil de `0,5 s`, le client réécrivait l'heure **à chaque message, une fois par
        /// seconde**, pour courir après cet écart constant — un soleil qui saute d'une seconde chaque
        /// seconde, pour rien. Cinq secondes est en dessous du visible (un jour dure 1 200 s) et au-dessus
        /// du bruit mesuré.
        /// </summary>
        public const float WorldTimeApplyThresholdSeconds = 5f;

        /// <summary>
        /// Distance (m) maximale entre le joueur demandeur et la position où il demande une fabrication.
        ///
        /// Ce n'est pas une règle de confort : sans elle, un client pourrait demander un Seamoth à l'autre
        /// bout de la carte, c'est-à-dire créer une entité hors de sa portée et hors de toute simulation.
        /// La position comparée est la dernière **acceptée** par la garde P1, pas celle que le client
        /// prétend avoir.
        /// </summary>
        public const float MaxEntitySpawnDistanceMeters = 8f;

        /// <summary>Délai (s) minimal entre deux fabrications accordées à la même session.</summary>
        public const float EntitySpawnCooldownSeconds = 5f;

        /// <summary>
        /// Nombre maximal de fabrications accordées à une session. Un joueur construit quelques véhicules
        /// dans une partie ; au-delà, la demande est refusée avec un motif, plutôt que de laisser un client
        /// peupler le monde du serveur.
        /// </summary>
        public const int MaxEntitySpawnsPerSession = 8;

        /// <summary>Longueur maximale d'un motif de refus d'apparition (journal + message fiable).</summary>
        public const int MaxSpawnReasonLength = 96;

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

        // — Empreinte de sauvegarde (P0, v13) ————————————————————————————————————————

        /// <summary>Longueur maximale d'un nom de slot (`slot0000`).</summary>
        public const int MaxSaveSlotLength = 64;

        /// <summary>Longueur maximale d'un identifiant de session de sauvegarde (GUID).</summary>
        public const int MaxSaveSessionLength = 64;

        /// <summary>Longueur maximale d'un nom de mode de jeu.</summary>
        public const int MaxGameModeLength = 16;

        /// <summary>
        /// Délai accordé au client pour résoudre son empreinte de sauvegarde avant de se connecter.
        ///
        /// AutoLoadBehaviour ouvre le panneau de chargement ~6 s après le menu ; 20 s couvre un
        /// chargement de disque lent sans laisser le client boucler indéfiniment en silence.
        /// </summary>
        public const float SaveIdentityResolveTimeoutSeconds = 20f;

        // — Positions des joueurs (P1, v13) ————————————————————————————————————————————

        /// <summary>Longueur maximale du motif d'une `PlayerCorrection` (journal, pas décision).</summary>
        public const int MaxCorrectionReasonLength = 96;

        /// <summary>
        /// Tolérance accordée au client pour appliquer une correction : en dessous, la correction est
        /// comptée mais **pas appliquée** (éviter de téléporter le joueur pour un centimètre).
        /// </summary>
        public const float CorrectionApplyThresholdMeters = 0.75f;
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

        /// <summary>Annonce d'une entité fabriquée par le serveur (ReliableOrdered, v10, M6).</summary>
        EntitySpawn = 19,

        /// <summary>Annonce de la disparition d'une entité (ReliableOrdered, v11, M6).</summary>
        EntityDespawn = 20,

        /// <summary>Libération du volant par son conducteur (ReliableOrdered, v12, M4).</summary>
        VehicleRelease = 21,

        /// <summary>Position autoritaire renvoyée à un client dont la position a été refusée (v13, P1).</summary>
        PlayerCorrection = 22,

        /// <summary>
        /// Modules installés sur un véhicule (ReliableOrdered, v15, M4). Fiabilité **obligatoire** :
        /// un module manquant change la profondeur et la charge utile du véhicule, donc il ne peut pas
        /// être perdu comme un état de position (le prochain le remplacerait).
        /// </summary>
        VehicleUpgrades = 23,

        /// <summary>
        /// Demande de fabrication d'une entité, émise par un client (ReliableOrdered, v16).
        ///
        /// Le client ne fabrique **rien** localement : il demande, le serveur décide. Sans ce message, une
        /// entité créée chez un client n'existerait pas chez les autres et le demandeur serait le seul à la
        /// voir — exactement ce que le modèle autoritaire interdit.
        /// </summary>
        EntitySpawnRequest = 24,

        /// <summary>Décision du serveur sur une demande de fabrication, au seul demandeur (v16).</summary>
        EntitySpawnResult = 25,

        /// <summary>
        /// Heure du monde (Unreliable, 1 Hz, v18). Le serveur envoie son `DayNightCycle` ; le client se
        /// recale quand l'écart dépasse un seuil, plutôt que d'écrire l'heure à chaque paquet.
        /// </summary>
        WorldTime = 26,

        /// <summary>
        /// Inventaire du joueur, poussé par le serveur à son propriétaire (ReliableOrdered, v19).
        ///
        /// Le serveur en tient le registre autoritaire : il l'annonce à l'admission (vide) puis après
        /// chaque **ramassage accordé**. Aucun message client ne peut y ajouter un objet — c'est ce qui
        /// distingue un registre d'une déclaration.
        /// </summary>
        InventorySnapshot = 27,

        /// <summary>
        /// Demande de **débit** d'inventaire, émise par un client (ReliableOrdered, v20).
        ///
        /// Le client ne retire rien de son propre chef : il demande, le serveur vérifie ce que **son**
        /// registre contient, puis décide. Un débit ne peut que diminuer — le protocole n'expose toujours
        /// aucune opération d'ajout, donc un client ne peut pas s'inventer un objet en « annulant » un
        /// débit.
        /// </summary>
        InventoryDebit = 28,

        /// <summary>
        /// Décision du serveur sur un débit, au seul demandeur (v20). Elle porte le refus **nommé**
        /// (`UnknownItem`, `NotEnough`, `Invalid`) : un refus silencieux laisserait le client croire que
        /// l'objet a été consommé alors que le registre ne l'a jamais contenu.
        /// </summary>
        InventoryDebitResult = 29,

        /// <summary>
        /// Demande d'**adoption** de l'inventaire (v21), du serveur vers un joueur qui vient d'être admis.
        ///
        /// Le serveur ne peut pas lire la sauvegarde d'un client : c'est le client qui envoie, **une fois**,
        /// ce que `Inventory.main` contient. Sans cette adoption, le registre ne verrait que les objets
        /// ramassés pendant la session et refuserait de payer un craft avec des matériaux sauvegardés.
        /// </summary>
        InventoryAdoptionRequest = 30,

        /// <summary>
        /// Inventaire initial d'un joueur (v21), en réponse à une demande d'adoption. Bornes et tri sont
        /// ceux de l'instantané : c'est la même vérité, dans l'autre sens.
        /// </summary>
        InventoryAdopt = 31,

        /// <summary>
        /// Demande de **craft** (v21). Le client ne paie rien lui-même : il demande, le serveur lit la
        /// recette du jeu, vérifie le registre et débite — ou refuse en nommant le matériau manquant.
        /// </summary>
        CraftRequest = 32,

        /// <summary>Décision du serveur sur un craft, au seul demandeur (v21).</summary>
        CraftResult = 33,
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

                    // Le nom doit être **exact** : `StartsWith` seul acceptait `-coopmodeevil=server`,
                    // c'est-à-dire un argument d'un autre outil prenant le rôle du nôtre. On exige le
                    // séparateur juste après `-coopmode`.
                    if (arg.Length <= ArgumentName.Length || arg[ArgumentName.Length] != '=')
                    {
                        continue;
                    }

                    if (TryParseName(arg.Substring(ArgumentName.Length + 1), out var mode))
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
