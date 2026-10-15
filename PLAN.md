# Plan de développement — Serveur autoritaire Subnautica (Idée 1)

> Architecture retenue : serveur = instance de jeu headless (BepInEx + Harmony) détenteur de
> l'autorité sur tout le gameplay ; client = rendu + input + interpolation, avec prédiction
> locale **uniquement** pour son propre personnage.

---

## 1. Décisions d'architecture (à figer avant d'écrire du code)

| Sujet | Décision | Pourquoi |
|---|---|---|
| Processus serveur | Instance Subnautica lancée par un launcher avec `-batchmode`, sans interaction | Pas de binaire serveur officiel |
| Injection | **BepInEx 5.x** (Mono, pas IL2CPP) + **HarmonyX** | Le jeu est Mono → le path le plus simple |
| Transport | **LiteNetLib** (UDP, canaux reliable/unreliable) | Même choix que Nitrox, éprouvé |
| Tick serveur | 20 Hz (50 ms) simulation / 20 Hz snapshots | Compromis latence/bande passante |
| Autorité | Serveur = vérité sur **tout** sauf le transform du joueur local | Corrige la faille de Nitrox |
| Prédiction client | Joueur local uniquement, avec réconciliation | Obligatoire pour le ressenti |
| Sérialisation | Binaire compact maison (quantization) ou MemoryPack | Snapshot à 20 Hz → compacité critique |
| Contenu | Registre serveur + **AssetBundles** poussés au client + hash de mod-list au login | Un seul mod serveur à maintenir |
| Runtime cible | Plugins client/serveur en **net472** (à vérifier en inspectant le jeu) ; lib partagée en `netstandard2.0` | Doit matcher le runtime Mono du jeu |

**À vérifier concrètement sur ta machine** (dnSpy ou ILSpy sur
`Subnautica_Data/Managed/Assembly-CSharp.dll`) : version Unity exacte et profil .NET du jeu.
Ne te fie pas à une valeur de mémoire : ouvre le DLL.

---

## 2. Arborescence du projet

```
SubnauticaMultiplayer/
├─ SubnauticaCoop.sln
├─ PLAN.md
├─ src/
│  ├─ Coop.Model/            # DTO, packets, IDs d'entités, enums — AUCUN déprêt Unity
│  │                          # netstandard2.0, partagé client + serveur + tests
│  ├─ Coop.Protocol/          # writer/reader binaire, registre de packets, quantization
│  ├─ Coop.Patches/           # transpilers/prefix Harmony communs client & serveur
│  │                          # (c'est ici que vivent les "hooks" de jeu)
│  ├─ Coop.Client/            # plugin BepInEx CLIENT : rendu, input, interpolation, prédiction
│  │                          # références : Assembly-CSharp.dll
│  ├─ Coop.Server/            # plugin BepInEx SERVEUR : simulation autoritaire + netcode
│  │                          # références : Assembly-CSharp.dll
│  └─ Coop.Launcher/          # launcher : choisit Client/Serveur, lance le jeu avec les args
│                             # + distribue/vérifie les AssetBundles
├─ tests/
│  ├─ Coop.Model.Tests/       # sérialisation round-trip, quantization, relevancy grid
│  └─ Coop.Server.Tests/      # logique de simulation pure (sans Unity) : validation, tick
├─ bundles/                   # AssetBundles de contenu custom (créatures, items, biomes)
└─ docs/
   └─ protocol.md             # spec de chaque packet (source de vérité pour client & serveur)
```

Règle d'or : **`Coop.Model` et `Coop.Protocol` ne référencent jamais Unity.** C'est ce qui
rend la logique testable sans lancer le jeu, et ce qui te permettra plus tard de migrer
vers l'idée 2 sans tout réécrire.

---

## 3. Protocole réseau (résumé — détail dans `docs/protocol.md`)

### Flux de connexion
```
Client → HandshakeRequest   { protocolVersion, gameVersion, modListHash, username }
Serveur → HandshakeResponse { ok | reject(raison) }         # rejette si version/modlist divergent
Serveur → WorldSnapshot     { baseline complet à l'instant T }
        ↕ boucle 20 Hz
Serveur → StateDelta        { entités changées, quantifiées }
Client  → PlayerInput       { intention de mouvement/action (PAS de position absolue) }
Serveur → PlayerCorrection  { position autoritaire pour réconciliation }
```

### Catégories de packets
- **Synchronisation monde** : `WorldSnapshot`, `StateDelta`, `EntitySpawn`, `EntityDespawn`
- **Joueur** : `PlayerInput`, `PlayerCorrection`, `PlayerJoined`, `PlayerLeft`
- **Actions de jeu** : `GameActionRequest` / `GameActionResponse` (pickup, craft, damage, use)
- **Méta** : `ChatMessage`, `AdminCommand`, `Ping`/`Pong`, `ModListMismatch`

### Compressions obligatoires (sinon le 20 Hz ne passe pas)
- **Quantization** : positions relatives à une cellule de grille (uint16 au cm), rotations en
  quaternion le plus-court (3×int16), vitesses en int16.
- **Area of interest** : grille spatiale de cellules (~64 m). Un client ne reçoit que les
  entités de sa cellule + voisines (avec hystérésis pour éviter le clignotement).
- **Delta par entité** : on n'envoie qu'un *champ* s'il a varié au-delà d'un seuil.
- **Baseline + ACK** : chaque delta s'appuie sur le dernier snapshot ACKé → reprise propre en
  cas de perte.

---

## 4. Politique d'autorité — qui décide quoi

| Système | Autorité | Client fait quoi |
|---|---|---|
| Transform du joueur local | Serveur (valide) | prédit + se réconcilie |
| Transform des autres joueurs | Serveur | interpole |
| Transform/état des véhicules (Seamoth, Exosuit, Cyclops) | Serveur | interpole (sauf conduit par soi) |
| **IA des créatures** | Serveur | **aucune IA locale**, suit le transform reçu |
| Ressources / récolte | Serveur | envoie une intention, joue l'anim |
| Dégâts / vie (`LiveMixin`) | Serveur | envoie la demande, joue le feedback |
| Inventaire / craft | Serveur | UI + requête |
| Bases / intégrité / énergie | Serveur | reconstruit visuellement |
| Oxygène / faim / température | Serveur | affiche |
| Journal / objectifs / PDA | Serveur | affiche |
| Particules, sons, animations | Client | **libre** (présentation pure) |

---

## 5. Les premiers patches Harmony à écrire (dans l'ordre)

C'est le cœur du travail. On patche le jeu pour qu'il cesse de simuler ce que le serveur
doit décider.

1. **`Creature.Update` / `Creature.Awake`** — prefix : si l'entité n'est pas sous contrôle
   serveur local, on **court-circuite l'IA** et on applique le transform reçu. → prouve tout
   le modèle en un seul patch.
2. **`PlayerMotor` / `GroundMotor` / `WaterMotor`** — empêcher le client d'appliquer ses
   propres résultats physiques sur les entités distantes ; le joueur local garde sa
   prédiction mais la position autoritaire vient du serveur.
3. **`LiveMixin.TakeDamage`** — prefix : rediriger vers `GameActionRequest`, ne pas appliquer
   localement. Le client applique seulement le résultat diffusé par le serveur.
4. **`Pickupable.Pickup` / `Inventory.AddItem`** — validation serveur, refus si entité déjà
   prise par un autre joueur.
