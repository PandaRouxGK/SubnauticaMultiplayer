# Contenu de base du jeu : ce qui est synchronisé, et ce qui ne l'est pas

Ce document répond à une question précise : **pour chaque famille de contenu de Subnautica, qu'est-ce qui
converge entre deux instances, et qu'est-ce qui reste local ?** Il ne décrit pas ce qui serait souhaitable,
et il ne remplace ni `docs/authority.md` (qui décide quoi) ni `docs/preuves.md` (ce qui est mesuré).

## Le principe, avant les tableaux

Les deux instances chargent la **même sauvegarde** — slot, session et mode de jeu sont un contrat vérifié
au handshake, avec un refus **nommé** si l'un diverge (P0, étapes P/R/S/T). Conséquences :

- tout ce qui est **déjà dans la sauvegarde au chargement** est identique des deux côtés (épaves, ressources
  présentes, créatures inertes, position des véhicules sauvegardés) ;
- ensuite, seuls les systèmes listés ci-dessous rattrapent les changements. **Tout le reste diverge
  silencieusement** : ce n'est pas un défaut de synchronisation, c'est un périmètre non encore couvert.

**Comment lire les statuts :** « Oui » veut dire **mesuré en jeu** avec un instrument, pas « prévu » ;
« Partiel » nomme une limite précise ; « Non » veut dire qu'aucun message ne transporte l'état.

## Joueur

| Contenu | Synchronisé ? | Mécanisme / raison |
|---|---|---|
| Avatar distant (corps, tête, tenue, couleur, animations, objet tenu, geste du PDA) | **Oui** | Protocole v3–v7 ; étapes A et B ; interpolation 0,05 s (`PLAN.md` §21, §22, §24, §27) |
| Position / orientation du joueur | **Partiel** | Le serveur relaie et **borne** (anti-téléport, correction renvoyée) ; pas de prédiction client (`PLAN.md` §43.2, étape P) |
| Équipement porté (réservoir, palmes, combinaison…) | **Oui** | v3 : relecture puis application sur l'avatar réel ; limite : « arrive déjà équipé » et objet tenu non exercés (`docs/preuves.md`) |
| Inventaire (noms, quantités) | **Oui** | Registre serveur, adoption à l'admission, réconciliation dans `Inventory.main` (v19–v20, étapes AB/AC) |
| Objets **équipés** dans le registre d'inventaire | **Non** | Ils vivent hors `Inventory.main.container` : ni adoptés ni réconciliés (README) |
| Santé, oxygène, faim/soif du joueur | **Non** | Aucun message ; chaque client simule le sien |
| Mort et respawn du joueur | **Non** | Aucun message ; un joueur mort localement reste vivant chez les autres |
| PDA : logs, blueprints débloqués, données scannées | **Non** | Progression purement locale |

## Monde et entités

