using System.IO;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Four of the generic challenges, by their descriptions: «Manos limpias» (no direct damage),
    /// «Cada uno con su monstruo» (nobody else's enemy), «Espejo» (an ally behind the one who falls)
    /// and how «El dos por uno» and the others are offered at all.
    /// </summary>
    [Collection("Challenges table")]   // Challenges.Initialize reloads a static table
    public class ChallengeRulesTests
    {
        private static bool Available => File.Exists(Paths.Resolve("retos_3.6.10.10.json"));

        private static readonly object _once = new object();
        private static bool _loaded;

        private static void Load()
        {
            lock (_once)
            {
                if (_loaded) return;
                // Loaded once for the whole run: reloading it under another class's feet empties it.
                // Not KindOf: with the table empty it answers the id itself, and nothing got loaded.
                if (Challenges.Get(ChallengeWatcher.Zombi)?.Kind != ChallengeWatcher.Zombi)
                {
                    Challenges.Initialize();
                    Challenges.OnlyOffer(ChallengeWatcher.Watched);
                }
                _loaded = true;
            }
        }

        private static (FightInstance Fight, Fighter A, Fighter B, Fighter Enemy) Fight(int challenge, long id)
        {
            var fight = new FightInstance(id, 191105026);
            var a = new Fighter { Id = id + 1, TeamId = 0, CellId = 200, MaxHP = 100, CurrentHP = 100, Name = "Uno" };
            var b = new Fighter { Id = id + 2, TeamId = 0, CellId = 300, MaxHP = 100, CurrentHP = 100, Name = "Dos" };
            var enemy = new Fighter { Id = -1, TeamId = 1, CellId = 228, MaxHP = 100, CurrentHP = 100, IsMonster = true, Name = "Bicho" };
            fight.AddPlayer(a);
            fight.AddPlayer(b);
            fight.AddMonster(enemy);
            fight.AddMonster(new Fighter { Id = -2, TeamId = 1, CellId = 400, MaxHP = 100, CurrentHP = 100, IsMonster = true, Name = "Otro" });
            fight.ChallengesFixed.Add((challenge, 50));
            return (fight, a, b, enemy);
        }

        [Fact]
        public void The_four_are_offered()
        {
            if (!Available) return;
            Load();
            foreach (int id in new[] { ChallengeWatcher.ManosLimpias, ChallengeWatcher.DosPorUno,
                                       ChallengeWatcher.CadaUnoConSuMonstruo, ChallengeWatcher.Espejo })
                Assert.Equal(id, Challenges.KindOf(id));
        }

        [Fact]
        public async Task Clean_hands_breaks_on_a_direct_hit_only()
        {
            if (!Available) return;
            Load();
            await using var pipe = await ClientPipe.OpenAsync(8_960_100_001, 8_960_100_001, 191105026, "Uno");
            var (fight, a, _, enemy) = Fight(ChallengeWatcher.ManosLimpias, 8_960_100_000);

            await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, a, 1, direct: false);   // a trap, a poison
            Assert.DoesNotContain(ChallengeWatcher.ManosLimpias, fight.ChallengesBroken);
            await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, a, 1, direct: true);
            Assert.Contains(ChallengeWatcher.ManosLimpias, fight.ChallengesBroken);
        }

        [Fact]
        public async Task Each_his_own_monster_breaks_when_another_ally_hits_it()
        {
            if (!Available) return;
            Load();
            await using var pipe = await ClientPipe.OpenAsync(8_960_100_011, 8_960_100_011, 191105026, "Uno");
            var (fight, a, b, enemy) = Fight(ChallengeWatcher.CadaUnoConSuMonstruo, 8_960_100_010);

            await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, a, 1);
            await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, a, 1);
            Assert.DoesNotContain(ChallengeWatcher.CadaUnoConSuMonstruo, fight.ChallengesBroken);
            await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, b, 1);
            Assert.Contains(ChallengeWatcher.CadaUnoConSuMonstruo, fight.ChallengesBroken);
        }

        [Fact]
        public async Task Mirror_wants_an_ally_behind_the_one_who_falls()
        {
            if (!Available) return;
            Load();
            await using var pipe = await ClientPipe.OpenAsync(8_960_100_021, 8_960_100_021, 191105026, "Uno");

            var (fight, a, b, enemy) = Fight(ChallengeWatcher.Espejo, 8_960_100_020);
            b.CellId = MapGeometry.Reflejar(a.CellId, enemy.CellId);
            Assert.True(b.CellId >= 0);
            enemy.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, enemy, false, a);
            Assert.DoesNotContain(ChallengeWatcher.Espejo, fight.ChallengesBroken);

            var (lonely, a2, _, enemy2) = Fight(ChallengeWatcher.Espejo, 8_960_100_030);
            enemy2.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, lonely, enemy2, false, a2);
            Assert.Contains(ChallengeWatcher.Espejo, lonely.ChallengesBroken);
        }

        /// <summary>
        /// «First» is met the moment its target falls first, as the real game draws it green then,
        /// and the end still pays it.
        /// </summary>
        [Fact]
        public async Task First_is_met_when_its_target_falls_first()
        {
            if (!Available) return;
            Load();
            await using var pipe = await ClientPipe.OpenAsync(8_960_100_041, 8_960_100_041, 191105026, "Uno");
            var (fight, a, _, enemy) = Fight(ChallengeWatcher.Primero, 8_960_100_040);
            fight.ChallengeTargets[ChallengeWatcher.Primero] = enemy.Id;

            enemy.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, enemy, false, a);
            Assert.Contains(ChallengeWatcher.Primero, fight.ChallengesMet);
            Assert.Contains(ChallengeWatcher.Primero, fight.ChallengesBroken);

            try
            {
                Assert.Equal(50, ChallengeWatcher.EndBonus(fight, won: true));
                Assert.Equal(50, await ChallengeWatcher.FightEndedAsync(pipe.ToClient, fight, won: true));
                Assert.Contains(ChallengeWatcher.Primero, ChallengeWatcher.WonIn(fight));
            }
            finally { ChallengeWatcher.Forget(fight); }
        }

        [Fact]
        public async Task First_is_broken_when_another_falls_first()
        {
            if (!Available) return;
            Load();
            await using var pipe = await ClientPipe.OpenAsync(8_960_100_051, 8_960_100_051, 191105026, "Uno");
            var (fight, a, _, enemy) = Fight(ChallengeWatcher.Primero, 8_960_100_050);
            fight.ChallengeTargets[ChallengeWatcher.Primero] = -2;

            enemy.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, enemy, false, a);
            Assert.Contains(ChallengeWatcher.Primero, fight.ChallengesBroken);
            Assert.DoesNotContain(ChallengeWatcher.Primero, fight.ChallengesMet);
            Assert.Equal(0, ChallengeWatcher.EndBonus(fight, won: true));
        }

        /// <summary>A boss's «CK#4051,1» (117, the Chadalid falls first): met when the Chadalid falls first.</summary>
        [Fact]
        public async Task A_boss_s_first_is_met_when_the_boss_falls_first()
        {
            if (!Available) return;
            Load();
            const int chadalidFirst = 117;
            Assert.Equal(new[] { 4051 }, Challenges.Get(chadalidFirst)!.KillFirst);
            await using var pipe = await ClientPipe.OpenAsync(8_960_100_061, 8_960_100_061, 191105026, "Uno");

            var (fight, a, _, enemy) = Fight(chadalidFirst, 8_960_100_060);
            enemy.MonsterId = 4051;
            enemy.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, enemy, false, a);
            Assert.Contains(chadalidFirst, fight.ChallengesMet);

            var (other, a2, _, enemy2) = Fight(chadalidFirst, 8_960_100_070);
            enemy2.MonsterId = 31;
            enemy2.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, other, enemy2, false, a2);
            Assert.Contains(chadalidFirst, other.ChallengesBroken);
            Assert.DoesNotContain(chadalidFirst, other.ChallengesMet);
        }

        [Theory]
        [InlineData("boss ends beside a player", true)]
        [InlineData("boss ends beside a monster", true)]
        [InlineData("boss ends alone", false)]
        [InlineData("Chadarib takes damage", true)]
        [InlineData("Chadarib loses shields", true)]
        [InlineData("boss takes damage", false)]
        [InlineData("Chadarib is pushed", true)]
        [InlineData("Chadarib is pulled", false)]
        public async Task Kardorim_special_checks_boss_position_and_protects_chadaribs(string action, bool broken)
        {
            Load();
            const int special = 977;
            const long characterId = 8_960_100_081;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, player, _, boss) = Fight(special, characterId - 1);
            boss.MonsterId = 4051;
            var chadarib = new Fighter
            {
                Id = -3, TeamId = 1, MonsterId = 4052, IsMonster = true, Invocador = boss.Id,
                CellId = 550, MaxHP = 100, CurrentHP = 100, Name = "Chadarib"
            };
            fight.AddMonster(chadarib);

            switch (action)
            {
                case "boss ends beside a player":
                    player.CellId = MapGeometry.GetNeighbors(boss.CellId).First();
                    await ChallengeWatcher.TurnEndedAsync(pipe.ToClient, fight, boss);
                    break;
                case "boss ends beside a monster":
                    chadarib.CellId = MapGeometry.GetNeighbors(boss.CellId).First();
                    await ChallengeWatcher.TurnEndedAsync(pipe.ToClient, fight, boss);
                    break;
                case "boss ends alone":
                    await ChallengeWatcher.TurnEndedAsync(pipe.ToClient, fight, boss);
                    break;
                case "Chadarib takes damage":
                    await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, chadarib, 10, player, 1);
                    break;
                case "Chadarib loses shields":
                    await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, chadarib, 0, player, 1, shieldLoss: 10);
                    break;
                case "boss takes damage":
                    await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, boss, 10, player, 1);
                    break;
                case "Chadarib is pushed":
                    await ChallengeWatcher.DisplacedAsync(pipe.ToClient, fight, chadarib, player, pushed: true);
                    break;
                case "Chadarib is pulled":
                    await ChallengeWatcher.DisplacedAsync(pipe.ToClient, fight, chadarib, player, pulled: true);
                    break;
            }

            Assert.Equal(broken, fight.ChallengesBroken.Contains(special));
        }

        [Fact]
        public async Task Maxilubo_counts_damage_lines_per_attacker_and_enemy_on_each_turn()
        {
            Load();
            const int special = 1015;
            const long characterId = 8_960_100_091;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, first, second, enemy) = Fight(special, characterId - 1);

            try
            {
                for (int i = 0; i < 3; i++)
                    await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, first, 1);
                await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, fight.Rojo[1], 10, first, 1);
                await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, second, 1);
                Assert.DoesNotContain(special, fight.ChallengesBroken);

                ChallengeWatcher.TurnStarted(fight, first);
                for (int i = 0; i < 3; i++)
                    await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, first, 1);
                Assert.DoesNotContain(special, fight.ChallengesBroken);
                await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 0, first, 1, shieldLoss: 10);
                Assert.Contains(special, fight.ChallengesBroken);
            }
            finally { ChallengeWatcher.Forget(fight); }
        }

        [Fact]
        public async Task Ush_counts_damage_across_allied_turns_in_the_same_round()
        {
            Load();
            const int special = 1051;
            const long characterId = 8_960_100_101;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, first, second, enemy) = Fight(special, characterId - 1);
            fight.StartFight();

            try
            {
                await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, first, 1);
                int firstRound = fight.RoundNumber;
                for (int i = 0; i < fight.TurnOrder.Count; i++) fight.NextTurn();
                Assert.Equal(firstRound + 1, fight.RoundNumber);
                await ChallengeWatcher.RoundStartedAsync(pipe.ToClient, fight);

                await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, first, 1);
                Assert.DoesNotContain(special, fight.ChallengesBroken);
                ChallengeWatcher.TurnStarted(fight, second);
                await ChallengeWatcher.DamagedAsync(pipe.ToClient, fight, enemy, 10, second, 1);
                Assert.Contains(special, fight.ChallengesBroken);
            }
            finally { ChallengeWatcher.Forget(fight); }
        }

        [Fact]
        public async Task Hungry_Sunflower_summons_get_one_turn_each()
        {
            Load();
            const int special = 978;
            const long characterId = 8_960_100_111;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, _, _, sunflower) = Fight(special, characterId - 1);
            sunflower.MonsterId = 799;
            var first = fight.Rojo[1];
            first.Invocador = sunflower.Id;
            var second = new Fighter { Id = -3, TeamId = 1, Invocador = sunflower.Id, CurrentHP = 100 };
            fight.AddMonster(second);

            await ChallengeWatcher.EnemyTurnStartedAsync(pipe.ToClient, fight, first);
            await ChallengeWatcher.EnemyTurnStartedAsync(pipe.ToClient, fight, second);
            Assert.DoesNotContain(special, fight.ChallengesBroken);
            await ChallengeWatcher.EnemyTurnStartedAsync(pipe.ToClient, fight, first);
            Assert.Contains(special, fight.ChallengesBroken);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Mob_Lasponja_must_be_pushed_or_pulled_in_the_round_it_dies(bool pushed)
        {
            Load();
            const int special = 979;
            long characterId = pushed ? 8_960_100_121 : 8_960_100_131;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, ally, _, boss) = Fight(special, characterId - 1);
            boss.MonsterId = 928;
            fight.StartFight();
            for (int i = 0; i < fight.TurnOrder.Count; i++) fight.NextTurn();
            Assert.Equal(2, fight.RoundNumber);

            try
            {
                await ChallengeWatcher.DisplacedAsync(pipe.ToClient, fight, boss, ally, pushed: pushed);
                boss.CurrentHP = 0;
                await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, boss, false, ally);
                Assert.Equal(!pushed, fight.ChallengesBroken.Contains(special));
            }
            finally { ChallengeWatcher.Forget(fight); }
        }

        [Fact]
        public async Task Cocabulia_counts_turn_starts_for_its_actual_shells()
        {
            Load();
            const int special = 1031;
            const long characterId = 8_960_100_141;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, _, _, boss) = Fight(special, characterId - 1);
            boss.MonsterId = 854;
            var shell = fight.Rojo[1];
            shell.MonsterId = 5815;

            try
            {
                for (int i = 0; i < 3; i++)
                    await ChallengeWatcher.EnemyTurnStartedAsync(pipe.ToClient, fight, boss);
                for (int i = 0; i < 2; i++)
                    await ChallengeWatcher.EnemyTurnStartedAsync(pipe.ToClient, fight, shell);
                Assert.DoesNotContain(special, fight.ChallengesBroken);
                await ChallengeWatcher.EnemyTurnStartedAsync(pipe.ToClient, fight, shell);
                Assert.Contains(special, fight.ChallengesBroken);
            }
            finally { ChallengeWatcher.Forget(fight); }
        }

        [Theory]
        [InlineData(5499, true)]
        [InlineData(1191, false)]
        public async Task Papa_No_es_requires_a_puppet_before_the_boss(int templateId, bool broken)
        {
            Load();
            const int special = 1280;
            long characterId = 8_960_100_150 + templateId;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, ally, _, boss) = Fight(special, characterId - 1);
            boss.MonsterId = 1194;
            var other = fight.Rojo[1];
            other.MonsterId = templateId;
            other.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, other, false, ally);
            boss.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, boss, false, ally);
            Assert.Equal(broken, fight.ChallengesBroken.Contains(special));
        }

        [Fact]
        public async Task Necronyx_checks_enemy_state_entry_only_while_the_boss_is_alive()
        {
            Load();
            const int special = 1086;
            const long characterId = 8_960_100_161;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, ally, _, boss) = Fight(special, characterId - 1);
            boss.MonsterId = 5110;
            var other = fight.Rojo[1];

            await ChallengeWatcher.LateSpecialStateAddedAsync(pipe.ToClient, fight, ally, 571);
            await ChallengeWatcher.LateSpecialStateAddedAsync(pipe.ToClient, fight, boss, 571);
            Assert.DoesNotContain(special, fight.ChallengesBroken);
            await ChallengeWatcher.LateSpecialStateAddedAsync(pipe.ToClient, fight, other, 571);
            Assert.Contains(special, fight.ChallengesBroken);

            var (after, _, _, defeatedBoss) = Fight(special, characterId + 1);
            defeatedBoss.MonsterId = 5110;
            defeatedBoss.CurrentHP = 0;
            await ChallengeWatcher.LateSpecialStateAddedAsync(pipe.ToClient, after, after.Rojo[1], 571);
            Assert.DoesNotContain(special, after.ChallengesBroken);
        }

        [Fact]
        public async Task Trankitronko_breaks_when_its_spell_transforms_an_ally()
        {
            Load();
            const int special = 1014;
            const long characterId = 8_960_100_171;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, ally, _, enemy) = Fight(special, characterId - 1);
            enemy.MonsterId = 670;

            await ChallengeWatcher.AppearanceChangedAsync(pipe.ToClient, fight, enemy, 607, 876);
            await ChallengeWatcher.AppearanceChangedAsync(pipe.ToClient, fight, ally, 607, 0);
            await ChallengeWatcher.AppearanceChangedAsync(pipe.ToClient, fight, ally, 606, 876);
            Assert.DoesNotContain(special, fight.ChallengesBroken);

            var transformation = SpellEffects.De(607, 1).Single(row => row.EffectId == 149);
            await FightHandler.CastEffectsAsync(pipe.ToClient, fight, enemy, 607, 1, ally, ally.CellId,
                                                false, new[] { transformation });
            Assert.Contains(special, fight.ChallengesBroken);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Puppet_Master_requires_summon_order_even_after_the_victim_reaches_zero_life(bool reversed)
        {
            Load();
            const int special = 1019;
            long characterId = reversed ? 8_960_100_181 : 8_960_100_191;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, ally, _, boss) = Fight(special, characterId - 1);
            boss.MonsterId = 3476;
            var first = new Fighter { Id = -3, MonsterId = 3478, CurrentHP = 100 };
            var second = new Fighter { Id = -4, MonsterId = 3477, CurrentHP = 100 };
            fight.Invocar(first, boss);
            ChallengeWatcher.PuppetSummoned(fight, first, boss);
            fight.Invocar(second, boss);
            ChallengeWatcher.PuppetSummoned(fight, second, boss);

            var victim = reversed ? second : first;
            victim.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, victim, false, ally);
            Assert.Equal(reversed, fight.ChallengesBroken.Contains(special));
            ChallengeWatcher.Forget(fight);
        }

        [Fact]
        public async Task Dazahk_checks_the_Nanoimpatience_state_of_the_enemy_that_dies()
        {
            Load();
            const int special = 1088;
            const long characterId = 8_960_100_201;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, ally, _, enemy) = Fight(special, characterId - 1);
            enemy.Buffs.PonerEstado(695);
            enemy.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, enemy, false, ally);
            Assert.DoesNotContain(special, fight.ChallengesBroken);

            var other = fight.Rojo[1];
            other.CurrentHP = 0;
            await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, other, false, ally);
            Assert.Contains(special, fight.ChallengesBroken);
            ChallengeWatcher.Forget(fight);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task War_breaks_when_an_ally_moves_or_kills_a_weapon(bool killed)
        {
            Load();
            const int special = 1093;
            long characterId = killed ? 8_960_100_211 : 8_960_100_221;
            await using var pipe = await ClientPipe.OpenAsync(characterId, characterId, 191105026, "Uno");
            var (fight, ally, _, weapon) = Fight(special, characterId - 1);
            weapon.MonsterId = 6010;
            var other = fight.Rojo[1];

            await ChallengeWatcher.DisplacedAsync(pipe.ToClient, fight, weapon, other);
            await ChallengeWatcher.DisplacedAsync(pipe.ToClient, fight, other, ally);
            Assert.DoesNotContain(special, fight.ChallengesBroken);

            if (killed)
            {
                weapon.CurrentHP = 0;
                await ChallengeWatcher.DiedAsync(pipe.ToClient, fight, weapon, false, ally);
            }
            else
            {
                await ChallengeWatcher.DisplacedAsync(pipe.ToClient, fight, weapon, ally);
            }

            Assert.Contains(special, fight.ChallengesBroken);
            ChallengeWatcher.Forget(fight);
        }
    }
}
