# Protocole réseau — référence

Ce document décrit **ce que le code fait aujourd'hui**, message par message, avec la justification de chaque
constante. Les jalons, les mesures et les décisions sont dans `PLAN.md` ; ici, c'est le contrat.

Tout ce qui suit est lu dans le code, et chaque ligne nomme sa source. Une valeur qui vient d'une mesure
porte la mesure ; une valeur choisie le dit.

---

## 1. Version et transport

| Élément | Valeur | Source |
|---|---|---|
| Version du protocole | `12` | `Protocol.Version` (`src/Coop.Model/Protocol.cs`) |
| Clé de connexion LiteNetLib | `coop-subnautica-v12` | `Protocol.ConnectionKey` |
| Port UDP par défaut | `7777` | `Protocol.DefaultPort` |
| Bibliothèque de transport | LiteNetLib | `Coop.Server/ServerNetwork.cs`, `Coop.Client/Plugin.cs` |
| Encodage | little-endian, tampons réutilisables, **indépendant du transport** | `PacketWriter.cs`, `PacketReader.cs` |

Deux classes de livraison sont utilisées, et seulement deux : `ReliableOrdered` pour tout ce qui décrit un
état durable (handshake, entrées/sorties de joueurs, équipement, objet tenu, couleur), `Unreliable` pour les
flux rapides qui se corrigent d'eux-mêmes (positions de joueurs, créatures, véhicules, `Pong`).

Toute lecture est bornée : un paquet tronqué lève `ProtocolException` au lieu de renvoyer des valeurs
inventées, et toute position ou quaternion non fini est refusé (`PlayerPositionMessage.EnsureFinite`).
`tools/NetProbe` vérifie les deux dans son autotest.

---

## 2. Messages

`MessageType` (`src/Coop.Model/Protocol.cs`) est le premier octet de chaque paquet ; le format est donc
auto-descriptif. Les champs sont listés dans l'ordre d'écriture
(`src/Coop.Model/Messages.cs`) ; sauf mention contraire, un `string` est préfixé par sa longueur en `ushort`
et encodé en UTF-8.

| # | Message | Sens | Livraison | Charge utile |
|---|---|---|---|---|
| 1 | `HandshakeRequest` | client → serveur | ReliableOrdered | `ProtocolVersion` int, `GameVersion` string, `ModListHash` string, `Username` string |
| 2 | `HandshakeResponse` | serveur → client | ReliableOrdered | `Accepted` bool, `Reason` string, `ServerTick` long, `AssignedPlayerId` ushort |
| 3 | `Ping` | client → serveur | Unreliable | `Sequence` long, `ClientTimeMs` long |
| 4 | `Pong` | serveur → client | Unreliable | `Sequence` long, `ClientTimeMs` long, `ServerTick` long, `ServerTimeMs` long |
| 5 | `Disconnect` | serveur → client | ReliableOrdered | `Reason` string |
| 6 | `PlayerJoined` | serveur → client | ReliableOrdered | `PlayerId` ushort, `Username` string, position (3 floats), quaternion (4 floats) |
| 7 | `PlayerLeft` | serveur → client | ReliableOrdered | `PlayerId` ushort, `Username` string, `Reason` string |
| 8 | `PlayerPosition` | client → serveur | Unreliable | `Sequence` int, position (3), quaternion (4), `MoveSpeed` (3), `ViewPitch`, `IsUnderwater` bool, `OnSurface` bool, `ActionFlags` octet |
| 9 | `PlayersSnapshot` | serveur → client | Unreliable | `ServerTick` long, compte octet, puis par joueur : les mêmes champs que #8 |
| 10 | `PlayerEquipment` | client → serveur, puis serveur → autres | ReliableOrdered | `PlayerId` ushort, compte octet, puis les noms de `TechType` |
| 11 | `PlayerHeldItem` | client → serveur, puis serveur → autres | ReliableOrdered | `PlayerId` ushort, `TechTypeName` string, compte octet, puis les paramètres d'animation actifs |
| 12 | `PlayerColor` | serveur → client | ReliableOrdered | `PlayerId` ushort, `ColorIndex` octet |
| 13 | `CreatureSnapshot` | serveur → client | Unreliable | `ServerTick` long, compte ushort, puis par créature : `Key` long, position (3), quaternion (4) |
| 14 | `VehicleSnapshot` | serveur → client | Unreliable | `ServerTick` long, compte octet, puis par véhicule : `Key` long, position (3), quaternion (4) |
| 15 | `PickupClaim` | client → serveur | ReliableOrdered | `PlayerId` ushort (revendiqué, falsifiable), `ItemKey` long, `TechTypeName` string |
| 16 | `PickupResult` | serveur → demandeur | ReliableOrdered | `ItemKey` long, `TechTypeName` string, `Granted` bool, `HolderId` ushort, `ClaimedBy` ushort |
| 17 | `VehicleControl` | client → serveur | Unreliable (10 Hz) | `PlayerId` ushort (revendiqué, falsifiable), `VehicleKey` long, `Sequence` int, position et rotation (7 floats), vitesse (3 floats) |
| 18 | `VehicleControlResult` | serveur → demandeur | ReliableOrdered | `VehicleKey` long, `Granted` bool, `DriverId` ushort |
| 19 | `EntitySpawn` | serveur → client | ReliableOrdered | `Key` long, `UniqueId` string, `TechTypeName` string, position (3 floats), quaternion (4 floats) |
| 20 | `EntityDespawn` | serveur → client | ReliableOrdered | `Key` long, `Kind` octet (`1` véhicule, `2` objet), `Reason` string |
| 21 | `VehicleRelease` | client → serveur | ReliableOrdered | `VehicleKey` long |

