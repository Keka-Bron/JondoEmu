using Jondo.Unity.Launcher;
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Google.Protobuf;

namespace Jondo.Unity.Server.Network
{
    public static class NetworkEnvelope
    {
        public static byte[] BuildGameNodePacket(string typeUrl, byte[] payload)
        {
            // Validate opcode length (Phase 0 - COMBAT_DIAGNOSTICS)
            string opcode = typeUrl.StartsWith("type.ankama.com/") ? typeUrl.Substring("type.ankama.com/".Length) : typeUrl;
            if (opcode.Length != 3)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[WARN] INVALID OPCODE LENGTH: '{opcode}' in '{typeUrl}' (length {opcode.Length} != 3). Dofus 3 opcodes MUST be 3 characters!");
                Console.ResetColor();
            }

            // 1. Build Any-like msg (Field 1: string typeUrl, Field 2: bytes payload)
            using var anyMs = new MemoryStream();
            {
                var output = new CodedOutputStream(anyMs);
                // Tag 1 (wire type 2, length-prefixed): typeUrl
                output.WriteTag((uint)((1 << 3) | 2));
                output.WriteString(typeUrl);

                // Tag 2 (wire type 2, length-prefixed): payload bytes
                if (payload != null && payload.Length > 0)
                {
                    output.WriteTag((uint)((2 << 3) | 2));
                    output.WriteBytes(ByteString.CopyFrom(payload));
                }

                output.Flush();
            }
            byte[] anyBytes = anyMs.ToArray();

