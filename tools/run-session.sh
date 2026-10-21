#!/bin/bash
# tools/run-session.sh — lance un hôte et un client, attend les marqueurs du journal, rend la main.
#
# Pourquoi ce script existe : lancer les deux instances puis « dormir 160 s, dormir 300 s » coûtait
# 460 s de marge pour 26 s de travail réel (mesuré : client lancé 00:36:57, monde chargé 00:37:08,
# sonde finie 00:37:23). Ici, chaque étape attend la ligne qui **prouve** qu'elle est terminée :
#   - hôte   : « MONDE CHARGÉ »       (≈ 20 s après le lancement, mesuré à 00:34:37)
#   - client : marqueur de fin de sonde, ou de connexion sans sonde (≈ 26 s)
# Un run complet tient donc en une cinquantaine de secondes, et une étape qui n'arrive pas est
# **annoncée** : code de sortie 1, journal affiché, et les instances **du run** arrêtées. Jamais un
# succès silencieux.
#
# Usage :
#   tools/run-session.sh                              # hôte + client avec la sonde d'avatar
#   tools/run-session.sh --client-args="-coopdebug=avatar"
#   tools/run-session.sh --keep                       # laisse les deux instances tourner
#   tools/run-session.sh --kill-all                   # arrêt d'urgence : TOUS les Subnautica.exe
#   tools/run-session.sh --no-build                   # ne rebâtit pas, ne redéploie pas
#   tools/run-session.sh --together                   # lance tous les clients **avant** d'attendre leurs
#                                                     # marqueurs (nécessaire aux sondes qui se mesurent
#                                                     # mutuellement : l'un doit déjà être là)
#   tools/run-session.sh --minutes=30 --clients=2 \
#       --client-marker="connecté au serveur"         # **session longue** (P4) : une fois les marqueurs
#                                                     # atteints, la session est tenue 30 min avant l'arrêt
#   tools/run-session.sh --help
#
# `--minutes` ne remplace pas la session humaine de P4 : il tient les instances en vie le temps demandé et
# laisse le graveur (`tools/session-verdict.sh`) décider. Sans lui, un run rend la main dès les marqueurs
# — c'est ce qui convenait aux sondes, pas à une session de jeu.
#
# Depuis que l'adresse du serveur n'a plus de défaut (`127.0.0.1` était un piège pour un joueur
# distant), chaque client lancé ici reçoit **explicitement** `-coopserver=<adresse>` : 127.0.0.1 par
# défaut, surchargeable par `--server-addr=` ou `COOP_SERVER`. Sans ça, toutes les étapes existantes
# resteraient au menu à attendre une saisie qui n'arrive jamais dans un run automatisé.
#
# Les marqueurs sont des expressions régulières étendues, cherchées **après l'en-tête du run courant**
# (l'en-tête « === Coop client — … === » que chaque instance réécrit au démarrage) : une ligne laissée
# par le run précédent ne peut donc pas passer pour un succès.
#
# **Ce script n'arrête que les instances qu'il a lancées.** Chaque lancement retient le PID Windows du
# processus — le PID MSYS rendu par `$!` est traduit par `ps -W`, avec quelques essais, parce qu'un
# processus met un instant à apparaître — et l'écrit dans `BepInEx/logs/.coop-session-pids`. Un run
# suivant relit ce fichier pour arrêter ce qu'un run précédent a laissé derrière lui (plantage, `--keep`),
# et rien d'autre : une session de jeu ouverte à côté **survit** à un run de test. L'arrêt global
# (`taskkill //F //IM Subnautica.exe`) reste possible, mais il faut le demander : `--kill-all`, et le
# script dit alors qu'il arrête tout.
#
# Limite dit franchement : Windows recycle les numéros de PID. Si un PID enregistré avait été repris par
# une autre instance de Subnautica lancée à la main entre-temps, cette instance serait arrêtée à tort. Le
# fichier est donc effacé dès que le script s'en sert (`prepare()` et la fin de run), et il ne survit
# qu'entre deux runs.

set -o pipefail
set -u

# Dossier du jeu. Surchargeable : SUBNAUTICA_GAME_DIR=/c/autre/chemin tools/run-session.sh
GAME_DIR_BASH="${SUBNAUTICA_GAME_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Subnautica}"

