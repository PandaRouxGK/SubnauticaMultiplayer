using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class EntityDefinitionEdgeTests
    {
        [Fact]
        public void La_forme_canonique_est_epinglee()
        {
            // Changer cette chaîne change le hash de toutes les définitions : c'est une
            // modification de protocole, pas un détail d'implémentation.
            Assert.Equal(
                "content-v1|1|SeaMoth|1000|1000|1000|0",
                EntityDefinition.ForVehicle("SeaMoth").Canonical());
        }

        [Fact]
        public void Le_hash_est_celui_de_la_forme_canonique()
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");

            Assert.Equal(StableHash.Of(definition.Canonical()), definition.ComputeHash());
        }

        [Fact]
        public void Un_nom_nul_devient_vide_et_change_le_hash()
        {
            var empty = EntityDefinition.ForVehicle(null);
            var named = EntityDefinition.ForVehicle("SeaMoth");

            Assert.Equal(string.Empty, empty.TechTypeName);
            Assert.NotEqual(named.ComputeHash(), empty.ComputeHash());
        }

        [Fact]
        public void La_revision_et_les_flags_changent_le_hash()
        {
            var reference = EntityDefinition.ForVehicle("SeaMoth");

            var otherRevision = EntityDefinition.ForVehicle("SeaMoth");
            otherRevision.Revision = 2;

            var otherFlags = EntityDefinition.ForVehicle("SeaMoth");
            otherFlags.Flags = 1;

            Assert.NotEqual(reference.ComputeHash(), otherRevision.ComputeHash());
            Assert.NotEqual(reference.ComputeHash(), otherFlags.ComputeHash());
        }

        [Fact]
        public void Une_echelle_negative_est_quantifiee_et_distincte_de_la_positive()
        {
            var negative = EntityDefinition.ForVehicle("SeaMoth");
            negative.ScaleX = -1.5f;

            var positive = EntityDefinition.ForVehicle("SeaMoth");
            positive.ScaleX = 1.5f;

            Assert.Contains("|-1500|", negative.Canonical());
            Assert.NotEqual(negative.ComputeHash(), positive.ComputeHash());
        }

        [Fact]
        public void Le_millimetre_est_arrondi_et_distingue_deux_echelles_proches()
        {
            var low = EntityDefinition.ForVehicle("SeaMoth");
            low.ScaleX = 1.0004f;

            var high = EntityDefinition.ForVehicle("SeaMoth");
            high.ScaleX = 1.0006f;

            Assert.Contains("|1000|", low.Canonical());
            Assert.Contains("|1001|", high.Canonical());
            Assert.NotEqual(low.ComputeHash(), high.ComputeHash());
        }

        [Fact]
        public void Une_echelle_de_mille_millions_ne_collisionne_pas_avec_son_opposee()
        {
            var positive = EntityDefinition.ForVehicle("SeaMoth");
            positive.ScaleX = 1_000_000_000f;

            var negative = EntityDefinition.ForVehicle("SeaMoth");
            negative.ScaleX = -1_000_000_000f;

            Assert.Contains("|1000000000000|", positive.Canonical());
            Assert.Contains("|-1000000000000|", negative.Canonical());
            Assert.NotEqual(positive.ComputeHash(), negative.ComputeHash());
        }

        [Fact]
        public void Une_echelle_hors_du_long_sature_mais_garde_le_signe()
        {
            var positive = EntityDefinition.ForVehicle("SeaMoth");
            positive.ScaleX = float.MaxValue;

            var negative = EntityDefinition.ForVehicle("SeaMoth");
            negative.ScaleX = -float.MaxValue;

            Assert.NotEqual(positive.Canonical(), negative.Canonical());
            Assert.NotEqual(positive.ComputeHash(), negative.ComputeHash());
        }

        [Fact]
        public void Une_echelle_non_finie_est_traitee_comme_zero_sans_lever()
        {
            var nan = EntityDefinition.ForVehicle("SeaMoth");
            nan.ScaleX = float.NaN;
            nan.ScaleY = float.PositiveInfinity;
            nan.ScaleZ = float.NegativeInfinity;

            Assert.Contains("|0|0|0|", nan.Canonical());
            Assert.Equal(StableHash.Of(nan.Canonical()), nan.ComputeHash());
        }

        [Fact]
        public void Le_contrat_de_contenu_accepte_deux_cents_definitions_aleatoires_et_refuse_une_echelle_falsifiee()
        {
            var random = new System.Random(20261019);

            for (var i = 0; i < 200; i++)
            {
                var name = new string('T', random.Next(0, 12));
                var definition = EntityDefinition.ForVehicle(name);
                definition.ScaleX = (float)(0.5 + random.NextDouble() * 2.5);
                definition.ScaleY = (float)(0.5 + random.NextDouble() * 2.5);
                definition.ScaleZ = (float)(0.5 + random.NextDouble() * 2.5);

                var spawn = new EntitySpawnMessage
                {
                    Key = i + 1,
                    UniqueId = "id-" + i,
                    TechTypeName = definition.TechTypeName,
                    RotW = 1f,
                    ScaleX = definition.ScaleX,
                    ScaleY = definition.ScaleY,
                    ScaleZ = definition.ScaleZ,
                    DefinitionHash = definition.ComputeHash(),
                };

                Assert.True(EntityDefinition.Verify(
                    spawn.BuildDefinition(), spawn.DefinitionHash, out var computed));
                Assert.Equal(spawn.DefinitionHash, computed);

                var tampered = spawn.BuildDefinition();
                tampered.ScaleX += 0.01f;
                Assert.False(EntityDefinition.Verify(tampered, spawn.DefinitionHash, out _));
            }
        }

        [Fact]
        public void Verify_refuse_un_hash_nul_pour_une_definition_reelle()
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");

            Assert.NotEqual(0L, definition.ComputeHash());
            Assert.False(EntityDefinition.Verify(definition, 0L, out var computed));
            Assert.Equal(definition.ComputeHash(), computed);
        }
    }
}
