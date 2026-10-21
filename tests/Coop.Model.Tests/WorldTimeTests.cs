using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// v18 : l'heure du monde voyage du serveur vers les clients. Deux règles comptent : un aller-retour
    /// exact (l'heure est une valeur de jeu, pas un compteur de diagnostic), et un refus net d'une valeur
    /// non finie ou négative — une heure négative n'existe pas, et une heure `NaN` ferait sauter le soleil
    /// à une position indéfinie chez le client.
    /// </summary>
    public sealed class WorldTimeTests
    {
        private static float RoundTrip(float timePassed)
        {
            var bytes = MessageCodec.Encode(new WorldTimeMessage { TimePassed = timePassed }.Write);
            var reader = new PacketReader(bytes);
            reader.ReadByte();
            return WorldTimeMessage.Read(reader).TimePassed;
        }

        [Fact]
        public void L_heure_fait_l_aller_retour()
        {
            Assert.Equal(1234.5f, RoundTrip(1234.5f), 3);
        }

        [Fact]
        public void Une_heure_non_finie_est_refusee_a_l_ecriture()
        {
            Assert.Throws<ProtocolException>(() =>
                MessageCodec.Encode(new WorldTimeMessage { TimePassed = float.NaN }.Write));
            Assert.Throws<ProtocolException>(() =>
                MessageCodec.Encode(new WorldTimeMessage { TimePassed = float.PositiveInfinity }.Write));
        }

        [Fact]
        public void Une_heure_negative_est_refusee_a_l_ecriture()
        {
            Assert.Throws<ProtocolException>(() =>
                MessageCodec.Encode(new WorldTimeMessage { TimePassed = -1f }.Write));
        }

        [Fact]
        public void Une_heure_negative_recue_est_refusee_a_la_lecture()
        {
            // Paquet forgé : opcode puis un float négatif, sans passer par `Write` (qui refuse déjà).
            var payload = BitConverter.GetBytes(-12.5f);
            var bytes = new byte[1 + payload.Length];
            bytes[0] = (byte)MessageType.WorldTime;
            Array.Copy(payload, 0, bytes, 1, payload.Length);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => WorldTimeMessage.Read(reader));
        }

        [Fact]
        public void L_heure_est_annoncee_au_rythme_d_une_fois_par_seconde()
        {
            // 20 Hz / 20 = 1 Hz : le seuil est une décision de bande passante, pas un réglage libre.
            Assert.Equal(20, Protocol.WorldTimeIntervalTicks);
            // 5 s : au-dessus de l'écart stable mesuré entre deux instances (0,6 à 1,1 s), en dessous du
            // visible sur un jour de 1 200 s. Un seuil plus bas faisait réécrire l'heure chaque seconde.
            Assert.Equal(5f, Protocol.WorldTimeApplyThresholdSeconds);
        }
    }
}
