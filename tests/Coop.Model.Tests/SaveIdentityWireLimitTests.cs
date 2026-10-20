using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class SaveIdentityWireLimitTests
    {
        [Fact]
        public void Les_longueurs_maximales_font_un_aller_retour()
        {
            var identity = SaveIdentity.Create(
                new string('s', Protocol.MaxSaveSlotLength),
                new string('g', Protocol.MaxSaveSessionLength),
                int.MaxValue,
                new string('m', Protocol.MaxGameModeLength));

            var writer = new PacketWriter();
            identity.WriteTo(writer);

            var read = SaveIdentity.ReadFrom(new PacketReader(writer.BackingBuffer, 0, writer.Length));

            Assert.Equal(identity.Slot, read.Slot);
            Assert.Equal(identity.SessionId, read.SessionId);
            Assert.Equal(identity.ChangeSet, read.ChangeSet);
            Assert.Equal(identity.GameMode, read.GameMode);
            Assert.Equal(identity.Hash, read.Hash);
        }

        [Fact]
        public void Un_slot_trop_long_est_refuse_a_l_ecriture()
        {
            var identity = SaveIdentity.Create(
                new string('s', Protocol.MaxSaveSlotLength + 1),
                "session",
                1,
                "Freedom");

            Assert.Throws<ProtocolException>(() => identity.WriteTo(new PacketWriter()));
        }

        [Fact]
        public void Une_session_trop_longue_est_refusee_a_l_ecriture()
        {
            var identity = SaveIdentity.Create(
                "slot0000",
                new string('g', Protocol.MaxSaveSessionLength + 1),
                1,
                "Freedom");

            Assert.Throws<ProtocolException>(() => identity.WriteTo(new PacketWriter()));
        }

        [Fact]
        public void Un_mode_trop_long_est_refuse_a_l_ecriture()
        {
            var identity = SaveIdentity.Create(
                "slot0000",
                "session",
                1,
                new string('m', Protocol.MaxGameModeLength + 1));

            Assert.Throws<ProtocolException>(() => identity.WriteTo(new PacketWriter()));
        }

        [Fact]
        public void Un_slot_recu_trop_long_est_refuse_a_la_lecture()
        {
            var writer = Forged(
                new string('s', Protocol.MaxSaveSlotLength + 1),
                "session",
                "Freedom");

            Assert.Throws<ProtocolException>(() => SaveIdentity.ReadFrom(new PacketReader(writer.BackingBuffer, 0, writer.Length)));
        }

        [Fact]
        public void Une_session_recue_trop_longue_est_refusee_a_la_lecture()
        {
            var writer = Forged(
                "slot0000",
                new string('g', Protocol.MaxSaveSessionLength + 1),
                "Freedom");

            Assert.Throws<ProtocolException>(() => SaveIdentity.ReadFrom(new PacketReader(writer.BackingBuffer, 0, writer.Length)));
        }

        [Fact]
        public void Un_mode_recu_trop_long_est_refuse_a_la_lecture()
        {
            var writer = Forged(
                "slot0000",
                "session",
                new string('m', Protocol.MaxGameModeLength + 1));

            Assert.Throws<ProtocolException>(() => SaveIdentity.ReadFrom(new PacketReader(writer.BackingBuffer, 0, writer.Length)));
        }

        [Fact]
        public void Une_empreinte_tronquee_leve_une_ProtocolException()
        {
            Assert.Throws<ProtocolException>(() =>
                SaveIdentity.ReadFrom(new PacketReader(Array.Empty<byte>())));

            var writer = new PacketWriter();
            writer.WriteString("slot0000");
            Assert.Throws<ProtocolException>(() =>
                SaveIdentity.ReadFrom(new PacketReader(writer.BackingBuffer, 0, writer.Length)));
        }

        [Fact]
        public void Une_empreinte_inconnue_se_decrit_sans_champs_inventes()
        {
            var unknown = SaveIdentity.Create(string.Empty, string.Empty, -1, string.Empty);

            Assert.True(unknown.IsUnknown);
            Assert.Equal("empreinte inconnue", unknown.Describe());
            Assert.True(new SaveIdentity().IsUnknown);
        }

        [Fact]
        public void Les_arguments_nuls_sont_refuses_explicitement()
        {
            Assert.Throws<ArgumentNullException>(() => SaveIdentity.Create("slot", "session", 1, "Freedom").WriteTo(null));
            Assert.Throws<ArgumentNullException>(() => SaveIdentity.ReadFrom(null));
        }

        private static PacketWriter Forged(string slot, string session, string mode)
        {
            var writer = new PacketWriter();
            writer.WriteString(slot);
            writer.WriteString(session);
            writer.WriteInt(1);
            writer.WriteString(mode);
            writer.WriteLong(0L);
            return writer;
        }
    }
}
