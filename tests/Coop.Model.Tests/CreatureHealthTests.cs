using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// M3/M4, v17 : la santé d'une créature voyage avec son état, et sa mort est une décision du serveur.
    ///
    /// Les trois règles sont celles de la santé des véhicules (v16), et elles ne sont pas décoratives :
    /// la sentinelle `-1` (créature sans `LiveMixin`) ne doit pas devenir « morte », une valeur non finie
    /// est un paquet invalide, et une valeur hors `0..1` est ramenée dans les bornes plutôt que rejetée.
    /// La quatrième est propre aux créatures : le retrait d'une créature est un `EntityDespawn` d'espèce
    /// `Creature`, refusé à l'écriture comme à la lecture pour toute autre valeur.
    /// </summary>
    public sealed class CreatureHealthTests
    {
        private static CreatureStateEntry Entry(float health) => new CreatureStateEntry
        {
            Key = 0x6dc21f3a7b19c480L,
            X = 1f,
            Y = 2f,
            Z = 3f,
            RotW = 1f,
            Health = health,
        };

        private static CreatureStateEntry RoundTrip(float health)
        {
            var bytes = MessageCodec.Encode(new CreatureSnapshotMessage
            {
                ServerTick = 11L,
                Creatures = new[] { Entry(health) },
            }.Write);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            return CreatureSnapshotMessage.Read(reader).Creatures[0];
        }

        [Fact]
        public void La_sante_de_creature_fait_l_aller_retour()
        {
            Assert.Equal(0.42f, RoundTrip(0.42f).Health, 3);
        }

        [Fact]
        public void La_sentinelle_moins_un_traverse_et_ne_devient_pas_une_creature_morte()
        {
            Assert.Equal(-1f, RoundTrip(-1f).Health);
        }

        [Fact]
        public void Une_sante_de_creature_non_finie_est_refusee_a_l_ecriture()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new CreatureSnapshotMessage
            {
                ServerTick = 11L,
                Creatures = new[] { Entry(float.NaN) },
            }.Write));

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new CreatureSnapshotMessage
            {
                ServerTick = 11L,
                Creatures = new[] { Entry(float.PositiveInfinity) },
            }.Write));
        }

        [Fact]
        public void Une_sante_de_creature_hors_bornes_est_ramenee_dans_les_bornes()
        {
            Assert.Equal(1f, RoundTrip(1.5f).Health);
            Assert.Equal(-1f, RoundTrip(-0.5f).Health);
        }

        [Fact]
        public void La_taille_d_une_entree_de_creature_suit_la_sante()
        {
            // 8 (clé) + 8 × 4 (position, quaternion, santé) : c'est ce chiffre qui fixe le plafond de
            // `MaxCreatureStates`, et il doit grossir avec l'entrée plutôt que rester écrit ailleurs.
            Assert.Equal(8 + 8 * 4, Protocol.CreatureStateEntryBytes);
        }

        [Fact]
        public void La_mort_d_une_creature_est_un_retrait_d_espece_creature()
        {
            var bytes = MessageCodec.Encode(new EntityDespawnMessage
            {
                Key = 0x6dc21f3a7b19c480L,
                Kind = EntityKind.Creature,
                Reason = "créature morte (santé 0)",
            }.Write);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            var message = EntityDespawnMessage.Read(reader);

            Assert.Equal(EntityKind.Creature, message.Kind);
            Assert.Equal("créature morte (santé 0)", message.Reason);
        }

        [Fact]
        public void Une_espece_inconnue_reste_refusee_a_l_ecriture_comme_a_la_lecture()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new EntityDespawnMessage
            {
                Key = 1L,
                Kind = 4,
                Reason = "espèce inventée",
            }.Write));

            // Un octet 4 reçu du réseau doit lever, pas être interprété comme un objet.
            var reader = new PacketReader(new byte[] { (byte)MessageType.EntityDespawn, 0, 0, 0, 0, 0, 0, 0, 1, 4, 0, 0 });
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => EntityDespawnMessage.Read(reader));
        }
    }
}
