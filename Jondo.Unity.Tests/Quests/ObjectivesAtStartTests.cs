using System.IO;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// «Bienvenido a Frigost» (649) is taken once the mayor's four quests are finished: its four
    /// «objectives» rows wait on their last objectives, which no later tick will close, so they
    /// close as the quest starts.
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class ObjectivesAtStartTests
    {
        private const long Character = 8_900_100_649;
        private const int Quest = 649;
        private static readonly int[] Mayor = { 612, 613, 614, 615 };
        private static readonly int[] Waiting = { 4010, 4062, 4063, 4064 };

        [Fact]
        public async Task Objectives_done_before_the_start_close_as_the_quest_starts()
        {
            if (!File.Exists(Paths.QuestsJson) || !File.Exists(Paths.WorldDb)) return;
            QuestEngine.Load();
            QuestEngine.ReloadContent();
            DatabaseManager.EnsureProgressionTables();
            foreach (int objective in Waiting)
                if (QuestEngine.Bindings?.Of(objective)?.AfterObjectives.Count is not > 0) return;
            Clean();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, 54_791_183);
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 200;
                    QuestEngine.LoadFrom(Character);
                    foreach (int quest in Mayor) QuestEngine.Log!.Restore(quest, 0, packed: null, finished: true);

                    Assert.True(await QuestEngine.StartAsync(pipe.Session, Quest));
                    var run = QuestEngine.Log!.Run(Quest)!;
                    foreach (int objective in Waiting) Assert.Contains(objective, run.Done);
                }
            }
            finally
            {
                Clean();
            }
        }

        private static void Clean()
        {
            using var db = new SqliteConnection(DatabaseManager.WorldConnectionString);
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM CharacterQuests WHERE CharacterId = $character";
            command.Parameters.AddWithValue("$character", Character);
            command.ExecuteNonQuery();
        }
    }
}
