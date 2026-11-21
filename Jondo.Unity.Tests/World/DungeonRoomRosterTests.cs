using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Who a dungeon room's group is drawn from. A subarea can list the bosses of other dungeons
    /// beside its own monsters: those stay out of the rooms, unless a person wrote them in.
    /// </summary>
    public class DungeonRoomRosterTests
    {
        private const long First = 100, Second = 200, Last = 300;
        private const int Own = 1, OwnToo = 2, OwnBoss = 10, ForeignBoss = 20, QuestTarget = 30;

        private static DungeonManager.Dungeon Dungeon()
        {
            var dungeon = new DungeonManager.Dungeon { Id = 1, LastRoom = Last };
            dungeon.Rooms.AddRange(new[] { First, Second, Last });
            dungeon.Bosses.Add(OwnBoss);
            dungeon.Quarry[Second] = new List<int> { QuestTarget };
            return dungeon;
        }

        private static bool AnyBoss(int monster) => monster == OwnBoss || monster == ForeignBoss;

        [Fact]
        public void Another_dungeons_boss_in_the_subarea_stays_out()
        {
            var roster = MobSpawnManager.RoomRoster(Dungeon(), First,
                new[] { Own, ForeignBoss, OwnToo, OwnBoss, QuestTarget }, new int[0], AnyBoss);

            Assert.Equal(new[] { Own, OwnToo }, roster);
        }

        [Fact]
        public void The_fallback_leaves_out_every_boss_too()
        {
            var roster = MobSpawnManager.RoomRoster(Dungeon(), First,
                new[] { ForeignBoss }, new[] { ForeignBoss, Own }, AnyBoss);

            Assert.Equal(new[] { Own }, roster);
        }

        [Fact]
        public void A_written_roster_keeps_whoever_a_person_put_in_it()
        {
            var dungeon = Dungeon();
            dungeon.WrittenRoster[First] = new List<int> { ForeignBoss, Own, OwnBoss };

            var roster = MobSpawnManager.RoomRoster(dungeon, First, new[] { OwnToo }, new int[0], AnyBoss);

            Assert.Equal(new[] { ForeignBoss, Own }, roster);
        }

        [Fact]
        public void The_quest_target_of_a_room_is_not_taken_out_of_its_own_room()
        {
            var roster = MobSpawnManager.RoomRoster(Dungeon(), Second,
                new[] { Own, QuestTarget }, new int[0], AnyBoss);

            Assert.Equal(new[] { Own, QuestTarget }, roster);
        }
    }
}
