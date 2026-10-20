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

        /// <summary>
        /// Tampon de conversion des floats, **par thread** : allouer un tableau de 4 octets à chaque
        /// `ReadFloat` produisait des milliers d'allocations par seconde sur un snapshot de joueurs.
        /// Un `PacketReader` n'est pas réentrant, et deux lecteurs de deux threads ne partagent pas
        /// ce champ grâce à `[ThreadStatic]`.
        /// </summary>
        [ThreadStatic] private static byte[] _floatScratch;

        /// <summary>
        /// Décodage **strict** : une séquence UTF-8 invalide est un paquet malformé, pas une chaîne
        /// à caractères de remplacement. Sans cela, un octet hostile devenait silencieusement U+FFFD
        /// dans un pseudo, un nom de `TechType` ou une empreinte de mods.
        /// </summary>
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public PacketReader(byte[] data) : this(data, 0, data?.Length ?? 0)
        {
        }

        public PacketReader(byte[] data, int offset, int length)
        {
            if (data == null)
            {
                throw new ProtocolException("tampon nul");
            }

            // `offset + length` peut déborder un int (offset=int.MaxValue, length=1) et
            // redevenir négatif : une telle plage passerait le contrôle puis laisserait
            // `_end < _start`. On teste donc la soustraction, qui ne déborde pas ici.
            if (offset < 0 || length < 0 || offset > data.Length - length)
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
            var scratch = _floatScratch ?? (_floatScratch = new byte[4]);
            Buffer.BlockCopy(_data, _position, scratch, 0, 4);
            _position += 4;
            return BitConverter.ToSingle(scratch, 0);
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            Require(length);

            string value;
            try
            {
                value = StrictUtf8.GetString(_data, _position, length);
            }
            catch (DecoderFallbackException)
            {
                throw new ProtocolException($"chaîne UTF-8 invalide ({length} octet(s))");
            }

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
