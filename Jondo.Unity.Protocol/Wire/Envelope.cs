using System;

namespace Jondo.Unity.Protocol.Wire
{
    /// <summary>Which way a frame was going, as told by the field it sits in at the root.</summary>
    public enum FrameDirection
    {
        /// <summary>The envelope did not say. The traffic log's own marker is then the only source.</summary>
        Unknown = 0,

        /// <summary>Root field 1. The server telling the client something nobody asked for.</summary>
        ServerPush = 1,

        /// <summary>Root field 2. The client asking for something.</summary>
        ClientRequest = 2,

        /// <summary>Root field 3. The server answering one of those.</summary>
        ServerReply = 3,
    }

    /// <summary>What was inside a frame: which way it went, which message it was, and its bytes.</summary>
    public readonly struct EnvelopeFrame
    {
        /// <summary>False when no message could be found in these bytes at all.</summary>
        public bool Found { get; init; }

        /// <summary>The field number at the root, 0 when there was none to read.</summary>
        public int RootField { get; init; }

        public FrameDirection Direction { get; init; }

        /// <summary>The three letters, with <c>type.ankama.com/</c> already off the front.</summary>
        public string Opcode { get; init; }

        /// <summary>The message itself, with every envelope stripped.</summary>
        public byte[] Payload { get; init; }

        /// <summary>How many envelopes had to be opened to reach it. 2 for a whole frame.</summary>
        public int Depth { get; init; }

        /// <summary>True when a length prefix was taken off the front before reading.</summary>
        public bool HadLengthPrefix { get; init; }

        public static readonly EnvelopeFrame NotFound = new EnvelopeFrame
        {
            Found = false, Opcode = "", Payload = Array.Empty<byte>(),
        };
    }

    /// <summary>
    /// Opens a frame: works out which way it was going, which message it carries and where its
    /// bytes start.
    /// </summary>
    /// <remarks>
    /// This exists because the same job was being done in three places with three different answers,
    /// and one of them was wrong in a way nothing noticed for months.
    ///
    /// The unknown-packet registry called <c>NetworkEnvelope.ExtractGameNodePayload</c>, which only
    /// ever looks at root field 3, and <c>GetMessageTypeUrl</c>, which looks at 1 and 3. Client
    /// frames sit at root field <b>2</b>. So every single packet the registry was asked to write
    /// down went in with no opcode and no payload: after weeks of play the table held two rows,
    /// both of them <c>(sin opcode)</c> over an empty body. The dispatcher never noticed because it
    /// matches opcodes by searching the frame's bytes as text, which works whatever the envelope
    /// looks like.
    ///
    /// The layouts here are measured over the 72,879 frames in <c>logs/gameserver_traffic.log</c>:
    ///
    ///     root 1 → 1 → 1     56,073   the server saying something
    ///     root 2 → 1 → 1      8,974   the client asking
    ///     root 3 → 1 → 1        481   the server answering
    ///     root 1 (alone)      4,605   an Any logged on its own, without its outer envelope
    ///     root 1 → 1             41   the same, one layer down
    ///
    /// Which is why the search descends rather than hardcoding one path: the last two shapes are a
    /// third of a percent of the traffic and would be silently dropped by a reader that only knew
    /// the first three.
    /// </remarks>
    public static class Envelope
    {
        /// <summary>What Ankama puts in front of the three letters.</summary>
        public const string TypePrefix = "type.ankama.com/";

        /// <summary>How many envelopes deep the message is looked for. Measured deepest is 2.</summary>
        private const int MaxDepth = 3;

        /// <summary>
        /// Reads a frame as it comes off the socket, with no length prefix in front of it.
        /// </summary>
        public static EnvelopeFrame Read(byte[]? frame)
        {
            if (frame == null || frame.Length == 0) return EnvelopeFrame.NotFound;
            return Descend(frame, 0, 0, false);
        }