| Contenu | Synchronisé ? | Mécanisme / raison |
|---|---|---|
| Créatures : position, rotation, vitesse, état (nage, sous l'eau) | **Oui** | IA **coupée chez les clients**, streaming serveur à 10 Hz, interpolation (M3, étapes E/W) |
| Créatures : santé | **Oui** | v17, transportée et appliquée (étape Z, `PLAN.md` §65) |
| Créatures : dégâts infligés par un joueur | **Oui** | v17 ; la mort est **décidée par le serveur** (`PLAN.md` §65) |
| Créatures : dégâts infligés **au joueur** | **Non** | Conséquence directe de « santé du joueur non synchronisée » |
| Butin lâché par une créature | **Non** | Aucune annonce de dépôt ; seul le **ramassage** d'un objet présent dans les deux mondes est arbitré |
| Ressources / minerais (objets ramassables) | **Oui** | Réclamation serveur, refus nommé si déjà prise, disparition chez les autres (M5, étapes L/O/Q) |
| Épaves, caisses, databoxes **déjà dans la sauvegarde** | **Partiel** | Identiques au chargement (même slot) ; un ramassage est arbitré, mais un contenu ouvert/modifié localement ne l'est pas |
| Récolte au couteau, farm, growbeds | **Non** | Aucun message ; les plantes repoussent localement |
| Portes, écoutilles, ascenseurs de base | **Non** | État local |
| Conteneurs (coffres, lockers, inventaire de base) | **Non** | Contenu local à chaque instance |
| Balises, signaux, radio | **Non** | Locaux |
| Caméras, drones, scanner room | **Non** | Locaux |
| Événements du monde (Aurora, fuites, incendies) | **Non** | Locaux |

## Véhicules

| Contenu | Synchronisé ? | Mécanisme / raison |
|---|---|---|
| **Seamoth** : position, orientation | **Oui** | 10 Hz + interpolation, jamais ≥ 1 m par image (M4, étapes J/K) |
| **Seamoth** : conduite (qui pilote) | **Oui** | Arbitrage serveur, refus nommé du second, libération (v9/v12, étape M) |
| **Seamoth** : énergie, modules, santé | **Oui** | Transportés et appliqués (v14–v16, étape J) |
| **Exosuit (Prawn)** | **Partiel** | Même chemin générique et autorisé à la création, mais **jamais mesuré spécifiquement** (`PLAN.md` §56, « limites ») |
| **Cyclops** | **Non mesuré** | Le serveur parcourt toute `Vehicle`, mais rien n'est prouvé dessus et il n'est pas dans la liste blanche de création |
| Création d'un véhicule | **Oui** | `EntitySpawn` v10, clé serveur, client tardif ; liste blanche **Seamoth/Exosuit**, portée 8 m, cadence, plafond |
| Retrait / destruction d'un véhicule | **Oui** | `EntityDespawn` v11 + rejeu pour un client arrivé après (étapes O/Q) |
| Mort réelle d'un véhicule par les dégâts | **Partiel** | La santé est appliquée, mais la copie locale ne meurt pas : la destruction reste une décision serveur (`docs/authority.md`) |

## Progression, histoire, bases

| Contenu | Synchronisé ? | Mécanisme / raison |
|---|---|---|
| Histoire (radio, Sunbeam, fusée Neptune, portes précureurs, story goals) | **Non** | Chaque client avance ses propres déclencheurs ; l'hôte ne les lui transmet pas |
| Blueprints et déblocages | **Non** | Locaux |
| Bases : construction, pièces, intégrité, fuites, incendies | **Non** | README, « ce qui n'existe pas encore » |
| Énergie et oxygène d'une base | **Non** | Idem |
| Téléporteurs / architecture précureur | **Non** | Locaux |

## Session et méta

| Contenu | Synchronisé ? | Mécanisme / raison |
|---|---|---|
| Heure du monde (jour/nuit) | **Oui** | Diffusion 1 Hz, recalage au-delà de 5 s (v18, étape AA) |
| Sauvegarde | **Partiel** | Seul l'hôte écrit ; clients protégés par deux remparts (étapes D/F) |
| Chargement, reconnexion | **Partiel** | Retraits et réclamations rejoués, aucun fantôme (P2) ; **l'état complet du monde après reconnexion n'est pas mesuré** (`PLAN.md` §82.4) |
| Contrat de partie (slot, session, mode, version, mods) | **Oui** | Handshake v13, refus nommés (P0, étapes P/R/S/T) |
| Économie partagée | **Non** | Le produit d'un craft reste local ; le débit n'est branché que sur le craft, pas sur manger/déposer |
| Contenu custom (item, AssetBundle) | **Non** | Gel levé le 2026-10-23 (`PLAN.md` §83), mais rien n'est encore livré |

## Ce que ce document ne prétend pas

- **Ce n'est pas une feuille de route** : une ligne « Non » n'est pas une promesse. Le prochain chantier
  décidé est le contenu custom (`PLAN.md` §83.3), pas la santé du joueur ni les bases.
- **« Partiel » n'est pas « presque bon »** : chaque ligne renvoie à une limite écrite (cas non exercé,
  décision assumée, ou périmètre). La liste des preuves manquantes vit dans `docs/preuves.md`
  (« Non prouvé ou partiel »).
- **Les lignes « Non » ne sont pas mesurées pour l'absence** : elles le sont par lecture du protocole
  (`Coop.Model/Messages.cs`) et des accroches (`src/Coop.Client`, `src/Coop.Server`). Une ligne qui
  changerait de statut doit d'abord produire sa mesure, pas son intention.

## Où le tenir à jour

| Fait qui change une ligne | Où il s'écrit d'abord |
|---|---|
| nouveau message ou nouveau champ transporté | `docs/protocol.md`, puis `docs/authority.md` |
| mesure en jeu d'un système | `docs/preuves.md`, avec la ligne d'étape `tools/verify.sh` |
| décision de périmètre (ce qu'on ne fera pas) | `PLAN.md`, section datée |
