using System;
using System.Linq;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Propriété du codec : écrire, relire, réécrire doit produire exactement les mêmes octets.
    /// Un champ lu dans le mauvais ordre, ou un `Write` qui n'utilise pas le champ relu, casse cette
    /// égalité sans qu'aucun test d'aller-retour champ par champ ne le voie forcément.
    /// </summary>
    public sealed class MessagePropertyTests
    {
        private const int Iterations = 100;

        private static void RoundTripStable<T>(
            T message,
            Action<T, PacketWriter> write,
            Func<PacketReader, T> read)
        {
            var first = MessageCodec.Encode(writer => write(message, writer));

            var reader = new PacketReader(first);
            reader.ReadByte();
            var copy = read(reader);
            Assert.Equal(0, reader.Remaining);

            var second = MessageCodec.Encode(writer => write(copy, writer));
            Assert.Equal(first, second);
        }

        private static float Finite(Random random) => (float)(random.NextDouble() * 200.0 - 100.0);

        /// <summary>
        /// Clé d'entité non nulle. `NextInt64()` peut théoriquement rendre 0 (une chance sur 2^64) :
        /// le codec le refuse désormais, donc tirer 0 rendrait ce test faux une fois sur 2^64.
        /// </summary>
        private static long NonZero(Random random)
        {
            var key = random.NextInt64();
            return key == 0L ? 1L : key;
        }

        private static string Name(Random random, int maxLength = 20)
        {
            var length = random.Next(0, maxLength + 1);
            return new string(Enumerable.Range(0, length).Select(_ => (char)('a' + random.Next(26))).ToArray());
        }

        [Fact]
        public void PlayerPosition_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(1);

            for (var i = 0; i < Iterations; i++)
            {
                var message = new PlayerPositionMessage
                {
                    Sequence = unchecked((uint)random.NextInt64()),
                    X = Finite(random), Y = Finite(random), Z = Finite(random),
                    RotX = Finite(random), RotY = Finite(random), RotZ = Finite(random), RotW = Finite(random),
                    MoveSpeedX = Finite(random), MoveSpeedY = Finite(random), MoveSpeedZ = Finite(random),
                    ViewPitch = Finite(random),
                    IsUnderwater = random.Next(2) == 0,
                    OnSurface = random.Next(2) == 0,
                    ActionFlags = (PlayerActionFlags)(byte)random.Next(32),
                };

                RoundTripStable(message, (m, w) => m.Write(w), PlayerPositionMessage.Read);
            }
        }

        [Fact]
        public void PlayersSnapshot_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(2);

            for (var i = 0; i < Iterations; i++)
            {
                var players = new PlayerStateEntry[random.Next(0, Protocol.MaxClients + 1)];
                for (var j = 0; j < players.Length; j++)
                {
                    players[j] = new PlayerStateEntry
                    {
                        PlayerId = (ushort)random.Next(ushort.MaxValue + 1),
                        X = Finite(random), Y = Finite(random), Z = Finite(random),
                        RotX = Finite(random), RotY = Finite(random), RotZ = Finite(random), RotW = Finite(random),
                        MoveSpeedX = Finite(random), MoveSpeedY = Finite(random), MoveSpeedZ = Finite(random),
                        ViewPitch = Finite(random),
                        IsUnderwater = random.Next(2) == 0,
                        OnSurface = random.Next(2) == 0,
                        ActionFlags = (PlayerActionFlags)(byte)random.Next(32),
                    };
                }

                var message = new PlayersSnapshotMessage { ServerTick = random.NextInt64(), Players = players };
                RoundTripStable(message, (m, w) => m.Write(w), PlayersSnapshotMessage.Read);
            }
        }

        [Fact]
        public void PlayerEquipment_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(3);

            for (var i = 0; i < Iterations; i++)
            {
                var names = new string[random.Next(0, Protocol.MaxEquipmentSlots + 1)];
                for (var j = 0; j < names.Length; j++)
                {
                    names[j] = Name(random, 30);
                }

                var message = new PlayerEquipmentMessage
                {
                    PlayerId = (ushort)random.Next(ushort.MaxValue + 1),
                    TechTypeNames = names,
                };

                RoundTripStable(message, (m, w) => m.Write(w), PlayerEquipmentMessage.Read);
            }
        }

        [Fact]
        public void PlayerHeldItem_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(4);

            for (var i = 0; i < Iterations; i++)
            {
                var parameters = new string[random.Next(0, Protocol.MaxActionParams + 1)];
                for (var j = 0; j < parameters.Length; j++)
                {
                    parameters[j] = "holding_" + Name(random, 10);
                }

                var message = new PlayerHeldItemMessage
                {
                    PlayerId = (ushort)random.Next(ushort.MaxValue + 1),
                    TechTypeName = Name(random),
                    HoldingParams = parameters,
                };

                RoundTripStable(message, (m, w) => m.Write(w), PlayerHeldItemMessage.Read);
            }
        }

        [Fact]
        public void CreatureSnapshot_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(5);

            for (var i = 0; i < Iterations; i++)
            {
                var creatures = new CreatureStateEntry[random.Next(0, Protocol.MaxCreatureStates + 1)];
                for (var j = 0; j < creatures.Length; j++)
                {
                    creatures[j] = new CreatureStateEntry
                    {
                        Key = random.NextInt64(),
                        X = Finite(random), Y = Finite(random), Z = Finite(random),
                        RotX = Finite(random), RotY = Finite(random), RotZ = Finite(random), RotW = Finite(random),
                    };
                }

                var message = new CreatureSnapshotMessage { ServerTick = random.NextInt64(), Creatures = creatures };
                RoundTripStable(message, (m, w) => m.Write(w), CreatureSnapshotMessage.Read);
            }
        }

        [Fact]
        public void VehicleSnapshot_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(6);

            for (var i = 0; i < Iterations; i++)
            {
                var vehicles = new VehicleStateEntry[random.Next(0, Protocol.MaxVehicleStates + 1)];
                for (var j = 0; j < vehicles.Length; j++)
                {
                    vehicles[j] = new VehicleStateEntry
                    {
                        Key = random.NextInt64(),
                        X = Finite(random), Y = Finite(random), Z = Finite(random),
                        RotX = Finite(random), RotY = Finite(random), RotZ = Finite(random), RotW = Finite(random),
                    };
                }

                var message = new VehicleSnapshotMessage { ServerTick = random.NextInt64(), Vehicles = vehicles };
                RoundTripStable(message, (m, w) => m.Write(w), VehicleSnapshotMessage.Read);
            }
        }

        [Fact]
        public void EntitySpawn_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(7);

            for (var i = 0; i < Iterations; i++)
            {
                var definition = EntityDefinition.ForVehicle(Name(random, 16));
                definition.ScaleX = (float)(random.NextDouble() * 2.0);
                definition.ScaleY = (float)(random.NextDouble() * 2.0);
                definition.ScaleZ = (float)(random.NextDouble() * 2.0);

                var message = new EntitySpawnMessage
                {
                    Key = NonZero(random),
                    UniqueId = Guid.NewGuid().ToString(),
                    TechTypeName = definition.TechTypeName,
                    X = Finite(random), Y = Finite(random), Z = Finite(random),
                    RotX = Finite(random), RotY = Finite(random), RotZ = Finite(random), RotW = Finite(random),
                    ScaleX = definition.ScaleX,
                    ScaleY = definition.ScaleY,
                    ScaleZ = definition.ScaleZ,
                    DefinitionHash = definition.ComputeHash(),
                };

                RoundTripStable(message, (m, w) => m.Write(w), EntitySpawnMessage.Read);
            }
        }

        [Fact]
        public void PickupResult_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(8);

            for (var i = 0; i < Iterations; i++)
            {
                var message = new PickupResultMessage
                {
                    ItemKey = random.NextInt64(),
                    TechTypeName = Name(random, 30),
                    Granted = random.Next(2) == 0,
                    HolderId = (ushort)random.Next(ushort.MaxValue + 1),
                    ClaimedBy = (ushort)random.Next(ushort.MaxValue + 1),
                };

                RoundTripStable(message, (m, w) => m.Write(w), PickupResultMessage.Read);
            }
        }

        [Fact]
        public void VehicleControl_encode_relit_et_reecrit_les_memes_octets()
        {
            var random = new Random(9);

            for (var i = 0; i < Iterations; i++)
            {
                var message = new VehicleControlMessage
                {
                    PlayerId = (ushort)random.Next(ushort.MaxValue + 1),
                    VehicleKey = random.NextInt64(),
                    Sequence = random.Next(),
                    X = Finite(random), Y = Finite(random), Z = Finite(random),
                    RotX = Finite(random), RotY = Finite(random), RotZ = Finite(random), RotW = Finite(random),
                    SpeedX = Finite(random), SpeedY = Finite(random), SpeedZ = Finite(random),
                };

                RoundTripStable(message, (m, w) => m.Write(w), VehicleControlMessage.Read);
            }
        }
    }
}
