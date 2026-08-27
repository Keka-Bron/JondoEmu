using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The one thing the client says about achievements: pay me.
    /// </summary>
    /// <remarks>
    /// Earning an achievement is the server's business — nobody is asked whether they finished a
    /// quest — so there is only the claim to handle. The capture
    /// <c>Logros\aceptar recompensas de un logro</c> is exactly one press of that button:
    /// <c>mga {1: 8990}</c> goes up, and the character sheet and the confirmation come back.
    /// </remarks>
    public static class AchievementHandler
    {
        /// <summary>
        /// The client wants the reward of an achievement (mga): f1 the achievement, or -1 for all.
        /// </summary>
        /// <remarks>
        /// The -1 is not a guess: three of the captures send <c>mga</c> with a varint of
        /// 18446744073709551615, which is what -1 looks like on the wire, and they send it on
        /// entering the world rather than in front of any particular achievement.
        /// </remarks>
        public static async Task ClaimAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? mga = ConnectionProtocol.ReadPayload(payload, Op.Mga);
            if (mga == null) return;

            int achievementId = -1;
            foreach (var field in ProtoMessage.Parse(mga).Fields)
            {
                if (field.FieldNumber != 1 || field.WireType != 0) continue;

                long value = field.VarIntValue;

                // Anything that does not fit in an int is the client's -1, not an id: achievement
                // ids stop at 9,062. Reading it as an unsigned number would ask for reward
                // number 18,446,744,073,709,551,615 and quietly do nothing.
                achievementId = value > 0 && value <= int.MaxValue ? (int)value : -1;
                break;
            }

            await Achievements.ClaimAsync(stream, achievementId);
        }
    }
}