HOST_ARGS="-coopmode=server"
CLIENT_ARGS="-coopmode=client -coopdebug=avatar"
# Arguments des clients **suivants** : le premier garde `--client-args`, les autres prennent ceux-ci
# quand ils sont fournis. C'est ce qui permet de lancer un client sur une autre sauvegarde que l'hôte
# (`-coopsave=slot0001`) sans changer le premier, qui doit rester identique pour servir de contrôle.
CLIENT_ARGS_2=""
HOST_MARKER="MONDE CHARGÉ"
CLIENT_MARKER=""

# Motif attendu dans le journal du **serveur** après le lancement des clients. Vide = rien à attendre.
# Sert aux autotests qui tournent sur l'hôte et finissent après la connexion des clients (la sauvegarde
# de l'hôte, par exemple) : sans cette attente, le harnais rendrait la main avant leur verdict.
WAIT_SERVER_MARKER=""
CLIENTS=1
STEP_TIMEOUT=90

# Adresse du serveur imposée à chaque client. `COOP_SERVER` est aussi ce que lit le plugin : le harnais
# et un lancement manuel parlent donc le même langage.
SERVER_ADDR="${COOP_SERVER:-127.0.0.1}"
KEEP=0
BUILD=1
KILL_ALL=0
TOGETHER=0
# Écart (s) entre deux lancements de clients en mode `--together`.
CLIENT_STAGGER=6

# Reconnexion (P2) : tuer un client déjà prêt, attendre que le serveur le constate, puis le relancer
# avec d'autres arguments (sans sonde, le plus souvent) et attendre son marqueur. Zéro = désactivé.
RECONNECT_CLIENT=0
RECONNECT_ARGS=""
RECONNECT_MARKER=""
RECONNECT_DELAY=6
RECONNECT_SERVER_MARKER="PlayerLeft diffusé"
RECONNECT_SETTLE=10
# Durée de **session** (minutes) tenue après les marqueurs, avant l'arrêt. Zéro = comportement des sondes :
# le script rend la main dès les marqueurs atteints. P4 demande 30.
MINUTES=0
# Délai (s) de session avant le kill du client de `--reconnect` : zéro = kill dès le marqueur. P4 veut une
# reconnexion **au milieu** de la session, pas au démarrage.
RECONNECT_AFTER=0
# Attente après les marqueurs, avant l'arrêt : laisse les compteurs périodiques (`[ping]`) se réécrire.
SETTLE=0
LAST_WINPID=""

# Marqueur de « le monde est chargé » d'un client — sert à ne pas lancer le suivant pendant ce chargement.
CLIENT_WORLD_MARKER="MONDE CHARGÉ"
HOST_ENV=""

# Variables d'environnement du **client** (séparées par des espaces), appliquées à chaque client et au
# client relancé. Une sonde qui doit se comporter autrement selon le run (garder le volant, par exemple)
# ne peut pas lire un argument : `-coopdebug` est déjà pris par le choix de la sonde.
CLIENT_ENV=""

# Motif dont la présence dans un journal de client fait **échouer** le run. Par défaut : un verdict de sonde
# qui vaut False. Sans ça, « le run s'est terminé » se lit comme « tout va bien », alors qu'un verdict False
# est justement le cas où il faut s'arrêter et regarder.
FAIL_IF="verdict=False"

