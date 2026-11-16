using System.IO;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Quests;
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

        [Theory]
        [InlineData(445, 901, 2670, 2669, 0)]
        [InlineData(1751, 2388, 11188, 11187, 11185)]
        [InlineData(1905, 2543, 13014, 13013, 13012)]
        [InlineData(1986, 2627, 13841, 13839, 13840)]
        [InlineData(2101, 2754, 15621, 15620, 15313)]
        [InlineData(2106, 2759, 15598, 15596, 0)]
        [InlineData(2106, 2759, 15606, 15605, 15597)]
        [InlineData(2118, 2773, 15888, 15880, 15882)]
        [InlineData(2154, 2810, 16549, 16548, 16546)]
        public async Task A_craft_waits_for_its_prerequisite_but_not_an_independent_objective(
            int quest, int stepId, int objectiveId, int independent, int prerequisite)
        {
            if (!File.Exists(Paths.WorldDb)) return;
            var step = new QuestCatalogue().Step(stepId)!;
            var craft = step.Objectives.Single(objective => objective.Id == objectiveId);
            await WithQuestAsync(quest, stepId, craft.MapId, async pipe =>
            {
                var run = QuestEngine.Log!.Run(quest)!;
                foreach (var earlier in step.Objectives.TakeWhile(objective => objective.Id != objectiveId))
                    if (earlier.Id != independent && earlier.Id != prerequisite) run.Done.Add(earlier.Id);

                if (prerequisite != 0)
                {
                    RecordCraft(pipe, craft.ItemId, craft.ItemCount);
                    await WorkshopHandler.CloseAsync(pipe.Session);
                    Assert.DoesNotContain(objectiveId, run.Done);
                    Assert.False(QuestEngine.NotYetRevealed(prerequisite));
                    run.Done.Add(prerequisite);
                }

                RecordCraft(pipe, craft.ItemId, craft.ItemCount);
                await WorkshopHandler.CloseAsync(pipe.Session);
                Assert.Contains(objectiveId, run.Done);
                Assert.DoesNotContain(independent, run.Done);
            });
        }

        [Fact]
        public async Task The_final_dummy_requires_all_components_and_the_final_artisans_instructions()
        {
            await WithQuestAsync(1986, 2627, 192940038, async pipe =>
            {
                const int instructions = 13844, craft = 13845, dummy = 19644;
                var run = QuestEngine.Log!.Run(1986)!;
                run.Done.UnionWith(new[] { 13836, 13838, 13840, 13842, 13837, 13841, 13843 });
                Assert.True(QuestEngine.NotYetRevealed(instructions));
                Assert.False(await QuestEngine.CompleteByReplyAsync(pipe.ToClient, 1986, instructions));

                run.Done.Add(13839);
                RecordCraft(pipe, dummy);
                await WorkshopHandler.CloseAsync(pipe.Session);
                Assert.DoesNotContain(craft, run.Done);

                Assert.True(await QuestEngine.CompleteByReplyAsync(pipe.ToClient, 1986, instructions));
                RecordCraft(pipe, dummy);
                await WorkshopHandler.CloseAsync(pipe.Session);
                Assert.Contains(craft, run.Done);
            });
        }

        private static void RecordCraft(ClientPipe pipe, int item, int quantity = 1)
        {
            pipe.Session.State.Workshop = new WorkshopHandler.Bench();
            pipe.Session.State.Workshop.Made.Add(item, quantity);
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
