using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// v19 : le registre serveur de l'inventaire. Les trois règles qui comptent sont celles qui décident
    /// d'une valeur opposable — un ajout ne peut pas dépasser les bornes, un retrait ne peut pas créer
    /// d'unités, et un refus ne change **rien** (ni contenu, ni révision).
    /// </summary>
    public sealed class PlayerInventoryTests
    {
        [Fact]
        public void Un_ajout_puis_un_retrait_laissent_un_registre_vide()
        {
            var inventory = new PlayerInventory();

            Assert.Equal(InventoryChangeOutcome.Applied, inventory.Add("Titanium", 2));
            Assert.Equal(2, inventory.CountOf("Titanium"));
            Assert.Equal(1, inventory.Revision);

            Assert.Equal(InventoryChangeOutcome.Applied, inventory.Remove("Titanium", 2));
            Assert.Equal(0, inventory.CountOf("Titanium"));
            Assert.Equal(0, inventory.Count);
            Assert.Equal(2, inventory.Revision);
        }

        [Fact]
        public void Un_retrait_trop_gros_est_refuse_sans_rien_changer()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Titanium", 1);

            Assert.Equal(InventoryChangeOutcome.NotEnough, inventory.Remove("Titanium", 2));
            Assert.Equal(1, inventory.CountOf("Titanium"));
            Assert.Equal(1, inventory.Revision);
        }

        [Fact]
        public void Un_retrait_d_un_objet_absent_est_refuse()
        {
            var inventory = new PlayerInventory();

            Assert.Equal(InventoryChangeOutcome.UnknownItem, inventory.Remove("Titanium", 1));
            Assert.Equal(0, inventory.Revision);
        }

        [Fact]
        public void Un_ajout_hors_bornes_est_refuse()
        {
            var inventory = new PlayerInventory();

            Assert.Equal(InventoryChangeOutcome.Invalid, inventory.Add(string.Empty, 1));
            Assert.Equal(InventoryChangeOutcome.Invalid, inventory.Add(null, 1));
            Assert.Equal(InventoryChangeOutcome.Invalid, inventory.Add(new string('x', Protocol.MaxTechTypeNameLength + 1), 1));
            Assert.Equal(InventoryChangeOutcome.Invalid, inventory.Add("Titanium", 0));
            Assert.Equal(InventoryChangeOutcome.Invalid, inventory.Add("Titanium", -3));
            Assert.Equal(0, inventory.Count);
            Assert.Equal(0, inventory.Revision);
        }

        [Fact]
        public void La_pile_est_bornee_et_le_depassement_est_refuse()
        {
            var inventory = new PlayerInventory();

            Assert.Equal(
                InventoryChangeOutcome.InventoryFull,
                inventory.Add("Titanium", Protocol.MaxInventoryStack + 1));
            Assert.Equal(0, inventory.Count);

            Assert.Equal(InventoryChangeOutcome.Applied, inventory.Add("Titanium", Protocol.MaxInventoryStack));
            Assert.Equal(
                InventoryChangeOutcome.InventoryFull,
                inventory.Add("Titanium", 1));
            Assert.Equal(Protocol.MaxInventoryStack, inventory.CountOf("Titanium"));
            Assert.Equal(1, inventory.Revision);
        }

        [Fact]
        public void Le_plafond_d_objets_distincts_est_respecte()
        {
            var inventory = new PlayerInventory();

            for (var i = 0; i < Protocol.MaxInventoryEntries; i++)
            {
                Assert.Equal(InventoryChangeOutcome.Applied, inventory.Add($"Objet{i:D2}", 1));
            }

            Assert.Equal(InventoryChangeOutcome.InventoryFull, inventory.Add("UnDeTrop", 1));
            Assert.Equal(Protocol.MaxInventoryEntries, inventory.Count);
            Assert.Equal(Protocol.MaxInventoryEntries, inventory.Revision);
        }

        [Fact]
        public void L_instantane_est_trie_et_ne_contient_pas_de_ligne_vide()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Zinc", 1);
            inventory.Add("Bladderfish", 3);
            inventory.Add("Copper", 2);
            inventory.Remove("Zinc", 1);

            var snapshot = inventory.Snapshot();

            Assert.Equal(2, snapshot.Length);
            Assert.Equal("Bladderfish", snapshot[0].TechTypeName);
            Assert.Equal(3, snapshot[0].Count);
            Assert.Equal("Copper", snapshot[1].TechTypeName);
            Assert.Equal(2, snapshot[1].Count);
        }

        [Fact]
        public void L_instantane_ne_partage_pas_le_tableau_interne()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Titanium", 1);

            var first = inventory.Snapshot();
            first[0] = new InventoryEntry("Falsifie", 42);

            Assert.Equal("Titanium", inventory.Snapshot()[0].TechTypeName);
        }

        [Fact]
        public void Un_aller_retour_d_instantane_conserve_la_revision_et_le_contenu()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Titanium", 4);
            inventory.Add("Copper", 1);

            var message = new InventorySnapshotMessage
            {
                Revision = inventory.Revision,
                Entries = inventory.Snapshot(),
            };

            var bytes = MessageCodec.Encode(message.Write);
            var reader = new PacketReader(bytes);
            Assert.Equal((byte)MessageType.InventorySnapshot, reader.ReadByte());

            var received = InventorySnapshotMessage.Read(reader);

            Assert.Equal(inventory.Revision, received.Revision);
            Assert.Equal(2, received.Entries.Length);
            Assert.Equal("Copper", received.Entries[0].TechTypeName);
            Assert.Equal(1, received.Entries[0].Count);
            Assert.Equal("Titanium", received.Entries[1].TechTypeName);
            Assert.Equal(4, received.Entries[1].Count);
        }

        [Fact]
        public void Un_instantane_desordonne_est_refuse_a_l_ecriture()
        {
            var message = new InventorySnapshotMessage
            {
                Revision = 1,
                Entries = new[]
                {
                    new InventoryEntry("Zinc", 1),
                    new InventoryEntry("Copper", 1),
                },
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(message.Write));
        }

        [Fact]
        public void Un_instantane_avec_doublon_ou_quantite_nulle_est_refuse()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventorySnapshotMessage
            {
                Revision = 1,
                Entries = new[]
                {
                    new InventoryEntry("Copper", 1),
                    new InventoryEntry("Copper", 2),
                },
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventorySnapshotMessage
            {
                Revision = 1,
                Entries = new[] { new InventoryEntry("Copper", 0) },
            }.Write));
        }

        [Fact]
        public void Un_instantane_recu_avec_revision_negative_est_refuse()
        {
            // Paquet forgé : opcode, révision négative, aucune ligne.
            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.InventorySnapshot);
            forged.WriteInt(-1);
            forged.WriteByte(0);

            var reader = new PacketReader(forged.BackingBuffer, 0, forged.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => InventorySnapshotMessage.Read(reader));
        }

        [Fact]
        public void Un_instantane_recu_avec_trop_de_lignes_est_refuse()
        {
            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.InventorySnapshot);
            forged.WriteInt(0);
            forged.WriteByte((byte)(Protocol.MaxInventoryEntries + 1));

            var reader = new PacketReader(forged.BackingBuffer, 0, forged.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => InventorySnapshotMessage.Read(reader));
        }
    }
}
