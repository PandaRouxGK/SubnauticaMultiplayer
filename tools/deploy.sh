#!/bin/bash
# tools/deploy.sh — construit les deux plugins et les installe dans le dossier du jeu, avec un
# manifeste qui dit **ce qui a été installé** et avec quelles empreintes.
#
# Pourquoi ce script : jusqu'ici, déployer voulait dire copier des DLL à la main dans
# `BepInEx/plugins/Coop`, sans trace de la version installée. Deux instances qui ne portent pas le
# même binaire ne se parlent pas (le handshake refuse une version différente), et rien ne permettait
# de le constater avant de lancer le jeu.
#
# Ce qu'il ne fait pas : il ne touche jamais `SNAppData/` (les sauvegardes), ne lance pas le jeu, et
# n'écrit rien hors de `BepInEx/plugins/Coop/`.
#
# Usage :
#   tools/deploy.sh                          # construit et installe
#   tools/deploy.sh --verify                 # vérifie l'installation (hashes + version de protocole)
#   tools/deploy.sh --no-build               # installe les binaires déjà construits
#   tools/deploy.sh --game-dir="D:/Jeux/Subnautica"
#   tools/deploy.sh --help
#
# Dossier du jeu : `COOP_GAME_DIR`, sinon `SUBNAUTICA_GAME_DIR` (même variable que les harnais),
# sinon l'emplacement Steam par défaut.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

GAME_DIR="${COOP_GAME_DIR:-${SUBNAUTICA_GAME_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Subnautica}}"
MODE="install"
BUILD=1

FILES=(Coop.Client.dll Coop.Server.dll Coop.Model.dll LiteNetLib.dll)
TEMPLATE="$ROOT/tools/mod.manifest.json"
PROTOCOL_SOURCE="$ROOT/src/Coop.Model/Protocol.cs"

usage() {
    sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'
}

# Git Bash comprend `/c/...` mais pas `C:\...` (les backslashes sont des échappements).
to_bash_path() {
    case "$1" in
        [A-Za-z]:[\\/]*)
            local drive
            drive="$(printf '%s' "${1:0:1}" | tr 'A-Z' 'a-z')"
            local rest="${1:2}"
            rest="${rest//\\//}"
            printf '/%s%s\n' "$drive" "$rest"
            ;;
        *)
            printf '%s\n' "$1"
            ;;
    esac
}

for argument in "$@"; do
    case "$argument" in
        --game-dir=*) GAME_DIR="${argument#*=}" ;;
        --verify) MODE="verify" ;;
        --no-build) BUILD=0 ;;
        --help|-h) usage; exit 0 ;;
        *)
            echo "[échec] argument inconnu : $argument" >&2
            usage >&2
            exit 2
            ;;
    esac
done

GAME_DIR="$(to_bash_path "$GAME_DIR")"
PLUGIN_DIR="$GAME_DIR/BepInEx/plugins/Coop"
MANIFEST="$PLUGIN_DIR/mod.manifest.json"

sha256_of() {
    local file="$1"
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$file" | awk '{print $1}'
    else
        echo "[échec] ni sha256sum ni shasum n'est disponible : impossible de sceller le manifeste." >&2
        exit 1
    fi
}

protocol_version() {
    local version
    version="$(sed -n 's/^[[:space:]]*public const int Version = \([0-9][0-9]*\);.*/\1/p' "$PROTOCOL_SOURCE" | head -1)"
    if [ -z "$version" ]; then
        echo "[échec] version de protocole introuvable dans $PROTOCOL_SOURCE" >&2
        exit 1
    fi
    printf '%s\n' "$version"
}

require_manifest() {
    if [ ! -f "$MANIFEST" ]; then
        echo "[échec] manifeste absent : $MANIFEST — lancez d'abord tools/deploy.sh" >&2
        exit 1
    fi
}

