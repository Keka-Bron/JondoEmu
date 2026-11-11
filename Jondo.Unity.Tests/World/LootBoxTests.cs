using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The boxes items open into: effect 222's group of the client's random drops, drawn by weight
    /// or given whole.
    /// </summary>
    public class LootBoxTests
    {
        /// <summary>
        /// The Cofre de la sima (34353) is "222, one draw, group 534", and group 534 is the five items
        /// its window lists at 69 %, 10 %, 10 %, 10 % and 1 %.
        /// </summary>
        [Fact]
        public void The_abyss_chest_opens_into_its_group()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;
            var box = LootBoxes.BoxOf(34353);
            Assert.NotNull(box);
            Assert.Equal(534, box.Value.Group.Id);
            Assert.Equal(1, box.Value.Draws);
            Assert.Equal(new[] { 34337, 34338, 34339, 34340, 34352 }, box.Value.Group.Items.Select(i => i.ItemId).OrderBy(i => i));
            Assert.Null(LootBoxes.BoxOf(34340));        // the Luz abisal itself opens into nothing
        }

        /// <summary>One draw, one item, by weight: over many boxes the Luz abisal comes out about 69 % of the time.</summary>
        [Fact]
        public void A_draw_takes_one_item_by_weight()
        {
            var group = LootBoxes.GroupOf(534);
            if (group == null) return;
            var dice = new Random(7);
            int boxes = 20000, light = 0;
            for (int i = 0; i < boxes; i++)
            {
                var given = LootBoxes.Open(group, 1, dice);
                Assert.Single(given);
                Assert.Equal(1, given[0].Quantity);
                if (given[0].ItemId == 34340) light++;
            }
            Assert.InRange(light / (double)boxes, 0.66, 0.72);
        }

        /// <summary>
        /// The Lotapes of «Reír a base de palos» name groups 16 to 21, which the client's file lacks:
        /// content/quests/loot_groups.json gives each the eight jokes of its flavour.
        /// </summary>
        [Fact]
        public void A_lotape_opens_into_a_joke_of_its_flavour()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;
            LootBoxes.Forget();
            var caramel = LootBoxes.BoxOf(13521);
            Assert.NotNull(caramel);
            Assert.Equal(16, caramel.Value.Group.Id);
            Assert.Equal(new[] { 13527, 13530, 13531, 13532, 13533, 13534, 13535, 13536 },
                         caramel.Value.Group.Items.Select(i => i.ItemId).OrderBy(i => i));
            var hazelnut = LootBoxes.BoxOf(13526);
            Assert.NotNull(hazelnut);
            Assert.Equal(Enumerable.Range(13569, 8), hazelnut.Value.Group.Items.Select(i => i.ItemId).OrderBy(i => i));

            var given = LootBoxes.Open(caramel.Value.Group, caramel.Value.Draws, new Random(3));
            Assert.Single(given);
            Assert.Contains(given[0].ItemId, caramel.Value.Group.Items.Select(i => i.ItemId));
        }

        /// <summary>
        /// Every monster beaten drops the five common Lotapes while 1198 is in hand, and only then
        /// (content/quests/fight_drops.json).
        /// </summary>
        [Fact]
        public void The_common_lotapes_drop_while_their_quest_is_in_hand()
        {
            QuestFightDrops.Forget();
            var rows = QuestFightDrops.All.Where(d => d.Item >= 13521 && d.Item <= 13525).ToList();
            Assert.Equal(new[] { 13521, 13522, 13523, 13524, 13525 }, rows.Select(d => d.Item).OrderBy(i => i));
            Assert.All(rows, d => Assert.Equal("Qa=1198", d.Criterion));
            Assert.All(rows, d => Assert.InRange(d.Percent, 0.1, 100));
            Assert.DoesNotContain(QuestFightDrops.All, d => d.Item == 13526);   // the bosses' own tables carry it
        }

        /// <summary>A group whose weights are all -1 gives every item it lists, each in its quantity.</summary>
        [Fact]
        public void A_group_of_minus_ones_gives_everything()
        {
            var group = new LootBoxes.Group(42, false, new[]
            {
                new LootBoxes.DropItem(15551, -1, 1, 1),
                new LootBoxes.DropItem(13052, -1, 1020, 1020),
            });
            Assert.True(group.GivesAll);
            var given = LootBoxes.Open(group, 1, new Random(1));
            Assert.Equal(new[] { (15551, 1), (13052, 1020) }, given);
        }
    }
}
