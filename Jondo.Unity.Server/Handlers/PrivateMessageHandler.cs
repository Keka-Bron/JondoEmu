using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Whispers: talking privately with another character.
    ///
    /// ─── What goes over the wire ────────────────────────────────────────────────────────────
    ///
    /// A whisper is NOT one more channel of the normal chat. Channel chat goes in <c>ktm</c>, with the
    /// channel number in its f3; the whisper has a message of its own:
    ///
    ///   ktb { f1: the text, f5: to whom }
    ///
    /// Measured in three captures, with this exact shape:
    ///
    ///   0a04 686f6c61  2200  2a0c 53616372692d4d6173746572
    ///   f1 = "hola"    f4 = ""    f5 = "Sacri-Master"
    ///
    /// The private channel is 9. That was not deduced: it is in the client's own table,
    /// ChatChannelsDataRoot, where 9 is called «Privado», carries <c>isPrivate</c> and its shortcut is
    /// <c>/w</c>. In that same table 10 is «Información» and 11 «Combate», the three of them private.
    ///
    /// When something cannot be said, the server answers with <c>ktl</c>, which the dump of real names
    /// calls <c>ChatErrorEvent</c>, and it carries a single number. The only value we have tied to a
    /// specific cause is 2, which is what the real server answered when whispering to oneself. The
    /// others seen -- 1, 4, 5, 8 and 10 -- come up when talking on channels where the player cannot, but
    /// it has not been possible to pair each number with its reason, so only 2, which is measured, is
    /// used here.
    ///
    /// ─── The message is NOT a chat line ─────────────────────────────────────────────────────
    ///
    /// This took a failed attempt: a whisper is not sent as a <c>kti</c> on channel 9. It has its own
    /// message, <c>kth</c> -- ChatPrivateCopyMessageEvent in the names dump --, and the client dispatches
    /// it by opcode, not by channel. Sending it as a channel 9 kti draws absolutely nothing: it arrives,
    /// the server considers it done, and there is nothing on screen.
    ///
    ///   kth { f1: date, f4: empty, f5: the other's id, f6: his name, f7: the text }
    ///
    /// And what it carries is not who speaks, but THE OTHER: in the sender's copy goes whom it is said
    /// to. It is measured in the guild capture, where the whisper to «Hiierbita-Xx» did reach its
    /// destination and the server answered with this kth.
    ///
    /// From the side of WHOEVER RECEIVES IT there is no capture -- it would need recording as the
    /// recipient -- so he is sent the same kth with the identity of whoever speaks. It is the natural
    /// reading of the format: the field is «the other», and for the receiver the other is whoever
    /// writes to him.
    /// </summary>
    public static class PrivateMessageHandler
    {
        /// <summary>The private channel, from ChatChannelsDataRoot.</summary>
        public const int PrivateChannel = 9;

        /// <summary>
        /// What the real server answers when the whisper does not go out. Measured by whispering to oneself;
        /// for «that character is not here» there is no capture, so the same one is used.
        /// </summary>
        public const int CannotWhisper = 2;

        public static async Task WhisperAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ktb = ConnectionProtocol.ReadPayload(payload, Op.Ktb);
            if (ktb == null) return;

            string text = "";
            string target = "";
            foreach (var field in ProtoMessage.Parse(ktb).Fields)
            {
                if (field.WireType != 2) continue;
                if (field.FieldNumber == 1) text = Text(field);
                else if (field.FieldNumber == 5) target = Text(field);
            }

            if (text.Length == 0 || target.Length == 0) return;

            string from = SessionContext.State.CharacterName;

            // Not to oneself. It is exactly the measured case: the real server answers ktl 2.
            if (string.Equals(target, from, StringComparison.OrdinalIgnoreCase))
            {
                await RefuseAsync(stream);
                Console.WriteLine($"[Privado] {from} intenta susurrarse a sí mismo.");
                return;
            }

            var destino = SessionRegistry.FindByName(target);
            if (destino == null)
            {
                await RefuseAsync(stream);
                Console.WriteLine($"[Privado] {from} susurra a «{target}», que no está conectado.");
                return;
            }

            string cuando = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

            // To the receiver: the other is whoever writes to him.
            await destino.SendAsync(ConnectionProtocol.Push(Op.Kth,
                ConnectionProtocol.BuildPrivateMessage(
                    cuando, SessionContext.State.CharacterId, from, text)));

            // And to the sender, his copy: the other is whom he says it to.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kth,
                    ConnectionProtocol.BuildPrivateMessage(
                        cuando, destino.State.CharacterId, destino.State.CharacterName, text)));

            Console.WriteLine($"[Privado] {from} → {destino.State.CharacterName}: {text}");
        }

        private static async Task RefuseAsync(NetworkStream stream)
            => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ktl, ConnectionProtocol.BuildChatError(CannotWhisper)));

        private static string Text(ProtoField field)
        {
            try
            {
                return System.Text.Encoding.UTF8.GetString(field.BytesValue);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
