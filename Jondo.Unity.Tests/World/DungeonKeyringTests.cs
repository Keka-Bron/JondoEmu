using System;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The keyring's free entry: one per dungeon per week, back on Tuesdays.
    /// </summary>
    /// <remarks>
    /// The rule is the client's own help text, translation 1189621: "usar el manojo de llaves para
    /// beneficiarse de una entrada gratis en cada mazmorra, una vez por semana [...] El manojo de
    /// llaves se reinicia todos los martes."
    ///
    /// These pin the week arithmetic, which is the part that is easy to write plausibly and wrong.
    /// A rolling seven days from the moment of use passes every casual reading of the sentence and
    /// is a different game: it would make Wednesday's entry come back on Wednesday instead of on
    /// the Tuesday five days later.
    /// </remarks>
    public class DungeonKeyringTests
    {
        // 2026-08-25 is a Tuesday. Everything below is anchored to it so the dates can be checked
        // against a calendar rather than against this code.
        private static readonly DateTime Tuesday = new DateTime(2026, 8, 25);

        [Fact]
        public void The_anchor_really_is_a_tuesday()
        {
            // Cheap, and it stops every other test in this class from being nonsense if the date
            // was mistyped.
            Assert.Equal(DayOfWeek.Tuesday, Tuesday.DayOfWeek);
            Assert.Equal(DayOfWeek.Tuesday, DungeonKeyring.ResetDay);
        }

        [Fact]
        public void A_tuesday_opens_its_own_week()
        {
            // Not the previous one. Tuesday at 00:00 is already the new week, so an entry used on
            // the Monday before does not count against it.
            Assert.Equal(Tuesday, DungeonKeyring.WeekOf(Tuesday));
            Assert.Equal(Tuesday, DungeonKeyring.WeekOf(Tuesday.AddHours(23).AddMinutes(59)));
        }

        [Theory]
        [InlineData(0)]   // tuesday
        [InlineData(1)]   // wednesday
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]   // monday
        public void Every_day_of_the_week_lands_on_the_same_tuesday(int daysIn)
        {
            Assert.Equal(Tuesday, DungeonKeyring.WeekOf(Tuesday.AddDays(daysIn)));
        }

        [Fact]
        public void The_next_tuesday_starts_a_new_week()
        {
            Assert.NotEqual(DungeonKeyring.WeekOf(Tuesday), DungeonKeyring.WeekOf(Tuesday.AddDays(7)));
            Assert.Equal(Tuesday.AddDays(7), DungeonKeyring.WeekOf(Tuesday.AddDays(7)));
        }

        [Fact]
        public void Monday_and_the_day_after_it_are_different_weeks()
        {
            // The whole point of a fixed weekday, stated as the case that a rolling seven-day
            // cooldown gets wrong: somebody who spends the entry on Monday has it back the very
            // next day, and that is correct.
            DateTime monday = Tuesday.AddDays(6);

            Assert.Equal(Tuesday, DungeonKeyring.WeekOf(monday));
            Assert.Equal(Tuesday.AddDays(7), DungeonKeyring.WeekOf(monday.AddDays(1)));
        }

        [Fact]
        public void A_week_is_written_down_as_the_date_of_its_tuesday()
        {
            Assert.Equal("2026-08-25", DungeonKeyring.WeekKey(Tuesday.AddDays(3)));
            Assert.Equal("2026-09-01", DungeonKeyring.WeekKey(Tuesday.AddDays(7)));
        }

        [Fact]
        public void The_reset_is_always_seven_days_after_the_week_started()
        {
            for (int day = 0; day < 7; day++)
            {
                DateTime now = Tuesday.AddDays(day).AddHours(13);
                Assert.Equal(Tuesday.AddDays(7), DungeonKeyring.NextReset(now));
            }
        }

        [Fact]
        public void The_year_boundary_is_not_a_special_case()
        {
            // Week arithmetic done with week-of-year numbers breaks here; this is done with dates,
            // so it should not. 2026-12-29 is a Tuesday.
            var lastOfYear = new DateTime(2026, 12, 29);
            Assert.Equal(DayOfWeek.Tuesday, lastOfYear.DayOfWeek);

            Assert.Equal(lastOfYear, DungeonKeyring.WeekOf(new DateTime(2027, 1, 3)));
            Assert.Equal(new DateTime(2027, 1, 5), DungeonKeyring.WeekOf(new DateTime(2027, 1, 5)));
        }

        [Fact]
        public void Nobody_without_a_character_or_a_dungeon_is_refused()
        {
            // Reached before a character is loaded, or with a map that is not a dungeon entrance.
            // Refusing there would be a lockout caused by bookkeeping.
            Assert.True(DungeonKeyring.FreeEntryLeft(0, 8, Tuesday));
            Assert.True(DungeonKeyring.FreeEntryLeft(13825558, 0, Tuesday));
        }
    }
}
