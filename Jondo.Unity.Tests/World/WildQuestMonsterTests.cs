using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The world's groups are generated from the subarea rosters, and those name the creatures a
    /// quest or an event brings out (m_flags IsQuestMonster) as well as the zone's own. The Shoon
    /// Mine's groups held its quest's level-140 monsters among its own miners; they are taken out
    /// of the wild groups as these are read. The dungeon rooms, an event's guests and La Bête in
    /// its cellar keep theirs.
    /// </summary>
    [Collection("MapManager")]
    public sealed class WildQuestMonsterTests
    {
        [Theory]
        [InlineData(97255955, 4211)]   // Mineur Fou
        [InlineData(97255955, 4212)]   // Craqueboule Cavernicole
        [InlineData(97260033, 4213)]   // Craqueleur Cavernicole
        [InlineData(97260033, 4214)]   // Larve Cavernicole
        [InlineData(97255949, 1234)]   // Lamineur Sombre
        [InlineData(97259013, 2903)]   // the Katigger, seeded on 212 maps
        public void A_quest_monster_does_not_stand_in_a_wild_group(long map, int monster)
        {
            Assert.True(MonsterFlags.IsQuest(monster));
            Assert.True(MobSpawnManager.QuestOnly(map, monster));
        }

        [Fact]
        public void The_mine_s_own_monsters_stay()
        {
            // Boulanger Sombre (153), of the same subarea's roster, is no quest monster.
            Assert.False(MonsterFlags.IsQuest(153));
            Assert.False(MobSpawnManager.QuestOnly(97255955, 153));
        }

        [Fact]
        public void La_Bete_stays_in_its_cellar_only()
        {
            Assert.False(MobSpawnManager.QuestOnly(MobSpawnManager.LaBeteBasement, MobSpawnManager.LaBete));
            Assert.True(MobSpawnManager.QuestOnly(97255955, MobSpawnManager.LaBete));
        }

        [Fact]
        public void An_event_guest_is_left_to_its_event()
        {
            // The Kwismas Minotoball (2819) carries the bit; EventGuests shows and hides it.
            Assert.True(MonsterFlags.IsQuest(2819));
            if (!EventGuests.IsGuest(2819)) return;
            Assert.False(MobSpawnManager.QuestOnly(97255955, 2819));
        }

        [Fact]
        public void A_dungeon_room_keeps_its_quest_monsters()
        {
            const long Room = 23068672;
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            DungeonManager.Initialize();
            if (!DungeonManager.IsRoom(Room)) return;
            Assert.True(MonsterFlags.IsQuest(5700));
            Assert.False(MobSpawnManager.QuestOnly(Room, 5700));
        }
    }
}
