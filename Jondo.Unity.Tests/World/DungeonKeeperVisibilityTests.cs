using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A dungeon's guardian on its own door is there for everybody. The Aquadome's guardian also
    /// hands a quest of the Merkator chain over, so his placement carried that quest's condition
    /// and nobody without the quest open saw anyone at the door, nor found a way in.
    /// </summary>
    [Collection("MapManager")]
    public sealed class DungeonKeeperVisibilityTests
    {
        private const long AquadomeDoor = 117706767, NextMap = 117705226;
        private const int Guardian = 2196, QuestGiver = 2190;

        private static void Load()
        {
            if (MapManager.GetMapInfo(AquadomeDoor) == null) MapManager.Initialize();
            DungeonManager.Initialize();
            Npcs.Initialize();
            DungeonDoor.Initialize();
        }

        [Fact]
        public void The_Aquadome_guardian_stands_at_his_door_whatever_the_quests()
        {
            Load();
            var guardian = Npcs.Of(AquadomeDoor).FirstOrDefault(n => n.NpcId == Guardian);
            if (guardian == null || DungeonManager.AtEntrance(AquadomeDoor) == null) return;   // no data

            Assert.True(Npcs.KeepsItsDoor(guardian));
            Assert.True(Npcs.VisibleNow(guardian));
            Assert.Contains(Npcs.OnMap(AquadomeDoor), n => n.NpcId == Guardian);
        }

        [Fact]
        public void A_quest_giver_off_any_door_keeps_his_condition()
        {
            Load();
            var giver = Npcs.Of(NextMap).FirstOrDefault(n => n.NpcId == QuestGiver);
            if (giver == null || string.IsNullOrEmpty(giver.Criterion)) return;   // no data

            Assert.False(Npcs.KeepsItsDoor(giver));
        }
    }
}
