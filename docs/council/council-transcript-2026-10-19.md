# Conseil d'architecture — transcript

- **Date** : 2026-10-19
- **Mode** : Standard (5 lentilles, revue croisée anonymisée, débat forcé, double présidence)
- **Identifiant de run** : `q2026-10-19` (les scripts `~/.claude/skills/claude-council/` et les outils de hachage ne sont pas installés dans cet environnement : pas de SHA1 calculé, l'identifiant est la date)
- **Portée** : `PLAN.md` (3 261 l.), `docs/authority.md`, `docs/preuves.md`, `docs/protocol.md`, `tools/verify.sh`, arborescence et tailles de `src/`
- **Limites d'outillage rencontrées en cours de run** : terminal indisponible (pas de `shasum`, pas d'écriture de journal par script), puis `spawn_agents` indisponible. Les cinq lentilles et les cinq revues ont tourné en parallèle ; les deux présidences et la passe de préservation des dissidences ont été rédigées par l'orchestrateur, seul, à partir des sorties ci-dessous. C'est dit ici plutôt que masqué.

---

## 1. Question cadrée

```
DECISION: L'architecture « serveur autoritaire = instance Subnautica headless pilotée par BepInEx
          + netcode maison + preuve par sondes in-game » est-elle la bonne trajectoire pour obtenir
          un coop réellement jouable, et quelle est la prochaine étape ?
CONTEXT:  5 jours, 25 commits, 21 672 lignes, protocole v12, 15 étapes de vérification (16 runs),
          7 sondes in-game, avatars/créatures/véhicules/pickup/spawn/despawn mesurés,
          aucun test hors-jeu, launcher hors dépôt, une seule sauvegarde de test.
STAKES:   temps d'un développeur seul ; valeur réelle du produit (présence synchronisée ≠ jeu partagé) ;
          dette de vérification (batterie de 1 559 s) ; risque de construire M6 sur une identité non stable.
OPTIONS:  A. continuer le plan (M6 contenu, puis industrialiser)
          B. consolider d'abord (autorité joueur, tests, découpage, session réelle)
          C. viser une tranche jouable et geler le reste
          D. reposer l'architecture (le client ne charge pas le monde)
PRIOR:    aucun conseil antérieur (première entrée du journal)
```

## 2. Audit de biais (orchestrateur — non délégué)

Le skill prévoit un audit délégué en mode Standard ; il n'a pas pu être lancé (outils indisponibles). Les biais visibles à la lecture de la question et des documents, et qui pèsent sur ce conseil :

- **Cadrage de confirmation** : « replanifie la suite si besoin » suppose qu'il faut replanifier.
- **Coût irrécupérable** : 25 commits et cinq jours rendent coûteux de conclure « l'ordre est faux ».
- **Optimisme de mesure** : « prouvé » signifie « une ligne de journal l'a dit pendant un run », pas « reproductible par un tiers ».
- **Ancrage interne** : le plan se cite lui-même (« M3 valide le modèle »), et les conseillers ont lu le plan avant le code.
- **Biais de disponibilité** : toutes les preuves viennent d'une sauvegarde, d'une zone, deux clients.

Ces biais sont des signaux, pas des verdicts.

## 3. Anonymisation

| Étiquette | Lentille |
|---|---|
| A | Red Team |
| B | First Principles |
| C | Expansionist |
| D | Outsider |
| E | Executor |

