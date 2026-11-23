using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Clodrum (16697) of Tanukoui San's Workshop: damage 99 and a pull of 4, both "A", in a
    /// circle of 4 around her. The carved stones (6224) and tanuki jars (6225) her side summons
    /// stand on her side and take none of it; her own "5 a" push 4 is what moves them.
    /// </summary>
    public class TamburradaSideTests
    {
        [Theory]
        [InlineData(6224)]
        [InlineData(6225)]
        public async Task It_leaves_the_summons_of_her_side_alone(int template)
        {
            if (SpellEffects.De(16697, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var (x, y) = MapGeometry.CellToPoint(300);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = MapGeometry.PointToCell(x + 3, y), MaxHP = 5000, CurrentHP = 5000,
                Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var caster = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 300, MaxHP = 3000, CurrentHP = 3000, Level = 130,
                IsMonster = true, MonsterId = 6221, MaxAP = 10, CurrentAP = 10, MaxMP = 3, CurrentMP = 3,
            };
            fight.AddPlayer(player);
            fight.AddMonster(caster);
            fight.StartFight();
            var summon = new Fighter
            {
                Id = -2, CellId = MapGeometry.PointToCell(x, y + 2), MaxHP = 1000, CurrentHP = 1000, Level = 130,
                IsMonster = true, MonsterId = template, SummonCost = 1,
            };
            fight.Invocar(summon, caster);

            await FightHandler.LanzarPorOrdenAsync(null, fight, caster, 16697, 1, caster, caster.CellId,
                                                   EffectEngine.AlLanzar, SpellEffects.De(16697, 1).ToList());

            Assert.True(player.CurrentHP < 5000, "the player in the circle is hit");
            Assert.Equal(1000, summon.CurrentHP);
        }
    }
}
