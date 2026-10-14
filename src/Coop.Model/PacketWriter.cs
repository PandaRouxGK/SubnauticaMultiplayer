using System;
using System.Text;

namespace Coop.Model
{
    /// <summary>
    /// Écriture binaire little-endian dans un tampon réutilisable.
    /// Volontairement indépendant de LiteNetLib : le protocole doit rester testable sans socket,
    /// et remplaçable si on change un jour de transport.
    /// </summary>
    public sealed class PacketWriter
    {
        private byte[] _buffer;
        private int _length;

        public PacketWriter(int capacity = 256)
        {
            _buffer = new byte[capacity < 16 ? 16 : capacity];
        }

        public int Length => _length;

        /// <summary>
        /// Tampon brut. Volontairement PAS nommé « Buffer » : ce nom masquerait
        /// <see cref="System.Buffer"/> à l'intérieur de la classe.
        /// </summary>
        public byte[] BackingBuffer => _buffer;

        public void Reset() => _length = 0;

        private void Ensure(int extra)
        {
            if (_length + extra <= _buffer.Length)
            {
                return;
            }

            var size = _buffer.Length * 2;
            while (size < _length + extra)
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, size);
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_length++] = value;
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUInt16(ushort value)
        {
            Ensure(2);
            _buffer[_length++] = (byte)value;
            _buffer[_length++] = (byte)(value >> 8);
        }

        public void WriteInt(int value)
        {
            Ensure(4);
            _buffer[_length++] = (byte)value;
            _buffer[_length++] = (byte)(value >> 8);
            _buffer[_length++] = (byte)(value >> 16);
            _buffer[_length++] = (byte)(value >> 24);
        }

        public void WriteLong(long value)
        {
            Ensure(8);
            for (var i = 0; i < 8; i++)
            {
                _buffer[_length++] = (byte)(value >> (8 * i));
            }
        }

        public void WriteFloat(float value)
        {
            // netstandard2.0 n'a pas BitConverter.SingleToInt32Bits : on passe par les octets.
            var bytes = BitConverter.GetBytes(value);
            Ensure(4);
            Buffer.BlockCopy(bytes, 0, _buffer, _length, 4);
            _length += 4;
        }

        /// <summary>Chaîne UTF-8 préfixée par sa longueur (ushort).</summary>
        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ProtocolException($"chaîne trop longue ({bytes.Length} octets)");
            }

            WriteUInt16((ushort)bytes.Length);
            Ensure(bytes.Length);
            Buffer.BlockCopy(bytes, 0, _buffer, _length, bytes.Length);
            _length += bytes.Length;
        }
    }
}
