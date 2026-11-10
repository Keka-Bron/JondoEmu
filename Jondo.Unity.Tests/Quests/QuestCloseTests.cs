using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// A quest done closes as the captures close one: «misión terminada» (lqn 56) and ied. Without
    /// them the client kept «Vaya par» in its followed box, and the recruiters' question marks with it.
    /// </summary>
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class QuestCloseTests
    {
        private const long Character = 8_900_100_082;
        private const int Pair = 1994;
        private const int BontaRecruiter = 735, BrakmarRecruiter = 734;
        private const long BontaRecruiterMap = 188743684, BrakmarRecruiterMap = 188746757;
        private const int BontaObjective = 13793, BrakmarObjective = 13794;

        private static void Clean()
        {
            foreach (int quest in FollowedQuests.Of(Character)) FollowedQuests.Unfollow(Character, quest);
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterQuests WHERE CharacterId = $c; " +
                                  "DELETE FROM CharacterItems WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Character);
            try { command.ExecuteNonQuery(); } catch (SqliteException) { }
        }

        [Fact]
        public async Task Seeing_both_recruiters_closes_the_pair_with_lqn_56_and_ied()
        {
            if (!File.Exists(Paths.QuestsJson) || !File.Exists(Paths.WorldDb)) return;
            QuestEngine.Load();
            DatabaseManager.EnsureProgressionTables();
            Clean();

            await using var pipe = await ClientPipe.OpenAsync(8_900_100_082, Character, BontaRecruiterMap, "Recluta");
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 30;
                    QuestEngine.LoadFrom(Character);
                    Assert.True(await QuestEngine.StartAsync(pipe.ToClient, Pair));
                    FollowedQuests.Follow(Character, Pair);

                    // Each recruiter's objective is done by her talk's last reply («Gracias por la
                    // explicación»), as content/npcs/dialogues.json writes it.
                    await QuestEngine.CompleteByReplyAsync(pipe.ToClient, Pair, BontaObjective);
                    Assert.True(QuestEngine.Log!.Active(Pair));

                    pipe.Session.State.MapId = BrakmarRecruiterMap;
                    await QuestEngine.CompleteByReplyAsync(pipe.ToClient, Pair, BrakmarObjective);
                    Assert.False(QuestEngine.Log!.Active(Pair));
                    Assert.DoesNotContain(Pair, FollowedQuests.Of(Character));
                }

                // Read on to the close: the starts, the steps and the reward come before it.
                byte[] finished = ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, QuestEngine.QuestFinishedMessage, "1994");
                bool saidFinished = false;
                while (true)
                {
                    var (opcode, payload) = await pipe.NextAny();
                    if (opcode == Op.Lqn && payload.SequenceEqual(finished)) saidFinished = true;
                    if (opcode != "ied") continue;

                    Assert.True(saidFinished, "ied came before «misión terminada»");
                    Assert.Equal("08ca0f", Convert.ToHexString(payload).ToLowerInvariant());
                    break;
                }

                // And the journal sends the followed box even empty: it replaces the client's.
                using (SessionContext.Push(pipe.Session)) await QuestEngine.SendJournalAsync(pipe.ToClient);
                (string Opcode, byte[] Payload) last = default;
                while (true)
                {
                    var frame = await pipe.NextAny();
                    if (frame.Opcode != Op.Iel) continue;
                    last = frame;
                    break;
                }
                Assert.Empty(last.Payload);
            }
            finally
            {
                Clean();
            }
        }
    }
}
