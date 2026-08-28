using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Protocol.Wire;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>One line of a decoded frame.</summary>
    public sealed class DecodedLine
    {
        public int Depth { get; init; }

        /// <summary>The field number, as it travels.</summary>
        public int Number { get; init; }

        /// <summary>The declared name, or empty when the protocol file did not have this one.</summary>
        public string Name { get; init; } = "";

        /// <summary>The declared type, or what the wire type suggests when there is no declaration.</summary>
        public string Type { get; init; } = "";

        public string Value { get; init; } = "";

        /// <summary>True when nothing declared this field: read off the wire and guessed at.</summary>
        public bool Guessed { get; init; }

        public override string ToString()
        {
            var text = new StringBuilder(new string(' ', Depth * 2));
            text.Append(Number);
            if (Name.Length > 0) text.Append(' ').Append(Name);
            text.Append("  ").Append(Type);
            if (Value.Length > 0) text.Append("  ").Append(Value);
            return text.ToString();
        }
    }

    /// <summary>
    /// Turns a payload into something readable, using the protocol the client declares when it is
    /// there and the wire types when it is not.
    /// </summary>
    /// <remarks>
    /// The declared types are what make this worth having rather than a hex dump with numbers. A
    /// length-delimited field on the wire could be a string, a nested message or a blob, and the
    /// three are genuinely not distinguishable: <c>kqz</c> field 3 is the two-byte language code
    /// <c>"es"</c>, and those two bytes parse perfectly well as a submessage with a field 12 in it.
    /// With <c>string fytl = 3</c> in front of you, there is nothing to guess.
    ///
    /// When the field is not declared — a message the extraction missed, or a client newer than the
    /// protocol file — the line is marked as guessed rather than quietly shown as if it were known.
    /// That distinction is the same one the provenance column makes everywhere else in this editor.
    /// </remarks>
    public static class FrameDecoder
    {
        /// <summary>How far down nested messages are followed. Deeper than any real message goes.</summary>
        private const int MaxDepth = 8;

        public static List<DecodedLine> Decode(byte[]? payload, string? messageName, ProtoSchema schema)
        {
            var lines = new List<DecodedLine>();
            Walk(payload, schema.Message(messageName), schema, 0, lines);
            return lines;
        }

        private static void Walk(byte[]? bytes, ProtoMessageDef? definition, ProtoSchema schema,
                                 int depth, List<DecodedLine> lines)
        {
            if (depth > MaxDepth) return;

            var message = WireMessage.Read(bytes);
            foreach (var field in message.Fields)
            {
                var declared = definition?.Field(field.Number);

                if (field.Type != 2)
                {
                    lines.Add(new DecodedLine
                    {
                        Depth = depth,
                        Number = field.Number,
                        Name = declared?.Name ?? "",
                        Type = declared?.Type ?? (field.Type == 0 ? "varint" : "fixed"),
                        Value = Number(field, declared?.Type),
                        Guessed = declared == null,
                    });
                    continue;
                }

                // Length-delimited, which is where the declared type earns its keep.
                if (declared != null && !declared.IsScalar)
                {
                    var nested = schema.Message(declared.Type);
                    lines.Add(new DecodedLine
                    {
                        Depth = depth,
                        Number = field.Number,
                        Name = declared.Name,
                        Type = (declared.Repeated ? "repeated " : "") + declared.Type,
                        Value = nested == null ? $"<{field.Bytes.Length} b, type not declared>" : "",
                    });

                    if (nested != null) Walk(field.Bytes, nested, schema, depth + 1, lines);
                    continue;
                }

                if (declared != null && declared.Repeated && IsPackable(declared.Type))
                {
                    lines.Add(new DecodedLine
                    {
                        Depth = depth,
                        Number = field.Number,
                        Name = declared.Name,
                        Type = "repeated " + declared.Type,
                        Value = Packed(field.Bytes, declared.Type),
                    });
                    continue;
                }

                if (declared != null && declared.IsMap)
                {
                    lines.Add(new DecodedLine
                    {
                        Depth = depth,
                        Number = field.Number,
                        Name = declared.Name,
                        Type = declared.Type,
                        Value = MapEntry(field.Bytes),
                    });
                    continue;
                }

                if (declared != null)
                {
                    lines.Add(new DecodedLine
                    {
                        Depth = depth,
                        Number = field.Number,
                        Name = declared.Name,
                        Type = declared.Type,
                        Value = declared.Type == "bytes"
                            ? Hex(field.Bytes, 32)
                            : Quote(field.Bytes),
                    });
                    continue;
                }

                // Nothing declared it. Fall back to the same guess the shape signature makes, and
                // say out loud that it is a guess.
                bool looksNested = WireMessage.LooksLikeMessage(field.Bytes);
                lines.Add(new DecodedLine
                {
                    Depth = depth,
                    Number = field.Number,
                    Name = "",
                    Type = looksNested ? "message?" : (ProtoShape.IsPrintable(field.Bytes) ? "string?" : "bytes"),
                    Value = looksNested ? "" :
                            ProtoShape.IsPrintable(field.Bytes) ? Quote(field.Bytes) : Hex(field.Bytes, 32),
                    Guessed = true,
                });

                if (looksNested) Walk(field.Bytes, null, schema, depth + 1, lines);
            }

            if (!message.Complete && message.TrailingBytes > 0)
            {
                lines.Add(new DecodedLine
                {
                    Depth = depth,
                    Number = 0,
                    Type = "unread",
                    Value = $"{message.TrailingBytes} byte(s) left over — these are not a message",
                    Guessed = true,
                });
            }
        }

        private static string Number(WireField field, string? type) => type switch
        {
            "int32" or "sfixed32" => ((int)(long)field.Value).ToString(),
            "int64" or "sfixed64" => ((long)field.Value).ToString(),
            "sint32" or "sint64" => ZigZag(field.Value).ToString(),
            "bool" => field.Value != 0 ? "true" : "false",
            "float" => BitConverter.Int32BitsToSingle((int)field.Value).ToString("R"),
            "double" => BitConverter.Int64BitsToDouble((long)field.Value).ToString("R"),
            _ => field.Value.ToString(),
        };

        private static long ZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

        private static bool IsPackable(string type) => type switch
        {
            "int32" or "int64" or "uint32" or "uint64" or "sint32" or "sint64" or "bool"
                or "fixed32" or "fixed64" or "sfixed32" or "sfixed64" or "float" or "double" => true,
            _ => false,
        };

        /// <summary>
        /// A repeated scalar, which travels packed into one field rather than repeated on the wire.
        /// </summary>
        /// <remarks>
        /// Worth doing rather than showing a byte count: 210 fields in the protocol are
        /// <c>repeated int32</c> and 84 more are <c>repeated int64</c>, and they carry the things
        /// most often being chased — cell lists, spell ids, effect ids.
        /// </remarks>
        private static string Packed(byte[] bytes, string type)
        {
            var values = new List<string>();
            int pos = 0;
            bool fixedWidth = type is "fixed32" or "sfixed32" or "float";
            bool wide = type is "fixed64" or "sfixed64" or "double";

            while (pos < bytes.Length && values.Count < 64)
            {
                if (fixedWidth)
                {
                    if (pos + 4 > bytes.Length) break;
                    values.Add(type == "float"
                        ? BitConverter.ToSingle(bytes, pos).ToString("R")
                        : BitConverter.ToUInt32(bytes, pos).ToString());
                    pos += 4;
                    continue;
                }

                if (wide)
                {
                    if (pos + 8 > bytes.Length) break;
                    values.Add(type == "double"
                        ? BitConverter.ToDouble(bytes, pos).ToString("R")
                        : BitConverter.ToUInt64(bytes, pos).ToString());
                    pos += 8;
                    continue;
                }

                ulong value = 0;
                int shift = 0;
                bool whole = false;
                while (pos < bytes.Length)
                {
                    byte b = bytes[pos++];
                    value |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) { whole = true; break; }
                    shift += 7;
                    if (shift > 63) break;
                }

                if (!whole) break;
                values.Add(type switch
                {
                    "bool" => value != 0 ? "true" : "false",
                    "sint32" or "sint64" => ZigZag(value).ToString(),
                    "int32" => ((int)(long)value).ToString(),
                    "int64" => ((long)value).ToString(),
                    _ => value.ToString(),
                });
            }

            string text = "[" + string.Join(", ", values) + (pos < bytes.Length ? ", …]" : "]");
            return $"{text}  ({bytes.Length} b)";
        }

        private static string MapEntry(byte[] bytes)
        {
            var entry = WireMessage.Read(bytes);
            string key = "", value = "";
            foreach (var field in entry.Fields)
            {
                string shown = field.Type == 2 ? Quote(field.Bytes) : field.Value.ToString();
                if (field.Number == 1) key = shown;
                else if (field.Number == 2) value = shown;
            }

            return $"{key} → {value}";
        }

        private static string Quote(byte[] bytes)
        {
            if (bytes.Length == 0) return "\"\"";
            if (ProtoShape.IsPrintable(bytes)) return "\"" + Encoding.UTF8.GetString(bytes) + "\"";

            try
            {
                string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                foreach (char c in text)
                {
                    if (char.IsControl(c) && c != '\n' && c != '\t') return Hex(bytes, 32);
                }

                return "\"" + text.Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
            }
            catch (ArgumentException)
            {
                return Hex(bytes, 32);
            }
        }

        public static string Hex(byte[] bytes, int limit)
        {
            if (bytes.Length == 0) return "<empty>";

            int shown = Math.Min(bytes.Length, limit);
            var text = new StringBuilder(shown * 3 + 16);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(bytes[i].ToString("X2"));
            }

            if (shown < bytes.Length) text.Append(" … ");
            text.Append("  (").Append(bytes.Length).Append(" b)");
            return text.ToString();
        }
    }
}
