using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The challenges a boss imposes, against the client's table: who judges each one, and which
    /// are left out because nobody does.
    /// </summary>
    public class BossChallengeTests
    {
        private static bool Available => File.Exists(Paths.Resolve("retos_3.6.10.10.json"));

        private static readonly object _once = new object();
        private static bool _loaded;

        /// <summary>The table is static: read once, however many tests ask.</summary>
        private static void Load()
        {
            lock (_once)
            {
                if (_loaded) return;
                Challenges.Initialize();
                Challenges.OnlyOffer(ChallengeWatcher.Watched);
                _loaded = true;
            }
        }

        [Theory]
        // The Jalató Real's Prudente and Blitzkrieg carry the generic ones' criteria word for word.
        [InlineData(121, ChallengeWatcher.Prudente)]
        [InlineData(122, ChallengeWatcher.Blitzkrieg)]
        [InlineData(115, ChallengeWatcher.Zombi)]
        [InlineData(125, ChallengeWatcher.Estatua)]
        // "Ma=1" says nothing, so the boss's Bárbaro is found by its name.
        [InlineData(266, ChallengeWatcher.Barbaro)]
        // A generic challenge is its own judge.
        [InlineData(ChallengeWatcher.Prudente, ChallengeWatcher.Prudente)]
        public void A_boss_challenge_is_judged_as_its_generic_twin(int id, int kind)
        {
            if (!Available) return;
            Load();

            Assert.Equal(kind, Challenges.KindOf(id));
        }

        [Fact]
        public void Kill_order_and_turn_limits_are_read_from_the_criterion()
        {
            if (!Available) return;
            Load();

            // 117 "Primero" CK#4051, 295 "Último" Ck#3534, 52 "Dúo" ST<20, 355 either of two first.
            Assert.Equal(new[] { 4051 }, Challenges.Get(117)!.KillFirst.ToArray());
            Assert.Empty(Challenges.Get(117)!.KillLast);
            Assert.Equal(new[] { 3534 }, Challenges.Get(295)!.KillLast.ToArray());
            Assert.Equal(new[] { 4264, 4265 }, Challenges.Get(355)!.KillFirst.ToArray());
            Assert.Equal(20, Challenges.Get(52)!.TurnLimit);

            // The boss's own Primero and Último are not the generic ones: those point at a random
            // enemy, and would break these on the first death.
            Assert.Equal(0, Challenges.KindOf(117));
            Assert.Equal(0, Challenges.KindOf(295));
        }

        [Fact]
        public void What_nobody_judges_is_not_imposed()
        {
            if (!Available) return;
            Load();

            // 124 "Manos limpias" (Sd!0), 296 "Místico" (Tc=0,0), 983 a boss's own mechanic (Ma=1).
            Assert.False(Challenges.Get(124)!.Judged);
            Assert.False(Challenges.Get(296)!.Judged);
            Assert.False(Challenges.Get(983)!.Judged);

            // Merkator, alone: Dúo, Último, "Hay gente por aquí" and Solo. Not Místico.
            var alone = Challenges.Imposed(new[] { 3534 }, new int[0], players: 1, monsterCount: 4);
            Assert.Equal(new[] { 294, 295, 1074, 2018 }, alone.Select(r => r.Id).OrderBy(i => i).ToArray());
        }

        [Fact]
        public void The_rules_read_from_a_description_are_wired_by_hand()
        {
            if (!Available) return;
            Load();

            Assert.Equal(Challenges.BossRule.BeginOrEndInLineWithEnemy, Challenges.Get(1074)!.Rule);
            Assert.Equal(Challenges.BossRule.NoHealEnemies, Challenges.Get(980)!.Rule);
            Assert.Equal(Challenges.BossRule.NoDamageToEnemySummons, Challenges.Get(996)!.Rule);

            // "Matar a {0} en último lugar" is the boss's own monster, last.
            Assert.Equal(new[] { 3652 }, Challenges.Get(1017)!.KillLast.ToArray());
            // "antes del inicio del turno 6".
            Assert.Equal(6, Challenges.Get(526)!.TurnLimit);
            // "en línea con un luchador aliado" is the generic Del mismo linaje.
            Assert.Equal(ChallengeWatcher.MismoLinaje, Challenges.KindOf(1080));
        }

        [Fact]
        public void Every_hand_wired_challenge_is_a_scripted_boss_challenge()
        {
            if (!Available) return;
            Load();

            // A hand-wired id that is not "Ma=1" in the table would be silently ignored: this
            // is what catches a typo in the list, or a table that moved under it.
            foreach (int id in Challenges.HandWired)
            {
                var reto = Challenges.Get(id);
                Assert.True(reto != null && reto.NeedsMonster && reto.Completion == "Ma=1" && reto.Judged,
                            $"challenge {id}");
            }
        }

        [Fact]
        public void Party_size_decides_duo_and_solo()
        {
            if (!Available) return;
            Load();

            int[] Of(int players) => Challenges.Imposed(new[] { 3534 }, new int[0], players, 4)
                                               .Select(r => r.Id).OrderBy(i => i).ToArray();

            Assert.Equal(new[] { 294, 295, 1074 }, Of(2));   // Dúo stays, Solo goes
            Assert.Equal(new[] { 295, 1074 }, Of(3));        // neither
        }

        [Fact]
        public void One_already_done_is_not_imposed_again()
        {
            if (!Available) return;
            Load();

            var again = Challenges.Imposed(new[] { 3534 }, new[] { 295 }, players: 1, monsterCount: 4);
            Assert.DoesNotContain(again, r => r.Id == 295);
        }
    }
}
