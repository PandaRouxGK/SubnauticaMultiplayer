using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// v20 : les deux messages du débit. Ce qui compte ici n'est pas l'aller-retour heureux, mais les
    /// paquets **forgés** : une quantité nulle, une issue inconnue, ou un accord qui contredit l'issue
    /// doivent être refusés à la lecture, pas interprétés.
    /// </summary>
    public sealed class InventoryDebitTests
    {
        [Fact]
        public void Un_debit_fait_un_aller_retour_complet()
        {
            var message = new InventoryDebitMessage
            {
                PlayerId = 3,
                TechTypeName = "PowerCell",
                Count = 2,
            };

            var bytes = MessageCodec.Encode(message.Write);
            var reader = new PacketReader(bytes);
            Assert.Equal((byte)MessageType.InventoryDebit, reader.ReadByte());

            var received = InventoryDebitMessage.Read(reader);

            Assert.Equal(3, received.PlayerId);
            Assert.Equal("PowerCell", received.TechTypeName);
            Assert.Equal(2, received.Count);
        }

        [Fact]
        public void Un_debit_hors_bornes_est_refuse_a_l_ecriture()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventoryDebitMessage
            {
                TechTypeName = "PowerCell",
                Count = 0,
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventoryDebitMessage
            {
                TechTypeName = "PowerCell",
                Count = -1,
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventoryDebitMessage
            {
                TechTypeName = "PowerCell",
                Count = Protocol.MaxInventoryStack + 1,
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventoryDebitMessage
            {
                TechTypeName = string.Empty,
                Count = 1,
            }.Write));
        }

        [Fact]
        public void Un_debit_recu_avec_quantite_nulle_est_refuse()
        {
            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.InventoryDebit);
            forged.WriteUInt16(1);
            forged.WriteString("PowerCell");
            forged.WriteUInt16(0);

            var reader = new PacketReader(forged.BackingBuffer, 0, forged.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => InventoryDebitMessage.Read(reader));
        }

        [Fact]
        public void Un_resultat_accorde_fait_un_aller_retour_complet()
        {
            var message = new InventoryDebitResultMessage
            {
                TechTypeName = "PowerCell",
                Count = 1,
                Granted = true,
                Reason = InventoryChangeOutcome.Applied,
                Revision = 2,
                ClaimedBy = 1,
            };

            var bytes = MessageCodec.Encode(message.Write);
            var reader = new PacketReader(bytes);
            Assert.Equal((byte)MessageType.InventoryDebitResult, reader.ReadByte());

            var received = InventoryDebitResultMessage.Read(reader);

            Assert.Equal("PowerCell", received.TechTypeName);
            Assert.Equal(1, received.Count);
            Assert.True(received.Granted);
            Assert.Equal(InventoryChangeOutcome.Applied, received.Reason);
            Assert.Equal(2, received.Revision);
            Assert.Equal(1, received.ClaimedBy);
        }

        [Fact]
        public void Un_resultat_refuse_porte_l_issue_nommee()
        {
            var received = InventoryDebitResultMessage.Read(ReadBack(new InventoryDebitResultMessage
            {
                TechTypeName = "PowerCell",
                Count = 1,
                Granted = false,
                Reason = InventoryChangeOutcome.UnknownItem,
                Revision = 1,
                ClaimedBy = 1,
            }.Write));

            Assert.False(received.Granted);
            Assert.Equal(InventoryChangeOutcome.UnknownItem, received.Reason);
        }

        [Fact]
        public void Un_resultat_qui_contredit_son_issue_est_refuse_a_l_ecriture()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventoryDebitResultMessage
            {
                TechTypeName = "PowerCell",
                Count = 1,
                Granted = true,
                Reason = InventoryChangeOutcome.NotEnough,
                Revision = 1,
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new InventoryDebitResultMessage
            {
                TechTypeName = "PowerCell",
                Count = 1,
                Granted = false,
                Reason = InventoryChangeOutcome.Applied,
                Revision = 1,
            }.Write));
        }

        [Fact]
        public void Un_resultat_recu_avec_issue_inconnue_est_refuse()
        {
            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.InventoryDebitResult);
            forged.WriteString("PowerCell");
            forged.WriteUInt16(1);
            forged.WriteBool(false);
            forged.WriteByte(200);
            forged.WriteInt(1);
            forged.WriteUInt16(1);

            var reader = new PacketReader(forged.BackingBuffer, 0, forged.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => InventoryDebitResultMessage.Read(reader));
        }

        [Fact]
        public void Un_resultat_recu_avec_revision_negative_est_refuse()
        {
            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.InventoryDebitResult);
            forged.WriteString("PowerCell");
            forged.WriteUInt16(1);
            forged.WriteBool(true);
            forged.WriteByte((byte)InventoryChangeOutcome.Applied);
            forged.WriteInt(-1);
            forged.WriteUInt16(1);

            var reader = new PacketReader(forged.BackingBuffer, 0, forged.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => InventoryDebitResultMessage.Read(reader));
        }

        private static PacketReader ReadBack(Action<PacketWriter> write)
        {
            var reader = new PacketReader(MessageCodec.Encode(write));
            reader.ReadByte();
            return reader;
        }
    }
}
