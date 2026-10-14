using System;
using System.Text;

namespace Coop.Model
{
    /// <summary>
    /// Lecture binaire little-endian, symétrique de <see cref="PacketWriter"/>.
    /// Chaque lecture est bornée : un paquet tronqué lève <see cref="ProtocolException"/>
    /// au lieu de renvoyer des valeurs inventées.
    /// </summary>
    public sealed class PacketReader
    {
        private readonly byte[] _data;
        private readonly int _start;
        private readonly int _end;
        private int _position;

        public PacketReader(byte[] data) : this(data, 0, data?.Length ?? 0)
        {
        }

        public PacketReader(byte[] data, int offset, int length)
        {
            if (data == null)
            {
                throw new ProtocolException("tampon nul");
            }

            if (offset < 0 || length < 0 || offset + length > data.Length)
            {
                throw new ProtocolException($"plage invalide (offset={offset}, length={length}, taille={data.Length})");
            }

            _data = data;
            _start = offset;
            _end = offset + length;
            _position = offset;
        }

        public int Remaining => _end - _position;

        private void Require(int count)
        {
            if (_position + count > _end)
            {
                throw new ProtocolException($"paquet tronqué : {count} octet(s) demandé(s), {Remaining} restant(s)");
            }
        }

        public byte ReadByte()
        {
            Require(1);
            return _data[_position++];
        }

        public bool ReadBool() => ReadByte() != 0;

        public ushort ReadUInt16()
        {
            Require(2);
            var value = (ushort)(_data[_position] | (_data[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public int ReadInt()
        {
            Require(4);
            var value = _data[_position]
                        | (_data[_position + 1] << 8)
                        | (_data[_position + 2] << 16)
                        | (_data[_position + 3] << 24);
            _position += 4;
            return value;
        }

        public long ReadLong()
        {
            Require(8);
            long value = 0;
            for (var i = 0; i < 8; i++)
            {
                value |= (long)_data[_position + i] << (8 * i);
            }

            _position += 8;
            return value;
        }

        public float ReadFloat()
        {
            Require(4);
            var bytes = new byte[4];
            Buffer.BlockCopy(_data, _position, bytes, 0, 4);
            _position += 4;
            return BitConverter.ToSingle(bytes, 0);
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            Require(length);
            var value = Encoding.UTF8.GetString(_data, _position, length);
            _position += length;
            return value;
        }

        /// <summary>Le reste des octets, sans copie inutile si tout a été consommé.</summary>
        public byte[] ReadRemainingBytes()
        {
            var result = new byte[Remaining];
            if (result.Length > 0)
            {
                Buffer.BlockCopy(_data, _position, result, 0, result.Length);
                _position += result.Length;
            }

            return result;
        }

        /// <summary>Repart du début : utile pour relire un paquet déjà bufferisé.</summary>
        public void Rewind() => _position = _start;
    }
}
