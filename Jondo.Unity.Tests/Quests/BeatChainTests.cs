using System;
using System.IO;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// Three «beat» rows on the same monsters, each after the one before (quest 1739): one won fight
    /// closes one link of the chain, not the three in a row as each tick unlocks the next.
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class BeatChainTests
    {
        private const long Character = 8_900_100_301;
        private const int Quest = 1739, Step = 2376, First = 11060, Second = 11061, Third = 11062;
        private const long Map = 168822786;

        [Fact]
        public async Task One_win_closes_one_link_of_a_beat_chain()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            QuestEngine.Load();
            QuestEngine.ReloadContent();
            DatabaseManager.EnsureProgressionTables();
            var binding = QuestEngine.Bindings?.Of(Second);
            if (binding == null || !binding.AfterObjectives.Contains(First)) return;

            await using var pipe = await ClientPipe.OpenAsync(Character, Character, Map);
            using var context = SessionContext.Push(pipe.Session);
            try
            {
                QuestEngine.LoadFrom(Character);
                QuestEngine.Log!.Restore(Quest, Step, packed: null, finished: false);
                var run = QuestEngine.Log!.Run(Quest)!;

                await QuestWatcher.FightEndedAsync(pipe.Session, Won(binding.AnyOfMonsters), won: true);
                Assert.Contains(First, run.Done);
                Assert.DoesNotContain(Second, run.Done);

                await QuestWatcher.FightEndedAsync(pipe.Session, Won(binding.AnyOfMonsters), won: true);
                Assert.Contains(Second, run.Done);
                Assert.DoesNotContain(Third, run.Done);
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

        private static FightInstance Won(System.Collections.Generic.IEnumerable<int> monsters)
        {
            var fight = new FightInstance(9_100_301, Map);
            fight.AddPlayer(new Fighter { Id = Character, TeamId = 0, MaxHP = 500, CurrentHP = 500 });
            long id = -1;
            foreach (int monster in monsters)
                fight.AddMonster(new Fighter { Id = id--, TeamId = 1, IsMonster = true, MonsterId = monster, MaxHP = 1, CurrentHP = 0 });
            return fight;
        }
    }
}
