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
| Autorité | Serveur = vérité sur **tout** sauf le transform du joueur local, qui est **client-authoritatif** — relayé aujourd'hui **sans borne** (constaté au §43.3) | Corrige la faille de Nitrox **partout sauf sur le joueur** ; la revendication est corrigée au §43.3, et P1 doit la rendre vraie |
| Prédiction client | Joueur local uniquement — **sans réconciliation à ce jour** (§43.3) | Obligatoire pour le ressenti, et c'est le trou d'autorité qui reste |
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
| **M2** | Présence joueurs | 2 clients se voient bouger ; self prédictif, distant interpolé — **clos le 2026-10-20 (§54.1)** : B (mouvement vu), interpolation `RemoteAvatar` 0,05 s, V (trois clients) | moyen |
| **M3** | **Une créature synchro, IA client coupée** | position identique chez les 2 clients, IA uniquement serveur — **clos le 2026-10-20 (§54.2)** : E (deux clients), IA relue coupée, W (trois clients) | **élevé — valide le modèle** |
| **M4** | Un véhicule (Seamoth) | **clos le 2026-10-20 (§56)** : position diffusée à 10 Hz et interpolée (§33), conduite autoritaire (§40), **énergie mesurée en jeu** (`200,00 -> 100,00`, appliquée chez le client) et **modules transportés puis appliqués** (`transport=True application=True`) | moyen |
| **M5** | Première action autoritaire | pickup : refus serveur si déjà réclamé — **clos le 2026-10-20 (§54.3)** : L (accordé/refusé, geste bloqué), O/Q (objet retiré, retrait rejoué), 9 tests du registre | moyen |
| **M6** | Pipeline de contenu | **création et retrait d'entité faits (§41, §42)** : l'hôte fabrique, les clients instancient avec la clé du serveur ; un retrait et un ramassage se répercutent ; reste l'item **custom** et son AssetBundle livré au client | moyen |

M3 est le vrai test : si une créature pilotée serveur avec IA client désactivée rend
correctement chez 2 clients, l'architecture est bonne pour tout le reste.

> **Révisé au §43 (2026-10-19)** : ce tableau est un journal de jalons, pas l'état courant. La séquence
> qui fait foi pour la suite est celle du §43.5, et **M6-contenu (AssetBundles) y est gelé** jusqu'à ce
> qu'une session réelle de 30 minutes tienne.
>
> **État au 2026-10-20** : M0, M1, M2, M3, **M4** et M5 sont **clos** (critères du §6 prouvés, §54 et §56) ;
> M6 reste ouvert (AssetBundle gelé, en attente d'une session réelle de 30 min).
> **Complément au 2026-10-22** : M6 reste ouvert (P4 et AssetBundle gelés), mais l'**économie** du monde
> est désormais arbitrée de bout en bout — ramassage, débit, adoption, coût du craft, cadence et latence
> mesurée (v19–v21, §67–§73) : c'est l'infrastructure dont M6 aura besoin, pas M6 lui-même.

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

**Mise à jour du 2026-10-17 — deux remparts, et le second est mesuré.** Le scan IL refait
(`tools/ApiDump callers Assembly-CSharp.dll SaveFile`) donne un point d'écriture unique pour les données de
sauvegarde : `SaveLoadManager/GameInfo.SaveFile(string, byte[])` n'est appelé que par
`GameInfo.SaveIntoCurrentSlot`, lui-même appelé par le seul itérateur de `SaveToTemporaryStorageAsync`, que
seul `IngameMenu.SaveGameAsync` atteint. La garde patchait déjà l'entrée ; elle patche désormais **aussi**
le point d'écriture, et l'autotest (`-coopdebug=saveguard`) l'appelle **directement** au lieu de passer par
le menu : `[autotest] entrée bloquée … [autotest] écriture bloquée : SaveFile intercepté hors du menu
(compteur 0 -> 1)` puis `[autotest] sauvegarde cliente bloquée (entrée+écriture) -> verdict=True`. Le
chemin « un client qui contourne le menu » n'écrit donc plus, et c'est constaté, pas déduit. Ce qui reste
ouvert, et c'est écrit tel quel : `SaveLoadManager.CreateSlotAsync` (« nouvelle partie ») écrit une
partie **neuve** et reste autorisé (sinon le mod casserait le jeu solo) ; `ScreenshotManager` écrit des
PNG, pas des données de sauvegarde ; et **le serveur n'a toujours pas été vu en train de sauvegarder**
pendant un run — c'est la seule moitié qui manque à « la sauvegarde du serveur fonctionne encore ». La
batterie `tools/verify.sh` rejoue le contrôle : étape **D**.

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

> **Résolu au §18** : la cause est `shadowCastingMode = ShadowsOnly`, un état volontaire posé par le jeu.
> Ce qui suit reste la trace utile des neuf pistes éliminées — et de la dixième, qui était de lire Nitrox.

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

- ~~**La tête** : neuf hypothèses éliminées, et les deux sources évidentes le sont aussi.~~ **Résolu en
  §18** : la dixième voie — regarder comment un autre mod (Nitrox) fait — a donné la réponse. Le renderer
  de la tête était en `ShadowsOnly`, c'est-à-dire *volontairement* non dessiné par le jeu.
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
- ~~L'avatar reste **sans tête** (cf. §16).~~ **Résolu en §18** : la tête se dessine (962 px mesurés).

---

## 18. M2.1 — la tête, résolue par la lecture de Nitrox (validé le 2026-10-15)

**Verdict : la tête se dessine. La cause n'était pas dans la copie mais dans un état du renderer que le jeu
pose lui-même — un état que les neuf hypothèses du §16 ne pouvaient pas atteindre, parce qu'aucune ne
regardait `shadowCastingMode`.**

### La lecture qui a tranché

Source : `SubnauticaNitrox/Nitrox`, commit `7815b4e` (v1.9.0.0), fichier
`NitroxClient/GameLogic/LocalPlayer.cs`, **ligne 151**. Le mécanisme, cité exactement :

```csharp
// Cheap fix for showing head, much easier since male_geo contains many different heads
prototype.GetComponentInParent<Player>().head.shadowCastingMode = ShadowCastingMode.On;
GameObject clone = Object.Instantiate(prototype, Multiplayer.Main.transform, false);
prototype.GetComponentInParent<Player>().head.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
```

Autrement dit : le renderer de la tête du joueur local est laissé en `ShadowsOnly` — **il projette une
ombre mais n'est jamais dessiné** — parce que la vue première personne montre une tête séparée. Tout ce
que le §16 décrivait comme un mystère (actif, activé, calque bon, matériau sans effet, géométrie cuite
correcte avec 39 399 indices, os présents, et **zéro pixel**) en découle directement : ce n'est pas un
défaut, c'est un choix de rendu, et il est copié avec le reste du corps.

Nitrox contourne le problème en basculant le renderer de la **source** le temps de l'`Instantiate`, puis en
la remettant en place. Le correctif retenu ici est différent et plus étroit : la copie est déjà détachée,
donc on se contente de **réactiver les renderers de la copie** après instanciation, sans jamais toucher
l'objet du joueur local (un mod qui modifie l'état partagé pendant un instant donné est une source de bugs
difficile à reproduire).

### Le correctif

`RemoteAvatar.AllowRenderingInsteadOfShadowsOnly`, appelée par `MakeCloneDrawable` au même titre que les
correctifs des §15 et §17 : tout renderer dont `shadowCastingMode` vaut `ShadowCastingMode.ShadowsOnly` est
repassé en `On`, et **chaque** correction est journalisée avec le chemin complet de l'objet — pas de silence,
pour qu'un futur renderer concerné se voie immédiatement dans le journal.

### Preuves mesurées (run du 2026-10-15 12:41, DLL finales)

```
[avatar] renderer 'Coop.RemoteAvatar/player_view/male_geo/diveSuit/diveSuit_head_geo'
         était en ShadowsOnly (projette une ombre, jamais dessiné) -> rendu réactivé.
[sonde] chaque maillage seul : 3/25 se dessinent
          962 px   28x 48  diveSuit_head_geo, cuit : taille=(0.166, 0.329, 0.275), indices=39399
[sonde] rendu isolé : 11765/262144 pixels non-fond (4.5 %), étendue du sujet=100x281 px
[sonde] décision : signal médian=13231 px, bruit médian=293 px -> la copie rend=True
```

| Mesure | Avant §18 | Après §18 |
|---|---|---|
| Renderers en `ShadowsOnly` sur la copie | 1 (la tête, non signalé) | **0** |
| `diveSuit_head_geo` dessiné seul | 0 px | **962 px (28 x 48)** |
| Étendue du sujet (rendu isolé) | 100 x 242 px | **100 x 281 px** (+39 px = la tête) |
| Verdict de rendu | True (9 812 / 383) | True (**13 231 / 293** px) |

L'étendue gagnée, +39 px pour une tête de 0,329 m vue à cette distance, est cohérente avec les 293 px
qu'annonçait le calcul du §16 pour un corps complet (1,92 m). Le run ne contient aucune exception et
l'instance a été arrêtée ensuite ; les deux captures sont écrites
(`coop-avatar-isolated.png`, `coop-avatar-proof.png`).

### Ce que la source Nitrox apprend pour la suite (au-delà de la tête)

- **Les autres tenues ne sont pas cachées, elles sont inactives.** La ligne relevée au §16 — 21 maillages sur
  25 `actifHierarchie=False` — correspond aux tenues que le joueur ne porte pas : Nitrox pilote la
  visibilité **par `GameObject.SetActive`** selon l'équipement
  (`PlayerEquipmentConstants` : `male_geo/diveSuit/diveSuit_body_geo`, `.../stillSuit/...`, casques
  `radiationSuit_head_geo`, `scuba_head`, palmes, gants, réservoirs). La copie hérite donc de la tenue du
  joueur local, ce qui suffit pour un premier jalon mais **est faux dès que les joueurs portent des tenues
  différentes** : il faudra appliquer ces mêmes règles à partir de l'équipement reçu par le réseau.
- **Le casque est un objet à part**, pas un enfant de la tête : Nitrox a un gestionnaire dédié
  (`PlayerHeadEquipmentHandler` / `PlayerRadiationHelmetVisibilityHandler`).
- **Nitrox retire les objets tenus** du modèle distant (le chemin `player_view/<attach1>`), ce qui recoupe
  la question du PDA restée ouverte au §16.

### Limites de ce qui vient d'être fait

- La tête se **dessine** ; elle ne **suit** pas encore le regard : le pilotage des os de mâchoire et d'yeux
  (les 5 os relevés) n'est pas câblé, et c'est cosmétique.
- La tenue reste héritée du joueur local (cf. ci-dessus) : correct pour un jalon, insuffisant pour du jeu
  réel avec des équipements différents.
- Le test de rendu ne juge que la présence de pixels : il ne dit pas que la tête est **bien orientée** ni
  qu'elle est à l'échelle exacte. C'est la mesure du §16 (0,33 m, +0,09 m au-dessus de l'origine) qui
  l'établit, et elle venait du maillage, pas de l'image.

---

## 19. M2.1 — l'avatar porte la tenue de **son** joueur (validé le 2026-10-15)

**Verdict : la copie ne se contente plus d'hériter de la tenue du joueur local. Les six groupes
d'équipement du jeu sont pilotés par les règles de Nitrox, transcrites et mesurées : cinq scénarios,
cinq verdicts vrais, deux exécutions concordantes.**

### Le défaut, qui n'était pas visible en jouant seul

Le corps du joueur contient **toutes** les tenues à la fois, et c'est le jeu qui n'en laisse voir qu'une.
La copie héritait donc de l'état du joueur local : tant qu'on joue seul avec la même tenue, personne ne le
voit ; dès que deux joueurs diffèrent, l'avatar est faux. Mesuré au moment de la copie, dans les deux
exécutions : **7 nœuds étaient visibles à tort** et la remise à zéro les a masqués. C'est exactement la
superposition que le §15 décrivait comme « à faire pour M2.1 », mais chiffrée : le défaut était réel, pas
théorique.

### La source, et ce qui a été transcrit

`SubnauticaNitrox/Nitrox`, commit `7815b4e` (v1.9.0.0) — six gestionnaires dans
`NitroxClient/GameLogic/PlayerLogic/PlayerModel/Equipment/`, plus les chemins de nœuds de
`PlayerEquipmentConstants.cs` :

| Gestionnaire | Ce qu'il décide | Dépendances croisées, telles quelles |
|---|---|---|
| `DiveSuitVisibilityHandler` | corps, tête et mains de base | s'effacent devant anti-radiations / survie / renforcée / casque / détendeur |
| `FinsVisibilityHandler` | trois paires de palmes, chacune avec sa racine | — |
| `RadiationSuitVisibilityHandler` | les 8 pièces de l'anti-radiations | gilet si corps **ou** casque ; bouteille si gilet ; tuyaux si bouteille **et** casque |
| `ReinforcedSuitVisibilityHandler` | corps et gants renforcés | — |
| `ScubaSuitVisibilityHandler` | détendeur, gilet-bouteille, tuyaux | gilet si bouteille **et pas** anti-radiations ; racine si détendeur **ou** bouteille |
| `StillSuitVisibilityHandler` | combinaison de survie | — |

Ces règles sont une **fonction pure** de l'équipement vers l'état des nœuds, ce qui a permis de les
mesurer sans réseau : la sonde appelle exactement la méthode qu'un paquet d'équipement déclenchera.

### Deux écarts assumés par rapport à Nitrox

- **Pas d'objets gestionnaires partagés.** Nitrox construit une liste par joueur distant, et le commentaire
de `PlayerModelManager` mentionne un bug causé par le partage de cette liste entre joueurs. Ici, une
fonction pure : rien à partager, rien à réinitialiser, appelable autant de fois qu'on veut.
- **Un nœud absent n'est jamais fatal.** Nitrox appelle `Transform.Find` sans garde (un nœud renommé par le
jeu lèverait une `NullReferenceException`). Ici les nœuds introuvables sont **comptés et nommés** dans le
journal, et un état de nœud a trois valeurs — `actif`, `masqué`, `INTROUVABLE` — parce que confondre
« masqué » et « introuvable » ferait passer un chemin de travers pour une tenue correcte.

### Preuves mesurées (deux exécutions : 13:03 et 13:07, DLL déployées)

| Scénario | Nœuds actifs | Pixels (run 1 / run 2) | Verdict |
|---|---|---|---|
| sans équipement | 3 : tête, corps, mains du scaphandre | 11 190 / **11 188** | True |
| anti-radiations + casque + gants + bouteille | 8 : dont casque, bouteille, tuyaux, col | 12 973 / **12 972** | True |
| combinaison de survie | 3 : tête, mains, corps de survie | 11 377 / **11 378** | True |
| combinaison renforcée + gants | 3 : tête, corps et gants renforcés | 11 377 / **11 377** | True |
| palmes + détendeur + bouteille | 8 : dont palmes et détendeur | 14 681 / **14 679** | True |

Chaque verdict vérifie deux choses : le nœud attendu **visible** l'est, et celui qui doit disparaître l'est
aussi (`diveSuit_body_geo` sous l'anti-radiations, `diveSuit_hands_geo` sous la renforcée,
`diveSuit_head_geo` sous le détendeur). Les écarts entre les deux exécutions sont de 1 à 2 pixels, donc la
mesure est stable. L'étendue passe de 124x298 à **188x322 px** avec les palmes : la silhouette change, ce
qui est bien ce qu'on demande à une tenue. Cinq images de tenue sont écrites dans `BepInEx/logs/`
(`coop-avatar-tenue-*.png`), en plus des deux captures du §18.

Aucune exception, et le correctif de tête du §18 tient toujours : `1 renderer(s) sortis de ShadowsOnly`,
étendue 100x281 px, rendu isolé 11 769 px (4,5 %), verdict de rendu True (12 839 px de signal contre
669 px de bruit).

### L'objet tenu : une règle corrigée par la mesure

Le jeu accroche l'outil du joueur sur l'os `attach1`, donc une copie du corps l'emporte — le joueur
distant brandirait l'outil de quelqu'un d'autre. Le premier jet masquait **tous** les enfants de `attach1`
et annonçait « 2 objet(s) masqué(s) » : la mesure a montré que ces deux enfants sont `attach1_forward` et
`attach1_up`, des nœuds d'orientation du jeu qui ne dessinent rien. La règle retenue est donc « un objet
tenu est **dessinable** » (au moins un `Renderer`), et le journal dit maintenant la vérité :
`0 objet(s) tenu(s) masqué(s) (rien de dessinable sur attach1), 2 nœud(s) structurel(s) laissé(s) tel quel`.

### L'aller-retour, et la question qu'il a ouverte (13:13)

Le seul test qui vérifie les deux moitiés ensemble : **lire** l'équipement du joueur local, le passer en
noms, l'appliquer à la copie, puis comparer la copie à l'original.

```
[avatar] équipement local : rien de porté.
[sonde] aller-retour : 0 pièce(s) lue(s), 10 nœud(s) actif(s) chez le joueur local contre 3 sur la copie
[sonde] aller-retour : 7 nœud(s) manquant(s) [flippers_basic_geo, flippers_basic_geo, flippers_basic_geo,
        scuba_head, scuba_vest, scuba_breatherTubes, radiationSuit_tubes_geo 1], 0 en trop -> verdict=False
```

**La lecture est juste** (rien n'est porté, et c'est bien ce qu'elle renvoie). Ce que ce relevé apprend
est plus intéressant : le modèle du joueur local garde actifs les **porteurs** des palmes, du détendeur, de
la bouteille et des tuyaux anti-radiations **alors qu'il ne porte rien**. L'état d'activation des nœuds du
modèle n'est donc **pas** une fonction pure de l'équipement : le jeu en pilote une partie par d'autres
moyens (`ConditionRules`, `ArmsController` — les composants qu'on retire de la copie).

Deux conséquences, et une question à trancher plus tard :

- **Le test n'est pas une preuve d'exactitude**, et il ne doit pas être lu comme un échec des règles : les
règles reproduisent le résultat de Nitrox, et les cinq scénarios du dessus restent valides. Comparer à
l'état brut du modèle local était une mauvaise référence ; le journal le dit maintenant au lieu de le
cacher.
- **La question ouverte** : un joueur distant qui ne porte pas de palmes doit-il en montrer ? Réponse
attendue : non — mais on ne peut pas la tirer du modèle local, qui les laisse actifs. Une référence
fiable serait le modèle **vu de l'extérieur**, et cette machine n'en a pas : il faudra la fabriquer avec un
second joueur, ou en lisant comment le jeu construit le modèle tiers.- Détail cohérent avec le §16 : ces mêmes nœuds étaient `actifHierarchie=False` lors du run de 11:37 et actifs à 13:13, sans que rien de notre code ne les touche. L'état du modèle local **change en cours de partie** — raison de plus pour ne pas s'en servir comme référence.

**Suivi (2026-10-17, plus tard) : le critère est remplacé.** Le verdict de l'aller-retour ne compare plus la
copie à l'état brut du modèle local — ce que ce relevé-ci a montré être une mauvaise référence — mais
`ApplyNames(noms)` à `Apply(types)` sur l'ensemble des pièces que ces règles connaissent, avec un contrôle de
discrimination qui prouve que la comparaison **peut** échouer. Le relevé ci-dessus reste journalisé comme
**observation**. Détail chiffré : « Les contrôles qui n'avaient jamais bronché » plus bas.

### Ce que le réseau devra faire (et qui reste à faire)

- **Le sens « reçu »** est prêt : `RemoteAvatarFactory.ApplyEquipment(clone, noms)` prend des **noms** de
`TechType` et renvoie ceux qu'il n'a pas reconnus. Rien n'est avalé en silence.
- ~~**Le sens « émis »** ne l'est pas.~~ **Fait** : `RemoteAvatarEquipment.ReadLocalEquipment()` lit
`Inventory.main.equipment` et rend des noms de `TechType`. L'API a été **relevée** avec `tools/ApiDump`
(`Inventory.main` champ statique, `Inventory.equipment` propriété, `Equipment.GetEquipment()` — un
énumérateur de `Dictionary` qui se parcourt par `MoveNext()`/`Current`, ce que le compilateur a confirmé —
et `Pickupable.GetTechType()`), pas devinée. Il reste à le mettre dans un paquet.
- Un paquet d'équipement ne transporte aujourd'hui **rien** : la sonde alimente les règles directement.

### Limites, dites franchement

- **Le cas « le joueur tient un outil » n'a pas été exercé** : pendant les deux exécutions, le joueur
local ne tenait rien. La règle est écrite et mesurée sur son cas vide, pas sur un objet réel.
  **Fermé le 2026-10-17** (sonde de présence) : la sonde se donne une lampe par la commande console du
  jeu, la met dans l'emplacement rapide 0 et la prend en main par `QuickSlots.Bind` + `Select` — le code
  de la touche « 1 » — puis attend que la boucle réseau l'émette d'elle-même : `[sonde] présence : outil
  réellement en main — 'Flashlight' … (outil lu : Flashlight, 2 essai(s)) -> verdict=True` et `objet tenu
  émis par la boucle réseau — annonces 1 -> 2 en 15s … -> verdict=True`, sur les deux clients. Deux
  pièges mesurés au passage : la commande attend le **nom de classe** (`AsString(false)`, pas `Flashlight`),
  et une pièce tout juste créée peut ne pas être encore « posée » dans l'emplacement — d'où le réessai.
- **Six groupes de tenues, pas la totalité du modèle** : le PDA et les maillages hors tenues ne sont pas
pilotés ici (le PDA est inactif dans la copie, mesuré au §16).
- **Pas de couleur par joueur** : Nitrox recolore les tenues (`ColorSwap`) pour distinguer les joueurs.
Rien de tel ici, donc deux joueurs en scaphandre de base restent identiques — c'est le prochain défaut
visible, après la tenue.
- Les règles viennent d'une lecture du code de Nitrox, pas d'une documentation officielle : si le jeu
renomme un nœud, le journal le dira (`nœud(s) INTROUVABLE(S)`), et c'est prévu pour être vu.

### Reproduire ces mesures

```
dotnet build src/Coop.Client/Coop.Client.csproj
cp src/Coop.Client/bin/Debug/Coop.Client.dll "<jeu>/BepInEx/plugins/Coop/"
cd "<jeu>" && echo 264710 > steam_appid.txt
./Subnautica.exe -coopmode=client -coopdebug=avatar      # les DEUX drapeaux sont nécessaires
# ~3 min plus tard : BepInEx/logs/coop-client.log
```

Sans `-coopmode=client`, les deux plugins restent inactifs (`mode=Off`) ; sans `-coopdebug=avatar`, la sonde
n'est pas attachée. Deux exécutions ont été perdues aujourd'hui pour avoir omis l'un puis l'autre — la
ligne ci-dessus est là pour que ça n'arrive plus.

---

## 20. M2.2 — présence et positions entre serveur et clients (validé le 2026-10-15)

**Verdict : les paquets de présence (`PlayerJoined`, `PlayerLeft`) et de position (`PlayerPosition` à 20 Hz,
`PlayersSnapshot` diffusé à chaque tick serveur) relient désormais le serveur et les clients de bout en bout.
Chaque client instancie et interpole dans le monde les avatars 3D des autres joueurs et de l'hôte.**

### Protocole v2 (`Coop.Model`)

Quatre nouveaux messages binaires ont été ajoutés au protocole (version passée de `1` à `2`) :

| Opcode | Message | Direction / Canal | Contenu |
|---|---|---|---|
| `2` | `HandshakeResponse` (étendu) | Serveur → Client (`ReliableOrdered`) | Ajout de `AssignedPlayerId` (`ushort`) attribué par le serveur |
| `5` | `PlayerJoined` | Serveur → Client (`ReliableOrdered`) | `PlayerId`, `Username`, position initiale `(X, Y, Z)` et rotation `(RotX, RotY, RotZ, RotW)` |
| `6` | `PlayerLeft` | Serveur → Client (`ReliableOrdered`) | `PlayerId`, `Username`, `Reason` |
| `7` | `PlayerPosition` | Client → Serveur (`Unreliable`, 20 Hz) | `Sequence` (`uint`), position `(X, Y, Z)`, rotation `(RotX, RotY, RotZ, RotW)` |
| `8` | `PlayersSnapshot` | Serveur → Client (`Unreliable`, 20 Hz) | `ServerTick` (`long`), `Count` (`byte`), suivi des `PlayerStateEntry` des autres joueurs |

Garde-fous intégrés à la désérialisation (`PlayerPositionMessage.EnsureFinite`) et à la réception serveur :
- Tout flottant `NaN` ou `Infinity` lève une `ProtocolException` (et éjecte le peer fautif côté serveur).
- Les paquets `PlayerPosition` arrivés dans le désordre (`Sequence <= LastPositionSequence`) sont ignorés.

### Architecture serveur (`Coop.Server`) et client (`Coop.Client`)

- **Côté serveur (`ServerNetwork` + `ServerTickBehaviour`)** :
  - Attribution d'un `PlayerId` unique (`ushort` ≥ 1) à chaque client accepté au handshake.
  - Envoi au nouvel arrivant d'un `PlayerJoined` pour chaque joueur déjà présent (ainsi que l'hôte local
    de l'instance serveur sous l'identifiant réservé `9999` si son `Player` est dans le monde), et diffusion
    d'un `PlayerJoined` aux autres clients.
  - À chaque pas de la boucle fixe à 20 Hz (`ServerTickBehaviour.Update`), appel de `BroadcastSnapshots(_tick)` :
    chaque client accepté reçoit la photographie des positions/rotations de tous les autres joueurs présents.
  - À la déconnexion d'un client, diffusion immédiate d'un `PlayerLeft` en `ReliableOrdered`.

- **Côté client (`ClientNetworkBehaviour` + `RemotePlayerManager`)** :
  - Émission de `PlayerPosition` toutes les 50 ms (20 Hz) dès que `Player.mainObject` existe dans le monde.
  - `RemotePlayerManager` maintient la table des joueurs distants. Tant que le monde local n'est pas encore
    chargé, les joueurs distants reçus sont conservés en mémoire ; dès que le corps local (`Player.mainObject`)
    est prêt, l'avatar distant est instancié via `RemoteAvatarFactory.TryCreate` et basculé sur le calque
    `Default` (`MoveRenderersToWorldLayer`).
  - Interpolation linéaire/sphérique (`Vector3.Lerp` / `Quaternion.Slerp`) sur une fenêtre de 50 ms entre
    deux snapshots consécutifs, avec téléportation directe au-delà de 10 m d'écart (spawn initial).
  - Destruction propre de l'avatar sur réception de `PlayerLeft` ou lors d'une déconnexion du serveur.

### Preuves mesurées (run du 2026-10-15 18:46–18:51, 3 participants simultanés)

1. **Autotest hors-ligne (`NetProbe selftest`)** :
   - Vérifie les round-trips binaires de `HandshakeRequest`, `HandshakeResponse` (avec `AssignedPlayerId`),
     `Ping`/`Pong`, `Disconnect`, `PlayerJoined`, `PlayerLeft`, `PlayerPosition`, `PlayersSnapshot`, ainsi
     que le rejet des paquets tronqués et des flottants `NaN`. Résultat : **SUCCES (`exit=0`)**.

2. **Vérification de bout en bout en conditions réelles** (instance `Subnautica.exe -coopmode=server`,
   instance `Subnautica.exe -coopmode=client`, et un second client `NetProbe probe` en mouvement) :

```
# Serveur (coop-server.log)
[18:49:13.394] [Coop Server] [net] handshake ACCEPTÉ : 'killi' (id=1, jeu=1.22.83031, modHash=da39a3ee5e6b4b0d) — 1/10 client(s)
[18:49:23.169] [Coop Server] [net] première position reçue de 'killi'#1 : (2.08, 3.40, -0.69)
[18:51:04.268] [Coop Server] [net] handshake ACCEPTÉ : 'JoueurSonde' (id=2, jeu=1.22.83031, modHash=da39a3ee5e6b4b0d) — 2/10 client(s)
[18:51:04.296] [Coop Server] [net] première position reçue de 'JoueurSonde'#2 : (-111.96, 2.29, -54.10)
[18:51:12.259] [Coop Server] [net] 'JoueurSonde#2' déconnecté (raison=RemoteConnectionClose, pings=8, positions=151)
[18:51:12.259] [Coop Server] [net] PlayerLeft diffusé pour 'JoueurSonde' (id=2)

# Client Subnautica (coop-client.log)
[18:49:13.547] [Coop Client] [net] handshake ACCEPTÉ (id=1, tick serveur=3601) — début des pings et positions
[18:49:13.548] [Coop Client] [presence] joueur rejoint : 'HoteServeur' (id=9999) pos=(-114.46, 2.29, -54.22) — total distants=1
[18:49:23.154] [Coop Client] [presence] première position locale émise : seq=1 pos=(2.08, 3.40, -0.69)
[18:49:23.161] [Coop Client] [presence] avatar instancié pour 'HoteServeur' (id=9999) à (-114.46, 2.29, -54.22) (24 renderers sur Default)
[18:51:04.279] [Coop Client] [presence] joueur rejoint : 'JoueurSonde' (id=2) pos=(0.00, 0.00, 0.00) — total distants=2
[18:51:04.315] [Coop Client] [presence] avatar instancié pour 'JoueurSonde' (id=2) à (-111.96, 2.29, -54.10) (24 renderers sur Default)
[18:51:08.481] [Coop Client] [ping] id=1 pongs=114 rtt dernier=21ms min=7ms moy=24ms max=102ms dernierTickServeur=5903 posEnvoyees=1880 snapshotsRecus=2302 joueursDistants=2 avatarsActifs=2
[18:51:12.266] [Coop Client] [presence] joueur parti : 'JoueurSonde' (id=2, raison=RemoteConnectionClose) — avatar détruit, total distants=1
```

### Limites actuelles (prochaines étapes de M2)

- **Pose d'animation sur le fil** : seuls la position et la rotation globales sont transmises dans
  `PlayerPosition` / `PlayersSnapshot` ; l'`Animator` de l'avatar joue sa pose par défaut sans recevoir encore
  les vitesses (`move_speed_x/y/z`) ni le regard (`view_pitch`).
- **Décalage d'origine verticale** : `Player.mainObject.transform.position` est au centre de la capsule du
  joueur alors que l'origine de la copie (`male_geo`) est décalée d'environ +0,9 m vers le haut du corps
  (relevé au §16) ; l'alignement fin pieds/sol et l'envoi des paramètres d'animation viendront compléter M2.

---

## 21. M2.3 — synchronisation réseau de l'équipement (`PlayerEquipment`) (validé le 2026-10-15)

**Verdict : les deux moitiés préparées au §19 (`ReadLocalEquipment` et `RemoteAvatarFactory.ApplyEquipment`)
sont désormais reliées par le paquet `PlayerEquipmentMessage` (opcode `10`, `ReliableOrdered`, protocole v3).
Chaque client publie sa tenue au chargement et à chaque changement, le serveur la mémorise par session (et
pour l'hôte `id=9999`), l'envoie aux nouveaux arrivants et la rediffuse en temps réel aux autres clients.**

### Protocole v3 (`Coop.Model`)

| Opcode | Message | Direction / Canal | Contenu et garde-fous |
|---|---|---|---|
| `10` | `PlayerEquipment` | Client ↔ Serveur (`ReliableOrdered`) | `PlayerId` (`ushort`), `Count` (`byte`, max `MaxEquipmentSlots = 32`), suivi des noms de `TechType` (`MaxTechTypeNameLength = 64`) |

- Tout dépassement de `MaxEquipmentSlots` (32) ou de `MaxTechTypeNameLength` (64) lève une `ProtocolException`.
- Côté serveur (`ServerNetwork.HandlePlayerEquipment`), `PlayerId` est **forcé** à `session.PlayerId` avant
  stockage et rediffusion : un client ne peut jamais modifier la tenue d'un autre joueur.

### Flux de bout en bout (`Coop.Server` + `Coop.Client`)

1. **Émission locale (`ClientNetworkBehaviour.SendLocalEquipmentIfChanged`)** :
   - Vérifie toutes les 250 ms (`EquipmentCheckIntervalMs`) l'équipement porté via
     `RemoteAvatarEquipment.ReadLocalEquipment(out failure, logResult: false)`.
   - Trie les noms de `TechType` et calcule une signature canonique : n'envoie un `PlayerEquipmentMessage`
     en `ReliableOrdered` que lors de la première lecture dans le monde ou lorsqu'une pièce est équipée/retirée.
2. **Mémorisation et relais serveur (`ServerNetwork`)** :
   - Conserve `session.Equipment` pour chaque client connecté et suit aussi l'équipement du joueur hôte
     (`id=9999`, `BroadcastHostEquipmentIfChanged`).
   - Lorsqu'un nouveau client réussit son handshake, lui envoie immédiatement le `PlayerEquipment` connu de
     chaque joueur déjà présent (et de l'hôte) juste après son `PlayerJoined`.
   - Lorsqu'un client envoie un `PlayerEquipment`, met à jour `session.Equipment` et le rediffuse à tous les
     autres clients acceptés.
3. **Application sur l'avatar distant (`RemotePlayerManager.OnPlayerEquipment`)** :
   - Si l'avatar distant n'est pas encore instancié (monde local en cours de chargement), mémorise la tenue
     dans `entry.Equipment` et l'applique dès la création de l'avatar dans `UpdateAvatars()`.
   - Si l'avatar existe déjà dans le monde, appelle immédiatement `RemoteAvatarFactory.ApplyEquipment(entry.Avatar, entry.Equipment)`
     et journalise les nœuds activés.

### Preuves mesurées (run du 2026-10-15 20:21–20:24)

1. **Autotest hors-ligne (`NetProbe selftest`)** :
   - Ajout de 4 assertions (`opcode PlayerEquipment`, `round-trip PlayerEquipment (5 pièces)`,
     `round-trip PlayerEquipment (vide)`, `PlayerEquipment (> MaxEquipmentSlots) -> ProtocolException`).
   - Résultat : **29/29 OK (`SELFTEST: SUCCES`, `exit=0`)**.

2. **Exécution de bout en bout (serveur Subnautica + client Subnautica + `NetProbe probe` équipé)** :
   - `JoueurEquipe` (`id=2`) se connecte, reçoit les `PlayerEquipment` initiaux de l'hôte (`id=9999`) et du
     client Subnautica (`id=1`), puis envoie sa tenue de 5 pièces
     (`RadiationSuit, RadiationHelmet, RadiationGloves, HighCapacityTank, Fins`).
   - Le serveur valide, journalise et rediffuse à `killi` (`id=1`), dont l'instance Subnautica applique
     immédiatement la tenue sur l'avatar 3D de `JoueurEquipe` :

```
# Sonde réseau (NetProbe probe)
[probe] PlayerJoined : id=9999 'HoteServeur' pos=(-114.46, 2.29, -54.22)
[probe] PlayerEquipment reçu : id=9999 (0 pièce(s)) -> (sans équipement)
[probe] PlayerJoined : id=1 'killi' pos=(-114.46, 2.29, -54.22)
[probe] PlayerEquipment reçu : id=1 (0 pièce(s)) -> (sans équipement)
[probe] PlayerEquipment envoyé : RadiationSuit, RadiationHelmet, RadiationGloves, HighCapacityTank, Fins

# Serveur (coop-server.log)
[20:23:54.281] [Coop Server] [net] équipement reçu de 'JoueurEquipe'#2 (5 pièce(s)) : RadiationSuit, RadiationHelmet, RadiationGloves, HighCapacityTank, Fins
[20:23:54.281] [Coop Server] [net] PlayerEquipment de 'JoueurEquipe'#2 diffusé à 1 autre(s) client(s).

# Client Subnautica (coop-client.log)
[20:23:10.938] [Coop Client] [presence] équipement local émis (#1, 0 pièce(s)) : (sans équipement)
[20:23:10.947] [Coop Client] [presence] avatar instancié pour 'HoteServeur' (id=9999) à (-114.46, 2.29, -54.22) (24 renderers sur Default, tenue=0 pièce(s), 3 nœud(s) actif(s) [diveSuit_head_geo, diveSuit_body_geo, diveSuit_hands_geo])
[20:23:54.292] [Coop Avatar] [avatar] équipement appliqué : 5 pièce(s) -> 10 nœud(s) montré(s), 3 masqué(s), 11 déjà dans le bon état
[20:23:54.292] [Coop Client] [presence] équipement appliqué sur l'avatar de 'JoueurEquipe' (id=2) : 5 pièce(s) [RadiationSuit, RadiationHelmet, RadiationGloves, HighCapacityTank, Fins] -> 10 nœud(s) actif(s) [generalSuit, flippers_basic_geo, radiationSuit_body_geo, radiationSuit_gloves_geo, radiationSuit_vest_reducedNeck_geo, radiationSuit_head_geo, radiationSuit_helmet_geo 1, radiationSuit_vest_neckExtension_geo, radiationSuit_tank_geo 1, radiationSuit_tubes_geo 1]
```

---

## 22. M2.4 — synchronisation réseau des paramètres d'animation (validé le 2026-10-15)

**Verdict : les paramètres de locomotion et de regard (`move_speed`, `move_speed_x/y/z`, `view_pitch`,
`is_underwater`, `on_surface`) sont lus selon les formules exactes d'`ArmsController` côté émetteur,
transportés dans `PlayerPosition` / `PlayersSnapshot` à 20 Hz (protocole v4), et appliqués avec interpolation
à l'`Animator` de chaque avatar distant. Mesuré par `BakeMesh` sur l'avatar distant en jeu : 0,5076 m de
déplacement moyen des sommets lors de la nage.**

### Relevé IL d'`ArmsController` (`tools/ApiDump il`)

Pour ne deviner aucune formule ni aucun nom de paramètre, les instructions IL de `ArmsController.SetPlayerSpeedParameters`,
`ArmsController.InstallAnimationRules` et `ArmsController.Update` ont été extraites d'`Assembly-CSharp.dll` :

| Paramètre `Animator` | Source exacte dans `ArmsController` |
|---|---|
| `move_speed_x`, `move_speed_y`, `move_speed_z` | `player.armsController.GetSmoothedVelocity()` (`Vector3` en référentiel local lissé) |
| `move_speed` | `smoothedVelocity.magnitude` (recalculé côté récepteur à partir du vecteur interpolé) |
| `view_pitch` | `MainCameraControl.main.GetCameraPitch()` |
| `is_underwater` | `player.IsUnderwater() && player.motorMode != Player.MotorMode.Vehicle` (`motorMode != 3`) |
| `on_surface` | `pos.y > waterLevel - 1f && pos.y < waterLevel + 1f && !player.IsInside() && player.IsUnderwaterForSwimming()` |

### Protocole v4 et interpolation client

- `PlayerPositionMessage` et `PlayerStateEntry` embarquent désormais `MoveSpeedX`, `MoveSpeedY`, `MoveSpeedZ`,
  `ViewPitch` (`float`, vérifiés finis par `EnsureFinite`) ainsi que `IsUnderwater` et `OnSurface` (`bool`).
- Côté serveur (`ServerNetwork`), ces champs sont stockés dans la session du joueur et recopiés dans chaque
  `PlayersSnapshot` à 20 Hz (ainsi que calculés en direct pour l'hôte `id=9999`).
- Côté client (`RemotePlayerManager.UpdateAvatars`), les vitesses locales (`PreviousMoveSpeed` → `TargetMoveSpeed`)
  et l'inclinaison du regard (`PreviousViewPitch` → `TargetViewPitch`) sont interpolées sur la fenêtre de 50 ms,
  puis poussées dans l'`Animator` de la copie via `SafeAnimator.SetFloat` / `SafeAnimator.SetBool`.

### Preuves mesurées (run du 2026-10-15 20:41–20:45)

1. **Autotest hors-ligne (`NetProbe selftest`)** :
   - Vérifie le round-trip de `PlayerPosition` et `PlayersSnapshot` avec tous les paramètres d'animation,
     ainsi que le rejet par `ProtocolException` d'un `ViewPitch = Infinity`.
   - Résultat : **30/30 OK (`SELFTEST: SUCCES`, `exit=0`)**.

2. **Exécution de bout en bout et mesure `BakeMesh` sur l'avatar distant** :
   - `NageurDistant` (`id=2`) envoie à 20 Hz une trajectoire de nage sous-marine (`MoveSpeedZ = 4.20`,
     `IsUnderwater = true`, `ViewPitch` sinusoïdal).
   - Le client Subnautica (`id=1`) instancie l'avatar, applique les paramètres à son `Animator` (`player_view`),
     et mesure 400 ms plus tard par `SkinnedMeshRenderer.BakeMesh` la déformation réelle du maillage par rapport
     à la pose initiale : **0,5076 m de déplacement moyen des sommets** :

```
# Serveur (coop-server.log)
[20:44:35.726] [Coop Server] [net] première position reçue de 'NageurDistant'#2 : (-111.96, 2.29, -54.11) v=(0.1,0.0,4.2) pitch=1.1 sousEau=True

# Client Subnautica (coop-client.log)
[20:43:06.290] [Coop Client] [presence] première position locale émise : seq=1 pos=(2.08, 3.40, -0.69) v=(0.0,0.0,0.0) pitch=0.0 sousEau=False surface=False
[20:44:35.759] [Coop Client] [presence] avatar instancié pour 'NageurDistant' (id=2) à (-111.96, 2.29, -54.11) (24 renderers sur Default, animator=player_view, tenue=5 pièce(s), 10 nœud(s) actif(s) [...])
[20:44:35.759] [Coop Client] [presence] animation active sur 'NageurDistant' (id=2) : move_speed=4.20 v=(0.07,0.00,4.20) view_pitch=1.1 is_underwater=True on_surface=False
[20:44:36.161] [Coop Client] [presence] déformation squelettique mesurée sur 'NageurDistant' (id=2) : déplacement moyen des sommets=0.5076 m (maillage='still_suit_01_body_geo')
```

---

## 23. M2.5 — alignement vertical (`Player.mainObject` vs `Player/body`) mesuré par `BakeMesh` (validé le 2026-10-15)

**Verdict : l'hypothèse d'un décalage d'origine entre `Player.mainObject` et `Player/body` a été testée et
tranchée par la mesure en jeu (`TryMeasureWorldVerticalSpan`). Sur le joueur local, `Player/body` a
`localPosition = (0, 0, 0)` et `localEuler = (0, 0, 0)` sous `Player.mainObject`, et les pieds cuits par
`BakeMesh` sont tous deux à `-1,651 m` sous `root.position.y`, avec un écart mesuré de `0,0000 m`.**

### Pourquoi un doute existait, et ce que la mesure montre

- Dans `AvatarProbeBehaviour.IsolatedRender` (§15), la caméra isolée visait `clone.transform.position - Vector3.up * 0.9f`
  parce que **l'origine de `Player/body` est en haut du corps** (les sommets cuits vont de `minY = -1,651 m`
  pour les pieds à `maxY = +0,219 m` pour le sommet du crâne, soit une taille de `1,87 m` et un centre
  géométrique du torse vers `-0,8 m` / `-0,9 m`).
- Un commentaire au §20 supposait à tort que `Player/body` avait son origine *aux pieds* tandis que
  `Player.mainObject` était au centre de la capsule. En réalité, c'est l'inverse : **`Player.mainObject` et
  `Player/body` partagent exactement la même origine en haut du corps** (`localPosition = (0.000, 0.000, 0.000)`),
  et les sommets des jambes descendent jusqu'à `-1,651 m` dans les deux cas.
- Tenter d'ajouter ou de retrancher `0,9 m` en dur aurait décalé les pieds de l'avatar distant de 90 cm par
  rapport au sol réel du joueur.

### Ce qui a été mis en place (`RemoteAvatar.cs`)

1. **`RemoteAvatarFactory.ComputeBodyWorldPose`** :
   - Lit dynamiquement `candidate.localPosition` et `candidate.localRotation` de `Player/body` sous
     `Player.mainObject` et calcule `bodyWorldPosition = networkRootPosition + networkRootRotation * candidate.localPosition`
     ainsi que `bodyWorldRotation = networkRootRotation * candidate.localRotation`.
   - Dans `RemotePlayerManager`, l'interpolation (`CurrentRootPosition` / `CurrentRootRotation`) s'effectue
     désormais proprement dans le référentiel de `Player.mainObject` avant conversion vers la pose de `Player/body`.
2. **`RemoteAvatarFactory.TryMeasureWorldVerticalSpan`** :
   - Cuit (`BakeMesh`) tous les `SkinnedMeshRenderer` actifs du corps local (`Player/body`) et de l'avatar
     distant, transforme chaque sommet en coordonnées mondiales (`TransformPoint`) et compare la hauteur des
     pieds relative à la racine (`minY - root.y`).

### Preuves mesurées en jeu (run du 2026-10-15 21:04)

```
[21:04:32.384] [Coop Avatar] [avatar] corps candidat : 'Player/body' — 24 SkinnedMeshRenderer,
               localPosition=(0.000, 0.000, 0.000), localEuler=(0.0, 0.0, 0.0), deltaWorld=(0.000, 0.000, 0.000)
[21:04:32.393] [Coop Client] [presence] avatar instancié pour 'HoteServeur' (id=9999)
               root=(-114.46, 2.29, -54.22) -> body=(-114.46, 2.29, -54.22)
               (24 renderers sur Default, animator=player_view, tenue=0 pièce(s),
               3 nœud(s) actif(s) [diveSuit_head_geo, diveSuit_body_geo, diveSuit_hands_geo],
               offsetCorpsLocal=(0.000, 0.000, 0.000),
               piedsRelRoot: local=-1.651m avatar=-1.651m (écart=0.0000m, taille=1.87m))
```

| Grandeur mesurée (`BakeMesh` en coordonnées mondiales) | Joueur local (`Player/body`) | Avatar distant (`Coop.RemoteAvatar`) | Écart |
|---|---|---|---|
| `localPosition` sous `Player.mainObject` | `(0.000, 0.000, 0.000)` | appliqué via `ComputeBodyWorldPose` | `0.000 m` |
| Hauteur des pieds relative à `root.y` (`minY - root.y`) | `-1.651 m` | `-1.651 m` | **`0.0000 m`** |
| Taille verticale totale (`maxY - minY`) | `1.87 m` | `1.87 m` | **`0.00 m`** |

---

## 24. M2.6 — synchronisation de l'objet tenu et du PDA (validé le 2026-10-16)

**Verdict : l'avatar distant tient désormais l'objet réel de son joueur — l'outil en main **et** le PDA
quand il est sorti. Le modèle vient du `PrefabDatabase` du jeu, la pose vient des paramètres `holding_*`
lus sur l'`Animator` de l'émetteur. Mesuré en jeu : un `Welder` monté sous `attach1` (`2 renderer(s)`,
taille `0,368 × 0,409 × 0,445 m`) puis le PDA sorti sur `PlayerPDA`, sur l'avatar d'un client distant.**

### Protocole v5 (`Coop.Model`)

| Opcode | Message | Direction / Canal | Contenu |
|---|---|---|---|
| `11` | `PlayerHeldItem` | Client ↔ Serveur (`ReliableOrdered`) | `PlayerId` (`ushort`), `TechTypeName` (`string`, max `MaxTechTypeNameLength = 64`), `Count` (`byte`, max `MaxActionParams = 8`) suivi des noms de paramètres |

- `PlayerActionFlags` (`[Flags] byte`) : `UsingPda`, `UsingTool`, `UsingToolAlt`, `InSeamoth`, `InExosuit`.
  Transporté dans `PlayerPositionMessage.ActionFlags` et `PlayerStateEntry.ActionFlags` (donc dans chaque
  snapshot à 20 Hz) : l'action est instantanée et suit le geste sans aller-retour supplémentaire.
- Côté serveur, `PlayerId` est **forcé** à `session.PlayerId` ; côté client, `PlayerHeldItem` n'est émis que
  sur changement (cadencé à `HeldItemCheckIntervalMs = 250`).

### Ce qui a été mis en place

1. **Émission (`Coop.Client/Plugin.cs`)** : `SendLocalHeldItemIfChanged` lit l'outil via
   `Inventory.main.GetHeldTool()` + `Pickupable.GetTechType()` et **lit** les booléens `holding_*` actifs sur
   l'`Animator` du joueur local (`AnimatorControllerParameterType.Bool`, préfixe `Protocol.HoldingParamPrefix`)
   — aucune règle recalculée, donc aucune table à tenir à jour. Les drapeaux d'action viennent de
   `Player.GetPDA().isInUse`, `guiHand.GetUsingTool()`, `guiHand.GetAltAttacking()`, `inSeamoth`, `inExosuit`.
2. **Relais (`Coop.Server/ServerNetwork.cs`)** : mémorise l'objet tenu par session, l'envoie aux nouveaux
   arrivants juste après leur `PlayerEquipment`, et le rediffuse aux autres clients à chaque changement.
   Les drapeaux de l'hôte (`id=9999`) sont calculés en direct depuis le `Player` local du serveur.
3. **Réception (`Coop.Client/RemoteAvatar.cs`)** : `SetBool` des paramètres `using_pda`, `using_tool`,
   `using_tool_alt`, `in_seamoth`, `in_exosuit` sur l'`Animator` de l'avatar à chaque frame, et
   `RemoteAvatarHeldItem.SetPdaVisible(...)` suivant le drapeau `UsingPda`.
4. **Modèle (`Coop.Client/RemoteAvatarHeldItem.cs`)** : demande du prefab au jeu
   (`CraftData.GetClassIdForTechType` → `PrefabDatabase.GetPrefabAsync`), instanciation **inactive** (donc
   aucun `Awake` de script de gameplay), retrait des composants non visuels, montage sous `attach1`.

### La cause de la panne, mesurée et non devinée

La première version ne montrait **aucun** modèle et journalisait, 4 ms après la demande :

```
[21:28:46.342] [Warning] [Coop Avatar] [avatar] objet tenu : prefab de 'Welder' pas encore lisible
               (Exception: Attempting to use an invalid operation handle) — nouvel essai jusqu'à l'échéance.
```

Le message est celui d'Addressables (`AsyncOperationHandle<T>.get_InternalOp` le lève quand
`m_InternalOp == null`). L'IL du jeu (`tools/ApiDump il`) donne l'explication complète :

| Méthode (IL) | Ce qu'elle fait |
|---|---|
| `PrefabDatabase.GetPrefabAsync(classId)` | `ScenePrefabDatabase.TryGetScenePrefab` puis `TryGetPrefabFilename` → `GetPrefabForFilenameAsync` → `new LoadingPrefabRequest(filename)` |
| `LoadingPrefabRequest..ctor(string)` | ne pose que `filename` ; `hasRequest` reste **faux**, la poignée Addressables est **par défaut** |
| `LoadingPrefabRequest.TryGetPrefab` | `result = request.Result; return result != null;` — **sans** initialiser la demande |
| `LoadingPrefabRequest.MoveNext()` | `LazyInitializeAsyncRequest(); return !request.IsDone;` |
| `LazyInitializeAsyncRequest()` | `if (hasRequest) return; request = AddressablesUtility.LoadAsync<GameObject>(filename); hasRequest = true;` |
| `CraftData.GetPrefabForTechTypeAsync` | crée la demande puis **`yield return request`** : c'est Unity qui appelle `MoveNext` et initialise le chargement |

Autrement dit : **la demande doit être avancée comme une coroutine**, et interroger `TryGetPrefab` sur une
demande neuve lève toujours (opération interne nulle) — la panne est donc définitive, puisque rien d'autre
n'initialise la demande. La correction (`Tick`) fait le pas du jeu à la main, une fois par frame
(`if (_request is IEnumerator driver) driver.MoveNext();`) avant `TryGetPrefab`. La poignée n'est plus rendue
dès la réception du prefab mais **conservée tant que le modèle est affiché** (Addressables décharge l'asset
quand la dernière poignée est rendue), et libérée au remplacement du modèle ou à la disparition de l'avatar.

### Preuves mesurées en jeu (run du 2026-10-16 21:33–21:36, hôte + client + `NetProbe probe`)

1. **Autotest hors-ligne (`NetProbe selftest`)** : assertions `opcode PlayerHeldItem`, round-trip outil tenu,
   round-trip « rien en main », rejet au-delà de `MaxActionParams`, et vérification des `ActionFlags` dans
   `PlayerPosition` et `PlayersSnapshot`. Résultat : **35/35 OK (`SELFTEST: SUCCES`, `exit=0`)**.
2. **Compilation** : `Coop.Model`, `Coop.Server`, `Coop.Client` et `NetProbe` — `0 erreur`, `0 avertissement`.
3. **Exécution de bout en bout** : la sonde joue la première moitié du run avec un `Welder` en main
   (`holding_welder`) et les drapeaux `UsingTool | UsingToolAlt`, puis la seconde moitié sans outil avec
   `UsingPda` — c'est-à-dire le geste « je range mon outil et je sors mon PDA ».

```
# Sonde réseau (NetProbe probe)
[probe] PlayerHeldItem envoyé : Welder [holding_welder]
[probe] PlayerHeldItem envoyé : (rien en main) + using_pda (phase PDA)
[probe] positions envoyées=874 snapshots reçus=897 joueurs vus=2 équipements reçus=2 objet tenu reçu(s)=2 phase PDA=True
PROBE: SUCCES

# Serveur (coop-server.log)
[21:35:41.240] [Coop Server] [net] objet tenu reçu de 'ToolHolder'#2 : Welder — paramètres : holding_welder
[21:36:03.647] [Coop Server] [net] objet tenu reçu de 'ToolHolder'#2 : (rien en main) — paramètres : (aucun paramètre)

# Client Subnautica (coop-client.log) — avatar de 'ToolHolder' (id=2)
[21:35:41.306] [Coop Avatar] [avatar] objet tenu : 'Welder' -> 1 paramètre(s) `holding_*` actif(s) [holding_welder]
[21:35:41.306] [Coop Avatar] [avatar] objet tenu : prefab '9ef36033-b60c-4f8b-8c3a-b15035de3116' demandé pour 'Welder' (chargement asynchrone).
[21:35:41.370] [Coop Avatar] [avatar] objet tenu : modèle de 'Welder' monté sous `attach1`
               (2 renderer(s), 18 composant(s) de gameplay retiré(s), taille=(0.368, 0.409, 0.445) m, technique='Welder').
[21:36:03.653] [Coop Avatar] [avatar] objet tenu : 1 objet(s) tenu(s) masqué(s) [Welder(Clone) (2 renderer(s))], 2 nœud(s) structurel(s) laissé(s) tel quel [attach1_forward, attach1_up]
[21:36:03.737] [Coop Avatar] [avatar] PDA sorti sur l'avatar ('PlayerPDA').
```

Le PDA n'a rien à instancier : il fait partie du corps copié (`.../hand_L/attachL/PlayerPDA`, `MeshRenderer`
`PDA_geo`, 4121 sommets) — il est activé quand le drapeau `UsingPda` arrive, et rangé sinon.

### Limites actuelles

- **Bruit de journal d'Unity à l'instanciation** : `Can't remove ConditionRules (Script) because ArmsController
  (Script) depends on it`, et un compteur de composants retirés qui surcomptait d'une unité. Les deux ont été
  traités au §26.
- **Paramètres de l'hôte** : à ce stade, `Coop.Server` **dérivait** `holding_` + nom du `TechType` pour
  l'hôte (`id=9999`) au lieu de lire son `Animator`. **Corrigé depuis** : `Coop.Server.csproj` référence
  `UnityEngine.AnimationModule` et `ServerNetwork.ReadHostHoldingParams` lit les booléens `holding_*` sur
  l'`Animator` de l'hôte — exactement comme un client le fait pour lui-même, pour qu'il n'y ait qu'une
  source de vérité au lieu de deux calculs qui divergent en silence.
- **Pose exacte du PDA** : à ce stade, le nœud du PDA était activé/désactivé **sèchement** sur le drapeau
  `UsingPda` reçu à 20 Hz. Le geste exact du jeu (`PDA.Open` / `PDA.Close`) a été relevé puis reproduit au
  §25.

---

## 25. M2.7 — le PDA de l'avatar distant suit le geste exact du joueur (validé le 2026-10-16)

**Verdict : le PDA de l'avatar distant ne s'allume plus et ne s'éteint plus sèchement. Il suit le geste du
jeu, relevé dans l'IL : le modèle apparaît **immédiatement** quand le joueur sort son PDA (c'est le bras qui
monte, pas le modèle qui s'allume en fondu), et il n'est éteint qu'à la **fin** du geste de rangement, 0,3 s
après, pendant que le bras redescend. Mesuré en jeu : `0,497 s` pour le geste de sortie, `0,298 s` pour le
rangement.**

### Relevé IL : ce que le jeu fait exactement (`tools/ApiDump il`)

| Méthode | Ce qu'elle fait |
|---|---|
| `ArmsController.SetUsingPda(bool)` | `SafeAnimator.SetBool(animator, "using_pda", isUsing)` — c'est **tout** le geste du bras, et ce booléen est déjà transporté par le réseau (§24) |
| `ArmsController.spawn_pda()` / `kill_pda()` | **vides** (`ret`) : la piste « ces événements d'animation pilotent le modèle » a été testée puis **écartée par la mesure** |
| `PDA.Open` | `set_isInUse(true)` → `ArmsController.SetUsingPda(true)` → `gameObject.SetActive(true)` → `sequence.Set(0.5f, true, …)` |
| `PDA.Close` | `SetUsingPda(false)` → `ui.OnClosePDA()` → `sequence.Set(0.3f, false, PDA.Deactivated)` |
| `PDA.Deactivated` (appelé **par la fin** de cette séquence) | `ui.OnPDAClosed()`, `gameObject.SetActive(false)`, `set_isInUse(false)` |
| `PDA.ManagedUpdate` | `sequence.Update(deltaTime)` puis `FreezeTime.Set(Id, sequence.t)` : la séquence sert au **gel du temps**, pas à animer le modèle |
| appelants de `PDA.Activated` / `PDA.Deactivated` | `PDA.Open` / `PDA.Close` **uniquement** : le nœud n'est donc allumé ou éteint qu'à ces deux moments |

Autrement dit : le PDA ne s'éteint pas quand le drapeau tombe, mais **0,3 s plus tard**, à la fin du geste.
C'est précisément ce que notre masquage instantané ne faisait pas — et c'est ce que la correction reproduit.

### Ce qui a été mis en place (`Coop.Client/RemoteAvatarHeldItem.cs`)

- `PdaDrawSeconds = 0.5f` et `PdaHolsterSeconds = 0.3f`, constants du jeu relevées ci-dessus (le commentaire
dit d'où elles viennent, elles ne sont pas choisies).
- `SetPdaVisible` : allume le nœud **tout de suite** à la sortie (comme `PDA.Open`) et démarre un geste de
0,5 s ; au rangement, il engage un geste de 0,3 s et **ne touche pas** au nœud.
- `AdvancePdaTransition` (appelé chaque frame depuis `Tick`) : fait avancer le geste, et c'est à la **fin**
qu'il éteint le nœud (`PDA.Deactivated`).
- Un aller-retour en cours de geste repart de la progression acquise — comme `Sequence.t` — pour qu'un joueur
qui ressort son PDA pendant les 0,3 s de rangement ne voie pas son modèle sauter.

### Preuves mesurées en jeu (run du 2026-10-16 21:45–21:47, hôte + client + `NetProbe probe`)

La sonde joue maintenant **trois** phases : outil en main (`Welder`, `UsingTool | UsingToolAlt`), puis PDA
(`using_pda`, rien en main), puis **rangement** (plus aucun drapeau).

1. **Compilation** : `Coop.Client` et `NetProbe` — `0 erreur`, `0 avertissement`.
2. **Autotest** : `NetProbe selftest` — **35/35 OK** (`SELFTEST: SUCCES`, `exit=0`).
3. **Bout en bout** : `PROBE: SUCCES` (`exit=0`), `phase PDA=True phase rangement=True`, et le modèle de
   l'outil se monte toujours (`2 renderer(s)`, `taille=(0.406, 0.411, 0.405) m`) sans « invalid operation
   handle ».

```
# Client Subnautica (coop-client.log) — avatar de 'ToolHolder' (id=2)
[21:46:22.699] [Coop Avatar] [avatar] PDA sorti sur l'avatar ('PlayerPDA') — geste de 0.5 s engagé (le modèle suit la main).
[21:46:23.196] [Coop Avatar] [avatar] PDA : geste de sortie terminé sur l'avatar ('PlayerPDA') après 0.5 s.
[21:46:37.647] [Coop Avatar] [avatar] PDA : rangement engagé sur l'avatar ('PlayerPDA') — le modèle reste en main pendant les 0.3 s du geste, comme `PDA.Close`.
[21:46:37.945] [Coop Avatar] [avatar] PDA rangé sur l'avatar ('PlayerPDA') — le modèle a suivi le bras pendant les 0.3 s du geste.
```

| Geste | Durée du jeu (IL) | Durée mesurée en jeu | Écart |
|---|---|---|---|
| Sortie (`PDA.Open`) | `0.5 s` | `0.497 s` (`22.699` → `23.196`) | `0.003 s` |
| Rangement (`PDA.Close` → `Deactivated`) | `0.3 s` | `0.298 s` (`37.647` → `37.945`) | `0.002 s` |

### Limites actuelles

- **Geste de sortie** : le modèle est visible dès le premier paquet, comme dans le jeu ; les 0,5 s ne bornent
  que la réversibilité du geste, pas l'apparition du modèle.
- **Durée du blend des bras** : elle appartient aux transitions de `player_view_controller`, qu'Unity n'expose
  pas au runtime ; les 0,5 s / 0,3 s sont donc celles de la séquence du jeu, pas celles de l'`Animator`.
- **Bruit de journal d'Unity** à l'instanciation du modèle (`ConditionRules`/`ArmsController`) : supprimé au
  §26.

---

## 26. M2.8 — retrait des composants **dépendants d'abord** : plus d'erreur Unity à l'instanciation (validé le 2026-10-16)

**Verdict : l'erreur native `Can't remove ConditionRules (Script) because ArmsController (Script) depends on
it` a disparu du journal — `0` occurrence sur le run complet, aux deux endroits qui retirent des composants
(la copie du corps et le modèle d'outil instancié) — et les comptes annoncés sont désormais ceux des
composants réellement partis (`16 détectés -> 16 retirés, 0 restant` ; `18/18` pour le `Welder`).**

### Pourquoi l'erreur existait, et pourquoi l'ordre suffit

- Unity refuse de retirer un composant qu'un **autre composant du même objet** déclare exiger via
  `[RequireComponent]`, et il refuse en **écrivant une erreur** dans le journal.
- Le retrait se faisait dans l'ordre du tableau rendu par `GetComponentsInChildren`, c'est-à-dire au hasard :
  quand `ConditionRules` tombait avant `ArmsController` (qui l'exige), Unity l'écrivait et le laissait en
  place.
- La copie du corps s'en sortait **en trichant** : trois passes, donc le composant partait au second tour —
  mais l'erreur, elle, était écrite à chaque création d'avatar. Le modèle d'outil instancié, lui, n'avait
  qu'une passe : `ConditionRules` **restait** sur le modèle tout en étant compté comme retiré.
- Les types exigés sont **lus**, pas devinés : l'IL de `UnityEngine.RequireComponent`
  (`UnityEngine.CoreModule.dll`, relevé à `tools/ApiDump`) montre trois champs **publics**, `m_Type0`,
  `m_Type1` et `m_Type2`.

### Ce qui a été mis en place

1. **`src/Coop.Client/RemoteAvatarComponents.cs`** (nouveau, partagé par les deux passes) :
   - `Strip(root, keepAnimator)` collecte les composants non visuels, les **trie** de façon que chaque
     dépendant soit retiré **avant** ce dont il dépend (visite de l'arête inverse `RequireComponent`, marque
     posée dès l'entrée pour casser un cycle — `A` exige `B` et `B` exige `A` est inséparable par Unity), puis
     retire dans cet ordre ;
   - le bilan est établi **par relecture** des références (`DestroyImmediate` peut échouer sans lever,
     Unity écrivant alors son erreur nativement) : `Removed = Detected - restants`, et les survivants sont
     nommés au journal au lieu d'être comptés comme retirés ;
   - `keepAnimator: true` pour la copie du corps (sans son `Animator`, les os restent figés), `false` pour un
     modèle d'outil.
2. **`RemoteAvatar.StripNonVisualComponents`** : appelle ce stripper et journalise le bilan vérifié.
3. **`RemoteAvatarHeldItem.InstantiateModel`** : même stripper, bilan `retirés/détectés`, et un avertissement
   nommant les composants si Unity en a laissé un.

### Preuves mesurées en jeu (run du 2026-10-16 21:51–21:54, hôte + client + `NetProbe probe` avec un `Welder`)

1. **Compilation** : `Coop.Client` — `0 erreur`, `0 avertissement`.
2. **`grep -c "Can't remove" coop-client.log` = `0`** (deux créations d'avatar et une instanciation de modèle
   comprises) ; les seules lignes `[Error]` restantes du client sont l'init OpenVR de la machine et un
   diagnostic de la sonde (voir les limites).
3. **Comptes vérifiés dans le journal** :

```
[21:52:08.652] [avatar] composants de gameplay : 16 détecté(s) -> 16 retiré(s), 0 restant
[21:52:34.411] [avatar] composants de gameplay : 16 détecté(s) -> 16 retiré(s), 0 restant
[21:53:18.204] [avatar] objet tenu : modèle de 'Welder' monté sous `attach1`
               (2 renderer(s), 18/18 composant(s) de gameplay retiré(s), taille=(0.352, 0.416, 0.461) m, technique='Welder').
```

4. **Aucune régression de rendu**, mesurée par la sonde d'avatar du même run, contre le run du §24 :

| Grandeur | §24 (avant) | §26 (après) |
|---|---|---|
| `avatar instancié` | `24 renderers sur Default, animator=player_view, 3 nœud(s) actif(s)` | identique |
| Pieds vs racine | `local=-1.651m avatar=-1.651m (écart=0.0000m, taille=1.87m)` | identique |
| Rendu isolé de la copie | `11758/262144 px (4.5 %), étendue 101x281` | `11757/262144 px (4.5 %), étendue 101x281` |

### Limites actuelles

- **Diagnostic de la sonde, pas du mod** : au run du §26, l'heuristique de la sonde (différence de pixels
  entre deux frames) a conclu `la copie rend=False` — son bruit médian ce jour-là (`17171 px`) dépassait le
  tiers de son signal (`42917 px`). Le rendu isolé de la même sonde, 17 ms plus tard, montre la copie dessinée
  (`11757 px`, 4.5 %), identique au run du §24 : c'est le seuil de la sonde qui a bronché, pas le rendu.
- **Un script de plus retiré du modèle d'outil** : `ConditionRules` partait déjà de la copie du corps, et
  c'est désormais le cas aussi pour un prefab instancié (il survivait auparavant, sans être nommé). Aucun effet
  visible mesuré.

---

## 27. M2.9 — couleur par joueur : deux joueurs se distinguent dans **une même image** (validé le 2026-10-17)

**Verdict : la couleur de joueur est décidée par le serveur, transportée en fiable, appliquée par le
client sur l'avatar distant — et la mesure montre deux avatars de deux teintes différentes dans la même
image, à `39/255` d'écart pour un seuil de `8`.**

Nitrox invente une couleur par joueur (Subnautica n'en a aucune) ; c'était l'écart assumé du §19. Il est
maintenant couvert de bout en bout.

### Ce qui a été mis en place

- `src/Coop.Model/Protocol.cs` : palette de **8** teintes (`PlayerColorsRgb`, `PlayerColorCount`),
  `PlayerColorAt(index)` qui **boucle** sur la palette au-delà de 8 joueurs.
- `src/Coop.Server/ServerNetwork.cs` : un indice par client, distribué à l'admission
  (`AllocateColorIndex`), l'hôte gardant l'indice `0` ; l'indice part au client en `ReliableOrdered`, avec
  celui de l'hôte puis ceux des autres joueurs.
- `src/Coop.Client/RemoteAvatar.cs` : `OnPlayerColor` mémorise l'indice **même si l'avatar n'existe pas
  encore**, et l'applique à la création (`RemoteAvatarColor.Apply`).
- `src/Coop.Client/RemoteAvatarColor.cs` : applique la teinte à **tous** les renderers de
  `male_geo/diveSuit`, relit les matériaux, et **nomme** les renderers sans propriété de couleur au lieu de
  les compter comme teintés.

### Preuves mesurées en jeu (run du 2026-10-17 01:06, hôte + client, `-coopdebug=avatar`)

| Grandeur | Mesure |
|---|---|
| Application sur le matériau | `3/3 renderer(s) teinté(s) de 'male_geo/diveSuit'`, propriété `MarmosetUBER/(_Color)` |
| Relu dans le matériau | `moyenneRVG=(255,82,82)` (indice 0), `moyenneRVG=(255,152,0)` (indice 1) |
| **Deux avatars, une image** | `corps A (indice 0)=1890 px moyenne=(10, 1, 2), corps B (indice 1)=64 px moyenne=(49, 15, 3), écart=39/255 (seuil 8) -> verdict=True` |
| Image conservée | `BepInEx/logs/coop-avatar-couleurs-deux-corps.png` |

Le rendu isolé de la même sonde reste sombre (luminance `14/255`) : la comparaison porte donc sur la
**teinte à luminance égale** autant que sur l'écart brut, comme au §28 pour les créatures.

### Limites

- **Au-delà de 8 joueurs, deux joueurs peuvent partager une couleur** : l'indice tourne. Ce n'est pas
  mesuré (il faudrait 9 clients) et c'est dit dans le code là où ça arrive.
- **Le corps B n'est mesuré que sur 64 px contre 1 890 px pour A** : sa région de mesure ne capte qu'une
  partie de sa silhouette (cadrage latéral). L'écart de couleur, lui, est mesuré sur les deux, et les deux
  comptes sont non nuls — c'est la condition que la sonde exige pour rendre un verdict.
- **La couleur de son propre personnage n'est pas affichée** : le client ne colore que les avatars
  distants. Un joueur ne voit donc pas sa propre couleur, seulement celle des autres.

---

## 28. M3 — l'identité partagée des créatures est **la position d'apparition** (décidé le 2026-10-17)

**Verdict : on assume la clé par position d'apparition** — `nom du prefab + Creature.leashPosition`
quantifiée à 5 cm — **et on n'ajoute ni graine ni identifiant fournis par le serveur.** La mesure qui le
permet : le serveur diffuse 64 créatures **toutes par position d'apparition** (`0` par repli, `0` sans
clé), le registre local en porte **100 dont 0 clé partagée par deux créatures**, et **66 créatures**
suivent le serveur avec l'IA vérifiée coupée (index relu `260 -> 0`) et un écart de **0,94 m** entre
états consécutifs.

**Conséquence assumée, et corrigée dans la foulée : l'identité est la clé, pas une proximité.** Le refus
d'adoption au-delà de 40 m est tombé (voir « Le refus par distance n'était pas un test d'identité »).

### Pourquoi pas d'identifiant fourni par le serveur

- **Un identifiant serveur ne résout pas la question, il la déplace.** Les deux instances créent leurs
  créatures **chacune de son côté** à partir de la même sauvegarde. Pour que le serveur *nomme* une
  créature, il faut déjà que ce nom désigne la même chose des deux côtés : c'est exactement le problème
  posé.
- **Le jeu n'expose aucun identifiant partageable — et c'est mesuré, pas supposé** :
  - `UniqueIdentifier.Id` **diverge** entre instances pour les créatures créées à l'exécution. Le premier
    run en conditions réelles l'a montré : 34 808 états envoyés, **0 apparié**, et 26 créatures sur 100
    sans identifiant du tout côté client ;
  - `EntitySlot` n'expose **aucun** identifiant (relevé dans l'assembly) ;
  - dériver la clé d'une graine demanderait de réimplémenter le spawner du jeu — invérifiable, et à la
    merci de la moindre mise à jour du jeu.
- **La seule architecture qui supprime le besoin d'une clé partagée** est celle où **le serveur crée les
  créatures chez le client** (commandes d'apparition, plutôt que deux mondes chargés séparément). C'est un
  autre jalon : il déplace la propriété du contenu du monde, ce que le plan place en M6, et il abandonne la
  prémisse de M3. Le décider ici remplacerait une mesure par une hypothèse.

### Ce que l'assomption suppose, et ce qui la casserait

| L'assomption | La mesure qui la falsifierait |
|---|---|
| Les deux instances chargent **la même sauvegarde** | des clés « absentes de ce monde » **en régime établi** (pas pendant le chargement) |
| Le jeu place chaque créature **au même endroit** dans les deux instances | un `clé inconnue` qui ne s'explique pas par le chargement |
| Deux créatures ne peuvent pas porter **la même clé** | `clés partagées max > 0` au registre local — compté à chaque run depuis ce jalon |

### Le refus par distance n'était pas un test d'identité (la mesure qui l'a fait tomber)

Le client refusait d'apparier une entrée quand la copie locale se trouvait à plus de 40 m de la position
annoncée, au motif qu'« une clé mal calculée pourrait désigner la mauvaise créature ». Le run du
2026-10-17 00:48 a montré que ce seuil rejetait des correspondances **justes** :

```
[00:48:21.369] [Info   ] [Coop Client] [créature] état : adoptées=61 (IA arrêtée=61) appariés=1980
               clé inconnue=9323 refusés(dist.)=132 clés locales=100 (sans clé=0) clés partagées max=0
               dérive max=1.16m
[00:48:18.129] [Warning] [Coop Client] [créature] correspondance REFUSÉE : la clé fe42fd2aa752e0ed
               désigne 'Skyray(Clone)' pos=(-65.5, 36.4, -72.6) indexIA=160, à 59.6 m de la position
               annoncée (-121.6, 28.7, -54.1) (seuil 40 m)
```

Les 132 refus sont **trois clés** de `Skyray(Clone)` réessayées ~44 fois chacune : ces trois copies locales
avaient dérivé de 45 à 73 m **parce que leur IA tournait encore**, et le refus leur interdisait précisément
l'adoption qui l'aurait arrêtée. Une créature qui a dérivé ne revient jamais sous les 40 m : le refus était
définitif. Or une clé partagée **désigne la même créature par construction** (même prefab, même position
d'apparition quantifiée) ; la distance entre deux copies ne dit rien de l'identité, elle dit seulement
combien la copie locale s'est éloignée en attendant. Le seuil reste donc comme **mesure** — une adoption
au-delà de 40 m est comptée et nommée, parce qu'une créature ramenée de 70 m saute à l'image — mais il ne
garde plus la porte.

### Preuves mesurées en jeu (hôte + client, 2026-10-17)

| Grandeur | Avant (00:48, refus à 40 m) | Après (00:54, identité = clé) |
|---|---|---|
| Créatures adoptées (IA vérifiée coupée) | `61` sur 64 diffusées | **`66`** |
| Refus par distance | `132` (3 clés, définitifs) | **`0`** |
| Clés inconnues **après** registre complet | `0` — reconstruit : `2 112 = 1 980 appariés + 132 refusés` (compteurs + débit mesuré, 64 créatures à 10 Hz) | `0 refus`, `2 209 appariés` ; le résidu d'inconnues n'est pas séparé (cf. limites) |
| Clés partagées par deux créatures | `0` sur 100 clés locales | `0` sur 100 |
| Créatures sans clé | `0` | `0` |
| Écart entre états consécutifs | `1.16 m` | **`0.94 m`** (`'Peeper(Clone)'`) |
| Adoption la plus lointaine | `35.92 m` | `52.62 m` (la créature est ramenée, plus laissée pour compte) |

1. **Le serveur ne diffuse rien sans clé** : `94 dans le monde, 90 à moins de 120 m, 64 diffusée(s) en
   4 paquet(s) à 'killi'#1 (64 par position d'apparition, 0 par identifiant (repli), 0 sans clé écartée(s))`.
2. **Toutes les espèces suivies** : `Peeper`, `Boomerang`, `GarryFish`, `BladderFish`,
   `BoomerangFishSchool`, `RabbitRay`, `Skyray`, `Crash` — y compris les `Skyray` que l'ancien refus
   laissait définitivement hors du flux.
3. **L'IA est coupée pour de bon** : l'adoption journalise l'index **relu** après l'appel
   (`IA coupée index 260 -> 0 (0 = retirée du planificateur du jeu), lue après l'appel : 0`).
4. **Le `clé inconnue` est un artefact de chargement, et il est recalculable** : le serveur diffuse dès la
   connexion, alors que le registre local n'existe qu'une fois le monde chargé (`0 clé(s) … scène
   'PreStartScreen'` puis `100 clé(s) … scène 'Main'`). Au run du 00:48, les états reçus **après** la
   construction du registre se reconstituent **exactement** : `2 112 = 1 980 appariés + 132 refusés +
   0 inconnu`. Le run du 00:54 présente le même motif (`9 299` inconnues, toutes antérieures au registre
   complet) mais son résidu n'a pas été séparé — c'est la limite ci-dessous.
5. **L'écart après interruption est mesuré séparément** : un relevé intermédiaire annonçait
   `dérive max=12.73m ('RabbitRay(Clone)')`, soit 127 m/s — impossible pour une raie. C'était un écart
   **après une interruption de diffusion** : la copie locale, IA coupée, reste figée pendant que celle du
   serveur continue de nager. Les deux grandeurs sont désormais distinctes (`dérive max` sur états
   consécutifs, `interruption max` + `écart après interruption`), sinon le chiffre ne veut rien dire.

### Verdict de sonde, et le plafond de diffusion chiffré (mêmes runs)

**Un verdict, pas des compteurs à lire** : `-coopdebug=creature` ouvre une fenêtre de 20 s **après** que le
registre local se soit stabilisé et rend une ligne unique —

```
[sonde] créatures : verdict=True — adoptées=65 (IA arrêtée=65, adoptions de loin=4),
        appariés +12928 sur la fenêtre, clé inconnue +0, clés locales=100 (sans clé=0),
        clés partagées=0, écart max=1.69m sur états consécutifs ('Peeper(Clone)', sur un intervalle de 0.10s),
        interruption max=1.3s ('Peeper(Clone)') avec écart 4.56m, 1re distance max=56.28m,
        seuils : dérive≤2m, clé inconnue=0, clé partagée=0
```

Deux chiffres ont dû être corrigés avant que ce `True` veuille dire quelque chose, et les deux l'étaient
par la **cause** :

1. **`clé inconnue +550`, puis zéro** : le registre local se construit **par paliers** (reconstructions
toutes les 2 s) et son premier palier non vide est partiel — relevé `3 clé(s)` puis `100 clé(s)` à 1,1 s
   d'intervalle. Une fenêtre ouverte dès le premier palier comptait 550 clés inconnues en deux secondes,
   puis zéro pendant les dix-huit suivantes : c'était la construction du registre, pas une divergence. La
   sonde attend désormais un compte stable entre deux relevés espacés d'un intervalle de reconstruction.
2. **`écart max=2.08m ('Peeper(Clone)')`, soit 20,8 m/s pour un poisson qui en fait 5** : l'écart était
   mesuré sur un intervalle de `0,30 s` compté comme « consécutif » (le seuil était à 0,35 s, soit 3,5
   intervalles d'envoi). Un écart de trois intervalles n'est pas une mesure de synchronisation. Le seuil
   est passé à **0,15 s**, la durée de l'intervalle est **journalisée** avec le maximum, et les écarts plus
   longs partent dans le compteur d'interruption, où ils sont nommés comme tels.

**Le plafond de diffusion, chiffré et gardé.** Le serveur diffuse au plus
`MaxCreatureChunksPerTick × MaxCreatureStates = 4 × 16 = 64` créatures par client et par tick, triées du
plus proche au plus loin :

```
[net] créatures : 87 dans le monde, 83 à moins de 120 m, 64 diffusée(s) en 4 paquet(s)
      (64 par position d'apparition, 0 par identifiant (repli), 0 sans clé écartée(s)),
      22 sacrifiée(s) au plafond de 64 (la plus proche à 68 m), clés partagées=0 (sur 86 candidates),
      débit=22.9 Ko/s (3200 état(s) en 200 paquet(s) sur 5 s)
```

- **Décision : le plafond reste à 64, et la raison est mesurée** — `22,9 Ko/s` par client, soit ~180 kbps,
  et les 22 créatures sacrifiées sont **le bout du rayon** : la plus proche l'est à **68 m**, très au-delà
  de ce qu'un joueur regarde. Monter le plafond à 96 coûterait `+12 Ko/s` par client pour synchroniser des
  silhouettes à 70 m ; réduire le rayon ne ferait que déplacer la frontière.
- **Ce qui rend le compromis réversible** : si le plafond venait à mordre dans ce que le joueur voit, le
  serveur **le dit** — un avertissement nomme la créature non diffusée dès qu'elle est à moins de `30 m`.
  Un compromis qu'on ne peut pas voir basculer n'est pas un compromis, c'est une panne silencieuse.
- **Le compteur de clés partagées existe désormais aussi côté serveur** : `clés partagées=0` sur 86
  candidates, en plus du `0` sur 100 clés côté client. La limitation du §28 est levée.

### Deux clients, et l'écart attribué (2026-10-17)

**Le critère du jalon est « position identique chez les 2 clients, IA uniquement serveur » : il est mesuré
à deux clients depuis ce run.** Le harnais a appris à en lancer plusieurs (`--clients=2`), chaque client
augmenté écrivant `coop-client-<n>.log` — sans suffixe, le second réécrivait le journal du premier.

| Mesure | Client 1 | Client 2 |
|---|---|---|
| Verdict de sonde | `True` (`adoptées=65`, IA arrêtée `65/65`) | `True` (`adoptées=64`, IA arrêtée `64/64`) |
| Clés inconnues dans la fenêtre | `+0` | `+0` |
| Clés partagées / sans clé | `0` / `0` sur 100 | `0` / `0` sur 100 |
| Écart max entre états consécutifs | `1.25 m` en `0.10 s` | `1.18 m` en `0.10 s` |
| Clés de créatures suivies | 82 | 76 — **toutes communes** aux deux |
| Serveur, par destinataire | `74 candidates, clés partagées=0, débit=23.4 Ko/s` | idem (`23.4 Ko/s`) |

Les deux clients suivent donc **le même ensemble de créatures** (76 clés sur 76), et pour les clés
échantillonnées, la position journalisée à l'adoption est **identique** dans les deux journaux
(`'Peeper(Clone)' pos=(-139.1, 2.1, -61.5)` chez les deux, par exemple) : les deux chargent la même
sauvegarde et reçoivent la même annonce. Coût serveur mesuré : `23,4 Ko/s` par client, soit ~`47 Ko/s`
d'émission pour deux.

**L'écart est désormais attribuable, et ça a servi tout de suite.** Le serveur relève maintenant **son
propre** déplacement entre deux diffusions : `0.44 m` à `0.78 m` selon les runs (4 à 8 m/s). Or le client,
au même moment, dérivait de `2.29 m` — quatre fois plus. L'hypothèse « c'est le serveur qui va vite » était
donc **fausse**, et la cause était locale : l'IA coupée (index relu à `0`, aucune mise en file d'attente —
`écart avant coupure vérifiée=0.00m`), la copie était **quand même déplacée** par la physique du jeu, qui
ré-accélère son `Rigidbody` (flottabilité, traînée, courants) entre deux états. La position n'étant
corrigée qu'à l'état suivant, la créature oscillait de deux mètres. Le frein est maintenant appliqué à
**chaque** état, pas seulement à l'adoption : `2.29 m` → `1.13 m`, et le verdict passe de `False` à `True`.
Le reliquat (`~1,2 m` contre `~0,5 m` côté serveur) est du même ordre que le déplacement du serveur sur un
intervalle : c'est la borne basse de ce qu'on peut attendre avec des annonces à 10 Hz.

**Le premier palier du registre local est corrigé à la cause.** Le suivi reconstruit son registre toutes
les 2 s une fois stabilisé, mais **toutes les 0,25 s** tant que deux relevés consécutifs diffèrent : la
construction passe de `3 clé(s)` puis `100 clé(s)` en 1,1 s (fenêtre trouble pendant laquelle des clés
légitimes étaient refusées et comptées « inconnues ») à `1` → `9` → `30` en 0,3 s, et la fenêtre de sonde
relève `clé inconnue +0`.

### Ce qui a été mis en place dans le code

- `src/Shared/CreatureIdentity.cs` : la clé, son ordre (position d'apparition d'abord, identifiant du jeu
  en repli, `0` = non diffusée), et la raison de cet ordre — mesurée, pas choisie.
- `src/Shared/InstanceLogFile.cs` : `COOP_LOG_SUFFIX` pour séparer les journaux de deux clients lancés
depuis le même dossier de jeu.
- `tools/run-session.sh` : `--clients=N`, chaque client attendu sur son propre marqueur.
- `src/Coop.Client/CreatureProbeBehaviour.cs` (nouveau, `-coopdebug=creature`) : la sonde qui rend le
  verdict ci-dessus, avec ses sept critères (au moins une adoption, IA coupée pour toutes, des états
  appariés dans la fenêtre, aucune clé inconnue ni partagée, aucune créature sans clé, écart ≤ 2 m) ; un
  échec écrit une erreur **par critère manqué**.
- `src/Coop.Server/ServerNetwork.cs` : débit cumulé par client sur 5 s (`22,9 Ko/s` mesurés), nombre de
  créatures sacrifiées au plafond et distance de la plus proche, compteur de clés partagées côté serveur,
  et rapport **par client** (le rapport était auparavant avancé par le premier client de la boucle, si
  bien que le second n'en recevait jamais).
- `src/Coop.Client/AvatarProbeBehaviour.cs` : le rendu isolé **tranche** désormais sur « la copie
  est-elle dessinée » (la mesure par différence d'images corrobore ou reste muette, elle ne peut plus
  conclure « rien ne s'affiche » quand le rendu isolé montre la copie) ; et la preuve à deux corps pour
  le §27.
- `src/Coop.Client/RemoteCreatureManager.cs` :
  - **`clés partagées`** : deux créatures qui tombent sur la même clé sont comptées et nommées au lieu
d'être écartées en silence. C'est la mesure qui rend l'assomption falsifiable à chaque run ;
  - **`DriftAnomalyMeters`** remplace `MatchMaxDistanceMeters` : plus de refus, mais une adoption
    lointaine **comptée et nommée** (`adoptée DE LOIN`), avec la distance et la créature ;
  - **`dérive max` séparée de l'`interruption`** : états consécutifs d'un côté (synchronisation), reprise
    après un trou de flux de l'autre (continuité), chacune avec le nom de la créature qui a produit le
    maximum — sans le nom, un maximum est ininterprétable.

### Limites actuelles

- **Le compteur de clés partagées est désormais des deux côtés** (`0` sur 86 candidates côté serveur, `0`
  sur 100 clés côté client). Reste que ces mesures portent sur **un monde et une zone** : rien n'a été
  mesuré sur une sauvegarde différente ni sur un biome lointain.
- **L'`clé inconnue` n'est plus une inconnue** : les 550 clés du run du 01:04 venaient de la construction
  du registre (le premier palier est partiel), et la sonde attend maintenant qu'il soit stable — la
  fenêtre suivante relève `clé inconnue +0`. **Le premier palier partiel est corrigé** (2026-10-17) : le
  suivi reconstruit toutes les 0,25 s tant que deux relevés consécutifs diffèrent, au lieu d'attendre le
  tick de 2 s — la construction passe de `3 clé(s)` puis `100 clé(s)` en 1,1 s à `1` -> `9` -> `30` en
  0,3 s.
- **L'écart est décomposé depuis le 2026-10-17** : le client mesure le déplacement inter-états **des deux
  côtés** — le serveur relève son propre mouvement entre deux diffusions, le client relève le mouvement local
  de sa copie (`dont mouvement local ≤0.67 m`, le reste étant le déplacement du serveur). Un écart de
  `1,17 m` n'est donc plus attribué au client par défaut. Il reste que le seuil de 2 m est une borne haute :
  il attrape une dérive franche, il ne prouve pas une synchronisation au centimètre.
- **Les interruptions de plus de 2 s** ne sont pas dans la mesure d'écart : la créature est **relâchée**
  (`relâchées=1` au run du 00:54) puis adoptée à nouveau, et la nouvelle adoption repart d'un compteur
  d'états à zéro. La distance de cette re-adoption, elle, est bien mesurée (`1re distance max`).- **La quantisation à 5 cm est un choix de contrat**, pas une mesure de précision du jeu : deux créatures
de même espèce apparues à moins de 5 cm partageraient une clé. Le compteur ci-dessus est là pour le dire
le jour où ça arrive — il n'en a pas vu un seul sur 100 créatures.

### Les contrôles qui n'avaient jamais bronché (2026-10-17)

Six points restaient ouverts après les runs ci-dessus. Quatre ont produit une mesure, et deux ont corrigé le
contrôle lui-même — ce qui est le résultat le plus utile des six.

**1. La garde du plafond n'avait jamais parlé : elle a été provoquée.** Le serveur avertit quand une créature
non diffusée passe à moins de 30 m, mais dans nos zones la plus proche sacrifiée était à 68–72 m :
l'avertissement n'avait donc **jamais** tourné. Un contrôle qu'on n'a jamais vu broncher n'est pas un
contrôle. `COOP_CREATURE_CHUNKS` (surcharge de `CreatureChunksPerTick` par variable d'environnement, pour le
serveur seul) abaisse le plafond le temps d'un run, et le run l'a fait parler :

```
[net] créatures : une créature à 30 m n'est PAS diffusée à 'killi'#1 — le plafond de 16 est atteint
      alors que 93 créatures sont dans le rayon. Le compromis « on ne suit que le proche » ne tient plus :
      il faut changer le plafond ou le rayon.
```

Trois occurrences à 29–30 m, avec `16 diffusée(s) en 1 paquet(s) … 66 sacrifiée(s) au plafond de 16`. Côté
client, le même run rend `verdict=True`, `adoptées=17` (16 diffusées plus le renouvellement du jeu),
`clé inconnue +0`, `écart max=1.09 m` : **la garde dit vrai et le suivi reste correct au plafond**.

**2. Le harnais est devenu un vérificateur, et il a trouvé tout de suite.** `tools/run-session.sh` fait
désormais échouer le run (code de sortie 1) dès qu'un `verdict=False` apparaît dans un journal de client, au
lieu de rendre la main sur « les marqueurs sont atteints ». Premier run après ce changement : il a refusé le
succès. Ce qu'il avait trouvé est le point suivant.

**3. Le critère de l'aller-retour d'équipement était faux — pas les règles.** La sonde comparait la copie à
l'**état brut du modèle local**, et rendait donc `verdict=False` à chaque run : `0 pièce(s) lue(s), 10 nœud(s)
actif(s) chez le joueur local contre 3 sur la copie`, 7 nœuds « manquants » (porteurs de palmes, de bouteille,
de détendeur). Ce n'était pas un défaut des règles : le jeu pilote une partie des nœuds du modèle local hors
équipement. Un verdict toujours faux est pire qu'aucun verdict — il apprend à ignorer les vrais. Le critère est
donc remplacé par trois contrôles, chacun falsifiable :

| Contrôle | Mesure du run |
|---|---|
| Le canal des noms ne perd rien : `ApplyNames(noms)` == `Apply(types)` sur les 14 pièces que les règles connaissent | `19 nœud(s) actif(s) par les types contre 19 par les noms` -> `True` |
| Le contrôle **peut** échouer : les mêmes noms corrompus (`"X" + nom`) doivent donner autre chose | `14 nom(s) corrompu(s) donnent 3 nœud(s) actif(s) contre 19` -> `True` |
| La lecture locale ne perd rien, et un échec de lecture ne passe pas pour un succès | `0 nom(s) pour 0 TechType lu(s)` (rien n'était porté) -> `True` |

L'état brut du modèle local reste journalisé **comme observation**, avec sa raison de ne pas conclure.

**4. Les images de preuve de la sonde d'avatar sont suffixées.** Deux clients lancés depuis le même dossier de
jeu écrivaient leurs preuves aux mêmes noms fixes et s'écrasaient mutuellement. Les chemins passent maintenant
par `COOP_LOG_SUFFIX` — la convention du journal d'instance — donc `coop-avatar-proof.png` et
`coop-avatar-proof-2.png` coexistent après un run à deux clients (vérifié sur disque, ainsi que
`coop-avatar-couleurs-deux-corps[-2].png`). Sans la variable, les noms sont exactement ceux d'avant.

**5. L'écart est décomposé.** Depuis que le serveur relève **son propre** déplacement entre deux diffusions,
le client relève séparément le **mouvement local** de sa copie : `écart max=1.17 m … dont mouvement local
≤0.67 m … le reste étant le déplacement du serveur entre les deux annonces`. Le seuil de 2 m n'est donc plus
la seule lecture possible d'un écart : ses deux composantes sont nommées, et la plus grosse est côté serveur.

**6. La couleur, avec deux vrais joueurs.** La preuve existante était à deux corps *de sonde*. Le run à deux
clients la refait avec deux vrais joueurs, chacun voyant la couleur assignée à l'autre :

```
serveur : [net] couleur assignée à 'killi'#1 : indice 1 = rgb(255,152,0)
          [net] couleur assignée à 'killi'#2 : indice 2 = rgb(255,235,59)
client 1 : avatar instancié pour 'killi' (id=2) … couleur 2 = rgb(255,235,59) -> 3/3 renderer(s) teinté(s)
           (couleurPerso=1)
client 2 : avatar instancié pour 'killi' (id=1) … couleur 1 = rgb(255,152,0) -> 3/3 renderer(s) teinté(s)
           (couleurPerso=2)
```

Les deux indices sont distincts et celui du serveur. La ligne `[presence] couleur appliquée sur l'avatar de
…` **ne sort pas dans ce scénario** : la couleur arrive avant que l'avatar existe (`couleur mémorisée pour …
avatar en attente`), c'est donc la ligne d'instanciation qui porte l'indice et le teintage — l'instrument à
citer est celle-là.

**7. Les deux documents que le plan citait existent.** `docs/protocol.md` (message par message, avec la
source de chaque constante) et `docs/authority.md` (méthode du §5 : ce que le client ne décide plus, où c'est
retiré, ce qui rend une violation visible). Chaque affirmation y nomme le fichier ou la constante dont elle
vient ; ce qui n'est pas implémenté y est écrit comme tel.

### Les sept suites, traitées (2026-10-17)

Sept points restaient ouverts après la session précédente. Les voici, dans l'ordre où ils avaient été
proposés — quatre ont produit une mesure, deux ont rendu un contrôle rejouable, et un a déplacé le travail
suivant (M4) plutôt que de l'écrire.

**1. L'équipement réellement porté est mesuré de bout en bout.** Nouvelle sonde
`-coopdebug=presence` (`src/Coop.Client/PresenceProbeBehaviour.cs`). Elle fait porter de vraies pièces —
par le chemin que le jeu expose lui-même (commande console `item <TechType>`, puis `Equipment.AddItem` dans
l'emplacement que le jeu accepte, demandé par `AllowedToAdd` et non déduit d'une table) — et **relit** avec
la fonction du plugin :

```
[sonde] présence : équipement local posé — 3 pièce(s) [Rebreather, Fins, Tank] relue(s) par ReadLocalEquipment,
                   dont 3/3 de cette passe [Fins, Rebreather, Tank] -> verdict=True
```

Côté destinataire, la même paire de runs a montré la chaîne se construire pièce par pièce sur l'avatar réel :
`équipement appliqué sur l'avatar de 'killi' (id=2) : 1 pièce(s) [Fins] -> 5 nœud(s) actif(s)` puis `2 pièce(s)
[…] -> 6 nœud(s)`, `3 pièce(s) […] -> 8 nœud(s)`, `4 pièce(s) [Fins, Rebreather, ReinforcedGloves, Tank] ->
8 nœud(s)`, et le second client a instancié l'avatar avec `tenue=4 pièce(s), 8 nœud(s) actif(s)`. La question
« est-ce que le jeu voit ce que le mod envoie » n'est donc plus répondue par une liste vide.

**Pourquoi la sonde pose en deux temps**, et c'est une leçon de ce run : l'équipement initial part pendant que
l'autre client charge encore son monde, donc celui-ci l'apprend à l'admission (`équipement mémorisé … avatar en
attente`) et l'applique à la création de l'avatar. La ligne **directe** n'apparaît que pour un changement qui
arrive alors qu'un avatar existe déjà — d'où la pièce ajoutée après un délai de stabilisation. En pratique :
la ligne directe se trouve chez le client **1** (lancé en premier, il a l'avatar du second avant que celui-ci
ne change d'équipement).

**2. La couleur sur un avatar déjà instancié est exercée.** La branche directe (`entry.Avatar != null`) ne
sortait jamais, parce que la couleur du serveur est assignée à l'admission — avant que l'avatar existe. La
sonde attend donc un **avatar distant réel**, puis rejoue un indice **par le gestionnaire lui-même**
(`RemotePlayerManager.ProbeReplayColor` appelle `OnPlayerColor`, il ne le réimplémente pas) :

```
[sonde] présence : couleur rejouée — indice 1 appliqué à l'avatar de 'HoteServeur' (id=9999) par la sonde,
                   teinte avant=(255, 82, 82), après=(255, 152, 0), écart=82/255 (seuil 8) -> verdict=True
[sonde] présence : couleur rejouée — indice 0 restauré (celui du serveur) … écart avec l'état initial=0/255 -> True
```

Ce qui est exercé est la **branche de code**, pas une décision du serveur — l'indice est rejoué par la sonde, et
celui du serveur est remis et vérifié. `docs/preuves.md` le dit dans ces termes.

**3. La batterie de contrôles tient en une commande.** `tools/verify.sh` enchaîne trois runs (avatar +
créatures ; deux clients avec la sonde de présence ; plafond abaissé), **affirme** les lignes attendues et rend
un verdict unique :

```
=== étape A — un client : copie d'avatar dessinée, aller-retour d'équipement, suivi des créatures ===
=== étape B — deux clients : équipement réel et couleur jusqu'à l'avatar de l'autre joueur ===
=== étape C — plafond de diffusion abaissé : la garde de sacrifice parle ===
  étape A : OK (68s) · étape B : OK (113s) · étape C : OK (69s)
VERDICT GLOBAL : tout est vert
```

276 s au total, code de sortie 0. Une étape affirme des **lignes de journal**, pas l'absence d'erreur : c'est
ce qui aurait attrapé le faux verdict d'équipement de la session précédente sans qu'on le lise.

**4. Le harnais n'arrête que ses propres instances.** `launch_instance` retient le PID Windows de chaque
instance lancée (le `$!` de MSYS n'est pas le PID Windows : la traduction se fait par `ps -W`, puisque `wmic`
n'existe plus), un fichier d'état (`BepInEx/logs/.coop-session-pids`) permet au run suivant de nettoyer ce
qu'un run interrompu a laissé, et `kill_instances` ne tue que ces PID-là. Le repli global existe toujours,
mais il faut le demander (`--kill-all`) et il dit ce qu'il fait.

**Ce qui le prouve, et non ce qui le laisse croire** : une **instance témoin** a été lancée avant la batterie
(PID Windows `16056`) et retrouvée **vivante** après les trois étapes, tandis qu'aucun `Subnautica` ne subsiste
après un run (`ps -W` : `0`). Avant ce correctif, chacun de ces runs tuait toute session ouverte à côté.

**5. `docs/preuves.md` : quelle affirmation repose sur quelle mesure.** Une ligne par affirmation (55), avec
l'instrument exact, le fichier d'où viennent la ligne et la valeur, et un statut `prouvé` / `partiel` /
`non prouvé` daté — plus un tableau des 12 lignes non prouvées ou partielles, chacune avec l'expérience qui la
fermerait, prise dans ce que les sources disent déjà.

**6. M4 n'a rien à mesurer dans cette sauvegarde, et c'est maintenant mesuré.** Le serveur inventorie les
véhicules du monde (`Vehicle`), et le client **répartit** le pas de temps du serveur entre deux annonces :

```
[net] véhicules : aucun dans le monde — M4 (Seamoth autoritaire) n'a donc **rien à mesurer** …
[sonde] créatures : pas de temps du serveur entre deux annonces — < 0,25 m : 6422 (erreur locale max 0,31 m)
        · 0,25–0,5 m : 9263 (0,36 m) · 0,5–1 m : 649 (0,08 m) · 1–2 m : 0 · ≥ 2 m : 0
        — plus grand pas = 0,99 m en 0,11 s ('Peeper(Clone)'), soit 9,4 m/s
```

**Conséquence, et c'est la décision que ce point devait éclairer** : ce monde ne contient aucun véhicule, et le
plus grand pas observé vaut `0,99 m` par annonce (`9,4 m/s`). Un Seamoth à `20 m/s` balaierait `2 m` par
annonce — dans une bande **jamais observée** (`1–2 m` et `≥ 2 m` à zéro). Le chemin des créatures applique par
ailleurs les positions **directement, sans interpolation**. Donc la cadence et l'interpolation de M4 ne peuvent
pas être tranchées depuis cette sauvegarde : la première tâche de M4 est de faire exister un véhicule en
mouvement, pas d'écrire son message. C'est écrit là où le code affirmait le contraire (le commentaire
« rien à interpoler » porte maintenant la mesure, et sa limite).

**7. Le clignotement a un chiffre.** Le suivi répartit les interruptions de flux par durée, avec le saut
maximal de chaque bande :

```
[sonde] créatures : interruptions par durée — < 0,5 s : 64 (saut max 0,52 m) · 0,5–1 s : 1 (saut max 2,42 m)
        · 1–2 s : 3 (saut max 8,63 m, 'BoomerangFishSchool(Clone)') · ≥ 2 s : 0
```

Sous `0,5 s`, le plus gros saut mesuré vaut `0,52 m` — la taille d'un poisson, c'est-à-dire la limite de ce qui
se voit ; à partir de `0,5 s`, les sauts se comptent en mètres (`2,42 m`, puis `8,63 m`). Le point où la
créature « clignote » est donc autour de **`0,5 s` d'interruption**. Ce que le chiffre ne dit pas, et qui reste
ouvert : le saut dépend aussi de la vitesse de la créature au moment de la coupure, et aucune mesure ne dit si
`0,5 m` se distingue à l'œil à la distance où se trouve la créature.

**Ni 6 ni 7 ne rendent de verdict**, et c'est délibéré : les deux répartitions disent où placer un seuil, et ces
seuils ne sont pas tranchés. Les faire entrer dans un verdict ferait passer un arbitrage pour une mesure —
l'erreur exacte que la session précédente venait de corriger.




---

## 29. Les claims « lus dans le code » deviennent des mesures : `PlayerId` falsifié, outil tenu, nage, garde d'écriture (2026-10-17)

**Verdict : trois affirmations qui ne tenaient qu'à une lecture de code ont désormais une ligne de journal
qui peut les contredire, et deux d'entre elles ont d'abord contredit la sonde.** La batterie
`tools/verify.sh` rejoue les quatre contrôles en une commande (`A`, `B`, `C`, `D`), et un `verdict=False`
fait échouer le run.

**1. « Le serveur force `PlayerId` » — mesuré de deux côtés.** La sonde de présence envoie un équipement
**marqué** (`[Tank, Fins]`, deux pièces alors qu'elle en porte quatre) sous l'id de l'autre joueur, par le
vrai chemin d'envoi (`ClientNetworkBehaviour.ProbeSendEquipment`, seul le champ est falsifié), puis relit
sous quel id le relais le diffuse. La mesure ne peut pas se faire sur soi — le serveur ne renvoie jamais un
message à son émetteur — donc **chaque client observe la falsification de l'autre** :

```
[sonde] présence : PlayerId falsifié — 'killi' a annoncé [Tank, Fins] sous l'id 1 (le mien) ; le relais me
        l'a diffusé sous l'id 2 (attendu : 2, l'id réel de l'émetteur ; ma propre falsification partie vers
        lui a été restaurée : True) -> verdict=True
[net] vérification PlayerId (équipement) : 'killi' a annoncé l'id 1, effectif=2 -> verdict=True
[net] vérification PlayerId (équipement) : diffusé sous l'id 2 (revendiqué 1) pour 1 client(s) -> verdict=True
```

Ce que ça ne couvre pas : les **autres** champs d'état (position, drapeaux d'action, nom d'objet tenu) ne
sont pas validés par le serveur — il les relaie, c'est écrit dans `docs/authority.md` §2.

**2. L'objet tenu d'un vrai joueur — et deux pièges trouvés en chemin.** La sonde se donne une lampe par
la commande console du jeu, la place dans l'emplacement rapide 0, la prend en main par `QuickSlots.Bind` +
`Select` (le code de la touche « 1 »), puis **attend que la boucle réseau l'émette d'elle-même** — sinon
elle mesurerait son propre envoi :

```
[sonde] présence : outil réellement en main — 'Flashlight' placé dans l'emplacement rapide 0 puis pris en
        main par QuickSlots (outil lu : Flashlight, 2 essai(s)) -> verdict=True
[sonde] présence : objet tenu émis par la boucle réseau — annonces 1 -> 2 en 15s (outil encore en main :
        True, reprises : 0) -> verdict=True
```

Les pièges : la commande console attend le **nom de classe** (`TechType.AsString(false)`, donc
`flashlight`, pas `Flashlight`) ; et une pièce créée à l'instant peut exister dans l'inventaire avant d'être
« posée » dans l'emplacement rapide — un `Select` sur cet état rend `GetHeldTool() == null` (constaté une
fois, 0,5 s après la création), d'où le réessai et la reprise pendant l'attente d'émission.

**3. La nage d'un vrai joueur, vue par l'autre client.** La sonde met le joueur à l'eau par
`Player.SetPosition` — le téléport du **jeu** — et nage en imposant sa vitesse **après les `FixedUpdate`**
du `PlayerMotor` ; l'autre client relit la vitesse cible et la position cible de son avatar :

```
nage puis arrêt vus par 'killi' (id=2) — vitesse max 2.60 m/s (min 1.0), déplacement 2.85 m (min 1.0),
        sous l'eau=True, vitesse après arrêt 0.00 m/s (max 0.5, minimale observée 0.00), avancées vues=10,
        arrêt tenu 1.5s sur 30s de nage puis 10s d'arrêt (déplacement local du nageur : 3.12 m) -> verdict=True
```

Quatre formes ont été essayées **et jetées parce qu'elles mesuraient autre chose** : une durée de nage fixe
(le premier arrivé nageait puis s'arrêtait pendant que le second lisait encore des zéros) ; un saut de 8 m
vers le bas (le joueur atterrit sur le plancher de la capsule, déplacement local `0,00 m`) ; une vitesse
posée depuis `Update` (le `PlayerMotor` l'écrase dans `FixedUpdate`) ; et **la vitesse annoncée du pair comme
critère de mouvement** — elle repasse par zéro entre deux messages alors que l'avatar avance de plusieurs
mètres, si bien qu'une exigence de quinze échantillons « vitesse ≥ 1 » n'était jamais satisfaite. Le
critère retenu est la **position** : dix échantillons où elle avance, puis `1,5 s` sans avance pour
l'arrêt — un téléport ne produit qu'une seule avance, une nage en produit beaucoup. Ce qui reste joué par
la sonde : la **décision** de nager — et la marche n'est pas exercée, la sauvegarde de test n'ayant pas de
terre ferme à proximité. La déformation du maillage pendant cette nage n'est pas remesurée non plus : c'est
le `0,5076 m` du §22, sur une trajectoire jouée.

**4. La garde de sauvegarde a un second rempart, et il est mesuré.** Le scan IL refait
(`tools/ApiDump callers … SaveFile`) désigne un point d'écriture unique des données de sauvegarde :
`SaveLoadManager/GameInfo.SaveFile(string, byte[])`, appelé seulement par `GameInfo.SaveIntoCurrentSlot`,
lui-même atteint seulement par l'itérateur de `SaveToTemporaryStorageAsync`, que seul
`IngameMenu.SaveGameAsync` appelle. La garde patche désormais **les deux** — l'entrée et l'écriture — et
l'autotest appelle l'écriture **directement**, hors du menu :

```
[garde] sauvegarde cliente désactivée — patché : IngameMenu.SaveGameAsync() (entrée) et
        SaveLoadManager+GameInfo.SaveFile(String, Byte[]) (écriture sur disque)
[autotest] entrée bloquée : IngameMenu.SaveGameAsync intercepté (compteur 0 -> 1).
[autotest] écriture bloquée : SaveFile intercepté hors du menu (compteur 0 -> 1).
[autotest] sauvegarde cliente bloquée (entrée+écriture) -> verdict=True
```

`CreateSlotAsync` (« nouvelle partie », qui écrit une partie **neuve**) reste autorisé — sinon le mod
casserait le jeu solo — et `ScreenshotManager` n'écrit que des PNG. Le serveur, lui, n'avait pas encore été
vu en train de sauvegarder pendant un run : c'est la moitié qui était ouverte, fermée au §30 (étape F).

**5. Le harnais sait maintenant lancer deux clients ensemble — et il a révélé un défaut.** La mesure
mutuelle exige que les deux clients tournent au même moment : `tools/run-session.sh --together` lance tous
les clients avant d'attendre leurs marqueurs (avec 6 s d'écart entre deux lancements ; sans cet écart, le
premier processus reste vivant sans écrire une ligne — mesuré). Conséquence immédiate : la sonde de
**créatures** rend `verdict=False` quand deux clients suivent le monde en même temps (`clé inconnue +4218`
côté client 1, `+103` côté client 2), alors qu'elle rend `True` à un seul client (§28). Le défaut est noté
dans `docs/preuves.md` et **n'est pas masqué** : l'étape B n'asserte plus les créatures, qui restent
couvertes par A et C. C'était le prochain point à instruire sur M3 — il est instruit au §30 : le défaut
venait de la sonde de présence, et l'étape E (créatures seules à deux clients) rend `True` des deux côtés.

---

## 30. Le groupe A fermé, et M4 débloqué par un véhicule qui existe (2026-10-17)

**Verdict : les trois points laissés ouverts sont fermés par des mesures rejouables, et M4 n'est plus bloqué
par une question de code mais par une décision que la mesure a tranchée — l'interpolation.** La batterie
passe de quatre à **sept** étapes (`A`…`G`) et rend toujours un seul verdict.

**1. Le suivi des créatures à deux clients : le défaut venait de la sonde, pas du suivi.** L'étape `B`
avait rendu `verdict=False` (`clé inconnue +4218` / `+103`) parce qu'elle faisait tourner **deux** sondes
sur le même joueur : la sonde de présence le téléporte à 25 m pour nager, ce qui déplace le rayon de
pertinence et fait apparaître des clés que le suivi n'a aucune raison de connaître. L'étape **E** lance les
deux clients avec la sonde de créatures **seule** (`--clients=2 --together`) :

```
[sonde] créatures : verdict=True — adoptées=65 (IA arrêtée=65, adoptions de loin=7), appariés +12928 sur la
        fenêtre, clé inconnue +0, clés locales=92 (sans clé=0), clés partagées=0, écart max=1.26m
[sonde] créatures : verdict=True — adoptées=64 (IA arrêtée=64, adoptions de loin=2), appariés +12928 sur la
        fenêtre, clé inconnue +0, clés locales=92 (sans clé=0), clés partagées=0, écart max=1.01m
```

Ce n'est pas un contournement : c'est la même règle qu'au §28 — **ne pas mesurer le suivi pendant qu'une
autre sonde déplace le joueur**. Le registre local, lui, se reconstruit désormais à la demande quand une clé
est inconnue (au plus une fois par seconde) et reste en reconstruction rapide tant qu'il n'est pas stable
(`RegistryUnsettledRefreshSeconds`, `_registrySettled`) : c'est ce qui permet à la fenêtre de 20 s de porter
le verdict au lieu de compter la construction du registre.

**2. L'avatar qui naît déjà habillé est de nouveau mesuré par la batterie.** L'étape `B` affirme de nouveau
`avatar instancié pour … tenue=N pièce(s)` avec `N ≥ 1`, et la ligne est présente chez le client qui arrive
en second (`tenue=2 pièce(s)`) : le premier client s'habille avant d'attendre l'autre, donc l'équipement est
connu à l'instanciation. La ligne n'est plus une lecture de journal d'une session passée ; elle est assertée.

**3. L'hôte sauvegarde vraiment — l'autre moitié de la garde.** L'étape **F** déclenche
`IngameMenu.SaveGame()` côté serveur pendant qu'un client est connecté, et compare le dossier temporaire de
la sauvegarde **avant/après** (nom, taille, date d'écriture) :

```
[autotest] sauvegarde serveur : déclenchement volontaire de IngameMenu.SaveGame()
        (40 fichier(s) dans le dossier temporaire avant, timeScale=1.00)
[autotest] sauvegarde serveur : 31 fichier(s) écrit(s) ou modifié(s) dans le dossier temporaire —
        modifié …/TempSave/tmp679409/gameinfo.json, …
[autotest] sauvegarde serveur pendant un run -> verdict=True
```

« La garde ne bloque que le client » ne repose donc plus sur le fait qu'il ne l'installe pas : l'hôte écrit,
le client est bloqué, et les deux lignes sont dans la même batterie. Ce qui n'est pas observé : le
remplacement du **slot** (le jeu le fait après coup), et une sauvegarde déclenchée par l'interface plutôt
que par l'appel que le bouton utilise.

**4. M4 : le véhicule existe, se déplace, et la mesure a tranché.** La sonde `-coopdebug=vehicle` fabrique
un Seamoth par la commande console du jeu (`spawn Seamoth`), le déplace à `20 m/s` pendant 10 s (Rigidbody
cinématique, transform piloté), échantillonne à la cadence réelle des annonces et **détruit** le véhicule
ensuite :

```
[sonde] véhicule : 'SeaMoth(Clone)' déplacé de 200.1 m en 10.0 s (vitesse mesurée 20.0 m/s),
        pas max à 10 Hz=5.65 m / à 20 Hz=4.51 m, bande ≥2 m à 10 Hz=92 annonce(s) -> verdict=True
[sonde] véhicule : pas entre deux annonces — à 10 Hz (créatures) : <0,25 m : 0 · 0,25–0,5 m : 0 ·
        0,5–1 m : 0 · 1–2 m : 0 · ≥2 m : 92 (92 annonce(s), pas moyen 2.15 m, max 5.65 m,
        vitesse implicite 20.0 m/s) · à 20 Hz (joueurs) : <0,25 m : 0 · … · 1–2 m : 175 · ≥2 m : 2
        (177 annonce(s), pas moyen 1.13 m, max 4.51 m, vitesse implicite 20.0 m/s)
```

**La décision que ces chiffres tranchent** : la cadence seule ne suffit pas. À `10 Hz`, **tous** les pas
tombent dans la bande `≥ 2 m` (moyenne `2,15 m`) ; à `20 Hz` — la cadence des joueurs — 175 pas sur 177
sont encore dans `1–2 m` (moyenne `1,13 m`). Or le chemin actuel des créatures applique les positions
**directement, sans interpolation**, avec un seuil de dérive de `2 m` : un véhicule envoyé tel quel
déclencherait ce seuil à `10 Hz`. Monter à `40 Hz` pour passer sous `0,5 m` doublerait la cadence des
joueurs et la bande passante pour un objet qui n'en a pas besoin.

Donc M4 s'écrit dans cet ordre : **(a)** le message de véhicule, à `10 Hz` comme les créatures, avec la
même identité (clé) et une position autoritaire ; **(b)** une **interpolation côté client** entre deux
annonces (le pas visible devient `20 m/s ÷ 60 fps ≈ 0,33 m` par image, contre `2,15 m` appliqués d'un
coup) ; **(c)** un compteur d'écart calqué sur celui des créatures, seuil à décider **après** mesure, pas
avant. Ce qui reste hors de la mesure actuelle : la sonde mesure ce qu'une annonce produirait, elle n'envoie
**aucun** message — le protocole ne transporte toujours rien du véhicule, et c'est le prochain travail.

**5. Ce que la batterie rejoue maintenant.** `tools/verify.sh` enchaîne `A` (avatar + créatures), `B` (deux
clients, présence : équipement, `PlayerId`, outil, nage), `C` (plafond), `D` (garde cliente), `E`
(créatures seules à deux clients), `F` (sauvegarde de l'hôte), `G` (Seamoth en mouvement), affirme les
lignes de chacune et écrit `VERDICT GLOBAL : tout est vert` avec le code de sortie 0. Aucune étape ne dépend
d'un réglage manuel : chacune passe ses arguments d'hôte et de client au harnais.

---

## 31. Le launcher démarre le serveur, et son bouton Sauvegarder parle au jeu (2026-10-17)

**Verdict : `ServerControlBehaviour` ouvre un canal fichier minimal — le launcher n'a plus besoin d'accéder
au réseau du jeu pour lui demander une sauvegarde.**

Le launcher (projet Avalonia séparé, `C:\Users\killi\Documents\Launcher`) lance deux fois le même
`Subnautica.exe` avec `-coopmode=server` et `-coopmode=client`, affiche `BepInEx/logs/coop-server.log` en
lecture seule, et propose Démarrer / Arrêter / Sauvegarder / Lancer le client. Le seul point qui demandait
une décision côté mod est la sauvegarde : un programme extérieur n'a aucun moyen d'appeler
`IngameMenu.SaveGame()`.

Le canal retenu, et pourquoi :

```
launcher  -> BepInEx/coop/save.request   (un jeton unique par clic)
serveur   -> BepInEx/coop/save.ack       (jeton|ok|message, après l'appel)
```

Un fichier plutôt qu'une commande dans le socket UDP : le protocole n'ouvre rien à l'administration, et un
fichier sous `BepInEx/coop/` ne peut être écrit que par un processus qui a déjà accès au dossier du jeu —
c'est exactement le périmètre du launcher. Le serveur relit toutes les 500 ms (`WaitForSecondsRealtime`,
parce que l'hôte peut être en pause), journalise `[contrôle] sauvegarde demandée par le launcher (jeton …)`
et répond. Un accusé `ok` veut dire « `IngameMenu.SaveGame()` a été appelé », pas « le fichier du slot est
déjà remplacé » : le jeu écrit en arrière-plan, et c'est écrit dans la ligne.

**Mesuré de bout en bout, sans lancer l'interface** (les arguments sont ceux que le launcher construit) :
serveur chargé (`MONDE CHARGÉ`), jeton écrit → `e2e-1792237004|ok|IngameMenu.SaveGame() appelé`, puis
client → `connecté au serveur 127.0.0.1:7777`, `handshake ACCEPTÉ (id=1)`, `joueur rejoint : 'HoteServeur'
(id=9999)`. L'arrêt par fermeture de fenêtre (le chemin du bouton Arrêter) journalise `[net] arrêt du
serveur réseau (fin de vie de l'instance)` et ne laisse aucun processus.

Ce que le launcher ne fait pas encore : déployer les DLL du mod (elles doivent déjà être dans
`BepInEx/plugins/Coop`), choisir l'adresse du serveur (le client lit `BepInEx/config`, donc `127.0.0.1`), ni
afficher les journaux du **client** ou envoyer des commandes dans le terminal.

### Le serveur est devenu dédié : sans fenêtre, sans personnage

Le launcher lance l'hôte avec `-batchmode -nographics` : **aucune fenêtre**, aucun rendu. Côté plugin, c'est
`Application.isBatchMode` qui décide (aucun argument supplémentaire à retenir), et `ServerNetwork` ne construit
plus d'état `id=9999` — ni dans le snapshot périodique, **ni dans l'admission** : c'est ce second chemin qui
faisait apparaître un « HoteServeur » dix secondes après le handshake, alors que la diffusion périodique était
déjà bloquée (mesuré, puis corrigé).

**Ce qui a été essayé et rejeté, avec sa mesure** : désactiver le GameObject du joueur. Le joueur disparaît de
`FindObjectOfType`, mais les systèmes du jeu continuent d'utiliser la référence statique `Player.main` et
lèvent une exception à chaque frame : **318 395 erreurs en 28 s** (`uGUI_Pings.UpdatePings`,
`DisableSkyBox.Update`, `EcoRegionManager.UpdateCameraRegion`, …), journal de **81 Mo**, et le serveur devient
trop lent pour que le contrôle du launcher réponde (la demande de sauvegarde n'a jamais été traitée). Le
joueur reste donc **simulé** — Subnautica en a besoin pour charger le monde, sauvegarder et le faire vivre —
mais il n'est **ni affiché** (pas de fenêtre), **ni piloté** (pas d'entrée), **ni diffusé** (aucun client ne
le voit). C'est ce que dit la ligne de log :

```
[boot] batchmode=True — serveur dédié : aucun personnage joueur ne sera exposé aux clients.
[dédié] serveur sans personnage exposé — joueur local à (-114.5, 2.3, -54.2), fenêtre=aucune (batchmode),
        état diffusé=aucun (id 9999 désactivé), entrées=aucune. Le joueur reste simulé : Subnautica en a
        besoin pour charger, sauvegarder et faire vivre le monde.
```

Mesuré de bout en bout avec un client réel : `handshake ACCEPTÉ`, **`joueursDistants=0 avatarsActifs=0`**,
aucune ligne `avatar instancié pour 'HoteServeur'`, créatures suivies (`verdict=True`), sauvegarde demandée
par le canal de contrôle (`…|ok|IngameMenu.SaveGame() appelé`) et arrêt propre par `quit.request` →
`Application.Quit()` → `[net] arrêt du serveur réseau`, **0 erreur** dans le journal du serveur.

**L'arrêt** utilise le même canal fichier que la sauvegarde : en `-batchmode` il n'y a aucune fenêtre, donc
`CloseMainWindow` ne peut rien faire. `quit.request` laisse Unity dérouler `OnDestroy` (coupure du réseau,
fermeture du journal) au lieu d'un `Kill` ; le bouton Arrêter attend 5 s, puis tue l'arbre en dernier
recours. L'étape **H** de `tools/verify.sh` rejoue le tout (hôte `-batchmode -nographics`, client admis, aucun
avatar d'hôte).

---

## 32. Deux défauts constatés en jeu, chacun fermé par sa mesure (2026-10-17)

**Verdict : les avatars distants regardent désormais où leur joueur regarde, et le menu Échap d'un client
ne fige plus la partie.** Les deux causes ont été mesurées avant d'être corrigées, et l'étape **I** de
`tools/verify.sh` rejoue la seconde.

### 32.1 L'avatar tournait avec la caméra de celui qui le regarde

Symptôme rapporté : « les corps des autres joueurs ne correspondent pas à là où ils regardent, mais à là où
tu regardes — les corps tournent tous en même temps ».

La mesure qui tranche, relevée sur un client dont le monde tourne (`-coopdebug=pause`, ligne
`[sonde] repères de rotation`) :

```
[sonde] repères de rotation : lacetVue=40.9°, lacetRacine=-0.2°, lacetCorpsLocal=41.1° (caméra présente : True)
```

Autrement dit : **la racine du joueur (`Player.mainObject`) ne porte pas le lacet de la vue** (-0,2°), c'est la
caméra qui le porte (40,9°), et `Player/body` le compense dans son repère local (41,1°). Or le client
envoyait `Player.mainObject.transform.rotation`, et le receveur appliquait :

```
bodyWorldRotation = networkRootRotation * candidate.localRotation   // candidate = corps du joueur LOCAL
```

Le second terme était donc le lacet du **receveur** : tous les avatars prenaient sa caméra. Le code envoyait
une orientation quasi fixe, et le seul lacet présent dans le calcul venait de celui qui regarde — d'où
l'impression que « tous les corps tournent en même temps ».

**Correctif** : `src/Shared/ViewDirection.cs` (partagé client/hôte) rend le lacet de la **caméra**, tangage
retiré ; le client et l'avatar de l'hôte (id=9999) l'envoient à la place de la racine ; et
`RemoteAvatarFactory.ComputeBodyWorldPose` applique cette rotation **telle quelle** au lieu de la multiplier
par la rotation locale du corps du receveur. Le décalage de position local (`localPosition`, mesuré à
`0,000`) est conservé.

**Ce qui a été vérifié après coup** : `avatar instancié pour 'HoteServeur'` avec le même alignement vertical
qu'avant (`piedsRelRoot` écart `0.0000 m`, taille `1.87 m`), aucun `verdict=False`. **Ce qui n'a pas été
mesuré** : une capture d'un joueur distant qui tourne la tête à l'écran — la preuve est la chaîne de lacets
ci-dessus, pas une image.

### 32.2 Le menu Échap du client mettait le monde en pause

Le scan IL (`tools/ApiDump il Assembly-CSharp IngameMenu OnSelect`) montre l'appel exact du menu :

```
0013: ldc.i4.5
0014: call System.Void UWE.FreezeTime::Begin(UWE.FreezeTime/Id)
```

`5` est `FreezeTime.Id.IngameMenu`, et `Begin`/`End` délèguent tous les deux à `FreezeTime.Set(Id, value)`
(IL vérifié), qui met `Time.timeScale` à 0. Un client qui ouvre son menu arrêtait donc sa simulation et
l'envoi de ses positions.

**Correctif** : `src/Coop.Client/ClientPauseGuard.cs` patche `UWE.FreezeTime.Set` et **ignore uniquement**
`Id.IngameMenu`. Les autres gels (`WaitScreen`, `PDA`, `TextInput`…) sont conservés : eux correspondent à des
états où le jeu doit s'arrêter. Le patch est installé seulement sur l'instance client.

**Mesure** (sonde `-coopdebug=pause`, qui appelle exactement `FreezeTime.Begin(Id.IngameMenu)`) :

```
[sonde] pause : verdict=True — Time.timeScale avant=1.00, pendant le gel demandé=1.00, après=1.00 ;
        gels refusés 0 -> 2 (appel réel du menu Échap : UWE.FreezeTime.Begin(Id.IngameMenu))
```

Une première version de la sonde avait rendu `verdict=False` avec `0,00` partout : elle mesurait 2 s après
l'activation de la scène `Main`, alors que `WaitScreen` gèle encore le temps pendant le chargement du terrain.
La sonde attend maintenant que `Time.timeScale` soit repassé au-dessus de zéro avant de mesurer — c'est un
défaut de la sonde, pas du garde, et il est corrigé.

**Étape I** de `tools/verify.sh` (hôte `-batchmode -nographics`, client `-coopdebug=pause`) : OK, exit 0,
`VERDICT GLOBAL : tout est vert`.

---

## 33. M4 (a) et (b) : le véhicule est diffusé à 10 Hz, et c'est le client qui l'interpole (2026-10-17)

**Verdict : un Seamoth déplacé par l'hôte est reçu par un client, suivi par sa clé, et interpolé — mesuré
image par image. Le pas visible tombe de `2,25 m` par annonce à **`0,39 m` au maximum par image sur la
fenêtre de mesure, `0,48 m` sur la session entière** (moyenne `0,14 m`) à ~143 fps, et **aucune** image ne
dépasse `1 m` — y compris sur une session où une annonce a sauté de `7,26 m` (à-coup du serveur). Ce que la
mesure du §30 avait annoncé comme nécessaire (le message, puis l'interpolation) est fait ; ce que M4 laisse
ouvert est nommé à la fin.

### Ce qui a été ajouté

```
src/Coop.Model/Protocol.cs              protocole v7 : opcode VehicleSnapshot = 14, 10 Hz, 150 m de relevancy
src/Coop.Model/Messages.cs              VehicleStateEntry (clé 64 bits + position + quaternion), VehicleSnapshotMessage
src/Shared/VehicleIdentity.cs           clé partagée, compilée dans les deux plugins
src/Coop.Server/ServerNetwork.cs        BroadcastVehicleSnapshots : relevancy par destinataire, 10 Hz, refus sans clé
src/Coop.Client/RemoteVehicleManager.cs registre des clés locales, appariement, gel de la physique, interpolation
src/Coop.Client/Plugin.cs               réception du flux, appel par image, compteurs, relâchement à la déconnexion
src/Coop.Client/VehicleClientProbeBehaviour.cs  sonde -coopdebug=vehicle (côté client) : pas par image
tools/NetProbe/Program.cs               autotest du message (aller-retour, vide, plafond, NaN/Inf, taille)
tools/verify.sh                         étape J : les deux sondes, et l'interpolation affirmée
```

### Les trois décisions, et ce qui les soutient

1. **Protocole v7, opcode à part.** Le flux de véhicules est un message propre, pas un champ ajouté aux
   créatures : un client v6 ne connaît pas l'opcode 14 et laisserait les véhicules du serveur simulés chez
   lui. Le handshake refuse donc les anciennes versions (c'est le comportement prévu depuis M1) plutôt que
   de laisser une divergence silencieuse. Entrée de 36 octets, plafond de 16 par paquet : un paquet plein
   fait **586 octets**, sous la limite non fiable de 1023 — mesuré par le selftest, comme pour les créatures.
2. **La clé d'un véhicule est son `UniqueIdentifier`** (`VehicleIdentity`), et non sa position d'apparition
   comme pour les créatures. La différence n'est pas un goût : un véhicule d'une sauvegarde est un objet
   sérialisé, son identifiant est écrit dans le fichier et les deux instances qui chargent la même partie
   lisent le même. Le repli par position (à la première observation, quantifiée à 5 cm) n'existe que pour
   un véhicule fabriqué à l'exécution. Le serveur dit lequel des deux a servi, par véhicule.
3. **L'interpolation dure l'intervalle réellement mesuré entre deux annonces**, borné à `0,04–0,50 s` : le
   client arrive sur la pose annoncée au moment où la suivante arrive, sans tampon de latence ajouté. Une
   annonce manquante n'allonge donc pas le glissement au-delà de 500 ms. La durée ne peut pas non plus être
   plus courte que `distance / 40 m/s` : des annonces livrées **en rafale** (une frame bloquée par un
   chargement, et les paquets arrivent ensemble) ont un intervalle mesuré presque nul, et s'y fier faisait
   repasser le saut entier en une image. La physique du véhicule suivi est gelée (`Rigidbody.isKinematic`,
   état d'origine mémorisé) : sans ça, la flottabilité et la traînée du jeu le déplacent entre deux annonces.
   Un véhicule **conduit par ce client** n'est ni déplacé ni gelé — M4 ne transporte pas la conduite, et
   figer le véhicule sous le joueur serait pire que l'absence de synchro.

### La mesure (étape J de `tools/verify.sh`, 2026-10-17 21:37)

Hôte `-coopmode=server -coopdebug=vehicle` avec `COOP_VEHICLE_SECONDS=120`, client
`-coopmode=client -coopdebug=vehicle` :

```
[21:56:36] [sonde] véhicule : verdict=True — 'SeaMoth(Clone)' déplacé de 1.7 m (parcouru 2400.4 m)
           en 120.0 s (vitesse mesurée 20.0 m/s), pas max à 10 Hz=7.26 m / à 20 Hz=6.00 m,
           bande ≥2 m à 10 Hz=1128 annonce(s)

[21:59:00] [sonde] véhicule client : verdict=True — 'SeaMoth(Clone)' suivi sur 1714 image(s) en 12.01s
           (fps≈143), déplacement=237.77 m, pas par image max=0.39 m / moyen=0.14 m, images ≥1 m=0,
           pertes de suivi=0 — seuils : images≥120, déplacement≥5 m, pas max≤1.0 m, pas moyen≤0.6 m.
           État du suivi : … pas par image max=0.48m moy=0.14m sur 10259 image(s) en mouvement
           (fps≈143) dont ≥1m=0, vitesse implicite≈20.4 m/s

[net] véhicules : 1 dans le monde, 1 à moins de 150 m du point (-114.5, 2.3, -54.2)
      [repli position de l'hôte], 1 diffusé(s) à 'killi'#1 (1 par identifiant, 0 par position
      d'apparition (repli), 0 sans clé écarté(s)), débit=0.5 Ko/s (51 état(s) en 51 paquet(s) sur 5 s)
```

Ce que ces chiffres disent, et ce qu'ils ne disent pas :

| Mesure | Valeur | Lecture |
|---|---|---|
| Pas entre deux annonces à 10 Hz | `2,2 m` en régime, `7,25 m` au maximum (un à-coup), `1130` annonces dans la bande `≥ 2 m` | le serveur tient la cadence, et le véhicule bouge assez pour que ce soit un vrai déplacement |
| Pas par image chez le client (~143 fps) | `0,39 m` sur la fenêtre de sonde, `0,48 m` sur toute la session, `0,14 m` moyen, **`0` image `≥ 1 m` sur 10 259 images en mouvement** | l'interpolation transforme le saut en glissement continu, et absorbe même l'à-coup de `7,26 m` (15× sous l'annonce) ; le seuil d'échec (`1 m`) reste 7× sous l'annonce maximale observée |
| Vitesse implicite | `20,3 m/s` | le glissement restitue la vitesse réelle, pas un ralenti |
| Écart avant lissage | `2,94 m` max | c'est l'annonce elle-même (2,25 m) plus les quelques centimètres de physique local : normal, et c'est **avant** le lissage |
| Débit | `0,4–0,5 Ko/s` par client pour un véhicule | des dizaines de fois moins qu'un flux de 64 créatures (23,4 Ko/s au §28) |

**Le véhicule de mesure a été fabriqué des deux côtés, et la clé ne pouvait donc pas coïncider** : un
`spawn Seamoth` créé à l'exécution reçoit un `UniqueIdentifier` neuf dans chaque instance. Le client a bien
calculé la sienne (`2067…`, mécanisme `identifiant`), mais elle diffère de celle du serveur (`3a38…`) —
c'est exactement le cas que le rattachement par proximité couvre : `1` rattachement, à `29,25 m`, nommé au
journal. Les `378` clés inconnues comptées avant ce rattachement sont les annonces arrivées pendant que le
client n'avait pas encore de contrepartie locale. **Sur une vraie partie, les deux instances chargent la
même sauvegarde et lisent le même identifiant** : le rattachement ne devrait pas servir ; il est là pour un
véhicule fabriqué, et il est plafonné (même type, un seul candidat, ≤ 30 m) et journalisé.

### Deux défauts trouvés en chemin, tous les deux mesurés

1. **`Player.main` existe dans les scènes de menu.** La sonde de l'hôte attendait `Player.main != null` :
   elle démarrait donc avant le chargement du monde et fabriquait le Seamoth à `(-8.2, 4.3, 5.2)` alors que
   le joueur était à `(-114.5, 2.3, -54.2)` — 400 m plus loin, hors du rayon de pertinence. La mesure d'étape
   J rendait `verdict=False` (`aucun véhicule suivi 180s après l'entrée dans le monde`) avec pourtant un
   serveur qui diffusait : le défaut était dans la sonde, pas dans le flux. Le signal « monde chargé » est
   maintenant `LargeWorldStreamer.main`, suivi de 3 s pour laisser le jeu placer le joueur à sa position
   sauvegardée. La scène ne suffit pas : elle peut déjà s'appeler `Main` pendant le chargement.
2. **Des annonces livrées en rafale faisaient revenir le saut.** Une frame bloquée (chargement d'assets)
   retient plusieurs paquets, qui arrivent ensemble : l'intervalle **mesuré entre deux annonces** vaut alors
   presque zéro, et la durée du segment tombait au minimum de `0,04 s`, si bien que `alpha` passait à 1 en une
   image. Mesuré avant correction : `pas par image max=21.11 m`, alors que la fenêtre de sonde de 12 s
   (prise plus tard, sans rafale) n'en voyait aucun. La durée d'un segment vaut désormais
   `max(intervalle, distance / 40 m/s)` : le rattrapage est étalé sur ce qu'il faut, et le compteur de toute
   la session est redescendu à `0` image `≥ 1 m`.

### Ce que M4 ne fait toujours pas

- **Ni énergie, ni modules, ni dégâts de véhicule** : le message ne transporte que position et orientation.
  Le critère du §6 (« position/énergie/upgrades autoritaires ») n'est donc rempli qu'à un tiers, et c'est
  écrit tel quel plutôt que coché.
- **Aucune conduite autoritaire** : rien ne remonte du client vers le serveur. Un client qui pilote son
  Seamoth le simule localement (le gestionnaire le laisse faire et le journalise), et le serveur ne le sait
  pas.
- **Un véhicule fabriqué par un client après le chargement n'existe pas dans le monde du serveur**, et
  réciproquement un véhicule fabriqué par l'hôte pendant la session n'existe pas chez le client : le
  protocole ne transporte pas encore la création d'entités. La synchronisation suppose donc un véhicule
  **présent dans la sauvegarde chargée des deux côtés** — c'est une limite de contenu/spawn, de la même
  famille que M6, pas de M4 (a)/(b).
- **La preuve d'interpolation porte sur un seul véhicule et un seul type** (Seamoth), à un client.

---

## 34. La clé nominale, et elle seule : le Seamoth est dans la sauvegarde, deux clients le suivent sans se rabattre sur la proximité (2026-10-17)

### Ce que l'étape J ne prouvait pas

L'étape J mesure l'interpolation, mais **son véhicule est fabriqué par la sonde des deux côtés** : un
`spawn Seamoth` crée un `UniqueIdentifier` neuf dans chaque instance, donc les clés diffèrent par
construction, et le client se rabat sur le rattachement par proximité (`1` fois, `29,25 m`, journalisé).
Ce chemin est prévu et borné, mais ce n'est pas celui d'une vraie partie : sur une partie réelle, les deux
instances chargent la **même** sauvegarde et lisent le **même** identifiant sérialisé. Cette moitié de la
promesse de M4 — « la clé nominale, sans rattachement » — n'était prouvée par rien.

### Ce qui a été ajouté

- **`-coopdebug=vpersist`**, `src/Coop.Server/VehiclePersistProbeBehaviour.cs` : la sonde attend un monde
  **réellement chargé**, fabrique un Seamoth par la commande console si le monde n'en a pas, le place à
  l'ancre `joueur + (0, -8, 0)`, relève sa clé, puis déclenche `IngameMenu.SaveGame()` — le chemin exact du
  bouton du menu — et attend que le dossier temporaire change. Elle **ne détruit pas** le véhicule : c'est
  précisément ce qui doit rester dans la sauvegarde.
- **`AutoLoadBehaviour.WorldLoaded`** (`src/Shared`) : un seul signal de « monde chargé », posé par la
  boucle qui journalise déjà `MONDE CHARGÉ`. Les deux sondes serveur l'attendent au lieu de deviner.
- **Étape K** de `tools/verify.sh`, en **deux runs** : une préparation (un client, `-coopdebug=vpersist`)
  qui met le Seamoth dans la sauvegarde de l'hôte, puis une mesure à deux clients (`-coopdebug=vehicle`).
  Un seul run ne peut pas faire les deux : les clients ont déjà chargé le monde quand la sauvegarde
  s'écrit.
- **`expect_last`** : une assertion sur la **dernière** ligne d'état, pour les compteurs cumulatifs. Un
  `expect` classique restait vert sur les lignes d'avant le défaut (voir §35.2).

### La mesure (2026-10-17, 22:25–22:29)

Préparation :

```
[sonde] véhicule persistant : verdict=True — 'SeaMoth(Clone)' (déjà présent), clé=0xe16d82d1f441441c
(identifiant) conservée dans la sauvegarde (aucune destruction), écritures observées=31,
ancre=(-120.74, -21.93, -64.54), joueur=(-120.74, -13.93, -64.54).
```

Mesure, côté serveur :

```
[net] identités de véhicules : 1 véhicule(s) : 1 par identifiant, 0 par position d'apparition (repli),
      0 sans clé — 'SeaMoth(Clone)' (SeaMoth) pos=(-120.7, -21.9, -64.5) clé=e16d82d1f441441c (identifiant)
```

Mesure, côté client 1 (l'état final ; le client 2 est identique) :

```
[véhicule] suivi adopté : clé=0xe16d82d1f441441c -> 'SeaMoth(Clone)' (SeaMoth) pos=(-120.7, -21.9, -64.5)
clé=e16d82d1f441441c (identifiant) (clé locale calculée par identifiant, physique gelée, écart initial=12.7 …)
[véhicule] état : suivis=1 … appariés=1962 clé inconnue=106 rattachés par proximité=0 relâchés=0
conduits localement=0 … dont ≥1m=0, vitesse implicite≈20.0 m/s
[sonde] véhicule client : verdict=True — … 1665 image(s) en 12.00s (fps≈139), déplacement=231.53 m,
pas par image max=0.52 m / moyen=0.14 m … images ≥1 m=0, pertes de suivi=0
```

| Mesure | Valeur | Lecture |
|---|---|---|
| Clé du véhicule, des deux côtés | `e16d82d1f441441c`, mécanisme `identifiant` | c'est le même `UniqueIdentifier` relu dans la sauvegarde, pas une position d'apparition |
| Rattachement par proximité | `0` en état final, et **aucune** ligne `rattachée par proximité` dans les deux journaux | le chemin de secours n'a pas servi : c'est la clé nominale qui a apparié |
| Contrepartie locale | **aucune** ligne `fabrication d'une contrepartie locale` | les deux clients ont trouvé le véhicule déjà suivi, ils n'ont rien fabriqué |
| Éléments appariés | `1962` et `1702` sur la session | le flux est suivi jusqu'à la fin, pas seulement à l'adoption |
| Clés inconnues | `106` et `132`, **constants** après l'adoption | ce sont les annonces reçues pendant que le client était encore au menu (le registre local n'existait pas) : aucune clé ne reste inconnue une fois le monde chargé |
| Pas par image | `0,52 m` max sur une image de `28 ms` (`18,6 m/s`), `0` image `≥ 1 m` | l'interpolation tient la même promesse que J, avec le véhicule de la sauvegarde |

Ce que ça ne prouve pas, et qui reste écrit comme tel : **un seul type** (Seamoth), **deux clients**, une
sauvegarde de test ; le remplacement du **slot** n'est pas observé (seul le dossier temporaire l'est) ; et
rien de ce chapitre ne concerne l'énergie, les modules, les dégâts ou la conduite (§33, « Ce que M4 ne fait
toujours pas »).

## 35. Trois défauts de mesure, dont un qui rendait l'étape verte sans preuve (2026-10-17)

1. **Les sondes démarraient avant le monde.** `Player.main` **et** `LargeWorldStreamer.main` existent dans
   les scènes de menu, et le streamer est même ré-initialisé pendant le chargement. Au premier run de K, la
   sonde de préparation a donc fabriqué le Seamoth à la position du joueur de menu `(2,08, -3,78, -0,69)`,
   l'a sauvegardé là — à 400 m de la capsule, hors du rayon de pertinence — et la mesure a dû le rattacher
   par proximité : l'étape « prouvait » l'inverse de son titre. Le signal est désormais
   `AutoLoadBehaviour.WorldLoaded`, et K affirme `joueur=(-1xx…` dans la ligne de verdict, ce qui rend la
   régression impossible à confondre avec un succès.
2. **Une assertion verte sur un compteur cumulatif.** `rattachés par proximité=0` figure aussi dans les
   lignes d'état d'avant le rattachement : `expect` (première occurrence) restait donc vert alors que
   l'état final valait `1`. `expect_last` lit la dernière ligne d'état, et l'étape affirme en plus
   l'**absence** de la ligne `rattachée par proximité` et de la `fabrication d'une contrepartie locale`.
3. **Un seuil de pas par image qui dépendait du framerate.** « Pas max ≤ 1 m » suppose une image de
   `25 ms` à la vitesse de rattrapage ; à 137 fps, une image de `41 ms` a produit `1,06 m` (`26 m/s`) et
   fait échouer la sonde alors que le glissement était correct. Le pas est maintenant jugé avec **sa**
   durée d'image, contre le plafond que l'interpolation s'impose déjà (`40 m/s`), et un diagnostic
   `[véhicule] pas rapide` (5 occurrences max) journalise toute image plus rapide que `50 m/s` — c'est lui
   qui distingue un vrai saut d'une image longue. Après correction : `0,52 m` et `0,59 m`, `0` image
   `≥ 1 m`, aucun `pas rapide`.

---

## 36. Les créatures glissent au lieu de sauter : interpolation à 10 Hz, mesurée image par image (2026-10-17)

### Le défaut

Constat en jeu : les créatures (« entités ») avancent par à-coups, alors que les autres joueurs sont fluides.
Les trois chemins n'étaient pas les mêmes :

| Ce qui bouge | Cadence serveur | Côté client avant ce chapitre |
|---|---|---|
| Créatures | **10 Hz** (`Protocol.CreatureStreamIntervalTicks = 2` × tick 20 Hz) | `transform.position = position` à chaque annonce — **aucune interpolation** (`RemoteCreatureManager`) |
| Joueurs | 20 Hz (`PositionSendIntervalMs = 50`) | interpolation sur `0,05 s` (`RemoteAvatar`) |
| Véhicules | 10 Hz | interpolation par segment (M4, §33) |

Une créature ne recevait donc une nouvelle position que dix fois par seconde : paliers de `0,25–0,5 m`
(max mesuré `0,99 m`), pendant que son `Animator` continuait de tourner. C'est cette combinaison qui donne
l'impression d'entités à ~10–30 images/s. Le commentaire d'origine assumait l'application directe ; il notait
déjà que « l'absence d'interpolation ne survit pas à la question qu'elle pose ».

### Ce qui a changé

- **Un segment par créature** (`RemoteCreatureManager.Adopted`) : `From/To` (position et rotation), `SegmentStart`,
  `SegmentDuration = clamp(max(intervalle mesuré, distance / 40 m/s), 0,04 s, 0,50 s)`. Le client avance le
  segment **à chaque image** dans `Update()`, et arrive sur la cible quand l'annonce suivante arrive — la même
  mécanique que `RemoteVehicleManager`, avec le même plancher de vitesse pour les rafales de paquets.
- **Deux applications directes seulement** : la première annonce d'une créature, et une interruption de flux
  de plus de `0,50 s`. Elles sont comptées (`applications directes=`) pour ne pas se confondre avec un
  glissement.
- **Aucun changement de protocole ni de bande passante** : c'est le client qui lisse. Le frein du `Rigidbody`
  à chaque état est conservé, l'IA reste coupée, et la physique ne peut plus dériver entre deux annonces
  puisque la position est réécrite à chaque image.
- **Mesure par image** : `_frameSteps`, pas max et sa durée d'image, moyenne, images `≥ 0,5 m`, images longues
  (`> 50 ms`) comptées à part. Publiée dans la ligne `[créature] état` et dans la sonde.
- **Critère 8 de la sonde** (`-coopdebug=creature`) : la copie doit avoir avancé pendant la fenêtre
  (`image(s) animée(s) > 0`) et son plus grand pas doit rester sous `0,5 m`, ou sous `40 m/s` pour une image
  longue. Sans interpolation, `image(s) animée(s)=0` : le critère est rouge par construction.
- **Assertions de `verify.sh`** : étapes **A** et **E** exigent `image(s) animée(s)=[1-9][0-9]*`.

### Une mesure qui a changé de sens

L'écart « avant application » (copie locale vs position annoncée) ne veut plus la même chose : avec
l'interpolation, la copie est encore en route quand l'annonce suivante arrive, donc l'écart inclut jusqu'à un
intervalle de retard. Et **deux états livrés dans la même image** (rafale) ne mesurent plus rien du tout : la
copie n'a pas eu une image pour avancer, l'écart vaut deux pas de serveur. Mesuré au premier run : `2,76 m`
sur un client, au-dessus du seuil `2 m`, alors que le plus grand pas d'annonce est `0,99 m`. Les couples plus
courts que `20 ms` sont donc **exclus de la mesure d'écart** et comptés à part (`rafales écartées=`) — pas
ignorés. Après correction : `1,29–1,51 m`, sous le seuil.

### La mesure (2026-10-17, 22:54–23:01)

| Run | Pas par image max | Moyenne | Images `≥ 0,5 m` | Écart max | Verdict |
|---|---|---|---|---|---|
| A (1 client) | `0,19 m` sur une image de `19 ms` (`9,6 m/s`, `RabbitRay`) | `0,020 m` | `0` sur `171 150` images animées | `1,51 m` | `True` |
| E client 1 | `0,19 m` sur `11 ms` (`17,5 m/s`, `Peeper`) | `0,021 m` | `0` sur `166 552` images | `1,44 m` | `True` |
| E client 2 | `0,11 m` sur `7 ms` (`16,5 m/s`, `Peeper`) | `0,020 m` | `0` sur `171 886` images | `1,29 m` | `True` |
| C (plafond abaissé) | — | — | — | `0,85 m` | `True` |

Lecture : le plus grand pas d'image (`0,19 m`) est **cinq fois plus petit** que le plus grand pas d'annonce
(`0,99 m`) et sous la limite de visibilité retenue (`0,5 m`) ; aucune image ne dépasse ce seuil sur plus de
`170 000` images animées par client. La vitesse implicite reste celle de la créature (`9,6–17,5 m/s` sur les
cas les plus rapides), donc ce n'est pas un ralenti : c'est le même mouvement, réparti.

### Ce que ça ne prouve pas

- **L'animation** n'est pas interpolée : la vitesse de nage vient toujours de l'`Animator` du jeu. Seule la
  position (et la rotation) est lissée ; si l'animation paraît décalée, c'est un autre sujet.
- **La cadence reste 10 Hz** : la bande passante est inchangée, et une créature peut toujours être en retard
  d'un intervalle (c'est le prix du glissement, le même que pour les véhicules).
- Preuve portée sur **un type de scène** (la sauvegarde de test), **64 créatures par client** au plus, et
  **deux clients** ; aucun test d'interaction véhicule/créature.

---

## 37. L'étape B jugeait l'arrêt avec un seuil de 1 cm, et K modifiait la sauvegarde du développeur (2026-10-17)

### 37.1 Le critère d'arrêt ne pouvait pas se confirmer

L'étape B (sonde de présence) a échoué deux fois de suite sur « nage puis arrêt » avant de passer deux fois.
Les deux échecs disent la même chose sous deux formes :

```
vitesse après arrêt 2.60 m/s (minimale observée 2.60), avancées vues=10,
arrêt tenu 1.5s sur 60s de nage puis 10s d'arrêt (déplacement local du nageur : 2.73 m)
vitesse après arrêt 0.00 m/s (minimale observée 0.00), avancées vues=7,
arrêt tenu 1.5s sur 60s de nage puis 10s d'arrêt (déplacement local du nageur : 106.03 m)
```

Le code exigeait **deux** conditions : la position du pair ne devait plus avancer de plus de
`PositionAdvanceMeters = 0,01 m` **et** la vitesse annoncée devait passer sous `0,5 m/s`. Les deux sont
mauvaises pour un avatar interpolé :

- **1 cm par échantillon** est en dessous du tremblement d'un avatar immobile (interpolation + animation de
  racine) : le compteur repartait sans cesse, l'arrêt n'était jamais confirmé même à `0,00 m/s` ;
- **la vitesse annoncée** repasse par zéro pendant la nage et peut rester figée à sa dernière valeur après
  l'arrêt (mesuré : `2,60 m/s` dix secondes après).

Le commentaire de la constante disait déjà « la position est le critère, la vitesse reste une observation » —
le code ne le faisait pas. Correction : **déplacement borné** (`StopRadiusMeters = 0,5 m`, la limite de
visibilité d'un avatar) sur `1,5 s`, la vitesse restant journalisée. La ligne de sonde affiche désormais
`écart à l'arrêt max=… m (critère : < 0,5 m)`, pour qu'un vrai glissement se voie au lieu d'être absorbé.

**Mesure** : deux runs consécutifs verts (`étape B : OK (103s)` puis `OK (101s)`, 2026-10-17 23:17 et 23:19),
contre 2 échecs sur 3 avec l'ancien critère. Ce n'est pas une preuve statistique — c'est la cause identifiée
dans le code et deux runs qui ne la contredisent pas.

### 37.2 K laissait la sauvegarde de test modifiée

La préparation de K écrit dans la sauvegarde de l'hôte : elle y ajoute un Seamoth et déplace le joueur à
l'ancre. C'est le but de l'étape, mais le run suivant héritait de cet état — et l'étape B dépend de la
position de départ du joueur. Chaque vérification modifiait donc la fixture de la suivante.

`tools/verify.sh` copie maintenant `SNAppData/SavedGames/slot0000` **avant** la préparation et la restaure
**après** l'arrêt des instances (elles sont fermées par `run-session.sh`, le jeu ne réécrit donc pas pendant
la copie). Une restauration qui échoue est un échec de l'étape, pas un avertissement.

**Mesure** : `étape K : OK (242s)` avec `sauvegarde : fixture slot0000 restaurée`, et l'empreinte du dossier
(`find | sort | sha1sum`) est **identique avant et après** : `19d74e76531a0e7cc41c9b019853f179e7629107`.

**Limite** : la fixture est désormais **stable**, pas **vierge** — elle contient encore le Seamoth et la
position laissés par les premiers runs de K. Pour repartir d'un état connu, il faut recharger une sauvegarde
manuelle ; K la préservera ensuite telle quelle.

### 37.3 Le calendrier de nage se refermait sur lui-même, et G supposait un monde vide

Deux causes distinctes, trouvées en rejouant la batterie complète le 2026-10-17 (11 étapes vertes, B et G
rouges) :

- **B** : chaque client sortait de la phase de nage dès qu'il avait vu **10 avancées** de l'autre, puis
  jugeait son arrêt. Le client 2 sortait donc après `1 s` — il nageait `3 m` — et le client 1, qui n'avait
  vu que `7` avancées de ces `3 m`, restait à nager `60 s`. Chacun produisait juste assez de mouvement pour
  faire sortir l'autre, pas assez pour être mesuré par lui : `déplacement 2,27 m`, `avancées vues=7`,
  `verdict=False`. Le calendrier était **circulaire**. Correction : une durée de nage **minimale** de `6 s`
  avant de pouvoir conclure « l'autre me voit », et une fenêtre d'arrêt portée de `10` à `20 s` (les deux
  ne s'arrêtent jamais à la même seconde). Les seuils de mesure (`10` avancées, `0,5 m`, `1,5 s`) ne sont
  pas touchés.
- **B (seconde moitié)** : l'arrêt de l'avatar était jugé pendant que le nageur **local** bougeait encore.
  Le critère est désormais ancré sur l'arrêt du joueur : les deux horloges sont mesurées séparément et
  journalisées (`écart local après l'arrêt`, `arrêt local confirmé`), pour qu'un échec dise lequel des deux
  a manqué.
- **B (mise à l'eau)** : un cap sur quatre plaçait le joueur dans le décor (déplacement local `2,93 m` en
  `60 s`). La sonde essaie maintenant chaque cap quelques secondes et ne retient que celui qui **déplace**
  le joueur (`déplacement local d'essai=2,15 m en 4s -> retenu`).
- **G** : l'assertion exigeait `aucun véhicule dans le monde` et la commande `spawn Seamoth`. Depuis que K
  écrit un Seamoth dans la sauvegarde (fixture stable, pas vierge), la sonde le trouve déjà là
  (`'SeaMoth(Clone)' déjà présent à 8.0 m de l'hôte — déplacé tel quel`, `retenu (déjà dans le monde)`).
  Les deux chemins sont maintenant acceptés **nommément** dans `tools/verify.sh` : c'est la ligne de
  journal qui est assertion, pas une supposition sur l'état du monde.

**Mesure** : `étape B : OK (110s)` dans la batterie complète, puis deux runs isolés verts
(`déplacement 11,51 m` / `11,44 m`, `avancées vues=54`, `écart à l'arrêt max=0,10 m` / `0,16 m`) — contre
`2,27 m / 7 avancées` et `0,63 m` avant. `étape G : OK (42s)` avec le Seamoth déjà présent.

---

## 38. M5 (a) : la première décision autoritaire — un objet réclamé deux fois n'est accordé qu'une (2026-10-17)

### Ce qui est en place

Le client ne fait plus seulement remonter des positions : il **demande** une décision. Le serveur tient
une carte `ItemKey → PlayerId` et répond, objet par objet :

- **première réclamation** d'une clé → accordée à l'émetteur, la clé est enregistrée ;
- **réclamation suivante par un autre joueur** → refusée, avec le détenteur nommé dans le journal et dans
  la réponse ;
- **réclamation du même joueur** → accordée (idempotente), pour qu'un renvoi après une perte de paquet ne
  se transforme pas en refus ;
- **clé nulle** → refusée (aucune identité d'objet) ;
- **`PlayerId` annoncé ≠ id de session** → la réclamation est attribuée à la **session**, et l'écart est
  compté (`ForgedPickupIds`) puis journalisé. Un client ne peut donc pas réclamer pour un autre.

Protocole **v8** : `PickupClaim` (#15, client → serveur) et `PickupResult` (#16, serveur → demandeur), la
clé de connexion LiteNetLib devient `coop-subnautica-v8`. Aucun état durable n'est ajouté côté client : la
décision est un aller-retour fiable, et le résultat est mémorisé par clé pour que la sonde puisse le relire.

### La mesure (étape L de `tools/verify.sh`, 2026-10-17 23:28)

Deux clients réclament la **même** clé synthétique ; l'ordre est déterministe par identifiant (le client 1
réclame à 10 s de sa connexion, le client 2 à 22 s), puis le second envoie une réclamation sous l'id 1.

```
client 1 — verdict=True : id=1, clé partagée 0x5049434b55503131 reçue=True accordée=True tenue par 1
client 2 — verdict=True : id=2, clé partagée 0x5049434b55503131 reçue=True accordée=False tenue par 1 ;
            clé forgée 0x5049434b55503232 reçue=True accordée=True tenue par 2
serveur  — pickup : clé=0x… -> ACCORDÉE (premier claim)
           pickup : clé=0x… -> REFUSÉE (déjà tenue par 'killi'#1)
           vérification PlayerId (pickup) : '…' a annoncé l'id 1, effectif=2 -> verdict=True
```

`étape L : OK (97s)`. Les assertions portent sur les trois lignes du serveur et sur les deux verdicts
clients — le refus n'est pas déduit d'un compteur, il est **nommé** avec son détenteur.

### Ce que M5 (a) ne faisait pas — la moitié qui reste

**Fermée le 2026-10-17 : voir §39.** Les trois limites ci-dessous (clé synthétique, ramassage local sans
permission, ni existence ni portée vérifiées) sont traitées ; lire cette liste comme l'état d'avant.

- **Le ramassage réel n'est pas branché** : la clé vient de la sonde, pas d'un objet du monde. Le client
  ramasse encore localement (le jeu ne demande pas la permission), et rien ne remet l'objet en place quand
  le serveur refuse. Brancher les deux exige l'identité de l'objet **avant** sa destruction — un hook sur
  `Pickupable` (Harmony) ou un inventaire de bord — puis un retour arrière (retirer de l'inventaire, faire
  réapparaître l'objet).
- **Aucune vérification de portée** : le serveur ne vérifie ni que l'objet existe ni que le joueur est à
  côté. Une fois la clé lue dans le monde, la distance sera la première garde à ajouter.
- **Pas de persistance** : la carte des réclamations vit avec la session serveur. Après un redémarrage,
  les objets déjà ramassés ne sont plus réclamés — c'est cohérent tant que la sauvegarde, elle, les a bien
  retirés.

---

## 39. M5 (b) : le ramassage réel passe par la décision, et un geste refusé est bloqué (2026-10-17)

### Ce qui est en place

Trois pièces, et chacune répond à une limite nommée du §38 :

1. **`Shared/ItemIdentity.cs`** — la clé d'un objet **réel**, calculée des deux côtés et compilée dans les
   deux plugins : `UniqueIdentifier.Id` d'abord (`identifiant`), position d'ancrage quantifiée à 5 cm +
   prefab + `TechType` en repli (`position d'ancrage (repli)`), `0` si aucun des deux. Les objets **dans un
   inventaire** sont exclus de la collecte : sinon le serveur croirait encore dans le monde un objet déjà
   ramassé.
2. **`Coop.Client/PickupGuard.cs`** — patch Harmony de `Pickupable.OnHandClick` (le geste du joueur, celui
   que le jeu appelle quand on clique sur l'objet) : objet **accordé** → passe ; **refusé** → bloqué avant
   l'inventaire ; **sans décision** → réclamation envoyée et ramassage **optimiste**, puis **retour
   arrière** si le refus arrive (retrait de l'inventaire + `Drop()`). Les objets **sans clé** passent et
   sont comptés (`sans clé=`) : c'est une limite, pas un refus silencieux.
3. **`ServerNetwork.TryResolveWorldItem` + `Protocol.MaxPickupRangeMeters`** — le serveur résout la clé dans
   **son** monde (index reconstruit au plus une fois par seconde, 449 objets) et refuse ce qu'il ne sait pas
   résoudre (`objet inconnu du serveur`) ou ce qui est trop loin (portée `6,0 m`, mesurée avec la dernière
   position reçue de la session, 20 Hz). Aucun champ ajouté au message : la v8 n'est pas rouverte.

La sonde (`-coopdebug=pickup`) ne réclame plus une clé inventée : elle choisit un **objet réel immobile**
(le plus proche du point d'apparition, hors créatures — un `Pickupable` porté par un poisson a une clé
stable mais une position qui nage, mesuré : refus `hors portée : 6,8 m > 6,0 m` pour un joueur pourtant à
`1,74 m`), s'y place, réclame, **ramasse** pour de vrai, et lit séparément le compteur de la garde et le
contenu de l'inventaire.

### La mesure (étape L, 2026-10-17 00:51)

```
serveur  pickup : clé=0x10ee181a0699267d 'PowerCell' réclamée par 'killi'#1 -> ACCORDÉE (premier claim, objet à 2.2 m)
         pickup : clé=0xbd613fd07fc6fef1 'AcidMushroom' réclamée par 'killi'#2 -> ACCORDÉE (premier claim, objet à 1.7 m)
         pickup : clé=0x10ee181a0699267d 'PowerCell' réclamée par 'killi'#2 -> REFUSÉE (déjà tenue par 'killi'#1)
         pickup : clé=0x5049434b55503232 'CléFalsifiée' réclamée par 'killi'#2 -> REFUSÉE (objet inconnu du serveur (449 objet(s) indexé(s)))
client 1 verdict=True — objet 'PowerCell(Clone)' clé=10ee181a0699267d (identifiant) ; garde=accord (accords 0->1), objet dans l'inventaire=True
client 2 verdict=True — clé partagée refusée tenue par 1 ; clé inventée refusée ; id falsifié accordé tenue par 2 revendiquée sous l'id 1 ;
         garde=blocage (blocages 0->1), objet dans l'inventaire=False
```

`étape L : OK (97s)`, puis `--only=B,L` : `B : OK (107s)`, `L : OK (96s)`, `VERDICT GLOBAL : tout est vert`.

### Ce que M5 (b) ne fait pas encore

- **Le monde du serveur garde l'objet.** Le client le retire du sien (son inventaire), mais aucun message
  ne dit « cet objet n'existe plus » : un troisième joueur qui le réclame après coup reçoit un accord, et
  ne verra rien. Il manque un `ItemRemoved` (ou la conséquence serveur d'un ramassage accordé), et c'est
  le prochain pas de M5.
- **Les objets mobiles** (butin porté par une créature, objet lancé) ont une clé stable mais une position
  qui change : la portée mesurée peut être fausse. La sonde les évite explicitement ; le jeu, lui, n'a pas
  cette politesse.
- **Les collisions de clé** sont comptées, pas résolues : l'index serveur signale `5 clé(s) d'objet
  partagée(s)` sur la sauvegarde de test (le repli par position confond des objets posés au même endroit).
  La décision porte alors sur le dernier indexé.
- **Le retour arrière n'est exercé par aucune mesure** : l'étape L vérifie le blocage **avant** l'inventaire
  (chemin 2), pas le retrait après coup (chemin 3), qui demanderait de provoquer un refus en pleine course
  — un objet déjà réclamé et ramassé pendant l'aller-retour.

---

## 40. M4 (b) : la conduite est autoritaire — un seul conducteur, et le refus est rendu au demandeur (2026-10-17)

### Le choix, dit franchement

Le véhicule est conduit par le joueur, avec les entrées du joueur et la physique locale du jeu. Le faire
simuler par le serveur demanderait de transporter les entrées (gaz, barre, tangage) et de rejouer le moteur
du jeu côté serveur. On fait donc l'inverse : **le client conduit et envoie son état**, et le serveur garde
ce qui compte — **le droit** de conduire, une portée, et un refus opposable. C'est un transfert d'autorité
sur une entité du monde, pas une simulation serveur.

### Ce qui est en place (protocole v9)

- **`VehicleControl` (#17, client → serveur, Unreliable, 10 Hz)** : clé du véhicule, position, rotation,
  vitesse, numéro d'ordre. `PlayerId` est **revendiqué** et falsifiable, comme pour la réclamation d'objet ;
  le serveur prend l'identité de la session et compte l'écart (`ForgedVehicleIds`).
- **`VehicleControlResult` (#18, serveur → demandeur, ReliableOrdered)** : accordé / refusé, et le
  conducteur **effectif**. Sans lui, un second joueur enverrait son état dans le vide et son véhicule local
  divergerait sans un mot. Réponse à chaque accord ; pour un refus, au plus une fois par seconde et par
  clé (le client envoie 10 états par seconde, il n'a pas besoin de 10 refus).
- **Serveur** : la clé doit désigner un véhicule **de son monde** (index reconstruit au plus une fois par
  seconde), le joueur doit être à moins de `MaxVehicleControlRangeMeters` (10 m), et un véhicule déjà
  conduit par un autre joueur n'est pas volé : le refus est journalisé **une fois par clé** (à 10 Hz, un
  refus répété noierait le journal). L'état accepté est appliqué au transform du véhicule serveur, sa
  vitesse physique annulée, puis **rediffusé** par le flux `VehicleSnapshot` déjà en place.
- **Client** : `VehicleDriverBehaviour` lit `Player.main.currentMountedVehicle` et envoie à 10 Hz ; la sonde
  emprunte **la même boucle**, seule la source du véhicule est forcée (`ProbeTarget`).

### La mesure (étape M de `tools/verify.sh`, 2026-10-17 01:03)

```
serveur  conduite : clé=0xe16d82d1f441441c — prise par 'killi'#1 ('SeaMoth(Clone)' (SeaMoth) pos=(-120.7, -21.9, -64.5) clé=e16d82d1f441441c (identifiant), joueur à 2.5 m), état appliqué et rediffusé.
         conduite : clé=0xe16d82d1f441441c réclamée par 'killi'#2 -> REFUSÉE (déjà conduite par 'killi'#1)
client 1 prise en main : clé=… (identifiant) — envoi à 10 Hz ; résultat : accordée=True conduite par 1 ; verdict=True (77 états en 8 s, accords=1, refus=0)
client 2 résultat : accordée=False conduite par 1 ; verdict=True (refus=2, accords=0)
```

`étape M : OK (93s)`, `VERDICT GLOBAL : tout est vert`.

### Ce que la conduite ne fait pas encore

- **Aucune libération** : le conducteur garde la main jusqu'à sa déconnexion, même après être descendu. Un
  second joueur ne peut donc pas reprendre un Seamoth que le premier a laissé — il faut un message de
  libération (ou l'expiration d'un silence de N secondes), et c'est le prochain pas de M4.
- **Le joueur n'est pas monté pour de vrai** : la sonde force la source du véhicule. Ce qui n'est pas
  exercé est la lecture `currentMountedVehicle` elle-même — un champ public, mais non mesuré.
- **Rien n'est transporté d'autre** : ni énergie, ni modules, ni dégâts (M4 « le reste », §2 de
  `docs/authority.md`). Un client qui conduit et un serveur qui ne simule pas le véhicule ne peuvent pas
  décider d'une panne de batterie.

## 41. M6 (a) : une entité fabriquée par l'hôte existe chez les clients, avec la **même** clé (2026-10-18)

### Le problème, dit par la mesure

Jusqu'à la v9, un `spawn Seamoth` fait chez l'hôte était **invisible** chez les clients : le serveur
diffusait bien sa position (`VehicleSnapshot`), mais sous une clé que le client ne pouvait rattacher à rien.
L'étape J l'avait déjà montré sans le nommer : `clé inconnue=378`, et le seul rattachement observé
(`rattachés par proximité=1`) était précisément ce Seamoth fabriqué, dont le `UniqueIdentifier` est **neuf
dans chaque instance**. Ce n'est pas un défaut du suivi : c'est que personne ne **crée** l'objet côté
client. Aucun flux de positions ne peut créer ce qui n'existe pas.

### Le choix : le serveur annonce, le client instancie, l'identité voyage

- **`EntitySpawn` (#19, serveur → client, ReliableOrdered, protocole v10)** : clé, `UniqueIdentifier.Id`,
  nom d'espèce (`Seamoth`), position et rotation. La clé seule ne suffisait pas : elle est **calculée** à
  partir de l'identifiant, et le client qui instancie reçoit un identifiant neuf. Le message transporte donc
  l'identifiant du serveur, et le client **écrit cet identifiant** sur son exemplaire — c'est ce qui rend
  les deux clés égales, et le suivi possible sans rattachement par proximité.
- **Serveur** : un balayage à la cadence du cache de véhicules (`2 s`) annonce les clés **nouvelles**. Le
  premier balayage **enregistre** sans annoncer, sinon un client qui se connecte verrait arriver une annonce
  par Seamoth de la carte, y compris ceux qu'il a déjà chargés. Une entité sans identifiant n'est **pas**
  annoncée (journalisé) : une entité en double vaudrait moins qu'une entité absente.
- **Client** : `EntitySpawner` instancie par `CraftData.GetPrefabForTechTypeAsync` (l'espèce, pas le nom du
  prefab), écrit l'identifiant, pose position et rotation, puis laisse `RemoteVehicleManager` l'apparier par
  sa clé comme n'importe quel véhicule. Une espèce inconnue est **comptée et refusée** — le client n'invente
  pas de prefab.

### Deux défauts que seule la mesure a montrés

1. **Le second client ne recevait rien.** Le premier run de l'étape N l'a mesuré sans ambiguïté : le client 1
   crée l'entité (`verdict=True`), le client 2 attend 150 s et rend `verdict=False — aucune entité créée`
   alors que le serveur a bien annoncé. Cause : l'annonce est **diffusée une fois**, aux sessions présentes
   à cet instant ; un client qui se connecte après ne l'a jamais. Correction : le serveur **garde** les
   annonces (`_spawnedEntities`, borné à `256`) et **les rejoue** au client qu'il vient d'admettre
   (`SendSpawnedEntities`) — journalisé `1 entité(s) fabriquée(s) rejouée(s) pour 'killi'#2 (arrivé après
   leur création)`. La sonde de l'hôte a aussi été rendue **déterministe** : elle attend qu'un client soit
   connecté avant de fabriquer, pour que « le second arrive après » soit le cas nominal et non un hasard.
2. **Un client connecté depuis le menu fabriquait dans la scène du menu.** Deuxième run : le client 2 crée
   l'objet (`créées=1`) et ne le retrouve **plus** trois secondes plus tard
   (`objet local retrouvé par sa clé=False`). Un client se connecte au **menu principal** : l'objet naissait
   dans la scène du menu, que le chargement du monde décharge. Correction : `EntitySpawner` ne draine sa
   file qu'une fois `AutoLoadBehaviour.WorldLoaded`, et la sonde cliente attend le monde avant de juger.

### La mesure (étape N de `tools/verify.sh`, 2026-10-18)

```
serveur  [sonde] apparition : 1 véhicule(s) avant la fabrication.
         [sonde] apparition : 1 client(s) connecté(s) avant la fabrication.
         [sonde] apparition : commande console « spawn Seamoth » (tentative 1).
         [sonde] apparition : véhicule fabriqué — 'SeaMoth(Clone)' (clé=0x7ce3b6a1e8e7e312, identifiant) après 1 tentative(s)
         [net] apparition : clé=0x7ce3b6a1e8e7e312 'SeaMoth(Clone)' (Seamoth) — diffusée à 1 client(s),
               pos=(-118.4, -13.4, -52.9), identité '0a472465-f9e8-4780-8b3e-a308429250ce'.
         [net] apparition : 1 entité(s) fabriquée(s) rejouée(s) pour 'killi'#2 (arrivé après leur création).
client 1 [net] apparition reçue : clé=0x7ce3b6a1e8e7e312 'Seamoth' identité '0a472465-…'
         [spawn] entité créée : clé=0x7ce3b6a1e8e7e312 'SeaMoth(Clone)' (Seamoth) pos=(-118.4, -13.4, -52.9)
               — identifiant du serveur '0a472465-…' écrit (oui), clé locale 0x7ce3b6a1e8e7e312, créées=1
client 2 les deux mêmes lignes, après son chargement de monde
les deux [sonde] apparition : verdict=True — entités créées=1, écartées (déjà présentes)=0, échecs=0,
          clés divergentes=0 ; dernière clé=0x7ce3b6a1e8e7e312 'SeaMoth(Clone)' — objet local retrouvé par sa clé=True
```

`étape N : OK (79s)`, `VERDICT GLOBAL : tout est vert`. La clé locale est **la clé annoncée** : le Seamoth
fabriqué se suit désormais sans rattachement par proximité.

### Ce que cette moitié de M6 ne fait pas

- **Aucun retrait.** `EntityDespawn` n'existe pas : détruire un véhicule chez l'hôte ne dit rien aux
  clients, qui gardent l'objet. Le carnet de rejeu est borné à `256` ; au-delà, la plus ancienne annonce est
  oubliée et un client qui se connecte ne la reçoit plus (journalisé).
- **Seuls les véhicules.** Un objet (`Pickupable`) ou une créature fabriqués chez l'hôte ne sont pas
  annoncés : la clé d'une créature est sa position d'apparition, et l'annonce d'un objet demanderait de
  choisir ce qui identifie un objet créé à l'exécution.
- **Un seul sens.** Un `spawn` fait **chez un client** n'existe pas dans le monde du serveur, donc pas chez
  les autres. Le déplacer demanderait un message client → serveur et un arbitrage d'existence (M6 b).
- **Rien de custom.** L'espèce annoncée doit exister dans la base du client : c'est le préfabriqué du jeu,
  pas un AssetBundle (M6 « contenu »).

## 42. M6 (b) et la fin de M4 : ce qui existe **cesse** d'exister, et un volant se **libère** (2026-10-18)

### Le problème, dans les deux cas

Deux propriétés manquaient, et toutes deux se voient à l'usage :

- **La v10 savait créer, pas retirer.** Un Seamoth détruit chez l'hôte restait chez tous les clients,
  immobile, et le flux `VehicleSnapshot` continuait à l'animer. Un objet ramassé restait **visible et
  réclamable** par les autres : le serveur accordait une clé qui ne désignait plus rien.
- **Le volant ne revenait jamais.** Un véhicule restait attribué à son dernier conducteur **jusqu'à sa
  déconnexion**. Un joueur qui descendait d'un Seamoth le rendait donc inutilisable — le refus renvoyait
  les autres à quelqu'un qui n'était plus dedans.

### Ce qui est en place

- **`EntityDespawn` (#20, serveur → client, ReliableOrdered, v11)** : clé, espèce (`véhicule` / `objet`) et
  motif. Deux sources, et une seule décision (le serveur) : le balayage des véhicules (une clé connue qui
  n'est plus dans le monde) et un ramassage accordé (l'objet est retiré du monde simulé).
- **Le détenteur est exclu de son propre retrait** : son exemplaire est déjà dans son inventaire, et c'est
  son geste — pas un message — qui le retire de son monde.
- **Le client applique, il ne décide pas** : `EntityDespawner` détruit la copie locale, ou le note comme
  absent. Rien n'est retiré avant `WorldLoaded` : un retrait reçu au menu serait défait par la sauvegarde.
- **`VehicleRelease` (#21, client → serveur, ReliableOrdered, v12)** : le conducteur qui descend rend le
  volant immédiatement ; un silence de `VehicleSilenceReleaseSeconds` (3 s) le libère aussi, et la
  déconnexion libère tout ce que le joueur conduisait.

### Ce que la mesure a corrigé (trois défauts, tous trouvés en jouant)

1. **Le détenteur perdait son objet.** Premier run de L avec le retrait : serveur `ACCORDÉE`, client
   `objet dans l'inventaire=False`. Cause : le retrait était diffusé **à tout le monde**, y compris celui qui
   venait de ramasser, avant que son geste ne range l'objet. Correction : diffusion qui exclut le détenteur.
2. **Une clé accordée devenait « inconnue ».** Le second client recevait `REFUSÉE (objet inconnu)` au lieu de
   `(déjà tenue par 'killi'#1)` : le retrait avait déjà vidé l'index. Correction : la carte des réclamations
   passe **avant** la résolution du monde — c'est le registre durable.
3. **L'objet revenait avec la sauvegarde.** Le retrait arrivait 10 s avant le chargement du second client ;
   il ne trouvait rien, puis le monde se chargeait avec l'objet intact. Correction : retraits mis en file
   jusqu'à `WorldLoaded`, et `EntitySpawner` refuse de créer une clé déjà annoncée disparue (`IsGone`) —
   sinon un fantôme survivrait à la destruction.

### La mesure

```
étape O (v11)   serveur  [sonde] disparition : véhicule 'SeaMoth(Clone)' (clé=0x…) détruit après 40s
                         [net] disparition : clé=0x… (véhicule) — annoncée à 2 client(s), motif « véhicule détruit chez le serveur »
                clients  [disparition] véhicule retiré : clé=0x… 'SeaMoth(Clone)'
                         [sonde] disparition : verdict=True — véhicules retirés=1 … cette instance avait l'objet et
                         l'a détruit, dernière clé retirée=0x5ebbcd3d006fb60c encore présente=False, entités créées ici=1

étape L (v11)   serveur  [net] pickup : clé=0x10ee181a0699267d 'PowerCell' retirée du monde serveur (…) — ramassée par 'killi'#1.
                         [net] disparition : clé=0x10ee181a0699267d (objet) — annoncée à 1 client(s), … sauf au détenteur (id 1)
                client 2 [disparition] objet retiré : clé=0x10ee181a0699267d ('PowerCell(Clone)' (PowerCell) …)

étape M (v12)   serveur  conduite : clé=0xe16d82d1f441441c — prise par 'killi'#1 (…)
                         conduite : clé=0xe16d82d1f441441c réclamée par 'killi'#2 -> REFUSÉE (déjà conduite par 'killi'#1)
                         conduite : clé=0xe16d82d1f441441c libérée par 'killi'#1 — le volant est libre (libérations=1, total=1)
                         conduite : clé=0xe16d82d1f441441c — prise par 'killi'#2 (…)      ← 0,65 s plus tard
                client 2 refus=2, accords=0 … après libération par le conducteur : accords=1 en 9 tentative(s)
```

`N : OK (78s)`, `O : OK (113s)`, `M : OK (119s)`, `L : OK (99s)`, `VERDICT GLOBAL : tout est vert`.

### Ce que la libération ne fait pas, et ce que le retrait ne fait pas

- **La libération par silence n'est mesurée que côté serveur** : la sonde fait **descendre** le conducteur
  (message #21). Le chemin « client planté, silence de 3 s » est écrit et journalisé, jamais exercé.
- **Le retrait ne vient que du serveur.** Un objet détruit **chez un client** ne disparaît pas ailleurs ;
  c'est la même asymétrie que la création (M6 b, non entamée).
- **La garde de ramassage n'est plus le rempart principal.** Depuis la v11, l'objet accordé à un autre est
  souvent retiré **avant** que le refusé ne clique : l'étape L accepte donc « geste bloqué » ou « objet déjà
  retiré », et nomme lequel des deux a joué. L'assertion n'a pas été affaiblie à l'aveugle : les deux issues
  sont écrites par la sonde, et l'objet ne rentre pas dans l'inventaire dans les deux cas.

---

## 43. Revue d'architecture : ce qui tient, ce qui ne tient pas, et la suite replanifiée (2026-10-19)

**Verdict : la couche de transport est bonne et la question de recherche est tranchée ; c'est l'ordre du plan
qui est faux.** Le projet mesure des **systèmes**, pas une **session** : 21 672 lignes, 15 étapes de
vérification (16 runs), **aucun test hors-jeu**, et aucune preuve qu'on puisse jouer trente minutes à deux. La suite est
replanifiée autour de trois propriétés manquantes — la prémisse de sauvegarde, l'autorité réelle du joueur, le
cycle de vie de session — **avant** tout nouveau mécanisme.

Cette revue est un conseil à cinq lentilles (Red Team, First Principles, Expansionist, Outsider, Executor),
revue croisée anonymisée et débat forcé. Accord **4,0/5** sur la destination, désaccord réel sur le premier
pas. Confiance globale : **moyenne**. Rapport et transcript : `docs/council/`.

### 43.1 Ce qui est bon, et qu'il ne faut pas défaire

| Actif | Preuve |
|---|---|
| Les deux inconnues les plus lourdes ont été levées **en premier** | M0/M0.5 : plugin chargé, monde chargé sans intervention, tick à pas fixe 20 Hz mesuré à 19,98–20,03/s (§9–§11) |
| La discipline de preuve | chaque affirmation porte son instrument ; les sondes rendent `verdict=True/False` ; `verify.sh` rend **un** verdict ; `expect_last`, restauration de fixture et tests discriminants ont corrigé de faux verts (§35, §37) |
| Les décisions sont écrites avec ce qui les falsifierait | §28 (identité des créatures), §33 (cadence vs interpolation), §39 (portée de ramassage) |
| La séparation `Coop.Model` / Unity | protocole binaire écrit à la main, testable sans socket ; 35/35 au selftest |
| Les victoires mesurées, pas regardées | interpolation véhicule `0,39 m/image` et `0` image ≥ 1 m sur 10 259 ; créatures `0,19 m` max ; pickup refusé nommé ; spawn/despawn avec identité serveur ; volant libéré et repris en 0,65 s |
| Les limites sont écrites, pas cachées | chaque chapitre a sa section « ce que ça ne prouve pas » |

### 43.2 Ce qui ne va pas

> **Photo datée du 2026-10-19**, conservée telle quelle. Les correctifs depuis sont au §44 (P0/P1/P1bis),
> §47 (durcissement du modèle) et §48 (symétrie écriture/lecture) : `tests/` existe (**290** tests),
> `PlayerPlausibility` garde la position, la sauvegarde est un contrat, et `verify.sh` compte désormais
> 18 étapes (A–R, dont R pour le refus P0, §49).

| Défaut | Fait | Conséquence |
|---|---|---|
| **Aucun test hors-jeu** | `tests/` était prévu au §2, n'existe pas ; 21 672 lignes, 0 test unitaire | la seule porte est une batterie de 16 runs in-game (un run complet documenté : **1 559 s**), Windows + jeu + `slot0000` ; boucle de feedback lente, non portable, non-CI |
| **Le joueur n'est pas autoritaire** | `HandlePlayerPosition` ne vérifie que NaN/Inf et l'ordre des séquences : toute position est relayée ; aucune prédiction/réconciliation dans le code | la promesse fondatrice (« corrige la faille de Nitrox ») est fausse sur la surface la plus visible ; un client peut se téléporter |
| **La prémisse « même sauvegarde » n'est pas un contrat** | le handshake vérifie `gameVersion` + `modListHash`, **pas** la sauvegarde ; l'identité des créatures en dépend entièrement (§28) | un client qui charge une vieille sauvegarde désynchronise **silencieusement** ; c'est la panne la plus probable d'une session de 30 min |
| **Pas de cycle de vie de session** | pas de reconnexion/resync ; les claims, retraits et conducteurs vivent avec la session ; les runs durent 40–240 s | fuites, dérives et pertes d'état longues ne sont pas mesurées ; un client qui plante ne revient pas proprement |
| **Monolithes** | `ServerNetwork.cs` 3 098 l. (handshake, présence, créatures, véhicules, pickup, volants, spawn/despawn, contrôle launcher) ; `Messages.cs` 1 185 ; `Plugin.cs` client 1 157 ; `AvatarProbe` 1 912 | chaque système futur édite le même fichier ; le coût de chaque correctif augmente |
| **M6 contenu est le prochain puits** | coût inconnu (AssetBundles Unity 2019), identité d'une entité custom non conçue, contrat de version non exprimable par l'égalité stricte v12 | des semaines possibles sans réponse mesurable, alors que la boucle de jeu n'est pas prouvée |
| **Rien n'est distribuable** | launcher hors dépôt, pas de `README`, pas de `.sln`, pas de script de déploiement, pas de CI ; hôte = instance complète (~2 Go, device D3D11) | le test prescrit (« deux humains, 30 min ») ne peut pas être délégué : le bus factor reste à 1 |
| **Le plan n'est plus pilotable** | `PLAN.md` = journal de 3 261 lignes / 216 Ko, sans état courant ni prochaines étapes en tête ; le tableau §6 est un journal de statuts, pas un état courant — il faut lire sept sections pour savoir ce qui reste ouvert | un lecteur (ou l'auteur dans trois semaines) ne sait pas ce qui reste |
| **Trois schémas d'identité** | créature = prefab + position d'apparition ; véhicule = `UniqueIdentifier` ; objet = les deux avec repli, 5 clés partagées sur 449 objets | chaque schéma a ses limites ; aucune couche d'identité commune |

### 43.3 La revendication à corriger, tout de suite

Le mot **« autoritaire »** est exact pour les créatures (IA coupée), les véhicules (position, droit de conduire),
le ramassage, la couleur, la création et le retrait d'entités. Il est **faux pour le joueur** : le serveur
relaie sa position et ne peut pas la contredire.

Décision : soit on rend la revendication vraie (P1 ci-dessous), soit on la renomme. Une heuristique de
plausibilité ne s'appelle pas « autorité » : elle s'appelle **garde anti-téléport**, et c'est ce que le code
dira. Tant que la mesure n'existe pas, le §1 et `docs/authority.md` portent la correction — faite le 2026-10-19 :
le §1 dit que le joueur n'est qu'un relais, et `docs/authority.md` §2 ouvre une ligne « Position du joueur »
qui nomme ce que le client décide encore et l'expérience qui le fermerait.

### 43.4 Deux portes, pas une

Le conseil a refusé de faire de la session de 30 minutes l'unique porte : elle juge l'intégration, pas la
régression, et elle est trop lente pour être une boucle.

1. **Porte de régression — rejeu hors jeu.** Enregistrer les flux d'une vraie session et les rejouer dans les
   mêmes gestionnaires, sans Unity, avec une horloge simulée. **On ne supprime aucune assertion** : on change
   leur substrat. Objectif : quelques secondes, n'importe quelle machine, exécutable en CI. Commencer par
   créatures, véhicules et décisions de ramassage (les gestionnaires ne consomment que messages + temps).
2. **Porte d'intégration — session réelle de 30 min à deux**, installée **par un second humain**, avec un
   manifeste de version épinglé. C'est un jalon, pas une boucle : on le passe quand il passe, et il ne
   remplace pas la porte 1.

### 43.5 Séquence replanifiée

| # | Travail | Jours | Critère observable |
|---|---|---|---|
| **P0** | La prémisse devient un contrat : empreinte de sauvegarde au handshake (slot + hash), **refus nommé** ; `README` + script de déploiement des DLL + manifeste épinglé ; `.sln` + `tests/Coop.Model.Tests` | 1–2 | deux instances sur des sauvegardes différentes sont **refusées avec leur raison** ; `dotnet test` vert en < 10 s |
| **P1** | Autorité joueur honnête : borne vitesse/déplacement, `PlayerCorrection`, compteur de faux positifs | 2–3 | une téléportation de 100 m est **refusée et journalisée** ; une nage normale produit **0 correction** ; le taux de faux positifs est publié |
| **P1bis** | Contrat de contenu (pas le pipeline) : une entité définie par le serveur, identité + `definitionHash`, instanciée par un client qui ne l'a jamais vue | 1 | le client instancie et **retrouve l'entité par sa clé** ; un hash divergent est refusé |
| **P2** | Reconnexion : rejeu des claims, apparitions, retraits, conducteurs à l'admission + resync de baseline | 2 | client tué puis relancé : `N claim(s) rejoué(s)`, aucun fantôme, objet absent des deux côtés |
| **P3** | Substrat de vérification : enregistreur + rejeu hors jeu, **toutes les assertions conservées** | 3–5 | une régression d'interpolation est détectée **hors du jeu**, en secondes |
| **P4** | Session réelle 30 min, deux joueurs, installation faite par le second | 2–3 | `0 exception`, `0 désync > 2 m`, sauvegarde + rechargement + reconnexion, verdict unique |
| **P5** | Découper `ServerNetwork` en cinq systèmes (présence, créatures, véhicules, pickup, spawn/despawn) | 5 | `verify.sh` identique, comportement inchangé, aucun nouveau message |
| — | **Gelé** : pipeline AssetBundles, énergie/modules de véhicule, nouvelles sondes, host migration, matchmaking | — | se rouvre après P4, sur décision explicite |

Ordonnancement : **P0 → P1 → P2 → P4** est le chemin critique (≈ 8–11 jours) ; **P1bis** et **P3** se font en
parallèle ; **P5** après, parce que découper un fichier qu'on va encore corriger coûte deux fois.

> **État au 2026-10-19** : P0 (nominal), P1 et P1bis sont **mesurés en jeu** (étapes P et N, §45.6) ;
> le refus P0 sur deux sauvegardes différentes reste hors jeu. **P2 est clos le 2026-10-20** : rejeu des
> retraits (§45), réclamations libérées et aucun fantôme (§52), volant tenu par un client tué libéré
> (§57, étape X). **P5 est fait au sens de son critère observable le 2026-10-20 (§58)** : découpage en
> cinq domaines, texte inchangé, `verify.sh` identique. **P3 est fait au sens de son critère le 2026-10-20
> (§59)** : rejeu hors jeu d'un flux réel, régression détectée en ~1 s. P4 reste à faire.

### 43.6 Critères d'arrêt

1. Après P1, **plus de 5 % de corrections** sur une nage normale → la validation a posteriori n'est pas viable :
   il faudra transporter les entrées (chantier d'une autre nature), et la revendication « autoritaire » doit
   être abandonnée pour le joueur.
2. Session de 30 min avec **plus de 3 désyncs > 2 m** ou **une exception par 10 min** → le problème n'est pas
   « il manque un message » : la simulation des deux mondes diverge structurellement, et la prémisse « deux
   instances chargent le même monde » doit être reposée.
3. Un rechargement qui **perd les clés d'entités** → l'identité par position/identifiant sérialisé n'est pas
   stable ; M6 (création, retrait, contenu) est bâti sur du sable.

### 43.7 Ce que cette revue ne prouve pas

- Les avis sont des raisonnements sur les documents et le code, **pas** des mesures : rien ici ne remplace un run.
- Le coût des étapes est une estimation, pas une mesure ; l'auteur est seul, et P5 peut doubler.
- La question « pourquoi ce projet » (apprendre, jouer entre amis, servir de base à des serveurs moddés) change
  la réponse sur le contenu : la revue tranche pour « jouer d'abord », et le dit.
- Le rejeu hors jeu n'est **pas** démontré faisable pour `RemoteAvatar`/équipement (dépendances Unity non
  mesurées) : P3 commence par les flux qui s'y prêtent, et son périmètre peut se réduire.

---

## 44. P0, P1 et P1bis : implémentation et preuves hors jeu (2026-10-19, mesures en jeu au §45.6)

**Verdict : les trois fermetures sont dans le code, en protocole v13, et elles sont vérifiables sans lancer
le jeu — 35 tests, 61 assertions de selftest, un déploiement qui détecte une DLL altérée. Aucune n'est encore
validée par un run en jeu, et c'est écrit tel quel.**

### 44.1 Ce qui a été ajouté

| Brique | Fichiers | Ce que ça change |
|---|---|---|
| Empreinte de sauvegarde (P0) | `Coop.Model/SaveIdentity.cs`, `HandshakeRequest.Save`, `Shared/SaveIdentitySource.cs`, `AutoLoadBehaviour` | le handshake transporte `slot + session + changeSet + mode` ; une partie différente, plus ancienne ou altérée est **refusée avec son motif** ; le serveur admet sous réserve pendant la course de démarrage, puis vérifie au tick |
| Garde de plausibilité (P1) | `Coop.Model/PlayerPlausibility.cs`, `PlayerCorrectionMessage` (#22), `ServerNetwork.HandlePlayerPosition`, `Coop.Client/Plugin.cs` | le serveur juge le déplacement contre la **dernière position acceptée**, refuse, renvoie la position acceptée, publie son taux de refus ; 3 refus consécutifs = resynchronisation, budget de 5/min au-delà duquel la session est éjectée ; le client applique la correction hors véhicule et au-delà de 0,75 m |
| Contrat de contenu (P1bis) | `Coop.Model/EntityDefinition.cs`, `EntitySpawnMessage.DefinitionHash/Scale`, `EntitySpawner`, sonde d'apparition | le serveur transporte la définition et son empreinte ; le client **recalcule** et refuse avant d'instancier ; la sonde prouve le contrôle en falsifiant une définition |
| Distribution (P0) | `README.md`, `tools/deploy.sh`, `tools/mod.manifest.json`, `SubnauticaCoop.sln` | construire, installer, vérifier une installation (sha256 + version de protocole) et lancer, sans lire le plan |

### 44.2 Ce qui est prouvé, et par quelle commande

| Vérification | Résultat relevé le 2026-10-19 |
|---|---|
| `dotnet build SubnauticaCoop.sln` | vert, 0 avertissement, 0 erreur (Model netstandard2.0, plugins net472, tests net9.0) |
| `dotnet test` | **35/35**, 0 échec, ~75 ms — identité de sauvegarde et ses douze cas de compatibilité, plausibilité (nominal, téléport, rafale, trou, véhicule, NaN), définition (canonical, quantification au mm, refus), messages (aller-retour, troncature, `definitionHash=0`) |
| `dotnet run --project tools/NetProbe -- selftest` | **61 assertions OK**, `SELFTEST: SUCCES`, exit 0 — dont `PlayerCorrection` et `EntitySpawn` avec hash |
| `tools/deploy.sh --game-dir=<temp>` | installe 4 DLL et écrit un manifeste (protocole v13, commit, sha256, tailles) |
| `tools/deploy.sh --verify` | conforme ; après avoir ajouté un octet à `Coop.Client.dll`, **exit 1** et le fichier fautif nommé |

### 44.3 Décisions à ne pas défaire

- **L'empreinte est une lignée, pas un hash de fichiers.** L'hôte sauvegarde pendant la session : un client
  tardif lit une sauvegarde plus récente que celle que le serveur a lue au démarrage. Un hash de fichiers
  refuserait toute reconnexion légitime ; `changeSet` client **≥** serveur est donc accepté, client plus
  ancien refusé.
- **Le client attend son empreinte, mais pas indéfiniment.** 20 s, puis il se connecte quand même et le refus
  est nommé — une attente infinie serait un blocage silencieux.
- **La garde est un anti-téléport, pas une autorité de simulation.** 15 m/s hors véhicule, 35 m/s en véhicule,
  2 m de tolérance. Un client qui ment sur `InSeamoth` obtient la borne véhicule : c'est une limite écrite,
  pas un oubli. Le mot « autoritaire » ne vaut pour le joueur que dans le sens « le serveur peut refuser ».
- **Une correction reçue en véhicule n'est pas appliquée.** Le siège appartient au véhicule ; téléporter le
  joueur le désolidariserait. Elle est comptée, pas ignorée.
- **Le hash de définition couvre une forme canonique quantifiée au millimètre**, pas les flottants bruts :
  deux runtimes (Mono du jeu, .NET des tests) doivent calculer le même hash. Un `ToString` culturel ferait
  échouer un hash correct.

### 44.4 Ce que ce chapitre ne prouve pas

- **Mis à jour le 2026-10-19** : l'application d'une correction, le chemin nominal de l'empreinte
  (étape P) et le contrat de contenu (étape N) sont **mesurés en jeu** (§45.6). Reste prouvé hors jeu
  seulement : le refus d'une sauvegarde **différente**.
- **Le taux de faux positifs de P1 n'existe pas encore** : la nage de 6 s de l'étape P a produit 0
  correction, mais une session de 30 min n'est pas mesurée. C'est le critère d'arrêt à 5 % qui tranchera.
- **Le contrat de contenu n'est pas un pipeline** : aucun AssetBundle, aucun prefab inconnu du client. Il
  vérifie ce que le client sait déjà résoudre.
- **La distribution n'a pas été testée sur une seconde machine** : le script a été exercé sur un dossier de
  jeu temporaire, pas par un autre humain qui installe et lance.
- **Le protocole passe à v13** : un client v12 est refusé au handshake (comportement voulu), et les deux
  plugins doivent être redéployés ensemble (`tools/deploy.sh`).

### 44.5 Prochain pas

1. **P2** (reconnexion : claims, apparitions, retraits, conducteurs) — entamé le 2026-10-19 : les retraits
   sont rejoués à l'admission (§45) ; restent les claims, les conducteurs et la resynchronisation de base.
2. **Valider P0/P1/P1bis en jeu** — P0 (nominal), P1 et P1bis sont mesurés par les étapes P et N, vertes le 2026-10-19 (§45.6) ; le refus P0 sur deux sauvegardes différentes reste à mesurer.
3. Ensuite seulement P4 (session de 30 min) puis P5 (découpage).

---

## 45. P2 (rejeu des retraits) et mesures en jeu de P0/P1/P1bis (2026-10-19)

**Verdict : P, Q et N ont tourné en jeu et sont vertes — 61 s, 124 s et 78 s (§45.6). P0 (chemin
nominal), P1, P1bis et le rejeu P2 sont donc mesurés ; le refus P0 sur deux sauvegardes différentes
reste prouvé hors jeu, et la reconnexion complète reste à faire.**

### 45.1 Rejeu des retraits (P2, première moitié)

| Brique | Fichier | Ce que ça change |
|---|---|---|
| Carnet de retraits | `Coop.Server/ServerNetwork.cs` (`RememberDespawn`, `_despawnedEntities`, `_despawnOrder`, `MaxRememberedDespawns = 256`) | un client admis reçoit les `EntityDespawn` survenus **avant** son arrivée, après le rejeu des apparitions ; sans ça il recharge la même sauvegarde et voit réapparaître l'objet ramassé ou le véhicule détruit |
| Cohérence des deux carnets | `RememberSpawn` / `BroadcastDespawn` | une clé recréée efface son ancien retrait (sinon le client créerait puis retirerait) ; une clé retirée sort du carnet d'apparitions ; la file de retraits ne contient jamais deux fois la même clé (sinon une éviction consommerait un créneau sans rien retirer) |

Ce qui reste de P2 et n'est pas fait : rejeu des **claims** de ramassage, des **conducteurs**, et
resynchronisation de **baseline** pour un client tué puis relancé. Le critère du §43.5 (`N claim(s)
rejoué(s)`, aucun fantôme) n'est donc pas atteignable aujourd'hui.

### 45.2 Sonde de position (`-coopdebug=position`)

`Coop.Client/PositionProbeBehaviour.cs`, dans **deux phases**, avec un verdict unique :

1. **nage nominale** — 30 pas de 0,25 m toutes les 0,2 s (1,25 m/s, 6 s) : critère `0 correction reçue` ;
2. **téléport** — 100 m maintenus 0,6 s image par image pour que l'envoi 20 Hz les émette : attente jusqu'à
   10 s d'une correction **reçue et appliquée**, puis restauration de la position d'origine.

La sonde refuse de conclure si le joueur est en véhicule (la correction n'y est pas appliquée, par décision
du §44.3) et échoue nommément si le monde, le joueur ou le handshake manque. Elle lit les compteurs réels
`CorrectionsReceived/Applied/Skipped` exposés par `ClientNetworkBehaviour`. **Limite** : elle n'affiche pas
l'écart en mètres de la correction appliquée — le gestionnaire réseau le journalise mais ne le stocke pas.

### 45.3 Étapes P et Q de `tools/verify.sh`

**P** — un client, hôte `-batchmode -nographics`, ~300 s : `save=slot=` et `empreinte de sauvegarde
acceptée` (P0 nominal), `[sonde] position : verdict=True`, `nage normale : 0 correction`, `téléport de
100 m : correction reçue=True, appliquée=True`, puis côté serveur `position REFUSÉE`, `RESYNCHRONISÉE` et
le taux publié.

**Q** — deux clients **séquentiels**, hôte `-coopdebug=despawn` avec `COOP_DESPAWN_CLIENTS=1` : la sonde
de l'hôte attend un seul client, fabrique le Seamoth, le détruit 20 s plus tard (`COOP_DESPAWN_DELAY`) ; le client 1 ne rend la
main qu'après son verdict, donc le client 2 démarre **après** le retrait. Les assertions portent sur le
rejeu (`[net] disparition : 1 entité(s) disparue(s) rejouée(s) pour …#2`), sur l'absence locale chez le
tardif (`véhicule 0x… absent localement`), sur son verdict, et sur l'**absence** de `[spawn] entité créée`
— un fantôme serait la régression que le carnet doit empêcher. C'est la première mesure du rejeu P2.

Le script compte désormais **dix-sept étapes (A–Q)** et **dix-huit runs** (K en compte deux).

### 45.4 Ce qui a été exécuté, et ce qui ne l'a pas été

| Vérification | Résultat |
|---|---|
| `dotnet build SubnauticaCoop.sln` | vert, 0 avertissement, 0 erreur (après les ajouts P2 et la sonde) |
| `dotnet test` | **35/35** (le modèle n'a pas changé depuis le §44.2) |
| `NetProbe selftest` | **61 assertions**, `SELFTEST: SUCCES`, exit 0 |
| `bash -n tools/deploy.sh tools/verify.sh tools/check-verify-assertions.sh` | OK |
| `tools/check-verify-assertions.sh` | vert : les **17 étapes déclarées** sont toutes acceptées par `--only` (le bug A–O ne peut plus revenir), les assertions P, Q et N acceptent les journaux au format du code, et **8 échecs nommés** apparaissent après retrait de cinq lignes exigées — satisfiables **et** discriminantes, sans lancer le jeu |
| `tools/verify.sh --only=P` | **vert, 61 s** : `verdict=True`, 0 correction en nage, téléport refusé, correction reçue et appliquée, empreinte acceptée |
| `tools/verify.sh --only=Q` | **vert, 124 s** : rejeu `1 entité(s) disparue(s) rejouée(s) pour 'killi'#2`, absence locale, `entités créées ici=0` chez le tardif |
| `tools/verify.sh --only=N` | **vert, 78 s** : `définition vérifiée=True (hash=0x3da1f4bf782becdf)`, `définition falsifiée refusée=True`, `définitions refusées=0` chez les deux clients |
| Run de reconnexion (client tué puis relancé) | **non exécuté** : aucune étape ne le couvre |

### 45.5 Ce que ce chapitre ne prouve pas

- **Le rejeu des retraits n'a aucun test automatique** : le carnet vit dans `ServerNetwork`, qui dépend
  d'Unity et de LiteNetLib. Sa preuve est le run de l'étape Q (vert le 2026-10-19), pas un test. Ce qui est vérifié hors jeu est
  plus étroit : `tools/check-verify-assertions.sh` extrait `expect`/`assert_p`/`assert_q`/`assert_n` de `verify.sh` et
  montre qu'elles acceptent les formats de journal du code, puis qu'elles échouent sur des lignes retirées
  — les assertions sont satisfiables et discriminantes, **pas** la mesure.
- **La sonde de position peut produire un faux négatif** : si le serveur n'a pas encore reçu la première
  position avant la nage, ou si le monde se charge lentement, elle échoue nommément plutôt que de conclure —
  c'est voulu, mais ça ne mesure pas la garde.
- **Le taux de refus de 5 % du §43.6 n'est toujours pas mesuré.** L'étape P imprime le taux, elle ne
  l'affirme pas sous 5 % : un taux élevé sur une nage de 6 s ne dirait rien d'une session de 30 min. C'est
  P4 qui tranchera.

### 45.6 Mesures en jeu du 2026-10-19 : P, Q et N sont vertes

**P** (`étape P : OK (61s)`) — hôte dédié, un client, sonde `-coopdebug=position` :

```
[sonde] position : verdict=True — nage normale : 0 correction sur 6.0s (seuil 0),
téléport de 100 m : correction reçue=True, appliquée=True (2 appliquée(s))
```

L'étape affirme aussi, côté serveur, `position REFUSÉE … correction renvoyée`, `RESYNCHRONISÉE` et le
taux publié, ainsi que `save=slot=…` + `empreinte de sauvegarde acceptée` : le chemin nominal de P0 est
mesuré, pas seulement testé. Le refus d'une sauvegarde **différente** reste prouvé hors jeu (douze cas).

**Q** (`étape Q : OK (124s)`) — deux clients séquentiels, `COOP_DESPAWN_CLIENTS=1` :

```
[net] disparition : 1 entité(s) disparue(s) rejouée(s) pour 'killi'#2 (arrivé après leur retrait).
[disparition] retrait de 0x7bc634c3cde1d338 mis en attente — le monde n'est pas encore chargé
[disparition] véhicule 0x7bc634c3cde1d338 absent localement — rien à retirer
[sonde] disparition : verdict=True — … cette instance ne l'a jamais eu, et ne l'a pas recréé … entités créées ici=0
```

C'est la première preuve que le carnet de retraits (P2) sert à quelque chose : un client qui n'était pas
là à la destruction ne voit ni fantôme ni objet à réclamer. Le défaut `LastRemovedKey` trouvé en écrivant
Q aurait rendu cette ligne rouge ; il est corrigé et **exercé** par ce run.

**N** (`étape N : OK (78s)`) — hôte `-coopdebug=spawn`, deux clients dont un arrivé après la fabrication :

```
[sonde] apparition : verdict=True — entités créées=1, écartées (déjà présentes)=0, échecs=0, clés divergentes=0 ;
objet local retrouvé par sa clé=True ; définition vérifiée=True (hash=0x3da1f4bf782becdf),
définition falsifiée refusée=True, définitions refusées=0
```

Les deux clients, y compris celui arrivé après le `spawn`, vérifient donc la définition **et** refusent
une définition falsifiée, sans qu'aucune définition réelle ne soit refusée (`définitions refusées=0`).

Ce que ces trois runs ne prouvent pas : le refus P0 sur deux sauvegardes différentes, le taux de faux
positifs sur 30 min, et la reconnexion complète.

> **Correctif de harnais trouvé en lançant P** : la validation de `--only` n'acceptait que A–O, donc les
> étapes P et Q étaient **inatteignables** (`étape inconnue`). Corrigé (`[!ABCDEFGHIJKLMNOPQ,]`) avant le
> run ; sans cet essai, les deux étapes seraient restées « écrites » sans jamais pouvoir s'exécuter.
- **P2 n'est pas « la reconnexion »** : un client relancé ne récupère ni ses claims, ni un volant, ni une
  baseline ; il ne reçoit que les retraits et les apparitions mémorisés.
- **L'étape Q ne prouve pas un retrait « avant chargement » de façon garantie** : selon le moment où le
  handshake tombe, le retrait est mis en file ou appliqué tout de suite. L'assertion porte donc sur
  l'**effet** (`absent localement`, verdict, pas de fantôme), pas sur un chemin de file précis.
- **Un défaut trouvé en écrivant Q, et corrigé** : la branche « l'entité est absente localement » de
  `EntityDespawner` n'affectait pas `LastRemovedKey`, donc la sonde de disparition concluait **False**
  (`key != 0` faux) précisément dans le cas que P2 doit rendre vert. La clé est maintenant mémorisée dans
  les deux branches ; aucun run ne l'a encore exercé.

## 46. Batterie complète rejouée le 2026-10-19 : deux faux négatifs de sonde corrigés

La batterie des dix-sept étapes a été rejouée après l'ajout de la garde de position (P1) et du contrat de
contenu (v13). Deux étapes sont tombées — **B** (présence) et **L** (ramassage) — et ni l'une ni l'autre
n'était un défaut du protocole : dans les deux cas, la **sonde** mesurait autre chose que ce qu'elle
affirmait. Les deux causes sont mesurées, corrigées, et les étapes rejouées vertes.

### 46.1 B — l'hôte compté comme un pair

La sonde de présence attendait `KnownRemoteCountForProbe` (tout joueur distant) avant de commencer ses
mesures mutuelles. Avec un hôte **non dédié**, l'avatar `HoteServeur` (id 9999) satisfaisait cette attente ;
la sonde du client 1 a donc conclu **13 s** après son entrée dans le monde — `PlayerId falsifié=False (défaut :
aucun joueur distant)`, `nage=False` — pendant que le client 2 chargeait encore son monde. Mesure du
2026-10-19 02:00 : `[sonde] présence : verdict=False` à 01:57:55, client 2 connecté à 01:58:00.

Correctif : `RemotePlayerManager.KnownPeerCountForProbe` (hôte exclu) et l'attente porte dessus. Le drapeau
« non exercé = défaut » continue de compter **tous** les distants, donc un run où l'hôte est seul échoue
toujours : la discrimination n'est pas affaiblie.

Preuve (`étape B : OK (119s)`, rejouée seule) :

```
[sonde] présence : 1 autre(s) client(s) connu(s) (2 joueur(s) distant(s) au total, hôte compris)
[sonde] présence : PlayerId falsifié — 'killi' a annoncé [Tank, Fins] sous l'id 1 (le mien) ; le relais
                   me l'a diffusé sous l'id 2 … -> verdict=True
[sonde] présence : nage puis arrêt vus par 'killi' (id=2) — vitesse max 2.60 m/s, déplacement 11.38 m,
                   avancées vues=54, écart à l'arrêt max=0.00 m -> verdict=True
```

### 46.2 L — téléportation refusée par P1, et client 2 arrivé après la réclamation

Cinq assertions rouges, deux causes :

1. **Le second client n'était pas encore là.** Client 1 a réclamé à 12:37:11 ; le client 2 s'est connecté à
   12:37:20. Le serveur a donc écrit `annoncée à 0 client(s)`, et le client 2 n'a pu ni voir le retrait ni
   observer l'accord.
2. **La mise en place téléportait le joueur.** `PlaceNear` faisait un `SetPosition` de 16 m en une image ;
   la garde P1 refuse (≈320 m/s), resynchronise le client, et le joueur restait à **15.99 m** de l'objet —
   la réclamation à id falsifié était alors refusée « hors portée (16.0 m > 6.0 m) », un refus de distance
   qui ressemble à s'y méprendre à une règle d'identité respectée.

Correctifs : la sonde du client 1 attend l'**avatar du pair** (90 s) avant sa première réclamation ; et
`PlaceNear` approche par pas de 1,5 m toutes les 0,15 s (10 m/s, sous les 15 m/s admis), en journalisant le
nombre de pas et en **avertissant** si la portée n'est pas atteinte après l'approche.

Preuve (`étape L : OK (94s)`) :

```
[net] disparition : clé=0x10ee181a0699267d (objet) — annoncée à 1 client(s), motif « ramassée par 'killi'#1 »
[sonde] ramassage : joueur placé à 1.74 m de l'objet (portée admise par le serveur : 6.0 m ; approche en 11 pas)
[sonde] ramassage : verdict=True — id=2, clé partagée … accordée=False tenue par 1 ; clé inventée … refusée ;
                    id falsifié 0xbd613fd07fc6fef1 reçue=True accordée=True tenue par 2 revendiquée sous l'id 1
```

### 46.3 Ce que la batterie ne prouve toujours pas

- Le refus P0 sur **deux sauvegardes différentes** (tests hors jeu seulement).
- Le taux de faux positifs de P1 sur 30 min (seuil 5 %, §43.6).
- La **reconnexion complète** : claims, conducteurs, baseline.
- La batterie n'a pas tourné en **un seul** passage : A–J dans une passe interrompue (B rouge, corrigée et
  rejouée), puis N, O, Q, M, L, I, P, D, puis H, K, J. Chaque étape a son verdict vert, mais le temps total
  d'un passage unique n'est pas mesuré.

### 46.4 Ce que la batterie sait maintenant refuser

L'étape B affirme en plus que la sonde a attendu **un autre client** (`autre(s) client(s) connu(s)`) : si
l'avatar de l'hôte redevenait un pair acceptable, l'étape échouerait en nommant la cause au lieu d'attendre
un `verdict=False` tardif. `tools/check-verify-assertions.sh` couvre désormais B (fixture + retrait de la
ligne) en plus de P, Q, N et R : **douze** échecs nommés après retrait de **neuf** lignes exigées (relevé le
2026-10-20), et les **dix-huit** étapes déclarées restent toutes acceptées par `--only`.

---

## 47. Durcissement du modèle et rejeu ciblé en jeu (2026-10-19, session autonome)

**Verdict : le modèle pur a été durci sans changer les hashes normaux, et sept étapes critiques ont été
rejouées vertes après les changements — P, Q, N, B, L, O et D.**

### 47.1 Correctifs de fond (hors jeu)

| Défaut | Correctif | Preuve |
|---|---|---|
| `PacketReader(offset, length)` acceptait un `offset + length` qui débordait un `int` | contrôle par soustraction `offset > data.Length - length` | 2 000 fenêtres aléatoires + cas `int.MaxValue` |
| `ReadFloat` allouait un tableau de 4 octets **par float** | tampon `[ThreadStatic]` | 200 000 lectures < 4 Ko alloués ; 4 threads × 20 000 valeurs sans mélange |
| Une séquence UTF-8 invalide devenait silencieusement U+FFFD | décodeur strict, `DecoderFallbackException` → `ProtocolException` | overlong, continuation isolée, séquence tronquée refusés ; accents/emoji acceptés |
| `SaveCompatibility` ne vérifiait que le hash **client** | hash serveur vérifié aussi, motif distinct | test de champ serveur altéré |
| `SaveIdentity.Canonical` pouvait collisionner (`slot="a\|b"/session="c"` vs `slot="a"/session="b\|c"`) | échappement de `\`, `\|`, `\n`, `\r` ; valeurs usuelles inchangées | forme canonique normale épinglée |
| `ModListHash` collisionnait sur `["a\nb"]` vs `["a","b"]` | échappement des séparateurs après tri brut ; hash normal `d8132ec29283184a` inchangé | 10 tests dédiés |
| `EntityDefinition.Milli` quantifiait en `int` : deux échelles opposées > 2 147 km collisionnaient | quantification en `long`, saturation signée | hash normal `0x3da1f4bf782becdf` inchangé (étape N) |
| `EntityDespawn` n'appliquait pas la règle documentée « clé 0 refusée » | refus à l'écriture **et** à la lecture | paquet forgé testé |
| `PickupResult.Write` acceptait un `TechTypeName` que la lecture refusait | même borne de 64 caractères des deux côtés | écriture et lecture testées |
| `PlayersSnapshot` acceptait jusqu'à 255 joueurs alors que `MaxClients = 10` | plafond `MaxClients` des deux côtés | écriture et lecture forgée |
| `PlayerPlausibility` rejetait sans motif une position précédente non finie, et laissait passer un `NaN` d'intervalle | refus nommés ; changement Seamoth → Exosuit traité comme saut légitime | 8 tests de bornes + 2 tests d'intervalles infinis |
| `LaunchModeParser` acceptait `-coopmodeevil=server` (préfixe seul) | nom exact suivi de `=` | 4 cas négatifs |
| `MessageCodec.Encode(null)` levait une `NullReferenceException` | `ArgumentNullException` | test dédié |
| `EntitySpawnMessage.Write` acceptait `DefinitionHash=0` et des coordonnées `NaN`/∞ que la lecture refusait | mêmes refus à l'écriture : hash nul, position/quaternion/échelle non finis | 3 tests d'écriture + paquet forgé |
| `EntitySpawnMessage` acceptait la clé 0, alors qu'une telle entité ne peut jamais être retirée ni réclamée (les deux messages refusent 0) | clé 0 refusée à l'écriture **et** à la lecture | paquet forgé + 2 tests |
| `PlayerCorrectionMessage.Write` acceptait une position `NaN`/∞ que la lecture refusait | même contrôle `EnsureFinite` des deux côtés | 2 tests (NaN, -∞) |
| `PacketWriter.WriteString` remplaçait un surrogate isolé par U+FFFD : le message relu n'était pas celui émis | encodage UTF-8 strict, `ProtocolException` à l'écriture | surrogate refusé, emoji accepté |
| Un paquet réduit à son opcode pouvait lever autre chose qu'une `ProtocolException` sans qu'aucun test ne le vérifie sur les 22 messages | test fuzz dédié : les 22 lecteurs refusent explicitement | 1 test, 22 lecteurs |

### 47.2 Contrôles hors jeu au moment du résumé

| Commande | Résultat |
|---|---|
| `dotnet test SubnauticaCoop.sln` | **260/260**, 0 échec, 0 avertissement |
| `dotnet run --project tools/NetProbe -- selftest` | **94/94**, `SELFTEST: SUCCES`, exit 0 |
| `tools/check-verify-assertions.sh` | vert (17 étapes acceptées, P/Q/N/B discriminantes) |
| builds `Coop.Client` + `Coop.Server` (Release) | 0 avertissement, 0 erreur |
| `tools/deploy.sh --no-build` + `--verify` (dossier temporaire) | manifeste v13, sha256 et tailles conformes |

### 47.3 Rejeu en jeu après les correctifs

| Étape | Résultat | Ce qu'elle vérifie |
|---|---|---|
| **P** | `OK (63s)` | handshake + empreinte acceptée, nage sans correction, téléport de 100 m refusé puis correction appliquée |
| **Q** | `OK (100s)` | retrait rejoué au client tardif, `absents localement=1`, `entités créées ici=0` |
| **N** | `OK (75s)` | `définition vérifiée=True (hash=0x3da1f4bf782becdf)`, falsifiée refusée, chez les deux clients |
| **B** | `OK (105s)` | présence des deux clients (`verdict=True` des deux côtés) avec le plafond `PlayersSnapshot` |
| **L** | `OK (94s)` | ramassage accordé/refusé, id falsifié réattribué, objet retiré par disparition |
| **O** | `OK (121s)` | véhicule détruit chez l'hôte : retiré chez les deux clients, aucune réapparition |
| **D** | `OK (50s)` | garde cliente : `sauvegarde cliente bloquée (entrée+écriture) -> verdict=True`, aucun `verdict=False` |

Aucune de ces étapes n'a produit de `verdict=False`, et chaque run a arrêté ses propres instances.

### 47.4 Limites réaffirmées

- **P1 attrape un téléport, pas un speedhack progressif** : à 20 Hz, 2 m de tolérance par message
  autorisent 40 m/s. C'est écrit dans `docs/authority.md` et `docs/protocol.md`.
- Le refus P0 sur **deux sauvegardes différentes** reste prouvé hors jeu.
- La batterie complète n'a **pas** été rejouée en un seul passage dans cette session ; sept étapes
  critiques l'ont été, les dix autres gardent leur verdict du 2026-10-19 (§46).

## 48. Symétrie écriture/lecture du codec (2026-10-20, session autonome)

La session précédente (§47) avait fermé les refus **à la lecture**. Restait une classe de défauts
inverse : un message que le serveur pouvait **écrire** et qu'aucun client n'aurait accepté. Un tel
message ne produit pas d'erreur visible côté émetteur — l'entité n'existe simplement jamais chez les
autres. Les correctifs ci-dessous alignent l'écriture sur la lecture, et les tests vérifient les deux
sens.

### 48.1 Correctifs

| Défaut | Correctif | Preuve |
|---|---|---|
| `EntitySpawnMessage.Write` acceptait `DefinitionHash=0` | refus nommé à l'écriture, comme à la lecture | 1 test + contrôle `NetProbe` |
| `EntitySpawnMessage.Write` acceptait une position, un quaternion ou une échelle `NaN`/∞ | `EnsureFinite` appliqué des deux côtés | 2 tests + contrôle `NetProbe` |
| `EntitySpawnMessage` acceptait la clé `0` : une telle entité ne peut être ni retirée ni réclamée, puisque `EntityDespawn` et `PickupClaim` refusent `0` | clé `0` refusée à l'écriture **et** à la lecture | 2 tests (dont paquet forgé) + 2 contrôles `NetProbe` |
| `PlayerCorrectionMessage.Write` acceptait une position `NaN`/∞ | même contrôle qu'à la lecture | 2 tests + 1 contrôle `NetProbe` |
| `PacketWriter.WriteString` remplaçait un surrogate isolé par U+FFFD : le message relu n'était pas celui émis | encodage UTF-8 strict à l'écriture | 2 tests (surrogate refusé, emoji accepté) |
| Aucun test ne vérifiait qu'un paquet réduit à son opcode est refusé par **tous** les lecteurs | test dédié sur les 22 messages, `ProtocolException` uniquement | `MessageFuzzTests` |

Décision assumée : la clé `0` est désormais refusée pour `EntitySpawn` comme elle l'était pour
`EntityDespawn`. Aucun chemin du serveur ne produit `0` (les clés viennent de `VehicleIdentity` et de
`ItemIdentity`) ; le refus ferme un cas limite plutôt qu'une fonctionnalité.

### 48.2 Contrôles hors jeu

| Commande | Résultat |
|---|---|
| `dotnet test SubnauticaCoop.sln` | **260/260**, 0 échec (Debug et Release) |
| `dotnet run --project tools/NetProbe -- selftest` | **94/94**, `SELFTEST: SUCCES`, exit 0 |
| builds `Coop.Client` + `Coop.Server` | Debug et Release, 0 erreur |
| `tools/check-verify-assertions.sh` | vert (17 étapes acceptées) |
| `tools/deploy.sh` dans un dossier temporaire | manifeste v13, sha256 et tailles conformes |

### 48.3 Ce que cette session ne prouve pas

- Aucune étape de `tools/verify.sh` n'a été rejouée en jeu : les verdicts datent du 2026-10-19 (§46,
  §47.3). Les correctifs sont hors jeu, sur le modèle pur, et n'ajoutent aucun chemin de jeu.
- Le refus P0 sur deux sauvegardes différentes, le taux de faux positifs P1 sur 30 minutes, la
  reconnexion complète et le contenu AssetBundle restent non mesurés — inchangé depuis §47.4.

### 48.4 Bornes du handshake, identifiant d'entité, et un pseudo trop long

Trois champs restaient sans borne **à l'entrée du protocole**, alors qu'ils viennent d'un pair encore
inconnu ou d'un identifiant dont dépend la correspondance d'entité :

| Défaut | Correctif | Preuve |
|---|---|---|
| `HandshakeRequest.GameVersion` et `ModListHash` acceptaient jusqu'à 64 Ko chacun, comparés puis refusés par le serveur | constantes `MaxGameVersionLength=32` et `MaxModListHashLength=64`, refusées à l'écriture **et** à la lecture | 5 tests + 2 contrôles `NetProbe` |
| Un pseudo Steam de plus de 32 caractères faisait refuser le handshake (« pseudo trop long ») : le joueur ne pouvait pas se connecter | `NormalizeUsername` tronque à `MaxUsernameLength`, sans couper une paire de surrogates, et le journalise | build client rc=0 |
| `EntitySpawnMessage` acceptait `UniqueId` vide : le client s'en sert comme `UniqueIdentifier` pour retrouver l'entité dans sa sauvegarde, donc une chaîne vide ne désigne rien | identifiant vide refusé à l'écriture **et** à la lecture | 2 tests + 2 contrôles `NetProbe` |

Contrôles hors jeu après ces ajouts : `dotnet test` **260/260**, `NetProbe selftest` **94/94**.

### 48.5 Zéro allocation à l'écriture, culture, et mémoire du retrait côté client

| Défaut | Correctif | Preuve |
|---|---|---|
| `PacketWriter.WriteFloat` appelait `BitConverter.GetBytes`, soit 4 octets alloués **par float** — des milliers d'allocations par seconde sur un snapshot complet | union explicite `FloatBits` et écriture little-endian par décalages, sans allocation | test de motifs de bits + 100 000 écritures avec **0 octet** alloué ; contrôle `NetProbe` |
| Aucun test ne vérifiait que les empreintes sont indépendantes de la culture de la machine, alors qu'elles sont recalculées des deux côtés | tests sous `tr-TR`, `fr-FR`, `ar-SA` pour `EntityDefinition`, `SaveIdentity` et `ModListHash`, plus forme canonique épinglée | 8 tests |
| Le registre client `EntityDespawner.Removed` grandissait sans borne pendant une session (une entrée par objet ramassé) | registre borné à **256** clés, éviction FIFO, comme le carnet du serveur ; la règle « une apparition tardive ne recrée rien » est conservée | build client rc=0 |

### 48.6 Pseudo normalisé et motifs bornés

| Défaut | Correctif | Preuve |
|---|---|---|
| La normalisation du pseudo vivait dans `Plugin.cs` (Unity, non testée) et le serveur recopiait `request.Username` brut dans les journaux, `PlayerJoined` et le nom du clone d'avatar | `Usernames.Sanitize` dans `Coop.Model` : trim, caractères de contrôle retirés, repli `joueur`, troncature à 32 sans couper une paire de surrogates ; utilisé par le client **et** le serveur | 10 tests |
| `HandshakeResponse.Reason`, `Disconnect.Reason` et `PlayerLeft.Reason` n'avaient aucune borne : un pair pouvait faire transporter 64 Ko de texte dans un champ journalisé | `Protocol.MaxReasonTextLength=160` appliqué à l'écriture **et** à la lecture via `MessageText.Reason` | 4 tests + 2 contrôles `NetProbe` |
| `PacketReader.Rewind`, `Remaining` et `ReadRemainingBytes` n'étaient exercés par aucun test direct | 8 tests de primitives (décalage de départ, reste exact, copie, lecture après la fin) | `dotnet test` |

Contrôles hors jeu à la clôture : `dotnet test` **260/260**, `NetProbe selftest` **94/94**.

## 49. Preuve en jeu du refus P0 : deux sauvegardes différentes (2026-10-20, mesurée)

### 49.1 Le trou, et pourquoi il existait

P0 promet que deux instances qui n'ont pas la même sauvegarde **ne se parlent pas**. Le chemin nominal
était mesuré (étape P : `save=slot=…` puis `empreinte de sauvegarde acceptée`), et le refus était couvert
par `SaveIdentityTests` et `SaveIdentityWireLimitTests`. Mais aucun run ne le prononçait : `AutoLoadBehaviour`
chargeait **la première sauvegarde chargeable**, donc les deux instances ouvraient forcément la même partie.
Le refus n'était vrai que dans le modèle.

### 49.2 Ce qui a été ajouté

| Élément | Rôle | Où |
|---|---|---|
| `SaveSlotPreference` | lit `-coopsave=slot0001` ou `COOP_SAVE_SLOT`, nom exact, valeur validée (lettres/chiffres/`-`/`_`, ≤ 64) ; une valeur explicite invalide donne `null` **sans repli** | `src/Coop.Model/SaveSlotPreference.cs`, 17 tests |
| `AutoLoadBehaviour` | choisit le bouton de chargement dont `saveGame` correspond, journalise `sauvegarde demandée : 'slot0001'` puis `trouvée`, et **s'arrête au menu** si elle est absente | `src/Shared/AutoLoadBehaviour.cs` |
| `run-session.sh --client-2-args` | arguments des clients 2+ (le client 1 garde les siens et sert de contrôle) | `tools/run-session.sh` |
| étape **R** | copie `slot0000` → `slot0001`, lance l'hôte + deux clients, exige le refus nommé des deux côtés et l'admission du client 1 | `tools/verify.sh` (`assert_r`) |
| `check-verify-assertions.sh` | vérifie que les assertions R acceptent le format du code **et** échouent quand on retire les lignes du refus (12 échecs nommés sur neuf lignes retirées) | `tools/check-verify-assertions.sh` |
| ordre déterministe des slots | `FindObjectsOfType` ne garantit aucun ordre : le premier run a chargé `slot0001` au lieu de `slot0000` partout, donc aucune divergence. Les boutons sont triés par nom de slot, et l'étape R nomme explicitement le slot de chaque rôle | `src/Shared/AutoLoadBehaviour.cs`, `tools/verify.sh` |

### 49.3 Procédure

```bash
# prérequis : jeu installé, fixture slot0000, Git Bash
tools/verify.sh --only=R          # ~2 min, deux clients
tools/verify.sh --only=R --keep-logs   # garde les journaux dans verify-logs/R/
```

Ce que la commande fait, dans l'ordre : copie `SNAppData/SavedGames/slot0000` vers `slot0001` (seulement
si `slot0001` n'existe pas déjà — un slot existant n'est jamais modifié), lance l'hôte sur `slot0000`, le
client 1 sans préférence (contrôle), puis le client 2 avec `-coopsave=slot0001`. À la fin, les instances
sont arrêtées **avant** la suppression de la copie.

### 49.4 Ce que l'étape exige

- client 2 : `[monde] sauvegarde demandée : 'slot0001'`, puis `sauvegarde demandée 'slot0001' trouvée`,
  puis `save=slot='slot0001'` dans le handshake ;
- serveur : `handshake REFUSÉ pour … : sauvegarde différente (client='slot0001', serveur='slot0000')` ;
- client 2 : `handshake REFUSÉ par le serveur : sauvegarde différente (client='slot0001', serveur='slot0000')` ;
- client 1, **même run** : `handshake ACCEPTÉ` — sans ce contrôle, une panne générale du handshake ferait
  « réussir » le refus ;
- client 2 : **aucun** `handshake ACCEPTÉ`.

### 49.5 Mesure du 2026-10-20 : étape verte

`tools/verify.sh --only=R` : **`étape R : OK (62s)`**, `VERDICT GLOBAL : tout est vert`, exit 0.
Lignes obtenues (horodatages retirés) :

```
client 1  [monde] sauvegarde demandée : 'slot0000'
          [monde] sauvegarde demandée 'slot0000' trouvée
          [net] handshake envoyé (protocole v13, jeu=1.22.83031, save=slot='slot0000' session='aae68629-…' changeSet=83031 mode=Creative hash=0xd2ec243fe410e147)
          [net] handshake ACCEPTÉ (id=1, tick serveur=553)
serveur   [net] empreinte de sauvegarde acceptée : client=slot='slot0000' … · serveur=slot='slot0000' … hash=0xd2ec243fe410e147
          [net] handshake REFUSÉ pour 'killi' : sauvegarde différente (client='slot0001', serveur='slot0000')
client 2  [monde] sauvegarde demandée : 'slot0001'
          [monde] sauvegarde demandée 'slot0001' trouvée
          [net] handshake envoyé (… save=slot='slot0001' … hash=0x7ea8cb612149479e)
          [net] handshake REFUSÉ par le serveur : sauvegarde différente (client='slot0001', serveur='slot0000')
```

Le **premier** run (avant correctif) a échoué pour une raison utile : `GetActiveSlotNames` rendait
`[slot0000, slot0001]`, mais `FindObjectsOfType` a rendu les boutons dans l'autre ordre, donc le serveur
**et** le client 1 ont chargé `slot0001`. Le client 2, lui aussi sur `slot0001`, a été **admis** — et
l'assertion `expect_missing` l'a dit nommément au lieu de laisser passer un vert. C'est le défaut que le
run devait révéler : sans ordre déterministe, « la même sauvegarde » n'était pas garantie. Corrigé par le
tri par nom de slot et par des slots explicites dans l'étape.

### 49.6 Ce que l'étape ne prouve pas

- Elle compare des **slots** : la copie conserve `sessionId`, `changeSet` et le mode, donc le motif exercé
  est la comparaison de slot. Les refus sur session, mode et `changeSet` plus ancien restent prouvés hors
  jeu seulement.
- Une copie de sauvegarde n'est pas une seconde **partie jouée** : si le jeu refusait de lister un dossier
  copié, le journal le dirait (`sauvegarde demandée 'slot0001' introuvable`) et l'étape échouerait — le
  test ne peut pas passer pour la mauvaise raison.
- Elle ne remplace pas les tests de `SaveCompatibility` : elle exerce **un** chemin (slot), pas les combinaisons
  session/mode/`changeSet`.
- Elle dépend de la présence de `slot0000` : sur une installation sans cette fixture, l'étape échoue avec
  `sauvegarde de référence absente` plutôt que de mesurer autre chose.

## 50. Reconnexion mesurée : ce qui tient, et la réclamation du mort (2026-10-20)

### 50.1 Protocole de la mesure

Deux clients avec la sonde de ramassage, hôte dédié ; le client 2 est **tué** (`taskkill`) pendant la
session, puis **relancé** avec les mêmes arguments. Journaux copiés avant/après chaque étape.

```
tools/run-session.sh --clients=2 --together --keep \
  --client-args="-coopmode=client -coopdebug=pickup" \
  --client-2-args="-coopmode=client -coopdebug=pickup"
# kill du PID du client 2, attente de la déconnexion, relance, attente de la sonde, arrêt
```

### 50.2 Ce qui tient

| Attendu (P2) | Relevé |
|---|---|
| `PlayerLeft` diffusé au survivant | `[net] 'killi#2' déconnecté (raison=Timeout, pings=32, positions=534)` puis `PlayerLeft diffusé pour 'killi' (id=2)` |
| nouvelle session admise sans fantôme | `handshake ACCEPTÉ : 'killi' (id=3)` ; chez le survivant `joueursDistants=1 avatarsActifs=1` (pas d'avatar id=2 résiduel) |
| retraits rejoués à l'arrivant | `[net] disparition : 2 entité(s) disparue(s) rejouée(s) pour 'killi'#3` |
| avatar du nouveau complet | `[presence] équipement appliqué sur l'avatar de 'killi' (id=3)`, `objet tenu appliqué … (rien en main)` |

### 50.3 Ce qui ne tenait pas : la réclamation du joueur mort

Première mesure (avant correctif), après relance :

```
[net] pickup : clé=0xbd613fd07fc6fef1 'AcidMushroom' réclamée par 'killi'#3
      -> REFUSÉE (déjà tenue par id 2)
```

L'objet avait pourtant été **retiré du monde serveur** quand `#2` l'avait ramassé. La carte
`_pickupClaims` gardait l'id du joueur parti : la clé restait refusée **au nom d'un joueur qui n'existe
plus**, avec un motif faux (`déjà tenue`), et la carte grossissait d'une entrée par objet ramassé, pour
toujours. Un simple `Alt+F4` suffisait donc à rendre une clé définitivement inexplicable.

**Correctif** (`src/Coop.Server/ServerNetwork.cs`, `OnPeerDisconnected`) : les réclamations du joueur qui
part sont libérées, avec le compte journalisé :

```
[net] pickup : 1 réclamation(s) libérée(s) par la déconnexion de 'killi'#2 — les clés redeviennent réclamables.
```

Deuxième mesure, même scénario : la ligne ci-dessus apparaît, **zéro** occurrence de `déjà tenue par id 2`,
et la réclamation de `#3` sur la clé retirée reçoit désormais le motif exact :
`REFUSÉE (objet inconnu du serveur (436 objet(s) indexé(s)) — la clé ne désigne rien dans le monde simulé)`.

### 50.4 Ce que la mesure ne prouve pas

- La sonde de ramassage n'est pas une sonde de reconnexion : après relance elle rend `verdict=False` parce
  qu'elle **attend un accord** sur un objet que la session précédente avait déjà pris. Le serveur, lui, dit
  la vérité (`objet inconnu`). Il faudra une sonde dédiée pour un verdict vert de reconnexion.
- Aucun volant n'était tenu pendant ce run : la libération des conducteurs à la déconnexion reste couverte
  par le code et l'étape M, pas par cette mesure.
- Ce n'est pas la session de 30 minutes (P4) : ~2 min par phase, deux clients, une seule reconnexion.

**État P2 après cette mesure : la reconnexion n'est plus « non mesurée » ; elle est mesurée, un défaut
réel a été trouvé et corrigé.** Restent ouverts : une sonde de reconnexion avec verdict, un run avec
véhicule, et la session longue.

## 51. Session autonome du 2026-10-20 : le registre de ramassage devient testable (soir)

Rien de nouveau dans le protocole : trois défauts de **couverture**, pas de conception.

### 51.1 Le correctif P2 n'était couvert par aucun test

La libération des réclamations à la déconnexion (`PLAN.md` §50.3) n'avait qu'une preuve : un run manuel
de deux minutes, avec un kill. Un `Dictionary<long, ushort>` dans `ServerNetwork` ne se teste pas hors jeu.

Extrait dans `src/Coop.Model/PickupClaimLedger.cs` (classe pure : `Claim`, `TryGetHolder`, `Release`,
`KeysOf`, `Count`), consommé par `ServerNetwork`. Le serveur garde la résolution du monde, la portée, les
journaux et la clé `0` ; le registre ne porte que la **décision**.

`tests/Coop.Model.Tests/PickupClaimLedgerTests.cs` — neuf cas, dont ceux qui auraient attrapé le défaut :

| Cas | Ce qu'il fixe |
|---|---|
| première réclamation d'une clé libre | `GrantedFirst`, détenteur enregistré |
| clé tenue par un autre | `RefusedOtherHolder`, détenteur **inchangé** |
| même joueur, même clé | `GrantedSameHolder`, une seule entrée |
| `Release` du joueur parti | rend **2**, laisse la clé du survivant, les deux autres redeviennent libres |
| clé libérée puis réclamée par un autre | `GrantedFirst` pour le nouveau, l'ancien détenteur a disparu |
| `Release` d'un joueur sans clé | rend **0**, ne touche à rien |
| `KeysOf` | rend les clés du joueur demandé, pas celles des autres |

### 51.2 Un writer réutilisé après une écriture refusée

`MessageCodec.Encode` commence par `writer.Reset()`, donc un message qui lève au milieu de son écriture ne
corrompt pas le suivant — mais rien ne le vérifiait. Test ajouté : `EntityDespawn` clé 0 (refusée) puis
`Pong` sur le **même** writer ; les octets sont identiques à un encodage neuf, le message se relit, zéro
octet restant.

### 51.3 Une étape que le défaut de `verify.sh` sautait

`tools/verify.sh` annonçait « les dix-huit étapes », mais ses deux messages d'usage et son défaut
`${ONLY:-…}` s'arrêtaient à **Q**. Un run sans `--only` — le cas normal — **n'exécutait donc jamais R**.
Corrigé (`…P,Q,R`) et vérifié par `bash -n` ; `README.md` dit désormais que le défaut lance les dix-huit.

### 51.4 Ce qui n'est pas fait

- Le registre n'est pas encore utilisé pour les **collisions de clé** (deux objets partageant une clé) :
  c'est toujours compté, pas arbitré (§ M5).
- Aucun nouveau run en jeu : le comportement du serveur est identique (même décision, même journal), seule
  la **localisation** du code change. La preuve d'intégration reste le run §50.3.
- La reconnexion complète reste ouverte : sonde dédiée avec verdict, volant tenu pendant le kill, baseline.

### 51.5 Un opcode ne peut plus échapper au harnais de fuzz

`MessageFuzzTests` avait une liste de 22 lecteurs et un test qui affirmait « 22 ». Un vingt-troisième
message ajouté au protocole serait donc passé sans jamais être lu par le harnais. Le nouveau test compare
les **noms** de `MessageType` aux noms de la liste : toute addition, tout renommage ou toute entrée
orpheline casse la suite (`dotnet test` **280/280**).

### 51.6 Le défaut sauté est désormais un échec de script

`tools/check-verify-assertions.sh` comparait `--only` aux étapes déclarées, mais pas la liste **par
défaut** — d'où le défaut du jour. Il extrait maintenant chaque `${ONLY:-…}` de `verify.sh` et exige
l'égalité avec les étapes déclarées : sur une copie où `R` manque au défaut, le script rend
`ECHEC : le défaut de verify.sh (ABCDEFGHIJKLMNOPQ) ne lance pas toutes les étapes déclarées (…R)` et le
code de sortie 1.

## 52. P0 clos pour la session et le mode, P2 rejouable (2026-10-20, soir)

### 52.1 P0 : les refus session et mode, mesurés en jeu

Le refus de slot était prouvé par l'étape R ; session et mode ne l'étaient qu'hors jeu, parce que les
trois champs vivent dans le même fichier de sauvegarde : on ne peut pas faire diverger un seul champ
sans dupliquer la partie.

Deux arguments de **sonde** (et rien d'autre) permettent de l'annoncer : `-coopsession=<id>` et
`-coopgamemode=<mode>` (`src/Coop.Model/SaveIdentityOverride.cs`, appliqués dans `AutoLoadBehaviour`
juste avant `Publish`). Le client charge **la même** sauvegarde, mais son handshake annonce autre chose ;
`SaveCompatibility.Explain` refuse alors sur le champ divergent, dans l'ordre slot → session → mode.
Le journal dit explicitement que l'empreinte annoncée diffère de la partie chargée, donc la mesure ne
peut pas passer pour une vraie partie.

| Étape | Run | Relevé |
|---|---|---|
| **S** (session) | 62 s, vert | client 2 : `handshake REFUSÉ par le serveur : session de sauvegarde différente (client='deadbeef', serveur='aae68629-…')` ; serveur : `handshake REFUSÉ pour 'killi' : session de sauvegarde différente (…)` ; client 1 `handshake ACCEPTÉ` dans le même run |
| **T** (mode) | 62 s, vert | client 2 : `handshake REFUSÉ par le serveur : mode de jeu différent (client='Freedom', serveur='Creative')` ; client 1 admis ; `expect_missing` confirme que le client 2 n'est jamais admis |

Les deux étapes nomment le champ refusé : ni un refus de version, ni un refus de pseudo ne peut les
satisfaire. Les tests d'`SaveIdentityOverride` (**9**) couvrent la lecture des arguments, le rejet des
valeurs douteuses et le recalcul du hash.

Reste hors jeu : le refus `changeSet` plus ancien (le probe devrait annoncer un entier antérieur — même
mécanisme, non exercé).

### 52.2 P2 : la reconnexion devient une étape rejouable

`tools/run-session.sh` gagne `--reconnect=N` : une fois le client N prêt, il est **tué**, le harnais
attend `PlayerLeft diffusé` côté serveur, attend `--reconnect-delay`, puis le relance sous un journal
séparé (`coop-client-N-b.log`) et attend son marqueur. Ce qui était une manipulation manuelle est
maintenant une étape de la batterie.

**Étape U** (130 s, verte) : deux clients avec la sonde de ramassage, client 2 tué puis relancé sans
sonde. Assertions, toutes relevées :

```
[net] PlayerLeft diffusé pour 'killi' (id=2)
[net] pickup : 1 réclamation(s) libérée(s) par la déconnexion de 'killi'#2 — les clés redeviennent réclamables.
[net] handshake ACCEPTÉ : 'killi' (id=3, …)
[net] disparition : 2 entité(s) disparue(s) rejouée(s) pour 'killi'#3
client 1 (dernier [ping]) : joueursDistants=1 avatarsActifs=1
```

`expect_missing` vérifie qu'aucune clé n'est refusée au nom du joueur parti (`déjà tenue par id 2`) : la
régression du §50.3 est donc gardée par une étape, plus seulement par un test du registre.

### 52.3 Ce qui n'est pas encore couvert

- ~~Aucun volant n'est tenu pendant le kill~~ — **couvert depuis le 2026-10-20 par l'étape X** (§57) :
  `COOP_DRIVE_KEEP=1` garde le volant, le client est tué, et le serveur le libère.
- Pas de resynchronisation de baseline (aucune n'existe) : la reconnexion repose sur les snapshots
  continus et le rejeu des retraits.
- Les étapes S, T et U allongent la batterie : 21 étapes, ~3 min de plus qu'avant en run complet.

## 53. M2 et M3 à trois clients ; un défaut de livraison hors ordre corrigé (2026-10-20, nuit)

### 53.1 M2 : la présence n'avait jamais été mesurée à trois

`RemotePlayerManager` et l'équipement fonctionnaient à deux ; au-delà, rien ne le prouvait. L'étape **V**
(trois instances, sans sonde — la sonde d'avatar attend un pair précis et ne conclut pas à trois)
affirme l'état réel : le serveur admet `id=3`, et **chez les trois** clients le dernier compteur `[ping]`
vaut `joueursDistants=2 avatarsActifs=2`. Run vert en 104 s, avec `--settle=12` (nouvelle option de
`run-session.sh`) pour laisser les compteurs se réécrire après le dernier marqueur.

Ce que V ne dit pas : que les avatars se **voient** bouger (la sonde d'avatar le fait à deux et ne
conclut pas à trois). Le nombre d'avatars et leur mouvement sont deux faits distincts.

### 53.2 M3 : trois clients révélaient un vrai défaut — les états périmés

Première tentative de l'étape **W** (trois clients, sonde de créatures) : le client 1 rendait `True`,
les clients 2 et 3 `False` avec

```
écart max=2.60m sur états consécutifs ('Peeper(Clone)' … sur un intervalle de 0.03s)
écart max=2.99m … 0.04s
```

soit 65–87 m/s pour des créatures qui nagent à 2 m/s. Le serveur, lui, n'avait pas bougé : `CreatureSnapshot`
voyage en **Unreliable**, et un état plus ancien livré après un plus récent **reculait** la créature. À
deux clients la rafale était rare ; à trois, elle arrivait en continu.

**Correctif** (`RemoteCreatureManager.OnSnapshot`) : un état dont le `ServerTick` n'est pas **postérieur**
au dernier appliqué est ignoré et compté (`états périmés ignorés=`). Un serveur **redémarré** repart de
zéro : un recul de plus de 2 000 ticks (100 s à 20 Hz) est traité comme un redémarrage, pas comme du
retard, sinon un client ne verrait plus jamais rien après la relance du serveur.

**Seconde mesure** : étape W verte en 126 s, `verdict=True` chez les trois, avec `états périmés
ignorés=2591 / 1704 / 951`. Le défaut n'était donc pas une mesure fragile : 951 à 2 591 états par client
écrivaient une position ancienne.

### 53.3 Ce qui n'est pas fait, et pourquoi

- **M4** (véhicule) : énergie, modules/upgrades et dégâts ne sont **pas** transportés. Ce n'est pas une
  omission de mesure : il faut un message (ou des champs dans `VehicleSnapshot`) et leur application côté
  client — un changement de protocole, pas une heure de travail. `spawn` initié par un client n'existe
  toujours pas.
- **M5** (ramassage) : objets mobiles mal jugés, collisions de clé **comptées mais non arbitrées**, retour
  arrière jamais exercé. Les collisions demandent un schéma d'identité (clé + position ?) et une décision
  de conception, pas un correctif local.
- **M6** (contenu) : le pipeline AssetBundle est **gelé** par décision (§43.5) jusqu'à ce qu'une session
  réelle de 30 minutes tienne. Le livrer ici reviendrait à rouvrir un gel sans la mesure qui le justifie.
- Les trois clients de V et W ne testent **pas** 4 à 10 joueurs ; `MaxClients=10` reste non exercé au-delà
  de trois.

## 54. Clôture des jalons M2, M3 et M5 (2026-10-20)

Règle appliquée : un jalon est clos quand **son critère du §6** est prouvé, pas quand tout ce qu'on
aimerait autour l'est. Les limites connues sont écrites à côté de chaque clôture plutôt que passées sous
silence, et elles n'invalident pas le critère.

### 54.1 M2 — présence joueurs : clos

| Critère §6 | Preuve |
|---|---|
| 2 clients se voient bouger | étape **B** verte (2026-10-17, `113 s` puis deux runs isolés `101 s`/`110 s`) : `[sonde] présence : verdict=True` des deux côtés, `nage puis arrêt vus par 'killi' (id=2)`, équipement appliqué sur l'avatar réel |
| distant interpolé | `RemoteAvatar.InterpolationWindowSeconds = 0.05` : position et rotation interpolées entre deux annonces 20 Hz (`PLAN.md` §30.4), au lieu d'un saut par paquet |
| self prédictif | **décision inverse, assumée** : la position locale est gardée par l'autorité serveur (P1), pas prédite. C'est écrit depuis §43.2 et mesuré par l'étape **P** (0 correction en nage normale, téléport refusé) |
| au-delà de deux joueurs (extension, hors critère) | étape **V** verte (2026-10-20, `104 s`) : `id=3` admis, `joueursDistants=2 avatarsActifs=2` chez les trois clients |

**Limites** : V prouve la présence de deux avatars, pas que l'avatar soit vu **en mouvement** à trois
(la sonde d'avatar conclut à deux) ; 4 à 10 joueurs non exercés.

### 54.2 M3 — une créature synchro, IA client coupée : clos

| Critère §6 | Preuve |
|---|---|
| position identique chez les 2 clients | étape **E** verte (2026-10-17) : `verdict=True` des deux côtés, `clé inconnue +0`, `76 sur 76` clés communes, position journalisée identique ; interpolation par image `0,19 m` au plus sur 170 000 images |
| IA uniquement serveur | `adoptées=65 (IA arrêtée=65)` et `IA coupée index 260 -> 0 …, lue après l'appel : 0` |
| au-delà de deux clients (extension, hors critère) | étape **W** verte une fois (2026-10-20, `126 s`), puis **rouge** au run de contrôle après extraction du filtre de tick : `écart max=2.01m` pour un seuil de `2,0 m` sur `0,10 s` chez le client 3. **Noté bloquant** (voir §54.5) ; le critère M3 est prouvé par E, à deux clients |

**Limites** : le « clignotement » à la coupure d'IA reste mesuré partiellement (§ preuves) ; une adoption
après déplacement de l'hôte n'est pas mesurée séparément ; 4 à 10 joueurs non exercés.

### 54.3 M5 — première action autoritaire : clos

| Critère §6 | Preuve |
|---|---|
| pickup : refus serveur si déjà réclamé | étape **L** verte (`94–97 s`, rejouée le 2026-10-19) : `ACCORDÉE (premier claim …)` au client 1, `REFUSÉE (déjà tenue par 'killi'#1)` au client 2, `garde=blocage … objet dans l'inventaire=False`, clé inventée refusée, `PlayerId` falsifié réattribué |
| conséquence du geste | étape **O** verte : l'objet accordé quitte le monde serveur et les deux clients le retirent ; le retrait est rejoué à un client tardif (étape **Q**) |
| décision hors jeu | `PickupClaimLedger` : 9 tests (première réclamation, refus d'un autre, idempotence, libération à la déconnexion), `dotnet test` 280/280 |

**Limites** : objets **mobiles** (butin porté par une créature) jugés par une position qui bouge ; objets
sans clé non arbitrés ; **collisions de clé** comptées, pas arbitrées ; **retour arrière** jamais exercé.

### 54.4 Les jalons qui restent ouverts, et ce qu'il faudrait

- **M4 (véhicule)** : **clos le 2026-10-20 (§56)** — l'énergie est drainée par la sonde et appliquée chez
  le client, et un module installé côté serveur arrive puis s'applique côté client (`transport=True
  application=True`). Restent hors du critère §6, et nommés : les **dégâts** (`LiveMixin`) et le `spawn`
  initié par un client ne voyagent toujours pas.
- **M6 (contenu)** : création/retrait/livraison d'une définition sont prouvés (N, O, Q), mais l'item
  **custom** (AssetBundle livré au client) est **gelé par décision** (§43.5) jusqu'à une session réelle
  de 30 min. Le critère §6 le nomme explicitement : M6 reste ouvert par choix, pas par oubli.

### 54.5 Le filtre de tick sort du gestionnaire — et révèle une limite de W

La règle « un état plus ancien ne s'applique pas » vivait dans `RemoteCreatureManager`, donc non testable.
Elle est maintenant `Coop.Model.SnapshotTickGate` (tick croissant accepté, égal ou ancien refusé et compté,
recul de plus de 2 000 ticks lu comme un redémarrage serveur, tick nul accepté sans déplacer le dernier),
avec **six tests** dans `SnapshotTickGateTests` (`dotnet test` **290/290**).

**Contrôle en jeu après extraction** : `tools/verify.sh --only=W` — **rouge**, client 3 :
`verdict=False … écart max=2.01m sur états consécutifs … sur un intervalle de 0.10s`, avec un
`mouvement local ≤1.47m` de la même `Peeper`. Le filtre a bien écarté `2602` états périmés chez ce client ;
l'écart vient donc d'ailleurs — probablement l'IA qui continue de faire nager la créature entre deux
relâchées, c'est-à-dire le « clignotement à la coupure » déjà partiellement mesuré.

**Piste principale, relevée dans le journal** (pas une cause démontrée) : la même créature entre dans un
cycle **relâchée → ré-adoptée** toutes les quelques secondes —

```
[créature] relâchée : 'Peeper(Clone)' pos=(-102.0, -8.0, -93.1) clé=fb8ea914290670c4
            (plus d'état depuis 2.0s) — l'IA reprend chez ce client (index relu > 0 : True).
[créature] adoptée : 'Peeper(Clone)' pos=(-101.1, -5.8, -84.1) clé=fb8ea914290670c4
            à 4.54 m de la position serveur …
```

Le cycle est fréquent (deux relâches en 6 s dans ce run) et chaque ré-adoption repose la copie plusieurs
mètres plus loin. L'écart de `2,01 m` mesuré 5 s après l'une d'elles tombe dans cette zone, mais le
journal ne permet pas de l'attribuer avec certitude : il faudrait dater chaque état et chaque relâche
côte à côte. Ce qui est sûr : ce n'est **pas** le filtre de tick (2 602 états périmés écartés chez ce
client), et la relâche à 2 s sans état est le mécanisme à examiner.

- Ce n'est **pas** corrigé ici : il faut soit exclure le premier état après ré-adoption de la mesure de
  dérive, soit espacer la relâche (2 s) — deux choix à mesurer, et l'heure de session ne le permet pas.
- W reste **strict et exécutable** (`--only=W`). Il était **intermittent** ; le §60 montre que la cause était
  la mesure (dérive relevée pendant le glissement) et il est **vert à trois clients** depuis le
  2026-10-20 (`écart max=1,02 / 1,04 / 1,28 m`). M3 reste clos sur son critère §6 (deux clients, E).

## 55. M4 avancé : la charge du véhicule voyage (v14, 2026-10-20)

`VehicleStateEntry` porte un champ `Energy` (float normalisé `0..1`) depuis la **v14** : le serveur lit
`EnergyMixin.charge` de son véhicule (`ReadVehicleEnergy`, `-1` quand le composant manque — un véhicule
sans batterie n'est pas « vide »), et le client applique la valeur sur sa copie via
`EnergyMixin.ModifyCharge` (`charge` est une propriété **lecture seule**, `capacity` donne l'échelle ;
l'écriture est ignorée en dessous de 1 % d'écart). `énergies appliquées=` est publié dans
`[véhicule] état`.

Hors jeu : `VehicleEnergyTests` (4 cas — aller-retour, sentinelle `-1`, valeur non finie refusée à
l'écriture, hors bornes ramené dans `0..1`) ; `dotnet test` **290/290** ; `NetProbe selftest` 94/94 ;
builds client et serveur rc=0 ; `tools/mod.manifest.json` et `Protocol.ConnectionKey` passent à 14.

**Ce qui n'est pas fait** : aucune mesure en jeu de l'énergie (le run J ne l'assertait pas), et les
**modules/upgrades** et les **dégâts** ne voyagent toujours pas. M4 reste donc **ouvert** : le critère §6
demande l'énergie *et* les upgrades, pas l'un des deux.

## 56. M4 clos : l'énergie est mesurée, les modules voyagent (v15, 2026-10-20)

Le §55 annonçait deux choses : la charge « transportée » et M4 « encore ouvert sur l'énergie et les
upgrades ». Le premier run de mesure a montré que la première affirmation était **fausse**, et la
correction a permis de fermer la seconde.

### 56.1 Le défaut de la v14, trouvé par la mesure et non par la lecture

`EnergyMixin.charge` d'un Seamoth vaut **`200`**, pas `1`. La v14 normalisait par une borne
(`charge > 1 ? 1 : charge`) : toute batterie pleine devenait `1`, et une batterie à moitié aussi. Côté
client, `ApplyEnergy` comparait cette valeur normalisée à une charge absolue — le run du 2026-10-20 17:35
a relevé **125 « énergies appliquées »** avec la charge du serveur affichée `200,00 -> 200,00` : les
compteurs montaient, aucune charge ne changeait.

Deux causes distinctes, mesurées :

1. **l'unité** — c'est `EnergyInterface.GetValues(charge, capacity)` qui donne l'échelle, pas
   `EnergyMixin.charge` seule ;
2. **l'écriture** — `EnergyInterface.ModifyCharge` commence par `GameModeUtils.RequiresPower()` (IL lu avec
   `tools/ApiDump il`), donc dans un mode de jeu qui ne consomme pas d'énergie l'appel **ne fait rien**. La
   batterie s'écrit par `IBattery.charge = valeur` (`IBattery.charge { get; set; }`), que la sonde et le
   client utilisent désormais.

La conversion vit dans `src/Shared/VehicleModules.cs` (`ReadNormalized`, `WriteNormalized`, `TryRead`,
`TryFindBattery`), compilée des deux côtés : une seule échelle, donc un écart client/serveur qui mesure
les mondes et pas deux morceaux de code.

### 56.2 Les modules : un message fiable, et il s'applique

`VehicleUpgradesMessage` (opcode `23`, **v15**, ReliableOrdered) porte la clé du véhicule et les entiers
`TechType` de ses modules, en forme canonique (triés, dédupliqués). Le serveur le diffuse **au changement**
seulement, et le **rejoue** à un client qui se connecte après l'installation — comme le carnet
d'apparitions. Le client compare à sa copie, nomme la divergence, puis applique par
`VehicleModuleApplier` (prefab asynchrone, `GetFreeSlot(EquipmentType.VehicleModule)`, `AddItem`, retrait
par slot).

Une erreur de méthode a été corrigée en chemin : `(IEnumerable<InventoryItem>)vehicle.modules` lève
`InvalidCastException` — `Equipment` n'expose que `IItemsContainer`. La lecture passe par
`GetSlots(EquipmentType.VehicleModule)` + `GetTechTypeInSlot`, et le journal du run précédent
(`modules illisibles : InvalidCastException`, répété toutes les 2 s) est ce qui l'a montré.

### 56.3 La mesure (étape J, `tools/verify.sh --only=J`, 2026-10-20 17:53)

`étape J : OK (152s)`, `VERDICT GLOBAL : tout est vert`, code de sortie 0.

```
serveur  [sonde] véhicule : module 'VehicleHullModule1' installé=True (slot 'SeamothModule4')
serveur  [sonde] véhicule : verdict=True — … énergie 200.00 -> 100.00 (0.50 de la capacité),
                 module 'VehicleHullModule1' installé=True
serveur  [net] modules : clé=0xe16d82d1f441441c 'SeaMoth(Clone)' — 1 module(s) [VehicleHullModule1]
                 annoncés à 1 client(s).
client   [sonde] véhicule modules : messages=1 serveur=[VehicleHullModule1] local avant=[aucun]
                 local maintenant=[VehicleHullModule1] divergences=1 charges appliquées=1
                 — transport=True application=True
```

Ce que ces lignes établissent, et ce qu'elles n'établissent pas :

- l'énergie **change** chez le serveur (moitié de la capacité) et le client écrit une charge reçue
  (`charges appliquées=1`) : la v14 n'aurait rien écrit ;
- le module est installé côté serveur, **reçu** (`messages=1`) et **appliqué** (la copie locale passe de
  `aucun` à `[VehicleHullModule1]`) : `transport=True application=True` ;
- ce n'est pas une preuve de **comportement de jeu** : la sonde ne vérifie pas que la profondeur admissible
  change, seulement que l'équipement de la copie porte le module.

### 56.4 Ce qui reste hors du critère, et qui est dit

- **Dégâts** (`LiveMixin.health`) et **`spawn` initié par un client** ne voyagent pas. Le critère §6 de M4
  nomme position, énergie, upgrades et conduite : ces deux-là sont des extras, pas des manques du critère.
- La sonde d'énergie vide la batterie d'un véhicule **du monde** puis la restaure ; sur un véhicule
  fabriqué (`spawn seamoth`), elle le détruit, donc rien ne reste.
- W n'est pas concerné : il mesure des créatures, et son intermittence (§54.5) est indépendante.

## 57. P2 mesuré : le volant d'un client tué est libéré (étape X, 2026-10-20)

Le §52.3 nommait la limite exacte : « aucun volant n'est tenu pendant le kill ». L'étape **U** tuait un
client dont la sonde avait déjà fini ; la libération des conducteurs à la déconnexion n'était donc
couverte que par le code et par l'étape M (qui ne tue personne).

### 57.1 Ce qui a été ajouté

- **`COOP_DRIVE_KEEP=1`** dans la sonde de conduite : elle prend le volant, puis **le garde** au lieu de le
  rendre, et journalise `[sonde] conduite : maintien`. Sans ce mode, la fin de sonde rendait toujours le
  volant avant qu'on puisse tuer le client.
- **`--client-env=VARIABLE=VALEUR`** dans `tools/run-session.sh` : une sonde ne peut pas lire un argument,
  `-coopdebug` étant déjà pris par le choix de la sonde.
- **Étape X** de `tools/verify.sh` : un client, `-coopdebug=drive` avec `COOP_DRIVE_KEEP=1`, tué puis
  relancé (`--reconnect=1`). Quatre assertions : la prise de volant a bien eu lieu, le départ est diffusé,
  le volant n'est plus tenu, et le revenu est admis sous un **nouvel** id puis recharge le monde.

### 57.2 La mesure (2026-10-20 18:02)

`étape X : OK (86s)`, `VERDICT GLOBAL : tout est vert`, code de sortie 0.

```
serveur  [net] conduite : clé=0xe16d82d1f441441c — prise par 'killi'#1 ('SeaMoth(Clone)' …), état appliqué et rediffusé.
serveur  [net] conduite : clé=0xe16d82d1f441441c libérée (silence de 3.0 s) — le conducteur ne l'envoie plus (libérations par silence=1).
serveur  [net] PlayerLeft diffusé pour 'killi' (id=1)
serveur  [net] handshake ACCEPTÉ : 'killi' (id=2, …)
```

### 57.3 Ce que la mesure a appris, et qui n'était pas prévu

Le premier run (18:02, rouge sur une assertion) a montré que le volant est libéré par le **silence**
(3 s sans état) **avant** la déconnexion : au moment du `PlayerLeft`, la carte des conducteurs était déjà
vide, donc la ligne « volant(s) libéré(s) par la déconnexion » n'apparaissait pas. L'assertion a été
écrite sur l'**issue** (le volant n'est plus tenu, par l'un ou l'autre mécanisme, tous deux nommés) et non
sur le chemin — ce n'est pas un assouplissement : le défaut redouté (un mort qui garde le volant) reste
exclu par les deux branches.

Conséquence à retenir : c'est le **silence** qui protège en pratique, et il est plus court (3 s) que le
temps de détection d'une déconnexion. Le chemin de déconnexion reste utile pour un client qui part
proprement en tenant le volant.

### 57.4 État de P2

**Clos sur son critère §43.5** : un client tué puis relancé rejoue ses réclamations (étape U), aucun
fantôme ne subsiste, et le volant qu'il tenait est libéré (étape X). Restent, nommés :

- **aucune resynchronisation de baseline** — il n'en existe pas ; la reconnexion repose sur les snapshots
  continus et le rejeu des carnets ;
- ce n'est pas la session de 30 minutes (P4) : ~90 s par étape, une seule reconnexion.

## 58. P5 : `ServerNetwork` découpé en cinq domaines, texte inchangé (2026-10-20)

Trois mille six cent vingt et une lignes dans un seul fichier : personne ne peut y vérifier qu'un
traitement de véhicule ne touche pas au ramassage. Le découpage a été fait de la façon la moins
ambitieuse possible, parce que c'est la seule qui ne change pas de comportement.

### 58.1 Ce qui a été fait

| Fichier | Lignes | Contenu |
|---|---|---|
| `ServerNetwork.cs` | 1 194 | champs partagés, `ClientSession`, constructeur, `Poll`, admission, boucle |
| `ServerNetwork.Presence.cs` | 687 | `PlayerJoined`/`Left`, équipement, objet tenu, couleurs, positions, corrections |
| `ServerNetwork.Vehicles.cs` | 727 | diffusion 10 Hz, conduite autoritaire, modules (v15), énergie, identités |
| `ServerNetwork.Pickup.cs` | 401 | réclamation, décision, retrait de l'objet, garde du registre |
| `ServerNetwork.Creatures.cs` | 401 | diffusion des créatures, plafond, répartition des pas |
| `ServerNetwork.Entities.cs` | 224 | apparition, retrait, carnets de rejeu, définitions |

La classe est devenue `partial` : **les membres privés restent partagés**, donc aucun appel n'a été
réécrit, aucune signature changée, aucun champ rendu public. C'est un découpage **par fichiers**, pas
encore cinq *systèmes* au sens d'interfaces séparées — la limite est dite au §58.4.

### 58.2 Comment le découpage a été rendu vérifiable

Deux outils ont échoué avant celui qui a servi, et c'est instructif :

1. un compteur d'accolades naïf — cassé par les `{` d'une interpolation ;
2. un scanner de chaînes — cassé par `$"[{(cond ? "a" : "b")}]"` (ligne 3347), une interpolation qui
   contient elle-même une chaîne.

Le découpage retenu se fait **par lignes de déclaration** (8 espaces + modificateur ou attribut), ce qui
suit le formatage réel du fichier. Trois contrôles ont ensuite été exécutés avant de le considérer fait :

- **comparaison textuelle** : les lignes non vides du corps d'origine et celles des six fichiers sont
  comparées comme des multiplicités — **3 098 lignes de part et d'autre, 0 manquante, 0 en trop** ;
- `dotnet build` rc=0, `dotnet test` **296/296** ;
- **étapes en jeu** : `B` (présence, 110 s) et `J` (véhicules, 152 s) — `VERDICT GLOBAL : tout est vert`.

### 58.3 Ce que P5 couvre, et ce qu'il ne couvre pas

Le critère du §43.5 était : « `verify.sh` identique, comportement inchangé, aucun nouveau message ».
Les trois sont tenus et mesurés. Ce qui n'est **pas** fait, et qui reste le vrai travail de P5 :

- il n'y a **pas cinq systèmes autonomes** : les fichiers partagent encore les champs de la classe et
  peuvent s'appeler librement. Un domaine peut donc encore écrire dans l'état d'un autre ;
- aucun test unitaire n'exerce `ServerNetwork` directement (il dépend d'Unity et de LiteNetLib) : la
  non-régression repose sur le build, les tests du modèle et les étapes en jeu, pas sur une couture.

Autrement dit : la carte est lisible, mais les murs porteurs ne sont pas posés. Le noter ainsi vaut mieux
que d'annoncer « cinq systèmes » et laisser croire à une isolation qui n'existe pas.

## 59. P3 : l'interpolation se rejoue hors du jeu, en une seconde (2026-10-20)

Le critère du §43.5 était : « une régression d'interpolation est détectée **hors du jeu**, en secondes ».
Jusqu'ici elle ne se voyait qu'à l'étape J : 152 secondes de jeu, un client, un hôte, un monde chargé.

### 59.1 Ce qui a été extrait

| Élément | Rôle |
|---|---|
| `Coop.Model/VehicleInterpolation.cs` | la **règle** : durée d'un segment (plancher 0,04 s, plafond 0,50 s, rattrapage à 40 m/s), avancement borné `0..1`, deux états consécutifs ou non. Sans Unity. |
| `Coop.Model/VehicleReplay.cs` | le **format** d'enregistrement (`tick;intervalle;x;y;z`, culture invariante) et le **rejeu** image par image, avec cette même règle. |
| `RemoteVehicleManager` | utilise la règle extraite (`SegmentDuration`) et **enregistre** le flux reçu quand `COOP_RECORD=1`. |
| `tests/.../fixtures/vehicles-j.csv` | **129 états réels**, écrits par le client pendant un run du 2026-10-20 18:23 (60 s de Seamoth à 20 m/s, `COOP_RECORD=1`). |

Les constantes de durée du gestionnaire pointent désormais sur celles du modèle
(`private const float MinSegmentSeconds = VehicleInterpolation.MinSegmentSeconds;`) : une seule source,
donc pas de dérive entre la règle mesurée en jeu et celle qui est rejouée.

### 59.2 La mesure

| Contrôle | Résultat |
|---|---|
| `dotnet test --filter VehicleReplayTests` | **6/6**, durée des tests **13 ms**, **1,1 s** de commande complète (contre 152 s pour l'étape J) |
| rejeu du flux réel, à 120 images/s | pas max **0,33 m**, pas moyen 0,16 m, 1 604 images — sous les seuils de J (1,0 m / 0,6 m) |
| même flux **sans interpolation** (l'état d'avant le §33) | pas max **2,13 m** — le test est donc **discriminant** : il échoue si la règle disparaît |
| rafale de 21 m en 2 ms (le cas mesuré au §33) | pas max ≤ 1,0 m sur 50 images |
| `--only=J` après extraction | **OK (152 s)**, `VERDICT GLOBAL : tout est vert` |

### 59.3 Ce que P3 ne couvre pas, et qui reste dit

- **Seuls les véhicules** sont rejouables. Les créatures, la présence et le ramassage ont la même forme de
  problème, mais leur flux n'est pas enregistré : leurs assertions restent en jeu.
- L'enregistrement est une **trace**, pas un enregistreur de session : il ne contient pas les ticks serveur
  des autres entités, ni les décisions (accords, refus), donc il ne rejoue pas une partie.
- Le fixture vient d'**un** run, sur une machine : il prouve la règle, pas la diversité des réseaux.
- Le format n'a pas de version : un champ ajouté plus tard devra soit changer le nombre de colonnes, soit
  être ignoré explicitement — c'est écrit ici pour que la rupture soit un choix.

## 60. W n'est plus intermittent : la dérive était mesurée pendant le glissement (2026-10-20)

Le §54.5 laissait W « strict et intermittent » : vert une fois, puis rouge avec `écart max=2.01 m` pour un
seuil de `2 m`. La piste écrite était le cycle relâche → ré-adoption. La mesure de ce soir montre que
c'était **la mesure elle-même** qui était fausse, et d'une façon vérifiable.

### 60.1 Ce qui l'a montré

Dans le run rouge, la ligne portait `écart max=2.01m … sur un intervalle de 0.10s` **et**
`mouvement local ≤1.47m`. Un `Peeper` ne nage pas à 14 m/s : ce « mouvement local » ne pouvait pas être la
créature. C'était le **glissement restant** de l'interpolation — la copie n'avait pas encore fini de
rejoindre l'annonce précédente quand la suivante est arrivée, et l'écart mesuré comparait donc une position
en cours de route à une cible neuve.

Autrement dit : la dérive brute ne teste la synchronisation que si la copie était **à l'arrêt sur la
position serveur précédente** au moment de l'annonce. Sinon elle mesure le retard d'interpolation, qui est
le comportement voulu.

### 60.2 Le correctif

`RemoteCreatureManager` ne mesure la dérive que si le segment en cours était terminé (`gap + 20 ms ≥
SegmentDuration`, `PreviousSegmentFinished`). Les paires écartées sont **comptées** (`StillGliding`) et
publiées dans le verdict de la sonde : `paires écartées faute de glissement fini=`.

Ce que le garde **ne peut pas** masquer : une annonce réellement manquée produit un intervalle supérieur à
`ConsecutiveStateGapSeconds`, qui part dans la branche « interruption » — toujours mesurée, toujours
publiée (`interruption max=1.4 à 2.0 s`, écarts `2,32 à 5,85 m` dans le run ci-dessous), et toujours hors
du verdict par décision antérieure. Le garde ne touche que les paires où la copie glissait.

### 60.3 La mesure (étape W, 3 clients, `tools/verify.sh --only=W`, 2026-10-20 18:45)

`étape W : OK (132s)`, `VERDICT GLOBAL : tout est vert`, code de sortie 0.

| Client | écart max | intervalle | mouvement local max | paires écartées (glissement) | verdict |
|---|---|---|---|---|---|
| 1 | **1,02 m** | 0,09 s | 0,24 m | 381 | True |
| 2 | **1,04 m** | 0,09 s | 0,22 m | 521 | True |
| 3 | **1,28 m** | 0,09 s | 0,80 m | 461 | True |

Les trois clients suivent 18 à 20 créatures, `IA arrêtée` égale `adoptées`, `clé inconnue=0`,
`clés partagées=0`, et le pas par image reste à 0,12–0,16 m.

### 60.4 Ce que ça change pour M3 et pour la suite

- **M3 reste clos**, et W cesse d'être une extension « intermittente » : il est vert à trois clients sur son
  propre critère, avec une marge de 0,7 m sous le seuil.
- La piste « relâche → ré-adoption » du §54.5 n'est **pas** confirmée comme cause : elle produisait bien des
  sauts visibles (décrits), mais ils n'entraient pas dans cette mesure. Elle reste un défaut d'affichage à
  traiter, pas la cause du rouge.
- Ce qui n'est **pas** prouvé : que la dérive soit nulle. Le garde réduit l'échantillon d'environ 12 % des
  paires (≈400 sur ≈3 200 appariés) ; la valeur publiée porte sur le reste, et c'est écrit dans le verdict.

## 61. Le garde de dérive sort du gestionnaire (2026-10-20)

La correction de W (§60) vivait dans `RemoteCreatureManager` sous forme de quatre lignes non testables.
Elle est maintenant `Coop.Model.CreatureDriftGate.SegmentFinished(gap, segment)` : le gestionnaire ne garde
que l'accès au segment en cours, et la règle se vérifie en millisecondes.

Hors jeu : `CreatureDriftGateTests` (5 cas — segment terminé, glissement inachevé, tolérance de 20 ms qui
couvre une image longue sans couvrir un segment entier, absence de segment, entrée non finie) ;
`dotnet test` **314/314**, build rc=0.

En jeu, après câblage : `étape E : OK (99s)`, `étape W : OK (126s)`, `VERDICT GLOBAL : tout est vert` —
la valeur mesurée ne bouge pas (1,02 / 1,04 / 1,28 m la veille), ce qui est attendu d'un déplacement de
règle sans changement de calcul.

Même motif que `SnapshotTickGate` (§54.5) : une règle qui décide d'une mesure doit pouvoir échouer hors du
jeu, sinon sa seule preuve est un run de deux minutes.

## 62. Kit P4 : la session longue devient une mesure rejouable (2026-10-21)

**Verdict : P4 avait un critère et aucun instrument.** `0 exception`, `0 désync > 2 m`, « sauvegarde +
rechargement + reconnexion » sont écrits dans les journaux, mais rien ne les agrègeait, et
`run-session.sh` rendait la main dès les marqueurs atteints — un run de 2 min ne pouvait pas se
distinguer de la session de 30 min. Trois pièces ajoutées, aucune assertion supprimée :

| Brique | Fichier | Ce qu'elle fait |
|---|---|---|
| session longue | `tools/run-session.sh --minutes=N` (+ `--reconnect-after=S`) | tient les instances N minutes après les marqueurs, et place la reconnexion **au milieu** de la session au lieu du démarrage |
| orchestrateur | `tools/p4-session.sh` | deux clients, hôte en `-coopdebug=save`, reconnexion au tiers, durée réelle, copie des journaux puis verdict unique |
| graveur | `tools/session-verdict.sh` | lit les journaux et note : exceptions du mod, désync des créatures, durée, sauvegarde, reconnexion, rechargement |

### 62.1 Ce que le graveur refuse de faire

- **Il ne prend pas `écart avant lissage max` pour une désync.** Un run réel mesure `7,04 m` à 20 m/s :
  c'est le mouvement légitime entre deux annonces, pas une divergence. Le seul compteur de divergence
  **au repos** publié en session est la `dérive max` des créatures (segments terminés, IA vérifiée
  coupée), le même que W compare à 2 m.
- **Il ne valide pas un vert vide.** `dérive max=0.00m` avec `segments=0` est un **échec** : aucune paire
  n'a été comparée.
- **Il ne confond pas une erreur d'environnement avec une exception du mod.** `XR: OpenVR Error!` au
  démarrage ([Unity Log]) est compté, nommé, et n'invalide pas le verdict ; une ligne
  `[Error] [Coop Client]` le fait échouer.
- **Il ne prouve pas tout.** La divergence des **véhicules** et celle de **position des avatars** ne
  sont pas mesurées (les avatars ne publient aucun écart ; les corrections P1 ne comptent que les
  téléports refusés). Les deux sont imprimées en `RÉSERVE`, pas passées sous silence.

### 62.2 Le kit a trouvé un défaut dès son premier run

Premier run de 2 min, verdict **rouge** pour une raison réelle — le critère « 0 exception » a payé
immédiatement :

```
[Error  ] [Coop Client] [net] erreur en traitant un paquet : System.InvalidOperationException:
Collection was modified; enumeration operation may not execute.
  at RemoteVehicleManager.FlushPendingUpgrades ()
  at RemoteVehicleManager.OnSnapshot (VehicleSnapshotMessage)
```

`FlushPendingUpgrades` énumérait `_pendingUpgrades` en appelant `ApplyUpgrades`, qui **retire** l'entrée
pendant l'énumération : chaque paquet de véhicule arrivant au moment d'une adoption levait, et le paquet
était perdu. Correctif : copie des clés avant la boucle (`new List<long>(_pendingUpgrades.Keys)`), l'entrée
traitée sortant du dictionnaire par le même chemin qu'avant. Deux occurrences dans le premier run
(client 2 et client 2 relancé), **zéro** dans le run de contrôle après correctif.

### 62.3 Mesures du 2026-10-21

| Contrôle | Valeur mesurée |
|---|---|
| `session-verdict.sh --self-test` | **8 cas / 0 échec** — 1 vert, 1 bruit d'environnement toléré, 6 rouges (exception, dérive 2,50 m, mesure vide, sauvegarde absente, reconnexion absente, durée courte) |
| run de contrôle `--minutes=2` | `exceptions du mod = 0` (4 lignes [Unity Log] nommées) · dérive `1,18 m` (6 697 segments) et `1,21 m` (36 932) · durée `4 min 33 s` · sauvegarde serveur `verdict=True` · reconnexion : départ diffusé + 3 admissions · rechargement : 1 relance · **VERDICT P4 : VERT**, rc=0 |
| run qui a trouvé le défaut | même scénario, rc=1 : 2 exceptions `InvalidOperationException` |

Le second run dure 4 min 33 s pour 2 min demandées : le chargement des mondes, la reconnexion et
l'attente de l'autotest de sauvegarde s'ajoutent à la durée tenue. Le graveur mesure donc la **durée du
journal serveur**, pas la durée demandée — et exige `--minutes` moins 60 s de tolérance.

### 62.4 Ce que ce chapitre ne prouve pas

- **La session de 30 minutes n'a pas été jouée.** Le kit est prêt ; il reste à lancer
  `tools/p4-session.sh` (30 min) avec deux instances, puis la vraie P4 — deux **humains**, installation
  faite par le second, qui est une propriété de la session et non du code.
- **La désync des véhicules et des avatars n'est pas mesurée** (§62.1). Un verdict vert ne dit donc pas
  « aucune divergence nulle part » : il dit « aucune exception, aucune dérive de créature au-dessus de
  2 m, session tenue, sauvegarde, reconnexion et rechargement prouvés ».
- Le seuil de 2 m porte sur la **dérive cumulée maximale** de la session : un seul échantillon au-dessus
  suffit à rougir. C'est le critère tel qu'il est écrit, pas une marge de confort.

## 63. Rejoindre un serveur distant : saisir l'adresse (2026-10-21)

**Verdict : le client ne pouvait pas se connecter à un serveur qui n'est pas sur sa machine sans éditer
un fichier de configuration.** `[Reseau] Hote` valait `127.0.0.1` par défaut, était lu au chargement du
plugin, et la connexion partait toute seule. Pour un ami derrière un autre réseau, il n'existait **aucun
endroit** où taper une IP publique.

### 63.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| règle d'adresse | `src/Coop.Model/ServerAddress.cs` | `host[:port]`, IPv4 ou nom d'hôte ; refus **nommés** (port hors 1..65535, IPv4 invalide, IPv6, schéma d'URL, hôte vide) ; lecture exacte de `-coopserver=` et `COOP_SERVER` |
| panneau de connexion | `src/Coop.Client/JoinPanelBehaviour.cs` | au menu : champ adresse, champ port, bouton « Rejoindre », Entrée valide ; l'adresse est mémorisée dans `[Reseau] Hote`/`Port` ; le panneau se masque quand la session est acceptée et revient si elle échoue |
| changement à chaud | `Plugin.SetEndpoint` + `CoopClientPlugin.ApplyEndpoint` | coupe la session en cours, oublie l'acceptation, reconnecte vers la nouvelle adresse — pas besoin de relancer le jeu |
| porte de chargement | `src/Shared/AutoLoadBehaviour.cs` (`BeforeLoad`) | côté client, le monde n'est **pas** ouvert avant qu'une adresse soit connue (`-coopserver`, fichier ou saisie) ; le serveur ne pose pas la condition et charge comme avant |
| sonde | `src/Coop.Client/JoinProbeBehaviour.cs` (`-coopdebug=join`) | remplit les **vrais** champs et appelle le **même** `Submit()` que le bouton : adresse invalide refusée **sans connexion**, puis adresse valide connectée |
| harnais | `tools/run-session.sh` (`--server-addr=`) | chaque client lancé par le harnais reçoit `-coopserver=<adresse>` (127.0.0.1 par défaut), sinon les 24 étapes resteraient au menu à attendre une saisie |

### 63.2 Mesures (2026-10-21)

| Contrôle | Résultat |
|---|---|
| `dotnet test` | **327/327** rc=0 (13 nouveaux cas `ServerAddressTests` : hôte+port, défaut, espaces, port 0/65536/-1/abc, port manquant, octet > 255, underscore, IPv6, schéma, argument exact vs `-coopserverevil=`) |
| build client + serveur | rc=0 |
| en jeu, chemin **panneau** : client lancé avec `-coopserver=999.999.1.1` (refusé au démarrage) + `-coopdebug=join -coopjoin=127.0.0.1:7777` | `adresse REFUSÉE : « 999.999.1.1 » — adresse IPv4 invalide` puis `aucune adresse de serveur — en attente d'une saisie` ; panneau construit ; `adresse refusée : « 999.999.1.1:7777 »` ; `nouvelle adresse : 127.0.0.1:7777 (changement)` ; `adresse de serveur connue -> chargement du monde` ; `handshake ACCEPTÉ (id=1)` ; `panneau … masqué` ; `[sonde] join : verdict=True` |
| non-régression | `tools/verify.sh --only=E` **verte en 102 s** (`VERDICT GLOBAL : tout est vert`) avec l'adresse injectée par le harnais |

### 63.3 Ce que ce chapitre ne prouve pas

- **La frappe réelle n'est pas simulée** : la sonde remplit les champs et soumet par le chemin du bouton.
  Le clavier, le focus et le rendu uGUI sont ceux de Unity, pas couverts par une assertion.
- **Pas de traversée de NAT, pas de lobby, pas de relais** : l'IP publique doit être saisie à la main, et
  le port UDP 7777 redirigé chez l'hôte. Un CGNAT d'opérateur ne se contourne qu'avec un réseau privé
  virtuel externe, que le mod n'intègre pas.
- **IPv6 est refusé** (nommément) : LiteNetLib est utilisé en IPv4 dans ce projet.
- **Aucune authentification ni chiffrement** : exposer le serveur sur Internet accepte quiconque connaît
  l'adresse. C'est écrit dans le README, à côté de la marche à suivre.

## 64. M4 fermé : la santé voyage, un client peut demander une fabrication (v16, 2026-10-21)

Deux manques étaient nommés depuis §33 et §56 : un véhicule endommagé chez l'hôte restait intact chez les
autres, et un `spawn` fait **chez un client** n'existait pas dans le monde du serveur — l'étape N ne prouve
que la fabrication par l'hôte. La v16 ferme les deux, et chacun par une décision qui doit être dite.

### 64.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| santé dans l'entrée de véhicule | `src/Coop.Model/Messages.cs` (`VehicleStateEntry.Health`) | fraction `0..1` (`LiveMixin.health / maxHealth`), **-1 = inconnue**, `0` = détruit ; `NaN`/∞ refusé à l'écriture **comme** à la lecture ; l'entrée passe de 36 à 44 octets |
| lecture côté hôte | `src/Coop.Server/ServerNetwork.Vehicles.cs` | `LiveMixin` lu à chaque diffusion, comme l'énergie ; pas de message nouveau, donc même rythme de 10 Hz |
| application côté client | `src/Coop.Client/RemoteVehicleManager.cs` (`ApplyHealth`) | écrit la fraction sur le `LiveMixin` de la copie **sans la tuer** : la mort reste une décision du serveur, annoncée par `EntityDespawn` |
| demande de fabrication | `src/Coop.Model/Protocol.cs` (#24), `Messages.cs` | `RequestId`, `TechType` (**jamais 0**), position et quaternion **finis** ; `EntitySpawnResult` (#25) porte la décision, la **clé** (jamais 0 si accordée) et un motif (≤ 96, non vide si refusée) |
| arbitrage | `src/Coop.Server/ServerNetwork.Spawns.cs` | liste blanche (`Seamoth`, `Exosuit`), position connue, **cadence** (5 s), **plafond** (8/session), **portée** (8 m autour de la dernière position **acceptée**) — dans cet ordre |
| fabrique | `src/Coop.Server/ServerEntityFactory.cs` | composant (le prefab se charge en asynchrone), file + callback sur le thread principal ; `UniqueIdentifier.Id` **neuf** écrit, puis clé relue **après** position ; échec ⇒ objet partiel détruit et refus nommé |
| sonde | `src/Coop.Client/ClientSpawnProbeBehaviour.cs` (`-coopdebug=requestspawn`) | demande légitime à ses pieds (accordée, copie créée par l'**annonce**, clé identique), puis demande à 500 m (refusée, aucune création de plus) |
| harnais | `tools/verify.sh` (étape **Y**) | un client, hôte non `-batchmode` ; assertions sur les deux journaux, y compris le **motif** rendu au demandeur |

### 64.2 Mesures du 2026-10-21

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet test` | **341/341** rc=0 — 14 nouveaux cas (`VehicleHealthTests` 5, `EntitySpawnRequestTests` 9 : aller-retour, bornes, sentinelle, `NaN`, `TechType` 0, décision sans clé, refus muet, motif trop long) |
| builds client + serveur | rc=0 |
| `NetProbe selftest` | **SUCCES** ; entrée de véhicule `44` octets, paquet plein mesuré à **714** octets (sous 1 023) |
| étape **J** (santé, hôte + client) | `[sonde] véhicule : santé 1.00 -> 0.40 (max 200.0, écriture effectuée=True)` ; client `[sonde] véhicule santé : appliquée=1 dernière=0.40 locale=0.40 écart=0.00 écart max observé=0.60 … verdict=True` ; **étape J : OK (151 s)**, tout vert |
| étape **Y** (fabrication demandée) | accordée : requête=1, `fabrication : 'Seamoth' créé à (-120.7, -13.9, -64.5) — clé=0x463a6eec6f562a50 (identifiant)` puis `ACCORDÉE (clé=0x463a6eec6f562a50 …)` ; clé identique chez le demandeur, `[spawn] entité créée : clé=0x463a6eec6f562a50 … identifiant du serveur 'aec34bd0-…' écrit (oui)` ; **étape Y : OK (61 s)** |
| refus hors portée | client `REFUSÉE : requête=2 — hors portée : 500.8 m > 8.0 m`, serveur `REFUSÉE — hors portée : 500.8 m > 8.0 m (requête=2)`, `aucune création supplémentaire=True` |
| non-régression (deux clients, créatures) | **étape E : OK (99 s)**, `VERDICT GLOBAL : tout est vert` |
| `check-verify-assertions.sh` | rc=0 — **25** étapes déclarées, défaut complet, assertions satisfiables et discriminantes |

### 64.3 Un défaut de **sonde** trouvé par la mesure, pas par la lecture

Premier run de Y : `verdict=True`, et pourtant l'assertion serveur sur la distance échouait. Le motif rendu
à la seconde demande était `cadence : encore 4.5 s avant la prochaine` — la cadence passe **avant** la
portée dans la décision du serveur, et la sonde envoyait ses deux demandes à 100 ms d'intervalle. Le
verdict était donc vrai pour une mauvaise raison : son critère « hors portée refusée » acceptait
**n'importe quel** refus, et la garde de distance n'était pas éprouvée du tout.

Deux corrections, et la seconde compte plus que la première :

1. la sonde attend `EntitySpawnCooldownSeconds + 1,5 s` avant la demande lointaine (commentaire dans le
   code : sans cette attente, la portée n'est pas mesurée) ;
2. `assert_y` affirme désormais le **motif** (`motif=« hors portée`), pas seulement le fait du refus : un
   refus de cadence ne peut plus passer pour un refus de portée.

C'est le troisième défaut de sonde du projet (après B et L, §46) et la même leçon : un critère qui accepte
« un refus » ne dit pas **lequel**, et une assertion qui ne nomme pas le motif laisse la garde visée non
testée.

### 64.4 Ce que ce chapitre ne prouve pas

- **La santé appliquée n'a pas d'effet de jeu mesuré** : la copie porte la fraction, mais ni la profondeur
  de destruction, ni la réparation, ni la destruction réelle ne sont observées. Un `0` **ne tue pas** la
  copie locale — c'est un choix, pas une mesure.
- **La liste blanche n'est éprouvée que sur `Seamoth`** : `Exosuit` est autorisé par le code et n'a pas été
  demandé en jeu.
- **Aucun coût de fabrication** : la sonde appelle `RequestEntitySpawn` directement ; l'intégration au
  menu de construction du jeu (inventaire, matériaux) n'existe pas, et un client modifié peut donc demander
  jusqu'à 8 véhicules gratuits par session. La portée et la cadence bornent le dégât, elles ne l'annulent
  pas.
- **Un seul client dans l'étape Y** : la copie du **demandeur** est vérifiée ; celle d'un second client ne
  l'est pas (l'étape N prouve la création chez deux clients, mais pour une fabrication faite par l'hôte).
- **La persistance n'est pas mesurée** : l'entité fabriquée reçoit un `UniqueIdentifier.Id` **à
  l'exécution** ; rien ne dit qu'elle survit à une sauvegarde/rechargement du serveur.
- **Le plafond de 8 et la cadence ne sont pas mesurés au-delà du premier refus** : Y observe un refus de
  cadence (premier run) et un refus de portée, jamais le plafond.

## 65. Les dégâts des créatures voyagent, leur mort est décidée par le serveur (v17, 2026-10-21)

Premier lot du programme « tout synchroniser ». La ligne du tableau était « dégâts infligés aux créatures /
mort : non ». Deux manques distincts : la **santé** ne voyageait pas (un poisson abattu chez l'hôte restait
intact chez les autres), et la **mort** n'était pas une décision transmise. Une créature détruite côté
serveur cessait simplement d'être annoncée ; au bout de `2 s`, le client la **relâchait** et rétablissait
son IA locale — un mort qui nage.

### 65.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| santé dans l'entrée de créature | `src/Coop.Model/Messages.cs` (`CreatureStateEntry.Health`) | fraction `0..1` (`LiveMixin.health / maxHealth`), **-1 = inconnue**, `0` = morte ; `NaN`/∞ refusé à l'écriture comme à la lecture ; entrée 36 → **40 octets** |
| lecture côté hôte | `src/Coop.Server/ServerNetwork.Creatures.cs` | `LiveMixin` résolu au (re)chargement du cache, fraction relue à chaque diffusion ; une créature morte sort du flux |
| décision de mort | `ServerNetwork.Entities.cs` (`BroadcastDespawn`) + `ServerNetwork.Creatures.cs` | `EntityDespawn` d'espèce **`Creature`** (Kind 3), **une fois** par clé (registre borné à 512), jamais rediffusé |
| application côté client | `src/Coop.Client/RemoteCreatureManager.cs` (`ApplyHealth`) | écrit la fraction sur le `LiveMixin` de la copie **sans la tuer** (pas de `TakeDamage`) |
| retrait côté client | `RemoteCreatureManager.Despawn` / `Forget` + `EntityDespawner` | détruit la copie **sans rendre l'IA**, garde la clé retirée ; si le gestionnaire ne connaît pas la clé (retrait reçu avant chargement), **balayage du monde local** par clé |
| sonde serveur | `src/Coop.Server/CreatureDamageProbeBehaviour.cs` (`-coopdebug=creaturehit`) | attend un client **dans le monde**, blesse une créature réelle à 40 %, puis écrit la mort |
| sonde cliente | `src/Coop.Client/CreatureHealthProbeBehaviour.cs` (`-coopdebug=creaturehealth`) | applique + retrait + clé plus connue → verdict |
| harnais | `tools/verify.sh` (**étape Z**) | six assertions sur les deux journaux, dont le retrait effectif et l'absence de fantôme |

### 65.2 Mesures du 2026-10-21

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet test` | **348/348** rc=0 — 7 nouveaux cas `CreatureHealthTests` (aller-retour, sentinelle, non fini, bornes, taille 40 o, retrait d'espèce créature, espèce inconnue refusée) |
| `NetProbe selftest` | **SUCCES** ; entrée de créature `40` octets, paquet plein mesuré à **651** octets (sous 1 023) |
| étape **Z** (santé + mort) | serveur `créature santé : clé=0x… 'Peeper(Clone)' 20.00 -> 8.00 (max 20.0, écriture effectuée=True)` puis `-> 0.00 (mort écrite=True)` ; `[net] disparition : clé=0x… (créature)` ; client `[créature] retirée : clé=0x…` et `[sonde] créature santé : appliquée=1 dernière=0.40 clé=0x…, retirée=True (… encore connue=False, absente à l'arrivée=0) … verdict=True` ; **étape Z : OK (50 s)** |
| non-régression (créatures, deux clients) | **étape E : OK (98 s)**, `VERDICT GLOBAL : tout est vert` |
| `check-verify-assertions.sh` | rc=0 — **26** étapes déclarées (A..Z), défaut complet, assertions satisfiables et discriminantes |

### 65.3 Deux défauts trouvés par le run, pas par la lecture

**Premier run de Z : `verdict=False`, et pour deux raisons distinctes.**

1. **La sonde tuait trop tôt.** L'hôte blessait puis tuait `12 s` après son propre chargement, alors que le
   client chargeait encore sa scène (`client prêt en 113 s`). Le client a reçu le retrait **avant** de
   connaître la créature : `EntityDespawner` l'a mis en attente, puis l'a appliqué à un registre vide
   (`absente à l'arrivée=1`). La créature a ensuite été chargée **vivante**, et son IA rétablie.
   Correction de mesure : la sonde attend `ServerNetwork.ClientsInWorld > 0` (un client qui a annoncé une
   position, donc qui a chargé) avant de blesser.
2. **Un retrait en attente ne retirait rien** pour une créature. Les objets et les véhicules sont retrouvés
   par balayage après chargement ; les créatures, non — leur registre est construit au premier état.
   Correction de comportement : `EntityDespawner` balaye le monde local par clé quand le gestionnaire ne
   connaît pas la clé, et `RemoteCreatureManager.Despawn` détruit aussi une copie **connue sans être
   adoptée**. La clé reste bloquée pour `TryAdopt` : un état en retard ne ramène pas la créature.

Le second point est un défaut **de produit**, pas de sonde : sans lui, toute créature tuée pendant qu'un
joueur charge son monde restait vivante chez lui, avec son IA. Le premier run de Z est la mesure qui l'a
trouvé ; il est cité ici pour que la correction ne se perde pas.

### 65.4 Ce que ce chapitre ne prouve pas

- **La source du dégât n'est pas exercée** : la sonde écrit la fraction sur `LiveMixin`, elle ne fait pas
  mordre une créature ni ne tire dessus. Le transport et la décision de mort sont mesurés, pas le système de
  dégâts du jeu.
- **Aucune preuve visuelle** : la copie est détruite (clé plus connue), mais aucune capture ne montre le
  cadavre disparaître à l'écran.
- **Une seule espèce** est exercée (`Peeper`), et **un seul client** (l'étape Z ne mesure pas deux clients
  simultanés).
- **La santé des créatures n'est pas visible dans une interface** : elle est appliquée à la copie, pas
  affichée.
- **Les dégâts des joueurs** ne sont pas traités : cette v17 ne concerne que les créatures.

## 66. L'heure du monde voyage : les clients se recalent sur le serveur (v18, 2026-10-21)

Deux instances chargent la même sauvegarde, donc la même heure de départ — puis **avancent chacune de leur
côté**. Un client qui met deux minutes à charger démarre avec l'heure **de la sauvegarde**, pas celle du
serveur qui a continué de tourner : les deux joueurs pouvaient vivre l'un en plein jour et l'autre de nuit,
sans qu'aucun message ne le dise. La v18 transporte l'heure ; la météo, elle, ne voyage toujours pas.

### 66.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| message | `src/Coop.Model/Protocol.cs` (`MessageType.WorldTime` = 26), `Messages.cs` (`WorldTimeMessage`) | `TimePassed` float ; **négatif, `NaN` ou ∞ refusé** à l'écriture comme à la lecture |
| rythme | `Protocol.WorldTimeIntervalTicks = 20` | 1 Hz à un tick de 20 Hz : l'heure avance lentement, vingt envois par seconde n'auraient rien corrigé de plus |
| seuil | `Protocol.WorldTimeApplyThresholdSeconds = 5` | en dessous, le client **n'écrit pas** — voir §66.3, le premier jet à `0,5 s` était mesuré trop bas |
| diffusion | `src/Coop.Server/ServerNetwork.cs` (`BroadcastWorldTime`) | lit `DayNightCycle.timePassedAsFloat`, **vérifie** qu'elle est finie et positive avant d'envoyer (une exception dans la boucle de tick ferait tomber l'instance pour une horloge), puis `Unreliable` à chaque client admis |
| application | `src/Coop.Client/Plugin.cs` (`case MessageType.WorldTime`) | lit l'écart **avant** d'écrire, écrit `DayNightCycle.timePassedAsDouble` au-delà du seuil ; compteurs `messages`, `appliqués`, écart du dernier recalage et **écart maximal corrigé** |
| sonde serveur | `src/Coop.Server/TimeShiftProbeBehaviour.cs` (`-coopdebug=timeshift`) | attend un client **dans le monde**, puis décale l'heure de **+3 600 s** et relit ce qui a été écrit |
| sonde cliente | `src/Coop.Client/TimeSyncProbeBehaviour.cs` (`-coopdebug=time`) | attend un recalage **du même ordre que le saut** (≥ 3 570 s), puis conclut sur le transport **et** sur l'application |
| harnais | `tools/verify.sh` (**étape AA**), `tools/check-verify-assertions.sh` | cinq assertions sur les deux journaux ; l'écart affirmé doit être de l'ordre du saut (`3[0-9]{3}`), pas un recalage de bruit |

### 66.2 Mesures du 2026-10-21

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet test` | **353/353** rc=0 — 5 nouveaux cas `WorldTimeTests` (aller-retour, `NaN`/∞ refusés à l'écriture, négatif refusé à l'écriture **et** à la lecture d'un paquet forgé, rythme et seuil figés) |
| `NetProbe selftest` | **SUCCES** — aller-retour `WorldTime` et trois refus (négatif, `NaN`, ∞) |
| étape **AA** (premier run) | **ÉCHEC** — `verdict=False … écart corrigé=0.6s (attendu ≈3600s)` : le saut **avait** été appliqué (`[temps] recalé … écart de 3601.0 s corrigé`), mais la sonde lisait le **dernier** écart, écrasé par un recalage de bruit arrivé après. Correction de mesure : compteur d'écart **maximal** |
| étape **AA** (second run) | **OK (107 s)** — serveur `[sonde] temps : décalage +3600 s (…, écriture effectuée=True)`, `[net] heure du monde : … s diffusée à 1 client(s)` ; client `[sonde] temps : messages=25 appliqués=5 écart corrigé max=3622.9s (attendu ≈3600s), heure locale=4516.1s dernière heure serveur=4517.4s — verdict=True` |
| non-régression (créatures, deux clients) | étape **E** : voir §66.5 |
| `check-verify-assertions.sh` | rc=0 — **27** étapes déclarées (A..Z, AA), défaut complet, assertions satisfaisantes et discriminantes |

### 66.3 Un seuil mesuré trop bas, et une divergence réelle observée

**Le premier run AA a montré les deux, dans le même journal.**

1. **La divergence existe bel et bien.** Au moment du saut, le serveur était à `900,6 s` de jeu et le client
   — encore au menu, horloge gelée — à `900,6 s` de sauvegarde : le premier recalage vaut `414,3 s`, puis
   `3 601,0 s` après le décalage de la sonde, puis `3 627,8 s` quand le client finit de charger son monde et
   repart de l'heure de la sauvegarde. C'est exactement le défaut visé : un client tardif vit un autre
   moment de la journée. Après le dernier recalage, `heure locale` et `dernière heure serveur` ne diffèrent
   plus que de `1,3 s`.
2. **`0,5 s` était trop bas.** Deux instances du même monde gardent un écart **stable** de `0,6 à 1,1 s`
   (leur horloge n'avance pas exactement au même rythme). Avec un seuil de `0,5 s`, le client réécrivait
   l'heure **à chaque message, une fois par seconde** : `39 application(s) sur 40 message(s)` en 40 s. Un
   soleil qui saute d'une seconde chaque seconde pour courir après un écart constant, invisible autrement.
   Le seuil passe à **5 s** — en dessous du visible (un jour dure 1 200 s), au-dessus du bruit mesuré.
3. **La sonde mesurait le mauvais écart.** Elle lisait l'écart du **dernier** recalage ; le saut de 3 600 s
   était pourtant écrit et journalisé. Le client expose désormais l'écart **maximal** corrigé, et l'étape AA
   affirme sa grandeur (`3[0-9]{3}`), pas seulement l'existence d'un recalage.

Sans le run, les trois points seraient restés invisibles : le code « marchait » — il réécrivait simplement
l'heure en boucle, et la sonde se serait satisfaite d'un écart de `0,6 s`.

### 66.4 Ce que ce chapitre ne prouve pas

- **La météo n'est pas transportée** (`WeatherManager`, aurores, événements de Radiations) : deux clients
  peuvent encore avoir le même soleil et un ciel différent.
- **Aucune preuve visuelle** : le décalage de 3 600 s est mesuré sur les champs, pas sur une capture du
  soleil ni sur un changement de luminosité perçu.
- **Deux instances seulement** : l'étape AA ne mesure pas un troisième client ni une reconnexion ; le
  comportement d'un client qui rejoint alors que le serveur est à mi-journée n'est prouvé que par le
  premier run (client encore au menu), pas par une arrivée tardive volontaire.
- **Le décalage vient de la sonde**, pas d'une horloge qui dérive naturellement de 3 600 s : le transport et
  l'application sont mesurés, pas la dérive réelle sur une session longue (P4).
- **Le seuil de 5 s n'est pas exercé à sa frontière** en jeu : les tests hors jeu figent la constante, aucune
  étape ne mesure un écart de 4,9 s (ignoré) puis 5,1 s (écrit).

### 66.5 Non-régression, et un run E tombé pour la raison déjà connue

L'heure du monde touche la boucle de tick du serveur et la boucle réseau du client : le premier réflexe est
de vérifier que les flux rapides n'ont pas bougé. L'étape **E** (créatures, deux clients) a été rejouée
avec les binaires v18.

| Run | Résultat |
|---|---|
| E, premier essai (2026-10-21, après AA) | **ÉCHEC (230 s)** — `verdict=False` chez le client : `écart de 7.04m entre deux états consécutifs ('Boomerang(Clone)')`, `pas par image de 1.90m en 47ms (40.4 m/s)`, `images longues=2033`, `interruption max=2.2s` avec `écart 17.92m`. Le pas par image dépasse le plafond de `40 m/s` de `0.4 m/s` — c'est le défaut de **mesure sous à-coup** décrit au §60, pas un écart de protocole |
| E, rejeu seul, binaires identiques (2026-10-21) | **OK (97 s)** — aucun `verdict=False`, aucun `verdict=True` manquant |

Le second run est identique au premier **au binaire près** : rien n'a été corrigé entre les deux. C'est donc
un run tombé pour une raison **déjà connue** (la dérive mesurée pendant les images longues, §60), pas une
régression de la v18 — et c'est écrit ici plutôt que masqué. Ce que cela ne prouve pas : que la v18 n'a
**aucun** effet sur la charge du client ; l'étape E ne mesure pas le coût du message d'heure (1 Hz, 5 octets
utiles) et aucun profil n'a été pris.

## 67. Première tranche de l'inventaire : un registre serveur, pas une déclaration (v19, 2026-10-21)

Le lot « inventaire » commence par une question de propriété : **qui tient la liste de ce qu'un joueur
possède ?** Si c'est le client, il peut annoncer n'importe quoi, et le coût de fabrication qui s'appuiera
dessus n'aura aucune valeur opposable. La v19 installe donc le registre sur le serveur, et ne l'expose qu'en
lecture au propriétaire. Rien n'est encore débité : c'est le socle du lot suivant.

### 67.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| registre | `src/Coop.Model/PlayerInventory.cs` | quantités par nom de `TechType`, bornées (`MaxInventoryEntries = 48`, `MaxInventoryStack = 9999`) ; refus **nommés** (`Invalid`, `UnknownItem`, `NotEnough`, `InventoryFull`) ; une ligne à zéro est supprimée ; `Snapshot()` trié par nom ordinal |
| message | `src/Coop.Model/Protocol.cs` (`InventorySnapshot` = 27), `Messages.cs` | révision int ≥ 0, compte ≤ 48, noms ≤ 64, quantités `1..9999` ; **tri et unicité vérifiés** à l'écriture comme à la lecture |
| registre par session | `src/Coop.Server/ServerNetwork.cs` (`ClientSession.Inventory`) | un registre par joueur, détruit avec la session : un nouveau joueur démarre vide, il n'hérite pas du prédécesseur |
| alimentation | `src/Coop.Server/ServerNetwork.Pickup.cs` → `RecordGrantedPickup` | **seul** chemin qui ajoute : un ramassage **nouveau et accordé**. Une réclamation répétée du même joueur est idempotente et n'ajoute pas |
| annonce | `src/Coop.Server/ServerNetwork.Inventory.cs` | instantané à l'admission (révision 0, vide) puis après chaque ajout ; **au propriétaire seul** |
| réception | `src/Coop.Client/Plugin.cs` | applique l'instantané, **écarte et compte** une révision plus ancienne que l'état courant |
| sonde cliente | `src/Coop.Client/InventoryProbeBehaviour.cs` (`-coopdebug=inventory`) | attend l'instantané d'admission, puis un ramassage réel (`-coopdebug=pickup`), puis compare la quantité du serveur à `Inventory.main` |
| harnais | `tools/verify.sh` (**étape AB**) | cinq assertions : admission vide, ajout après ramassage, réception chez le client, **égalité serveur/local**, aucun instantané périmé |

### 67.2 Mesures du 2026-10-21

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet test` | **366/366** rc=0 — 13 nouveaux cas (`PlayerInventoryTests` : retrait trop gros, objet inconnu, bornes de nom/quantité, plafond de pile, plafond d'objets, tri, instantané forgé, révision négative, doublon, ordre faux, trop de lignes) et `MessageFuzzTests` passe à **27** lecteurs |
| `NetProbe selftest` | **SUCCES** — aller-retour `InventorySnapshot` trié + non trié refusé à l'écriture + doublon forgé refusé à la lecture |
| étape **AB** (premier run) | **ÉCHEC** — sonde `verdict=False`, `'PowerCell' serveur=1 local=0` : le registre était juste, la **mesure** lisait trop tôt (voir §67.3) |
| étape **AB** (deuxième run) | sonde `verdict=True` — `messages=2, révision 0 -> 1, 'PowerCell' serveur=1 local=1` — mais l'étape **échoue** sur une assertion mal échappée (§67.3) |
| étape **AB** (troisième run) | **OK (149 s)** — `VERDICT GLOBAL : tout est vert` ; serveur `révision 0, 0 objet(s) (0 unité(s)) — admission` puis `révision 1, 1 objet(s) (1 unité(s)) — ramassage accordé de 'PowerCell'` |
| `check-verify-assertions.sh` | rc=0 — **28** étapes déclarées (A..Z, AA, AB), défaut complet |

### 67.3 Deux défauts trouvés par le run, aucun par la lecture

1. **La sonde lisait avant que le monde n'ait changé.** `serveur=1 local=0` : la décision du serveur arrive
   **dans le préfixe** de `Pickupable.OnHandClick`, alors que `Inventory.Pickup` n'a lieu qu'après. La sonde
   comparait donc un registre déjà à jour à un inventaire de jeu pas encore modifié. Correction de mesure :
   elle attend (jusqu'à 30 s) que les deux quantités se rejoignent au lieu de lire une seule fois. C'est un
   défaut de sonde, pas de produit — mais il aurait pu faire conclure à une divergence inexistante.
2. **Une assertion mal échappée fait tomber une étape verte.** Le journal disait `verdict=True` et
   `serveur=1 local=1` ; l'assertion cherchait `serveur=\[1-9\]`, c'est-à-dire le texte littéral `[1-9]`.
   L'étape a échoué **après** un run réussi. Correction : `serveur=[1-9][0-9]*`, et vérification du motif
   contre le journal conservé avant de relancer. Le run de confirmation est le troisième.

### 67.4 Ce que cette tranche ne fait pas — et qui reste à faire

- **Aucune réconciliation** : le client reçoit et journalise l'instantané, il n'écrit rien dans
  `Inventory.main`. Un objet retiré du registre serveur resterait donc dans l'inventaire du jeu.
- **Aucun débit** : rien ne retire du registre (craft, dépôt, consommation, mort). Le registre ne fait que
  grossir, et un joueur qui consomme un objet rendrait le registre faux dès la première utilisation.
- **La récolte n'est pas arbitrée** : plantes, veines et conteneurs ne passent pas par `Pickupable.OnHandClick`
  de la même façon ; ils restent locaux.
- **Un seul objet, un seul client** mesurés : l'étape AB ne prouve ni un ramassage en rafale, ni deux joueurs
  simultanés, ni le comportement d'un objet empilé.
- **Le rapprochement de noms est celui du jeu** : la sonde compare `TechType.ToString()` au nom transporté ;
  un `TechType` dont le nom d'énumération diffère du nom `AsString` ne serait pas compté (aucun cas observé
  sur `PowerCell`, mais ce n'est pas prouvé pour les 400 objets).
- **Persistance** : le registre vit avec la session. Une reconnexion repart à zéro, même si le joueur a
  toujours les objets dans sa sauvegarde.

## 68. Seconde tranche de l'inventaire : le débit décidé par le serveur, et la réconciliation (v20, 2026-10-22)

La v19 savait **compter ce qui entre**. Elle ne savait pas ce qui **sort** : le registre ne faisait que
grossir, et un objet consommé chez le joueur y restait compté — un coût de fabrication aurait donc été payé
avec des objets déjà mangés. La v20 ferme les deux manques ensemble, parce qu'ils n'ont de sens que l'un
avec l'autre : un **débit** que le serveur décide, et une **réconciliation** qui le rend vrai dans le monde
du client.

### 68.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| message de demande | `src/Coop.Model/Messages.cs` (`InventoryDebit`, #28) | `PlayerId` **revendiqué** (le serveur prend la session), nom ≤ 64, quantité `1..MaxInventoryStack` ; hors bornes refusé à l'écriture comme à la lecture |
| message de décision | `Messages.cs` (`InventoryDebitResult`, #29) | `Granted`, `Reason` (`InventoryChangeOutcome`), `Revision`, `ClaimedBy` ; `Granted` **doit** valoir `Reason == Applied`, sinon le paquet est refusé des deux côtés |
| serveur | `src/Coop.Server/ServerNetwork.Inventory.cs` (`HandleInventoryDebit`) | identité de session, retrait borné par `PlayerInventory.Remove`, refus **nommé**, `InventoryDebitResult` puis instantané au demandeur seul ; un id revendiqué différent est compté |
| réconciliation (modèle) | `src/Coop.Model/InventoryReconciliation.cs` | delta entre l'instantané précédent et le nouveau, **limité aux noms que les instantanés citent** ; ne retire que l'excédent local ; l'écart inverse est compté, jamais fabriqué |
| réconciliation (geste) | `src/Coop.Client/InventoryReconciler.cs` | retrouve l'objet par `TechType.ToString()`, le sort de `Inventory.main` et le **détruit** (un objet consommé ne retourne pas dans le monde) |
| client | `src/Coop.Client/Plugin.cs` | `SendInventoryDebit`, réception du résultat, application de la réconciliation **avant** de remplacer le miroir, révisions périmées toujours écartées |
| sonde | `src/Coop.Client/InventoryProbeBehaviour.cs` (`-coopdebug=inventory`) | après le ramassage : **demande** un débit (sans rien retirer elle-même), attend que `Inventory.main` ait changé, puis demande un second débit qui doit être refusé nommément |
| harnais | `tools/verify.sh` (**étape AC**) | six assertions : débit accordé (révision +1), refus `UnknownItem`, unités réellement retirées localement, `serveur=0 local=0`, verdict de sonde, aucun instantané périmé |

### 68.2 Mesures du 2026-10-22

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet build SubnauticaCoop.sln` | rc=0, **0 avertissement** |
| `dotnet test` | **384/384** rc=0 — 18 nouveaux cas (`InventoryReconciliationTests` : admission qui ne vide rien, ramassage, débit d'une et deux unités, objet jamais cité, serveur en avance, idempotence, tri, compteur négatif ; `InventoryDebitTests` : aller-retours, quantités hors bornes, issue inconnue, accord contredit par l'issue, révision négative) et `MessageFuzzTests` passe à **29** lecteurs |
| `NetProbe selftest` | **SUCCES** — aller-retour `InventoryDebit` et `InventoryDebitResult` (refus nommé), quantité nulle refusée à l'écriture, accord contredit refusé à la lecture |
| étape **AC** (premier run) | **OK (160 s)** — `[sonde] débit : accordés=1 refusés=1 retirés localement=1 échecs=0, 'PowerCell' serveur=0 local=0, révision 1 -> 2, refus nommé=UnknownItem — verdict=True` |
| non-régression | étape **AB** rejouée avec les binaires v20 : **OK (152 s)**, `messages=2, révision 0 -> 1, 'PowerCell' serveur=1 local=1 … verdict=True`, `périmés écartés=0` |
| `check-verify-assertions.sh` | rc=0 — **29** étapes déclarées (A..Z, AA, AB, AC), défaut complet, assertions satisfiables et discriminantes |

### 68.3 Ce que le run a montré, et ce qu'il n'a pas cassé

Le run AC a produit, en plus des lignes affirmées, un écart **transitoire** au moment du ramassage :

```
[inventaire] instantané du serveur : révision 1, 1 objet(s), 1 unité(s) [PowerCell ×1]
[inventaire] réconciliation : 1 objet(s) que le serveur compte et que le jeu n'a pas encore — aucune création locale (limite connue).
```

C'est l'ordre déjà mesuré au §67.3 : la décision du serveur arrive **avant** que `Inventory.Pickup` n'ait
fini d'ajouter l'objet. La réconciliation ne fabrique rien et ne retire rien — elle le dit. Le choix de ne
réconcilier que les noms **cités par les instantanés** est ce qui évite le défaut grave : l'instantané
d'admission est vide, et un miroir naïf aurait vidé l'inventaire sauvegardé du joueur à la connexion. Ce cas
est figé hors jeu (`L_instantane_d_admission_ne_vide_pas_l_inventaire_du_jeu`).

Aucune étape n'a échoué pendant cette tranche ; l'unique run AC est vert, et l'étape AB l'a été après lui
avec les mêmes binaires.

### 68.4 Ce que cette tranche ne prouve pas

- **Aucun consommateur du jeu n'appelle le débit.** La sonde le demande à la place d'un craft, d'une
  nourriture ou d'un dépôt ; le branchement réel appartient au lot du craft, avec la vérification des
  matériaux côté serveur.
- **Les objets équipés ne sont pas réconciliés** : `Inventory.main.container` ne les contient pas, et un
  débit les concernant échouerait en le disant (`échecs=` incrémenté), pas en les retirant.
- **Un seul objet, une seule unité, un seul client** : ni pile, ni débit en rafale, ni deux joueurs
  simultanés ; le refus mesuré est `UnknownItem`, pas `NotEnough`.
- **Le rapprochement de noms reste `TechType.ToString()`**, prouvé sur `PowerCell` seulement.
- **Le registre n'est toujours ni complet ni persistant** : il ne suit que ce que la session a acquis, ne
  connaît pas la sauvegarde, et repart à zéro à la reconnexion — un débit ne « rembourse » jamais (aucun
  message d'ajout n'existe).

## 69. Craft payé : le serveur adopte l'inventaire, le conserve, et vérifie le coût (v21, 2026-10-22)

La v20 savait débiter sur demande, mais la demande venait d'une sonde : aucun craft réel ne la produisait,
et surtout le registre ne connaissait **pas** les matériaux de la sauvegarde. Un joueur avec quarante titanes
aurait vu son premier craft refusé pour « matériau manquant ». La v21 ferme ce trou par une **adoption**
unique à l'admission — la seule déclaration que le protocole accepte —, **conserve** ensuite le registre sur
disque, et arbitre le **coût** d'une recette réelle.

### 69.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| adoption (demande) | `Coop.Model/Messages.cs` (`InventoryAdoptionRequest`, #30) | `Sequence` > 0, envoyé à l'admission ; un paquet tronqué est détectable |
| adoption (réponse) | `Messages.cs` (`InventoryAdopt`, #31) | contenu borné, trié, sans doublon ; refusé en entier sinon |
| conservation | `Coop.Model/InventoryStore.cs` | format `coop-inventory 1` + lignes `Nom\tQuantité`, relecture stricte, écriture par fichier temporaire puis remplacement ; chemin `BepInEx/coop/inventories/<partie>/<pseudo>.inv` (valeurs assainies) |
| coût atomique | `Coop.Model/PlayerInventory.cs` (`RemoveMany`, `TryReplace`) | toutes les quantités vérifiées **avant** la première suppression ; premier manque nommé ; une seule révision par craft |
| craft (demande/décision) | `Messages.cs` (`CraftRequest` #32, `CraftResult` #33, `CraftOutcome`) | `TechType` jamais 0 ; `Granted ⇔ Applied` ; un refus de matériau **doit** nommer le matériau et la quantité manquante |
| serveur | `ServerNetwork.Inventory.cs` | adoption à l'admission (fichier relu puis remplacé), persistance à chaque mutation, recette lue par `TechData.GetIngredients`, prélèvement `RemoveMany`, refus `MissingMaterial`/`NotEnoughMaterial` |
| client | `Coop.Client/Plugin.cs` | adoption **différée** jusqu'au chargement du monde, `SendCraftRequest`, réception et journalisation du résultat |
| sonde | `Coop.Client/CraftProbeBehaviour.cs` (`-coopdebug=craft`) | choisit la recette la moins chère que `Inventory.main` satisfait réellement, la demande, vérifie le prélèvement **et** le retrait local, puis la rejoue pour obtenir le refus nommé |
| harnais | `tools/verify.sh` (**étape AD**) | cinq assertions : adoption envoyée, adoption enregistrée, coût prélevé (révision +1), refus nommé, verdict de sonde |

**L'API des recettes a été vérifiée sur l'assembly du jeu, pas devinée** : `CraftData.GetRecipe` **n'existe
pas** dans 2019.4.36f1 (le compilateur l'a refusé) ; la source utilisée par le fabricator est
`TechData.GetIngredients(TechType)`, qui rend une `ReadOnlyCollection<Ingredient>` dont chaque `Ingredient`
porte `techType` et `amount`. C'est cette méthode que le serveur appelle.

### 69.2 Mesures du 2026-10-22

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet build` | rc=0, **0 avertissement** |
| `dotnet test` | **404/404** rc=0 — 20 nouveaux cas (`PlayerInventoryCraftTests` : coût atomique, manque nommé, sommation de lignes, adoption et validation ; `InventoryStoreTests` : aller-retour, fichier absent, en-tête, ligne mal formée, doublon, bornes, assainissement de chemin) et `MessageFuzzTests` passe à **33** lecteurs |
| `NetProbe selftest` | **SUCCES** — aller-retours `InventoryAdopt`, `CraftRequest`, `CraftResult` (manque nommé) |
| étape **AD** (premier run) | **ÉCHEC** — sonde `verdict=False`, `MissingMaterial : Titanium ×1 (registre 0)` : l'adoption était partie **avant** le chargement du monde, avec `0 objet(s)` (voir §69.3) |
| étape **AD** (deuxième run) | sonde `verdict=True` — `recette='Accumulator' coût=[Titanium ×1] accordé=True révision 2 -> 3, matériaux retirés localement=1/1, refus nommé=True` — mais une assertion serveur ne correspondait pas au format de la ligne (voir §69.3) |
| étape **AD** (troisième run) | **OK (48 s)** |
| non-régression | **AB OK (151 s)** et **AC OK (152 s)** avec les binaires v21, `VERDICT GLOBAL : tout est vert` — l'admission n'envoie plus d'instantané vide, l'assertion d'admission a été réécrite pour l'adoption |
| `check-verify-assertions.sh` | rc=0 — **30** étapes déclarées (A..Z, AA, AB, AC, AD), défaut complet |

Lignes mesurées du run vert :

```
[net] inventaire : 'killi'#1 restauré depuis le disque (2 objet(s), 2 unité(s)) — en attente de l'adoption.
[inventaire] adoption envoyée : 3 objet(s) [Copper ×1, FireExtinguisher ×1, Titanium ×1]
[net] inventaire de 'killi'#1 : révision 2, 3 objet(s) (3 unité(s)) — adoption
[net] craft : 'killi'#1 -> 'Accumulator' ACCORDÉ (coût : Titanium ×1) — révision 2 -> 3.
[inventaire] réconciliation : 1 unité(s) retirée(s) de Inventory.main, échecs=0 (total retiré=1).
[net] craft : 'killi'#1 -> 'Accumulator' REFUSÉ (MissingMaterial : Titanium ×1 (registre 0)) — révision 3 inchangée.
[sonde] craft : recette='Accumulator' coût=[Titanium ×1] accordé=True révision 2 -> 3, matériaux retirés localement=1/1, refus nommé=True (MissingMaterial) — verdict=True
```

### 69.3 Ce que les runs ont trouvé

1. **L'adoption partait trop tôt, et déclarait un inventaire vide.** Le serveur demande l'adoption au
   handshake ; le client répondait immédiatement, or `Inventory.main` n'existe qu'une fois le monde chargé.
   Le premier run a mesuré `adoption envoyée : 0 objet(s) [vide]` alors que la sonde trouvait du titane
   localement — le craft était donc refusé pour une raison qui accusait à tort le registre. Correction : la
   demande est **mémorisée** et l'envoi a lieu dans `Update`, quand `AutoLoadBehaviour.WorldLoaded` et
   `Inventory.main` sont vrais. Le journal dit les deux cas (`différée jusqu'au chargement du monde`).
   C'est un défaut de produit, pas de mesure : un vrai joueur aurait perdu son coût de craft sans comprendre.
2. **Une assertion ne correspondait pas au format écrit.** La ligne serveur fermait la parenthèse après la
   révision (`ACCORDÉ (coût : … — révision 2 -> 3).`) alors que l'assertion attendait `ACCORDÉ (coût : …) —`.
   Le run était fonctionnellement vert, l'étape rouge. Correction du **format** (plus lisible) et non de
   l'assertion : le coût est une information entre parenthèses, la révision une conséquence à côté.

### 69.4 Ce que cette tranche ne prouve pas

- **Aucun menu de craft n'est patché.** La sonde est le seul demandeur ; un joueur qui craft dans le
  fabricator n'obtient ni prélèvement serveur ni refus. C'est la dette la plus visible de ce lot.
  *(Clos par §70 : `CrafterLogic.Craft` est intercepté, bloqué avant consommation, rejoué sur accord.)*
- **Le produit n'est pas instancié.** Le serveur décide le coût ; le client « fabriquera après accord » — le
  branchement et la création de l'objet restent à écrire, et aucune étape ne les mesure.
  *(Clos par §70 : la reprise appelle `CrafterLogic.Craft` sur l'instance interceptée, le produit apparaît
  au ramassage — `produit apparu=True (0 -> 1)` ; le serveur, lui, n'instancie toujours rien.)*
- **L'adoption n'est pas vérifiable.** Le serveur ne peut pas lire la sauvegarde d'un client : un client
  hostile peut déclarer des matériaux qu'il n'a pas. Bornée, triée et unique par session, elle reste une
  déclaration — c'est la limite qui justifie de ne pas s'en servir comme preuve d'intégrité.
- **La conservation est écrasée à chaque admission** : le fichier sert de filet en cours de session et de
  trace lisible, pas d'état de référence entre deux parties.
- **Un objet, une unité, un client, une recette** : la recette choisie est la moins chère (`Accumulator`,
  `Titanium ×1`) ; ni pile, ni craft en rafale, ni deux joueurs simultanés, ni recette à plus de trois
  matériaux. Aucune cadence ni plafond ne borne les demandes de craft (chaque accord consomme le registre,
  donc l'abus est borné par l'inventaire, pas par une règle).
- **L'hôte n'a pas d'adoption** : son registre démarre vide (ou restauré) et ses ramassages ne sont pas
  comptés — le craft mesuré est celui d'un client.

## 70. Le fabricator réel demande l'accord du serveur (2026-10-22)

La v21 arbitrait le coût, mais seul le message `CraftRequest` savait le demander : un joueur qui craftait
dans le fabricator consommait ses matériaux **sans** que le registre ne bouge. Ce lot branche le **vrai**
chemin du jeu — `CrafterLogic.Craft(TechType, float)` — et mesure ce chemin, pas une requête de sonde.

*(Cible corrigée par le §71 : la mesure en **Survie** a montré que `CrafterLogic.Craft` ne consomme rien et
que la consommation est faite par `GhostCrafter.Craft`, appelé par l'action du bouton ; le garde vit
désormais sur `Crafter.PerformAction`.)*

### 70.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| garde | `Coop.Client/CraftGuard.cs` | préfixe Harmony sur `CrafterLogic.Craft` : hors session il laisse passer ; sinon il **bloque l'appel** (`return false`) après avoir envoyé `CraftRequest`, puis **rejoue** l'appel sur la même instance à l'accord, avec un jeton consommé une fois *(cible déplacée par le §71)* |
| protection du coût | `CraftGuard.RememberCost` + `InventoryReconciler.IsCostInFlight` | les matériaux d'un craft approuvé sont **écartés de la réconciliation** pendant 10 s, sinon le monde les retirerait avant que le fabricator ne les lise (et le craft approuvé échouerait, registre déjà débité) |
| mode de jeu | `GameModeUtils.RequiresIngredients()` | la protection ne s'arme que si le jeu consomme réellement : en **Créatif/Freedom** rien n'est consommé, et c'est la réconciliation qui applique le coût |
| sonde | `Coop.Client/CraftProbeBehaviour.cs` | appelle `CrafterLogic.Craft` sur un fabricator **réel** : vérifie le blocage avant consommation, la reprise, l'apparition du produit (`TryPickup`) et le refus sans effet |
| harnais | `tools/verify.sh` (**étape AD**) | sept assertions : garde installé, interception, reprise, coût prélevé, refus nommé, adoption, verdict de sonde |

Pourquoi bloquer plutôt que laisser crafter puis corriger : un craft lancé a déjà consommé et planifié son
produit ; le défaire laisserait un objet fabriqué pour rien. Le prix est une **latence d'un aller-retour**
avant que le fabricator ne démarre — visible, bornée, et préférable à un craft gratuit.

### 70.2 Mesures du 2026-10-22

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet build` | rc=0, **0 avertissement** |
| `dotnet test` | **404/404** rc=0 (aucun nouveau cas hors jeu : le garde est du code Unity, éprouvé en jeu) |
| `NetProbe selftest` | **SUCCES** |
| étape **AD** (runs intermédiaires) | deux échecs de **mesure** : l'arrêt du harnais se déclenchait sur `[sonde] craft : 1 CrafterLogic trouvé(s)` ; puis le produit n'apparaissait pas (`progression maximale=1.00`, `inProgress=True`) parce que le fabricator attend le **ramassage** |
| étape **AD** (run vert) | **OK (52 s)** — `bloqué=True/True, matériaux intacts avant accord=True, accordé=True, rejoué=True, produit apparu=True (0 -> 1), matériaux consommés=True, refus nommé=True (MissingMaterial), rien consommé après refus=True … verdict=True` |
| non-régression | **AB OK (152 s)**, **AC OK (153 s)**, `VERDICT GLOBAL : tout est vert` |
| `check-verify-assertions.sh` | rc=0 — 30 étapes déclarées, défaut complet |

### 70.3 Ce que les runs ont trouvé

1. **Le marqueur du harnais arrêtait le run trop tôt.** `[sonde] craft :` matchait la ligne
   « 1 CrafterLogic trouvé(s) », donc le client était tué avant le verdict. Correction de **mesure** :
   marqueur `[sonde] craft : recette=`.
2. **Le `CrafterLogic` du caisson est désactivé et n'avance que si on l'active.** Sans réactivation, la
   reprise approuvée ne progressait jamais (`inProgress=False`). La sonde l'active pour la mesure et le
   journalise — c'est un état de test, pas une règle de produit.
3. **Le fabricator ne crée l'objet qu'au ramassage.** La progression atteignait `1.00` et restait en
   attente ; c'est le bouton de l'interface qui appelle `TryPickup()`. La sonde fait ce que ce bouton
   ferait, et la ligne le dit.
4. **La sauvegarde de test est en mode Créatif.** Le jeu n'y consomme **aucun** matériau : exiger
   « matériaux consommés localement » était impossible, et protéger le coût de la réconciliation le faisait
   persister indéfiniment (`Titanium` restait à 3 quand le serveur était à 2). Correction : la protection ne
   s'arme que si `GameModeUtils.RequiresIngredients()` est vrai ; en Créatif, la réconciliation applique le
   coût décidé par le serveur. Le journal écrit lequel des deux cas s'applique.

### 70.4 Ce que cette tranche ne prouve pas

- **Une seule instance, un seul produit, un seul client** : un `CrafterLogic` réel (celui du caisson), une
  recette à un matériau (`Accumulator`, `Titanium ×1`), pas de craft en rafale ni de file d'attente.
- **Aucune cadence ni plafond** côté serveur : un client peut demander des crafts en boucle ; chaque accord
  consomme son registre, donc l'abus est borné par l'inventaire, pas par une règle.
- **Le produit reste local** : le serveur arbitre le coût, le client fabrique ; deux joueurs peuvent encore
  avoir chacun le leur (économie non partagée).
- **Un échec de reprise laisse le registre débité** : si le jeu refuse le craft rejoué (recette non
  satisfaite localement), le journal le nomme (`échecs de reprise=`) mais aucun remboursement n'existe —
  le protocole n'a toujours pas d'ajout.
- **La sauvegarde de test est en Créatif** : le chemin « le jeu consomme réellement » (Survie) est codé et
  protégé, mais **mesuré nulle part**. C'est la limite la plus importante de ce lot.
- **Latence non mesurée** : le blocage ajoute un aller-retour avant le démarrage du fabricator ; aucune
  ligne ne chronomètre ce délai.

## 71. Le craft en Survie : le garde remonte au-dessus de la consommation (2026-10-22)

Le §70 prouvait le blocage **en Créatif**, où le jeu ne consomme aucun ingrédient : « matériaux consommés »
y était le fait de la réconciliation, pas du jeu. Ce lot rejoue le même craft sur une **copie en Survie** de
la fixture (`slot0002`, `gameInfo.gameMode` 3 -> 0) et mesure ce qui est réellement consommé. La mesure a
trouvé un défaut de produit : le garde était posé **sous** le point de consommation.

### 71.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| copie de sauvegarde | `tools/verify.sh` (bloc **AE**) | copie `slot0000` -> `slot0002`, écrit `"gameMode":0` (Survie), relit la valeur, joue **explicitement** sur `-coopsave=slot0002`, puis supprime la copie ; `slot0000` n'est jamais modifiée et une copie préexistante n'est jamais écrasée |
| garde remonté | `Coop.Client/CraftGuard.cs` | préfixe Harmony sur `Crafter.PerformAction` — l'action que le bouton du menu déclenche, **avant** `ConsumeEnergy` et `ConsumeResources` — ; la reprise à l'accord rappelle la même action sur la même instance |
| sonde | `Coop.Client/CraftProbeBehaviour.cs` | appelle `((ITreeActionReceiver)crafter).PerformAction(…)` sur un `Crafter` réel ; relève le mode (`GameModeUtils.RequiresIngredients()`), compte les retraits de `InventoryReconciler` avant/après, et **retente** le ramassage tant que la progression est à 1 |
| étape | `tools/verify.sh` (**AE**) | neuf assertions : garde installé, interception, reprise, mode `'Survie'`, coût payé **sans** la réconciliation (`0`), matériaux partis, verdict, et **absence** de la ligne d'exemption Créatif |

### 71.2 Ce que les runs ont trouvé

1. **Le garde était sous la consommation.** Premier run AE, garde encore sur `CrafterLogic.Craft` :
   `matériaux consommés=False`, `coût retiré par la réconciliation=0`, `produit apparu=True` — le joueur
   obtenait un produit **gratuit** en Survie, et le registre serveur était pourtant débité. L'IL du jeu
   explique pourquoi : `CrafterLogic.Craft` ne fait que planifier (`timeCraftingBegin`, `numCrafted`,
   `NotifyChanged`) ; `GhostCrafter.Craft` est `private virtual` et appelle `CrafterLogic.ConsumeEnergy`
   puis `CrafterLogic.ConsumeResources` **avant** de démarrer ; le bouton passe par
   `ITreeActionReceiver.PerformAction`, qui rend `false` si l'action est bloquée. Le garde est donc remonté
   sur `PerformAction` — un seul point, avant toute consommation, pour tous les `Crafter` (fabricator du
   caisson, fabricator du Cyclops, **et** `ConstructorInput` de la baie de montage).
2. **Un ramassage tenté une seule fois peut ne rien faire.** `TryPickupAsync` s'abandonne sans effet si
   `progress < 1` ; avec un seuil de `0,99` déclenché une seule fois, la progression atteignait `1,00` et
   le produit n'apparaissait jamais (mesuré en Créatif après le déplacement du garde : `produit apparu=False
   (0 -> 0)`). La sonde retente maintenant `TryPickup` à chaque cycle de 0,25 s, comme un joueur qui
   reprend son objet.

### 71.3 Mesures du 2026-10-22

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet build` | rc=0, **0 avertissement** |
| `dotnet test` | **404/404** rc=0 |
| `check-verify-assertions.sh` | rc=0 — **31** étapes déclarées, `--only` et le défaut les acceptent toutes |
| étape **AD** (Créatif, après déplacement du garde) | **OK (53 s)** — `mode='Créatif' … coût retiré par la réconciliation=1 … verdict=True` |
| étape **AE** (Survie) | **OK (53 s)** — `recette='Accumulator' coût=[Titanium ×1] … mode='Survie', bloqué=True/True, matériaux intacts avant accord=True, accordé=True, rejoué=True, produit apparu=True (0 -> 1), matériaux consommés=True, coût retiré par la réconciliation=0, refus nommé=True (MissingMaterial), rien consommé après refus=True, échecs de réconciliation=0 — verdict=True` |
| côté serveur, même run | `[net] craft : 'killi'#1 -> 'Accumulator' ACCORDÉ (coût : Titanium ×1) — révision 2 -> 3.` puis `REFUSÉ (MissingMaterial : Titanium ×1 (registre 0)) — révision 3 inchangée.` |
| copie de sauvegarde | `sauvegarde : copie de slot0000 vers slot0002`, `mode de la copie -> 0 (Survie), relu : "gameMode":0`, puis `sauvegarde : slot0002 (copie Survie) supprimée` |

### 71.4 Ce que cette tranche ne prouve pas

- **Un fabricator, une recette, un client** : `Accumulator` (`Titanium ×1`) sur le fabricator du caisson.
  Les autres `Crafter` passent par la même action, mais aucun autre n'est exercé.
- **`RocketConstructor` n'est pas intercepté** : il ne dérive pas de `Crafter` et lance sa construction par
  son propre chemin (`StartRocketConstruction`), où `CrafterLogic.ConsumeResources` est appelé directement.
- **La copie Survie est une conversion** : `gameInfo.json` a été édité, la partie n'a pas été *jouée* en
  Survie. La nourriture, l'oxygène et la mort du joueur n'ont donc pas été observés ; ce que le mode change
  ici — `RequiresIngredients()` et la consommation réelle — l'a été.
- **Le produit reste local** : le serveur arbitre le coût, le client fabrique ; deux joueurs peuvent avoir
  chacun le leur.
- **Aucun remboursement** si la reprise échoue ; la latence de l'accord est mesurée depuis le §73.

## 72. Deux crafts de la même recette, et une cadence nommée (2026-10-22)

Après la mesure en Survie (§71), deux dettes restaient dans le lot craft : la comptabilité des demandes
était indexée par **TechType** (deux crafts de la même recette se recouvraient, et un jeton d'approbation
valait pour n'importe quelle instance de cette recette), et le serveur n'avait **aucune borne de rythme**
— seul le coût était borné, par l'inventaire.

### 72.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| registre des demandes | `Coop.Model/CraftRequestLedger.cs` | une demande par **id de requête** (celui que le serveur renvoie), un **jeton d'approbation par instance** ; id inconnu, recette incohérente, doublon d'id ou table pleine (64) sont refusés sans armer de jeton ; `Clear()` pour une nouvelle session |
| garde | `Coop.Client/CraftGuard.cs` | consomme le jeton de **son** instance au préfixe ; une réponse ne rejoue que l'instance qui a demandé ; une instance perdue retire son jeton au lieu de le laisser traîner |
| cadence serveur | `Coop.Model/CraftBudget.cs` | seau à jetons **entier** (milli-jetons) : 8 en rafale, puis 1 par 500 ms (~120/min) ; le refus rend le délai d'attente |
| refus nommé | `CraftOutcome.RateLimited = 5` (`PlayerInventory.cs`) | nouveau motif, transporté par `CraftResult` (#33) ; révision inchangée, rien n'est débité |
| branchement | `ServerNetwork.Inventory.cs` (`HandleCraftRequest`) | la cadence est testée **avant** toute lecture de recette ; le journal nomme et date le refus (`cadence : N ms avant le prochain jeton`) |
| sonde | `CraftProbeBehaviour` | après la mesure du craft, envoie **12 demandes brutes** (hors garde) et compte les refus `RateLimited` |
| harnais | étapes **AD** et **AE** | deux assertions de plus chacune : verdict de cadence côté client, refus nommé côté serveur |
| fermeture de session | `Plugin.cs` (déconnexion, message `Disconnect`, changement d'adresse) | `CraftGuard.Clear()` oublie demandes et jetons : une réponse tardive ne rejoue rien, et le journal compte ce qui a été oublié |
| id de demande valide | `Coop.Model/Messages.cs` (`CraftRequest`) | `RequestId <= 0` est refusé à l'écriture **et** à la lecture : `0` est le sentinelle « non partie », donc une telle demande ne pourrait pas être appariée à sa réponse |

Le seau est en arithmétique entière : le premier jet était en `double`, et un test a montré qu'une recharge
partielle de `499/500` refusait un jeton pourtant disponible à `500` (arrondi flottant).

### 72.2 Mesures du 2026-10-22

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet build` | rc=0, **0 avertissement** |
| `dotnet test` | **424/424** rc=0 — dont 12 tests `CraftRequestLedger`, 7 `CraftBudget` (aller-retour de `RateLimited` compris) et le refus d'un id de requête invalide |
| étape **AD** (Créatif) | **OK (56 s)** — craft inchangé, puis `[sonde] cadence : demandes=12 refus de cadence=4 (rafale 8, 1 par 500 ms) — verdict=True` ; serveur `REFUSÉ (RateLimited : cadence : 499 ms avant le prochain jeton (rafale 8, 1 par 500 ms)) — révision 3 inchangée.` |
| étape **AE** (Survie) | **OK (56 s)** — le craft consomme toujours réellement le matériau, et la cadence nomme sa rafale |
| `dotnet run --project tools/NetProbe -- selftest` | **SUCCES** — le codec n'a pas de liste d'issues figée : `RateLimited` passe les contrôles existants |
| `tools/session-verdict.sh --self-test` | **SUCCES (8 cas, 0 échec)** — le verrou de la session P4 n'a pas été touché |
| non-régression de l'inventaire | **AB OK (152 s)**, **AC OK (152 s)**, `VERDICT GLOBAL : tout est vert` — le ramassage et le débit ne sont pas touchés par les changements du garde |

### 72.3 Ce que ce lot ne prouve pas

- **La cadence n'est mesurée qu'en rafale de 12** sur un seul client ; le régime établi (~120/min) n'est pas
  chronométré, et la constante n'a pas été choisie par mesure d'usage — c'est une borne de sécurité.
- **Le seau vit en mémoire** : il repart plein à la reconnexion, ce qui est voulu (nouvelle session) mais
  n'empêche pas un client de se reconnecter en boucle pour contourner le rythme (le handshake reste borné
  par le reste du protocole).
- **La purge à la fermeture de session est branchée mais non exercée en jeu** : `CraftGuard.Clear()` est
  appelé sur la déconnexion, sur le message `Disconnect` et sur un changement d'adresse ; aucune étape ne
  ferme une session avec une demande en attente pour vérifier que la réponse tardive ne rejoue rien.
- **La rafale de la sonde envoie des demandes brutes**, hors garde : avec un inventaire plus riche, une
  partie serait **accordée** sans reprise locale (le registre serait débité). La fixture ne le permet
  pas — le premier craft vide le titane — mais un futur décor plus fourni rendrait la sonde bruyante.
- **Rien de nouveau côté contenu** : `RocketConstructor` reste hors du garde, et le produit reste local.

## 73. La latence de l'accord est mesurée, plus supposée (2026-10-22)

Le garde bloque le craft jusqu'à la décision du serveur : c'était le prix assumé du §70, et il était écrit
« non chronométré ». La sonde relève maintenant l'instant de l'interception (`SentAt`, par id de requête)
et l'instant où la décision est traitée, et imprime l'écart.

### 73.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| mesure | `Coop.Client/CraftGuard.cs` | `TakeLatency(requestId)` au traitement de la réponse ; `LastLatencyMs`, `MinLatencyMs`, `MaxLatencyMs` ; la ligne de décision écrit `décision en X ms` et **rien** si la demande n'est pas passée par le garde |
| sonde | `CraftProbeBehaviour` | la ligne de verdict porte `attente de l'accord=X ms (min=…, max=…)` |
| harnais | étapes **AD** et **AE** | assertion `attente de l'accord=[0-9]+\.[0-9] ms` : une valeur absente ou négative fait échouer l'étape |

### 73.2 Mesures du 2026-10-22

| Run | Valeur mesurée |
|---|---|
| étape **AD** (Créatif, 54 s) | accord en **26,6 ms**, refus en **19,4 ms** — `attente de l'accord=26.6 ms (min=19.4, max=26.6)` |
| étape **AE** (Survie, 53 s) | `attente de l'accord=27.4 ms (min=26.0, max=27.4)` — la décision d'un accord et celle d'un refus sont du même ordre |

### 73.3 Ce que cette mesure ne dit pas

- **Localhost seulement** : serveur et client sur la même machine. La latence d'un aller-retour réel
  (Internet) s'y ajoute ; ce qui est mesuré ici est le coût **propre** du garde et de la boucle réseau
  locale, pas l'expérience d'un joueur distant.
- Elle inclut le traitement serveur et la cadence de lecture des paquets du client ; elle n'isole pas la
  part du blocage lui-même.
- Elle ne dit rien du **démarrage** perçu par le joueur (animation, son) : seule la décision est datée.

## 74. La protection des matériaux se voit dans le journal (2026-10-22)

En Survie, le coût d'une recette est **écarté de la réconciliation** pendant la reprise locale
(`InFlightSeconds`, 10 s) : sans cela, la réconciliation retirerait le matériau avant que le jeu ne le
consomme, et la reprise échouerait faute d'ingrédients. Cette branche était **muette** : rien dans un
journal ne disait si la protection avait été armée, ni pour quels matériaux. La seule preuve était une
déduction (`coût retiré par la réconciliation=0`) — une absence d'effet, pas une trace.

### 74.1 Ce qui a été ajouté

| Brique | Fichier | Comportement |
|---|---|---|
| journal | `Coop.Client/CraftGuard.cs` (`RememberCost`) | une ligne par craft protégé : `[craft] matériaux protégés 10 s : Titanium (le craft approuvé les consomme lui-même).` — les noms sont collectés pendant l'armement, donc une recette sans ingrédient lisible ne produit rien |
| harnais, Survie | `tools/verify.sh` (étape **AE**) | `expect` sur `matériaux protégés [0-9]+ s : [A-Za-z]+` : un journal sans protection fait échouer l'étape |
| harnais, hors jeu | `tools/check-verify-assertions.sh` | la fixture **AE** porte la ligne au format du code, la fixture **AD** ne l'a pas : l'assertion reste satisfiable **et** discriminante sans relancer le jeu |
| harnais, Créatif | `tools/verify.sh` (étape **AD**) | `expect_missing` sur `matériaux protégés` : en Créatif, rien n'est protégé, et c'est la réconciliation qui applique le coût — l'assertion croisée interdit de « protéger » par erreur dans ce mode |

### 74.2 Mesures du 2026-10-22

| Contrôle | Valeur mesurée |
|---|---|
| `dotnet build` | rc=0, **0 avertissement** |
| `dotnet test` | **424/424** rc=0 |
| `check-verify-assertions.sh` | rc=0 — 31 étapes, assertions P, Q, N, B, R, AD et AE satisfiables, **20 échecs nommés** après retrait de treize lignes |
| étape **AD** (Créatif, 17:43) | **OK** — `mode='Créatif' … matériaux consommés=True, coût retiré par la réconciliation=1, attente de l'accord=13.3 ms (min=13.3, max=20.0) … verdict=True` ; `grep -c "matériaux protégés"` = **0** |
| étape **AE** (Survie, 17:44) | **OK** — `matériaux protégés 10 s : Titanium (le craft approuvé les consomme lui-même).` puis `mode='Survie' … matériaux consommés=True, coût retiré par la réconciliation=0, attente de l'accord=19.9 ms (min=19.8, max=19.9) … verdict=True` |
| latence, comparée au §73.2 | **13,3 / 19,9 ms** ici contre **26,6 / 27,4 ms** au run précédent : même chemin de code, écart de charge de la machine — ce n'est pas un gain, seulement une mesure de plus |

### 74.3 Ce que ce lot ne prouve pas

- **La fenêtre de 10 s n'est pas éprouvée** : si la décision arrivait après l'expiration, la réconciliation
  pourrait retirer le coût avant la reprise. Aucune étape ne retarde la réponse au-delà de la fenêtre.
- **Un seul matériau journalisé** : `Accumulator` n'a qu'un ingrédient ; une recette à plusieurs ingrédients
  n'est pas exercée, et le format de la ligne n'est pas testé pour les cas vides ou l'échec de lecture.
- **L'absence en Créatif est une assertion négative** : elle prouve que la branche n'est pas armée, pas que
  la réconciliation applique le bon coût au-delà du compteur `1` déjà mesuré au §70.
