using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Tanukis of Tanukoui San's Workshop arm 16707 on themselves: grade 2 under DM (a melee
    /// hit) is "1041 Moves back 2" and grade 3 under DR (a ranged hit) is "1042 Moves forward 2",
    /// both "A,O,*E3505". The hook casts at the Tanuki's own cell; what he steps away from or
    /// towards is the attacker.
    /// </summary>
    public class TanukiGlyphStepTests
    {
        private const int Step = 16707, GlyphState = 3505;

        [Theory]
        [InlineData(2, 1, 3)]
        [InlineData(3, 4, 2)]
        public async Task A_hit_steps_the_tanuki_relative_to_the_attacker(int grade, int attackerDx, int expectedDistance)
        {
            if (SpellEffects.De(Step, grade).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var (tx, ty) = MapGeometry.CellToPoint(300);
            var attacker = new Fighter
            {
                Id = 10, TeamId = 0, CellId = MapGeometry.PointToCell(tx + attackerDx, ty), MaxHP = 5000, CurrentHP = 5000,
                Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var tanuki = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 300, MaxHP = 3000, CurrentHP = 3000, Level = 130,
                IsMonster = true, MonsterId = 6221, MaxAP = 10, CurrentAP = 10, MaxMP = 3, CurrentMP = 3,
            };
            fight.AddPlayer(attacker);
            fight.AddMonster(tanuki);
            fight.StartFight();
            tanuki.Buffs.PonerEstado(GlyphState);

            fight.TriggeringAttacker = attacker;
            await FightHandler.AplicarEfectosAsync(null, fight, tanuki, Step, grade, tanuki,
                                                   EffectEngine.AlLanzar, tanuki.CellId);
            fight.TriggeringAttacker = null;

            Assert.Equal(expectedDistance, MapGeometry.Distance(tanuki.CellId, attacker.CellId));
        }
    }
}
