using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class SaveIdentityTests
    {
        private static SaveIdentity Server() =>
            SaveIdentity.Create("slot0000", "aae68629-1111-2222-3333-444455556666", 83031, "Freedom");

        [Fact]
        public void Aller_retour_binaire_conserve_tous_les_champs_et_le_hash()
        {
            var identity = Server();
            var writer = new PacketWriter();
            identity.WriteTo(writer);

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            var read = SaveIdentity.ReadFrom(reader);

            Assert.Equal(identity.Slot, read.Slot);
            Assert.Equal(identity.SessionId, read.SessionId);
            Assert.Equal(identity.ChangeSet, read.ChangeSet);
            Assert.Equal(identity.GameMode, read.GameMode);
            Assert.Equal(identity.Hash, read.Hash);
            Assert.Equal(read.Hash, read.RecomputeHash());
        }

        [Fact]
        public void La_forme_canonique_des_valeurs_normales_est_inchangee()
        {
            Assert.Equal(
                "save-v1|slot0000|aae68629-1111-2222-3333-444455556666|83031|Freedom",
                Server().Canonical());
        }

        [Fact]
        public void Deux_repartitions_de_champs_ne_collisionnent_plus()
        {
            var a = SaveIdentity.Create("a|b", "c", 1, "Freedom");
            var b = SaveIdentity.Create("a", "b|c", 1, "Freedom");

            Assert.NotEqual(a.Hash, b.Hash);
            Assert.NotEqual(a.Canonical(), b.Canonical());
        }

        [Fact]
        public void Un_antislash_et_un_pipe_ne_collisionnent_pas()
        {
            var a = SaveIdentity.Create("a\\|b", "c", 1, "Freedom");
            var b = SaveIdentity.Create("a", "c", 1, "Freedom");

            Assert.NotEqual(a.Hash, b.Hash);
        }

        [Fact]
        public void Le_hash_est_stable_pour_la_meme_partie()
        {
            Assert.Equal(Server().Hash, Server().Hash);
            Assert.NotEqual(Server().Hash, SaveIdentity.Create("slot0001", "x", 1, "Freedom").Hash);
        }

        [Fact]
        public void Un_slot_vide_est_une_empreinte_inconnue()
        {
            var unknown = SaveIdentity.Create(string.Empty, string.Empty, -1, string.Empty);
            Assert.True(unknown.IsUnknown);
            Assert.Contains("inconnue", unknown.Describe());
        }

        [Fact]
        public void Meme_partie_et_changeSet_egal_est_compatible()
        {
            Assert.Null(SaveCompatibility.Explain(Server(), Server()));
        }

        [Fact]
        public void Un_client_plus_recent_est_compatible_car_l_hote_a_sauvegarde()
        {
            var server = Server();
            var client = SaveIdentity.Create(server.Slot, server.SessionId, server.ChangeSet + 1, server.GameMode);

            Assert.Null(SaveCompatibility.Explain(server, client));
        }

        [Fact]
        public void Un_client_plus_ancien_est_refuse_avec_les_deux_changeSet()
        {
            var server = Server();
            var client = SaveIdentity.Create(server.Slot, server.SessionId, server.ChangeSet - 1, server.GameMode);

            var reason = SaveCompatibility.Explain(server, client);

            Assert.NotNull(reason);
            Assert.Contains("plus ancienne", reason);
            Assert.Contains((server.ChangeSet - 1).ToString(), reason);
            Assert.Contains(server.ChangeSet.ToString(), reason);
        }

        [Fact]
        public void Un_autre_slot_est_refuse_et_nomme_les_deux_slots()
        {
            var client = SaveIdentity.Create("slot0007", Server().SessionId, 83031, "Freedom");

            var reason = SaveCompatibility.Explain(Server(), client);

            Assert.NotNull(reason);
            Assert.Contains("sauvegarde différente", reason);
            Assert.Contains("slot0007", reason);
            Assert.Contains("slot0000", reason);
        }

        [Fact]
        public void Une_autre_session_de_sauvegarde_est_refusee()
        {
            var client = SaveIdentity.Create(Server().Slot, "autre-session", 83031, "Freedom");

            var reason = SaveCompatibility.Explain(Server(), client);

            Assert.NotNull(reason);
            Assert.Contains("session de sauvegarde différente", reason);
        }

        [Fact]
        public void Un_autre_mode_de_jeu_est_refuse()
        {
            var client = SaveIdentity.Create(Server().Slot, Server().SessionId, 83031, "Survival");

            var reason = SaveCompatibility.Explain(Server(), client);

            Assert.NotNull(reason);
            Assert.Contains("mode de jeu différent", reason);
            Assert.Contains("Survival", reason);
            Assert.Contains("Freedom", reason);
        }

        [Fact]
        public void Une_empreinte_cliente_absente_est_refusee_nommement()
        {
            var reason = SaveCompatibility.Explain(Server(), null);

            Assert.NotNull(reason);
            Assert.Contains("inconnue", reason);
            Assert.Contains("client", reason);
        }

        [Fact]
        public void Une_empreinte_serveur_absente_est_refusee_nommement()
        {
            var reason = SaveCompatibility.Explain(null, Server());

            Assert.NotNull(reason);
            Assert.Contains("serveur", reason);
        }

        [Fact]
        public void Un_changeSet_egal_est_compatible()
        {
            var server = Server();
            var client = SaveIdentity.Create(server.Slot, server.SessionId, server.ChangeSet, server.GameMode);

            Assert.Null(SaveCompatibility.Explain(server, client));
        }

        [Fact]
        public void Un_client_nettement_plus_recent_est_accepte()
        {
            var server = Server();
            var client = SaveIdentity.Create(server.Slot, server.SessionId, server.ChangeSet + 100, server.GameMode);

            Assert.Null(SaveCompatibility.Explain(server, client));
        }

        [Fact]
        public void Le_mode_est_compare_sans_la_casse()
        {
            var server = Server();
            var client = SaveIdentity.Create(server.Slot, server.SessionId, server.ChangeSet, "freedom");

            Assert.Null(SaveCompatibility.Explain(server, client));
        }

        [Fact]
        public void Le_slot_est_compare_avec_la_casse()
        {
            var server = Server();
            var client = SaveIdentity.Create("Slot0000", server.SessionId, server.ChangeSet, server.GameMode);

            var reason = SaveCompatibility.Explain(server, client);

            Assert.NotNull(reason);
            Assert.Contains("sauvegarde différente", reason);
        }

        [Fact]
        public void Deux_empreintes_absentes_sont_refusees_cote_client_d_abord()
        {
            var reason = SaveCompatibility.Explain(null, null);

            Assert.NotNull(reason);
            Assert.Contains("client", reason);
        }

        [Fact]
        public void Un_champ_serveur_modifie_apres_coup_est_detecte_comme_altere()
        {
            var server = Server();
            server.ChangeSet += 1; // le hash n'a pas été recalculé

            var reason = SaveCompatibility.Explain(server, Server());

            Assert.NotNull(reason);
            Assert.Contains("altérée", reason);
            Assert.Contains("serveur", reason);
        }

        [Fact]
        public void Un_champ_modifie_apres_coup_est_detecte_comme_altere()
        {
            var client = Server();
            client.ChangeSet += 1; // le hash n'a pas été recalculé

            var reason = SaveCompatibility.Explain(Server(), client);

            Assert.NotNull(reason);
            Assert.Contains("altérée", reason);
        }
    }
}
