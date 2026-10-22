# Subnautica Coop — serveur autoritaire

Coopération pour Subnautica (2019.4.36f1, Mono) : une instance de jeu sert de **serveur
autoritaire** (BepInEx 5 + HarmonyX), les autres instances sont des clients. Transport UDP
LiteNetLib, protocole binaire maison **v21**, tick serveur à **20 Hz**.

L'architecture et l'historique des mesures vivent dans `PLAN.md`. La politique d'autorité système
par système vit dans `docs/authority.md`. Ce README ne dit que ce qui est vrai aujourd'hui.

## État au 2026-10-22

**Mesuré en jeu, avec instrument :**

- serveur headless (`-batchmode -nographics`) : monde chargé sans intervention, tick 20 Hz tenu ;
- présence : avatars distants (tenue, objet tenu, PDA, animation, couleur), interpolation 20 Hz ;
- créatures : IA locale **coupée**, position serveur à 10 Hz, interpolation cliente mesurée image
  par image, **santé transportée** et **mort décidée par le serveur** (le client retire sa copie) ;
- véhicules : position à 10 Hz + interpolation, conduite **arbitrée** (un conducteur, libération),
  création et retrait d'entité avec l'identité du serveur, **énergie**, **modules** et **santé**
  transportés et appliqués sur la copie locale ;
- **fabrication demandée par un client** : le client ne fabrique rien, le serveur arbitre (espèce,
  portée de 8 m, cadence, plafond) puis annonce l'entité à tout le monde — demandeur compris ;
- ramassage : première réclamation accordée, suivantes refusées avec le détenteur nommé, `PlayerId`
  falsifié réattribué à la session ; l'objet disparaît chez les autres ;
- **heure du monde** : le serveur diffuse son `DayNightCycle` à 1 Hz, le client se recale au-delà de
  5 s d'écart — un client qui charge pendant que l'hôte avance ne démarre plus de nuit quand il fait jour ;
- **inventaire** : le serveur tient un registre par joueur (borné) et l'annonce à son propriétaire ; un
  ramassage accordé y ajoute l'objet, et la quantité annoncée égale celle que le jeu contient chez le
  client ; un **débit demandé** par le client est appliqué ou **refusé nommément** par le serveur, et
  l'unité retirée du registre quitte réellement `Inventory.main` (réconciliation) ; l'inventaire réel du
  joueur est **adopté** une fois à l'admission, puis **conservé sur disque** par le serveur ;
- **craft payé** : le serveur lit la **recette du jeu** (`TechData.GetIngredients`), prélève les matériaux
  **atomiquement** sur son registre et refuse en nommant le manque ; l'**action du fabricator**
  (`Crafter.PerformAction`, celle que le bouton du menu déclenche) attend cet accord avant de consommer
  l'énergie ou les matériaux, puis le craft est rejoué — le produit, lui, reste fabriqué par le client ;
  une rafale de demandes est refusée nommément par le serveur (`RateLimited`) ; la **protection des
  matériaux** pendant la reprise est tracée, levée dès que le craft est joué ou refusé, un retrait tombé
  dans cette fenêtre est **reporté puis repris à la levée**, et la **purge de fin de session** est
  exercée en jeu (`PLAN.md` §74–§81) ;
- sauvegarde : le client n'écrit pas (garde sur deux remparts), l'hôte écrit toujours ;
- hôte dédié : aucun personnage joueur exposé aux clients ;
- **empreinte de sauvegarde (P0)** : handshake v13 accepté avec `save=slot=…` ; étape P verte ;
- **garde de position (P1)** : étape P verte — nage 6 s à 1,25 m/s **0 correction**, téléport de
  100 m **refusé**, resynchronisation journalisée, correction **reçue et appliquée** (2 appliquées) ;
- **rejeu des retraits (P2)** : étape Q verte — un client arrivé après la destruction reçoit le
  retrait rejoué, le met en attente avant son chargement, constate l'absence et ne recrée rien
  (`entités créées ici=0`) ;
- **contrat de contenu (P1bis)** : étape N verte — `définition vérifiée=True (hash=0x3da1f4bf782becdf)`
  et `définition falsifiée refusée=True` chez **les deux** clients, `définitions refusées=0` ;
