using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Aller-retour binaire des messages qui n'étaient couverts par aucun test : le codec est
    /// écrit à la main, une inversion de champ ne se voit pas à la compilation.
    /// </summary>
    public sealed class MessageRoundTripTests
    {
        private static PacketReader Encoded(Action<PacketWriter> write, MessageType expected)
        {
            var bytes = MessageCodec.Encode(write);
            var reader = new PacketReader(bytes);
            Assert.Equal((byte)expected, reader.ReadByte());
            return reader;
        }

        [Fact]
        public void HandshakeResponse_aller_retour()
        {
            var message = new HandshakeResponse
            {
                Accepted = true,
                Reason = "bienvenue",
                ServerTick = 123456789L,
                AssignedPlayerId = 42,
            };

            var reader = Encoded(message.Write, MessageType.HandshakeResponse);
            var read = HandshakeResponse.Read(reader);

            Assert.True(read.Accepted);
            Assert.Equal(message.Reason, read.Reason);
            Assert.Equal(message.ServerTick, read.ServerTick);
            Assert.Equal(message.AssignedPlayerId, read.AssignedPlayerId);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void Ping_et_Pong_aller_retour()
        {
            var ping = new PingMessage { Sequence = 7L, ClientTimeMs = 111L };
            var reader = Encoded(ping.Write, MessageType.Ping);
            var readPing = PingMessage.Read(reader);
            Assert.Equal(ping.Sequence, readPing.Sequence);
            Assert.Equal(ping.ClientTimeMs, readPing.ClientTimeMs);
            Assert.Equal(0, reader.Remaining);

            var pong = new PongMessage
            {
                Sequence = 7L,
                ClientTimeMs = 111L,
                ServerTick = 222L,
                ServerTimeMs = 333L,
            };
            reader = Encoded(pong.Write, MessageType.Pong);
            var readPong = PongMessage.Read(reader);
            Assert.Equal(pong.Sequence, readPong.Sequence);
            Assert.Equal(pong.ClientTimeMs, readPong.ClientTimeMs);
            Assert.Equal(pong.ServerTick, readPong.ServerTick);
            Assert.Equal(pong.ServerTimeMs, readPong.ServerTimeMs);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void Disconnect_aller_retour()
        {
            var message = new DisconnectMessage { Reason = "serveur plein" };

            var reader = Encoded(message.Write, MessageType.Disconnect);
            var read = DisconnectMessage.Read(reader);

            Assert.Equal(message.Reason, read.Reason);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void PlayerJoined_et_PlayerLeft_aller_retour()
        {
            var joined = new PlayerJoinedMessage
            {
                PlayerId = 3,
                Username = "Élodie",
                X = 1.5f, Y = -2.5f, Z = 3.5f,
                RotX = 0.1f, RotY = 0.2f, RotZ = 0.3f, RotW = 0.9f,
            };

            var reader = Encoded(joined.Write, MessageType.PlayerJoined);
            var readJoined = PlayerJoinedMessage.Read(reader);
            Assert.Equal(joined.PlayerId, readJoined.PlayerId);
            Assert.Equal(joined.Username, readJoined.Username);
            Assert.Equal(joined.X, readJoined.X);
            Assert.Equal(joined.Y, readJoined.Y);
            Assert.Equal(joined.Z, readJoined.Z);
            Assert.Equal(joined.RotX, readJoined.RotX);
            Assert.Equal(joined.RotY, readJoined.RotY);
            Assert.Equal(joined.RotZ, readJoined.RotZ);
            Assert.Equal(joined.RotW, readJoined.RotW);
            Assert.Equal(0, reader.Remaining);

            var left = new PlayerLeftMessage { PlayerId = 3, Username = "Élodie", Reason = "Timeout" };
            reader = Encoded(left.Write, MessageType.PlayerLeft);
            var readLeft = PlayerLeftMessage.Read(reader);
            Assert.Equal(left.PlayerId, readLeft.PlayerId);
            Assert.Equal(left.Username, readLeft.Username);
            Assert.Equal(left.Reason, readLeft.Reason);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void PlayerPosition_aller_retour_avec_sequence_non_signee_et_drapeaux()
        {
            var message = new PlayerPositionMessage
            {
                Sequence = 0xFFFFFFFFu,
                X = -114.46f, Y = 2.29f, Z = -54.21f,
                RotX = 0.1f, RotY = 0.2f, RotZ = 0.3f, RotW = 0.9f,
                MoveSpeedX = 1.25f, MoveSpeedY = -0.5f, MoveSpeedZ = 0.25f,
                ViewPitch = -12.5f,
                IsUnderwater = true,
                OnSurface = false,
                ActionFlags = PlayerActionFlags.UsingPda | PlayerActionFlags.InSeamoth,
            };

            var reader = Encoded(message.Write, MessageType.PlayerPosition);
            var read = PlayerPositionMessage.Read(reader);

            Assert.Equal(message.Sequence, read.Sequence);
            Assert.Equal(message.X, read.X);
            Assert.Equal(message.Y, read.Y);
            Assert.Equal(message.Z, read.Z);
            Assert.Equal(message.ViewPitch, read.ViewPitch);
            Assert.True(read.IsUnderwater);
            Assert.False(read.OnSurface);
            Assert.Equal(message.ActionFlags, read.ActionFlags);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void PlayersSnapshot_aller_retour_avec_plusieurs_joueurs()
        {
            var message = new PlayersSnapshotMessage
            {
                ServerTick = 99L,
                Players = new[]
                {
                    new PlayerStateEntry
                    {
                        PlayerId = 1,
                        X = 1f, Y = 2f, Z = 3f,
                        RotW = 1f,
                        ActionFlags = PlayerActionFlags.UsingTool,
                    },
                    new PlayerStateEntry
                    {
                        PlayerId = 2,
                        X = -1f, Y = -2f, Z = -3f,
                        RotW = 1f,
                        IsUnderwater = true,
                    },
                },
            };

            var reader = Encoded(message.Write, MessageType.PlayersSnapshot);
            var read = PlayersSnapshotMessage.Read(reader);

            Assert.Equal(message.ServerTick, read.ServerTick);
            Assert.Equal(2, read.Players.Length);
            Assert.Equal((ushort)1, read.Players[0].PlayerId);
            Assert.Equal(PlayerActionFlags.UsingTool, read.Players[0].ActionFlags);
            Assert.Equal((ushort)2, read.Players[1].PlayerId);
            Assert.True(read.Players[1].IsUnderwater);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void PlayerEquipment_aller_retour_vide_et_rempli()
        {
            var empty = new PlayerEquipmentMessage { PlayerId = 4, TechTypeNames = Array.Empty<string>() };
            var reader = Encoded(empty.Write, MessageType.PlayerEquipment);
            var readEmpty = PlayerEquipmentMessage.Read(reader);
            Assert.Equal((ushort)4, readEmpty.PlayerId);
            Assert.Empty(readEmpty.TechTypeNames);
            Assert.Equal(0, reader.Remaining);

            var filled = new PlayerEquipmentMessage
            {
                PlayerId = 5,
                TechTypeNames = new[] { "Tank", "Fins", " knife " },
            };
            reader = Encoded(filled.Write, MessageType.PlayerEquipment);
            var readFilled = PlayerEquipmentMessage.Read(reader);
            Assert.Equal(filled.TechTypeNames, readFilled.TechTypeNames);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void PlayerHeldItem_aller_retour()
        {
            var message = new PlayerHeldItemMessage
            {
                PlayerId = 6,
                TechTypeName = "Welder",
                HoldingParams = new[] { "holding_welder" },
            };

            var reader = Encoded(message.Write, MessageType.PlayerHeldItem);
            var read = PlayerHeldItemMessage.Read(reader);

            Assert.Equal(message.PlayerId, read.PlayerId);
            Assert.Equal(message.TechTypeName, read.TechTypeName);
            Assert.Equal(message.HoldingParams, read.HoldingParams);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void PlayerColor_aller_retour()
        {
            var message = new PlayerColorMessage { PlayerId = 7, ColorIndex = 5 };

            var reader = Encoded(message.Write, MessageType.PlayerColor);
            var read = PlayerColorMessage.Read(reader);

            Assert.Equal(message.PlayerId, read.PlayerId);
            Assert.Equal(message.ColorIndex, read.ColorIndex);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void CreatureSnapshot_aller_retour()
        {
            var message = new CreatureSnapshotMessage
            {
                ServerTick = 10L,
                Creatures = new[]
                {
                    new CreatureStateEntry
                    {
                        Key = 0x1122334455667788L,
                        X = 1f, Y = 2f, Z = 3f,
                        RotW = 1f,
                    },
                },
            };

            var reader = Encoded(message.Write, MessageType.CreatureSnapshot);
            var read = CreatureSnapshotMessage.Read(reader);

            Assert.Equal(message.ServerTick, read.ServerTick);
            Assert.Single(read.Creatures);
            Assert.Equal(message.Creatures[0].Key, read.Creatures[0].Key);
            Assert.Equal(message.Creatures[0].Y, read.Creatures[0].Y);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void VehicleSnapshot_aller_retour()
        {
            var message = new VehicleSnapshotMessage
            {
                ServerTick = 11L,
                Vehicles = new[]
                {
                    new VehicleStateEntry
                    {
                        Key = 0x7ce3b6a1e8e7e312L,
                        X = -118.4f, Y = -13.4f, Z = -52.9f,
                        RotW = 1f,
                    },
                },
            };

            var reader = Encoded(message.Write, MessageType.VehicleSnapshot);
            var read = VehicleSnapshotMessage.Read(reader);

            Assert.Equal(message.ServerTick, read.ServerTick);
            Assert.Single(read.Vehicles);
            Assert.Equal(message.Vehicles[0].Key, read.Vehicles[0].Key);
            Assert.Equal(message.Vehicles[0].X, read.Vehicles[0].X);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void PickupClaim_aller_retour()
        {
            var message = new PickupClaimMessage
            {
                PlayerId = 8,
                ItemKey = -1234567890123L,
                TechTypeName = "MetalDetritus",
            };

            var reader = Encoded(message.Write, MessageType.PickupClaim);
            var read = PickupClaimMessage.Read(reader);

            Assert.Equal(message.PlayerId, read.PlayerId);
            Assert.Equal(message.ItemKey, read.ItemKey);
            Assert.Equal(message.TechTypeName, read.TechTypeName);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void VehicleControl_aller_retour()
        {
            var message = new VehicleControlMessage
            {
                PlayerId = 9,
                VehicleKey = 0x0102030405060708L,
                Sequence = 123456,
                X = 1f, Y = 2f, Z = 3f,
                RotX = 0f, RotY = 0f, RotZ = 0f, RotW = 1f,
                SpeedX = 20f, SpeedY = -1f, SpeedZ = 0.5f,
            };

            var reader = Encoded(message.Write, MessageType.VehicleControl);
            var read = VehicleControlMessage.Read(reader);

            Assert.Equal(message.PlayerId, read.PlayerId);
            Assert.Equal(message.VehicleKey, read.VehicleKey);
            Assert.Equal(message.Sequence, read.Sequence);
            Assert.Equal(message.SpeedX, read.SpeedX);
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void VehicleControlResult_et_VehicleRelease_aller_retour()
        {
            var result = new VehicleControlResultMessage
            {
                VehicleKey = 3L,
                Granted = false,
                DriverId = 10,
            };

            var reader = Encoded(result.Write, MessageType.VehicleControlResult);
            var readResult = VehicleControlResultMessage.Read(reader);
            Assert.Equal(result.VehicleKey, readResult.VehicleKey);
            Assert.False(readResult.Granted);
            Assert.Equal(result.DriverId, readResult.DriverId);
            Assert.Equal(0, reader.Remaining);

            var release = new VehicleReleaseMessage { VehicleKey = 42L };
            reader = Encoded(release.Write, MessageType.VehicleRelease);
            var readRelease = VehicleReleaseMessage.Read(reader);
            Assert.Equal(release.VehicleKey, readRelease.VehicleKey);
            Assert.Equal(0, reader.Remaining);
        }
    }
}
