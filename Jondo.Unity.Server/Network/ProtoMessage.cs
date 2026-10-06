using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Server.Network
{
    public class ProtoField
    {
        public int FieldNumber { get; set; }
        public int WireType { get; set; }
        public long VarIntValue { get; set; }
        public byte[] BytesValue { get; set; } = Array.Empty<byte>();
        public uint Fixed32Value { get; set; }
        public ulong Fixed64Value { get; set; }
    }

    public class ProtoMessage
    {
        public List<ProtoField> Fields { get; set; } = new List<ProtoField>();

        /// <summary>
        /// Unpicks a protobuf into loose fields.
        ///
        /// What comes in here comes from the socket, so it can be wrong on purpose. Before, a
        /// field with a length greater than what was left asked for the array all the same —up to 4 GB with
        /// five bytes— and one of the unused wire types (3, 4, 6 or 7) threw an
        /// exception nobody catches. Now both things cut the walk short and return what was
        /// read up to there, which is the same a truncated message sees and what all the
        /// handlers already know how to treat: they walk Fields looking for theirs and if it is not there, they leave.
        /// </summary>
        public static ProtoMessage Parse(byte[] data)
        {
            var msg = new ProtoMessage();
            if (data == null) return msg;

            int pos = 0;
            while (pos < data.Length)
            {
                uint tag = ReadVarInt(data, ref pos);
                int wireType = (int)(tag & 7);
                int fieldNum = (int)(tag >> 3);
                var field = new ProtoField { FieldNumber = fieldNum, WireType = wireType };
                if (wireType == 0)
                {
                    field.VarIntValue = (long)ReadVarInt64(data, ref pos);
                }
                else if (wireType == 1)
                {
                    if (pos + 8 > data.Length) break;
                    field.Fixed64Value = BitConverter.ToUInt64(data, pos);
                    pos += 8;
                }
                else if (wireType == 2)
                {
                    int len = (int)ReadVarInt(data, ref pos);
                    // The natural ceiling is what is left: a field with a length cannot go past
                    // the end of the message carrying it.
                    if (len < 0 || len > data.Length - pos) break;
                    field.BytesValue = new byte[len];
                    Array.Copy(data, pos, field.BytesValue, 0, len);
                    pos += len;
                }
                else if (wireType == 5)
                {
                    if (pos + 4 > data.Length) break;
                    field.Fixed32Value = BitConverter.ToUInt32(data, pos);
                    pos += 4;
                }
                else
                {
                    // 3 and 4 are groups, which protobuf3 no longer emits; 6 and 7 do not exist.
                    break;
                }
                msg.Fields.Add(field);
            }
            return msg;
        }

        public byte[] ToByteArray()
        {
            using var ms = new MemoryStream();
            
            // Sort fields in strictly ascending order by FieldNumber
            // to guarantee compatibility with optimized client decodifiers.
            var sortedFields = new List<ProtoField>(Fields);
            sortedFields.Sort((a, b) => a.FieldNumber.CompareTo(b.FieldNumber));

            foreach (var field in sortedFields)
            {
                WriteVarInt(ms, (ulong)((field.FieldNumber << 3) | field.WireType));
                if (field.WireType == 0)
                {
                    WriteVarInt(ms, (ulong)field.VarIntValue);
                }
                else if (field.WireType == 1)
                {
                    byte[] bytes = BitConverter.GetBytes(field.Fixed64Value);
                    ms.Write(bytes, 0, 8);
                }
                else if (field.WireType == 2)
                {
                    WriteVarInt(ms, (ulong)field.BytesValue.Length);
                    ms.Write(field.BytesValue, 0, field.BytesValue.Length);
                }
                else if (field.WireType == 5)
                {
                    byte[] bytes = BitConverter.GetBytes(field.Fixed32Value);
                    ms.Write(bytes, 0, 4);
                }
            }
            return ms.ToArray();
        }

        private static uint ReadVarInt(byte[] data, ref int pos)
        {
            uint value = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return value;
        }

        private static ulong ReadVarInt64(byte[] data, ref int pos)
        {
            ulong value = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return value;
        }

        /// <summary>One writer for the whole project. See <see cref="NetworkEnvelope.WriteVarInt"/>.</summary>
        public static void WriteVarInt(Stream stream, ulong value)
            => NetworkEnvelope.WriteVarInt(stream, value);

        /// <summary>
        /// The fields on ONE line, sniffer style: <c>{ 1: 453 2: "1630" }</c>.
        ///
        /// The tree dump down here is still there and serves to look at a specific packet,
        /// but for the log it is no good: twenty lines per packet and after three seconds nothing
        /// can be seen. A packet is one line, and what does not fit is cut with an ellipsis.
        /// </summary>
        public string Compact(int budget = 96)
        {
            var sb = new System.Text.StringBuilder("{ ");
            bool cut = Write(sb, budget);
            if (cut) sb.Append("… ");
            sb.Append('}');
            return sb.Length <= 3 ? "" : sb.ToString();
        }

        /// <summary>Returns true if something was left out for lack of room.</summary>
        private bool Write(System.Text.StringBuilder sb, int budget)
        {
            foreach (var field in Fields)
            {
                if (sb.Length >= budget) return true;

                sb.Append(field.FieldNumber).Append(": ");

                if (field.WireType == 0)
                {
                    sb.Append(field.VarIntValue);
                }
                else if (field.WireType == 2)
                {
                    Bytes(sb, field, budget);
                }
                else
                {
                    // The fixed 32 and 64 bit ones: shown raw, which is what they are.
                    sb.Append("0x").Append(Convert.ToHexString(field.BytesValue).ToLowerInvariant());
                }

                sb.Append(' ');
            }
            return false;
        }

        private static void Bytes(System.Text.StringBuilder sb, ProtoField field, int budget)
        {
            // A submessage first, because it is what says the most. If it does not parse, text is tried, and
            // if not that either, hexadecimal: the same order as the tree dump, so that both
            // views tell the same about the same packet.
            if (field.BytesValue.Length > 0 && field.BytesValue.Length < 2000)
            {
                try
                {
                    var sub = Parse(field.BytesValue);
                    if (sub.Fields.Count > 0)
                    {
                        sb.Append("{ ");
                        if (sub.Write(sb, budget)) sb.Append("… ");
                        sb.Append('}');
                        return;
                    }
                }
                catch { }
            }

            string text = System.Text.Encoding.UTF8.GetString(field.BytesValue);
            if (text.Length > 0 && text.All(c => !char.IsControl(c)))
            {
                sb.Append('"').Append(text).Append('"');
                return;
            }

            sb.Append("0x").Append(Convert.ToHexString(field.BytesValue).ToLowerInvariant());
        }

        public string DumpFieldsToString(string indent = "  ", int maxLines = 15)
        {
            var sb = new System.Text.StringBuilder();
            int lineCount = 0;
            DumpFieldsRecursive(sb, indent, ref lineCount, maxLines);
            return sb.ToString();
        }

        private void DumpFieldsRecursive(System.Text.StringBuilder sb, string indent, ref int lineCount, int maxLines)
        {
            foreach (var field in Fields)
            {
                if (lineCount >= maxLines)
                {
                    sb.AppendLine($"{indent}... [Truncated remaining fields]");
                    return;
                }

                sb.Append($"{indent}Tag #{field.FieldNumber} (Type {field.WireType}): ");
                lineCount++;

                if (field.WireType == 0)
                {
                    sb.AppendLine($"VarInt = {field.VarIntValue}");
                }
                else if (field.WireType == 2)
                {
                    bool parsedSub = false;
                    if (field.BytesValue.Length > 0 && field.BytesValue.Length < 2000)
                    {
                        try
                        {
                            var sub = Parse(field.BytesValue);
                            if (sub.Fields.Count > 0)
                            {
                                sb.AppendLine($"SubMessage ({field.BytesValue.Length} B) [{sub.Fields.Count} fields]:");
                                sub.DumpFieldsRecursive(sb, indent + "    ", ref lineCount, maxLines);
                                parsedSub = true;
                            }
                        }
                        catch { }
                    }

                    if (!parsedSub)
                    {
                        string text = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                        bool isPrintable = text.Length > 0 && text.All(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t');
                        if (isPrintable)
                        {
                            sb.AppendLine($"String = \"{text}\"");
                        }
                        else
                        {
                            string hex = BitConverter.ToString(field.BytesValue).Replace("-", " ");
                            if (hex.Length > 40) hex = hex.Substring(0, 40) + "...";
                            sb.AppendLine($"Bytes[{field.BytesValue.Length}] = {hex}");
                        }
                    }
                }
                else if (field.WireType == 1)
                {
                    sb.AppendLine($"Fixed64 = {field.Fixed64Value}");
                }
                else if (field.WireType == 5)
                {
                    sb.AppendLine($"Fixed32 = {field.Fixed32Value}");
                }
            }
        }
    }
}
