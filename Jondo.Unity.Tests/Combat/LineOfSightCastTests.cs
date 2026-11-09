using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A spell whose level asks for line of sight (CastTestLos) is refused at a cell the caster
    /// does not see -- a fighter standing between -- as the client refuses to offer it; with the
    /// line clear it goes through.
    /// </summary>
    public class LineOfSightCastTests
    {
        private const long Sram = 54000000121, Foe = 54000000122, Wall = 54000000123;
        private const int Rebanacuellos = 12935;

        private static async Task CastAsHim(FightInstance fight, PortalTests.Wire mine, int spell, int cell)
        {
            var active = (System.Collections.Concurrent.ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            active[fight.FightId] = fight;
            try
            {
                using (SessionContext.Push(mine.Session))
                    await FightHandler.CastAsync(mine.Session.Stream, ConnectionProtocol.Push(Op.Jwh, Pb.New().Var(1, cell).Var(4, spell).Build()));
            }
            finally { active.TryRemove(fight.FightId, out _); }
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Behind_a_fighter_the_cast_is_refused(bool blocked, bool cast)
        {
            var fight = new FightInstance(Sram, 1);
            var sram = new Fighter { Id = Sram, Breed = 4, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var wall = new Fighter { Id = Wall, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(sram); fight.AddOpponent(foe); fight.AddOpponent(wall);
            var (x, y) = MapGeometry.CellToPoint(300);
            sram.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 3, y);
            wall.CellId = blocked ? MapGeometry.PointToCell(x + 2, y) : MapGeometry.PointToCell(x, y + 4);
            fight.StartFight();
            await using var mine = await PortalTests.Wire.Open(Sram);
            Assert.Same(sram, fight.CurrentFighter);

            await CastAsHim(fight, mine, Rebanacuellos, foe.CellId);
            Assert.Equal(cast, sram.CurrentAP < 12);
        }
    }
}