usage() {
    # En-tête du fichier, jusqu'à la première ligne de code : la documentation est à un seul endroit.
    awk 'NR > 1 && /^set -/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"
    cat <<'OPTIONS'

Options :
  --host-args="…"       arguments de l'hôte        (défaut : -coopmode=server)
  --client-args="…"     arguments du client        (défaut : -coopmode=client -coopdebug=avatar)
  --client-2-args="…"   arguments du client n° 2+  (défaut : ceux du client 1 ; sert à imposer une
                        autre sauvegarde avec -coopsave=slot0001)
  --host-marker=REGEX   ce qui prouve que l'hôte a chargé son monde (défaut : MONDE CHARGÉ)
  --wait-server-marker=REGEX  ligne du journal **serveur** attendue après les clients (autotest de l'hôte)
  --client-marker=REGEX ce qu'on attend du client  (défaut : fin de sonde si -coopdebug, sinon connexion)
  --clients=N           nombre de clients à lancer   (défaut : 1 ; les suivants écrivent coop-client<i>.log)
  --server-addr=ADRESSE adresse imposée aux clients via -coopserver  (défaut : 127.0.0.1)
  --host-env=VARIABLE=VALEUR   variable(s) d'environnement pour l'hôte, séparées par des espaces
  --client-env=VARIABLE=VALEUR variable(s) d'environnement pour chaque client (et le client relancé)
                               (ex. "COOP_CREATURE_CHUNKS=1 COOP_DESPAWN_DELAY=20")
  --fail-if=REGEX       motif qui fait échouer le run (défaut : verdict=False ; "" pour désactiver)
  --timeout=SECONDES    délai maximal **par étape**  (défaut : 90)
  --together            lance tous les clients (sans chevaucher leurs chargements de monde)
  --keep                ne tue pas les instances à la fin (leurs PID restent enregistrés)
  --kill-all            arrête TOUS les Subnautica.exe de la machine, pas seulement ceux lancés ici
  --no-build            ne reconstruit pas et ne redéploie pas les DLL
  --reconnect=N         tue le client N une fois prêt, attend « PlayerLeft diffusé » côté serveur,
                        puis le relance (journal coop-client-N-b.log) — scénario P2
  --reconnect-args="…"  arguments du client relancé (défaut : ceux du client N)
  --reconnect-marker=REGEX  marqueur attendu du client relancé (défaut : CLIENT_MARKER)
  --reconnect-after=S   attente de S secondes de session **avant** le kill du client (défaut : 0)
  --reconnect-delay=S   attente entre le kill et la relance (défaut : 6)
  --reconnect-server-marker=REGEX  ligne serveur attendue après le kill (défaut : PlayerLeft diffusé)
  --reconnect-settle=S  attente après la relance pour laisser les compteurs se réécrire (défaut : 10)
  --settle=S            attente après les marqueurs, avant l'arrêt (défaut : 0) — pour lire un compteur
  --minutes=N           tient la session N minutes après les marqueurs avant d'arrêter (défaut : 0)
OPTIONS
}

for argument in "$@"; do
    case "$argument" in
        --host-args=*) HOST_ARGS="${argument#*=}" ;;
        --client-args=*) CLIENT_ARGS="${argument#*=}" ;;
        --client-2-args=*) CLIENT_ARGS_2="${argument#*=}" ;;
        --host-marker=*) HOST_MARKER="${argument#*=}" ;;
        --wait-server-marker=*) WAIT_SERVER_MARKER="${argument#*=}" ;;
        --client-marker=*) CLIENT_MARKER="${argument#*=}" ;;
        --clients=*) CLIENTS="${argument#*=}" ;;
        --server-addr=*) SERVER_ADDR="${argument#*=}" ;;
        --host-env=*) HOST_ENV="${argument#*=}" ;;
        --client-env=*) CLIENT_ENV="${argument#*=}" ;;
        --fail-if=*) FAIL_IF="${argument#*=}" ;;
        --timeout=*) STEP_TIMEOUT="${argument#*=}" ;;
        --together) TOGETHER=1 ;;
        --keep) KEEP=1 ;;
        --kill-all) KILL_ALL=1 ;;
        --no-build) BUILD=0 ;;
        --reconnect=*) RECONNECT_CLIENT="${argument#*=}" ;;
        --reconnect-args=*) RECONNECT_ARGS="${argument#*=}" ;;
        --reconnect-marker=*) RECONNECT_MARKER="${argument#*=}" ;;
        --reconnect-after=*) RECONNECT_AFTER="${argument#*=}" ;;
        --reconnect-delay=*) RECONNECT_DELAY="${argument#*=}" ;;
        --reconnect-server-marker=*) RECONNECT_SERVER_MARKER="${argument#*=}" ;;
        --reconnect-settle=*) RECONNECT_SETTLE="${argument#*=}" ;;
        --settle=*) SETTLE="${argument#*=}" ;;
        --minutes=*) MINUTES="${argument#*=}" ;;
        -h|--help) usage; exit 0 ;;
        *) echo "option inconnue : $argument (--help)" >&2; exit 2 ;;
    esac
done

# Sans marqueur imposé : une sonde se termine par son relevé final, une session ordinaire par la
# connexion au serveur (et, quand l'autre joueur est là, par l'avatar instancié).
if [ -z "$CLIENT_MARKER" ]; then
    case "$CLIENT_ARGS" in
        *-coopdebug=*) CLIENT_MARKER='\[sonde\] capture écrite' ;;
        *) CLIENT_MARKER='connecté au serveur|avatar instancié pour' ;;
    esac
