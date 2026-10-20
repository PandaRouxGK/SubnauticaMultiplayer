using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Couverture des primitives du lecteur qui ne sont exercées par aucun message : `Rewind`,
    /// `Remaining` et `ReadRemainingBytes`. Une erreur de position ici décalerait **tous** les champs
    /// lus ensuite, sans qu'un test par message ne le voie forcément.
    /// </summary>
    public sealed class PacketReaderPrimitivesTests
    {
        [Fact]
        public void Rewind_remet_la_position_au_debut_de_la_plage()
        {
            var writer = new PacketWriter();
            writer.WriteByte(0x11);
            writer.WriteInt(0x11223344);

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            Assert.Equal(0x11, reader.ReadByte());
            Assert.Equal(0x11223344, reader.ReadInt());
            Assert.Equal(0, reader.Remaining);

            reader.Rewind();
            Assert.Equal(writer.Length, reader.Remaining);
            Assert.Equal(0x11, reader.ReadByte());
        }

        [Fact]
        public void Rewind_respecte_un_decalage_de_depart()
        {
            var writer = new PacketWriter();
            writer.WriteByte(0xAA); // préfixe ignoré
            writer.WriteByte(0x42);
            writer.WriteLong(7L);

            // La plage commence après le préfixe : `Rewind` doit revenir à `offset`, pas à 0.
            var reader = new PacketReader(writer.BackingBuffer, 1, writer.Length - 1);
            Assert.Equal(0x42, reader.ReadByte());
            reader.Rewind();
            Assert.Equal(0x42, reader.ReadByte());
            Assert.Equal(7L, reader.ReadLong());
            Assert.Equal(0, reader.Remaining);

            Assert.Throws<ProtocolException>(() => reader.ReadByte());
        }

        [Fact]
        public void ReadRemainingBytes_rend_exactement_le_reste_sans_toucher_au_dela_de_la_plage()
        {
            var writer = new PacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte(0x02);
            writer.WriteByte(0x03);
            writer.WriteByte(0x04);

            var reader = new PacketReader(writer.BackingBuffer, 1, 2); // 0x02 0x03
            Assert.Equal(0x02, reader.ReadByte());

            var rest = reader.ReadRemainingBytes();
            Assert.Single(rest);
            Assert.Equal(0x03, rest[0]);
            Assert.Equal(0, reader.Remaining);

            // Un second appel rend un tableau vide, pas le tampon d'origine.
            Assert.Empty(reader.ReadRemainingBytes());
        }

        [Fact]
        public void ReadRemainingBytes_sur_tout_le_tampon_rend_une_copie()
        {
            var writer = new PacketWriter();
            writer.WriteInt(123456);

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            var bytes = reader.ReadRemainingBytes();

            Assert.Equal(writer.Length, bytes.Length);
            Assert.NotSame(writer.BackingBuffer, bytes);
            Assert.Equal(123456, new PacketReader(bytes).ReadInt());
        }

        [Theory]
        [InlineData(new byte[] { 0 }, false)]
        [InlineData(new byte[] { 1 }, true)]
        [InlineData(new byte[] { 2 }, true)]
        [InlineData(new byte[] { 255 }, true)]
        public void ReadBool_lit_tout_octet_non_nul_comme_vrai(byte[] bytes, bool expected)
        {
            var reader = new PacketReader(bytes, 0, 1);
            Assert.Equal(expected, reader.ReadBool());
        }
    }
}
