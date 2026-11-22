using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Audacia and Bravura expeditions: the guardian holds his "- Fun" or "- Hard" spell from the
    /// start of the fight, and its first grade puts state 3501 (Audacia) or 3500 (Bravura) on him.
    /// </summary>
    [Collection("MapManager")]
    public class ExpeditionModifiersTests
    {
        private const int AudaciaState = 3501;
        private const int BravuraState = 3500;

        private static Fighter Boss(int monsterId) => new()
        {
            Id = -1, Name = "Boss", IsMonster = true, CellId = 165, Level = 200, MaxHP = 30000,
            CurrentHP = 30000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = monsterId,
        };

        private static Fighter Minion(int id) => new()
        {
            Id = id, Name = "Minion", IsMonster = true, CellId = 200, Level = 200, MaxHP = 3000,
            CurrentHP = 3000, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4, MonsterId = 3410,
        };

        [Fact]
        public void Every_Audacia_and_Bravura_expedition_has_its_modifier()
        {
            ExpeditionModifiers.Forget();
            Assert.Equal(40, ExpeditionModifiers.Count);
            for (int dungeon = 166; dungeon <= 205; dungeon++)
            {
                var modifier = ExpeditionModifiers.Of(dungeon);
                Assert.NotNull(modifier);
                Assert.Equal(dungeon <= 185 ? "audacia" : "bravura", modifier!.Kind);
            }
            Assert.Null(ExpeditionModifiers.Of(171 - 30));
        }

        [Fact]
        public void Each_spell_puts_its_own_state_on_the_boss()
        {
            for (int dungeon = 166; dungeon <= 205; dungeon++)
            {
                var modifier = ExpeditionModifiers.Of(dungeon)!;
                int state = modifier.Kind == "audacia" ? AudaciaState : BravuraState;
                Assert.Contains(SpellEffects.De(modifier.Spell, 1),
                                e => e.EffectId == 950 && e.Value == state);
            }
        }

        [Fact]
        public async Task The_boss_holds_it_and_his_monsters_do_not()
        {
            var modifier = ExpeditionModifiers.Of(171)!;
            var boss = Boss(modifier.Boss);
            var minion = Minion(-2);
            var fight = new FightInstance(modifier.Boss, 990_000_171);
            fight.AddMonster(boss);
            fight.AddMonster(minion);

            Assert.Equal(1, ExpeditionModifiers.Give(fight, modifier, new[] { modifier.Boss }));
            Assert.Contains(modifier.Spell, boss.Buffs.Actitudes);
            Assert.Empty(minion.Buffs.Actitudes);

            // What the fight start does with every attitude: its first grade, cast on himself.
            await FightHandler.AplicarEfectosAsync(null, fight, boss, modifier.Spell, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            Assert.Contains(AudaciaState, boss.Buffs.Estados);
        }

        [Fact]
        public async Task Meno_s_monsters_hold_his_Audacia_and_it_mutates_them()
        {
            // 23938 names Meno (4460) in its masks: it is written for each of his monsters.
            var modifier = ExpeditionModifiers.Of(179)!;
            Assert.True(ExpeditionModifiers.HeldByEveryMonster(modifier));
            Assert.False(ExpeditionModifiers.HeldByEveryMonster(ExpeditionModifiers.Of(171)!));
            var boss = Boss(modifier.Boss);
            var minion = Minion(-2);
            var fight = new FightInstance(modifier.Boss, 990_000_179);
            fight.AddMonster(boss);
            fight.AddMonster(minion);

            Assert.Equal(2, ExpeditionModifiers.Give(fight, modifier, new[] { modifier.Boss }));
            Assert.Contains(modifier.Spell, minion.Buffs.Actitudes);

            await FightHandler.AplicarEfectosAsync(null, fight, boss, modifier.Spell, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            await FightHandler.AplicarEfectosAsync(null, fight, minion, modifier.Spell, 1, minion,
                                                   EffectEngine.AlLanzar, minion.CellId);
            // Meno alone is in Audacia; his monster cast at him, took 3599, and mutated (3517).
            Assert.Contains(AudaciaState, boss.Buffs.Estados);
            Assert.DoesNotContain(AudaciaState, minion.Buffs.Estados);
            Assert.Contains(3599, minion.Buffs.Estados);
            Assert.Contains(3517, minion.Buffs.Estados);
            Assert.DoesNotContain(3517, boss.Buffs.Estados);
        }
    }
}
