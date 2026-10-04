using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// What the client says about achievements: open the window, show a category or one
    /// achievement, and pay me.
    /// </summary>
    /// <remarks>
    /// Earning an achievement is the server's business — nobody is asked whether they finished a
    /// quest — so everything that comes in is either the window asking what to draw or the claim.
    /// The capture <c>Logros\aceptar recompensas de un logro</c> is exactly one press of the claim
    /// button: <c>mga {1: 8990}</c> goes up, and the character sheet, the experience gained and the
    /// confirmation come back. <c>Chats\usando todos los chats</c> ends with the window opening:
    /// <c>mfe</c>, <c>mfp</c> and <c>mff {40}</c> go up, and <c>mgb</c>, <c>mfx</c> and <c>mfo</c>
    /// come back.
    /// </remarks>
    public static class AchievementHandler
    {
        /// <summary>The client wants the reward of an achievement (mga): f1 the achievement, or -1 for all.</summary>
        public static async Task ClaimAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? mga = ConnectionProtocol.ReadPayload(payload, Op.Mga);
            if (mga == null) return;

            await Achievements.ClaimAsync(stream, AchievementProtocol.ReadClaim(mga));
        }

        /// <summary>The window has opened (mfe, empty).</summary>
        public static Task OpenedAsync(NetworkStream stream) => Achievements.OpenedAsync(stream);

        /// <summary>The window's second request (mfp, empty), answered on root 3 with the request's id.</summary>
        public static Task SecondRequestAsync(NetworkStream stream, byte[] payload)
            => Achievements.SecondRequestAsync(stream, payload);

        /// <summary>A category of the window (mff): f1 the category.</summary>
        public static async Task CategoryAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? mff = ConnectionProtocol.ReadPayload(payload, Op.Mff);
            if (mff == null) return;

            await Achievements.CategoryAsync(stream, AchievementProtocol.ReadCategory(mff));
        }

        /// <summary>One achievement of the window (mfm). INFERRED: never captured.</summary>
        public static async Task DetailsAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? mfm = ConnectionProtocol.ReadPayload(payload, Op.Mfm);
            if (mfm == null) return;

            await Achievements.DetailsAsync(stream, AchievementProtocol.ReadDetailsRequest(mfm));
        }
    }
}