fi

LOG_DIR="$GAME_DIR_BASH/BepInEx/logs"
PLUGINS="$GAME_DIR_BASH/BepInEx/plugins/Coop"
SERVER_LOG="$LOG_DIR/coop-server.log"
CLIENT_LOG="$LOG_DIR/coop-client.log"

elapsed_since() { # $1 = instant de départ en secondes
    echo "$(( $(date +%s) - $1 ))s"
}

# PID Windows des instances lancées par ce script — et par les runs précédents : une ligne par PID.
#
# Ce fichier n'est pas un confort : sans lui, `prepare()` ne peut pas savoir quelles instances traîner
# d'un run précédent sans arrêter **toutes** celles de la machine, y compris une session que le joueur a
# ouverte pour jouer. C'est exactement ce que le script faisait avant.
PID_STATE="$LOG_DIR/.coop-session-pids"

# Vrai si ce PID Windows appartient encore au jeu (garde avant un `taskkill` : Windows recycle les PID,
# et un PID enregistré peut désigner un tout autre programme si l'instance est morte depuis longtemps).
#
# `***` est accepté parce que MSYS ne sait pas toujours nommer un processus natif : refuser ce cas
# laisserait des instances vivantes derrière nous, ce qui est le défaut que ce fichier corrige.
is_game_pid() {
    local winpid="$1"
    [ -n "$winpid" ] || return 1
    ps -W 2>/dev/null | awk -v p="$winpid" '
        $4 == p { found = 1; if ($NF ~ /Subnautica/ || $NF == "***") named = 1 }
        END { exit (found && named) ? 0 : 1 }
    '
}

# Traduit un PID MSYS (celui que rend `$!`) en PID Windows (celui qu'accepte `taskkill`).
#
# Les deux ne sont pas les mêmes nombres : MSYS tient sa propre table de processus, et `ps -W` est le seul
# outil présent ici qui les donne tous les deux (wmic a été retiré de Windows 11). D'où les quelques
# essais : le processus met un instant à apparaître dans cette table.
winpid_of() {
    local msys_pid="$1" attempt=0 winpid=""
    while [ "$attempt" -lt 20 ]; do
        winpid=$(ps -W 2>/dev/null | awk -v p="$msys_pid" '$1 == p { print $4; exit }')
        if [ -n "$winpid" ]; then
            echo "$winpid"
            return 0
        fi
        attempt=$((attempt + 1))
        sleep 0.25
    done
    return 1
}

# Lance une instance du jeu et **retient son PID Windows**.
#
# $1 = variable d'environnement à poser (vide = aucune), $2 = arguments du jeu, $3 = étiquette.
launch_instance() {
    local env_pair="$1" args="$2" label="$3"
    local msys_pid winpid
    local env_words=()

    # Le `cd` est fait dans le shell courant puis défait, et non dans un sous-shell : `( … & )` rendrait le
    # PID du sous-shell, pas celui de l'exécutable, et on arrêterait le mauvais processus. `env` remplace
    # son propre processus (exec), donc le PID rendu par `$!` reste bien celui du jeu.
    pushd "$GAME_DIR_BASH" >/dev/null || return 1
    if [ -n "$env_pair" ]; then
        # Plusieurs `VARIABLE=VALEUR` séparées par des espaces (« COOP_A=1 COOP_B=2 ») : chacune doit
        # être un argument distinct d'`env`. Une valeur contenant un espace n'est pas supportée, et
        # aucune variable du projet n'en a besoin (compteurs et durées sont des nombres).
        read -r -a env_words <<< "$env_pair"
        env "${env_words[@]}" ./Subnautica.exe $args >/dev/null 2>&1 &
    else
        ./Subnautica.exe $args >/dev/null 2>&1 &
    fi
    msys_pid=$!
    popd >/dev/null

    if ! winpid=$(winpid_of "$msys_pid"); then
        echo "[avertissement] $label : PID Windows introuvable pour le PID MSYS $msys_pid — cette instance" >&2
        echo "                ne sera pas arrêtée par ce script (la retrouver avec --kill-all)." >&2
        return 1
    fi

    mkdir -p "$LOG_DIR"
    echo "$winpid" >> "$PID_STATE"
    LAST_WINPID="$winpid"
    echo "      $label lancé (PID Windows $winpid)"
    return 0
}