            // 2. Build wrapperMsg (Field 1: anyMsg)
            using var wrapperMs = new MemoryStream();
            {
                var output = new CodedOutputStream(wrapperMs);
                // Tag 1 (wire type 2, length-prefixed)
                output.WriteTag((uint)((1 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(anyBytes));
                output.Flush();
            }
            byte[] wrapperBytes = wrapperMs.ToArray();

            // 3. Build rootMsg (Field 3: wrapperMsg)
            using var rootMs = new MemoryStream();
            {
                var output = new CodedOutputStream(rootMs);
                // Tag 3 (wire type 2, length-prefixed)
                output.WriteTag((uint)((3 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(wrapperBytes));
                output.Flush();
            }

            return rootMs.ToArray();
        }

        public static byte[]? ExtractGameNodePayload(byte[] rootMsgBytes)
        {
            try
            {
                // 1. Parse rootMsg -> Field 3 is wrapperMsg
                int pos = 0;
                byte[]? wrapperBytes = null;
                while (pos < rootMsgBytes.Length)
                {
                    uint tag = ReadVarInt(rootMsgBytes, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if (fieldNum == 3 && wireType == 2)
                    {
                        uint length = ReadVarInt(rootMsgBytes, ref pos);
                        if (length > (uint)(rootMsgBytes.Length - pos)) return null;
                        wrapperBytes = new byte[length];
                        Array.Copy(rootMsgBytes, pos, wrapperBytes, 0, length);
                        break;
                    }
                    else
                    {
                        SkipField(rootMsgBytes, wireType, ref pos);
                    }
                }

                if (wrapperBytes == null) return null;

                // 2. Parse wrapperMsg -> Field 1 is anyMsg
                pos = 0;
                byte[]? anyBytes = null;
                while (pos < wrapperBytes.Length)
                {
                    uint tag = ReadVarInt(wrapperBytes, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if (fieldNum == 1 && wireType == 2)
                    {
                        uint length = ReadVarInt(wrapperBytes, ref pos);
                        if (length > (uint)(wrapperBytes.Length - pos)) return null;
                        anyBytes = new byte[length];
                        Array.Copy(wrapperBytes, pos, anyBytes, 0, length);
                        break;
                    }
                    else
                    {
                        SkipField(wrapperBytes, wireType, ref pos);
                    }
                }

                if (anyBytes == null) return null;

                // 3. Parse anyMsg -> Field 2 is payload bytes
                pos = 0;
                byte[]? payloadBytes = null;
                while (pos < anyBytes.Length)
                {
                    uint tag = ReadVarInt(anyBytes, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if (fieldNum == 2 && wireType == 2)
                    {
                        uint length = ReadVarInt(anyBytes, ref pos);
                        payloadBytes = new byte[length];
                        Array.Copy(anyBytes, pos, payloadBytes, 0, length);
                        break;
                    }
                    else
                    {
                        SkipField(anyBytes, wireType, ref pos);
                    }
                }

                return payloadBytes;
            }
            catch
            {
                return null;
            }
        }

        public static byte[]? UnpackLengthPrefixed(byte[] rawFrame)
        {
            try
            {
                int pos = 0;
                uint length = ReadVarInt(rawFrame, ref pos);
                if (pos + length > rawFrame.Length) return null;
                byte[] payload = new byte[length];
                Array.Copy(rawFrame, pos, payload, 0, length);
                return payload;
            }
            catch
            {
                return null;
            }
        }

        public static byte[]? ExtractMessagePayload(byte[] fullFrame, string expectedTypeUrl)
        {
            try
            {
                // The client frame (fullFrame) has the length prefix stripped.
                // It is a root message. Field 1 (tag 0x0a) contains the Envelope.
                int pos = 0;
                byte[]? envelopeBytes = null;
                while (pos < fullFrame.Length)
                {
                    uint tag = ReadVarInt(fullFrame, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if (fieldNum == 1 && wireType == 2)
                    {
                        uint len = ReadVarInt(fullFrame, ref pos);
                        if (len > (uint)(fullFrame.Length - pos)) return null;
                        envelopeBytes = new byte[len];
                        Array.Copy(fullFrame, pos, envelopeBytes, 0, len);
                        pos += (int)len;
                        break;
                    }
                    else
                    {
                        SkipField(fullFrame, wireType, ref pos);
                    }
                }

                if (envelopeBytes == null) return null;

                // Inside the Envelope: Field 2 (tag 0x12) is the Any message.
                pos = 0;
                byte[]? anyBytes = null;
                while (pos < envelopeBytes.Length)
                {
                    uint tag = ReadVarInt(envelopeBytes, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if (fieldNum == 2 && wireType == 2)
                    {
                        uint len = ReadVarInt(envelopeBytes, ref pos);
                        if (len > (uint)(envelopeBytes.Length - pos)) return null;
                        anyBytes = new byte[len];
                        Array.Copy(envelopeBytes, pos, anyBytes, 0, len);
                        pos += (int)len;
                        break;
                    }
                    else
                    {
                        SkipField(envelopeBytes, wireType, ref pos);
                    }
                }

                if (anyBytes == null) return null;

                // Inside the Any message: Field 1 (tag 0x0a) is typeUrl (string), Field 2 (tag 0x12) is value (bytes).
                pos = 0;
                string? typeUrl = null;
                byte[]? valueBytes = null;
                while (pos < anyBytes.Length)
                {
                    uint tag = ReadVarInt(anyBytes, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if (fieldNum == 1 && wireType == 2)
                    {
                        uint len = ReadVarInt(anyBytes, ref pos);
                        typeUrl = System.Text.Encoding.UTF8.GetString(anyBytes, pos, (int)len);
                        pos += (int)len;
                    }
                    else if (fieldNum == 2 && wireType == 2)
                    {
                        uint len = ReadVarInt(anyBytes, ref pos);
                        valueBytes = new byte[len];
                        Array.Copy(anyBytes, pos, valueBytes, 0, len);
                        pos += (int)len;
                    }
                    else
                    {
                        SkipField(anyBytes, wireType, ref pos);
                    }
                }

                if (typeUrl == expectedTypeUrl)
                {
                    return valueBytes;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static string? GetMessageTypeUrl(byte[] fullFrame)
        {
            try
            {
                int pos = 0;
                byte[]? innerBytes = null;
                bool isServer = false;
                while (pos < fullFrame.Length)
                {
                    uint tag = ReadVarInt(fullFrame, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if (fieldNum == 3 && wireType == 2)
                    {
                        uint len = ReadVarInt(fullFrame, ref pos);
                        innerBytes = new byte[len];
                        Array.Copy(fullFrame, pos, innerBytes, 0, len);
                        isServer = true;
                        break;
                    }
                    else if (fieldNum == 1 && wireType == 2)
                    {
                        uint len = ReadVarInt(fullFrame, ref pos);
                        innerBytes = new byte[len];
                        Array.Copy(fullFrame, pos, innerBytes, 0, len);
                        break;
                    }
                    else
                    {
                        SkipField(fullFrame, wireType, ref pos);
                    }
                }

                if (innerBytes == null) return null;

                pos = 0;
                byte[]? anyBytes = null;
                while (pos < innerBytes.Length)
                {
                    uint tag = ReadVarInt(innerBytes, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if ((isServer ? (fieldNum == 1) : (fieldNum == 2)) && wireType == 2)
                    {
                        uint len = ReadVarInt(innerBytes, ref pos);
                        anyBytes = new byte[len];
                        Array.Copy(innerBytes, pos, anyBytes, 0, len);
                        break;
                    }
                    else
                    {
                        SkipField(innerBytes, wireType, ref pos);
                    }
                }

                if (anyBytes == null) return null;

                pos = 0;
                while (pos < anyBytes.Length)
                {
                    uint tag = ReadVarInt(anyBytes, ref pos);
                    int wireType = (int)(tag & 7);
                    int fieldNum = (int)(tag >> 3);
                    if (fieldNum == 1 && wireType == 2)
                    {
                        uint len = ReadVarInt(anyBytes, ref pos);
                        return System.Text.Encoding.UTF8.GetString(anyBytes, pos, (int)len);
                    }
                    else
                    {
                        SkipField(anyBytes, wireType, ref pos);
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static uint ReadVarInt(byte[] bytes, ref int pos)
        {
            uint value = 0;
            int shift = 0;
            while (pos < bytes.Length)
            {
                byte b = bytes[pos++];
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
            }
            throw new InvalidDataException("Invalid VarInt");
        }

        public static ulong ReadVarInt64(byte[] bytes, ref int pos)
        {
            ulong value = 0;
            int shift = 0;
            while (pos < bytes.Length)
            {
                byte b = bytes[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
            }
            throw new InvalidDataException("Invalid VarInt64");
        }

        public static void WriteVarInt(Stream stream, ulong value)
        {
            while (true)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value > 0)
                {
                    stream.WriteByte((byte)(b | 0x80));
                }
                else
                {
                    stream.WriteByte(b);
                    break;
                }
            }
        }

        /// <summary>
        /// Salta el campo que hay en pos.
        ///
        /// Todos los saltos acaban al final del buffer como muy lejos. Antes «pos += (int)len»
        /// con una longitud inventada podia dejar pos NEGATIVO, y quien llama recorre con
        /// «while (pos < bytes.Length)»: el bucle no terminaba nunca. Y un tipo de cable de los
        /// que no se usan lanzaba una excepcion en mitad del enrutado. Las dos cosas terminan
        /// ahora poniendo pos al final, que es lo que se hace con lo que no se entiende.
        /// </summary>
        public static void SkipField(byte[] bytes, int wireType, ref int pos)
        {
            if (wireType == 0)
            {
                ReadVarInt(bytes, ref pos);
            }
            else if (wireType == 1)
            {
                pos = pos + 8 > bytes.Length ? bytes.Length : pos + 8;
            }
            else if (wireType == 2)
            {
                uint len = ReadVarInt(bytes, ref pos);
                pos = len > (uint)(bytes.Length - pos) ? bytes.Length : pos + (int)len;
            }
            else if (wireType == 5)
            {
                pos = pos + 4 > bytes.Length ? bytes.Length : pos + 4;
            }
            else
            {
                pos = bytes.Length;
            }
        }

        public static bool ContainsSequence(byte[] source, byte[] target)
        {
            if (source.Length < target.Length) return false;
            for (int i = 0; i <= source.Length - target.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < target.Length; j++)
                {
                    if (source[i + j] != target[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }

        public static byte[] ConvertHexStringToByteArray(string hex)
        {
            hex = hex.Replace("-", "").Replace(" ", "");
            byte[] raw = new byte[hex.Length / 2];
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return raw;
        }

        public static string ResolvePacketPath(string filename)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
            if (File.Exists(path)) return path;

            path = Path.Combine(Directory.GetCurrentDirectory(), filename);
            if (File.Exists(path)) return path;

            string parentDir = Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? "";
            if (!string.IsNullOrEmpty(parentDir))
            {
                path = Path.Combine(parentDir, filename);
                if (File.Exists(path)) return path;
            }

            path = Paths.Resolve(filename);
            if (File.Exists(path)) return path;

            return filename;
        }

        public static byte[]? ExtractAnyPayloadByType361010(byte[] packet,string expectedTypeUrl)
        {
            try
            {
                var root = ProtoMessage.Parse(packet);

                return FindAnyPayloadRecursive361010(
                    root,
                    expectedTypeUrl,
                    0
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Game Node] Any extraction error: {ex.Message}"
                );

                return null;
            }
        }

        private static byte[]? FindAnyPayloadRecursive361010(
            ProtoMessage message,
            string expectedTypeUrl,
            int depth)
        {
            // Protection contre une récursion foireuse
            if (depth > 8)
                return null;

            /*
             * Cherche d'abord si CE message est directement un google.protobuf.Any :
             *
             * field1 = type_url
             * field2 = value
             */

            var typeField =
                message.Fields.FirstOrDefault(
                    f => f.FieldNumber == 1 &&
                         f.WireType == 2
                );

            var valueField =
                message.Fields.FirstOrDefault(
                    f => f.FieldNumber == 2 &&
                         f.WireType == 2
                );

            if (typeField != null &&
                valueField != null)
            {
                try
                {
                    string typeUrl =
                        Encoding.UTF8.GetString(
                            typeField.BytesValue
                        );

                    if (typeUrl == expectedTypeUrl)
                    {
                        Console.WriteLine(
                            $"[Game Node] Found Any {typeUrl}, " +
                            $"inner={valueField.BytesValue.Length} B"
                        );

                        return valueField.BytesValue;
                    }
                }
                catch
                {
                    // Ce field1 n'était simplement pas une string.
                }
            }

            /*
             * Sinon on descend dans tous les fields protobuf
             * length-delimited.
             */

            foreach (
                var field in message.Fields.Where(
                    f => f.WireType == 2 &&
                         f.BytesValue != null &&
                         f.BytesValue.Length > 0
                )
            )
            {
                try
                {
                    var child =
                        ProtoMessage.Parse(
                            field.BytesValue
                        );

                    var found =
                        FindAnyPayloadRecursive361010(
                            child,
                            expectedTypeUrl,
                            depth + 1
                        );

                    if (found != null)
                        return found;
                }
                catch
                {
                    /*
                     * Normal :
                     * certains wire type 2 sont des strings
                     * ou des bytes quelconques et non des protobuf.
                     */
                }
            }

            return null;
        }
    }
}
