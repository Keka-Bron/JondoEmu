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
    /// Every other value is on. 980 guards the sea passage to Vulkania and its pier's element: it
    /// is Vulkania's, an alias of 979.
    ///
    /// Kwismas Island has no «Sc» in the client's data (its boat is the real server's NPC alone):
    /// <see cref="Kwismas"/> is this server's own value, written in content only -- Hector
    /// Kwismas's spawn at the Madrestam port and his reply that sails there.
    ///
    /// The operator holds an event open or shut from the server's Events page
    /// (<see cref="EventSettings"/>); left on Auto it follows the calendar below.
    /// </remarks>
    public static class SeasonalEvents
    {
        public const int Halouine = 967, SainBallotin = 982, Pwak = 945, Vulkania = 979, Ripate = 953;

        /// <summary>Kwismas Island's event: a value of this server's own, no client data carries it.</summary>
        public const int Kwismas = 90001;

        /// <summary>The sea passage to Vulkania's value, which is Vulkania's.</summary>
        public const int VulkaniaPassage = 980;

        /// <summary>The events, in the calendar's order, for the Events page.</summary>
        public static readonly int[] Events = { SainBallotin, Pwak, Vulkania, Ripate, Halouine, Kwismas };

        /// <summary>The value an alias stands for: 980 for 979; any other value itself.</summary>
        public static int Canonical(int criterion) => criterion == VulkaniaPassage ? Vulkania : criterion;

        /// <summary>An event's key, the one config\events.json and the window's texts use.</summary>
        public static string KeyOf(int criterion) => Canonical(criterion) switch
        {
            Kwismas => "kwismas",
            Halouine => "halouine",
            Vulkania => "vulkania",
            SainBallotin => "sainballotin",
            Pwak => "pwak",
            Ripate => "ripate",
            _ => criterion.ToString(),
        };

        /// <summary>Each event's days in a year: [start, end).</summary>
        private static readonly Dictionary<int, Func<int, (DateTime Start, DateTime End)>> Windows = new()
        {
            [Halouine] = year => Weeks(TuesdayOnOrBefore(new DateTime(year, 10, 21)), 2),
            [SainBallotin] = year => Weeks(TuesdayOnOrBefore(new DateTime(year, 2, 14)), 1),
            [Pwak] = year => Weeks(TuesdayOnOrBefore(new DateTime(year, 4, 1)), 4),
            [Vulkania] = year => (new DateTime(year, 7, 1), new DateTime(year, 9, 1)),
            [Ripate] = year => Weeks(TuesdayOnOrBefore(new DateTime(year, 9, 19)).AddDays(-7), 2),
            // Mid-December into January, as the island's events have run: across the new year.
            [Kwismas] = year => (new DateTime(year, 12, 15), new DateTime(year + 1, 2, 1)),
        };

        private static DateTime TuesdayOnOrBefore(DateTime day)
            => day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7));

        private static (DateTime, DateTime) Weeks(DateTime start, int weeks) => (start, start.AddDays(7 * weeks));

        /// <summary>
        /// Whether that «Sc» is met on that day: an event while it runs -- or as the operator holds
        /// it --, anything else always.
        /// </summary>
        public static bool IsOn(int criterion, DateTime now)
        {
            criterion = Canonical(criterion);
            if (ForcedOn.ContainsKey(criterion)) return true;
            if (!Windows.ContainsKey(criterion)) return true;
            switch (EventSettings.Current.ModeOf(criterion))
            {
                case EventMode.On: return true;
                case EventMode.Off: return false;
            }
            return OnTheCalendar(criterion, now);
        }

        /// <summary>Whether that event's days run on that day, whatever the operator holds.</summary>
        public static bool OnTheCalendar(int criterion, DateTime now)
        {
            if (!Windows.TryGetValue(Canonical(criterion), out var window)) return true;
            // This year's window, and last year's for one that runs across the new year.
            foreach (int year in new[] { now.Year, now.Year - 1 })
            {
                var (start, end) = window(year);
                if (now.Date >= start && now.Date < end) return true;
            }
            return false;
        }

        /// <summary>
        /// The event's days that run on that day, or the next ones to come: what the Events page
        /// shows beside each event.
        /// </summary>
        public static (DateTime Start, DateTime End) WindowAround(int criterion, DateTime now)
        {
            var window = Windows[Canonical(criterion)];
            foreach (int year in new[] { now.Year - 1, now.Year, now.Year + 1 })
            {
                var (start, end) = window(year);
                if (now.Date < end) return (start, end);
            }
            return window(now.Year + 1);
        }

        /// <summary>
        /// The events held open whatever the day, by the test API only (route «season»): Vulkania's
        /// quests are played in November that way. Empty in a normal run, and forgotten on restart.
        /// </summary>
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> ForcedOn = new();

        /// <summary>Whether that value is one of the yearly events.</summary>
        public static bool IsEvent(int criterion) => Windows.ContainsKey(Canonical(criterion));

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
