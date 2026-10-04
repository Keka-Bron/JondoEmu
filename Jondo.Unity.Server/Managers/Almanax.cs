using System;
using System.Collections.Generic;
using Jondo.Unity.World.Almanax;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Almanax: today's saint, today's offering, today's bonuses.
    /// </summary>
    /// <remarks>
    /// <b>INFERRED throughout.</b> No capture visits the sanctuary, so nothing here is checked
    /// against the real server's traffic. What is here is what the client's own data defines, run
    /// through the quest engine that already exists:
    ///
    /// <list type="bullet">
    ///   <item>Each of the 376 calendar entries leads to an ordinary quest, "Ofrenda para
    ///   &lt;saint&gt;", whose start condition is <c>PL&gt;19&amp;Ad=&lt;entry&gt;</c>. <c>Ad</c> is
    ///   answered here: the entry of today's date.</item>
    ///   <item>All 376 are handed out by Ontoral Zo (NPC 1625) in the sanctuary, map 101450251,
    ///   and none of their steps names a line of his to hand it over on. So the rule the quest
    ///   engine applies to an NPC with no written tree — the opening line hands over what can be
    ///   taken — is applied to him for today's offering.</item>
    ///   <item>The four objectives are the same for every day: bring the offering to Ontoral, pray
    ///   at the altar (free text, which the client reports), see the saint, go back to Ontoral.
    ///   The quest engine already closes each of those kinds.</item>
    ///   <item>An offering is made once a day: finishing it writes the day down, and the same
    ///   quest cannot be taken again that day.</item>
    /// </list>
    ///
    /// Of the day's bonuses, three kinds are applied when they come with no condition: job
    /// experience, quest experience and quest kamas (see <see cref="BonusType"/>). The rest — the
    /// experience and drops of monsters, harvest quantities, challenge bonuses — are read and
    /// named, not applied: they come with conditions whose types the client data does not
    /// explain, and applying them would mean guessing inside the fight code.
    /// </remarks>
    public static class Almanax
    {
        /// <summary>The operator the offering quests' start conditions use for the day.</summary>
        public const string DayOperator = "Ad";

        /// <summary>The tally kind the day of the last offering is written under.</summary>
        public const string OfferingKind = "Ax";

        /// <summary>
        /// Ontoral Zo, who hands out every one of the 376 offerings in the client's data. Not
        /// written here: whoever the offering quests themselves name as their giver, worked out
        /// the first time it is asked once both the calendar and the quests are loaded.
        /// </summary>
        public static int Giver
        {
            get
            {
                if (_giver == 0) _giver = FindGiver();
                return _giver;
            }
        }

        private static int _giver;

        private static int FindGiver()
        {
            var calendar = _calendar;
            var book = Quests.Book;
            if (calendar == null || !calendar.Ready || book == null) return 0;

            var givers = new Dictionary<int, int>();
            foreach (var quest in book.All())
            {
                if (!calendar.IsOffering(quest.Id)) continue;
                foreach (var giver in quest.Givers)
                {
                    givers.TryGetValue(giver.NpcId, out int n);
                    givers[giver.NpcId] = n + 1;
                }
            }

            int best = 0;
            foreach (var (npc, n) in givers)
            {
                if (best == 0 || n > givers[best]) best = npc;
            }

            return best;
        }

        private static volatile AlmanaxCalendar? _calendar;
        private static readonly object _loadLock = new object();

        public static AlmanaxCalendar? Calendar => _calendar;

        /// <summary>The server's clock. Replaceable so the tests can pick a day.</summary>
        public static Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        /// <summary>Reads the calendar. Once, at startup, after the quests.</summary>
        public static void Load()
        {
            if (_calendar != null) return;
            lock (_loadLock)
            {
                if (_calendar != null) return;
                var calendar = new AlmanaxCalendar(Console.WriteLine);
                _calendar = calendar;

                var today = Today();
                if (today != null)
                {
                    Console.WriteLine($"[Almanax] {calendar.Count} días en el calendario; hoy es el {today.Id}, " +
                                      $"ofrenda en la misión {today.QuestId}, santo {today.Saint}, " +
                                      $"{today.Bonuses.Count} bonus sin aplicar. La entrega Ontoral (NPC {Giver}).");
                }
            }
        }

        /// <summary>
        /// Puts the Almanax back to not loaded, for the tests: a calendar left behind by one test
        /// would put a day's bonus on the job experience another test measures.
        /// </summary>
        internal static void Reset()
        {
            lock (_loadLock)
            {
                _calendar = null;
                _giver = 0;
            }
        }

        /// <summary>Today's calendar entry, or null without a calendar.</summary>
        public static AlmanaxDay? Today() => _calendar?.DayOf(Clock());

        /// <summary>
        /// What <c>Ad</c> answers: today's entry. Null — the term left unjudged — without a calendar.
        /// </summary>
        public static long? Scalar(string op)
        {
            if (op != DayOperator) return null;
            var today = Today();
            return today?.Id;
        }

        /// <summary>
        /// The bonus types applied here, each named by the one day whose text says what it does
        /// and has no condition attached. The client data has only the number; the meaning is read
        /// off that day's own description.
        /// </summary>
        public static class BonusType
        {
            /// <summary>Day 129: "la ganancia de experiencia aumenta un 25% para todos los oficios".</summary>
            public const int JobExperience = 2;

            /// <summary>Day 277: "La ganancia de experiencia aumenta un 100% para todas la misiones".</summary>
            public const int QuestExperience = 14;

            /// <summary>Day 250: "La ganancia de kamas aumentará en un 100% en todas las misiones".</summary>
            public const int QuestKamas = 15;
        }

        /// <summary>
        /// Today's bonus of that type, in percent, counting only the ones with no condition.
        /// </summary>
        /// <remarks>
        /// A bonus with a condition — a monster race, a dungeon, a zone — is left out: what each
        /// condition type means is not in the client data either, and applying one on a guess
        /// would give the bonus where the game does not.
        /// </remarks>
        public static int BonusPercent(int type)
        {
            var today = Today();
            if (today == null) return 0;

            int percent = 0;
            foreach (var bonus in today.Bonuses)
            {
                if (bonus.Type == type && bonus.Criteria.Count == 0) percent += bonus.Amount;
            }

            return percent;
        }

        /// <summary>An amount with today's unconditional bonus of that type on top.</summary>
        public static long WithBonus(int type, long amount)
        {
            int percent = BonusPercent(type);
            return percent <= 0 || amount <= 0 ? amount : amount + amount * percent / 100;
        }

        /// <summary>Whether a quest is one of the Almanax offerings.</summary>
        public static bool IsOffering(int questId) => _calendar?.IsOffering(questId) ?? false;

        /// <summary>Today's offering quest, or zero.</summary>
        public static int TodaysOffering() => Today()?.QuestId ?? 0;

        /// <summary>The date as the tally keeps it, 20260926.</summary>
        public static long DayKey(DateTime date) => date.Year * 10000L + date.Month * 100 + date.Day;

        /// <summary>
        /// Whether this character has already made an offering today. The quests are repeatable,
        /// so without this the same one could be taken again straight after finishing it.
        /// </summary>
        public static bool OfferedToday(IReadOnlyDictionary<(string Kind, long Key), long> tallies, int questId)
            => tallies.TryGetValue((OfferingKind, questId), out long day) && day == DayKey(Clock());
    }
}
