using System.Diagnostics;
using Coop.Model;
using LiteNetLib;

namespace NetProbe
{
    /// <summary>
    /// Outil de développement (hors mod) : permet de vérifier le protocole M1 sans lancer un
    /// second jeu Subnautica. Il utilise exactement le même code de protocole que les plugins.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Usage();
                return 2;
            }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "selftest":
                        return SelfTest();

                    case "probe":
                        if (args.Length < 4)
                        {
                            Usage();
                            return 2;
                        }

                        var seconds = args.Length > 4 ? int.Parse(args[4]) : 10;
                        var gameVersion = args.Length > 5 ? args[5] : string.Empty;
                        var modHash = args.Length > 6 ? args[6] : string.Empty;
                        return Probe(args[1], int.Parse(args[2]), args[3], seconds, gameVersion, modHash);

                    default:
                        Usage();
                        return 2;
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"erreur fatale : {exception}");
                return 3;
            }
        }

        private static void Usage()
        {
            Console.WriteLine("usage:");
            Console.WriteLine("  NetProbe selftest");
            Console.WriteLine("  NetProbe probe <hote> <port> <pseudo> [secondes] [versionJeu] [modHash]");
            Console.WriteLine();
            Console.WriteLine("  versionJeu et modHash doivent correspondre au serveur, sinon le handshake est refusé.");
            Console.WriteLine("  Le serveur affiche les siens au démarrage (ligne [boot] jeu=... modHash=...).");
        }

        // ---- Autotest de sérialisation ---------------------------------------------------

        private static int SelfTest()
        {
            var failures = 0;

            void Check(bool condition, string label)
            {
                Console.WriteLine((condition ? "  OK    " : "  ECHEC ") + label);
                if (!condition)
                {
                    failures++;
                }
            }

            var handshake = new HandshakeRequest
            {
                ProtocolVersion = Protocol.Version,
                GameVersion = "2.0.0.1",
                ModListHash = "a1b2c3d4e5f60718",
                Username = "Élodie-Ünïcode",
            };

            var handshakeBytes = MessageCodec.Encode(handshake.Write);
            Check(handshakeBytes[0] == (byte)MessageType.HandshakeRequest, "opcode HandshakeRequest");

            var handshakeReader = new PacketReader(handshakeBytes);
            handshakeReader.ReadByte();
            var handshakeRoundTrip = HandshakeRequest.Read(handshakeReader);
            Check(
                handshakeRoundTrip.ProtocolVersion == handshake.ProtocolVersion
                && handshakeRoundTrip.GameVersion == handshake.GameVersion
                && handshakeRoundTrip.ModListHash == handshake.ModListHash
                && handshakeRoundTrip.Username == handshake.Username,
                "round-trip HandshakeRequest (accents inclus)");

            var response = new HandshakeResponse
            {
                Accepted = true,
                Reason = string.Empty,
                ServerTick = 1234567890123L,
                AssignedPlayerId = 7,
            };
            var responseBytes = MessageCodec.Encode(response.Write);
            Check(responseBytes[0] == (byte)MessageType.HandshakeResponse, "opcode HandshakeResponse");
            var responseReader = new PacketReader(responseBytes);
            responseReader.ReadByte();
            var responseRoundTrip = HandshakeResponse.Read(responseReader);
            Check(
                responseRoundTrip.Accepted == response.Accepted
                && responseRoundTrip.Reason == response.Reason
                && responseRoundTrip.ServerTick == response.ServerTick
                && responseRoundTrip.AssignedPlayerId == 7,
                "round-trip HandshakeResponse (long sur 64 bits + AssignedPlayerId)");

            var refusal = new HandshakeResponse { Accepted = false, Reason = "liste de mods différente", ServerTick = 1 };
            var refusalReader = new PacketReader(MessageCodec.Encode(refusal.Write));
            refusalReader.ReadByte();
            var refusalRoundTrip = HandshakeResponse.Read(refusalReader);
            Check(!refusalRoundTrip.Accepted && refusalRoundTrip.Reason == refusal.Reason, "round-trip refus de handshake");

            var ping = new PingMessage { Sequence = 42, ClientTimeMs = -1 };
            var pingReader = new PacketReader(MessageCodec.Encode(ping.Write));
            Check(pingReader.ReadByte() == (byte)MessageType.Ping, "opcode Ping");
            var pingRoundTrip = PingMessage.Read(pingReader);
            Check(pingRoundTrip.Sequence == ping.Sequence && pingRoundTrip.ClientTimeMs == -1, "round-trip Ping (valeur négative)");

            var pong = new PongMessage { Sequence = 7, ClientTimeMs = 1000, ServerTick = 9876543210L, ServerTimeMs = 2000 };
            var pongReader = new PacketReader(MessageCodec.Encode(pong.Write));
            Check(pongReader.ReadByte() == (byte)MessageType.Pong, "opcode Pong");
            var pongRoundTrip = PongMessage.Read(pongReader);
            Check(
                pongRoundTrip.Sequence == pong.Sequence
                && pongRoundTrip.ClientTimeMs == pong.ClientTimeMs
                && pongRoundTrip.ServerTick == pong.ServerTick
                && pongRoundTrip.ServerTimeMs == pong.ServerTimeMs,
                "round-trip Pong");

            var disconnect = new DisconnectMessage { Reason = "serveur plein" };
            var disconnectReader = new PacketReader(MessageCodec.Encode(disconnect.Write));
            Check(disconnectReader.ReadByte() == (byte)MessageType.Disconnect, "opcode Disconnect");
            Check(DisconnectMessage.Read(disconnectReader).Reason == disconnect.Reason, "round-trip Disconnect");

            var joined = new PlayerJoinedMessage
            {
                PlayerId = 3,
                Username = "Capitaine-Nautilus",
                X = -114.46f,
                Y = 2.29f,
                Z = -54.21f,
                RotX = 0f,
                RotY = 0.7071f,
                RotZ = 0f,
                RotW = 0.7071f,
            };
            var joinedReader = new PacketReader(MessageCodec.Encode(joined.Write));
            Check(joinedReader.ReadByte() == (byte)MessageType.PlayerJoined, "opcode PlayerJoined");
            var joinedRt = PlayerJoinedMessage.Read(joinedReader);
            Check(
                joinedRt.PlayerId == 3 && joinedRt.Username == joined.Username
                && Math.Abs(joinedRt.X - joined.X) < 0.0001f && Math.Abs(joinedRt.RotW - joined.RotW) < 0.0001f,
                "round-trip PlayerJoined");

            var left = new PlayerLeftMessage { PlayerId = 3, Username = "Capitaine-Nautilus", Reason = "Timeout" };
            var leftReader = new PacketReader(MessageCodec.Encode(left.Write));
            Check(leftReader.ReadByte() == (byte)MessageType.PlayerLeft, "opcode PlayerLeft");
            var leftRt = PlayerLeftMessage.Read(leftReader);
            Check(leftRt.PlayerId == 3 && leftRt.Username == left.Username && leftRt.Reason == "Timeout", "round-trip PlayerLeft");

            var pos = new PlayerPositionMessage
            {
                Sequence = 999,
                X = 12.5f,
                Y = -30.25f,
                Z = 4.75f,
                RotX = 0.1f,
                RotY = 0.2f,
                RotZ = 0.3f,
                RotW = 0.9f,
                MoveSpeedX = 1.25f,
                MoveSpeedY = -0.5f,
                MoveSpeedZ = 4.5f,
                ViewPitch = 28.5f,
                IsUnderwater = true,
                OnSurface = false,
                ActionFlags = PlayerActionFlags.UsingTool | PlayerActionFlags.InSeamoth,
            };
            var posReader = new PacketReader(MessageCodec.Encode(pos.Write));
            Check(posReader.ReadByte() == (byte)MessageType.PlayerPosition, "opcode PlayerPosition");
            var posRt = PlayerPositionMessage.Read(posReader);
            Check(
                posRt.Sequence == 999
                && Math.Abs(posRt.Y - (-30.25f)) < 0.0001f
                && Math.Abs(posRt.MoveSpeedX - 1.25f) < 0.0001f
                && Math.Abs(posRt.MoveSpeedZ - 4.5f) < 0.0001f
                && Math.Abs(posRt.ViewPitch - 28.5f) < 0.0001f
                && posRt.IsUnderwater && !posRt.OnSurface
                && posRt.ActionFlags == (PlayerActionFlags.UsingTool | PlayerActionFlags.InSeamoth),
                "round-trip PlayerPosition (avec paramètres d'animation et drapeaux d'action)");

            var nanPosThrew = false;
            try
            {
                var badPos = new PlayerPositionMessage { Sequence = 1, X = float.NaN, RotW = 1f };
                var badReader = new PacketReader(MessageCodec.Encode(badPos.Write));
                badReader.ReadByte();
                PlayerPositionMessage.Read(badReader);
            }
            catch (ProtocolException)
            {
                nanPosThrew = true;
            }

            Check(nanPosThrew, "PlayerPosition avec NaN -> ProtocolException");

            var nanAnimThrew = false;
            try
            {
                var badAnim = new PlayerPositionMessage { Sequence = 2, RotW = 1f, ViewPitch = float.PositiveInfinity };
                var badAnimReader = new PacketReader(MessageCodec.Encode(badAnim.Write));
                badAnimReader.ReadByte();
                PlayerPositionMessage.Read(badAnimReader);
            }
            catch (ProtocolException)
            {
                nanAnimThrew = true;
            }

            Check(nanAnimThrew, "PlayerPosition avec ViewPitch Infinity -> ProtocolException");

            var snap = new PlayersSnapshotMessage
            {
                ServerTick = 4200,
                Players = new[]
                {
                    new PlayerStateEntry
                    {
                        PlayerId = 0,
                        X = -114f,
                        Y = 2f,
                        Z = -54f,
                        RotW = 1f,
                        MoveSpeedZ = 3.2f,
                        ViewPitch = -15f,
                        IsUnderwater = true,
                        ActionFlags = PlayerActionFlags.UsingPda,
                    },
                    new PlayerStateEntry
                    {
                        PlayerId = 1,
                        X = -110f,
                        Y = 2f,
                        Z = -50f,
                        RotY = 1f,
                        MoveSpeedX = -1.0f,
                        OnSurface = true,
                        ActionFlags = PlayerActionFlags.InExosuit,
                    },
                },
            };
            var snapReader = new PacketReader(MessageCodec.Encode(snap.Write));
            Check(snapReader.ReadByte() == (byte)MessageType.PlayersSnapshot, "opcode PlayersSnapshot");
            var snapRt = PlayersSnapshotMessage.Read(snapReader);
            Check(
                snapRt.ServerTick == 4200 && snapRt.Players.Length == 2
                && snapRt.Players[0].PlayerId == 0 && snapRt.Players[1].PlayerId == 1
                && Math.Abs(snapRt.Players[1].X - (-110f)) < 0.0001f
                && Math.Abs(snapRt.Players[0].MoveSpeedZ - 3.2f) < 0.0001f
                && Math.Abs(snapRt.Players[0].ViewPitch - (-15f)) < 0.0001f
                && snapRt.Players[0].IsUnderwater && snapRt.Players[1].OnSurface
                && snapRt.Players[0].ActionFlags == PlayerActionFlags.UsingPda
                && snapRt.Players[1].ActionFlags == PlayerActionFlags.InExosuit,
                "round-trip PlayersSnapshot (2 joueurs + animation + drapeaux d'action)");

            var equip = new PlayerEquipmentMessage
            {
                PlayerId = 2,
                TechTypeNames = new[] { "RadiationSuit", "RadiationHelmet", "RadiationGloves", "HighCapacityTank", "Fins" },
            };
            var equipReader = new PacketReader(MessageCodec.Encode(equip.Write));
            Check(equipReader.ReadByte() == (byte)MessageType.PlayerEquipment, "opcode PlayerEquipment");
            var equipRt = PlayerEquipmentMessage.Read(equipReader);
            Check(
                equipRt.PlayerId == 2 && equipRt.TechTypeNames.Length == 5
                && equipRt.TechTypeNames[0] == "RadiationSuit"
                && equipRt.TechTypeNames[4] == "Fins",
                "round-trip PlayerEquipment (5 pièces)");

            var emptyEquip = new PlayerEquipmentMessage { PlayerId = 9, TechTypeNames = Array.Empty<string>() };
            var emptyEquipReader = new PacketReader(MessageCodec.Encode(emptyEquip.Write));
            emptyEquipReader.ReadByte();
            var emptyEquipRt = PlayerEquipmentMessage.Read(emptyEquipReader);
            Check(emptyEquipRt.PlayerId == 9 && emptyEquipRt.TechTypeNames.Length == 0, "round-trip PlayerEquipment (vide)");

            var oversizedEquipThrew = false;
            try
            {
                var tooMany = new string[Protocol.MaxEquipmentSlots + 1];
                for (var i = 0; i < tooMany.Length; i++)
                {
                    tooMany[i] = "Fins";
                }

                MessageCodec.Encode(new PlayerEquipmentMessage { PlayerId = 1, TechTypeNames = tooMany }.Write);
            }
            catch (ProtocolException)
            {
                oversizedEquipThrew = true;
            }

            Check(oversizedEquipThrew, "PlayerEquipment (> MaxEquipmentSlots) -> ProtocolException");

            // Objet tenu : l'outil en main, et les paramètres `holding_*` que le jeu a lui-même posés.
            var held = new PlayerHeldItemMessage
            {
                PlayerId = 2,
                TechTypeName = "Welder",
                HoldingParams = new[] { "holding_welder" },
            };
            var heldReader = new PacketReader(MessageCodec.Encode(held.Write));
            Check(heldReader.ReadByte() == (byte)MessageType.PlayerHeldItem, "opcode PlayerHeldItem");
            var heldRt = PlayerHeldItemMessage.Read(heldReader);
            Check(
                heldRt.PlayerId == 2 && heldRt.TechTypeName == "Welder"
                && heldRt.HoldingParams.Length == 1 && heldRt.HoldingParams[0] == "holding_welder",
                "round-trip PlayerHeldItem (outil tenu)");

            var emptyHeld = new PlayerHeldItemMessage
            {
                PlayerId = 5,
                TechTypeName = string.Empty,
                HoldingParams = Array.Empty<string>(),
            };
            var emptyHeldReader = new PacketReader(MessageCodec.Encode(emptyHeld.Write));
            emptyHeldReader.ReadByte();
            var emptyHeldRt = PlayerHeldItemMessage.Read(emptyHeldReader);
            Check(
                emptyHeldRt.PlayerId == 5 && emptyHeldRt.TechTypeName == string.Empty
                && emptyHeldRt.HoldingParams.Length == 0,
                "round-trip PlayerHeldItem (rien en main)");

            var oversizedHeldThrew = false;
            try
            {
                var tooManyParams = new string[Protocol.MaxActionParams + 1];
                for (var i = 0; i < tooManyParams.Length; i++)
                {
                    tooManyParams[i] = "holding_x";
                }

                MessageCodec.Encode(new PlayerHeldItemMessage { PlayerId = 1, HoldingParams = tooManyParams }.Write);
            }
            catch (ProtocolException)
            {
                oversizedHeldThrew = true;
            }

            Check(oversizedHeldThrew, "PlayerHeldItem (> MaxActionParams) -> ProtocolException");

            // Couleur de joueur (§27) : assignée par le serveur, elle voyage en fiable comme la tenue.
            var color = new PlayerColorMessage { PlayerId = 3, ColorIndex = 5 };
            var colorReader = new PacketReader(MessageCodec.Encode(color.Write));
            Check(colorReader.ReadByte() == (byte)MessageType.PlayerColor, "opcode PlayerColor");
            var colorRoundTrip = PlayerColorMessage.Read(colorReader);
            Check(
                colorRoundTrip.PlayerId == 3 && colorRoundTrip.ColorIndex == 5,
                "round-trip PlayerColor (joueur + indice)");

            // La palette et son compte doivent rester d'accord : une couleur ajoutée sans toucher au
            // compte ferait sortir `PlayerColorAt` de la palette, et personne ne s'en apercevrait
            // avant de voir un avatar de la mauvaise couleur.
            var paletteSized = Protocol.PlayerColorsRgb.Length == Protocol.PlayerColorCount * 3;
            Check(paletteSized, "palette : 3 composantes par couleur de PlayerColorCount");
            if (paletteSized)
            {
                Protocol.PlayerColorAt(0, out var r0, out var g0, out var b0);
                Protocol.PlayerColorAt(Protocol.PlayerColorCount, out var rWrap, out var gWrap, out var bWrap);
                Protocol.PlayerColorAt(7, out var r7, out var g7, out var b7);
                Check(
                    r0 == Protocol.PlayerColorsRgb[0] && g0 == Protocol.PlayerColorsRgb[1] && b0 == Protocol.PlayerColorsRgb[2]
                    && r0 == rWrap && g0 == gWrap && b0 == bWrap
                    && r7 == Protocol.PlayerColorsRgb[7 * 3] && g7 == Protocol.PlayerColorsRgb[7 * 3 + 1] && b7 == Protocol.PlayerColorsRgb[7 * 3 + 2],
                    "PlayerColorAt : composantes exactes + bouclage des indices hors bornes");
            }

            // Créatures (§28) : l'état que le serveur simule, avec sa clé stable sur 64 bits.
            var creatureSnapshot = new CreatureSnapshotMessage
            {
                ServerTick = 987654321L,
                Creatures = new[]
                {
                    // Une clé négative à dessein : un hachage 64 bits est signé, et un aller-retour qui
                    // la transformerait en valeur positive signalerait une perte d'information.
                    new CreatureStateEntry { Key = -1L, X = -112.5f, Y = 2.25f, Z = -52.75f, RotY = 0.7071f, RotW = 0.7071f },
                    new CreatureStateEntry { Key = long.MaxValue, X = 1f, Y = -2f, Z = 3f, RotX = 0.5f, RotZ = -0.5f, RotW = 1f },
                },
            };
            var creatureReader = new PacketReader(MessageCodec.Encode(creatureSnapshot.Write));
            Check(creatureReader.ReadByte() == (byte)MessageType.CreatureSnapshot, "opcode CreatureSnapshot");
            var creatureRoundTrip = CreatureSnapshotMessage.Read(creatureReader);
            Check(
                creatureRoundTrip.ServerTick == 987654321L && creatureRoundTrip.Creatures.Length == 2
                && creatureRoundTrip.Creatures[0].Key == -1L
                && creatureRoundTrip.Creatures[1].Key == long.MaxValue
                && Math.Abs(creatureRoundTrip.Creatures[0].X - (-112.5f)) < 0.0001f
                && Math.Abs(creatureRoundTrip.Creatures[0].Z - (-52.75f)) < 0.0001f
                && Math.Abs(creatureRoundTrip.Creatures[1].RotX - 0.5f) < 0.0001f
                && Math.Abs(creatureRoundTrip.Creatures[1].RotZ - (-0.5f)) < 0.0001f,
                "round-trip CreatureSnapshot (2 créatures, clé 64 bits signée)");

            var emptyCreatureSnapshot = new CreatureSnapshotMessage { ServerTick = 5L, Creatures = Array.Empty<CreatureStateEntry>() };
            var emptyCreatureReader = new PacketReader(MessageCodec.Encode(emptyCreatureSnapshot.Write));
            emptyCreatureReader.ReadByte();
            var emptyCreatureRoundTrip = CreatureSnapshotMessage.Read(emptyCreatureReader);
            Check(
                emptyCreatureRoundTrip.ServerTick == 5L && emptyCreatureRoundTrip.Creatures.Length == 0,
                "round-trip CreatureSnapshot (aucune créature pertinente)");

            var oversizedCreaturesThrew = false;
            try
            {
                var tooManyCreatures = new CreatureStateEntry[Protocol.MaxCreatureStates + 1];
                for (var i = 0; i < tooManyCreatures.Length; i++)
                {
                    tooManyCreatures[i] = new CreatureStateEntry { Key = i, X = 1f, Y = 2f, Z = 3f, RotW = 1f };
                }

                MessageCodec.Encode(new CreatureSnapshotMessage { ServerTick = 1L, Creatures = tooManyCreatures }.Write);
            }
            catch (ProtocolException)
            {
                oversizedCreaturesThrew = true;
            }

            Check(
                oversizedCreaturesThrew,
                "CreatureSnapshot (> MaxCreatureStates) -> ProtocolException");

            // Une créature non finie doit être refusée à la lecture : une position NaN venue du réseau
            // ne doit pas atterrir dans le monde du client.
            var nonFiniteCreatureThrew = false;
            try
            {
                var nonFinite = MessageCodec.Encode(new CreatureSnapshotMessage
                {
                    ServerTick = 2L,
                    Creatures = new[] { new CreatureStateEntry { Key = 7L, X = float.NaN, Y = 1f, Z = 1f, RotW = 1f } },
                }.Write);
                var nonFiniteReader = new PacketReader(nonFinite);
                nonFiniteReader.ReadByte();
                CreatureSnapshotMessage.Read(nonFiniteReader);
            }
            catch (ProtocolException)
            {
                nonFiniteCreatureThrew = true;
            }

            Check(nonFiniteCreatureThrew, "CreatureSnapshot (position NaN) -> ProtocolException à la lecture");

            var truncatedCreatureThrew = false;
            try
            {
                var fullCreaturePacket = MessageCodec.Encode(creatureSnapshot.Write);
                var cutCreaturePacket = new byte[fullCreaturePacket.Length - 4];
                Array.Copy(fullCreaturePacket, cutCreaturePacket, cutCreaturePacket.Length);
                var cutReader = new PacketReader(cutCreaturePacket);
                cutReader.ReadByte();
                CreatureSnapshotMessage.Read(cutReader);
            }
            catch (ProtocolException)
            {
                truncatedCreatureThrew = true;
            }

            Check(truncatedCreatureThrew, "CreatureSnapshot tronqué -> ProtocolException");

            // Véhicules (M4) : même forme de clé que les créatures, mais un opcode à part — un client qui
            // ne connaît pas ce flux laisserait les véhicules du serveur simulés chez lui.
            var vehicleSnapshot = new VehicleSnapshotMessage
            {
                ServerTick = 424242L,
                Vehicles = new[]
                {
                    new VehicleStateEntry { Key = -2L, X = 12.5f, Y = -30.25f, Z = 4.75f, RotY = 0.7071f, RotW = 0.7071f },
                    new VehicleStateEntry { Key = long.MaxValue, X = -1f, Y = 2f, Z = -3f, RotX = -0.5f, RotZ = 0.5f, RotW = 1f },
                },
            };
            var vehicleReader = new PacketReader(MessageCodec.Encode(vehicleSnapshot.Write));
            Check(vehicleReader.ReadByte() == (byte)MessageType.VehicleSnapshot, "opcode VehicleSnapshot");
            var vehicleRoundTrip = VehicleSnapshotMessage.Read(vehicleReader);
            Check(
                vehicleRoundTrip.ServerTick == 424242L && vehicleRoundTrip.Vehicles.Length == 2
                && vehicleRoundTrip.Vehicles[0].Key == -2L
                && vehicleRoundTrip.Vehicles[1].Key == long.MaxValue
                && Math.Abs(vehicleRoundTrip.Vehicles[0].X - 12.5f) < 0.0001f
                && Math.Abs(vehicleRoundTrip.Vehicles[0].Y - (-30.25f)) < 0.0001f
                && Math.Abs(vehicleRoundTrip.Vehicles[0].RotW - 0.7071f) < 0.0001f
                && Math.Abs(vehicleRoundTrip.Vehicles[1].RotX - (-0.5f)) < 0.0001f,
                "round-trip VehicleSnapshot (2 véhicules, clé 64 bits signée)");

            var emptyVehicleSnapshot = new VehicleSnapshotMessage { ServerTick = 7L, Vehicles = Array.Empty<VehicleStateEntry>() };
            var emptyVehicleReader = new PacketReader(MessageCodec.Encode(emptyVehicleSnapshot.Write));
            emptyVehicleReader.ReadByte();
            var emptyVehicleRoundTrip = VehicleSnapshotMessage.Read(emptyVehicleReader);
            Check(
                emptyVehicleRoundTrip.ServerTick == 7L && emptyVehicleRoundTrip.Vehicles.Length == 0,
                "round-trip VehicleSnapshot (aucun véhicule pertinent)");

            var oversizedVehiclesThrew = false;
            try
            {
                var tooManyVehicles = new VehicleStateEntry[Protocol.MaxVehicleStates + 1];
                for (var i = 0; i < tooManyVehicles.Length; i++)
                {
                    tooManyVehicles[i] = new VehicleStateEntry { Key = i + 1, X = 1f, Y = 2f, Z = 3f, RotW = 1f };
                }

                MessageCodec.Encode(new VehicleSnapshotMessage { ServerTick = 1L, Vehicles = tooManyVehicles }.Write);
            }
            catch (ProtocolException)
            {
                oversizedVehiclesThrew = true;
            }

            Check(
                oversizedVehiclesThrew,
                "VehicleSnapshot (> MaxVehicleStates) -> ProtocolException");

            var nonFiniteVehicleThrew = false;
            try
            {
                var nonFinite = MessageCodec.Encode(new VehicleSnapshotMessage
                {
                    ServerTick = 3L,
                    Vehicles = new[] { new VehicleStateEntry { Key = 9L, X = 1f, Y = float.PositiveInfinity, Z = 1f, RotW = 1f } },
                }.Write);
                var nonFiniteReader = new PacketReader(nonFinite);
                nonFiniteReader.ReadByte();
                VehicleSnapshotMessage.Read(nonFiniteReader);
            }
            catch (ProtocolException)
            {
                nonFiniteVehicleThrew = true;
            }

            Check(nonFiniteVehicleThrew, "VehicleSnapshot (position infinie) -> ProtocolException à la lecture");

            var fullVehicleSnapshot = new VehicleSnapshotMessage
            {
                ServerTick = 1L,
                Vehicles = new VehicleStateEntry[Protocol.MaxVehicleStates],
            };
            for (var i = 0; i < fullVehicleSnapshot.Vehicles.Length; i++)
            {
                fullVehicleSnapshot.Vehicles[i] = new VehicleStateEntry
                {
                    Key = i + 1,
                    X = i, Y = -i, Z = i * 2f,
                    RotX = 0f, RotY = 0f, RotZ = 0f, RotW = 1f,
                };
            }

            var fullVehicleBytes = MessageCodec.Encode(fullVehicleSnapshot.Write);
            Check(
                fullVehicleBytes.Length <= Protocol.MaxUnreliablePayloadBytes,
                $"VehicleSnapshot plein tient dans un paquet non fiable " +
                $"({fullVehicleBytes.Length} <= {Protocol.MaxUnreliablePayloadBytes} octets)");

            Check(
                Protocol.VehicleStateEntryBytes == 8 + 7 * 4,
                "taille d'une entrée de véhicule : clé 64 bits + position + quaternion");

            // Un paquet tronqué doit lever, pas renvoyer des valeurs inventées.
            // Le plafond de créatures doit tenir dans un paquet non fiable : c'est cette contrainte
            // qui a fait jeter le premier envoi de créatures (TooBigPacketException côté LiteNetLib,
            // invisible dans nos journaux). Le test mesure la taille réelle plutôt que de refaire
            // l'arithmétique à la main.
            var fullSnapshot = new CreatureSnapshotMessage
            {
                ServerTick = 987654321L,
                Creatures = new CreatureStateEntry[Protocol.MaxCreatureStates],
            };
            for (var i = 0; i < fullSnapshot.Creatures.Length; i++)
            {
                fullSnapshot.Creatures[i] = new CreatureStateEntry
                {
                    Key = i + 1,
                    X = i, Y = -i, Z = i * 2f,
                    RotX = 0f, RotY = 0f, RotZ = 0f, RotW = 1f,
                };
            }

            var fullSnapshotBytes = MessageCodec.Encode(fullSnapshot.Write);
            Check(
                fullSnapshotBytes.Length <= Protocol.MaxUnreliablePayloadBytes,
                $"CreatureSnapshot plein tient dans un paquet non fiable " +
                $"({fullSnapshotBytes.Length} <= {Protocol.MaxUnreliablePayloadBytes} octets)");

            var truncated = new byte[] { (byte)MessageType.Ping, 0x01, 0x02 };
            var threw = false;
            try
            {
                var reader = new PacketReader(truncated);
                reader.ReadByte();
                PingMessage.Read(reader);
            }
            catch (ProtocolException)
            {
                threw = true;
            }

            Check(threw, "paquet tronqué -> ProtocolException");

            // L'empreinte de mods doit ignorer l'ordre de chargement mais réagir au contenu.
            var hashA = ModListHash.Compute(new[] { "mod.b", "mod.a" });
            var hashB = ModListHash.Compute(new[] { "mod.a", "mod.b" });
            var hashC = ModListHash.Compute(new[] { "mod.a", "mod.c" });
            Check(hashA == hashB, "ModListHash indépendant de l'ordre");
            Check(hashA != hashC, "ModListHash sensible au contenu");

            // Lecture du rôle depuis la ligne de commande.
            Check(LaunchModeParser.Parse(new[] { "Subnautica.exe", "-coopmode=server" }) == LaunchMode.Server, "parse -coopmode=server");
            Check(LaunchModeParser.Parse(new[] { "Subnautica.exe", "-coopmode=client" }) == LaunchMode.Client, "parse -coopmode=client");
            Check(LaunchModeParser.Parse(new[] { "Subnautica.exe" }) == LaunchMode.Off, "parse sans argument -> Off");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "SELFTEST: SUCCES" : $"SELFTEST: {failures} ECHEC(S)");
            return failures == 0 ? 0 : 1;
        }

        // ---- Sonde réseau ----------------------------------------------------------------

        private static int Probe(string host, int port, string username, int seconds, string gameVersion, string modHash)
        {
            var listener = new EventBasedNetListener();
            var net = new NetManager(listener, null) { AutoRecycle = true, UnconnectedMessagesEnabled = false };
            var writer = new PacketWriter();
            var clock = Stopwatch.StartNew();
            var sentAt = new Dictionary<long, long>();

            var accepted = false;
            var refused = false;
            string refusalReason = null;
            ushort assignedPlayerId = 0;
            var pongs = 0;
            var snapshots = 0;
            var joinedPlayers = new List<string>();
            var leftPlayers = new List<string>();
            var anchorInitialized = false;
            var anchorX = -112f;
            var anchorY = 2.0f;
            var anchorZ = -52f;
            uint posSequence = 0;
            long sequence = 0;
            long lastRtt = 0;
            long minRtt = long.MaxValue;
            long maxRtt = 0;
            long rttSum = 0;
            long lastServerTick = 0;
            var sentEquipment = false;
            var receivedEquipments = new List<string>();
            var sentHeldItem = false;
            var sentPdaPhase = false;
            var sentQuietPhase = false;
            var receivedHeldItems = new List<string>();
            var receivedColors = new List<string>();
            var creatureSnapshotsReceived = 0;
            var creatureStatesReceived = 0;
            long firstCreatureKey = 0;
            string firstCreaturePosition = null;
            var disconnects = new List<string>();

            listener.PeerConnectedEvent += peer =>
            {
                Console.WriteLine($"[probe] connecté à {peer} — envoi du handshake");
                writer.Reset();
                new HandshakeRequest
                {
                    ProtocolVersion = Protocol.Version,
                    GameVersion = gameVersion,
                    ModListHash = modHash,
                    Username = username,
                }.Write(writer);
                peer.Send(writer.BackingBuffer, 0, writer.Length, DeliveryMethod.ReliableOrdered);
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                disconnects.Add(info.Reason.ToString());
                Console.WriteLine($"[probe] déconnecté (raison={info.Reason})");
            };

            listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                try
                {
                    var decoded = new PacketReader(reader.GetRemainingBytes());
                    switch ((MessageType)decoded.ReadByte())
                    {
                        case MessageType.HandshakeResponse:
                            var response = HandshakeResponse.Read(decoded);
                            if (response.Accepted)
                            {
                                accepted = true;
                                assignedPlayerId = response.AssignedPlayerId;
                                Console.WriteLine(
                                    $"[probe] handshake ACCEPTÉ (id={response.AssignedPlayerId}, tick serveur={response.ServerTick})");
                            }
                            else
                            {
                                refused = true;
                                refusalReason = response.Reason;
                                Console.WriteLine($"[probe] handshake REFUSÉ : {response.Reason}");
                            }

                            break;

                        case MessageType.Pong:
                            var pong = PongMessage.Read(decoded);
                            if (!sentAt.TryGetValue(pong.Sequence, out var sent))
                            {
                                break;
                            }

                            sentAt.Remove(pong.Sequence);
                            var rtt = clock.ElapsedMilliseconds - sent;
                            pongs++;
                            lastRtt = rtt;
                            rttSum += rtt;
                            maxRtt = Math.Max(maxRtt, rtt);
                            minRtt = Math.Min(minRtt, rtt);
                            lastServerTick = pong.ServerTick;
                            break;

                        case MessageType.PlayerJoined:
                            var joinedMsg = PlayerJoinedMessage.Read(decoded);
                            joinedPlayers.Add($"{joinedMsg.Username}#{joinedMsg.PlayerId}@({joinedMsg.X:F1},{joinedMsg.Y:F1},{joinedMsg.Z:F1})");
                            if (!anchorInitialized && (Math.Abs(joinedMsg.X) > 0.1f || Math.Abs(joinedMsg.Z) > 0.1f))
                            {
                                anchorX = joinedMsg.X;
                                anchorY = joinedMsg.Y;
                                anchorZ = joinedMsg.Z;
                                anchorInitialized = true;
                            }

                            Console.WriteLine(
                                $"[probe] PlayerJoined : id={joinedMsg.PlayerId} '{joinedMsg.Username}' pos=({joinedMsg.X:F2}, {joinedMsg.Y:F2}, {joinedMsg.Z:F2})");
                            break;

                        case MessageType.PlayerLeft:
                            var leftMsg = PlayerLeftMessage.Read(decoded);
                            leftPlayers.Add($"{leftMsg.Username}#{leftMsg.PlayerId}({leftMsg.Reason})");
                            Console.WriteLine(
                                $"[probe] PlayerLeft : id={leftMsg.PlayerId} '{leftMsg.Username}' raison={leftMsg.Reason}");
                            break;

                        case MessageType.PlayersSnapshot:
                            var snapMsg = PlayersSnapshotMessage.Read(decoded);
                            snapshots++;
                            lastServerTick = snapMsg.ServerTick;
                            if (!anchorInitialized && snapMsg.Players.Length > 0)
                            {
                                var p0 = snapMsg.Players[0];
                                if (Math.Abs(p0.X) > 0.1f || Math.Abs(p0.Z) > 0.1f)
                                {
                                    anchorX = p0.X;
                                    anchorY = p0.Y;
                                    anchorZ = p0.Z;
                                    anchorInitialized = true;
                                }
                            }

                            if (snapshots == 1 || snapshots % 40 == 0)
                            {
                                Console.WriteLine(
                                    $"[probe] PlayersSnapshot #{snapshots} tick={snapMsg.ServerTick} joueurs={snapMsg.Players.Length}");
                            }

                            break;

                        case MessageType.PlayerEquipment:
                            var equipMsg = PlayerEquipmentMessage.Read(decoded);
                            var desc = equipMsg.TechTypeNames.Length == 0
                                ? "(sans équipement)"
                                : string.Join(", ", equipMsg.TechTypeNames);
                            receivedEquipments.Add($"id={equipMsg.PlayerId}[{desc}]");
                            Console.WriteLine(
                                $"[probe] PlayerEquipment reçu : id={equipMsg.PlayerId} ({equipMsg.TechTypeNames.Length} pièce(s)) -> {desc}");
                            break;

                        case MessageType.PlayerHeldItem:
                            var heldMsg = PlayerHeldItemMessage.Read(decoded);
                            var heldName = string.IsNullOrEmpty(heldMsg.TechTypeName)
                                ? "(rien en main)"
                                : heldMsg.TechTypeName;
                            var heldParams = heldMsg.HoldingParams.Length == 0
                                ? "(aucun)"
                                : string.Join(", ", heldMsg.HoldingParams);
                            receivedHeldItems.Add($"id={heldMsg.PlayerId} '{heldName}' holding=[{heldParams}]");
                            Console.WriteLine(
                                $"[probe] PlayerHeldItem reçu : id={heldMsg.PlayerId} '{heldName}' holding=[{heldParams}]");
                            break;

                        case MessageType.PlayerColor:
                            var colorMsg = PlayerColorMessage.Read(decoded);
                            Protocol.PlayerColorAt(colorMsg.ColorIndex, out var colorR, out var colorG, out var colorB);
                            receivedColors.Add($"id={colorMsg.PlayerId} couleur={colorMsg.ColorIndex} rgb({colorR},{colorG},{colorB})");
                            Console.WriteLine(
                                $"[probe] PlayerColor reçu : id={colorMsg.PlayerId} couleur={colorMsg.ColorIndex} rgb({colorR},{colorG},{colorB})");
                            break;

                        case MessageType.CreatureSnapshot:
                            var creatureMsg = CreatureSnapshotMessage.Read(decoded);
                            creatureSnapshotsReceived++;
                            creatureStatesReceived += creatureMsg.Creatures.Length;
                            if (creatureMsg.Creatures.Length > 0 && firstCreaturePosition == null)
                            {
                                var firstCreature = creatureMsg.Creatures[0];
                                firstCreatureKey = firstCreature.Key;
                                firstCreaturePosition = $"({firstCreature.X:F2}, {firstCreature.Y:F2}, {firstCreature.Z:F2})";
                                Console.WriteLine(
                                    $"[probe] CreatureSnapshot #{creatureSnapshotsReceived} tick={creatureMsg.ServerTick} " +
                                    $"créatures={creatureMsg.Creatures.Length} première clé=0x{firstCreature.Key:x16} pos={firstCreaturePosition}");
                            }
                            else if (creatureSnapshotsReceived % 40 == 0)
                            {
                                Console.WriteLine(
                                    $"[probe] CreatureSnapshot #{creatureSnapshotsReceived} tick={creatureMsg.ServerTick} " +
                                    $"créatures={creatureMsg.Creatures.Length}");
                            }

                            break;
                    }
                }
                finally
                {
                    reader.Recycle();
                }
            };

            if (!net.Start())
            {
                Console.Error.WriteLine("[probe] impossible de démarrer LiteNetLib");
                return 3;
            }

            net.Connect(host, port, Protocol.ConnectionKey);

            var nextPing = 0.0;
            var nextPos = 0.0;
            var nextPdaPhase = clock.Elapsed.TotalSeconds + seconds / 3.0;
            var nextQuietPhase = clock.Elapsed.TotalSeconds + 2 * seconds / 3.0;
            var deadline = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < deadline)
            {
                net.PollEvents();

                var now = clock.Elapsed.TotalSeconds;
                if (accepted && !sentEquipment)
                {
                    sentEquipment = true;
                    var probeItems = new[] { "RadiationSuit", "RadiationHelmet", "RadiationGloves", "HighCapacityTank", "Fins" };
                    writer.Reset();
                    new PlayerEquipmentMessage
                    {
                        PlayerId = assignedPlayerId,
                        TechTypeNames = probeItems,
                    }.Write(writer);
                    net.FirstPeer.Send(writer.BackingBuffer, 0, writer.Length, DeliveryMethod.ReliableOrdered);
                    Console.WriteLine($"[probe] PlayerEquipment envoyé : {string.Join(", ", probeItems)}");
                }

                if (accepted && !sentHeldItem)
                {
                    sentHeldItem = true;
                    writer.Reset();
                    new PlayerHeldItemMessage
                    {
                        PlayerId = assignedPlayerId,
                        TechTypeName = "Welder",
                        HoldingParams = new[] { "holding_welder" },
                    }.Write(writer);
                    net.FirstPeer.Send(writer.BackingBuffer, 0, writer.Length, DeliveryMethod.ReliableOrdered);
                    Console.WriteLine("[probe] PlayerHeldItem envoyé : Welder [holding_welder]");
                }

                // Seconde moitié de la sonde : rien en main + `using_pda` actif. C'est le geste du
                // joueur qui range son outil et sort son PDA — la moitié « PDA » de la synchronisation.
                if (accepted && !sentPdaPhase && now >= nextPdaPhase)
                {
                    sentPdaPhase = true;
                    writer.Reset();
                    new PlayerHeldItemMessage
                    {
                        PlayerId = assignedPlayerId,
                        TechTypeName = string.Empty,
                        HoldingParams = Array.Empty<string>(),
                    }.Write(writer);
                    net.FirstPeer.Send(writer.BackingBuffer, 0, writer.Length, DeliveryMethod.ReliableOrdered);
                    Console.WriteLine("[probe] PlayerHeldItem envoyé : (rien en main) + using_pda (phase PDA)");
                }

                // Dernier tiers : plus aucun drapeau d'action. Le PDA doit être rangé, et `PDA.Close` ne
                // l'éteint qu'à la fin de son geste de 0,3 s — c'est le délai qu'on vient mesurer.
                if (accepted && !sentQuietPhase && now >= nextQuietPhase)
                {
                    sentQuietPhase = true;
                    Console.WriteLine("[probe] phase rangement : plus aucun drapeau d'action (PDA rangé côté client)");
                }

                if (accepted && now >= nextPing)
                {
                    nextPing = now + Protocol.PingIntervalMs / 1000.0;
                    sequence++;
                    sentAt[sequence] = clock.ElapsedMilliseconds;
                    writer.Reset();
                    new PingMessage { Sequence = sequence, ClientTimeMs = clock.ElapsedMilliseconds }.Write(writer);
                    net.FirstPeer.Send(writer.BackingBuffer, 0, writer.Length, DeliveryMethod.Unreliable);
                }

                if (accepted && now >= nextPos)
                {
                    nextPos = now + Protocol.PositionSendIntervalMs / 1000.0;
                    posSequence++;
                    var angle = (float)now;
                    writer.Reset();
                    new PlayerPositionMessage
                    {
                        Sequence = posSequence,
                        X = anchorX + (float)Math.Cos(angle) * 2.5f,
                        Y = anchorY,
                        Z = anchorZ + (float)Math.Sin(angle) * 2.5f,
                        RotX = 0f,
                        RotY = (float)Math.Sin(angle * 0.5f),
                        RotZ = 0f,
                        RotW = (float)Math.Cos(angle * 0.5f),
                        MoveSpeedX = (float)Math.Sin(angle) * 1.5f,
                        MoveSpeedY = 0f,
                        MoveSpeedZ = 4.2f,
                        ViewPitch = (float)Math.Sin(angle) * 25f,
                        IsUnderwater = true,
                        OnSurface = false,
                        ActionFlags = sentQuietPhase
                            ? PlayerActionFlags.None
                            : sentPdaPhase
                                ? PlayerActionFlags.UsingPda
                                : PlayerActionFlags.UsingTool | PlayerActionFlags.UsingToolAlt,
                    }.Write(writer);
                    net.FirstPeer.Send(writer.BackingBuffer, 0, writer.Length, DeliveryMethod.Unreliable);
                }

                Thread.Sleep(5);
            }

            net.Stop();

            var average = pongs > 0 ? rttSum / pongs : 0;
            var minimum = minRtt == long.MaxValue ? 0 : minRtt;

            Console.WriteLine();
            Console.WriteLine($"[probe] id attribué={assignedPlayerId} pings envoyés={sequence} pongs reçus={pongs}");
            Console.WriteLine($"[probe] positions envoyées={posSequence} snapshots reçus={snapshots} joueurs vus={joinedPlayers.Count} équipements reçus={receivedEquipments.Count} objet tenu reçu(s)={receivedHeldItems.Count} phase PDA={sentPdaPhase} phase rangement={sentQuietPhase}");
            Console.WriteLine(
                $"[probe] couleurs reçues={receivedColors.Count} snapshots de créatures reçus={creatureSnapshotsReceived} " +
                $"états de créature reçus={creatureStatesReceived} " +
                (firstCreaturePosition == null
                    ? "première créature=(aucune)"
                    : $"première créature clé=0x{firstCreatureKey:x16} pos={firstCreaturePosition}"));
            Console.WriteLine($"[probe] rtt dernier={lastRtt}ms min={minimum}ms moy={average}ms max={maxRtt}ms");
            Console.WriteLine($"[probe] dernier tick serveur={lastServerTick}");
            if (disconnects.Count > 0)
            {
                Console.WriteLine($"[probe] déconnexions : {string.Join(", ", disconnects)}");
            }

            if (refused)
            {
                Console.Error.WriteLine($"ECHEC: handshake refusé ({refusalReason})");
                return 1;
            }

            if (!accepted)
            {
                Console.Error.WriteLine("ECHEC: aucun handshake accepté (serveur absent ou injoignable ?)");
                return 1;
            }

            if (pongs == 0)
            {
                Console.Error.WriteLine("ECHEC: handshake accepté mais aucun pong reçu");
                return 1;
            }

            Console.WriteLine("PROBE: SUCCES");
            return 0;
        }
    }
}
