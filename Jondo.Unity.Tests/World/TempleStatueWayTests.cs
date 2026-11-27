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
    /// «La colère des dieux» (1938): «Reza delante de la estatua del templo para entrar en el
    /// territorio de los dioses» (13470) is the statue's own way «Hacia el territorio de los dioses»
    /// (SkillNamesDataRoot 48, content/interactives/ways.json), not a second element: the statue
    /// is declared once, as the way it is, with no placement of its own (a way is attached to the
    /// map's own data, MapInteractiveMessages.Registered).
    /// </summary>
    [Collection("MapManager")]
    public sealed class TempleStatueWayTests
    {
        private const long Temple = 183762950;
        private const int Statue = 511845, GodsTerritory = 48;

        private readonly ITestOutputHelper _out;
        public TempleStatueWayTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void The_statue_is_declared_once_as_its_way()
        {
            if (MapManager.GetMapInfo(Temple) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(Temple) == null) { _out.WriteLine("no world data"); return; }
            QuestEngine.Load();
            var book = QuestEngine.Book!;
            var step = book.Of(1938)?.Steps.FirstOrDefault(s => s.Objectives.Any(o => o.Id == 13470));
            if (step == null) { _out.WriteLine("no quest data"); return; }

            var session = GameSession.SinSocket();
            session.State.CharacterLevel = 200;
            var log = new QuestLog(book, () => 200, () => Temple);
            session.State.Quests = log;
            log.Restore(1938, step.Id, null, false);
            using var context = SessionContext.Push(session);

            Interactives.Initialize();
            TeleportManager.Initialize();
            Resources.Initialize();
            InteractiveWays.Forget();
            InteractiveRegistry.Initialize();
            var (elements, states) = MapInteractiveMessages.Snapshot(Temple, 0);
            foreach (var e in elements.Where(e => e.ElementId == Statue))
                _out.WriteLine($"declared type {e.ElementTypeId} enabled "
                               + string.Join(", ", e.EnabledSkills.Select(s => $"{s.SkillId}/{s.SkillInstanceUid}/{s.NameId}")));
            foreach (var s in states.Where(s => s.ElementId == Statue)) _out.WriteLine($"placed on {s.CellId} state {s.State}");

            var statue = Assert.Single(elements, e => e.ElementId == Statue);
            Assert.Contains(statue.EnabledSkills, s => s.NameId == GodsTerritory);
            Assert.DoesNotContain(states, s => s.ElementId == Statue);

            // What the click sends resolves to the way, which the handler takes once the quest has
            // heard it (InteractiveActionHandler): to the gods' territory.
            int instance = statue.EnabledSkills.First(s => s.NameId == GodsTerritory).SkillInstanceUid;
            Assert.True(InteractiveRegistry.TryResolveUse(Temple, Statue, instance, out var used, out var action));
            Assert.Equal(InteractiveActionKind.Way, action.Kind);
            var way = InteractiveWays.WayOf(InteractiveWays.Of(Temple, Statue)!, action.SkillId, action.SkillNameId);
            Assert.Equal(188219392, way!.ToMap);
        }
    }
}
