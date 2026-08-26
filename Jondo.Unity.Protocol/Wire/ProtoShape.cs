using System.Collections.Generic;
using System.Text;

namespace Jondo.Unity.Protocol.Wire
{
    /// <summary>
    /// The shape of a message: field numbers and wire types, submessages included, and nothing of
    /// what the values actually were.
    /// </summary>
    /// <remarks>
    /// <code>
    ///   v    a number (varint)
    ///   f    a fixed-width number
    ///   s    a string, or bytes that are not a submessage
    ///   {…}  a submessage, with its own shape inside
    /// </code>
    ///
    /// This is the key everything about unknown packets hangs off, and the reason is worth writing
    /// down: <b>Ankama renames every message to three random letters on some patches.</b> A
    /// registry filed under <c>jxw</c> evaporates the day of the patch and takes every note anybody
    /// wrote with it. Filed under the shape, it survives — and it does better than survive, because
    /// a message we can already recognise by its shape is a free anchor for
    /// <c>protocolbuilder</c>'s structural matcher, which is the thing that produced the 2,169
    /// identity mappings across the last patch.
    ///
    /// Grouping by shape rather than by opcode also splits what needs splitting: one opcode carries
    /// different payloads depending on what the player is doing, and counting those together hides
    /// exactly what you opened the list to see.
    ///
    /// It lives here, and not in the server where it started, because the editor has to compute the
    /// same string the server wrote into <c>paquetes.db</c>. Two copies of this algorithm would
    /// agree right up until one of them was improved.
    /// </remarks>
    public static class ProtoShape
    {
        /// <summary>Nothing in the body. Common and not a problem: plenty of messages are just a name.</summary>
        public const string Empty = "(empty)";

        /// <summary>The bytes are not a protobuf message at all.</summary>
        public const string Unreadable = "(unreadable)";

        /// <summary>How far down submessages are followed before the shape stops describing.</summary>
        private const int MaxDepth = 6;

        /// <summary>The shape of these bytes, as a string that can be used as a key.</summary>
        public static string Of(byte[]? payload) => Of(payload, 0);

        private static string Of(byte[]? payload, int depth)
        {
            if (payload == null || payload.Length == 0) return Empty;

            // Past this the shape stops telling anything apart, and a malformed message could
            // otherwise have no bottom at all.
            if (depth >= MaxDepth) return "…";

            var message = WireMessage.Read(payload);
            if (message.Fields.Count == 0) return depth == 0 ? Unreadable : Empty;

            var parts = new List<string>(message.Fields.Count);
            foreach (var field in message.Fields)
            {
                switch (field.Type)
                {
                    case 0:
                        parts.Add($"{field.Number}:v");
                        break;

                    case 1:
                    case 5:
                        parts.Add($"{field.Number}:f");
                        break;

                    case 2:
                        parts.Add(WireMessage.LooksLikeMessage(field.Bytes)
                            ? $"{field.Number}:{{{Of(field.Bytes, depth + 1)}}}"
                            : $"{field.Number}:s");
                        break;

                    default:
                        parts.Add($"{field.Number}:?");
                        break;
                }
            }

            // A message the reader could not finish is worth saying so, because that is the
            // difference between "I understand this and it has three fields" and "I got three
            // fields in before it stopped making sense".
            string shape = string.Join(",", parts);
            return message.Complete ? shape : shape + ",+" + message.TrailingBytes + "b";
        }

        /// <summary>
        /// A one-line description of a message's contents, values included. For the frame view, not
        /// for keys.
        /// </summary>
        public static string Summarise(byte[]? payload, int limit = 160)
        {
            var message = WireMessage.Read(payload);
            if (message.Fields.Count == 0) return "";

            var text = new StringBuilder();
            foreach (var field in message.Fields)
            {
                if (text.Length > 0) text.Append("  ");
                if (text.Length >= limit) { text.Append('…'); break; }

                text.Append(field.Number).Append('=');
                switch (field.Type)
                {
                    case 0:
                    case 1:
                    case 5:
                        text.Append(field.Value);
                        break;

                    case 2:
                        if (WireMessage.LooksLikeMessage(field.Bytes)) text.Append("{…}");
                        else if (IsPrintable(field.Bytes)) text.Append('"')
                            .Append(Encoding.UTF8.GetString(field.Bytes)).Append('"');
                        else text.Append('<').Append(field.Bytes.Length).Append("b>");
                        break;
                }
            }

            return text.ToString();
        }

        /// <summary>Whether bytes can be shown as text rather than as a length.</summary>
        public static bool IsPrintable(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;

            foreach (byte b in bytes)
            {
                // Anything above 0x7F is left to the length view rather than guessed at: a UTF-8
                // name and a block of binary look the same from here, and a mangled name in a list
                // is worse than an honest byte count.
                if (b < 0x20 || b > 0x7E) return false;
            }

            return true;
        }
    }
}
