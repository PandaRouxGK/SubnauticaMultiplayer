using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// v21 : le coût d'un craft est prélevé **en une fois**. Ce qui est éprouvé ici est l'atomicité — un
    /// craft dont il manque un matériau ne doit rien coûter — et la forme des nouveaux messages, y compris
    /// leurs refus forgés.
    /// </summary>
    public sealed class PlayerInventoryCraftTests
    {
        private static InventoryEntry[] Cost(params (string Name, int Count)[] items)
        {
            var entries = new InventoryEntry[items.Length];
            for (var index = 0; index < items.Length; index++)
            {
                entries[index] = new InventoryEntry(items[index].Name, items[index].Count);
            }

            return entries;
        }

        [Fact]
        public void Un_cout_complet_est_preleve_et_la_revision_avance_une_fois()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Titanium", 5);
            inventory.Add("Copper", 3);
            var revisionBefore = inventory.Revision;

            var outcome = inventory.RemoveMany(
                Cost(("Titanium", 2), ("Copper", 1)),
                out var missing,
                out _,
                out _);

            Assert.Equal(InventoryChangeOutcome.Applied, outcome);
            Assert.Null(missing);
            Assert.Equal(3, inventory.CountOf("Titanium"));
            Assert.Equal(2, inventory.CountOf("Copper"));
            Assert.Equal(revisionBefore + 1, inventory.Revision);
        }

        [Fact]
        public void Un_materiau_manquant_ne_coute_rien_du_tout()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Titanium", 5);
            var revisionBefore = inventory.Revision;

            var outcome = inventory.RemoveMany(
                Cost(("Titanium", 2), ("Copper", 1)),
                out var missing,
                out var needed,
                out var have);

            Assert.Equal(InventoryChangeOutcome.UnknownItem, outcome);
            Assert.Equal("Copper", missing);
            Assert.Equal(1, needed);
            Assert.Equal(0, have);

            // Le titane n'a **pas** été débité : c'est toute la valeur de l'atomicité.
            Assert.Equal(5, inventory.CountOf("Titanium"));
            Assert.Equal(revisionBefore, inventory.Revision);
        }

        [Fact]
        public void Une_quantite_insuffisante_est_nommee_avec_le_manque()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Copper", 1);

            var outcome = inventory.RemoveMany(
                Cost(("Copper", 3)),
                out var missing,
                out var needed,
                out var have);

            Assert.Equal(InventoryChangeOutcome.NotEnough, outcome);
            Assert.Equal("Copper", missing);
            Assert.Equal(3, needed);
            Assert.Equal(1, have);
            Assert.Equal(1, inventory.CountOf("Copper"));
        }

        [Fact]
        public void Deux_lignes_du_meme_materiau_sont_sommees()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Copper", 2);

            var refused = inventory.RemoveMany(
                Cost(("Copper", 2), ("Copper", 1)),
                out var missing,
                out var needed,
                out _);

            Assert.Equal(InventoryChangeOutcome.NotEnough, refused);
            Assert.Equal("Copper", missing);
            Assert.Equal(3, needed);

            var accepted = inventory.RemoveMany(Cost(("Copper", 1), ("Copper", 1)), out _, out _, out _);
            Assert.Equal(InventoryChangeOutcome.Applied, accepted);
            Assert.Equal(0, inventory.CountOf("Copper"));
        }

        [Fact]
        public void Un_cout_vide_ou_hors_bornes_est_refuse()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Copper", 1);

            Assert.Equal(
                InventoryChangeOutcome.Invalid,
                inventory.RemoveMany(null, out _, out _, out _));

            Assert.Equal(
                InventoryChangeOutcome.Invalid,
                inventory.RemoveMany(Array.Empty<InventoryEntry>(), out _, out _, out _));

            Assert.Equal(
                InventoryChangeOutcome.Invalid,
                inventory.RemoveMany(Cost(("Copper", 0)), out _, out _, out _));

            Assert.Equal(1, inventory.CountOf("Copper"));
        }

        [Fact]
        public void L_adoption_remplace_tout_le_contenu_et_exige_l_ordre()
        {
            var inventory = new PlayerInventory();
            inventory.Add("Zinc", 9);

            Assert.True(inventory.TryReplace(Cost(("Copper", 2), ("Titanium", 10)), out var error), error);
            Assert.Equal(0, inventory.CountOf("Zinc"));
            Assert.Equal(2, inventory.CountOf("Copper"));
            Assert.Equal(10, inventory.CountOf("Titanium"));

            var revision = inventory.Revision;

            Assert.False(inventory.TryReplace(Cost(("Titanium", 1), ("Copper", 1)), out var unordered));
            Assert.Contains("triés ou dupliqués", unordered);

            Assert.False(inventory.TryReplace(Cost(("Copper", 0)), out var zero));
            Assert.Contains("hors bornes", zero);

            Assert.Equal(revision, inventory.Revision);
            Assert.Equal(2, inventory.CountOf("Copper"));
        }

        [Fact]
        public void La_demande_d_adoption_porte_son_numero_et_tient_en_un_paquet()
        {
            var bytes = MessageCodec.Encode(new InventoryAdoptionRequestMessage { Sequence = 1 }.Write);
            var reader = new PacketReader(bytes);

            Assert.Equal((byte)MessageType.InventoryAdoptionRequest, reader.ReadByte());
            Assert.Equal(1, InventoryAdoptionRequestMessage.Read(reader).Sequence);
            Assert.Equal(0, reader.Remaining);

            Assert.Throws<ProtocolException>(() =>
                MessageCodec.Encode(new InventoryAdoptionRequestMessage { Sequence = 0 }.Write));
        }

        [Fact]
        public void Une_adoption_fait_un_aller_retour_et_refuse_le_desordre()
        {
            var message = new InventoryAdoptMessage
            {
                Entries = Cost(("Copper", 2), ("Titanium", 10)),
            };

            var reader = new PacketReader(MessageCodec.Encode(message.Write));
            Assert.Equal((byte)MessageType.InventoryAdopt, reader.ReadByte());

            var received = InventoryAdoptMessage.Read(reader);
            Assert.Equal(2, received.Entries.Length);
            Assert.Equal("Titanium", received.Entries[1].TechTypeName);

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventoryAdoptMessage
            {
                Entries = Cost(("Titanium", 1), ("Copper", 1)),
            }.Write));
        }

        [Fact]
        public void Une_demande_de_craft_fait_un_aller_retour_et_refuse_l_absence_de_type()
        {
            var request = new CraftRequestMessage { RequestId = 7, TechType = 66 };
            var reader = new PacketReader(MessageCodec.Encode(request.Write));

            Assert.Equal((byte)MessageType.CraftRequest, reader.ReadByte());
            var received = CraftRequestMessage.Read(reader);
            Assert.Equal(7, received.RequestId);
            Assert.Equal(66, received.TechType);

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new CraftRequestMessage
            {
                RequestId = 1,
                TechType = 0,
            }.Write));
        }

        [Fact]
        public void Une_demande_de_craft_sans_id_de_requete_est_refusee()
        {
            // `0` est le sentinelle « demande non partie » (SendCraftRequest) : une demande qui le porte ne
            // pourrait pas être appariée à sa réponse.
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new CraftRequestMessage
            {
                RequestId = 0,
                TechType = 66,
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new CraftRequestMessage
            {
                RequestId = -3,
                TechType = 66,
            }.Write));

            // Paquet forgé : id négatif reçu du réseau, refusé plutôt que lu.
            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.CraftRequest);
            forged.WriteInt(-1);
            forged.WriteUInt16(66);

            var reader = new PacketReader(forged.BackingBuffer, 0, forged.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => CraftRequestMessage.Read(reader));
        }

        [Fact]
        public void Un_resultat_de_craft_porter_le_manque_nomme()
        {
            var message = new CraftResultMessage
            {
                RequestId = 7,
                TechType = 66,
                Granted = false,
                Outcome = CraftOutcome.NotEnoughMaterial,
                MissingTechType = 12,
                MissingAmount = 2,
                Revision = 4,
            };

            var reader = new PacketReader(MessageCodec.Encode(message.Write));
            Assert.Equal((byte)MessageType.CraftResult, reader.ReadByte());

            var received = CraftResultMessage.Read(reader);
            Assert.False(received.Granted);
            Assert.Equal(CraftOutcome.NotEnoughMaterial, received.Outcome);
            Assert.Equal(12, received.MissingTechType);
            Assert.Equal(2, received.MissingAmount);
            Assert.Equal(4, received.Revision);
        }

        [Fact]
        public void Un_resultat_incoherent_est_refuse_a_l_ecriture_comme_a_la_lecture()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new CraftResultMessage
            {
                RequestId = 1,
                TechType = 5,
                Granted = true,
                Outcome = CraftOutcome.MissingMaterial,
                MissingTechType = 12,
                MissingAmount = 1,
                Revision = 1,
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new CraftResultMessage
            {
                RequestId = 1,
                TechType = 5,
                Granted = false,
                Outcome = CraftOutcome.MissingMaterial,
                Revision = 1,
            }.Write));

            // Paquet forgé : refus pour matériau sans nommer le matériau.
            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.CraftResult);
            forged.WriteInt(1);
            forged.WriteUInt16(5);
            forged.WriteBool(false);
            forged.WriteByte((byte)CraftOutcome.NotEnoughMaterial);
            forged.WriteUInt16(0);
            forged.WriteUInt16(0);
            forged.WriteInt(1);

            var reader = new PacketReader(forged.BackingBuffer, 0, forged.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => CraftResultMessage.Read(reader));
        }
    }
}
