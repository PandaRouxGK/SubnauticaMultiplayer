using System;
using System.Globalization;

namespace Coop.Model
{
    /// <summary>
    /// Verdict de plausibilité d'une position de joueur (P1).
    /// </summary>
    public struct PositionVerdict
    {
        /// <summary>La position peut être retenue et diffusée.</summary>
        public bool Accepted;

        /// <summary>La position a été acceptée **sans être jugée** (rafale, trou de flux, changement de véhicule).</summary>
        public bool Skipped;

        /// <summary>Le joueur est (ou était) dans un véhicule : la borne est celle du véhicule.</summary>
        public bool Mounted;

        public float Distance;

        /// <summary>Vitesse implicite du déplacement (`Distance / dt`), en m/s.</summary>
        public float ImpliedSpeed;

        /// <summary>Distance maximale autorisée pour cet intervalle, tolérance comprise.</summary>
        public float Allowed;

        /// <summary>Motif lisible — rempli aussi quand la position est acceptée sans jugement.</summary>
        public string Reason;
    }

    /// <summary>
    /// Garde de plausibilité appliquée aux positions de joueur reçues par le serveur (P1).
    ///
    /// Ce qu'elle est : **une garde anti-téléport**, pas une simulation. Le serveur ne simule pas le
    /// motor du joueur, ses courants, les échelles ou les véhicules ; il ne peut donc pas arbitrer un
    /// désaccord de physique. Il peut refuser un saut qu'aucune vitesse humaine n'explique, renvoyer
    /// la dernière position acceptée, et **compter** combien de fois il le fait. C'est exactement le
    /// vocabulaire du §43.3 : tant que ce taux n'est pas mesuré, le mot « autoritaire » ne s'applique
    /// pas au joueur.
    ///
    /// Les bornes sont volontairement larges (15 m/s à pied/nage, 35 m/s en véhicule) : le but est
    /// d'attraper un saut de dizaines de mètres, pas de juger une accélération. Une téléportation
    /// légitime (console, téléporteur, réapparition) est refusée **au plus trois messages**, puis
    /// resynchronisée par le serveur — refuser pour toujours bloquerait le joueur dans un monde
    /// qu'il a légitimement quitté.
    ///
    /// Ce type est pur (aucune dépendance Unity, aucun socket) : `tests/Coop.Model.Tests` le couvre,
    /// et c'est ce qui permet de régler les bornes sans lancer le jeu.
    /// </summary>
    public static class PlayerPlausibility
    {
        /// <summary>Vitesse maximale retenue hors véhicule (nage ~5 m/s, chute ~10 m/s, marge incluse).</summary>
        public const float MaxSpeedMetersPerSecond = 15f;

        /// <summary>Vitesse maximale retenue dans un véhicule (Seamoth mesuré à 20 m/s).</summary>
        public const float MaxMountedSpeedMetersPerSecond = 35f;

        /// <summary>Tolérance fixe par message : absorbe le bruit de position et l'arrondi du jeu.</summary>
        public const float GraceMeters = 2f;

        /// <summary>En dessous, deux messages sont une rafale : l'intervalle ne mesure rien.</summary>
        public const double MinJudgeSeconds = 0.02;

        /// <summary>Au-dessus, c'est un trou de flux (pause, chargement) : on ne juge pas.</summary>
        public const double MaxJudgeSeconds = 2.0;

        /// <summary>Nombre de refus consécutifs avant d'accepter la position comme resynchronisation.</summary>
        public const int MaxConsecutiveRejections = 3;

        public static bool IsMounted(PlayerActionFlags flags)
        {
            return (flags & (PlayerActionFlags.InSeamoth | PlayerActionFlags.InExosuit)) != 0;
        }

