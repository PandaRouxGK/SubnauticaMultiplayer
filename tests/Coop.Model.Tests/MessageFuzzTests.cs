using System;
using System.Collections.Generic;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// Un paquet reçu peut être n'importe quoi. Le contrat de lecture est : soit un message,
    /// soit <see cref="ProtocolException"/> — jamais une exception d'implémentation qui ferait
    /// remonter un paquet hostile jusqu'à la boucle réseau.
    /// </summary>
    public sealed class MessageFuzzTests
    {
        private static readonly (string Name, Action<PacketReader> Read)[] Readers =
        {
            ("HandshakeRequest", r => HandshakeRequest.Read(r)),
            ("HandshakeResponse", r => HandshakeResponse.Read(r)),
            ("Ping", r => PingMessage.Read(r)),
            ("Pong", r => PongMessage.Read(r)),
            ("Disconnect", r => DisconnectMessage.Read(r)),
            ("PlayerJoined", r => PlayerJoinedMessage.Read(r)),
            ("PlayerLeft", r => PlayerLeftMessage.Read(r)),
            ("PlayerPosition", r => PlayerPositionMessage.Read(r)),
            ("PlayersSnapshot", r => PlayersSnapshotMessage.Read(r)),
            ("PlayerEquipment", r => PlayerEquipmentMessage.Read(r)),
            ("PlayerHeldItem", r => PlayerHeldItemMessage.Read(r)),
            ("PlayerColor", r => PlayerColorMessage.Read(r)),
            ("CreatureSnapshot", r => CreatureSnapshotMessage.Read(r)),
            ("VehicleSnapshot", r => VehicleSnapshotMessage.Read(r)),
            ("PickupClaim", r => PickupClaimMessage.Read(r)),
            ("PickupResult", r => PickupResultMessage.Read(r)),
            ("VehicleControl", r => VehicleControlMessage.Read(r)),
            ("VehicleControlResult", r => VehicleControlResultMessage.Read(r)),
            ("EntitySpawn", r => EntitySpawnMessage.Read(r)),
            ("PlayerCorrection", r => PlayerCorrectionMessage.Read(r)),
            ("VehicleRelease", r => VehicleReleaseMessage.Read(r)),
            ("EntityDespawn", r => EntityDespawnMessage.Read(r)),
        };

        /// <summary>
        /// Le harnais de fuzz ne vaut que s'il couvre **tous** les messages. Un opcode ajouté à
        /// `MessageType` sans entrée ici passerait les tests sans jamais être lu par personne :
        /// l'égalité nom pour nom rend cet oubli impossible à commettre silencieusement.
        /// </summary>
        [Fact]
        public void La_liste_des_lecteurs_couvre_exactement_les_opcodes_de_MessageType()
        {
            var opcodes = System.Enum.GetNames(typeof(MessageType));
            var readers = new string[Readers.Length];

            for (var index = 0; index < Readers.Length; index++)
            {
                readers[index] = Readers[index].Name;
            }

            Assert.Equal(
                System.Linq.Enumerable.OrderBy(opcodes, name => name),
                System.Linq.Enumerable.OrderBy(readers, name => name));
        }

        [Fact]
        public void Aucun_paquet_aleatoire_ne_leve_autre_chose_qu_une_ProtocolException()
        {
            var random = new Random(20261019);
            var failures = new List<string>();

            for (var iteration = 0; iteration < 500; iteration++)
            {
                var bytes = new byte[random.Next(1, 160)];
                random.NextBytes(bytes);

                foreach (var (name, read) in Readers)
                {
                    var reader = new PacketReader(bytes);
                    try
                    {
                        reader.ReadByte(); // opcode, comme le fait l'appelant
                        read(reader);
                    }
                    catch (ProtocolException)
                    {
                        // Comportement attendu d'un paquet malformé.
                    }
                    catch (Exception exception)
                    {
                        failures.Add(
                            $"{name} (itération {iteration}, {bytes.Length} octets) : " +
                            $"{exception.GetType().Name} — {exception.Message}");
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void Un_paquet_reduit_a_l_opcode_est_refuse_par_les_22_lecteurs()
        {
            // Un opcode seul est le cas le plus simple d'un paquet tronqué : chaque lecteur doit
            // s'arrêter sur son premier champ manquant, avec un `ProtocolException` nommée. Un
            // `NullReferenceException` ou un `IndexOutOfRangeException` remonterait jusqu'à la
            // boucle réseau, qui ne l'attrape pas.
            var failures = new List<string>();

            foreach (var (name, read) in Readers)
            {
                var reader = new PacketReader(new[] { (byte)MessageType.Ping });
                reader.ReadByte();

                try
                {
                    read(reader);
                    failures.Add($"{name} : paquet vide accepté (message relu au lieu d'un refus)");
                }
                catch (ProtocolException)
                {
                    // Refus attendu.
                }
                catch (Exception exception)
                {
                    failures.Add($"{name} : {exception.GetType().Name} — {exception.Message}");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            Assert.Equal(22, Readers.Length);
        }

        [Fact]
        public void Les_plages_aleatoires_du_PacketReader_sont_acceptees_ou_refusees_proprement()
        {
            var random = new Random(42);
            var failures = new List<string>();

            for (var iteration = 0; iteration < 2000; iteration++)
            {
                var data = new byte[random.Next(0, 65)];
                var offset = random.Next(-3, data.Length + 4);
                var length = random.Next(-3, data.Length + 4);

                try
                {
                    var reader = new PacketReader(data, offset, length);
                    while (reader.Remaining > 0)
                    {
                        reader.ReadByte();
                    }

                    Assert.Equal(0, reader.Remaining);
                }
                catch (ProtocolException)
                {
                    // Plage invalide : refus attendu.
                }
                catch (Exception exception)
                {
                    failures.Add(
                        $"offset={offset}, length={length}, taille={data.Length} : " +
                        $"{exception.GetType().Name} — {exception.Message}");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void Une_plage_debordante_n_est_jamais_acceptee()
        {
            var data = new byte[8];
            var cases = new (int Offset, int Length)[]
            {
                (int.MaxValue, 1),
                (1, int.MaxValue),
                (int.MaxValue, int.MaxValue),
                (-1, int.MaxValue),
            };

            foreach (var (offset, length) in cases)
            {
                Assert.Throws<ProtocolException>(() => new PacketReader(data, offset, length));
            }
        }
    }
}
