using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Captain Scarlet (3753) in his Ring. His behaviour spell, 4804, hooks "1160 4802 under D on
    /// C": every time he takes damage he casts 4802 on himself, which sends him back to his
    /// last cell (1100; its 1109 heal only lands on a telefrag), and his 786 rows heal the
    /// attacker by the damage dealt. The hook stays armed past the second round.
    /// </summary>
    public class ScarletCaptainTests
    {
        private const int Captain = 3753, Behaviour = 4804;

        [Fact]
        public async Task Every_hit_he_takes_casts_his_hook()
        {
            if (SpellEffects.De(Behaviour, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var attacker = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 272, MaxHP = 20000, CurrentHP = 20000, Level = 200,
                IsMonster = true, MonsterId = Captain, MaxAP = 12, CurrentAP = 12, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(attacker);
            fight.AddMonster(boss);
            fight.StartFight();
            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);

            for (int hit = 1; hit <= 3; hit++)
            {
                boss.Andar(new[] { 272, 286, 314 });
                attacker.CurrentHP = 3000;
                boss.CurrentHP -= 500;
                fight.TriggeringAttacker = attacker; fight.DanoDelDisparo = 500;
                await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlRecibirDano);
                fight.TriggeringAttacker = null; fight.DanoDelDisparo = 0;
                Assert.Equal(286, boss.CellId);
                Assert.True(attacker.CurrentHP > 3000, $"hit {hit}: attacker {attacker.CurrentHP}");
                int round = fight.RoundNumber;
                while (fight.RoundNumber == round) fight.NextTurn();
                foreach (var f in fight.Todos) f.Buffs.BarrerEnganches(fight.RoundNumber);
            }
        }
    }
}
