using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Handles all chat message logic:
    /// - Receiving chat messages (kqn) from the client
    /// - Building and sending chat broadcast packets (kqp)
    /// </summary>
    public static class ChatHandler
    {
        public static async Task HandleChatMessage(NetworkStream stream, byte[] payload,
                                                   long accountId = 0)
        {
            Console.WriteLine("[Game Node] Received Chat Message (kqn)");
            byte[]? inner   = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/kqn");
            string? msgText = ExtractStringField(inner, 3);

            if (!string.IsNullOrEmpty(msgText))
            {
                // A command is NOT published: it is handled and that is the end of it, with no echo. If it
                // fell through to the echo, a ".kamas 10000" would appear written in the tab where it was
                // typed and everybody would see it. Only the ones that exist are swallowed; a line that
                // starts with a dot and is none of them goes on its way like any other.
                if (await CommandHandler.TryHandleAsync(stream, msgText, channel: 0, accountId: accountId))
                {
                    return;
                }

                Console.WriteLine($"[Chat] {GameState.CharacterName}: {msgText}");
                byte[] echoPacket = BuildChatBroadcastPacket(msgText, GameState.CharacterName, channel: 0);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, echoPacket);

                // And to the others, which is what a chat is about.
                //
                // This was only an echo: the message went back to whoever wrote it and that was it,
                // so two players on the same map each talked to their own screen. It was called
                // «broadcast» without being one, from when the server served a single player.
                //
                // Dofus's general channel is the MAP's: whoever is there hears it, not the whole
                // server. The packet is the same for everybody -- it carries the speaker's id and
                // name inside --, so it is sent as it is.
                // With the speaker's fight: two fights on the same map share an arena -- one per
                // surface map, which is how it comes out of the game's data -- so without this the
                // map channel crossed the two and each group read the other.
                int oidos = await SessionRegistry.BroadcastToMapAsync(
                    SessionContext.State.MapId, echoPacket, SessionContext.Current.Id,
                    SessionContext.State.FightId);
                Console.WriteLine($"[Chat] Repartido a {oidos} jugador(es) más en el mapa " +
                                  $"{SessionContext.State.MapId}.");
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
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Kqp), ms.ToArray());
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
