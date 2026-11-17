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

        /// <summary>The uid of grade 4's boost row in the client's effects table: «+20 % final damage» on Wilbur.</summary>
        private const int DevotionBoostRow = 269691;

        /// <summary>
        /// A monster's rows on the others go with it when it dies (a Pépite's mark, an Éclat's
        /// invulnerability) -- but not the boost of «Dévotion monstrueuse», which the sectarian dies
        /// FOR: its row is written to outlive its caster (dispellable 3), and the engine keeps every
        /// such row (Buffs.OutlivesItsCaster).
        /// </summary>
        [Fact]
        public async Task The_boost_of_the_death_spell_outlives_the_one_who_died_and_the_others_do_not()
        {
            if (!SpellEffects.De(Devotion, 4).Any(row => row.EffectUid == DevotionBoostRow)) return;
            var fight = new FightInstance(1, 1);
            var wilbur = High(300);
            var cultist = Cultist(-2, 6941, 330);
            fight.AddMonster(wilbur); fight.AddMonster(cultist);
            int n = 0;
            var boost = wilbur.Buffs.Poner(new Buff
            {
                EffectId = 1171, Cuanto = 20, Quien = cultist.Id, HechizoOrigen = Devotion, NivelOrigen = 4,
                EffectUid = DevotionBoostRow, CaducaEnRonda = -1,
            }, () => ++n);
            var mark = wilbur.Buffs.Poner(new Buff
            {
                EffectId = 1171, Cuanto = 20, Quien = cultist.Id, HechizoOrigen = 12345, CaducaEnRonda = -1,
            }, () => ++n);
            cultist.CurrentHP = 0;

            await FightHandler.CaenSusInvocadosAsync(null, fight, cultist);

            Assert.Equal(Buffs.DispellableByStrongDispelOnly, boost.Dispellable);
            Assert.Contains(boost, wilbur.Buffs.Puestos);
            Assert.DoesNotContain(mark, wilbur.Buffs.Puestos);
        }
    }
}
