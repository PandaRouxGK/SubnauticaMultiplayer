using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de **présence réelle**, activée par <c>-coopdebug=presence</c> : les deux moitiés de
    /// l'équipement et de la couleur qui n'avaient jamais été exercées avec de vrais joueurs.
    ///
    /// Pourquoi elle existe, et ce qu'elle répare : les runs d'équipement se terminaient toujours par
    /// `0 pièce(s) lue(s)`, parce que personne ne portait rien. La chaîne « ce que le joueur porte » →
    /// « ce que le réseau envoie » → « ce que l'autre voit » n'était donc mesurée qu'avec une liste vide,
    /// et la ligne `[presence] équipement appliqué sur l'avatar de …` ne s'était jamais affichée non plus :
    /// la couleur du serveur arrive **avant** que l'avatar distant existe, donc c'est le chemin « mémorisé
    /// puis appliqué à la création » qui s'exécute en pratique, jamais le chemin direct.
    ///
    /// Cette sonde-ci ne fabrique aucun état parallèle :
    ///
    /// - elle **fait vraiment porter** les pièces au joueur local (`Inventory.main.equipment`), par le
    ///   chemin que le jeu expose lui-même (une pièce déjà présente, sinon la commande console `item
    ///   &lt;TechType&gt;`), puis relit l'équipement avec la même fonction que le plugin
    ///   (<see cref="RemoteAvatarEquipment.ReadLocalEquipment"/>) : si la lecture ne voyait pas ce qui
    ///   est porté, ça se verrait ici ;
    /// - elle **attend un vrai joueur distant** avant de poser l'équipement, précisément pour que l'autre
    ///   client reçoive un **changement** d'équipement et exécute la ligne directe
    ///   (`équipement appliqué sur l'avatar de …`) sur un avatar réel ;
    /// - pour la couleur, elle **rejoue le chemin réel** d'un message du serveur
    ///   (<see cref="RemotePlayerManager.ProbeReplayColor"/> appelle le gestionnaire, il ne le réimplémente
    ///   pas), mesure la teinte de l'avatar **avant / après / après restauration**, et vérifie que l'indice
    ///   assigné par le serveur est bien remis. Le client ne décide toujours rien : c'est la sonde qui
    ///   décide, et elle le dit.
    ///
    /// Le verdict est écrit en dernière ligne, avec le marqueur stable `[sonde] présence : verdict=`.
    /// </summary>
    internal sealed class PresenceProbeBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Présence");

        /// <summary>Attente maximale (s) du monde et de l'inventaire.</summary>
        private const float WorldTimeoutSeconds = 240f;

        /// <summary>
        /// Attente maximale (s) d'un **joueur distant** avant de poser l'équipement.
        ///
        /// Ce n'est pas du confort : c'est ce qui fait que l'autre client reçoit un **changement**
        /// d'équipement (et exécute donc `équipement appliqué sur l'avatar de …`) au lieu de le mémoriser à
        /// l'admission. Au-delà du délai, on pose quand même l'équipement — un client seul doit pouvoir
        /// exercer la pose et la relecture — mais la sonde le dit, et la partie « changement vu par l'autre »
        /// n'est alors pas mesurée.
        /// </summary>
        private const float RemotePlayerWaitSeconds = 60f;

        /// <summary>Attente maximale (s) d'un objet, après la commande console qui le fait apparaître.</summary>
        private const float ItemSpawnTimeoutSeconds = 20f;

        /// <summary>Attente maximale (s) d'un avatar distant réel, pour la couleur.</summary>
        private const float RemoteAvatarTimeoutSeconds = 120f;

        /// <summary>Attente maximale (s) du client réseau et de son gestionnaire de joueurs distants.</summary>
        private const float NetworkWaitSeconds = 120f;

        /// <summary>
        /// Attente maximale (s) de la falsification envoyée par **l'autre** client.
        ///
        /// Large à dessein : les deux clients n'arrivent pas dans cette étape au même instant (le second
        /// charge son monde plus tard). Une fenêtre courte rendrait la mesure dépendante de l'ordre de
        /// lancement, et un client qui échoue parce que l'autre n'a pas encore commencé serait un faux
        /// négatif — exactement ce que cette sonde doit éviter.
        /// </summary>
        private const float ForgedWaitSeconds = 60f;

        /// <summary>Intervalle (s) de renvoi de la falsification pendant l'attente : voir ci-dessus.</summary>
        private const float ForgedResendSeconds = 8f;

        /// <summary>
        /// Délai (s) avant de remettre l'équipement réel après avoir vu la falsification de l'autre.
        ///
        /// Sans lui, la restauration du premier client pourrait arriver chez le second avant que celui-ci
        /// n'ait relu l'id diffusé — et la mesure serait perdue pour une question d'ordre de messages.
        /// </summary>
        private const float ForgedRestoreDelaySeconds = 8f;

        /// <summary>Attente maximale (s) de l'annonce d'objet tenu, après que l'outil est en main.</summary>
        private const float HeldItemEmitTimeoutSeconds = 15f;

        /// <summary>Attente maximale (s) de la prise en main d'un outil, réessais compris.</summary>
        private const float ToolHoldTimeoutSeconds = 8f;

        /// <summary>
        /// Borne (s) de la phase de nage : on s'arrête dès que l'autre client voit le déplacement, cette
        /// borne n'est donc qu'un garde-fou contre une nage sans observateur.
        ///
        /// Elle est large (une minute) parce que les deux sondes n'entrent pas dans cette étape ensemble :
        /// mesure faite, 22 s d'écart entre les deux. À 30 s, le premier arrivé sortait de sa fenêtre avant que
        /// le second ne nage — et lisait « déplacement 1,77 m », un chiffre vrai sur un désaccord de calendrier.
        /// </summary>
        private const float SwimSeconds = 60f;

        /// <summary>Durée (s) d'arrêt observée après la nage.</summary>
        private const float StopSeconds = 10f;

        /// <summary>Vitesse (m/s) imposée au moteur pendant la nage.</summary>
        private const float SwimSpeed = 2.6f;

        /// <summary>Vitesse minimale (m/s) que l'autre client doit voir pendant la nage.</summary>
        private const float SwimMinSpeed = 1f;

        /// <summary>Déplacement minimal (m) que l'autre client doit voir pendant la nage.</summary>
        private const float SwimMinDistanceMeters = 1f;

        /// <summary>
        /// Nombre d'échantillons où la position du pair **avance** avant de conclure qu'il nage vraiment. Un
        /// seul échantillon ne distingue pas une nage d'une correction de position.
        /// </summary>
        private const int SwimConfirmSamples = 10;

        /// <summary>Avance minimale (m) d'un échantillon à l'autre pour compter comme un pas de nage.</summary>
        private const float PositionAdvanceMeters = 0.01f;

        /// <summary>
        /// Durée (s) sans avance de la position du pair pour conclure qu'il s'est arrêté. 1,5 s couvre
        /// largement deux messages de position (20 Hz) perdus, sans attendre la fin de la fenêtre.
        /// </summary>
        private const float StopConfirmSeconds = 1.5f;

        /// <summary>
        /// Rayon (m) dans lequel la position du pair doit rester pendant <see cref="StopConfirmSeconds"/> pour
        /// conclure qu'il s'est arrêté.
        ///
        /// Pourquoi ce n'est pas « aucun échantillon n'avance de plus de 1 cm » : c'est ce que faisait la
        /// première version, avec <see cref="PositionAdvanceMeters"/>, et un avatar interpolé tremble de plus
        /// d'un centimètre même immobile — l'arrêt n'était alors jamais confirmé (mesuré : deux runs de
        /// l'étape B échoués, `arrêt tenu` jamais atteint alors que la vitesse observée valait `0,00 m/s`).
        /// Pourquoi ce n'est pas la vitesse annoncée seule : elle repasse par zéro **pendant** la nage et peut
        /// rester bloquée à sa dernière valeur **après** l'arrêt (mesuré : `2,60 m/s` dix secondes après
        /// l'arrêt). Le critère est donc un **déplacement borné** — `0,5 m`, la limite de visibilité retenue
        /// pour un avatar — sur une fenêtre de 1,5 s. La vitesse reste une observation journalisée.
        /// </summary>
        private const float StopRadiusMeters = 0.5f;

        /// <summary>
        /// Écart minimal (0-255) entre deux teintes relues pour les compter comme différentes.
        /// Même seuil que la sonde d'avatar, et même raison : au-dessus de l'arrondi d'un aller-retour
        /// couleur → matériau, très en dessous de ce que deux entrées de la palette produisent.
        /// </summary>
        private const int ColorDifferenceThreshold = 8;

        /// <summary>Écart toléré (0-255) à la restauration : la remise du même indice doit être exacte.</summary>
        private const int ColorRestoreTolerance = 2;

        /// <summary>
        /// Les pièces que la sonde fait porter. Choisies parce que leur effet est **visible** dans le
        /// modèle et déjà piloté par les règles de tenue : palmes (`flippers_basic_geo`), détendeur
        /// (`scuba_head`, `scuba_breatherTubes`), bouteille (`scuba_vest`). Trois pièces suffisent à
        /// distinguer « rien de porté » (3 nœuds actifs) de « équipé ».
        /// </summary>
        private static readonly TechType[] Wanted = { TechType.Fins, TechType.Rebreather, TechType.Tank };

        /// <summary>
        /// La pièce **ajoutée ensuite**, une fois que les deux joueurs sont là.
        ///
        /// Pourquoi une seconde pose : l'équipement initial est posé pendant que l'autre client charge encore
        /// son monde, donc il l'apprend à l'admission (ligne « mémorisé ») et l'applique à la création de
        /// l'avatar. Pour exercer la **ligne directe** (`équipement appliqué sur l'avatar de …`), il faut un
        /// changement qui arrive alors que l'avatar distant existe déjà. C'est un changement réel — une pièce
        /// de plus, portée pour de bon — et non un message fabriqué.
        /// </summary>
        private static readonly TechType[] Extra = { TechType.ReinforcedGloves };

        /// <summary>
        /// Délai (s) après la vue d'un avatar distant, avant la pose de la pièce supplémentaire.
        ///
        /// Il n'est pas décoratif : la vue de leur avatar chez moi ne garantit pas la vue du mien chez eux
        /// (les deux mondes chargent à leur rythme). Ce délai laisse à l'autre client le temps d'instancier
        /// mon avatar, faute de quoi son journal dirait « mémorisé » et la ligne directe ne serait pas exercée
        /// — ce serait une occasion manquée, pas une mesure.
        /// </summary>
        private const float SettleBeforeChangeSeconds = 10f;

        private IEnumerator Start()
        {
            Log.LogWarning(
                "[sonde] sonde de présence activée (-coopdebug=presence) — attente du monde puis d'un joueur distant...");

            var worldDeadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (Player.main == null || Inventory.main == null || Inventory.main.equipment == null)
            {
                if (Time.realtimeSinceStartup > worldDeadline)
                {
                    Log.LogError(
                        $"[sonde] présence : verdict=False — le joueur ou son équipement n'existe pas après " +
                        $"{WorldTimeoutSeconds:F0}s (Player.main / Inventory.main.equipment) : rien n'a pu être mesuré.");
                    yield break;
                }

                yield return new WaitForSeconds(1f);
            }

            // Le client réseau peut ne pas avoir commencé (son `Start` crée le gestionnaire distant) : sans
            // cette attente, `remotes == null` saute le délai de joueur distant, la sonde conclut « aucun
            // joueur » en quelques secondes — et un run à deux clients mesure un désert. Constaté une fois.
            var networkDeadline = Time.realtimeSinceStartup + NetworkWaitSeconds;
            var network = ClientNetworkBehaviour.Current;
            while ((network == null || network.Remotes == null) && Time.realtimeSinceStartup < networkDeadline)
            {
                yield return new WaitForSeconds(1f);
                network = ClientNetworkBehaviour.Current;
            }

            if (network == null || network.Remotes == null)
            {
                Log.LogWarning(
                    $"[sonde] présence : client réseau absent après {NetworkWaitSeconds:F0}s — les parties qui " +
                    "dépendent d'un joueur distant ne seront pas mesurées.");
            }

            var remotes = network != null ? network.Remotes : null;

            // — Poser l'équipement **avant** d'attendre qui que ce soit, puis relire avec la fonction du
            //   plugin. L'ordre a changé après une mesure : posé après l'arrivée de l'autre client, il
            //   n'était qu'un *changement* et la ligne « avatar instancié … tenue=N pièce(s) » ne pouvait
            //   plus être obtenue — les deux clients démarraient ensemble, et l'avatar de l'un naissait
            //   avant que l'autre ne se soit habillé. Posé d'abord (ce que fait un joueur qui se prépare
            //   avant d'inviter), l'équipement est visible à l'instanciation **et** la pièce ajoutée plus
            //   loin reste le changement reçu sur un avatar déjà là.
            yield return EquipAndVerify(Wanted, "posé");
            _equipOk = _lastEquipOk;

            // — Attendre un vrai joueur distant : c'est ce qui transforme la pièce ajoutée en *changement* vu
            //   par l'autre client, au lieu d'un état appris à l'admission.
            var remoteDeadline = Time.realtimeSinceStartup + RemotePlayerWaitSeconds;
            while (remotes != null && remotes.KnownRemoteCountForProbe == 0 && Time.realtimeSinceStartup < remoteDeadline)
            {
                yield return new WaitForSeconds(1f);
            }

            var remotesKnown = remotes != null ? remotes.KnownRemoteCountForProbe : 0;
            Log.LogInfo(
                remotesKnown > 0
                    ? $"[sonde] présence : {remotesKnown} joueur(s) distant(s) connu(s) — la pièce ajoutée sera un " +
                      "changement reçu par l'autre client, pas un état appris à l'admission."
                    : $"[sonde] présence : aucun joueur distant après {RemotePlayerWaitSeconds:F0}s — l'équipement est " +
                      "posé et relu (mesurable seul), mais l'autre moitié ne le sera pas.");

            // — Attendre un avatar distant, puis **changer** l'équipement : c'est ce changement-là que
            //   l'autre client verra arriver sur un avatar déjà instancié (ligne directe).
            yield return VerifyChangeAndColor(remotes);

            // — Falsifier le `PlayerId` : la seule façon de mesurer que le serveur le réécrit.
            yield return VerifyForgedPlayerId(remotes, network);

            // — Tenir un vrai outil, puis nager et s'arrêter : les deux lignes §19 qui restaient au client
            //   synthétique / à une trajectoire jouée hors du moteur du jeu.
            yield return HoldToolAndSwim(remotes, network);

            // — Verdict, écrit ici et **toujours** : une sonde qui ne conclut pas sur un chemin non exercé
            //   laisserait croire à un succès (le harnais ne cherche qu'un `verdict=False`).
            var colorExercised = _colorExercised;
            var colorPart = colorExercised ? _colorOk : !_colorFailed;
            var changePart = _changeExercised ? _changeOk : !_changeFailed;
            var forgedPart = _forgedExercised ? _forgedOk : !_forgedFailed;
            var toolPart = _toolExercised ? _toolOk : !_toolFailed;
            var motionPart = _motionExercised ? _motionOk : !_motionFailed;
            var verdict = _equipOk && changePart && colorPart && forgedPart && toolPart && motionPart;

            Log.LogInfo(
                $"[sonde] présence : équipement={_equipOk}, changement=" +
                (_changeExercised
                    ? $"{_changeOk}"
                    : (_changeFailed ? "False (défaut)" : "non exercé (aucun avatar distant)")) +
                $", couleur=" +
                (colorExercised
                    ? $"{_colorOk}"
                    : (_colorFailed ? "False (défaut : joueur distant sans avatar)" : "non exercée (aucun joueur distant)")) +
                $", PlayerId falsifié=" +
                (_forgedExercised
                    ? $"{_forgedOk}"
                    : (_forgedFailed ? "False (défaut : aucun joueur distant)" : "non exercé (aucun joueur distant)")) +
                $", outil en main=" +
                (_toolExercised
                    ? $"{_toolOk}"
                    : (_toolFailed ? "False (défaut)" : "non exercé")) +
                $", nage=" +
                (_motionExercised
                    ? $"{_motionOk}"
                    : (_motionFailed ? "False (défaut : aucun joueur distant)" : "non exercé (aucun joueur distant)")) +
                $" -> verdict={verdict}");
            Log.LogInfo($"[sonde] présence : verdict={verdict}");

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] présence : l'équipement porté n'est pas relu, ou la couleur ne change pas l'avatar " +
                    "distant, ou l'indice du serveur n'a pas été restauré — un joueur verrait un avatar faux.");
            }
        }

        /// <summary>
        /// Fait porter les pièces manquantes, puis relit l'équipement local avec la fonction du plugin.
        ///
        /// Le verdict de cette moitié est volontairement grossier — « les pièces voulues sont lues » — parce
        /// que c'est exactement la question : la lecture voit-elle ce que le joueur porte ?
        /// </summary>
        private IEnumerator EquipAndVerify(TechType[] types, string phase)
        {
            var equipment = Inventory.main.equipment;

            foreach (var type in types)
            {
                if (FindInEquipment(equipment, type) != null)
                {
                    Log.LogInfo($"[sonde] présence : '{type}' déjà porté — rien à poser.");
                    continue;
                }

                var pickupable = FindPickupable(type);
                if (pickupable == null)
                {
                    // Le chemin que le jeu expose lui-même pour se donner une pièce. On ne devine aucune
                    // API de fabrique : la commande console est le mécanisme prévu pour ça.
                    Log.LogInfo(
                        $"[sonde] présence : '{type}' introuvable localement — commande console « item {type} » " +
                        "(le jeu la traite comme si un joueur l'avait tapée).");
                    DevConsole.SendConsoleCommand($"item {type.AsString(false)}");

                    var spawnDeadline = Time.realtimeSinceStartup + ItemSpawnTimeoutSeconds;
                    while (pickupable == null && Time.realtimeSinceStartup < spawnDeadline)
                    {
                        yield return new WaitForSeconds(0.5f);
                        pickupable = FindPickupable(type);
                    }
                }

                if (pickupable == null)
                {
                    Log.LogError(
                        $"[sonde] présence : '{type}' n'est apparu nulle part après la commande console — " +
                        "cette pièce n'a pas pu être portée, donc la chaîne n'est pas mesurée pour elle.");
                    continue;
                }

                var slot = FindCompatibleSlot(equipment, pickupable);
                if (slot == null)
                {
                    Log.LogError(
                        $"[sonde] présence : '{type}' est là mais aucun emplacement de l'équipement ne l'accepte " +
                        "(AllowedToAdd faux sur tous les emplacements) — la pièce n'a pas pu être portée.");
                    continue;
                }

                // `forced: false` d'abord : les règles du jeu doivent accepter la pièce. Le second essai
                // n'est pas un contournement silencieux, il est journalisé.
                var item = new InventoryItem(pickupable);
                var accepted = equipment.AddItem(slot, item, false);
                if (!accepted)
                {
                    accepted = equipment.AddItem(slot, item, true);
                    Log.LogWarning(
                        $"[sonde] présence : '{type}' refusé par les règles (AddItem forced=false) — posé de force " +
                        $"(forced=true -> {accepted}) pour que la mesure ait lieu.");
                }

                Log.LogInfo(
                    accepted
                        ? $"[sonde] présence : '{type}' porté dans l'emplacement « {slot} »."
                        : $"[sonde] présence : '{type}' n'a PAS pu être porté (emplacement « {slot} »).");
            }

            yield return new WaitForEndOfFrame();

            var read = RemoteAvatarEquipment.ReadLocalEquipment(out var failure);
            var worn = new List<string>();
            foreach (var type in types)
            {
                if (read.Contains(type.ToString(), System.StringComparer.Ordinal))
                {
                    worn.Add(type.ToString());
                }
            }

            var equipOk = worn.Count == types.Length && failure == null;
            Log.LogInfo(
                $"[sonde] présence : équipement local {phase} — {read.Count} pièce(s) [{string.Join(", ", read)}] " +
                $"relue(s) par ReadLocalEquipment, dont {worn.Count}/{types.Length} de cette passe " +
                $"[{string.Join(", ", worn)}]" +
                (failure == null ? string.Empty : $", lecture en échec ({failure})") +
                $" -> verdict={equipOk}");

            if (!equipOk)
            {
                Log.LogError(
                    "[sonde] présence : les pièces posées ne sont pas relues — soit la pose a échoué, soit la " +
                    "lecture de l'équipement local ne voit pas ce qui est porté (c'est la moitié « émission »).");
            }

            _lastEquipOk = equipOk;
        }

        /// <summary>
        /// Rejoue, sur un **avatar distant réel**, le chemin d'un message de couleur du serveur, en mesure la
        /// teinte avant / après, puis remet l'indice assigné par le serveur et vérifie qu'il est revenu.
        ///
        /// Ce que ça prouve exactement : le gestionnaire qui applique une couleur à un avatar **déjà
        /// instancié** fonctionne et se voit sur les matériaux. C'est la branche que le hasard de l'ordre
        /// des messages n'exerce jamais en pratique (la couleur arrive avec l'admission, l'avatar plus tard).
        /// Ce que ça ne prouve pas, et la sonde le dit : qu'un vrai serveur enverrait cet indice-là.
        /// </summary>
        private IEnumerator VerifyChangeAndColor(RemotePlayerManager remotes)
        {
            if (remotes == null)
            {
                Log.LogWarning(
                    "[sonde] présence : couleur non exercée — le gestionnaire d'avatars distants n'existe pas " +
                    "(client non connecté ?).");
                _colorExercised = false;
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + RemoteAvatarTimeoutSeconds;

            // Déclarés ici, et pas dans la condition : en C#, une variable déclarée par `out var` dans la
            // condition d'un `while` n'existe plus après la boucle — et c'est justement après qu'on mesure.
            ushort playerId = 0;
            string username = null;
            byte assigned = 0;
            GameObject avatar = null;

            while (!remotes.TryGetFirstAvatarForProbe(out playerId, out username, out assigned, out avatar))
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Log.LogWarning(
                        $"[sonde] présence : couleur non exercée — aucun avatar distant réel après " +
                        $"{RemoteAvatarTimeoutSeconds:F0}s ({remotes.KnownRemoteCountForProbe} joueur(s) distant(s) " +
                        "connu(s)). Le chemin direct reste donc NON exercé.");

                    // Un joueur distant connu mais aucun avatar n'est pas un manque d'occasion : c'est un
                    // défaut, et il doit se lire comme tel.
                    _colorExercised = false;
                    _colorFailed = remotes.KnownRemoteCountForProbe > 0;
                    yield break;
                }

                yield return new WaitForSeconds(1f);
            }

            // Un avatar distant existe : l'autre client a donc un monde chargé. Le délai de stabilisation
            // s'assure qu'il a aussi **mon** avatar, avant que je change d'équipement.
            yield return new WaitForSeconds(SettleBeforeChangeSeconds);

            yield return EquipAndVerify(Extra, "changé");
            _changeExercised = true;
            _changeOk = _lastEquipOk;
            _changeFailed = !_changeOk;

            if (!_changeOk)
            {
                Log.LogError(
                    "[sonde] présence : la pièce ajoutée n'est pas relue — le changement d'équipement qui devait " +
                    "être vu par l'autre client n'a pas eu lieu.");
            }

            _colorExercised = true;

            var before = RemoteAvatarColor.AverageTint(avatar);
            var replay = (byte)((assigned + 1) % Coop.Model.Protocol.PlayerColorCount);

            remotes.ProbeReplayColor(playerId, replay);
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            var afterReplay = RemoteAvatarColor.AverageTint(avatar);

            var applied = Delta(before, afterReplay);
            var appliedOk = applied >= ColorDifferenceThreshold;

            Log.LogInfo(
                $"[sonde] présence : couleur rejouée — indice {replay} appliqué à l'avatar de '{username}' " +
                $"(id={playerId}) par la sonde, teinte avant={Format(before)}, après={Format(afterReplay)}, " +
                $"écart={applied}/255 (seuil {ColorDifferenceThreshold}) -> verdict={appliedOk}");

            // Restauration : l'indice du serveur doit reprendre la main, et la teinte revenir.
            remotes.ProbeReplayColor(playerId, assigned);
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            var restored = RemoteAvatarColor.AverageTint(avatar);
            var restoreDelta = Delta(before, restored);
            var restoreOk = restoreDelta <= ColorRestoreTolerance;

            Log.LogInfo(
                $"[sonde] présence : couleur rejouée — indice {assigned} restauré (celui du serveur) sur l'avatar de " +
                $"'{username}' (id={playerId}) : teinte={Format(restored)}, écart avec l'état initial={restoreDelta}/255 " +
                $"(tolérance {ColorRestoreTolerance}) -> verdict={restoreOk}");

            _colorOk = appliedOk && restoreOk;
            _colorFailed = !_colorOk;
            yield break;
        }

        private bool _equipOk;
        private bool _lastEquipOk;
        private bool _changeExercised;
        private bool _changeOk;
        private bool _changeFailed;
        private bool _colorExercised;
        private bool _colorOk;
        private bool _colorFailed;
        private bool _forgedExercised;
        private bool _forgedOk;
        private bool _forgedFailed;
        private bool _toolExercised;
        private bool _toolOk;
        private bool _toolFailed;
        private bool _motionExercised;
        private bool _motionOk;
        private bool _motionFailed;

        /// <summary>
        /// Falsifie le `PlayerId` de son propre équipement, puis **relit sous quel id le relais a diffusé la
        /// falsification de l'autre joueur**.
        ///
        /// Pourquoi ça ne peut pas se mesurer sur soi : le serveur ne renvoie jamais un message à son
        /// émetteur. Ce client-ci n'observe donc pas sa propre falsification, mais celle de l'autre — et
        /// réciproquement. Chacun annonce un équipement **marqué** (deux pièces, alors que la sonde en porte
        /// quatre) sous l'id de l'autre, puis attend que le message marqué arrive chez lui.
        ///
        /// Ce que le test distingue, et c'est tout l'intérêt :
        /// - l'id reçu est celui de **l'autre joueur** → le relais a réécrit le champ, la règle tient ;
        /// - l'id reçu est le **mien** → le relais a recopié le champ revendiqué, et n'importe quel client
        ///   pourrait donc modifier la tenue d'un autre joueur.
        ///
        /// Ce que ça ne mesure pas : un client malveillant qui falsifierait autre chose que ce champ.
        /// </summary>
        private IEnumerator VerifyForgedPlayerId(RemotePlayerManager remotes, ClientNetworkBehaviour network)
        {
            if (remotes == null || network == null)
            {
                Log.LogWarning("[sonde] présence : PlayerId falsifié non exercé — client non connecté.");
                _forgedExercised = false;
                yield break;
            }

            ushort remoteId = 0;
            string remoteName = null;
            GameObject avatar = null;

            // Un **autre client**, pas l'avatar de l'hôte : seul un client qui lance la même sonde peut
            // observer la falsification et la journaliser.
            if (!remotes.TryGetPeerAvatarForProbe(out remoteId, out remoteName, out avatar) || remoteId == 0)
            {
                Log.LogWarning(
                    "[sonde] présence : PlayerId falsifié non exercé — aucun autre client à qui prêter son id " +
                    $"({remotes.KnownRemoteCountForProbe} joueur(s) distant(s) connu(s)).");
                _forgedExercised = false;
                _forgedFailed = remotes.KnownRemoteCountForProbe > 0;
                yield break;
            }

            var mine = network.AssignedPlayerIdForProbe;

            // Liste **marquée** : deux pièces, alors que la sonde en porte quatre à ce stade. Un équipement
            // reçu avec exactement ces deux-là ne peut donc venir que de cet envoi.
            var marker = new[] { TechType.Tank.ToString(), TechType.Fins.ToString() };

            _forgedExercised = true;
            if (!network.ProbeSendEquipment(remoteId, marker, $"envoi à '{remoteName}' sous son id"))
            {
                _forgedFailed = true;
                _forgedOk = false;
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + ForgedWaitSeconds;
            var nextSend = Time.realtimeSinceStartup + ForgedResendSeconds;
            var observed = false;
            ushort receivedId = 0;
            string[] received = null;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (remotes.TryGetLastEquipmentForProbe(out var id, out var types, out _) && SameTypes(types, marker))
                {
                    observed = true;
                    receivedId = id;
                    received = types;
                    break;
                }

                // Renvoi régulier : l'autre client peut entrer dans cette étape bien après moi, et une
                // falsification envoyée une seule fois serait alors passée inaperçue.
                if (Time.realtimeSinceStartup >= nextSend)
                {
                    nextSend = Time.realtimeSinceStartup + ForgedResendSeconds;
                    network.ProbeSendEquipment(remoteId, marker, $"renvoi à '{remoteName}' sous son id");
                }

                yield return new WaitForSeconds(0.5f);
            }

            // Laisse à l'autre client le temps de relire l'id diffusé avant que ma restauration ne
            // remplace la liste marquée chez lui.
            if (observed)
            {
                yield return new WaitForSeconds(ForgedRestoreDelaySeconds);
            }

            // Remise de l'état réel : sans elle, l'avatar de l'autre client garderait la liste de la
            // falsification, et la prochaine mesure partirait d'un état faux.
            var restored = network.ProbeSendLocalEquipmentNow();

            if (!observed)
            {
                Log.LogError(
                    $"[sonde] présence : PlayerId falsifié — la falsification de l'autre joueur " +
                    $"[{string.Join(", ", marker)}] n'est jamais arrivée chez moi en {ForgedWaitSeconds:F0}s. " +
                    "Soit le relais l'a refusée sans le dire, soit l'autre client n'a pas envoyé la sienne : " +
                    "dans les deux cas la réécriture n'est pas démontrée.");
                _forgedOk = false;
                _forgedFailed = true;
                yield break;
            }

            var ok = receivedId == remoteId && receivedId != mine;
            Log.LogInfo(
                $"[sonde] présence : PlayerId falsifié — '{remoteName}' a annoncé [{string.Join(", ", received)}] " +
                $"sous l'id {mine} (le mien) ; le relais me l'a diffusé sous l'id {receivedId} " +
                $"(attendu : {remoteId}, l'id réel de l'émetteur ; ma propre falsification partie vers lui a été " +
                $"restaurée : {restored}) -> verdict={ok}");

            if (!ok)
            {
                Log.LogError(
                    "[sonde] présence : l'équipement falsifié est revenu sous l'id revendiqué — un client pourrait " +
                    "donc modifier la tenue d'un autre joueur. (Le serveur devrait journaliser le même écart : " +
                    "« vérification PlayerId (équipement) ».)");
            }

            _forgedOk = ok;
            _forgedFailed = !ok;
        }

        /// <summary>
        /// Fait tenir un **vrai outil** au joueur local, puis le fait **nager** et s'arrêter.
        ///
        /// Pourquoi ces deux moitiés ensemble : ce sont les deux lignes §19 qui n'avaient jamais été mesurées
        /// avec une instance de jeu. L'outil est pris par le chemin du jeu (pièce réelle → emplacement
        /// d'inventaire → `QuickSlots.Bind` → `Select`, le code que la touche « 1 » exécute), donc la lecture
        /// `Inventory.main.GetHeldTool()` de l'émetteur voit un outil comme chez un joueur qui l'a sorti.
        ///
        /// Pour le déplacement : la sonde **place** le joueur (téléportation sous la capsule, là où il y a de
        /// l'eau) puis laisse le **moteur du jeu** le déplacer (`Rigidbody.velocity` du joueur, celui que le
        /// `PlayerMotor` lit). Ce qui est réel : le moteur, la vitesse lissée de `ArmsController`, l'animateur
        /// et le réseau. Ce qui ne l'est pas : la décision de nager, qui est celle de la sonde — et « marcher »
        /// n'est pas exercé, la sauvegarde de test n'ayant pas de terre ferme à proximité.
        /// </summary>
        private IEnumerator HoldToolAndSwim(RemotePlayerManager remotes, ClientNetworkBehaviour network)
        {
            // La nage passe **avant** l'outil : la pose d'outil a un délai variable (création de la pièce,
            // attente de l'émission), et deux délais variables de part et d'autre décaleraient les deux sondes
            // l'une de l'autre — c'est ce décalage qui avait fait lire un joueur immobile.
            yield return SwimThenStop(remotes, network);
            yield return HoldRealTool(network);
        }

        private IEnumerator HoldRealTool(ClientNetworkBehaviour network)
        {
            _toolExercised = true;

            var type = TechType.Flashlight;
            var sentBefore = network != null ? network.HeldItemsSentForProbe : 0;

            if (Inventory.main.GetPickupCount(type) == 0)
            {
                // La commande console attend le nom de classe du jeu (« flashlight »), pas le nom de l'enum :
                // `AsString` est la conversion officielle, et elle évite une commande silencieusement ignorée.
                var classId = type.AsString(false);
                Log.LogInfo($"[sonde] présence : aucun '{type}' en inventaire — commande console « item {classId} ».");
                DevConsole.SendConsoleCommand($"item {classId}");

                var spawnDeadline = Time.realtimeSinceStartup + ItemSpawnTimeoutSeconds;
                while (Inventory.main.GetPickupCount(type) == 0 && Time.realtimeSinceStartup < spawnDeadline)
                {
                    yield return new WaitForSeconds(0.5f);
                }
            }

            var item = FindInventoryItem(type);
            if (item == null)
            {
                // Repli : un objet déjà présent dans le monde, ramassé par le chemin du jeu.
                var pickupable = FindPickupable(type);
                if (pickupable != null)
                {
                    Inventory.main.Pickup(pickupable, true);
                    yield return new WaitForEndOfFrame();
                    item = FindInventoryItem(type);
                }
            }

            if (item == null)
            {
                Log.LogError(
                    $"[sonde] présence : outil en main NON mesuré — aucun '{type}' n'a pu être mis dans " +
                    "l'inventaire (ni présent, ni créé par la commande console).");
                _toolOk = false;
                _toolFailed = true;
                yield break;
            }

            // La pose est réessayée, et ce n'est pas de la prudence gratuite : une pièce tout juste créée par
            // la console peut exister dans l'inventaire avant d'être "posée" dans l'emplacement rapide, et un
            // `Select` sur cet état rend `GetHeldTool() == null` (mesuré une fois, 0,5 s après la création).
            var held = default(PlayerTool);
            var heldType = TechType.None;
            var attempts = 0;
            var holdDeadline = Time.realtimeSinceStartup + ToolHoldTimeoutSeconds;
            while (Time.realtimeSinceStartup < holdDeadline)
            {
                attempts++;

                // L'item d'inventaire est relu à chaque essai : celui du premier passage peut être périmé.
                var current = FindInventoryItem(type);
                if (current != null)
                {
                    item = current;
                }

                Inventory.main.quickSlots.Bind(0, item);
                Inventory.main.quickSlots.Select(0);
                yield return new WaitForEndOfFrame();

                held = Inventory.main.GetHeldTool();
                if (held == null)
                {
                    // Le premier `Select` peut n'avoir fait que choisir l'emplacement ; la variante immédiate
                    // prend l'outil en main sans geste, ce qui est exactement ce que la sonde veut mesurer.
                    Inventory.main.quickSlots.SelectImmediate(0);
                    yield return new WaitForEndOfFrame();
                    held = Inventory.main.GetHeldTool();
                }

                heldType = TechType.None;
                if (held != null)
                {
                    var pickupable = held.GetComponent<Pickupable>();
                    heldType = pickupable != null ? pickupable.GetTechType() : TechType.None;
                }

                if (heldType == type)
                {
                    break;
                }

                yield return new WaitForSeconds(0.5f);
            }

            var heldOk = held != null && heldType == type;
            Log.LogInfo(
                $"[sonde] présence : outil réellement en main — '{type}' placé dans l'emplacement rapide 0 puis " +
                $"pris en main par QuickSlots (outil lu : {(held == null ? "aucun" : heldType.ToString())}, " +
                $"{attempts} essai(s)) -> verdict={heldOk}");

            if (!heldOk)
            {
                Log.LogError(
                    "[sonde] présence : aucun outil en main après la pose — la moitié « émission de l'objet tenu » " +
                    "ne peut pas être mesurée.");
                _toolOk = false;
                _toolFailed = true;
                yield break;
            }

            // La boucle réseau émet sur changement : elle doit voir l'outil d'elle-même. On l'attend, plutôt
            // que d'envoyer à sa place — sinon la sonde mesurerait son propre envoi.
            // Le jeu peut **relâcher** l'outil tout seul (constaté : tenu à la pose, puis plus rien une image
            // plus tard, et l'émission sur changement ne voit alors jamais de changement). On le reprend donc
            // pendant l'attente, et on compte les reprises : c'est ce compteur qui distingue « la boucle
            // n'émet pas » de « l'outil n'est pas resté en main ».
            var reheld = 0;
            var emitDeadline = Time.realtimeSinceStartup + HeldItemEmitTimeoutSeconds;
            while (network != null && network.HeldItemsSentForProbe <= sentBefore && Time.realtimeSinceStartup < emitDeadline)
            {
                if (Inventory.main.GetHeldTool() == null)
                {
                    reheld++;
                    Inventory.main.quickSlots.SelectImmediate(0);
                }

                yield return new WaitForSeconds(0.5f);
            }

            var stillHeld = Inventory.main.GetHeldTool() != null;
            var emitted = network != null && network.HeldItemsSentForProbe > sentBefore;
            Log.LogInfo(
                $"[sonde] présence : objet tenu émis par la boucle réseau — annonces {sentBefore} -> " +
                $"{(network != null ? network.HeldItemsSentForProbe : sentBefore)} en " +
                $"{HeldItemEmitTimeoutSeconds:F0}s (outil encore en main : {stillHeld}, reprises : {reheld}) " +
                $"-> verdict={emitted}");

            _toolOk = emitted;
            _toolFailed = !emitted;
        }

        private IEnumerator SwimThenStop(RemotePlayerManager remotes, ClientNetworkBehaviour network)
        {
            if (remotes == null || network == null || Player.main == null || Player.mainObject == null || Player.main.rigidBody == null)
            {
                Log.LogWarning("[sonde] présence : nage non exercée — joueur local ou gestionnaire distant absent.");
                _motionExercised = false;
                yield break;
            }

            ushort remoteId = 0;
            string remoteName = null;
            GameObject avatar = null;
            if (!remotes.TryGetPeerAvatarForProbe(out remoteId, out remoteName, out avatar) || remoteId == 0)
            {
                Log.LogWarning("[sonde] présence : nage non exercée — aucun autre client ne peut l'observer.");
                _motionExercised = false;
                _motionFailed = remotes.KnownRemoteCountForProbe > 0;
                yield break;
            }

            _motionExercised = true;

            // Mise à l'eau : on **sort de la capsule** avant de descendre, et on essaie plusieurs caps.
            //
            // Deux échecs mesurés ont dicté cette forme : un saut de 8 m vers le bas depuis la capsule pose le
            // joueur sur son plancher (le moteur le repousse, déplacement local 0,00 m) ; et un cap unique peut
            // atterrir dans le décor. Ici la sonde essaie quatre directions, s'arrête à la première où le jeu
            // dit « sous l'eau », et le dit dans le journal — le joueur reste libre de ses mouvements ensuite.
            var shore = Player.mainObject.transform.position;
            var current = new Vector3(0f, -1.5f, 25f);
            foreach (var candidate in new[]
                     {
                         new Vector3(0f, -3f, 25f),
                         new Vector3(25f, -3f, 0f),
                         new Vector3(-25f, -3f, 0f),
                         new Vector3(0f, -3f, -25f),
                     })
            {
                // `Player.SetPosition` est le téléport du **jeu** : il gère le déchargement des cellules et
                // le placement hors décor. Écrire directement la position laissait le joueur dans la
                // géométrie de la capsule, où le moteur le repoussait à chaque image (mesuré : 0,20 m de
                // déplacement local pour 4 s de poussée).
                Player.main.SetPosition(shore + candidate, Player.mainObject.transform.rotation);
                Player.main.rigidBody.velocity = Vector3.zero;

                var settle = Time.realtimeSinceStartup + 2f;
                while (Player.main.isWaitingForTeleportation && Time.realtimeSinceStartup < settle)
                {
                    yield return new WaitForSeconds(0.2f);
                }

                yield return new WaitForSeconds(0.4f);

                current = candidate;
                if (Player.main.IsUnderwater())
                {
                    break;
                }
            }

            if (!Player.main.IsUnderwater())
            {
                Log.LogWarning(
                    "[sonde] présence : le joueur n'est pas sous l'eau après les quatre caps essayés — la nage " +
                    "est mesurée quand même, mais la condition « sous l'eau » restera fausse.");
            }

            Log.LogInfo(
                $"[sonde] présence : mise à l'eau — cap ({current.x:F0}, {current.z:F0}) depuis la capsule, " +
                $"sous l'eau={Player.main.IsUnderwater()}");

            var forward = Player.mainObject.transform.forward;
            var firstSample = true;
            var origin = Vector3.zero;
            var maxSpeed = 0f;
            var maxDistance = 0f;
            var sawUnderwater = false;
            var peerMoving = false;
            var advancing = 0;
            var lastPosition = Vector3.zero;
            var swimOrigin = Player.mainObject.transform.position;

            // Phase 1 — nager **jusqu'à ce que l'autre client le voie bouger**, pas pendant une durée fixe.
            //
            // Une durée fixe a été essayée et ne tient pas : les deux sondes n'entrent pas dans cette étape
            // au même instant (mesuré : 8 s d'écart), et le premier arrivé nageait puis s'arrêtait pendant que
            // le second lisait encore des zéros. Ici chaque client nage tant que l'autre ne le voit pas, et
            // s'arrête sur la preuve — le décalage ne change plus le résultat, il coûte seulement du temps.
            var swimDeadline = Time.realtimeSinceStartup + SwimSeconds;
            while (Time.realtimeSinceStartup < swimDeadline)
            {
                // Attendre la **fin des FixedUpdate** avant d'imposer la vitesse : le `PlayerMotor` du jeu y
                // écrit la sienne (nulle sans commande), et une vitesse posée depuis `Update` est donc
                // écrasée avant le pas physique. C'est ce qui expliquait des déplacements locaux de 0,2 m.
                yield return new WaitForFixedUpdate();

                if (Player.main != null && Player.main.rigidBody != null)
                {
                    Player.main.rigidBody.velocity = forward * SwimSpeed;
                }

                if (remotes.TryGetMotionForProbe(remoteId, out var speed, out var position, out var underwater))
                {
                    if (firstSample)
                    {
                        firstSample = false;
                        origin = position;
                    }

                    maxSpeed = Mathf.Max(maxSpeed, speed);
                    maxDistance = Mathf.Max(maxDistance, Vector3.Distance(origin, position));
                    sawUnderwater |= underwater;

                    // Le mouvement doit **durer**, et c'est la **position** qui le dit, pas la vitesse.
                    //
                    // Deux essais ont montré que la vitesse annoncée par le pair est un mauvais critère :
                    // elle repasse par zéro entre deux messages (20 Hz) et elle vaut souvent 0 alors que
                    // l'avatar avance de plusieurs mètres — compter les échantillons « vitesse ≥ 1 » ne
                    // satisfaisait jamais la condition, alors que la position bougeait. On compte donc les
                    // échantillons où la position **avance**, ce qu'un téléport ne fait qu'une fois, et on
                    // garde la vitesse comme observation (`maxSpeed`).
                    if (advancing > 0 && Vector3.Distance(position, lastPosition) > PositionAdvanceMeters)
                    {
                        advancing++;
                    }
                    else if (advancing == 0 && maxDistance >= SwimMinDistanceMeters)
                    {
                        advancing = 1;
                    }

                    lastPosition = position;
                    peerMoving = advancing >= SwimConfirmSamples
                                 && maxDistance >= SwimMinDistanceMeters
                                 && maxSpeed >= SwimMinSpeed;
                }

                if (peerMoving)
                {
                    break;
                }
            }

            // Phase 2 — l'arrêt : c'est la moitié qui manquait au §22 (la trajectoire s'arrêtait sans que la
            // pose soit revérifiée après). Le critère est un **déplacement borné** : la position du pair doit
            // rester dans un rayon de `StopRadiusMeters` autour du point d'arrêt pendant `StopConfirmSeconds`.
            // Ni le pas de 1 cm par échantillon (un avatar interpolé tremble), ni la vitesse annoncée (elle
            // repasse par zéro et peut rester figée) ne conviennent — les deux ont été mesurés en échec.
            var finalSpeed = maxSpeed;
            var restSpeed = float.MaxValue;
            var stopped = false;
            var stopDrift = 0f;
            var stopAnchor = lastPosition;
            var stopClock = Time.realtimeSinceStartup;
            var stopDeadline = Time.realtimeSinceStartup + StopSeconds;
            while (Time.realtimeSinceStartup < stopDeadline)
            {
                if (Player.main != null && Player.main.rigidBody != null)
                {
                    Player.main.rigidBody.velocity = Vector3.zero;
                }

                if (remotes.TryGetMotionForProbe(remoteId, out var speed, out var position, out _))
                {
                    finalSpeed = speed;
                    restSpeed = Mathf.Min(restSpeed, speed);

                    // Tant que le pair reste dans le rayon, il ne bouge pas ; s'il en sort, il avance encore
                    // et l'ancre repart de sa nouvelle position — le maximum d'écart reste journalisé, pour
                    // qu'un vrai glissement se voie au lieu d'être absorbé par le rayon.
                    var drift = Vector3.Distance(position, stopAnchor);
                    stopDrift = Mathf.Max(stopDrift, drift);
                    if (drift > StopRadiusMeters)
                    {
                        stopAnchor = position;
                        stopClock = Time.realtimeSinceStartup;
                    }

                    lastPosition = position;
                    stopped = peerMoving && Time.realtimeSinceStartup - stopClock >= StopConfirmSeconds;
                    if (stopped)
                    {
                        break;
                    }
                }

                yield return null;
            }

            var movedOk = peerMoving;
            var stoppedOk = stopped;
            var ok = movedOk && stoppedOk && sawUnderwater;

            // Déplacement **local** : c'est le diagnostic qui distingue « le joueur n'a pas bougé » (la
            // poussée du moteur n'a pas pris) de « il a bougé mais rien n'est arrivé chez l'autre » (le
            // réseau ou l'observation est en cause). Sans cette ligne, les deux pannes se ressemblent.
            var localMoved = Player.mainObject != null
                ? Vector3.Distance(swimOrigin, Player.mainObject.transform.position)
                : 0f;

            if (localMoved < 0.5f)
            {
                Log.LogError(
                    $"[sonde] présence : le moteur n'a pas déplacé le joueur local ({localMoved:F2} m pour " +
                    $"{SwimSeconds:F0}s de poussée) — la moitié réseau de la nage n'a pas pu être exercée, et ce " +
                    "n'est pas le réseau qui est en cause.");
            }

            Log.LogInfo(
                $"[sonde] présence : nage puis arrêt vus par '{remoteName}' (id={remoteId}) — vitesse max " +
                $"{maxSpeed:F2} m/s (min {SwimMinSpeed:F1}), déplacement {maxDistance:F2} m (min " +
                $"{SwimMinDistanceMeters:F1}), sous l'eau={sawUnderwater}, vitesse après arrêt {finalSpeed:F2} m/s " +
                $"(observée, hors critère ; vitesse minimale vue {restSpeed:F2}), avancées vues={advancing}, " +
                $"écart à l'arrêt max={stopDrift:F2} m (critère : < {StopRadiusMeters:F1} m), arrêt tenu " +
                $"{StopConfirmSeconds:F1}s sur {SwimSeconds:F0}s de nage puis {StopSeconds:F0}s d'arrêt " +
                $"(déplacement local du nageur : {localMoved:F2} m) -> verdict={ok}");

            if (!ok)
            {
                Log.LogError(
                    "[sonde] présence : le déplacement du joueur n'est pas arrivé chez l'autre client, ou il ne " +
                    "s'est pas arrêté — un joueur verrait un avatar immobile ou qui glisse.");
            }

            _motionOk = ok;
            _motionFailed = !ok;
        }

        /// <summary>Même liste, dans le même ordre trié : les deux côtés trient avant d'envoyer.</summary>
        private static bool SameTypes(string[] left, string[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i], right[i], System.StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// L'`InventoryItem` de ce type **dans l'inventaire du joueur**, ou <c>null</c>.
        ///
        /// Le filtre passe par <see cref="Inventory.Contains"/> plutôt que par un parcours du conteneur :
        /// un objet du monde du même type ne doit pas être confondu avec celui qu'on vient de ramasser.
        /// </summary>
        private static InventoryItem FindInventoryItem(TechType type)
        {
            if (Inventory.main == null)
            {
                return null;
            }

            var pickupable = FindPickupable(type);
            if (pickupable == null || !Inventory.main.Contains(pickupable))
            {
                return null;
            }

            // `ItemsContainer` n'expose son énumérateur que par l'interface (implémentation explicite) :
            // c'est la forme que le compilateur accepte, pas `container.GetEnumerator()`.
            var container = Inventory.main.container as IEnumerable<InventoryItem>;
            if (container == null)
            {
                return null;
            }

            foreach (var item in container)
            {
                if (item != null && item.item == pickupable)
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>Écart maximal (0-255) entre deux teintes relues, composante par composante.</summary>
        private static int Delta(Color? a, Color? b)
        {
            if (a == null || b == null)
            {
                return -1;
            }

            var left = a.Value;
            var right = b.Value;
            return Mathf.Max(
                Mathf.Abs(Mathf.RoundToInt(left.r * 255f) - Mathf.RoundToInt(right.r * 255f)),
                Mathf.Max(
                    Mathf.Abs(Mathf.RoundToInt(left.g * 255f) - Mathf.RoundToInt(right.g * 255f)),
                    Mathf.Abs(Mathf.RoundToInt(left.b * 255f) - Mathf.RoundToInt(right.b * 255f))));
        }

        private static string Format(Color? color) =>
            color == null
                ? "illisible"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "({0:F0}, {1:F0}, {2:F0})",
                    color.Value.r * 255f,
                    color.Value.g * 255f,
                    color.Value.b * 255f);

        /// <summary>La pièce est-elle déjà portée ?</summary>
        private static Pickupable FindInEquipment(Equipment equipment, TechType type)
        {
            try
            {
                var slots = equipment.GetEquipment();
                while (slots.MoveNext())
                {
                    var item = slots.Current.Value;
                    var pickupable = item != null ? item.item : null;
                    if (pickupable != null && pickupable.GetTechType() == type)
                    {
                        return pickupable;
                    }
                }
            }
            catch (System.Exception exception)
            {
                Log.LogWarning($"[sonde] présence : lecture de l'équipement interrompue ({exception.Message}).");
            }

            return null;
        }

        /// <summary>
        /// Cherche une pièce de ce type **déjà présente** dans le monde ou l'inventaire : les objets créés à
        /// l'exécution sont dans une scène, les prefabs d'assets n'en ont pas — d'où le filtre, sans lequel on
        /// pourrait « équiper » un prefab de catalogue.
        /// </summary>
        private static Pickupable FindPickupable(TechType type)
        {
            foreach (var pickupable in Resources.FindObjectsOfTypeAll<Pickupable>())
            {
                if (pickupable == null || !pickupable.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (pickupable.GetTechType() == type)
                {
                    return pickupable;
                }
            }

            return null;
        }

        /// <summary>
        /// Premier emplacement de l'équipement qui accepte la pièce, **demandé au jeu** (`AllowedToAdd`)
        /// plutôt que déduit d'une table de noms : c'est l'autorité du jeu sur ses propres emplacements, et
        /// une table recopiée pourrait diverger sans que rien ne le dise.
        /// </summary>
        private static string FindCompatibleSlot(Equipment equipment, Pickupable pickupable)
        {
            var slots = equipment.SaveEquipment();
            foreach (var slot in slots.Keys.OrderBy(name => name, System.StringComparer.Ordinal))
            {
                if (equipment.AllowedToAdd(slot, pickupable, false))
                {
                    return slot;
                }
            }

            // Aucun emplacement libre n'accepte la pièce : on le dit avec la liste, plutôt que de rendre
            // un silence qu'on prendrait pour un défaut de modèle.
            var names = new StringBuilder();
            foreach (var slot in slots.Keys.OrderBy(name => name, System.StringComparer.Ordinal))
            {
                names.Append(names.Length == 0 ? string.Empty : ", ").Append(slot);
            }

            Log.LogWarning($"[sonde] présence : emplacements essayés [{names}] — aucun n'accepte '{pickupable.GetTechType()}'.");
            return null;
        }
    }
}
