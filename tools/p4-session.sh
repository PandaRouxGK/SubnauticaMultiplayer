#!/bin/bash
# tools/p4-session.sh — tient une session de P4 (deux clients, durée réelle) et rend **un seul verdict**.
#
# P4 demande quatre choses mesurables : `0 exception`, `0 désync > 2 m`, « sauvegarde + rechargement +
# reconnexion », et un verdict unique. Elles sont toutes présentes dans les journaux, mais aucune commande
# ne les produisait : `verify.sh` enchaîne des étapes de 90–150 s, et `run-session.sh` rendait la main dès
# les marqueurs atteints. Ce script ajoute la seule chose qui manquait — la **durée** — et confie la note
# au graveur hors jeu (`tools/session-verdict.sh`), qui sait déjà dire pourquoi un run est rouge.
#
# Ce qu'il fait, dans l'ordre :
#   1. hôte lancé avec `-coopdebug=save` : l'autotest de sauvegarde serveur tourne **pendant** la session
#      (c'est la moitié « sauvegarde » du critère) ;
#   2. deux clients qui **chargent leur monde** (marqueur `MONDE CHARGÉ`, pas « connecté ») ;
#   3. au tiers de la session, le client 2 est **tué puis relancé** : le serveur doit diffuser son départ,
#      l'admettre à nouveau et lui recharger le monde (les moitiés « reconnexion » et « rechargement ») ;
#   4. la session est tenue `--minutes` **après la reconnexion** (30 par défaut), puis les instances sont
#      arrêtées — la durée totale du run dépasse donc largement `--minutes` (chargements + reconnexion +
#      durée tenue), et c'est la borne basse que le graveur exige ;
#   5. les journaux sont copiés dans `p4-logs/session-<horodatage>/` et notés.
#
# Ce que ce script ne peut pas faire, et qui reste à ta charge : l'**installation par le second joueur**.
# Le plan l'exige (§43.5 P4) et c'est une propriété de la session, pas du code. Ce que ce script mesure,
# c'est une session de deux instances sur une machine — utile pour la boucle, insuffisante pour clore P4.
#
# Usage :
#   tools/p4-session.sh                     # 30 min, reconnexion à 10 min
#   tools/p4-session.sh --minutes=5         # même scénario, plus court (mise au point)
#   tools/p4-session.sh --minutes=30 --reconnect-after=900 --no-build
#   tools/p4-session.sh --help
#
# Code de sortie : 0 seulement si la session **et** le verdict sont verts. Un harnais en échec n'empêche
# pas la notation : les journaux sont notés quand même, parce que c'est là qu'est l'explication.

set -o pipefail
set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SESSION="$SCRIPT_DIR/run-session.sh"
VERDICT="$SCRIPT_DIR/session-verdict.sh"

GAME_DIR_BASH="${SUBNAUTICA_GAME_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Subnautica}"
LOG_DIR="$GAME_DIR_BASH/BepInEx/logs"

MINUTES=30
RECONNECT_AFTER=""
BUILD=1
KEEP_LOGS=1

usage() {
    awk 'NR > 1 && /^set -/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"
    cat <<'OPTIONS'

Options :
  --minutes=N            durée de la session (défaut : 30)
  --reconnect-after=S    secondes de session avant de tuer le client 2 (défaut : un tiers de la durée)
  --no-build             ne reconstruit pas et ne redéploie pas les DLL
  --no-keep-logs         ne copie pas les journaux dans ./p4-logs/
OPTIONS
}

for argument in "$@"; do
    case "$argument" in
        --minutes=*) MINUTES="${argument#*=}" ;;
        --reconnect-after=*) RECONNECT_AFTER="${argument#*=}" ;;
        --no-build) BUILD=0 ;;
        --no-keep-logs) KEEP_LOGS=0 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "option inconnue : $argument (--help)" >&2; exit 2 ;;
    esac
done

case "$MINUTES" in
    ''|*[!0-9]*) echo "[échec] --minutes doit être un entier positif (reçu « $MINUTES »)" >&2; exit 2 ;;
esac
if [ "$MINUTES" -lt 1 ]; then
    echo "[échec] --minutes doit valoir au moins 1" >&2
    exit 2
fi

# Par défaut, la reconnexion tombe au **tiers** de la session : assez tôt pour que le client revenu vive
# une bonne partie de la session, assez tard pour que son départ arrive en cours de route et pas au
# chargement du monde.
if [ -z "$RECONNECT_AFTER" ]; then
    RECONNECT_AFTER=$(( MINUTES * 60 / 3 ))
fi

for script in "$SESSION" "$VERDICT"; do
    if [ ! -f "$script" ]; then
        echo "[échec] $script introuvable : ce script s'utilise à côté du harnais." >&2
        exit 2
    fi
done

if [ ! -d "$LOG_DIR" ]; then
    echo "[échec] dossier de journaux introuvable : $LOG_DIR" >&2
    echo "        (le dossier du jeu se surcharge par SUBNAUTICA_GAME_DIR=/chemin/du/jeu)" >&2
    exit 2
fi

echo "=== session P4 — ${MINUTES} min, deux clients, reconnexion à ${RECONNECT_AFTER}s ==="
echo "    hôte : -coopmode=server -coopdebug=save (autotest de sauvegarde pendant la session)"
echo "    clients : -coopmode=client, marqueur « MONDE CHARGÉ »"

args=(--clients=2 --together --timeout=420
    "--host-args=-coopmode=server -coopdebug=save"
    "--client-args=-coopmode=client"
    "--client-marker=MONDE CHARGÉ"
    "--minutes=$MINUTES"
    "--reconnect=2"
    "--reconnect-after=$RECONNECT_AFTER"
    "--reconnect-args=-coopmode=client"
    "--reconnect-marker=MONDE CHARGÉ"
    "--wait-server-marker=\\[autotest\\] sauvegarde serveur pendant un run")
if [ "$BUILD" = 0 ]; then
    args+=(--no-build)
fi

session_rc=0
"$SESSION" "${args[@]}" || session_rc=$?

# Copie **après** l'arrêt des instances : un journal encore ouvert peut être tronqué au moment de la
# copie, et un verdict rendu sur un fichier partiel est pire qu'aucun verdict.
copy_dir=""
if [ "$KEEP_LOGS" = 1 ]; then
    copy_dir="$ROOT/p4-logs/session-$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$copy_dir"
    cp -f "$LOG_DIR/coop-server.log" "$copy_dir/" 2>/dev/null || true
    for f in "$LOG_DIR"/coop-client*.log; do
        [ -f "$f" ] || continue
        cp -f "$f" "$copy_dir/$(basename "$f")"
    done
    echo "[journaux] copiés dans $copy_dir"
fi

grade_logs="$LOG_DIR"
[ -n "$copy_dir" ] && grade_logs="$copy_dir"

verdict_rc=0
"$VERDICT" --logs="$grade_logs" --minutes="$MINUTES" || verdict_rc=$?

echo
if [ "$session_rc" = 0 ] && [ "$verdict_rc" = 0 ]; then
    echo "P4 : session tenue ${MINUTES} min et verdict vert."
    echo "Rappel : l'installation par le second joueur (§43.5) n'est pas mesurée par ce script."
    exit 0
fi

[ "$session_rc" != 0 ] && echo "P4 : le harnais a échoué (code $session_rc) — journaux : $grade_logs"
[ "$verdict_rc" != 0 ] && echo "P4 : le verdict est rouge — journaux : $grade_logs"
exit 1
