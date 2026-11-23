using System.Linq;
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

        [Fact]
        public async Task Cast_by_his_behaviour_at_his_turn_start_the_third_level_still_opens_him()
        {
            // Live, grade 3 is no cast of its own: the behaviour (grade 1) casts it under TB on
            // King Dazahk, and the player pushes him in a later turn.
            var player = Player(250);
            var boss = Dazahk(259);
            var fight = new FightInstance(5319, 990_005_320);
            fight.AddMonster(boss);
            fight.AddPlayer(player);
            player.CellId = 250; boss.CellId = 259;
            fight.StartFight();
            boss.Conducta = (Impatience, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);

            // Six turns of his before the third level: grade 3 at every start, grade 2 at every end.
            for (int turn = 0; turn < 6; turn++)
            {
                while (fight.CurrentFighter != boss) fight.NextTurn();
                await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlEmpezarElTurno);
                await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
                fight.NextTurn();
            }
            while (fight.CurrentFighter != boss) fight.NextTurn();
            foreach (int level in new[] { 693, 694 }) boss.Buffs.QuitarEstado(level);
            boss.Buffs.PonerEstado(ThirdLevel);
            await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlEmpezarElTurno);
            Assert.True(boss.Buffs.TieneEstado(697));
            // Live, 695 is a row of 10332, which the same grade 3 takes off (406).
            boss.Buffs.QuitarEstado(ThirdLevel);
            int before = boss.Buffs.De(EveryElementResistance, fight.RoundNumber);

            // His next turn under 697: the behaviour casts grade 3 again, with 695 gone.
            int round = fight.RoundNumber;
            while (fight.CurrentFighter != boss || fight.RoundNumber == round) fight.NextTurn();
            await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlEmpezarElTurno);
            Assert.True(boss.Buffs.TieneEstado(697));

            while (fight.CurrentFighter != player) fight.NextTurn();   // the player's turn, after his
            fight.TriggeringAttacker = player;
            await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlSerMovido);
            fight.TriggeringAttacker = null;
            Assert.True(boss.Buffs.De(EveryElementResistance, fight.RoundNumber) < before);
        }
    }
}
