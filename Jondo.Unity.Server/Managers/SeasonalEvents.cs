using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The quests' «Sc» (the client's StaticCriterionCriterion): a switch only the real server
    /// judges and no data of the client describes. 196 of the 1,976 quests carry one.
    /// </summary>
    /// <remarks>
    /// Read off the quests each value guards and what Dofus pour les noobs says of them (22/10/2026):
    /// most are content that is simply on -- 703 Frigost's quests and wanted posters (« On recherche
    /// le Comte Harebourg »), 702 the other Frigost ones (« On recherche Fuji Givrefoux »), 835 and
    /// 822 more wanted posters, 940 the Ankama building's quests (« Le monde à l'envers »), 842 the
    /// Crocabulia line, 993 « Le divin enfant », once only for the Goultard manga's buyers and now
    /// for everybody... -- and five are the yearly events, whose quests exist only while they run:
    /// <code>
    ///   967 Halouine        « Des bonbons ou un sort »: two weeks at the end of October, Tuesday to Tuesday
    ///   982 Sain Ballotin   « Cœur brisé »: the week of 14 February (2026: Tuesday 10 to Tuesday 17)
    ///   945 Île de Pwâk     « La crise de foi »: four weeks before May (2026: 31 March to 28 April)
    ///   979 Vulkania        « Premier Contact », « Prise de sang »: 1 July to 1 September
    ///   953 Fête du Ripate  « Chasse au trésor des pirates »: two weeks around 19 September
    /// </code>
    /// Every other value is on.
    /// </remarks>
    public static class SeasonalEvents
    {
        public const int Halouine = 967, SainBallotin = 982, Pwak = 945, Vulkania = 979, Ripate = 953;

        /// <summary>Each event's days in a year: [start, end).</summary>
        private static readonly Dictionary<int, Func<int, (DateTime Start, DateTime End)>> Windows = new()
        {
            [Halouine] = year => Weeks(TuesdayOnOrBefore(new DateTime(year, 10, 21)), 2),
            [SainBallotin] = year => Weeks(TuesdayOnOrBefore(new DateTime(year, 2, 14)), 1),
            [Pwak] = year => Weeks(TuesdayOnOrBefore(new DateTime(year, 4, 1)), 4),
            [Vulkania] = year => (new DateTime(year, 7, 1), new DateTime(year, 9, 1)),
            [Ripate] = year => Weeks(TuesdayOnOrBefore(new DateTime(year, 9, 19)).AddDays(-7), 2),
        };

        private static DateTime TuesdayOnOrBefore(DateTime day)
            => day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7));

        private static (DateTime, DateTime) Weeks(DateTime start, int weeks) => (start, start.AddDays(7 * weeks));

        /// <summary>Whether that «Sc» is met on that day: an event while it runs, anything else always.</summary>
        public static bool IsOn(int criterion, DateTime now)
        {
            if (!Windows.TryGetValue(criterion, out var window)) return true;
            var (start, end) = window(now.Year);
            return now.Date >= start && now.Date < end;
        }

        /// <summary>Whether that value is one of the yearly events.</summary>
        public static bool IsEvent(int criterion) => Windows.ContainsKey(criterion);

        /// <summary>
        /// Whether that value's meaning is known: a yearly event, or a switch some quest's criterion
        /// carries -- what the «on» above was read off. The monsters' loot asks only these; the
        /// values only it carries (the bosses' shields' 3000 to 3069, the kings' cake's 947, 629)
        /// are nobody's to guess.
        /// </summary>
        public static bool IsKnown(int criterion) => IsEvent(criterion) || QuestSwitches().Contains(criterion);

        private static HashSet<int>? _questSwitches;

        private static HashSet<int> QuestSwitches()
        {
            if (_questSwitches != null) return _questSwitches;
            var book = Quests.Book;
            if (book == null) return new HashSet<int>();   // not read yet: none known, and asked again
            var switches = new HashSet<int>();
            foreach (var quest in book.All())
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(quest.Criterion, @"Sc[=!](\d+)"))
                    switches.Add(int.Parse(m.Groups[1].Value));
            return _questSwitches = switches;
        }
    }
}