# Arrête les instances **lancées par ce script** — celles enregistrées dans $PID_STATE — puis efface le
# fichier. Aucun autre `Subnautica.exe` n'est touché : c'est la garantie qui manquait.
kill_instances() {
    if [ "$KILL_ALL" = "1" ]; then
        echo "[arrêt] --kill-all : TOUS les Subnautica.exe de la machine sont arrêtés, pas seulement ceux du run." >&2
        taskkill //F //IM Subnautica.exe >/dev/null 2>&1 || true
        rm -f "$PID_STATE"
        return
    fi

    if [ ! -s "$PID_STATE" ]; then
        return
    fi

    local pid
    while read -r pid; do
        [ -n "$pid" ] || continue
        if ! is_game_pid "$pid"; then
            echo "[arrêt] PID $pid ignoré : plus un Subnautica.exe en vie sous ce numéro."
            continue
        fi

        if taskkill //F //PID "$pid" >/dev/null 2>&1; then
            echo "[arrêt] instance arrêtée (PID Windows $pid)."
        else
            echo "[arrêt] PID $pid : taskkill a échoué (instance déjà fermée ?)." >&2
        fi
    done < "$PID_STATE"

    rm -f "$PID_STATE"
}

# Arrête les instances laissées par un run précédent (celles de $PID_STATE), construit, déploie, et
# **efface les journaux**.
#
# L'effacement n'est pas un détail : sans lui, la première seconde d'un run voyait encore le fichier du
# run précédent, et un marqueur périmé passait pour un succès — un run de 4 s déclarait « marqueurs
# atteints » en citant les heures du run d'avant. Le jeu réécrit de toute façon ces fichiers au
# démarrage (FileMode.Create), donc les effacer ne perd rien : tout ce qui sera lu ensuite appartient
# au run courant.
prepare() {
    echo "[1/3] arrêt des instances du run précédent (PID enregistrés), déploiement des DLL, journaux effacés"
    kill_instances
    sleep 2
    # Tous les journaux de client, y compris suffixés : un run à deux clients relit les deux, et un ancien
    # fichier non effacé ferait passer un marqueur périmé pour un succès (déjà vu, cf. plus haut).
    rm -f "$SERVER_LOG" "$LOG_DIR"/coop-client*.log

    if [ "$BUILD" = "1" ]; then
        dotnet build src/Coop.Client/Coop.Client.csproj -v q --nologo || return 1
        dotnet build src/Coop.Server/Coop.Server.csproj -v q --nologo || return 1
        dotnet build src/Coop.Model/Coop.Model.csproj -v q --nologo || return 1
    fi

    cp -f src/Coop.Client/bin/Debug/Coop.Client.dll "$PLUGINS/" || return 1
    cp -f src/Coop.Server/bin/Debug/Coop.Server.dll "$PLUGINS/" || return 1
    cp -f src/Coop.Model/bin/Debug/Coop.Model.dll "$PLUGINS/" || return 1
}

# Attend un marqueur **après l'en-tête du run courant**. Le fichier est réécrit au démarrage de
# l'instance (FileMode.Create) : chercher à partir du dernier en-tête évite qu'une ligne du run
# précédent, encore présente pendant les premières millisecondes, fasse croire à un succès.
wait_for_marker() {
    local log="$1" pattern="$2" label="$3"
    local deadline=$(( $(date +%s) + STEP_TIMEOUT ))

    while :; do
        if [ -s "$log" ]; then
            local header
            header=$(grep -n '^=== Coop .* — ' "$log" | tail -1 | cut -d: -f1)
            if [ -n "$header" ] && tail -n "+$((header + 1))" "$log" | grep -qE "$pattern"; then
                tail -n "+$((header + 1))" "$log" | grep -E "$pattern" | tail -1
                return 0
            fi
        fi

        if [ "$(date +%s)" -ge "$deadline" ]; then
            echo "[échec] $label : « $pattern » n'est pas apparu en ${STEP_TIMEOUT}s" >&2
            if [ -s "$log" ]; then
                echo "--- dernières lignes de $(basename "$log") ---" >&2
                tail -15 "$log" >&2
            else
                # Un journal vide est une information en soi : l'instance n'a pas démarré (ou pas dans
                # ce rôle), ce n'est pas la même panne qu'un marqueur qui tarde.
                echo "--- $(basename "$log") est vide : l'instance n'a rien écrit ---" >&2
            fi
            return 1
        fi
        sleep 0.25
    done
}

