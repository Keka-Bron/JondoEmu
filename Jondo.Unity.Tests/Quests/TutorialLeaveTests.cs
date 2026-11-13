using System.IO;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
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
    /// «Abandonar el tutorial» (idt) only for a character in the middle of it.
    /// </summary>
    /// <remarks>
    /// The client sends its idt again on the next character's entry when the one before was switched
    /// out mid-tutorial (main menu, «Cambiar de personaje»). Honoured as it came, a level-200
    /// character in Cania had the tutorial's quests validated, its set given and was put in the
    /// Dofusarium, and its client, which confirmed the map it stood on, was disconnected.
    /// </remarks>
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class TutorialLeaveTests
    {
        private const long Character = 8_900_100_091;
        private const long CaniaMap = 158336514;
        private static readonly int[] TutorialQuests = { 2545, 2502, 2511, 1629, 1630 };

        private static void Clean()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterQuests WHERE CharacterId = $c; " +
                                  "DELETE FROM CharacterItems WHERE CharacterId = $c; " +
                                  "DELETE FROM CharacterAchievements WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Character);
            try { command.ExecuteNonQuery(); } catch (SqliteException) { }
        }

        private static bool Ready()
            => File.Exists(Paths.QuestsJson) && File.Exists(Paths.WorldDb)
               && File.Exists(Paths.ContentFile(Path.Combine("quests", "tutorial.json")));

        [Fact]
        public async Task A_character_out_of_the_tutorial_ignores_the_idt()
        {
            if (!Ready()) return;
            QuestEngine.Load();
            DatabaseManager.EnsureProgressionTables();
            Clean();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, CaniaMap, "Lejos");
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 200;
                    QuestEngine.LoadFrom(Character);
                    await QuestEngine.LeaveTutorialAsync(pipe.ToClient);

                    foreach (int quest in TutorialQuests)
                    {
                        Assert.False(QuestEngine.Log!.Finished(quest), $"tutorial quest {quest} was validated");
                        Assert.False(QuestEngine.Log!.Active(quest), $"tutorial quest {quest} was started");
                    }
                    Assert.Equal(CaniaMap, pipe.Session.State.MapId);
                }
                await pipe.Quiet();
            }
            finally
            {
                Clean();
            }
        }

        [Fact]
        public async Task A_new_character_where_it_wakes_up_leaves_the_tutorial()
        {
            if (!Ready()) return;
            QuestEngine.Load();
            DatabaseManager.EnsureProgressionTables();
            Clean();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, CharacterCreationHandler.TutorialMap, "Nuevo");
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 1;
                    QuestEngine.LoadFrom(Character);
                    await QuestEngine.LeaveTutorialAsync(pipe.ToClient);

                    foreach (int quest in TutorialQuests)
                        Assert.True(QuestEngine.Log!.Finished(quest), $"tutorial quest {quest} was not validated");
                }
            }
            finally
            {
                Clean();
            }
        }
    }
}
