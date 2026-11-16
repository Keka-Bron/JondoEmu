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

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// A hero's achievements (Achievements.ForHeroAsync): his own, earned in his session, told on
    /// his master's client and the account's from then on -- not earned twice.
    /// </summary>
    public class HeroAchievementsTests
    {
        private const long Master = 990000301, Hero = 990000302, Account = 990000303;

        private static void Forget()
        {
            DatabaseManager.EnsureProgressionTables();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM CharacterAchievements WHERE CharacterId IN ($m, $h);
                DELETE FROM CharacterAchievementCounters WHERE CharacterId IN ($m, $h);";
            command.Parameters.AddWithValue("$m", Master);
            command.Parameters.AddWithValue("$h", Hero);
            command.ExecuteNonQuery();
        }

        [Fact]
        public async Task A_heros_level_earns_for_the_account_once_and_on_his_masters_client()
        {
            if (!File.Exists(Paths.WorldDb) || !File.Exists(Paths.AchievementsJson)) return;
            Achievements.Load();
            Forget();

            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, Master, 1);
                var master = pipe.Session;
                master.State.CharacterLevel = 1;
                using (SessionContext.Push(master)) Achievements.LoadFrom(Master);

                var hero = GameSession.SinSocket();
                hero.State.CharacterId = Hero;
                hero.State.CharacterName = "Mule";
                hero.State.CharacterLevel = 200;
                using (SessionContext.Push(hero)) Achievements.LoadFrom(Hero);

                // His level, never looked at since he was connected: the level achievements.
                await Achievements.ForHeroAsync(master, hero);
                var earned = hero.State.Achievements!.Earned.ToList();
                Assert.NotEmpty(earned);
                // Told on his master's client, one mfu each.
                foreach (var _ in earned) await pipe.Next(Op.Mfu);

                // The account's now, credited to him.
                var account = master.State.Achievements!;
                Assert.All(earned, id => Assert.Equal(Hero, account.AchieverOf(id)));

                // Looked at again, nothing new: no achievement is earned twice.
                hero.State.AchievementLevelChecked = 0;
                await Achievements.ForHeroAsync(master, hero);
                await pipe.Quiet();
            }
            finally
            {
                Forget();
            }
        }
    }
}