Cinq points de contrat que ces messages portent :

- **Le serveur force `PlayerId`** sur ce qu'il rediffuse (#10, #11, #12) : un client ne peut donc pas se faire
  passer pour un autre ni choisir sa couleur (voir `docs/authority.md`).
- **Les priorités d'animation voyagent avec l'objet tenu** (#11) : les paramètres `holding_*` sont **lus sur
  l'`Animator` de l'émetteur**, pas re-dérivés par le destinataire (`PlayerHeldItemMessage`).
- **La première réclamation d'un objet gagne (#15/#16, v8).** Le serveur tient une carte `ItemKey →
  PlayerId` : la première clé vue est accordée à son émetteur, toute réclamation suivante par un autre
  joueur est **refusée** avec le détenteur nommé, et une réclamation du même joueur est idempotente. Le
  `PlayerId` du message n'est jamais recopié : la décision est attribuée à la session, et l'écart est
  compté (`ForgedPickupIds`) — la même règle que #10/#11/#12, appliquée à une **décision** et non à un
  relais.
- **L'annonce d'une entité fabriquée transporte son identité (#19, v10).** Le serveur diffuse la clé
  **et** l'identifiant du jeu (`UniqueIdentifier.Id`) du véhicule qu'il vient de fabriquer, et le client
  **écrit cet identifiant** sur son exemplaire : sans lui, deux instances d'un même `spawn` calculeraient
  deux clés différentes (mesuré en J) et le flux #14 n'aurait aucun objet à déplacer. Une entité sans
  identifiant n'est **pas** annoncée — c'est journalisé — plutôt que de laisser le client inventer une clé.
- **Le retrait d'une entité est décidé par le serveur seul (#20, v11).** Le client ne demande jamais
  qu'une entité disparaisse : le serveur annonce ce qu'il constate (véhicule détruit) ou ce qu'il vient de
  faire (objet ramassé). `Kind` évite deux index côté client — une clé de véhicule et une clé d'objet sont
  deux hachages de formes différentes. Le détenteur d'un objet ramassé est **exclu** de la diffusion de
  son propre retrait : son exemplaire est déjà dans son inventaire.
- **La libération du volant voyage (#21, v12).** Le conducteur dit « je ne conduis plus », le serveur libère
  aussitôt ; un silence de `VehicleSilenceReleaseSeconds` (3 s) fait de même, au cas où le client aurait
  planté. Sans ces deux chemins, un véhicule restait attribué à son dernier conducteur jusqu'à sa
  déconnexion.
- **Les flux #9, #13 et #14 sont filtrés par destinataire.** #13 n'est pas « toutes les créatures du
  monde » mais « celles qui comptent pour toi », dans le rayon ci-dessous (`ServerNetwork`,
  `BroadcastCreatureSnapshots`) ; #14 suit la même règle pour les véhicules, dans un rayon plus large
  (`BroadcastVehicleSnapshots`).

`PlayerActionFlags` (`Protocol.cs`) est un octet de drapeaux instantanés : `UsingPda=1`, `UsingTool=2`,
`UsingToolAlt=4`, `InSeamoth=8`, `InExosuit=16`. Ils voyagent dans le flux rapide (#8) parce qu'un clic ne
doit pas coûter un aller-retour.

---

## 3. Constantes, et pourquoi elles valent ça

### Rythmes

| Constante | Valeur | Justification (source) |
|---|---|---|
| `PositionSendIntervalMs` | `50` | 20 Hz de positions de joueur : c'est le compromis latence/bande passante retenu au plan §3 |
| `PingIntervalMs` | `1000` | un aller-retour par seconde suffit à mesurer le RTT |
| `EquipmentCheckIntervalMs` | `250` | l'équipement change rarement ; on vérifie sans inonder |
| `HeldItemCheckIntervalMs` | `250` | idem pour l'objet tenu |
| `CreatureStreamIntervalTicks` | `2` | les créatures bougent plus lentement qu'un joueur ; 20 Hz doublerait la bande passante pour rien (`Protocol.cs`) |

### Limites de taille

| Constante | Valeur | Justification |
|---|---|---|
| `MaxUnreliablePayloadBytes` | `1023` | **mesurée**, pas supposée : LiteNetLib refuse au-delà et l'écrit dans le journal *Unity*, hors de nos logs — le premier envoi de créatures (64 × 36 = 2 304 octets) a été jeté sans que rien ne le dise côté mod. La valeur vient du message de LiteNetLib lui-même : « exceeded maximum of 1023 bytes » (`Protocol.cs`) |
| `CreatureStateEntryBytes` | `8 + 7 × 4 = 36` | calculée ici pour que le plafond suive si une entrée grossit (clé 64 bits + position + quaternion) |
| `MaxCreatureStates` | `16` | 16 × 36 = 576 octets + en-tête (opcode, tick, compte). L'autotest de `tools/NetProbe` mesure un paquet plein à **587 octets** et vérifie qu'il tient sous 1 023 |
| `MaxEquipmentSlots` | `32` | borne de lecture d'un `PlayerEquipmentMessage` |
| `MaxTechTypeNameLength` | `64` | borne des noms de `TechType` et de paramètres |
| `MaxActionParams` | `8` | paramètres `holding_*` d'un objet tenu |
| `MaxUsernameLength` | `32` | borne du pseudo au handshake |

### Créatures

| Constante | Valeur | Justification |
|---|---|---|
| `CreatureRelevancyRadiusMeters` | `120` | un client n'a rien à faire d'une créature à 800 m : la relevancy s'applique **dès le premier envoi**, pas après (`Protocol.cs`, plan §7) |
| `MaxCreatureChunksPerTick` | `4` | un paquet ne peut porter que `MaxCreatureStates` créatures ; n'en envoyer qu'un seul faisait couper/rétablir l'IA de la même créature une fois par seconde (mesuré). Quatre paquets couvrent donc **64 créatures par client et par tick** |
| `CreatureAdoptionTimeoutSeconds` | `2.0` | durée pendant laquelle une créature reste adoptée après son dernier état ; 1 s suffisait à la relâcher puis la ré-adopter en boucle (mesuré) |

**Coût mesuré du plafond** (rapports du serveur, 2026-10-17) : `23,4 Ko/s` par client pour 64 créatures
diffusées en 4 paquets, `19` à `22` créatures **sacrifiées** au plafond dans une zone à 83–87 candidates,
la plus proche à `68`–`72` m. Le serveur avertit si une créature non diffusée passe sous `30 m`
(`ServerNetwork`, `CloseSacrificeMeters`) : le compromis « on ne suit que le proche » ne doit pas basculer
en silence.

### Véhicules

| Constante | Valeur | Justification |
|---|---|---|
| `VehicleStreamIntervalTicks` | `2` | **10 Hz**, la cadence des créatures : la mesure du §30 a montré qu'un Seamoth à 20 m/s produit alors des pas de `2,15 m`, et que c'est l'**interpolation cliente** — pas une cadence plus élevée — qui rend le mouvement continu (`RemoteVehicleManager`) |
| `VehicleStateEntryBytes` | `8 + 7 × 4 = 36` | même forme que l'entrée de créature (clé 64 bits + position + quaternion), calculée pour que le plafond suive si elle grossit |
| `MaxVehicleStates` | `16` | 16 × 36 = 576 octets + en-tête ; l'autotest de `tools/NetProbe` mesure un paquet plein à **586 octets**, sous 1 023 |
| `VehicleRelevancyRadiusMeters` | `150` | plus large que le rayon des créatures (`120 m`) : un Seamoth se voit de loin et **se conduit de loin** ; le perdre à 121 m ferait sauter sa position à chaque passage de la frontière |
| `VehicleTrackingTimeoutSeconds` | `2.0` | durée pendant laquelle le client continue d'interpoler sans nouvelle annonce ; au-delà, il relâche le véhicule (la simulation locale reprend) plutôt que de le laisser figé |

Le message ne transporte **que** la position et l'orientation. Ni énergie, ni modules, ni dégâts, ni
conduite : c'est dit dans `PLAN.md` §33 et dans la section 6 ci-dessous.

### Couleur de joueur

| Élément | Valeur | Source |
|---|---|---|
| Taille de la palette | `8` | `Protocol.PlayerColorCount` |
| Palette | rouge `255,82,82` · orange `255,152,0` · jaune `255,235,59` · vert `76,175,80` · cyan `0,188,212` · bleu `33,150,243` · violet `156,39,176` · rose `255,64,129` | `Protocol.PlayerColorsRgb` |
| Hors palette | l'indice **boucle** (`((index % count) + count) % count`) | `Protocol.PlayerColorAt` |
| Distribution | un indice par client à l'admission, l'hôte occupant l'indice `0` quand son personnage est dans le monde | `ServerNetwork.AllocateColorIndex` |

Les teintes ne sont pas inventées : la combinaison de plongée du jeu est un gris clair, et Nitrox la
distingue en décalant la teinte de sa texture dans la bande 5°–45° (`HsvSwapper.SetHueRange(5f, 45f)`,
`DiveSuitColorSwapManager`) ; les huit teintes ci-dessus couvrent la même idée sans réécrire les pixels
(`Protocol.cs`).

### Divers

| Constante | Valeur | Source |
|---|---|---|
| `MaxClients` | `10` | `Protocol.MaxClients` |
| `HandshakeTimeoutSeconds` | `10` | au-delà, un pair qui n'a pas fini son handshake est éjecté |
| `ServerHostPlayerId` | `0` | identifiant réservé au personnage de l'instance serveur |
| `MaxEntityIdLength` | `64` | longueur maximale d'un `UniqueId` dans `EntitySpawn` (#19) : un identifiant du jeu fait 36 caractères, `64` laisse de la marge sans permettre un paquet démesuré (écriture et lecture refusent au-delà) |
| `MaxDespawnReasonLength` | `96` | motif lisible d'un `EntityDespawn` (#20) : il ne sert qu'au journal, mais une chaîne non bornée est un paquet non borné (refusé à l'écriture comme à la lecture) |
| `VehicleSilenceReleaseSeconds` | `3.0` | silence au bout duquel le serveur libère un volant (v12) : le conducteur envoie à 10 Hz, donc 30 états manqués — bien au-delà d'une rafale de latence, bien en dessous de l'attente d'un joueur qui veut reprendre le véhicule |
| Identifiant de l'hôte vu par un client | `9999` | `ServerNetwork.BroadcastSnapshots` : l'hôte est inclus comme joueur virtuel pour qu'un client seul voie aussi son personnage |

---

## 4. Clé de créature : le contrat d'identité

`Coop.Shared.CreatureIdentity.KeyOf` (`src/Shared/CreatureIdentity.cs`), compilé dans **les deux** plugins
via un `<Compile Include>` partagé — c'est la seule façon d'être sûr que les deux côtés calculent la même
clé :

1. **Position d'apparition (mécanisme par défaut)** — `StableHash.Of("spawn|" + nom du prefab + "|" +
   x + "|" + y + "|" + z)`, la position étant lue dans `Creature.leashPosition` (champ **public**, relevé
   dans l'IL : `Creature.InitializeOnce` fait `leashPosition = transform.position`, une seule fois) et
   quantifiée à **5 cm** (`SpawnQuantizationMeters`, arrondi par `Quantize`).
2. **Identifiant du jeu (repli)** — `StableHash.Of("uid|" + UniqueIdentifier.Id)`. Il ne passe qu'en
   dernier : mesuré, il **diffère** d'une instance à l'autre pour les créatures créées à l'exécution (les
   bancs de poissons), 34 808 états envoyés pour **0 apparié** au premier run réel.
3. **Aucun des deux** — la clé vaut `0`, la créature n'est pas diffusée et elle est nommée au journal. Le
   serveur compte ces refus (`refused` dans le rapport de diffusion).

Le hachage est **FNV-1a 64 bits** (`StableHash`), et non `string.GetHashCode()` : ce dernier n'est pas
garanti stable d'un processus à l'autre.

Pourquoi ce contrat, ce qu'il suppose et ce qui le casserait : `PLAN.md` §28. Côté client, une clé inconnue
n'est jamais devinée : elle est comptée et nommée, et une correspondance lointaine est signalée comme
anomalie plutôt que refusée (`RemoteCreatureManager`).

---

## 4 bis. Clé de véhicule : le contrat d'identité

`Coop.Shared.VehicleIdentity.KeyOf` (`src/Shared/VehicleIdentity.cs`), compilé dans **les deux** plugins
par le même `<Compile Include>` partagé que `CreatureIdentity` :

1. **`UniqueIdentifier.Id` (mécanisme par défaut)** — `StableHash.Of("vehicle-uid|" + id)`. Un véhicule
d'une sauvegarde est un objet sérialisé : son identifiant est écrit dans le fichier, donc les deux instances
qui chargent la même partie lisent le même. C'est l'inverse du choix fait pour les créatures, et pour une
raison mesurée : leurs bancs sont créés à l'exécution, et leur identifiant diffère d'une instance à l'autre.
2. **Position d'apparition (repli)** — `StableHash.Of("vehicle-spawn|" + type + "|" + nom + "|" + x + "|" +
y + "|" + z)`, la position étant celle du véhicule **à la première observation**, quantifiée à `5 cm`.
Ce repli n'existe que pour un véhicule fabriqué à l'exécution (`spawn`), qui n'a pas d'identifiant sérieux.
3. **Aucun des deux** — clé `0`, véhicule non diffusé, compté par le serveur.

Ce que le repli suppose, et qui doit être dit : que les deux instances observent le véhicule **avant qu'il
n'ait beaucoup bougé**. Un client qui rejoint pendant que le Seamoth roule calculerait une autre ancre. Le
client ne devine jamais la clé dans ce cas : il compte la clé inconnue, et la seule exception est nommée au
journal — même type, **un seul** véhicule local non suivi à moins de `30 m` de la position annoncée
(`RemoteVehicleManager.UnknownKeyBindingMeters`).

---

## 4 ter. Création d'entité : ce que l'annonce promet

Un véhicule fabriqué à l'exécution (`spawn`, épave qui se détache) n'est pas dans la sauvegarde : les
clients ne l'ont pas, et le flux #14 ne leur donnait qu'une clé sans objet à déplacer. La v10 ajoute donc
une annonce (#19) et trois décisions qui méritent d'être dites :

1. **Le serveur est la seule source d'apparition.** Il balaie ses véhicules à la cadence du flux de
   positions (`VehicleCacheSeconds = 2 s`) et annonce ceux dont la clé est **nouvelle**. Le premier
   balayage **enregistre** sans annoncer : sans ça, un client qui se connecte verrait arriver une annonce
   pour chaque Seamoth de la carte, y compris ceux qu'il a déjà chargés.
2. **L'espèce voyage en clair, l'objet est instancié par le client.** `TechTypeName` est le nom d'espèce
   (`Seamoth`), pas celui du prefab (`SeaMoth(Clone)`) : c'est ce que `CraftData.GetPrefabForTechTypeAsync`
   sait résoudre chez le client. Une espèce inconnue est **comptée et refusée** — le client n'invente pas de
   prefab.
3. **Rien n'est créé avant que le monde ne soit chargé.** Le client se connecte au **menu principal** : une
   annonce reçue à ce moment-là reste en file (`EntitySpawner`) jusqu'à `AutoLoadBehaviour.WorldLoaded`.
   Mesuré à l'étape N : sans cette attente, le Seamoth était fabriqué dans la scène du menu, que le
   chargement décharge ensuite — l'objet créé n'était plus retrouvé trois secondes plus tard.

Ce que l'annonce ne fait pas, elle seule : **retirer**. Le retrait est un message distinct (#20, section
4 quater), et le carnet des entités fabriquées est borné à `256` (`MaxRememberedSpawns`) — une entité
oubliée n'est plus rejouée aux clients suivants (c'est journalisé).

---

## 4 quater. Retrait d'entité : trois décisions, chacune née d'une mesure

La v11 ajoute `EntityDespawn` (#20). Ce n'est pas la symétrie décorative de la création : sans lui, une
entité créée restait chez tout le monde après sa destruction chez l'hôte, et un objet ramassé restait
**visible et réclamable** par les autres — le serveur accordait alors une clé qui ne désignait plus rien.

1. **Le serveur décide, le client constate.** Aucun retrait n'est demandé par un client. Deux sources : le
   balayage des véhicules (une clé connue qui n'est plus dans le monde) et un ramassage accordé (l'objet
   est retiré du monde simulé avant d'être annoncé).
2. **Le détenteur est exclu de son propre retrait.** Sinon, un ramassage accordé détruisait l'objet chez
   celui qui venait de le prendre, **avant** que son geste ne le range dans l'inventaire — mesuré à l'étape
   L : `accordée=True` côté serveur, `objet dans l'inventaire=False` côté client.
3. **Un retrait reçu avant le chargement du monde est mis en file.** Le client se connecte au menu : sans
   cette file, l'objet revenait avec la sauvegarde (mesuré : retrait reçu 10 s avant le chargement, puis
   `PowerCell` présent dans le monde). Même raison pour `EntitySpawner` : une apparition arrivée **après**
   un retrait ne recrée rien (`EntityDespawner.IsGone`), sinon un fantôme survivrait à la destruction.

