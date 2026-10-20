using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Le protocole transporte des floats **bit à bit** : une position recopiée d'un client à l'autre
    /// ne doit pas changer de valeur, et un motif spécial ne doit pas être « normalisé » par la
    /// conversion. Ces tests fixent les deux propriétés, plus l'absence d'allocation à l'écriture.
    /// </summary>
    public sealed class FloatWireTests
    {
        [Fact]
        public void Les_motifs_speciaux_survivent_a_un_aller_retour_exact()
        {
            var patterns = new[]
            {
                0f,
                -0f,
                float.PositiveInfinity,
                float.NegativeInfinity,
                float.Epsilon,           // plus petit dénormal
                -float.Epsilon,
                float.MinValue,
                float.MaxValue,
                BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001)), // NaN silencieux avec charge
                BitConverter.Int32BitsToSingle(unchecked((int)0x7f800001)), // NaN signalé
            };

            foreach (var value in patterns)
            {
                var writer = new PacketWriter();
                writer.WriteFloat(value);

                var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
                var read = reader.ReadFloat();

                Assert.Equal(
                    BitConverter.SingleToInt32Bits(value),
                    BitConverter.SingleToInt32Bits(read));
            }
        }

        [Fact]
        public void Tous_les_floats_aleatoires_survivent_a_un_aller_retour_exact()
        {
            var random = new Random(20261020);

            for (var i = 0; i < 5000; i++)
            {
                var value = BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue));

                var writer = new PacketWriter();
                writer.WriteFloat(value);

                var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(value),
                    BitConverter.SingleToInt32Bits(reader.ReadFloat()));
            }
        }

        [Fact]
        public void Ecrire_100_000_floats_n_alloue_pas()
        {
            const int count = 100_000;

            // Tampon dimensionné d'avance : `Ensure` ne doit pas agrandir pendant la mesure.
            var writer = new PacketWriter(count * 4 + 16);
            var probe = new PacketWriter(16);

            // Chauffe : JIT du chemin, première croissance éventuelle.
            for (var i = 0; i < 1000; i++)
            {
                probe.Reset();
                probe.WriteFloat(i * 0.5f);
            }

            writer.Reset();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < count; i++)
            {
                writer.WriteFloat(i * 0.5f);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(count * 4, writer.Length);
            Assert.True(
                allocated < 4096,
                $"écrire {count} floats a alloué {allocated} octets (attendu < 4096)");
        }
    }
}