started=$(date +%s)

prepare || { echo "[échec] construction ou déploiement" >&2; exit 2; }

echo "[2/3] hôte : $HOST_ARGS ${HOST_ENV:+(env $HOST_ENV)} — attente de « $HOST_MARKER »"
host_start=$(date +%s)
# Une variable par processus : c'est ce qui permet de faire varier le serveur (plafond de diffusion
# abaissé pour éprouver sa garde, par exemple) sans toucher aux clients ni à la configuration partagée.
launch_instance "$HOST_ENV" "$HOST_ARGS" "hôte" || true
wait_for_marker "$SERVER_LOG" "$HOST_MARKER" "hôte" || { kill_instances; exit 1; }
echo "      hôte prêt en $(elapsed_since "$host_start")"

echo "[3/3] $CLIENTS client(s) : $CLIENT_ARGS${CLIENT_ARGS_2:+ (clients 2+ : $CLIENT_ARGS_2)} — attente de « $CLIENT_MARKER »"

# Par défaut les clients sont lancés **l'un après l'autre**, chacun attendu sur son propre marqueur : les
# mondes arrivent ainsi séparément et on sait lequel est prêt.
#
# `--together` lance tous les clients avant d'attendre leurs marqueurs : c'est la seule façon de mesurer ce
# qui se mesure **entre deux clients**. Un harnais qui n'ouvre le second client qu'après le verdict du premier
# ne lui laisse qu'un désert à mesurer (constaté : « aucun joueur distant », puis un verdict False parfaitement
# correct et parfaitement inutile).
#
# Mais les **chargements de monde**, eux, restent séparés : le client suivant n'est lancé qu'une fois le
# précédent entré dans le monde. Deux instances qui chargent la même sauvegarde en même temps se disputent le
# dossier `TempSave` du jeu — mesuré : `IOException: Sharing violation on path …/TempSave/tmp…/scene-objects.bin`
# — et la seconde termine parfois au point d'origine (2.1, -2.6, -0.7) au lieu du point de la sauvegarde, à
# 128 m de là. Ce n'était pas un défaut du suivi des créatures mais un monde chargé de travers, et les deux
# se ressemblaient : le client recevait les créatures d'un rayon où il n'était pas.
client_logs=()
client_suffixes=()
client_starts=()
client_pids=()

for index in $(seq 1 "$CLIENTS"); do
    if [ "$index" = "1" ]; then
        client_logs+=("$CLIENT_LOG")
        client_suffixes+=("")
    else
        # Sans suffixe, le second client réécrirait le journal du premier (même rôle « client », même
        # dossier de jeu) : on ne saurait plus lequel a écrit quoi. Le nom du fichier est **le même
        # suffixe** que la variable d'environnement — `coop-client-2.log` et non `coop-client2.log` :
        # les deux formes ont coexisté une fois, et le harnais a attendu 200 s un fichier qui n'existait
        # pas pendant que le client écrivait à côté.
        client_logs+=("$LOG_DIR/coop-client-$index.log")
        client_suffixes+=("-$index")
    fi
    client_starts+=("0")
    client_pids+=("")
done

# Adresse explicite ajoutée aux arguments d'un client, sans écraser un `-coopserver` déjà fourni.
with_server_addr() { # $1 = arguments du client
    case " $1 " in
        *" -coopserver="*) printf '%s' "$1" ;;
        *) printf '%s -coopserver=%s' "$1" "$SERVER_ADDR" ;;
    esac
}

launch_client() { # $1 = index
    local index="$1" suffix="${client_suffixes[$((index - 1))]}"
    client_starts[$((index - 1))]=$(date +%s)

    # Le client 1 garde ses arguments : c'est le contrôle « même sauvegarde, donc admis ». Les suivants
    # peuvent recevoir une autre sauvegarde, ce qui est la seule façon d'éprouver le refus P0 en jeu.
    local client_args="$CLIENT_ARGS"
    if [ "$index" -gt 1 ] && [ -n "$CLIENT_ARGS_2" ]; then
        client_args="$CLIENT_ARGS_2"
    fi

    client_args=$(with_server_addr "$client_args")

    if [ -n "$suffix" ]; then
        launch_instance "COOP_LOG_SUFFIX=$suffix${CLIENT_ENV:+ $CLIENT_ENV}" "$client_args" "client $index" || true
    else
        launch_instance "${CLIENT_ENV:-}" "$client_args" "client $index" || true
    fi

    client_pids[$((index - 1))]="$LAST_WINPID"
}

