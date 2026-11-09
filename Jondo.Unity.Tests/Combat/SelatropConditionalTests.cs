using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Two Selatrop spells whose effect waits on something: Distribución («Cura a los aliados una
    /// parte de los daños ocasionados al objetivo en zona a su alrededor») and Conjuro («Reduce la
    /// duración de los efectos en el objetivo»).
    /// </summary>
    public class SelatropConditionalTests
    {
        private const long Sela = 54000000111, Ally = 54000000112, Foe = 54000000113;
        private readonly ITestOutputHelper _out;
        public SelatropConditionalTests(ITestOutputHelper output) => _out = output;

        private static (FightInstance Fight, Fighter Sela, Fighter Ally, Fighter Foe) Board()
        {
            var fight = new FightInstance(Sela, 1);
            var sela = new Fighter { Id = Sela, Breed = 16, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 30, CurrentAP = 30, MaxMP = 6, CurrentMP = 6 };
            var ally = new Fighter { Id = Ally, Breed = 8, MaxHP = 4000, CurrentHP = 2000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(sela); fight.AddPlayer(ally); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            sela.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 3, y);
            ally.CellId = MapGeometry.PointToCell(x + 3, y + 1);
            return (fight, sela, ally, foe);
        }

        [Fact]
        public async Task Distribucion_heals_the_allies_round_the_target_when_he_is_hurt()
        {
            var (fight, sela, ally, foe) = Board();
            await FightHandler.CastEffectsAsync(null, fight, sela, 14577, 3, foe, foe.CellId, false,
                                                EffectEngine.EfectosSorteados(14577, 3, false));
            int allyLife = ally.CurrentHP, foeLife = foe.CurrentHP;
            Assert.Contains(foe.Buffs.ActiveSpells, h => h.Hechizo == 14577);
            // A blow on him: Puño Relámpago, the Selatrop's own.
            const int blow = 14576;
            await FightHandler.CastEffectsAsync(null, fight, sela, blow, 1, foe, foe.CellId, false,
                                                EffectEngine.EfectosSorteados(blow, 1, false));
            _out.WriteLine($"blow {blow}: foe {foeLife} -> {foe.CurrentHP}, ally {allyLife} -> {ally.CurrentHP}");
            Assert.True(foe.CurrentHP < foeLife, "the blow did not land");
            Assert.True(ally.CurrentHP > allyLife, "Distribución healed nobody round him");
        }

        [Fact]
        public async Task Conjuro_shortens_the_effects_on_its_target()
        {
            var (fight, sela, _, foe) = Board();
            var row = foe.Buffs.Poner(new Buff { EffectId = 111, Caracteristica = 1, Cuanto = 1, CaducaEnRonda = 4, EmpiezaEnRonda = 0, Disparador = "I", HechizoOrigen = 1 }, () => 1);
            await FightHandler.CastEffectsAsync(null, fight, sela, 14622, 2, foe, foe.CellId, false,
                                                EffectEngine.EfectosSorteados(14622, 2, false));
            _out.WriteLine($"Conjuro: the row now ends in round {row.CaducaEnRonda} (was 4)");
            Assert.Equal(3, row.CaducaEnRonda);
        }
    }
}
