using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// La règle qui a corrigé les bonds de créatures à trois clients (§53.2). Elle était enfouie dans
    /// `RemoteCreatureManager`, donc non testable ; ces cas la fixent, y compris le cas du serveur
    /// redémarré qui ne doit pas condamner le client au silence.
    /// </summary>
    public sealed class SnapshotTickGateTests
    {
        [Fact]
        public void Un_tick_croissant_est_accepte()
        {
            var gate = new SnapshotTickGate();

            Assert.True(gate.TryAccept(100));
            Assert.True(gate.TryAccept(101));
            Assert.Equal(101, gate.LastAccepted);
            Assert.Equal(0, gate.StaleCount);
        }

        [Fact]
        public void Un_tick_egal_ou_plus_ancien_est_refuse_et_compte()
        {
            var gate = new SnapshotTickGate();
            gate.TryAccept(100);

            Assert.False(gate.TryAccept(100));
            Assert.False(gate.TryAccept(99));
            Assert.Equal(100, gate.LastAccepted);
            Assert.Equal(2, gate.StaleCount);
        }

        [Fact]
        public void Un_recul_de_plus_de_deux_mille_ticks_est_lu_comme_un_redemarrage()
        {
            var gate = new SnapshotTickGate();
            gate.TryAccept(50000);

            Assert.True(gate.TryAccept(1));
            Assert.Equal(1, gate.LastAccepted);
            Assert.Equal(0, gate.StaleCount);
        }

        [Fact]
        public void Juste_avant_la_tolerance_un_recul_reste_du_retard()
        {
            var gate = new SnapshotTickGate();
            gate.TryAccept(50000);

            Assert.False(gate.TryAccept(50000 - SnapshotTickGate.RestartToleranceTicks + 1));
            Assert.Equal(50000, gate.LastAccepted);
            Assert.Equal(1, gate.StaleCount);
        }

        [Fact]
        public void Un_tick_nul_est_accepte_sans_deplacer_le_dernier_tick()
        {
            var gate = new SnapshotTickGate();
            gate.TryAccept(42);

            Assert.True(gate.TryAccept(0));
            Assert.True(gate.TryAccept(-5));
            Assert.Equal(42, gate.LastAccepted);
            Assert.Equal(0, gate.StaleCount);
        }

        [Fact]
        public void Le_compteur_d_etats_perimes_ne_depend_pas_de_l_ordre_des_rejets()
        {
            var gate = new SnapshotTickGate();

            // Un état accepté, puis trois rejets, puis un état encore plus récent.
            Assert.True(gate.TryAccept(10));
            Assert.False(gate.TryAccept(9));
            Assert.False(gate.TryAccept(8));
            Assert.False(gate.TryAccept(7));
            Assert.True(gate.TryAccept(20));

            Assert.Equal(3, gate.StaleCount);
            Assert.Equal(20, gate.LastAccepted);
        }
    }
}