- **batterie des trente et une étapes (A–Z, AA, AB, AC, AD, AE)** : les dix-sept premières ont un verdict vert le 2026-10-19
  (`PLAN.md` §46) ; `R`, `S` et `T` prouvent les refus P0 en jeu (slot, session, mode — client divergent
  refusé nommément, l'autre admis), `U` la reconnexion (départ diffusé, réclamations libérées, retour admis, aucun avatar fantôme),
  `V` la présence à **trois** clients et `W` les créatures à trois clients, avec les états périmés
  écartés (`PLAN.md` §49.5, §52 et §53) ; `X` la libération du volant par une déconnexion, `Y` la
  fabrication demandée par un client (`PLAN.md` §64), `Z` la santé et la mort d'une créature
  (`PLAN.md` §65), `AA` l'heure du monde diffusée puis appliquée (`PLAN.md` §66), `AB` le registre
  d'inventaire du serveur confronté au ramassage réel, `AC` le débit accordé puis refusé avec le retrait
  réel dans `Inventory.main` (`PLAN.md` §67 et §68) et `AD` l'adoption de l'inventaire, le coût d'une vraie
  recette prélevé puis refusé nommément (`PLAN.md` §69), le **fabricator réel** bloqué avant consommation
  puis rejoué sur accord (`PLAN.md` §70), et `AE` le même craft en **Survie**, sur une copie non-Créative de
  la fixture : le jeu y consomme lui-même les ingrédients, sans la réconciliation (`PLAN.md` §71).
  `tools/verify.sh` sans `--only` lance bien les trente et une.
  Deux étapes étaient tombées pour un défaut de **sonde**, pas du protocole : B comptait l'avatar de
  l'hôte comme un pair (corrigé : attente d'un **autre client**), et L téléportait le joueur de 16 m
  en une image — refusé par la garde P1 — avant d'être réclamé (corrigé : approche à 10 m/s et attente
  de l'avatar du pair).

**Reste non mesuré en jeu :** la **session de 30 min** (P4), une coïncidence **spontanée** d'un retrait tombé dans la fenêtre de protection (la sonde la force, `PLAN.md` §78.3), la marche (pas de terre ferme près de la
capsule de test), plus de 8 joueurs, et des sauvegardes ou zones différentes ; la désynchronisation des
véhicules et des avatars n'est pas mesurée non plus (`PLAN.md` §62.4).

**Ce qui n'existe pas encore :** récolte, oxygène et bases partagés ; l'**économie partagée** (le produit
reste local) ; le branchement du débit sur les autres consommateurs du jeu (nourriture, dépôt) ; la
réconciliation des objets équipés ; contenu custom livré au client ; authentification ou chiffrement.

## Prérequis

| Élément | Valeur |
|---|---|
| Subnautica | 2019.4.36f1, build Steam `20241558` |
| BepInEx | 5.4.21 x64 (Mono, pas IL2CPP) |
| SDK .NET | 9.x (build ; les plugins ciblent `net472`) |
| OS | Windows (les harnais tournent sous Git Bash) |

## Construire et installer

```bash
tools/deploy.sh                 # build Release + copie dans BepInEx/plugins/Coop + manifeste
tools/deploy.sh --verify        # recalcule les sha256 et vérifie la version de protocole
tools/deploy.sh --no-build      # réinstalle des binaires déjà construits
tools/deploy.sh --game-dir="D:/Jeux/Subnautica"
```

Le dossier du jeu vient de `COOP_GAME_DIR`, sinon `SUBNAUTICA_GAME_DIR`, sinon l'emplacement Steam
par défaut. Le script n'écrit que dans `BepInEx/plugins/Coop/` et ne touche jamais `SNAppData/`.

Fichiers installés : `Coop.Client.dll`, `Coop.Server.dll`, `Coop.Model.dll`, `LiteNetLib.dll`, plus
`mod.manifest.json` (version de protocole, commit, tailles, sha256). Le modèle versionné est
`tools/mod.manifest.json` ; `deploy.sh` refuse d'installer si sa version ne correspond plus au code.

## Lancer

