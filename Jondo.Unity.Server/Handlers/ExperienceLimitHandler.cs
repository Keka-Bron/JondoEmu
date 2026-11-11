using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The limit on earned experience, set from the characteristics panel (see <see cref="ExperienceLimit"/>).
    ///
    ///   C  kts { f1: the percent }
    ///   S  hox { f1, f2, f4: the percent }       the character's experience settings, again
    ///
    /// Measured in the two captures «desde panel caracteristicas limitar experiencia obtenida»:
    /// 100 to 50 is kts 0832 and hox 086910692032, and back is kts 0864 and hox 086910692064. Nothing
    /// else goes; the kqy around them answer the heartbeat.
    /// </summary>
    public static class ExperienceLimitHandler
    {
        /// <summary>The kts: the limit is stored and the client told it, as the real server does.</summary>
        public static async Task SetAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kts = ConnectionProtocol.ReadPayload(payload, Op.Kts);
            if (kts == null) return;
            int asked = 0;
            foreach (var field in ProtoMessage.Parse(kts).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) asked = (int)field.VarIntValue;
            }

            int limit = ExperienceLimit.Set(SessionContext.State.CharacterId, asked);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hox, BuildSettings(limit)));
        }

        /// <summary>
        /// f2 − 100 of the hox, which the client keeps beside the experience bonus (eet +536). It is 5
        /// in every capture too; what it is, nothing in the client's tooltip says.
        /// </summary>
        private const int SecondBonusPercent = 5;

        /// <summary>
        /// The character's experience settings (hox): 100 + his experience bonus in f1 (see
        /// <see cref="CombatExperience.CharacterBonusPercent"/>, which the fights pay), 100 + the
        /// other bonus in f2, and his limit in f4.
        /// </summary>
        internal static ServerExperienceModifierEvent Settings(int limit)
            => new(ExperienceLimit.Full + CombatExperience.CharacterBonusPercent,
                ExperienceLimit.Full + SecondBonusPercent, limit);

        /// <summary>Legacy settings payload for the limit-change request handler.</summary>
        public static byte[] BuildSettings(int limit)
        {
            var settings = Settings(limit);
            return Pb.New()
                .Var(1, settings.ExperiencePercentage)
                .Var(2, settings.SecondaryPercentage)
                .VarIfNotZero(4, settings.ExperienceLimitPercentage)
                .Build();
        }
    }
}