5. **`EnergyMixin` / `PowerRelay`** — consommation d'énergie décidée serveur.
6. **`LargeWorldEntity` / streaming de monde** — le serveur dicte quelles entités sont
   actives pour chaque client selon l'area of interest.
7. **`DayNightCycle`** — horloge pilotée serveur (un seul temps pour tous).
8. **`WaterTemperatureSimulation` / `PlayerNeeds`** — valeurs serveur.

Méthode : pour chaque système, écrire dans `docs/authority.md` une ligne
« ce que le client ne doit plus décider », puis le patch, puis un test qui vérifie côté
serveur qu'une valeur falsifiée est rejetée.

---

## 6. Jalons

| # | Jalon | Critère de succès | Risque |
|---|---|---|---|
| **M0** | Serveur headless boote | plugin chargé, log `tick 20 Hz OK`, monde chargé sans rendu | ⚠️ **gate bloquant** (voir §7) |
| **M1** | Squelette réseau | handshake + ping/pong entre 2 process | faible |
| **M2** | Présence joueurs | 2 clients se voient bouger ; self prédictif, distant interpolé | moyen |
| **M3** | **Une créature synchro, IA client coupée** | position identique chez les 2 clients, IA uniquement serveur | **élevé — valide le modèle** |
| **M4** | Un véhicule (Seamoth) | position/énergie/upgrades autoritaires | moyen |
| **M5** | Première action autoritaire | pickup : refus serveur si déjà réclamé | moyen |
| **M6** | Pipeline de contenu | item custom ajouté côté serveur, AssetBundle livré au client | moyen |

M3 est le vrai test : si une créature pilotée serveur avec IA client désactivée rend
correctement chez 2 clients, l'architecture est bonne pour tout le reste.

---

## 7. Risques et parades

- **`-batchmode -nographics` casse le chargement des assets** (shaders/matériaux nécessitent
  souvent un contexte GPU). Parade : démarrer windowed/minimisé, ou sur Linux un display
  virtuel (Xvfb). **Trancher ça en M0**, avant tout le reste.
- **PhysX non déterministe** → ne jamais viser le lockstep. Le serveur simule, on streame.
- **Fuite d'autorité** : un patch oublié = client qui triche/désync. Parade : checklist
  §5 + test de rejet par système.
- **Mises à jour du jeu** qui cassent les patches → épingler une version du jeu, tester la mise
  à jour dans une branche.
- **Bande passante** si beaucoup de créatures → relevancy + quantization dès M1, pas après.

---

## 8. Ordre de travail recommandé

1. M0 (headless) — **ne rien construire avant d'avoir validé ce point**.
2. Libs partagées (`Coop.Model` + `Coop.Protocol`) avec tests de sérialisation.
3. M1 réseau, puis M2 joueurs.
4. M3 créature + `Creature.Update` patché → décision go/no-go sur l'architecture.
5. Ensuite M4→M6, puis industrialiser la politique d'autorité système par système.

---

## 9. Résultats M0 (validé le 2026-10-14)

**Verdict : le gate M0 est franchi.** Une instance Subnautica lancée en `-batchmode -nographics`
charge BepInEx, charge nos plugins, atteint le menu et fait tourner notre boucle de jeu
sans interruption (~143 ticks/s, mesuré sur 45 s).

### Environnement constaté (relevé, plus d'approximation)

| Élément | Valeur mesurée |
|---|---|
| Unity | `2019.4.36f1` |
| CLR (Mono) | `4.0.30319.42000` |
| BuildID Steam | `20241558` |
| `Assembly-CSharp.dll` vanilla | **3 777 024 octets** |
| `Assembly-CSharp.dll` patché par Nitrox | 3 770 368 octets (≠, patch confirmé) |
| BepInEx | 5.4.21 x64 (installe `winhttp.dll` + `doorstop_config.ini`) |
| Cible de compilation du plugin | **`net472`** (les référence-assemblies sont locales → aucun restore NuGet requis) |

### Le piège découvert (à retenir pour tous les futurs plugins)

**Subnautica détruit le GameObject du manager BepInEx au chargement de son premier écran.**
Conséquence : un `Update()` écrit directement dans une classe héritant de `BaseUnityPlugin`
**n'est jamais appelé** — et le plugin se charge sans le moindre message d'erreur (le log
s'arrête simplement après `Chainloader startup complete`, suivi d'un `OnDestroy`).
Piège vicieux : les handlers branchés sur des événements statiques (`SceneManager.sceneLoaded`)
continuent de fonctionner, ce qui donne l'illusion que le plugin est vivant.

**Parade retenue :** héberger la boucle de jeu sur notre propre objet persistant.

```csharp
var go = new GameObject("Coop.Server.Bootstrap");
Object.DontDestroyOnLoad(go);
go.AddComponent<ServerTickBehaviour>();   // le Update() de CE composant tourne
```

### Ce qui n'est PAS encore fait

- `-nographics` **ne supprime pas** le device graphique : Unity rapporte toujours
  `graphicsDeviceType=Direct3D11` et le processus conserve un handle de fenêtre. L'instance
  tourne donc « sans interaction », pas « sans GPU ». À re-valider si on veut un vrai serveur
  sans display (piste : Xvfb sous Linux).
- **Le monde n'est pas encore chargé** : l'instance s'arrête au menu. Auto-charger une partie
  (bypass du menu / appel direct du `SaveLoadManager`) est l'étape suivante : **M0.5**.
- La boucle tourne à ~143 Hz non plafonnée ; le vrai serveur devra boucler à un pas fixe de
  20 Hz avec accumulateur, découplé du framerate.

### Fichiers produits

```
Directory.Build.props            # chemins partagés (dossier jeu, Managed, BepInEx core)
src/Coop.Server/Coop.Server.csproj   # net472, refs BepInEx.dll + UnityEngine.CoreModule.dll
src/Coop.Server/Plugin.cs            # plugin M0 : bootstrap persistant + heartbeat + tick
```

---

## 10. Résultats M0.5 (validé le 2026-10-14)

**Verdict : le monde se charge automatiquement au démarrage headless, sans intervention.**

Séquence observée sur `-batchmode -nographics` :

```
[M0.5] uGUI_MainMenu.main disponible.
[M0.5] GetActiveSlotNames: 1 -> [slot0000]
[M0.5] ouverture du panneau de chargement (OnButtonLoad)...
[M0.5] MainMenuLoadButton trouvés: 1
[M0.5]   slot='slot0000' session='aae68629-...' changeSet=83031 mode=Freedom
[M0.5] >>> chargement de la sauvegarde 'slot0000'
[M0.5] *** MONDE CHARGÉ : scene 'Main' ***
```

Preuves que le monde est réellement simulé (et pas juste une scène changée) :
- 96 ticks moteur dans la scène `Main` (t=97 s), heartbeat stable, processus ~2 Go.
- Stdout Unity : `CellManager`, `ClipMapManager`, `PerformGarbageAndAssetCollectionAsync`,
  `Loaded Objects now: 151109` — le streaming de monde tourne.
- **`Heartbeat CSV ... PlayerPos,-114.4612,2.289913,-54.21698`** — un joueur existe dans le monde.

### L'API réelle du jeu (relevée, pas devinée)

Dumpée depuis `Assembly-CSharp.dll` avec Mono.Cecil (`tools/ApiDump`). Types en **namespace global** :

