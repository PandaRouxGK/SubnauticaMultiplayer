using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class EntityDefinitionTests
    {
        [Fact]
        public void La_forme_canonique_est_deterministe()
        {
            var a = EntityDefinition.ForVehicle("SeaMoth");
            var b = EntityDefinition.ForVehicle("SeaMoth");

            Assert.Equal(a.Canonical(), b.Canonical());
            Assert.Equal(a.ComputeHash(), b.ComputeHash());
        }

        [Fact]
        public void Une_echelle_au_sous_millimetre_ne_change_pas_le_hash()
        {
            var a = EntityDefinition.ForVehicle("SeaMoth");
            var b = EntityDefinition.ForVehicle("SeaMoth");
            b.ScaleX = 1.0004f;

            Assert.Equal(a.ComputeHash(), b.ComputeHash());
        }

        [Fact]
        public void Un_centimetre_d_echelle_change_le_hash()
        {
            var a = EntityDefinition.ForVehicle("SeaMoth");
            var b = EntityDefinition.ForVehicle("SeaMoth");
            b.ScaleX = 1.01f;

            Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
        }

        [Fact]
        public void Verify_accepte_le_hash_de_la_definition()
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");

            var ok = EntityDefinition.Verify(definition, definition.ComputeHash(), out var computed);

            Assert.True(ok);
            Assert.Equal(definition.ComputeHash(), computed);
        }

        [Fact]
        public void Verify_refuse_une_definition_falsifiee_et_rend_le_hash_recalcule()
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");
            var declared = definition.ComputeHash();

            definition.ScaleX = 2f; // falsification après coup
            var ok = EntityDefinition.Verify(definition, declared, out var computed);

            Assert.False(ok);
            Assert.NotEqual(declared, computed);
            Assert.Equal(definition.ComputeHash(), computed);
        }

        [Fact]
        public void Verify_refuse_une_definition_nulle()
        {
            var ok = EntityDefinition.Verify(null, 123L, out var computed);

            Assert.False(ok);
            Assert.Equal(0L, computed);
        }

        [Fact]
        public void ForVehicle_met_l_echelle_a_un()
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");

            Assert.Equal(1f, definition.ScaleX);
            Assert.Equal(1f, definition.ScaleY);
            Assert.Equal(1f, definition.ScaleZ);
            Assert.Equal(EntityDefinition.CurrentRevision, definition.Revision);
        }
    }
}
