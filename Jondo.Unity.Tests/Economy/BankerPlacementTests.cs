using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Every bank has its banker: the ten banks the client marks on its world map, Bonta's
    /// measured, the others placed in content/npcs/spawns.json on a free walkable cell of the
    /// bank's first room.
    /// </summary>
    public class BankerPlacementTests
    {
        /// <summary>Each bank's first room and the banker templates that may stand in it.</summary>
        private static readonly Dictionary<long, int[]> Banks = new()
        {
            [217059328] = new[] { 6394 },            // Bonta, measured
            [192415750] = new[] { 100, 520, 522 },   // Astrub
            [214695944] = new[] { 6374 },            // Brakmar
            [99095051] = new[] { 100 },              // Amakna village
            [207618052] = new[] { 5653 },            // Pandala village
            [84935175] = new[] { 100 },              // Pueblo de los ganaderos
            [91753985] = new[] { 100 },              // Sufokia
            [86511105] = new[] { 100 },              // Pueblo costero
            [54534165] = new[] { 100 },              // Burgo, Frigost
            [173937154] = new[] { 3516 },            // Puerto de Picanesburgo
        };

        [Fact]
        public void Every_bank_has_a_banker_on_a_free_walkable_cell()
        {
            var spawns = NpcSpawnContent.Load(Paths.WorldNpcsJson,
                Paths.ContentFile(NpcSpawnContent.AuthoredFile), _ => { }, Paths.WorldNpcsDerivedJson);
            if (spawns.Count == 0) return;   // no data files: nothing to check

            using var walkable = JsonDocument.Parse(File.ReadAllText(Paths.WalkableCellsJson));
            using var interactives = JsonDocument.Parse(File.ReadAllText(Paths.Resolve("interactive_elements.json")));

            foreach (var (map, bankers) in Banks)
            {
                var here = spawns.Values.Where(s => s.MapId == map).ToList();
                var found = here.Where(s => bankers.Contains(s.NpcId)).ToList();
                Assert.True(found.Count > 0, $"no banker on the bank map {map}");
                var banker = found[0];

                var cells = walkable.RootElement.TryGetProperty(map.ToString(), out var list)
                    ? list.EnumerateArray().Select(c => c.GetInt32()).ToHashSet()
                    : new HashSet<int>();
                // Bonta's measured banker stands behind his counter, on a cell nobody walks on; the
                // inferred ones stand in the room, where they can be reached.
                if (map != 217059328) Assert.Contains(banker.Cell, cells);

                var busy = interactives.RootElement.TryGetProperty(map.ToString(), out var els)
                    ? els.EnumerateArray().Select(e => e.GetProperty("c").GetInt32()).ToHashSet()
                    : new HashSet<int>();
                Assert.DoesNotContain(banker.Cell, busy);
                Assert.Single(here, s => s.Cell == banker.Cell);
            }
        }

        /// <summary>The banker the derived layer had from Giny is gone from its Giny cell.</summary>
        [Fact]
        public void No_banker_stands_where_giny_put_him()
        {
            var spawns = NpcSpawnContent.Load(Paths.WorldNpcsJson,
                Paths.ContentFile(NpcSpawnContent.AuthoredFile), _ => { }, Paths.WorldNpcsDerivedJson);
            if (spawns.Count == 0) return;
            Assert.DoesNotContain(spawns.Values, s => s.MapId == 192415750 && s.NpcId == 100 && s.Cell == 318);
        }
    }
}
