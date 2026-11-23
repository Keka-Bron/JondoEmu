using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Crocantería boss (5664). His behaviour, 12291, arms 12292 at the grade of the element
    /// he was hit with (fire 1, air 2, water 3, earth 4). Each grade summons its own pepita (5665,
    /// through 12293) when he is moved, but only in its own phase: "C,*E1088" for the first,
    /// "C,*E1089" for the second, and so on.
    /// </summary>
    public class CrunchidorPepitaTests
    {
        private const int Boss = 5664, Behaviour = 12291, Pepita = 5665;

        [Theory]
        [InlineData("DF,DA")]
        [InlineData("DA")]
        [InlineData("DF")]
        public async Task A_push_summons_only_the_pepita_of_his_phase(string hits)
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
                Id = -1, TeamId = 1, CellId = 272, MaxHP = 10000, CurrentHP = 10000, Level = 200,
                IsMonster = true, MonsterId = Boss, MaxAP = 12, CurrentAP = 12, MaxMP = 3, CurrentMP = 3,
            };
            fight.AddPlayer(attacker);
            fight.AddMonster(boss);
            fight.StartFight();
            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            boss.Buffs.PonerEstado(1089);

            foreach (var element in hits.Split(','))
            {
                fight.TriggeringAttacker = attacker; fight.DanoDelDisparo = 100;
                await FightHandler.DispararAsync(null, fight, boss, element);
                fight.TriggeringAttacker = null; fight.DanoDelDisparo = 0;
            }
            fight.TriggeringAttacker = attacker;
            await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlSerMovido);
            fight.TriggeringAttacker = null;

            var pepitas = fight.Todos.Where(f => f.MonsterId == Pepita).Select(f => f.MaxHP).ToList();
            Assert.True(pepitas.Count == (hits.Contains("DA") ? 1 : 0), hits + " pepitas: " + string.Join(",", pepitas));
        }
    }
}
