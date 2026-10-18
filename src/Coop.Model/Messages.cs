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

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.HandshakeRequest);
            writer.WriteInt(ProtocolVersion);
            writer.WriteString(GameVersion);
            writer.WriteString(ModListHash);
            writer.WriteString(Username);
        }

        public static HandshakeRequest Read(PacketReader reader) => new HandshakeRequest
        {
            ProtocolVersion = reader.ReadInt(),
            GameVersion = reader.ReadString(),
            ModListHash = reader.ReadString(),
            Username = reader.ReadString(),
        };
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
            writer.WriteString(Reason);
            writer.WriteLong(ServerTick);
            writer.WriteUInt16(AssignedPlayerId);
        }

        public static HandshakeResponse Read(PacketReader reader) => new HandshakeResponse
        {
            Accepted = reader.ReadBool(),
            Reason = reader.ReadString(),
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
            writer.WriteString(Reason);
        }

        public static DisconnectMessage Read(PacketReader reader) => new DisconnectMessage
        {
            Reason = reader.ReadString(),
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
            writer.WriteString(Reason);
        }

        public static PlayerLeftMessage Read(PacketReader reader) => new PlayerLeftMessage
        {
            PlayerId = reader.ReadUInt16(),
            Username = reader.ReadString(),
            Reason = reader.ReadString(),
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
            if (list.Length > byte.MaxValue)
            {
                throw new ProtocolException($"trop de joueurs dans le snapshot ({list.Length})");
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

        public void Write(PacketWriter writer)
        {
            writer.WriteLong(Key);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
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
            };
            PlayerPositionMessage.EnsureFinite(
                entry.X, entry.Y, entry.Z,
                entry.RotX, entry.RotY, entry.RotZ, entry.RotW);
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

        public void Write(PacketWriter writer)
        {
            writer.WriteLong(Key);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
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
            };
            PlayerPositionMessage.EnsureFinite(
                entry.X, entry.Y, entry.Z,
                entry.RotX, entry.RotY, entry.RotZ, entry.RotW);
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
            writer.WriteByte((byte)MessageType.PickupResult);
            writer.WriteLong(ItemKey);
            writer.WriteString(TechTypeName ?? string.Empty);
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

        public void Write(PacketWriter writer)
        {
            var id = UniqueId ?? string.Empty;
            if (id.Length > Protocol.MaxEntityIdLength)
            {
                throw new ProtocolException($"identifiant d'entité trop long ({id.Length} > {Protocol.MaxEntityIdLength})");
            }

            var name = TechTypeName ?? string.Empty;
            if (name.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType trop long ({name.Length} > {Protocol.MaxTechTypeNameLength})");
            }

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
            };

            if (msg.UniqueId.Length > Protocol.MaxEntityIdLength)
            {
                throw new ProtocolException($"identifiant d'entité reçu trop long ({msg.UniqueId.Length})");
            }

            if (msg.TechTypeName.Length > Protocol.MaxTechTypeNameLength)
            {
                throw new ProtocolException($"nom de TechType reçu trop long ({msg.TechTypeName.Length})");
            }

            PlayerPositionMessage.EnsureFinite(
                msg.X, msg.Y, msg.Z,
                msg.RotX, msg.RotY, msg.RotZ, msg.RotW);

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

        /// <summary><see cref="EntityKind.Vehicle"/> ou <see cref="EntityKind.Item"/>.</summary>
        public byte Kind;

        /// <summary>Motif lisible, pour le journal du receveur · jamais interprété.</summary>
        public string Reason;

        public void Write(PacketWriter writer)
        {
            if (Kind != EntityKind.Vehicle && Kind != EntityKind.Item)
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

            if (msg.Kind != EntityKind.Vehicle && msg.Kind != EntityKind.Item)
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

    public static class MessageCodec
    {
        public static byte[] Encode(Action<PacketWriter> write, PacketWriter reusable = null)
        {
            var writer = reusable ?? new PacketWriter();
            writer.Reset();
            write(writer);
            var result = new byte[writer.Length];
            Buffer.BlockCopy(writer.BackingBuffer, 0, result, 0, writer.Length);
            return result;
        }
    }
}