Le rôle vient de la ligne de commande — jamais d'un fichier de config, puisque les deux instances
partagent le même dossier `BepInEx/config`.

```bash
cd "/c/Program Files (x86)/Steam/steamapps/common/Subnautica"

# serveur dédié, sans fenêtre ni personnage exposé
./Subnautica.exe -coopmode=server -batchmode -nographics

# client (charge la même partie ; la garde de sauvegarde l'empêche d'écrire)
./Subnautica.exe -coopmode=client
```

- port UDP **7777** ;
- client : l'adresse du serveur se **saisit dans le menu** — panneau « Coop — rejoindre un serveur »
  (champ adresse, champ port, bouton « Rejoindre ») — puis elle est mémorisée dans
  `BepInEx/config/coop.subnautica.client.cfg`, section `[Reseau] Hote` / `Port` ; le pseudo vit dans
  `[Joueur] Pseudo` (créé au premier lancement) ;
- **sans adresse, le client reste au menu** : il ne charge aucune partie et ne tente aucune connexion.
  Deux façons de la fournir sans passer par le panneau : `-coopserver=203.0.113.10:7777` (ou
  `COOP_SERVER`), ou le fichier de configuration. Une adresse invalide est **refusée avec sa raison**
  (`port invalide`, `adresse IPv4 invalide`, `IPv6 non pris en charge`…) et ne retombe jamais sur
  `127.0.0.1` ;
- serveur : `BepInEx/config/coop.subnautica.server.cfg`, section `[Reseau] Port` ;
- variable d'environnement équivalente : `COOP_MODE=server|client` ;
- sauvegarde imposée (utile pour le harnais, et pour deux parties différentes côte à côte) :
  `-coopsave=slot0001` ou `COOP_SAVE_SLOT=slot0001`. Sans ce réglage, le menu charge la première
  sauvegarde venue. Une valeur invalide **n'ouvre aucune partie** : l'instance reste au menu avec le
  motif dans le journal, plutôt que de charger silencieusement une autre sauvegarde ;
- journaux séparés par instance : `BepInEx/logs/coop-server.log`, `coop-client.log`,
  `coop-client-<n>.log` ;
- un launcher existe **hors de ce dépôt** (démarrage, arrêt, sauvegarde par fichier de contrôle).

Le serveur est une instance complète du jeu : il consomme ~2 Go et garde un device graphique même
avec `-nographics`. Ce n'est pas un binaire serveur.

## Jouer à distance (deux réseaux différents)

Le serveur écoute sur **toutes** les interfaces (`UDP 0.0.0.0:7777`) : rien à changer du côté du code.
Ce qui reste à faire est du réseau, chez l'hôte :

1. **Rediriger le port UDP 7777** vers la machine qui héberge le serveur (box/routeur), et autoriser
   l'application dans le pare-feu Windows (profil privé **et** public) ;
2. **Communiquer son IP publique** aux autres joueurs — chaque client la tape dans le panneau, avec
   `:7777` si le port redirigé n'est pas celui par défaut ;
