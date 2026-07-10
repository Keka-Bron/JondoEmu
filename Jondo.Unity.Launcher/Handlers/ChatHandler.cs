using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Handles all chat message logic:
    /// - Receiving chat messages (kqn) from the client
    /// - Building and sending chat broadcast packets (kqp)
    /// </summary>
    public static class ChatHandler
    {
        public static async Task HandleChatMessage(NetworkStream stream, byte[] payload)
        {
            Console.WriteLine("[Game Node] Received Chat Message (kqn)");
            byte[]? inner   = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/kqn");
            string? msgText = ExtractStringField(inner, 3);

            if (!string.IsNullOrEmpty(msgText))
            {
                if (msgText.StartsWith("."))
                {
                    await CommandHandler.HandleCommand(stream, msgText);
                }
                else
                {
                    Console.WriteLine($"[Chat] {GameState.CharacterName}: {msgText}");
                    byte[] echoPacket = BuildChatBroadcastPacket(msgText, GameState.CharacterName, channel: 0);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, echoPacket);
                    Console.WriteLine("[Chat] Echoed chat message back to client.");
                }
            }
        }

        /// <summary>Builds a kqp (ChatServerMessage) broadcast packet.</summary>
        public static byte[] BuildChatBroadcastPacket(string messageText, string senderName, int channel = 0)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            output.WriteTag((uint)((4  << 3) | 0)); // Field 4:  timestamp
            output.WriteInt64(timestamp);

            output.WriteTag((uint)((7  << 3) | 0)); // Field 7:  actor ID
            output.WriteInt64(GameState.CharacterId);

            output.WriteTag((uint)((8  << 3) | 0)); // Field 8:  channel
            output.WriteInt32(channel);

            output.WriteTag((uint)((9  << 3) | 2)); // Field 9:  message text
            output.WriteString(messageText);

            output.WriteTag((uint)((10 << 3) | 2)); // Field 10: sender name
            output.WriteString(senderName);

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kqp", ms.ToArray());
        }

        // ─── Helpers ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads a length-delimited (wire type 2) string field from a raw Protobuf payload.
        /// Returns null if the field is absent or the payload is null.
        /// </summary>
        private static string? ExtractStringField(byte[]? payload, int targetFieldNum)
        {
            if (payload == null) return null;
            try
            {
                int pos = 0;
                while (pos < payload.Length)
                {
                    uint tag      = NetworkEnvelope.ReadVarInt(payload, ref pos);
                    int  wireType = (int)(tag & 7);
                    int  fieldNum = (int)(tag >> 3);

                    if (fieldNum == targetFieldNum && wireType == 2)
                    {
                        int len = (int)NetworkEnvelope.ReadVarInt(payload, ref pos);
                        if (pos + len <= payload.Length)
                            return Encoding.UTF8.GetString(payload, pos, len);
                    }
                    else
                    {
                        NetworkEnvelope.SkipField(payload, wireType, ref pos);
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
