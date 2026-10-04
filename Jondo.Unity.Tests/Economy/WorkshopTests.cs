using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The workshop: its messages byte for byte against the 3.6.10.10 captures, the recipe a bench
    /// makes, the experience of a craft, and which graphics are stations.
    /// </summary>
    [Collection("forgemagic")]
    public class WorkshopTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private static Effect E(int effect, int value) => new Effect(effect, value, 0, 0);

        // ─── The tutorial's ring: kgq, kfb, kfs, itf, kdr and isz ───────────────────────────

        [Fact]
        public void The_tutorials_jewellery_station_opens_with_its_skill()
            => Assert.Equal(Hex("080c"), WorkshopProtocol.BuildOpened(12));

        [Fact]
        public void An_ingredient_enters_with_the_float_written_at_zero()
            => Assert.Equal(Hex("0a0e083f2a0a08b80218012083a2d7531d00000000"),
                            WorkshopProtocol.BuildAdded(312, Array.Empty<Effect>(), 1, 175493379));

        [Fact]
        public void An_ingredient_leaves_by_its_uid()
            => Assert.Equal(Hex("0883a2d753"), WorkshopProtocol.BuildRemoved(175493379));

        [Fact]
        public void The_crafted_ring_arrives_and_is_reported_whole()
        {
            var ring = new List<Effect> { E(138, 3) };
            Assert.Equal(Hex("0a16083f2a1208a6990112052003588a01180120f788d853"),
                         WorkshopProtocol.BuildItemsArrived(new[] { (19622, (IReadOnlyList<Effect>)ring, 1, 175506551L) }));
            Assert.Equal(Hex("1214221208a6990112052003588a01180120f788d8531802"),
                         WorkshopProtocol.BuildCrafted(19622, ring, 1, 175506551));
        }

        [Fact]
        public void The_jeweller_goes_up_to_level_two()
            => Assert.Equal(Hex("0a0a1010220612020864200c1802"),
                            WorkshopProtocol.BuildJobLevelUp(16, 2, new[] { (12, false, 0, 0) }));

        // ─── The grinder's fusion: kgl, itu, kdr of several, the workshop's ivj ─────────────

        [Fact]
        public void A_craft_of_several_reports_the_template_and_the_count()
        {
            Assert.Equal(Hex("0802"), WorkshopProtocol.BuildCount(2));
            Assert.Equal(Hex("0a0810e094e2fc011845"), WorkshopProtocol.BuildStackGrew(530090592, 69));
            Assert.Equal(Hex("1207220508fc5a18021802"),
                         WorkshopProtocol.BuildCrafted(11644, Array.Empty<Effect>(), 2, 0));
            Assert.Equal(Hex("120508e50410011a0910b994e2fc0118e504"), WorkshopProtocol.BuildStackUsed(530090553, 613));
        }

        // ─── A rune: kdr, kex, kdb ──────────────────────────────────────────────────────────

        /// <summary>The first Vi rune of the shoes session: it fails and nothing moves.</summary>
        [Fact]
        public void A_failed_rune_is_reported_with_the_pool_change_written_at_zero()
        {
            var boots = new List<Effect> { E(125, 19), E(118, 19) };
            var result = new Forgemagic.Result
            {
                Outcome = Forgemagic.Outcome.Failure, PoolChange = Forgemagic.PoolChange.Same, Pool = 0, Effects = boots,
            };
            Assert.Equal(Hex("121b0800221708a54012042013587d12042013587618012083cafafc011801"),
                         WorkshopProtocol.BuildRuneResult(result, 8229, 530490627));
            Assert.Equal(Hex("121b083f2a1708a54012042013587d12042013587618012083cafafc01"),
                         WorkshopProtocol.BuildModified(8229, boots, 1, 530490627));
            Assert.Equal(Hex("1001"), WorkshopProtocol.BuildRuneDone());
        }

        [Fact]
        public void The_pool_never_goes_on_the_wire()
        {
            var plain = new List<Effect> { E(125, 19), E(118, 19) };
            var pooled = new List<Effect>(plain) { new Effect(Forgemagic.PoolEffect, 20, 0, 0) };
            Assert.Equal(WorkshopProtocol.BuildModified(8229, plain, 1, 530490627),
                         WorkshopProtocol.BuildModified(8229, pooled, 1, 530490627));
        }

        // ─── Recipes and experience ─────────────────────────────────────────────────────────

        /// <summary>The ring of the tutorial: five ingredients, one of each, skill 12.</summary>
        [Fact]
        public void A_bench_makes_a_recipe_only_with_exactly_its_ingredients()
        {
            RecipeManager.Declare(new RecipeDefinition { ResultId = 19622, ResultLevel = 1, JobId = 16, SkillId = 12 },
                new RecipeIngredient(289, 1), new RecipeIngredient(303, 1), new RecipeIngredient(312, 1),
                new RecipeIngredient(421, 1), new RecipeIngredient(1782, 1));
            var bench = new Dictionary<int, int> { [289] = 1, [303] = 1, [312] = 1, [421] = 1, [1782] = 1 };

            Assert.Equal(19622, CraftHandler.Match(12, bench)?.ResultId);
            Assert.Null(CraftHandler.Match(11, bench));
            Assert.Null(CraftHandler.Match(12, new Dictionary<int, int>(bench) { [312] = 2 }));
            var missing = new Dictionary<int, int>(bench);
            missing.Remove(1782);
            Assert.Null(CraftHandler.Match(12, missing));
        }

        [Fact]
        public void A_craft_gives_twenty_a_level_less_the_gap()
        {
            // The tutorial's level-1 ring at job level 1: +20, which is level 2 exactly.
            Assert.Equal(20, JobExperience.Craft(1, 1));
            Assert.Equal(JobExperience.Floor(2), JobExperience.Craft(1, 1));
            Assert.Equal(4000, JobExperience.Craft(200, 200));
            Assert.True(JobExperience.Craft(200, 1) < 2);
        }

        [Fact]
        public void A_level_200_magus_gets_one_a_rune()
        {
            // 83 runes entered in the two sessions, on items of level 7, 10 and 44: +1 each.
            Assert.Equal(1, JobExperience.Magus(200, 7));
            Assert.Equal(1, JobExperience.Magus(200, 10));
            Assert.Equal(1, JobExperience.Magus(200, 44));
            Assert.Equal(44, JobExperience.Magus(44, 44));
        }

        // ─── The stations ───────────────────────────────────────────────────────────────────

        [Fact]
        public void The_stations_come_from_the_captures_and_the_workshop_interiors()
        {
            if (!File.Exists(Paths.WorkshopsJson)) return;
            Workshops.Initialize();

            // Bonta's oven is only ever seen used: iwo on 521069, iwn with skill 27.
            Assert.True(Workshops.TryGet(49492, out var oven));
            Assert.Equal(new[] { 27 }, oven.Skills);

            // The magus table of Bonta declares three skills on one element.
            Assert.True(Workshops.TryGet(49506, out var table));
            Assert.Equal(new[] { 113, 118, 351 }, table.Skills.OrderBy(s => s).ToArray());
            Assert.Equal(117, table.Type);

            Assert.True(Workshops.TryGet(49510, out var anvil));
            Assert.Equal(new[] { 20 }, anvil.Skills);

            // A tailor's station no capture shows, found inside six tailors' workshops.
            Assert.True(Workshops.TryGet(9797, out var sewing));
            Assert.Equal(new[] { 63 }, sewing.Skills);
            Assert.Equal("inferred", sewing.Source);
        }
    }
}
