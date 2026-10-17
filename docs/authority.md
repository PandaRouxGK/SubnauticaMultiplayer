# Politique d'autorité — état et méthode

Ce document suit la méthode prescrite par `PLAN.md` §5, en trois étapes **par système** :

1. écrire une ligne : **ce que le client ne doit plus décider** ;
2. écrire le patch qui le lui retire ;
3. écrire un contrôle qui montre qu'une valeur falsifiée est **rejetée** (et non simplement « corrigée dans
   le cas nominal »).

L'étape 3 est la plus souvent sautée, et elle est la seule qui vaille : une autorité qu'on n'a jamais vue
rejeter quelque chose n'est pas une autorité, c'est une convention. Ce document dit donc, pour chaque
système, **ce qui rend une violation visible aujourd'hui** — un compteur, une sonde, une ligne de journal —
et signale franchement les systèmes où il n'y a rien.

---

## 1. Systèmes dont l'autorité est en place

| Système | Ce que le client ne doit plus décider | Où c'est retiré | Ce qui rend une violation visible aujourd'hui |
|---|---|---|---|
| **Créatures** | leur position et leur IA : le serveur simule, le client suit, **coupe son IA locale** et **glisse** sa position entre deux annonces au lieu de l'appliquer d'un coup | `Coop.Server/ServerNetwork.cs` (`BroadcastCreatureSnapshots`) · `Coop.Client/RemoteCreatureManager.cs` (segment `From/To`, `AdvanceInterpolation`) · `Coop.Shared/CreatureIdentity.cs` (`SetAiScheduled`) | compteurs du suivi : `[créature] état : adoptées=… (IA arrêtée=…) appariés=… clé inconnue=… clés partagées max=… dérive max=… pas par image max=… image(s) animée(s)=…` ; **verdict automatisé** `-coopdebug=creature` (`[sonde] créatures : verdict=True/False`, huit critères dont le pas par image) ; rapport serveur par destinataire (`diffusée(s)`, `sacrifiée(s)`, `clés partagées=`, `débit=`) ; ligne d'adoption avec l'index d'IA **relu** (`IA coupée index 260 -> 0 … lue après l'appel : 0`) |
| **Couleur de joueur** | la couleur de **son propre** personnage : c'est le serveur qui l'assigne, un client ne peut ni la choisir ni la confondre avec celle d'un autre | `ServerNetwork.AllocateColorIndex` + `PlayerColorMessage` · `Coop.Client/RemoteAvatar.cs` (`OnPlayerColor`) | serveur : `[net] couleur assignée à '<pseudo>'#<id> : indice N = rgb(…)` ; client : `[presence] couleur appliquée sur l'avatar de '<pseudo>' (id=…) : indice N rgb(…) -> …` **quand l'avatar existait déjà** — sinon la couleur est « mémorisée » et c'est la ligne d'instanciation qui la porte (`[presence] avatar instancié pour '<pseudo>' (id=…) … couleur N = rgb(…) -> k/k renderer(s) teinté(s)`), ce qui est le cas mesuré à deux clients le 2026-10-17 ; preuve à deux corps de la sonde d'avatar (`-coopdebug=avatar`), `PLAN.md` §27 |
| **Équipement des avatars distants** | l'apparence équipée des autres : le serveur **force `PlayerId`** sur ce qu'il rediffuse, donc un client ne peut pas équiper le personnage d'un autre | `ServerNetwork` (relais `PlayerEquipment`, `PlayerId` écrasé) · `Coop.Client/RemoteAvatarEquipment.cs` | `[presence] équipement local émis (#n, k pièce(s)) : …` ; compteur `equipEnvoyes=` dans la ligne `[ping]` ; sonde d'avatar : **verdict** `[sonde] aller-retour d'équipement : verdict=True/False`, qui porte trois contrôles — l'égalité `ApplyNames(noms)` == `Apply(types)` sur les 14 pièces que les règles connaissent, un **contrôle de discrimination** (les mêmes noms corrompus doivent donner autre chose, sinon l'égalité ne prouve rien), et la lecture locale (autant de noms que de `TechType`, un échec de lecture ne passant pas pour un succès) ; et sonde de **présence** (`-coopdebug=presence`, 2026-10-17) : un joueur **réellement équipé** (`[sonde] présence : équipement local posé — k pièce(s) […] relue(s) par ReadLocalEquipment`) vu équipé par l'autre (`[presence] équipement appliqué sur l'avatar de '<pseudo>' (id=…) : k pièce(s) […] -> n nœud(s) actif(s)`). **La falsification est mesurée** depuis le 2026-10-17 : la sonde envoie un équipement marqué sous l'id de l'autre joueur (`ProbeSendEquipment`), et deux lignes disent l'id effectif — côté serveur `vérification PlayerId (équipement) : … a annoncé l'id X, effectif=Y -> verdict=True` puis `diffusé sous l'id Y (revendiqué X)`, côté client (l'autre) `PlayerId falsifié — '<pseudo>' a annoncé […] sous l'id <le mien> ; le relais me l'a diffusé sous l'id <id réel de l'émetteur> -> verdict=True`. Compteur : `ForgedPlayerIds` |
| **Objet tenu et PDA** | l'objet en main des autres, **et** les paramètres d'animation qui vont avec : ils sont lus sur l'`Animator` de l'émetteur, jamais re-dérivés par le destinataire | `ServerNetwork` (relais `PlayerHeldItem`) · `Coop.Client/RemoteAvatarHeldItem.cs` · `Protocol.HoldingParamPrefix` | `[presence] objet tenu émis (#n) : 'X' holding=[…]` ; `outilsEnvoyes=` dans `[ping]` ; montage du modèle journalisé (`objet tenu : modèle de 'Welder' monté sous attach1`) ; phases PDA de la sonde (`PLAN.md` §25) |
| **Personnage de l'hôte (serveur dédié)** | le serveur ne doit pas exposer de personnage joueur : lancé en `-batchmode`, il ne diffuse **aucun** état `id=9999`, ni à l'admission ni dans les snapshots | `CoopServerPlugin.Dedicated` (`Application.isBatchMode`) · `ServerNetwork` (deux chemins : `HandleHandshake` et `BroadcastSnapshots`) | `[boot] batchmode=True — serveur dédié : aucun personnage joueur ne sera exposé aux clients.` ; `[dédié] serveur sans personnage exposé …` ; côté client `joueursDistants=0 avatarsActifs=0` et **aucune** ligne `avatar instancié pour 'HoteServeur'` ; étape H de `tools/verify.sh`. **Limite mesurée** : le joueur reste simulé côté serveur (Subnautica l'exige) ; le désactiver produit 318 395 exceptions en 28 s, donc il n'est ni affiché, ni piloté, ni diffusé — mais il existe dans la sauvegarde |
| **Positions et animation des avatars distants** | où se trouve l'autre joueur et à quelle vitesse ses bras bougent : le client applique ce qu'il reçoit (`PlayersSnapshot`, 20 Hz) au lieu de simuler | `Coop.Client/RemoteAvatar.cs` (interpolation, `PLAN.md` §22) | `[presence] première position locale émise : seq=… pos=… v=(…) pitch=… sousEau=… surface=…` ; `posEnvoyees=` dans `[ping]` ; l'avatar distant est créé à la position **annoncée** (`avatar instancié pour …`) |
| **Position des véhicules** | où se trouve un véhicule du monde : le serveur le diffuse à 10 Hz (`VehicleSnapshot`), le client l'applique **interpolé** et gèle la physique de sa copie au lieu de la simuler | `Coop.Server/ServerNetwork.cs` (`BroadcastVehicleSnapshots`) · `Coop.Client/RemoteVehicleManager.cs` · `Coop.Shared/VehicleIdentity.cs` | compteurs cliente : `[véhicule] état : suivis=… appariés=… clé inconnue=… écart avant lissage max=… pas par image max=…` ; rapport serveur par destinataire (`diffusé(s)`, `sans clé écarté(s)`, `débit=`) ; **verdict automatisé** `-coopdebug=vehicle` des deux côtés (étape J de `tools/verify.sh`) : `[sonde] véhicule : verdict=True` chez l'hôte (déplacement) et `[sonde] véhicule client : verdict=True` (pas par image ≤ 1 m, aucune image ≥ 1 m). Un véhicule **conduit par le client** est laissé au joueur et compté (`conduits localement=`) — M4 ne transporte pas la conduite, et c'est dit plutôt que masqué |
| **Propriété de la sauvegarde** | écrire dans la sauvegarde du monde : le serveur est propriétaire, le client ne sauvegarde pas | `Coop.Client/SaveGuard.cs` (garde installée **avant** le chargement du monde ; sans elle, le chargement est annulé) — **deux** remparts : l'entrée `IngameMenu.SaveGameAsync` et l'écriture `SaveLoadManager/GameInfo.SaveFile` | autotest explicite `-coopdebug=saveguard` (`PLAN.md` §19, passe A) : `autotest entrée bloquée` **et** `autotest écriture bloquée` (l'écriture est appelée **directement**, hors du menu), puis `sauvegarde cliente bloquée (entrée+écriture) -> verdict=True` ; le plugin journalise les deux signatures patchées. Étape D de `tools/verify.sh`. **La moitié positive est mesurée depuis le 2026-10-17** : l'étape F déclenche `IngameMenu.SaveGame()` **côté hôte** pendant qu'un client est connecté et relève `31 fichier(s) écrit(s) ou modifié(s)` dans le dossier temporaire de la sauvegarde, puis `sauvegarde serveur pendant un run -> verdict=True` — l'hôte peut donc encore écrire, et c'est la garde cliente qui l'en empêche, pas un défaut de la sauvegarde. **Le launcher s'en sert** (2026-10-17) : son bouton « Sauvegarder » n'a aucun accès réseau au jeu — il écrit un jeton dans `BepInEx/coop/save.request`, `ServerControlBehaviour` le relit toutes les 500 ms, appelle `IngameMenu.SaveGame()` et répond `jeton|ok|message` dans `save.ack`. Le jeton est unique par clic, donc deux clics demandent bien deux sauvegardes, et le launcher n'accepte un accusé que pour le jeton qu'il a émis |

**Les verdicts automatisés sont ceux des trois sondes** : `-coopdebug=creature` (sept critères, une erreur
écrite par critère manqué) et `-coopdebug=avatar` (rendu isolé, cinq tenues, deux couleurs, deux corps,
aller-retour d'équipement) et `-coopdebug=presence` (équipement réellement porté, changement d'équipement vu
par l'autre joueur, couleur appliquée à un avatar déjà instancié, indice du serveur restauré, `PlayerId`
falsifié réécrit par le relais, vrai outil tenu puis émis, nage et arrêt vus par l'autre). Depuis le
2026-10-17, `tools/run-session.sh` **fait échouer le run** (code de sortie 1) dès qu'un `verdict=False`
apparaît dans un journal de client, et `tools/verify.sh` rejoue les **dix** runs (A avatar + créatures,
B deux clients en sonde de présence, C plafond abaissé, D autotest de la garde cliente, E créatures seules à
deux clients, F sauvegarde de l'hôte, G Seamoth en mouvement, H serveur dédié, I menu Échap du client,
J diffusion et interpolation du véhicule) et rend un verdict unique :
lire un journal n'est plus nécessaire pour savoir qu'un contrôle est tombé. Les autres lignes de la colonne de droite sont des
**compteurs et des relevés** : elles rendent une violation visible, elles ne la verdictent pas.

---

## 2. Systèmes qui restent à retirer au client

| Système | Ce que le client décide encore | Jalon | Ce qui manque pour le prouver |
|---|---|---|---|
| **Véhicules (Seamoth, Exosuit) — le reste** | **l'énergie** (consommation, recharge, panne), **les upgrades** (modules, dégâts, énergie du module) et **la conduite** : un client qui pilote son Seamoth le simule localement, et rien ne remonte vers le serveur. La **position**, elle, est désormais autoritaire (ligne ci-dessus) | M4 (`PLAN.md` §6 et §33) | les messages d'énergie/modules, un flux montant de conduite, et le refus serveur d'une valeur falsifiée. Mesuré le 2026-10-17 (étape J) : diffusion à 10 Hz d'un Seamoth à 20 m/s (`2,25 m` par annonce) et interpolation cliente (`0,39 m` par image au maximum sur la fenêtre de mesure, aucune image ≥ 1 m sur 10 259 images de session) — la partie **position** est faite |
| **Première action autoritaire (pickup)** | récolter, ramasser, prendre une ressource : le client décide, et rien ne l'en empêche | M5 | un refus serveur (« déjà réclamé ») **et** le compteur qui montre le refus |
| **Contenu (objets/creatures custom)** | la définition même de ce qui existe dans le monde | M6 | la livraison d'un AssetBundle côté client, et la vérification que le serveur refuse une définition falsifiée |

---

## 3. Ce que ce document ne prétend pas

- **Aucun test de rejet unitaire n'existe** pour la couleur, l'objet tenu ou l'animation. Ce qui existe est
  ce que l'étape 3 demande dans son esprit — *voir* la violation — mais pas sous forme de test exécutable ;
  les seules exceptions sont la sonde de créatures, l'aller-retour d'équipement de la sonde d'avatar (qui
  vérifie le canal des noms **et** se prouve discriminant) et `tools/NetProbe selftest` pour la sérialisation.
  Aucun de ces contrôles n'est un test *unitaire* : ils s'exécutent dans le jeu, sur un run complet.
- **« Le serveur force `PlayerId` » ne se lit plus seulement dans le code** (2026-10-17) : l'étape B de
  `tools/verify.sh` envoie un équipement marqué sous l'id d'un autre joueur et affirme que le message
  revient **sous l'id réel de l'émetteur**, côté serveur (`diffusé sous l'id Y (revendiqué X)`) comme côté
  client. Ce qui reste hors périmètre : les **autres** champs d'état (position, drapeaux d'action, nom
  d'objet tenu) ne sont pas validés par le serveur — il les relaie, cf. §2.
- Les instruments cités ci-dessus vivent dans les journaux d'instance
  (`BepInEx/logs/coop-client.log`, `coop-server.log`, et `coop-client-<n>.log` pour un client
  supplémentaire, cf. `tools/run-session.sh --clients=N`). Un compteur absent d'un journal n'est pas une
  preuve d'absence de violation : chaque ligne doit être lue avec son propre périmètre.
