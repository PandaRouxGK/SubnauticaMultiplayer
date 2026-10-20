using System;

namespace Coop.Model
{
    /// <summary>
    /// Règle d'interpolation d'un véhicule distante, **sans Unity** (P3, M4).
    ///
    /// Pourquoi elle sort du gestionnaire : la décision qui compte — combien de temps étaler un
    /// déplacement entre deux annonces — vivait dans `RemoteVehicleManager`, donc n'était vérifiable
    /// qu'en lançant le jeu. Une régression sur cette durée ne se voit que comme un « pas par image »
    /// trop grand, c'est-à-dire à l'étape J, en 150 secondes. Ici, elle se vérifie en millisecondes.
    ///
    /// Les trois bornes, et ce qu'elles empêchent :
    ///
    /// - **minimale** : deux annonces arrivées dans la même image donnent un intervalle presque nul ;
    ///   diviser par lui ferait passer `alpha` à 1 d'un coup, donc un saut ;
    /// - **maximale** : une annonce manquante allonge l'intervalle mesuré ; au-delà, mieux vaut un
    ///   rattrapage court qu'un glissement de plusieurs secondes qui laisserait le véhicule en retard ;
    /// - **de rattrapage** : un écart de 21 m (paquets livrés en rafale) est étalé sur
    ///   `distance / 40 m/s` au lieu d'une image — c'est le défaut mesuré au §33 (`pas par image max=21,11 m`).
    /// </summary>
    public static class VehicleInterpolation
    {
        /// <summary>Durée minimale (s) d'un segment : évite une division par zéro sur deux états rapprochés.</summary>
        public const float MinSegmentSeconds = 0.04f;

        /// <summary>Durée maximale (s) d'un segment : borne le retard accumulé après une annonce perdue.</summary>
        public const float MaxSegmentSeconds = 0.50f;

        /// <summary>
        /// Vitesse (m/s) au-delà de laquelle un segment est allongé. Le double de la vitesse d'un Seamoth :
        /// plus bas ajouterait du retard sur un mouvement normal, plus haut laisserait repasser un saut.
        /// </summary>
        public const float MaxCatchUpSpeedMetersPerSecond = 40f;

        /// <summary>
        /// Durée (s) sur laquelle étaler un déplacement de <paramref name="distance"/> mètres, l'intervalle
        /// mesuré entre deux annonces étant <paramref name="gap"/>.
        ///
        /// Une valeur non finie ou négative en entrée rend la durée minimale : c'est le cas d'une première
        /// annonce, et inventer un segment long y produirait un glissement depuis une position inconnue.
        /// </summary>
        public static float SegmentDuration(float gap, float distance)
        {
            if (float.IsNaN(gap) || float.IsInfinity(gap) || gap < 0f)
            {
                gap = MinSegmentSeconds;
            }

            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
            {
                distance = 0f;
            }

            var catchUp = distance / MaxCatchUpSpeedMetersPerSecond;
            var duration = Math.Max(gap, catchUp);

            if (duration < MinSegmentSeconds)
            {
                return MinSegmentSeconds;
            }

            return duration > MaxSegmentSeconds ? MaxSegmentSeconds : duration;
        }

        /// <summary>
        /// Avancement `0..1` d'un segment. Un segment de durée nulle vaut 1 : la cible est atteinte, et
        /// diviser par zéro ferait une position `NaN` qui se propagerait à tout le véhicule.
        /// </summary>
        public static float Alpha(float elapsed, float duration)
        {
            if (float.IsNaN(elapsed) || elapsed <= 0f)
            {
                return 0f;
            }

            if (float.IsNaN(duration) || duration <= 0f)
            {
                return 1f;
            }

            var alpha = elapsed / duration;
            return alpha >= 1f ? 1f : alpha;
        }

        /// <summary>
        /// Vrai si deux états se suivent : au-delà de <paramref name="gapSeconds"/>, le véhicule a quitté
        /// le flux puis y est revenu, et l'écart mesuré ne dit plus rien de la synchronisation.
        /// </summary>
        public static bool AreConsecutive(float gapSeconds, float gapLimit)
        {
            if (float.IsNaN(gapSeconds) || gapSeconds < 0f)
            {
                return false;
            }

            return gapSeconds <= gapLimit;
        }
    }
}
