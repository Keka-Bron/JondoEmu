using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Bots;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A walk can end a fight without the walker dying: the trap he steps on hits its whole area,
    /// and a Sram walking into his own trap killed the last enemy while the fight waited for the
    /// end of his turn to notice.
    /// </summary>
    [Collection("MapManager")]
    public sealed class WalkEndsTheFightTests
    {
        private const long Me = 990_000_071, Enemy = 990_000_072;

        private static ConcurrentDictionary<long, FightInstance> Active
            => (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        [Fact]
        public async Task A_walk_after_which_the_enemy_side_has_nobody_ends_the_fight()
        {
            long map = CharacterCreationHandler.StartingMap;
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            var floor = MapManager.GetFightWalkable(map);
            Assert.NotNull(floor);
            int from = floor!.OrderBy(c => c).First(c => MapGeometry.GetNeighbors(c).Count(floor.Contains) >= 2);
            int to = MapGeometry.GetNeighbors(from).First(floor.Contains);
            int far = floor.OrderByDescending(c => MapGeometry.Distance(from, c)).First();

            var fight = new FightInstance(990_071, map);
            var me = new Fighter { Id = Me, MaxHP = 100, CurrentHP = 100, Level = 200, Initiative = 9000, CellId = from, MaxMP = 6, CurrentMP = 6 };
            // Dead already, as the trap left him: the walk is what has to notice it.
            var enemy = new Fighter { Id = Enemy, MaxHP = 100, CurrentHP = 0, Level = 200, Initiative = 1, CellId = far };
            fight.AddPlayer(me);
            fight.AddOpponent(enemy);
            fight.StartFight();
            Assert.Same(me, fight.CurrentFighter);
            me.CurrentMP = 6;
            Active[fight.FightId] = fight;

            await using var pipe = await ClientPipe.OpenAsync(8_900_000_071, Me, map, "Trampero");
            try
            {
                var walk = ConnectionProtocol.Push(Op.Jrw, Pb.New().Var(1, map)
                    .Packed(2, SimulatedPlayer.Corners(new[] { from, to })).Build());
                using (SessionContext.Push(pipe.Session)) await FightHandler.WalkAsync(pipe.ToClient, walk);

                Assert.Equal(to, me.CellId);
                Assert.Equal(FightInstance.WaitsForTheJwz, fight.FinPendiente);
            }
            finally
            {
                Active.TryRemove(fight.FightId, out _);
            }
        }
    }
}
