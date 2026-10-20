using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class SaveSlotPreferenceTests
    {
        [Fact]
        public void Un_argument_exact_est_lu()
        {
            Assert.Equal("slot0001", SaveSlotPreference.Parse(new[] { "Subnautica.exe", "-coopsave=slot0001" }));
        }

        [Fact]
        public void La_casse_de_l_argument_est_ignoree_mais_pas_celle_de_la_valeur()
        {
            Assert.Equal("slot0001", SaveSlotPreference.Parse(new[] { "-COOPSAVE=slot0001" }));
        }

        [Theory]
        [InlineData("-coopsaveevil=slot0001")]
        [InlineData("-coopsave")]
        [InlineData("-coopsave=")]
        [InlineData("coopsave=slot0001")]
        public void Un_argument_qui_n_est_pas_exactement_le_notre_est_ignore(string argument)
        {
            Assert.Null(SaveSlotPreference.Parse(new[] { argument }));
        }

        [Theory]
        [InlineData("slot0001 ")]
        [InlineData(" slot0001")]
        public void Les_espaces_de_bord_sont_retires(string argument)
        {
            Assert.Equal("slot0001", SaveSlotPreference.Parse(new[] { "-coopsave=" + argument }));
        }

        [Theory]
        [InlineData("../slot0001")]
        [InlineData("slot 0001")]
        [InlineData("slot\\0001")]
        [InlineData("slot/0001")]
        [InlineData("")]
        public void Un_nom_de_sauvegarde_invalide_donne_null_sans_repli(string value)
        {
            // Une demande explicite invalide ne doit jamais charger une autre partie : le harnais
            // croirait mesurer un refus P0 alors qu'il aurait ouvert la mauvaise sauvegarde.
            Assert.Null(SaveSlotPreference.Parse(new[] { "-coopsave=" + value }));
        }

        [Fact]
        public void Un_nom_trop_long_est_refuse()
        {
            Assert.Null(SaveSlotPreference.Parse(new[] { "-coopsave=" + new string('s', Protocol.MaxSaveSlotLength + 1) }));
            Assert.Equal(
                new string('s', Protocol.MaxSaveSlotLength),
                SaveSlotPreference.Parse(new[] { "-coopsave=" + new string('s', Protocol.MaxSaveSlotLength) }));
        }

        [Fact]
        public void Une_valeur_presente_dans_l_environnement_sert_de_repli()
        {
            var previous = Environment.GetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName);
            try
            {
                Environment.SetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName, "slot0002");
                Assert.Equal("slot0002", SaveSlotPreference.Parse(Array.Empty<string>()));
            }
            finally
            {
                Environment.SetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName, previous);
            }
        }

        [Fact]
        public void L_argument_prime_sur_l_environnement()
        {
            var previous = Environment.GetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName);
            try
            {
                Environment.SetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName, "slot0002");
                Assert.Equal("slot0003", SaveSlotPreference.Parse(new[] { "-coopsave=slot0003" }));
            }
            finally
            {
                Environment.SetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName, previous);
            }
        }

        [Fact]
        public void Un_argument_explicite_invalide_ne_retombe_pas_sur_l_environnement()
        {
            var previous = Environment.GetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName);
            try
            {
                Environment.SetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName, "slot0002");
                Assert.Null(SaveSlotPreference.Parse(new[] { "-coopsave=../evil" }));
            }
            finally
            {
                Environment.SetEnvironmentVariable(SaveSlotPreference.EnvironmentVariableName, previous);
            }
        }
    }
}