| Type | Membre utilisé |
|---|---|
| `uGUI_MainMenu` | `static uGUI_MainMenu main`, `void OnButtonLoad()`, `IEnumerator LoadGameAsync(string saveGame, string sessionId, int changeSet, GameMode gameMode)` |
| `MainMenuLoadButton` | champs `saveGame`, `sessionId`, `changeSet`, `gameMode` ; `bool IsEmpty()` ; **`void Load()`** |
| `SaveLoadManager` | `static main`, `string[] GetActiveSlotNames()`, `string GetCurrentSlot()`, `CoroutineTask<LoadResult> LoadAsync()` |
| `GameMode` | `Survival`, `Freedom`, `Hardcore`, `Creative`, `None` |

**Décision d'implémentation :** ne pas reconstruire `sessionId` / `changeSet` / `gameMode` à la main,
mais **réutiliser le chemin du menu** — ouvrir le panneau (`OnButtonLoad()`) puis appeler le
`Load()` du bouton, exactement comme un clic. C'est robuste aux changements de format de sauvegarde.

```csharp
yield return new WaitUntil(() => uGUI_MainMenu.main != null);
uGUI_MainMenu.main.OnButtonLoad();          // peuple les boutons de slots
var button = Object.FindObjectsOfType<MainMenuLoadButton>()[0];
button.Load();                               // démarre le chargement du monde
```

### Fait notable sur l'emplacement des sauvegardes

Le jeu lit ses sauvegardes dans **`<dossier du jeu>/SNAppData/SavedGames/`** (et non dans
`AppData\LocalLow\Unknown Worlds\Subnautica\SavedGames`, qui reste vide). C'est pratique pour un
serveur : le dossier de jeu est autoportant et copiable tel quel.

`SNAppData` n'était donc **pas** un artefact Nitrox comme supposé en §9 : c'est le dossier de
sauvegardes réellement utilisé par le jeu. Il a bien été préservé (copie dans `_backup_nitrox/`).

### Limites restantes

- Le chargement cible le **premier** slot trouvé ; le choix du slot (et « démarrer une nouvelle
  partie » si aucun slot) reste à câbler proprement.
- `Player.main` n'est pas encore vérifié explicitement côté plugin ; la preuve vient de la stdout
  du moteur (`PlayerPos`). Un contrôle direct serait plus propre.
- Toujours pas de vrai « sans GPU » : Unity conserve un device D3D11 (cf. §9).

---

## 11. Boucle serveur à pas fixe 20 Hz (validé le 2026-10-14)

La boucle non plafonnée (~143 Hz, dépendante du framerate) est remplacée par une boucle
de simulation **à pas fixe de 50 ms**, découplée du framerate. C'est la base du déterminisme
côté serveur : la simulation avance à la même vitesse que la machine fasse 20 ou 300 fps.

### Implémentation (`ServerTickBehaviour`)

```csharp
public const double TickRate = 20.0;
public const double TickInterval = 1.0 / TickRate;   // 50 ms
private const int MaxCatchUpSteps = 5;

_accumulator += Time.unscaledDeltaTime;
while (_accumulator >= TickInterval && steps < MaxCatchUpSteps)
{
    _accumulator -= TickInterval;
    _tick++;
    Tick?.Invoke(_tick);          // <-- point d'accroche des futurs systèmes
}
```

Trois choix à ne pas défaire :

- **`Time.unscaledDeltaTime`** (et non `deltaTime`) : sinon un `Time.timeScale = 0` (menu de
  pause) ou un ralentissement du jeu gèle la simulation du serveur.
- **Accumulateur en `double`** : un `float` dérive sur plusieurs heures de session.
- **Plafond de rattrapage (`MaxCatchUpSteps`)** : après un à-coup (chargement de monde, GC),
  on ne rejoue pas 2 s de simulation d'un coup — c'est la spirale de la mort. On abandonne le
  surplus et on le compte dans `DroppedTicks`.

Le hook `public static event Action<long> Tick` est le point d'entrée prévu pour les systèmes
du §5 (IA, dégâts, inventaires…) : chacun s'y abonnera au lieu de créer sa propre boucle.

### Mesure sur l'instance headless (110 s, scène `Main`)

| Mesure | Valeur observée |
|---|---|
| Taux instantané en régime établi | **19,98 – 20,03 /s** sur 21 rapports consécutifs |
| Ticks par fenêtre de 5 s | **exactement +100** |
| Framerate du jeu pendant ce temps | ~143 fps (donc ≠ 20 Hz : le découplage fonctionne) |
| `reste` (accumulateur) | 33–40 ms, **toujours < 50 ms** → aucun retard ne s'accumule |
| `abandonnes` | **20, une seule fois** (à-coup de chargement), ne bouge plus jamais |
| `derive` | ≈ −1050 ms **constante**, pas croissante → décalage initial, pas une dérive |

Le seul relevé sous 20/s (15,99/s) correspond à la fenêtre contenant le chargement du monde ;
c'est le comportement correct et il est explicitement signalé.

### Limites

