using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class PacketBoundaryTests
    {
        [Fact]
        public void Une_sequence_UTF8_invalide_est_refusee()
        {
            var writer = new PacketWriter();
            writer.WriteUInt16(2);
            writer.WriteByte(0x00);
            writer.WriteByte(0xC3); // début d'une séquence de deux octets, sans continuation

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);

            Assert.Throws<ProtocolException>(() => reader.ReadString());
        }

        [Fact]
        public void Une_sequence_UTF8_overlong_ou_continuation_isolee_est_refusee()
        {
            var cases = new[]
            {
                new byte[] { 0xC0, 0x80 }, // overlong de U+0000
                new byte[] { 0x80, 0x80 }, // continuations sans début
                new byte[] { 0xE2, 0x82 }, // début de trois octets tronqué
            };

            foreach (var payload in cases)
            {
                var writer = new PacketWriter();
                writer.WriteUInt16((ushort)payload.Length);
                foreach (var value in payload)
                {
                    writer.WriteByte(value);
                }

                var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
                Assert.Throws<ProtocolException>(() => reader.ReadString());
            }
        }

        [Fact]
        public void Les_accents_et_les_emoji_restent_acceptes_par_le_decodeur_strict()
        {
            var writer = new PacketWriter();
            writer.WriteString("Élodie-Ünïcode-🌊");

            var read = new PacketReader(writer.BackingBuffer, 0, writer.Length).ReadString();

            Assert.Equal("Élodie-Ünïcode-🌊", read);
        }

        [Fact]
        public void Un_surrogate_UTF16_isole_est_refuse_a_l_ecriture()
        {
            Assert.Throws<ProtocolException>(() => new PacketWriter().WriteString("\uD800"));
            Assert.Throws<ProtocolException>(() => new PacketWriter().WriteString("fin\uDC00"));
        }

        [Fact]
        public void Une_paire_de_surrogates_valide_reste_acceptee_a_l_ecriture()
        {
            var value = "paire-\uD83C\uDF0A";
            var writer = new PacketWriter();
            writer.WriteString(value);

            Assert.Equal(value, new PacketReader(writer.BackingBuffer, 0, writer.Length).ReadString());
        }

        [Fact]
        public void Une_chaine_de_soixante_cinq_mille_cing_trente_cinq_octets_est_acceptee()
        {
            var value = new string('x', ushort.MaxValue);
            var writer = new PacketWriter();
            writer.WriteString(value);

            Assert.Equal(ushort.MaxValue + 2, writer.Length);

            var read = new PacketReader(writer.BackingBuffer, 0, writer.Length).ReadString();
            Assert.Equal(value, read);
        }

        [Fact]
        public void La_limite_UTF8_est_comptee_en_octets_et_non_en_caracteres()
        {
            // Un emoji occupe 4 octets : 16 383 × 4 = 65 532 ≤ 65 535, 16 384 × 4 = 65 536 > limite.
            var accepted = string.Concat(Enumerable.Repeat("🌊", 16_383));
            var writer = new PacketWriter();
            writer.WriteString(accepted);

            var read = new PacketReader(writer.BackingBuffer, 0, writer.Length).ReadString();
            Assert.Equal(accepted, read);

            var refused = string.Concat(Enumerable.Repeat("🌊", 16_384));
            Assert.Throws<ProtocolException>(() => new PacketWriter().WriteString(refused));
        }

        [Fact]
        public void Un_prefixe_de_longueur_maximale_sans_les_octets_leve_une_ProtocolException()
        {
            var writer = new PacketWriter();
            writer.WriteUInt16(ushort.MaxValue);
            writer.WriteByte(0x41);

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);

            Assert.Throws<ProtocolException>(() => reader.ReadString());
        }

        [Fact]
        public void Reutiliser_un_writer_apres_un_message_plus_long_ne_laisse_pas_d_octets_residuels()
        {
            var reusable = new PacketWriter();
            var longMessage = MessageCodec.Encode(w => w.WriteString(new string('x', 100)), reusable);
            var shortMessage = MessageCodec.Encode(w => w.WriteInt(42), reusable);

            Assert.Equal(102, longMessage.Length);
            Assert.Equal(4, shortMessage.Length);
            Assert.Equal(42, new PacketReader(shortMessage).ReadInt());
        }

        [Fact]
        public void ReadRemainingBytes_ne_lit_que_la_fenetre()
        {
            var data = new byte[] { 0xAA, 1, 2, 3, 0xBB };
            var reader = new PacketReader(data, 1, 3);

            Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadRemainingBytes());
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void Un_octet_residuel_est_visible_apres_lecture_d_un_message()
        {
            var writer = new PacketWriter();
            writer.WriteInt(7);
            writer.WriteByte(0xFF);

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            Assert.Equal(7, reader.ReadInt());
            Assert.Equal(1, reader.Remaining);
            Assert.Equal((byte)0xFF, reader.ReadByte());
        }

        [Fact]
        public void ReadFloat_n_alloue_pas_un_tableau_par_lecture()
        {
            const int reads = 200_000;
            var reader = new PacketReader(new byte[4 * (reads + 1)]);

            reader.ReadFloat(); // amorçage : allocation du tampon [ThreadStatic] et JIT de la boucle

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < reads; i++)
            {
                reader.ReadFloat();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 4096, $"{allocated} octet(s) alloué(s) pour {reads} lectures de float");
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void Le_tampon_de_ReadFloat_est_independant_par_thread()
        {
            const int threadCount = 4;
            const int readsPerThread = 20_000;
            var failures = new ConcurrentBag<string>();
            var workers = new List<Thread>();

            for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
            {
                var index = threadIndex;
                var worker = new Thread(() =>
                {
                    var expected = Enumerable.Range(0, readsPerThread)
                        .Select(offset => index * 1_000_000f + offset)
                        .ToArray();
                    var bytes = new byte[expected.Length * 4];
                    Buffer.BlockCopy(expected, 0, bytes, 0, bytes.Length);
                    var reader = new PacketReader(bytes);

                    for (var i = 0; i < expected.Length; i++)
                    {
                        var actual = reader.ReadFloat();
                        if (actual != expected[i])
                        {
                            failures.Add($"thread {index}, lecture {i} : attendu {expected[i]}, lu {actual}");
                            return;
                        }
                    }
                });

                worker.IsBackground = true;
                workers.Add(worker);
            }

            foreach (var worker in workers)
            {
                worker.Start();
            }

            foreach (var worker in workers)
            {
                Assert.True(worker.Join(TimeSpan.FromSeconds(20)), "un thread de lecture n'a pas terminé");
            }

            Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void Un_bool_non_nul_est_lu_comme_vrai()
        {
            // Le writer n'écrit que 0/1, mais un paquet forgé peut porter 0xFF : on pin le comportement.
            var reader = new PacketReader(new byte[] { 0xFF, 0x00 });

            Assert.True(reader.ReadBool());
            Assert.False(reader.ReadBool());
        }
    }
}
