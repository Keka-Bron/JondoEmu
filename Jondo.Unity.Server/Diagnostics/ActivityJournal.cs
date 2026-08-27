using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// Append-only activity journal. Each call produces one self-contained JSON line so an
    /// interrupted write cannot make the rest of the file unreadable.
    ///
    /// Callers deliberately choose the detail fields instead of passing request bodies or packet
    /// payloads. Passwords, launcher tokens and game tickets therefore never reach this log.
    /// </summary>
    public sealed class ActivityJournal
    {
        private readonly Action<string> _writeLine;
        private readonly Func<DateTimeOffset> _clock;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static ActivityJournal Current { get; } =
            new ActivityJournal(LogFile.Activity.WriteLine);

        public ActivityJournal(Action<string> writeLine, Func<DateTimeOffset>? clock = null)
        {
            _writeLine = writeLine ?? throw new ArgumentNullException(nameof(writeLine));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public void Write(string eventName, long accountId = 0, long characterId = 0,
                          object? details = null)
        {
            if (string.IsNullOrWhiteSpace(eventName)) return;

            try
            {
                var entry = new Entry
                {
                    Timestamp = _clock().ToUniversalTime(),
                    Event = eventName.Trim(),
                    AccountId = accountId > 0 ? accountId : null,
                    CharacterId = characterId > 0 ? characterId : null,
                    Details = details,
                };
                _writeLine(JsonSerializer.Serialize(entry, JsonOptions));
            }
            catch (Exception ex)
            {
                // Diagnostics must never interrupt gameplay, but losing the event entirely is the
                // worst thing an audit log can do: the reason it failed is almost always the
                // DETAIL, and dropping the whole line takes the event name, the account and the
                // character with it. So the line is written again without the detail, and what
                // went wrong is said out loud rather than swallowed.
                try
                {
                    _writeLine(JsonSerializer.Serialize(new Entry
                    {
                        Timestamp = _clock().ToUniversalTime(),
                        Event = eventName.Trim(),
                        AccountId = accountId > 0 ? accountId : null,
                        CharacterId = characterId > 0 ? characterId : null,
                        Details = new { journalError = ex.GetType().Name },
                    }, JsonOptions));
                }
                catch
                {
                    // And if even that fails it is the writer, not the detail: LogFile has already
                    // given up on its own and there is nowhere left to say so.
                }
            }
        }

        private sealed class Entry
        {
            public DateTimeOffset Timestamp { get; init; }
            public string Event { get; init; } = "";
            public long? AccountId { get; init; }
            public long? CharacterId { get; init; }
            public object? Details { get; init; }
        }
    }
}
