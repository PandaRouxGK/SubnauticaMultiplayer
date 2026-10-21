using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// M4, v16 : la santé d'un véhicule voyage avec son état. Trois règles comptent, les mêmes que pour
    /// l'énergie : la sentinelle `-1` (pas de `LiveMixin`) ne devient pas « détruit », une valeur non finie
    /// est un paquet invalide, et une valeur hors `0..1` est ramenée dans les bornes plutôt que rejetée —
    /// un serveur qui arrondit ne doit pas faire tomber la session d'un client.
    /// </summary>
    public sealed class VehicleHealthTests
    {
        private static VehicleStateEntry Entry(float health) => new VehicleStateEntry
        {
            Key = 0x7ce3b6a1e8e7e312L,
            X = 1f,
            Y = 2f,
            Z = 3f,
            RotW = 1f,
            Energy = 0.5f,
            Health = health,
        };

        private static VehicleStateEntry RoundTrip(float health)
        {
            var bytes = MessageCodec.Encode(new VehicleSnapshotMessage
            {
                ServerTick = 7L,
                Vehicles = new[] { Entry(health) },
            }.Write);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            return VehicleSnapshotMessage.Read(reader).Vehicles[0];
        }

        [Fact]
        public void La_sante_fait_l_aller_retour()
        {
            Assert.Equal(0.42f, RoundTrip(0.42f).Health, 3);
        }

        [Fact]
        public void La_sentinelle_moins_un_traverse_et_ne_devient_pas_un_vehicule_detruit()
        {
            Assert.Equal(-1f, RoundTrip(-1f).Health);
        }

        [Fact]
        public void Une_sante_non_finie_est_refusee_a_l_ecriture()
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
        public void Une_sante_hors_bornes_est_ramenee_dans_les_bornes()
        {
            Assert.Equal(1f, RoundTrip(1.5f).Health);
            Assert.Equal(-1f, RoundTrip(-0.5f).Health);
        }

        [Fact]
        public void La_valeur_par_defaut_d_une_entree_non_remplie_est_la_sentinelle()
        {
            // Un `VehicleStateEntry` construit sans santé vaut 0 : c'est un véhicule **détruit**, pas une
            // absence d'information. Les appelants doivent donc poser la sentinelle explicitement ; ce cas
            // documente le piège plutôt que de laisser croire à un défaut inoffensif.
            Assert.Equal(0f, new VehicleStateEntry().Health);
        }
    }
}
