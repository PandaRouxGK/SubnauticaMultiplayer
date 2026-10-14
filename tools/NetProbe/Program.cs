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

            var response = new HandshakeResponse { Accepted = true, Reason = string.Empty, ServerTick = 1234567890123L };
            var responseBytes = MessageCodec.Encode(response.Write);
            Check(responseBytes[0] == (byte)MessageType.HandshakeResponse, "opcode HandshakeResponse");
            var responseReader = new PacketReader(responseBytes);
            responseReader.ReadByte();
            var responseRoundTrip = HandshakeResponse.Read(responseReader);
            Check(
                responseRoundTrip.Accepted == response.Accepted
                && responseRoundTrip.Reason == response.Reason
                && responseRoundTrip.ServerTick == response.ServerTick,
                "round-trip HandshakeResponse (long sur 64 bits)");

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

            // Un paquet tronqué doit lever, pas renvoyer des valeurs inventées.
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
            var pongs = 0;
            long sequence = 0;
            long lastRtt = 0;
            long minRtt = long.MaxValue;
            long maxRtt = 0;
            long rttSum = 0;
            long lastServerTick = 0;
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
                                Console.WriteLine($"[probe] handshake ACCEPTÉ (tick serveur={response.ServerTick})");
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
            var deadline = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < deadline)
            {
                net.PollEvents();

                var now = clock.Elapsed.TotalSeconds;
                if (accepted && now >= nextPing)
                {
                    nextPing = now + Protocol.PingIntervalMs / 1000.0;
                    sequence++;
                    sentAt[sequence] = clock.ElapsedMilliseconds;
                    writer.Reset();
                    new PingMessage { Sequence = sequence, ClientTimeMs = clock.ElapsedMilliseconds }.Write(writer);
                    net.FirstPeer.Send(writer.BackingBuffer, 0, writer.Length, DeliveryMethod.Unreliable);
                }

                Thread.Sleep(5);
            }

            net.Stop();

            var average = pongs > 0 ? rttSum / pongs : 0;
            var minimum = minRtt == long.MaxValue ? 0 : minRtt;

            Console.WriteLine();
            Console.WriteLine($"[probe] pings envoyés={sequence} pongs reçus={pongs}");
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
