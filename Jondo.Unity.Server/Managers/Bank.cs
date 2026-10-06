using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What an account keeps in the bank: items and kamas, shared by every character of that
    /// account and kept in world.db.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per ACCOUNT, not per character: the bank is the one storage every character of an account
    /// opens, whoever the banker is and whatever the city. The Bontarian banker's capture
    /// ("Interactivos varios/entrar en banco bonta-abrir cofre gremio-usarlo-abrir cofre personal
    /// del banco") opens it with 1,397 stacks inside, frame 84; that is one account's life in the
    /// game and not one character's.
    /// </para>
    /// <para>
    /// Two tables, created at start with <c>CREATE TABLE IF NOT EXISTS</c> -- datos/world.zip, the
    /// base CI tests against, has never heard of them:
    ///
    ///   BankAccounts (AccountId, Kamas)
    ///   BankItems    (Uid, AccountId, Gid, Quantity, Effects)
    ///
    /// An item keeps what CharacterItems keeps -- template, quantity, effects -- so a stack stored
    /// and taken back is the same stack. It is in the bag or in the bank, never both: every move
    /// takes it out of one table and puts it in the other in ONE transaction.
    /// </para>
    /// <para>
    /// A stack changes uid when it changes side, because the real server does: in that capture the
    /// deposit of 534451715 answers an itd for 537272712 (frames 48-50) and taking that one out
    /// answers an iua for 537283702 (frames 61-64). And an arrival lands on an identical stack when
    /// there is one: the bin in front of that same bank, a storage of the same family, sends ivj on
    /// the bag's stack instead of a new iua from the second unit on ("Interactivos varios/abrir
    /// papelera frente a banco bonta y sacar cosas", frames 31-47). Identical here means the same
    /// template and the same effects; that is the inference behind "the same stack".
    /// </para>
    /// <para>
    /// Thread-safety: every change to an account's bank takes that account's gate, an async lock of
    /// its own. The owner moving things at the counter and the marketplace crediting a sale from the
    /// buyer's session are two sessions touching one row; with the gate they queue instead of
    /// losing one of the two writes. Different accounts never wait on each other.
    /// </para>
    /// </remarks>
    public static class Bank
    {
        /// <summary>The bank's side of a deposit, and what is left of the stack in the bag.</summary>
        public sealed class Deposit
        {
            /// <summary>The bank stack the units went into, with its new total.</summary>
            public HavenBagStore.StoredItem InBank { get; init; } = new HavenBagStore.StoredItem();

            /// <summary>False when they joined a stack that was already in the bank.</summary>
            public bool NewStack { get; init; }

            /// <summary>The bag stack they came from.</summary>
            public long FromUid { get; init; }
            public int Moved { get; init; }

            /// <summary>What stays in the bag. Zero: the whole stack went.</summary>
            public int LeftInBag { get; init; }
        }

        /// <summary>The bag's side of a withdrawal, and what is left of the stack in the bank.</summary>
        public sealed class Withdrawal
        {
            /// <summary>The bag stack the units went into, with its new total.</summary>
            public HavenBagStore.StoredItem InBag { get; init; } = new HavenBagStore.StoredItem();

            /// <summary>True when they joined a stack that was already in the bag.</summary>
            public bool Merged { get; init; }

            /// <summary>The bank stack they came from.</summary>
            public long FromUid { get; init; }
            public int Moved { get; init; }

            /// <summary>What stays of it in the bank, whole, or null when it went entirely.</summary>
            public HavenBagStore.StoredItem? LeftInBank { get; init; }
        }

        /// <summary>Kamas that changed side: where each total stands afterwards.</summary>
        public readonly record struct KamasMove(long Character, long Bank, long Moved);

        /// <summary>What opening the bank found, and whether the fee was paid.</summary>
        public sealed class Opening
        {
            public IReadOnlyList<HavenBagStore.StoredItem> Items { get; init; } = Array.Empty<HavenBagStore.StoredItem>();
            public long Kamas { get; init; }

            /// <summary>One kama a stack, see <see cref="FeeOf"/>.</summary>
            public long Fee { get; init; }

            /// <summary>False when the character could not pay it; nothing was taken then.</summary>
            public bool Paid { get; init; }

            /// <summary>The character's kamas once the fee is paid.</summary>
            public long CharacterKamas { get; init; }
        }

        private static readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();
        private static readonly object _tablesLock = new object();
        private static volatile bool _tablesReady;

        private static SemaphoreSlim GateOf(long accountId)
            => _gates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

        // â”€â”€â”€ Tables â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Creates the tables if they are not there. Called at start, and again, for nothing, by
        /// every read and write: the marketplace or a test may reach the bank before the server's
        /// own start-up has.
        /// </summary>
        public static void Initialize()
        {
            EnsureTables();
            Console.WriteLine($"[Bank] {Accounts()} account(s) with something in the bank.");
        }

        private static void EnsureTables()
        {
            if (_tablesReady) return;
            lock (_tablesLock)
            {
                if (_tablesReady) return;
                try
                {
                    using var connection = Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            CREATE TABLE IF NOT EXISTS BankAccounts (
                                AccountId INTEGER PRIMARY KEY,
                                Kamas     INTEGER NOT NULL DEFAULT 0);

                            CREATE TABLE IF NOT EXISTS BankItems (
                                Uid       INTEGER PRIMARY KEY,
                                AccountId INTEGER NOT NULL,
                                Gid       INTEGER NOT NULL,
                                Quantity  INTEGER NOT NULL DEFAULT 1,
                                Effects   TEXT);

                            CREATE INDEX IF NOT EXISTS BankItemsByAccount ON BankItems (AccountId);";
                        command.ExecuteNonQuery();
                    }

                    // A bank uid comes from the same dispenser as any other item's, and that one
                    // starts above the highest uid in CharacterItems only. Without this, the first
                    // start after the highest-numbered item went into the bank would hand its uid
                    // out again.
                    using (var highest = connection.CreateCommand())
                    {
                        highest.CommandText = "SELECT MAX(Uid) FROM BankItems;";
                        if (highest.ExecuteScalar() is long max) DatabaseManager.KeepUidsAbove(max);
                    }

                    _tablesReady = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Bank] The tables could not be created: {ex.Message}");
                }
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            return connection;
        }

        private static long Accounts()
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM (SELECT AccountId FROM BankItems " +
                                      "UNION SELECT AccountId FROM BankAccounts WHERE Kamas > 0);";
                return command.ExecuteScalar() is long n ? n : 0;
            }
            catch
            {
                return 0;
            }
        }

        // â”€â”€â”€ Reading â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>The kamas in this account's bank.</summary>
        public static long KamasOf(long accountId)
        {
            EnsureTables();
            try
            {
                using var connection = Open();
                return KamasOf(connection, null, accountId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] Could not read the kamas of account {accountId}: {ex.Message}");
                return 0;
            }
        }

        private static long KamasOf(SqliteConnection connection, SqliteTransaction? transaction, long accountId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Kamas FROM BankAccounts WHERE AccountId = $a;";
            command.Parameters.AddWithValue("$a", accountId);
            return command.ExecuteScalar() is long kamas ? kamas : 0;
        }

        /// <summary>Every stack in this account's bank, oldest uid first.</summary>
        public static List<HavenBagStore.StoredItem> ItemsOf(long accountId)
        {
            EnsureTables();
            try
            {
                using var connection = Open();
                return ItemsOf(connection, accountId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] Could not read the items of account {accountId}: {ex.Message}");
                return new List<HavenBagStore.StoredItem>();
            }
        }

        private static List<HavenBagStore.StoredItem> ItemsOf(SqliteConnection connection, long accountId)
        {
            var items = new List<HavenBagStore.StoredItem>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Uid, Gid, Quantity, Effects FROM BankItems " +
                                  "WHERE AccountId = $a ORDER BY Uid;";
            command.Parameters.AddWithValue("$a", accountId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new HavenBagStore.StoredItem
                {
                    Uid = reader.GetInt64(0),
                    Gid = reader.GetInt32(1),
                    Quantity = reader.IsDBNull(2) ? 1 : reader.GetInt32(2),
                    Effects = reader.IsDBNull(3) ? "" : reader.GetString(3),
                });
            }
            return items;
        }

        /// <summary>
        /// What opening the bank costs: one kama a stack.
        /// </summary>
        /// <remarks>
        /// Measured, on the one bank visit there is. The banker says the price before anything is
        /// agreed -- ios f3 "1397", frame 74 -- the server charges exactly that once the player
        /// asks for the chest -- lqn 20 "Has pagado $quantity{0} kamas para acceder a este cofre"
        /// with "1397", frame 81 -- and the content it then sends, frame 84, is 1,397 entries:
        /// 1,300 templates, 43,449 units. It is the stacks that are counted, not the units nor the
        /// templates. The banker's own sentence says the same in words: "El precio de las
        /// consultas dependerá de la cantidad de objetos que metas dentro".
        /// </remarks>
        public static long FeeOf(long accountId) => ItemsOf(accountId).Count;

        // â”€â”€â”€ Kamas â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Adds kamas to this account's bank (or takes them, with a negative amount; never below
        /// zero), persisted at once. If a character of that account has the bank window open, it
        /// is told. False when it could not be done.
        /// </summary>
        /// <remarks>
        /// A take larger than what is there is refused whole rather than clamped, so the caller
        /// learns that it did not happen: false, and the bank untouched. So is a total that would
        /// overflow. Zero changes nothing and is true.
        ///
        /// Safe from any session and from none: the seller the marketplace credits may be online,
        /// at the counter, or long gone. Online at the counter, every one of his windows gets the
        /// bank's content again with the new total -- see <see cref="Handlers.BankHandler"/>.
        /// </remarks>
        public static async Task<bool> AddKamasAsync(long accountId, long amount)
        {
            if (accountId <= 0) return false;
            if (amount == 0) return true;

            EnsureTables();
            var gate = GateOf(accountId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();

                long had = KamasOf(connection, transaction, accountId);
                long now;
                try
                {
                    now = checked(had + amount);
                }
                catch (OverflowException)
                {
                    return false;
                }
                if (now < 0) return false;

                WriteKamas(connection, transaction, accountId, now);
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] Could not add {amount} kamas to account {accountId}: {ex.Message}");
                return false;
            }
            finally
            {
                gate.Release();
            }

            await Handlers.BankHandler.RefreshWindowsAsync(accountId, null).ConfigureAwait(false);
            return true;
        }

        private static void WriteKamas(SqliteConnection connection, SqliteTransaction transaction,
                                       long accountId, long kamas)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO BankAccounts (AccountId, Kamas) VALUES ($a, $k) " +
                                  "ON CONFLICT(AccountId) DO UPDATE SET Kamas = $k;";
            command.Parameters.AddWithValue("$a", accountId);
            command.Parameters.AddWithValue("$k", kamas);
            command.ExecuteNonQuery();
        }

        private static void WriteCharacterKamas(SqliteConnection connection, SqliteTransaction transaction,
                                                long characterId, long kamas)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Characters SET Kamas = $k WHERE Id = $id;";
            command.Parameters.AddWithValue("$k", kamas);
            command.Parameters.AddWithValue("$id", characterId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Kamas between a character and the bank: in with a positive amount, out with a negative
        /// one. Both totals are written in one transaction.
        /// </summary>
        /// <remarks>
        /// Clamped to what the giving side has, as a trade clamps what one lays on the table; the
        /// client does not offer more anyway. Null when nothing moves.
        ///
        /// <paramref name="characterKamas"/> is the character's purse as the session holds it --
        /// that is the one that counts while he is online -- and what comes back is what it has to
        /// become.
        /// </remarks>
        public static async Task<KamasMove?> MoveKamasAsync(long accountId, long characterId,
                                                            long characterKamas, long amount)
        {
            if (accountId <= 0 || amount == 0) return null;

            EnsureTables();
            var gate = GateOf(accountId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();

                long bank = KamasOf(connection, transaction, accountId);
                long moved = amount > 0
                    ? Math.Min(amount, Math.Max(0, characterKamas))
                    : -Math.Min(-amount, bank);
                if (moved == 0) return null;

                long character = characterKamas - moved;
                long banked = bank + moved;

                WriteKamas(connection, transaction, accountId, banked);
                WriteCharacterKamas(connection, transaction, characterId, character);
                transaction.Commit();
                return new KamasMove(character, banked, moved);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] Could not move {amount} kamas for account {accountId}: {ex.Message}");
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        // â”€â”€â”€ Opening â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// What is inside, with the fee taken from the character: see <see cref="FeeOf"/>.
        /// </summary>
        /// <remarks>
        /// Read and charged under the gate, so the price is the price of what the window will show.
        /// A character who cannot pay gets <c>Paid = false</c> and loses nothing; the capture never
        /// shows that case, so what the counter says then is the handler's inference.
        /// </remarks>
        public static async Task<Opening?> OpenAsync(long accountId, long characterId, long characterKamas)
        {
            if (accountId <= 0) return null;

            EnsureTables();
            var gate = GateOf(accountId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = Open();
                var items = ItemsOf(connection, accountId);
                long kamas = KamasOf(connection, null, accountId);
                long fee = items.Count;

                if (characterKamas < fee)
                {
                    return new Opening
                    {
                        Items = items, Kamas = kamas, Fee = fee, Paid = false, CharacterKamas = characterKamas,
                    };
                }

                if (fee > 0)
                {
                    using var transaction = connection.BeginTransaction();
                    WriteCharacterKamas(connection, transaction, characterId, characterKamas - fee);
                    transaction.Commit();
                }

                return new Opening
                {
                    Items = items, Kamas = kamas, Fee = fee, Paid = true, CharacterKamas = characterKamas - fee,
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] Could not open the bank of account {accountId}: {ex.Message}");
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        // â”€â”€â”€ Items â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// From the bag to the bank: <paramref name="quantity"/> units of the bag stack
        /// <paramref name="uid"/>, or the whole stack if it has fewer. Null when it cannot be done.
        /// </summary>
        /// <remarks>
        /// Only the bag. A worn item is refused: the capture shows nothing but bag stacks going in,
        /// and taking a ring off a finger into the bank would leave the sheet counting a ring that
        /// is not there until the next relog.
        /// </remarks>
        public static async Task<Deposit?> DepositAsync(long accountId, long characterId, long uid, int quantity)
        {
            if (accountId <= 0 || uid == 0 || quantity <= 0) return null;

            EnsureTables();
            var gate = GateOf(accountId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = Open();

                int gid, have, position;
                string effects;
                using (var read = connection.CreateCommand())
                {
                    read.CommandText = "SELECT Gid, Quantity, Position, Effects FROM CharacterItems " +
                                       "WHERE Uid = $uid AND CharacterId = $c;";
                    read.Parameters.AddWithValue("$uid", uid);
                    read.Parameters.AddWithValue("$c", characterId);
                    using var reader = read.ExecuteReader();
                    if (!reader.Read()) return null;
                    gid = reader.GetInt32(0);
                    have = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
                    position = reader.IsDBNull(2) ? Equipment.Bag : reader.GetInt32(2);
                    effects = reader.IsDBNull(3) ? "" : reader.GetString(3);
                }
                if (position != Equipment.Bag || have <= 0) return null;

                int moving = Math.Min(quantity, have);

                using var transaction = connection.BeginTransaction();

                // Out of the bag, the owner in every statement as everywhere else.
                using (var take = connection.CreateCommand())
                {
                    take.Transaction = transaction;
                    take.CommandText = moving >= have
                        ? "DELETE FROM CharacterItems WHERE Uid = $uid AND CharacterId = $c;"
                        : "UPDATE CharacterItems SET Quantity = Quantity - $n WHERE Uid = $uid AND CharacterId = $c;";
                    take.Parameters.AddWithValue("$uid", uid);
                    take.Parameters.AddWithValue("$c", characterId);
                    if (moving < have) take.Parameters.AddWithValue("$n", moving);
                    if (take.ExecuteNonQuery() != 1) return null;
                }

                // Into the bank: on an identical stack if there is one, else as a new one.
                long into = SameStack(connection, transaction,
                    "SELECT Uid, Quantity FROM BankItems WHERE AccountId = $o AND Gid = $gid " +
                    "AND IFNULL(Effects, '') = $e ORDER BY Uid LIMIT 1;", accountId, gid, effects, out int there);

                bool fresh = into == 0;
                if (fresh)
                {
                    into = DatabaseManager.NextItemUid();
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO BankItems (Uid, AccountId, Gid, Quantity, Effects) " +
                                         "VALUES ($uid, $a, $gid, $n, $e);";
                    insert.Parameters.AddWithValue("$uid", into);
                    insert.Parameters.AddWithValue("$a", accountId);
                    insert.Parameters.AddWithValue("$gid", gid);
                    insert.Parameters.AddWithValue("$n", moving);
                    insert.Parameters.AddWithValue("$e", effects);
                    insert.ExecuteNonQuery();
                }
                else
                {
                    using var grow = connection.CreateCommand();
                    grow.Transaction = transaction;
                    grow.CommandText = "UPDATE BankItems SET Quantity = Quantity + $n WHERE Uid = $uid AND AccountId = $a;";
                    grow.Parameters.AddWithValue("$n", moving);
                    grow.Parameters.AddWithValue("$uid", into);
                    grow.Parameters.AddWithValue("$a", accountId);
                    grow.ExecuteNonQuery();
                }

                transaction.Commit();

                return new Deposit
                {
                    InBank = new HavenBagStore.StoredItem
                    {
                        Uid = into, Gid = gid, Quantity = there + moving, Effects = effects,
                    },
                    NewStack = fresh,
                    FromUid = uid,
                    Moved = moving,
                    LeftInBag = have - moving,
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] Could not put {uid} in the bank of account {accountId}: {ex.Message}");
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Puts items straight into an account's bank, from nowhere in particular: onto an identical
        /// stack if there is one, else as a new stack. What a marketplace gives back when a lot's
        /// time on sale is over. Safe from any session and from none; an open bank window of that
        /// account is refreshed. False when nothing was put in.
        /// </summary>
        public static async Task<bool> AddItemAsync(long accountId, int gid, int quantity, string? effects)
        {
            if (accountId <= 0 || gid <= 0 || quantity <= 0) return false;
            string stored = effects ?? "";

            EnsureTables();
            var gate = GateOf(accountId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();

                long into = SameStack(connection, transaction,
                    "SELECT Uid, Quantity FROM BankItems WHERE AccountId = $o AND Gid = $gid " +
                    "AND IFNULL(Effects, '') = $e ORDER BY Uid LIMIT 1;", accountId, gid, stored, out _);

                using var write = connection.CreateCommand();
                write.Transaction = transaction;
                if (into == 0)
                {
                    write.CommandText = "INSERT INTO BankItems (Uid, AccountId, Gid, Quantity, Effects) " +
                                        "VALUES ($uid, $a, $gid, $n, $e);";
                    write.Parameters.AddWithValue("$uid", DatabaseManager.NextItemUid());
                    write.Parameters.AddWithValue("$gid", gid);
                    write.Parameters.AddWithValue("$e", stored);
                }
                else
                {
                    write.CommandText = "UPDATE BankItems SET Quantity = Quantity + $n WHERE Uid = $uid AND AccountId = $a;";
                    write.Parameters.AddWithValue("$uid", into);
                }
                write.Parameters.AddWithValue("$a", accountId);
                write.Parameters.AddWithValue("$n", quantity);
                write.ExecuteNonQuery();
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] Could not put {gid} x{quantity} in the bank of account {accountId}: {ex.Message}");
                return false;
            }
            finally
            {
                gate.Release();
            }

            await Handlers.BankHandler.RefreshWindowsAsync(accountId, null).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// From the bank to the bag: <paramref name="quantity"/> units of the bank stack
        /// <paramref name="uid"/>, or the whole stack if it has fewer. Null when it cannot be done.
        /// </summary>
        public static async Task<Withdrawal?> WithdrawAsync(long accountId, long characterId, long uid, int quantity)
        {
            if (accountId <= 0 || uid == 0 || quantity <= 0) return null;

            EnsureTables();
            var gate = GateOf(accountId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = Open();

                int gid, have;
                string effects;
                using (var read = connection.CreateCommand())
                {
                    read.CommandText = "SELECT Gid, Quantity, Effects FROM BankItems " +
                                       "WHERE Uid = $uid AND AccountId = $a;";
                    read.Parameters.AddWithValue("$uid", uid);
                    read.Parameters.AddWithValue("$a", accountId);
                    using var reader = read.ExecuteReader();
                    if (!reader.Read()) return null;
                    gid = reader.GetInt32(0);
                    have = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
                    effects = reader.IsDBNull(2) ? "" : reader.GetString(2);
                }
                if (have <= 0) return null;

                int moving = Math.Min(quantity, have);

                using var transaction = connection.BeginTransaction();

                using (var take = connection.CreateCommand())
                {
                    take.Transaction = transaction;
                    take.CommandText = moving >= have
                        ? "DELETE FROM BankItems WHERE Uid = $uid AND AccountId = $a;"
                        : "UPDATE BankItems SET Quantity = Quantity - $n WHERE Uid = $uid AND AccountId = $a;";
                    take.Parameters.AddWithValue("$uid", uid);
                    take.Parameters.AddWithValue("$a", accountId);
                    if (moving < have) take.Parameters.AddWithValue("$n", moving);
                    if (take.ExecuteNonQuery() != 1) return null;
                }

                long into = SameStack(connection, transaction,
                    "SELECT Uid, Quantity FROM CharacterItems WHERE CharacterId = $o AND Gid = $gid " +
                    "AND Position = " + Equipment.Bag + " AND IFNULL(Effects, '') = $e ORDER BY Uid LIMIT 1;",
                    characterId, gid, effects, out int there);

                bool merged = into != 0;
                if (!merged)
                {
                    into = DatabaseManager.NextItemUid();
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO CharacterItems (CharacterId, Uid, Gid, Quantity, Position, Effects) " +
                                         "VALUES ($c, $uid, $gid, $n, $pos, $e);";
                    insert.Parameters.AddWithValue("$c", characterId);
                    insert.Parameters.AddWithValue("$uid", into);
                    insert.Parameters.AddWithValue("$gid", gid);
                    insert.Parameters.AddWithValue("$n", moving);
                    insert.Parameters.AddWithValue("$pos", Equipment.Bag);
                    insert.Parameters.AddWithValue("$e", effects);
                    insert.ExecuteNonQuery();
                }
                else
                {
                    using var grow = connection.CreateCommand();
                    grow.Transaction = transaction;
                    grow.CommandText = "UPDATE CharacterItems SET Quantity = Quantity + $n WHERE Uid = $uid AND CharacterId = $c;";
                    grow.Parameters.AddWithValue("$n", moving);
                    grow.Parameters.AddWithValue("$uid", into);
                    grow.Parameters.AddWithValue("$c", characterId);
                    grow.ExecuteNonQuery();
                }

                transaction.Commit();

                return new Withdrawal
                {
                    InBag = new HavenBagStore.StoredItem
                    {
                        Uid = into, Gid = gid, Quantity = there + moving, Effects = effects,
                    },
                    Merged = merged,
                    FromUid = uid,
                    Moved = moving,
                    LeftInBank = moving >= have
                        ? null
                        : new HavenBagStore.StoredItem { Uid = uid, Gid = gid, Quantity = have - moving, Effects = effects },
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] Could not take {uid} out of the bank of account {accountId}: {ex.Message}");
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>The stack an arrival joins: its uid and how many it has, or zero.</summary>
        private static long SameStack(SqliteConnection connection, SqliteTransaction transaction, string sql,
                                      long owner, int gid, string effects, out int quantity)
        {
            quantity = 0;
            using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = sql;
            find.Parameters.AddWithValue("$o", owner);
            find.Parameters.AddWithValue("$gid", gid);
            find.Parameters.AddWithValue("$e", effects ?? "");
            using var reader = find.ExecuteReader();
            if (!reader.Read()) return 0;
            quantity = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
            return reader.GetInt64(0);
        }

        /// <summary>Whether this uid is one of this account's bank stacks.</summary>
        public static bool Holds(long accountId, long uid) => ItemsOf(accountId).Any(i => i.Uid == uid);
    }
}
