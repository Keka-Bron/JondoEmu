using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The friends window's in-game lists, per account: the contacts one has added, the players
    /// one has blocked and the ones one ignores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A contact is one-way: adding someone puts him in one's own list only. Two accounts that
    /// have added each other are mutual friends, and only they see each other playing (character,
    /// level, class, whether in a fight); a one-way contact shows his account and nothing else.
    /// That is the split the client draws between its «mutual friends» and «contacts» sections,
    /// and the real server's lists keep them apart.
    /// </para>
    /// <para>
    /// Contacts and blocks are kept in world.db, in a table created at first use:
    /// </para>
    /// <code>
    ///   AccountContacts (AccountId, OtherId, Kind)   Kind 0 contact, 1 blocked
    ///   AccountConnectionWarnings (AccountId)        warned when a mutual friend connects
    /// </code>
    /// <para>
    /// Ignoring lasts the session: it is kept in memory and forgotten when the account leaves.
    /// </para>
    /// </remarks>
    public static class Contacts
    {
        public const int Contact = 0;
        public const int Blocked = 1;

        private static readonly object _gate = new object();
        private static readonly Dictionary<long, HashSet<long>> _ignored = new();
        private static volatile bool _tableReady;

        /// <summary>The accounts this one has added, oldest first.</summary>
        public static IReadOnlyList<long> ContactsOf(long accountId) => Read(accountId, Contact);

        /// <summary>The accounts this one has blocked, oldest first.</summary>
        public static IReadOnlyList<long> BlockedOf(long accountId) => Read(accountId, Blocked);

        /// <summary>The accounts this one ignores this session.</summary>
        public static IReadOnlyList<long> IgnoredOf(long accountId)
        {
            lock (_gate)
                return _ignored.TryGetValue(accountId, out var set) ? set.ToList() : new List<long>();
        }

        /// <summary>The accounts that have this one among their contacts.</summary>
        public static IReadOnlyList<long> ListedBy(long accountId)
        {
            var found = new List<long>();
            if (accountId <= 0) return found;
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT AccountId FROM AccountContacts WHERE OtherId = $o AND Kind = $k ORDER BY rowid;";
                command.Parameters.AddWithValue("$o", accountId);
                command.Parameters.AddWithValue("$k", Contact);
                using var reader = command.ExecuteReader();
                while (reader.Read()) found.Add(reader.GetInt64(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Contacts] Could not read who lists {accountId}: {ex.Message}");
            }
            return found;
        }

        public static bool IsContact(long accountId, long otherId) => ContactsOf(accountId).Contains(otherId);

        public static bool IsBlocked(long accountId, long otherId) => BlockedOf(accountId).Contains(otherId);

        public static bool IsIgnored(long accountId, long otherId)
        {
            lock (_gate)
                return _ignored.TryGetValue(accountId, out var set) && set.Contains(otherId);
        }

        /// <summary>Both have added each other: they see each other playing.</summary>
        public static bool AreMutual(long a, long b) => IsContact(a, b) && IsContact(b, a);

        /// <summary>Whether a player keeps another away: blocked, or ignored this session.</summary>
        public static bool Shuts(long accountId, long otherId)
            => accountId > 0 && otherId > 0 && (IsBlocked(accountId, otherId) || IsIgnored(accountId, otherId));

        /// <summary>Puts another account in one of the lists; false if it was there already.</summary>
        public static bool Add(long accountId, long otherId, int kind)
        {
            if (accountId <= 0 || otherId <= 0) return false;
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO AccountContacts (AccountId, OtherId, Kind) VALUES ($a, $o, $k);";
                command.Parameters.AddWithValue("$a", accountId);
                command.Parameters.AddWithValue("$o", otherId);
                command.Parameters.AddWithValue("$k", kind);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Contacts] Could not add {otherId} to the list {kind} of {accountId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Takes another account out of one of the lists; false if it was not there.</summary>
        public static bool Remove(long accountId, long otherId, int kind)
        {
            if (accountId <= 0 || otherId <= 0) return false;
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM AccountContacts WHERE AccountId = $a AND OtherId = $o AND Kind = $k;";
                command.Parameters.AddWithValue("$a", accountId);
                command.Parameters.AddWithValue("$o", otherId);
                command.Parameters.AddWithValue("$k", kind);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Contacts] Could not remove {otherId} from the list {kind} of {accountId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Ignores another account until this one leaves; false if it was ignored already.</summary>
        public static bool Ignore(long accountId, long otherId)
        {
            if (accountId <= 0 || otherId <= 0) return false;
            lock (_gate)
            {
                if (!_ignored.TryGetValue(accountId, out var set)) _ignored[accountId] = set = new HashSet<long>();
                return set.Add(otherId);
            }
        }

        /// <summary>Stops ignoring another account; false if it was not ignored.</summary>
        public static bool Unignore(long accountId, long otherId)
        {
            lock (_gate)
                return _ignored.TryGetValue(accountId, out var set) && set.Remove(otherId);
        }

        /// <summary>
        /// Whether the account wants to hear when a mutual friend connects: the friends window's
        /// «Avertir quand mes amis se connectent», kept in world.db as AccountConnectionWarnings.
        /// </summary>
        public static bool WarnsOnConnection(long accountId)
        {
            if (accountId <= 0) return false;
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM AccountConnectionWarnings WHERE AccountId = $a;";
                command.Parameters.AddWithValue("$a", accountId);
                return command.ExecuteScalar() != null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Contacts] Could not read the connection warning of {accountId}: {ex.Message}");
                return false;
            }
        }

        public static void SetWarnsOnConnection(long accountId, bool warns)
        {
            if (accountId <= 0) return;
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = warns
                    ? "INSERT OR IGNORE INTO AccountConnectionWarnings (AccountId) VALUES ($a);"
                    : "DELETE FROM AccountConnectionWarnings WHERE AccountId = $a;";
                command.Parameters.AddWithValue("$a", accountId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Contacts] Could not save the connection warning of {accountId}: {ex.Message}");
            }
        }

        /// <summary>The account left: the session's ignored list goes with it.</summary>
        public static void ForgetSession(long accountId)
        {
            lock (_gate) _ignored.Remove(accountId);
        }

        private static IReadOnlyList<long> Read(long accountId, int kind)
        {
            var found = new List<long>();
            if (accountId <= 0) return found;
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT OtherId FROM AccountContacts WHERE AccountId = $a AND Kind = $k ORDER BY rowid;";
                command.Parameters.AddWithValue("$a", accountId);
                command.Parameters.AddWithValue("$k", kind);
                using var reader = command.ExecuteReader();
                while (reader.Read()) found.Add(reader.GetInt64(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Contacts] Could not read the list {kind} of {accountId}: {ex.Message}");
            }
            return found;
        }

        private static void EnsureTable()
        {
            if (_tableReady) return;
            lock (_gate)
            {
                if (_tableReady) return;
                using var connection = Open();
                using var create = connection.CreateCommand();
                create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS AccountContacts (
                        AccountId INTEGER NOT NULL,
                        OtherId   INTEGER NOT NULL,
                        Kind      INTEGER NOT NULL,
                        PRIMARY KEY (AccountId, OtherId, Kind));
                    CREATE TABLE IF NOT EXISTS AccountConnectionWarnings (
                        AccountId INTEGER NOT NULL PRIMARY KEY);";
                create.ExecuteNonQuery();
                _tableReady = true;
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            return connection;
        }
    }
}
