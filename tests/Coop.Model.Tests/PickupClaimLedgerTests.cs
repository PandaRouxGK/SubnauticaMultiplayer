using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Le registre des réclamations porte une décision durable : « cette clé appartient à ce joueur ».
    /// Le défaut du 2026-10-20 (clé tenue au nom d'un joueur déconnecté, objet perdu pour tous) n'était
    /// couvert par aucun test ; ces cas le fixent.
    /// </summary>
    public sealed class PickupClaimLedgerTests
    {
        private const long Key = 0x3d613fd07fc6fef1L;

        [Fact]
        public void Une_cle_libre_est_accordee_au_premier_demandeur()
        {
            var ledger = new PickupClaimLedger();

            Assert.Equal(PickupClaimOutcome.GrantedFirst, ledger.Claim(Key, 2));
            Assert.True(ledger.TryGetHolder(Key, out var holder));
            Assert.Equal(2, holder);
            Assert.Equal(1, ledger.Count);
        }

        [Fact]
        public void Une_cle_tenue_par_un_autre_joueur_est_refusee_sans_changer_de_detenteur()
        {
            var ledger = new PickupClaimLedger();
            ledger.Claim(Key, 2);

            Assert.Equal(PickupClaimOutcome.RefusedOtherHolder, ledger.Claim(Key, 3));
            Assert.True(ledger.TryGetHolder(Key, out var holder));
            Assert.Equal(2, holder);
        }

        [Fact]
        public void Le_meme_joueur_peut_reclamer_sa_propre_cle()
        {
            var ledger = new PickupClaimLedger();
            ledger.Claim(Key, 2);

            Assert.Equal(PickupClaimOutcome.GrantedSameHolder, ledger.Claim(Key, 2));
            Assert.Equal(1, ledger.Count);
        }

        /// <summary>
        /// Le correctif P2 : sans libération, la clé restait refusée au nom d'un joueur qui n'existe
        /// plus — « REFUSÉE (déjà tenue par id 2) » — et l'objet était perdu pour la session entière.
        /// </summary>
        [Fact]
        public void La_deconnexion_libere_les_cles_du_joueur_parti()
        {
            var ledger = new PickupClaimLedger();
            ledger.Claim(Key, 2);
            ledger.Claim(Key + 1, 2);
            ledger.Claim(Key + 2, 3);

            Assert.Equal(2, ledger.Release(2));
            Assert.Equal(1, ledger.Count);
            Assert.False(ledger.TryGetHolder(Key, out _));
            Assert.False(ledger.TryGetHolder(Key + 1, out _));
            Assert.True(ledger.TryGetHolder(Key + 2, out var survivor));
            Assert.Equal(3, survivor);
        }

        [Fact]
        public void Une_cle_liberee_redevient_reclamable_et_rend_son_nouveau_detenteur()
        {
            var ledger = new PickupClaimLedger();
            ledger.Claim(Key, 2);
            ledger.Release(2);

            Assert.Equal(PickupClaimOutcome.GrantedFirst, ledger.Claim(Key, 3));
            Assert.True(ledger.TryGetHolder(Key, out var holder));
            Assert.Equal(3, holder);
        }

        [Fact]
        public void Liberer_un_joueur_sans_reclamation_ne_change_rien()
        {
            var ledger = new PickupClaimLedger();
            ledger.Claim(Key, 3);

            Assert.Equal(0, ledger.Release(2));
            Assert.Equal(1, ledger.Count);
            Assert.Equal(Key, ledger.KeysOf(3)[0]);
        }

        [Fact]
        public void Les_cles_rendues_sont_celles_du_joueur_demande()
        {
            var ledger = new PickupClaimLedger();
            ledger.Claim(Key, 2);
            ledger.Claim(Key + 1, 2);
            ledger.Claim(Key + 2, 3);

            var keys = ledger.KeysOf(2);

            Assert.Equal(2, keys.Count);
            Assert.Contains(Key, keys);
            Assert.Contains(Key + 1, keys);
            Assert.DoesNotContain(Key + 2, keys);
        }
    }
}
