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
    [Collection("Challenges table")]   // Challenges.Initialize reloads a static table
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

            // 296 "Místico" (Tc=0,0), 983 a boss's own mechanic (Ma=1). 124 "Manos limpias" (Sd!0)
            // is judged now, as the generic 19 it copies.
            Assert.True(Challenges.Get(124)!.Judged);
            Assert.Equal(ChallengeWatcher.ManosLimpias, Challenges.KindOf(124));
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

        [Theory]
        [InlineData(4051, 977)]
        [InlineData(799, 978)]
        [InlineData(928, 979)]
        [InlineData(2975, 981)]
        [InlineData(800, 984)]
        [InlineData(6903, 986)]
        [InlineData(792, 987)]
        [InlineData(252, 988)]
        [InlineData(1027, 989)]
        [InlineData(457, 991)]
        [InlineData(2960, 997)]
        [InlineData(180, 1002)]
        [InlineData(3100, 1005)]
        [InlineData(173, 1010)]
        [InlineData(3996, 1012)]
        [InlineData(670, 1014)]
        [InlineData(232, 1015)]
        [InlineData(5819, 1016)]
        [InlineData(226, 1018)]
        [InlineData(3476, 1019)]
        [InlineData(1071, 1020)]
        [InlineData(940, 1021)]
        [InlineData(382, 1026)]
        [InlineData(121, 1028)]
        [InlineData(3852, 1029)]
        [InlineData(1188, 1030)]
        [InlineData(854, 1031)]
        [InlineData(2854, 1032)]
        [InlineData(4609, 1034)]
        [InlineData(3618, 1035)]
        [InlineData(6256, 1036)]
        [InlineData(6218, 1038)]
        [InlineData(943, 1043)]
        [InlineData(2877, 1044)]
        [InlineData(827, 1048)]
        [InlineData(2924, 1049)]
        [InlineData(4264, 1051)]
        [InlineData(6388, 1052)]
        [InlineData(3849, 1055)]
        [InlineData(6394, 1057)]
        [InlineData(2942, 1065)]
        [InlineData(4803, 1067)]
        [InlineData(7219, 1068)]
        [InlineData(3409, 1069)]
        [InlineData(3384, 1070)]
        [InlineData(3397, 1072)]
        [InlineData(3648, 1075)]
        [InlineData(3828, 1077)]
        [InlineData(4453, 1081)]
        [InlineData(4744, 1083)]
        [InlineData(5110, 1086)]
        [InlineData(5100, 1087)]
        [InlineData(5319, 1088)]
        [InlineData(6026, 1090)]
        [InlineData(5955, 1091)]
        [InlineData(6014, 1093)]
        [InlineData(6792, 1097)]
        [InlineData(3306, 1267)]
        [InlineData(1179, 1278)]
        [InlineData(1194, 1280)]
        [InlineData(5653, 1405)]
        [InlineData(5666, 1406)]
        public void Supported_specials_are_imposed_in_their_boss_fights(int bossId, int specialId)
        {
            Load();

            Assert.Contains(Challenges.Imposed(new[] { bossId }, new int[0], 2, 4),
                            challenge => challenge.Id == specialId);
            Assert.DoesNotContain(Challenges.Imposed(new[] { bossId }, new[] { specialId }, 2, 4),
                                  challenge => challenge.Id == specialId);
        }

        [Fact]
        public void Every_hand_wired_challenge_has_its_expected_boss_criterion()
        {
            if (!Available) return;
            Load();

            // Catch mistyped ids and changes to the challenge catalogue.
            foreach (int id in Challenges.HandWired)
            {
                var reto = Challenges.Get(id);
                string criterion = id == 1030 ? "CK#1188,1&Ma=1" : "Ma=1";
                Assert.True(reto != null && reto.NeedsMonster && reto.Completion == criterion && reto.Judged,
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