Les réponses ci-dessous sont des **digests fidèles** (texte intégral non conservé — limite d'outillage), avec les chiffres et les recommandations d'origine.

## 4. Les cinq lentilles

### A — Red Team

**Mode de défaillance** : le projet meurt en *prototype-miroir*. Deux joueurs se voient, les créatures et les véhicules bougent, un objet se ramasse — et au bout de trente minutes il n'y a rien à faire ensemble. Aucun patch de `LiveMixin.TakeDamage`, `EnergyMixin`, `CraftTree` ; ni inventaire, ni oxygène, ni jour/nuit, ni réapparition (grep sur `src/`). Les huit patches du §5, présentés comme « le cœur du travail », sont faits à 2/8. Pendant ce temps la boucle de vérification s'allonge : 16 runs, un run complet documenté à 1 559 s. Chaque nouveau système ajoute une sonde, pas une heure de jeu.

**Cause racine** : l'unité de progrès est « un système, une sonde, une ligne de log », jamais « une session de jeu ». `verify.sh` est devenu le produit : 976 lignes d'assertions pour 21 700 lignes de code. Un système peut être vert et le jeu injouable.

**Affirmation non prouvée** : le serveur autoritaire **pour le joueur**. `HandlePlayerPosition` ne vérifie que NaN/Inf et l'ordre des séquences ; il relaie n'importe quelle position. `TeleportThresholdMeters = 10` ne fait que couper l'interpolation. La prédiction/réconciliation du §1 n'existe nulle part. Falsification : 30 minutes, une sonde qui envoie `x += 500` par le chemin d'envoi existant.

**Signal manqué** : l'identité a été réinventée **trois fois** (créature = position d'apparition, véhicule = `UniqueIdentifier`, objet = les deux avec repli). C'est la preuve que « le monde est reconstructible des deux côtés » ne tient que pour une sauvegarde, une zone, un type. Personne n'a chargé une **autre** sauvegarde.

**Prochaines étapes** : (1) session de 30 min à deux comme seul critère d'acceptation, tout nouveau message gelé tant qu'elle ne tourne pas ; (2) fermer le trou d'autorité joueur + extraire les tests unitaires promis au §2 ; (3) découper `ServerNetwork` avant d'y ajouter un système, reporter M6-contenu.

Confiance : **high**.

### B — First Principles

**Preuve minimale viable** — sept propriétés, classées, et leur état :

1. **Un monde, un écrivain** — existe et mesuré (§14, §30-F : 31 fichiers écrits par l'hôte, garde cliente sur l'entrée *et* `SaveFile`). Faiblesse : rien n'impose l'invariant à la connexion — le client rejoint avec la sauvegarde qu'il a sur disque.
2. **Présence** — existe (20 Hz + interpolation, §20–§27).
3. **Convergence des entités** — existe (10 Hz + interpolation, `0` image ≥ 1 m sur 10 259).
4. **Cycle de vie des entités** — existe, serveur → client seulement (§41–§42).
5. **Économie partagée** — **partiel** : claim + despawn existent, mais inventaire, craft, dégâts, oxygène et bases restent locaux. Deux joueurs peuvent détenir le même objet unique et progresser sur des copies non réconciliées.
6. **Cycle de vie de session** — **manquant** : rejoindre, se déconnecter, revenir, reprendre. Un retour sur une sauvegarde locale périmée est la rupture la plus probable d'une session de 30 min.
7. **Confinement des pannes** — **non mesuré** : les runs durent 40–120 s.