        /// <summary>
        /// Juge un déplacement entre la dernière position **acceptée** et la position reçue.
        /// </summary>
        /// <param name="previousX">Dernière position acceptée.</param>
        /// <param name="deltaSeconds">Temps réel écoulé entre les deux messages (horloge du serveur).</param>
        /// <param name="previousFlags">Drapeaux du message précédent (changement de véhicule = saut légitime).</param>
        public static PositionVerdict Evaluate(
            float previousX, float previousY, float previousZ,
            float nextX, float nextY, float nextZ,
            double deltaSeconds,
            PlayerActionFlags previousFlags,
            PlayerActionFlags nextFlags)
        {
            var mounted = IsMounted(nextFlags) || IsMounted(previousFlags);
            var verdict = new PositionVerdict
            {
                Mounted = mounted,
                Reason = string.Empty,
            };

            if (float.IsNaN(nextX) || float.IsNaN(nextY) || float.IsNaN(nextZ) ||
                float.IsInfinity(nextX) || float.IsInfinity(nextY) || float.IsInfinity(nextZ))
            {
                verdict.Reason = "position non finie (NaN/Infinity)";
                verdict.Accepted = false;
                return verdict;
            }

            // La référence est la dernière position **acceptée** : si elle est elle-même non finie,
            // toute distance calculée serait NaN et le rejet sortirait sans motif. On refuse
            // explicitement plutôt que de laisser un verdict muet.
            if (float.IsNaN(previousX) || float.IsNaN(previousY) || float.IsNaN(previousZ) ||
                float.IsInfinity(previousX) || float.IsInfinity(previousY) || float.IsInfinity(previousZ))
            {
                verdict.Reason = "position précédente non finie (NaN/Infinity) : position reçue refusée";
                verdict.Accepted = false;
                return verdict;
            }

            // NaN n'est ni "trop court" ni "trop long" : sans ce cas, toutes les comparaisons
            // seraient fausses et le message sortirait avec un motif contenant "NaN".
            if (double.IsNaN(deltaSeconds))
            {
                verdict.Reason = "intervalle non fini (NaN) : position refusée";
                verdict.Accepted = false;
                return verdict;
            }

            var dx = (double)nextX - previousX;
            var dy = (double)nextY - previousY;
            var dz = (double)nextZ - previousZ;
            verdict.Distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (deltaSeconds <= 0.0 || deltaSeconds < MinJudgeSeconds)
            {
                verdict.Accepted = true;
                verdict.Skipped = true;
                verdict.Reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "intervalle trop court ({0:F3} s) : rafale, non jugée",
                    deltaSeconds);
                return verdict;
            }

            if (deltaSeconds > MaxJudgeSeconds)
            {
                verdict.Accepted = true;
                verdict.Skipped = true;
                verdict.Reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "intervalle trop long ({0:F2} s) : trou de flux, non jugé",
                    deltaSeconds);
                return verdict;
            }

            // Monter, descendre ou **changer** de véhicule déplace le joueur d'un coup, légitimement.
            // Comparer les bits et non `IsMounted` couvre le passage Seamoth -> Exosuit, que deux
            // booléens « monté » considéreraient à tort comme un déplacement ordinaire.
            const PlayerActionFlags mountedMask = PlayerActionFlags.InSeamoth | PlayerActionFlags.InExosuit;
            if ((previousFlags & mountedMask) != (nextFlags & mountedMask))
            {
                verdict.Accepted = true;
                verdict.Skipped = true;
                verdict.Reason = "changement de véhicule : saut légitime, non jugé";
                return verdict;
            }

            var maxSpeed = mounted ? MaxMountedSpeedMetersPerSecond : MaxSpeedMetersPerSecond;
            verdict.Allowed = (float)(maxSpeed * deltaSeconds) + GraceMeters;
            verdict.ImpliedSpeed = (float)(verdict.Distance / deltaSeconds);

            if (verdict.Distance <= verdict.Allowed)
            {
                verdict.Accepted = true;
                return verdict;
            }

            verdict.Accepted = false;
            verdict.Reason = string.Format(
                CultureInfo.InvariantCulture,
                "déplacement de {0:F2} m en {1:F3} s ({2:F1} m/s) > {3:F1} m/s + {4:F1} m de tolérance ({5:F2} m)",
                verdict.Distance,
                deltaSeconds,
                verdict.ImpliedSpeed,
                maxSpeed,
                GraceMeters,
                verdict.Allowed);

            return verdict;
        }
    }
}
