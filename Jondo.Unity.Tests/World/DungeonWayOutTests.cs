using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
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

        /// <summary>A dungeon one of whose items is a travel item into its first room.</summary>
        private static bool ItsItemIsTheWayIn(DungeonManager.Dungeon d)
            => d.Required.Any(r => TravelItemHandler.TravelOf(DatabaseManager.GetItemEffectsData(
                       DatabaseManager.GetItemTemplatePossibleEffects(r.Item))) is { } travel
                   && TravelData.TryMapReference(travel.Value, out long map, out _) && map == d.FirstRoom);

        /// <summary>
        /// Rewriting a keyed dungeon used to take its keeper's door away: its keeper answered "No"
        /// to the White Rat's Pantry key, and so did five other keepers. A dungeon whose item is itself
        /// the way in has no keeper to keep: the Jellith Dimension's Multygely («il suffit de consommer
        /// un Multygely pour être téléporté dans le donjon», «il n'y a donc pas de sauvegarde»).
        /// </summary>
        [Fact]
        public void A_rewritten_keyed_dungeon_keeps_its_keepers_door()
        {
            if (!Load() || !File.Exists(Paths.DungeonsJson)) { _out.WriteLine("no world data"); return; }
            var doorless = Rewritten().Where(d => d.Required.Count > 0 && !d.KeepsADoor)
                .Where(d => !ItsItemIsTheWayIn(d))
                .Select(d => $"{d.Id} {d.Name}").ToList();
            foreach (var l in doorless) _out.WriteLine(l);
            Assert.Empty(doorless);
        }

        /// <summary>
        /// A rewritten dungeon keeps its list as written, so it must be in the client's own room
        /// numbers: the White Rat's Pantry ran Fifth Room first and its boss stood in the First.
        /// Each part numbers its own rooms: Koolich Cavern walks through Koolich's Lair, First to
        /// Sixth Room, between its own Second and Third.
        /// </summary>
        [Fact]
        public void A_rewritten_dungeon_walks_its_rooms_in_the_clients_numbers()
        {
            if (!Load() || !File.Exists(Paths.DungeonsJson)) { _out.WriteLine("no world data"); return; }
            var shuffled = new List<string>();
            foreach (var d in Rewritten())
            {
                foreach (var part in d.Rooms.Select(r => MapManager.GetMapInfo(r)?.Name ?? "")
                             .GroupBy(n => n.Split(" - ")[0]))
                {
                    var numbers = part.Select(DungeonManager.OrdinalOf).Where(n => n > 0).ToList();
                    if (!numbers.SequenceEqual(numbers.OrderBy(n => n)))
                        shuffled.Add($"{d.Id} {d.Name}: {part.Key}: {string.Join(",", numbers)}");
                }
            }
            foreach (var l in shuffled) _out.WriteLine(l);
            Assert.Empty(shuffled);
        }

        /// <summary>
        /// Al Howin's Vegetable Patch lets its winners out in its own lobby, which has no door out:
        /// leaving from there goes back through the portal that leads in.
        /// </summary>
        [Fact]
        public void A_lobby_that_is_its_own_exit_leaves_by_the_portal()
        {
            if (!Load()) { _out.WriteLine("no world data"); return; }
            const long Lobby = 101187584, Portal = 88212737;
            if (MapManager.GetMapInfo(Lobby) == null || MapManager.GetMapInfo(Portal) == null) return;
            TeleportManager.Initialize();
            Assert.NotNull(DungeonManager.LobbyAt(Lobby));
            Assert.Equal(Portal, DungeonManager.OutsideOf(Lobby));
        }
    }
}
