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

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)MessageType.HandshakeResponse);
            writer.WriteBool(Accepted);
            writer.WriteString(Reason);
            writer.WriteLong(ServerTick);
        }

        public static HandshakeResponse Read(PacketReader reader) => new HandshakeResponse
        {
            Accepted = reader.ReadBool(),
            Reason = reader.ReadString(),
            ServerTick = reader.ReadLong(),
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

    /// <summary>Regroupe l'écriture d'un message dans un tampon réutilisable.</summary>
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
