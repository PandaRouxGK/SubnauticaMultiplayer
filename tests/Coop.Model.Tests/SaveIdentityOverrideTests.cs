using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Les overrides servent à **mesurer** les refus P0 (session, mode) sans dupliquer une sauvegarde.
    /// S'ils étaient trop permissifs, le harnais croirait mesurer une divergence qui n'a pas eu lieu ;
    /// s'ils laissaient l'ancien hash, le refus dirait « empreinte altérée » au lieu du motif voulu.
    /// </summary>
    public sealed class SaveIdentityOverrideTests
    {
        private static SaveIdentity Real() => SaveIdentity.Create("slot0000", "aae68629", 3, "Survival");

        [Fact]
        public void L_override_de_session_change_la_session_et_recalcule_le_hash()
        {
            var identity = SaveIdentityOverride.Apply(Real(), "deadbeef", null);

            Assert.Equal("deadbeef", identity.SessionId);
            Assert.Equal("slot0000", identity.Slot);
            Assert.Equal(3, identity.ChangeSet);
            Assert.Equal("Survival", identity.GameMode);
            Assert.Equal(identity.RecomputeHash(), identity.Hash);
        }

        [Fact]
        public void L_override_de_mode_change_le_mode_et_recalcule_le_hash()
        {
            var identity = SaveIdentityOverride.Apply(Real(), null, "Freedom");

            Assert.Equal("Freedom", identity.GameMode);
            Assert.Equal("aae68629", identity.SessionId);
            Assert.Equal(identity.RecomputeHash(), identity.Hash);
        }

        [Fact]
        public void Sans_override_l_empreinte_est_rendue_telle_quelle()
        {
            var real = Real();

            Assert.Same(real, SaveIdentityOverride.Apply(real, null, null));
        }

        [Fact]
        public void Une_empreinte_nulle_reste_nulle()
        {
            Assert.Null(SaveIdentityOverride.Apply(null, "deadbeef", null));
        }

        [Fact]
        public void L_override_de_session_est_bien_vu_comme_une_divergence_nommee()
        {
            var server = Real();
            var client = SaveIdentityOverride.Apply(Real(), "deadbeef", null);

            var reason = SaveCompatibility.Explain(server, client);

            Assert.NotNull(reason);
            Assert.Contains("session de sauvegarde différente", reason);
            Assert.Contains("deadbeef", reason);
        }

        [Fact]
        public void L_override_de_mode_est_bien_vu_comme_une_divergence_nommee()
        {
            var reason = SaveCompatibility.Explain(Real(), SaveIdentityOverride.Apply(Real(), null, "Freedom"));

            Assert.NotNull(reason);
            Assert.Contains("mode de jeu différent", reason);
            Assert.Contains("Freedom", reason);
        }

        [Fact]
        public void L_argument_est_lu_avec_son_nom_exact_et_un_signe_egal()
        {
            Assert.Equal("deadbeef", SaveIdentityOverride.Read(new[] { "-coopsession=deadbeef" }, "-coopsession"));
            Assert.Null(SaveIdentityOverride.Read(new[] { "-coopsessionevil=deadbeef" }, "-coopsession"));
            Assert.Null(SaveIdentityOverride.Read(new[] { "-coopsession" }, "-coopsession"));
            Assert.Null(SaveIdentityOverride.Read(new[] { "-coopsession=" }, "-coopsession"));
        }

        [Fact]
        public void Une_valeur_hors_caracteres_surs_est_ignoree()
        {
            Assert.Null(SaveIdentityOverride.Read(new[] { "-coopsession=a/b" }, "-coopsession"));
            Assert.Null(SaveIdentityOverride.Read(new[] { "-coopsession=..\\slot" }, "-coopsession"));
            Assert.Equal("Survival-2", SaveIdentityOverride.Read(new[] { "-coopgamemode=Survival-2" }, "-coopgamemode"));
        }

        [Fact]
        public void Une_valeur_demesuree_est_ignoree()
        {
            var tooLong = new string('a', Protocol.MaxSaveSessionLength + 1);

            Assert.False(SaveIdentityOverride.IsUsable(tooLong));
            Assert.Null(SaveIdentityOverride.Read(new[] { "-coopsession=" + tooLong }, "-coopsession"));
        }
    }
}