**Trois reframings** : exercice de protocole (perd : l'effort le plus coûteux — avatars, couleurs, PDA — n'a aucune valeur dans une étude de réplication) ; **produit jouable** (gagne : les deux propriétés manquantes sont exactement celles qu'un joueur remarque en premier) ; spike de recherche (la question est déjà répondue **oui**, deux fois, avec des chiffres — le critère d'arrêt est atteint et personne ne l'a écrit).

**Le plus fort** : produit jouable. La décomposition « un système à la fois » est juste en principe, fausse dans l'ordre : elle achète de la largeur pendant que la boucle reste ouverte. L'autorité est poursuivie au-delà du besoin produit : un mouvement client-authoritatif est acceptable entre amis, l'anti-triche est un sur-investissement par rapport à la propriété de l'inventaire et à la continuité de session.

Confiance : **high**.

### C — Expansionist

**Option débloquée** : **enregistrer et rejouer une session hors du jeu**. Le modèle est déjà sans Unity, les gestionnaires ne consomment que des messages et du temps (`From/To`, `SegmentDuration`, `AdvanceInterpolation`), les sondes écrivent déjà un verdict par critère. Il manque l'enregistreur : capturer les paquets d'une session + le transform du monde au démarrage, puis rejouer ce flux contre les gestionnaires, sans Subnautica. Les 15 étapes deviennent des fixtures, une régression d'interpolation se voit en secondes.

**Pourquoi maintenant** : le flux est aujourd'hui presque entièrement « position + orientation + identité ». Après l'énergie, les modules, les dégâts, les inventaires, le rejeu devra modéliser des objets du jeu et ne sera plus rejouable.

**OPTION X** — enregistreur + rejeu (3–5 j). **OPTION Y** — panneau d'hôte sur le canal fichier existant (2 j) : qui est connecté, ce que coûte chaque client, exclure. **OPTION Z** — reconnexion + empreinte de sauvegarde au handshake (3–4 j) : le handshake vérifie version et mod-list, **pas** la sauvegarde, alors que toute l'identité des créatures repose sur « même sauvegarde, même placement ».

**Dominante** : X, payé par M6-contenu — X réduit le coût de *toutes* les vérifications futures ; M6 a un coût inconnu et une valeur nulle tant que personne n'a tenu 30 minutes à deux.

**À ne pas construire** : M6 contenu / AssetBundles. C'est le piège le plus tentant du plan : deuxième dette d'identité, contrat de version que l'égalité stricte ne sait pas exprimer, invérifiable avant X. Ni host migration, ni spectateur, ni matchmaking : personne ne les a demandés.

Confiance : **medium**.

### D — Outsider

**Classement** : spike de R&D instrumenté, ni prototype du jeu ni mod livrable. Deux faits décident : `verify.sh` affirme des lignes nommées sur quinze runs et restaure ses fixtures — discipline très au-dessus de la norme du modding ; mais pas de `tests/`, pas de `.sln`, pas de `README`, pas de CI, launcher hors dépôt — en dessous de ce qu'on appellerait un produit. La question « Subnautica peut-elle être répliquée côté serveur ? » est presque répondue, et bien répondue. Celle qui n'a pas commencé : « est-ce un jeu auquel on joue une heure ? »

**Trois choses qui semblent fausses** : (1) la distribution des tailles **confirme** — `ServerNetwork` 3 098 lignes possède neuf responsabilités ; les fichiers de sonde sont de l'instrumentation, pas de la dette de conception ; (2) le protocole v12 à égalité stricte est **exonéré** aujourd'hui (refuser plutôt que diverger silencieusement) mais à revoir avant le message 30 (masque de capacités) ; (3) l'absence de tests est **partiellement exonérée** — la batterie bash est réellement bonne — mais **structurellement confirmée** : `Coop.Model` est délibérément sans Unity pour être testable, et n'est jamais testé.

**Angles morts** : l'identité par position d'apparition ne vaut que pour une sauvegarde fixture (et M6 produira précisément spawns dynamiques, respawns, sauvegardes différentes) ; « autoritaire » relaie position, vitesse et drapeaux d'action ; « prouvé » veut dire « une ligne de journal dans un run dont le run suivant efface les journaux ».

**À adopter tel quel** : rendre la spécification exécutable — `docs/protocol.md` est déjà une table de messages précise ; en faire des fixtures binaires rejouées dans `PacketReader` et les gestionnaires, byte-identiques, en CI, en secondes.

**La question à poser avant de financer un mois de plus** : si deux joueurs jouaient une heure ce soir, qu'est-ce qui casse en premier — et pourquoi n'est-ce pas le prochain jalon ?

Confiance : **high**.

### E — Executor

**Chemin critique, 14 jours** : (1) J1–2 autorité joueur minimale (borne vitesse/téléport + `PlayerCorrection` ; acceptation : refus journalisé sur un téléport de 100 m, **aucun** refus sur une nage normale) ; (2) J3 reconnexion (rejeu claims, spawns/despawns, conducteurs) ; (3) J4–5 session scriptée de 30 min (`0 verdict=False`, `0 désync > 2 m`, `0 exception`) ; (4) J6–7 sauvegarde/rechargement réels avec clés d'entités qui survivent ; (5) J8–14 découpage de `ServerNetwork` en cinq systèmes, à comportement identique.

**À couper** : M6 contenu, énergie/modules véhicule, nouvelles sondes.

**RICE** (Reach = sert le but jouable) :

| Candidat | Reach | Impact | Conf. | Effort | Score |
|---|---|---|---|---|---|
| Autorité joueur + correction | 1 | 5 | 5 | 3 j | 8.3 |
| Reconnexion + rejeu complet | 1 | 4 | 4 | 2 j | 8.0 |
| Session 30 min scénarisée | 1 | 5 | 4 | 3 j | 6.7 |
| Découpage `ServerNetwork` | 1 | 3 | 5 | 5 j | 3.0 |
| M6 AssetBundles | 0 | 4 | 2 | 15 j | 0.5 |

**Semaine 1** : lundi validation de vitesse (preuve : refus sur téléport, aucun sur nage) ; mardi correction + réconciliation (retour en ≤ 2 corrections) ; mercredi rejeu des claims/retraits à l'admission ; jeudi run 30 min à deux (`VERDICT SESSION : 0 désync, 0 exception, 30:00`) ; vendredi sauvegarde + reload (clés identiques après reload).

**Critères d'arrêt** : > 5 % de corrections sur une nage normale (validation non viable → transporter les entrées) ; session avec > 3 désyncs > 2 m ou 1 exception / 10 min (divergence structurelle) ; reload perdant les clés d'entité (M6 bâti sur du sable).

Confiance : **medium**.

## 5. Revue croisée (5 reviewers, anonymisée)

| Reviewer | Le plus fort | Le plus dangereux si faux | Ce que tous ont manqué | Accord |
|---|---|---|---|---|
| 1 | E — seul à rendre la séquence exécutable dès demain | B — « l'anti-triche est du sur-investissement » retire la justification fondatrice **et** le seul mécanisme qui détecterait une divergence ordinaire | l'autorité est traitée comme une propriété par message : le client continue de simuler tous les systèmes non patchés, la divergence s'accumule **entre** les messages | **4** |
| 2 | E — séquence datée, falsifiable, critères d'arrêt | B — abandonner l'autorité joueur sur une hypothèse d'audience, sans mesure | la simplification qui dissout le problème : un client qui **ne charge pas** le monde et reçoit son état | **4** |
| 3 | A — nomme le mode de défaillance, sa cause et l'affirmation non prouvée | la prémisse partagée que la session de 30 min est la contrainte liante | rien ne coûte l'**installabilité** : DLL, épinglage, ports, pare-feu, launcher hors dépôt — le test prescrit n'est pas délégable | **4** |
| 4 | A — nomme le mécanisme que les autres décrivent | le budget de « une boucle d'état partagée de bout en bout » (3–4 j) : si l'inventaire ne se superpose pas au modèle d'autorité actuel, le chemin de 14 jours est fictif | la **distribution** : qui héberge, comment un ami installe la bonne version, ce qui se passe quand l'hôte quitte | **4** |
| 5 | E — étapes datées avec test d'acceptation et critère d'arrêt | B — client-authoritatif « entre amis » retire la raison d'être du projet sans mesure | l'hébergement et la distribution ; aucune des étapes ne les couvre | **4** |

**Accord moyen : 4,0/5.** Les cinq partagent la destination (session réelle comme juge, M6-contenu gelé) et divergent sur le premier pas : autorité (A, E), session (B, D), outillage (C).

## 6. Débat forcé

### 6.1 Procureur (verbatim)

> Le conseil a correctement diagnostiqué que la boucle n'est pas prouvée, puis prescrit une porte que seul l'auteur peut franchir, sur un client dont personne n'a mesuré la simulation du monde ; il traite un problème de **mesure** comme un problème de **séquencement**, et ce faisant il retire en silence les deux choses qui rendent ce projet digne d'être construit — l'autorité sur le contenu et la prémisse anti-confiance.
>
> **Chef 1 — la session de 30 min mesure la mauvaise chose, au pire coût.** Elle mesure l'absence de crash, les désyncs grossières et la patience de l'auteur ; elle cache l'installation, la version, l'hébergement ; elle n'est pas automatisable. Coût par itération : ~5 min machine + 30 min humaine, pour un pass/fail sans attribution. Alternative : deux portes — un rejeu de session automatisé de 3 min en CI, plus une session humaine avec script d'installation et manifeste épinglé, enregistrée. La session humaine est un jalon, pas la seule porte.
>
> **Chef 2 — geler le contenu, c'est geler la raison d'exister.** Le §1 justifie l'architecture par le contenu possédé par le serveur : un seul mod serveur, des AssetBundles poussés aux clients. Geler ça donne « Nitrox, mais plus lent à construire et avec moins de fonctionnalités ». Le conseil confond *jouable* et *digne d'intérêt*. Alternative : geler seulement le **pipeline** AssetBundle, et passer une journée à prouver le **contrat** de contenu — une entité définie par le serveur, avec son identité et un hash de définition, instanciée par un client qui ne l'a jamais vue.
>
> **Chef 3 — des bornes de vitesse et de téléport sont un limiteur de débit, pas une autorité.** Le serveur ne simule ni le motor du joueur, ni les courants, ni les véhicules, ni les échelles, ni les inondations ; une borne assez serrée rejettera du jeu légitime sur une physique qu'elle ne reproduit pas, une borne assez lâche laissera passer un tricheur à 4,9 m/s. Pire : elle ne peut pas arbitrer un désaccord, seulement punir un camp. Alternative : l'appeler ce qu'elle est (une garde anti-téléport), et soit transporter les entrées et simuler le motor côté serveur, soit supprimer la revendication anti-triche du §1 et de `authority.md`. Ne pas livrer une heuristique sous le mot « autoritaire ».
>
> **Chef 4 — couper des sondes rouvre la classe de bug qui a failli passer verte.** Le §35.2 est le conte d'avertissement : un `expect` sur un compteur cumulatif restait vert alors que l'état final était faux. La vitesse est venue en **ajoutant** `expect_last`, pas en retirant des contrôles. Alternative : ne pas couper les sondes — changer leur substrat : enregistrer une vraie session, la rejouer dans les mêmes gestionnaires hors jeu, garder toutes les assertions.
>
> **Chef 5 — la prémisse que personne n'a nommée : les deux instances chargent et simulent la même sauvegarde.** L'identité par position d'apparition, le rejeu à la reconnexion et l'empreinte de sauvegarde manquante sont trois rustines sur cette prémisse. Alternative : faire de la connexion un **diff**, pas un saut de confiance — le serveur envoie un hash du contenu du monde et une baseline à l'admission, et toute divergence est un refus nommé.
>
> **La phrase à laquelle le conseil ne peut pas répondre** : si le client doit charger et simuler le même monde pour que quoi que ce soit fonctionne, sur quoi le serveur est-il autoritaire — ou « serveur autoritaire » n'est-il que le nom qu'on donne à un pair qui diffuse ?

### 6.2 Défenseur

**Défense.** Le procureur a raison sur le mot et sur la prémisse, et tort sur la conclusion : la réponse à « l'autorité est mal nommée » n'est pas de supprimer la revendication, c'est de la rendre vraie sur la seule surface où elle compte et de la renommer partout ailleurs — et la réponse à « la prémisse n'est pas un contrat » n'est pas de faire charger le monde par le serveur (ce qui invaliderait M0.5, M2 et toute la couche d'identité), c'est de transformer la connexion en **diff nommé**.

