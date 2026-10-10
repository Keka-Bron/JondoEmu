using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The rules the Amakna village vendors are laid out by (datos/vendedores_jondo.json, its
    /// "_comentario_colocacion"): on cells a player can see them on, apart, facing the way the real
    /// game faces NPCs, and out of the way of what the map already has.
    /// </summary>
    public class VillageVendorLayoutTests
    {
        private const string Village = "88212759";

        /// <summary>The map's two groups of Píos, from the base's MapMobs.</summary>
        private static readonly int[] Groups = { 312, 344 };

        private static List<(int Npc, int Cell, int Facing)> Placement()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.JondoVendorsJson));
            return doc.RootElement.GetProperty("colocacion").EnumerateObject()
                .Select(p => (int.Parse(p.Name), p.Value.GetProperty("casilla").GetInt32(),
                              p.Value.GetProperty("orientacion").GetInt32()))
                .ToList();
        }

        [Fact]
        public void Every_vendor_stands_on_a_walkable_cell_of_his_own()
        {
            if (!File.Exists(Paths.JondoVendorsJson) || !File.Exists(Paths.WalkableCellsJson)) return;
            using var walkable = JsonDocument.Parse(File.ReadAllText(Paths.WalkableCellsJson));
            var cells = walkable.RootElement.GetProperty(Village).EnumerateArray().Select(e => e.GetInt32()).ToHashSet();

            var placement = Placement();
            Assert.Equal(23, placement.Count);
            Assert.All(placement, p => Assert.Contains(p.Cell, cells));
            Assert.Equal(placement.Count, placement.Select(p => p.Cell).Distinct().Count());
        }

        /// <summary>No two side by side, as Ankama hardly ever does: the nearest are two cells apart.</summary>
        [Fact]
        public void No_two_vendors_are_neighbours()
        {
            if (!File.Exists(Paths.JondoVendorsJson)) return;
            var cells = Placement().Select(p => p.Cell).ToList();
            foreach (int a in cells)
                foreach (int b in cells.Where(b => b > a))
                    Assert.True(MapGeometry.Distance(a, b) >= 2, $"{a} and {b} are neighbours");
        }

        /// <summary>1 in the columns 0-6 and 3 in 7-13, the way the real game splits them.</summary>
        [Fact]
        public void A_vendor_faces_the_way_his_column_does()
        {
            if (!File.Exists(Paths.JondoVendorsJson)) return;
            Assert.All(Placement(), p => Assert.Equal(p.Cell % 14 <= 6 ? 1 : 3, p.Facing));
        }

        /// <summary>None on an interactive (the well, the zaap) nor next to a group of monsters.</summary>
        [Fact]
        public void The_vendors_leave_the_interactives_and_the_groups_alone()
        {
            if (!File.Exists(Paths.JondoVendorsJson) || !File.Exists(Paths.InteractiveElementsJson)) return;
            using var interactives = JsonDocument.Parse(File.ReadAllText(Paths.InteractiveElementsJson));
            var taken = interactives.RootElement.GetProperty(Village).EnumerateArray()
                .Select(e => e.GetProperty("c").GetInt32()).ToHashSet();

            foreach (var (_, cell, _) in Placement())
            {
                Assert.DoesNotContain(cell, taken);
                Assert.All(Groups, group => Assert.True(MapGeometry.Distance(cell, group) >= 2,
                                                        $"{cell} is next to the group on {group}"));
            }
        }
    }
}
