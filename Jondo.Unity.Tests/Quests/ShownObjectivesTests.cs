using System.Collections.Generic;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// Which of a step's objectives the journal shows: an objective whose binding waits for others
    /// stays out of the list until they are done («En la encrucijada de los mundos» showed the
    /// audience and the shield before the recommendations).
    /// </summary>
    public class ShownObjectivesTests
    {
        private static readonly Dictionary<int, QuestBinding> Rows = new()
        {
            [16380] = new QuestBinding { ObjectiveId = 16380, QuestId = 2150, Kind = QuestBindingKind.Talk,
                                         AfterObjectives = new[] { 16378, 16379 } },
            [16381] = new QuestBinding { ObjectiveId = 16381, QuestId = 2150, Kind = QuestBindingKind.Click,
                                         AfterObjectives = new[] { 16380 } },
            [16390] = new QuestBinding { ObjectiveId = 16390, QuestId = 2150, Kind = QuestBindingKind.Objectives,
                                         AfterObjectives = new[] { 16378 } },
        };

        private static List<int> Shown(IReadOnlyCollection<int> done, params int[] elsewhere)
            => Jondo.Unity.Server.Managers.Quests.Shown(new[] { 16378, 16379, 16380, 16381, 16390 }, done,
                            o => Rows.TryGetValue(o, out var row) ? row : null,
                            o => System.Array.IndexOf(elsewhere, o) >= 0);

        [Fact]
        public void An_objective_waiting_for_others_is_held_back()
        {
            Assert.Equal(new[] { 16378, 16379, 16390 }, Shown(new HashSet<int>()));
            Assert.Equal(new[] { 16378, 16379, 16380, 16390 }, Shown(new HashSet<int> { 16378, 16379 }));
            Assert.Equal(new[] { 16378, 16379, 16380, 16381, 16390 }, Shown(new HashSet<int> { 16378, 16379, 16380 }));
        }

        [Fact]
        public void One_done_in_another_quest_counts()
            => Assert.Contains(16380, Shown(new HashSet<int> { 16378 }, 16379));

        [Fact]
        public void A_step_never_looks_finished_while_it_is_not()
        {
            // Everything shown is done, and the rest waits for something not done anywhere: the
            // whole list goes out rather than an empty to-do.
            var rows = new Dictionary<int, QuestBinding>
            {
                [2] = new QuestBinding { ObjectiveId = 2, Kind = QuestBindingKind.Talk, AfterObjectives = new[] { 99 } },
            };
            var shown = Jondo.Unity.Server.Managers.Quests.Shown(new[] { 1, 2 }, new HashSet<int> { 1 },
                                     o => rows.TryGetValue(o, out var row) ? row : null, _ => false);
            Assert.Equal(new[] { 1, 2 }, shown);
        }
    }
}