**Chef 1 — concédé à moitié.** Deux portes, oui : le rejeu est la porte de régression, la session humaine est le jalon d'intégration. Mais « elle mesure la mauvaise chose » est faux : elle mesure exactement la seule chose que rien d'autre ne mesure — l'intégration de systèmes qui ont chacun leur sonde. Ce qui manque, et que la revue croisée a trouvé trois fois, c'est **l'installabilité** : sans script de déploiement et manifeste épinglé, la session n'est pas délégable. On l'ajoute, on ne remplace pas la session.

**Chef 2 — concédé sur le pipeline, rejeté sur le contrat.** Le contrat de contenu en une journée est une bonne idée et le conseil l'intègre. Mais « le contenu est la raison d'exister » n'est pas établi : le §1 promet *un seul mod serveur à maintenir*, pas du contenu custom. La raison d'exister écrite est l'autorité, pas les AssetBundles. Et l'ordre reste : une entité custom n'a aujourd'hui ni identité sérialisée ni position d'apparition fiable — construire le pipeline avant le contrat, c'est empiler une deuxième dette d'identité.

**Chef 3 — concédé sur le vocabulaire, rejeté sur l'alternative.** Transporter les entrées et simuler le motor côté serveur est un chantier d'une autre nature, hors de portée d'un développeur seul, et le §43 le dit. Supprimer la revendication est prématuré : le trou est fermable par une garde bornée + correction, dont on mesure le taux de faux positifs. Ce qui est non négociable, c'est de ne pas appeler « autorité » ce qui n'en est pas : le document est corrigé, et le critère d'arrêt à 5 % tranchera.

