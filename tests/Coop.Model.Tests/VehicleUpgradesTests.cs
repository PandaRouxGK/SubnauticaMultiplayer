using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// M4, v15 : les modules installés voyagent dans un message **fiable**, séparé du snapshot de position.
    ///
    /// Ce qui compte ici n'est pas le codec mais les règles qui empêchent un faux changement : deux
    /// ensembles identiques doivent produire **la même** forme canonique (sinon le serveur rediffuserait à
    /// chaque balayage), un ensemble trop grand est refusé plutôt que tronqué en silence, et la clé 0 est
    /// interdite des deux côtés — elle désignerait un véhicule que personne ne peut retrouver.
    /// </summary>
    public sealed class VehicleUpgradesTests
    {
        private static VehicleUpgradesMessage Message(params ushort[] types) => new VehicleUpgradesMessage
        {
            VehicleKey = 0x7ce3b6a1e8e7e312L,
            TechTypes = types,
        };

        private static ushort Hull => (ushort)(int)TechTypeProbe.VehicleHullModule1;

        private static ushort Storage => (ushort)(int)TechTypeProbe.VehicleStorageModule;

        [Fact]
        public void Les_modules_font_l_aller_retour()
        {
            var bytes = MessageCodec.Encode(Message(Hull, Storage).Write);

            var reader = new PacketReader(bytes);
            Assert.Equal((byte)MessageType.VehicleUpgrades, reader.ReadByte());

            var read = VehicleUpgradesMessage.Read(reader);
            Assert.Equal(0x7ce3b6a1e8e7e312L, read.VehicleKey);
            Assert.Equal(2, read.TechTypes.Length);
            Assert.Contains(Hull, read.TechTypes);
            Assert.Contains(Storage, read.TechTypes);
        }

        [Fact]
        public void Un_vehicule_sans_module_fait_l_aller_retour()
        {
            var bytes = MessageCodec.Encode(Message().Write);

            var reader = new PacketReader(bytes);
            reader.ReadByte();
            var read = VehicleUpgradesMessage.Read(reader);

            Assert.Equal(0x7ce3b6a1e8e7e312L, read.VehicleKey);
            Assert.Empty(read.TechTypes);
        }

        [Fact]
        public void La_forme_canonique_est_triee_et_sans_doublon()
        {
            var raw = new[] { Storage, Hull, Storage, Hull };
            var canonical = VehicleUpgradesMessage.Canonicalize(raw);

            Assert.Equal(2, canonical.Length);
            Assert.Equal(Hull <= Storage ? Hull : Storage, canonical[0]);

            // L'ordre d'itération ne doit pas fabriquer un « changement » : deux formes identiques.
            var again = VehicleUpgradesMessage.Canonicalize(new[] { Hull, Storage });
            Assert.True(VehicleUpgradesMessage.SameSet(canonical, again));
        }

        [Fact]
        public void Un_ensemble_trop_grand_est_refuse_a_l_ecriture()
        {
            var tooMany = new ushort[Protocol.MaxVehicleUpgrades + 1];
            for (var i = 0; i < tooMany.Length; i++)
            {
                tooMany[i] = (ushort)(i + 1);
            }

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(Message(tooMany).Write));
            Assert.Throws<ProtocolException>(() => VehicleUpgradesMessage.Canonicalize(tooMany));
        }

        [Fact]
        public void La_cle_zero_est_refusee_des_deux_cotes()
        {
            var message = new VehicleUpgradesMessage
            {
                VehicleKey = 0L,
                TechTypes = new[] { Hull },
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(message.Write));

            // L'octet d'opcode est présent, la clé 0 le suit : la lecture doit refuser, pas inventer
            // un véhicule à la clé nulle.
            var raw = MessageCodec.Encode(writer =>
            {
                writer.WriteByte((byte)MessageType.VehicleUpgrades);
                writer.WriteLong(0L);
                writer.WriteByte(0);
            });

            var reader = new PacketReader(raw);
            reader.ReadByte();
            Assert.Throws<ProtocolException>(() => VehicleUpgradesMessage.Read(reader));
        }

        [Fact]
        public void SameSet_distingue_un_module_manquant_d_un_module_remplace()
        {
            var two = VehicleUpgradesMessage.Canonicalize(new[] { Hull, Storage });
            var one = VehicleUpgradesMessage.Canonicalize(new[] { Hull });

            Assert.True(VehicleUpgradesMessage.SameSet(two, VehicleUpgradesMessage.Canonicalize(new[] { Storage, Hull })));
            Assert.False(VehicleUpgradesMessage.SameSet(two, one));
            Assert.False(VehicleUpgradesMessage.SameSet(one, Array.Empty<ushort>()));
            Assert.True(VehicleUpgradesMessage.SameSet(Array.Empty<ushort>(), Array.Empty<ushort>()));
        }

        /// <summary>
        /// Les deux `TechType` utilisés par les tests, en valeur brute : le projet de tests ne référence pas
        /// les assemblies du jeu, donc les entiers sont figés ici — et un changement de ces valeurs dans une
        /// sauvegarde ne doit pas transformer un test en réussite silencieuse (seul le codec est testé).
        /// </summary>
        private static class TechTypeProbe
        {
            internal const int VehicleHullModule1 = 312;
            internal const int VehicleStorageModule = 318;
        }
    }
}
