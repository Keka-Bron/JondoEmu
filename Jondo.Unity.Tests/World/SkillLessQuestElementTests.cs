using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;
using Xunit;
using Xunit.Abstractions;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A prop of the map's own interactive list that has no skill of its own (type -1 in the map
    /// data) is offered to the client while a quest objective asks for it to be clicked: the
    /// Sramvil grille of «Los viajeros del canal» (1541, 8773) and the first ash urn of «La muerta
    /// Adela» (1554, 9038).
    /// </summary>
    [Collection("MapManager")]
    public sealed class SkillLessQuestElementTests
    {
        private readonly ITestOutputHelper _out;
        public SkillLessQuestElementTests(ITestOutputHelper output) => _out = output;

        [Theory]
        [InlineData(1541, 8773, 134218754L, 485439)]
        [InlineData(1554, 9038, 135267841L, 485612)]
        public void The_quest_prop_is_declared_usable(int quest, int objective, long map, int element)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(map) == null) { _out.WriteLine("no world data"); return; }
            QuestEngine.Load();
            var book = QuestEngine.Book!;
            var step = book.Of(quest)?.Steps.FirstOrDefault(s => s.Objectives.Any(o => o.Id == objective));
            if (step == null) { _out.WriteLine("no quest data"); return; }

            var session = GameSession.SinSocket();
            session.State.CharacterLevel = 200;
            var log = new QuestLog(book, () => 200, () => map);
            session.State.Quests = log;
            log.Restore(quest, step.Id, null, false);
            using var context = SessionContext.Push(session);

            Interactives.Initialize();
            TeleportManager.Initialize();
            Resources.Initialize();
            InteractiveWays.Forget();
            InteractiveRegistry.Initialize();
            _out.WriteLine($"bindings on map: {string.Join(", ", QuestEngine.Bindings.OnMap(map).Select(b => $"{b.ObjectiveId}:{QuestEngine.ShouldSee(b)}"))}");
            var (elements, states) = MapInteractiveMessages.Snapshot(map, 0);
            foreach (var e in elements)
                _out.WriteLine($"declared {e.ElementId} type {e.ElementTypeId} enabled "
                               + string.Join(", ", e.EnabledSkills.Select(s => $"{s.SkillId}/{s.SkillInstanceUid}")));
            foreach (var s in states) _out.WriteLine($"placed {s.ElementId} on {s.CellId} state {s.State}");

            var declared = Assert.Single(elements, e => e.ElementId == element);
            Assert.NotEmpty(declared.EnabledSkills);
        }
    }
}
