using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// King Dazahk's Impatience (10346): at its third level (state 695) grade 3 gives him MP,
    /// damage and state 697, and arms 10347 under M (moved) and PMD (pushed into a fighter). 10347
    /// takes resistance off him: the only way under his resistances.
    /// </summary>
    [Collection("MapManager")]
    public class DazahkImpatienceTests
    {
        private const int Impatience = 10346;
        private const int ThirdLevel = 695;
        private const int EveryElementResistance = 101;

        private static Fighter Player(int cell) => new()
        {
            Id = 9_970_501, Name = "P", CellId = cell, Level = 200, MaxHP = 5000, CurrentHP = 5000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        private static Fighter Dazahk(int cell) => new()
        {
            Id = -1, Name = "Dazahk", IsMonster = true, CellId = cell, Level = 200, MaxHP = 26000,
            CurrentHP = 26000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = 5319,
        };

        [Fact]
        public async Task Moving_him_at_the_third_level_takes_resistance_off_him()
        {
            var player = Player(250);
            var boss = Dazahk(259);
            var fight = new FightInstance(5319, 990_005_319);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            player.CellId = 250; boss.CellId = 259;
            fight.StartFight();

            boss.Buffs.PonerEstado(ThirdLevel);
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Impatience, 3, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            int before = boss.Buffs.De(EveryElementResistance, fight.RoundNumber);

            // The same Impatience swaps 695 for 697 before he is pushed: what was armed holds.
            boss.Buffs.QuitarEstado(ThirdLevel);
            await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlSerMovido);
            Assert.True(boss.Buffs.De(EveryElementResistance, fight.RoundNumber) < before);
        }
    }
}
