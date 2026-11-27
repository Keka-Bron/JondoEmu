using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// The Watchers' chain: in «1571» the overseer by the Cania zaap joins as a follower and Qiu's
    /// reply 22903 hands over the flask (15850) for Doctor Gaw; in «1608» going down to Vardo's
    /// floor ticks its arrival.
    /// </summary>
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class WatchersChainTests
    {
        private const long Character = 8_900_100_271;

        [Fact]
        public async Task Qius_reply_hands_over_the_flask()
        {
            const int quest = 1571, flask = 15850, npc = 2339;
            const long qiuMap = 130023939;
            QuestEngine.Load();
            DatabaseManager.EnsureProgressionTables();
            if (QuestEngine.Book?.Of(quest) == null) return;   // no quest data
            ClearCharacter();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, qiuMap);
            try
            {
                using var context = SessionContext.Push(pipe.Session);
                pipe.Session.State.CharacterLevel = 120;
                QuestEngine.LoadFrom(Character);
                var log = QuestEngine.Log!;
                var step = QuestEngine.Book!.Of(quest)!.Steps.First(s => s.Objectives.Any(o => o.Id == 9244));
                var run = log.Restore(quest, step.Id, null, false);
                run.Done.UnionWith(step.Objectives.TakeWhile(o => o.Id != 9244).Select(o => o.Id));
                Assert.Equal(0, Equipment.HowMany(flask));

                pipe.Session.State.MapId = qiuMap;
                pipe.Session.State.OpenDialogueNpcId = npc;
                pipe.Session.State.OpenDialogueMapId = qiuMap;
                pipe.Session.State.OpenDialogueMessage = 19215;
                await NpcHandler.ReplyAsync(pipe.Session, new(22903));
                Assert.True(log.ObjectiveDone(9244));
                Assert.Equal(1, Equipment.HowMany(flask));
            }
            finally
            {
                ClearCharacter();
            }
        }

        [Fact]
        public async Task Going_down_to_Vardos_floor_ticks_the_arrival()
        {
            const int quest = 1608;
            const long lowerFloor = 91751943;
            QuestEngine.Load();
            DatabaseManager.EnsureProgressionTables();
            if (QuestEngine.Book?.Of(quest) == null) return;   // no quest data
            ClearCharacter();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, lowerFloor);
            try
            {
                using var context = SessionContext.Push(pipe.Session);
                pipe.Session.State.CharacterLevel = 120;
                QuestEngine.LoadFrom(Character);
                var log = QuestEngine.Log!;
                var step = QuestEngine.Book!.Of(quest)!.Steps.First(s => s.Objectives.Any(o => o.Id == 9403));
                var run = log.Restore(quest, step.Id, null, false);
                run.Done.UnionWith(step.Objectives.TakeWhile(o => o.Id != 9403).Select(o => o.Id));
                string order = string.Join(",", step.Objectives.Select(o => $"{o.Id}/{o.TypeId}"));

                pipe.Session.State.MapId = lowerFloor;
                await QuestEngine.OnMapEnteredAsync(pipe.Session, lowerFloor, 0);
                Assert.True(log.ObjectiveDone(9403), $"step {step.Id}: {order}; done {string.Join(",", run.Done)}");

                // The wooden crate in the middle of the room: the client offers it with no skill and
                // sends the use of instance 497852.
                Assert.Equal(0, Equipment.HowMany(16021));
                bool used = await QuestEngine.OnInteractiveUsedAsync(pipe.Session, lowerFloor, 487852, 497852);
                Assert.True(used && log.ObjectiveDone(9404), $"used {used}; done {string.Join(",", log.Run(quest)?.Done ?? new())}");
                Assert.Equal(1, Equipment.HowMany(16021));
            }
            finally
            {
                ClearCharacter();
            }
        }

        [Fact]
        public async Task The_overseer_by_the_Cania_zaap_joins_as_a_follower()
        {
            const int quest = 1571, follower = 15848, overseer = 2655;
            const long zaapMap = 164364304;
            QuestEngine.Load();
            DatabaseManager.EnsureProgressionTables();
            if (QuestEngine.Book?.Of(quest) == null) return;   // no quest data
            ClearCharacter();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, zaapMap);
            try
            {
                using var context = SessionContext.Push(pipe.Session);
                pipe.Session.State.CharacterLevel = 120;
                QuestEngine.LoadFrom(Character);
                var log = QuestEngine.Log!;
                var step = QuestEngine.Book!.Of(quest)!.Steps.First(s => s.Objectives.Any(o => o.Id == 9243));
                var run = log.Restore(quest, step.Id, null, false);
                run.Done.UnionWith(step.Objectives.TakeWhile(o => o.Id != 9243).Select(o => o.Id));
                Assert.Equal(0, Equipment.HowMany(follower));

                pipe.Session.State.MapId = zaapMap;
                await QuestEngine.OnTalkingToAsync(pipe.Session, overseer);
                Assert.True(log.ObjectiveDone(9243));
                Assert.Equal(1, Equipment.HowMany(follower));
            }
            finally
            {
                ClearCharacter();
            }
        }

        private static void ClearCharacter()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterQuests WHERE CharacterId = $id; " +
                                  "DELETE FROM CharacterItems WHERE CharacterId = $id;";
            command.Parameters.AddWithValue("$id", Character);
            command.ExecuteNonQuery();
        }
    }
}
