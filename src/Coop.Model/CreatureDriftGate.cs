using System;

namespace Coop.Model
{
    /// <summary>
    /// Décide si un écart de position relevé entre deux annonces de créature **dit quelque chose** de la
    /// synchronisation (M3, P3).
    ///
    /// La règle tient en une phrase : un écart ne mesure la synchronisation que si la copie locale était
    /// **arrivée** sur la position serveur précédente. Sinon elle glissait encore, et l'écart mesuré est
    /// le retard d'interpolation — c'est-à-dire le comportement voulu.
    ///
    /// Ce défaut a rendu l'étape W intermittente le 2026-10-20 : `écart max=2,01 m` pour un seuil de 2 m,
    /// accompagné d'un « mouvement local » de 1,47 m sur 0,10 s, soit 14 m/s pour un `Peeper`. La valeur
    /// n'était pas une créature qui fuit, c'était un glissement inachevé. La règle vit ici pour être
    /// testée en millisecondes, comme <see cref="SnapshotTickGate"/>.
    /// </summary>
    public static class CreatureDriftGate
    {
        /// <summary>
        /// Tolérance (s) : couvre une image longue sans rouvrir la porte à un segment entier (les segments
        /// durent ~0,1 s).
        /// </summary>
        public const float ToleranceSeconds = 0.02f;

        /// <summary>
        /// Vrai si le segment précédent était terminé — donc si l'écart peut être mesuré.
        /// Une durée de segment nulle ou négative vaut « rien à glisser » : la copie est déjà sur la cible.
        /// Les entrées non finies sont traitées comme « pas mesurable » : mieux vaut ne rien compter que
        /// compter une valeur venue d'un `NaN`.
        /// </summary>
        public static bool SegmentFinished(float gapSeconds, float segmentSeconds)
        {
            if (float.IsNaN(segmentSeconds) || segmentSeconds <= 0f)
            {
                return true;
            }

            if (float.IsNaN(gapSeconds) || float.IsInfinity(gapSeconds) || gapSeconds < 0f)
            {
                return false;
            }

            return gapSeconds + ToleranceSeconds >= segmentSeconds;
        }
    }
}
