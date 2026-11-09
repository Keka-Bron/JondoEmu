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
    /// Vértebra: «Aplica un veneno de agua de inicio de turno sobre el enemigo objetivo». Its
    /// sub-spell's rows wait on the target's turn start for two turns (effectTriggerDuration 2).
    /// </summary>
    public class VertebraTests
    {
        private const int Vertebra = 13800;
        private readonly ITestOutputHelper _out;
        public VertebraTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public async Task Its_poison_hurts_at_the_target_s_turn_start()
        {
            var fight = new FightInstance(1, 1);
            var ugi = new Fighter { Id = 1, Breed = 18, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = 2, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(ugi); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            ugi.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 3, y);

            await FightHandler.CastEffectsAsync(null, fight, ugi, Vertebra, 1, foe, foe.CellId, false,
                                                EffectEngine.EfectosSorteados(Vertebra, 1, false));
            _out.WriteLine($"after the cast: life {foe.CurrentHP}, hooks [{string.Join(", ", foe.Buffs.ActiveSpells.Select(h => h.Hechizo))}], rows {foe.Buffs.Puestos.Count}");
            int afterCast = foe.CurrentHP;

            await FightHandler.EngancheAsync(null, fight, foe, EffectEngine.AlEmpezarElTurno);
            _out.WriteLine($"after his turn start: life {foe.CurrentHP}");
            Assert.True(foe.CurrentHP < afterCast, "the poison did not go off at his turn start");
        }
    }
}
