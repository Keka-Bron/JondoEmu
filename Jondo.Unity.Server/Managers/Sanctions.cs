using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the staff's ".mute" and ".ban" put on somebody.
    /// </summary>
    /// <remarks>
    /// A mute is a character's, lasts minutes, and is kept in memory only: a restart gives the
    /// floor back, as the chat's own flood timers do. A ban is an ACCOUNT's -- the person, not the
    /// character -- and is kept in the Bans table of world.db, so a restart does not lift it; the
    /// game entry refuses the account's ticket while it holds (GameEntryBackend.BindTicket), and
    /// the command that bans also kicks the account if it is connected.
    /// </remarks>
    public static class Sanctions
    {
        /// <summary>A ban: whose account, the name it was given under (to lift it by name), and until when.</summary>
        public sealed record Ban(long AccountId, string Name, DateTime? UntilUtc, long BannedBy)
        {
            public bool Holds(DateTime nowUtc) => UntilUtc == null || UntilUtc > nowUtc;
        }

        /// <summary>The longest a mute may last, in minutes: three weeks.</summary>
        public const int MaxMuteMinutes = 60 * 24 * 21;

        private static readonly ConcurrentDictionary<long, DateTime> _mutes = new();
        private static readonly ConcurrentDictionary<long, Ban> _bans = new();

        // ─── Mutes ──────────────────────────────────────────────────────────────────

        public static void Mute(long characterId, TimeSpan time) => _mutes[characterId] = DateTime.UtcNow + time;

        public static bool Unmute(long characterId) => _mutes.TryRemove(characterId, out _);

        public static bool IsMuted(long characterId)
            => characterId > 0 && _mutes.TryGetValue(characterId, out var until) && until > DateTime.UtcNow;

        /// <summary>The minutes a mute has left, rounded up; zero when there is none.</summary>
        public static int MinutesLeft(long characterId)
            => _mutes.TryGetValue(characterId, out var until) && until > DateTime.UtcNow
                ? (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes)
                : 0;

        // ─── Bans ───────────────────────────────────────────────────────────────────

        public static void Initialize()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Bans (
                        AccountId INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL,
                        UntilMs INTEGER,
                        BannedBy INTEGER NOT NULL
                    );";
                create.ExecuteNonQuery();

                _bans.Clear();
                using var read = connection.CreateCommand();
                read.CommandText = "SELECT AccountId, Name, UntilMs, BannedBy FROM Bans;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    DateTime? until = reader.IsDBNull(2)
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).UtcDateTime;
                    _bans[reader.GetInt64(0)] = new Ban(reader.GetInt64(0), reader.GetString(1), until, reader.GetInt64(3));
                }
                Console.WriteLine($"[Sanctions] {_bans.Values.Count(b => b.Holds(DateTime.UtcNow))} ban(s) in force.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sanctions] Could not read the Bans table: {ex.Message}");
            }
        }

        public static bool IsBanned(long accountId)
            => accountId > 0 && _bans.TryGetValue(accountId, out var ban) && ban.Holds(DateTime.UtcNow);

        /// <summary>The bans in force, the ones that end first earliest, the permanent ones last.</summary>
        public static IReadOnlyList<Ban> Bans()
            => _bans.Values.Where(b => b.Holds(DateTime.UtcNow))
                .OrderBy(b => b.UntilUtc ?? DateTime.MaxValue).ToList();

        /// <summary>Bans the account for a time, or for good when it is null.</summary>
        public static Ban Add(long accountId, string name, TimeSpan? time, long bannedBy)
        {
            var ban = new Ban(accountId, name, time == null ? null : DateTime.UtcNow + time.Value, bannedBy);
            _bans[accountId] = ban;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO Bans (AccountId, Name, UntilMs, BannedBy) VALUES ($a, $n, $u, $b) " +
                                      "ON CONFLICT(AccountId) DO UPDATE SET Name = excluded.Name, UntilMs = excluded.UntilMs, BannedBy = excluded.BannedBy;";
                command.Parameters.AddWithValue("$a", accountId);
                command.Parameters.AddWithValue("$n", name);
                command.Parameters.AddWithValue("$u", ban.UntilUtc == null
                    ? DBNull.Value : new DateTimeOffset(ban.UntilUtc.Value).ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$b", bannedBy);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sanctions] Could not store the ban of {name}: {ex.Message}");
            }
            return ban;
        }

        /// <summary>Lifts the ban whose name matches (case aside). False when none holds.</summary>
        public static bool Lift(string name)
        {
            var ban = _bans.Values.FirstOrDefault(b => b.Holds(DateTime.UtcNow)
                && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (ban == null) return false;
            _bans.TryRemove(ban.AccountId, out _);
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM Bans WHERE AccountId = $a;";
                command.Parameters.AddWithValue("$a", ban.AccountId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sanctions] Could not delete the ban of {name}: {ex.Message}");
            }
            return true;
        }

        // ─── Durations ──────────────────────────────────────────────────────────────

        /// <summary>
        /// A duration as the staff types it: a whole number of minutes, or a number with a unit --
        /// "30m", "12h", "7d" -- ("0" is "for good" for a ban, and is told apart by the caller).
        /// False when it is anything else or negative.
        /// </summary>
        public static bool TryParseDuration(string word, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            word = (word ?? "").Trim().ToLowerInvariant();
            if (word.Length == 0) return false;

            double unit = 1;   // minutes
            char last = word[^1];
            if (!char.IsDigit(last))
            {
                unit = last switch { 'm' => 1, 'h' => 60, 'd' => 60 * 24, _ => 0 };
                if (unit == 0) return false;
                word = word[..^1];
            }

            if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out int amount)) return false;
            double minutes = amount * unit;
            if (minutes > 60 * 24 * 3650) return false;
            time = TimeSpan.FromMinutes(minutes);
            return true;
        }
    }
}