3. si la connexion échoue alors que le port est bien redirigé, le cas le plus courant est un partage
d'adresse par l'opérateur (CGNAT) : un réseau privé virtuel entre les joueurs contourne le problème,
mais **rien de tel n'est intégré au mod**. Il n'y a ni lobby, ni relais, ni traversée de NAT : l'IP
publique (ou l'adresse du réseau privé) doit être saisie à la main.

Limite de sécurité, inchangée : ni authentification ni chiffrement. Un serveur exposé sur Internet
accepte n'importe qui connaissant l'IP, la clé LiteNetLib ne filtrant que les paquets malformés.

## Vérifier

| Commande | Durée | Ce qu'elle couvre |
|---|---|---|
| `dotnet test` | secondes | logique pure : empreinte de sauvegarde, plausibilité, définition/hash, messages, bornes de lecture |
| `dotnet run --project tools/NetProbe -- selftest` | secondes | aller-retour de chaque message, refus des paquets tronqués et des `NaN` |
| `tools/deploy.sh --verify` | secondes | binaires installés = manifeste |
| `tools/check-verify-assertions.sh` | secondes | toutes les étapes déclarées sont acceptées par `--only` **et** lancées par le défaut, et les assertions P/Q/N/B/R acceptent les formats de journal **et** échouent quand une ligne exigée manque (sans lancer le jeu) |
| `tools/session-verdict.sh --self-test` | secondes | le graveur de session longue accepte un journal vert **et** échoue sur les six cas rouges (exception, dérive > 2 m, mesure vide, sauvegarde absente, reconnexion absente, durée courte) |
| `tools/verify.sh --only=A` | ~1 min + chargement | un contrôle **en jeu** (voir l'en-tête de `verify.sh` pour A–AE) |
| `tools/verify.sh` | long (~45 min) | les vingt-huit étapes, un seul verdict |
| `tools/p4-session.sh --minutes=30` | 30 min + chargement | une session longue à deux clients (reconnexion au tiers, sauvegarde de l'hôte) et **un** verdict P4 (`PLAN.md` §62) |

`verify.sh` et `run-session.sh` lancent de vraies instances : ils exigent le jeu, une sauvegarde
`slot0000` et Git Bash. Les tests hors jeu ne remplacent pas ces runs — ils les rendent moins
souvent nécessaires.

## Carte du dépôt

```
src/Coop.Model/    protocole, messages, empreinte de sauvegarde, plausibilité, définitions — aucun Unity
src/Shared/        code compilé dans les deux plugins (identités, autoload, journal d'instance)
src/Coop.Client/   plugin client : réseau, avatars, créatures, véhicules, gardes, sondes
src/Coop.Server/   plugin serveur : admission, diffusion, décisions, canal du launcher
tests/             tests hors jeu de Coop.Model
tools/             ApiDump, NetProbe, deploy.sh, run-session.sh, verify.sh
docs/              protocole, politique d'autorité, table des preuves, comptes rendus de conseil
```

## Limites connues

- **Le joueur n'est pas simulé par le serveur.** Sa position est relayée, avec une garde de
  plausibilité et une correction opposable. Le mot « autoritaire » s'applique aux créatures, aux
  véhicules, au ramassage, à la couleur et au cycle de vie des entités — pas au mouvement du joueur.
- **La partie doit être la même.** L'empreinte au handshake refuse une sauvegarde différente, plus
  ancienne ou altérée, avec un motif lisible. Elle ne garantit pas que deux fichiers soient
  identiques octet par octet.
- **L'heure est partagée, pas le ciel.** Le serveur diffuse l'heure (`WorldTime`, v18) ; la météo, les
  aurores et les événements de Radiations ne voyagent pas — deux joueurs peuvent voir le même soleil et
  un temps différent.
- **L'inventaire serveur est un registre, pas celui du jeu.** Il compte des quantités par objet, s'alimente
  d'un ramassage accordé et de l'adoption initiale (la seule déclaration cliente, non vérifiable), se
  débite sur demande et au coût d'un craft, et se réconcilie avec `Inventory.main` ; il ignore en revanche
  les emplacements, les tailles de pile et les objets équipés.
- **L'économie n'est pas partagée.** Deux joueurs peuvent encore avoir chacun leur copie d'un objet
  fabriqué ou consommé ; seul le ramassage d'un objet du monde est arbitré.
- **Une seule sauvegarde et un seul biome ont servi de preuve.** Les identités de créatures reposent
  sur la position d'apparition ; c'est valable pour une même partie chargée des deux côtés.
- **Pas de sécurité.** Aucune authentification, aucun chiffrement ; la clé LiteNetLib filtre les
  paquets parasites, elle n'identifie personne.
- **Les sondes en jeu restent la seule preuve du comportement de gameplay.** Les tests hors jeu
  couvrent la logique et la sérialisation, pas le rendu ni la physique du jeu.

## Documents

- `PLAN.md` §43 — revue d'architecture et séquence P0…P5 (ce qui est fait, ce qui est gelé).
- `docs/authority.md` — pour chaque système, ce que le client ne décide plus et ce qui rend une
  violation visible.
- `docs/protocol.md` — message par message, avec la source de chaque constante.
- `docs/preuves.md` — quelle affirmation repose sur quel instrument, et son statut.
