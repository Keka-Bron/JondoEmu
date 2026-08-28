using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Rewrites the identity inside the captured messages.
    ///
    /// The world entry is replayed from a real capture, so every message in it talks about the
    /// account that was recorded. Replacing only the character-selection reply is not enough: the
    /// client then holds two contradictory identities and refuses to leave the character screen.
    /// It has to be changed everywhere it appears.
    ///
    /// A byte-for-byte search and replace does not work. The captured character id takes six
    /// bytes as a varint and ours takes four, so swapping them shifts every length that encloses
    /// it. The message has to be taken apart, changed and put back together, recomputing the
    /// lengths on the way up.
    ///
    /// Two rules keep this safe:
    ///   - Field order is preserved. These messages repeat the same field number, so order
    ///     carries meaning.
    ///   - It only descends into a field when the value being looked for is somewhere inside it.
    ///     That way a block of binary data that is not a submessage is never mistaken for one and
    ///     mangled.
    /// </summary>
    public static class CaptureRewriter
    {
        /// <summary>What to swap: whole varints, and whole strings.</summary>
        public sealed class Identity
        {
            public Dictionary<long, long> Numbers { get; } = new Dictionary<long, long>();
            public Dictionary<string, string> Texts { get; } = new Dictionary<string, string>();

            /// <summary>Byte patterns worth looking for before descending into a field.</summary>
            internal List<byte[]> Needles { get; } = new List<byte[]>();

            public Identity Number(long from, long to)
            {
                if (from == to) return this;
                Numbers[from] = to;
                Needles.Add(VarInt(from));
                return this;
            }

            public Identity Text(string from, string to)
            {
                if (string.IsNullOrEmpty(from) || from == to) return this;
                Texts[from] = to;
                Needles.Add(Encoding.UTF8.GetBytes(from));
                return this;
            }

            public bool IsEmpty => Numbers.Count == 0 && Texts.Count == 0;
        }

        public static byte[] Rewrite(byte[] frame, Identity identity)
        {
            if (frame == null || frame.Length == 0 || identity == null || identity.IsEmpty) return frame;
            if (!ContainsAnyNeedle(frame, identity)) return frame;

            byte[]? rewritten = RewriteMessage(frame, identity);
            return rewritten ?? frame;
        }

        /// <summary>
        /// Rewrites one message. Returns null when the bytes do not parse cleanly as protobuf, so
        /// the caller can leave them exactly as they were.
        /// </summary>
        private static byte[]? RewriteMessage(byte[] data, Identity identity)
        {
            using var output = new MemoryStream();
            int p = 0;

            while (p < data.Length)
            {
                if (!TryReadVarInt(data, ref p, out ulong tag)) return null;

                int fieldNumber = (int)(tag >> 3);
                int wireType = (int)(tag & 7);
                if (fieldNumber == 0) return null;

                switch (wireType)
                {
                    case 0:
                    {
                        if (!TryReadVarInt(data, ref p, out ulong value)) return null;
                        long asSigned = unchecked((long)value);
                        if (identity.Numbers.TryGetValue(asSigned, out long replacement))
                        {
                            value = unchecked((ulong)replacement);
                        }
                        WriteVarInt(output, tag);
                        WriteVarInt(output, value);
                        break;
                    }

                    case 2:
                    {
                        if (!TryReadVarInt(data, ref p, out ulong length)) return null;
                        if (p + (int)length > data.Length) return null;

                        var content = new byte[(int)length];
                        Array.Copy(data, p, content, 0, content.Length);
                        p += content.Length;

                        byte[] result = RewriteBytes(content, identity);
                        WriteVarInt(output, tag);
                        WriteVarInt(output, (ulong)result.Length);
                        output.Write(result, 0, result.Length);
                        break;
                    }

                    case 5:
                        if (p + 4 > data.Length) return null;
                        WriteVarInt(output, tag);
                        output.Write(data, p, 4);
                        p += 4;
                        break;

                    case 1:
                        if (p + 8 > data.Length) return null;
                        WriteVarInt(output, tag);
                        output.Write(data, p, 8);
                        p += 8;
                        break;

                    default:
                        return null;
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// Decides what a length-delimited field holds. If what we are looking for is not inside,
        /// it is returned untouched, which is what keeps binary blobs safe.
        /// </summary>
        private static byte[] RewriteBytes(byte[] content, Identity identity)
        {
            if (content.Length == 0 || !ContainsAnyNeedle(content, identity)) return content;

            // A whole string that has to change.
            try
            {
                string text = Encoding.UTF8.GetString(content);
                if (identity.Texts.TryGetValue(text, out string? replacement))
                {
                    return Encoding.UTF8.GetBytes(replacement);
                }
            }
            catch
            {
                // Not text. Carry on and try it as a submessage.
            }

            byte[]? asMessage = RewriteMessage(content, identity);
            return asMessage ?? content;
        }

        private static bool ContainsAnyNeedle(byte[] data, Identity identity)
        {
            foreach (byte[] needle in identity.Needles)
            {
                if (Contains(data, needle)) return true;
            }
            return false;
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || needle.Length > haystack.Length) return false;

            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }

        private static bool TryReadVarInt(byte[] data, ref int p, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (p < data.Length)
            {
                byte b = data[p++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift > 63) return false;
            }
            return false;
        }

        /// <summary>One writer for the whole project. See <see cref="NetworkEnvelope.WriteVarInt"/>.</summary>
        private static void WriteVarInt(Stream stream, ulong value)
            => NetworkEnvelope.WriteVarInt(stream, value);

        public static byte[] VarInt(long value)
        {
            using var ms = new MemoryStream();
            WriteVarInt(ms, unchecked((ulong)value));
            return ms.ToArray();
        }
    }
}
