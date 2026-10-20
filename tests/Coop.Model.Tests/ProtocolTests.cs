using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class LaunchModeParserTests
    {
        private static T WithEnvironment<T>(string value, Func<T> action)
        {
            var previous = Environment.GetEnvironmentVariable(LaunchModeParser.EnvironmentVariableName);
            try
            {
                Environment.SetEnvironmentVariable(LaunchModeParser.EnvironmentVariableName, value);
                return action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(LaunchModeParser.EnvironmentVariableName, previous);
            }
        }

        [Theory]
        [InlineData("-coopmode=server", LaunchMode.Server)]
        [InlineData("-coopmode=client", LaunchMode.Client)]
        [InlineData("-coopmode=off", LaunchMode.Off)]
        [InlineData("-COOPMODE=CLIENT", LaunchMode.Client)]
        [InlineData("-coopmode= server ", LaunchMode.Server)]
        public void Le_role_vient_de_l_argument(string argument, LaunchMode expected)
        {
            WithEnvironment(null, () =>
            {
                Assert.Equal(expected, LaunchModeParser.Parse(new[] { argument }));
                return 0;
            });
        }

        [Fact]
        public void Un_argument_invalide_est_ignore_et_l_environnement_prend_le_relais()
        {
            WithEnvironment("client", () =>
            {
                var mode = LaunchModeParser.Parse(new[] { "-coopmode=licorne", "-autre=server" });
                Assert.Equal(LaunchMode.Client, mode);
                return 0;
            });
        }

        [Fact]
        public void L_argument_prime_sur_l_environment()
        {
            WithEnvironment("server", () =>
            {
                Assert.Equal(LaunchMode.Client, LaunchModeParser.Parse(new[] { "-coopmode=client" }));
                return 0;
            });
        }

        [Fact]
        public void Sans_argument_ni_environment_le_role_est_off()
        {
            WithEnvironment(null, () =>
            {
                Assert.Equal(LaunchMode.Off, LaunchModeParser.Parse(null));
                Assert.Equal(LaunchMode.Off, LaunchModeParser.Parse(Array.Empty<string>()));
                return 0;
            });
        }

        [Fact]
        public void Un_argument_sans_egal_est_ignore()
        {
            WithEnvironment(null, () =>
            {
                Assert.Equal(LaunchMode.Off, LaunchModeParser.Parse(new[] { "-coopmode" }));
                return 0;
            });
        }

        [Theory]
        [InlineData("-coopmodeevil=server")]
        [InlineData("-coopmode2=server")]
        [InlineData("x-coopmode=server")]
        [InlineData("-coopmode.server")]
        public void Un_argument_qui_commence_seulement_par_le_nom_est_ignore(string argument)
        {
            WithEnvironment(null, () =>
            {
                Assert.Equal(LaunchMode.Off, LaunchModeParser.Parse(new[] { argument }));
                return 0;
            });
        }

        [Fact]
        public void Un_argument_null_dans_la_liste_ne_fait_pas_echouer_le_parseur()
        {
            WithEnvironment(null, () =>
            {
                Assert.Equal(LaunchMode.Server, LaunchModeParser.Parse(new[] { null, "-coopmode=server" }));
                return 0;
            });
        }
    }

    public sealed class PlayerColorTests
    {
        [Fact]
        public void La_palette_contient_autant_de_triplets_que_d_entrees()
        {
            Assert.Equal(Protocol.PlayerColorCount * 3, Protocol.PlayerColorsRgb.Length);
        }

        [Fact]
        public void Chaque_indice_valide_rend_le_triplet_de_la_palette()
        {
            for (var i = 0; i < Protocol.PlayerColorCount; i++)
            {
                Protocol.PlayerColorAt(i, out var r, out var g, out var b);

                Assert.Equal(Protocol.PlayerColorsRgb[i * 3], r);
                Assert.Equal(Protocol.PlayerColorsRgb[i * 3 + 1], g);
                Assert.Equal(Protocol.PlayerColorsRgb[i * 3 + 2], b);
            }
        }

        [Fact]
        public void Les_huit_couleurs_sont_distinctes_et_dans_l_intervalle()
        {
            var seen = new System.Collections.Generic.HashSet<(byte, byte, byte)>();

            for (var i = 0; i < Protocol.PlayerColorCount; i++)
            {
                Protocol.PlayerColorAt(i, out var r, out var g, out var b);
                seen.Add((r, g, b));
            }

            Assert.Equal(Protocol.PlayerColorCount, seen.Count);
            Assert.All(Protocol.PlayerColorsRgb, component => Assert.InRange(component, (byte)0, (byte)255));
        }

        [Fact]
        public void Les_indices_extremes_d_un_int_sont_ramenes_sans_deborder()
        {
            Protocol.PlayerColorAt(int.MinValue, out var minR, out var minG, out var minB);
            Assert.Equal((Protocol.PlayerColorsRgb[0], Protocol.PlayerColorsRgb[1], Protocol.PlayerColorsRgb[2]),
                (minR, minG, minB));

            Protocol.PlayerColorAt(int.MaxValue, out var maxR, out var maxG, out var maxB);
            var last = (Protocol.PlayerColorCount - 1) * 3;
            Assert.Equal((Protocol.PlayerColorsRgb[last], Protocol.PlayerColorsRgb[last + 1], Protocol.PlayerColorsRgb[last + 2]),
                (maxR, maxG, maxB));
        }

        [Fact]
        public void Un_indice_hors_palette_est_ramene_par_modulo_positif()
        {
            Protocol.PlayerColorAt(Protocol.PlayerColorCount, out var r, out var g, out var b);
            Assert.Equal(Protocol.PlayerColorsRgb[0], r);
            Assert.Equal(Protocol.PlayerColorsRgb[1], g);
            Assert.Equal(Protocol.PlayerColorsRgb[2], b);

            Protocol.PlayerColorAt(-1, out r, out g, out b);
            var last = (Protocol.PlayerColorCount - 1) * 3;
            Assert.Equal(Protocol.PlayerColorsRgb[last], r);
            Assert.Equal(Protocol.PlayerColorsRgb[last + 1], g);
            Assert.Equal(Protocol.PlayerColorsRgb[last + 2], b);
        }
    }
}
