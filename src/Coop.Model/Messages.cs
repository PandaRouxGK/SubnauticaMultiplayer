using System;

namespace Coop.Model
{
    /// <summary>
    /// Messages du protocole M1. Chaque message s'écrit avec son propre opcode en tête,
    /// ce qui rend le format auto-descriptif côté réception.
    /// </summary>
    public sealed class HandshakeRequest
    {
        public int ProtocolVersion;
        public string GameVersion;
        public string ModListHash;
        public string Username;

        /// <summary>
        /// Empreinte de la partie chargée (v13, P0). Jamais <c>null</c> à l'écriture : un client qui
        /// n'a pas encore résolu sa sauvegarde envoie une empreinte vide, et le serveur la refuse
        /// nommément plutôt que de laisser passer une divergence silencieuse.
        /// </summary>
        public SaveIdentity Save;

        public void Write(PacketWriter writer)
        {
            // Bornes du handshake : ce sont les seuls champs qu'un pair inconnu choisit librement.
            // `Username` garde sa règle de refus nommée côté serveur (`Validate`) ; les deux autres
            // sont produits par le plugin, donc une valeur hors borne est un défaut local, pas une
            // faute de l'utilisateur.
            var gameVersion = GameVersion ?? string.Empty;
            if (gameVersion.Length > Protocol.MaxGameVersionLength)
            {
                throw new ProtocolException($"version du jeu trop longue ({gameVersion.Length} > {Protocol.MaxGameVersionLength})");
            }

            var modListHash = ModListHash ?? string.Empty;
            if (modListHash.Length > Protocol.MaxModListHashLength)
            {
                throw new ProtocolException($"empreinte de mods trop longue ({modListHash.Length} > {Protocol.MaxModListHashLength})");
            }

            writer.WriteByte((byte)MessageType.HandshakeRequest);
            writer.WriteInt(ProtocolVersion);
            writer.WriteString(gameVersion);
            writer.WriteString(modListHash);
            writer.WriteString(Username ?? string.Empty);
            (Save ?? SaveIdentity.Create(string.Empty, string.Empty, -1, string.Empty)).WriteTo(writer);
        }

        public static HandshakeRequest Read(PacketReader reader)
        {
            var request = new HandshakeRequest
            {
                ProtocolVersion = reader.ReadInt(),
                GameVersion = reader.ReadString(),
                ModListHash = reader.ReadString(),
                Username = reader.ReadString(),
                Save = SaveIdentity.ReadFrom(reader),
            };

            if (request.GameVersion.Length > Protocol.MaxGameVersionLength)
            {
                throw new ProtocolException($"version du jeu reçue trop longue ({request.GameVersion.Length})");
            }

            if (request.ModListHash.Length > Protocol.MaxModListHashLength)
            {
                throw new ProtocolException($"empreinte de mods reçue trop longue ({request.ModListHash.Length})");
            }

            return request;
        }
    }

    /// <summary>Réponse du serveur : accepté ou refusé, avec la raison en clair pour le log client.</summary>
    public sealed class HandshakeResponse
    {
        public bool Accepted;
        public string Reason;
        public long ServerTick;