**Chef 4 — entièrement concédé, et ce n'est pas un désaccord.** Le conseil n'a jamais proposé de supprimer des assertions : il propose de changer leur substrat. Le procureur reformule la recommandation d'un conseiller (C) et d'un reviewer (D). Aucune assertion n'est retirée.

**Chef 5 — concédé, et c'est la meilleure contribution du débat.** La connexion doit être un diff. Une empreinte de sauvegarde + une baseline à l'admission coûtent une journée et transforment une hypothèse silencieuse en refus nommé. Ce qui reste refusé, c'est de faire du client un terminal sans monde : cela invaliderait M0.5, M2, l'identité et l'interpolation, pour un gain qui n'est pas démontré nécessaire.

**Recommandation resserrée** : session réelle comme juge, **avec installation par un second humain** ; rejeu comme porte de régression, **sans retirer d'assertion** ; prémisse de sauvegarde transformée en refus nommé **avant** la session ; autorité joueur = garde + correction, **nommée honnêtement** et jugée sur son taux de faux positifs ; contrat de contenu en un jour, pipeline AssetBundle gelé.

Confiance : **high** sur la prémisse et le vocabulaire ; **medium** sur le séquencement.

## 7. Présidence — consensus

**Council confidence: medium (3/5 high, 2/5 medium, 0/5 low)**
**Dominant assumption** : les deux instances chargent la même sauvegarde, donc l'identité dérivée du contenu (position d'apparition, `UniqueIdentifier`) désigne la même entité des deux côtés.
**Breakers** : (1) une session de 30 min qui passe **sans** inventaire/craft partagés ; (2) une empreinte de sauvegarde divergente après une sauvegarde d'hôte client connecté ; (3) plus de 5 % de corrections sur une nage normale.

