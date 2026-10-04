using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Emotes, smileys and the mood smiley, as the client asks for them: from its emote bar and
    /// its smiley panel. See <see cref="Emotes"/> for what is done with each.
    /// </summary>
    public static class EmoteHandler
    {
        /// <summary>Play an emote (khl): f1 the emote.</summary>
        public static async Task PlayAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? khl = ConnectionProtocol.ReadPayload(payload, Op.Khl);
            if (khl == null) return;

            await Emotes.PlayAsync(stream, EmoteProtocol.ReadPlay(khl));
        }

        /// <summary>A smiley over the head (hov): f2 the smiley.</summary>
        public static async Task SmileyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hov = ConnectionProtocol.ReadPayload(payload, Op.Hov);
            if (hov == null) return;

            await Emotes.SmileyAsync(stream, EmoteProtocol.ReadSmiley(hov));
        }

        /// <summary>The mood smiley (hor): f5 the smiley, or nothing to clear it.</summary>
        public static async Task MoodAsync(NetworkStream stream, byte[] payload)
        {
            // An empty hor has no payload at all, which is how the capture clears it: that is a
            // mood of zero, not a message to ignore.
            byte[] hor = ConnectionProtocol.ReadPayload(payload, Op.Hor) ?? System.Array.Empty<byte>();
            await Emotes.MoodAsync(stream, EmoteProtocol.ReadMood(hor));
        }
    }
}
