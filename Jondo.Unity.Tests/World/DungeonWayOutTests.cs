using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
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

        /// <summary>
        /// The catalogue dungeons that content/world/dungeons.json rewrites, the White Rat's Pantry
        /// and the rest written only to give them a way out among them.
        /// </summary>
        private static List<DungeonManager.Dungeon> Rewritten()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.DungeonsJson));
            var catalogue = doc.RootElement.EnumerateObject()
                .Select(e => int.TryParse(e.Name, out int id) ? id : 0).ToHashSet();
            return DungeonManager.All.Values.Where(d => d.Authored && catalogue.Contains(d.Id)).ToList();
        }

        /// <summary>
        /// Rewriting a keyed dungeon used to take its keeper's door away: its keeper answered "No"
        /// to the White Rat's Pantry key, and so did five other keepers.
        /// </summary>
        [Fact]
        public void A_rewritten_keyed_dungeon_keeps_its_keepers_door()
        {
            if (!Load() || !File.Exists(Paths.DungeonsJson)) { _out.WriteLine("no world data"); return; }
            var doorless = Rewritten().Where(d => d.Required.Count > 0 && !d.KeepsADoor)
                .Select(d => $"{d.Id} {d.Name}").ToList();
            foreach (var l in doorless) _out.WriteLine(l);
            Assert.Empty(doorless);
        }

        /// <summary>
        /// A rewritten dungeon keeps its list as written, so it must be in the client's own room
        /// numbers: the White Rat's Pantry ran Fifth Room first and its boss stood in the First.
        /// </summary>
        [Fact]
        public void A_rewritten_dungeon_walks_its_rooms_in_the_clients_numbers()
        {
            if (!Load() || !File.Exists(Paths.DungeonsJson)) { _out.WriteLine("no world data"); return; }
            var shuffled = new List<string>();
            foreach (var d in Rewritten())
            {
                var numbers = d.Rooms.Select(r => DungeonManager.OrdinalOf(MapManager.GetMapInfo(r)?.Name ?? ""))
                    .Where(n => n > 0).ToList();
                if (!numbers.SequenceEqual(numbers.OrderBy(n => n)))
                    shuffled.Add($"{d.Id} {d.Name}: {string.Join(",", numbers)}");
            }
            foreach (var l in shuffled) _out.WriteLine(l);
            Assert.Empty(shuffled);
        }
    }
}
