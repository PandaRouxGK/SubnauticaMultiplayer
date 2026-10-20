using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Les comparaisons sont inclusives (`&lt;=` pour la distance, `&lt;` pour les intervalles) :
    /// ces tests épinglent les bornes pour qu'un `&lt;` transformé en `&lt;=` ne passe pas inaperçu.
    /// </summary>
    public sealed class PlayerPlausibilityBoundaryTests
    {
        private const PlayerActionFlags None = PlayerActionFlags.None;

        [Fact]
        public void La_distance_exactement_autorisee_est_acceptee()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                2.75f, 0f, 0f,
                0.05,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.False(verdict.Skipped);
            Assert.Equal(2.75f, verdict.Allowed);
        }

        [Fact]
        public void Un_centimetre_au_dela_de_la_tolerance_est_refuse()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                2.7501f, 0f, 0f,
                0.05,
                None, None);

            Assert.False(verdict.Accepted);
        }

        [Fact]
        public void L_intervalle_minimal_est_inclusif()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                2.3f, 0f, 0f,
                PlayerPlausibility.MinJudgeSeconds,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.False(verdict.Skipped);
            Assert.Equal(2.3f, verdict.Allowed);
        }

        [Fact]
        public void Juste_sous_l_intervalle_minimal_le_saut_n_est_pas_juge()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                PlayerPlausibility.MinJudgeSeconds - 0.0001,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
        }

        [Fact]
        public void L_intervalle_maximal_est_inclusif()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                32f, 0f, 0f,
                PlayerPlausibility.MaxJudgeSeconds,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.False(verdict.Skipped);
            Assert.Equal(32f, verdict.Allowed);
        }

        [Fact]
        public void Juste_au_dela_de_l_intervalle_maximal_le_saut_n_est_pas_juge()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                PlayerPlausibility.MaxJudgeSeconds + 0.0001,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
            Assert.Contains("trou de flux", verdict.Reason);
        }

        [Fact]
        public void Le_seuil_vehicule_est_inclusif_et_un_peu_plus_haut_refuse()
        {
            var accepted = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                5.5f, 0f, 0f,
                0.1,
                PlayerActionFlags.InSeamoth, PlayerActionFlags.InSeamoth);

            Assert.True(accepted.Accepted);
            Assert.Equal(5.5f, accepted.Allowed);

            var refused = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                5.51f, 0f, 0f,
                0.1,
                PlayerActionFlags.InSeamoth, PlayerActionFlags.InSeamoth);

            Assert.False(refused.Accepted);
        }

        [Fact]
        public void Un_intervalle_infini_positif_est_traite_comme_un_trou_de_flux()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                double.PositiveInfinity,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
            Assert.Contains("trou de flux", verdict.Reason);
        }

        [Fact]
        public void Un_intervalle_infini_negatif_est_traite_comme_une_rafale()
        {
            var verdict = PlayerPlausibility.Evaluate(
                0f, 0f, 0f,
                50f, 0f, 0f,
                double.NegativeInfinity,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.True(verdict.Skipped);
            Assert.Contains("rafale", verdict.Reason);
        }

        [Fact]
        public void Une_position_immobile_est_acceptee_sans_etre_skip()
        {
            var verdict = PlayerPlausibility.Evaluate(
                1f, 2f, 3f,
                1f, 2f, 3f,
                0.05,
                None, None);

            Assert.True(verdict.Accepted);
            Assert.False(verdict.Skipped);
            Assert.Equal(0f, verdict.Distance);
            Assert.Equal(0f, verdict.ImpliedSpeed);
        }
    }
}