Ce que ça ne fait pas : **aucun retrait d'objet par un client** (seul le serveur le décide), et un objet
que le serveur n'a pas accordé ne disparaît pas. Les véhicules détruits **hors** d'un balayage réussi
(clé passagèrement nulle) ne produisent pas de faux retrait : une clé absente du monde est annoncée une
seule fois, puis oubliée.

---

## 5. Admission et refus

`ServerNetwork.Validate` (`src/Coop.Server/ServerNetwork.cs`) refuse un handshake, **en donnant la raison en
clair** dans un `HandshakeResponse.Accepted = false`, en ReliableOrdered, puis laisse le client fermer
(couper juste après l'envoi faisait perdre le motif) :

| Motif de refus | Condition |
|---|---|
| `version de protocole incompatible` | `ProtocolVersion != Protocol.Version` |
| `serveur plein (10 clients)` | `AcceptedClients >= Protocol.MaxClients` |
| `pseudo vide` | `IsNullOrWhiteSpace(Username)` |
| `pseudo trop long` | `Username.Length > MaxUsernameLength` |
| `liste de mods différente` | `ModListHash` différent (comparaison ordinale) |
| — | version de jeu différente (même comparaison) |

À l'acceptation, le serveur : attribue un `PlayerId`, assigne la couleur, envoie au nouvel arrivant un
`PlayerJoined` + sa couleur + l'équipement et l'objet tenu des joueurs déjà présents, annonce son arrivée
aux autres (`ServerNetwork.HandleHandshake`), puis lui **rejoue les annonces d'entités fabriquées avant son
arrivée** (`SendSpawnedEntities`). Ce dernier point n'est pas un confort : les entités d'une sauvegarde
existent déjà chez lui, une entité fabriquée non. Mesuré à l'étape N : le second client se connecte après
le `spawn` de l'hôte et, sans ce rejeu, ne reçoit rien — verdict False à 150 s alors que le premier avait
tout créé.

---

## 6. Ce qui n'est pas implémenté

Dit franchement, pour que ce document ne laisse pas croire à un contrat plus large qu'il n'est :

- **Véhicules : position et conduite, rien d'autre.** Le flux #14 transporte la position et l'orientation
  des véhicules du serveur, le client les interpole (`PLAN.md` §33), et depuis la v9 le **conducteur**
  envoie son état (#17) : le serveur vérifie que la clé désigne un de ses véhicules, que le joueur est à
  moins de `10 m`, et qu'aucun autre joueur ne conduisait déjà, puis applique et rediffuse (#18 rend la
  décision au demandeur). Ne sont **pas** transportés : l'énergie, les modules, les dégâts — un client qui
  conduit et un serveur qui ne simule pas le véhicule ne peuvent pas décider d'une panne. Il n'y a **pas
  de libération** du véhicule : le conducteur garde la main jusqu'à sa déconnexion (`PLAN.md` §40). La  synchronisation suppose aussi un véhicule **présent dans la sauvegarde des deux côtés** : la création
d'entité (v10, #19) et son retrait (v11, #20) existent désormais, mais **seulement du serveur vers les
clients** — un véhicule fabriqué **chez un client** n'existe pas dans le monde du serveur.
- **Le volant se libère (v12, #21)** : le conducteur qui descend le rend immédiatement, et un silence de
  `VehicleSilenceReleaseSeconds` (3 s) le libère aussi. Le véhicule n'est donc plus perdu jusqu'à la
  déconnexion de son dernier conducteur.
- **Le ramassage est autoritaire, mais son effet sur le monde ne l'est pas** : depuis la v8 et la fin de
  M5, le serveur accorde ou refuse une clé **d'un objet réel** (il la résout dans son propre monde, et
  refuse ce qu'il ne trouve pas ou qui est à plus de `6 m` du joueur), et le client **bloque** le geste de
  ramassage (`Pickupable.OnHandClick`) quand la réponse est un refus, avec retour arrière si le refus arrive
  après un ramassage optimiste (`PLAN.md` §39). Ce qui manque : **le monde du serveur garde l'objet** —
  aucun message ne dit « cet objet n'existe plus » —, les objets **mobiles** peuvent être jugés hors portée
  à tort, et les objets **sans clé** ne sont pas arbitrés. Les collisions de clé sont comptées, pas
  résolues. Aucun autre arbitrage de ressource (récolte, craft, dégâts) n'existe.
- **Aucun transport de contenu** : pas d'AssetBundle, pas de définition d'objet envoyée au client (M6).
- **Version 12** : deux instances de versions différentes ne se parlent pas (refus au handshake), et les
  tests de sérialisation vivent dans `tools/NetProbe` (`selftest`).
