using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Achievements;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The achievement engine against the catalogue: what each kind of objective needs, the
    /// window's progress, and paying once.
    /// </summary>
    public class AchievementEngineTests
    {
        private static bool Available
            => File.Exists(Paths.AchievementsJson) && File.Exists(Paths.QuestsJson)
               && File.Exists(Paths.AchievementLinksJson);

        /// <summary>A character who has done exactly what the test says, with the tallies the server keeps.</summary>
        private sealed class Player : IQuestFacts
        {
            public int Level { get; set; } = 1;
            public long MapId { get; set; }
            public HashSet<int> Done { get; } = new HashSet<int>();
            public Dictionary<(string, long), long> Tallies { get; } = new Dictionary<(string, long), long>();
            public Dictionary<int, int> Bag { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> JobLevels { get; } = new Dictionary<int, int>();
            public long Crafted { get; set; }

            public bool Finished(int questId) => Done.Contains(questId);
            public bool Active(int questId) => false;
            public bool ObjectiveDone(int objectiveId) => false;

            public long? Scalar(string op) => op switch
            {
                "SC" => 0,
                AchievementCatalogue.CraftKey => Crafted,
                _ => null,
            };

            public long? Count(string op, long key, string flag) => op switch
            {
                "PO" => Bag.TryGetValue((int)key, out int n) ? n : 0,
                "EM" or "Ef" or "EH" or AchievementCatalogue.ExploreKey
                    => Tallies.TryGetValue((flag == "d" ? op + "d" : op, key), out long t) ? t : 0,
                AchievementCatalogue.JobKey => JobLevels.Values.Count(l => l >= key),
                _ => null,
            };
        }

        private static readonly AchievementCatalogue Book = Available ? new AchievementCatalogue() : null!;

        // ─── What the link file ties ──────────────────────────────────────────────

        [Theory]
        // Every exploration achievement the captures earn, with the subarea of the map it was
        // earned on: the long route, the rat hunt, the tutorial, Pandala and Anutropia.
        [InlineData(307, 518)]
        [InlineData(301, 69)]
        [InlineData(302, 59)]
        [InlineData(5179, 974)]
        [InlineData(5180, 976)]
        [InlineData(9024, 1119)]
        [InlineData(270, 315)]
        [InlineData(338, 517)]
        [InlineData(1603, 889)]
        [InlineData(5186, 980)]
        [InlineData(5183, 978)]
        [InlineData(1604, 886)]
        [InlineData(384, 472)]
        [InlineData(8992, 1114)]
        [InlineData(1098, 821)]
        [InlineData(423, 448)]
        [InlineData(424, 446)]
        public void An_exploration_achievement_is_tied_to_the_subarea_the_capture_earns_it_in(int achievement, int subarea)
        {
            if (!Available) return;

            var badge = Book.Of(achievement);
            Assert.NotNull(badge);
            var link = Assert.Single(badge!.Objectives).Link;
            Assert.NotNull(link);
            Assert.Equal(AchievementLink.Explore, link!.Kind);
            Assert.Equal(subarea, link.Subarea);
            Assert.Contains(achievement, Book.WaitingOn(AchievementCatalogue.ExploreKey, subarea));
        }

        [Fact]
        public void Entering_the_zone_earns_it_and_nothing_else_does()
        {
            if (!Available) return;

            var player = new Player { Level = 200 };
            var log = new AchievementLog(Book, player);

            Assert.False(log.Holds(307));

            player.Tallies[(AchievementCatalogue.ExploreKey, 518)] = 1;
            var earned = log.CheckAll(Book.WaitingOn(AchievementCatalogue.ExploreKey, 518));

            Assert.Contains(307, earned);
            Assert.True(log.Has(307));
        }

        [Fact]
        public void The_level_achievements_want_the_level_and_nothing_more()
        {
            if (!Available) return;

            // "Principiante", "Alcanzar el nivel 10" ... "Veterano", level 200: eleven of them,
            // none of which the client describes.
            var levelOnes = new[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };

            var low = new AchievementLog(Book, new Player { Level = 9 });
            Assert.Empty(low.CheckAll(Book.WaitingOn("PL")).Intersect(levelOnes));

            var high = new AchievementLog(Book, new Player { Level = 200 });
            var earned = high.CheckAll(Book.WaitingOn("PL"));
            Assert.Equal(levelOnes, earned.Intersect(levelOnes).OrderBy(i => i));
        }

        [Fact]
        public void Somebody_who_has_done_nothing_earns_only_what_their_level_gives()
        {
            if (!Available) return;

            // The opposite rule from a quest's start condition: what cannot be judged does not
            // pass. A level-1 character who has done nothing earns nothing at all on login, and a
            // level-200 one earns the level achievements and whatever is built on them alone.
            var nobody = new AchievementLog(Book, new Player { Level = 1 });
            Assert.Empty(nobody.CheckEverything());

            var veteran = new AchievementLog(Book, new Player { Level = 200 });
            foreach (int id in veteran.CheckEverything())
            {
                var badge = Book.Of(id)!;
                bool byLevel = badge.Objectives.All(o =>
                    o.Link?.Kind == AchievementLink.Level
                    || o.Terms.All(t => t.Op is "PL" or "OA" or "SC" or "Oa"));
                Assert.True(byLevel, $"{id} {badge} was earned by a level-200 nobody");
            }
        }

        [Fact]
        public void The_job_achievements_count_jobs_at_a_level()
        {
            if (!Available) return;

            var player = new Player();
            var log = new AchievementLog(Book, player);

            player.JobLevels[24] = 10;
            var earned = log.CheckAll(Book.WaitingOn(AchievementCatalogue.JobKey));
            Assert.Contains(14, earned);        // level 10 in one job
            Assert.DoesNotContain(15, earned);  // level 100 in one

            player.JobLevels[24] = 200;
            player.JobLevels[26] = 200;
            earned = log.CheckAll(Book.WaitingOn(AchievementCatalogue.JobKey));
            Assert.Contains(15, earned);
            Assert.Contains(16, earned);
            Assert.Contains(766, earned);       // level 200 in two
            Assert.DoesNotContain(767, earned); // in three
        }

        [Fact]
        public void Crafting_once_earns_the_prototype()
        {
            if (!Available) return;

            // The tutorial earns 120 "El prototipo" right after its ring.
            var player = new Player();
            var log = new AchievementLog(Book, player);
            Assert.False(log.Holds(120));

            player.Crafted = 1;
            Assert.Contains(120, log.CheckAll(Book.WaitingOn(AchievementCatalogue.CraftKey)));
        }

        [Fact]
        public void Beating_monsters_with_a_challenge_won_counts_for_Ef()
        {
            if (!Available) return;

            // 1045 "Obscurantis": five monsters beaten with a challenge won, and level 190.
            var player = new Player { Level = 190 };
            var log = new AchievementLog(Book, player);

            foreach (int monster in new[] { 3567, 3568, 3566, 3569 }) player.Tallies[("Ef", monster)] = 1;
            Assert.False(log.Holds(1045));
            Assert.Contains(1045, log.AlmostFinished(int.MaxValue));

            player.Tallies[("Ef", 3570)] = 1;
            Assert.Contains(1045, log.CheckAll(Book.WaitingOn("Ef", 3570)));
        }

        [Fact]
        public void A_dungeon_boss_counts_only_in_its_dungeon()
        {
            if (!Available) return;

            // 37 "Corte del Jalató Real": EM>147,0,d, the Royal Gobball beaten in its dungeon.
            var player = new Player { Level = 50 };
            var log = new AchievementLog(Book, player);

            player.Tallies[("EM", 147)] = 1;
            Assert.False(log.Holds(37));

            player.Tallies[("EMd", 147)] = 1;
            Assert.True(log.Holds(37));
        }

        // ─── The window ───────────────────────────────────────────────────────────

        [Fact]
        public void A_tally_draws_as_a_bar_and_a_yes_or_no_as_one_of_one()
        {
            if (!Available) return;

            var player = new Player { Level = 190 };
            var log = new AchievementLog(Book, player);

            // 1045: Ef>3567,0 is out of 1, PL>189 is out of 1 and already there — the done ones go last.
            player.Tallies[("Ef", 3567)] = 1;
            var progress = log.Progress(1045);
            Assert.Equal(6, progress.Count);
            Assert.All(progress, p => Assert.Equal(1, p.Maximum));
            Assert.Equal(4, progress.Count(p => !p.Done));
            Assert.True(progress.Skip(4).All(p => p.Done));
        }

        [Fact]
        public void A_category_holds_every_achievement_the_capture_lists()
        {
            if (!Available) return;

            // mff {40} is answered with the 23 achievements of "Misiones de mundo".
            var ids = Book.InCategory(40).Select(a => a.Id).ToHashSet();
            var captured = new[] { 5222, 556, 559, 585, 586, 564, 563, 562, 561, 560, 1650, 7761, 5220, 2198,
                                   1101, 565, 1036, 1048, 1385, 1622, 1543, 1704, 1705 };
            Assert.Equal(captured.OrderBy(i => i), ids.OrderBy(i => i));
        }

        [Fact]
        public void A_category_lists_the_ones_still_to_earn_first()
        {
            if (!Available) return;

            // As the capture's answer for category 40 does: the earned ones last.
            var log = new AchievementLog(Book, new Player { Level = 200 });
            log.Restore(565, claimed: true);

            var list = Jondo.Unity.Server.Managers.Achievements.CategoryList(log, 40);
            Assert.Equal(23, list.Count);
            Assert.Equal(565, list[^1].Achievement);
            Assert.All(list.Take(22), entry => Assert.False(log.Has(entry.Achievement)));
        }

        [Fact]
        public void A_parent_category_is_answered_with_what_hangs_from_it()
        {
            if (!Available) return;

            // Category 8, "Misiones", holds none of its own; 40 is one of its children.
            var log = new AchievementLog(Book, new Player());
            var ids = Jondo.Unity.Server.Managers.Achievements.CategoryList(log, 8).Select(e => e.Achievement).ToList();
            Assert.Contains(5222, ids);
            Assert.Contains(8518, ids);   // category 118, "General", also under 8
        }

        [Fact]
        public void The_list_on_entering_the_world_marks_the_unpaid_ones_with_the_level()
        {
            if (!Available) return;

            var log = new AchievementLog(Book, new Player { Level = 57 });
            log.Restore(423, claimed: true);
            log.Restore(307, claimed: false);

            var list = Jondo.Unity.Server.Managers.Achievements.ListOf(log, 57);
            Assert.Equal(new[] { (307, 57), (423, 0) }, list);

            // An omega character is 200 on the wire, as all fifteen mfu of the route captures are.
            Assert.Equal(new[] { (307, 200), (423, 0) }, Jondo.Unity.Server.Managers.Achievements.ListOf(log, 354));
        }

        // ─── Paying ───────────────────────────────────────────────────────────────

        [Fact]
        public void A_reward_for_not_having_been_paid_yet_pays_once()
        {
            if (!Available) return;

            // 1045's items are guarded by "Ob!1045": 1,060 rewards say that of themselves.
            var player = new Player { Level = 190 };
            var log = new AchievementLog(Book, player);
            log.Restore(1045, claimed: false);

            var before = log.Payout(1045, 190);
            Assert.Contains((14927, 1), before.Items);
            Assert.True(before.Experience > 0);
            Assert.True(before.Kamas > 0);

            Assert.True(log.MarkClaimed(1045));
            Assert.False(log.MarkClaimed(1045));

            var after = log.Payout(1045, 190);
            Assert.DoesNotContain((14927, 1), after.Items);
        }

        [Fact]
        public void The_Logros_capture_claim_pays_545_with_the_bonus_it_had()
        {
            if (!Available) return;

            // 8990 "Recibidor de gremio bontariano": ratio 1, level 1. The capture pays 545 to a
            // character with 5 % bonus; without it the base is 520.
            var log = new AchievementLog(Book, new Player { Level = 200 });
            log.Restore(8990, claimed: false);
            Assert.Equal(520, log.Payout(8990, 200).Experience);
        }

        [Fact]
        public void A_reward_for_an_item_not_held_is_not_paid_when_it_is()
        {
            if (!Available) return;

            // 8520 gives item 10207 on condition "PO!10207".
            var player = new Player { Level = 2 };
            var log = new AchievementLog(Book, player);
            log.Restore(8520, claimed: false);

            Assert.Contains((10207, 1), log.Payout(8520, 2).Items);
            player.Bag[10207] = 1;
            Assert.DoesNotContain((10207, 1), log.Payout(8520, 2).Items);
        }
    }
}
