#!/bin/bash
# tools/session-verdict.sh — lit les journaux d'une session réelle (P4) et rend **un seul verdict**.
#
# Pourquoi ce script existe : P4 demande « 0 exception, 0 désync > 2 m, sauvegarde + rechargement +
# reconnexion ». Ces quatre faits sont déjà écrits dans les journaux par le mod (aucun nouveau message),
# mais personne ne les agrège : une session de 30 min laisse trois fichiers de plusieurs milliers de
# lignes, et c'est un humain qui décide s'ils sont verts. Un verdict qu'on ne peut pas rejouer n'est pas
# une preuve. Ici, chaque contrôle est nommé, sa valeur mesurée est imprimée, et le code de sortie suit.
#
# Ce que le script lit, et où :
#   - exception(s) du mod : niveau `[Error  ]` ou `[Fatal  ]` émis par `[Coop Client]` / `[Coop Server]`,
#     dans **tous** les journaux de la session (`coop-server.log`, `coop-client*.log`, relances comprises).
#     Le bruit de l'environnement (Unity, XR, Steam) est **compté et nommé** à part : il ne fait pas
#     échouer le verdict, mais il n'est pas caché pour autant.
#   - désync > 2 m : la dernière ligne `[créature] état : …` de chaque client porte `dérive max=X.XXm`.
#     C'est la seule mesure de divergence **au repos** publiée en session ordinaire (segments terminés,
#     IA du client vérifiée coupée — cf. `RemoteCreatureManager`). Une ligne sans `segments>0` ne prouve
#     rien et compte comme un échec : `0.00 m` sur zéro segment est un vert vide.
#   - durée : de la première à la dernière ligne horodatée de `coop-server.log` — un run de 3 min ne doit
#     pas passer pour la session de 30 min.
#   - sauvegarde : `[autotest] sauvegarde serveur pendant un run -> verdict=True` (hôte lancé avec
#     `-coopdebug=save`).
#   - reconnexion : `PlayerLeft diffusé` côté serveur **et** un `handshake ACCEPTÉ` de plus que le nombre
#     de clients lancés.
#   - rechargement : le journal de relance (`coop-client*-b.log`) contient `handshake ACCEPTÉ` **et**
#     `MONDE CHARGÉ`.
#
# Ce que le script ne mesure pas, et qui est donc imprimé en **réserve** plutôt que tu :
#   - la divergence des **véhicules** : `écart avant lissage max` contient le mouvement légitime entre
#     deux annonces (7,04 m à 20 m/s dans un run réel) — ce n'est pas une désync, et s'en servir comme
#     seuil serait faux dans les deux sens ;
#   - la divergence de position des **avatars** : rien ne la publie (seul `correctionsRecues` du `[ping]`
#     parle des téléports refusés).
#
# Usage :
#   tools/session-verdict.sh                        # lit BepInEx/logs du dossier de jeu
#   tools/session-verdict.sh --logs=/chemin/logs    # lit un dossier de journaux copiés
#   tools/session-verdict.sh --minutes=30           # durée exigée (défaut 30, tolérance 60 s)
#   tools/session-verdict.sh --self-test            # fixtures synthétiques, sans jeu ni journaux réels
#   tools/session-verdict.sh --help
#
# Le mode `--self-test` n'est pas un ornement : c'est la seule façon de vérifier qu'un ÉCHEC est bien
# détecté (exception, dérive au-dessus du seuil, mesure vide, durée courte, sauvegarde absente) sans
# attendre une session de 30 minutes pour découvrir que le graveur laisse tout passer.

set -o pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
GAME_DIR_BASH="${SUBNAUTICA_GAME_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Subnautica}"
LOGS=""
MINUTES=30
TOLERANCE_SECONDS=60
DESYNC_METERS=2.0
SELF_TEST=0

usage() {
    # En-tête du fichier, jusqu'à la première ligne de code : la documentation est à un seul endroit.
    awk 'NR > 1 && /^set -/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"
    cat <<'OPTIONS'

Options :
  --logs=DIR        dossier des journaux à noter (défaut : BepInEx/logs du dossier de jeu)
  --minutes=N       durée minimale exigée de la session (défaut : 30)
  --tolerance=S     tolérance sur cette durée, en secondes (défaut : 60)
  --desync=M        seuil de désync des créatures, en mètres (défaut : 2.0)
  --self-test       rejoue le graveur sur des journaux synthétiques (vert et rouges)
OPTIONS
}

