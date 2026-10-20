using System;
using System.Runtime.InteropServices;
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
        /// <summary>
        /// Encodage **strict** : un surrogate UTF-16 isolé n'a pas de représentation UTF-8.
        /// Le remplacer silencieusement par U+FFFD changerait la chaîne à l'insu de l'appelant
        /// (pseudo, identifiant, empreinte) ; on refuse le message à l'écriture, comme le
        /// décodeur strict refuse une séquence invalide à la lecture.
        /// </summary>
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

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

        /// <summary>
        /// Recouvrement explicite float ↔ bits : netstandard2.0 n'a pas `BitConverter.SingleToInt32Bits`,
        /// et `BitConverter.GetBytes` alloue un tableau de 4 octets **par float**. À 20 Hz, un snapshot
        /// de dix joueurs en écrit une centaine par seconde, plus les créatures et les véhicules :
        /// c'était des milliers d'allocations par seconde pour quatre octets. La conversion par union
        /// ne copie rien et préserve les motifs exacts (`-0`, NaN silencieux, dénormaux).
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public int Bits;
        }

        public void WriteFloat(float value)
        {
            var bits = new FloatBits { Value = value }.Bits;
            Ensure(4);
            // Écriture little-endian explicite : le protocole ne dépend pas de l'endianness de la machine.
            _buffer[_length++] = (byte)bits;
            _buffer[_length++] = (byte)(bits >> 8);
            _buffer[_length++] = (byte)(bits >> 16);
            _buffer[_length++] = (byte)(bits >> 24);
        }

        /// <summary>Chaîne UTF-8 préfixée par sa longueur (ushort).</summary>
        public void WriteString(string value)
        {
            byte[] bytes;
            try
            {
                bytes = StrictUtf8.GetBytes(value ?? string.Empty);
            }
            catch (EncoderFallbackException)
            {
                throw new ProtocolException("chaîne UTF-16 invalide (surrogate isolé)");
            }

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
