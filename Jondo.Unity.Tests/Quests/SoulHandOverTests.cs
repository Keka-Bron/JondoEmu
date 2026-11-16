using System.IO;
using System.Linq;
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
    /// «Le voleur d'âmes» (1465): Tungusa asks for five souls, and her «Toma Tungusa, aquí tienes lo
    /// que me pediste» is a partial hand-over. It is offered once one of them is carried and takes
    /// only what is brought; a partial reply used to weigh items alone, so a soul never counted.
    /// </summary>
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class SoulHandOverTests
    {
        private const long Character = 8_900_100_146;
        private const int Previous = 1464, Quest = 1465, Step = 2085;
        private const long Tierradala = 205261570;
        private static readonly int[] Souls = { 8128, 8129, 8130, 8131, 8132 };
        private const int Dragenerys = 5823;

        private static void Clean()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterQuests WHERE CharacterId = $c; " +
                                  "DELETE FROM CharacterItems WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Character);
            try { command.ExecuteNonQuery(); } catch (SqliteException) { }
        }

        [Fact]
        public async Task A_partial_hand_over_is_offered_for_one_soul_and_takes_only_that_one()
        {
            if (!File.Exists(Paths.QuestsJson) || !File.Exists(Paths.WorldDb)) return;
            QuestEngine.Load();
            DatabaseManager.EnsureProgressionTables();
            Clean();

            await using var pipe = await ClientPipe.OpenAsync(8_900_100_146, Character, Tierradala, "Almas");
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 80;
                    QuestEngine.LoadFrom(Character);
                    var log = QuestEngine.Log!;
                    log.Restore(Previous, QuestEngine.Book!.Of(Previous)!.Steps[^1].Id, null, true);
                    log.Restore(Quest, Step, null, false);
                    Assert.True(log.Active(Quest));

                    // Empty-handed: neither the whole hand-over nor a part of it.
                    Assert.False(QuestEngine.HandsOverEnough(Souls, partial: false));
                    Assert.False(QuestEngine.HandsOverEnough(Souls, partial: true));

                    // Dragenerys' soul alone: the partial reply is offered, the whole one is not.
                    var stone = new SoulCapture.Stone(SoulCapture.FullStone,
                        new[] { new SoulCapture.Soul(Dragenerys, 1, 0, false, false) });
                    Assert.True(await SoulCaptureHandler.GrantAsync(pipe.ToClient, stone));
                    Assert.True(QuestEngine.HandsOverEnough(Souls, partial: true));
                    Assert.False(QuestEngine.HandsOverEnough(Souls, partial: false));

                    // Choosing it does the soul brought and leaves the other four to do; the stone goes.
                    foreach (int objective in Souls)
                        await QuestEngine.CompleteByReplyAsync(pipe.Session, Quest, objective);
                    Assert.True(log.ObjectiveDone(Souls[0]));
                    Assert.All(Souls.Skip(1), objective => Assert.False(log.ObjectiveDone(objective)));
                    Assert.DoesNotContain(Equipment.All, item => item.Effects.Any(e => e.Effect == SoulCapture.SoulEffect));
                    Assert.False(QuestEngine.HandsOverEnough(Souls, partial: true));
                }
            }
            finally
            {
                Clean();
            }
        }
    }
}