for argument in "$@"; do
    case "$argument" in
        --logs=*) LOGS="${argument#*=}" ;;
        --minutes=*) MINUTES="${argument#*=}" ;;
        --tolerance=*) TOLERANCE_SECONDS="${argument#*=}" ;;
        --desync=*) DESYNC_METERS="${argument#*=}" ;;
        --self-test) SELF_TEST=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "option inconnue : $argument (--help)" >&2; exit 2 ;;
    esac
done

# Horodatage `[HH:MM:SS.mmm]` en millisecondes depuis minuit.
timestamp_ms() { # $1 = "HH:MM:SS.mmm"
    local t="$1" hh mm ss ms
    hh="${t:0:2}" mm="${t:3:2}" ss="${t:6:2}" ms="${t:9:3}"
    echo $(( (10#$hh * 3600 + 10#$mm * 60 + 10#$ss) * 1000 + 10#$ms ))
}

# Durée couverte par un journal, en secondes (0 si aucune ligne horodatée).
log_span_seconds() { # $1 = fichier
    local file="$1" first last
    [ -s "$file" ] || { echo "0"; return; }
    first=$(grep -oE '^\[[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}\]' "$file" | head -1)
    last=$(grep -oE '^\[[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}\]' "$file" | tail -1)
    [ -n "$first" ] && [ -n "$last" ] || { echo "0"; return; }

    local first_ms last_ms
    first_ms=$(timestamp_ms "${first#[}") ; first_ms="${first_ms%]}"
    last_ms=$(timestamp_ms "${last#[}") ; last_ms="${last_ms%]}"

    # Minuit passe : une session qui franchit 00:00 donnerait une durée négative. Elle est alors
    # comptée de la première ligne à minuit + de minuit à la dernière, sans prétendre à la milliseconde.
    if [ "$last_ms" -ge "$first_ms" ]; then
        echo $(( (last_ms - first_ms) / 1000 ))
    else
        echo $(( (86400000 - first_ms + last_ms) / 1000 ))
    fi
}

# Compare deux nombres décimaux : vrai (rc 0) si $1 > $2.
greater_than() { awk -v a="$1" -v b="$2" 'BEGIN { exit !(a > b) }'; }

problems=0
FAILS=()

ok() { echo "  OK      $*"; }
fail() { echo "  ÉCHEC   $*"; problems=$((problems + 1)); FAILS+=("$*"); }
info() { echo "  RÉSERVE $*"; }

# Note un dossier de journaux. Imprime les contrôles ; ne termine pas le script (le mode --self-test
# appelle cette fonction plusieurs fois).
evaluate() { # $1 = dossier
    local dir="$1"
    local server="$dir/coop-server.log"
    local client_logs=() reconnect_logs=() all_logs=() file

    # `coop-client-2-b.log` est la **relance** du client 2 : elle est lue comme journal de client pour
    # les exceptions et le rechargement, mais elle ne porte pas la mesure de dérive de la session.
    for file in "$dir"/coop-client*.log; do
        [ -f "$file" ] || continue
        all_logs+=("$file")
        case "$file" in
            *-b.log) reconnect_logs+=("$file") ;;
            *) client_logs+=("$file") ;;
        esac
    done
    all_logs=("$server" "${all_logs[@]}")

    if [ ! -s "$server" ]; then
        fail "coop-server.log absent ou vide dans $dir — aucun verdict possible"
        return
    fi

    # --- 1. exceptions du mod -------------------------------------------------------------------
    local coop_errors=0 noise=0 line
    for file in "${all_logs[@]}"; do
        [ -s "$file" ] || continue
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            coop_errors=$((coop_errors + 1))
            if [ "$coop_errors" -le 3 ]; then
                fail "exception du mod dans $(basename "$file") : $(printf '%s' "$line" | cut -c1-200)"
            fi
        done < <(grep -E '\[(Error  |Fatal  )\] \[Coop (Client|Server)\]' "$file")

        # Bruit de l'environnement : compté, nommé, et **pas** attribué au mod (XR/OpenVR au démarrage,
        # par exemple, dans une machine sans casque).
        noise=$((noise + $(grep -cE '\[(Error  |Fatal  )\] \[Unity Log\]' "$file")))
    done
    if [ "$coop_errors" -eq 0 ]; then
        ok "exceptions du mod = 0 (bruit d'environnement ignoré et nommé : $noise ligne(s) [Unity Log])"
    elif [ "$coop_errors" -gt 3 ]; then
        fail "exceptions du mod = $coop_errors au total (3 premières ci-dessus)"
    fi

    # --- 2. désync des créatures ----------------------------------------------------------------
    if [ "${#client_logs[@]}" = 0 ]; then
        fail "aucun journal de client (coop-client*.log) — la désync ne peut pas être mesurée"
    fi

    for file in "${client_logs[@]}"; do
        local last drift segments
        last=$(grep -E '\[créature\] état :' "$file" | tail -1)
        if [ -z "$last" ]; then
            fail "$(basename "$file") : aucune ligne « [créature] état » — dérive non mesurée"
            continue
        fi

        drift=$(printf '%s\n' "$last" | sed -n 's/.*dérive max=\([0-9][0-9.]*\)m.*/\1/p')
        segments=$(printf '%s\n' "$last" | sed -n 's/.*segments=\([0-9][0-9]*\).*/\1/p')

        if [ -z "$drift" ]; then
            fail "$(basename "$file") : la ligne « [créature] état » ne porte pas de « dérive max= »"
            continue
        fi
        if [ -z "$segments" ] || [ "$segments" = "0" ]; then
            # Un `dérive max=0.00m` sur zéro segment est un vert vide : rien n'a été comparé.
            fail "$(basename "$file") : dérive max=${drift}m mais segments=${segments:-0} — aucune paire mesurée"
            continue
        fi

        if greater_than "$drift" "$DESYNC_METERS"; then
            fail "$(basename "$file") : dérive max=${drift}m > seuil ${DESYNC_METERS}m (segments=$segments)"
        else
            ok "$(basename "$file") : dérive max=${drift}m ≤ seuil ${DESYNC_METERS}m (segments=$segments)"
        fi
    done

    # --- 3. durée de la session -----------------------------------------------------------------
    local span required
    span=$(log_span_seconds "$server")
    required=$(( MINUTES * 60 - TOLERANCE_SECONDS ))
    [ "$required" -lt 0 ] && required=0
    if [ "$span" -ge "$required" ]; then
        ok "durée de session = $((span / 60)) min $((span % 60)) s (exigé ${MINUTES} min, tolérance ${TOLERANCE_SECONDS} s)"
    else
        fail "durée de session = $((span / 60)) min $((span % 60)) s < ${MINUTES} min exigées (tolérance ${TOLERANCE_SECONDS} s)"
    fi

    # --- 4. sauvegarde de l'hôte ----------------------------------------------------------------
    if grep -qE '\[autotest\] sauvegarde serveur pendant un run -> verdict=True' "$server"; then
        ok "sauvegarde de l'hôte pendant la session : verdict=True"
    else
        fail "sauvegarde de l'hôte : « verdict=True » absent du journal serveur (hôte lancé sans -coopdebug=save ?)"
    fi

    # --- 5. reconnexion -------------------------------------------------------------------------
    local accepted
    accepted=$(grep -cE 'handshake ACCEPTÉ' "$server")
    if ! grep -qE 'PlayerLeft diffusé' "$server"; then
        fail "reconnexion : aucun « PlayerLeft diffusé » — le départ n'a pas été constaté"
    elif [ "${accepted:-0}" -lt 3 ]; then
        fail "reconnexion : $accepted handshake(s) accepté(s) < 3 — le client n'est pas revenu"
    else
        ok "reconnexion : départ diffusé et $accepted admissions (le client revenu est admis à nouveau)"
    fi

    # --- 6. rechargement après reconnexion ------------------------------------------------------
    if [ "${#reconnect_logs[@]}" = 0 ]; then
        fail "rechargement : aucun journal de relance (coop-client*-b.log)"
    else
        local reloaded=0
        for file in "${reconnect_logs[@]}"; do
            if grep -qE 'handshake ACCEPTÉ' "$file" && grep -qE 'MONDE CHARGÉ' "$file"; then
                reloaded=$((reloaded + 1))
            else
                fail "rechargement : $(basename "$file") n'a pas « handshake ACCEPTÉ » + « MONDE CHARGÉ »"
            fi
        done
        [ "$reloaded" -gt 0 ] && ok "rechargement : $reloaded relance(s) ont rechargé le monde"
    fi

    # --- réserves, nommées ----------------------------------------------------------------------
    local vehicle_line
    vehicle_line=$(grep -E '\[véhicule\] état :' "$server" "$dir"/coop-client*.log 2>/dev/null | tail -1)
    if [ -n "$vehicle_line" ]; then
        local before_smoothing
        before_smoothing=$(printf '%s\n' "$vehicle_line" | sed -n 's/.*écart avant lissage max=\([0-9][0-9.]*\)m.*/\1/p')
        info "véhicules : divergence non mesurée — « écart avant lissage max=${before_smoothing:-?}m » contient le mouvement légitime entre deux annonces, ce n'est pas une désync"
    else
        info "véhicules : aucun état publié dans la session (rien à mesurer)"
    fi
    info "avatars : divergence de position non mesurée par ce verdict (seuls les téléports refusés sont comptés, via correctionsRecues)"
}

finish() { # imprime le verdict unique et rend le code de sortie
    echo
    if [ "$problems" -eq 0 ]; then
        echo "VERDICT P4 : VERT"
        return 0
    fi

    echo "VERDICT P4 : ÉCHEC ($problems problème(s))"
    for failure in "${FAILS[@]}"; do
        echo "  - $failure"
    done
    return 1
}

# --- mode self-test -----------------------------------------------------------------------------
# Chaque cas écrit ses journaux, appelle `evaluate`, et affiche ce qu'il a mesuré : un graveur qui
# n'échoue jamais passerait tous les cas verts, donc les cas rouges sont aussi nombreux que le vert.
self_test() {
    local root cases=0 failures=0
    root=$(mktemp -d)
    trap 'rm -rf "$root"' EXIT

    # Écrit un dossier de session. $1 = nom, $2 = "vert" ou une variante rouge.
    write_case() {
        local name="$1" variant="$2" dir="$root/$1"
        mkdir -p "$dir"

        local drift="1.21" segments="359" save="oui" reconnect="oui" span_end="10:00:30.000" noise=""
        case "$variant" in
            exception) ;;
            desync) drift="2.50" ;;
            sans-mesure) segments="0" ;;
            sans-sauvegarde) save="non" ;;
            sans-reconnexion) reconnect="non" ;;
            court) span_end="09:35:00.000" ;;
            bruit) noise="[10:00:05.000] [Error  ] [Unity Log] XR: OpenVR Error! (environnement)" ;;
        esac

        {
            echo "=== Coop server — 2026-10-21 09:29:00 (pid 100) ==="
            echo "[09:29:00.000] [Info   ] [Coop Server] [boot] serveur prêt"
            echo "[09:29:05.000] [Info   ] [Coop Server] handshake ACCEPTÉ : 'a' (id=1"
            echo "[09:29:09.000] [Info   ] [Coop Server] handshake ACCEPTÉ : 'b' (id=2"
            [ "$reconnect" = "oui" ] && echo "[09:40:00.000] [Info   ] [Coop Server] PlayerLeft diffusé pour 'b' (id=2)"
            [ "$reconnect" = "oui" ] && echo "[09:40:10.000] [Info   ] [Coop Server] handshake ACCEPTÉ : 'b' (id=3"
            [ "$save" = "oui" ] && echo "[09:41:00.000] [Info   ] [Coop Server] [autotest] sauvegarde serveur pendant un run -> verdict=True"
            [ -n "$noise" ] && echo "$noise"
            echo "[$span_end] [Info   ] [Coop Server] [net] tick"
        } > "$dir/coop-server.log"

        {
            echo "=== Coop client — 2026-10-21 09:29:02 (pid 101) ==="
            echo "[09:29:10.000] [Info   ] [Coop Client] [boot] client prêt"
            if [ "$variant" = "sans-mesure" ]; then
                echo "[$span_end] [Info   ] [Coop Client] [créature] état : adoptées=1 dérive max=0.00m sur 0.00s (aucune) — segments=0, applications directes=0, rafales écartées=0"
            else
                echo "[$span_end] [Info   ] [Coop Client] [créature] état : adoptées=1 clé inconnue=0 dérive max=${drift}m sur 0.03s ('Peeper(Clone)') — segments=${segments}, applications directes=1, rafales écartées=0"
            fi
            echo "[$span_end] [Info   ] [Coop Client] [véhicule] état : suivis=1 écart avant lissage max=7.04m ('SeaMoth(Clone)') pas par image max=0.64m"
        } > "$dir/coop-client.log"

        if [ "$variant" = "exception" ]; then
            echo "[09:50:00.000] [Error  ] [Coop Client] [net] erreur en traitant un paquet : System.InvalidOperationException: Collection was modified" \
                >> "$dir/coop-client.log"
            echo "[09:50:01.000] [Info   ] [Coop Client] [créature] état : adoptées=1 dérive max=1.21m sur 0.03s ('Peeper') — segments=359, applications directes=1, rafales écartées=0" \
                >> "$dir/coop-client.log"
        fi

        if [ "$variant" = "desync" ]; then
            # La mesure fautive doit être la **dernière** : c'est elle que le graveur lit.
            echo "[09:59:00.000] [Info   ] [Coop Client] [créature] état : adoptées=1 dérive max=2.50m sur 0.03s ('Peeper') — segments=400, applications directes=1, rafales écartées=0" \
                >> "$dir/coop-client.log"
        fi

        if [ "$reconnect" = "oui" ]; then
            {
                echo "=== Coop client — 2026-10-21 09:40:20 (pid 202) ==="
                echo "[09:40:20.000] [Info   ] [Coop Client] handshake ACCEPTÉ"
                echo "[09:40:35.000] [Info   ] [Coop Client] [monde] *** MONDE CHARGÉ : scene 'EscapePod' ***"
                echo "[$span_end] [Info   ] [Coop Client] [ping] id=3"
            } > "$dir/coop-client-2-b.log"
        fi
    }

    local expectations=(
        "vert:0:VERDICT P4 : VERT"
        "exception:1:exception du mod"
        "desync:1:seuil 2.0m"
        "sans-mesure:1:aucune paire mesurée"
        "sans-sauvegarde:1:sauvegarde de l'hôte"
        "sans-reconnexion:1:reconnexion"
        "court:1:durée de session"
        "bruit:0:VERDICT P4 : VERT"
    )

    local entry name expected_rc expected_text output rc
    for entry in "${expectations[@]}"; do
        name="${entry%%:*}"
        expected_rc="${entry#*:}"; expected_rc="${expected_rc%%:*}"
        expected_text="${entry#*:*:}"
        cases=$((cases + 1))

        write_case "$name" "$name"
        # `finish` est appelé **dans** la substitution : c'est lui qui rend le code de sortie du cas,
        # et il lit les globals posés par `evaluate` dans le même sous-shell.
        output=$( problems=0; FAILS=(); evaluate "$root/$name"; finish )
        rc=$?

        # Le bruit d'environnement du cas « bruit » doit être **compté** : sinon on ne sait pas s'il a
        # été ignoré ou jamais vu.
        local extra_ok=1
        if [ "$name" = "bruit" ] && ! printf '%s\n' "$output" | grep -q 'bruit d.environnement ignoré et nommé : 1'; then
            extra_ok=0
        fi

        if [ "$rc" = "$expected_rc" ] && printf '%s\n' "$output" | grep -qE "$expected_text" && [ "$extra_ok" = 1 ]; then
            echo "  OK      cas « $name » : rc=$rc, « $expected_text » présent"
        else
            failures=$((failures + 1))
            echo "  ÉCHEC   cas « $name » : rc=$rc (attendu $expected_rc), motif « $expected_text » $(printf '%s\n' "$output" | grep -qE "$expected_text" && echo présent || echo ABSENT)"
            printf '%s\n' "$output" | sed 's/^/          /' | tail -20
        fi
    done

    echo
    if [ "$failures" -eq 0 ]; then
        echo "SELFTEST: SUCCES ($cases cas, $failures échec)"
        return 0
    fi

    echo "SELFTEST: ECHEC ($failures cas sur $cases)"
    return 1
}

if [ "$SELF_TEST" = "1" ]; then
    self_test
    exit $?
fi

if [ -z "$LOGS" ]; then
    LOGS="$GAME_DIR_BASH/BepInEx/logs"
fi

if [ ! -d "$LOGS" ]; then
    echo "[échec] dossier de journaux introuvable : $LOGS" >&2
    echo "        (le dossier du jeu se surcharge par SUBNAUTICA_GAME_DIR=/chemin/du/jeu)" >&2
    exit 2
fi

echo "=== verdict P4 — $LOGS (session exigée : ${MINUTES} min, désync ≤ ${DESYNC_METERS} m) ==="
problems=0
FAILS=()
evaluate "$LOGS"
finish