### Recommandation

Ne pas ajouter de mécanisme avant d'avoir prouvé une session. La prochaine étape est une **session réelle de 30 minutes à deux joueurs**, préparée par trois fermetures peu coûteuses : la prémisse « même sauvegarde » devient un refus nommé au handshake ; l'autorité du joueur est soit rendue vraie (garde de plausibilité + correction + taux de faux positifs mesuré), soit renommée dans la documentation ; la reconnexion rejoue claims, apparitions, retraits et conducteurs. M6 contenu/AssetBundles est gelé ; seul le **contrat** de contenu (identité + hash de définition) est instruit, une journée, parce qu'il teste la thèse sans construire le pipeline.

### Pourquoi

La couche de transport est la partie saine du projet : tick fixe, snapshots quantifiés, relevancy, interpolation mesurée image par image, autorité réelle sur les créatures, les véhicules, le ramassage, la création et le retrait d'entités. Rien de tout cela n'est à refaire.

Ce qui n'est pas prouvé, c'est la **boucle**. Les preuves viennent d'une sauvegarde, d'une zone, de runs de 40 à 240 secondes. Trois propriétés qu'une session de 30 minutes exige manquent ou sont partielles : la continuité de session (reconnexion, resync), la propriété partagée de l'économie (inventaire, craft, dégâts), et la continuité d'exécution (fuites, dérive sur la durée). La session de 30 minutes est le seul instrument qui les mesure toutes les trois à la fois.

