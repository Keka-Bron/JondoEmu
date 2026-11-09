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
    /// Fulgor: «Teletransporta al lanzador a la primera casilla disponible entre el objetivo y él
    /// y ocasiona daños de aire a los enemigos en zona». Its rows go along the line from the
    /// caster to the aimed cell (l1/63, stopping there).
    /// </summary>
    public class FulgorTests
    {
        private const int Fulgor = 12724;
        private readonly ITestOutputHelper _out;
        public FulgorTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public async Task It_lands_beside_the_target_and_hurts_it()
        {
            var fight = new FightInstance(1, 1);
            var sacro = new Fighter { Id = 1, Breed = 11, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = 2, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(sacro); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            sacro.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 3, y);
            int beside = MapGeometry.PointToCell(x + 2, y);

            await FightHandler.CastEffectsAsync(null, fight, sacro, Fulgor, 2, foe, foe.CellId, false,
                                                EffectEngine.EfectosSorteados(Fulgor, 2, false));
            _out.WriteLine($"sacro on {sacro.CellId} (beside is {beside}), foe life {foe.CurrentHP}");
            Assert.Equal(beside, sacro.CellId);
            Assert.True(foe.CurrentHP < 4000, "Fulgor did not hurt the foe it was aimed at");
        }
    }
}
