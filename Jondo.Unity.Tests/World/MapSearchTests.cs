using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The administrator's place search: maps found by part of their area's or subarea's name, by
    /// coordinates, or by id, as Jondo Studio's map field finds them.
    /// </summary>
    [Collection("MapManager")]
    public class MapSearchTests
    {
        private static bool WorldLoaded()
        {
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            return MapManager.GetMapInfo(Jail.MapId) != null;
        }

        [Fact]
        public void A_name_finds_the_maps_of_its_area_or_subarea_and_outdoor_ones_first()
        {
            if (!WorldLoaded()) return;   // no world data

            var found = MapSearch.Find("bonta");
            Assert.NotEmpty(found);
            Assert.All(found, p => Assert.Contains("bonta", MapSearch.Fold(p.Area + " " + p.SubArea)));
            // Outdoor ones come before any indoor one.
            Assert.DoesNotContain(found.SkipWhile(p => p.Outdoor), p => p.Outdoor);
        }

        [Fact]
        public void Accents_and_case_do_not_matter()
        {
            if (!WorldLoaded()) return;

            var plain = MapSearch.Find("prision de los gm").Select(p => p.MapId).OrderBy(id => id).ToList();
            Assert.Contains(Jail.MapId, plain);
            Assert.Equal(plain, MapSearch.Find("PRISIÓN de los GM").Select(p => p.MapId).OrderBy(id => id).ToList());
        }

        [Fact]
        public void Coordinates_and_ids_find_their_maps()
        {
            if (!WorldLoaded()) return;

            var info = MapManager.GetMapInfo(Jail.MapId)!;
            Assert.All(MapSearch.Find($"[{info.PosX}, {info.PosY}]"), p => Assert.Equal((info.PosX, info.PosY), (p.X, p.Y)));
            Assert.Contains(MapSearch.Find($"{info.PosX},{info.PosY}"), p => p.MapId == Jail.MapId);
            Assert.Equal(Jail.MapId, MapSearch.Find(Jail.MapId.ToString()).First().MapId);
        }

        [Fact]
        public void Nothing_typed_finds_nothing()
        {
            Assert.Empty(MapSearch.Find(""));
            Assert.Empty(MapSearch.Find("   "));
        }
    }
}