Le coût de vérification est devenu un coût produit : 16 runs, un run complet à 1 559 s, une seule machine, une seule sauvegarde. Chaque système ajouté allonge la batterie. Le rejeu hors jeu n'est pas un luxe : c'est ce qui permet de garder les assertions tout en revenant à une boucle de quelques secondes.

### Ce qui est bon / ce qui ne l'est pas

Bon : le franchissement des deux inconnues majeures en premier (headless, autoload), la discipline de preuve, les décisions écrites avec leur falsificateur, la séparation du modèle, et le fait que les limites soient écrites au lieu d'être cachées.

Pas bon : zéro test hors jeu alors que le modèle a été conçu pour ça ; un joueur non autoritaire sous une revendication contraire ; une prémisse de sauvegarde qui n'est pas un contrat ; pas de cycle de vie de session ; des monolithes de 3 098 / 1 185 / 1 157 lignes ; M6 contenu programmé avant que la boucle existe ; rien de distribuable (launcher hors dépôt, pas de README, pas de `.sln`, pas de CI) ; un plan de 3 261 lignes qui n'est plus pilotable ; trois schémas d'identité.

### Où le conseil se divise

1. **Premier pas** — autorité (A, E) contre session (B, D) contre outillage (C). Tranché : la session est le **juge**, l'autorité est le premier des trois prérequis, parce qu'une session sans correction prouve seulement qu'un client peut se téléporter.
2. **Autorité joueur** — vraie validation (A, E) contre sur-investissement (B) contre heuristique mal nommée (procureur). Tranché : garde + correction, nommée honnêtement, critère d'arrêt à 5 % de faux positifs.
3. **Contenu** — geler (C, E) contre contrat en un jour (procureur). Tranché : contrat oui, pipeline non.
4. **Substrat des preuves** — rejeu (C, D) contre crainte de couper les sondes (A). Tranché : mêmes assertions, autre substrat ; aucune assertion supprimée.

### Ce qu'il faut faire d'abord

1. Empreinte de sauvegarde au handshake + refus nommé (1 j).
2. `README`, script de déploiement, manifeste épinglé, `.sln`, projet de tests (1 j) — sans quoi la session n'est pas délégable.
3. Garde de plausibilité + `PlayerCorrection`, taux de faux positifs publié (2–3 j).
4. Reconnexion : rejeu claims/spawns/despawns/conducteurs (2 j).
5. Session de 30 min à deux, installée par le second joueur (2–3 j).

## 8. Présidence — dissidence

**Council confidence: medium** — la présidence de dissidence est plus inquiète sur un point : le conseil a raison de dire « jouer d'abord », mais il sous-estime que la **prémisse** est l'architecture.