- Le tick ne fait encore **rien** : c'est une boucle vide instrumentée. Le premier travail
  réel (autorité joueur ou synchronisation d'entités) reste à brancher sur `Tick`.
- Les 20 ticks abandonnés au démarrage sont assumés (on ne rejoue pas le passé) ; si un jour
  ça devient gênant, il faudra une politique de rattrapage plus fine que « on jette ».- La mesure de taux est échantillonnée sur 5 s : elle ne détecte pas une micro-irrégularité intra-fenêtre. Un histogramme des écarts inter-ticks serait plus fin si nécessaire.

---

## 12. Réseau M1 : handshake + ping/pong (validé le 2026-10-14)

**Verdict : deux instances Subnautica réelles se parlent.** Un serveur headless et un client de jeu
ont établi un handshake validé, et le client a tourné sa boucle de ping à 1 Hz.

### Structure ajoutée

```
src/Coop.Model/          netstandard2.0 — AUCUNE dépendance Unity / jeu / transport
  Protocol.cs            version, port, opcodes, LaunchMode, ProtocolException
  PacketWriter.cs        écriture binaire little-endian, tampon réutilisable
  PacketReader.cs        lecture bornée (paquet tronqué -> ProtocolException)
  Messages.cs            HandshakeRequest/Response, Ping, Pong, Disconnect
  ModListHash.cs         empreinte déterministe de la liste de mods
src/Coop.Server/ServerNetwork.cs   LiteNetLib : admission, handshake, pong
src/Coop.Client/Plugin.cs          client : connexion, handshake, ping, RTT
tools/NetProbe/                    client console + autotest du protocole
```

Le format des messages est écrit à la main (pas de LiteNetLib.Utils dans le modèle) : le protocole
reste testable sans socket et remplaçable si on change de transport.

### Dépendance : pourquoi LiteNetLib 1.3.5 et pas 2.x

LiteNetLib 2.x ne publie que `net8.0` et `netstandard2.1`. Le jeu tourne en **net472**, qui plafonne à
`netstandard2.0` : la branche 2.x est donc inutilisable ici. 1.3.5 est la dernière 1.x à fournir
`netstandard2.0`.

Piège d'API associé : **`NetPeer` hérite de `System.Net.IPEndPoint`** — il n'a pas de propriété
`EndPoint`, c'est lui l'endpoint (`peer.ToString()` donne `adresse:port`).

### Le rôle vient de la ligne de commande, pas de la config

Serveur et client partagent le même dossier de jeu, donc le même `BepInEx/config` : un réglage de
fichier ne pourrait pas les distinguer. Le rôle est lu dans `-coopmode=server|client` (ou la variable
`COOP_MODE`), sinon `Off`. Les deux plugins sont déployés ensemble et chacun se désactive si ce n'est
pas son rôle.

### Deux bugs trouvés et corrigés (les deux sont instructifs)

1. **Le socket se fermait quelques secondes après l'ouverture.** Le log annonçait « à l'écoute sur
   UDP 7777 » mais `netstat` ne montrait aucun socket et toute connexion échouait en
   `ConnectionFailed`. Cause : `CoopServerPlugin.OnDestroy()` appelait `Network.Dispose()` — et
   Subnautica **détruit le GameObject du manager BepInEx** au premier écran (le piège du §9). Le
   serveur ouvrait le port puis le refermait. Correctif : le réseau n'est plus possédé par le plugin
   mais par l'objet persistant (`ServerTickBehaviour`), qui ne meurt qu'à la fermeture du processus.
2. **Le motif de refus n'arrivait jamais au client.** Le serveur envoyait la réponse de refus puis
   appelait `Disconnect()` immédiatement : la coupure dépassait le message fiable, le client ne
   voyait qu'un `RemoteConnectionClose` sans explication. Correctif : le serveur envoie le motif et
   laisse le client fermer, avec un délai d'expiration côté serveur
   (`Protocol.HandshakeTimeoutSeconds`) pour qu'une connexion muette ne reste pas ouverte.

Le second est le plus vicieux : sans test du chemin d'erreur, la fonctionnalité « expliquer le refus »
aurait été livrée morte et personne ne l'aurait vu.

### Preuves mesurées

| Vérification | Résultat |
|---|---|
| Autotest de sérialisation (`NetProbe selftest`) | **17/17 OK**, exit 0 |
| Socket serveur réellement lié | `UDP 0.0.0.0:7777` **et** `[::]:7777` (PID du jeu) |
| Sonde : acceptation | handshake accepté, **8 pings / 8 pongs**, RTT min 11 ms / moy 18 ms / max 34 ms |
| Sonde : refus (modHash faux) | refusé + **motif transmis** : `liste de mods différente (client=deadbeef…, serveur=da39a3ee…)`, exit 1 |
| Sonde : refus (version de jeu fausse) | refusé, exit 1 |
| **Vrai client de jeu** (2ᵉ instance Subnautica) | `handshake ACCEPTÉ : 'killi' … 1/10 client(s)`, `clients=1` pendant 64 s, puis `'killi' déconnecté (raison=Timeout, **pings=64**)` |

Le compteur de pings côté serveur est la preuve la plus forte : il ne peut être alimenté que par la
boucle de ping du vrai client.

### Limites

- **Les deux instances partagent `BepInEx/LogOutput.log`** : les logs du client ne sont pas lisibles
  quand un serveur tourne sur la même machine. La vérification s'est donc appuyée sur le journal du
  serveur. Un fichier de log par instance serait utile pour déboguer en local — **fait en §13**.
- Le RTT observé en **loopback** est de 11–34 ms, ce qui est élevé pour de la boucle locale : le
  serveur pompe le réseau à chaque frame (~143 fps) sur une machine chargée. À surveiller quand de
  vrais paquets d'état circuleront.
- Le client de M1 **reste au menu** : il ne charge pas de monde (voulu — l'état du monde est M2).
- Aucun chiffrement, aucune authentification : le handshake valide la compatibilité, pas l'identité.
  La clé LiteNetLib n'est qu'un filtre à paquets parasites.
- `-netstandard2.0` pour `Coop.Model` : penser à ne jamais y faire entrer une dépendance Unity ou
  LiteNetLib, c'est ce qui la garde testable hors du jeu.

---

## 13. Un fichier de log par instance (validé le 2026-10-14)

**Verdict : serveur et client écrivent chacun leur log, simultanément, sur la même machine.**

### Le problème

`BepInEx/LogOutput.log` a un **nom fixe**. Deux instances lancées depuis le même dossier de jeu
(un serveur et un client en local) l'ouvrent en même temps : la seconde n'arrive pas à écrire et
**ses logs disparaissent**. C'est exactement ce qui s'était produit au jalon M1 — la vérification
avait dû se rabattre sur le journal du serveur, sans aucune preuve côté client. BepInEx n'expose pas
le chemin du log en configuration (vérifié dans `BepInEx.cfg` : la section `[Logging.Disk]` n'a pas
d'entrée de chemin), d'où un listener maison.

### La solution

`src/Shared/InstanceLogFile.cs` — un `ILogListener` qui écrit dans
**`BepInEx/logs/coop-<role>.log`**, branché sur la collection **globale** `Logger.Listeners`.

```csharp
var listener = InstanceLogFile.Attach(Log, "server");   // ou "client"
```

Comme il est branché sur le pipeline global, il capte **tout** le processus : les messages du jeu,
de BepInEx, et ceux de l'autre plugin (utile : on voit « plugin client inactif » dans le log serveur,
et inversement).

### Choix à ne pas défaire

- **Un seul fichier par processus** : seul le plugin dont le rôle correspond au lancement branche le
  listener, l'autre est inactif. Pas de doublon, pas d'ambiguïté sur qui écrit quoi.
- **`FileMode.Create`** : le fichier reflète l'exécution courante, pas un empilement de runs. Un
  en-tête donne le rôle, la date et le **pid**, ce qui suffit à relier un fichier à un processus.
- **`FileShare.Read`** : un éditeur peut lire le log pendant que le jeu tourne.
- **`AutoFlush = true`** : un crash du jeu ne doit pas emporter les dernières lignes, précisément le
  moment où on en a le plus besoin.
- **Échec non bloquant** : si le fichier ne s'ouvre pas, on journalise un avertissement et on
  continue. Le logging ne doit jamais faire tomber le jeu.

### Preuves

Deux instances réelles lancées en parallèle (`-coopmode=server` et `-coopmode=client`) :

| Fichier | Contenu |
|---|---|
| `BepInEx/logs/coop-server.log` | 18,6 Ko, en-tête `=== Coop server — 17:41:17 (pid 3288) ===` |
| `BepInEx/logs/coop-client.log` | 5,5 Ko, en-tête `=== Coop client — 17:41:42 (pid 2404) ===` |

Le plus important : le log **client** contient enfin ce qui était invisible en §12 —

```
[net] connecté au serveur 127.0.0.1:7777 — envoi du handshake
[net] handshake ACCEPTÉ (tick serveur=490) — début des pings
[ping] seq=1 rtt=102ms tickServeur=493
[ping] pongs=10 rtt dernier=35ms min=7ms moy=31ms max=102ms dernierTickServeur=688
```

ce qui apporte la **preuve côté client** des pongs et du RTT, mesurée par le vrai client de jeu —
la lacune de vérification du jalon précédent est donc comblée, pas contournée.

### Limites

- Les messages **antérieurs au chargement du plugin** (préloader, chainloader BepInEx) ne sont pas
  repris dans le fichier : le listener n'existe pas encore. Ils restent dans `LogOutput.log`.
- Un seul run conservé par rôle (pas d'historique). Si besoin, ajouter un horodatage dans le nom.
- Deux instances du **même** rôle sur une machine se disputeraient le même chemin : la seconde échoue
  proprement avec un avertissement et se contente de `LogOutput.log`.
- Le fichier grossit sans rotation ; sans importance pour du débogage local, à revoir si un serveur
  tourne des semaines.

---

## 14. Prérequis de M2 : client dans le monde + garde de sauvegarde (validé le 2026-10-14)

**Verdict : les deux instances chargent la même partie, et le client n'écrit jamais dedans.**

M2 (présence des joueurs) suppose deux instances *dans le monde* — donc deux instances qui ouvrent la
même sauvegarde, avec la même `sessionId`. C'est un problème de propriété : si les deux écrivent, la
partie est corrompue. Ce prérequis devait être réglé **avant** d'ajouter le premier état réseau,
parce que le jour où on le découvre au milieu de la synchronisation, on ne sait plus qui a cassé quoi.

### Structure ajoutée

```
src/Shared/AutoLoadBehaviour.cs     extrait du serveur -> partagé (via <Compile Include> dans les 2 csproj)
src/Coop.Client/SaveGuard.cs        prefix Harmony sur IngameMenu.SaveGameAsync + autotest
src/Coop.Client/Coop.Client.csproj  référence Assembly-CSharp (le client charge le monde désormais)
```

### Le point de blocage a été choisi sur preuve IL, pas au jugé

Un scan des appelants dans `Assembly-CSharp.dll` (outil `tools/ApiDump`, mode `callers`) donne :

- `IngameMenu.SaveGameAsync` (itérateur privé, sans paramètre) n'a que **deux** appelants :
  `IngameMenu.SaveGame` (le bouton « Sauvegarder » du menu pause) et
  `IngameMenu/<QuitGameAsync>d__65.MoveNext` (la sauvegarde au moment de quitter) ;
- `SaveToTemporaryStorageAsync` / `SaveToDeepStorageAsync` n'ont **aucun autre** appelant que
  `SaveGameAsync`.

Un seul prefix sur `SaveGameAsync` ferme donc *tous* les chemins d'écriture, y compris celui de la
fermeture du jeu — celui qu'on n'aurait pas pensé à tester.

**Contre-exemple instructif :** `SaveLoadManager.GetAllowWritingFiles()` a un nom qui donne envie de
le patcher, mais le scan montre qu'il n'est appelé que par `LargeWorldStreamer.TryUnloadBatch` —
c'est un garde-fou de **déchargement de cellules**, pas de sauvegarde. Le patcher n'aurait rien
protégé du tout ; seul le scan IL a évité cette erreur.

### Choix à ne pas défaire

- **Échec fermé** : si le patch échoue, le chargement du monde est **annulé** et le client reste au
  menu. Un client bloqué au menu est réparable ; une sauvegarde corrompue ne l'est pas.
- **On remplace le résultat** par une coroutine vide au lieu de le laisser à `null` : les deux
  appelants écrivent `StartCoroutine(SaveGameAsync())`, donc un `null` ferait lever une exception
  Unity dans `QuitGameAsync` et laisserait le jeu à moitié quitté.
- **Le rôle décide, pas la config** : la garde est installée par le plugin client uniquement. Le
  serveur est le propriétaire légitime du monde et doit continuer à sauvegarder.
- **Autotest par argument explicite** (`-coopdebug=saveguard`), jamais en permanence : il déclenche
  une vraie sauvegarde, ce n'est pas quelque chose qu'on veut voir tourner par accident.
- **Le `-coopdebug` est parsé, pas deviné** : `Environment.GetCommandLineArgs()`, comparaison
  ordinale insensible à la casse.

### Pourquoi un autotest, et pas seulement « aucun fichier modifié »

Constater qu'aucun fichier de sauvegarde n'a bougé ne prouve **rien** : ça serait également vrai si
la garde était inopérante et que rien n'avait tenté d'écrire. Le chemin de fermeture du jeu, par
exemple, ne tente aucune sauvegarde quand on le tue — l'observer ne dit rien. L'autotest déclenche
donc une **vraie** sauvegarde depuis le client et vérifie que le compteur d'interceptions s'incrémente.
Le test doit être capable de *détecter* une écriture, sinon il ne teste rien.

### Bug attrapé en cours d'écriture

La première version cherchait le menu pause via une méthode utilitaire `FindMenu()` qui appelait
`Thread.Sleep(2000)` en boucle. Comme ce n'était pas une coroutine, cela aurait **gelé le jeu entier**
(jusqu'à 240 s). La recherche a été repliée dans la coroutine `Start()` avec `yield return
new WaitForSeconds(2f)`. Un `Thread.Sleep` dans un `MonoBehaviour` est toujours un bug en puissance.

### Preuves mesurées

Les mesures viennent de **deux passes distinctes**, volontairement : l'autotest ne tourne qu'à la
demande, il ne doit donc pas être activé pendant la passe « deux instances dans le monde ». Chaque
passe est citée avec ses horodatages.

**Passe A — garde de sauvegarde** (client seul, `-coopmode=client -coopdebug=saveguard`) :

| Vérification | Résultat |
|---|---|
| Cible réellement patchée | `[garde] sauvegarde cliente désactivée — patché : IngameMenu.SaveGameAsync()` (20:03:27,876) |
| Cible **non** patchée côté serveur | `grep -c '[garde]' coop-server.log` = **0** |
| **Tentative de sauvegarde interceptée** | `[garde] sauvegarde interceptée et bloquée (n°1)` puis `[autotest] RÉSULTAT OK : tentative interceptée (compteur 0 -> 1)` (20:03:50,942) |
| Aucune exception levée | aucune ligne `Exception`/`NullReference` dans le log client après le déclenchement |

**Passe B — deux instances dans le monde** (serveur puis client, DLL finales, sans autotest) :

| Vérification | Résultat |
|---|---|
| Serveur : entre dans le monde | `*** MONDE CHARGÉ : scene 'Main' ***` (20:04:31,491) |
| Client : entre dans le monde | `*** MONDE CHARGÉ : scene 'Main' ***` (20:05:57,366) |
| Liaison tenue entre les deux instances | serveur `handshake ACCEPTÉ : 'killi' … 1/10 client(s)` (20:05:38,502), `clients=1` sur 38 heartbeats ; client `handshake ACCEPTÉ (tick serveur=1691)` (20:05:38,603) |
| Client : boucle de ping stable | `pongs=184 rtt dernier=28ms min=14ms moy=26ms max=43ms` |
| Serveur : tick tenu, dans le monde | `tick=5479 tauxInstantane=20.00/s simule=273,95s reel=275,00s abandonnes=20 scene='Main'` |
| **Sauvegarde intacte** | sha1 des **13** fichiers de `SNAppData/SavedGames` **identiques** avant/après les deux passes ; fichier le plus récent daté **16:28**, donc antérieur à tous les runs |

Le compteur d'interceptions est la preuve forte : il ne peut être incrémenté que par un passage réel
dans `IngameMenu.SaveGameAsync`, c'est-à-dire exactement le point de passage unique identifié par le
scan IL. Et il était nécessaire de le mesurer : la fermeture du jeu, elle, **ne tente aucune
sauvegarde**, donc observer « rien n'a changé » à la fermeture ne disait rien de la garde.

### Limites

- L'autotest s'est déclenché **1,0 s avant** la ligne `MONDE CHARGÉ` (20:03:45,938 vs 20:03:46,941) :
  il prouve le prefix sur le point de passage réel, mais il n'a pas reproduit un clic sur le bouton
  « Sauvegarder » d'un menu pause ouvert *dans* le monde. Le chemin de code exercé est identique, la
  fidélité moindre.
- L'autotest a dû ouvrir le menu pause (`IngameMenu.main.Open()`) car il le trouve **inactif dans la
  hiérarchie** menu fermé : `actifDansHierarchie=False, composantActif=True`. `Open()` ne l'a d'ailleurs
  pas activé (`actifDansHierarchie=False` après coup), et l'appel a malgré tout atteint le point de
  passage patché — c'est le remplacement du résultat par une coroutine vide (et non `null`) qui rend
  ce cas sain.
- **Rien ne prouve que la sauvegarde du serveur fonctionne encore** : le serveur n'a jamais tenté de
  sauvegarder pendant ce run. La garde est censée être purement cliente (elle n'est installée que par
  le plugin client), mais ce n'est pas démontré par une mesure.
- La garde couvre **un seul chemin** : `IngameMenu.SaveGameAsync`. Les autres écritures du jeu
  (options, `steam_autocloud.vdf`, et tout ce que le streaming de monde pourrait écrire au
  déchargement d'une cellule) ne sont pas concernées. Aucune n'a été observée pendant le run, mais
  « non observée » n'est pas « impossible ».
- Le client et le serveur partagent toujours le même dossier `SNAppData` et la même `sessionId`.
  C'est un choix assumé pour un test en local sur une seule machine ; un vrai serveur aura son propre
  dossier de jeu.
- Le chargement cible toujours le **premier** slot trouvé (limite héritée de M0.5, cf. §10).

---

## 15. M2.0 — un second corps de joueur : fabriqué, et rendu (validé le 2026-10-15)

**Verdict : le client sait fabriquer un second corps de joueur, et il se dessine — mesuré, pas regardé.**

M2 (présence des joueurs) suppose de voir bouger un autre joueur. Or **rien dans l'API n'expose de
prefab de joueur** : `tools/ApiDump`, champs privés compris, ne trouve sur `Player` ni propriété ni
champ de modèle. Le corps n'existe qu'à l'exécution, construit par le jeu dans la hiérarchie de
`Player.mainObject`. Écrire la synchronisation des joueurs avant de savoir si un second corps **peut
exister** et **s'afficher**, c'est risquer de jeter le travail : ces deux questions sont donc traitées
seules, avant M2 proprement dit.

### Fichiers ajoutés

```
src/Coop.Client/RemoteAvatar.cs            fabrique : choix du corps, refus si dangereux, copie, nettoyage
src/Coop.Client/AvatarProbeBehaviour.cs    sonde -coopdebug=avatar : relevés, mesures, images
tools/ApiDump/Program.cs                   inclut désormais les champs privés (c'est là qu'était l'information)
src/Coop.Client/Coop.Client.csproj         + AnimationModule, PhysicsModule, AudioModule,
                                           ScreenCaptureModule, ImageConversionModule
```

Le dernier point mérite d'être noté : **Unity 2019.4 éclate `UnityEngine` en modules**, et `Animator`,
`Collider`, `Rigidbody`, `AudioListener`, `ScreenCapture`, `EncodeToPNG` sont *forwardés* depuis
`UnityEngine.dll`. Sans ces références, rien de tout cela ne compile — le message d'erreur ne le dit
pas, il renvoie seulement vers l'assembly à ajouter.

### La hiérarchie réelle du joueur (relevée, pas supposée)

```
Player                       [calque Player] Player, GroundMotor, LiveMixin, PlayerController,
                                              UnderwaterMotor, Collider, CharacterController, ...
├─ body                      [calque Default]   <- le corps visible
│  └─ player_view            [calque Viewmodel] Animator player_view_controller, ArmsController, ...
│     ├─ export_skeleton                          (squelette)
│     └─ male_geo                                 diveSuit, radiationSuit, reinforcedSuit,
│                                                 scubaSuit, stillSuit, palmes, bouteilles...
├─ camPivot/camRoot          MainCamera, MainCameraControl
├─ Inventory Storage / Equipment Storage / Pending Items
└─ SpawnPlayerSounds, SpawnPlayerWorldArrows, acidLoopingSound, ...
```

Deux faits utiles : **tout ce qui est visible tient sous un seul nœud (`Player/body`)** — un seul point
d'entrée suffit — et **ce nœud est sur le calque `Viewmodel`**, celui que le jeu réserve à la vue
première personne. C'est la première chose que la sonde a apprise, et elle a coûté une mesure.

### Le point de copie, choisi sur des faits

La copie part de `Player/body`, jamais de `Player` : le composant `Player` est un singleton statique
(`Player.main`), donc instancier le root exécuterait l'`Awake` d'un second joueur. La fabrique
**refuse** de copier un sous-arbre contenant `Player`, `Camera`, `AudioListener`, `PlayerMotor`,
`GroundMotor` ou `LiveMixin` — chacun de ces composants détournerait l'autorité, le rendu ou l'audio.

Sur la copie, tout ce qui n'est pas visuel est retiré (`Transform`, `Renderer`, `MeshFilter`,
`Animator` sont conservés), et **chaque type retiré est journalisé**.

### Ce que la sonde mesure, et pourquoi l'œil ne suffit pas

« Ça a l'air de rendre » ne vaut rien : un objet peut être parfaitement construit et n'apparaître
nulle part (calque hors `cullingMask`, renderer désactivé, maillage nul, position dans une paroi).
Trois instruments, dans cet ordre :

1. **Différence de pixels en jeu, avec témoin.** Cinq clichés à une frame d'intervalle : masquée,
   masquée, visible, visible, masquée. Le bruit se mesure entre deux clichés du **même** état, le
   signal entre deux états différents — **même intervalle**, donc comparaison honnête.
2. **Rendu isolé.** Notre propre caméra, sur un calque libre (`25`) et un fond magenta : rien ne peut
   occulter la copie, et un maillage sombre reste comptable (noir sur noir ne se mesure pas). L'image
   est écrite en PNG, et l'**étendue du sujet** est relevée : la couverture seule ne distingue pas
   « tout le corps, petit » de « un morceau de corps, grand ».
3. **Contribution par maillage.** Chaque renderer est éteint à tour de rôle : on sait ce qui dessine
   réellement, au lieu de le déduire d'un total.

### Preuves mesurées (run du 2026-10-15 11:37, DLL finales)

| Vérification | Résultat |
|---|---|
| Corps identifié | `Player/body`, **24 SkinnedMeshRenderer**, 88 526 sommets, 37 matériaux |
| Composants de gameplay retirés | **16 détectés -> 16 retirés, 0 restant** (vérifié en relisant la copie) |
| Aucun composant dangereux copié | ni `Player`, ni `Camera`, ni `AudioListener` : la garde n'a jamais eu à refuser un candidat |
| **La copie se dessine en jeu** | signal **15 781** et **16 043** px (une frame d'écart, changement d'état) contre un bruit de **399** et **364** px (même état, même écart) -> **rend = vrai**, rapport 40x |
| Calques : objet du monde | les 24 renderers basculés sur `Default` rendent autant que sur `Viewmodel` (mesures précédentes : 13 164 vs 13 423 px ; 10 274 vs 10 660 px) : **le calque est un choix, pas une contrainte de rendu** |
| **Rendu isolé** | 10 868/262 144 px non-fond (4,1 %), **étendue du sujet 100 x 242 px** sur 512x512 — la forme attendue d'un corps d'1,90 m vu à 3,6 m (environ 290 px de haut), donc le corps est là en entier, pas un fragment |
| Images produites | `coop-avatar-isolated.png` (30 677 o, corps seul sur fond magenta) et `coop-avatar-proof.png` (1 080 613 o, vue en jeu) |
| Maillages contribuant visiblement | **2/25** : `diveSuit_body_geo` (9 528 px) et `diveSuit_hands_geo` (678 px) — les autres sont *derrière* la combinaison la plus externe, pas absents (voir limites) |

### Cinq défauts de méthode corrigés en route (tous instructifs)

1. **Compter les triangles ne marche pas** : `mesh.triangles` est interdit sur un maillage non lisible
   (tous ceux du jeu), Unity ne renvoie que des erreurs et un zéro trompeur. Compter `vertexCount`.
2. **Retirer des composants en une passe ne suffit pas** : Unity refuse de retirer un composant dont un
   autre dépend (`Can't remove ConditionRules because ArmsController depends on it`). La première
   version annonçait « 16 retirés » alors que `ConditionRules` restait. Deux passes, et le compte rendu
   est établi **en relisant la copie** — ce qui est écrit correspond à ce qui reste.
3. **La capture d'écran n'existe pas au moment où on la cherche** : Unity l'écrit en fin de frame,
   plus tard que deux frames. Le premier test l'a déclarée absente alors qu'elle est arrivée. Il faut
   attendre le fichier.
4. **Comparer un signal de 3 frames à un bruit de 7 frames n'a pas de sens** : le verdict basculait
   d'un run à l'autre sans que le rendu change (bruit 456 px ici, 2 111 px là, signal stable à
   11 000 px). Toutes les mesures se font maintenant sur le **même** intervalle.
5. **Alterner l'état à chaque frame met l'avatar dans chaque écart** : les quatre mesures donnaient
   11 346, 11 797, 11 505 et 11 434 px — quatre fois le même signal, dont un étiqueté « bruit », ce qui
   faisait échouer un avatar qui rendait. Le bruit se mesure entre deux clichés **du même état**.

### Le placement, lui aussi, a dû être mesuré

Premier essai : la copie 3 m devant `Player.main`. Résultat sans ambiguïté — **3 renderers sur 25 dans
le frustum des caméras, aucun pixel changé** : le joueur est à l'intérieur du module de survie, et 3 m
"devant lui", c'est dans la coque. La sonde place donc la copie devant la **caméra**, avec un produit
scalaire de 1,000 par rapport à son axe pour que le placement soit vérifiable et non approximatif.

Au passage, `Renderer.isVisible` a été disqualifié : **3/25 alors que 15 781 px changent**. Sur ces
maillages squelettiques copiés, il est faux, comme la boîte englobante (4,01 m annoncés pour un corps
d'1,90 m). Aucun culling ne devra être bâti là-dessus : la mesure de pixels est l'instrument fiable.

### Limites (le programme de M2.1)

- ~~La tenue affichée n'est pas encore choisie.~~ **Cette limite était fausse, corrigée en §16** : les
  tenues ne sont pas actives simultanément. La visibilité se fait par activation de GameObject, donc la
  copie hérite de la tenue réellement portée, sans travail supplémentaire.
- **L'`Animator` est conservé mais pas piloté** : la copie est en pose figée. Il faudra le piloter
  (nage, marche, immobile) depuis l'état réseau.
- Le **PDA** (`PlayerPDA/Mesh`) fait partie du corps et se retrouve dans la copie.
- L'isolement se fait sur un calque marqué « libre » (ni nommé, ni dans le `cullingMask` du jeu) ;
  l'hypothèse « aucun autre objet ne l'utilise » n'est pas vérifiée par une mesure.
- La luminance du rendu isolé est faible (22/255) : sur ce calque, aucune lumière du jeu n'éclaire la
  copie. Cela ne gêne pas la mesure (fond magenta) mais l'image de preuve est sombre.
- **Le rendu n'a été vérifié que sur une seule instance et un seul modèle** (`male_geo`) : la variété
  des corps du jeu (et le modèle féminin, s'il existe) n'a pas été explorée.
- L'autotest reste un autotest : il prouve qu'une copie se dessine, **pas** qu'un avatar distant se
  déplace correctement — c'est M2.1, et aucun paquet réseau n'a encore transporté de position.

---

## 16. M2.1 — la copie se dessine, la tenue suit, la tête manque (2026-10-15)

**Verdict : le corps d'avatar se dessine (mesuré), la tenue portée est respectée sans travail
supplémentaire, et la tête est absente — c'est le seul point ouvert, et il est caractérisé.**

### Correction d'une affirmation du §15

Le §15 annonçait que « les huit branches de tenues sont actives et se recouvrent ». **C'est faux**, et la
mesure le dit clairement : la visibilité des tenues se fait par **activation de GameObject**, pas par
`Renderer.enabled`. Sur les 23 maillages qui ne se dessinent pas, 21 ont `activeInHierarchy=False` —
ils appartiennent aux tenues que le joueur ne porte pas. Conséquence heureuse : **la copie hérite
automatiquement de la tenue portée**, il n'y a rien à choisir ni à câbler pour M2.1. Le « 2 maillages
sur 25 » mesuré au §15 s'explique entièrement par là : seuls les maillages de la tenue portée sont
candidats, et deux d'entre eux se dessinent.

### Le test de rendu, rendu robuste

Le verdict du §15 changeait de réponse sans que le rendu change, parce que le bruit de la scène est
erratique (une vague, une lueur de HUD : 2 381 px sur une frame isolée contre 214 px sur une autre). Le
test échantillonne maintenant **par paires du même état** — `[masquée, masquée]`, `[visible, visible]`,
puis on recommence — et compare des **médianes** : quatre échantillons de bruit, trois de signal, tous à
une frame d'écart.

| Mesure | Valeurs |
|---|---|
| Signal (changement d'état, une frame) | 10 656, 10 872, 10 869 px -> **médiane 10 869** |
| Bruit (même état, même écart) | 285, 212, 410, 300 px -> **médiane 293** |
| Décision | seuil 2 000 px (3x le bruit, plancher) -> **la copie rend** |

Deux erreurs de motif ont été corrigées avant d'y arriver, et elles se ressemblent : un motif
« quatre masquées puis quatre visibles » ne laisse **qu'un seul** vrai changement d'état, les trois
autres « signaux » étant des paires du même état (426, 295, 272 px) ; et un motif alterné à chaque
frame met l'avatar dans **chaque** écart, si bien que le « bruit » mesuré était le signal lui-même.
Dans les deux cas, le test ne mesurait plus ce qu'il annonçait.

### La tête : ce qui est mesuré, et ce qui a été éliminé

`diveSuit_head_geo` est **actif dans la hiérarchie** (contrairement aux 21 maillages des autres tenues),
activé, et son maillage est réel : cuit par Unity (`BakeMesh`), il donne une tête de
**0,166 x 0,329 x 0,275 m** centrée **0,09 m au-dessus** de l'origine de la copie, c'est-à-dire à
hauteur de tête. Et il produit **0 pixel** — dessiné seul, ou même avec un **matériau neuf**. L'étendue
du corps visible (100 x 242 px, soit environ 1,59 m) est exactement celle d'un corps sans tête
(1,59 m + 0,33 m = 1,92 m, soit les 293 px attendus avec la tête).

Hypothèses testées puis **falsifiées**, chacune par une mesure :

| Hypothèse | Test | Résultat |
|---|---|---|
| Bornes de culling figées sur l'original | `updateWhenOffscreen` + bornes du maillage | 2/25 avant, **2/25 après** |
| Occlusion dynamique | `allowOcclusionWhenDynamic = false` | aucun changement |
| Maillage dégénéré | `BakeMesh` | tête correcte, à sa place |
| Os absents de la copie | comptage des os | **5/5 dans la copie** (os de mâchoire/yeux, étendue 13 cm) |
| GameObject inactif | `activeInHierarchy` | **True** |
| `forceRenderingOff` hérité | relevé sur les 25 renderers | **0** renderer concerné |
| Matériau en cause | matériau neuf, opaque | **0 px** aussi |
| Géométrie absente | comptage des indices du maillage cuit | **39 399 indices** (13 133 triangles), 7 553 sommets — il y a bien de quoi dessiner |
| Recréer le renderer | `Destroy` + `AddComponent<SkinnedMeshRenderer>` | `NullReferenceException`, **fait tomber le jeu** — piste fermée, pas seulement infructueuse |

**Deuxième source de tête essayée et écartée.** Le jeu a un second objet de tête,
`Player/camPivot/camRoot/player_head` (`SkinnedMeshRenderer`, maillage `scuba_head`), qui vit **hors** de
`Player/body`. Copié puis rattaché à l'avatar, il donne un maillage cuit **vide** (0 x 0 x 0) alors qu'il
porte 12 252 indices : ses sommets s'effondrent tous à l'origine, faute d'os dans la copie (son tableau
d'os est vide, donc « 0 os remappé, 0 non remappé »). Ce n'est pas une source utilisable, et le code qui
le copiait a été retiré plutôt que laissé derrière un garde-fou : un objet fantôme à 78 m ne doit pas
rester dans la scène d'un client.

Un **second exemple du même symptôme** a été trouvé au passage : `scubaSuit/scuba_head`, à l'intérieur
du corps, cuit correctement (0,205 x 0,351 x 0,294 m, 12 252 indices) et ne se dessine pas non plus.
Deux têtes, deux branches de tenues différentes, même comportement — ce n'est donc pas un défaut propre
à la tenue portée. À noter que les trois maillages de tête du jeu (les deux ci-dessus et les versions
`radiationSuit_head_geo` / `diveSuit_head_geo`) partagent la même particularité : **5 os seulement**
(mâchoire et yeux), là où le corps en a 43 et les mains 36.

### Ce qui reste ouvert (suite de M2.1)

- **La tête** : neuf hypothèses éliminées, et les deux sources évidentes le sont aussi. La suite ne doit
  plus viser le maillage, le matériau, l'activation, les bornes, les os, le calque ni le renderer, mais
  **la façon dont le jeu lui-même dessine une tête** — c'est la seule chose qui n'a pas été regardée. Un
  avatar sans tête est un défaut visible mais qui ne bloque rien : il ne doit pas retarder le réseau.
- ~~L'`Animator` n'est pas piloté : la copie est en pose figée.~~ **Corrigé en §17** : le squelette
  répondait en réalité à **0 paramètre sur 201** avant correction, et à **200 sur 201** après. Le câblage
  « état réseau -> paramètres » reste à faire, mais la partie moteur est prouvée.
- Le **PDA** fait partie du corps copié ; dans la copie il est **inactif dans la hiérarchie** et ne se
  dessine pas (mesuré), donc il ne gêne pas. À retirer seulement si la composition de l'avatar est décidée
  pièce par pièce.
- Aucun paquet réseau n'a encore transporté la moindre position : c'est le travail de M2.1 proprement dit,
  et il n'est bloqué ni par la tête ni par la pose.

---

## 17. M2.1 — l'avatar peut prendre une pose (validé le 2026-10-15)

**Verdict : l'`Animator` conservé sur la copie pilote réellement le squelette — après un correctif — et
les 201 paramètres qu'il écoute sont relevés.**

### Le problème, mesuré avant toute correction

L'animator était présent, alimenté, et **silencieusement inerte** :

- `pose spontanée : 0,0000 m` de déplacement moyen des sommets en 0,5 s (mesure par `BakeMesh`, qui donne
  les sommets réellement déformés par le skinning) ;
- et **aucun** des 201 paramètres testés un par un ne déplaçait le squelette : 0 sur 201.

Un `Animator` qui répond à rien est plus traître qu'un animator absent : rien ne le signale, et on ne le
découvre qu'au moment où un joueur distant devrait bouger.

### La cause

`Animator.cullingMode` vaut **`CullUpdateTransforms`** par défaut : Unity cesse alors de mettre à jour les
os d'un maillage qu'il ne considère pas visible. Ce jugement est **faux sur une copie** — mesuré :
3 renderers sur 25 déclarés « visibles » alors que la copie change 10 000 px à l'écran (cf. §15 et §16, où
la même méfiance s'applique déjà aux bornes et à `Renderer.isVisible`). Le squelette restait donc figé.

### Le correctif

`AlwaysAnimate`, appliqué dans la fabrique (`MakeAnimationIgnoreVisibility`) : l'animation ne dépend plus
d'un jugement de visibilité, qui n'a aucun sens pour un avatar qu'on veut précisément voir bouger.

### Preuves mesurées (run du 2026-10-15 12:33, DLL finales)

| Vérification | Avant | Après |
|---|---|---|
| `cullingMode` appliqué | `CullUpdateTransforms` | `AlwaysAnimate` (1 animator sur 1) |
| Pose spontanée (0,5 s) | 0,0000 m | **0,0017 m** |
| Paramètres qui déplacent le squelette | **0/201** | **200/201** |
| Exemples de déplacement | — | `vr_active` 0,2259 m, `move_speed_x` 0,0974 m, `move_speed_y` 0,0962 m, `using_pda` 0,0583 m |
| Signal de rendu | 9 812 px | **15 451 px** (l'avatar bouge, donc plus de pixels changent) |
| Bruit de scène | 383 px | 326 px |

Le signal de rendu qui augmente est un effet attendu du correctif, pas une coincidence : un squelette qui
bouge change plus de pixels qu'un squelette figé. Un seul paramètre sur 201 reste sans effet ; il n'a pas
été identifié, et c'est écrit ici plutôt que passé sous silence.

### Ce que le réseau devra pousser (relevé sur le vrai contrôleur)

Le contrôleur est `player_view_controller`, et les paramètres utiles pour un joueur distant sont déjà
nommés comme le gameplay — il n'y a donc **pas de nomenclature à inventer** :

```
move_speed, move_speed_x, move_speed_y, move_speed_z,
is_underwater, on_surface, swimming_fast, can_exit,
in_seamoth, in_exosuit, using_pda, using_tool, using_tool_first, using_tool_alt,
suit_first_use, pda_distance, vr_active,
holding_* (19 paramètres : knife, flashlight, constructor, seaglide, stasisrifle, ...)
```

C'est la liste à porter dans `docs/protocol.md` le jour où les paquets joueur sont écrits.

### Limites

- **La pose n'est pas encore pilotée par un état de jeu** : la sonde pose les paramètres à la main pour
  mesurer. Le câblage « état reçu -> paramètres » appartient au netcode, et c'est le prochain travail.
- `vr_active` déplace le squelette de 0,23 m : c'est un paramètre à ne **jamais** activer par accident sur
  un avatar (le jeu a son propre avion, et il n'est pas là pour ça).
- La mesure est faite en posant les paramètres un par un : elle prouve que le squelette répond, pas qu'une
  combinaison donnée produit la pose voulue (marche, nage, immobile). Cette vérification-là viendra avec
  l'état réseau, qui donnera les valeurs réelles.
- L'avatar reste **sans tête** (cf. §16) : c'est un défaut visible, mais indépendant de la pose.