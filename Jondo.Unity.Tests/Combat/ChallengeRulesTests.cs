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
    }
}
