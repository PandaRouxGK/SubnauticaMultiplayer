# Subnautica Coop — serveur autoritaire

Coopération pour Subnautica (2019.4.36f1, Mono) : une instance de jeu sert de **serveur
autoritaire** (BepInEx 5 + HarmonyX), les autres instances sont des clients. Transport UDP
LiteNetLib, protocole binaire maison **v13**, tick serveur à **20 Hz**.

L'architecture et l'historique des mesures vivent dans `PLAN.md`. La politique d'autorité système
par système vit dans `docs/authority.md`. Ce README ne dit que ce qui est vrai aujourd'hui.

## État au 2026-10-19

**Mesuré en jeu, avec instrument :**

- serveur headless (`-batchmode -nographics`) : monde chargé sans intervention, tick 20 Hz tenu ;
- présence : avatars distants (tenue, objet tenu, PDA, animation, couleur), interpolation 20 Hz ;
- créatures : IA locale **coupée**, position serveur à 10 Hz, interpolation cliente mesurée image
  par image ;
- véhicules : position à 10 Hz + interpolation, conduite **arbitrée** (un conducteur, libération),
  création et retrait d'entité avec l'identité du serveur ;
- ramassage : première réclamation accordée, suivantes refusées avec le détenteur nommé, `PlayerId`
  falsifié réattribué à la session ; l'objet disparaît chez les autres ;
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
- **batterie des vingt-et-une étapes (A–U)** : les dix-sept premières ont un verdict vert le 2026-10-19
  (`PLAN.md` §46) ; `R`, `S` et `T` prouvent les refus P0 en jeu (slot, session, mode — client divergent
  refusé nommément, l'autre admis), et `U` la reconnexion (départ diffusé, réclamations libérées, retour
  admis, aucun avatar fantôme) (`PLAN.md` §49.5 et §52). `tools/verify.sh` sans `--only` lance bien les
  vingt-et-une.
  Deux étapes étaient tombées pour un défaut de **sonde**, pas du protocole : B comptait l'avatar de
  l'hôte comme un pair (corrigé : attente d'un **autre client**), et L téléportait le joueur de 16 m
  en une image — refusé par la garde P1 — avant d'être réclamé (corrigé : approche à 10 m/s et attente
  de l'avatar du pair).

**Reste non mesuré en jeu :** le refus d'une sauvegarde **différente** (26 cas hors jeu) et la
**reconnexion complète** — claims, conducteurs, resynchronisation de base.

**Ce qui n'existe pas encore :** inventaire, craft, dégâts, oxygène et bases partagés ; énergie,
modules et dégâts de véhicule ; reconnexion d'un client tué puis relancé ; contenu custom livré au
client ; authentification ou chiffrement.

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
- client : adresse du serveur dans `BepInEx/config/coop.subnautica.client.cfg`, sections
  `[Reseau] Hote` / `Port` et `[Joueur] Pseudo` (créé au premier lancement) ;
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

## Vérifier

| Commande | Durée | Ce qu'elle couvre |
|---|---|---|
| `dotnet test` | secondes | logique pure : empreinte de sauvegarde, plausibilité, définition/hash, messages, bornes de lecture |
| `dotnet run --project tools/NetProbe -- selftest` | secondes | aller-retour de chaque message, refus des paquets tronqués et des `NaN` |
| `tools/deploy.sh --verify` | secondes | binaires installés = manifeste |
| `tools/check-verify-assertions.sh` | secondes | toutes les étapes déclarées sont acceptées par `--only` **et** lancées par le défaut, et les assertions P/Q/N/B/R acceptent les formats de journal **et** échouent quand une ligne exigée manque (sans lancer le jeu) |
| `tools/verify.sh --only=A` | ~1 min + chargement | un contrôle **en jeu** (voir l'en-tête de `verify.sh` pour A–U) |
| `tools/verify.sh` | long (~40 min) | les vingt-et-une étapes, un seul verdict |

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
