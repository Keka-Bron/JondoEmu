using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Tofu House and the Royal Tofu House share their door map, the peasants' workshop: Kurk
    /// Ingals keeps the first and Kazy Ingals the second. The lowest id won the map, so Kazy's
    /// «Darle la llave del Tofullinero Real» opened the Tofu House, and Kazy herself stood on the
    /// Hungry Sunflower Barn's door map instead of in the workshop.
    /// </summary>
    [Collection("MapManager")]
    public sealed class RoyalTofuHouseDoorTests
    {
        private const long Workshop = 96338944, BarnDoor = 192937992;
        private const int Kurk = 777, Kazy = 778, TofuHouse = 48, RoyalTofuHouse = 50;

        private readonly ITestOutputHelper _out;
        public RoyalTofuHouseDoorTests(ITestOutputHelper output) => _out = output;

        private static void Load()
        {
            if (MapManager.GetMapInfo(Workshop) == null) MapManager.Initialize();
            DungeonManager.Initialize();
            Npcs.Initialize();
            DungeonDoor.Initialize();
        }

        [Fact]
        public void Each_keeper_opens_his_own_dungeon()
        {
            Load();
            if (DungeonManager.Get(RoyalTofuHouse) == null || DungeonDoor.For(Kazy).Key == 0) return;   // no data

            Assert.Equal(TofuHouse, DungeonManager.AtEntrance(Workshop)!.Id);
            Assert.Equal(TofuHouse, DungeonManager.AtEntrance(Workshop, Kurk)!.Id);
            Assert.Equal(RoyalTofuHouse, DungeonManager.AtEntrance(Workshop, Kazy)!.Id);
        }

        [Fact]
        public void Kazy_stands_in_the_workshop()
        {
            Load();
            if (MapManager.GetMapInfo(Workshop) == null || Npcs.Of(Workshop).All(n => n.NpcId != Kurk)) return;   // no data

            var kurk = Npcs.Of(Workshop).First(n => n.NpcId == Kurk);
            Assert.True(Npcs.KeepsItsDoor(kurk));

            var kazy = Npcs.Of(Workshop).FirstOrDefault(n => n.NpcId == Kazy);
            Assert.NotNull(kazy);
            Assert.True(MapManager.IsCellWalkable(Workshop, kazy!.Cell));
            Assert.True(Npcs.KeepsItsDoor(kazy));
            Assert.DoesNotContain(Npcs.Of(BarnDoor), n => n.NpcId == Kazy);
        }

        [Fact]
        public void The_Royal_Tofu_ends_in_his_room()
        {
            Load();
            var royal = DungeonManager.Get(RoyalTofuHouse);
            if (royal == null || royal.FirstRoom == 0) return;   // no data

            _out.WriteLine($"first {royal.FirstRoom} last {royal.LastRoom}");
            Assert.Equal(96338948, royal.FirstRoom);
            Assert.Equal(96209926, royal.LastRoom);
        }
    }
}
