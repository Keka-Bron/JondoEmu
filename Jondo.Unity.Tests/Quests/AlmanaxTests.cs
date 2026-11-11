using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Almanax;
using Jondo.Unity.World.Quests;
using Xunit;
using AlmanaxManager = Jondo.Unity.Server.Managers.Almanax;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// The Almanax calendar and its offering quests, from the client's data. INFERRED as a whole:
    /// no capture visits the sanctuary. What is tested is that the data is read the way it is
    /// written, and that the quest engine runs the offering.
    /// </summary>
    [Collection(AlmanaxClock)]
    public class AlmanaxTests
    {
        /// <summary>
        /// The tests that move the Almanax's clock run one at a time: the clock and the calendar
        /// are the server's, shared by every test in the run.
        /// </summary>
        public const string AlmanaxClock = "Almanax clock";

        private static bool Available => File.Exists(Paths.AlmanaxJson) && File.Exists(Paths.QuestsJson);

        [Fact]
        public void Entry_calendar_uses_the_live_server_day_across_midnight_and_omits_an_unloaded_calendar()
        {
            if (!Available) return;
            var previousClock = AlmanaxManager.Clock;
            try
            {
                AlmanaxManager.Reset();
                Assert.Null(Jondo.Unity.Server.Network.EntryEnvironmentMessages.Live.Calendar());
                AlmanaxManager.Clock = () => new DateTime(2026, 9, 18, 12, 0, 0);
                AlmanaxManager.Load();
                Assert.Equal(new Jondo.Unity.Protocol.Game.AlmanachCalendarDateEvent(3),
                    Jondo.Unity.Server.Network.EntryEnvironmentMessages.Live.Calendar());
                AlmanaxManager.Clock = () => new DateTime(2026, 9, 25, 23, 59, 59);
                Assert.NotEqual(new Jondo.Unity.Protocol.Game.AlmanachCalendarDateEvent(11),
                    Jondo.Unity.Server.Network.EntryEnvironmentMessages.Live.Calendar());
                AlmanaxManager.Clock = () => new DateTime(2026, 9, 26, 0, 0, 0);
                Assert.Equal(new Jondo.Unity.Protocol.Game.AlmanachCalendarDateEvent(11),
                    Jondo.Unity.Server.Network.EntryEnvironmentMessages.Live.Calendar());
            }
            finally { AlmanaxManager.Clock = previousClock; AlmanaxManager.Reset(); }
        }

        [Fact]
        public void The_calendar_holds_every_day_with_its_quest()
        {
            if (!Available) return;

            var calendar = new AlmanaxCalendar();
            Assert.Equal(376, calendar.Count);

            // Every day of a year resolves to exactly one entry, and every entry has a quest.
            for (var day = new DateTime(2026, 1, 1); day.Year == 2026; day = day.AddDays(1))
            {
                var entry = calendar.DayOf(day);
                Assert.NotNull(entry);
                Assert.NotEqual(0, entry!.QuestId);
            }
        }

        [Fact]
        public void The_month_s_saint_wins_over_the_stand_in()
        {
            if (!Available) return;

            // 18 September is named by Guidys (entry 3, that day alone) and by Bryss (entry 2, 33
            // days, "stands in when the awaited saint does not come"). Guidys is the saint.
            var calendar = new AlmanaxCalendar();
            Assert.Equal(3, calendar.DayOf(new DateTime(2026, 9, 18))!.Id);
            Assert.Equal(11, calendar.DayOf(new DateTime(2026, 9, 26))!.Id);
        }

        [Fact]
        public void A_moveable_feast_wins_on_its_year_s_date()
        {
            if (!Available) return;

            // Entry 47 names 12/10/2026 with its year; the month's saint names 12/10 of any year.
            var calendar = new AlmanaxCalendar();
            var feast = calendar.DayOf(new DateTime(2026, 10, 12));
            Assert.Equal(47, feast!.Id);
            Assert.NotEqual(47, calendar.DayOf(new DateTime(2027, 10, 12))!.Id);
        }

        [Fact]
        public void Today_s_offering_can_be_taken_today_and_not_tomorrow()
        {
            if (!Available) return;

            // Quest 965, "PL>19&Ad=11": the offering of 26 September.
            var calendar = new AlmanaxCalendar();
            var book = new QuestCatalogue();
            var today = new DateTime(2026, 9, 26);

            var log = new QuestLog(book, () => 50, () => 101450251,
                op => op == "Ad" ? calendar.DayOf(today)!.Id : null);
            Assert.True(log.CanStart(965, out var verdict));
            Assert.True(verdict.FullyJudged);

            var tomorrow = new QuestLog(book, () => 50, () => 101450251,
                op => op == "Ad" ? calendar.DayOf(today.AddDays(1))!.Id : null);
            Assert.False(tomorrow.CanStart(965, out _));

            var tooLow = new QuestLog(book, () => 19, () => 101450251,
                op => op == "Ad" ? calendar.DayOf(today)!.Id : null);
            Assert.False(tooLow.CanStart(965, out _));
        }

        [Fact]
        public void Only_the_reward_of_the_character_s_bracket_is_paid()
        {
            if (!Available) return;

            // Step 1583 lists ten rewards, from level 9-29 to 190-200. Paid together they would
            // be every bracket's Almanax items at once.
            var step = new QuestCatalogue().Step(1583);
            Assert.NotNull(step);
            Assert.Equal(10, step!.Rewards.Count);

            foreach (int level in new[] { 20, 45, 100, 199, 200 })
            {
                Assert.Single(step.Rewards, r => r.For(level));
            }

            Assert.Equal(0.5, step.Duration);
            Assert.Equal(200, step.OptimalLevel);
        }

        [Fact]
        public void Only_the_unconditional_bonuses_are_applied_and_only_on_their_day()
        {
            if (!Available) return;

            var clock = AlmanaxManager.Clock;
            try
            {
                AlmanaxManager.Load();

                // 22 May: "La ganancia de experiencia aumenta un 100% para todas la misiones".
                AlmanaxManager.Clock = () => new DateTime(2026, 5, 22);
                Assert.Equal(100, AlmanaxManager.BonusPercent(AlmanaxManager.BonusType.QuestExperience));
                Assert.Equal(282, AlmanaxManager.WithBonus(AlmanaxManager.BonusType.QuestExperience, 141));
                Assert.Equal(0, AlmanaxManager.BonusPercent(AlmanaxManager.BonusType.QuestKamas));

                // 26 April: the quests' kamas instead.
                AlmanaxManager.Clock = () => new DateTime(2026, 4, 26);
                Assert.Equal(100, AlmanaxManager.BonusPercent(AlmanaxManager.BonusType.QuestKamas));

                // 26 September: bonuses with conditions only, and none of them is applied.
                AlmanaxManager.Clock = () => new DateTime(2026, 9, 26);
                Assert.Equal(141, AlmanaxManager.WithBonus(AlmanaxManager.BonusType.QuestExperience, 141));
                Assert.Equal(20, AlmanaxManager.WithBonus(AlmanaxManager.BonusType.JobExperience, 20));
            }
            finally
            {
                AlmanaxManager.Clock = clock;
                AlmanaxManager.Reset();
            }
        }

        [Fact]
        public void The_offering_is_a_quest_whose_objectives_the_engine_closes()
        {
            if (!Available) return;

            // Bring the offering to Ontoral (type 3), pray (free text, the client reports it),
            // see the saint (type 1), back to Ontoral (type 9).
            var step = new QuestCatalogue().Step(1583)!;
            Assert.Equal(new[] { 3, 0, 1, 9 }, step.Objectives.Select(o => o.TypeId));
            Assert.Equal(1625, step.Objectives[0].NpcId);
            Assert.True(step.Objectives[0].ConsumesItems);
            Assert.Equal(1625, step.Objectives[3].NpcId);
        }

        private static AlmanaxBonus Bonus(int type, params (int Kind, int Value)[] criteria)
            => new AlmanaxBonus { Type = type, Amount = 50, Criteria = criteria };

        private static AlmanaxManager.Where At(int area = -1, int subarea = 0, int[]? monsters = null, int[]? races = null,
                                               int[]? worn = null, bool dungeon = false)
            => new(area, subarea, monsters ?? Array.Empty<int>(), races ?? Array.Empty<int>(), worn ?? Array.Empty<int>(), dungeon);

        /// <summary>
        /// The conditions as the days' descriptions word them: a race with its archmonsters is one
        /// condition, kinds add up, a job and its skills, and what no anomaly here can meet.
        /// </summary>
        [Fact]
        public void A_bonus_s_conditions_are_read_as_its_day_words_them()
        {
            var cuerboks = Bonus(1, (4, 51), (3, 2437));   // «criaturas de tipo cuerboks», with 2437
            Assert.True(AlmanaxManager.Fits(cuerboks, At(races: new[] { 51 })));
            Assert.True(AlmanaxManager.Fits(cuerboks, At(monsters: new[] { 2437 })));
            Assert.False(AlmanaxManager.Fits(cuerboks, At(monsters: new[] { 31 }, races: new[] { 3 })));

            Assert.True(AlmanaxManager.Fits(Bonus(1, (2, 1030)), At(subarea: 1030)));          // la Cámara de los Maleficios
            Assert.True(AlmanaxManager.Fits(Bonus(16, (1, 53), (1, 54), (1, 55)), At(area: 54)));  // dimensiones divinas
            Assert.False(AlmanaxManager.Fits(Bonus(16, (2, 210), (4, 9)), At(subarea: 210)));       // both kinds, or nothing
            Assert.True(AlmanaxManager.Fits(Bonus(16, (2, 210), (4, 9)), At(subarea: 210, races: new[] { 9 })));
            Assert.True(AlmanaxManager.Fits(Bonus(1, (7, 12035)), At(worn: new[] { 12035 })));
            Assert.True(AlmanaxManager.Fits(Bonus(35, (15, 1)), At(dungeon: true)));
            Assert.False(AlmanaxManager.Fits(Bonus(1, (13, 1)), At(subarea: 166)));             // no anomaly here
            Assert.True(AlmanaxManager.Fits(Bonus(1), At()));                                   // no condition at all

            Assert.True(AlmanaxManager.Fits(Bonus(7, (5, 36)), AlmanaxManager.Where.AtWork(36)));      // los pescadores
            Assert.True(AlmanaxManager.Fits(Bonus(7, (10, 52), (10, 159)), AlmanaxManager.Where.AtWork(28, 52)));   // centeno
            Assert.False(AlmanaxManager.Fits(Bonus(7, (10, 52)), AlmanaxManager.Where.AtWork(28, 46)));
        }

        /// <summary>«Ahorran un 15%», «un 25% de posibilidades»: each unit its own chance.</summary>
        [Fact]
        public void Chances_are_drawn_unit_by_unit()
        {
            var dice = new Random(7);
            Assert.Equal(0, AlmanaxManager.ChancesMet(100, 0, dice));
            Assert.Equal(100, AlmanaxManager.ChancesMet(100, 100, dice));
            int some = AlmanaxManager.ChancesMet(1000, 25, dice);
            Assert.InRange(some, 180, 320);
        }
    }
}
