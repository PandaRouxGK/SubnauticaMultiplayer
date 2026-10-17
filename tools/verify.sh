#!/bin/bash
# tools/verify.sh — rejoue la batterie de contrôles en jeu et rend **un seul verdict**.
#
# Pourquoi ce script existe : chaque contrôle du projet se lance aujourd'hui à la main (`run-session.sh`
# avec ses options), et son résultat se lit dans un journal. Quatre runs, quatre journaux, et c'est un
# humain qui décide si « le jalon tient » — une vérification que personne ne peut rejouer. Ici les étapes
# sont enchaînées, chacune **affirme** les lignes qu'elle attend, et la commande se termine par un verdict
# unique avec un code de sortie.
#
# Ce que ce script ne fait pas : il ne lance pas les instances lui-même (il appelle `run-session.sh`, qui
# sait attendre ses marqueurs et arrêter ses propres instances) et il ne décide pas de ce qui est vert —
# les motifs affirmés sont ceux des sondes, recopiés ici tels quels.
#
# Les douze étapes, et ce que chacune prouve :
#   A  un client, sondes avatar + créatures     -> la copie d'avatar est dessinée, l'aller-retour
#      d'équipement (canal des noms) rend True, et le suivi des créatures rend True.
#   B  deux clients, sondes de présence + créatures -> l'équipement d'un joueur s'applique sur l'avatar
#      réel de l'autre, la couleur est rejouée sur un avatar qui existait déjà, le `PlayerId` falsifié est
#      réécrit par le relais, un vrai outil est tenu puis le joueur nage et s'arrête, et les deux clients
#      suivent les mêmes créatures.
#   C  un client, plafond de diffusion abaissé  -> la garde du plafond **parle** (une créature proche est
#      sacrifiée, et le serveur le dit).
#   D  un client, autotest de la garde          -> les deux remparts de `SaveGuard` parlent : l'entrée
#      (`IngameMenu.SaveGameAsync`) **et** l'écriture (`SaveLoadManager/GameInfo.SaveFile`, appelée
#      directement, hors du menu). C'est la preuve qu'un chemin qui contourne le menu n'écrit pas non plus.
#   E  deux clients, sonde de créatures seule   -> les deux clients suivent le **même** monde, sans clé
#      inconnue. Séparée de B parce que les deux sondes se disputent le joueur : la présence le téléporte pour
#      nager, ce qui change le rayon de pertinence et fabrique des clés inconnues qui ne disent rien du suivi.
#   F  un client, autotest de sauvegarde **côté hôte** -> l'hôte sauvegarde vraiment pendant qu'un client est
#      connecté : un fichier du dossier temporaire apparaît ou change. C'est la moitié qui manquait à D —
#      « la garde ne bloque que le client » n'avait jamais été vu de l'autre côté.
#   G  un client, sonde de véhicule côté hôte -> un Seamoth **existe et se déplace à 20 m/s** : la bande
#      `≥ 2 m` par annonce, jamais atteinte par les créatures, est peuplée. C'est la mesure que M4 attendait
#      avant d'écrire un message de véhicule (PLAN.md §29).
#   H  un client, hôte `-batchmode -nographics` -> le serveur n'a **ni fenêtre ni personnage** : le client
#      est admis et aucun avatar `HoteServeur` (id 9999) n'apparaît. Mesuré par `avatarsActifs=0` dans la
#      ligne `[ping]` du client, qui arrive après le délai où l'avatar apparaîtrait.
#   I  un client, sonde de pause -> l'appel exact du menu Échap (`FreezeTime.Begin(Id.IngameMenu)`)
#      ne change pas `Time.timeScale` : un client qui consulte son menu ne fige pas la partie. La ligne
#      `repères de rotation` du même run mesure aussi le lacet de la racine, du corps local et de la vue.
#   J  un client, sondes de véhicule **des deux côtés** -> l'hôte déplace un Seamoth à 20 m/s et le
#      serveur le diffuse à 10 Hz (`VehicleSnapshot`) ; le client le suit et l'**interpole** : le pas par
#      image reste sous 1 m alors qu'une annonce en vaut 2,15. C'est la mesure qui ferme M4 (b).
#   L  deux clients, sonde de ramassage -> la première réclamation d'une clé gagne, la seconde est
#      **refusée** avec son détenteur nommé, et une réclamation sous un id falsifié est réattribuée à la
#      session émettrice. C'est la première décision autoritaire du plan (M5).
#   K  deux clients, Seamoth **dans la sauvegarde** -> la clé nominale, et elle seule : les deux clients
#      lisent le même `UniqueIdentifier` sérialisé, suivent le véhicule et l'interpolent **sans aucun
#      rattachement par proximité** (dernière ligne d'état : `rattachés par proximité=0`, aucune ligne
#      `rattachée par proximité`, aucune `contrepartie locale` fabriquée). Deux runs : une préparation qui
#      fabrique le Seamoth (dans le monde **chargé** : `joueur=(-1xx…`, pas la scène de menu) et sauvegarde
#      la partie de l'hôte, puis la mesure à deux clients.
#
# Usage :
#   tools/verify.sh                    # les douze étapes
#   tools/verify.sh --only=B           # une seule étape (A..K, ou une liste : --only=A,D)
#   tools/verify.sh --no-build         # ne rebâtit pas les DLL (transmis à chaque run)
#   tools/verify.sh --step-timeout=240 # borne chaque étape (via `timeout`, s'il est présent)
#   tools/verify.sh --keep-logs        # garde les journaux copiés dans ./verify-logs/
#   tools/verify.sh --help
#
# Deux détails qui ne sont pas de la coquetterie :
#   - les journaux du jeu sont **effacés par `prepare` au début de chaque run** : chaque étape copie donc
#     les siens avant que la suivante ne commence. Sans cette copie, une étape qui échoue ne laisse aucune
#     preuve à relire.
#   - une sonde peut écrire son verdict **après** la propre analyse du harnais (sa dernière ligne arrive
#     en fin de sonde, quand le harnais a déjà fini d'attendre ses marqueurs). Chaque étape attend donc
#     quelques secondes, puis **relit** les journaux copiés à la recherche de `verdict=False` : un verdict
#     tardif ne doit pas passer sous le radar.
#
# Toutes les étapes sont tentées même si l'une échoue : un rapport complet vaut mieux qu'un arrêt au
# premier rouge. En cas d'échec, les journaux copiés sont laissés sur place et leur chemin est affiché.

