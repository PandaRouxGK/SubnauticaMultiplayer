using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Chaque nom transporté est borné à `MaxTechTypeNameLength` (64). La borne doit être appliquée
    /// des **deux** côtés : un serveur ne doit pas pouvoir émettre ce qu'un client refusera.
    /// </summary>
    public sealed class MessageStringLimitTests
    {
        private static PacketReader SkipOpcode(PacketWriter writer)
        {
            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();
            return reader;
        }

        [Fact]
        public void PickupClaim_accepte_64_caracteres_et_refuse_65()
        {
            var name = new string('x', Protocol.MaxTechTypeNameLength);
            var accepted = new PickupClaimMessage { PlayerId = 1, ItemKey = 1L, TechTypeName = name };
            var read = PickupClaimMessage.Read(SkipOpcode(Write(w => accepted.Write(w))));
            Assert.Equal(name, read.TechTypeName);

            var tooLong = new PickupClaimMessage
            {
                PlayerId = 1,
                ItemKey = 1L,
                TechTypeName = name + "x",
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooLong.Write));

            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.PickupClaim);
            forged.WriteUInt16(1);
            forged.WriteLong(1L);
            forged.WriteString(name + "x");
            Assert.Throws<ProtocolException>(() => PickupClaimMessage.Read(SkipOpcode(forged)));
        }

        [Fact]
        public void PlayerEquipment_accepte_64_caracteres_et_refuse_65()
        {
            var name = new string('x', Protocol.MaxTechTypeNameLength);
            var accepted = new PlayerEquipmentMessage { PlayerId = 1, TechTypeNames = new[] { name } };
            var read = PlayerEquipmentMessage.Read(SkipOpcode(Write(w => accepted.Write(w))));
            Assert.Equal(name, read.TechTypeNames[0]);

            var tooLong = new PlayerEquipmentMessage
            {
                PlayerId = 1,
                TechTypeNames = new[] { name + "x" },
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooLong.Write));

            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.PlayerEquipment);
            forged.WriteUInt16(1);
            forged.WriteByte(1);
            forged.WriteString(name + "x");
            Assert.Throws<ProtocolException>(() => PlayerEquipmentMessage.Read(SkipOpcode(forged)));
        }

        [Fact]
        public void PlayerHeldItem_accepte_un_parametre_de_64_caracteres_et_refuse_65()
        {
            var name = new string('x', Protocol.MaxTechTypeNameLength);
            var accepted = new PlayerHeldItemMessage
            {
                PlayerId = 1,
                TechTypeName = "Welder",
                HoldingParams = new[] { name },
            };
            var read = PlayerHeldItemMessage.Read(SkipOpcode(Write(w => accepted.Write(w))));
            Assert.Equal(name, read.HoldingParams[0]);

            var tooLong = new PlayerHeldItemMessage
            {
                PlayerId = 1,
                TechTypeName = "Welder",
                HoldingParams = new[] { name + "x" },
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooLong.Write));

            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.PlayerHeldItem);
            forged.WriteUInt16(1);
            forged.WriteString("Welder");
            forged.WriteByte(1);
            forged.WriteString(name + "x");
            Assert.Throws<ProtocolException>(() => PlayerHeldItemMessage.Read(SkipOpcode(forged)));
        }

        [Fact]
        public void EntitySpawn_accepte_un_TechType_de_64_caracteres_et_refuse_65()
        {
            var name = new string('x', Protocol.MaxTechTypeNameLength);
            var definition = EntityDefinition.ForVehicle(name);
            var accepted = new EntitySpawnMessage
            {
                Key = 1L,
                UniqueId = "id",
                TechTypeName = name,
                RotW = 1f,
                DefinitionHash = definition.ComputeHash(),
            };
            var read = EntitySpawnMessage.Read(SkipOpcode(Write(w => accepted.Write(w))));
            Assert.Equal(name, read.TechTypeName);

            var tooLong = new EntitySpawnMessage
            {
                Key = 1L,
                UniqueId = "id",
                TechTypeName = name + "x",
                RotW = 1f,
                DefinitionHash = definition.ComputeHash(),
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooLong.Write));

            var forged = new PacketWriter();
            forged.WriteByte((byte)MessageType.EntitySpawn);
            forged.WriteLong(1L);
            forged.WriteString("id");
            forged.WriteString(name + "x");
            Assert.Throws<ProtocolException>(() => EntitySpawnMessage.Read(SkipOpcode(forged)));
        }

        private static PacketWriter Write(Action<PacketWriter> write)
        {
            var writer = new PacketWriter();
            write(writer);
            return writer;
        }
    }
}
