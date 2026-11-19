using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The monster group tooltip's loot (GroupPreview): each row's chance as the fight rolls it,
    /// and an item several monsters drop shown at the chance of getting at least one.
    /// </summary>
    public class GroupPreviewTests
    {
        [Fact]
        public void One_roll_is_its_own_chance()
            => Assert.Equal(12.8, GroupPreview.AtLeastOne(new[] { 12.8 }), 6);

        [Fact]
        public void Two_rolls_are_at_least_one_of_them()
            => Assert.Equal(75.0, GroupPreview.AtLeastOne(new[] { 50.0, 50.0 }), 6);

        [Fact]
        public void A_certain_roll_makes_it_certain()
            => Assert.Equal(100.0, GroupPreview.AtLeastOne(new[] { 3.0, 100.0, 0.5 }), 6);

        [Fact]
        public void Nothing_rolled_is_nothing()
            => Assert.Equal(0.0, GroupPreview.AtLeastOne(System.Array.Empty<double>()), 6);

        [Fact]
        public void Prospecting_raises_a_row_as_the_fight_does()
        {
            double multiplier = FightHandler.DropMultiplier(400);
            Assert.Equal(10.0 * multiplier, GroupPreview.ChanceOf(10.0, false, multiplier, 0), 6);
        }

        [Fact]
        public void A_row_that_disables_prospecting_keeps_its_chance()
            => Assert.Equal(10.0, GroupPreview.ChanceOf(10.0, true, FightHandler.DropMultiplier(400), 0), 6);

        [Fact]
        public void The_server_bonus_goes_on_top_and_never_past_certainty()
        {
            Assert.Equal(15.0, GroupPreview.ChanceOf(10.0, false, 1.0, 50), 6);
            Assert.Equal(100.0, GroupPreview.ChanceOf(80.0, false, 2.0, 50), 6);
        }

        /// <summary>A Red Piwi's feather at 100 %: two of them in the group, still one row at 100 %.</summary>
        [Fact]
        public void A_group_lists_each_item_once_with_the_coin()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            var group = new[] { new GroupPreview.Member(489, 1), new GroupPreview.Member(489, 3) };
            var drops = GroupPreview.Drops(group, 100, 0, (_, _) => false);
            if (drops.Count <= 1) return;

            Assert.Equal(drops.Count, drops.Select(d => d.Item).Distinct().Count());
            Assert.Contains(drops, d => d.Item == JondoCoin.TemplateId && d.Percent == 100.0);
            Assert.All(drops, d => Assert.InRange(d.Percent, 0.0, 100.0));
            Assert.True(drops.Zip(drops.Skip(1), (a, b) => a.Percent >= b.Percent).All(x => x));
        }
    }
}