wait_client() { # $1 = index
    local index="$1" log="${client_logs[$((index - 1))]}" start="${client_starts[$((index - 1))]}"
    wait_for_marker "$log" "$CLIENT_MARKER" "client $index" || { kill_instances; exit 1; }
    echo "      client $index prêt en $(elapsed_since "$start") ($(basename "$log"))"
}

if [ "$TOGETHER" = "1" ]; then
    # Écart volontaire entre deux lancements : deux `Subnautica.exe` démarrés dans le même dixième de
    # seconde n'aboutissent pas tous les deux (mesuré : le premier processus reste vivant sans jamais
    # écrire une ligne de log). Quelques secondes suffisent, et restent très en dessous de ce qu'une
    # sonde mutuelle peut tolérer.
    for index in $(seq 1 "$CLIENTS"); do
        [ "$index" -gt 1 ] && sleep "$CLIENT_STAGGER"
        launch_client "$index"

        # Le monde du client précédent doit être chargé avant de lancer le suivant : voir l'en-tête
        # (`TempSave` partagé). Le lancement reste « groupé » — tous les clients vivent pendant les sondes —
        # mais les chargements ne se chevauchent plus.
        if [ "$index" -lt "$CLIENTS" ]; then
            previous_log="${client_logs[$((index - 1))]}"
            wait_for_marker "$previous_log" "$CLIENT_WORLD_MARKER" "client $index (monde chargé)" || { kill_instances; exit 1; }
        fi
    done
    for index in $(seq 1 "$CLIENTS"); do
        wait_client "$index"
    done
else
    for index in $(seq 1 "$CLIENTS"); do
        launch_client "$index"
        wait_client "$index"
    done
fi

# Reconnexion (P2) : le client N est **tué** une fois sa sonde terminée, le serveur doit le constater
# (`PlayerLeft diffusé`), puis il revient sous un nouveau journal. Sans ce mécanisme, la reconnexion ne
# pouvait être mesurée qu'à la main — c'est-à-dire pas rejouée, donc pas une preuve.
if [ "$RECONNECT_CLIENT" -gt 0 ]; then
    idx="$RECONNECT_CLIENT"
    if [ "$idx" -gt "$CLIENTS" ]; then
        echo "[échec] --reconnect=$idx : seuls $CLIENTS client(s) sont lancés" >&2
        kill_instances
        exit 2
    fi

    pid="${client_pids[$((idx - 1))]}"
    if [ -z "$pid" ]; then
        echo "[échec] --reconnect=$idx : PID du client $idx inconnu (lancement raté ?)" >&2
        kill_instances
        exit 1
    fi

    # P4 veut une reconnexion **au milieu** de la session : sans ce délai, le kill tombe au démarrage et
    # ne prouve que « le client revient juste après être entré » (c'est ce que font les étapes U et X).
    if [ "$RECONNECT_AFTER" -gt 0 ]; then
        echo "[reconnexion] ${RECONNECT_AFTER}s de session avant l'arrêt forcé du client $idx"
        sleep "$RECONNECT_AFTER"
    fi

    echo "[reconnexion] arrêt forcé du client $idx (PID Windows $pid)"
    taskkill //F //PID "$pid" >/dev/null 2>&1 || true

    # Le PID tué ne doit plus figurer dans l'état : la fin de run l'ignorerait sinon (et dirait
    # « taskkill a échoué » sur un processus qui n'existe plus).
    if [ -f "$PID_STATE" ]; then
        grep -vx "$pid" "$PID_STATE" > "$PID_STATE.tmp" 2>/dev/null || true
        mv -f "$PID_STATE.tmp" "$PID_STATE"
    fi

    wait_for_marker "$SERVER_LOG" "$RECONNECT_SERVER_MARKER" "serveur (déconnexion du client $idx)"         || { kill_instances; exit 1; }

    echo "[reconnexion] attente de ${RECONNECT_DELAY}s avant la relance du client $idx"
    sleep "$RECONNECT_DELAY"

    reconnect_args="$RECONNECT_ARGS"
    [ -n "$reconnect_args" ] || reconnect_args="$CLIENT_ARGS"
    reconnect_args=$(with_server_addr "$reconnect_args")
    reconnect_marker="$RECONNECT_MARKER"
    [ -n "$reconnect_marker" ] || reconnect_marker="$CLIENT_MARKER"
    reconnect_log="$LOG_DIR/coop-client-$idx-b.log"

    launch_instance "COOP_LOG_SUFFIX=-$idx-b" "$reconnect_args" "client $idx (relancé)" || true
    client_pids[$((idx - 1))]="$LAST_WINPID"
    wait_for_marker "$reconnect_log" "$reconnect_marker" "client $idx (relancé)"         || { kill_instances; exit 1; }

    echo "[reconnexion] client $idx revenu — ${RECONNECT_SETTLE}s pour que les compteurs se réécrivent"
    sleep "$RECONNECT_SETTLE"
