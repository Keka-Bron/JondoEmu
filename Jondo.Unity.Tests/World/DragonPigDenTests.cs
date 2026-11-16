using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Dragon Pig's Den as the game has it (Dofus pour les noobs, «Antre du Dragon Cochon»):
    /// thirteen rooms, its fights in every other one, the Marranórgola alone in the eleventh and
    /// the Dragocerdo in the thirteenth. The catalogue put a group in every room, the Marranórgola
    /// in the first.
    /// </summary>
    [Collection("MapManager")]
    public sealed class DragonPigDenTests
    {
        private const long FirstRoom = 72351744, SecondRoom = 72352768, ThirdRoom = 72353792,
                           FourthRoom = 72354816, EleventhRoom = 72359936, TwelfthRoom = 72360960,
                           ThirteenthRoom = 72361984;
        private const int Marranorgola = 3137, Dragocerdo = 113;

        private static void Load()
        {
            if (MapManager.GetMapInfo(FirstRoom) == null) MapManager.Initialize();
            DungeonManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
        }

        [Fact]
        public void The_den_is_walked_room_by_room_to_the_dragon_pig()
        {
            Load();
            var den = DungeonManager.Get(6)!;
            Assert.Equal(13, den.Rooms.Count);
            Assert.Equal(FirstRoom, den.FirstRoom);
            Assert.Equal(ThirteenthRoom, den.LastRoom);
            Assert.Equal(new[] { Dragocerdo }, den.Bosses);
            // A win in a fight room leaves the winners in the empty room after it.
            Assert.Equal(FourthRoom, DungeonHandler.AfterAWinIn(ThirdRoom).Map);
            Assert.Equal(TwelfthRoom, DungeonHandler.AfterAWinIn(EleventhRoom).Map);
        }

        [Fact]
        public void The_empty_rooms_keep_nobody_and_the_fight_rooms_their_own()
        {
            Load();
            foreach (long empty in new[] { FirstRoom, SecondRoom, FourthRoom, TwelfthRoom })
                Assert.Null(MobSpawnManager.RecomposeDungeonRoom(empty));

            var third = MobSpawnManager.RecomposeDungeonRoom(ThirdRoom)!;
            Assert.All(third.Members, m => Assert.Contains(m.Monster.Id, new[] { 603, 3304 }));

            var gorgouille = MobSpawnManager.RecomposeDungeonRoom(EleventhRoom)!;
            Assert.Equal(new[] { Marranorgola }, gorgouille.Members.Select(m => m.Monster.Id));
            Assert.Equal(1, gorgouille.Leaders);
            foreach (int players in new[] { 1, 4, 8 })
                Assert.Equal(gorgouille.Members, MobSpawnManager.MembersFor(gorgouille, players));

            var boss = MobSpawnManager.RecomposeDungeonRoom(ThirteenthRoom)!;
            Assert.Equal(Dragocerdo, boss.Members[0].Monster.Id);
            Assert.DoesNotContain(boss.Members, m => m.Monster.Id == Marranorgola);
        }

        [Fact]
        public void The_two_gas_rooms_warn_and_curse_whoever_lingers()
        {
            Load();
            var gas = DungeonManager.Get(6)!.Lingering!;
            Assert.Equal(new[] { FirstRoom, SecondRoom }, gas.Rooms.OrderBy(r => r));
            Assert.Equal(13284, gas.Warning);
            Assert.Equal((6, gas), LingeringRooms.RuleOf(SecondRoom)!.Value);
            Assert.Null(LingeringRooms.RuleOf(ThirdRoom));
            Assert.Equal(0, LingeringRooms.StepAfter(gas, 59));
            Assert.Equal(1, LingeringRooms.StepAfter(gas, 60));
            Assert.Equal(13282, gas.Steps[0].Item);
            Assert.Equal(2, LingeringRooms.StepAfter(gas, 600));
            Assert.Equal(13283, gas.Steps[1].Item);
        }
    }
}
