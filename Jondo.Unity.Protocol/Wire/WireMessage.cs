using System;
using System.Collections.Generic;

namespace Jondo.Unity.Protocol.Wire
{
    /// <summary>One field as it travels on the wire, before anybody says what it means.</summary>
    public sealed class WireField
    {
        /// <summary>The field number from the tag.</summary>
        public int Number { get; init; }

        /// <summary>0 varint, 1 fixed64, 2 length-delimited, 5 fixed32. Nothing else exists here.</summary>
        public int Type { get; init; }

        /// <summary>The value when <see cref="Type"/> is 0, 1 or 5. Raw, unzigzagged.</summary>
        public ulong Value { get; init; }

        /// <summary>The bytes when <see cref="Type"/> is 2.</summary>
        public byte[] Bytes { get; init; } = Array.Empty<byte>();

        /// <summary>How many bytes this field took, tag included. Used to point at it in a hex dump.</summary>
        public int Offset { get; init; }

        public int Length { get; init; }
    }

    /// <summary>
    /// A protobuf message pulled apart without a schema: field numbers, wire types and raw values.
    /// </summary>
    /// <remarks>
    /// There is already a reader like this in the server (<c>Network.ProtoMessage</c>), and this is
    /// not meant to replace it. That one exists to serve the game: it stops at the first field it
    /// cannot make sense of and hands the handlers what it managed to read, because a handler that
    /// gets half a message walks away and a handler that gets an exception drops somebody's
    /// connection.
    ///
    /// This one exists to be looked at, and the difference is <see cref="Complete"/>. For
    /// inspection, "I read three fields and then hit something I could not read" is the single most
    /// interesting thing a parse can tell you — it is how a block of opaque bytes is told apart
    /// from a real nested message — and the server's reader has nowhere to put it.
    ///
    /// It also lives here, in <c>Jondo.Unity.Protocol</c>, and not in the server, because the
    /// editor has to compute exactly the same shapes as the server does. If the two had their own
    /// copies of this, they would agree until the day one of them was fixed, and then the editor
    /// would quietly stop matching the rows the server had written.
    ///
    /// It never throws.
    /// </remarks>
    public sealed class WireMessage
    {
        private static readonly WireField[] None = Array.Empty<WireField>();

        private WireMessage(IReadOnlyList<WireField> fields, bool complete, int bytesRead, int total)
        {
            Fields = fields;
            Complete = complete;
            BytesRead = bytesRead;
            TotalBytes = total;
        }

        public IReadOnlyList<WireField> Fields { get; }

        /// <summary>True when every byte was accounted for as a well-formed field.</summary>
        public bool Complete { get; }

        /// <summary>How far the reader got before it gave up. Equals <see cref="TotalBytes"/> when complete.</summary>
        public int BytesRead { get; }

        public int TotalBytes { get; }

        /// <summary>Bytes left over after the last field the reader could make sense of.</summary>
        public int TrailingBytes => TotalBytes - BytesRead;

        public static readonly WireMessage Empty = new WireMessage(None, true, 0, 0);

        /// <summary>Reads a message. Returns what it managed to read and says whether that was all of it.</summary>
        public static WireMessage Read(byte[]? data)
        {
            if (data == null || data.Length == 0) return Empty;

            var fields = new List<WireField>();
            int pos = 0;
            bool complete = true;

            while (pos < data.Length)
            {
                int start = pos;

                if (!ReadVarInt(data, ref pos, out ulong tag)) { complete = false; pos = start; break; }

                int type = (int)(tag & 7);
                int number = (int)(tag >> 3);

                // Field 0 does not exist in protobuf, and a tag of zero is what a run of padding
                // bytes looks like. Without this a block of zeroes reads as an endless list of
                // valid empty fields.
                if (number <= 0) { complete = false; pos = start; break; }

                switch (type)
                {
                    case 0:
                        if (!ReadVarInt(data, ref pos, out ulong varint)) { complete = false; pos = start; goto done; }
                        fields.Add(new WireField
                        {
                            Number = number, Type = 0, Value = varint,
                            Offset = start, Length = pos - start,
                        });
                        break;

                    case 1:
                        if (pos + 8 > data.Length) { complete = false; pos = start; goto done; }
                        fields.Add(new WireField
                        {
                            Number = number, Type = 1, Value = BitConverter.ToUInt64(data, pos),
                            Offset = start, Length = pos + 8 - start,
                        });
                        pos += 8;
                        break;

                    case 2:
                        if (!ReadVarInt(data, ref pos, out ulong length)) { complete = false; pos = start; goto done; }
                        if (length > (ulong)(data.Length - pos)) { complete = false; pos = start; goto done; }
                        var bytes = new byte[(int)length];
                        Array.Copy(data, pos, bytes, 0, (int)length);
                        pos += (int)length;
                        fields.Add(new WireField
                        {
                            Number = number, Type = 2, Bytes = bytes,
                            Offset = start, Length = pos - start,
                        });
                        break;

                    case 5:
                        if (pos + 4 > data.Length) { complete = false; pos = start; goto done; }
                        fields.Add(new WireField
                        {
                            Number = number, Type = 5, Value = BitConverter.ToUInt32(data, pos),
                            Offset = start, Length = pos + 4 - start,
                        });
                        pos += 4;
                        break;

                    default:
                        // 3 and 4 are the groups, which proto3 does not emit; 6 and 7 have never
                        // existed. Meeting one means these bytes are not a message.
                        complete = false;
                        pos = start;
                        goto done;
                }
            }

        done:
            return new WireMessage(fields.Count == 0 ? None : fields, complete, pos, data.Length);
        }

        /// <summary>
        /// Whether a length-delimited field's bytes are really a nested message, or data that only
        /// looks like one.
        /// </summary>
        /// <remarks>
        /// Two checks, and the second one was learned the hard way.
        ///
        /// The first is that it reads whole: a parse that stops halfway means these bytes are not a
        /// message, they are something whose first byte happened to look like a tag.
        ///
        /// The second is the ceiling on the field number, and without it this was worth nothing.
        /// The walking packet carries the path as a block of bytes, and that block parsed as a
        /// structure with fields 1024, 1025, 1566 and 1600, different on every step the player
        /// took: 307 distinct "shapes" of one message out of 1,798 captured.
        ///
        /// The ceiling is measured, not guessed. Across the whole 3.6.10.10 protocol as extracted
        /// from the client there are 6,186 declared message fields and <em>the highest number is
        /// 40</em>; the median is 2 and the 99th percentile is 14. 64 fits every one of them with
        /// room to spare.
        /// </remarks>
        public static bool LooksLikeMessage(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;

            var read = Read(bytes);
            if (!read.Complete || read.Fields.Count == 0) return false;

            foreach (var field in read.Fields)
            {
                if (field.Number > HighestFieldNumber) return false;
            }

            return true;
        }

        /// <summary>
        /// The highest field number accepted as part of a real structure. Measured: the highest in
        /// the whole 3.6.10.10 protocol is 40, out of 6,186 declared message fields.
        /// </summary>
        public const int HighestFieldNumber = 64;

        private static bool ReadVarInt(byte[] data, ref int pos, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift > 63) return false;
            }

            // Ran off the end mid-varint: the message is truncated.
            return false;
        }
    }
}
