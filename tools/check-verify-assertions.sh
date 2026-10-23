#!/bin/bash
# tools/check-verify-assertions.sh — vérifie, **sans lancer le jeu**, que les assertions des étapes P, Q, N,
# B et R de `tools/verify.sh` ne sont ni trop laxistes ni impossibles à satisfaire.
#
# Pourquoi ce script existe : les étapes P (position) et Q (rejeu d'un retrait) n'ont pas encore tourné.
# Leurs assertions sont des expressions régulières recopiées à la main depuis les formats de journal du
# code ; une faute de frappe y produirait soit un faux vert (motif qui ne peut pas échouer), soit un faux
# rouge (motif qui ne peut pas s'appliquer). Ici, les **mêmes fonctions** `expect` / `assert_p` / `assert_q`
# sont extraites de `verify.sh` et exécutées sur des journaux synthétiques aux formats du code :
#
#   1. les lignes attendues passent (aucune assertion impossible) ;
#   2. en retirant des lignes exigées, les assertions échouent **et nomment** ce qui manque (discrimination).
#
# Ce que ce script ne prouve pas : que le jeu produit ces lignes. Il valide le contrat des assertions, pas
# la mesure. C'est écrit dans `PLAN.md` §45.
#
# Usage : tools/check-verify-assertions.sh
set -o pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
VERIFY="$SCRIPT_DIR/verify.sh"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

FAILURES=()
source <(sed -n '/^expect()/,/^}/p;/^expect_missing()/,/^}/p;/^expect_any()/,/^}/p;/^assert_p()/,/^}/p;/^assert_q()/,/^}/p;/^assert_n()/,/^}/p;/^assert_b()/,/^}/p;/^assert_r()/,/^}/p;/^assert_ad()/,/^}/p;/^assert_ae()/,/^}/p' "$VERIFY")

for function in expect expect_missing expect_any assert_p assert_q assert_n assert_b assert_r assert_ad assert_ae; do
    if ! declare -F "$function" >/dev/null; then
        echo "ECHEC : la fonction $function n'a pas pu être extraite de $VERIFY" >&2
        echo "        (le découpage sed dépend des lignes « function » et « } » en colonne 0)" >&2
        exit 1
    fi
done

mkdir -p "$WORK_DIR/P" "$WORK_DIR/Q" "$WORK_DIR/N" "$WORK_DIR/B" "$WORK_DIR/R" "$WORK_DIR/AD" "$WORK_DIR/AE"

# 0. Toutes les étapes déclarées doivent être **atteignables**. Le 2026-10-19, la validation de `--only`
#    s'arrêtait à `O` alors que P et Q existaient : les deux étapes répondaient « étape inconnue », donc
#    elles n'auraient jamais pu tourner. Ce contrôle compare la liste des `if wanted X` à la classe de
#    caractères qui les autorise.
declared=$(grep -oE '^if wanted [A-Z]+ ' "$VERIFY" | awk '{print $3}' | sort -u | tr -d '\n')
declared_count=$(grep -cE '^if wanted [A-Z]+ ' "$VERIFY")
allowed=$(grep -oE '\*\[\![A-Z,]+,?\]\*\)' "$VERIFY" | head -1 | sed -e 's/^.*\[!//' -e 's/,\].*$//')
if [ -z "$declared" ] || [ -z "$allowed" ]; then
    echo "ECHEC : impossible de lire les étapes déclarées ('$declared') ou la classe autorisée ('$allowed')" >&2
    exit 1
