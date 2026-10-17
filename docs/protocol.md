# Protocole réseau — référence

Ce document décrit **ce que le code fait aujourd'hui**, message par message, avec la justification de chaque
constante. Les jalons, les mesures et les décisions sont dans `PLAN.md` ; ici, c'est le contrat.

Tout ce qui suit est lu dans le code, et chaque ligne nomme sa source. Une valeur qui vient d'une mesure
porte la mesure ; une valeur choisie le dit.

---

## 1. Version et transport

| Élément | Valeur | Source |
|---|---|---|
| Version du protocole | `8` | `Protocol.Version` (`src/Coop.Model/Protocol.cs`) |
| Clé de connexion LiteNetLib | `coop-subnautica-v8` | `Protocol.ConnectionKey` |
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

Quatre points de contrat que ces messages portent :

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
`PlayerJoined` + sa couleur + l'équipement et l'objet tenu des joueurs déjà présents, puis annonce son
arrivée aux autres (`ServerNetwork.HandleHandshake`).

---

## 6. Ce qui n'est pas implémenté

Dit franchement, pour que ce document ne laisse pas croire à un contrat plus large qu'il n'est :

- **Véhicules : position seulement.** Le flux #14 transporte la position et l'orientation des véhicules du
  serveur, et le client les interpole (`PLAN.md` §33). Ne sont **pas** transportés : l'énergie, les modules,
  les dégâts, et surtout **la conduite** — rien ne remonte du client vers le serveur, donc un client qui
  pilote simule localement et le serveur ne le sait pas. La synchronisation suppose aussi un véhicule
  **présent dans la sauvegarde des deux côtés** : la création d'entité n'est pas dans le protocole (même
  famille de limite que M6).
- **Le ramassage n'est pas encore autoritaire** : la **décision** l'est depuis la v8 (messages #15/#16 : la
  première réclamation d'une clé gagne, les suivantes sont refusées avec le détenteur nommé, et l'id
  revendiqué est remplacé par celui de la session), mais rien ne la branche sur le monde — le client
  ramasse toujours localement, sans demander la permission, et le serveur ne vérifie ni que l'objet existe
  ni que le joueur est à portée. La clé vient d'une sonde (`PLAN.md` §38). Aucun autre arbitrage de
  ressource (récolte, craft, dégâts) n'existe.
- **Aucun transport de contenu** : pas d'AssetBundle, pas de définition d'objet envoyée au client (M6).
- **Version 8** : deux instances de versions différentes ne se parlent pas (refus au handshake), et les
  tests de sérialisation vivent dans `tools/NetProbe` (`selftest`).
