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
    /// Trance: «Sacrifica una parte de la vida del lanzador para aplicar un escudo a los aliados en
    /// zona». The life goes, and the shield it buys stays whole: it does not soak up its own cost.
    /// </summary>
    public class TranceTests
    {
        private const int Trance = 13399;
        private readonly ITestOutputHelper _out;
        public TranceTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public async Task The_life_is_paid_and_the_shield_stays_whole()
        {
            var fight = new FightInstance(1, 1);
            var zobal = new Fighter { Id = 1, Breed = 14, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = 2, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(zobal); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            zobal.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 5, y);

            await FightHandler.CastEffectsAsync(null, fight, zobal, Trance, 2, zobal, zobal.CellId, false,
                                                EffectEngine.EfectosSorteados(Trance, 2, false));
            _out.WriteLine($"Trance: life {zobal.CurrentHP}/{zobal.MaxHP}, shield {zobal.PuntosDeEscudo}");
            Assert.True(zobal.CurrentHP < 4000, "the life was not paid");
            Assert.Equal(900, zobal.PuntosDeEscudo);
        }
    }
}
