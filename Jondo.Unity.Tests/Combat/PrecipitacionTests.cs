using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Iop's Precipitación: «Aumenta los PA del aliado objetivo y le aplica el estado
    /// Debilitado. Retira PA no esquivables a este aliado en el turno siguiente. El estado no se
    /// aplica al lanzador.» Two AP, +5 AP now, -3 AP the next turn.
    /// </summary>
    public class PrecipitacionTests
    {
        private const long Iop = 54000000121, Ally = 54000000122, Foe = 54000000123;
        private const int Precipitacion = 13114, Debilitado = 42;
        private readonly ITestOutputHelper _out;
        public PrecipitacionTests(ITestOutputHelper output) => _out = output;

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

        private static (FightInstance Fight, Fighter Iop, Fighter Ally) Board()
        {
            var fight = new FightInstance(Iop, 1);
            var iop = new Fighter { Id = Iop, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3 };
            var ally = new Fighter { Id = Ally, Breed = 7, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3 };
            fight.AddPlayer(iop); fight.AddPlayer(ally); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            iop.CellId = 300;
            ally.CellId = MapGeometry.PointToCell(x + 2, y);
            foe.CellId = MapGeometry.PointToCell(x + 6, y);
            fight.StartFight();
            return (fight, iop, ally);
        }

        [Fact]
        public async Task On_himself_it_costs_two_and_gives_five()
        {
            var (fight, iop, _) = Board();
            await using var wire = await PortalTests.Wire.Open(Iop);
            Assert.Equal(Iop, fight.CurrentFighter?.Id);
            await CastAsHim(fight, wire, Precipitacion, iop.CellId);
            _out.WriteLine($"Precipitación on himself: AP 7 -> {iop.CurrentAP}, state 42 {iop.Buffs.TieneEstado(Debilitado)}");
            Assert.Equal(7 - 2 + 5, iop.CurrentAP);
            Assert.False(iop.Buffs.TieneEstado(Debilitado), "the state is not for the caster");
        }

        [Fact]
        public async Task On_an_ally_he_pays_two_and_the_ally_gets_five_and_the_state()
        {
            var (fight, iop, ally) = Board();
            await using var wire = await PortalTests.Wire.Open(Iop);
            await CastAsHim(fight, wire, Precipitacion, ally.CellId);
            _out.WriteLine($"Precipitación on an ally: Iop AP 7 -> {iop.CurrentAP}, state 42 {ally.Buffs.TieneEstado(Debilitado)}");
            Assert.Equal(5, iop.CurrentAP);
            Assert.True(ally.Buffs.TieneEstado(Debilitado));
            // The five are his on his turn, the one that follows; the three go on his next one.
            // The turns pass as the server passes them, with what each start fires.
            var active = (System.Collections.Concurrent.ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            active[fight.FightId] = fight;
            try
            {
                async Task ToHisTurn()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        using (SessionContext.Push(wire.Session))
                        {
                            await FightHandler.PassTurnAsync(wire.Session.Stream);
                            await FightHandler.ConfirmAsync(wire.Session.Stream);
                        }
                        if (fight.CurrentFighter?.Id == Ally) return;
                    }
                }
                int round = fight.RoundNumber;
                await ToHisTurn();
                _out.WriteLine($"his turn in round {fight.RoundNumber} (cast in {round}): AP {ally.CurrentAP}");
                Assert.Equal(12, ally.CurrentAP);
                await ToHisTurn();
                _out.WriteLine($"his next turn in round {fight.RoundNumber}: AP {ally.CurrentAP}, state 42 {ally.Buffs.TieneEstado(Debilitado)}");
                Assert.Equal(7 - 3, ally.CurrentAP);
            }
            finally { active.TryRemove(fight.FightId, out _); }
        }
    }
}
