using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class PlayerPlausibilityTests
    {
        private const PlayerActionFlags None = PlayerActionFlags.None;

        [Fact]
        public void Un_deplacement_normal_est_accepte_et_juge()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                0.75f, 0f, 0f,
                0.05,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.False(verdict.Skipped);
            Assert.InRange(verdict.Distance, 0.74f, 0.76f);
        }

        [Fact]
        public void Un_saut_de_cent_metres_en_un_message_est_refuse()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                100f, 0f, 0f,
                0.05,
                None, None);

            Assert.False(verdict.Accepted);
            Assert.Contains("m/s", verdict.Reason);
            Assert.Contains("100.00 m", verdict.Reason);
            Assert.InRange(verdict.ImpliedSpeed, 1999f, 2001f);
        }

        [Fact]
        public void Une_rafale_de_paquets_n_est_pas_jugee()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                0.005,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
            Assert.Contains("rafale", verdict.Reason);
        }

        [Fact]
        public void Un_trou_de_flux_de_trois_secondes_n_est_pas_juge()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                3.0,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
            Assert.Contains("trou de flux", verdict.Reason);
        }

        [Fact]
        public void Un_changement_de_vehicule_est_un_saut_legitime()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                0.05,
                None, PlayerActionFlags.InSeamoth);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
            Assert.Contains("véhicule", verdict.Reason);
        }

        [Fact]
        public void Trente_metres_par_seconde_en_vehicule_est_accepte()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                3f, 0f, 0f,
                0.1,
                PlayerActionFlags.InSeamoth, PlayerActionFlags.InSeamoth);

            Assert.True(verdict.Accepted);
            Assert.False(verdict.Skipped);
            Assert.True(verdict.Mounted);
        }

        [Fact]
        public void Cent_metres_par_seconde_en_vehicule_est_refuse()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                10f, 0f, 0f,
                0.1,
                PlayerActionFlags.InExosuit, PlayerActionFlags.InExosuit);

            Assert.False(verdict.Accepted);
            Assert.True(verdict.Mounted);
            Assert.Contains("m/s", verdict.Reason);
        }

        [Fact]
        public void Une_position_non_finie_est_refusee()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                float.NaN, 0f, 0f,
                0.05,
                None, None);

            Assert.False(verdict.Accepted);
            Assert.Contains("non finie", verdict.Reason);
        }

        [Fact]
        public void Une_position_precedente_non_finie_est_refusee_avec_un_motif()
        {
            var verdict = PlayerPlausibility.Evaluate(
                float.NaN, 0f, 0f,
                1f, 0f, 0f,
                0.05,
                None, None);

            Assert.False(verdict.Accepted);
            Assert.False(verdict.Skipped);
            Assert.Contains("précédente non finie", verdict.Reason);
            Assert.False(float.IsNaN(verdict.Distance));
        }

        [Fact]
        public void Un_intervalle_NaN_est_refuse_avec_un_motif()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                1f, 0f, 0f,
                double.NaN,
                None, None);

            Assert.False(verdict.Accepted);
            Assert.False(verdict.Skipped);
            Assert.Contains("intervalle non fini", verdict.Reason);
            Assert.Contains("NaN", verdict.Reason);
        }

        [Fact]
        public void Un_intervalle_negatif_est_traite_comme_une_rafale()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                -0.05,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
            Assert.Contains("rafale", verdict.Reason);
        }

        [Fact]
        public void Un_passage_Seamoth_vers_Exosuit_est_un_changement_de_vehicule()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                0.05,
                PlayerActionFlags.InSeamoth, PlayerActionFlags.InExosuit);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
            Assert.Contains("véhicule", verdict.Reason);
        }

        [Fact]
        public void Les_bornes_sont_celles_annoncees_par_le_document()
        {
            // Ces valeurs sont citées dans PLAN.md §43.3/§43.5 et docs/authority.md §2 : si elles
            // changent, la documentation doit changer avec elles, et ce test le rappelle.
            Assert.Equal(15f, PlayerPlausibility.MaxSpeedMetersPerSecond);
            Assert.Equal(35f, PlayerPlausibility.MaxMountedSpeedMetersPerSecond);
            Assert.Equal(2f, PlayerPlausibility.GraceMeters);
            Assert.Equal(3, PlayerPlausibility.MaxConsecutiveRejections);
        }
    }
}
