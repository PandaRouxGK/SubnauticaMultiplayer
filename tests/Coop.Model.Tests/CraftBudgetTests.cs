using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Cadence des demandes de craft (v21) : le coût était borné par l'inventaire, le **rythme** ne l'était
    /// pas. Un seau à jetons par session : 8 en rafale, puis 1 par 500 ms. Le temps est un argument, donc
    /// ces tests sont instantanés et ne dépendent d'aucune horloge.
    /// </summary>
    public sealed class CraftBudgetTests
    {
        [Fact]
        public void La_rafale_est_bornee_et_le_refus_est_date()
        {
            var budget = new CraftBudget();

            for (var index = 0; index < CraftBudget.DefaultBurst; index++)
            {
                Assert.True(budget.TryTake(1000, out var retryAfter), $"jeton {index + 1} refusé");
                Assert.Equal(0, retryAfter);
            }

            Assert.False(budget.TryTake(1000, out var wait));
            Assert.Equal(CraftBudget.DefaultRefillMs, wait);
        }

        [Fact]
        public void Un_jeton_se_recharge_apres_l_intervalle()
        {
            var budget = new CraftBudget(burst: 1, refillMs: 500);
            Assert.True(budget.TryTake(0, out _));
            Assert.False(budget.TryTake(499, out var wait));
            Assert.Equal(1, wait);

            Assert.True(budget.TryTake(500, out var retryAfter));
            Assert.Equal(0, retryAfter);
            Assert.False(budget.TryTake(500, out _));
        }

        [Fact]
        public void Une_recharge_partielle_raccourcit_l_attente()
        {
            var budget = new CraftBudget(burst: 1, refillMs: 400);
            budget.TryTake(0, out _);

            Assert.False(budget.TryTake(150, out var wait));
            Assert.Equal(250, wait);

            Assert.True(budget.TryTake(400, out _));
        }

        [Fact]
        public void Le_seau_ne_depasse_pas_sa_capacite()
        {
            var budget = new CraftBudget(burst: 2, refillMs: 100);
            // Une très longue inactivité ne doit pas accumuler 1 000 jetons.
            budget.TryTake(0, out _);
            budget.TryTake(0, out _);

            Assert.True(budget.TryTake(100_000, out _));
            Assert.True(budget.TryTake(100_000, out _));
            Assert.False(budget.TryTake(100_000, out _));
        }

        [Fact]
        public void Une_horloge_qui_recule_ne_rend_pas_de_jetons()
        {
            var budget = new CraftBudget(burst: 1, refillMs: 100);
            Assert.True(budget.TryTake(10_000, out _));
            Assert.False(budget.TryTake(10_000, out _));

            // Recalage arrière : toujours aucun jeton, et le seau repart de ce point.
            Assert.False(budget.TryTake(5_000, out _));
            Assert.False(budget.TryTake(5_050, out var wait));
            Assert.Equal(50, wait);
            Assert.True(budget.TryTake(5_100, out _));
        }

        [Fact]
        public void Les_parametres_invalides_sont_ramenes_a_un()
        {
            var budget = new CraftBudget(burst: 0, refillMs: 0);
            Assert.Equal(1, budget.Burst);
            Assert.Equal(1, budget.RefillMs);

            Assert.True(budget.TryTake(0, out _));
            Assert.False(budget.TryTake(0, out var wait));
            Assert.Equal(1, wait);
        }

        [Fact]
        public void Le_regime_etabli_tient_huit_en_rafale_puis_cent_vingt_par_minute()
        {
            // Un client qui demande à chaque tick (20 Hz) pendant une minute. Le seau plein donne la
            // rafale, puis la recharge de 1 par 500 ms : 120 jetons en 60 s. C'est la constante que
            // `PLAN.md` §72.3 annonçait sans la mesurer.
            var budget = new CraftBudget();
            var granted = 0;
            var refused = 0;

            for (var tick = 0; tick <= 1_200; tick++)
            {
                if (budget.TryTake(tick * 50L, out var retryAfter))
                {
                    granted++;
                }
                else
                {
                    refused++;
                    Assert.InRange(retryAfter, 1, CraftBudget.DefaultRefillMs);
                }
            }

            Assert.Equal(128, granted);
            Assert.Equal(1_201 - 128, refused);
        }

        [Fact]
        public void Le_refus_de_cadence_fait_l_aller_retour_sur_le_fil()
        {
            var message = new CraftResultMessage
            {
                RequestId = 12,
                TechType = 1520,
                Granted = false,
                Outcome = CraftOutcome.RateLimited,
                MissingTechType = 0,
                MissingAmount = 0,
                Revision = 3,
            };

            var bytes = MessageCodec.Encode(message.Write);
            var reader = new PacketReader(bytes);
            Assert.Equal((byte)MessageType.CraftResult, reader.ReadByte());

            var decoded = CraftResultMessage.Read(reader);

            Assert.Equal(12, decoded.RequestId);
            Assert.Equal(1520, decoded.TechType);
            Assert.False(decoded.Granted);
            Assert.Equal(CraftOutcome.RateLimited, decoded.Outcome);
            Assert.Equal(3, decoded.Revision);
        }
    }
}
