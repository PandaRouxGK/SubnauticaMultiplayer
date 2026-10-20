using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class MessageLimitTests
    {
        private static PacketReader Encode(Action<PacketWriter> write)
        {
            var reader = new PacketReader(MessageCodec.Encode(write));
            reader.ReadByte();
            return reader;
        }

        [Fact]
        public void Le_snapshot_de_joueurs_accepte_le_plafond_et_refuse_au_dela_a_l_ecriture()
        {
            var accepted = new PlayersSnapshotMessage
            {
                ServerTick = 1L,
                Players = FilledPlayers(Protocol.MaxClients),
            };

            var read = PlayersSnapshotMessage.Read(Encode(accepted.Write));
            Assert.Equal(Protocol.MaxClients, read.Players.Length);

            var tooMany = new PlayersSnapshotMessage
            {
                ServerTick = 1L,
                Players = FilledPlayers(Protocol.MaxClients + 1),
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooMany.Write));
        }

        [Fact]
        public void Le_snapshot_de_joueurs_refuse_un_compte_forge_au_dela_du_plafond()
        {
            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.PlayersSnapshot);
            writer.WriteLong(1L);
            writer.WriteByte((byte)(Protocol.MaxClients + 1));

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => PlayersSnapshotMessage.Read(reader));
        }

        [Fact]
        public void L_equipement_accepte_trente_deux_pieces_et_refuse_la_trente_troisieme()
        {
            var accepted = new PlayerEquipmentMessage
            {
                PlayerId = 1,
                TechTypeNames = FilledNames(Protocol.MaxEquipmentSlots),
            };
            var read = PlayerEquipmentMessage.Read(Encode(accepted.Write));
            Assert.Equal(Protocol.MaxEquipmentSlots, read.TechTypeNames.Length);

            var tooMany = new PlayerEquipmentMessage
            {
                PlayerId = 1,
                TechTypeNames = FilledNames(Protocol.MaxEquipmentSlots + 1),
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooMany.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.PlayerEquipment);
            writer.WriteUInt16(1);
            writer.WriteByte((byte)(Protocol.MaxEquipmentSlots + 1));

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => PlayerEquipmentMessage.Read(reader));
        }

        [Fact]
        public void L_objet_tenu_accepte_huit_parametres_et_refuse_le_neuvieme()
        {
            var accepted = new PlayerHeldItemMessage
            {
                PlayerId = 1,
                TechTypeName = "Welder",
                HoldingParams = FilledNames(Protocol.MaxActionParams),
            };
            var read = PlayerHeldItemMessage.Read(Encode(accepted.Write));
            Assert.Equal(Protocol.MaxActionParams, read.HoldingParams.Length);

            var tooMany = new PlayerHeldItemMessage
            {
                PlayerId = 1,
                TechTypeName = "Welder",
                HoldingParams = FilledNames(Protocol.MaxActionParams + 1),
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooMany.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.PlayerHeldItem);
            writer.WriteUInt16(1);
            writer.WriteString("Welder");
            writer.WriteByte((byte)(Protocol.MaxActionParams + 1));

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => PlayerHeldItemMessage.Read(reader));
        }

        [Fact]
        public void Le_snapshot_de_creatures_accepte_seize_entrees_et_refuse_la_dix_septieme()
        {
            var accepted = new CreatureSnapshotMessage
            {
                ServerTick = 1L,
                Creatures = FilledCreatures(Protocol.MaxCreatureStates),
            };
            var read = CreatureSnapshotMessage.Read(Encode(accepted.Write));
            Assert.Equal(Protocol.MaxCreatureStates, read.Creatures.Length);

            var tooMany = new CreatureSnapshotMessage
            {
                ServerTick = 1L,
                Creatures = FilledCreatures(Protocol.MaxCreatureStates + 1),
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooMany.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.CreatureSnapshot);
            writer.WriteLong(1L);
            writer.WriteUInt16((ushort)(Protocol.MaxCreatureStates + 1));

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => CreatureSnapshotMessage.Read(reader));
        }

        [Fact]
        public void Le_snapshot_de_vehicules_accepte_seize_entrees_et_refuse_la_dix_septieme()
        {
            var accepted = new VehicleSnapshotMessage
            {
                ServerTick = 1L,
                Vehicles = FilledVehicles(Protocol.MaxVehicleStates),
            };
            var read = VehicleSnapshotMessage.Read(Encode(accepted.Write));
            Assert.Equal(Protocol.MaxVehicleStates, read.Vehicles.Length);

            var tooMany = new VehicleSnapshotMessage
            {
                ServerTick = 1L,
                Vehicles = FilledVehicles(Protocol.MaxVehicleStates + 1),
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooMany.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.VehicleSnapshot);
            writer.WriteLong(1L);
            writer.WriteByte((byte)(Protocol.MaxVehicleStates + 1));

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => VehicleSnapshotMessage.Read(reader));
        }

        [Fact]
        public void Le_snapshot_de_creatures_ne_depasse_pas_la_taille_d_un_paquet_non_fiable()
        {
            var message = new CreatureSnapshotMessage
            {
                ServerTick = long.MaxValue,
                Creatures = FilledCreatures(Protocol.MaxCreatureStates),
            };

            var bytes = MessageCodec.Encode(message.Write);

            Assert.True(
                bytes.Length <= Protocol.MaxUnreliablePayloadBytes,
                $"{bytes.Length} octets > {Protocol.MaxUnreliablePayloadBytes}");
        }

        [Fact]
        public void L_identifiant_d_entite_accepte_soixante_quatre_caracteres_et_refuse_le_suivant()
        {
            var accepted = SpawnWithId(new string('i', Protocol.MaxEntityIdLength));
            var read = EntitySpawnMessage.Read(Encode(accepted.Write));
            Assert.Equal(accepted.UniqueId, read.UniqueId);

            var tooLong = SpawnWithId(new string('i', Protocol.MaxEntityIdLength + 1));
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooLong.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.EntitySpawn);
            writer.WriteLong(1L);
            writer.WriteString(new string('i', Protocol.MaxEntityIdLength + 1));

            var forged = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            forged.ReadByte();
            Assert.Throws<ProtocolException>(() => EntitySpawnMessage.Read(forged));
        }

        [Fact]
        public void Le_motif_de_correction_accepte_la_limite_et_refuse_au_dela()
        {
            var accepted = new PlayerCorrectionMessage
            {
                ServerTick = 1L,
                X = 1f, Y = 2f, Z = 3f,
                Reason = new string('x', Protocol.MaxCorrectionReasonLength),
            };
            var read = PlayerCorrectionMessage.Read(Encode(accepted.Write));
            Assert.Equal(Protocol.MaxCorrectionReasonLength, read.Reason.Length);

            var tooLong = new PlayerCorrectionMessage
            {
                ServerTick = 1L,
                Reason = new string('x', Protocol.MaxCorrectionReasonLength + 1),
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooLong.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.PlayerCorrection);
            writer.WriteLong(1L);
            writer.WriteFloat(1f);
            writer.WriteFloat(2f);
            writer.WriteFloat(3f);
            writer.WriteString(new string('x', Protocol.MaxCorrectionReasonLength + 1));

            var forged = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            forged.ReadByte();
            Assert.Throws<ProtocolException>(() => PlayerCorrectionMessage.Read(forged));
        }

        [Fact]
        public void Le_motif_de_disparition_accepte_la_limite_et_refuse_au_dela()
        {
            var accepted = new EntityDespawnMessage
            {
                Key = 1L,
                Kind = EntityKind.Item,
                Reason = new string('x', Protocol.MaxDespawnReasonLength),
            };
            var read = EntityDespawnMessage.Read(Encode(accepted.Write));
            Assert.Equal(Protocol.MaxDespawnReasonLength, read.Reason.Length);

            var tooLong = new EntityDespawnMessage
            {
                Key = 1L,
                Kind = EntityKind.Item,
                Reason = new string('x', Protocol.MaxDespawnReasonLength + 1),
            };
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(tooLong.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.EntityDespawn);
            writer.WriteLong(1L);
            writer.WriteByte(EntityKind.Item);
            writer.WriteString(new string('x', Protocol.MaxDespawnReasonLength + 1));

            var forged = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            forged.ReadByte();
            Assert.Throws<ProtocolException>(() => EntityDespawnMessage.Read(forged));
        }

        private static PlayerStateEntry[] FilledPlayers(int count)
        {
            var players = new PlayerStateEntry[count];
            for (var i = 0; i < count; i++)
            {
                players[i] = new PlayerStateEntry
                {
                    PlayerId = (ushort)(i + 1),
                    X = i,
                    RotW = 1f,
                };
            }

            return players;
        }

        private static string[] FilledNames(int count)
        {
            var names = new string[count];
            for (var i = 0; i < count; i++)
            {
                names[i] = "item" + i;
            }

            return names;
        }

        private static CreatureStateEntry[] FilledCreatures(int count)
        {
            var entries = new CreatureStateEntry[count];
            for (var i = 0; i < count; i++)
            {
                entries[i] = new CreatureStateEntry { Key = i + 1, X = i, RotW = 1f };
            }

            return entries;
        }

        private static VehicleStateEntry[] FilledVehicles(int count)
        {
            var entries = new VehicleStateEntry[count];
            for (var i = 0; i < count; i++)
            {
                entries[i] = new VehicleStateEntry { Key = i + 1, X = i, RotW = 1f };
            }

            return entries;
        }

        private static EntitySpawnMessage SpawnWithId(string uniqueId)
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");
            return new EntitySpawnMessage
            {
                Key = 1L,
                UniqueId = uniqueId,
                TechTypeName = "SeaMoth",
                RotW = 1f,
                DefinitionHash = definition.ComputeHash(),
            };
        }
    }
}
