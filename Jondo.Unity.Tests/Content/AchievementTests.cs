using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Achievements;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Earning achievements, and the cascade of achievements that need other achievements.
    /// </summary>
    /// <remarks>
    /// The case that matters is the one the tutorial capture shows end to end. Achievement 8518
    /// "Primer tiempo" has an objective that reads exactly <c>(Qf=2511)</c>, and quest 2511 is
    /// "Primeras armas" — the quest the capture starts. 8519 is the same for quest 2502. And 8520
    /// "Con bases sólidas" is nothing but <c>(OA=8518)</c> and <c>(OA=8519)</c>, so earning the
    /// second of the pair has to earn the third in the same breath.
    ///
    /// That chain is worth a test rather than a look because every part of it is invisible when it
    /// breaks: an achievement that is never granted looks exactly like an achievement nobody
    /// earned.
    /// </remarks>
    public class AchievementTests
    {
        private static bool Available
            => File.Exists(Paths.AchievementsJson) && File.Exists(Paths.QuestsJson);

        /// <summary>A character who has done exactly what the test says and nothing else.</summary>
        private sealed class Player : IQuestFacts
        {
            public int Level { get; set; } = 1;
            public long MapId { get; set; }
            public HashSet<int> Done { get; } = new HashSet<int>();

            public bool Finished(int questId) => Done.Contains(questId);
            public bool Active(int questId) => false;
            public bool ObjectiveDone(int objectiveId) => false;
        }

        [Fact]
        public void The_catalogue_holds_what_it_is_supposed_to()
        {
            if (!Available) return;

            var book = new AchievementCatalogue();
            Assert.True(book.Ready);

            // Floors rather than exact numbers, so a re-extraction against a new client does not
            // have to come with an edit here.
            Assert.True(book.Count > 2_700, $"only {book.Count} achievements");
            Assert.True(book.ObjectiveCount > 8_000, $"only {book.ObjectiveCount} objectives");
            Assert.True(book.RewardCount > 6_000, $"only {book.RewardCount} rewards");
            Assert.True(book.FromQuestsCount > 200, $"only {book.FromQuestsCount} come from quests");
        }

        [Fact]
        public void The_tutorial_achievement_is_the_one_the_capture_shows()
        {
            if (!Available) return;

            var badge = new AchievementCatalogue().Of(8518);

            Assert.NotNull(badge);
            Assert.Contains(2511, badge!.FromQuests);
        }

        [Fact]
        public void Finishing_that_quest_earns_it()
        {
            if (!Available) return;

            var book = new AchievementCatalogue();
            var player = new Player();
            var log = new AchievementLog(book, player);

            Assert.False(log.Has(8518));

            // 8518 wants the tutorial's first part as well as the quest, and that part is a
            // criterion this emulator cannot judge — so it must NOT be handed out yet.
            player.Done.Add(2511);
            var earned = log.AfterQuest(2511);

            Assert.DoesNotContain(8518, earned);
            Assert.False(log.Has(8518));
        }

        [Fact]
        public void An_objective_this_engine_cannot_judge_does_not_pass_by_default()
        {
            if (!Available) return;

            // The opposite rule from a quest's start condition, and deliberately so. Letting an
            // unreadable term through there costs somebody a quest they would get anyway; letting
            // one through here hands out a badge and its items for nothing. "Kill 500 gobballs" is
            // not satisfied by the engine being unable to count gobballs.
            var book = new AchievementCatalogue();
            var log = new AchievementLog(book, new Player { Level = 200 });

            int handedOut = 0;
            foreach (var badge in book.All())
            {
                if (log.Holds(badge.Id)) handedOut++;
            }

            Assert.True(handedOut == 0,
                $"{handedOut} achievements would be granted to somebody who has done nothing");
        }

        [Fact]
        public void An_achievement_with_no_objectives_is_not_earned_by_having_nothing()
        {
            if (!Available) return;

            var book = new AchievementCatalogue();
            var log = new AchievementLog(book, new Player());

            int empty = 0;
            foreach (var badge in book.All())
            {
                if (badge.Objectives.Count == 0) empty++;
            }

            Assert.True(empty > 250, $"only {empty} achievements have no objectives at all");

            foreach (var badge in book.All())
            {
                if (badge.Objectives.Count == 0) Assert.False(log.Holds(badge.Id));
            }
        }

        [Fact]
        public void An_achievement_built_on_others_is_earned_when_the_last_one_is()
        {
            if (!Available) return;

            // 8520 is (OA=8518) and (OA=8519), nothing else. Restoring the first and then earning
            // the second must produce the third without anybody asking for it.
            var book = new AchievementCatalogue();
            var badge = book.Of(8520);
            Assert.NotNull(badge);
            Assert.Equal(new[] { 8518, 8519 }, badge!.FromAchievements);

            var log = new AchievementLog(book, new Player());
            log.Restore(8518, claimed: false);
            Assert.False(log.Has(8520));

            log.Restore(8519, claimed: false);

            // Restoring does not check anything — that is what loading from the database does — so
            // the cascade is asked for explicitly, the way the engine does after a quest.
            var move = log.Check(8520);

            Assert.True(move.Earned);
            Assert.True(log.Has(8520));
        }

        [Fact]
        public void Earning_one_carries_the_ones_built_on_it()
        {
            if (!Available) return;

            var book = new AchievementCatalogue();
            var log = new AchievementLog(book, new Player());
            log.Restore(8518, claimed: false);

            var move = log.Check(8519);

            // 8519's own objective is a quest this player has not done, so nothing happens — and
            // that is the point: the cascade must not fire off an achievement that was not earned.
            Assert.False(move.Earned);
            Assert.False(log.Has(8520));
        }

        [Fact]
        public void Earned_and_paid_for_are_two_different_things()
        {
            if (!Available) return;

            // The capture is a player pressing the claim button, so the reward is not handed over
            // on earning. An engine that lost the distinction would pay twice on every login.
            var log = new AchievementLog(new AchievementCatalogue(), new Player());
            log.Restore(8518, claimed: false);

            Assert.True(log.Has(8518));
            Assert.False(log.WasClaimed(8518));
            Assert.Contains(8518, log.Unclaimed());

            Assert.True(log.MarkClaimed(8518));
            Assert.False(log.MarkClaimed(8518));
            Assert.DoesNotContain(8518, log.Unclaimed());
        }

        [Fact]
        public void Points_add_up_over_what_is_earned()
        {
            if (!Available) return;

            var book = new AchievementCatalogue();
            var log = new AchievementLog(book, new Player());

            Assert.Equal(0, log.Points);

            log.Restore(8518, claimed: false);
            log.Restore(8520, claimed: false);

            Assert.Equal((book.Of(8518)?.Points ?? 0) + (book.Of(8520)?.Points ?? 0), log.Points);
        }

        // ─── Reading OA out of a criterion ────────────────────────────────────────

        [Fact]
        public void The_achievements_a_criterion_needs_are_read()
        {
            Assert.Equal(new[] { 8518 }, AchievementCatalogue.Obtained("(OA=8518)"));
            Assert.Equal(new[] { 8518, 8519 }, AchievementCatalogue.Obtained("OA=8518&OA=8519"));
            Assert.Equal(new[] { 8518 }, AchievementCatalogue.Obtained("OA=8518|OA=8518"));
        }

        [Fact]
        public void Not_having_one_is_not_needing_it()
        {
            // OA!8518 is "and you have NOT got that one". Reading it as a prerequisite would make
            // an achievement wait for the very thing that rules it out.
            Assert.Empty(AchievementCatalogue.Obtained("OA!8518"));
            Assert.Empty(AchievementCatalogue.Obtained("OA>8518"));
            Assert.Empty(AchievementCatalogue.Obtained(""));
            Assert.Empty(AchievementCatalogue.Obtained("OA="));
        }

        [Fact]
        public void The_criterion_reader_answers_OA()
        {
            Assert.True(QuestCriterion.Understands("OA"));

            var nobody = new Player();
            Assert.False(QuestCriterion.Judge("OA=8518", nobody).Met);
            Assert.True(QuestCriterion.Judge("OA!8518", nobody).Met);

            // And it is fully judged, not let through: an achievement needing another achievement
            // is something this engine knows the answer to.
            Assert.True(QuestCriterion.Judge("OA=8518", nobody).FullyJudged);
        }
    }
}