fi

if [ "$SETTLE" -gt 0 ]; then
    echo "[attente] ${SETTLE}s après les marqueurs (compteurs périodiques)"
    sleep "$SETTLE"
fi

# Autotest de l'hôte : il se déclenche après la connexion des clients et finit bien après. Sans cette
# attente, le harnais rendrait la main, `verify.sh` copierait les journaux, et le verdict — écrit plus tard —
# ne serait pas dans la copie.
if [ -n "$WAIT_SERVER_MARKER" ]; then
    echo "[4/4] côté serveur : attente de « $WAIT_SERVER_MARKER »"
    wait_for_marker "$SERVER_LOG" "$WAIT_SERVER_MARKER" "serveur (autotest)" || { kill_instances; exit 1; }
fi

# Session longue (P4) : les marqueurs disent que tout est en place, pas que la session tient. Ici on la
# **tient** le temps demandé, et chaque minute restante est écrite — un run qui s'arrête à 3 min sur 30 ne
# doit pas ressembler à un run complet.
if [ "$MINUTES" -gt 0 ]; then
    session_end=$(( $(date +%s) + MINUTES * 60 ))
    echo "[session] ${MINUTES} min tenues après les marqueurs — arrêt automatique à l'échéance"
    while [ "$(date +%s)" -lt "$session_end" ]; do
        remaining=$(( session_end - $(date +%s) ))
        printf '      %s min %ss restantes\n' "$((remaining / 60))" "$((remaining % 60))"
        sleep 60
    done
    echo "[session] échéance atteinte ($(elapsed_since "$started") depuis le lancement)"
fi

echo "run complet en $(elapsed_since "$started") (marqueurs atteints)"

# Vérification : un marqueur atteint dit que le run est allé au bout, pas qu'il a réussi. Ce qui échoue
# vraiment, c'est un verdict de sonde à False — et c'est au harnais de le dire, avec un code de sortie que
# personne n'a à interpréter.
failed_marker=0
if [ -n "$FAIL_IF" ]; then
    for index in $(seq 1 "$CLIENTS"); do
        if [ "$index" = "1" ]; then
            client_log="$CLIENT_LOG"
        else
            client_log="$LOG_DIR/coop-client-$index.log"
        fi

        if [ "$RECONNECT_CLIENT" = "$index" ] && [ -s "$LOG_DIR/coop-client-$index-b.log" ]; then
            # Le client relancé écrit dans un journal séparé : sans cette ligne, un verdict False
            # émis après la reconnexion passerait sous le radar.
            client_log="$LOG_DIR/coop-client-$index-b.log"
        fi

        if [ -s "$client_log" ] && grep -qE "$FAIL_IF" "$client_log"; then
            failed_marker=1
            echo "--- $(basename "$client_log") : « $FAIL_IF » présent ---" >&2
            grep -E "$FAIL_IF" "$client_log" | tail -5 >&2
        fi
    done
fi

if [ "$failed_marker" = "1" ]; then
    echo "run EN ÉCHEC : un verdict de sonde est faux (motif « $FAIL_IF »)" >&2
    kill_instances
    exit 1
fi

echo "aucun « $FAIL_IF » dans les journaux de client"

if [ "$KEEP" = "1" ]; then
    echo "instances laissées en marche (--keep) : un prochain run les arrêtera (PID enregistrés dans"
    echo "                                   « $PID_STATE »), sinon « tools/run-session.sh --kill-all »"
else
    kill_instances
    echo "instances arrêtées"
fi
