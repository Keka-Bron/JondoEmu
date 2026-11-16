using System.IO;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class CraftingQuestTests
    {
        private const long Character = 8_900_100_169;

        [Theory]
        [InlineData(1649, 2269, 153355270, 1182, 9866, 9863, 9870)]
        [InlineData(1650, 2270, 153355264, 16440, 9872, 9871, 9874)]
        [InlineData(1651, 2271, 153355272, 8537, 9877, 9875, 9883)]
        [InlineData(2513, 3194, 153354248, 8545, 19268, 19264, 19269)]
        public async Task Closing_the_workshop_credits_a_craft_in_any_order(
            int quest, int step, long map, int item, int objective, int earlierCraft, int showItem)
        {
            await WithQuestAsync(quest, step, map, async pipe =>
            {
                RecordCraft(pipe, item);
                Assert.False(QuestEngine.Log!.ObjectiveDone(objective));

                await WorkshopHandler.CloseAsync(pipe.Session);

                QuestEngine.LoadFrom(Character);
                Assert.True(QuestEngine.Log!.ObjectiveDone(objective));
                Assert.False(QuestEngine.Log.ObjectiveDone(earlierCraft));
                Assert.True(QuestEngine.NotYetRevealed(showItem));
            });
        }

        [Fact]
        public async Task The_incarnam_cake_still_requires_reading_the_recipe_first()
        {
            const int quest = 1637, cake = 16520, recipe = 9805, craft = 9806;
            await WithQuestAsync(quest, 2257, 153354242, async pipe =>
            {
                RecordCraft(pipe, cake);
                await WorkshopHandler.CloseAsync(pipe.Session);
                Assert.False(QuestEngine.Log!.ObjectiveDone(craft));

                Assert.True(await QuestEngine.CompleteByReplyAsync(pipe.ToClient, quest, recipe));
                RecordCraft(pipe, cake);
                await WorkshopHandler.CloseAsync(pipe.Session);
                Assert.True(QuestEngine.Log.ObjectiveDone(craft));
            });
        }

        private static void RecordCraft(ClientPipe pipe, int item)
        {
            pipe.Session.State.Workshop = new WorkshopHandler.Bench();
            pipe.Session.State.Workshop.Made.Add(item, 1);
        }

        private static async Task WithQuestAsync(int quest, int step, long map, Func<ClientPipe, Task> test)
        {
            if (!File.Exists(Paths.WorldDb)) return;
            QuestEngine.Load();
            QuestEngine.ReloadContent();
            DatabaseManager.EnsureProgressionTables();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, map);
            using var context = SessionContext.Push(pipe.Session);
            try
            {
                QuestEngine.LoadFrom(Character);
                QuestEngine.Log!.Restore(quest, step, packed: null, finished: false);
                await test(pipe);
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
