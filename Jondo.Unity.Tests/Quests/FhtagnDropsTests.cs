using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Quests;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// «Fhtagn» (1765): each P'Koutoulou drops off the klutist (4452) of its own map at the
    /// client's 30 %, never for sure, and its objective closes when it drops there -- not when the
    /// klutist is merely beaten.
    /// </summary>
    public sealed class FhtagnDropsTests
    {
        private static readonly (int Objective, int Item, long Map)[] Koutoulous =
        {
            (11280, 18108, 168559628), (11281, 18109, 168558606), (11282, 18110, 168559632),
        };

        [Fact]
        public void Each_P_Koutoulou_drops_on_its_map_and_its_drop_closes_the_objective()
        {
            QuestEngine.Load();
            if (QuestEngine.Bindings.Of(11280) == null) return;   // no quest data
            foreach (var (objective, item, map) in Koutoulous)
            {
                var binding = QuestEngine.Bindings.Of(objective)!;
                Assert.Equal(QuestBindingKind.Gather, binding.Kind);
                Assert.Equal((item, map), (binding.ItemId, binding.MapId));

                var drop = Assert.Single(QuestFightDrops.All, d => d.Item == item);
                Assert.Equal(30, drop.Percent);
                Assert.True(drop.From(4452));
                Assert.False(drop.From(4453));
                Assert.Contains($"Pm={map}", drop.Criterion);
            }
        }
    }
}
