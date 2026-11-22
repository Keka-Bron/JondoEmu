using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Menace Creciente (19791), cast by every Ritual de Kabaal monster: it lays the arm's rune (19793)
    /// on the aimed cell, and a turn later its delayed 19792 sets off the runes round that cell,
    /// so the arm (6655) comes out of an empty rune at the caster's next turn. Cast on the
    /// caster's own cell, 19792 found no rune, and the arms came out only when someone stepped on one.
    /// </summary>
    [Collection("MapManager")]
    public class RuneArmTests
    {
        private const int MenaceCreciente = 19791;
        private const int Arm = 6655;
        private const int RuneCell = 300;

        private static Fighter Player(int cell) => new()
        {
            Id = 9_970_301, Name = "P", CellId = cell, Level = 200, MaxHP = 5000, CurrentHP = 5000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        private static Fighter Aninferal(int cell) => new()
        {
            Id = -1, Name = "Aninferal", IsMonster = true, CellId = cell, Level = 200, MaxHP = 4000,
            CurrentHP = 4000, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = 6656,
        };

        [Fact]
        public async Task The_arm_comes_out_of_the_empty_rune_at_the_caster_s_next_turn()
        {
            var player = Player(250);
            var caster = Aninferal(100);
            var fight = new FightInstance(4106, 990_004_106);
            fight.AddPlayer(player);
            player.CellId = 250;
            fight.AddMonster(caster);
            caster.CellId = 100;

            await FightHandler.AplicarEfectosAsync(null, fight, caster, MenaceCreciente, 1, null,
                                                   EffectEngine.AlLanzar, RuneCell);
            Assert.Contains(fight.Glifos, g => g.Dueno == caster.Id && g.Casillas.Contains(RuneCell));
            var waiting = Assert.Single(caster.Buffs.Puestos, b => b.Pendiente);
            Assert.Equal(RuneCell, waiting.PendingCastCell);

            for (int turn = 0; turn < 6 && !(fight.RoundNumber >= 2 && fight.CurrentFighter == caster); turn++)
                fight.NextTurn();
            Assert.Equal(caster, fight.CurrentFighter);
            await FightHandler.ApplyDuePendingAsync(null, fight);

            var arm = Assert.Single(fight.Todos, f => f.IsAlive && f.MonsterId == Arm);
            Assert.Equal(RuneCell, arm.CellId);
        }
    }
}
