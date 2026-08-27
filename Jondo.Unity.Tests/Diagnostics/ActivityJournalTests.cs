using System;
using System.Collections.Generic;
using System.Text.Json;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.Diagnostics
{
    public class ActivityJournalTests
    {
        [Fact]
        public void One_action_is_one_complete_json_line()
        {
            var lines = new List<string>();
            var instant = new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.FromHours(2));
            var journal = new ActivityJournal(lines.Add, () => instant);

            journal.Write("equipment.moved", 42, 84,
                new { uid = 1234, position = 12, known = true });

            string line = Assert.Single(lines);
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            Assert.Equal("2026-08-27T12:30:00+00:00", root.GetProperty("timestamp").GetString());
            Assert.Equal("equipment.moved", root.GetProperty("event").GetString());
            Assert.Equal(42, root.GetProperty("accountId").GetInt64());
            Assert.Equal(84, root.GetProperty("characterId").GetInt64());
            Assert.Equal(1234, root.GetProperty("details").GetProperty("uid").GetInt64());
            Assert.Equal(12, root.GetProperty("details").GetProperty("position").GetInt32());
            Assert.True(root.GetProperty("details").GetProperty("known").GetBoolean());
        }

        [Fact]
        public void Unknown_identities_are_omitted_instead_of_written_as_zero()
        {
            var lines = new List<string>();
            var journal = new ActivityJournal(lines.Add);

            journal.Write("server.started");

            using var json = JsonDocument.Parse(Assert.Single(lines));
            Assert.False(json.RootElement.TryGetProperty("accountId", out _));
            Assert.False(json.RootElement.TryGetProperty("characterId", out _));
            Assert.False(json.RootElement.TryGetProperty("details", out _));
        }

        [Fact]
        public void Invalid_events_and_unserializable_details_cannot_break_gameplay()
        {
            var lines = new List<string>();
            var journal = new ActivityJournal(lines.Add);
            var cycle = new CyclicDetail();
            cycle.Self = cycle;

            journal.Write("   ");
            Exception? error = Record.Exception(() => journal.Write("broken.detail", details: cycle));

            Assert.Null(error);
            Assert.Empty(lines);
        }

        private sealed class CyclicDetail
        {
            public CyclicDetail? Self { get; set; }
        }
    }
}
