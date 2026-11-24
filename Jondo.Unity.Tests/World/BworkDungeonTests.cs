using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Bwork Dungeon (Dofus pour les noobs, «Donjon des Bworks»): three fights, then the Cachot,
    /// a room only walked through whose door leads on to the Fourth Room and whose grille drops to
    /// Kreuvète's cell, the pit, whose ladder and whose fight both bring the party back up; then the
    /// Fourth Room and the Bworkette's. The catalogue walked the five rooms straight and put the two
    /// cells after the boss, where nothing led to them.
    /// </summary>
    [Collection("MapManager")]
    public sealed class BworkDungeonTests
    {
        private const int Id = 13, Bworkette = 792, TamedTrool = 791, Swimp = 2680;
        private const long First = 104595969, Second = 104596993, Third = 104598017, Cachot = 104599041,
                           Pit = 104599043, Fourth = 104595971, Boss = 104596995;
        private const int Grille = 372, CachotDoor = 477758, Ladder = 477755, CachotLanding = 312;
        private static readonly int[] Bworks = { 53, 74, 876 };

        private static DungeonManager.Dungeon Load()
        {
            if (MapManager.GetMapInfo(First) == null) MapManager.Initialize();
            DungeonManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            return DungeonManager.Get(Id)!;
        }

        [Fact]
        public void The_third_win_leads_to_the_cachot_and_the_fourth_to_the_boss()
        {
            var bworks = Load();
            Assert.True(bworks.Authored);
            Assert.True(bworks.KeepsADoor);
            Assert.Equal(First, bworks.FirstRoom);
            Assert.Equal(Boss, bworks.LastRoom);
            Assert.Equal(Second, DungeonManager.NextRoom(bworks, First));
            Assert.Equal(Third, DungeonManager.NextRoom(bworks, Second));
            Assert.Equal(Cachot, DungeonManager.NextRoom(bworks, Third));
            Assert.Equal(Boss, DungeonManager.NextRoom(bworks, Fourth));
            Assert.Equal(Id, DungeonManager.OfRoom(Pit)!.Id);

            // The pit's win lands back in the Cachot, however many times it is won.
            var hub = bworks.Hub!;
            Assert.Equal((Cachot, CachotLanding), hub.AfterAWin(Pit, new long[0]));
            Assert.Equal((Cachot, CachotLanding), hub.AfterAWin(Pit, new[] { Pit }));
            Assert.Null(hub.AfterAWin(Third, new long[0]));
            Assert.Null(hub.AfterAWin(Boss, new long[0]));
        }

        [Fact]
        public void The_cachot_is_empty_and_the_pit_holds_the_tamed_trool_alone()
        {
            var bworks = Load();
            Assert.Contains(Cachot, bworks.EmptyRooms);
            Assert.Null(MobSpawnManager.RecomposeDungeonRoom(Cachot));
            Assert.DoesNotContain(Pit, bworks.EmptyRooms);

            var pit = MobSpawnManager.RecomposeDungeonRoom(Pit)!;
            Assert.All(pit.Members, m => Assert.Equal(TamedTrool, m.Monster.Id));
            Assert.Single(MobSpawnManager.MembersFor(pit, 1));
        }

        [Fact]
        public void The_rooms_hold_the_three_bworks_and_the_boss_room_the_bworkette_too()
        {
            Load();
            foreach (long room in new[] { First, Second, Third, Fourth, Boss })
            {
                var group = MobSpawnManager.RecomposeDungeonRoom(room)!;
                Assert.DoesNotContain(group.Members, m => m.Monster.Id is TamedTrool or Swimp);
                // A player alone meets all three: the room's species come once before any twice.
                var solo = MobSpawnManager.MembersFor(group, 1).Select(m => m.Monster.Id).ToList();
                Assert.Equal(4, solo.Count);
                Assert.All(Bworks, b => Assert.Contains(b, solo));
                Assert.Equal(room == Boss, solo.Contains(Bworkette));
            }
        }

        [Fact]
        public void The_grille_drops_to_the_pit_the_ladder_climbs_back_and_the_door_leads_on()
        {
            Load();
            TeleportManager.Initialize();
            Assert.True(TeleportManager.TryGetCellTrigger(Cachot, Grille, out var fall));
            Assert.Equal(Pit, fall.DestinationMapId);
            Assert.False(TeleportManager.TryGetCellTrigger(Cachot, CachotLanding, out _));

            Assert.True(TeleportManager.TryGet(Pit, Ladder, out var up));
            Assert.Equal(Cachot, up.DestinationMapId);
            Assert.Equal(CachotLanding, up.DestinationCellId);
            Assert.True(TeleportManager.TryGet(Cachot, CachotDoor, out var door));
            Assert.Equal(Fourth, door.DestinationMapId);
        }
    }
}
