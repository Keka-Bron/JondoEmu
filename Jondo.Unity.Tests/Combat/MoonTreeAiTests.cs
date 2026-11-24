using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Moon's Tree (dungeon 92) as the AI plays it: Darkli Moon (225) puts out his four totems
    /// one after another, and Zampavoro (216) eats whoever he pulled next to him.
    /// </summary>
    public class MoonTreeAiTests
    {
        private const int Darkli = 225, Moon = 226, Zampavoro = 216, Pull = 6076, Gulp = 6077;
        private static readonly int[] Totems = { 298, 299, 300, 301 };

        private static Fighter Person(int cell) => new()
        {
            Id = 10, TeamId = 0, CellId = cell, MaxHP = 3000, CurrentHP = 3000, CurrentAP = 6, CurrentMP = 3, Level = 200,
        };

        /// <summary>The four totem spells carry a 63-round wait as their grade's initial cooldown, and that wait is their once-a-fight lock: none is born waiting.</summary>
        [Fact]
        public void Darkli_is_born_able_to_cast_each_totem_spell()
        {
            var recipe = Summons.De(Darkli, 1);
            if (recipe == null || recipe.Plantilla != Darkli) return;   // no monster data

            var darkli = new Fighter { Id = -5, MonsterId = Darkli, IsMonster = true, TeamId = 1, Invocador = -1 };
            Summons.Characteristics(darkli, recipe, new Fighter { Id = -1, MonsterId = Moon, IsMonster = true, Level = 100, TeamId = 1 });
            FightHandler.SpellsBornWaiting(darkli);

            foreach (int totem in Totems) Assert.False(darkli.Recarga.ContainsKey(totem), $"spell {totem} is born waiting");
        }

        /// <summary>
        /// Round after round, a totem not yet out is the one he casts: each spell locks itself for
        /// the fight once cast (63 rounds), so four rounds bring out four different totems.
        /// </summary>
        [Fact]
        public void Darkli_brings_out_every_totem_over_the_fight()
        {
            var recipe = Summons.De(Darkli, 1);
            if (recipe == null || recipe.Plantilla != Darkli) return;

            var moon = new Fighter { Id = -1, MonsterId = Moon, IsMonster = true, Level = 100, TeamId = 1, CellId = 301, MaxHP = 5000, CurrentHP = 5000, MaxAP = 12, CurrentAP = 12 };
            var darkli = new Fighter { Id = -5, MonsterId = Darkli, IsMonster = true, TeamId = 1, Invocador = moon.Id, SummonCost = 1, CellId = 300 };
            Summons.Characteristics(darkli, recipe, moon);
            FightHandler.SpellsBornWaiting(darkli);
            var person = Person(360);
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(person); fight.AddMonster(moon); fight.AddMonster(darkli);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { moon, darkli, person },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
                CanSummon = (c, t, g) => FightHandler.FitsTheSummonLimit(fight, c, t, g),
            };

            var cast = new List<int>();
            for (int round = 0; round < 20; round++)
            {
                darkli.CurrentAP = darkli.MaxAP;
                var spells = FightHandler.TacticsOf(darkli);
                var usable = spells.Where(s => SpellCriteria.Allows(darkli, s.Id, s.Grade)).ToList();
                var action = MonsterTactics.Next(board, darkli, usable, new List<MonsterTactics.Spell>());
                if (action != null && Totems.Contains(action.Spell.Id))
                {
                    cast.Add(action.Spell.Id);
                    darkli.Recarga[action.Spell.Id] = action.Spell.Interval;
                }
                // The turn's end takes one round off every wait (FightHandler's end of turn).
                foreach (int key in darkli.Recarga.Keys.ToList()) if (darkli.Recarga[key] > 0) darkli.Recarga[key]--;
            }
            Assert.Equal(Totems.OrderBy(t => t), cast.OrderBy(t => t));
        }

        /// <summary>The cast of a totem spell writes its lock the way the engine does after a monster's cast: the grade's MinCastInterval.</summary>
        [Fact]
        public void A_totem_spell_cast_at_its_grade_locks_for_the_fight()
        {
            var recipe = Summons.De(Darkli, 1);
            if (recipe == null || recipe.Plantilla != Darkli) return;
            var darkli = new Fighter { Id = -5, MonsterId = Darkli, IsMonster = true, TeamId = 1, Invocador = -1 };
            Summons.Characteristics(darkli, recipe, new Fighter { Id = -1, MonsterId = Moon, IsMonster = true, Level = 100, TeamId = 1 });
            var spells = FightHandler.TacticsOf(darkli);
            foreach (int totem in Totems)
            {
                var spell = spells.Single(s => s.Id == totem);
                Assert.True(spell.Interval >= 63, $"spell {totem} grade {spell.Grade} has interval {spell.Interval}");
                Assert.True(FightHandler.LimitesDeGrado(totem, spell.Grade).Intervalo >= 63);
            }
        }

        /// <summary>
        /// His behaviour spell chains 302, whose rows (one random group of 24) unlock one totem spell
        /// per turn start, each once ("trigger duration 1"). Hooked for good with the behaviour, the
        /// delay-0 row went off at every turn start, so the first totem was cast every round.
        /// </summary>
        [Collection("MapManager")]
        public class Unlocks
        {
            [Fact]
            public async System.Threading.Tasks.Task Each_totem_spell_is_unlocked_once_in_the_four_turns_after_the_chain_is_cast()
            {
                var recipe = Summons.De(Darkli, 1);
                if (recipe == null || recipe.Plantilla != Darkli || recipe.HechizoPropio == 0) return;

                var moon = new Fighter { Id = -1, MonsterId = Moon, IsMonster = true, Level = 100, TeamId = 1, CellId = 301, MaxHP = 5000, CurrentHP = 5000, MaxAP = 12, CurrentAP = 12 };
                var darkli = new Fighter { Id = -5, MonsterId = Darkli, IsMonster = true, TeamId = 1, Invocador = moon.Id, SummonCost = 1, CellId = 300 };
                Summons.Characteristics(darkli, recipe, moon);
                var person = Person(360);
                var fight = new FightInstance(1, 990_092_225);
                fight.AddPlayer(person); fight.AddMonster(moon); fight.AddMonster(darkli);
                fight.StartFight();
                darkli.Conducta = (recipe.HechizoPropio, recipe.GradoDelHechizoPropio);
                await FightHandler.LanzarLaConductaAsync(null, fight, darkli);

                var unlocked = Totems.ToDictionary(t => t, _ => 0);
                for (int turn = 0; turn < 4; turn++)
                {
                    while (fight.CurrentFighter != darkli) fight.NextTurn();
                    foreach (int t in Totems) darkli.Recarga[t] = 63;   // as if every totem had just been cast
                    await FightHandler.EngancheAsync(null, fight, darkli, EffectEngine.AlEmpezarElTurno);
                    foreach (int t in Totems) if (darkli.Recarga.GetValueOrDefault(t) == 0) unlocked[t]++;
                    fight.NextTurn();
                }
                foreach (var (t, n) in unlocked) Assert.True(n == 1, $"totem spell {t} unlocked {n} times in four turns");
            }
        }

        /// <summary>Zampada kills the enemy at his side and heals and speeds the caster: weighed row by row it is a kill, and it is cast when the enemy is adjacent.</summary>
        [Fact]
        public void Zampavoro_gulps_the_enemy_beside_him_and_pulls_the_one_far_off()
        {
            var recipe = Summons.De(Zampavoro, 1);
            if (recipe == null || recipe.Plantilla != Zampavoro) return;

            Fighter Zampa() => new()
            {
                Id = -1, MonsterId = Zampavoro, IsMonster = true, Level = 100, TeamId = 1, CellId = 300,
                MaxHP = 5000, CurrentHP = 5000, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
                SpellIds = { Pull, Gulp }, SpellGrades = { [Pull] = 1, [Gulp] = 1 },
            };
            MonsterTactics.Action? Decide(int enemyCell)
            {
                var z = Zampa();
                var enemy = Person(enemyCell);
                var fight = new FightInstance(1, 1);
                fight.AddPlayer(enemy); fight.AddMonster(z);
                var board = new MonsterTactics.Board
                {
                    Fighters = new[] { z, enemy },
                    Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
                };
                return MonsterTactics.Next(board, z, FightHandler.TacticsOf(z));
            }

            Assert.True(FightHandler.KillsEnemiesOnly(Gulp, 1));
            Assert.Equal(Gulp, Decide(301)!.Spell.Id);
            Assert.Equal(Pull, Decide(360)!.Spell.Id);
        }
    }
}
