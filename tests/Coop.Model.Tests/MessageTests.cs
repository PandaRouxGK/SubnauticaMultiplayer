using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class MessageTests
    {
        [Fact]
        public void Handshake_aller_retour_avec_empreinte_de_sauvegarde()
        {
            var request = new HandshakeRequest
            {
                ProtocolVersion = Protocol.Version,
                GameVersion = "2019.4.36f1",
                ModListHash = "0123456789abcdef",
                Username = "Élodie-Ünïcode",
                Save = SaveIdentity.Create("slot0000", "session-abc", 83031, "Freedom"),
            };

            var bytes = MessageCodec.Encode(request.Write);
            Assert.Equal((byte)MessageType.HandshakeRequest, bytes[0]);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            var read = HandshakeRequest.Read(reader);

            Assert.Equal(request.ProtocolVersion, read.ProtocolVersion);
            Assert.Equal(request.GameVersion, read.GameVersion);
            Assert.Equal(request.ModListHash, read.ModListHash);
            Assert.Equal(request.Username, read.Username);
            Assert.NotNull(read.Save);
            Assert.Equal(request.Save.Slot, read.Save.Slot);
            Assert.Equal(request.Save.SessionId, read.Save.SessionId);
            Assert.Equal(request.Save.ChangeSet, read.Save.ChangeSet);
            Assert.Equal(request.Save.GameMode, read.Save.GameMode);
            Assert.Equal(read.Save.Hash, read.Save.RecomputeHash());
        }

        [Fact]
        public void Un_handshake_sans_empreinte_ecrit_une_empreinte_inconnue_et_est_refuse()
        {
            var request = new HandshakeRequest
            {
                ProtocolVersion = Protocol.Version,
                GameVersion = "2019.4.36f1",
                ModListHash = "hash",
                Username = "joueur",
                Save = null,
            };

            var reader = new PacketReader(MessageCodec.Encode(request.Write));
            reader.ReadByte();
            var read = HandshakeRequest.Read(reader);

            Assert.NotNull(read.Save);
            Assert.True(read.Save.IsUnknown);
            Assert.Equal(read.Save.Hash, read.Save.RecomputeHash());

            var reason = SaveCompatibility.Explain(
                SaveIdentity.Create("slot0000", "session", 1, "Freedom"),
                read.Save);

            Assert.NotNull(reason);
            Assert.Contains("inconnue", reason);
        }

        [Fact]
        public void Un_handshake_tronque_leve_une_ProtocolException()
        {
            var request = new HandshakeRequest
            {
                ProtocolVersion = Protocol.Version,
                GameVersion = "2019.4.36f1",
                ModListHash = "hash",
                Username = "joueur",
                Save = SaveIdentity.Create("slot0000", "session", 1, "Freedom"),
            };

            var bytes = MessageCodec.Encode(request.Write);

            // Couper 4 octets ampute le hash de sauvegarde : la lecture doit échouer, pas inventer.
            var truncated = new byte[bytes.Length - 4];
            Array.Copy(bytes, truncated, truncated.Length);

            var reader = new PacketReader(truncated);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => HandshakeRequest.Read(reader));
        }

        [Fact]
        public void EntitySpawn_aller_retour_avec_definition_et_hash()
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");
            var spawn = new EntitySpawnMessage
            {
                Key = 0x7ce3b6a1e8e7e312L,
                UniqueId = "0a472465-f9e8-4780-8b3e-a308429250ce",
                TechTypeName = "SeaMoth",
                X = -118.4f,
                Y = -13.4f,
                Z = -52.9f,
                RotW = 1f,
                ScaleX = 1f,
                ScaleY = 1f,
                ScaleZ = 1f,
                DefinitionHash = definition.ComputeHash(),
            };

            var bytes = MessageCodec.Encode(spawn.Write);
            var reader = new PacketReader(bytes);
            Assert.Equal((byte)MessageType.EntitySpawn, reader.ReadByte());
            var read = EntitySpawnMessage.Read(reader);

            Assert.Equal(spawn.Key, read.Key);
            Assert.Equal(spawn.UniqueId, read.UniqueId);
            Assert.Equal(spawn.TechTypeName, read.TechTypeName);
            Assert.Equal(spawn.DefinitionHash, read.DefinitionHash);
            Assert.Equal(spawn.ScaleX, read.ScaleX);
            Assert.Equal(read.BuildDefinition().ComputeHash(), read.DefinitionHash);
        }

        [Fact]
        public void Une_apparition_sans_hash_de_definition_est_refusee()
        {
            // L'écriture refuse désormais le hash nul : on forge le paquet à la main pour
            // vérifier que la lecture ne l'accepte pas non plus.
            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.EntitySpawn);
            writer.WriteLong(1L);
            writer.WriteString("id");
            writer.WriteString("SeaMoth");
            writer.WriteFloat(0f);
            writer.WriteFloat(0f);
            writer.WriteFloat(0f);
            writer.WriteFloat(0f);
            writer.WriteFloat(0f);
            writer.WriteFloat(0f);
            writer.WriteFloat(1f);
            writer.WriteFloat(1f);
            writer.WriteFloat(1f);
            writer.WriteFloat(1f);
            writer.WriteLong(0L);

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => EntitySpawnMessage.Read(reader));
        }

        [Fact]
        public void PlayerCorrection_aller_retour_avec_motif()
        {
            var correction = new PlayerCorrectionMessage
            {
                ServerTick = 4242L,
                X = -114.46f,
                Y = 2.29f,
                Z = -54.21f,
                Reason = "déplacement de 100.00 m en 0.050 s (2000.0 m/s)",
            };

            var reader = new PacketReader(MessageCodec.Encode(correction.Write));
            Assert.Equal((byte)MessageType.PlayerCorrection, reader.ReadByte());
            var read = PlayerCorrectionMessage.Read(reader);

            Assert.Equal(correction.ServerTick, read.ServerTick);
            Assert.Equal(correction.Reason, read.Reason);
            Assert.True(Math.Abs(correction.X - read.X) < 0.001f);
            Assert.True(Math.Abs(correction.Y - read.Y) < 0.001f);
            Assert.True(Math.Abs(correction.Z - read.Z) < 0.001f);
        }

        [Fact]
        public void Un_motif_de_correction_trop_long_est_refuse_a_l_ecriture()
        {
            var correction = new PlayerCorrectionMessage
            {
                ServerTick = 1L,
                Reason = new string('x', Protocol.MaxCorrectionReasonLength + 1),
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(correction.Write));
        }

        [Fact]
        public void Lire_un_tampon_vide_leve_une_ProtocolException()
        {
            var reader = new PacketReader(Array.Empty<byte>());

            Assert.Throws<ProtocolException>(() => reader.ReadByte());
        }
    }
}
