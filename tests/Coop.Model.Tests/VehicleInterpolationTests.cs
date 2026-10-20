using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// P3, M4 : la règle de durée de segment sort du gestionnaire Unity pour être **rejouée hors du jeu**.
    ///
    /// Chaque cas correspond à un défaut déjà mesuré en jeu, pas à une hypothèse :
    /// - des annonces en rafale donnaient un intervalle presque nul (d'où le plancher) ;
    /// - un rattrapage de 21 m tenait en une image (d'où `distance / 40 m/s`, `pas par image max=21,11 m`
    ///   au §33) ;
    /// - une annonce perdue allongeait le segment au-delà d'une demi-seconde (d'où le plafond).
    /// </summary>
    public sealed class VehicleInterpolationTests
    {
        [Fact]
        public void Un_intervalle_normal_donne_un_segment_de_cet_intervalle()
        {
            // 2 m à 20 m/s : l'intervalle à 10 Hz domine le rattrapage (0,05 s contre 0,05 s).
            Assert.Equal(0.1f, VehicleInterpolation.SegmentDuration(0.1f, 2f), 3);
        }

        [Fact]
        public void Des_annonces_en_rafale_ne_font_pas_un_saut()
        {
            // Intervalle mesuré presque nul : sans plancher, alpha passerait à 1 en une image.
            var duration = VehicleInterpolation.SegmentDuration(0.001f, 0.5f);
            Assert.Equal(VehicleInterpolation.MinSegmentSeconds, duration, 3);
        }

        [Fact]
        public void Un_rattrapage_de_21_metres_est_etale_par_la_vitesse_de_rattrapage()
        {
            var duration = VehicleInterpolation.SegmentDuration(0.01f, 21.11f);

            // 21,11 / 40 = 0,53 s, borné à 0,50 : le saut de 21 m mesuré au §33 ne peut plus tenir
            // en une image, c'est exactement ce que la borne existe pour empêcher.
            Assert.Equal(VehicleInterpolation.MaxSegmentSeconds, duration, 3);
            Assert.True(duration >= 21.11f / VehicleInterpolation.MaxCatchUpSpeedMetersPerSecond - 0.001f
                        || duration == VehicleInterpolation.MaxSegmentSeconds);
        }

        [Fact]
        public void Un_intervalle_trop_long_est_plafonne()
        {
            Assert.Equal(VehicleInterpolation.MaxSegmentSeconds,
                VehicleInterpolation.SegmentDuration(5f, 0f), 3);
        }

        [Fact]
        public void Une_entree_non_finie_ou_negative_retombe_sur_le_plancher()
        {
            Assert.Equal(VehicleInterpolation.MinSegmentSeconds,
                VehicleInterpolation.SegmentDuration(float.NaN, 1f), 3);
            Assert.Equal(VehicleInterpolation.MinSegmentSeconds,
                VehicleInterpolation.SegmentDuration(float.PositiveInfinity, 1f), 3);
            Assert.Equal(VehicleInterpolation.MinSegmentSeconds,
                VehicleInterpolation.SegmentDuration(-1f, 1f), 3);
            // Une distance non finie vaut « aucun déplacement », pas « entrée invalide » : l'intervalle
            // mesuré (0,05 s) reste alors la durée du segment. C'est la seule lecture qui ne fasse pas
            // d'une position illisible un mouvement de 0,04 s à vitesse inconnue.
            Assert.Equal(0.05f, VehicleInterpolation.SegmentDuration(0.05f, float.NaN), 3);
        }

        [Fact]
        public void L_avancement_est_borne_et_ne_divise_jamais_par_zero()
        {
            Assert.Equal(0f, VehicleInterpolation.Alpha(0f, 0.1f), 3);
            Assert.Equal(0.5f, VehicleInterpolation.Alpha(0.05f, 0.1f), 3);
            Assert.Equal(1f, VehicleInterpolation.Alpha(0.2f, 0.1f), 3);
            Assert.Equal(1f, VehicleInterpolation.Alpha(0.05f, 0f), 3);
            Assert.Equal(0f, VehicleInterpolation.Alpha(float.NaN, 0.1f), 3);
        }

        [Fact]
        public void Deux_etats_ne_se_suivent_plus_au_dela_de_la_limite()
        {
            Assert.True(VehicleInterpolation.AreConsecutive(0.1f, 0.5f));
            Assert.True(VehicleInterpolation.AreConsecutive(0.5f, 0.5f));
            Assert.False(VehicleInterpolation.AreConsecutive(0.51f, 0.5f));
            Assert.False(VehicleInterpolation.AreConsecutive(float.NaN, 0.5f));
        }
    }
}
