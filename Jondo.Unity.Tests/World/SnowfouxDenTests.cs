using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Snowfoux Den as the game has it (Dofus pour les noobs, «Tanière Givrefoux»): four fight
    /// rooms, then a transition room where Givrihaltès lets the party fight Tengu Snowfoux alone or
    /// Tengu and Fuji Snowfoux together, each in its own room, both leading to the Exit to the
    /// Petrified Forest.
    /// </summary>
    [Collection("MapManager")]
    public sealed class SnowfouxDenTests
    {
        private const long Entrance = 59510784, FourthRoom = 59514880, Transition = 59516928,
                           TenguRoom = 205787160, TenguFujiRoom = 205916168, ExitRoom = 59518976;
        private const int Tengu = 2967, Fuji = 3234, Givrihaltes = 1385;

        private static void Load()
        {
            if (MapManager.GetMapInfo(Transition) == null) MapManager.Initialize();
            DungeonManager.Initialize();
        }

        [Fact]
        public void The_fourth_room_leads_to_the_transition_and_each_boss_room_to_the_exit()
        {
            Load();
            var den = DungeonManager.Get(58)!;
            Assert.Equal(4, den.Rooms.Count);
            Assert.Equal(FourthRoom, den.Rooms[^1]);
            Assert.Empty(den.Bosses);
            Assert.Same(den, DungeonManager.AtEntrance(Entrance));
            Assert.Equal(Transition, DungeonHandler.AfterAWinIn(FourthRoom).Map);
            Assert.False(DungeonManager.IsRoom(Transition));

            foreach (var (room, bosses) in new[] { (TenguRoom, new[] { Tengu }), (TenguFujiRoom, new[] { Tengu, Fuji }) })
            {
                var dungeon = DungeonManager.OfRoom(room)!;
                Assert.Equal(bosses, dungeon.Bosses);
                Assert.Equal(room, dungeon.LastRoom);
                Assert.Equal(ExitRoom, DungeonHandler.AfterAWinIn(room).Map);
            }
        }

        /// <summary>«Fuji remplace la Kami Givrefoux dans le combat» (Dofus pour les noobs).</summary>
        [Fact]
        public void Tengu_comes_with_the_kami_and_fuji_takes_her_place()
        {
            Load();
            MobSpawnManager.EnsureMonsterData();
            var alone = MobSpawnManager.RecomposeDungeonRoom(TenguRoom)!;
            Assert.Equal(new[] { Tengu, 2982, 2891, 2888 }, alone.Members.Select(m => m.Monster.Id));
            var both = MobSpawnManager.RecomposeDungeonRoom(TenguFujiRoom)!;
            Assert.Equal(new[] { Tengu, Fuji, 2891, 2888 }, both.Members.Select(m => m.Monster.Id));
        }

        [Fact]
        public void Givrihaltes_offers_both_boss_rooms_in_the_transition_room()
        {
            var tree = NpcDialogueContent.Load(Paths.ContentFile(NpcDialogueContent.AuthoredFile)).Rows
                .Select(r => r.Value.Value).Single(d => d.NpcId == Givrihaltes && d.MapId == Transition);
            Assert.Equal(8988, tree.Opening);
            var goesTo = tree.Lines.SelectMany(l => l.Choices).Where(c => c.TeleportsTo != 0)
                .Select(c => c.TeleportsTo).OrderBy(m => m);
            Assert.Equal(new[] { TenguRoom, TenguFujiRoom }.OrderBy(m => m), goesTo);
        }
    }
}
