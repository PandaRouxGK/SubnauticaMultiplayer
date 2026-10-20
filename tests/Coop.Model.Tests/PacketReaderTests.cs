using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class PacketReaderTests
    {
        [Fact]
        public void Un_tampon_nul_est_refuse()
        {
            Assert.Throws<ProtocolException>(() => new PacketReader(null));
        }

        [Fact]
        public void Une_plage_hors_du_tampon_est_refusee()
        {
            var data = new byte[4];

            Assert.Throws<ProtocolException>(() => new PacketReader(data, -1, 1));
            Assert.Throws<ProtocolException>(() => new PacketReader(data, 0, -1));
            Assert.Throws<ProtocolException>(() => new PacketReader(data, 0, 5));
            Assert.Throws<ProtocolException>(() => new PacketReader(data, 4, 1));
        }

        [Fact]
        public void Une_plage_qui_deborde_un_int_est_refusee_sans_overflow()
        {
            var data = new byte[4];

            // Sans le contrôle par soustraction, offset + length = int.MinValue : la plage
            // passait le test de validité puis rendait Remaining négatif.
            Assert.Throws<ProtocolException>(() => new PacketReader(data, int.MaxValue, 1));
        }

        [Fact]
        public void La_fenetre_ne_lit_que_sa_portion_et_rewind_revient_au_debut()
        {
            var data = new byte[] { 0xAA, 1, 2, 3, 0xBB };
            var reader = new PacketReader(data, 1, 3);

            Assert.Equal(3, reader.Remaining);
            Assert.Equal((byte)1, reader.ReadByte());
            Assert.Equal((byte)2, reader.ReadByte());
            Assert.Equal(1, reader.Remaining);
            Assert.Equal((byte)3, reader.ReadByte());
            Assert.Equal(0, reader.Remaining);
            Assert.Throws<ProtocolException>(() => reader.ReadByte());

            reader.Rewind();
            Assert.Equal(3, reader.Remaining);
            Assert.Equal((byte)1, reader.ReadByte());
        }

        [Fact]
        public void ReadRemainingBytes_copie_puis_vide_le_tampon()
        {
            var reader = new PacketReader(new byte[] { 1, 2, 3 });

            Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadRemainingBytes());
            Assert.Equal(0, reader.Remaining);
            Assert.Empty(reader.ReadRemainingBytes());
        }

        [Fact]
        public void Les_types_primitifs_font_un_aller_retour()
        {
            var writer = new PacketWriter();
            writer.WriteByte(0xFE);
            writer.WriteBool(true);
            writer.WriteBool(false);
            writer.WriteUInt16(0xBEEF);
            writer.WriteInt(-123456789);
            writer.WriteLong(0x0123456789ABCDEFL);
            writer.WriteFloat(12.5f);

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            Assert.Equal((byte)0xFE, reader.ReadByte());
            Assert.True(reader.ReadBool());
            Assert.False(reader.ReadBool());
            Assert.Equal((ushort)0xBEEF, reader.ReadUInt16());
            Assert.Equal(-123456789, reader.ReadInt());
            Assert.Equal(0x0123456789ABCDEFL, reader.ReadLong());
            Assert.Equal(12.5f, reader.ReadFloat());
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void Une_lecture_tronquee_leve_une_ProtocolException()
        {
            var cases = new (byte[] data, Action<PacketReader> read)[]
            {
                (Array.Empty<byte>(), r => r.ReadByte()),
                (new byte[1], r => r.ReadUInt16()),
                (new byte[3], r => r.ReadInt()),
                (new byte[7], r => r.ReadLong()),
                (new byte[3], r => r.ReadFloat()),
                (new byte[] { 2, 0x41 }, r => r.ReadString()),
            };

            foreach (var (data, read) in cases)
            {
                var reader = new PacketReader(data);
                Assert.Throws<ProtocolException>(() => read(reader));
            }
        }

        [Fact]
        public void Une_lecture_tronquee_d_un_type_primitif_ne_consomme_rien()
        {
            var cases = new (byte[] data, Action<PacketReader> read)[]
            {
                (Array.Empty<byte>(), r => r.ReadByte()),
                (new byte[1], r => r.ReadUInt16()),
                (new byte[3], r => r.ReadInt()),
                (new byte[7], r => r.ReadLong()),
                (new byte[3], r => r.ReadFloat()),
            };

            foreach (var (data, read) in cases)
            {
                var reader = new PacketReader(data);
                Assert.Throws<ProtocolException>(() => read(reader));
                Assert.Equal(data.Length, reader.Remaining);
            }
        }

        [Fact]
        public void Une_chaine_vide_et_une_chaine_unicode_font_un_aller_retour()
        {
            var writer = new PacketWriter();
            writer.WriteString(null);
            writer.WriteString(string.Empty);
            writer.WriteString("Élodie-Ünïcode-🌊");

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            Assert.Equal(string.Empty, reader.ReadString());
            Assert.Equal(string.Empty, reader.ReadString());
            Assert.Equal("Élodie-Ünïcode-🌊", reader.ReadString());
        }

        [Fact]
        public void Une_chaine_trop_longue_pour_l_ecriture_est_refusee()
        {
            var writer = new PacketWriter();

            Assert.Throws<ProtocolException>(() => writer.WriteString(new string('x', ushort.MaxValue + 1)));
        }
    }

    public sealed class PacketWriterTests
    {
        [Fact]
        public void Reset_remet_la_longueur_a_zero_sans_perdre_le_tampon()
        {
            var writer = new PacketWriter();
            writer.WriteInt(42);
            var capacity = writer.BackingBuffer.Length;

            writer.Reset();

            Assert.Equal(0, writer.Length);
            Assert.Equal(capacity, writer.BackingBuffer.Length);
            writer.WriteByte(1);
            Assert.Equal(1, writer.Length);
        }

        [Fact]
        public void Une_petite_capacite_est_remontee_a_seize_octets()
        {
            Assert.Equal(16, new PacketWriter(2).BackingBuffer.Length);
            Assert.Equal(16, new PacketWriter(0).BackingBuffer.Length);
            Assert.Equal(16, new PacketWriter(-1).BackingBuffer.Length);
            Assert.Equal(16, new PacketWriter(int.MinValue).BackingBuffer.Length);
        }

        [Fact]
        public void WriteString_null_ecrit_une_chaine_vide()
        {
            var writer = new PacketWriter();
            writer.WriteString(null);

            Assert.Equal(2, writer.Length);
            Assert.Equal(string.Empty, new PacketReader(writer.BackingBuffer, 0, writer.Length).ReadString());
        }

        [Fact]
        public void Reset_apres_croissance_conserve_la_capacite()
        {
            var writer = new PacketWriter();
            for (var i = 0; i < 100; i++)
            {
                writer.WriteByte((byte)i);
            }

            var capacity = writer.BackingBuffer.Length;
            Assert.True(capacity > 16);

            writer.Reset();

            Assert.Equal(0, writer.Length);
            Assert.Equal(capacity, writer.BackingBuffer.Length);
        }

        [Fact]
        public void La_croissance_conserve_les_octets_deja_ecrits()
        {
            var writer = new PacketWriter(16);
            for (var i = 0; i < 100; i++)
            {
                writer.WriteByte((byte)i);
            }

            Assert.Equal(100, writer.Length);
            for (var i = 0; i < 100; i++)
            {
                Assert.Equal((byte)i, writer.BackingBuffer[i]);
            }
        }

        [Fact]
        public void MessageCodec_reutilise_un_tampon_sans_garder_l_ancien_message()
        {
            var reusable = new PacketWriter();
            var first = MessageCodec.Encode(w => w.WriteInt(1), reusable);
            var second = MessageCodec.Encode(w => w.WriteInt(2), reusable);

            Assert.Equal(4, first.Length);
            Assert.Equal(4, second.Length);
            Assert.Equal(1, new PacketReader(first).ReadInt());
            Assert.Equal(2, new PacketReader(second).ReadInt());
        }
    }
}
