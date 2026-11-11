using System;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

[Collection("forgemagic")]
public sealed class EntryInformationFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selection_retains_the_previous_visit_before_recording_the_current_connection(bool returning)
    {
        const long account = 9_274_110_001;
        long id = 0;
        var session = GameSession.SinSocket();
        session.State.ClientIp = "198.51.100.8";
        using var context = SessionContext.Push(session);
        try
        {
            id = DatabaseManager.CreateCharacter(account, 1, "Entry-" + Guid.NewGuid().ToString("N"), 1, 0, 1,
                [-1, -1, -1, -1, -1], 191105026, 1, 0, 0, Array.Empty<(int, int)>());
            Assert.True(id > 0);
            DatabaseManager.AddItemToInventory(id, 303, 1);
            DatabaseManager.TouchLastConnection(id); // Ensure the older database's LastIp column exists.
            using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                using var seed = connection.CreateCommand();
                seed.CommandText = "UPDATE Characters SET LastConnection = $when, LastIp = $ip WHERE Id = $id;";
                seed.Parameters.AddWithValue("$when", returning ? "2024-01-02T03:04:00.000-07:00" : "");
                seed.Parameters.AddWithValue("$ip", returning ? "203.0.113.7" : "");
                seed.Parameters.AddWithValue("$id", id);
                seed.ExecuteNonQuery();
            }

            var before = DateTimeOffset.UtcNow;
            Assert.True(CharacterSelectionHandler.SelectCharacter(id, account));

            var notice = EntryInformationMessages.PreviousConnection(session.State.PreviousVisit);
            if (returning)
            {
                Assert.NotNull(notice);
                Assert.Equal(TextInformationType.TextInformationMessage, notice.MessageType);
                Assert.Equal(152, notice.MessageId);
                Assert.Equal(new[] { "2024", "01", "02", "03", "04", "203.0.113.7" }, notice.Parameters);
                Assert.Equal(TimeSpan.FromHours(-7), session.State.PreviousVisit!.When.Offset);
            }
            else
            {
                Assert.Null(session.State.PreviousVisit);
                Assert.Null(notice);
            }
            var recorded = DatabaseManager.ReadLastVisit(id);
            Assert.NotNull(recorded);
            Assert.Equal("198.51.100.8", recorded.Ip);
            Assert.InRange(recorded.When, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        }
        finally { if (id > 0) DatabaseManager.DeleteCharacter(id, account); }
    }
}
