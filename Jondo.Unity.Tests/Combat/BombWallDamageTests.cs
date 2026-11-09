using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What a bomb wall hits for, against the class sheet: «Los muros se benefician de la mitad
    /// del combo para infligir sus daños. Asimismo, los daños de los muros se han reducido
    /// considerablemente fuera del turno del tymador y de sus invocaciones.»
    /// </summary>
    /// <remarks>
    /// The reduction is the wall spells' own: each carries its blow twice, 21 to 24 (Muro de
    /// Fuego) on «the caster holds state 2483» -- which «La Astucia del Tymador» puts on him at his
    /// turn start and takes away at its end -- and 11 to 12 on «he does not».
    /// </remarks>
    public class BombWallDamageTests
    {
        private const int MuroDeFuego = 13458;
        private const int SuTurno = 2483;
        private readonly ITestOutputHelper _out;
        public BombWallDamageTests(ITestOutputHelper output) => _out = output;

        private static (FightInstance Fight, Fighter Rogue, Fighter Foe, Fighter A, Fighter B) Board()
        {
            var fight = new FightInstance(54000000131, 1);
            var rogue = new Fighter { Id = 54000000131, Breed = 13, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = 54000000132, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(rogue); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            rogue.CellId = MapGeometry.PointToCell(x - 4, y);
            foe.CellId = 300;
            // Two fire bombs, the foe on the middle of the three cells between them.
            Fighter Bomb(long id, int cell) => new()
            {
                Id = id, TeamId = rogue.TeamId, CellId = cell, MaxHP = 90, CurrentHP = 90, IsMonster = true, MonsterId = 3112,
                GradeIndex = 3, Invocador = rogue.Id, SummonCost = 0, JuegaTurno = false,
            };
            var a = Bomb(-2, MapGeometry.PointToCell(x, y - 2));
            var b = Bomb(-3, MapGeometry.PointToCell(x, y + 2));
            fight.AddPlayer(a); fight.AddPlayer(b);
            return (fight, rogue, foe, a, b);
        }

        private static async Task<int> WallBlow(FightInstance fight, Fighter rogue, Fighter foe)
        {
            int life = foe.CurrentHP;
            await FightHandler.CastEffectsAsync(null, fight, rogue, MuroDeFuego, 1, foe, foe.CellId, false,
                                                EffectEngine.EfectosSorteados(MuroDeFuego, 1, false));
            int lost = life - foe.CurrentHP;
            foe.CurrentHP = life;
            return lost;
        }

        private static void Climb(FightInstance fight, Fighter bomb, int level)
        {
            while (Combo.LevelOf(bomb) < level)
                EffectEngine.Resolver(fight, bomb, Combo.LadderSpell, 1, bomb, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: bomb.CellId);
        }

        [Fact]
        public async Task In_the_rogue_s_turn_a_fire_wall_burns_21_to_24_and_outside_it_11_to_12()
        {
            var (fight, rogue, foe, _, _) = Board();
            Assert.NotNull(BombWalls.Covering(fight.Todos, rogue, foe.CellId));
            rogue.Buffs.PonerEstado(SuTurno);
            var inTurn = Enumerable.Range(0, 30).Select(_ => WallBlow(fight, rogue, foe).Result).ToList();
            rogue.Buffs.QuitarEstado(SuTurno);
            var outside = Enumerable.Range(0, 30).Select(_ => WallBlow(fight, rogue, foe).Result).ToList();
            _out.WriteLine($"in his turn {inTurn.Min()}-{inTurn.Max()}, outside {outside.Min()}-{outside.Max()}");
            Assert.All(inTurn, d => Assert.InRange(d, 21, 24));
            Assert.All(outside, d => Assert.InRange(d, 11, 12));
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 10)]
        [InlineData(5, 40)]
        [InlineData(10, 95)]
        [InlineData(15, 180)]
        public async Task A_wall_hits_with_half_the_combo_of_its_highest_bomb(int level, int halfPercent)
        {
            var (fight, rogue, foe, a, b) = Board();
            Climb(fight, a, level);
            Climb(fight, b, 1);
            rogue.Buffs.PonerEstado(SuTurno);
            var blows = Enumerable.Range(0, 30).Select(_ => WallBlow(fight, rogue, foe).Result).ToList();
            int low = (int)System.Math.Round(21 * (100 + halfPercent) / 100.0), high = (int)System.Math.Round(24 * (100 + halfPercent) / 100.0);
            _out.WriteLine($"Combo {level} (+{halfPercent}%): {blows.Min()}-{blows.Max()}, expected {low}-{high}");
            Assert.All(blows, d => Assert.InRange(d, low, high));
        }
    }
}
