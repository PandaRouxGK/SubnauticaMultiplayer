using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class MessageEdgeCaseTests
    {
        [Fact]
        public void Encoder_une_action_nulle_leve_ArgumentNullException()
        {
            Assert.Throws<System.ArgumentNullException>(() => MessageCodec.Encode(null));
        }

        [Fact]
        public void Une_disparition_avec_une_cle_nulle_est_refusee_a_l_ecriture()
        {
            var despawn = new EntityDespawnMessage
            {
                Key = 0L,
                Kind = EntityKind.Vehicle,
                Reason = "détruit",
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(despawn.Write));
        }

        [Fact]
        public void Une_disparition_recue_avec_une_cle_nulle_est_refusee()
        {
            // On fabrique le paquet à la main : l'écriture refuse désormais la clé 0,
            // donc c'est le seul moyen de vérifier que la lecture ne l'accepte pas non plus.
            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.EntityDespawn);
            writer.WriteLong(0L);
            writer.WriteByte(EntityKind.Item);
            writer.WriteString("ramassé");

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => EntityDespawnMessage.Read(reader));
        }

        [Fact]
        public void Une_disparition_valide_fait_un_aller_retour()
        {
            var despawn = new EntityDespawnMessage
            {
                Key = 0x7ce3b6a1e8e7e312L,
                Kind = EntityKind.Item,
                Reason = "ramassé par 'killi'",
            };

            var reader = new PacketReader(MessageCodec.Encode(despawn.Write));
            Assert.Equal((byte)MessageType.EntityDespawn, reader.ReadByte());
            var read = EntityDespawnMessage.Read(reader);

            Assert.Equal(despawn.Key, read.Key);
            Assert.Equal(EntityKind.Item, read.Kind);
            Assert.Equal(despawn.Reason, read.Reason);
        }

        [Fact]
        public void Un_resultat_de_ramassage_avec_un_nom_trop_long_est_refuse_a_l_ecriture()
        {
            var result = new PickupResultMessage
            {
                ItemKey = 1L,
                TechTypeName = new string('x', Protocol.MaxTechTypeNameLength + 1),
                Granted = true,
                HolderId = 1,
                ClaimedBy = 1,
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(result.Write));
        }

        [Fact]
        public void Un_resultat_de_ramassage_avec_un_nom_trop_long_est_refuse_a_la_lecture()
        {
            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.PickupResult);
            writer.WriteLong(1L);
            writer.WriteString(new string('x', Protocol.MaxTechTypeNameLength + 1));
            writer.WriteBool(true);
            writer.WriteUInt16(1);
            writer.WriteUInt16(1);

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => PickupResultMessage.Read(reader));
        }

        [Fact]
        public void Un_motif_de_deconnexion_trop_long_est_refuse_des_deux_cotes()
        {
            var reason = new string('x', Protocol.MaxReasonTextLength + 1);

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new DisconnectMessage { Reason = reason }.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.Disconnect);
            writer.WriteString(reason);
            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => DisconnectMessage.Read(reader));
        }

        [Fact]
        public void Un_motif_de_depart_trop_long_est_refuse_des_deux_cotes()
        {
            var reason = new string('x', Protocol.MaxReasonTextLength + 1);
            var message = new PlayerLeftMessage { PlayerId = 2, Username = "killi", Reason = reason };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(message.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.PlayerLeft);
            writer.WriteUInt16(2);
            writer.WriteString("killi");
            writer.WriteString(reason);
            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => PlayerLeftMessage.Read(reader));
        }

        [Fact]
        public void Un_motif_de_refus_de_handshake_trop_long_est_refuse_des_deux_cotes()
        {
            var reason = new string('x', Protocol.MaxReasonTextLength + 1);
            var message = new HandshakeResponse { Accepted = false, Reason = reason, ServerTick = 1L };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(message.Write));

            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.HandshakeResponse);
            writer.WriteBool(false);
            writer.WriteString(reason);
            writer.WriteLong(1L);
            writer.WriteUInt16(0);
            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => HandshakeResponse.Read(reader));
        }

        [Fact]
        public void Un_motif_a_la_borne_exacte_passe()
        {
            var reason = new string('m', Protocol.MaxReasonTextLength);
            var bytes = MessageCodec.Encode(new DisconnectMessage { Reason = reason }.Write);
            var reader = new PacketReader(bytes);
            reader.ReadByte();

            Assert.Equal(reason, DisconnectMessage.Read(reader).Reason);
        }

        [Fact]
        public void Une_version_de_jeu_trop_longue_est_refusee_a_l_ecriture()
        {
            var request = MinimalHandshake();
            request.GameVersion = new string('9', Protocol.MaxGameVersionLength + 1);

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(request.Write));
        }

        [Fact]
        public void Une_version_de_jeu_trop_longue_est_refusee_a_la_lecture()
        {
            var writer = ForgeHandshake(new string('9', Protocol.MaxGameVersionLength + 1), "hash");
            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => HandshakeRequest.Read(reader));
        }

        [Fact]
        public void Une_empreinte_de_mods_trop_longue_est_refusee_a_l_ecriture()
        {
            var request = MinimalHandshake();
            request.ModListHash = new string('a', Protocol.MaxModListHashLength + 1);

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(request.Write));
        }

        [Fact]
        public void Une_empreinte_de_mods_trop_longue_est_refusee_a_la_lecture()
        {
            var writer = ForgeHandshake("2019.4.36f1", new string('a', Protocol.MaxModListHashLength + 1));
            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => HandshakeRequest.Read(reader));
        }

        [Fact]
        public void Un_handshake_aux_bornes_exactes_passe_des_deux_cotes()
        {
            var request = MinimalHandshake();
            request.GameVersion = new string('9', Protocol.MaxGameVersionLength);
            request.ModListHash = new string('a', Protocol.MaxModListHashLength);

            var reader = new PacketReader(MessageCodec.Encode(request.Write));
            reader.ReadByte();
            var read = HandshakeRequest.Read(reader);

            Assert.Equal(request.GameVersion, read.GameVersion);
            Assert.Equal(request.ModListHash, read.ModListHash);
        }

        private static HandshakeRequest MinimalHandshake() => new HandshakeRequest
        {
            ProtocolVersion = Protocol.Version,
            GameVersion = "2019.4.36f1",
            ModListHash = "0123456789abcdef",
            Username = "killi",
            Save = SaveIdentity.Create("slot0000", "session", 1, "Freedom"),
        };

        private static PacketWriter ForgeHandshake(string gameVersion, string modListHash)
        {
            // L'écriture refuse ces valeurs : pour vérifier la lecture, le paquet est fabriqué à la main.
            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.HandshakeRequest);
            writer.WriteInt(Protocol.Version);
            writer.WriteString(gameVersion);
            writer.WriteString(modListHash);
            writer.WriteString("killi");
            SaveIdentity.Create("slot0000", "session", 1, "Freedom").WriteTo(writer);
            return writer;
        }

        [Fact]
        public void Un_ecrivain_reutilise_apres_un_refus_repart_sur_un_tampon_vide()
        {
            // Le client et le serveur réutilisent un `PacketWriter` pour chaque envoi. Si un message
            // refusé au milieu de l'écriture laissait ses octets partiels, l'envoi suivant
            // contiendrait un préfixe du message précédent — un paquet que le receveur interpréterait
            // comme un autre type. `MessageCodec.Encode` remet la longueur à zéro avant chaque écriture.
            var writer = new PacketWriter();

            // Ce message écrit l'opcode et l'identifiant **avant** de découvrir que le second
            // paramètre d'animation est trop long : le refus laisse donc des octets partiels.
            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(
                new PlayerHeldItemMessage
                {
                    PlayerId = 1,
                    TechTypeName = "SeaMoth",
                    HoldingParams = new[] { "holding_ok", new string('x', Protocol.MaxTechTypeNameLength + 1) },
                }.Write,
                writer));

            Assert.True(writer.Length > 0, "le refus doit avoir laissé des octets partiels pour que le test ait un sens");

            var ping = new PingMessage { Sequence = 42L, ClientTimeMs = 1234L };
            var reused = MessageCodec.Encode(ping.Write, writer);

            Assert.Equal(MessageCodec.Encode(ping.Write), reused);
            Assert.Equal(reused.Length, writer.Length);
        }

        [Fact]
        public void Une_apparition_sous_la_cle_zero_est_refusee_a_l_ecriture()
        {
            var spawn = ValidSpawn();
            spawn.Key = 0L;

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(spawn.Write));
        }

        [Fact]
        public void Une_apparition_recue_sous_la_cle_zero_est_refusee()
        {
            // L'écriture refuse déjà la clé 0 : on forge le paquet pour vérifier que la lecture
            // ne l'accepte pas non plus, sinon un client hostile créerait un fantôme indéscriptible.
            var definition = EntityDefinition.ForVehicle("SeaMoth");
            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.EntitySpawn);
            writer.WriteLong(0L);
            writer.WriteString("entity-0");
            writer.WriteString(definition.TechTypeName);
            for (var i = 0; i < 7; i++)
            {
                writer.WriteFloat(0f);
            }

            writer.WriteFloat(1f);
            writer.WriteFloat(1f);
            writer.WriteFloat(1f);
            writer.WriteLong(definition.ComputeHash());

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => EntitySpawnMessage.Read(reader));
        }

        [Fact]
        public void Une_apparition_sans_identifiant_est_refusee_a_l_ecriture()
        {
            var spawn = ValidSpawn();
            spawn.UniqueId = string.Empty;

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(spawn.Write));
        }

        [Fact]
        public void Une_apparition_recue_sans_identifiant_est_refusee()
        {
            // L'écriture refuse déjà l'identifiant vide : on forge le paquet pour la lecture.
            var definition = EntityDefinition.ForVehicle("SeaMoth");
            var writer = new PacketWriter();
            writer.WriteByte((byte)MessageType.EntitySpawn);
            writer.WriteLong(1L);
            writer.WriteString(string.Empty);
            writer.WriteString(definition.TechTypeName);
            for (var i = 0; i < 7; i++)
            {
                writer.WriteFloat(0f);
            }

            writer.WriteFloat(1f);
            writer.WriteFloat(1f);
            writer.WriteFloat(1f);
            writer.WriteLong(definition.ComputeHash());

            var reader = new PacketReader(writer.BackingBuffer, 0, writer.Length);
            reader.ReadByte();

            Assert.Throws<ProtocolException>(() => EntitySpawnMessage.Read(reader));
        }

        [Fact]
        public void Une_apparition_sans_empreinte_est_refusee_a_l_ecriture()
        {
            var spawn = ValidSpawn();
            spawn.DefinitionHash = 0L;

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(spawn.Write));
        }

        [Fact]
        public void Une_apparition_avec_une_position_non_finie_est_refusee_a_l_ecriture()
        {
            var spawn = ValidSpawn();
            spawn.X = float.NaN;

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(spawn.Write));
        }

        [Fact]
        public void Une_apparition_avec_une_echelle_infinie_est_refusee_a_l_ecriture()
        {
            var spawn = ValidSpawn();
            spawn.ScaleX = float.PositiveInfinity;

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(spawn.Write));
        }

        [Fact]
        public void Une_correction_avec_une_position_NaN_est_refusee_a_l_ecriture()
        {
            var correction = new PlayerCorrectionMessage
            {
                ServerTick = 1L,
                X = float.NaN,
                Reason = "téléport",
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(correction.Write));
        }

        [Fact]
        public void Une_correction_avec_une_position_infinie_est_refusee_a_l_ecriture()
        {
            var correction = new PlayerCorrectionMessage
            {
                ServerTick = 1L,
                Z = float.NegativeInfinity,
                Reason = "téléport",
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(correction.Write));
        }

        [Fact]
        public void Une_espece_inconnue_dans_une_disparition_est_refusee()
        {
            var despawn = new EntityDespawnMessage
            {
                Key = 1L,
                Kind = EntityKind.None,
                Reason = string.Empty,
            };

            Assert.Throws<ProtocolException>(() => MessageCodec.Encode(despawn.Write));
        }

        private static EntitySpawnMessage ValidSpawn()
        {
            var definition = EntityDefinition.ForVehicle("SeaMoth");
            return new EntitySpawnMessage
            {
                Key = 1L,
                UniqueId = "entity-1",
                TechTypeName = definition.TechTypeName,
                RotW = 1f,
                DefinitionHash = definition.ComputeHash(),
            };
        }
    }
}