        /// <summary>
        /// Reads a frame that may still carry its length prefix, which is what the traffic log has.
        /// </summary>
        /// <remarks>
        /// The log is written from two places that do not agree: what comes off
        /// <c>ReadFrameAsync</c> has had its prefix taken off, and what goes through
        /// <c>WriteFrameAsync</c> is logged with the prefix still on. Measured over the whole log:
        /// 27,565 of 72,879 rows carry one.
        ///
        /// The prefix is only taken off when reading straight through fails <em>and</em> the varint
        /// on the front is exactly the number of bytes behind it. Without that second condition a
        /// perfectly good frame whose first tag byte happens to equal its own length would lose its
        /// first byte.
        /// </remarks>
        public static EnvelopeFrame ReadLogged(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return EnvelopeFrame.NotFound;

            var straight = Descend(bytes, 0, 0, false);
            if (straight.Found) return straight;

            if (!TryReadVarInt(bytes, out ulong declared, out int header)) return straight;
            if (declared != (ulong)(bytes.Length - header)) return straight;

            var body = new byte[bytes.Length - header];
            Array.Copy(bytes, header, body, 0, body.Length);

            var stripped = Descend(body, 0, 0, true);
            return stripped.Found ? stripped : straight;
        }

        private static EnvelopeFrame Descend(byte[] bytes, int depth, int rootField, bool hadPrefix)
        {
            if (depth > MaxDepth) return EnvelopeFrame.NotFound;

            var message = WireMessage.Read(bytes);
            if (message.Fields.Count == 0) return EnvelopeFrame.NotFound;

            // An Any is a submessage whose field 1 is the type url and whose field 2 is the body.
            // Looking for the url rather than for a fixed field number is what makes this survive
            // the layouts above.
            foreach (var field in message.Fields)
            {
                if (field.Type != 2) continue;

                string? url = TypeUrlOf(field.Bytes);
                if (url == null) continue;

                int top = depth == 0 ? field.Number : rootField;
                return new EnvelopeFrame
                {
                    Found = true,
                    RootField = top,
                    Direction = DirectionOf(top),
                    Opcode = url,
                    Payload = BodyOf(field.Bytes),
                    Depth = depth,
                    HadLengthPrefix = hadPrefix,
                };
            }

            foreach (var field in message.Fields)
            {
                if (field.Type != 2 || field.Bytes.Length == 0) continue;

                var deeper = Descend(field.Bytes, depth + 1, depth == 0 ? field.Number : rootField, hadPrefix);
                if (deeper.Found) return deeper;
            }

            return EnvelopeFrame.NotFound;
        }

        /// <summary>The three letters, if these bytes are an Any. Null when they are not.</summary>
        private static string? TypeUrlOf(byte[] any)
        {
            var read = WireMessage.Read(any);
            foreach (var field in read.Fields)
            {
                if (field.Number != 1 || field.Type != 2) continue;
                if (field.Bytes.Length <= TypePrefix.Length) return null;

                for (int i = 0; i < TypePrefix.Length; i++)
                {
                    if (field.Bytes[i] != (byte)TypePrefix[i]) return null;
                }

                return System.Text.Encoding.ASCII.GetString(
                    field.Bytes, TypePrefix.Length, field.Bytes.Length - TypePrefix.Length);
            }

            return null;
        }

        private static byte[] BodyOf(byte[] any)
        {
            var read = WireMessage.Read(any);
            foreach (var field in read.Fields)
            {
                if (field.Number == 2 && field.Type == 2) return field.Bytes;
            }

            // A message with no body at all is normal: plenty of them are just a name.
            return Array.Empty<byte>();
        }

        private static FrameDirection DirectionOf(int rootField) => rootField switch
        {
            1 => FrameDirection.ServerPush,
            2 => FrameDirection.ClientRequest,
            3 => FrameDirection.ServerReply,
            _ => FrameDirection.Unknown,
        };

        private static bool TryReadVarInt(byte[] data, out ulong value, out int length)
        {
            value = 0;
            length = 0;
            int shift = 0;
            while (length < data.Length)
            {
                byte b = data[length++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift > 28) return false;
            }

            return false;
        }
    }
}
