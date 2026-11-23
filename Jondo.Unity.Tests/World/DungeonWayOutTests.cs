using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Every dungeon lets its winners out on a map the client can load. Crocabulia's Lair, Koolich
    /// Cavern and the Royal Tofu House had catalogue exits the world data does not have: the
    /// teleport refused them and the winners stayed in the boss room.
    /// </summary>
    [Collection("MapManager")]
    public sealed class DungeonWayOutTests
    {
        private const int CrocabuliasLair = 7, KoolichCavern = 30, RoyalTofuHouse = 50;

        private readonly ITestOutputHelper _out;
        public DungeonWayOutTests(ITestOutputHelper output) => _out = output;

        private static bool Load()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            DungeonManager.Initialize();
            return MapManager.Maps.Count > 0 && DungeonManager.Get(CrocabuliasLair) != null;
        }

        [Theory]
        [InlineData(CrocabuliasLair)]
        [InlineData(KoolichCavern)]
        [InlineData(RoyalTofuHouse)]
        public void A_missing_exit_leaves_by_the_entrance(int id)
        {
            if (!Load()) { _out.WriteLine("no world data"); return; }
            var dungeon = DungeonManager.Get(id)!;
            Assert.Equal(dungeon.EntranceMapId, DungeonManager.WayOut(dungeon));
        }

        [Fact]
        public void Every_way_out_is_a_known_map()
        {
            if (!Load()) { _out.WriteLine("no world data"); return; }
            var lost = DungeonManager.All.Values
                .Where(d => d.EntranceMapId != 0 && MapManager.GetMapInfo(d.EntranceMapId) != null)
                .Where(d => MapManager.GetMapInfo(DungeonManager.WayOut(d)) == null)
                .Select(d => $"{d.Id} {d.Name} -> {DungeonManager.WayOut(d)}").ToList();
            foreach (var l in lost) _out.WriteLine(l);
            Assert.Empty(lost);
        }
    }
}