# `set -u` n'est pas utilisé : plusieurs tableaux (échecs, rapports) sont légitimement vides tant qu'aucune
# étape n'a tourné, et les expansions de tableaux vides sous `set -u` dépendent de la version de bash.
set -o pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SESSION="$SCRIPT_DIR/run-session.sh"

# Dossier du jeu : le même défaut que `run-session.sh`, surchargeable de la même façon, parce que les
# journaux à affirmer sont là-bas.
GAME_DIR_BASH="${SUBNAUTICA_GAME_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Subnautica}"
LOG_DIR="$GAME_DIR_BASH/BepInEx/logs"

STEP_TIMEOUT=300
ONLY=""
KEEP_LOGS=0
NO_BUILD=0

# Délai (s) avant la relecture des verdicts tardifs. Une sonde écrit ses dernières lignes à quelques
# centièmes de seconde de la fin du run ; huit secondes sont très largement au-dessus, et coûtent peu
# devant les ~70 à ~120 s d'une étape.
SETTLE_SECONDS=8

usage() {
    # En-tête du fichier, jusqu'à la première ligne de code : la documentation est à un seul endroit.
    awk 'NR > 1 && /^set -/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"
    cat <<'OPTIONS'

Options :
  --only=A,B,C,D,E,F,G,H,I,J,K,L  étapes à lancer (défaut : les douze)
  --step-timeout=SEC  borne chaque étape (défaut : 300 s ; sans effet si `timeout` est absent)
  --no-build          ne reconstruit pas et ne redéploie pas les DLL (transmis à chaque run)
  --keep-logs         garde les journaux copiés dans ./verify-logs/ (sinon supprimés si tout est vert)
OPTIONS
}

for argument in "$@"; do
    case "$argument" in
        --only=*) ONLY="$(printf '%s' "${argument#*=}" | tr 'a-z' 'A-Z')" ;;
        --step-timeout=*) STEP_TIMEOUT="${argument#*=}" ;;
        --keep-logs) KEEP_LOGS=1 ;;
        --no-build) NO_BUILD=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "option inconnue : $argument (--help)" >&2; exit 2 ;;
    esac
done

# Une étape inconnue doit se dire tout de suite : sinon le script « réussit » en ne lançant rien.
case ",$ONLY," in
    *[!ABCDEFGHIJKLM,]*)
        if [ -n "$ONLY" ]; then
            echo "étape inconnue : « $ONLY » (attendu A, B, C, D, E, F, G, H, I, J, K, L, M, ou une liste comme A,D)" >&2
            exit 2
        fi
        ;;
esac

if [ ! -f "$SESSION" ]; then
    echo "[échec] $SESSION introuvable : ce script n'est utilisable qu'à côté du harnais." >&2
    exit 2
fi

if [ ! -d "$LOG_DIR" ]; then
    echo "[échec] dossier de journaux introuvable : $LOG_DIR" >&2
    echo "        (le dossier du jeu se surcharge par SUBNAUTICA_GAME_DIR=/chemin/du/jeu)" >&2
    exit 2
fi

# `timeout` borne une étape ; s'il n'existe pas sur cette machine, la borne est absente — et c'est écrit
# dans l'en-tête du rapport plutôt que de faire croire à une garantie qui n'existe pas.
TIMEOUT_CMD=""
if command -v timeout >/dev/null 2>&1; then
    TIMEOUT_CMD="timeout $STEP_TIMEOUT"
fi

if [ "$KEEP_LOGS" = 1 ]; then
    WORK_DIR="$ROOT/verify-logs"
    rm -rf "$WORK_DIR"
else
    WORK_DIR="$(mktemp -d)"
fi
mkdir -p "$WORK_DIR"

FAILURES=()
STEP_REPORTS=()

# Vrai si l'étape demandée par l'utilisateur contient $1 (liste vide = toutes les étapes).
wanted() { # $1 = nom d'étape, $2 = liste demandée
    [ -z "$2" ] && return 0
    case ",$2," in
        *",$1,"*) return 0 ;;
        *) return 1 ;;
    esac
}

copy_logs() { # $1 = nom d'étape
    local name="$1" f
    mkdir -p "$WORK_DIR/$name"
    cp -f "$LOG_DIR/coop-server.log" "$WORK_DIR/$name/coop-server.log" 2>/dev/null || true
    for f in "$LOG_DIR"/coop-client*.log; do
        [ -f "$f" ] || continue
        cp -f "$f" "$WORK_DIR/$name/$(basename "$f")"
    done
}

expect() { # $1 = étape, $2 = fichier, $3 = motif ERE, $4 = ce que la ligne prouve
    local name="$1" file="$2" pattern="$3" what="$4"
    if [ ! -s "$file" ]; then
        FAILURES+=("$name : $what — $(basename "$file") absent ou vide")
        return 1
    fi

    if grep -qE "$pattern" "$file"; then
        return 0
    fi

    FAILURES+=("$name : $what — « $pattern » absent de $(basename "$file")")
    return 1
}

# Comme `expect`, mais la valeur est cherchée **dans la dernière** ligne portant un préfixe donné.
# Nécessaire pour un compteur cumulatif : « rattachés par proximité=0 » figure aussi au début du journal
# (avant que la sonde ne fabrique quoi que ce soit), donc un `expect` classique reste vert même quand
# l'état final est faux — c'est le défaut du premier run de l'étape K (compteur final à 1, assertion verte).
expect_last() { # $1 = étape, $2 = fichier, $3 = préfixe ERE, $4 = motif ERE, $5 = ce que la ligne prouve
    local name="$1" file="$2" prefix="$3" pattern="$4" what="$5"
    if [ ! -s "$file" ]; then
        FAILURES+=("$name : $what — $(basename "$file") absent ou vide")
        return 1
    fi

    local line
    line=$(grep -E "$prefix" "$file" | tail -1)
    if [ -z "$line" ]; then
        FAILURES+=("$name : $what — aucune ligne « $prefix » dans $(basename "$file")")
        return 1
    fi

    if printf '%s\n' "$line" | grep -qE "$pattern"; then
        return 0
    fi

    FAILURES+=("$name : $what — état final sans « $pattern » : $(printf '%s' "$line" | cut -c1-220)")
    return 1
}

