using System.IO;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// A reply that hands over several items at once names the last of its objectives in «completes»; the
    /// later ones open only as the earlier are marked, so the reply must be offered once the first is due.
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class MultiBringReplyTests
    {
        private const long Character = 8_900_100_201;
        private const int Quest = 704, Step = 1319, FirstBring = 4559, LastBring = 4560;
        private const long Map = 192414728;

        [Fact]
        public async Task A_multi_bring_reply_is_offered_once_its_first_objective_is_due()
        {
            await WithQuestAsync(async () =>
            {
                var run = QuestEngine.Log!.Run(Quest)!;
                run.Done.UnionWith(new[] { 4557, 4558 });
                var reply = new DialogueChoice
                {
                    Quest = Quest,
                    Completes = LastBring,
                    CompletesAll = new[] { FirstBring, LastBring },
                };
                var single = new DialogueChoice { Quest = Quest, Completes = LastBring };

                Assert.False(QuestEngine.NotYetRevealed(FirstBring));
                Assert.False(NpcHandler.NotItsTurnYet(reply));
                Assert.Equal(QuestEngine.NotYetRevealed(LastBring), NpcHandler.NotItsTurnYet(single));
            });
        }

        private static async Task WithQuestAsync(Func<Task> test)
        {
            if (!File.Exists(Paths.WorldDb)) return;
            QuestEngine.Load();
            QuestEngine.ReloadContent();
            DatabaseManager.EnsureProgressionTables();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, Map);
            using var context = SessionContext.Push(pipe.Session);
            try
            {
                QuestEngine.LoadFrom(Character);
                QuestEngine.Log!.Restore(Quest, Step, packed: null, finished: false);
                await test();
            }
            finally
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
}
