using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Minimal protobuf writer that keeps fields in the order they were added.
    ///
    /// We need our own because <see cref="ProtoMessage"/> sorts fields by number when it
    /// serializes, and in the messages of this phase the same field number shows up more
    /// than once (one f1 per server, one f3 per character). There the order carries
    /// information, it is not just cosmetic.
    /// </summary>
    public sealed class Pb
    {
        private readonly MemoryStream _ms = new MemoryStream();

        public static Pb New() => new Pb();

        public int Length => (int)_ms.Length;

        /// <summary>Variable-length integer (wire type 0).</summary>
        public Pb Var(int field, long value)
        {
            WriteTag(field, 0);
            WriteVarInt((ulong)value);
            return this;
        }

        /// <summary>Variable-length integer, but only when the value is not zero (proto3 omits zeros).</summary>
        public Pb VarIfNotZero(int field, long value) => value == 0 ? this : Var(field, value);

        public Pb Str(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value ?? ""));

        public Pb Bytes(int field, byte[] value)
        {
            value ??= Array.Empty<byte>();
            WriteTag(field, 2);
            WriteVarInt((ulong)value.Length);
            _ms.Write(value, 0, value.Length);
            return this;
        }

        public Pb Msg(int field, Pb inner) => Bytes(field, inner.Build());

        /// <summary>
        /// Four bytes, exactly as given (wire type 5).
        /// </summary>
        /// <remarks>
        /// For rebuilding a captured message that carries one, which is rare — there is a single
        /// fixed32 in the whole world-entry sequence. The bytes go through untouched rather than
        /// being read as a number and written back: whatever it is, floats included, it comes out
        /// the way it went in.
        /// </remarks>
        public Pb Fixed32(int field, byte[] value)
        {
            if (value == null || value.Length != 4)
                throw new ArgumentException("A fixed32 is four bytes.", nameof(value));

            WriteTag(field, 5);
            _ms.Write(value, 0, 4);
            return this;
        }

        /// <summary>Eight bytes, exactly as given (wire type 1). See <see cref="Fixed32"/>.</summary>
        public Pb Fixed64(int field, byte[] value)
        {
            if (value == null || value.Length != 8)
                throw new ArgumentException("A fixed64 is eight bytes.", nameof(value));

            WriteTag(field, 1);
            _ms.Write(value, 0, 8);
            return this;
        }

        /// <summary>
        /// Empty submessage. This is not the same as leaving the field out: in the character
        /// list, for instance, the sex block is present but empty when the sex is 0.
        /// </summary>
        public Pb EmptyMsg(int field) => Bytes(field, Array.Empty<byte>());

        /// <summary>Packed repeated field (colors, skins, scales...).</summary>
        public Pb Packed(int field, IEnumerable<long> values)
        {
            using var inner = new MemoryStream();
            foreach (long v in values)
            {
                WriteVarInt(inner, (ulong)v);
            }
            return Bytes(field, inner.ToArray());
        }

        public byte[] Build() => _ms.ToArray();

        private void WriteTag(int field, int wireType) => WriteVarInt((ulong)((field << 3) | wireType));

        private void WriteVarInt(ulong value) => WriteVarInt(_ms, value);

        private static void WriteVarInt(Stream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }
    }
}