# Comme `expect`, mais vrai dès que **l'un** des fichiers porte le motif : certaines lignes ne peuvent pas
# être attribuées d'avance à un client précis (voir `assert_b`). Chaque fichier manquant est compté comme un
# échec à part : sans ça, deux fichiers absents donneraient « motif absent » alors que rien n'a été lu.
expect_any() { # $1 = étape, $2 = motif ERE, $3 = ce que la ligne prouve, $4.. = fichiers
    local name="$1" pattern="$2" what="$3"
    shift 3

    local file missing=0
    for file in "$@"; do
        if [ ! -s "$file" ]; then
            FAILURES+=("$name : $what — $(basename "$file") absent ou vide")
            missing=$((missing + 1))
            continue
        fi

        if grep -qE "$pattern" "$file"; then
            return 0
        fi
    done

    if [ "$missing" = 0 ]; then
        FAILURES+=("$name : $what — « $pattern » absent de tous les journaux lus")
    fi

    return 1
}

# Un verdict faux écrit **après** l'analyse du harnais : c'est la raison de la relecture différée.
expect_no_false_verdict() { # $1 = nom d'étape
    local name="$1" hit line
    hit=$(grep -lE "verdict=False" "$WORK_DIR/$name"/coop-client*.log 2>/dev/null | head -1)
    if [ -n "$hit" ]; then
        line=$(grep -hE "verdict=False" "$hit" | tail -1 | cut -c1-220)
        FAILURES+=("$name : un verdict de sonde est faux dans $(basename "$hit") — $line")
    fi
}

# Comme `expect`, mais vrai quand le motif est **absent** : c'est la seule forme correcte pour « aucun
# avatar d'hôte n'est apparu ». Un `expect` classique ne sait pas prouver une absence.
expect_missing() { # $1 = étape, $2 = fichier, $3 = motif ERE, $4 = ce que l'absence prouve
    local name="$1" file="$2" pattern="$3" what="$4"
    if [ ! -s "$file" ]; then
        FAILURES+=("$name : $what — $(basename "$file") absent ou vide")
        return 1
    fi

    if grep -qE "$pattern" "$file"; then
        local line
        line=$(grep -E "$pattern" "$file" | tail -1 | cut -c1-220)
        FAILURES+=("$name : $what — « $pattern » présent dans $(basename "$file") : $line")
        return 1
    fi

    return 0
}

run_session() { # borne optionnelle, puis appel du harnais (l'interface reste celle de run-session.sh)
    if [ -n "$TIMEOUT_CMD" ]; then
        $TIMEOUT_CMD "$@"
    else
        "$@"
    fi
}

