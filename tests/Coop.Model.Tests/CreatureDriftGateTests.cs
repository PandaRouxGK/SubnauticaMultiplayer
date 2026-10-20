using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// M3 / W : le garde qui empêche de mesurer une dérive pendant le glissement. Chaque cas correspond au
    /// comportement mesuré le 2026-10-20 (§60), pas à une hypothèse.
    /// </summary>
    public sealed class CreatureDriftGateTests
    {
        [Fact]
        public void Un_segment_termine_permet_la_mesure()
        {
            // Annonces à 10 Hz, segment de 0,1 s : la copie est arrivée quand la suivante tombe.
            Assert.True(CreatureDriftGate.SegmentFinished(0.1f, 0.1f));
            Assert.True(CreatureDriftGate.SegmentFinished(0.12f, 0.1f));
        }

        [Fact]
        public void Un_glissement_inacheve_interdit_la_mesure()
        {
            // Segment de 0,5 s (rattrapage après une rafale) et annonce 0,1 s plus tard : la copie est
            // encore en route, l'écart mesuré serait le retard d'interpolation.
            Assert.False(CreatureDriftGate.SegmentFinished(0.1f, 0.5f));
        }

        [Fact]
        public void La_tolerance_couvre_une_image_longue_sans_couvrir_un_segment_entier()
        {
            // Juste sous la fin : une image longue (≈15 ms) ne doit pas faire perdre la paire.
            Assert.True(CreatureDriftGate.SegmentFinished(0.085f, 0.1f));
            // Nettement avant la fin : la paire est écartée.
            Assert.False(CreatureDriftGate.SegmentFinished(0.06f, 0.1f));
        }

        [Fact]
        public void Sans_segment_il_n_y_a_rien_a_attendre()
        {
            Assert.True(CreatureDriftGate.SegmentFinished(0f, 0f));
            Assert.True(CreatureDriftGate.SegmentFinished(0.1f, -1f));
        }

        [Fact]
        public void Une_entree_non_finie_n_est_pas_mesurable()
        {
            Assert.False(CreatureDriftGate.SegmentFinished(float.NaN, 0.1f));
            Assert.False(CreatureDriftGate.SegmentFinished(-0.1f, 0.1f));
            Assert.False(CreatureDriftGate.SegmentFinished(float.PositiveInfinity, 0.1f));
        }
    }
}
