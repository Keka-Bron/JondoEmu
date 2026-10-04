using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.World.Emotes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// Achievements, their tallies and the learned emotes, written and read back per character.
    /// </summary>
    /// <remarks>
    /// Run against whatever base is in bases/, including one fresh out of datos/world.zip, which
    /// has none of these tables: that is why every reader and writer creates them itself.
    /// </remarks>
    public class ProgressionPersistenceTests
    {
        // Negative, so they can never be anybody's character.
        private const long Character = -910_001;
        private const long Other = -910_002;

        private static bool Available => File.Exists(Paths.WorldDb);

        private static void Forget()
        {
            DatabaseManager.EnsureProgressionTables();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM CharacterAchievements WHERE CharacterId IN ($a, $b);
                DELETE FROM CharacterAchievementCounters WHERE CharacterId IN ($a, $b);
                DELETE FROM CharacterEmotes WHERE CharacterId IN ($a, $b);";
            command.Parameters.AddWithValue("$a", Character);
            command.Parameters.AddWithValue("$b", Other);
            command.ExecuteNonQuery();
        }

        [Fact]
        public void An_achievement_is_kept_with_whether_it_was_paid()
        {
            if (!Available) return;
            Forget();
            try
            {
                DatabaseManager.SaveAchievement(Character, 307, claimed: false);
                DatabaseManager.SaveAchievement(Character, 423, claimed: false);
                DatabaseManager.SaveAchievement(Character, 423, claimed: true);

                var rows = DatabaseManager.LoadAchievements(Character);
                Assert.Contains((307, false), rows);
                Assert.Contains((423, true), rows);
                Assert.Equal(2, rows.Count);

                // Another character's badges are theirs.
                Assert.Empty(DatabaseManager.LoadAchievements(Other));
            }
            finally
            {
                Forget();
            }
        }

        [Fact]
        public void A_tally_is_kept_as_it_last_stood()
        {
            if (!Available) return;
            Forget();
            try
            {
                DatabaseManager.SaveAchievementCounter(Character, "EM", 147, 1);
                DatabaseManager.SaveAchievementCounter(Character, "EM", 147, 3);
                DatabaseManager.SaveAchievementCounter(Character, "Xs", 518, 1);

                var tallies = DatabaseManager.LoadAchievementCounters(Character);
                Assert.Equal(3, tallies[("EM", 147)]);
                Assert.Equal(1, tallies[("Xs", 518)]);
                Assert.Equal(2, tallies.Count);
                Assert.Empty(DatabaseManager.LoadAchievementCounters(Other));
            }
            finally
            {
                Forget();
            }
        }

        [Fact]
        public void A_learned_emote_is_kept_once()
        {
            if (!Available) return;
            Forget();
            try
            {
                DatabaseManager.SaveEmote(Character, 208);
                DatabaseManager.SaveEmote(Character, 208);

                var emotes = DatabaseManager.LoadEmotes(Character);
                Assert.Single(emotes);
                Assert.Contains(208, emotes);

                // The starting four are not written: every character has them.
                foreach (int starting in EmoteRules.Starting) Assert.DoesNotContain(starting, emotes);
            }
            finally
            {
                Forget();
            }
        }
    }
}
