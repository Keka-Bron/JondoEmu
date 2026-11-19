using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class ChachaBluesTests
    {
        private const long Character = 8_900_100_199;

        [Fact]
        public async Task The_first_cellar_victory_gives_Soki_one_mouse()
        {
            const int quest = 1992, stepId = 2653, capture = 14207, handOver = 14208, mouse = 19763;
            const long cellar = 192676864, sokiMap = 188746754;
            QuestEngine.Load();
            DatabaseManager.EnsureProgressionTables();
            ClearCharacter();

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, cellar);
            try
            {
                using var context = SessionContext.Push(pipe.Session);
                pipe.Session.State.CharacterLevel = 30;
                QuestEngine.LoadFrom(Character);
                var log = QuestEngine.Log!;
                var step = QuestEngine.Book!.Step(stepId)!;
                var run = log.Restore(quest, stepId, null, false);
                run.Done.UnionWith(step.Objectives.TakeWhile(o => o.Id != capture).Select(o => o.Id));
                Assert.True(log.ObjectiveDone(14206));
                Assert.False(log.ObjectiveDone(capture));
                Assert.Equal(0, Equipment.HowMany(mouse));

                var encounter = Assert.IsType<NpcFights.Fight>(NpcFights.For(4465, cellar, 38192));
                var fight = new FightInstance(Character, cellar, encounter.ArenaMapId);
                fight.AddPlayer(new Fighter { Id = Character, Level = 30, MaxHP = 100, CurrentHP = 100 });
                long id = -1;
                foreach (var member in encounter.Monsters)
                    fight.AddMonster(new Fighter
                    {
                        Id = id--, MonsterId = member.Monster, GradeIndex = member.Grade,
                        IsMonster = true, MaxHP = 30, CurrentHP = 0, Level = 10,
                    });

                Assert.True(await FightHandler.CheckFightOverAsync(pipe.ToClient, fight));
                Assert.True(log.ObjectiveDone(capture));
                Assert.Equal(1, Equipment.HowMany(mouse));

                pipe.Session.State.MapId = sokiMap;
                pipe.Session.State.OpenDialogueNpcId = 4467;
                pipe.Session.State.OpenDialogueMapId = sokiMap;
                pipe.Session.State.OpenDialogueMessage = 31502;
                await NpcHandler.ReplyAsync(pipe.Session, ConnectionProtocol.Push(Op.Ioy, Pb.New().Var(1, 38183).Build()));
                Assert.True(log.ObjectiveDone(handOver));
                Assert.True(log.Run(quest)!.Finished);
                Assert.Equal(0, Equipment.HowMany(mouse));
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