        /// <summary>Identifiant attribué à ce client par le serveur (valide si <see cref="Accepted"/> est vrai).</summary>
        public ushort AssignedPlayerId;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.HandshakeResponse);
            writer.WriteBool(Accepted);
            writer.WriteString(MessageText.Reason(Reason, "motif de refus de handshake"));
            writer.WriteLong(ServerTick);
            writer.WriteUInt16(AssignedPlayerId);
        }

        public static HandshakeResponse Read(PacketReader reader) => new HandshakeResponse
        {
            Accepted = reader.ReadBool(),
            Reason = MessageText.Reason(reader.ReadString(), "motif de refus de handshake reçu"),
            ServerTick = reader.ReadLong(),
            AssignedPlayerId = reader.ReadUInt16(),
        };
    }

    /// <summary>Ping émis par le client. <see cref="ClientTimeMs"/> sert à calculer l'aller-retour.</summary>
    public sealed class PingMessage
    {
        public long Sequence;
        public long ClientTimeMs;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.Ping);
            writer.WriteLong(Sequence);
            writer.WriteLong(ClientTimeMs);
        }

        public static PingMessage Read(PacketReader reader) => new PingMessage
        {
            Sequence = reader.ReadLong(),
            ClientTimeMs = reader.ReadLong(),
        };
    }

    /// <summary>
    /// Réponse du serveur : elle renvoie le ping d'origine tel quel (le client mesure l'aller-retour
    /// avec sa propre horloge, sans jamais faire confiance à l'heure du serveur) et y ajoute son
    /// numéro de tick pour corréler avec la simulation.
    /// </summary>
    public sealed class PongMessage
    {
        public long Sequence;
        public long ClientTimeMs;
        public long ServerTick;
        public long ServerTimeMs;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.Pong);
            writer.WriteLong(Sequence);
            writer.WriteLong(ClientTimeMs);
            writer.WriteLong(ServerTick);
            writer.WriteLong(ServerTimeMs);
        }

        public static PongMessage Read(PacketReader reader) => new PongMessage
        {
            Sequence = reader.ReadLong(),
            ClientTimeMs = reader.ReadLong(),
            ServerTick = reader.ReadLong(),
            ServerTimeMs = reader.ReadLong(),
        };
    }

    /// <summary>Fermeture explicite, avec une raison lisible plutôt qu'une coupure muette.</summary>
    public sealed class DisconnectMessage
    {
        public string Reason;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.Disconnect);
            writer.WriteString(MessageText.Reason(Reason, "motif de déconnexion"));
        }

        public static DisconnectMessage Read(PacketReader reader) => new DisconnectMessage
        {
            Reason = MessageText.Reason(reader.ReadString(), "motif de déconnexion reçu"),
        };
    }

    /// <summary>
    /// Présence d'un joueur (ReliableOrdered). Envoyé à tous quand un joueur rejoint, et envoyé au
    /// nouvel arrivant pour chaque joueur déjà présent (y compris le personnage de l'instance serveur).
    /// </summary>
    public sealed class PlayerJoinedMessage
    {
        public ushort PlayerId;
        public string Username;
        public float X;
        public float Y;
        public float Z;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.PlayerJoined);
            writer.WriteUInt16(PlayerId);
            writer.WriteString(Username);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
        }

        public static PlayerJoinedMessage Read(PacketReader reader) => new PlayerJoinedMessage
        {
            PlayerId = reader.ReadUInt16(),
            Username = reader.ReadString(),
            X = reader.ReadFloat(),
            Y = reader.ReadFloat(),
            Z = reader.ReadFloat(),
            RotX = reader.ReadFloat(),
            RotY = reader.ReadFloat(),
            RotZ = reader.ReadFloat(),
            RotW = reader.ReadFloat(),
        };
    }

    /// <summary>Départ d'un joueur (ReliableOrdered) : le client détruit l'avatar correspondant.</summary>
    public sealed class PlayerLeftMessage
    {
        public ushort PlayerId;
        public string Username;
        public string Reason;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.PlayerLeft);
            writer.WriteUInt16(PlayerId);
            writer.WriteString(Username);
            writer.WriteString(MessageText.Reason(Reason, "motif de départ"));
        }

        public static PlayerLeftMessage Read(PacketReader reader) => new PlayerLeftMessage
        {
            PlayerId = reader.ReadUInt16(),
            Username = reader.ReadString(),
            Reason = MessageText.Reason(reader.ReadString(), "motif de départ reçu"),
        };
    }

    /// <summary>
    /// État de position/orientation et paramètres d'animation envoyé par le client au serveur (Unreliable, 20 Hz).
    /// <see cref="Sequence"/> permet au serveur d'ignorer les paquets arrivés dans le désordre.
    /// </summary>
    public sealed class PlayerPositionMessage
    {
        public uint Sequence;
        public float X;
        public float Y;
        public float Z;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public float MoveSpeedX;
        public float MoveSpeedY;
        public float MoveSpeedZ;
        public float ViewPitch;
        public bool IsUnderwater;
        public bool OnSurface;
        public PlayerActionFlags ActionFlags;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.PlayerPosition);
            writer.WriteInt(unchecked((int)Sequence));
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
            writer.WriteFloat(MoveSpeedX);
            writer.WriteFloat(MoveSpeedY);
            writer.WriteFloat(MoveSpeedZ);
            writer.WriteFloat(ViewPitch);
            writer.WriteBool(IsUnderwater);
            writer.WriteBool(OnSurface);
            writer.WriteByte((byte)ActionFlags);
        }

        public static PlayerPositionMessage Read(PacketReader reader)
        {
            var msg = new PlayerPositionMessage
            {
                Sequence = unchecked((uint)reader.ReadInt()),
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat(),
                RotX = reader.ReadFloat(),
                RotY = reader.ReadFloat(),
                RotZ = reader.ReadFloat(),
                RotW = reader.ReadFloat(),
                MoveSpeedX = reader.ReadFloat(),
                MoveSpeedY = reader.ReadFloat(),
                MoveSpeedZ = reader.ReadFloat(),
                ViewPitch = reader.ReadFloat(),
                IsUnderwater = reader.ReadBool(),
                OnSurface = reader.ReadBool(),
                ActionFlags = (PlayerActionFlags)reader.ReadByte(),
            };
            EnsureFinite(
                msg.X, msg.Y, msg.Z,
                msg.RotX, msg.RotY, msg.RotZ, msg.RotW,
                msg.MoveSpeedX, msg.MoveSpeedY, msg.MoveSpeedZ, msg.ViewPitch);
            return msg;
        }

        internal static void EnsureFinite(
            float x, float y, float z,
            float rx, float ry, float rz, float rw,
            float vx = 0f, float vy = 0f, float vz = 0f, float pitch = 0f)
        {
            if (IsInvalid(x) || IsInvalid(y) || IsInvalid(z) ||
                IsInvalid(rx) || IsInvalid(ry) || IsInvalid(rz) || IsInvalid(rw) ||
                IsInvalid(vx) || IsInvalid(vy) || IsInvalid(vz) || IsInvalid(pitch))
            {
                throw new ProtocolException("coordonnées, quaternion ou paramètres d'animation non finis (NaN/Infinity)");
            }
        }

        private static bool IsInvalid(float v) => float.IsNaN(v) || float.IsInfinity(v);
    }

    /// <summary>État d'un joueur dans un <see cref="PlayersSnapshotMessage"/>.</summary>
    public struct PlayerStateEntry
    {
        public ushort PlayerId;
        public float X;
        public float Y;
        public float Z;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public float MoveSpeedX;
        public float MoveSpeedY;
        public float MoveSpeedZ;
        public float ViewPitch;
        public bool IsUnderwater;
        public bool OnSurface;
        public PlayerActionFlags ActionFlags;

        public void Write(PacketWriter writer)
        {
            writer.WriteUInt16(PlayerId);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
            writer.WriteFloat(MoveSpeedX);
            writer.WriteFloat(MoveSpeedY);
            writer.WriteFloat(MoveSpeedZ);
            writer.WriteFloat(ViewPitch);
            writer.WriteBool(IsUnderwater);
            writer.WriteBool(OnSurface);
            writer.WriteByte((byte)ActionFlags);
        }

        public static PlayerStateEntry Read(PacketReader reader)
        {
            var entry = new PlayerStateEntry
            {
                PlayerId = reader.ReadUInt16(),
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat(),
                RotX = reader.ReadFloat(),
                RotY = reader.ReadFloat(),
                RotZ = reader.ReadFloat(),
                RotW = reader.ReadFloat(),
                MoveSpeedX = reader.ReadFloat(),
                MoveSpeedY = reader.ReadFloat(),
                MoveSpeedZ = reader.ReadFloat(),
                ViewPitch = reader.ReadFloat(),
                IsUnderwater = reader.ReadBool(),
                OnSurface = reader.ReadBool(),
                ActionFlags = (PlayerActionFlags)reader.ReadByte(),
            };
            PlayerPositionMessage.EnsureFinite(
                entry.X, entry.Y, entry.Z,
                entry.RotX, entry.RotY, entry.RotZ, entry.RotW,
                entry.MoveSpeedX, entry.MoveSpeedY, entry.MoveSpeedZ, entry.ViewPitch);
            return entry;
        }
    }

    /// <summary>
    /// Photographie diffusée par le serveur à chaque tick (20 Hz, Unreliable) contenant les positions
    /// connues de tous les joueurs présents dans le monde.
    /// </summary>
    public sealed class PlayersSnapshotMessage
    {
        public long ServerTick;
        public PlayerStateEntry[] Players;

        public void Write(PacketWriter writer)
        {
            var list = Players ?? Array.Empty<PlayerStateEntry>();
            // Le serveur n'admet que `MaxClients` sessions et le snapshot exclut le destinataire :
            // `MaxClients` est donc le vrai plafond. Le laisser à 255 laissait lire un paquet forgé
            // de 255 joueurs, que le protocole n'autorise nulle part.
            if (list.Length > Protocol.MaxClients)
            {
                throw new ProtocolException($"trop de joueurs dans le snapshot ({list.Length} > {Protocol.MaxClients})");
            }

            writer.WriteByte((byte)MessageType.PlayersSnapshot);
            writer.WriteLong(ServerTick);
            writer.WriteByte((byte)list.Length);
            for (var i = 0; i < list.Length; i++)
            {
                list[i].Write(writer);
            }
        }

        public static PlayersSnapshotMessage Read(PacketReader reader)
        {
            var tick = reader.ReadLong();
            var count = reader.ReadByte();
            if (count > Protocol.MaxClients)
            {
                throw new ProtocolException($"trop de joueurs reçus dans le snapshot ({count} > {Protocol.MaxClients})");
            }

            var players = count == 0 ? Array.Empty<PlayerStateEntry>() : new PlayerStateEntry[count];
            for (var i = 0; i < count; i++)
            {
                players[i] = PlayerStateEntry.Read(reader);
            }

            return new PlayersSnapshotMessage
            {
                ServerTick = tick,
                Players = players,
            };
        }
    }

    /// <summary>
    /// Équipement complet porté par un joueur (ReliableOrdered).
    ///
    /// Émis par un client vers le serveur dès que son équipement change (et une première fois à
    /// l'entrée dans le monde), puis rediffusé par le serveur à tous les autres clients (avec
    /// <see cref="PlayerId"/> forcé à l'identifiant du joueur source). Également envoyé au nouvel
    /// arrivant au handshake pour chaque joueur déjà présent.
    /// </summary>
    public sealed class PlayerEquipmentMessage
    {
        public ushort PlayerId;
        public string[] TechTypeNames;

        public void Write(PacketWriter writer)
        {
            var list = TechTypeNames ?? Array.Empty<string>();
            if (list.Length > Protocol.MaxEquipmentSlots)
            {
                throw new ProtocolException($"trop de pièces d'équipement ({list.Length} > {Protocol.MaxEquipmentSlots})");
            }

            writer.WriteByte((byte)MessageType.PlayerEquipment);
            writer.WriteUInt16(PlayerId);
            writer.WriteByte((byte)list.Length);
            for (var i = 0; i < list.Length; i++)
            {
                var name = list[i] ?? string.Empty;
                if (name.Length > Protocol.MaxTechTypeNameLength)
                {
                    throw new ProtocolException($"nom de TechType trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
                }

                writer.WriteString(name);
            }
        }

        public static PlayerEquipmentMessage Read(PacketReader reader)
        {
            var playerId = reader.ReadUInt16();
            var count = reader.ReadByte();
            if (count > Protocol.MaxEquipmentSlots)
            {
                throw new ProtocolException($"trop de pièces d'équipement reçues ({count} > {Protocol.MaxEquipmentSlots})");
            }

            var names = count == 0 ? Array.Empty<string>() : new string[count];
            for (var i = 0; i < count; i++)
            {
                var name = reader.ReadString();
                if (name.Length > Protocol.MaxTechTypeNameLength)
                {
                    throw new ProtocolException($"nom de TechType reçu trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
                }

                names[i] = name;
            }

            return new PlayerEquipmentMessage
            {
                PlayerId = playerId,
                TechTypeNames = names,
            };
        }
    }

    /// <summary>
    /// Objet tenu par un joueur (ReliableOrdered) : l'outil en main, et les paramètres d'animation que
    /// le jeu a lui-même posés pour cet outil.
    ///
    /// Émis par un client quand son objet tenu change (et une première fois à l'entrée dans le monde),
    /// puis rediffusé par le serveur aux autres clients avec <see cref="PlayerId"/> forcé à l'émetteur.
    /// Envoyé aussi au nouvel arrivant au handshake pour chaque joueur déjà présent.
    /// </summary>
    public sealed class PlayerHeldItemMessage
    {
        public ushort PlayerId;

        /// <summary>Nom technique du <c>TechType</c> tenu ; chaîne vide si le joueur n'a rien en main.</summary>
        public string TechTypeName;

        /// <summary>
        /// Paramètres d'animation `holding_*` actifs chez l'émetteur, lus **sur son propre `Animator`**
        /// plutôt que re-dérivés : c'est le jeu qui les a calculés, et il n'y a donc rien à deviner.
        /// </summary>
        public string[] HoldingParams;

        public void Write(PacketWriter writer)
        {
            var paramsList = HoldingParams ?? Array.Empty<string>();
            if (paramsList.Length > Protocol.MaxActionParams)
            {
                throw new ProtocolException($"trop de paramètres d'action ({paramsList.Length} > {Protocol.MaxActionParams})");
            }

            var name = TechTypeName ?? string.Empty;
            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType tenu trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            writer.WriteByte((byte)MessageType.PlayerHeldItem);
            writer.WriteUInt16(PlayerId);
            writer.WriteString(name);
            writer.WriteByte((byte)paramsList.Length);
            for (var i = 0; i < paramsList.Length; i++)
            {
                var param = paramsList[i] ?? string.Empty;
                if (param.Length > Protocol.MaxTechTypeNameLength)
                {
                    throw new ProtocolException($"nom de paramètre d'animation trop long ({param.Length})");
                }

                writer.WriteString(param);
            }
        }

        public static PlayerHeldItemMessage Read(PacketReader reader)
        {
            var playerId = reader.ReadUInt16();
            var name = reader.ReadString();
            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType tenu reçu trop long ({name.Length})");
            }

            var count = reader.ReadByte();
            if (count > Protocol.MaxActionParams)
            {
                throw new ProtocolException($"trop de paramètres d'action reçus ({count} > {Protocol.MaxActionParams})");
            }

            var holding = count == 0 ? Array.Empty<string>() : new string[count];
            for (var i = 0; i < count; i++)
            {
                var param = reader.ReadString();
                if (param.Length > Protocol.MaxTechTypeNameLength)
                {
                    throw new ProtocolException($"nom de paramètre d'action reçu trop long ({param.Length})");
                }

                holding[i] = param;
            }

            return new PlayerHeldItemMessage
            {
                PlayerId = playerId,
                TechTypeName = name,
                HoldingParams = holding,
            };
        }
    }

    /// <summary>
    /// Couleur assignée à un joueur (ReliableOrdered), décidée **par le serveur** et par personne
    /// d'autre.
    ///
    /// Pourquoi le serveur décide : c'est la seule façon de garantir que deux joueurs ne portent
    /// jamais la même couleur. Un client qui choisirait la sienne pourrait tomber sur celle d'un
    /// autre, et il faudrait alors arbitrer — c'est-à-dire refaire ce que fait déjà le serveur.
    /// Même forme que l'équipement : le serveur force <see cref="PlayerId"/> et rediffuse.
    /// </summary>
    public sealed class PlayerColorMessage
    {
        public ushort PlayerId;

        /// <summary>Indice dans la palette de <see cref="Protocol.PlayerColorAt"/>.</summary>
        public byte ColorIndex;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.PlayerColor);
            writer.WriteUInt16(PlayerId);
            writer.WriteByte(ColorIndex);
        }

        public static PlayerColorMessage Read(PacketReader reader) => new PlayerColorMessage
        {
            PlayerId = reader.ReadUInt16(),
            ColorIndex = reader.ReadByte(),
        };
    }

    /// <summary>État d'une créature dans un <see cref="CreatureSnapshotMessage"/>.</summary>
    public struct CreatureStateEntry
    {
        /// <summary>
        /// Clé de la créature, calculée de la même façon des deux côtés (voir
        /// <c>Coop.Shared.CreatureIdentity</c>) : c'est elle qui permet au client de retrouver, dans
        /// son propre monde, la créature que le serveur simule.
        /// </summary>
        public long Key;

        public float X;
        public float Y;
        public float Z;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;

        /// <summary>
        /// Points de vie, normalisés `0..1` (`LiveMixin.health / maxHealth`), ou **-1 = inconnue**
        /// (créature sans `LiveMixin`), v17. Même sentinelle que celle des véhicules, et pour la même
        /// raison : `0` est une créature morte, pas une absence de mesure. Un écrivain **doit** la
        /// renseigner — la valeur par défaut d'une structure est `0`, donc « morte ».
        ///
        /// Le client l'applique **sans tuer** : la mort est une décision du serveur, annoncée par
        /// `EntityDespawn` (`EntityKind.Creature`).
        /// </summary>
        public float Health;

        public void Write(PacketWriter writer)
        {
            if (float.IsNaN(Health) || float.IsInfinity(Health))
            {
                throw new ProtocolException($"santé de créature non finie ({Health})");
            }

            writer.WriteLong(Key);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
            writer.WriteFloat(Health < 0f ? -1f : (Health > 1f ? 1f : Health));
        }

        public static CreatureStateEntry Read(PacketReader reader)
        {
            var entry = new CreatureStateEntry
            {
                Key = reader.ReadLong(),
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat(),
                RotX = reader.ReadFloat(),
                RotY = reader.ReadFloat(),
                RotZ = reader.ReadFloat(),
                RotW = reader.ReadFloat(),
                Health = reader.ReadFloat(),
            };
            PlayerPositionMessage.EnsureFinite(
                entry.X, entry.Y, entry.Z,
                entry.RotX, entry.RotY, entry.RotZ, entry.RotW);

            if (float.IsNaN(entry.Health) || float.IsInfinity(entry.Health))
            {
                throw new ProtocolException($"santé de créature reçue non finie ({entry.Health})");
            }

            entry.Health = entry.Health < 0f ? -1f : (entry.Health > 1f ? 1f : entry.Health);
            return entry;
        }
    }

    /// <summary>
    /// Photographie des créatures que le serveur simule (Unreliable, tous les
    /// <see cref="Protocol.CreatureStreamIntervalTicks"/> ticks).
    ///
    /// Le serveur ne dit pas « voici toutes les créatures du monde » mais « voici celles qui
    /// comptent pour toi » : les entrées sont déjà filtrées par <see cref="Protocol.CreatureRelevancyRadiusMeters"/>.
    /// </summary>
    public sealed class CreatureSnapshotMessage
    {
        public long ServerTick;
        public CreatureStateEntry[] Creatures;

        public void Write(PacketWriter writer)
        {
            var list = Creatures ?? Array.Empty<CreatureStateEntry>();
            if (list.Length > Protocol.MaxCreatureStates)
            {
                throw new ProtocolException($"trop de créatures dans le snapshot ({list.Length} > {Protocol.MaxCreatureStates})");
            }

            writer.WriteByte((byte)MessageType.CreatureSnapshot);
            writer.WriteLong(ServerTick);
            writer.WriteUInt16((ushort)list.Length);
            for (var i = 0; i < list.Length; i++)
            {
                list[i].Write(writer);
            }
        }

        public static CreatureSnapshotMessage Read(PacketReader reader)
        {
            var tick = reader.ReadLong();
            var count = reader.ReadUInt16();
            if (count > Protocol.MaxCreatureStates)
            {
                throw new ProtocolException($"trop de créatures reçues ({count} > {Protocol.MaxCreatureStates})");
            }

            var creatures = count == 0 ? Array.Empty<CreatureStateEntry>() : new CreatureStateEntry[count];
            for (var i = 0; i < count; i++)
            {
                creatures[i] = CreatureStateEntry.Read(reader);
            }

            return new CreatureSnapshotMessage
            {
                ServerTick = tick,
                Creatures = creatures,
            };
        }
    }

    /// <summary>
    /// État d'un véhicule dans un <see cref="VehicleSnapshotMessage"/>.
    ///
    /// La clé est calculée des deux côtés par <c>Coop.Shared.VehicleIdentity</c> : sans elle, le client
    /// ne pourrait pas dire « ce Seamoth-là, c'est celui que le serveur simule » — un véhicule n'a pas
    /// d'identifiant réseau, et deux Seamoth de la même sauvegarde sont indiscernables par leur nom.
    /// </summary>
    public struct VehicleStateEntry
    {
        /// <summary>Clé stable du véhicule, calculée de la même façon des deux côtés.</summary>
        public long Key;

        public float X;
        public float Y;
        public float Z;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;

        /// <summary>
        /// Charge de la batterie, normalisée `0..1` (`EnergyMixin.charge`), ou **-1 = inconnue**
        /// (véhicule sans batterie, composant absent). Le sentinelle est explicite : `0` est une
        /// batterie vide, pas une absence d'information — les confondre ferait croire à un client
        /// qu'un Seamoth neuf est à plat.
        /// </summary>
        public float Energy;

        /// <summary>
        /// Points de vie, normalisés `0..1` (`LiveMixin.health / LiveMixin.maxHealth`), ou **-1 = inconnue**
        /// (véhicule sans `LiveMixin`, ou composant absent). Même sentinelle que l'énergie, et pour la même
        /// raison : `0` est un véhicule détruit, pas une absence d'information.
        ///
        /// Ce que le client en fait : il applique la fraction sur sa copie **sans la tuer** — la mort est
        /// une décision du serveur, qui annonce le retrait. Tuer localement sur une santé à zéro ferait
        /// disparaître un véhicule que le serveur pourrait encore réparer.
        /// </summary>
        public float Health;

        public void Write(PacketWriter writer)
        {
            if (float.IsNaN(Energy) || float.IsInfinity(Energy))
            {
                throw new ProtocolException($"énergie de véhicule non finie ({Energy})");
            }

            if (float.IsNaN(Health) || float.IsInfinity(Health))
            {
                throw new ProtocolException($"santé de véhicule non finie ({Health})");
            }

            writer.WriteLong(Key);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
            writer.WriteFloat(Energy < 0f ? -1f : (Energy > 1f ? 1f : Energy));
            writer.WriteFloat(Health < 0f ? -1f : (Health > 1f ? 1f : Health));
        }

        public static VehicleStateEntry Read(PacketReader reader)
        {
            var entry = new VehicleStateEntry
            {
                Key = reader.ReadLong(),
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat(),
                RotX = reader.ReadFloat(),
                RotY = reader.ReadFloat(),
                RotZ = reader.ReadFloat(),
                RotW = reader.ReadFloat(),
                Energy = reader.ReadFloat(),
                Health = reader.ReadFloat(),
            };
            PlayerPositionMessage.EnsureFinite(
                entry.X, entry.Y, entry.Z,
                entry.RotX, entry.RotY, entry.RotZ, entry.RotW);

            if (float.IsNaN(entry.Energy) || float.IsInfinity(entry.Energy))
            {
                throw new ProtocolException($"énergie de véhicule reçue non finie ({entry.Energy})");
            }

            entry.Energy = entry.Energy < 0f ? -1f : (entry.Energy > 1f ? 1f : entry.Energy);

            if (float.IsNaN(entry.Health) || float.IsInfinity(entry.Health))
            {
                throw new ProtocolException($"santé de véhicule reçue non finie ({entry.Health})");
            }

            entry.Health = entry.Health < 0f ? -1f : (entry.Health > 1f ? 1f : entry.Health);
            return entry;
        }
    }

    /// <summary>
    /// Photographie des véhicules que le serveur considère pertinents pour un client (Unreliable, 10 Hz).
    ///
    /// Le serveur est la source de vérité : la position annoncée est celle de **son** véhicule, et le
    /// client l'applique avec une interpolation entre deux annonces (voir <c>RemoteVehicleManager</c>).
    /// Rien ne remonte du client : conduire un véhicule reste, pour l'instant, une simulation locale que
    /// ce flux ne transporte pas (M4 ne couvre que la position).
    /// </summary>
    public sealed class VehicleSnapshotMessage
    {
        public long ServerTick;
        public VehicleStateEntry[] Vehicles;

        public void Write(PacketWriter writer)
        {
            var list = Vehicles ?? Array.Empty<VehicleStateEntry>();
            if (list.Length > Protocol.MaxVehicleStates)
            {
                throw new ProtocolException($"trop de véhicules dans le snapshot ({list.Length} > {Protocol.MaxVehicleStates})");
            }

            writer.WriteByte((byte)MessageType.VehicleSnapshot);
            writer.WriteLong(ServerTick);
            writer.WriteByte((byte)list.Length);
            for (var i = 0; i < list.Length; i++)
            {
                list[i].Write(writer);
            }
        }

        public static VehicleSnapshotMessage Read(PacketReader reader)
        {
            var tick = reader.ReadLong();
            var count = reader.ReadByte();
            if (count > Protocol.MaxVehicleStates)
            {
                throw new ProtocolException($"trop de véhicules reçus ({count} > {Protocol.MaxVehicleStates})");
            }

            var vehicles = count == 0 ? Array.Empty<VehicleStateEntry>() : new VehicleStateEntry[count];
            for (var i = 0; i < count; i++)
            {
                vehicles[i] = VehicleStateEntry.Read(reader);
            }

            return new VehicleSnapshotMessage
            {
                ServerTick = tick,
                Vehicles = vehicles,
            };
        }
    }

    /// <summary>
    /// Modules installés sur un véhicule (ReliableOrdered, v15, M4) : « ce véhicule-là porte ces modules ».
    ///
    /// Pourquoi un message à part, et fiable : le snapshot de véhicule est **non fiable** et arrive dix fois
    /// par seconde ; y glisser un tableau variable de modules ferait perdre un module à la première perte de
    /// paquet, et l'écart ne se verrait qu'à la prochaine annonce. Un module change la flottabilité, la
    /// profondeur admissible et la charge utile : il doit arriver **une fois**, et être rejoué à un client qui
    /// se connecte après l'installation.
    ///
    /// Les modules sont identifiés par l'entier de `TechType`, pas par un nom : les deux côtés compilent le
    /// même jeu, donc la même énumération — un nom aurait ajouté une chaîne variable par module à un message
    /// déjà rejoué.
    /// </summary>
    public sealed class VehicleUpgradesMessage
    {
        /// <summary>Clé stable du véhicule, la même que dans le snapshot et la conduite.</summary>
        public long VehicleKey;

        /// <summary>Entiers de `TechType` des modules, **triés** par <see cref="Canonicalize"/>.</summary>
        public ushort[] TechTypes;

        /// <summary>
        /// Trie et déduplique les entiers reçus : l'ordre d'itération de l'équipement du jeu n'est pas
        /// garanti, et deux ensembles identiques doivent produire **le même** message — sinon le serveur
        /// croirait à un changement à chaque balayage et rediffuserait sans fin.
        /// </summary>
        public static ushort[] Canonicalize(ushort[] raw)
        {
            if (raw == null || raw.Length == 0)
            {
                return Array.Empty<ushort>();
            }

            var copy = (ushort[])raw.Clone();
            Array.Sort(copy);

            var unique = 0;
            for (var i = 0; i < copy.Length; i++)
            {
                if (i == 0 || copy[i] != copy[unique - 1])
                {
                    copy[unique++] = copy[i];
                }
            }

            if (unique > Protocol.MaxVehicleUpgrades)
            {
                throw new ProtocolException(
                    $"trop de modules pour un véhicule ({unique} > {Protocol.MaxVehicleUpgrades})");
            }

            if (unique == copy.Length)
            {
                return copy;
            }

            var trimmed = new ushort[unique];
            Array.Copy(copy, trimmed, unique);
            return trimmed;
        }

        /// <summary>Vrai si les deux ensembles sont identiques (les deux sont supposés canoniques).</summary>
        public static bool SameSet(ushort[] left, ushort[] right)
        {
            var a = left ?? Array.Empty<ushort>();
            var b = right ?? Array.Empty<ushort>();
            if (a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        public void Write(PacketWriter writer)
        {
            if (VehicleKey == 0L)
            {
                throw new ProtocolException("modules de véhicule sous la clé 0");
            }

            var list = TechTypes ?? Array.Empty<ushort>();
            if (list.Length > Protocol.MaxVehicleUpgrades)
            {
                throw new ProtocolException(
                    $"trop de modules à écrire ({list.Length} > {Protocol.MaxVehicleUpgrades})");
            }

            writer.WriteByte((byte)MessageType.VehicleUpgrades);
            writer.WriteLong(VehicleKey);
            writer.WriteByte((byte)list.Length);
            for (var i = 0; i < list.Length; i++)
            {
                writer.WriteUInt16(list[i]);
            }
        }

        public static VehicleUpgradesMessage Read(PacketReader reader)
        {
            var key = reader.ReadLong();
            if (key == 0L)
            {
                throw new ProtocolException("modules de véhicule reçus sous la clé 0");
            }

            var count = reader.ReadByte();
            if (count > Protocol.MaxVehicleUpgrades)
            {
                throw new ProtocolException(
                    $"trop de modules reçus ({count} > {Protocol.MaxVehicleUpgrades})");
            }

            var types = count == 0 ? Array.Empty<ushort>() : new ushort[count];
            for (var i = 0; i < count; i++)
            {
                types[i] = reader.ReadUInt16();
            }

            return new VehicleUpgradesMessage
            {
                VehicleKey = key,
                TechTypes = Canonicalize(types),
            };
        }
    }

    /// <summary>Regroupe l'écriture d'un message dans un tampon réutilisable.</summary>
    /// <summary>
    /// Réclamation d'un objet du monde (ReliableOrdered, protocole v8, M5) : « ce ramassage est à moi ».
    ///
    /// C'est la première action **autoritaire** du plan : le client ne fait plus seulement remonter une
    /// position, il demande une décision. `ItemKey` est l'identité partagée de l'objet (même valeur des deux
    /// côtés, comme pour les créatures et les véhicules) ; le serveur la compare à sa carte des objets déjà
    /// réclamés et répond par <see cref="PickupResultMessage"/>.
    ///
    /// `PlayerId` est **revendiqué** et falsifiable : le serveur ne le recopie jamais, il prend l'identité de
    /// la session et se sert de l'écart pour compter la falsification — même règle que l'équipement, et c'est
    /// ce qui rend la règle mesurable au lieu de lisible.
    /// </summary>
    public sealed class PickupClaimMessage
    {
        public ushort PlayerId;
        public long ItemKey;
        public string TechTypeName;

        public void Write(PacketWriter writer)
        {
            var name = TechTypeName ?? string.Empty;
            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            writer.WriteByte((byte)MessageType.PickupClaim);
            writer.WriteUInt16(PlayerId);
            writer.WriteLong(ItemKey);
            writer.WriteString(name);
        }

        public static PickupClaimMessage Read(PacketReader reader)
        {
            var playerId = reader.ReadUInt16();
            var itemKey = reader.ReadLong();
            var name = reader.ReadString();
            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType reçu trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            return new PickupClaimMessage
            {
                PlayerId = playerId,
                ItemKey = itemKey,
                TechTypeName = name,
            };
        }
    }

    /// <summary>
    /// Décision du serveur sur une réclamation (ReliableOrdered, v8), envoyée au **seul demandeur**.
    ///
    /// `HolderId` est le propriétaire **effectif** — celui de la session qui a réclamé en premier — jamais
    /// celui que le message revendiquait. `ClaimedBy` recopie la revendication pour que le client puisse
    /// constater lui-même la réécriture, comme le fait déjà l'aller-retour d'équipement.
    /// </summary>
    public sealed class PickupResultMessage
    {
        public long ItemKey;
        public string TechTypeName;
        public bool Granted;
        public ushort HolderId;
        public ushort ClaimedBy;

        public void Write(PacketWriter writer)
        {
            // Même borne qu'à la lecture : sans elle, le serveur pouvait émettre un message que
            // ses propres clients refusaient ensuite (nom > 64 caractères accepté par WriteString).
            var name = TechTypeName ?? string.Empty;
            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            writer.WriteByte((byte)MessageType.PickupResult);
            writer.WriteLong(ItemKey);
            writer.WriteString(name);
            writer.WriteBool(Granted);
            writer.WriteUInt16(HolderId);
            writer.WriteUInt16(ClaimedBy);
        }

        public static PickupResultMessage Read(PacketReader reader)
        {
            var itemKey = reader.ReadLong();
            var name = reader.ReadString();
            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType reçu trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            return new PickupResultMessage
            {
                ItemKey = itemKey,
                TechTypeName = name,
                Granted = reader.ReadBool(),
                HolderId = reader.ReadUInt16(),
                ClaimedBy = reader.ReadUInt16(),
            };
        }
    }

    /// <summary>
    /// État de conduite d'un véhicule (Unreliable, 10 Hz, protocole v9, M4) : « ce véhicule-là est conduit
    /// par moi, et voici où il est ».
    ///
    /// C'est la première fois qu'un client ne se contente pas d'annoncer sa position : il annonce celle
    /// d'une **entité du monde**. Le serveur ne le croit pas sur parole — il vérifie que la clé désigne un
    /// de **ses** véhicules, que le joueur est à moins de `MaxVehicleControlRangeMeters`, et qu'aucun autre
    /// joueur ne conduisait déjà ce véhicule.
    ///
    /// `PlayerId` est **revendiqué** et falsifiable, comme pour la réclamation d'objet : le serveur prend
    /// l'identité de la session et compte l'écart.
    /// </summary>
    public sealed class VehicleControlMessage
    {
        public ushort PlayerId;
        public long VehicleKey;

        /// <summary>Numéro d'ordre, croissant : il ne sert qu'à repérer un état arrivé dans le désordre.</summary>
        public int Sequence;

        public float X, Y, Z;
        public float RotX, RotY, RotZ, RotW;
        public float SpeedX, SpeedY, SpeedZ;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.VehicleControl);
            writer.WriteUInt16(PlayerId);
            writer.WriteLong(VehicleKey);
            writer.WriteInt(Sequence);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
            writer.WriteFloat(SpeedX);
            writer.WriteFloat(SpeedY);
            writer.WriteFloat(SpeedZ);
        }

        public static VehicleControlMessage Read(PacketReader reader)
        {
            var msg = new VehicleControlMessage
            {
                PlayerId = reader.ReadUInt16(),
                VehicleKey = reader.ReadLong(),
                Sequence = reader.ReadInt(),
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat(),
                RotX = reader.ReadFloat(),
                RotY = reader.ReadFloat(),
                RotZ = reader.ReadFloat(),
                RotW = reader.ReadFloat(),
                SpeedX = reader.ReadFloat(),
                SpeedY = reader.ReadFloat(),
                SpeedZ = reader.ReadFloat(),
            };

            PlayerPositionMessage.EnsureFinite(
                msg.X, msg.Y, msg.Z,
                msg.RotX, msg.RotY, msg.RotZ, msg.RotW,
                msg.SpeedX, msg.SpeedY, msg.SpeedZ);

            return msg;
        }
    }

    /// <summary>
    /// Décision du serveur sur une conduite (ReliableOrdered, v9), envoyée au seul demandeur.
    ///
    /// Elle existe pour que le client **sache** qu'il ne conduit pas : sans elle, un second joueur
    /// enverrait son état dans le vide et son véhicule local divergerait sans un mot. `DriverId` est le
    /// conducteur **effectif**, jamais celui que le message revendiquait.
    /// </summary>
    public sealed class VehicleControlResultMessage
    {
        public long VehicleKey;
        public bool Granted;
        public ushort DriverId;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.VehicleControlResult);
            writer.WriteLong(VehicleKey);
            writer.WriteBool(Granted);
            writer.WriteUInt16(DriverId);
        }

        public static VehicleControlResultMessage Read(PacketReader reader)
        {
            return new VehicleControlResultMessage
            {
                VehicleKey = reader.ReadLong(),
                Granted = reader.ReadBool(),
                DriverId = reader.ReadUInt16(),
            };
        }
    }

    /// <summary>
    /// Annonce d'une entité **fabriquée par le serveur** (ReliableOrdered, v10, M6) : « ce véhicule vient
    /// d'apparaître, voici son identité, sa position et son orientation ».
    ///
    /// Pourquoi l'identité voyage dans le message : une entité créée à l'exécution reçoit un
    /// `UniqueIdentifier` **neuf dans chaque instance** (mesuré en J : deux `spawn` de Seamoth donnent deux
    /// clés différentes). Sans ça, le client créerait un objet impossible à apparier aux positions
    /// suivantes. Le client **écrit** l'id du serveur sur son exemplaire, et les deux clés coïncident.
    ///
    /// `TechTypeName` est le nom d'espèce (`SeaMoth`), pas celui du prefab : c'est ce que le client sait
    /// résoudre pour instancier.
    /// </summary>
    public sealed class EntitySpawnMessage
    {
        public long Key;
        public string UniqueId;
        public string TechTypeName;
        public float X, Y, Z;
        public float RotX, RotY, RotZ, RotW;

        /// <summary>Échelle de la définition (1,1,1 pour un véhicule du jeu).</summary>
        public float ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f;

        /// <summary>
        /// Empreinte de la définition que le serveur applique (v13, P1bis). Le client la **recalcule**
        /// et refuse l'entité si elle diffère : c'est ce qui rend le contrat de contenu falsifiable
        /// au lieu d'être une convention. `0` est refusé à l'écriture comme à la lecture — aucune
        /// définition réelle ne produit un hash nul.
        /// </summary>
        public long DefinitionHash;

        public EntityDefinition BuildDefinition() => new EntityDefinition
        {
            TechTypeName = TechTypeName ?? string.Empty,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
            ScaleZ = ScaleZ,
        };

        public void Write(PacketWriter writer)
        {
            // Même règle que `EntityDespawn` : une entité annoncée sous la clé 0 ne pourrait
            // jamais être retirée ni réclamée, puisque ces deux messages refusent la clé 0.
            // Annoncer une entité impossible à désigner produirait un fantôme permanent.
            if (Key == 0L)
            {
                throw new ProtocolException("apparition sans clé d'entité (clé=0)");
            }

            var id = UniqueId ?? string.Empty;
            if (id.Length > Protocol.MaxEntityIdLength)
            {
                throw new ProtocolException($"identifiant d'entité trop long ({id.Length} > {Protocol.MaxEntityIdLength})");
            }

            // Un identifiant vide ne porte aucune identité : le client s'en sert comme `UniqueIdentifier`
            // pour retrouver l'objet dans sa sauvegarde, et une chaîne vide ne désigne rien. Le serveur
            // n'annonce jamais cet état (il renonce à l'annonce sans identifiant).
            if (id.Length == 0)
            {
                throw new ProtocolException("apparition sans identifiant d'entité (UniqueId vide)");
            }

            var name = TechTypeName ?? string.Empty;
            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            if (DefinitionHash == 0L)
            {
                throw new ProtocolException("apparition sans empreinte de définition (definitionHash=0)");
            }

            // La lecture refuse déjà ces valeurs : les accepter à l'écriture produirait un
            // message que nos propres clients rejetteraient, et l'entité ne serait jamais créée.
            PlayerPositionMessage.EnsureFinite(
                X, Y, Z,
                RotX, RotY, RotZ, RotW,
                ScaleX, ScaleY, ScaleZ);

            writer.WriteByte((byte)MessageType.EntitySpawn);
            writer.WriteLong(Key);
            writer.WriteString(id);
            writer.WriteString(name);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
            writer.WriteFloat(ScaleX);
            writer.WriteFloat(ScaleY);
            writer.WriteFloat(ScaleZ);
            writer.WriteLong(DefinitionHash);
        }

        public static EntitySpawnMessage Read(PacketReader reader)
        {
            var msg = new EntitySpawnMessage
            {
                Key = reader.ReadLong(),
                UniqueId = reader.ReadString(),
                TechTypeName = reader.ReadString(),
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat(),
                RotX = reader.ReadFloat(),
                RotY = reader.ReadFloat(),
                RotZ = reader.ReadFloat(),
                RotW = reader.ReadFloat(),
                ScaleX = reader.ReadFloat(),
                ScaleY = reader.ReadFloat(),
                ScaleZ = reader.ReadFloat(),
                DefinitionHash = reader.ReadLong(),
            };

            if (msg.Key == 0L)
            {
                throw new ProtocolException("apparition reçue sans clé d'entité (clé=0)");
            }

            if (msg.UniqueId.Length == 0)
            {
                throw new ProtocolException("apparition reçue sans identifiant d'entité (UniqueId vide)");
            }

            if (msg.UniqueId.Length > Protocol.MaxEntityIdLength)
            {
                throw new ProtocolException($"identifiant d'entité reçu trop long ({msg.UniqueId.Length})");
            }

            if (msg.TechTypeName.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType reçu trop long ({msg.TechTypeName.Length})");
            }

            if (msg.DefinitionHash == 0L)
            {
                throw new ProtocolException("apparition sans empreinte de définition (definitionHash=0)");
            }

            PlayerPositionMessage.EnsureFinite(
                msg.X, msg.Y, msg.Z,
                msg.RotX, msg.RotY, msg.RotZ, msg.RotW,
                msg.ScaleX, msg.ScaleY, msg.ScaleZ);

            return msg;
        }
    }

    /// <summary>
    /// Position autoritaire renvoyée au client dont la position a été refusée (v13, P1).
    ///
    /// Pourquoi un message et pas un simple silence : le serveur a refusé la position et garde la
    /// précédente. Sans correction, le client continuerait à se voir à l'endroit refusé tandis que
    /// tous les autres le voient à l'ancien — une divergence qui ne se corrige jamais. Le message
    /// porte la position **acceptée** et le motif, pour que le client puisse la journaliser et la
    /// distinguer d'un simple retard réseau.
    /// </summary>
    public sealed class PlayerCorrectionMessage
    {
        public long ServerTick;
        public float X, Y, Z;
        public string Reason;

        public void Write(PacketWriter writer)
        {
            var reason = Reason ?? string.Empty;
            if (reason.Length > Protocol.MaxCorrectionReasonLength)
            {
                throw new ProtocolException($"motif de correction trop long ({reason.Length} > {Protocol.MaxCorrectionReasonLength})");
            }

            // La lecture refuse une correction non finie : l'écrire serait envoyer au client
            // un message qu'il rejette, donc une correction perdue sans le dire.
            PlayerPositionMessage.EnsureFinite(X, Y, Z, 0f, 0f, 0f, 1f);

            writer.WriteByte((byte)MessageType.PlayerCorrection);
            writer.WriteLong(ServerTick);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteString(reason);
        }

        public static PlayerCorrectionMessage Read(PacketReader reader)
        {
            var msg = new PlayerCorrectionMessage
            {
                ServerTick = reader.ReadLong(),
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat(),
                Reason = reader.ReadString(),
            };

            if (msg.Reason.Length > Protocol.MaxCorrectionReasonLength)
            {
                throw new ProtocolException($"motif de correction reçu trop long ({msg.Reason.Length})");
            }

            PlayerPositionMessage.EnsureFinite(msg.X, msg.Y, msg.Z, 0, 0, 0, 1);
            return msg;
        }
    }

    /// <summary>
    /// Libération du volant (ReliableOrdered, v12, M4) : « je ne conduis plus ce véhicule ».
    ///
    /// Pourquoi un message alors que le serveur pourrait se contenter du silence : le silence est
    /// indiscernable d'une latence tant qu'il dure, alors qu'une descente du véhicule est un fait immédiat.
    /// Le message rend le volant **tout de suite** ; le silence reste le filet de sécurité, au bout de
    /// `VehicleSilenceReleaseSeconds` (`PLAN.md` §42).
    ///
    /// Aucun champ d'identité de joueur : c'est la session qui libère, jamais « au nom de » quelqu'un.
    /// </summary>
    public sealed class VehicleReleaseMessage
    {
        public long VehicleKey;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.VehicleRelease);
            writer.WriteLong(VehicleKey);
        }

        public static VehicleReleaseMessage Read(PacketReader reader)
        {
            return new VehicleReleaseMessage
            {
                VehicleKey = reader.ReadLong(),
            };
        }
    }

    /// <summary>
    /// Espèce d'entité qu'un <see cref="EntityDespawnMessage"/> désigne.
    ///
    /// Pourquoi un octet plutôt qu'un message par espèce : la clé d'un véhicule et celle d'un objet sont
    /// deux hachages de formes différentes, et sans cette étiquette le client devrait essayer les deux
    /// index. La valeur `0` est réservée pour qu'un paquet tronqué ne se lise pas comme « véhicule ».
    /// </summary>
    public static class EntityKind
    {
        /// <summary>Valeur non écrite : un message qui la porte est refusé à la lecture.</summary>
        public const byte None = 0;

        /// <summary>Véhicule : clé de <c>VehicleIdentity</c>.</summary>
        public const byte Vehicle = 1;

        /// <summary>Objet ramassable : clé de <c>ItemIdentity</c>.</summary>
        public const byte Item = 2;

        /// <summary>
        /// Créature : clé de <c>CreatureIdentity</c> (v17). Un troisième index, parce qu'une clé de
        /// créature est un troisième hachage de forme différente — la confondre avec un objet ferait
        /// chercher un `Pickupable` à la place d'un poisson.
        /// </summary>
        public const byte Creature = 3;
    }

    /// <summary>
    /// Annonce de la **disparition** d'une entité (ReliableOrdered, v11, M6) : « ce véhicule a été détruit »,
    /// « cet objet a été ramassé ».
    ///
    /// Pourquoi ce message existe alors que la v10 savait créer : une entité créée reste sinon chez tout le
    /// monde après sa destruction chez l'hôte, et un objet ramassé par un joueur reste **visible et
    /// réclamable** par les autres — le serveur accordait alors une clé qui ne désignait plus rien. La
    /// décision de retrait est prise **par le serveur seul** : le message part de lui, ne se demande pas,
    /// et `Reason` ne sert qu'au journal.
    ///
    /// Aucun champ de position : un retrait n'a pas de lieu. Une clé `0` est refusée à l'écriture, comme
    /// pour l'apparition.
    /// </summary>
    public sealed class EntityDespawnMessage
    {
        public long Key;

        /// <summary><see cref="EntityKind.Vehicle"/>, <see cref="EntityKind.Item"/> ou <see cref="EntityKind.Creature"/> (v17).</summary>
        public byte Kind;

        /// <summary>Motif lisible, pour le journal du receveur · jamais interprété.</summary>
        public string Reason;

        public void Write(PacketWriter writer)
        {
            if (Key == 0L)
            {
                throw new ProtocolException("disparition sans clé d'entité (clé=0)");
            }

            if (Kind != EntityKind.Vehicle && Kind != EntityKind.Item && Kind != EntityKind.Creature)
            {
                throw new ProtocolException($"espèce d'entité invalide ({Kind}) dans une disparition");
            }

            var reason = Reason ?? string.Empty;
            if (reason.Length > Protocol.MaxDespawnReasonLength)
            {
                throw new ProtocolException($"motif de disparition trop long ({reason.Length} > {Protocol.MaxDespawnReasonLength})");
            }

            writer.WriteByte((byte)MessageType.EntityDespawn);
            writer.WriteLong(Key);
            writer.WriteByte(Kind);
            writer.WriteString(reason);
        }

        public static EntityDespawnMessage Read(PacketReader reader)
        {
            var msg = new EntityDespawnMessage
            {
                Key = reader.ReadLong(),
                Kind = reader.ReadByte(),
                Reason = reader.ReadString(),
            };

            if (msg.Key == 0L)
            {
                throw new ProtocolException("disparition reçue sans clé d'entité (clé=0)");
            }

            if (msg.Kind != EntityKind.Vehicle && msg.Kind != EntityKind.Item && msg.Kind != EntityKind.Creature)
            {
                throw new ProtocolException($"espèce d'entité inconnue ({msg.Kind}) dans une disparition");
            }

            if (msg.Reason.Length > Protocol.MaxDespawnReasonLength)
            {
                throw new ProtocolException($"motif de disparition reçu trop long ({msg.Reason.Length})");
            }

            return msg;
        }
    }

    /// <summary>
    /// Aides de texte partagées par les messages qui transportent un motif **lisible**. Une borne
    /// appliquée à un seul des deux sens finirait par produire un message que nos propres clients
    /// refusent — c'est exactement la classe de défaut que cette session ferme.
    /// </summary>
    internal static class MessageText
    {
        public static string Reason(string value, string label)
        {
            var reason = value ?? string.Empty;
            if (reason.Length > Protocol.MaxReasonTextLength)
            {
                throw new ProtocolException($"{label} trop long ({reason.Length} > {Protocol.MaxReasonTextLength})");
            }

            return reason;
        }
    }

    /// <summary>
    /// Demande de fabrication d'une entité par un client (ReliableOrdered, v16, M4/M6).
    ///
    /// Pourquoi un message descendant : une entité fabriquée par un joueur n'existe que dans **son**
    /// monde. La laisser vivre localement reviendrait à la voir seul, à la sauvegarder peut-être, et à
    /// diverger des autres instances — le contraire du modèle de ce projet. Le client **demande** donc,
    /// le serveur fabrique, et l'annonce existante (<see cref="EntitySpawnMessage"/>) crée la copie chez
    /// tout le monde, demandeur compris.
    ///
    /// `TechType` voyage en entier : les deux côtés compilent le même jeu, donc la même énumération
    /// (même choix que les modules de véhicule, v15). Le serveur n'accepte qu'une **liste blanche** :
    /// une valeur reçue n'est pas une permission.
    ///
    /// Les nombres sont finis ou le message est refusé : une position `NaN` créerait une entité que
    /// personne ne pourrait ni voir ni retirer.
    /// </summary>
    public sealed class EntitySpawnRequestMessage
    {
        /// <summary>Identifiant choisi par le client, rendu tel quel dans la décision.</summary>
        public uint RequestId;

        /// <summary>Entier de `TechType` demandé (`Seamoth`, `Exosuit`…), jamais 0.</summary>
        public ushort TechType;

        public float X, Y, Z;
        public float RotX, RotY, RotZ, RotW;

        public void Write(PacketWriter writer)
        {
            if (TechType == 0)
            {
                throw new ProtocolException("demande d'apparition sans TechType (0)");
            }

            PlayerPositionMessage.EnsureFinite(X, Y, Z, RotX, RotY, RotZ, RotW);

            writer.WriteByte((byte)MessageType.EntitySpawnRequest);
            writer.WriteInt((int)RequestId);
            writer.WriteUInt16(TechType);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
        }

        public static EntitySpawnRequestMessage Read(PacketReader reader)
        {
            var msg = new EntitySpawnRequestMessage
            {
                RequestId = (uint)reader.ReadInt(),
                TechType = reader.ReadUInt16(),
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat(),
                RotX = reader.ReadFloat(),
                RotY = reader.ReadFloat(),
                RotZ = reader.ReadFloat(),
                RotW = reader.ReadFloat(),
            };

            if (msg.TechType == 0)
            {
                throw new ProtocolException("demande d'apparition reçue sans TechType (0)");
            }

            PlayerPositionMessage.EnsureFinite(
                msg.X, msg.Y, msg.Z,
                msg.RotX, msg.RotY, msg.RotZ, msg.RotW);

            return msg;
        }
    }

    /// <summary>
    /// Décision du serveur sur une demande de fabrication (ReliableOrdered, v16), envoyée au seul
    /// demandeur.
    ///
    /// Pourquoi une décision explicite : une demande refusée sans réponse laisserait le joueur devant un
    /// objet qui n'apparaît pas, sans savoir si c'est un bug ou un refus. Le motif est **nommé** et
    /// transporté, comme pour le ramassage (v8) et la conduite (v9).
    ///
    /// Une fabrication accordée porte la clé du serveur : c'est la même que celle de l'annonce
    /// <see cref="EntitySpawnMessage"/> qui suit, et c'est ce qui permet au demandeur de vérifier que
    /// l'entité qu'il voit est bien celle qu'il a demandée.
    /// </summary>
    public sealed class EntitySpawnResultMessage
    {
        public uint RequestId;
        public bool Granted;

        /// <summary>Clé de l'entité fabriquée — 0 si la demande est refusée.</summary>
        public long Key;

        public string Reason = string.Empty;

        public void Write(PacketWriter writer)
        {
            var reason = Reason ?? string.Empty;
            if (reason.Length > Protocol.MaxSpawnReasonLength)
            {
                throw new ProtocolException($"motif d'apparition trop long ({reason.Length} > {Protocol.MaxSpawnReasonLength})");
            }

            // Une décision *accordée* sans clé ne désigne rien : le client ne pourrait pas relier
            // l'annonce qui suit à sa demande. Et un refus muet n'explique rien.
            if (Granted && Key == 0L)
            {
                throw new ProtocolException("apparition accordée sans clé d'entité");
            }

            if (!Granted && reason.Length == 0)
            {
                throw new ProtocolException("refus d'apparition sans motif");
            }

            writer.WriteByte((byte)MessageType.EntitySpawnResult);
            writer.WriteInt((int)RequestId);
            writer.WriteByte((byte)(Granted ? 1 : 0));
            writer.WriteLong(Key);
            writer.WriteString(reason);
        }

        public static EntitySpawnResultMessage Read(PacketReader reader)
        {
            var msg = new EntitySpawnResultMessage
            {
                RequestId = (uint)reader.ReadInt(),
                Granted = reader.ReadByte() != 0,
                Key = reader.ReadLong(),
                Reason = reader.ReadString(),
            };

            if (msg.Reason.Length > Protocol.MaxSpawnReasonLength)
            {
                throw new ProtocolException($"motif d'apparition reçu trop long ({msg.Reason.Length})");
            }

            if (msg.Granted && msg.Key == 0L)
            {
                throw new ProtocolException("apparition accordée reçue sans clé d'entité");
            }

            return msg;
        }
    }

    /// <summary>
    /// Heure du monde (Unreliable, 1 Hz, v18) : « il est <c>TimePassed</c> secondes dans le monde du
    /// serveur ».
    ///
    /// Pourquoi un message : chaque instance simule sa propre horloge, et un client qui rejoint charge
    /// l'heure **de la sauvegarde** — pas celle du serveur, qui a continué d'avancer pendant le
    /// chargement. Deux joueurs pouvaient donc vivre deux moments de la journée, l'un en plein jour et
    /// l'autre de nuit.
    ///
    /// Le client ne s'aligne que si l'écart dépasse
    /// <see cref="Protocol.WorldTimeApplyThresholdSeconds"/> : l'heure avance toute seule des deux côtés,
    /// et réécrire à chaque paquet ferait sauter le soleil sans rien corriger.
    /// </summary>
    public sealed class WorldTimeMessage
    {
        /// <summary>Secondes écoulées dans le monde du serveur (`DayNightCycle.timePassedAsFloat`).</summary>
        public float TimePassed;

        public void Write(PacketWriter writer)
        {
            if (float.IsNaN(TimePassed) || float.IsInfinity(TimePassed))
            {
                throw new ProtocolException($"heure du monde non finie ({TimePassed})");
            }

            if (TimePassed < 0f)
            {
                throw new ProtocolException($"heure du monde négative ({TimePassed})");
            }

            writer.WriteByte((byte)MessageType.WorldTime);
            writer.WriteFloat(TimePassed);
        }

        public static WorldTimeMessage Read(PacketReader reader)
        {
            var message = new WorldTimeMessage
            {
                TimePassed = reader.ReadFloat(),
            };

            if (float.IsNaN(message.TimePassed) || float.IsInfinity(message.TimePassed))
            {
                throw new ProtocolException($"heure du monde reçue non finie ({message.TimePassed})");
            }

            if (message.TimePassed < 0f)
            {
                throw new ProtocolException($"heure du monde reçue négative ({message.TimePassed})");
            }

            return message;
        }
    }

    /// <summary>
    /// Inventaire d'un joueur, poussé par le serveur à son propriétaire (ReliableOrdered, v19).
    ///
    /// Pourquoi un instantané entier plutôt qu'un delta : les mutations sont **rares** (un ramassage
    /// accordé), l'objet tient dans un paquet, et un instantané complet rend la réconciliation triviale —
    /// un delta perdu laisserait le client avec un inventaire faux jusqu'à la déconnexion, alors qu'un
    /// instantané reçu remplace l'état précédent entier.
    ///
    /// Le tri et l'unicité des noms sont **vérifiés** à l'écriture comme à la lecture. Sans cela, deux
    /// paquets pouvaient décrire le même contenu dans deux ordres, et un contenu avec deux lignes du même
    /// objet aurait deux significations (somme ou écrasement) selon le lecteur.
    /// </summary>
    public sealed class InventorySnapshotMessage
    {
        /// <summary>Numéro d'ordre des changements serveur ; `0` = inventaire d'admission, vide.</summary>
        public int Revision;

        public InventoryEntry[] Entries = Array.Empty<InventoryEntry>();

        public void Write(PacketWriter writer)
        {
            EnsureValid(Entries, "instantané");

            if (Revision < 0)
            {
                throw new ProtocolException($"révision d'inventaire négative ({Revision})");
            }

            writer.WriteByte((byte)MessageType.InventorySnapshot);
            writer.WriteInt(Revision);
            writer.WriteByte((byte)Entries.Length);

            foreach (var entry in Entries)
            {
                writer.WriteString(entry.TechTypeName);
                writer.WriteUInt16((ushort)entry.Count);
            }
        }

        public static InventorySnapshotMessage Read(PacketReader reader)
        {
            var revision = reader.ReadInt();
            if (revision < 0)
            {
                throw new ProtocolException($"révision d'inventaire reçue négative ({revision})");
            }

            var count = reader.ReadByte();
            if (count > Protocol.MaxInventoryEntries)
            {
                throw new ProtocolException(
                    $"instantané d'inventaire trop long ({count} > {Protocol.MaxInventoryEntries})");
            }

            var entries = new InventoryEntry[count];
            for (var i = 0; i < count; i++)
            {
                entries[i] = new InventoryEntry(reader.ReadString(), reader.ReadUInt16());
            }

            EnsureValid(entries, "instantané reçu");

            return new InventorySnapshotMessage
            {
                Revision = revision,
                Entries = entries,
            };
        }

        /// <summary>
        /// Refuse un contenu que le lecteur interpréterait de deux façons : nom vide, quantité nulle ou
        /// au-delà du plafond, doublon, ordre non croissant, trop de lignes.
        /// </summary>
        internal static void EnsureValid(InventoryEntry[] entries, string label)
        {
            if (entries == null)
            {
                throw new ProtocolException($"{label} sans contenu (null)");
            }

            if (entries.Length > Protocol.MaxInventoryEntries)
            {
                throw new ProtocolException(
                    $"{label} trop long ({entries.Length} > {Protocol.MaxInventoryEntries})");
            }

            string previous = null;

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.TechTypeName))
                {
                    throw new ProtocolException($"{label} avec un nom d'objet vide");
                }

                if (entry.TechTypeName.Length > Protocol.MaxTechTypeNameLength)
                {
                    throw new ProtocolException(
                        $"{label} : nom d'objet trop long ({entry.TechTypeName.Length} > {Protocol.MaxTechTypeNameLength})");
                }

                if (entry.Count <= 0 || entry.Count > Protocol.MaxInventoryStack)
                {
                    throw new ProtocolException(
                        $"{label} : quantité hors bornes pour '{entry.TechTypeName}' ({entry.Count})");
                }

                if (previous != null && string.CompareOrdinal(previous, entry.TechTypeName) >= 0)
                {
                    throw new ProtocolException(
                        $"{label} : objets non triés ou dupliqués ('{previous}' puis '{entry.TechTypeName}')");
                }

                previous = entry.TechTypeName;
            }
        }
    }

    /// <summary>
    /// Demande de **débit** d'inventaire (ReliableOrdered, v20), émise par le propriétaire du registre.
    ///
    /// `PlayerId` est **revendiqué** : le serveur prend l'identité de la session, jamais celle du message.
    /// Sinon un client pourrait vider l'inventaire d'un autre joueur — un débit est une mutation, même
    /// s'il ne peut que diminuer.
    ///
    /// Le message ne dit pas *pourquoi* l'objet est consommé (craft, dépôt, dégât) : le registre n'a pas
    /// à connaître la cause, seulement la quantité qui en sort. Le lot qui introduira le coût de
    /// fabrication ajoutera sa propre vérification de matériaux ; ici, la seule règle est « on ne retire
    /// pas plus que ce que le registre contient ».
    /// </summary>
    public sealed class InventoryDebitMessage
    {
        public ushort PlayerId;
        public string TechTypeName;
        public int Count;

        public void Write(PacketWriter writer)
        {
            var name = TechTypeName ?? string.Empty;
            if (name.Length == 0)
            {
                throw new ProtocolException("débit d'inventaire sans nom d'objet");
            }

            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException(
                    $"nom de TechType trop long pour un débit ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            if (Count <= 0 || Count > Protocol.MaxInventoryStack)
            {
                throw new ProtocolException($"quantité de débit hors bornes ({Count})");
            }

            writer.WriteByte((byte)MessageType.InventoryDebit);
            writer.WriteUInt16(PlayerId);
            writer.WriteString(name);
            writer.WriteUInt16((ushort)Count);
        }

        public static InventoryDebitMessage Read(PacketReader reader)
        {
            var playerId = reader.ReadUInt16();
            var name = reader.ReadString();
            if (name.Length == 0)
            {
                throw new ProtocolException("débit d'inventaire reçu sans nom d'objet");
            }

            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException(
                    $"nom de TechType reçu trop long pour un débit ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            var count = reader.ReadUInt16();
            if (count == 0 || count > Protocol.MaxInventoryStack)
            {
                throw new ProtocolException($"quantité de débit reçue hors bornes ({count})");
            }

            return new InventoryDebitMessage
            {
                PlayerId = playerId,
                TechTypeName = name,
                Count = count,
            };
        }
    }

    /// <summary>
    /// Décision du serveur sur un débit (ReliableOrdered, v20), envoyée au **seul demandeur**.
    ///
    /// Le refus est **nommé** par <see cref="InventoryChangeOutcome"/> (`UnknownItem`, `NotEnough`,
    /// `Invalid`, `InventoryFull`) : le client peut alors dire au joueur ce qui n'a pas été consommé,
    /// et une sonde peut distinguer « refusé » de « jamais demandé ».
    ///
    /// `Granted` et `Reason` sont tenus cohérents à l'écriture comme à la lecture (`Granted` si et
    /// seulement si `Reason == Applied`) : deux champs qui se contrediraient donneraient deux lectures du
    /// même paquet, ce que le reste du protocole refuse déjà pour les instantanés.
    /// </summary>
    public sealed class InventoryDebitResultMessage
    {
        public string TechTypeName;
        public int Count;
        public bool Granted;
        public InventoryChangeOutcome Reason;
        public int Revision;
        public ushort ClaimedBy;

        public void Write(PacketWriter writer)
        {
            Validate(TechTypeName, Count, Granted, Reason, Revision, "débit");

            writer.WriteByte((byte)MessageType.InventoryDebitResult);
            writer.WriteString(TechTypeName);
            writer.WriteUInt16((ushort)Count);
            writer.WriteBool(Granted);
            writer.WriteByte((byte)Reason);
            writer.WriteInt(Revision);
            writer.WriteUInt16(ClaimedBy);
        }

        public static InventoryDebitResultMessage Read(PacketReader reader)
        {
            var name = reader.ReadString();
            var count = reader.ReadUInt16();
            var granted = reader.ReadBool();
            var reason = (InventoryChangeOutcome)reader.ReadByte();
            var revision = reader.ReadInt();
            var claimedBy = reader.ReadUInt16();

            Validate(name, count, granted, reason, revision, "débit reçu");

            return new InventoryDebitResultMessage
            {
                TechTypeName = name,
                Count = count,
                Granted = granted,
                Reason = reason,
                Revision = revision,
                ClaimedBy = claimedBy,
            };
        }

        private static void Validate(
            string name,
            int count,
            bool granted,
            InventoryChangeOutcome reason,
            int revision,
            string label)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ProtocolException($"{label} sans nom d'objet");
            }

            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException(
                    $"{label} : nom d'objet trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

            if (count <= 0 || count > Protocol.MaxInventoryStack)
            {
                throw new ProtocolException($"{label} : quantité hors bornes ({count})");
            }

            if (!Enum.IsDefined(typeof(InventoryChangeOutcome), reason))
            {
                throw new ProtocolException($"{label} : issue inconnue ({(byte)reason})");
            }

            if (granted != (reason == InventoryChangeOutcome.Applied))
            {
                throw new ProtocolException(
                    $"{label} : accord ({granted}) incohérent avec l'issue ({reason})");
            }

            if (revision < 0)
            {
                throw new ProtocolException($"{label} : révision négative ({revision})");
            }
        }
    }

    /// <summary>
    /// Demande d'adoption de l'inventaire (v21), serveur → client. Aucune charge utile : c'est un
    /// **ordre de dire une fois** ce que le joueur possède, pas une question.
    /// </summary>
    public sealed class InventoryAdoptionRequestMessage
    {
        /// <summary>Numéro de la demande pour cette session (`1` à la première). Sert au journal et rend
        /// un paquet tronqué **détectable** : un message sans charge utile serait indiscernable d'un octet
        /// d'opcode seul.</summary>
        public int Sequence;

        public void Write(PacketWriter writer)
        {
            if (Sequence <= 0)
            {
                throw new ProtocolException($"demande d'adoption avec un numéro non positif ({Sequence})");
            }

            writer.WriteByte((byte)MessageType.InventoryAdoptionRequest);
            writer.WriteInt(Sequence);
        }

        public static InventoryAdoptionRequestMessage Read(PacketReader reader)
        {
            var sequence = reader.ReadInt();
            if (sequence <= 0)
            {
                throw new ProtocolException($"demande d'adoption reçue avec un numéro non positif ({sequence})");
            }

            return new InventoryAdoptionRequestMessage { Sequence = sequence };
        }
    }

    /// <summary>
    /// Inventaire initial d'un joueur (v21), en réponse à <see cref="InventoryAdoptionRequestMessage"/>.
    ///
    /// Même contrat de forme que l'instantané : trié, sans doublon, quantités bornées. Le serveur refuse
    /// le tout — jamais une ligne — si le contenu est ambigu : une adoption partielle serait un inventaire
    /// faux conservé sur disque.
    /// </summary>
    public sealed class InventoryAdoptMessage
    {
        public InventoryEntry[] Entries = Array.Empty<InventoryEntry>();

        public void Write(PacketWriter writer)
        {
            InventorySnapshotMessage.EnsureValid(Entries, "adoption");

            writer.WriteByte((byte)MessageType.InventoryAdopt);
            writer.WriteByte((byte)Entries.Length);

            foreach (var entry in Entries)
            {
                writer.WriteString(entry.TechTypeName);
                writer.WriteUInt16((ushort)entry.Count);
            }
        }

        public static InventoryAdoptMessage Read(PacketReader reader)
        {
            var count = reader.ReadByte();
            if (count > Protocol.MaxInventoryEntries)
            {
                throw new ProtocolException(
                    $"adoption trop longue ({count} > {Protocol.MaxInventoryEntries})");
            }

            var entries = new InventoryEntry[count];
            for (var i = 0; i < count; i++)
            {
                entries[i] = new InventoryEntry(reader.ReadString(), reader.ReadUInt16());
            }

            InventorySnapshotMessage.EnsureValid(entries, "adoption reçue");

            return new InventoryAdoptMessage { Entries = entries };
        }
    }

    /// <summary>
    /// Demande de craft (v21). Le client ne débite rien lui-même et ne fabrique rien avant la réponse :
    /// c'est le serveur qui lit la recette du jeu et paie le coût depuis son registre.
    /// </summary>
    public sealed class CraftRequestMessage
    {
        public int RequestId;
        public ushort TechType;

        public void Write(PacketWriter writer)
        {
            if (TechType == 0)
            {
                throw new ProtocolException("demande de craft sans TechType (0)");
            }

            // `0` est le sentinelle « demande non partie » côté client (`SendCraftRequest`) : une demande
            // qui le porte ne pourrait pas être appariée à sa réponse, donc elle n'est pas un message valide.
            if (RequestId <= 0)
            {
                throw new ProtocolException($"demande de craft avec un id de requête invalide ({RequestId} <= 0)");
            }

            writer.WriteByte((byte)MessageType.CraftRequest);
            writer.WriteInt(RequestId);
            writer.WriteUInt16(TechType);
        }

        public static CraftRequestMessage Read(PacketReader reader)
        {
            var requestId = reader.ReadInt();
            var techType = reader.ReadUInt16();

            if (techType == 0)
            {
                throw new ProtocolException("demande de craft reçue avec TechType 0");
            }

            if (requestId <= 0)
            {
                throw new ProtocolException($"demande de craft reçue avec un id de requête invalide ({requestId} <= 0)");
            }

            return new CraftRequestMessage
            {
                RequestId = requestId,
                TechType = techType,
            };
        }
    }

    /// <summary>
    /// Décision du serveur sur un craft (v21), au seul demandeur.
    ///
    /// `MissingTechType`/`MissingAmount` nomment le matériau qui bloque : un refus sans nom obligerait le
    /// client à deviner, et un craft refusé pour deux unités de cuivre serait indiscernable d'un craft
    /// refusé pour une recette inconnue.
    /// </summary>
    public sealed class CraftResultMessage
    {
        public int RequestId;
        public ushort TechType;
        public bool Granted;
        public CraftOutcome Outcome;
        public ushort MissingTechType;
        public ushort MissingAmount;
        public int Revision;

        public void Write(PacketWriter writer)
        {
            Validate(TechType, Granted, Outcome, MissingTechType, MissingAmount, Revision, "craft");

            writer.WriteByte((byte)MessageType.CraftResult);
            writer.WriteInt(RequestId);
            writer.WriteUInt16(TechType);
            writer.WriteBool(Granted);
            writer.WriteByte((byte)Outcome);
            writer.WriteUInt16(MissingTechType);
            writer.WriteUInt16(MissingAmount);
            writer.WriteInt(Revision);
        }

        public static CraftResultMessage Read(PacketReader reader)
        {
            var requestId = reader.ReadInt();
            var techType = reader.ReadUInt16();
            var granted = reader.ReadBool();
            var outcome = (CraftOutcome)reader.ReadByte();
            var missingTechType = reader.ReadUInt16();
            var missingAmount = reader.ReadUInt16();
            var revision = reader.ReadInt();

            Validate(techType, granted, outcome, missingTechType, missingAmount, revision, "craft reçu");

            return new CraftResultMessage
            {
                RequestId = requestId,
                TechType = techType,
                Granted = granted,
                Outcome = outcome,
                MissingTechType = missingTechType,
                MissingAmount = missingAmount,
                Revision = revision,
            };
        }

        private static void Validate(
            ushort techType,
            bool granted,
            CraftOutcome outcome,
            ushort missingTechType,
            ushort missingAmount,
            int revision,
            string label)
        {
            if (techType == 0)
            {
                throw new ProtocolException($"{label} : TechType 0");
            }

            if (!Enum.IsDefined(typeof(CraftOutcome), outcome))
            {
                throw new ProtocolException($"{label} : issue inconnue ({(byte)outcome})");
            }

            if (granted != (outcome == CraftOutcome.Applied))
            {
                throw new ProtocolException(
                    $"{label} : accord ({granted}) incohérent avec l'issue ({outcome})");
            }

            if (revision < 0)
            {
                throw new ProtocolException($"{label} : révision négative ({revision})");
            }

            if (outcome == CraftOutcome.MissingMaterial || outcome == CraftOutcome.NotEnoughMaterial)
            {
                if (missingTechType == 0 || missingAmount == 0)
                {
                    throw new ProtocolException(
                        $"{label} : manque de matériau sans le nommer ({missingTechType} ×{missingAmount})");
                }
            }
            else if (missingTechType != 0 || missingAmount != 0)
            {
                throw new ProtocolException(
                    $"{label} : matériau manquant annoncé hors d'un refus de matériau ({outcome})");
            }
        }
    }

    public static class MessageCodec
    {
        public static byte[] Encode(Action<PacketWriter> write, PacketWriter reusable = null)
        {
            if (write == null)
            {
                throw new ArgumentNullException(nameof(write));
            }

            var writer = reusable ?? new PacketWriter();
            writer.Reset();
            write(writer);
            var result = new byte[writer.Length];
            Buffer.BlockCopy(writer.BackingBuffer, 0, result, 0, writer.Length);
            return result;
        }
    }
}
