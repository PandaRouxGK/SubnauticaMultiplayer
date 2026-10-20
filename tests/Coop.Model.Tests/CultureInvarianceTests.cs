using System;
using System.Globalization;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Les empreintes sont recalculées **des deux côtés** : un hash qui dépendrait de la culture de la
    /// machine (virgule décimale, `tr-TR` qui met `I` en minuscule sans point) ferait refuser un client
    /// pourtant identique. Ces tests changent la culture courante et vérifient que rien ne bouge.
    /// </summary>
    public sealed class CultureInvarianceTests
    {
        [Theory]
        [InlineData("tr-TR")]
        [InlineData("fr-FR")]
        [InlineData("ar-SA")]
        public void L_empreinte_d_une_definition_ne_depend_pas_de_la_culture(string culture)
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");
            definition.ScaleX = 1.2345f;
            definition.ScaleY = 0.0005f;
            definition.ScaleZ = 1000.25f;

            var canonical = definition.Canonical();
            var hash = definition.ComputeHash();

            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                Assert.Equal(canonical, definition.Canonical());
                Assert.Equal(hash, definition.ComputeHash());
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void La_forme_canonique_d_une_definition_est_celle_attendue()
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");
            definition.ScaleX = 1.5f;

            // 1.5 → 1500 millimètres, sans séparateur décimal ni signe superflu.
            Assert.Equal("content-v1|1|SeaMoth|1500|1000|1000|0", definition.Canonical());
        }

        [Theory]
        [InlineData("tr-TR")]
        [InlineData("fr-FR")]
        [InlineData("ar-SA")]
        public void L_empreinte_de_sauvegarde_ne_depend_pas_de_la_culture(string culture)
        {
            var identity = SaveIdentity.Create(
                "slot0000",
                "aae68629-1111-2222-3333-444455556666",
                83031,
                "Freedom");

            var canonical = identity.Canonical();
            var hash = identity.RecomputeHash();

            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                Assert.Equal(canonical, identity.Canonical());
                Assert.Equal(hash, identity.RecomputeHash());
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void La_liste_de_mods_ne_depend_pas_de_la_culture()
        {
            var mods = new[] { "Mod.Un", "Mod.Deux" };
            var hash = ModListHash.Compute(mods);

            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                Assert.Equal(hash, ModListHash.Compute(mods));

                // Le tri est ordinal : en `tr-TR`, un tri culturel placerait `I` avant `i` différemment.
                Assert.Equal(
                    ModListHash.Compute(new[] { "I.Mod", "i.mod" }),
                    ModListHash.Compute(new[] { "i.mod", "I.Mod" }));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
