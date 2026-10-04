using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Content
{
    /// <summary>
    /// The raid chest: where the treasures go in, and where the raid ends.
    ///
    /// It is NPC 7861, "Cofre de la raid", and like the luminomachine it says everything about
    /// itself in the client's own data. Two screens and five replies:
    ///
    ///     *emite una extraña vibración*
    ///       · Soltar todos los tesoros.
    ///       · Acercarte al cofre a pesar de la sombra que acecha.
    ///       · Retroceder.
    ///
    ///     *¡CUIDADO! Si tomas el cofre, ¡la raid se acabará para todo el equipo!*
    ///     *asegúrate de que todos los aventureros puedan unirse a ti*
    ///       · Tomar el cofre y escapar.
    ///       · Retroceder.
    ///
    /// And it fills up as the score rises: its look carries FIVE variants with a criterion each,
    /// reading the raid's own <c>Raid_Score</c>, so an emptyish chest and a heaped one are the same
    /// actor drawn differently. The thresholds are exact and they are the game's, not ours.
    /// </summary>
    /// <remarks>
    /// WHAT A TREASURE IS WORTH is not written here either, and that is the good part: eleven items
    /// in the whole game carry effect 4063, "Valor de un objeto", and all eleven are raid resources
    /// -- the seven gems from 2 to 30, the three guardians' trophies at 1000, 5000 and 10000, and
    /// the salt at 1. So a treasure is not a list to keep up to date, it is anything that carries a
    /// value, and the value is the item's own. See <c>RaidTreasures</c>.
    ///
    /// NOT DONE, and it is the other half of the ending: when the clock runs out before the chest
    /// is taken, the client has a line that says "El Gigalodón acaba de devorar vuestros tesoros.
    /// ¡Subid deprisa para enfrentaros a él!". That fight needs a boss placed and scripted, and
    /// there is none. Today the clock just closes the raid.
    /// </remarks>
    public static class RaidChest
    {
        /// <summary>The NPC the chest is, "Cofre de la raid" in the client's catalogue.</summary>
        public const int NpcId = 7861;

        /// <summary>"*emite una extraña vibración*", the first screen and the ambient bubble.</summary>
        public const long Vibrating = 59741;

        /// <summary>The second screen: taking the chest ends the raid for the whole team.</summary>
        public const long Warning = 60312;

        /// <summary>"Soltar todos los tesoros."</summary>
        public const long DropTreasures = 81757;

        /// <summary>"Acercarte al cofre a pesar de la sombra que acecha." — opens the warning.</summary>
        public const long Approach = 81758;

        /// <summary>"Retroceder.", on the first screen.</summary>
        public const long StepBack = 83105;

        /// <summary>"Tomar el cofre y escapar." — the raid ends here.</summary>
        public const long TakeAndRun = 82946;

        /// <summary>"Retroceder.", on the warning.</summary>
        public const long StepBackFromWarning = 83107;

        /// <summary>
        /// How full the chest looks, and from what score.
        /// </summary>
        /// <remarks>
        /// Straight off the template's own look, which is five variants with a criterion each:
        /// <c>{10152|||95$1;0;0;RV&lt;7,Raid_Score,5000}</c>, then 4999&lt;score&lt;13000, then
        /// &lt;27000, then &lt;45000, then the rest. The bones are written down because they ARE the
        /// data; what the emulator does with them is pick the variant whose criterion holds.
        /// </remarks>
        public static readonly IReadOnlyList<(long From, long Bones)> Tiers = new[]
        {
            (0L, 10152L),
            (5_000L, 10151L),
            (13_000L, 10150L),
            (27_000L, 10149L),
            (45_000L, 10148L),
        };

        /// <summary>Which of the five the chest is at, counting from zero.</summary>
        public static int TierOf(long score)
        {
            int tier = 0;
            for (int i = 0; i < Tiers.Count; i++)
            {
                if (score >= Tiers[i].From) tier = i;
            }

            return tier;
        }

        /// <summary>
        /// The first screen. Emptying your bag into it is only offered when there is something to
        /// empty, for the same reason as at the luminomachine: a button that cannot work is worse
        /// than no button.
        /// </summary>
        public static IReadOnlyList<long> FirstReplies(bool carrying)
        {
            var replies = new List<long>();
            if (carrying) replies.Add(DropTreasures);
            replies.Add(Approach);
            replies.Add(StepBack);
            return replies;
        }

        /// <summary>The warning screen: take it and end the raid, or back away.</summary>
        /// <remarks>
        /// Anybody may take it, not only the captain, and that is a reading of the data rather than
        /// a rule of ours: the warning -- "asegúrate de que todos los aventureros puedan unirse a
        /// ti" -- is written for a person who is about to end twelve people's raid, and it would
        /// not need writing if only the captain could.
        /// </remarks>
        public static IReadOnlyList<long> WarningReplies() => new[] { TakeAndRun, StepBackFromWarning };

        /// <summary>Whether a reply is one of the chest's.</summary>
        public static bool Owns(long reply)
            => reply == DropTreasures || reply == Approach || reply == StepBack ||
               reply == TakeAndRun || reply == StepBackFromWarning;
    }
}
