using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Experience and kamas handed over by something other than a fight: an achievement claimed,
    /// a quest step finished.
    /// </summary>
    /// <remarks>
    /// The packets are the ones the captures send when an achievement is paid for, in their order:
    ///
    /// <code>
    ///   kamas        ivf {1: kamas held}, lqn 45 "gained N kamas"          tutorial, achievement 120
    ///   experience   [kua {1: new level} on a level-up] kub (the sheet), kuf {1: gained}
    /// </code>
    ///
    /// <c>kuf</c> carries the experience gained: in twelve captured frames its f1 is exactly what
    /// the experience in the kub before it went up by — 545 on 23,800,900,773 → 23,800,901,318 in
    /// the Pandala route capture, 109 in the rat-hunt one. After a fight it travels empty, which is
    /// how the fight code sends it.
    ///
    /// A level-up gives five characteristic points a level up to 200, as a fight does. Omega
    /// levels move the level on and give nothing, because what they give is not measured.
    /// </remarks>
    public static class CharacterRewards
    {
        private const int MaxLevelWithPoints = Handlers.StatsHandler.MaxLevelWithStats;

        /// <summary>Hands over kamas and says so. Returns false when there was nothing to give.</summary>
        public static async Task<bool> GiveKamasAsync(NetworkStream stream, long kamas)
        {
            if (kamas <= 0) return false;

            var state = SessionContext.State;
            state.Kamas += kamas;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(state.Kamas)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Info, InfoMessages.KamasGained, kamas.ToString())));
            return true;
        }

        /// <summary>
        /// Hands over experience: the level-up window when it is one, the new character sheet and
        /// the gain. Returns whether the level went up. <paramref name="serverBonus"/> false leaves a
        /// fixed amount as it is -- an experience scroll's 2,000 is 2,000 whatever the server's bonus.
        /// <paramref name="limited"/> puts it under the limit the player set on what he earns (see
        /// <see cref="ExperienceLimit"/>), for the gains the client warns about.
        /// </summary>
        public static async Task<bool> GiveExperienceAsync(NetworkStream stream, long experience, bool serverBonus = true,
                                                           bool limited = false)
        {
            if (experience <= 0) return false;
            if (serverBonus) experience = ServerSettings.WithBonus(experience, ServerSettings.Current.ExperienceBonusPercent);

            var state = SessionContext.State;
            if (limited) experience = ExperienceLimit.ApplyFor(state.CharacterId, experience);
            if (experience <= 0) return false;
            state.Experience += experience;

            bool up = false;
            int newLevel = ExperienceTable.LevelForXp(state.Experience);
            if (newLevel > state.CharacterLevel)
            {
                int points = Math.Max(0, Math.Min(newLevel, MaxLevelWithPoints)
                                         - Math.Min(state.CharacterLevel, MaxLevelWithPoints));
                if (points > 0) state.CharacterRemainingPoints += points * 5;
                state.CharacterLevel = newLevel;
                up = true;

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kua, ConnectionProtocol.BuildLevelUp(newLevel)));

                // And what the new level opens, in the book and on the bar.
                await Handlers.SpellHandler.SendSpellsAndBarAsync(SessionContext.Current);
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kuf, BuildExperienceGained(experience)));
            return up;
        }

        /// <summary>
        /// Writes the character down after a reward, without letting a failure stop the rest of
        /// what the client is waiting for: the mfs that closes a claim has to go either way.
        /// </summary>
        public static void Save()
        {
            try
            {
                DatabaseManager.SaveCurrentCharacter();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Rewards] The character could not be saved: {ex.Message}");
            }
        }

        /// <summary>The experience just gained (kuf): f1.</summary>
        public static byte[] BuildExperienceGained(long experience)
            => Network.Pb.New().VarIfNotZero(1, experience).Build();
    }
}
