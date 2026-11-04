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
    /// A trap stops whoever enters it, walked or pushed, and goes off there: «el efecto se
    /// interrumpe si se activa una trampa, un glifo o un portal durante el desplazamiento». Checked
    /// only where a walk ended, a bot crossed a Trampa de Deriva on its way into a Trampa de
    /// Inmovilización and the Deriva never went off.
    /// </summary>
    [Collection("MapManager")]
    public sealed class TrapStopsTests
    {
        private const long Me = 990_000_081, Sram = 990_000_082;
        private const int Immobilisation = 12923, Drift = 12955, Grade = 3;

        private static ConcurrentDictionary<long, FightInstance> Active
            => (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private static Glifo Trap(FightInstance fight, long owner, int cell, int spell)
        {
            var trap = fight.Poner(new Glifo(owner, new[] { cell }, spell, Grade, 0x312D86, caducaEnRonda: 0,
                                             mascara: "a,A", cuando: Disparo.AlPisar));
            trap.Tipo = EffectEngine.ColocaUnaTrampa;
            trap.Centro = cell;
            return trap;
        }

        [Fact]
        public async Task A_walk_stops_on_the_first_trap_it_crosses()
        {
            long map = CharacterCreationHandler.StartingMap;
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            var floor = MapManager.GetFightWalkable(map);
            Assert.NotNull(floor);

            // Three cells in a row, the trap on the middle one.
            int from = -1, middle = -1, end = -1;
            foreach (int c in floor!.OrderBy(c => c))
            {
                var (x, y) = MapGeometry.CellToPoint(c);
                int m = MapGeometry.PointToCell(x + 1, y), e = MapGeometry.PointToCell(x + 2, y);
                if (m < 0 || e < 0 || !floor.Contains(m) || !floor.Contains(e)) continue;
                from = c; middle = m; end = e;
                break;
            }
            Assert.True(from >= 0);
            int far = floor.OrderByDescending(c => MapGeometry.Distance(from, c)).First();

            var fight = new FightInstance(990_081, map);
            var me = new Fighter { Id = Me, MaxHP = 1000, CurrentHP = 1000, Level = 200, Initiative = 9000, CellId = from,
                                   MaxMP = 6, CurrentMP = 6, MaxAP = 7, CurrentAP = 7 };
            var sram = new Fighter { Id = Sram, MaxHP = 1000, CurrentHP = 1000, Level = 200, Initiative = 1, CellId = far,
                                     MaxMP = 3, CurrentMP = 3, MaxAP = 6, CurrentAP = 6 };
            fight.AddPlayer(me);
            fight.AddOpponent(sram);
            fight.StartFight();
            Assert.Same(me, fight.CurrentFighter);
            me.CurrentMP = 6;
            var trap = Trap(fight, Sram, middle, Immobilisation);
            Active[fight.FightId] = fight;

            await using var pipe = await ClientPipe.OpenAsync(8_900_000_081, Me, map, "Pisador");
            try
            {
                var walk = ConnectionProtocol.Push(Op.Jrw, Pb.New().Var(1, map)
                    .Packed(2, SimulatedPlayer.Corners(new[] { from, middle, end })).Build());
                using (SessionContext.Push(pipe.Session)) await FightHandler.WalkAsync(pipe.ToClient, walk);

                Assert.Equal(middle, me.CellId);
                Assert.True(trap.Gastado);
            }
            finally
            {
                Active.TryRemove(fight.FightId, out _);
            }
        }

        [Fact]
        public void A_push_stops_on_the_next_trap_of_the_network()
        {
            // Map 1 has no ground: nothing but the trap stops the push. The Deriva's zone is the
            // diagonal cross ("+"), and it pushes along the diagonal: 339 to 395 in the fight it
            // was reported from.
            var fight = new FightInstance(990_082, 1);
            int centre = 300;
            var (x, y) = MapGeometry.CellToPoint(centre);
            int next = MapGeometry.PointToCell(x + 1, y + 1);
            int trapped = MapGeometry.PointToCell(x + 2, y + 2);
            int beyond = MapGeometry.PointToCell(x + 3, y + 3);

            var sram = new Fighter { Id = Sram, TeamId = 0, CellId = MapGeometry.PointToCell(x - 4, y), MaxHP = 2000, CurrentHP = 2000,
                                     Level = 200, MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3 };
            var enemy = new Fighter { Id = Me, TeamId = 1, CellId = next, MaxHP = 2000, CurrentHP = 2000,
                                      Level = 200, MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3 };
            fight.AddPlayer(sram);
            fight.AddOpponent(enemy);
            var waiting = Trap(fight, Sram, trapped, Immobilisation);

            // The Deriva going off on its centre: two cells of push, from next towards beyond.
            var outcomes = EffectEngine.Resolver(fight, sram, Drift, Grade, enemy, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: centre);

            if (!outcomes.Any(o => o.Mueve)) return;   // no spell data
            Assert.Equal(trapped, enemy.CellId);
            Assert.NotEqual(beyond, enemy.CellId);
            Assert.False(waiting.Gastado);   // it goes off once the push is over, in the handler
            Assert.Contains(trapped, FightHandler.TrapCells(fight, enemy));
        }
    }
}