fi
missing=""
for (( i = 0; i < ${#declared}; i++ )); do
    letter="${declared:$i:1}"
    case "$allowed" in
        *"$letter"*) ;;
        *) missing="$missing$letter" ;;
    esac
done
if [ -n "$missing" ]; then
    echo "ECHEC : étapes déclarées mais refusées par --only : $missing (autorisées : $allowed)" >&2
    exit 1
fi
echo "OK   les ${declared_count} étapes déclarées ($declared) sont toutes acceptées par --only"

# 0 bis. Le **défaut** doit lancer les mêmes étapes. Le 2026-10-20, `--only` acceptait R mais la liste
#    par défaut (`${ONLY:-…}`) s'arrêtait à Q : un run sans `--only` — le cas normal — sautait R en
#    silence, alors que le script annonçait « les dix-huit étapes ». Ce contrôle-là l'aurait vu.
defaults=$(grep -oE 'ONLY:-[A-Z]+(,[A-Z]+)*' "$VERIFY" | sed 's/^ONLY:-//' | tr ',' '\n' | sort -u | tr -d '\n')
if [ -z "$defaults" ]; then
    echo "ECHEC : aucune liste par défaut (\${ONLY:-…}) trouvée dans $VERIFY" >&2
    exit 1
fi
while read -r defaultSteps; do
    [ -z "$defaultSteps" ] && continue
    if [ "$defaultSteps" != "$declared" ]; then
        echo "ECHEC : le défaut de verify.sh ($defaultSteps) ne lance pas toutes les étapes déclarées ($declared)" >&2
        exit 1
    fi
done <<< "$defaults"
echo "OK   le défaut de verify.sh lance les ${declared_count} étapes déclarées"

write_fixtures() {
    cat > "$WORK_DIR/P/coop-client.log" <<'LOG'
[net] handshake envoyé (protocole v13, jeu=2019.4.36f1, save=slot='slot0000' session='aae68629' changeSet=3 mode=Survival hash=0x1234)
[sonde] position : verdict=True — nage normale : 0 correction sur 6.0s (seuil 0), téléport de 100 m : correction reçue=True, appliquée=True (2 appliquée(s)) — seuils : 0 en nage, ≥1 appliquée après téléport
LOG
    cat > "$WORK_DIR/P/coop-server.log" <<'LOG'
[net] empreinte de sauvegarde acceptée : client=slot='slot0000' session='aae68629' changeSet=3 mode=Survival hash=0x1234 · serveur=slot='slot0000' session='aae68629' changeSet=2 mode=Survival hash=0x1234
[net] position REFUSÉE de 'killi'#1 : déplacement de 100.00 m en 0.20 s (500.0 m/s > 15.0 m/s) — correction renvoyée (refus=1)
[net] position RESYNCHRONISÉE de 'killi'#1 après 3 refus consécutifs : déplacement de 100.00 m en 0.20 s — la position reçue est adoptée.
[net] position : 'killi'#1 200 reçue(s), 3 refusée(s) (1.5%), 1 resynchronisée(s)
LOG
    cat > "$WORK_DIR/Q/coop-client.log" <<'LOG'
[sonde] disparition : verdict=True — véhicules retirés=1 objets retirés=0 absents localement=0 en attente=0 ; cette instance avait l'objet et l'a détruit, dernière clé retirée=0x7ce3b6a1e8e7e312 encore présente=False, entités créées ici=1 (un fantôme après le retrait serait un échec)
LOG
    cat > "$WORK_DIR/Q/coop-server.log" <<'LOG'
[sonde] disparition : véhicule 'SeaMoth(Clone)' (clé=0x7ce3b6a1e8e7e312) détruit après 40s (pos=(-118.4, -13.4, -52.9)) — le serveur doit l'annoncer aux clients.
[net] disparition : 1 entité(s) disparue(s) rejouée(s) pour 'killi'#2 (arrivé après leur retrait).
LOG
    cat > "$WORK_DIR/N/coop-server.log" <<'LOG'
[sonde] apparition : commande console « spawn Seamoth » (tentative 1).
[sonde] apparition : véhicule fabriqué — 'SeaMoth(Clone)' (clé=0x0518aa8f25e17d2f, identifiant) après 1 tentative(s).
[net] apparition : clé=0x0518aa8f25e17d2f 'SeaMoth(Clone)' (Seamoth) — diffusée à 1 client(s), pos=(-118.4, -13.4, -52.9), identité '0a472465-f9e8-4780-8b3e-a308429250ce'
[net] apparition : 1 entité(s) fabriquée(s) rejouée(s) pour 'killi'#2 (arrivé après leur création)
LOG
    cat > "$WORK_DIR/N/coop-client.log" <<'LOG'
[net] apparition reçue : clé=0x0518aa8f25e17d2f 'Seamoth' identité '0a472465-f9e8-4780-8b3e-a308429250ce'
[spawn] entité créée : clé=0x0518aa8f25e17d2f 'SeaMoth(Clone)' (Seamoth) pos=(-118.4, -13.4, -52.9) — identifiant du serveur '0a472465-f9e8-4780-8b3e-a308429250ce' écrit (oui), clé locale 0x0518aa8f25e17d2f, créées=1
[sonde] apparition : verdict=True — entités créées=1, écartées (déjà présentes)=0, échecs=0, clés divergentes=0 ; dernière clé=0x0518aa8f25e17d2f 'SeaMoth(Clone)' — objet local retrouvé par sa clé=True ; définition vérifiée=True (hash=0x3da1f4bf782becdf), définition falsifiée refusée=True, définitions refusées=0
LOG
    cat > "$WORK_DIR/B/coop-client.log" <<'LOG'
[Coop Présence] [sonde] présence : 1 autre(s) client(s) connu(s) (2 joueur(s) distant(s) au total, hôte compris) — la pièce ajoutée sera un changement reçu par l'autre client, pas un état appris à l'admission.
[Coop Client] [presence] avatar instancié pour 'killi' (id=1) root=(-120.74, -13.93, -64.54) -> body=(-120.74, -13.93, -64.54) (24 renderers sur Default, animator=player_view, tenue=3 pièce(s), 8 nœud(s) actif(s) [diveSuit_body_geo, diveSuit_hands_geo, generalSuit, flippers_basic_geo, scubaSuit, scuba_head, scuba_vest, scuba_breatherTubes])
[Coop Client] [presence] équipement appliqué sur l'avatar de 'killi' (id=2) : 1 pièce(s) [Fins] -> 5 nœud(s) actif(s) [diveSuit_head_geo, diveSuit_body_geo, diveSuit_hands_geo, generalSuit, flippers_basic_geo]
[Coop Présence] [sonde] présence : PlayerId falsifié — 'killi' a annoncé [Tank, Fins] sous l'id 1 (le mien) ; le relais me l'a diffusé sous l'id 2 (attendu : 2, l'id réel de l'émetteur ; ma propre falsification partie vers lui a été restaurée : True) -> verdict=True
[Coop Présence] [sonde] présence : nage puis arrêt vus par 'killi' (id=2) — vitesse max 2.60 m/s (min 1.0), déplacement 11.18 m (min 1.0), sous l'eau=True, avancées vues=52, écart à l'arrêt max=0.00 m (critère : < 0.5 m) -> verdict=True
[Coop Présence] [sonde] présence : outil réellement en main — 'Flashlight' placé dans l'emplacement rapide 0 puis pris en main par QuickSlots (outil lu : Flashlight, 1 essai(s)) -> verdict=True
[Coop Présence] [sonde] présence : objet tenu émis par la boucle réseau — annonces 1 -> 2 en 15s (outil encore en main : True, reprises : 0) -> verdict=True
[Coop Présence] [sonde] présence : verdict=True
LOG
    cat > "$WORK_DIR/B/coop-client-2.log" <<'LOG'
[Coop Présence] [sonde] présence : couleur rejouée — indice 1 appliqué à l'avatar de 'HoteServeur' (id=9999) par la sonde, teinte avant=(255, 82, 82), après=(255, 152, 0), écart=82/255 (seuil 8) -> verdict=True
[Coop Présence] [sonde] présence : verdict=True
LOG
    cat > "$WORK_DIR/N/coop-client-2.log" <<'LOG'
[net] apparition reçue : clé=0x0518aa8f25e17d2f 'Seamoth' identité '0a472465-f9e8-4780-8b3e-a308429250ce'
[spawn] entité créée : clé=0x0518aa8f25e17d2f 'SeaMoth(Clone)' (Seamoth) pos=(-118.4, -13.4, -52.9) — identifiant du serveur '0a472465-f9e8-4780-8b3e-a308429250ce' écrit (oui), clé locale 0x0518aa8f25e17d2f, créées=1
[sonde] apparition : verdict=True — entités créées=1, écartées (déjà présentes)=0, échecs=0, clés divergentes=0 ; dernière clé=0x0518aa8f25e17d2f 'SeaMoth(Clone)' — objet local retrouvé par sa clé=True ; définition vérifiée=True (hash=0x3da1f4bf782becdf), définition falsifiée refusée=True, définitions refusées=0
LOG
    # Étape R : refus P0 en jeu. Le client 1 est admis (contrôle), le client 2 a chargé `slot0001` et
    # reçoit le refus nommé. Les deux motifs sont recopiés **du code** (`SaveCompatibility.Explain`).
    cat > "$WORK_DIR/R/coop-client.log" <<'LOG'
[monde] sauvegarde demandée : 'slot0000'
[net] connexion à 127.0.0.1:7777...
[net] handshake envoyé (protocole v13, jeu=2019.4.36f1, save=slot='slot0000' session='aae68629' changeSet=3 mode=Survival hash=0x1234)
[net] handshake ACCEPTÉ (id=1, tick serveur=123456) — début des pings et positions
LOG
    cat > "$WORK_DIR/R/coop-client-2.log" <<'LOG'
[monde] sauvegarde demandée : 'slot0001'
[monde]   slot='slot0001' session='aae68629' changeSet=3 mode=Survival
[monde] sauvegarde demandée 'slot0001' trouvée
[net] handshake envoyé (protocole v13, jeu=2019.4.36f1, save=slot='slot0001' session='aae68629' changeSet=3 mode=Survival hash=0x1234)
[net] handshake REFUSÉ par le serveur : sauvegarde différente (client='slot0001', serveur='slot0000')
LOG
    cat > "$WORK_DIR/R/coop-server.log" <<'LOG'
[net] empreinte de sauvegarde acceptée : client=slot='slot0000' session='aae68629' changeSet=3 mode=Survival hash=0x1234 · serveur=slot='slot0000' session='aae68629' changeSet=3 mode=Survival hash=0x1234
[net] handshake REFUSÉ pour 'killi' : sauvegarde différente (client='slot0001', serveur='slot0000')
LOG
    # Étapes AD (Créatif) et AE (Survie) : le craft payé, la cadence nommée, et le régime du jeu. Les lignes
    # sont recopiées du code (`CraftGuard`, `CraftProbeBehaviour`, `HandleCraftRequest`) et de runs verts.
    cat > "$WORK_DIR/AD/coop-client.log" <<'LOG'
[Coop Client] [boot] GameObject 'Coop.Client.Bootstrap' créé (DontDestroyOnLoad, garde=True, pauseCliente=désactivée, ramassage=arbitré, craft=arbitré).
[Coop Client] [craft] interception : 'Accumulator' bloqué avant consommation, en attente du serveur (requête 1, bloqués=1).
[Coop Client] [craft] accord du serveur : 'Accumulator' — reprise du craft local sur le fabricator (requête 1, reprises=1).
[Coop Client] [inventaire] adoption envoyée : 3 objet(s) [Copper ×1, FireExtinguisher ×1, Titanium ×1] — le serveur les remplace ensuite par ses décisions.
[Coop Craft] [sonde] craft : recette='Accumulator' coût=[Titanium ×1] produit='Accumulator' mode='Créatif', bloqué=True/True, matériaux intacts avant accord=True, accordé=True, rejoué=True, produit apparu=True (0 -> 1), matériaux consommés=True, coût retiré par la réconciliation=1, attente de l'accord=26.6 ms (min=19.4, max=26.6), refus nommé=True (MissingMaterial), rien consommé après refus=True, échecs de réconciliation=0 — verdict=True
[Coop Craft] [sonde] cadence : demandes=12 refus de cadence=4 (rafale 8, 1 par 500 ms) — verdict=True
[Coop Craft] [sonde] reconnexion : adresse='127.0.0.1:7777', demande retenue=1, encore en attente=0, latence avant=26.6 ms puis -1.0, accepté pendant la coupure=False, réadmis=True, id 1 -> 2 — verdict=True
LOG
    cat > "$WORK_DIR/AD/coop-server.log" <<'LOG'
[Coop Server] [net] inventaire de 'killi'#1 : révision 2, 3 objet(s) (3 unité(s)) — adoption
[Coop Server] [net] craft : 'killi'#1 -> 'Accumulator' ACCORDÉ (coût : Titanium ×1) — révision 2 -> 3.
[Coop Server] [net] craft : 'killi'#1 -> 'Accumulator' REFUSÉ (MissingMaterial : Titanium ×1 (registre 0)) — révision 3 inchangée.
[Coop Server] [net] craft : 'killi'#1 -> 'Accumulator' REFUSÉ (RateLimited : cadence : 499 ms avant le prochain jeton (rafale 8, 1 par 500 ms)) — révision 3 inchangée.
LOG
    cat > "$WORK_DIR/AE/coop-client.log" <<'LOG'
[Coop Client] [boot] GameObject 'Coop.Client.Bootstrap' créé (DontDestroyOnLoad, garde=True, pauseCliente=désactivée, ramassage=arbitré, craft=arbitré).
[Coop Client] [craft] interception : 'Accumulator' bloqué avant consommation, en attente du serveur (requête 1, bloqués=1).
[Coop Client] [craft] accord du serveur : 'Accumulator' — reprise du craft local sur le fabricator (requête 1, reprises=1).
[Coop Client] [craft] matériaux protégés 10 s : Titanium (le craft approuvé les consomme lui-même).
[Coop Client] [craft] protection levée : Titanium (consommation locale faite ou refus : rien ne reste à protéger) — la réconciliation peut de nouveau agir sur ce matériau.
[Coop Client] [craft] sonde : protection forcée de Copper.
[Coop Client] [inventaire] réconciliation : retrait de 'Copper' ×1 reporté — un craft approuvé consomme ce matériau lui-même.
[Coop Client] [craft] sonde : protection levée de Copper — reprise des reports.
[Coop Client] [inventaire] réconciliation : reprise de 1 retrait(s) reporté(s) [Copper] — un craft vient de rendre la main.
[Coop Client] [inventaire] réconciliation : 1 unité(s) retirée(s) de Inventory.main, échecs=0 (total retiré=2).
[Coop Craft] [sonde] report : matériau='Copper' local=1, reportés=1, retiré à la reprise=1 (1 -> 0), attentes restantes=0, instantané restauré=True (5 entrée(s)) — verdict=True
[Coop Client] [craft] 1 demande(s) en attente oubliée(s) à la fermeture de session.
[Coop Craft] [sonde] reconnexion : adresse='127.0.0.1:7777', demande retenue=1, encore en attente=0, latence avant=27.4 ms puis -1.0, accepté pendant la coupure=False, réadmis=True, id 1 -> 2 — verdict=True
[Coop Craft] [sonde] craft : recette='Accumulator' coût=[Titanium ×1] produit='Accumulator' mode='Survie', bloqué=True/True, matériaux intacts avant accord=True, accordé=True, rejoué=True, produit apparu=True (0 -> 1), matériaux consommés=True, coût retiré par la réconciliation=0, attente de l'accord=27.4 ms (min=26.0, max=27.4), refus nommé=True (MissingMaterial), rien consommé après refus=True, échecs de réconciliation=0 — verdict=True
[Coop Craft] [sonde] cadence : demandes=12 refus de cadence=4 (rafale 8, 1 par 500 ms) — verdict=True
LOG
    cat > "$WORK_DIR/AE/coop-server.log" <<'LOG'
[Coop Server] [net] craft : 'killi'#1 -> 'Accumulator' ACCORDÉ (coût : Titanium ×1) — révision 2 -> 3.
[Coop Server] [net] craft : 'killi'#1 -> 'Accumulator' REFUSÉ (RateLimited : cadence : 499 ms avant le prochain jeton (rafale 8, 1 par 500 ms)) — révision 3 inchangée.
LOG
    cat > "$WORK_DIR/Q/coop-client-2.log" <<'LOG'
[net] disparition reçue : clé=0x7ce3b6a1e8e7e312 (véhicule) — motif « véhicule détruit chez le serveur »
[disparition] retrait de 0x7ce3b6a1e8e7e312 mis en attente — le monde n'est pas encore chargé (appliqué dès qu'il le sera, sinon la sauvegarde ferait revenir l'objet).
[disparition] véhicule 0x7ce3b6a1e8e7e312 absent localement — rien à retirer (motif serveur « véhicule détruit chez le serveur »). Ce client ne l'avait pas.
[sonde] disparition : verdict=True — véhicules retirés=0 objets retirés=0 absents localement=1 en attente=0 ; cette instance ne l'a jamais eu, et ne l'a pas recréé, dernière clé retirée=0x7ce3b6a1e8e7e312 encore présente=False, entités créées ici=0 (un fantôme après le retrait serait un échec)
LOG
}

# 1. Nominal : toutes les assertions doivent accepter ces lignes.
write_fixtures
FAILURES=()
assert_p
assert_q
assert_n
assert_b
assert_r
assert_ad
assert_ae
if [ "${#FAILURES[@]}" -ne 0 ]; then
    echo "ECHEC : une assertion ne peut pas accepter le format documenté :" >&2
    printf '  - %s\n' "${FAILURES[@]}" >&2
    exit 1
fi
echo "OK   les assertions P, Q, N, B, R, AD et AE acceptent les journaux au format du code"

# 2. Discrimination : retirer des lignes exigées doit produire des échecs **nommés**. Une assertion qui
#    resterait verte ici ne prouverait rien en jeu.
grep -v 'position REFUSÉE' "$WORK_DIR/P/coop-server.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/P/coop-server.log"
grep -v 'correction reçue=True' "$WORK_DIR/P/coop-client.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/P/coop-client.log"
grep -v 'entité(s) disparue(s) rejouée(s)' "$WORK_DIR/Q/coop-server.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/Q/coop-server.log"
grep -v 'absent localement' "$WORK_DIR/Q/coop-client-2.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/Q/coop-client-2.log"
grep -v 'définition vérifiée=True' "$WORK_DIR/N/coop-client.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/N/coop-client.log"
grep -v 'autre(s) client(s) connu(s)' "$WORK_DIR/B/coop-client.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/B/coop-client.log"
grep -v 'couleur rejouée' "$WORK_DIR/B/coop-client-2.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/B/coop-client-2.log"
grep -v 'handshake REFUSÉ pour' "$WORK_DIR/R/coop-server.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/R/coop-server.log"
grep -v 'handshake REFUSÉ par le serveur' "$WORK_DIR/R/coop-client-2.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/R/coop-client-2.log"
grep -v "\[craft\] interception" "$WORK_DIR/AD/coop-client.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/AD/coop-client.log"
grep -v 'RateLimited' "$WORK_DIR/AD/coop-server.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/AD/coop-server.log"
grep -v "mode='Survie'" "$WORK_DIR/AE/coop-client.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/AE/coop-client.log"
grep -v 'RateLimited' "$WORK_DIR/AE/coop-server.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/AE/coop-server.log"
grep -v '\[sonde\] reconnexion' "$WORK_DIR/AE/coop-client.log" > "$WORK_DIR/tmp" && mv "$WORK_DIR/tmp" "$WORK_DIR/AE/coop-client.log"

FAILURES=()
assert_p
assert_q
assert_n
assert_b
assert_r
assert_ad
assert_ae
if [ "${#FAILURES[@]}" -lt 14 ]; then
    echo "ECHEC : seulement ${#FAILURES[@]} échec(s) après retrait de quatorze lignes exigées —" >&2
    echo "        au moins une assertion ne dépend pas de la ligne qu'elle prétend vérifier." >&2
    exit 1
fi

expected=(
    'le serveur refuse le saut et renvoie une correction'
    'téléport de 100 m'
    'le serveur rejoue le retrait au client arrivé après coup'
    'absent localement'
    'contrat de contenu vérifie la définition'
    "a attendu un autre client, pas l'avatar de l'hôte"
    'la couleur est appliquée à un avatar qui existait déjà'
    'le serveur refuse la seconde sauvegarde en nommant les deux slots'
    'client 2 : le refus lui est rendu avec le même motif'
    "l'appel réel du fabricator est bloqué avant toute consommation"
    'le serveur nomme et date le refus de cadence'
    "la sonde mesure bien une partie en Survie"
    'le refus de cadence est nommé côté serveur, en Survie'
    "le changement d'adresse purge la demande et la latence, puis le client est réadmis"
)
for what in "${expected[@]}"; do
    found=0
    for failure in "${FAILURES[@]}"; do
        case "$failure" in
            *"$what"*) found=1 ;;
        esac
    done
    if [ "$found" = 0 ]; then
        echo "ECHEC : aucun échec nommé ne mentionne « $what »" >&2
        printf '  - %s\n' "${FAILURES[@]}" >&2
        exit 1
    fi
done

echo "OK   ${#FAILURES[@]} échec(s) nommés après retrait de quatorze lignes — les assertions discriminent"
echo "VERDICT : les assertions P, Q, N, B, R, AD et AE sont satisfiables et discriminantes (mesure en jeu non incluse)"
