using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Forgelance's throws (Lanzapiedras, Lanza Ciclón, Jabalina Keatina, Jabalina Rayo...):
    /// «invoca a la lanza» on the aimed cell when it is or ends up free, with no lance on the
    /// ground (Armado) or standing beside it (3589), which takes the old one back first.
    /// </summary>
    public class LanceThrowTests
    {
        private const long Forge = 54000000091, Foe = 54000000092;
        private const int Lance = 7139;
        private readonly ITestOutputHelper _out;
        public LanceThrowTests(ITestOutputHelper output) => _out = output;

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
        [InlineData(23719)] // Lanzapiedras
        [InlineData(23729)] // Lanza Ciclón
        [InlineData(23392)] // Jabalina Rayo
        [InlineData(23828)] // Oriflama
        public async Task A_throw_with_no_lance_out_lays_it_on_the_free_cell(int spell)
        {
            var fight = new FightInstance(Forge, 1);
            var forge = new Fighter { Id = Forge, Breed = 20, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(forge); fight.AddOpponent(foe);
            // Armado, as the fight's start leaves him with no lance out.
            forge.Buffs.PonerEstado(3360);
            var (x, y) = MapGeometry.CellToPoint(300);
            forge.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x, y + 5);
            int aim = MapGeometry.PointToCell(x + 2, y);
            fight.StartFight();
            await using var mine = await PortalTests.Wire.Open(Forge);
            Assert.Same(forge, fight.CurrentFighter);

            await CastAsHim(fight, mine, spell, aim);

            var lances = fight.Todos.Where(f => f.IsAlive && f.EsInvocado && f.MonsterId == Lance).ToList();
            _out.WriteLine($"spell {spell}: aimed at {aim}, lance(s) on [{string.Join(", ", lances.Select(l => l.CellId))}], forge on {forge.CellId}, AP {forge.CurrentAP}");
            Assert.True(forge.CurrentAP < 12, "the cast was refused");
            Assert.Single(lances);
        }
        /// <summary>
        /// Balestra: «teletransporta al lanzador hasta el objetivo, lo teletransporta simétricamente
        /// y lo aleja respecto del objetivo, y ocasiona daños de agua» -- he ends past the target.
        /// </summary>
        [Fact]
        public async Task Balestra_jumps_past_its_target_and_hurts_him()
        {
            var fight = new FightInstance(Forge, 1);
            var forge = new Fighter { Id = Forge, Breed = 20, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(forge); fight.AddOpponent(foe);
            forge.Buffs.PonerEstado(3360);
            var (x, y) = MapGeometry.CellToPoint(300);
            forge.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 3, y);
            fight.StartFight();
            await using var mine = await PortalTests.Wire.Open(Forge);

            await CastAsHim(fight, mine, 23434, foe.CellId);
            var (fx, fy) = MapGeometry.CellToPoint(forge.CellId);
            _out.WriteLine($"Balestra: forge 300 -> {forge.CellId} ({fx - x},{fy - y} from where he stood), foe on {foe.CellId}, life {foe.CurrentHP}, AP {forge.CurrentAP}");
            Assert.True(forge.CurrentAP < 12, "the cast was refused");
            Assert.True(foe.CurrentHP < 4000, "Balestra did not hurt its target");
            Assert.True(fx - x > 3, "he did not end up past his target");
        }
    }
}
