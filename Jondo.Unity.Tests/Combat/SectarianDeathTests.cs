using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// «La campagne hallucinée»: the sectarians that reach their threshold die at their own turn
    /// and leave a boost on Wilbur (Dofus pour les noobs). Run through the real engine on the
    /// real data: 21990 is "141 on the caster, then the boost on the monster 6940".
    /// </summary>
    [Collection("MapManager")]
    public class SectarianDeathTests
    {
        private const int Wilbur = 6940;
        private const int Devotion = 21990;   // grades 1 to 4, one boost each

        private static Fighter Cultist(long id, int monster, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 3000, CurrentHP = 450, Level = 150,
            IsMonster = true, MonsterId = monster, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
        };

        private static Fighter High(int cell) => new()
        {
            Id = -1, TeamId = 1, CellId = cell, MaxHP = 6900, CurrentHP = 6900, Level = 150,
            IsMonster = true, MonsterId = Wilbur, MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
        };

        /// <summary>
        /// Each grade's boost reaches Wilbur, as the client's effects table has it: +4 MP (128), 25 %
        /// as a shield (1039, told as 1040 with the points), 15 % vitality (2844, as a row of
        /// vitality worth 15 % of his 6,900) and +20 % final damage (1171).
        /// </summary>
        [Theory]
        [InlineData(1, 128, 4)]
        [InlineData(2, 1040, 1725)]
        [InlineData(3, 125, 1035)]
        [InlineData(4, 1171, 20)]
        public void The_boost_a_sectarian_leaves_reaches_Wilbur(int grade, int effect, int amount)
        {
            var fight = new FightInstance(1, 1);
            var wilbur = High(300);
            var cultist = Cultist(-2, 6941, 330);
            var hero = new Fighter { Id = 10, TeamId = 0, CellId = 360, MaxHP = 5000, CurrentHP = 5000, Level = 200 };
            fight.AddMonster(wilbur); fight.AddMonster(cultist); fight.AddPlayer(hero);

            var outcomes = EffectEngine.Resolver(fight, cultist, Devotion, grade, cultist, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: cultist.CellId);

            Assert.Contains(outcomes, o => o.Efecto.EffectId == EffectEngine.MataAlObjetivo && o.Sobre == cultist);
            Assert.Contains(outcomes, o => o.Sobre == wilbur && o.Buff != null && o.Buff.EffectId == effect && o.Buff.Cuanto == amount);
        }

        /// <summary>
        /// A monster's rows on the others go with it when it dies (a Pépite's mark, an Éclat's
        /// invulnerability) -- except the ones its own death spell put: the boost is what the
        /// sectarian dies FOR, and it was lost with it.
        /// </summary>
        [Fact]
        public async Task The_rows_of_the_death_spell_outlive_the_one_who_died_and_the_others_do_not()
        {
            var fight = new FightInstance(1, 1);
            var wilbur = High(300);
            var cultist = Cultist(-2, 6941, 330);
            fight.AddMonster(wilbur); fight.AddMonster(cultist);
            Buff Row(int spell) => wilbur.Buffs.Poner(new Buff { EffectId = 1171, Cuanto = 20, Quien = cultist.Id, HechizoOrigen = spell, CaducaEnRonda = -1 }, () => 1);
            var boost = Row(Devotion);
            var mark = Row(12345);
            cultist.CurrentHP = 0;

            await FightHandler.CaenSusInvocadosAsync(null, fight, cultist, Devotion);

            Assert.Contains(boost, wilbur.Buffs.Puestos);
            Assert.DoesNotContain(mark, wilbur.Buffs.Puestos);
        }

        /// <summary>Without a death spell to spare, everything the dead one put goes, as it always did.</summary>
        [Fact]
        public async Task A_monster_that_dies_by_a_blow_takes_all_its_rows_with_it()
        {
            var fight = new FightInstance(1, 1);
            var wilbur = High(300);
            var cultist = Cultist(-2, 6941, 330);
            fight.AddMonster(wilbur); fight.AddMonster(cultist);
            var row = wilbur.Buffs.Poner(new Buff { EffectId = 1171, Cuanto = 20, Quien = cultist.Id, HechizoOrigen = Devotion, CaducaEnRonda = -1 }, () => 1);
            cultist.CurrentHP = 0;

            await FightHandler.CaenSusInvocadosAsync(null, fight, cultist);

            Assert.DoesNotContain(row, wilbur.Buffs.Puestos);
        }
    }
}
