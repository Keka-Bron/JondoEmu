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
    /// Imantación: «Atrae las bombas del lanzador hacia el centro y les añade 1 combo en zona. El
    /// combo solo se aplica una vez por bomba por turno y solo si las bombas son desplazadas.»
    /// </summary>
    /// <remarks>
    /// The combo is a hook: «1160 20676 under MA» on each bomb in the zone, armed at the cast --
    /// the capture registers it on a bomb that is not moved at all -- and set off by the pull.
    /// 20676 climbs the ladder once and takes Imantación's effects off the bomb. Armed on nobody,
    /// the pull moved the bombs and no combo came (the Rogue's combo scenario: 3 -> 3).
    /// </remarks>
    public class ImantacionComboTests
    {
        private const int Imantacion = 13437;
        private readonly ITestOutputHelper _out;
        public ImantacionComboTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public async Task A_bomb_pulled_in_gets_one_combo_and_one_left_where_it_was_none()
        {
            var fight = new FightInstance(54000000151, 1);
            var rogue = new Fighter { Id = 54000000151, Breed = 13, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, CellId = 300 };
            var foe = new Fighter { Id = 54000000152, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(rogue); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            foe.CellId = MapGeometry.PointToCell(x - 5, y + 5);
            Fighter Bomb(long id, int cell) => new()
            {
                Id = id, TeamId = rogue.TeamId, CellId = cell, MaxHP = 900, CurrentHP = 900, IsMonster = true, MonsterId = 3112,
                GradeIndex = 3, Invocador = rogue.Id, SummonCost = 0, JuegaTurno = false, Level = 3,
            };
            var far = Bomb(-2, MapGeometry.PointToCell(x + 3, y));
            var near = Bomb(-3, MapGeometry.PointToCell(x, y + 1));
            fight.Invocar(far, rogue); fight.Invocar(near, rogue);
            foreach (var bomb in new[] { far, near })
                EffectEngine.Resolver(fight, bomb, Combo.LadderSpell, 1, bomb, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: bomb.CellId);
            Assert.Equal(1, Combo.LevelOf(far));
            Assert.Equal(1, Combo.LevelOf(near));
            int from = far.CellId;

            await FightHandler.CastEffectsAsync(null, fight, rogue, Imantacion, 3, rogue, rogue.CellId, false,
                                                EffectEngine.EfectosSorteados(Imantacion, 3, false));
            _out.WriteLine($"far bomb {from} -> {far.CellId}, combo {Combo.LevelOf(far)}; near bomb on {near.CellId}, combo {Combo.LevelOf(near)}");
            Assert.NotEqual(from, far.CellId);
            Assert.Equal(2, Combo.LevelOf(far));
            Assert.Equal(1, Combo.LevelOf(near));
        }
    }
}
