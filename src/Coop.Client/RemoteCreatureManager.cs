using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Coop.Client
{
    /// <summary>
    /// Fait suivre au client les créatures que le serveur simule, et **coupe leur IA chez lui** (M3).
    ///
    /// C'est le jalon qui valide le modèle d'autorité du plan (§4, §6) : si une créature pilotée par
    /// le serveur, avec l'IA désactivée chez les clients, se trouve au même endroit des deux côtés,
    /// alors l'architecture tient pour tout le reste — véhicules, actions autoritaires, contenu.
    ///
    /// Le partage est net :
    ///
    /// - le **serveur** simule et envoie une position ; il ne reçoit rien ;
    /// - le **client** retrouve la même créature dans son propre monde (même sauvegarde, donc même
    ///   objet, cf. <see cref="CreatureIdentity"/>), arrête son IA, et applique la position reçue.
    ///
    /// Pourquoi arrêter l'IA au lieu de la laisser tourner : deux simulations de la même créature
    /// divergent forcément (PhysX n'est pas déterministe, §7), et le client se battrait contre l'état
    /// reçu. L'arrêt passe par le chemin du jeu (`Creature.AllowCreatureUpdates`), donc ce n'est pas
    /// un `enabled = false` qui ferait disparaître la créature : le maillage, l'animation et les
    /// collisions continuent.
    ///
    /// L'identité est **la clé elle-même**, pas une proximité : voir <see cref="DriftAnomalyMeters"/>
    /// pour la mesure qui a fait tomber l'ancien refus par distance (et ce qu'il coûtait).
    ///
    /// Ce que la classe **refuse** de faire, volontairement :
    ///
    /// - **appliquer un état qui ne désigne personne** : une clé absente du registre local n'est pas
    ///   devinée, l'entrée est comptée et nommée au journal ;
    /// - **garder une créature adoptée indéfiniment** : si le serveur cesse de l'envoyer (elle est
    ///   sortie du rayon, ou le serveur s'est arrêté), l'IA reprend chez le client après
    ///   <see cref="Protocol.CreatureAdoptionTimeoutSeconds"/>. Une créature figée sur une position
    ///   périmée serait un mensonge permanent.
    /// </summary>
    internal sealed class RemoteCreatureManager
    {
        internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Créature");

        /// <summary>
        /// Distance (m) au-delà de laquelle une adoption est **signalée comme anormale** — elle n'est
        /// plus refusée.
        ///
        /// **Ce seuil gardait autrefois la porte, et la mesure l'a démenti** (run du 2026-10-17 00:48,
        /// hôte + client : 64 créatures diffusées, 2 112 états après chargement du monde, **1 980
        /// appariés, 132 refusés, 0 clé inconnue**, registre local à **100 clés dont 0 partagée entre
        /// deux créatures**). Les 132 refus étaient **trois clés** de `Skyray(Clone)` réessayées
        /// ~44 fois chacune : la copie locale de ces trois-là avait dérivé de 45 à 73 m parce que son
        /// IA tournait encore, et le refus lui interdisait précisément l'adoption qui l'aurait arrêtée —
        /// une créature qui a dérivé ne revient jamais sous les 40 m, donc le refus était définitif.
        ///
        /// Or une clé partagée **désigne la même créature par construction** (même prefab, même position
        /// d'apparition quantifiée) : le test d'identité est la clé, et la distance entre deux copies ne
        /// dit rien de l'identité — elle dit seulement combien la copie locale s'est éloignée en
        /// attendant. Confondre les deux faisait refuser des correspondances justes (4,7 % du flux) en
        /// croyant protéger d'une erreur de clé qui, elle, ne s'est pas produite une seule fois.
        ///
        /// Le seuil reste donc comme **mesure** : une adoption au-delà est comptée et nommée, parce
        /// qu'une copie qui avait dérivé de 70 m va sauter de 70 m à l'écran, et ça doit se voir dans
        /// le journal plutôt que d'être lissé.
        /// </summary>
        private const float DriftAnomalyMeters = 40f;

        /// <summary>
        /// Rayon (m) dans lequel le registre local des clés est tenu à jour, **dérivé** du rayon de
        /// diffusion du protocole (× 1,5) plutôt que fixé à part.
        ///
        /// Deux constantes à tenir d'accord finiraient par diverger en silence : le jour où le
        /// serveur diffuse à 300 m, un registre local à 200 m refuserait des créatures que le serveur
        /// envoie, et l'échec ressemblerait à « clé inconnue » au lieu de « rayon trop court ». La
        /// marge de 50 % absorbe la latence et le déplacement entre deux rafraîchissements.
        /// </summary>
        private const float RegistryRadiusMeters = Protocol.CreatureRelevancyRadiusMeters * 1.5f;

        /// <summary>Intervalle (s) de reconstruction du registre des clés locales, **une fois stabilisé**.</summary>
        private const float RegistryRefreshSeconds = 2f;

        /// <summary>
        /// Intervalle (s) de reconstruction tant que le registre n'est **pas stabilisé**.
        ///
        /// Pourquoi ce second rythme : le registre se construit **par paliers**, et son premier palier non
        /// vide attrape un monde à moitié chargé — relevé du 2026-10-17, `3 clé(s)` puis `100 clé(s)` à 1,1 s
        /// d'intervalle. Pendant cette seconde, le client refusait des clés parfaitement légitimes, ce qui se
        /// lisait « clé inconnue » dans les compteurs et ressemblait à une divergence (mesuré : 550 états
        /// comptés ainsi en deux secondes, puis zéro pendant les dix-huit suivantes). Deux relevés consécutifs
        /// égaux valent « le monde a fini de peupler le registre » ; avant ça, on reconstruit quatre fois plus
        /// vite pour que la fenêtre trouble soit courte.
        /// </summary>
        private const float RegistryUnsettledRefreshSeconds = 0.25f;

        /// <summary>
        /// Écart de temps (s) au-delà duquel deux états reçus ne se suivent pas : la créature a quitté le
        /// flux (relevancy, cellule déchargée) puis y est revenue.
        ///
        /// Sans cette distinction, l'écart maximum mesuré ne veut rien dire. Relevé du 2026-10-17 :
        /// `dérive max=12.73m ('RabbitRay(Clone)')` — or 12,73 m en 100 ms ferait 127 m/s, ce qu'aucune
        /// raie ne fait. C'est un écart **après une interruption** : la copie locale, IA coupée, reste
        /// figée là où elle était, pendant que celle du serveur continue de nager.
        ///
        /// **1,5 × l'intervalle d'envoi (100 ms) et non 3,5 ×** : à 0,35 s, deux états espacés de 0,3 s
        /// passaient pour consécutifs, et l'écart mesuré valait alors le triple d'un intervalle — c'est
        /// ainsi qu'un `Peeper` a été relevé à `2,08 m`, soit 20,8 m/s pour un poisson qui en fait 5. Un
        /// écart qui a duré trois intervalles n'est pas une mesure de synchronisation, c'est une
        /// interruption courte : elle part dans l'autre compteur, où elle est nommée comme telle.
        /// </summary>
        private const float ConsecutiveStateGapSeconds = 0.15f;

        /// <summary>
        /// Intervalle (s) en dessous duquel deux états sont livrés dans la **même image** (rafale).
        ///
        /// Depuis que les créatures sont interpolées, un tel couple ne mesure plus rien : entre les deux
        /// annonces, la copie n'a pas eu une seule image pour avancer, donc l'écart vaut deux pas de serveur
        /// au lieu d'un — mesuré `2,76 m` sur un client alors que le plus grand pas d'annonce est `0,99 m`.
        /// Ce n'est pas une dérive locale, c'est l'interpolation qui n'a pas encore eu la main. Les couples
        /// plus courts que cette borne sont donc exclus de la mesure d'écart **et** de la courbe des pas ;
        /// les compter ferait accuser la synchronisation à la place de la cadence de livraison.
        /// </summary>
        private const float MinMeasuredGapSeconds = 0.02f;

        /// <summary>
        /// Durée maximale (s) d'un segment d'interpolation — la même borne que les véhicules.
        ///
        /// Pourquoi interpoler du tout : une créature ne reçoit sa position qu'à **10 Hz**
        /// (`Protocol.CreatureStreamIntervalTicks = 2`), et l'ancien code l'appliquait d'un coup. Le corps
        /// avançait donc par paliers de `0,25–0,5 m` (max mesuré `0,99 m`) pendant que son `Animator`
        /// continuait — d'où l'impression d'entités à ~10–30 images/s, alors que les joueurs (20 Hz +
        /// interpolation) et les véhicules (10 Hz + interpolation) sont fluides. Le remède est celui de M4 :
        /// le client glisse d'une annonce à la suivante au lieu de sauter.
        /// </summary>
        private const float MaxSegmentSeconds = 0.50f;

        /// <summary>Durée minimale (s) d'un segment : évite une division par zéro sur deux états rapprochés.</summary>
        private const float MinSegmentSeconds = 0.04f;

        /// <summary>
        /// Vitesse (m/s) au-delà de laquelle un segment est **allongé** au lieu de tout absorber en une
        /// image. Même valeur et même raison que `RemoteVehicleManager` : une rafale d'annonces a un
        /// intervalle mesuré presque nul, et s'y fier ferait réapparaître le saut.
        /// </summary>
        private const float MaxCatchUpSpeedMetersPerSecond = 40f;

        /// <summary>
        /// Au-delà de cette durée d'image (s), le pas mesuré est mis de côté : c'est une image longue
        /// (chargement, GC), pas un défaut d'interpolation. Elle est comptée et journalisée à part pour ne
        /// pas disparaître.
        /// </summary>
        private const float LongFrameSeconds = 0.05f;

        /// <summary>
        /// Intervalle minimal (s) entre deux reconstructions **imposées** par une clé inconnue.
        /// Le registre périodique suffit dans le cas normal ; celui-ci n'existe que pour rattraper
        /// une créature qui vient d'apparaître, sans transformer une clé définitivement inconnue en
        /// balayage permanent de la scène.
        /// </summary>
        private const float ForcedRebuildSeconds = 1f;

        private sealed class Adopted
        {
            public long Key;
            public string Label;
            public Creature Creature;
            public bool AiDisabled;

            /// <summary>
            /// Vrai quand l'index d'IA a été **relu et trouvé nul**. Distinct de <see cref="AiDisabled"/> :
            /// le jeu diffère parfois une désinscription demandée pendant qu'il parcourt son
            /// planificateur (`ScheduledUpdateBehaviourSet.Remove` ne fait que mettre l'objet dans une
            /// file dans ce cas), donc une lecture immédiate peut encore voir un index positif. On
            /// revérifie au lieu de conclure — dans un sens comme dans l'autre.
            /// </summary>
            public bool AiVerified;

            public bool AiFailureLogged;
            public float AdoptionRealtime;
            public int IndexBefore;
            public int IndexAfter;
            public float LastStateRealtime;
            public Vector3 LastServerPosition;
            public Quaternion LastServerRotation;
            public long StatesReceived;
            public float MaxDriftMeters;

            /// <summary>Écart mesuré **avant** que la coupure d'IA soit vérifiée (cf. la mesure).</summary>
            public float MaxPendingCutDriftMeters;

            public float FirstDistanceMeters;

            // — Segment d'interpolation : d'où l'on vient, où l'on va, et sur combien de temps —
            //
            // `From` est la position **au moment de l'annonce** (pas la position annoncée précédente) :
            // si une image a pris du retard, le segment part d'où la copie se trouve vraiment, sans quoi
            // elle reculerait pour repartir de l'ancienne cible.
            public Vector3 From;
            public Quaternion FromRotation;
            public Vector3 To;
            public Quaternion ToRotation;
            public float SegmentStart;
            public float SegmentDuration;
        }

        private readonly Dictionary<long, Adopted> _adopted = new Dictionary<long, Adopted>();
        private readonly Dictionary<long, Creature> _registry = new Dictionary<long, Creature>();
        private readonly ManualLogSource _log;

        private float _registryRealtime = float.NegativeInfinity;
        private float _nextForcedRebuildRealtime = float.NegativeInfinity;
        private float _nextReport;

        private long _snapshotsReceived;
        private long _statesReceived;
        private long _statesMatched;
        private long _statesUnknownKey;

        /// <summary>Nombre de clés inconnues déjà journalisées (borné : le journal ne doit pas noyer le run).</summary>
        private int _unknownLogged;
        private long _adoptionsFarFromServer;
        private long _adoptions;
        private long _releases;
        private long _unkeyedRegistry;

        // — Mesure par image de l'interpolation (ce que la sonde juge) —
        //
        // Sans elle, « c'est fluide » resterait une impression : le pas par image est la seule quantité
        // qui distingue un glissement d'un saut, et elle se mesure sur la copie réelle, pas sur le flux.
        private long _segmentsArmed;
        private long _snaps;
        private long _frameSteps;
        private double _frameStepSum;
        private float _maxFrameStepMeters;
        private float _maxFrameStepDelta;
        private string _maxFrameStepLabel = "aucune";
        private long _frameStepsAboveHalfMeter;
        private long _longFrameSteps;
        private float _maxLongFrameStepMeters;

        /// <summary>Couples d'états livrés dans la même image, exclus de la mesure d'écart (cf. la borne).</summary>
        private long _burstPairs;

        /// <summary>
        /// Nombre de clés **partagées par deux créatures** dans le registre local, relevé au dernier
        /// passage, et son maximum depuis le début.
        ///
        /// C'est la mesure qui met l'identité à l'épreuve : la clé est faite du nom du prefab et de la
        /// position d'apparition quantifiée à 5 cm, donc deux individus de la même espèce apparus au
        /// même endroit — un banc de poissons — peuvent porter **la même clé**. Tant que ce nombre est
        /// nul, « une clé désigne une créature » est vrai ; s'il ne l'est pas, la clé désigne un groupe,
        /// et c'est le refus par distance qui doit trancher. Sans ce compteur, le cas resterait
        /// silencieux : le registre garde le premier arrivé sans rien dire.
        /// </summary>
        private long _registryCollisions;
        private long _maxRegistryCollisions;
        private long _lastRegistryCollisions = -1;
        private int _lastRegistryKeyCount = -1;
        private long _lastRegistryUnkeyed = -1;
        private bool _registryLogged;

        /// <summary>
        /// Vrai quand deux reconstructions consécutives ont donné le même nombre de clés : le registre ne
        /// bouge plus, on peut espacer les relevés. Faux au démarrage et à chaque retour dans le menu.
        /// </summary>
        private bool _registrySettled;

        public RemoteCreatureManager(ManualLogSource log)
        {
            _log = log;
        }

        public int AdoptedCount => _adopted.Count;

        /// <summary>
        /// Nombre de clés du registre local. Vaut <c>0</c> tant que le monde n'est pas chargé : une sonde
        /// doit attendre qu'il soit non nul avant de juger quoi que ce soit, sinon elle compte comme
        /// « clés inconnues » tous les états reçus pendant le chargement.
        /// </summary>
        public int RegistryKeyCount => _registry.Count;

        /// <summary>Créatures du registre **sans clé** : elles ne peuvent pas être suivies (mesure, pas hypothèse).</summary>
        public long UnkeyedRegistry => _unkeyedRegistry;

        /// <summary>
        /// Maximum observé de clés **partagées par deux créatures** du registre local. Zéro veut dire que
        /// l'identité est bijective : une clé désigne une créature et une seule.
        /// </summary>
        public long MaxSharedKeys => _maxRegistryCollisions;

        /// <summary>Nombre de créatures actuellement adoptées **dont l'IA est effectivement arrêtée**.</summary>
        public int AiDisabledCount
        {
            get
            {
                var count = 0;
                foreach (var entry in _adopted.Values)
                {
                    if (entry.AiVerified)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public long StatesMatched => _statesMatched;

        /// <summary>
        /// Adoptions où la copie locale avait dérivé au-delà de <see cref="DriftAnomalyMeters"/>. Ce
        /// n'est plus un refus : c'est le nombre de créatures qui ont dû être ramenées de loin à la
        /// position du serveur, donc ce que l'ancien refus laissait définitivement désynchronisé.
        /// </summary>
        public long AdoptionsFarFromServer => _adoptionsFarFromServer;

        public long StatesUnknownKey => _statesUnknownKey;

        /// <summary>
        /// Dérive maximale observée (m) : l'écart, **avant application**, entre la position de la
        /// créature locale et celle que le serveur vient d'annoncer.
        ///
        /// C'est la mesure qui dit si le client est bien spectateur : si son IA tournait, l'écart
        /// entre deux états (100 ms) grandirait avec le temps, alors qu'il reste ici borné par ce que
        /// la créature bouge entre deux envois.
        /// </summary>
        public float MaxDriftMeters { get; private set; }

        /// <summary>
        /// Créature qui a produit <see cref="MaxDriftMeters"/>.
        ///
        /// Sans son nom, un maximum de dérive est ininterprétable : 3,89 m en 100 ms, c'est énorme pour
        /// un poisson et parfaitement normal pour une raie qui vole vite. Le chiffre seul ne dit pas si
        /// la synchronisation tient, le nom le dit.
        /// </summary>
        public string MaxDriftLabel { get; private set; } = "aucune";

        /// <summary>
        /// Écart maximum (m) mesuré **avant** que la coupure d'IA soit vérifiée, et sa créature.
        ///
        /// Publié à part parce qu'il ne teste pas la même chose : pendant la mise en file d'attente de la
        /// désinscription, la créature simule encore chez le client — c'est prévu. Confondre les deux faisait
        /// passer ce délai pour une dérive de synchronisation.
        /// </summary>
        public float MaxPendingCutDriftMeters { get; private set; }

        public string MaxPendingCutDriftLabel { get; private set; } = "aucune";

        /// <summary>
        /// Part de l'écart qu'aucun déplacement du serveur n'explique, et sa créature.
        ///
        /// L'écart mesuré est la distance entre la copie locale et la position annoncée. Si la copie locale
        /// était exactement à la position annoncée précédente, cet écart vaudrait le déplacement du serveur
        /// entre les deux annonces — et rien d'autre. Ce qui reste au-delà est donc du **mouvement local** :
        /// un `Rigidbody` que la physique du jeu re-accélère, une animation à déplacement racine, un frein pas
        /// encore appliqué. C'est la seule partie de l'écart que le mod puisse corriger.
        ///
        /// Mesure du 2026-10-17 : `1,13 m` d'écart pour `0,85 m` de déplacement serveur au maximum — donc
        /// l'essentiel de l'écart est le mouvement du serveur lui-même, et c'est le nombre à regarder pour
        /// M4 (un Seamoth à 20 m/s balaie 2 m par intervalle).
        /// </summary>
        public float MaxLocalJitterMeters { get; private set; }

        public string MaxLocalJitterLabel { get; private set; } = "aucune";

        /// <summary>Écart maximum (m) **après une interruption de diffusion**, et sa créature.</summary>
        public float MaxGapDriftMeters { get; private set; }

        public string MaxGapDriftLabel { get; private set; } = "aucune";

        /// <summary>Interruption la plus longue (s) avant qu'un état ne revienne, et sa créature.</summary>
        public float MaxGapSeconds { get; private set; }

        public string MaxGapLabel { get; private set; } = "aucune";

        /// <summary>
        /// Durée (s) réelle de l'intervalle sur lequel <see cref="MaxDriftMeters"/> a été mesuré.
        ///
        /// Un écart de 2 m n'a pas le même sens selon qu'il a été mesuré en 100 ms (20 m/s, énorme) ou en
        /// 150 ms (13 m/s, plausible pour un poisson qui accélère). Sans cette durée, le maximum reste
        /// ambigu — c'est exactement ce qui a fait annoncer `127 m/s` pour une raie immobile.
        /// </summary>
        public float MaxDriftGapSeconds { get; private set; }

        public float MaxFirstDistanceMeters { get; private set; }

        // — Lecture des compteurs par image (utilisés par `CreatureProbeBehaviour`) —

        /// <summary>Nombre d'images où la copie a réellement avancé (interpolation en cours).</summary>
        public long FrameSteps => _frameSteps;

        /// <summary>Pas par image maximal (m), et la durée (s) de l'image où il a eu lieu.</summary>
        public float MaxFrameStepMeters => _maxFrameStepMeters;

        public float MaxFrameStepDelta => _maxFrameStepDelta;

        public string MaxFrameStepLabel => _maxFrameStepLabel;

        /// <summary>Nombre d'images où le pas a dépassé `0,5 m` — seuil de visibilité pour un poisson.</summary>
        public long FrameStepsAboveHalfMeter => _frameStepsAboveHalfMeter;

        /// <summary>Images trop longues pour juger un pas (`> 50 ms`), comptées à part.</summary>
        public long LongFrameSteps => _longFrameSteps;

        public float MaxLongFrameStepMeters => _maxLongFrameStepMeters;

        public long SegmentsArmed => _segmentsArmed;

        public long Snaps => _snaps;

        /// <summary>Pas par image moyen (m) sur les images animées.</summary>
        public float MeanFrameStepMeters => _frameSteps > 0 ? (float)(_frameStepSum / _frameSteps) : 0f;

        /// <summary>
        /// Remet à zéro la seule mesure qui doit porter sur une **fenêtre** : celle par image. Les autres
        /// compteurs sont cumulés depuis le démarrage et sont lus tels quels par les sondes.
        /// </summary>
        public void ResetFrameWindow()
        {
            _frameSteps = 0;
            _frameStepSum = 0;
            _maxFrameStepMeters = 0f;
            _maxFrameStepDelta = 0f;
            _maxFrameStepLabel = "aucune";
            _frameStepsAboveHalfMeter = 0;
            _longFrameSteps = 0;
            _maxLongFrameStepMeters = 0f;
        }

        /// <summary>
        /// La mesure par image, telle quelle : c'est elle qui prouve que le saut de l'annonce a été
        /// remplacé par un glissement. Le libellé de la créature est inclus — un maximum sans nom ne dit
        /// pas si le défaut vient d'une raie rapide ou d'une interpolation cassée.
        /// </summary>
        public string DescribeFrameSteps()
        {
            var speed = _maxFrameStepDelta > 0f ? _maxFrameStepMeters / _maxFrameStepDelta : 0f;
            return string.Format(
                CultureInfo.InvariantCulture,
                "pas par image max={0:F2}m sur une image de {1:F0}ms (soit {2:F1} m/s, {3}), " +
                "moy={4:F3}m, image(s) animée(s)={5}, dont ≥0,5m={6}, images longues={7} (pas max {8:F2}m) " +
                "— segments={9}, applications directes={10}, rafales écartées={11}",
                _maxFrameStepMeters,
                _maxFrameStepDelta * 1000f,
                speed,
                _maxFrameStepLabel,
                MeanFrameStepMeters,
                _frameSteps,
                _frameStepsAboveHalfMeter,
                _longFrameSteps,
                _maxLongFrameStepMeters,
                _segmentsArmed,
                _snaps,
                _burstPairs);
        }

        // — Courbes mesurées : où se situe la limite d'un véhicule, et à partir de quand ça clignote ———
        //
        // Deux questions différentes, une même forme de réponse : un **histogramme**. Une moyenne ou un
        // maximum ne dit pas où se situe une limite ; une répartition, si.
        //
        // 1. **M4** : la quantité qui décide pour un véhicule rapide est le déplacement du serveur entre
        //    deux annonces (`serverMove`), parce que c'est ce que le client applique d'un coup, sans
        //    interpolation. Un pas de `0,85 m` à 10 Hz, c'est 8,5 m/s ; un Seamoth à 20 m/s balaierait
        //    `2 m` par annonce. La répartition dit si cette bande-là a déjà été observée.
        // 2. **Le clignotement** : une interruption de flux est suivie d'un écart entre la copie locale et
        //    la position annoncée — c'est ce saut qui se voit. La répartition dit à partir de quelle durée
        //    d'interruption les sauts se comptent en mètres.
        //
        // Aucune des deux n'entre dans le verdict : elles mesurent **où placer un seuil**, ce qui n'est pas
        // encore décidé. Les inclure dans un verdict ferait passer un arbitrage pour une mesure.
        private static readonly float[] StepBucketsMeters = { 0.25f, 0.5f, 1f, 2f };
        private static readonly float[] GapBucketsSeconds = { 0.5f, 1f, 2f };

        private readonly long[] _stepCounts = new long[StepBucketsMeters.Length + 1];
        private readonly float[] _stepMaxLocalMeters = new float[StepBucketsMeters.Length + 1];
        private readonly string[] _stepMaxLocalLabels = new string[StepBucketsMeters.Length + 1];

        private readonly long[] _gapCounts = new long[GapBucketsSeconds.Length + 1];
        private readonly float[] _gapMaxSnapMeters = new float[GapBucketsSeconds.Length + 1];
        private readonly string[] _gapMaxSnapLabels = new string[GapBucketsSeconds.Length + 1];

        /// <summary>Le plus grand pas observé du serveur entre deux annonces, et sa créature.</summary>
        public float MaxServerStepMeters { get; private set; }

        public string MaxServerStepLabel { get; private set; } = "aucun";

        /// <summary>Durée de l'intervalle sur lequel ce plus grand pas a été mesuré.</summary>
        public float MaxServerStepGapSeconds { get; private set; }

        private void RecordStep(float serverMove, float local, float gap, string label)
        {
            var bucket = BucketIndex(serverMove, StepBucketsMeters);
            _stepCounts[bucket]++;
            if (local > _stepMaxLocalMeters[bucket])
            {
                _stepMaxLocalMeters[bucket] = local;
                _stepMaxLocalLabels[bucket] = label;
            }

            if (serverMove > MaxServerStepMeters)
            {
                MaxServerStepMeters = serverMove;
                MaxServerStepLabel = label;
                MaxServerStepGapSeconds = gap;
            }
        }

        private void RecordGap(float gap, float snap, string label)
        {
            var bucket = BucketIndex(gap, GapBucketsSeconds);
            _gapCounts[bucket]++;
            if (snap > _gapMaxSnapMeters[bucket])
            {
                _gapMaxSnapMeters[bucket] = snap;
                _gapMaxSnapLabels[bucket] = label;
            }
        }

        private static int BucketIndex(float value, float[] bounds)
        {
            for (var index = 0; index < bounds.Length; index++)
            {
                if (value < bounds[index])
                {
                    return index;
                }
            }

            return bounds.Length;
        }

        /// <summary>
        /// La répartition des pas du serveur entre deux annonces, avec l'erreur **locale** maximale de
        /// chaque bande. C'est la mesure qui doit décider M4 : ce que le client applique d'un coup.
        /// </summary>
        public string DescribeStepCurve()
        {
            var text = new StringBuilder();
            for (var index = 0; index < _stepCounts.Length; index++)
            {
                if (text.Length > 0)
                {
                    text.Append(" · ");
                }

                text.Append(StepBucketLabel(index)).Append(" : ").Append(_stepCounts[index]);
                if (_stepCounts[index] > 0)
                {
                    text.Append(" (erreur locale max ")
                        .Append(_stepMaxLocalMeters[index].ToString("F2", CultureInfo.InvariantCulture))
                        .Append(" m, ")
                        .Append(_stepMaxLocalLabels[index])
                        .Append(')');
                }
            }

            text.Append(" — plus grand pas = ")
                .Append(MaxServerStepMeters.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" m en ")
                .Append(MaxServerStepGapSeconds.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" s (")
                .Append(MaxServerStepLabel)
                .Append("), soit ")
                .Append((MaxServerStepGapSeconds > 0f
                        ? MaxServerStepMeters / MaxServerStepGapSeconds
                        : 0f)
                    .ToString("F1", CultureInfo.InvariantCulture))
                .Append(" m/s");

            return text.ToString();
        }

        private static string StepBucketLabel(int index)
        {
            switch (index)
            {
                case 0: return "< 0,25 m";
                case 1: return "0,25–0,5 m";
                case 2: return "0,5–1 m";
                case 3: return "1–2 m";
                default: return "≥ 2 m";
            }
        }

        /// <summary>
        /// La répartition des **interruptions de flux** par durée, avec le saut maximal observé dans chaque
        /// bande : c'est la mesure du « clignotement » (la copie reprend l'IA, puis est ré-annonce ailleurs).
        /// </summary>
        public string DescribeGapHistogram()
        {
            var text = new StringBuilder();
            for (var index = 0; index < _gapCounts.Length; index++)
            {
                if (text.Length > 0)
                {
                    text.Append(" · ");
                }

                text.Append(GapBucketLabel(index)).Append(" : ").Append(_gapCounts[index]);
                if (_gapCounts[index] > 0)
                {
                    text.Append(" (saut max ")
                        .Append(_gapMaxSnapMeters[index].ToString("F2", CultureInfo.InvariantCulture))
                        .Append(" m, ")
                        .Append(_gapMaxSnapLabels[index])
                        .Append(')');
                }
            }

            return text.ToString();
        }

        private static string GapBucketLabel(int index)
        {
            switch (index)
            {
                case 0: return "< 0,5 s";
                case 1: return "0,5–1 s";
                case 2: return "1–2 s";
                default: return "≥ 2 s";
            }
        }

        /// <summary>
        /// Reçoit un état de créatures du serveur : apparie, arrête l'IA si besoin, applique la
        /// position. Renvoie le nombre d'entrées appliquées.
        /// </summary>
        public int OnSnapshot(CreatureSnapshotMessage message)
        {
            if (message == null || message.Creatures == null || message.Creatures.Length == 0)
            {
                return 0;
            }

            _snapshotsReceived++;
            EnsureRegistry();
            var applied = 0;

            for (var i = 0; i < message.Creatures.Length; i++)
            {
                var state = message.Creatures[i];
                _statesReceived++;

                if (state.Key == 0L)
                {
                    // Une clé nulle ne désigne rien : c'est une entrée que le serveur n'aurait pas dû
                    // envoyer (il refuse justement les créatures sans clé). On la compte.
                    _statesUnknownKey++;
                    continue;
                }

                if (!_adopted.TryGetValue(state.Key, out var entry))
                {
                    if (!TryAdopt(state, out entry))
                    {
                        continue;
                    }
                }
                else if (entry.Creature == null)
                {
                    // La créature a été détruite depuis l'adoption (déchargement de cellule) : son
                    // suivi n'a plus d'objet. `Update` la relâchera ; ici on ne touche à rien.
                    continue;
                }

                var position = new Vector3(state.X, state.Y, state.Z);
                var rotation = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);

                // Mesure **avant** d'écraser la position : c'est là, et seulement là, qu'on voit ce
                // que la créature locale a fait de son côté depuis le dernier état.
                if (entry.StatesReceived > 0)
                {
                    var drift = Vector3.Distance(entry.Creature.transform.position, position);
                    var gap = Time.realtimeSinceStartup - entry.LastStateRealtime;

                    if (gap < MinMeasuredGapSeconds)
                    {
                        // Rafale : la copie n'a pas eu une image pour avancer, l'écart mesurerait deux
                        // annonces au lieu d'une. Compté, pas ignoré.
                        _burstPairs++;
                    }
                    else if (gap <= ConsecutiveStateGapSeconds)
                    {
                        if (!entry.AiVerified)
                        {
                            // L'IA n'est **pas encore** vérifiée coupée : le jeu met la désinscription en
                            // file d'attente, et pendant ce temps la créature simule normalement — une
                            // `Peeper` qui fuit parcourt des mètres. Compter cet écart comme une dérive
                            // ferait dire « le client ne suit pas » alors que le client n'a pas encore
                            // reçu la main. Relevé du 2026-10-17 : écart client `2.18 m` sur un intervalle
                            // de `0.11 s` quand le serveur, lui, n'avait bougé que de `0.55 m`.
                            entry.MaxPendingCutDriftMeters = Mathf.Max(entry.MaxPendingCutDriftMeters, drift);
                            if (drift > MaxPendingCutDriftMeters)
                            {
                                MaxPendingCutDriftMeters = drift;
                                MaxPendingCutDriftLabel = entry.Label;
                            }
                        }
                        else
                        {
                            // États qui se suivent **et** IA vérifiée coupée : c'est la seule valeur qui
                            // teste la synchronisation, parce que rien d'autre que la nage de la créature
                            // n'a pu bouger la copie locale.
                            entry.MaxDriftMeters = Mathf.Max(entry.MaxDriftMeters, drift);
                            if (drift > MaxDriftMeters)
                            {
                                MaxDriftMeters = drift;
                                MaxDriftLabel = entry.Label;
                                MaxDriftGapSeconds = gap;
                            }

                            // Décomposition : la copie locale était à la position annoncée précédente, donc
                            // l'écart dû au **serveur** vaut sa distance entre les deux annonces. Le reste
                            // est du mouvement local, la seule part que ce mod puisse corriger.
                            var serverMove = Vector3.Distance(entry.LastServerPosition, position);
                            var local = Mathf.Max(0f, drift - serverMove);
                            if (local > MaxLocalJitterMeters)
                            {
                                MaxLocalJitterMeters = local;
                                MaxLocalJitterLabel = entry.Label;
                            }

                            // Répartition des pas : la mesure qui doit décider M4 (cf. les champs ci-dessus).
                            RecordStep(serverMove, local, gap, entry.Label);
                        }
                    }
                    else
                    {
                        // Interruption : la copie locale est restée figée pendant que le serveur
                        // continuait. Ce qui est mesuré ici est la **continuité du flux**, pas la
                        // synchronisation — les confondre faisait passer une raie immobile pour une
                        // créature à 127 m/s.
                        if (drift > MaxGapDriftMeters)
                        {
                            MaxGapDriftMeters = drift;
                            MaxGapDriftLabel = entry.Label;
                        }

                        if (gap > MaxGapSeconds)
                        {
                            MaxGapSeconds = gap;
                            MaxGapLabel = entry.Label;
                        }

                        // Répartition des interruptions : la mesure du « clignotement ».
                        RecordGap(gap, drift, entry.Label);
                    }
                }

                // La durée du segment est l'intervalle **mesuré** entre deux annonces : elle doit être lue
                // avant que `LastStateRealtime` ne soit écrasé.
                var stateNow = Time.realtimeSinceStartup;
                var gapSinceLastState = entry.StatesReceived > 0 ? stateNow - entry.LastStateRealtime : 0f;

                entry.LastServerPosition = position;
                entry.LastServerRotation = rotation;
                entry.LastStateRealtime = stateNow;
                entry.StatesReceived++;
                _statesMatched++;

                // Interpolation : le serveur reste la source de vérité, mais sa position est rejointe **par
                // glissement** sur la durée mesurée entre deux annonces, au lieu d'être appliquée d'un
                // coup. C'est la correction de la cadence à 10 Hz des créatures : le client avance à chaque
                // image, la copie arrive sur la cible quand l'annonce suivante arrive.
                //
                // Deux cas restent des **applications directes**, parce qu'il n'y a rien à glisser :
                //  - la première annonce (aucune position de départ n'existe) ;
                //  - une interruption de flux de plus de `MaxSegmentSeconds` : la durée mesurée ne décrit
                //    plus le mouvement, et glisser sur elle laisserait la créature en retard.
                // Dans les deux cas, `_snaps` le compte — un glissement ne doit pas se confondre avec un
                // rattrapage.
                if (entry.StatesReceived == 1 || gapSinceLastState > MaxSegmentSeconds)
                {
                    entry.Creature.transform.position = position;
                    entry.Creature.transform.rotation = rotation;
                    entry.From = position;
                    entry.FromRotation = rotation;
                    entry.SegmentStart = stateNow;
                    entry.SegmentDuration = MinSegmentSeconds;
                    _snaps++;
                }
                else
                {
                    var distance = Vector3.Distance(entry.Creature.transform.position, position);
                    // Même garde que les véhicules : une rafale d'annonces a un intervalle presque nul, et
                    // s'y fier ferait passer `alpha` à 1 en une image — le saut reviendrait entier.
                    var catchUp = distance / MaxCatchUpSpeedMetersPerSecond;
                    entry.From = entry.Creature.transform.position;
                    entry.FromRotation = entry.Creature.transform.rotation;
                    entry.SegmentStart = stateNow;
                    entry.SegmentDuration = Mathf.Clamp(
                        Mathf.Max(gapSinceLastState, catchUp), MinSegmentSeconds, MaxSegmentSeconds);
                    _segmentsArmed++;
                }

                entry.To = position;
                entry.ToRotation = rotation;

                // Le frein est réappliqué à **chaque** état, pas seulement à l'adoption.
                //
                // Mesure du 2026-10-17 : l'IA coupée (index relu à 0, vérifié, pas de mise en file
                // d'attente — `écart avant coupure vérifiée=0.00m`), un `Peeper` parcourait tout de même
                // `2.29 m` entre deux états séparés de `0.10 s` — soit 20 m/s — alors que le serveur, lui,
                // n'avait bougé que de `0.55 m`. Quelque chose déplace la copie en local : un `Rigidbody`
                // que la physique du jeu (flottabilité, traînée, courants) re-accélère entre deux états.
                // La position mesurée n'est corrigée qu'à l'état suivant, donc la créature oscillait.
                // L'autorité veut que rien de local ne la déplace : on remet la vitesse à zéro à chaque fois.
                var body = entry.Creature.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                applied++;
            }

            return applied;
        }

        /// <summary>
        /// Relâche les créatures dont l'état n'arrive plus : leur IA reprend, sinon elles resteraient
        /// figées sur une position que plus personne ne met à jour.
        /// </summary>
        public void Update()
        {
            if (_adopted.Count == 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            List<Adopted> stale = null;

            foreach (var entry in _adopted.Values)
            {
                // Vérification différée de l'arrêt de l'IA : le jeu peut avoir mis la désinscription en
                // file d'attente, auquel cas l'index est encore positif à l'instant de l'appel.
                if (!entry.AiVerified && entry.Creature != null)
                {
                    var index = CreatureIdentity.ScheduledUpdateIndex(entry.Creature);
                    if (index <= 0)
                    {
                        entry.AiVerified = true;
                        entry.AiDisabled = true;

                        // Le délai de vérification est journalisé : le jeu peut différer la
                        // désinscription, et c'est pendant ce délai que la copie locale bouge encore. Sans
                        // ce chiffre, l'écart « avant coupure » resterait sans explication.
                        _log.LogInfo(
                            $"[créature] IA arrêtée (vérifiée) : {entry.Label} — index relu={index} " +
                            $"(0 = absente du planificateur du jeu), {now - entry.AdoptionRealtime:F2}s après " +
                            $"l'adoption" +
                            (entry.MaxPendingCutDriftMeters > 0f
                                ? $", pendant lesquelles la copie avait bougé de {entry.MaxPendingCutDriftMeters:F2}m"
                                : string.Empty) +
                            ".");
                    }
                    else if (!entry.AiFailureLogged && now - entry.AdoptionRealtime > 0.5f)
                    {
                        entry.AiFailureLogged = true;
                        _log.LogError(
                            $"[créature] IA TOUJOURS ACTIVE après 0,5 s : {entry.Label} — index={index}. " +
                            "Cette créature est simulée des deux côtés et divergera.");
                    }
                }

                // Le glissement se fait **chaque image**, avant tout jugement de fraîcheur : une créature
                // qui vient de recevoir un état doit avancer maintenant, pas au prochain rapport.
                AdvanceInterpolation(entry, now);

                var creatureGone = entry.Creature == null;
                var stateExpired = now - entry.LastStateRealtime > Protocol.CreatureAdoptionTimeoutSeconds;

                if (!creatureGone && !stateExpired)
                {
                    continue;
                }

                if (stale == null)
                {
                    stale = new List<Adopted>();
                }

                stale.Add(entry);

                var reason = creatureGone
                    ? "objet détruit"
                    : $"plus d'état depuis {now - entry.LastStateRealtime:F1}s";
                var restored = TryRestoreAi(entry);
                _log.LogInfo(
                    $"[créature] relâchée : {entry.Label} ({reason}) — l'IA reprend chez ce client " +
                    $"(index relu > 0 : {restored}).");
            }

            if (stale == null)
            {
                return;
            }

            foreach (var entry in stale)
            {
                _adopted.Remove(entry.Key);
                _releases++;
            }
        }

        /// <summary>
        /// Rend toutes les créatures adoptées à leur IA. Appelé à la déconnexion : c'est la seule
        /// façon de ne pas laisser le monde du client à moitié figé après un serveur disparu.
        /// </summary>
        public void ReleaseAll(string reason)
        {
            if (_adopted.Count == 0)
            {
                return;
            }

            var restored = 0;
            foreach (var entry in _adopted.Values)
            {
                if (TryRestoreAi(entry))
                {
                    restored++;
                }
            }

            _log.LogInfo(
                $"[créature] {_adopted.Count} créature(s) adoptée(s) relâchée(s) ({reason}) — " +
                $"{restored} IA rétablie(s).");
            _adopted.Clear();
        }

        /// <summary>Résumé d'une ligne, pour le journal périodique et pour une sonde.</summary>
        public string Describe() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "adoptées={0} (IA arrêtée={1}) snapshots={2} états={3} appariés={4} clé inconnue={5} " +
                "adoptions de loin={6} adoptions={7} relâchées={8} clés locales={9} (sans clé={10}) " +
                "clés partagées max={13} dérive max={11:F2}m sur {19:F2}s ({14}, dont mouvement local " +
                "≤{22:F2}m sur {23}), écart avant coupure vérifiée={20:F2}m ({21}), interruption max={15:F1}s ({16}), " +
                "écart après interruption={17:F2}m ({18}), 1re distance max={12:F2}m",
                _adopted.Count,
                AiDisabledCount,
                _snapshotsReceived,
                _statesReceived,
                _statesMatched,
                _statesUnknownKey,
                _adoptionsFarFromServer,
                _adoptions,
                _releases,
                _registry.Count,
                _unkeyedRegistry,
                MaxDriftMeters,
                MaxFirstDistanceMeters,
                _maxRegistryCollisions,
                MaxDriftLabel,
                MaxGapSeconds,
                MaxGapLabel,
                MaxGapDriftMeters,
                MaxGapDriftLabel,
                MaxDriftGapSeconds,
                MaxPendingCutDriftMeters,
                MaxPendingCutDriftLabel,
                MaxLocalJitterMeters,
                MaxLocalJitterLabel)
            + ", " + DescribeFrameSteps();

        /// <summary>Une ligne par créature adoptée : c'est ce qu'une sonde peut vérifier.</summary>
        public IReadOnlyList<string> DescribeAdopted()
        {
            var lines = new List<string>();
            foreach (var entry in _adopted.Values)
            {
                var currentIndex = entry.Creature != null
                    ? CreatureIdentity.ScheduledUpdateIndex(entry.Creature)
                    : int.MinValue;
                var position = entry.Creature != null ? entry.Creature.transform.position : Vector3.zero;

                lines.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} clé={1:x16} indexIA={2} (avant {3}) états={4} dériveMax={5:F3}m " +
                    "1reDistance={6:F2}m position={7} serveur={8}",
                    entry.Label,
                    entry.Key,
                    currentIndex == int.MinValue ? "illisible" : currentIndex.ToString(CultureInfo.InvariantCulture),
                    entry.IndexBefore == int.MinValue ? "illisible" : entry.IndexBefore.ToString(CultureInfo.InvariantCulture),
                    entry.StatesReceived,
                    entry.MaxDriftMeters,
                    entry.FirstDistanceMeters,
                    position.ToString("F2"),
                    entry.LastServerPosition.ToString("F2")));
            }

            return lines;
        }

        /// <summary>Journalise le résumé toutes les 5 s : un run doit se lire, pas se déduire.</summary>
        public void ReportPeriodically()
        {
            var now = Time.realtimeSinceStartup;
            if (now < _nextReport)
            {
                return;
            }

            _nextReport = now + 5f;

            if (_snapshotsReceived == 0)
            {
                return;
            }

            _log.LogInfo($"[créature] état : {Describe()}");
        }

        /// <summary>
        /// Décrit la créature du registre **la plus proche** d'une position annoncée, avec sa propre
        /// position d'apparition.
        ///
        /// Pourquoi cette ligne existe : « clé absente de ce monde » a deux causes opposées — la créature
        /// n'est pas chargée chez ce client (une question de cellules), ou elle y est mais **sous une autre
        /// clé** (une question d'identité). Les deux produisent le même compteur, et sans cette description
        /// on corrigerait la mauvaise. La position d'apparition est affichée parce que c'est exactement ce
        /// qui entre dans la clé : si elle diffère de celle du pair, la clé ne peut pas correspondre.
        /// </summary>
        private string DescribeNearestLocal(Vector3 position)
        {
            Creature nearest = null;
            var best = float.MaxValue;

            foreach (var pair in _registry)
            {
                var creature = pair.Value;
                if (creature == null || creature.transform == null)
                {
                    continue;
                }

                var distance = Vector3.Distance(creature.transform.position, position);
                if (distance < best)
                {
                    best = distance;
                    nearest = creature;
                }
            }

            if (nearest == null)
            {
                return "registre local vide";
            }

            CreatureIdentity.TryGetSpawnPosition(nearest, out var spawn);
            return string.Format(
                CultureInfo.InvariantCulture,
                "la plus proche du registre : {0}, à {1:F1} m — apparition=({2:F1}, {3:F1}, {4:F1})",
                CreatureIdentity.Describe(nearest),
                best,
                spawn.x,
                spawn.y,
                spawn.z);
        }

        /// <summary>
        /// Apparie une entrée reçue et, si la correspondance tient, arrête l'IA de la créature.
        ///
        /// Le refus est le cœur de cette méthode : une clé peut désigner la mauvaise créature (une
        /// clé de repli calculée sur une position d'apparition, par exemple), et dans ce cas la
        /// position reçue est très loin de la créature locale. Refuser en nommant la distance est
        /// infiniment préférable à un téléport : le second passerait pour une synchronisation qui
        /// marche.
        /// </summary>
        private bool TryAdopt(CreatureStateEntry state, out Adopted entry)
        {
            entry = null;

            if (!_registry.TryGetValue(state.Key, out var creature) || creature == null)
            {
                // Le registre est rafraîchi à la demande : une créature qui vient d'apparaître (ou de
                // sortir de portée) peut manquer à l'appel. Au plus une fois par seconde : sans ce
                // plafond, une clé restée inconnue ferait reconstruire le registre à chaque entrée
                // reçue, soit un balayage de la scène toutes les 100 ms.
                var rebuildAt = Time.realtimeSinceStartup;
                if (rebuildAt >= _nextForcedRebuildRealtime)
                {
                    _nextForcedRebuildRealtime = rebuildAt + ForcedRebuildSeconds;
                    _registryRealtime = float.NegativeInfinity;
                    EnsureRegistry();
                }
                if (!_registry.TryGetValue(state.Key, out creature) || creature == null)
                {
                    _statesUnknownKey++;
                    // Journalisé une fois le registre **peuplé** : les premiers états arrivent avant que le
                    // monde ait enregistré ses créatures, et une ligne « registre local vide » ne dit rien
                    // de l'identité (elle prouve juste qu'on a reçu trop tôt).
                    if (_unknownLogged < 3 && _registry.Count > 0)
                    {
                        _unknownLogged++;
                        var announced = new Vector3(state.X, state.Y, state.Z);
                        _log.LogWarning(
                            $"[créature] clé {state.Key:x16} reçue du serveur mais absente de ce monde " +
                            $"(position annoncée {announced.ToString("F1")}, {_registry.Count} clé(s) locales) " +
                            $"— entrée ignorée ; {DescribeNearestLocal(announced)}");
                    }

                    return false;
                }
            }

            var distance = Vector3.Distance(creature.transform.position, new Vector3(state.X, state.Y, state.Z));
            var farFromServer = distance > DriftAnomalyMeters;
            if (farFromServer)
            {
                _adoptionsFarFromServer++;
                if (_adoptionsFarFromServer <= 5)
                {
                    _log.LogWarning(
                        $"[créature] adoptée DE LOIN : la clé {state.Key:x16} désigne " +
                        $"{CreatureIdentity.Describe(creature)}, à {distance:F1} m de la position annoncée " +
                        $"{new Vector3(state.X, state.Y, state.Z).ToString("F1")}. L'identité est la clé, pas la " +
                        $"proximité : la copie locale avait dérivé (son IA tournait), elle est ramenée à la " +
                        $"position du serveur — la refuser l'aurait laissée dériver pour de bon.");
                }
            }

            // L'IA coupée, il reste une source de mouvement que le jeu ne coupe pas : le Rigidbody.
            // Sans ça, la créature garderait sa vitesse et dériverait entre deux états autoritaires —
            // c'est justement la dérive que la sonde mesure, et la laisser serait un mensonge sur la
            // source du mouvement. Sur un Rigidbody cinématique, l'appel n'a pas de sens : on n'y touche pas.
            var rigidbody = creature.GetComponent<Rigidbody>();
            var velocityZeroed = false;
            if (rigidbody != null && !rigidbody.isKinematic)
            {
                rigidbody.velocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
                velocityZeroed = true;
            }

            var indexBefore = CreatureIdentity.ScheduledUpdateIndex(creature);
            var indexAfter = CreatureIdentity.SetAiScheduled(creature, false);

            var now = Time.realtimeSinceStartup;
            entry = new Adopted
            {
                Key = state.Key,
                Label = CreatureIdentity.Describe(creature),
                Creature = creature,
                AiDisabled = indexAfter <= 0,
                AiVerified = indexAfter <= 0,
                AdoptionRealtime = now,
                IndexBefore = indexBefore,
                IndexAfter = indexAfter,
                LastStateRealtime = now,
                FirstDistanceMeters = distance,
            };

            _adopted[state.Key] = entry;
            _adoptions++;
            MaxFirstDistanceMeters = Mathf.Max(MaxFirstDistanceMeters, distance);

            // Le verdict est explicite : l'index d'IA **relu après l'appel** doit valoir 0. Le journal
            // n'affirme donc pas « IA coupée » parce qu'on a appelé une méthode, mais parce que la
            // planification du jeu ne contient plus la créature.
            _log.LogInfo(
                $"[créature] adoptée : {entry.Label} à {distance:F2} m de la position serveur — " +
                $"IA coupée index {Format(indexBefore)} -> {Format(indexAfter)} " +
                $"(0 = retirée du planificateur du jeu), lue après l'appel : " +
                $"{Format(CreatureIdentity.ScheduledUpdateIndex(creature))}" +
                (velocityZeroed ? ", vitesse du Rigidbody remise à zéro" : ", aucun Rigidbody à freiner"));

            if (!entry.AiVerified)
            {
                _log.LogWarning(
                    "[créature] index d'IA encore positif juste après la demande d'arrêt : le jeu l'a " +
                    "probablement mise en file d'attente. La vérification est reconduite à la frame suivante.");
            }

            return true;
        }

        /// <summary>
        /// Reconstruit le registre local des clés autour du joueur.
        ///
        /// Les créatures sont cherchées **y compris inactives** : `Resources.FindObjectsOfTypeAll`
        /// filtré aux objets de scène, exactement comme `SaveGuardSelfTestBehaviour` le fait déjà pour
        /// le menu pause. `FindObjectsOfType`, qui ignore l'inactivité, ne voit rien d'un monde dont
        /// les cellules ne sont pas encore réveillées — et un monde en cours de chargement ressemble
        /// alors à un monde sans créatures, ce qui est précisément le piège dans lequel la première
        /// mesure est tombée.
        /// </summary>
        private void EnsureRegistry()
        {
            var now = Time.realtimeSinceStartup;
            var interval = _registrySettled ? RegistryRefreshSeconds : RegistryUnsettledRefreshSeconds;
            if (now - _registryRealtime < interval)
            {
                return;
            }

            _registryRealtime = now;
            _registry.Clear();
            _unkeyedRegistry = 0;
            _registryCollisions = 0;
            var previousKeyCount = _lastRegistryKeyCount;
            var previousUnkeyed = _lastRegistryUnkeyed;
            var previousCollisions = _lastRegistryCollisions;
            var collisionExamples = new List<string>();

            var origin = Player.mainObject != null ? Player.mainObject.transform.position : Vector3.zero;
            var radiusSquared = RegistryRadiusMeters * RegistryRadiusMeters;
            var candidates = Resources.FindObjectsOfTypeAll<Creature>();
            var inScene = 0;
            var active = 0;
            var unkeyedNames = new List<string>();

            for (var i = 0; i < candidates.Length; i++)
            {
                var creature = candidates[i];
                if (creature == null || creature.transform == null)
                {
                    continue;
                }

                // `scene.IsValid()` écarte les prefabs chargés depuis les paquets d'assets : ils
                // portent eux aussi un composant `Creature`, mais ils ne sont pas dans le monde, et
                // leur position n'a aucun sens.
                if (creature.gameObject == null || !creature.gameObject.scene.IsValid())
                {
                    continue;
                }

                inScene++;
                if (creature.gameObject.activeInHierarchy && creature.enabled)
                {
                    active++;
                }

                if (Player.mainObject != null &&
                    (creature.transform.position - origin).sqrMagnitude > radiusSquared)
                {
                    continue;
                }

                var key = CreatureIdentity.KeyOf(creature, out _);
                if (key == 0L)
                {
                    _unkeyedRegistry++;
                    if (unkeyedNames.Count < 5)
                    {
                        unkeyedNames.Add(creature.gameObject.name);
                    }

                    continue;
                }

                // Premier arrivé : deux créatures qui partagent une clé ne peuvent pas être
                // distinguées par elle. Le registre garde la première, et le nombre d'ambiguïtés est
                // **compté** — c'est cette mesure qui dit si « une clé désigne une créature » tient.
                if (_registry.TryGetValue(key, out var alreadyThere))
                {
                    _registryCollisions++;
                    if (collisionExamples.Count < 3 && alreadyThere != null)
                    {
                        collisionExamples.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "'{0}' / '{1}' à {2:F2} m",
                            alreadyThere.gameObject != null ? alreadyThere.gameObject.name : "?",
                            creature.gameObject.name,
                            Vector3.Distance(alreadyThere.transform.position, creature.transform.position)));
                    }

                    continue;
                }

                _registry.Add(key, creature);
            }

            _lastRegistryKeyCount = _registry.Count;
            _lastRegistryUnkeyed = _unkeyedRegistry;
            _lastRegistryCollisions = _registryCollisions;
            _maxRegistryCollisions = Math.Max(_maxRegistryCollisions, _registryCollisions);

            // Deux relevés consécutifs au même compte, non nul : le monde a fini de peupler le registre. Un
            // compte nul veut dire « pas de monde » (menu, chargement, cellule déchargée) : on reste alors en
            // reconstruction rapide, pour rattraper le prochain chargement sans attendre 2 s.
            _registrySettled = _registry.Count > 0 && _registry.Count == previousKeyCount;

            // Journalisé **seulement quand le registre change** : un relevé toutes les 2 s noierait le
            // journal, alors que ce qui compte est de voir apparaître une créature (ou une clé
            // manquante). La première construction est toujours journalisée, elle.
            var changed = _lastRegistryKeyCount != previousKeyCount
                || _lastRegistryUnkeyed != previousUnkeyed
                || _lastRegistryCollisions != previousCollisions;
            if (!changed && _registryLogged)
            {
                return;
            }

            _registryLogged = true;
            _log.LogInfo(
                $"[créature] registre local : {_registry.Count} clé(s) dans {RegistryRadiusMeters:F0} m — " +
                $"{inScene} créature(s) en scène dont {active} active(s), au point {origin.ToString("F1")} " +
                $"(scène '{SceneManager.GetActiveScene().name}', échelle de temps {Time.timeScale:F1})" +
                (_unkeyedRegistry == 0
                    ? string.Empty
                    : $", {_unkeyedRegistry} sans clé (ex. : {string.Join(", ", unkeyedNames)})") +
                (_registryCollisions == 0
                    ? ", aucune clé partagée entre deux créatures"
                    : $", {_registryCollisions} clé(s) PARTAGÉE(S) entre deux créatures " +
                      $"(ex. : {string.Join(", ", collisionExamples)})"));
        }

        /// <summary>
        /// Avance d'une image le segment d'interpolation d'une créature adoptée.
        ///
        /// Pourquoi une méthode séparée : c'est la boucle d'image, et elle doit rester lisible à côté de la
        /// mesure. Le pas est compté **sur la copie réelle** (`transform.position` avant/après), pas sur la
        /// valeur théorique d'`alpha` : si un autre système déplaçait la créature, la mesure le verrait.
        ///
        /// Les images plus longues que <see cref="LongFrameSeconds"/> sont comptées à part et exclues du
        /// maximum : un chargement d'assets de `80 ms` produit mécaniquement un grand pas sans que
        /// l'interpolation soit en cause. Elles restent dans la moyenne et dans le journal — les sortir de
        /// la mesure sans les nommer serait les cacher.
        /// </summary>
        private void AdvanceInterpolation(Adopted entry, float now)
        {
            if (entry.Creature == null || entry.StatesReceived == 0)
            {
                return;
            }

            var elapsed = now - entry.SegmentStart;
            var alpha = entry.SegmentDuration > 0f
                ? Mathf.Clamp01(elapsed / entry.SegmentDuration)
                : 1f;

            var target = alpha >= 1f ? entry.To : Vector3.Lerp(entry.From, entry.To, alpha);
            var targetRotation = alpha >= 1f
                ? entry.ToRotation
                : Quaternion.Slerp(entry.FromRotation, entry.ToRotation, alpha);

            var current = entry.Creature.transform.position;
            var step = Vector3.Distance(current, target);
            var delta = Time.unscaledDeltaTime;

            entry.Creature.transform.position = target;
            entry.Creature.transform.rotation = targetRotation;

            // Seules les images **pendant** un segment décrivent un mouvement : une fois arrivée, la copie
            // attend l'annonce suivante, et compter ces zéros ferait baisser une moyenne qui doit décrire
            // le glissement.
            if (alpha >= 1f || step <= 0f)
            {
                return;
            }

            _frameSteps++;
            _frameStepSum += step;

            if (delta > LongFrameSeconds)
            {
                _longFrameSteps++;
                _maxLongFrameStepMeters = Mathf.Max(_maxLongFrameStepMeters, step);
                return;
            }

            if (step > _maxFrameStepMeters)
            {
                _maxFrameStepMeters = step;
                _maxFrameStepDelta = delta;
                _maxFrameStepLabel = entry.Label;
            }

            if (step >= 0.5f)
            {
                _frameStepsAboveHalfMeter++;
            }
        }

        /// <summary>
        /// Remet une créature dans le planificateur du jeu. L'opération est journalisée par l'appelant
        /// avec la valeur relue, jamais supposée.
        /// </summary>
        private bool TryRestoreAi(Adopted entry)
        {
            if (entry.Creature == null)
            {
                return false;
            }

            var index = CreatureIdentity.SetAiScheduled(entry.Creature, true);
            return index > 0;
        }

        private static string Format(int index) =>
            index == int.MinValue ? "illisible" : index.ToString(CultureInfo.InvariantCulture);
    }
}
