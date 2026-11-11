using System;
using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The repeatable quests (the client's repeatType): daily ones come back every day at 7:00,
    /// weekly ones every Tuesday at 7:00, as Dofus pour les noobs says.
    /// </summary>
    public class QuestRepeatTests
    {
        private static bool Available => File.Exists(Paths.QuestsJson);

        [Theory]
        [InlineData("2026-11-11 06:59", "2026-11-10 07:00")]
        [InlineData("2026-11-11 07:00", "2026-11-11 07:00")]
        [InlineData("2026-11-11 23:30", "2026-11-11 07:00")]
        public void A_day_turns_at_seven(string now, string turned)
            => Assert.Equal(DateTime.Parse(turned), QuestResets.Daily(DateTime.Parse(now)));

        [Theory]
        [InlineData("2026-11-11 12:00", "2026-11-10 07:00")]   // a Wednesday: the Tuesday before
        [InlineData("2026-11-10 06:00", "2026-11-03 07:00")]   // a Tuesday before seven: last week's
        [InlineData("2026-11-10 07:00", "2026-11-10 07:00")]
        [InlineData("2026-11-16 22:00", "2026-11-10 07:00")]   // the Monday after
        public void A_week_turns_on_tuesday_at_seven(string now, string turned)
            => Assert.Equal(DateTime.Parse(turned), QuestResets.Weekly(DateTime.Parse(now)));

        [Fact]
        public void A_year_turns_on_new_years_day_at_seven()
        {
            Assert.Equal(DateTime.Parse("2026-01-01 07:00"), QuestResets.Yearly(DateTime.Parse("2026-11-11 12:00")));
            Assert.Equal(DateTime.Parse("2025-01-01 07:00"), QuestResets.Yearly(DateTime.Parse("2026-01-01 06:00")));
        }

        [Fact]
        public void A_daily_quest_opens_again_the_next_day()
        {
            var quest = new Quest { Id = 1, RepeatType = 1, Repeatable = true };
            var done = DateTime.Parse("2026-11-11 08:00");
            Assert.False(quest.OpensAgain(done, DateTime.Parse("2026-11-11 22:00")));
            Assert.False(quest.OpensAgain(done, DateTime.Parse("2026-11-12 06:59")));
            Assert.True(quest.OpensAgain(done, DateTime.Parse("2026-11-12 07:00")));
            Assert.True(quest.OpensAgain(default, DateTime.Parse("2026-11-11 22:00")));   // its end unknown
        }

        [Fact]
        public void A_weekly_quest_opens_again_the_next_tuesday()
        {
            var quest = new Quest { Id = 1, RepeatType = 2, Repeatable = true };
            var done = DateTime.Parse("2026-11-10 09:00");   // a Tuesday
            Assert.False(quest.OpensAgain(done, DateTime.Parse("2026-11-16 23:00")));
            Assert.True(quest.OpensAgain(done, DateTime.Parse("2026-11-17 07:00")));
        }

        [Fact]
        public void A_quest_that_does_not_repeat_never_opens_again()
            => Assert.False(new Quest { Id = 1 }.OpensAgain(default, DateTime.Now));

        /// <summary>«Caza de dopeul ocra» (458) is daily: done today, it waits for tomorrow.</summary>
        [Fact]
        public void The_dopeul_hunt_comes_back_the_day_after()
        {
            if (!Available) return;

            var book = new QuestCatalogue();
            Assert.Equal(1, book.Of(458)!.RepeatType);
            var log = new QuestLog(book, () => 50, () => 0);

            Assert.NotNull(log.Start(458));
            Assert.True(log.FinishStep(458).QuestFinished);
            Assert.False(log.CanStart(458, out _));

            log.Restore(458, 0, "", true, QuestResets.Daily(DateTime.Now).AddMinutes(-1));
            Assert.True(log.CanStart(458, out _));
        }

        /// <summary>
        /// «Uno contra diecinueve» (470) opens before a temple's master with his hunt under way
        /// (Qo=3147, in hand) or done (Qc=458), as «Luchar contra todos los dopeuls» starts both.
        /// </summary>
        [Fact]
        public void The_tour_opens_with_the_hunt_in_hand()
        {
            if (!Available) return;

            var book = new QuestCatalogue();
            long map = 183765002;   // the ocras' dopeul master
            var log = new QuestLog(book, () => 50, () => map);
            Assert.False(log.CanStart(470, out _));
            Assert.NotNull(log.Start(458));
            Assert.True(log.ObjectivePending(3147));
            Assert.True(log.CanStart(470, out _));

            map = 183766020;   // another temple, whose hunt is not in hand
            Assert.False(log.CanStart(470, out _));
        }
    }
}
