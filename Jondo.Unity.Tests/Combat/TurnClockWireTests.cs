using System.Linq;
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
    /// The turn a character is given, as the jzc tells his client: on a server not started for the
    /// test harness it is the real game's 40 s, and the turn clock is armed with the same.
    /// </summary>
    public class TurnClockWireTests
    {
        // These characters and their fight must not share another test's registry keys.
        private const long Hero = 54_000_009_301, Foe = 54_000_009_302;

        [Fact]
        public async Task A_characters_turn_goes_out_at_the_servers_own_clock()
        {
            var fight = new FightInstance(Hero, 1);
            var hero = new Fighter { Id = Hero, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3 };
            fight.AddPlayer(hero); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            hero.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 6, y);
            fight.StartFight();
            Assert.Equal(Hero, fight.CurrentFighter?.Id);

            var active = (System.Collections.Concurrent.ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            Assert.True(active.TryAdd(fight.FightId, fight));
            await using var wire = await PortalTests.Wire.Open(Hero);
            try
            {
                using (SessionContext.Push(wire.Session)) await FightHandler.ConfirmAsync(wire.Session.Stream);

                // The test run is a server not started for the harness unless JONDO_TEST_KEY is
                // set around it; either way the jzc carries what TurnDeciseconds decides.
                int expected = ControlApi.TestKey.Length > 0 ? FightHandler.HarnessTurnDeciseconds : 400;
                var jzc = (await wire.Drain()).Where(f => f.Op == Op.Jzc).Select(f => ProtoMessage.Parse(f.Payload)).First();
                Assert.Equal(Hero, jzc.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
                Assert.Equal(expected, jzc.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
                Assert.Equal(expected, fight.LastAnnouncedTurn.Deciseconds);
                Assert.NotNull(fight.TurnTimerCts);
            }
            finally
            {
                active.TryRemove(fight.FightId, out _);
                fight.CancelTurnTimer();
            }
        }
    }
}
