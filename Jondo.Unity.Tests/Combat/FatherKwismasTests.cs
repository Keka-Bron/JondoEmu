using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Father Kwismas (872): his Cake (883) gives him state 16, which his Dichotomy (881) needs
    /// and takes away, leaving him in state 17. His other spells wait on "not 16".
    /// </summary>
    [Collection("MapManager")]
    public class FatherKwismasTests
    {
        private const int Klaus = 872, Cake = 883, Dichotomy = 881;

        [Fact]
        public async Task His_dichotomy_takes_away_the_cakes_state()
        {
            if (!SpellEffects.De(Dichotomy, 5).Any(row => row.EffectId == 951)) return;   // no spell data
            var player = new Fighter
            {
                Id = 9_970_872, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "K", IsMonster = true, Level = 180, MaxHP = 6000, CurrentHP = 6000, TeamId = 1,
                MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = Klaus,
            };
            var fight = new FightInstance(990_000_872, 0);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            player.CellId = 250; boss.CellId = 280;
            fight.StartFight();

            await FightHandler.AplicarEfectosAsync(null, fight, boss, Cake, 5, boss, EffectEngine.AlLanzar, boss.CellId);
            Assert.True(boss.Buffs.TieneEstado(16));
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Dichotomy, 5, boss, EffectEngine.AlLanzar, 251);
            Assert.True(boss.Buffs.TieneEstado(17));
            Assert.False(boss.Buffs.TieneEstado(16));
        }
    }
}