verify_install() {
    local protocol version_in_manifest failures=0
    protocol="$(protocol_version)"

    require_manifest

    version_in_manifest="$(sed -n 's/.*"protocolVersion":[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$MANIFEST" | head -1)"
    if [ -z "$version_in_manifest" ]; then
        echo "[échec] le manifeste ne porte pas de protocolVersion lisible." >&2
        exit 1
    fi

    if [ "$version_in_manifest" != "$protocol" ]; then
        echo "[échec] version de protocole : source=$protocol, manifeste=$version_in_manifest" >&2
        failures=$((failures + 1))
    else
        echo "  protocole v$protocol : conforme au manifeste"
    fi

    for file in "${FILES[@]}"; do
        local path="$PLUGIN_DIR/$file"
        if [ ! -f "$path" ]; then
            echo "  [échec] $file absent de $PLUGIN_DIR" >&2
            failures=$((failures + 1))
            continue
        fi

        local expected actual expected_bytes actual_bytes
        expected="$(grep -F "\"name\": \"$file\"" "$MANIFEST" | sed -n 's/.*"sha256": "\([0-9a-f]\{64\}\)".*/\1/p' | head -1)"
        expected_bytes="$(grep -F "\"name\": \"$file\"" "$MANIFEST" | sed -n 's/.*"bytes": \([0-9][0-9]*\).*/\1/p' | head -1)"
        actual="$(sha256_of "$path")"
        actual_bytes="$(wc -c < "$path" | tr -d ' ')"

        if [ -z "$expected" ]; then
            echo "  [échec] $file n'a pas d'empreinte dans le manifeste" >&2
            failures=$((failures + 1))
        elif [ "$expected" != "$actual" ]; then
            echo "  [échec] $file : sha256 installé=$actual, manifeste=$expected" >&2
            failures=$((failures + 1))
        elif [ "$expected_bytes" != "$actual_bytes" ]; then
            echo "  [échec] $file : taille installée=$actual_bytes, manifeste=$expected_bytes" >&2
            failures=$((failures + 1))
        else
            echo "  $file : ${actual:0:16}… ($actual_bytes o)"
        fi
    done

    if [ "$failures" -ne 0 ]; then
        echo "[échec] installation NON conforme ($failures problème(s)) — relancez tools/deploy.sh" >&2
        exit 1
    fi

    echo "[ok] installation conforme : $PLUGIN_DIR (protocole v$protocol)"
}

install_plugins() {
    if [ ! -f "$GAME_DIR/Subnautica_Data/Managed/Assembly-CSharp.dll" ]; then
        echo "[échec] dossier du jeu invalide : $GAME_DIR" >&2
        echo "        attendu : <jeu>/Subnautica_Data/Managed/Assembly-CSharp.dll" >&2
        echo "        passez --game-dir=... ou COOP_GAME_DIR=..." >&2
        exit 1
    fi

    local protocol template_protocol
    protocol="$(protocol_version)"

    if [ -f "$TEMPLATE" ]; then
        template_protocol="$(sed -n 's/.*"protocolVersion":[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$TEMPLATE" | head -1)"
        if [ -n "$template_protocol" ] && [ "$template_protocol" != "$protocol" ]; then
            echo "[échec] le manifeste versionné épingle le protocole v$template_protocol, le code dit v$protocol." >&2
            echo "        mettez à jour tools/mod.manifest.json dans le même changement que Protocol.cs." >&2
            exit 1
        fi
    fi

    if [ "$BUILD" -eq 1 ]; then
        echo "=== construction (Release) ==="
        dotnet build "$ROOT/src/Coop.Client/Coop.Client.csproj" -c Release --nologo -v q
        dotnet build "$ROOT/src/Coop.Server/Coop.Server.csproj" -c Release --nologo -v q
    fi

    local client_bin="$ROOT/src/Coop.Client/bin/Release"
    local server_bin="$ROOT/src/Coop.Server/bin/Release"

    mkdir -p "$PLUGIN_DIR"

    for file in "${FILES[@]}"; do
        local source=""
        case "$file" in
            Coop.Server.dll) source="$server_bin/$file" ;;
            *) source="$client_bin/$file" ;;
        esac

        if [ ! -f "$source" ]; then
            echo "[échec] binaire introuvable : $source" >&2
            echo "        construisez d'abord (sans --no-build) : dotnet build -c Release" >&2
            exit 1
        fi

        cp -f "$source" "$PLUGIN_DIR/$file"
        echo "  copié : $file -> $PLUGIN_DIR"
    done

    local commit
    commit="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || true)"
    if [ -z "$commit" ]; then
        commit="inconnu"
    fi

    {
        printf '{\n'
        printf '  "schemaVersion": 1,\n'
        printf '  "protocolVersion": %s,\n' "$protocol"
        printf '  "builtAtUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf '  "gitCommit": "%s",\n' "$commit"
        printf '  "pluginDir": "BepInEx/plugins/Coop",\n'
        printf '  "files": [\n'
        local first=1
        for file in "${FILES[@]}"; do
            if [ "$first" -eq 0 ]; then
                printf ',\n'
            fi
            first=0
            printf '    { "name": "%s", "bytes": %s, "sha256": "%s" }' \
                "$file" \
                "$(wc -c < "$PLUGIN_DIR/$file" | tr -d ' ')" \
                "$(sha256_of "$PLUGIN_DIR/$file")"
        done
        printf '\n  ]\n}\n'
    } > "$MANIFEST"

    echo "=== installé ==="
    echo "  dossier   : $PLUGIN_DIR"
    echo "  protocole : v$protocol"
    echo "  commit    : $commit"
    echo "  manifeste : $MANIFEST"
    echo
    echo "Vérifier ensuite : tools/deploy.sh --verify"
}

case "$MODE" in
    verify) verify_install ;;
    install) install_plugins ;;
esac
