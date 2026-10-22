using System.Collections.Generic;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Le garde de craft (v21) bloque avant consommation puis rejoue sur accord. Deux défauts de la
    /// première version sont figés ici : la demande était indexée par `TechType` (une seconde demande de
    /// la même recette écrasait la première), et le jeton d'approbation l'était aussi (n'importe quelle
    /// instance de cette recette traversait le préfixe). La clé est maintenant l'**id de requête**, le
    /// jeton une **instance**.
    /// </summary>
    public sealed class CraftRequestLedgerTests
    {
        [Fact]
        public void L_accord_rend_l_instance_qui_a_demande()
        {
            var ledger = new CraftRequestLedger();
            Assert.True(ledger.Add(1, 4242, 1520));

            Assert.True(ledger.TryApprove(1, 1520, out var pending));
            Assert.Equal(4242, pending.InstanceId);
            Assert.Equal(1520, pending.TechType);
            Assert.Equal(0, ledger.PendingCount);
            Assert.Equal(1, ledger.ApprovedCount);
        }

        [Fact]
        public void Un_jeton_ne_sert_qu_une_fois()
        {
            var ledger = new CraftRequestLedger();
            ledger.Add(1, 4242, 1520);
            ledger.TryApprove(1, 1520, out _);

            Assert.True(ledger.ConsumeApproval(4242));
            Assert.False(ledger.ConsumeApproval(4242));
            Assert.Equal(0, ledger.ApprovedCount);
        }

        [Fact]
        public void Deux_crafts_de_la_meme_recette_ne_se_recouvrent_pas()
        {
            var ledger = new CraftRequestLedger();
            Assert.True(ledger.Add(1, 100, 1520));
            Assert.True(ledger.Add(2, 200, 1520));

            // La réponse de la **seconde** demande ne doit rejouer que la seconde instance.
            Assert.True(ledger.TryApprove(2, 1520, out var second));
            Assert.Equal(200, second.InstanceId);
            Assert.Equal(1, ledger.PendingCount);
            Assert.False(ledger.ConsumeApproval(100));
            Assert.True(ledger.ConsumeApproval(200));

            // La première est toujours en attente, et son accord rejoue bien la sienne.
            Assert.True(ledger.TryApprove(1, 1520, out var first));
            Assert.Equal(100, first.InstanceId);
        }

        [Fact]
        public void Un_refus_retire_la_demande_sans_armer_de_jeton()
        {
            var ledger = new CraftRequestLedger();
            ledger.Add(7, 55, 1520);

            Assert.True(ledger.TryRefuse(7, out var refused));
            Assert.Equal(55, refused.InstanceId);
            Assert.Equal(0, ledger.PendingCount);
            Assert.False(ledger.ConsumeApproval(55));
        }

        [Fact]
        public void Une_reponse_perimee_ne_rejoue_rien()
        {
            var ledger = new CraftRequestLedger();

            Assert.False(ledger.TryApprove(99, 1520, out _));
            Assert.False(ledger.TryRefuse(99, out _));
            Assert.Equal(0, ledger.ApprovedCount);
        }

        [Fact]
        public void Un_id_deja_connu_ne_remplace_pas_l_instance()
        {
            var ledger = new CraftRequestLedger();
            Assert.True(ledger.Add(3, 111, 1520));
            Assert.False(ledger.Add(3, 222, 1520));

            Assert.True(ledger.TryApprove(3, 1520, out var pending));
            Assert.Equal(111, pending.InstanceId);
        }

        [Fact]
        public void Une_recette_qui_ne_correspond_pas_est_refusee()
        {
            var ledger = new CraftRequestLedger();
            ledger.Add(4, 333, 1520);

            // Réponse incohérente : ni rejeu, ni jeton, et la demande ne reste pas bloquée.
            Assert.False(ledger.TryApprove(4, 16, out _));
            Assert.Equal(0, ledger.PendingCount);
            Assert.False(ledger.ConsumeApproval(333));
        }

        [Fact]
        public void Une_demande_invalide_est_rejetee()
        {
            var ledger = new CraftRequestLedger();

            Assert.False(ledger.Add(0, 10, 1520));
            Assert.False(ledger.Add(-1, 10, 1520));
            Assert.False(ledger.Add(5, 10, 0));
            Assert.False(ledger.Add(6, 0, 1520));
            Assert.Equal(0, ledger.PendingCount);
        }

        [Fact]
        public void La_table_est_bornee()
        {
            var ledger = new CraftRequestLedger();
            for (var index = 1; index <= CraftRequestLedger.MaxPending; index++)
            {
                Assert.True(ledger.Add(index, 1000 + index, 1520));
            }

            Assert.False(ledger.Add(CraftRequestLedger.MaxPending + 1, 9999, 1520));
            Assert.Equal(CraftRequestLedger.MaxPending, ledger.PendingCount);
        }

        [Fact]
        public void Clear_oublie_tout()
        {
            var ledger = new CraftRequestLedger();
            ledger.Add(1, 10, 1520);
            ledger.Add(2, 20, 16);
            ledger.TryApprove(1, 1520, out _);

            ledger.Clear();

            Assert.Equal(0, ledger.PendingCount);
            Assert.Equal(0, ledger.ApprovedCount);
            Assert.False(ledger.ConsumeApproval(10));
            Assert.False(ledger.TryApprove(2, 16, out _));
        }

        [Fact]
        public void Deux_instances_ne_partagent_pas_leur_jeton()
        {
            var ledger = new CraftRequestLedger();
            ledger.Add(1, 10, 1520);
            ledger.Add(2, 20, 1520);
            ledger.TryApprove(1, 1520, out _);

            // Le jeton de 10 n'autorise pas 20.
            Assert.False(ledger.ConsumeApproval(20));
            Assert.True(ledger.ConsumeApproval(10));
        }

        [Fact]
        public void Les_ids_sont_independants_du_tech_type()
        {
            var ledger = new CraftRequestLedger();
            var ids = new List<int>();
            for (var index = 1; index <= 8; index++)
            {
                Assert.True(ledger.Add(index, index, 1520));
                ids.Add(index);
            }

            foreach (var id in ids)
            {
                Assert.True(ledger.TryApprove(id, 1520, out var pending));
                Assert.Equal(id, pending.InstanceId);
            }

            Assert.Equal(0, ledger.PendingCount);
            Assert.Equal(8, ledger.ApprovedCount);
        }
    }
}
