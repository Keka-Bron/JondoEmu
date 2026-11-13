using System.Collections.Generic;
using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Achievements;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    public class AchievementObjectiveCriterionTests
    {
        private sealed class PlayerFacts : IQuestFacts
        {
            public HashSet<int> Items { get; } = new();
            public bool AssumeEverythingElse { get; set; }

            public int Level => 200;
            public long MapId => 0;
            public bool Finished(int questId) => AssumeEverythingElse;
            public bool Active(int questId) => AssumeEverythingElse;
            public bool ObjectiveDone(int objectiveId) => AssumeEverythingElse;

            public long? Count(string op, long key, string flag)
                => AssumeEverythingElse ? long.MaxValue
                    : op == "PO" ? (Items.Contains((int)key) ? 1 : 0) : null;
        }

        private static bool Met(string criterion, PlayerCriteria.Facts facts)
            => Criterion.Met(criterion, condition => PlayerCriteria.Resolve(condition, facts));

        [Fact]
        public void A_BI_objective_with_an_authored_criterion_is_judged_by_it()
        {
            if (!File.Exists(Paths.AchievementsJson) || !File.Exists(Paths.QuestsJson)
                || !File.Exists(Paths.AchievementLinksJson)) return;

            var facts = new PlayerFacts { AssumeEverythingElse = true };
            var book = new AchievementCatalogue();
            var log = new AchievementLog(book, facts);
            // 17380 is «BI=1» in the client; content/achievements/objectives.json judges it by the
            // tutorial step the capture earns it after, so «Primer tiempo» (8518) can be held.
            Assert.Equal(Answer.True, log.ObjectiveState(17380));
            Assert.True(log.Holds(8518));
        }

        [Fact]
        public void OO_uses_the_character_s_achievement_objective_state_with_negation()
        {
            if (!File.Exists(Paths.AchievementsJson) || !File.Exists(Paths.QuestsJson)
                || !File.Exists(Paths.AchievementLinksJson)) return;

            var session = GameSession.SinSocket();
            var player = new PlayerFacts();
            var log = new AchievementLog(new AchievementCatalogue(), player);
            session.State.Achievements = log;
            using (SessionContext.Push(session))
            {
                var facts = new PlayerCriteria.Facts(session.State, null, null, 0, _ => 0);

                // Objective 11281 asks for seven specific worn-looking items in inventory. A quest
                // objective with the same integer id must have no bearing on this result.
                Assert.False(Met("OO=11281", facts));
                Assert.True(Met("OO!11281", facts));

                foreach (int item in new[] { 26776, 26778, 26780, 26782, 26784, 26786, 26789 })
                    player.Items.Add(item);
                Assert.True(Met("OO=11281", facts));
                Assert.False(Met("OO!11281", facts));

                // Missing catalog state remains unknown under either polarity.
                Assert.False(Met("OO=999999999", facts));
                Assert.False(Met("OO!999999999", facts));
            }

            var noCharacter = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
            Assert.False(Met("OO=11281", noCharacter));
            Assert.False(Met("OO!11281", noCharacter));
        }
    }
}
