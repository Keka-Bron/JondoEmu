using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Solar's Nadir aura: 9439 lays an aura glyph (1091) of 9443 grade 2, -4 range with no end of
    /// its own, taken back when one leaves it. Walking from one of its cells to another fired it
    /// again at every step, and the range went down to -14.
    /// </summary>
    public class SolarNadirAuraTests
    {
        private const int NadirAura = 9443;

        // Its range row names the ring it covers (a ';' zone): the test stands on three of them.
        [Fact]
        public async Task Walking_inside_the_aura_takes_the_range_once()
        {
            if (!SpellEffects.De(NadirAura, 2).Any(row => row.EffectId == 116)) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var player = new Fighter
            {
                Id = 10, CellId = 266, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(player);
            var solar = new Fighter
            {
                Id = -1, CellId = 200, MaxHP = 19000, CurrentHP = 19000, Level = 200, IsMonster = true,
                MonsterId = 1, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            fight.AddMonster(solar);
            fight.StartFight();
            var aura = fight.Poner(new Glifo(solar.Id, new[] { 266, 253, 225 }, NadirAura, 2, 0, -1, "A",
                                             Disparo.AlPisarYAlEmpezar));
            aura.Tipo = 1091;

            int RangeTaken() => player.Buffs.De(19, fight.RoundNumber);   // range

            await FightHandler.DispararLosGlifosAsync(null, fight, player, alPisar: true);
            int once = RangeTaken();
            Assert.True(once < 0);
            foreach (int cell in new[] { 253, 225 })
            {
                player.CellId = cell;
                await FightHandler.DispararLosGlifosAsync(null, fight, player, alPisar: true);
            }
            Assert.Equal(once, RangeTaken());

            // Out of it, the range comes back; in again, it goes once more.
            player.CellId = 400;
            await FightHandler.DispararLosGlifosAsync(null, fight, player, alPisar: true);
            Assert.Equal(0, RangeTaken());
            player.CellId = 266;
            await FightHandler.DispararLosGlifosAsync(null, fight, player, alPisar: true);
            Assert.Equal(once, RangeTaken());
        }
    }
}