**Recommandation** : faire de la connexion un **diff** avant la première session, et rendre le mod installable par un tiers avant de prétendre mesurer quoi que ce soit à deux. Tant que ces deux points ne sont pas faits, la « session de 30 minutes » est une séance de l'auteur pour l'auteur, et elle mesurera la tolérance de l'auteur, pas le jeu.

**Pourquoi la majorité a tort sur ce point** : les trois schémas d'identité, la reconnexion et l'empreinte de sauvegarde sont trois rustines sur une seule prémisse non écrite. Le conseil propose de les traiter comme des tâches séparées ; la dissidence soutient que tant que la prémisse n'est pas un contrat observable, chaque nouveau système en hérite et il faudra une quatrième rustine.

**Pourquoi la majorité a raison** : sur la destination. Geler le pipeline de contenu, garder toutes les assertions, et ne pas faire du client un terminal sans monde sont les trois bons arbitrages. La dissidence ne les conteste pas.

**Ce que la dissidence ajoute au verdict** : (a) l'installabilité est un critère d'acceptation de la session, pas une politesse ; (b) un hash de définition de contenu coûte une journée et teste la thèse ; (c) le mot « autoritaire » doit être retiré du §1 tant qu'il est faux pour le joueur.

## 9. Passe de préservation des dissidences

- **Le mot « autoritaire » est faux là où un joueur le remarque en premier.** Le conseil l'a corrigé en note ; la dissidence exige que ce soit un critère d'acceptation (garde + correction + faux positifs), pas une phrase de documentation.
- **La prémisse « même sauvegarde » est l'architecture, pas un détail.** Un hash au handshake est une journée ; sans lui, l'identité restera une heuristique à trois têtes.
- **Rien n'est distribuable, et aucune des étapes ne le mesure.** La session prescrite ne peut pas être déléguée aujourd'hui : bus factor 1, et le test est auto-réalisateur.
- **Le contrat de contenu n'est pas le pipeline de contenu.** Une journée pour prouver identité + hash garde la thèse vivante sans payer le prix d'AssetBundles.
- **La batterie est un actif, pas un coût.** Le conseil a failli la couper ; la bonne réponse est de changer son substrat, pas de retirer des assertions — le §35.2 est la preuve de ce que coûte un contrôle qui ne peut pas échouer.

## 10. Decision Science (Codex)

Non exécuté : `codex` n'est pas disponible dans cet environnement, et l'outil de spawn n'a pas pu être utilisé pour le repli. Les options ont donc été comparées qualitativement dans les sections 7 à 9, sans matrice chiffrée. C'est une lacune du run, pas un résultat.

## 11. Journal

```json
{"ts":"2026-10-19","question_id":"q2026-10-19","mode":"Standard","codex_used":false,
 "biases_flagged":["confirmation framing","sunk cost","measurement optimism","internal anchoring","availability"],
 "advisors_confidence":{"red_team":"high","first_principles":"high","expansionist":"medium","outsider":"high","executor":"medium"},
 "chairman_confidence":"medium","consensus_strength":4.0,
 "recommendation_one_liner":"Ne pas ajouter de mécanisme avant d'avoir prouvé une session réelle de 30 minutes à deux, préparée par un refus nommé sur la sauvegarde, une autorité joueur honnêtement nommée et la reconnexion.",
 "dissent_ledger":["Le mot autoritaire est faux pour le joueur, et c'est la surface la plus visible","La prémisse même-sauvegarde est l'architecture, pas un détail","Rien n'est distribuable et aucune étape ne le mesure","Le contrat de contenu n'est pas le pipeline de contenu","La batterie est un actif : changer son substrat, pas retirer des assertions"],
 "html_path":"docs/council/council-report-2026-10-19.html",
 "transcript_path":"docs/council/council-transcript-2026-10-19.md",
 "outcome":null}
```
