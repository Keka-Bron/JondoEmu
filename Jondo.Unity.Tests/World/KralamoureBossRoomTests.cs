using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Giant Kralamoure is fought alone in his room. Composed like any boss room -- the boss
    /// leading, the room filled up to eight --, he came with seven of his lair's monsters.
    /// </summary>
    [Collection("MapManager")]
    public class KralamoureBossRoomTests
    {
        private const long BossRoom = 26740736;
        private const int Kralamoure = 423;

        [Fact]
        public void The_kralamoure_stands_alone_in_his_room()
        {
            // The rooms are composed from the dungeons: they have to be there first.
            DungeonManager.Initialize();
            if (MobSpawnManager.GetMonsterData(Kralamoure) == null) MobSpawnManager.InitializeAndSpawnAll();
            if (MobSpawnManager.GetMonsterData(Kralamoure) == null) return;   // no world data
            Assert.NotNull(MobSpawnManager.RecomposeDungeonRoom(BossRoom));
            var groups = MobSpawnManager.GetMobsForMap(BossRoom);
            if (groups.Count == 0) return;   // no world data

            var group = Assert.Single(groups);
            var member = Assert.Single(group.Members);
            Assert.Equal(Kralamoure, member.Monster!.Id);
            Assert.Single(MobSpawnManager.MembersFor(group, 1));
        }

        [Fact]
        public void Other_boss_rooms_are_still_filled_up()
        {
            Assert.Contains(31, DungeonManager.BossFightsAlone);
            Assert.DoesNotContain(1, DungeonManager.BossFightsAlone);   // the Royal Gobball leads his group
        }
    }
}
