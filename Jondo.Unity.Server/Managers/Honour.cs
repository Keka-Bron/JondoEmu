using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Honour and dishonour: what an aggression between the two cities is worth, and the grade --
    /// the wings -- the honour gives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client keeps the pieces and none of the rules: the alignment tab's honour bar
    /// (AlignmentUi, lbl_honourValue over lbl_honourMax, the sheet's alignment block f3 { f1
    /// honour, f3 the next grade's, f4 this grade's }), the wings by grade, the end screen's
    /// honour column for the fights it calls honour fights -- types 1 and 2, the aggression first
    /// (FightResultCharacterTab.m_isHonorFight) -- and its messages: «Has ganado {0} puntos de
    /// honor» (info 74), «Tu derrota te ha hecho perder {0} puntos de honor» (81), «Este
    /// humillante combate te ha sancionado con {0} punto(s) de deshonor» (84), «Has limpiado tu
    /// deshonor con {0} punto(s) al demostrar tu valor» (85), «Tu presencia en este territorio no
    /// te ha proporcionado honor esta vez. Hace poco luchaste con honra.» (155), and the loading
    /// tip «Tu personaje gana puntos de honor solo en los combates JcJ equilibrados».
    /// </para>
    /// <para>
    /// The grades are the old game's: ten of them, the first at 0 honour and the last at 17 500, out
    /// of 20 000. The rest is this server's, written down here as rules:
    /// </para>
    /// <list type="bullet">
    /// <item>Only a balanced fight gives honour: the losers' levels, together, at least half the
    /// winners'. A fight won against much less is a humiliation, and the attackers who won it get
    /// dishonour, one to three points by how lopsided it was.</item>
    /// <item>A balanced win is worth a hundred honour, more the stronger the losers were against
    /// the winners -- their levels, how many they were, their grades -- between 10 and 300. Each
    /// loser loses six tenths of what the winners got on average.</item>
    /// <item>The same loser beaten again within the hour gives nothing (155).</item>
    /// <item>Dishonour is paid off before honour is earned: a dishonoured winner of a balanced
    /// fight cleans a point instead (85).</item>
    /// </list>
    /// </remarks>
    public static class Honour
    {
        /// <summary>The honour the bar is drawn out of.</summary>
        public const int Most = 20000;

        /// <summary>The honour each of the ten grades starts at.</summary>
        public static readonly int[] GradeFloors = { 0, 500, 1500, 3000, 5000, 7500, 10000, 12500, 15000, 17500 };

        /// <summary>The losers' levels against the winners' under which a win is a humiliation.</summary>
        public const double Balanced = 0.5;

        /// <summary>What a balanced win against an equal is worth, and its bounds.</summary>
        public const int Base = 100, Least = 10, Utmost = 300;

        /// <summary>The share of the winners' gain each loser loses.</summary>
        public const double LoserShare = 0.6;

        /// <summary>How long the same loser gives nothing again.</summary>
        public static readonly TimeSpan Lately = TimeSpan.FromHours(1);

        // The client's information messages (type 0), by their text.
        public const int Gained = 74, DishonourGained = 75, Lost = 76, DishonourLost = 77,
                         ValourReward = 80, DefeatLost = 81, Humiliation = 84, Cleaned = 85, NotThisTime = 155;

        /// <summary>The grade, 1 to 10, an honour gives.</summary>
        public static int GradeOf(int honour)
        {
            int grade = 1;
            for (int i = 0; i < GradeFloors.Length; i++) if (honour >= GradeFloors[i]) grade = i + 1;
            return grade;
        }

        /// <summary>Where a grade's bar starts.</summary>
        public static int FloorOf(int grade) => GradeFloors[Math.Clamp(grade, 1, GradeFloors.Length) - 1];

        /// <summary>Where it ends: the next grade's start, or the most for the last.</summary>
        public static int CeilingOf(int grade) => grade >= GradeFloors.Length ? Most : GradeFloors[Math.Max(grade, 1)];

        /// <summary>One who fought: his level, his city, his honour and dishonour as they stood.</summary>
        public sealed record Fighter(long Id, int Level, int Side, int Honour, int Dishonour);

        /// <summary>What the fight did to one of them, and the message that says so.</summary>
        public sealed record Change(long Id, int Honour, int Dishonour, int Message, int Amount);

        /// <summary>
        /// What an aggression leaves each of its sworn fighters, by the rules above.
        /// <paramref name="beatenLately"/> says whether a winner beat that loser within the hour.
        /// </summary>
        public static List<Change> Settle(IReadOnlyList<Fighter> winners, IReadOnlyList<Fighter> losers,
                                          bool attackersWon, Func<long, long, bool> beatenLately)
        {
            var changes = new List<Change>();
            if (winners.Count == 0 || losers.Count == 0) return changes;

            double strongest = winners.Sum(w => Math.Min(w.Level, 200));
            double weakest = losers.Sum(l => Math.Min(l.Level, 200));
            double ratio = strongest <= 0 ? 0 : weakest / strongest;

            if (ratio < Balanced)
            {
                // A humiliation: nothing is won, and whoever started it is ashamed of it.
                if (attackersWon)
                {
                    int shame = ratio < 0.25 ? 3 : ratio < 0.4 ? 2 : 1;
                    foreach (var winner in winners) changes.Add(new Change(winner.Id, 0, shame, Humiliation, shame));
                }
                return changes;
            }

            double numbers = Math.Clamp(losers.Count / (double)winners.Count, 0.5, 2.0);
            double losersGrade = losers.Average(l => GradeOf(l.Honour));
            var gains = new List<int>();
            foreach (var winner in winners)
            {
                if (losers.All(l => beatenLately(winner.Id, l.Id)))
                {
                    changes.Add(new Change(winner.Id, 0, 0, NotThisTime, 0));
                    continue;
                }
                double grades = Math.Clamp(1 + 0.1 * (losersGrade - GradeOf(winner.Honour)), 0.5, 2.0);
                int gain = (int)Math.Round(Math.Clamp(Base * Math.Min(ratio, 2.0) * numbers * grades, Least, Utmost));
                gains.Add(gain);
                if (winner.Dishonour > 0) changes.Add(new Change(winner.Id, 0, -1, Cleaned, 1));
                else changes.Add(new Change(winner.Id, Math.Min(gain, Most - winner.Honour), 0, Gained, Math.Min(gain, Most - winner.Honour)));
            }

            if (gains.Count == 0) return changes;
            int loss = (int)Math.Round(gains.Average() * LoserShare);
            foreach (var loser in losers)
            {
                int lost = Math.Min(loss, loser.Honour);
                if (lost > 0) changes.Add(new Change(loser.Id, -lost, 0, DefeatLost, lost));
            }
            return changes;
        }
    }
}
