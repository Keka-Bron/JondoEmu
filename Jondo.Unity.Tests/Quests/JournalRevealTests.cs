using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Quests;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// What the journal lists of a step: what a talk or an element closes waits its turn like the
    /// dialogue does, and fights side by side stay on screen together, since any of them can be won.
    /// </summary>
    public class JournalRevealTests
    {
        private static List<int> Shown(QuestStep step, params int[] done)
        {
            var run = new QuestRun(step.QuestId, step.Id);
            foreach (int id in done) run.Done.Add(id);
            return QuestEngine.Shown(step, run, step.Objectives.Select(o => o.Id).ToList(), _ => false);
        }

        [Fact]
        public void Talks_wait_their_turn()
        {
            QuestEngine.Load();
            var step = QuestEngine.Book!.Step(1510)!;
            Assert.Equal(new[] { 5510, 5511 }, Shown(step));
        }

        [Fact]
        public void Fights_side_by_side_are_all_listed()
        {
            QuestEngine.Load();
            var steps = QuestEngine.Book!.All().SelectMany(q => q.Steps)
                .Where(s => s.Objectives.Count >= 2
                            && s.Objectives.Take(2).All(o => o.MonsterId != 0 && QuestEngine.Bindings.Of(o.Id) == null))
                .ToList();
            Assert.NotEmpty(steps);
            foreach (var step in steps)
            {
                var shown = Shown(step);
                Assert.Contains(step.Objectives[0].Id, shown);
                Assert.Contains(step.Objectives[1].Id, shown);
            }
        }
    }
}
