using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// M4, v14 : la charge de la batterie voyage avec l'état du véhicule. Trois règles comptent —
    /// le sentinelle `-1` (pas de batterie) ne doit pas devenir « vide », une valeur non finie est un
    /// paquet invalide, et une valeur hors `0..1` est ramenée dans les bornes plutôt que rejetée.
    /// </summary>
    public sealed class VehicleEnergyTests
    {
        private static VehicleStateEntry Entry(float energy) => new VehicleStateEntry
        {
            Key = 0x7ce3b6a1e8e7e312L,
            X = 1f,
            Y = 2f,
            Z = 3f,
            RotW = 1f,
            Energy = energy,
        };

        [Fact]
        public void La_charge_fait_l_aller_retour()
        {
            var bytes = MessageCodec.Encode(new VehicleSnapshotMessage
            {
                ServerTick = 7L,
                Vehicles = new[] { Entry(0.42f) },
            }.Write);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            var read = VehicleSnapshotMessage.Read(reader);

            Assert.Equal(0.42f, read.Vehicles[0].Energy, 3);
        }

        [Fact]
        public void Le_sentinelle_moins_un_traverse_et_ne_devient_pas_une_batterie_vide()
        {
            var bytes = MessageCodec.Encode(new VehicleSnapshotMessage
            {
                ServerTick = 7L,
                Vehicles = new[] { Entry(-1f) },
            }.Write);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            var read = VehicleSnapshotMessage.Read(reader);

            Assert.Equal(-1f, read.Vehicles[0].Energy);
        }

        [Fact]
        public void Une_charge_non_finie_est_refusee_a_l_ecriture()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new VehicleSnapshotMessage
            {
                ServerTick = 7L,
                Vehicles = new[] { Entry(float.NaN) },
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new VehicleSnapshotMessage
            {
                ServerTick = 7L,
                Vehicles = new[] { Entry(float.PositiveInfinity) },
            }.Write));
        }

        [Fact]
        public void Une_charge_hors_bornes_est_ramenee_dans_les_bornes()
        {
            var over = MessageCodec.Encode(new VehicleSnapshotMessage
            {
                ServerTick = 7L,
                Vehicles = new[] { Entry(1.5f) },
            }.Write);
            var overReader = new PacketReader(over);
            overReader.ReadByte();
            Assert.Equal(1f, VehicleSnapshotMessage.Read(overReader).Vehicles[0].Energy);

            var under = MessageCodec.Encode(new VehicleSnapshotMessage
            {
                ServerTick = 7L,
                Vehicles = new[] { Entry(-0.5f) },
            }.Write);
            var underReader = new PacketReader(under);
            underReader.ReadByte();
            Assert.Equal(-1f, VehicleSnapshotMessage.Read(underReader).Vehicles[0].Energy);
        }
    }
}