step() { # $1 = nom, $2 = ce que l'étape prouve, $3 = fonction d'assertions, $4.. = arguments
    local name="$1" description="$2" assertions="$3"
    shift 3

    local started=$SECONDS
    local failures_before=${#FAILURES[@]}
    echo
    echo "=== étape $name — $description ==="
    echo "    $(basename "$SESSION") $*"

    run_session "$SESSION" "$@"
    local rc=$?
    local elapsed=$((SECONDS - started))

    if [ "$rc" = 0 ]; then
        echo "  harnais : OK (${elapsed}s)"
    else
        # Le code de sortie du harnais est pris tel quel : 1 = un marqueur manquant ou un verdict faux,
        # 2 = construction ou déploiement. Les deux comptent comme un échec de l'étape.
        FAILURES+=("$name : le harnais a échoué (code $rc) — journaux dans $WORK_DIR/$name")
        echo "  harnais : ÉCHEC (code $rc, ${elapsed}s)"
    fi

    # La copie doit suivre **avant** l'étape suivante : `prepare` efface les journaux du jeu à chaque run.
    sleep "$SETTLE_SECONDS"
    copy_logs "$name"
    expect_no_false_verdict "$name"

    "$assertions" "$name"

    if [ "${#FAILURES[@]}" = "$failures_before" ]; then
        STEP_REPORTS+=("étape $name : OK (${elapsed}s)")
    else
        STEP_REPORTS+=("étape $name : ÉCHEC ($(( ${#FAILURES[@]} - failures_before )) problème(s), ${elapsed}s)")
    fi
}

assert_a() {
    local log="$WORK_DIR/A/coop-client.log"
    expect A "$log" '\[sonde\] créatures : verdict=True' "le suivi des créatures rend True"
    expect A "$log" 'image\(s\) animée\(s\)=[1-9][0-9]*' \
        "les créatures avancent à chaque image (interpolation), pas par paliers de 10 Hz"
    expect A "$log" "\[sonde\] aller-retour d'équipement : verdict=True" "le canal des noms d'équipement rend True"
    expect A "$log" '\[sonde\] verdict de rendu : la copie EST dessinée' "la copie d'avatar est réellement dessinée"
}

assert_b() {
    local c1="$WORK_DIR/B/coop-client.log"
    local c2="$WORK_DIR/B/coop-client-2.log"

    # La ligne directe se trouve dans le journal du client dont l'avatar pour l'autre **existait déjà**
    # quand le changement est parti : mesuré, c'est le client 1 (lancé en premier, il a l'avatar du second
    # avant que celui-ci ne change d'équipement). Le script ne fixe donc pas lequel des deux la porte, il
    # exige qu'elle existe chez **l'un des deux** — c'est la même preuve, et elle ne dépend pas de l'ordre
    # de lancement.
    expect_any B "équipement appliqué sur l'avatar de" "l'équipement d'un joueur s'applique sur l'avatar réel de l'autre" "$c1" "$c2"
    expect_any B 'pièce\(s\) \[' "le compte de pièces arrive avec leur liste" "$c1" "$c2"
    # L'ancienne assertion portait sur la ligne d'**instanciation** (« tenue=N pièce(s) »). Depuis que les
    # deux clients sont lancés ensemble (--together, exigé par la mesure mutuelle), les avatars naissent
    # avant que quiconque se soit habillé : la ligne existe toujours, mais avec 0 pièce. Ce qui compte — et
    # qui est plus fort — c'est que l'équipement **posé** arrive sur un avatar déjà là, avec un compte
    # non nul et des nœuds réellement activés.
    expect_any B "équipement appliqué sur l'avatar de .* : [1-9][0-9]* pièce\(s\)" "l'équipement porté s'applique sur l'avatar réel d'un joueur, nœuds compris" "$c1" "$c2"
    expect B "$c2" 'couleur rejouée' "la couleur est appliquée à un avatar qui existait déjà"
    # L'avatar distant naît **déjà habillé** (fermé le 2026-10-17) : la sonde pose l'équipement avant
    # d'attendre l'autre client, et les chargements de monde ne se chevauchent plus. C'est la ligne que la
    # présente étape avait perdue quand les deux clients démarraient dans le même dixième de seconde.
    expect_any B 'avatar instancié pour .*tenue=[1-9][0-9]* pièce\(s\)' "l'avatar distant naît avec la tenue de son joueur" "$c1" "$c2"
    expect B "$c1" '\[sonde\] présence : verdict=True' "client 1 : la sonde de présence rend True"
    expect B "$c2" '\[sonde\] présence : verdict=True' "client 2 : la sonde de présence rend True"

    # Les trois affirmations qui ne tenaient qu'à une lecture de code, désormais mesurées. Elles sont
    # cherchées chez l'un ou l'autre client : la falsification du `PlayerId` est observée par le client qui
    # **reçoit** celle de l'autre (le serveur ne renvoie jamais un message à son émetteur), et la nage est
    # vue par le pair, pas par celui qui nage.
    expect_any B 'PlayerId falsifié — .* -> verdict=True' "le relais réécrit le PlayerId falsifié" "$c1" "$c2"
    expect_any B 'outil réellement en main .* -> verdict=True' "un vrai outil est pris en main par le chemin du jeu" "$c1" "$c2"
    expect_any B 'objet tenu émis par la boucle réseau .* -> verdict=True' "l'objet tenu est émis par la boucle réseau, pas par la sonde" "$c1" "$c2"
    expect_any B 'nage puis arrêt vus par .* -> verdict=True' "l'autre client voit la nage puis l'arrêt" "$c1" "$c2"
}

assert_e() {
    local c1="$WORK_DIR/E/coop-client.log"
    local c2="$WORK_DIR/E/coop-client-2.log"

    expect E "$c1" '\[sonde\] créatures : verdict=True' "client 1 : le suivi des créatures rend True à deux clients"
    expect E "$c2" '\[sonde\] créatures : verdict=True' "client 2 : le suivi des créatures rend True à deux clients"
    expect E "$c1" 'clé inconnue \+0' "client 1 : aucune clé inconnue sur la fenêtre"
    expect E "$c2" 'clé inconnue \+0' "client 2 : aucune clé inconnue sur la fenêtre"

    # La fluidité se mesure sur la copie, pas sur le flux : un pas par image petit et fréquent est la
    # preuve que l'interpolation tourne, là où l'ancien code appliquait 10 positions par seconde.
    expect E "$c1" 'image\(s\) animée\(s\)=[1-9][0-9]*' \
        "client 1 : la copie avance à chaque image, pas par paliers de 10 Hz"
    expect E "$c2" 'image\(s\) animée\(s\)=[1-9][0-9]*' \
        "client 2 : la copie avance à chaque image, pas par paliers de 10 Hz"
}

assert_d() {
    local log="$WORK_DIR/D/coop-client.log"

    # Les deux remparts, chacun mesuré : l'entrée par le menu, et l'écriture appelée directement.
    expect D "$log" '\[autotest\] entrée bloquée' "l'entrée IngameMenu.SaveGameAsync est interceptée"
    expect D "$log" '\[autotest\] écriture bloquée' "l'écriture SaveFile est interceptée même hors du menu"
    expect D "$log" '\[garde\] écriture de sauvegarde interceptée avant le disque' "la garde dit ce qu'elle a bloqué (fichier compris)"
    expect D "$log" 'sauvegarde cliente bloquée \(entrée\+écriture\) -> verdict=True' "l'autotest conclut True sur les deux chemins"
}

assert_c() {
    expect C "$WORK_DIR/C/coop-server.log" "n'est PAS diffusée" "la garde du plafond parle quand une créature proche est sacrifiée"
    expect C "$WORK_DIR/C/coop-client.log" '\[sonde\] créatures : verdict=True' "le suivi reste correct au plafond abaissé"
}

assert_g() {
    local log="$WORK_DIR/G/coop-server.log"

    # Le point de départ dépend de la **sauvegarde**, pas du code : la préparation de K écrit un Seamoth
    # dans la partie et cette écriture est désormais permanente (la fixture est stable, pas vierge).
    # Les deux chemins sont donc acceptés **nommément** — monde vide, ou véhicule déjà présent — et
    # c'est la ligne de journal qui est assertion, pas une supposition sur l'état du monde.
    expect G "$log" 'aucun véhicule dans le monde|déjà présent à [0-9.]+ m de l.hôte' \
        "le point de départ est dit (monde vide, ou véhicule déjà présent et repris tel quel)"
    expect G "$log" 'commande console « spawn Seamoth »|retenu \(déjà dans le monde\)' \
        "le Seamoth vient de la commande du jeu, ou du monde déjà chargé — la sonde dit lequel"
    expect G "$log" 'pas entre deux annonces — à 10 Hz' "la répartition des pas entre annonces est mesurée"
    expect G "$log" 'bande ≥2 m à 10 Hz=[1-9][0-9]*' "la bande ≥2 m par annonce est peuplée — celle que les créatures n'ont jamais atteinte"
    expect G "$log" '\[sonde\] véhicule : verdict=True' "la sonde de véhicule conclut True"
}

assert_k_prepare() {
    local log="$WORK_DIR/K-prepare/coop-server.log"

    expect K "$log" '\[sonde\] véhicule persistant : verdict=True' \
        "la préparation sauvegarde un Seamoth dans la partie de l'hôte"
    expect K "$log" 'conservée dans la sauvegarde \(aucune destruction\)' \
        "le véhicule n'est pas détruit après la sauvegarde"
    expect K "$log" 'écritures observées=[1-9][0-9]*' "la sauvegarde a réellement écrit sur le disque"

    # Le monde doit être **chargé** avant la fabrication. Au premier run de K, la sonde travaillait encore
    # dans la scène de menu (joueur à 2,08) : le Seamoth a été sauvegardé à 400 m de la capsule, hors du
    # rayon de pertinence, et la mesure a dû le rattacher par proximité — l'inverse de ce que K prouve. La
    # position du joueur dans la ligne de verdict est la preuve que le chargement avait eu lieu.
    expect K "$log" 'joueur=\(-1[0-9][0-9]\.' \
        "le Seamoth est fabriqué dans le monde chargé (joueur à la capsule), pas dans la scène de menu"
}

assert_k() {
    local server="$WORK_DIR/K/coop-server.log"
    local c1="$WORK_DIR/K/coop-client.log"
    local c2="$WORK_DIR/K/coop-client-2.log"

    # Le serveur dit par quel mécanisme la clé est calculée : l'identifiant, pas la position de repli.
    expect K "$server" 'identités de véhicules : [0-9]+ véhicule\(s\) : [1-9][0-9]* par identifiant' \
        "le serveur lit l'identifiant sérialisé du Seamoth de la sauvegarde"
    expect K "$server" '1 par identifiant, 0 par position d.apparition' \
        "aucun véhicule de la sauvegarde n'est identifié par le repli de position"
    expect K "$server" 'diffusé\(s\) à' "le véhicule sauvegardé est diffusé aux clients"
    expect K "$server" "déjà présent .* — déplacé tel quel" \
        "le serveur déplace le véhicule de la sauvegarde, sans en fabriquer un second"
    expect_missing K "$server" '\[sonde\] véhicule : aucun véhicule dans le monde' \
        "aucun second Seamoth n'est fabriqué au chargement"

    # Chaque client : adoption par la clé **sans** rattachement par proximité, puis interpolation mesurée.
    # `expect_last` lit l'**état final** du compteur : un `expect` simple reste vert sur les lignes d'avant
    # la fabrication de la contrepartie (défaut du premier run de K). L'absence de la ligne de rattachement
    # est la preuve directe, et l'absence de fabrication locale explique pourquoi il n'y en a pas.
    expect K "$c1" '\[véhicule\] suivi adopté : .*clé locale calculée par identifiant' \
        "client 1 : le véhicule est adopté par sa clé d'identifiant"
    expect_last K "$c1" '\[véhicule\] état :' 'rattachés par proximité=0' \
        "client 1 : l'état final ne compte aucun rattachement par proximité"
    expect_missing K "$c1" 'rattachée par proximité' "client 1 : aucune clé n'est rattachée par proximité"
    expect_missing K "$c1" 'fabrication d'une contrepartie locale' \
        "client 1 : le véhicule est trouvé suivi, aucune contrepartie n'est fabriquée"
    expect K "$c1" '\[sonde\] véhicule client : .*rattachés par proximité=0' \
        "client 1 : la sonde de mesure ne s'appuie sur aucun rattachement par proximité"
    expect K "$c1" '\[sonde\] véhicule client : verdict=True' "client 1 : l'interpolation conclut True"

    expect K "$c2" '\[véhicule\] suivi adopté : .*clé locale calculée par identifiant' \
        "client 2 : le véhicule est adopté par sa clé d'identifiant"
    expect_last K "$c2" '\[véhicule\] état :' 'rattachés par proximité=0' \
        "client 2 : l'état final ne compte aucun rattachement par proximité"
    expect_missing K "$c2" 'rattachée par proximité' "client 2 : aucune clé n'est rattachée par proximité"
    expect_missing K "$c2" 'fabrication d'une contrepartie locale' \
        "client 2 : le véhicule est trouvé suivi, aucune contrepartie n'est fabriquée"
    expect K "$c2" '\[sonde\] véhicule client : .*rattachés par proximité=0' \
        "client 2 : la sonde de mesure ne s'appuie sur aucun rattachement par proximité"
    expect K "$c2" '\[sonde\] véhicule client : verdict=True' "client 2 : l'interpolation conclut True"
}

assert_m() {
    local server="$WORK_DIR/M/coop-server.log"
    local c1="$WORK_DIR/M/coop-client.log"
    local c2="$WORK_DIR/M/coop-client-2.log"

    # La **prise de main** et le **refus**, tous deux nommés côté serveur.
    expect M "$server" "conduite : clé=0x[0-9a-f]+ — prise par 'killi'#1" \
        "le serveur attribue la conduite au premier client, et le nomme"
    expect M "$server" 'conduite : clé=0x[0-9a-f]+ réclamée par .* -> REFUSÉE \(déjà conduite par' \
        "le serveur refuse la conduite au second, en nommant le conducteur"

    expect M "$c1" '\[conduite\] résultat : .* accordée=True' \
        "client 1 : le serveur lui accorde la conduite"
    expect M "$c1" '\[sonde\] conduite : verdict=True' "client 1 : la sonde conclut True"
    expect M "$c1" 'états envoyés=[1-9][0-9]*' "client 1 : l'état du véhicule est réellement parti"

    expect M "$c2" '\[conduite\] résultat : .* accordée=False' \
        "client 2 : le serveur lui refuse la conduite"
    expect M "$c2" '\[sonde\] conduite : verdict=True' "client 2 : la sonde conclut True"
}

assert_l() {
    local server="$WORK_DIR/L/coop-server.log"
    local c1="$WORK_DIR/L/coop-client.log"
    local c2="$WORK_DIR/L/coop-client-2.log"

    # La **décision** du serveur, sur un objet réel : un accord nommé (avec la distance), un refus nommé,
    # et le refus d'une clé qui ne désigne rien — c'est la valeur falsifiée.
    expect L "$server" 'pickup : clé=0x[0-9a-f]+ .* -> ACCORDÉE \(premier claim, objet à [0-9.]+\.?[0-9]* m\)' \
        "le serveur accorde la première réclamation, et dit à quelle distance était l'objet"
    expect L "$server" 'pickup : clé=0x[0-9a-f]+ .* -> REFUSÉE \(déjà tenue par' \
        "le serveur refuse la seconde réclamation en nommant le détenteur"
    expect L "$server" 'pickup : clé=0x[0-9a-f]+ .* -> REFUSÉE \(objet inconnu du serveur' \
        "le serveur refuse une clé qui ne désigne aucun objet de son monde"
    expect L "$server" 'vérification PlayerId \(pickup\) : .* effectif=2 -> verdict=True' \
        "l'id revendiqué est comparé à l'id de session"

    expect L "$c1" '\[pickup\] résultat : .* accordée=True tenue par 1' \
        "client 1 : sa réclamation est accordée"
    expect L "$c1" 'objet dans l.inventaire=True' \
        "client 1 : le geste de ramassage a bien pris l'objet accordé"
    expect L "$c1" '\[sonde\] ramassage : verdict=True' "client 1 : la sonde conclut True"

    expect L "$c2" '\[pickup\] résultat : .* accordée=False tenue par 1' \
        "client 2 : la même clé lui est refusée"
    expect L "$c2" 'clé inventée 0x[0-9a-f]+ reçue=True accordée=False' \
        "client 2 : la clé inventée est refusée par le serveur"
    expect L "$c2" 'id falsifié 0x[0-9a-f]+ reçue=True accordée=True tenue par 2 revendiquée sous l.id 1' \
        "client 2 : l'id falsifié est réécrit, un objet réel lui revient"
    expect L "$c2" 'garde=blocage \(blocages [0-9]+->[1-9][0-9]*' \
        "client 2 : la garde a réellement bloqué le geste de ramassage"
    expect L "$c2" 'objet dans l.inventaire=False' \
        "client 2 : l'objet refusé n'est pas entré dans son inventaire"
    expect L "$c2" '\[sonde\] ramassage : verdict=True' "client 2 : la sonde conclut True"
}

assert_j() {
    local server="$WORK_DIR/J/coop-server.log"
    local client="$WORK_DIR/J/coop-client.log"

    # Côté serveur : le flux existe et il porte un véhicule réel, pas une liste vide.
    expect J "$server" '\[net\] véhicules : [1-9][0-9]* dans le monde' "le serveur voit le véhicule fabriqué"
    expect J "$server" 'diffusé\(s\) à' "le serveur diffuse un état de véhicule à un client"
    expect J "$server" 'par identifiant|par position d.apparition' "la clé de diffusion est calculée, et son mécanisme est dit"
    expect J "$server" '\[sonde\] véhicule : verdict=True' "la sonde de l'hôte conclut True sur le déplacement"

    # Côté client : le message arrive pour de vrai (compteurs de la boucle réseau), le véhicule est suivi,
    # et la mesure image par image conclut True — pas max sous 1 m, alors qu'une annonce en vaut 2,15.
    expect J "$client" 'snapshotsVehicules=[1-9][0-9]* etatsVehicules=[1-9][0-9]*' \
        "le client reçoit des snapshots de véhicules non vides"
    expect J "$client" '\[véhicule\] état : suivis=[1-9]' "le gestionnaire client suit au moins un véhicule"
    expect J "$client" '\[sonde\] véhicule client : verdict=True' "l'interpolation cliente conclut True"
    expect J "$client" 'pas par image max=[0-9]\.[0-9]+ m' "le pas par image mesuré est chiffré"
}

assert_i() {
    local client="$WORK_DIR/I/coop-client.log"

    expect I "$client" '\[sonde\] pause : verdict=True' "le menu Échap demandé ne gèle pas le client"
    expect I "$client" 'Time.timeScale avant=1.00, pendant le gel demandé=1.00, après=1.00' \
        "le temps reste à 1,00 pendant et après l'appel du menu"
    expect I "$client" 'gels refusés 0 -> 2' "les deux appels (Begin puis End) sont bien interceptés"
}

assert_h() {
    local server="$WORK_DIR/H/coop-server.log"
    local client="$WORK_DIR/H/coop-client.log"

    expect H "$server" 'batchmode=True — serveur dédié' "l'hôte est lancé sans fenêtre (-batchmode)"
    expect H "$server" '\[dédié\] serveur sans personnage exposé' "le serveur dit qu'il n'expose aucun personnage"
    expect H "$client" 'handshake ACCEPTÉ' "le client est admis par le serveur dédié"
    expect H "$client" 'avatarsActifs=0' "le client ne compte aucun avatar distant — pas même celui de l'hôte"
    expect_missing H "$client" "avatar instancié pour 'HoteServeur'" "aucun avatar d'hôte id=9999 n'est instancié"
    expect_missing H "$client" 'joueur rejoint.*HoteServeur' "le client n'apprend jamais un joueur hôte"
}

assert_f() {
    local log="$WORK_DIR/F/coop-server.log"

    # Le déclenchement doit avoir eu lieu **dans un monde chargé** : sinon un verdict True ne dirait rien de
    # la sauvegarde en jeu (un menu ouvert au chargement pourrait écrire autre chose).
    expect F "$log" 'déclenchement volontaire de IngameMenu\.SaveGame\(\)' "l'hôte déclenche la sauvegarde depuis le jeu"
    expect F "$log" 'sauvegarde serveur pendant un run -> verdict=True' "l'hôte écrit sur le disque pendant qu'un client est connecté"
    # Et le fichier, nommé : un « quelque chose a changé » sans nom serait une mesure qu'on ne peut pas relire.
    expect F "$log" '\[autotest\] sauvegarde serveur : [0-9]+ fichier\(s\) écrit\(s\) ou modifié\(s\)' "l'écriture observée est comptée"
}

VERIFY_STARTED=$SECONDS

if [ "$KEEP_LOGS" = 1 ]; then
    echo "verify.sh — journaux conservés dans $WORK_DIR"
else
    echo "verify.sh — journaux temporaires dans $WORK_DIR (supprimés si tout est vert)"
fi
if [ -n "$TIMEOUT_CMD" ]; then
    echo "            borne par étape : ${STEP_TIMEOUT}s (timeout) ; étapes : ${ONLY:-A,B,C,D,E,F,G,H,I,J,K,L,M}"
else
    echo "            borne par étape : aucune (« timeout » absent de cette machine) ; étapes : ${ONLY:-A,B,C,D,E,F,G,H,I,J,K,L,M}"
fi

if wanted A "$ONLY"; then
    args=(--clients=1 --timeout=200
        "--client-args=-coopmode=client -coopdebug=avatar -coopdebug=creature"
        "--client-marker=\[sonde\] créatures : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step A "un client : copie d'avatar dessinée, aller-retour d'équipement, suivi des créatures" assert_a "${args[@]}"
fi

if wanted B "$ONLY"; then
    # `--together` est indispensable ici : la sonde se mesure **entre deux clients** (falsification vue par
    # l'autre, nage vue par l'autre). Un harnais qui n'ouvre le second client qu'après le verdict du premier
    # laisserait au premier un désert à mesurer — déjà vu, et le verdict False était correct et inutile.
    # Les chargements de monde des deux clients, eux, restent séquentiels : `--together` attend que le
    # premier soit entré dans le monde avant de lancer le second (deux chargements de la même sauvegarde en
    # même temps se disputent `TempSave`). Les deux clients vivent ensuite ensemble, ce qui suffit aux sondes
    # qui se mesurent mutuellement.
    #
    # La sonde de **créatures** n'est pas activée ici : elle a son étape (E). Les deux sondes se disputent le
    # joueur — la présence le téléporte à 25 m pour nager, ce qui déplace le rayon de pertinence et fait
    # apparaître des clés inconnues qui ne disent rien du suivi (mesuré : +422 sur la fenêtre, verdict faux).
    #
    # Le marqueur attendu est le **verdict de présence**, pas celui des créatures : la sonde finit bien après
    # le suivi des créatures, et attendre le premier marqueur couperait le run avant la preuve qui compte.
    args=(--clients=2 --timeout=300 --together
        "--client-args=-coopmode=client -coopdebug=presence"
        "--client-marker=\[sonde\] présence : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step B "deux clients : équipement réel et couleur jusqu'à l'avatar de l'autre joueur" assert_b "${args[@]}"
fi

if wanted C "$ONLY"; then
    args=(--clients=1 --timeout=200 --host-env=COOP_CREATURE_CHUNKS=1
        "--client-args=-coopmode=client -coopdebug=creature"
        "--client-marker=\[sonde\] créatures : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step C "plafond de diffusion abaissé : la garde de sacrifice parle" assert_c "${args[@]}"
fi

if wanted E "$ONLY"; then
    # Même harnais que B (deux clients vivants en même temps, chargements de monde séquentiels), sans la
    # sonde de présence : c'est le seul moyen de mesurer le suivi des créatures à deux clients sans que
    # l'autre sonde déplace le joueur sous ses pieds.
    args=(--clients=2 --timeout=260 --together
        "--client-args=-coopmode=client -coopdebug=creature"
        "--client-marker=\[sonde\] créatures : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step E "deux clients : le même monde suivi de part et d'autre, sans clé inconnue" assert_e "${args[@]}"
fi

if wanted F "$ONLY"; then
    # L'hôte porte l'autotest (`-coopdebug=save` vit dans le plugin **serveur**). Le client n'a aucune sonde :
    # il sert à rendre le run réaliste — la question est « l'hôte sauvegarde pendant qu'un client est là »,
    # pas « l'hôte sauvegarde seul ». `--wait-server-marker` attend le verdict de l'hôte **après** le client.
    args=(--clients=1 --timeout=300
        "--host-args=-coopmode=server -coopdebug=save"
        "--client-args=-coopmode=client"
        "--client-marker=\\[net\\] connecté au serveur"
        "--wait-server-marker=\[autotest\] sauvegarde serveur pendant un run -> verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step F "sauvegarde côté hôte : déclenchée en jeu, écriture disque observée" assert_f "${args[@]}"
fi

if wanted G "$ONLY"; then
    # La sonde vit chez l'hôte (elle déplace un véhicule du monde serveur). Le client ne sert qu'à rendre
    # le run réaliste : la question de M4 est ce qu'une annonce coûte, pas si un client isolé voit le Seamoth.
    args=(--clients=1 --timeout=300
        "--host-args=-coopmode=server -coopdebug=vehicle"
        "--client-args=-coopmode=client"
        "--client-marker=\\[net\\] connecté au serveur"
        "--wait-server-marker=\\[sonde\\] véhicule : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step G "véhicule en mouvement : un Seamoth à 20 m/s peuple la bande ≥2 m par annonce" assert_g "${args[@]}"
fi

if wanted H "$ONLY"; then
    # L'hôte est **dédié** : `-batchmode` supprime la fenêtre et fait basculer le plugin serveur en mode
    # sans personnage exposé ; `-nographics` évite d'ouvrir un device graphique. Le marqueur client attend
    # la ligne `[ping]` (≈ 25 s après la connexion), donc bien après le délai où un avatar apparaîtrait.
    args=(--clients=1 --timeout=300
        "--host-args=-coopmode=server -batchmode -nographics"
        "--client-args=-coopmode=client"
        "--client-marker=\\[ping\\].*avatarsActifs=0")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step H "serveur dédié : sans fenêtre, sans personnage, client admis" assert_h "${args[@]}"
fi

if wanted K "$ONLY"; then
    # Deux runs, et c'est le point de l'étape : le premier met le Seamoth **dans la sauvegarde**, le second
    # mesure une vraie partie. Un seul run ne peut pas faire les deux — les clients ont déjà chargé le monde
    # quand la sauvegarde est écrite, donc leur monde ne contient pas encore le véhicule.
    #
    # La préparation **modifie la sauvegarde du développeur** : elle y ajoute un Seamoth et déplace le joueur
    # à l'ancre. C'est voulu pour la mesure, mais ça contamine les runs suivants — l'étape B (nage puis arrêt)
    # dépend de la position de départ du joueur et a échoué deux fois après une préparation de K. La fixture
    # est donc copiée avant, puis restaurée après : K devient rejouable sans laisser de trace.
    SAVE_DIR="$GAME_DIR_BASH/SNAppData/SavedGames/slot0000"
    SAVE_BACKUP=""
    if [ -d "$SAVE_DIR" ]; then
        SAVE_BACKUP="$WORK_DIR/save-slot0000"
        rm -rf "$SAVE_BACKUP"
        if cp -a "$SAVE_DIR" "$SAVE_BACKUP" 2>/dev/null; then
            echo "  sauvegarde : copie de sécurité de slot0000 (restaurée à la fin de l'étape)"
        else
            SAVE_BACKUP=""
            echo "  sauvegarde : copie impossible — la fixture ne sera pas restaurée" >&2
        fi
    else
        echo "  sauvegarde : $SAVE_DIR introuvable — la fixture ne sera pas restaurée" >&2
    fi

    echo
    echo "=== étape K (préparation) — fabriquer un Seamoth et le sauvegarder dans la partie de l'hôte ==="
    prep_args=(--clients=1 --timeout=300
        "--host-args=-coopmode=server -coopdebug=vpersist"
        "--client-args=-coopmode=client"
        "--client-marker=\\[net\\] connecté au serveur"
        "--wait-server-marker=\\[sonde\\] véhicule persistant : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        prep_args+=(--no-build)
    fi

    prep_started=$SECONDS
    run_session "$SESSION" "${prep_args[@]}"
    prep_rc=$?
    if [ "$prep_rc" = 0 ]; then
        echo "  préparation : OK ($((SECONDS - prep_started))s)"
    else
        FAILURES+=("K : la préparation a échoué (code $prep_rc) — le Seamoth n'est pas garanti dans la sauvegarde")
        echo "  préparation : ÉCHEC (code $prep_rc)"
    fi

    sleep "$SETTLE_SECONDS"
    copy_logs "K-prepare"
    assert_k_prepare

    # Deux clients, chargements séquentiels (`--together`) pendant que l'hôte déplace le véhicule pendant
    # 210 s : chacun a le temps d'entrer dans le monde et d'échantillonner ses 12 s.
    args=(--clients=2 --together --timeout=420
        "--host-env=COOP_VEHICLE_SECONDS=210"
        "--host-args=-coopmode=server -coopdebug=vehicle"
        "--client-args=-coopmode=client -coopdebug=vehicle"
        "--client-marker=\\[sonde\\] véhicule client : verdict="
        "--wait-server-marker=\\[sonde\\] véhicule : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step K "véhicule de la sauvegarde : clé d'identifiant identique des deux côtés, sans rattachement" assert_k "${args[@]}"

    # Restauration **après** l'arrêt des instances (run-session les a fermées) : le jeu ne doit pas réécrire
    # la sauvegarde pendant qu'on la remplace.
    if [ -n "$SAVE_BACKUP" ] && [ -d "$SAVE_BACKUP" ]; then
        rm -rf "$SAVE_DIR"
        if cp -a "$SAVE_BACKUP" "$SAVE_DIR" 2>/dev/null; then
            echo "  sauvegarde : fixture slot0000 restaurée (Seamoth et position du joueur d'origine)"
        else
            FAILURES+=("K : la fixture slot0000 n'a pas pu être restaurée — les runs suivants partiront d'un état modifié")
            echo "  sauvegarde : RESTAURATION ÉCHOUÉE" >&2
        fi
    fi
fi

if wanted J "$ONLY"; then
    # Le couple de sondes : l'hôte déplace le Seamoth **120 s** (`COOP_VEHICLE_SECONDS`), assez pour qu'un
    # client entre dans le monde et observe ; le client mesure le pas par image pendant 12 s. Le véhicule
    # fait des allers-retours dans ±50 m pour rester dans le rayon de pertinence (150 m) pendant toute la
    # durée — une ligne droite de 120 s à 20 m/s sortirait du rayon et le client cesserait de le recevoir.
    args=(--clients=1 --timeout=300
        "--host-env=COOP_VEHICLE_SECONDS=120"
        "--host-args=-coopmode=server -coopdebug=vehicle"
        "--client-args=-coopmode=client -coopdebug=vehicle"
        "--client-marker=\\[sonde\\] véhicule client : verdict="
        "--wait-server-marker=\\[sonde\\] véhicule : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step J "véhicule : diffusion serveur à 10 Hz et interpolation cliente mesurée image par image" assert_j "${args[@]}"
fi

if wanted M "$ONLY"; then
    # M4 / v9 : un client **conduit** le Seamoth de la sauvegarde, l'autre tente de le lui prendre. Le
    # premier garde la main ; le second reçoit un refus nommé (`VehicleControlResult`).
    args=(--clients=2 --together --timeout=240
        "--host-args=-coopmode=server"
        "--client-args=-coopmode=client -coopdebug=drive"
        "--client-marker=\\[sonde\\] conduite : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step M "conduite autoritaire : un client conduit, l'autre se voit refuser le volant" assert_m "${args[@]}"
fi

if wanted L "$ONLY"; then
    # M5 : deux clients réclament le **même objet réel**. Le premier est accordé, le second refusé ; le
    # second envoie ensuite une clé inventée et une réclamation sous l'id 1, puis ramasse pour de vrai.
    args=(--clients=2 --together --timeout=240
        "--host-args=-coopmode=server"
        "--client-args=-coopmode=client -coopdebug=pickup"
        "--client-marker=\\[sonde\\] ramassage : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step L "ramassage autoritaire : la première réclamation gagne, la seconde est refusée, l'id falsifié réécrit" assert_l "${args[@]}"
fi

if wanted I "$ONLY"; then
    # Hôte dédié : aucun avatar d'hôte ne vient perturber la mesure. Le client exécute la sonde qui appelle
    # `UWE.FreezeTime.Begin(Id.IngameMenu)` — l'appel exact du menu pause — après que le monde a repris.
    args=(--clients=1 --timeout=300
        "--host-args=-coopmode=server -batchmode -nographics"
        "--client-args=-coopmode=client -coopdebug=pause"
        "--client-marker=\\[sonde\\] pause : verdict=")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step I "menu Échap du client : le temps ne s'arrête pas" assert_i "${args[@]}"
fi

if wanted D "$ONLY"; then
    # L'autotest ouvre le menu pause lui-même, puis appelle l'écriture directement : aucun réglage
    # particulier côté hôte, le client est le seul à devoir se taire.
    args=(--clients=1 --timeout=240
        "--client-args=-coopmode=client -coopdebug=saveguard"
        "--client-marker=\[autotest\] sauvegarde cliente bloquée")
    if [ "$NO_BUILD" = 1 ]; then
        args+=(--no-build)
    fi
    step D "garde de sauvegarde : entrée et écriture bloquées, mesurées" assert_d "${args[@]}"
fi

echo
echo "===== RÉSULTAT ($((SECONDS - VERIFY_STARTED))s) ====="
for report in "${STEP_REPORTS[@]}"; do
    echo "  $report"
done
if [ "${#STEP_REPORTS[@]}" = 0 ]; then
    echo "  (aucune étape lancée)"
fi

if [ "${#FAILURES[@]}" = 0 ]; then
    echo "VERDICT GLOBAL : tout est vert"
    if [ "$KEEP_LOGS" = 1 ]; then
        echo "journaux conservés : $WORK_DIR"
    else
        rm -rf "$WORK_DIR"
        echo "journaux temporaires supprimés (--keep-logs pour les garder)"
    fi
    exit 0
fi

echo "VERDICT GLOBAL : ${#FAILURES[@]} échec(s)"
for failure in "${FAILURES[@]}"; do
    echo "  - $failure"
done
echo "journaux conservés : $WORK_DIR"
exit 1
