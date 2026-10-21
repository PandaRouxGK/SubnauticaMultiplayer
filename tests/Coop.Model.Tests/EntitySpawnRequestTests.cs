using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// v16 : une entité fabriquée par un client passe par une **demande** et une **décision**. Les cas
    /// couverts sont ceux qui, sans eux, produiraient une panne silencieuse : une demande sans espèce,
    /// une pose non finie, une décision accordée sans clé (impossible à relier à l'annonce) et un refus
    /// sans motif (le joueur ne saurait pas pourquoi rien n'apparaît).
    /// </summary>
    public sealed class EntitySpawnRequestTests
    {
        private static byte[] EncodeRequest(ushort techType = 2, float x = 1f)
        {
            return MessageCodec.Encode(new EntitySpawnRequestMessage
            {
                RequestId = 7u,
                TechType = techType,
                X = x,
                Y = 2f,
                Z = 3f,
                RotW = 1f,
            }.Write);
        }

        [Fact]
        public void Une_demande_fait_l_aller_retour()
        {
            var bytes = EncodeRequest();
            var reader = new PacketReader(bytes);
            Assert.Equal((byte)MessageType.EntitySpawnRequest, reader.ReadByte());
            var message = EntitySpawnRequestMessage.Read(reader);

            Assert.Equal(7u, message.RequestId);
            Assert.Equal((ushort)2, message.TechType);
            Assert.Equal(1f, message.X);
            Assert.Equal(3f, message.Z);
            Assert.Equal(1f, message.RotW);
        }

        [Fact]
        public void Une_demande_sans_espece_est_refusee_a_l_ecriture()
        {
            Assert.Throws<ProtocolException>(() => EncodeRequest(techType: 0));
        }

        [Fact]
        public void Une_demande_recue_sans_espece_est_refusee()
        {
            var bytes = EncodeRequest();
            // Opcode (1 octet) + RequestId (4) : c'est là que commence `TechType`.
            bytes[5] = 0;
            bytes[6] = 0;

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => EntitySpawnRequestMessage.Read(reader));
        }

        [Fact]
        public void Une_pose_non_finie_est_refusee_a_l_ecriture()
        {
            var message = new EntitySpawnRequestMessage
            {
                RequestId = 1u,
                TechType = 2,
                X = float.NaN,
                Y = 0f,
                Z = 0f,
                RotW = 1f,
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(message.Write));
        }

        [Fact]
        public void Une_decision_accordee_porte_la_cle_du_serveur()
        {
            var bytes = MessageCodec.Encode(new EntitySpawnResultMessage
            {
                RequestId = 7u,
                Granted = true,
                Key = 0x0518aa8f25e17d2fL,
            }.Write);

            var reader = new PacketReader(bytes);
            Assert.Equal((byte)MessageType.EntitySpawnResult, reader.ReadByte());
            var result = EntitySpawnResultMessage.Read(reader);

            Assert.True(result.Granted);
            Assert.Equal(0x0518aa8f25e17d2fL, result.Key);
            Assert.Equal(string.Empty, result.Reason);
        }

        [Fact]
        public void Une_decision_accordee_sans_cle_est_refusee()
        {
            var message = new EntitySpawnResultMessage
            {
                RequestId = 7u,
                Granted = true,
                Key = 0L,
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(message.Write));
        }

        [Fact]
        public void Un_refus_sans_motif_est_refuse()
        {
            var message = new EntitySpawnResultMessage
            {
                RequestId = 7u,
                Granted = false,
                Key = 0L,
                Reason = string.Empty,
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(message.Write));
        }

        [Fact]
        public void Un_refus_trop_long_est_refuse()
        {
            var message = new EntitySpawnResultMessage
            {
                RequestId = 7u,
                Granted = false,
                Reason = new string('x', Protocol.MaxSpawnReasonLength + 1),
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(message.Write));
        }

        [Fact]
        public void Un_refus_nomme_fait_l_aller_retour()
        {
            var bytes = MessageCodec.Encode(new EntitySpawnResultMessage
            {
                RequestId = 9u,
                Granted = false,
                Reason = "hors portée : 500.0 m > 8.0 m",
            }.Write);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            var result = EntitySpawnResultMessage.Read(reader);

            Assert.False(result.Granted);
            Assert.Equal("hors portée : 500.0 m > 8.0 m", result.Reason);
        }
    }
}
