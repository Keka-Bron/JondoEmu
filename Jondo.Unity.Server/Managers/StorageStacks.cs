using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Stacks of items kept outside a character's bag -- a house chest, a bin, a guild chest tab,
    /// the haven bag chest -- and the one way they move between the bag and there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are the bank's (see <see cref="Bank"/>), measured on the storages of the same
    /// family, frame numbers being positions in <c>hilo.tramas</c>:
    ///
    ///   a stack changes uid when it changes side. House chest ("Casas/abrir cofre de la
    ///   casa-mover items-cerrarlo"): 268210 comes out as 576937855 (frames 10-12) and goes back in
    ///   as 576943299 (16-18). Haven bag chest ("Interactivos varios/abrir cofre de mi
    ///   merkasako-cambiar cosas entre cofre e inventario-cerrar"): the same, frames 12-29.
    ///
    ///   kcr's f1 is a signed count: positive puts that many in, negative takes that many out.
    ///   The bin in front of the Bonta bank ("abrir papelera frente a banco bonta y sacar cosas",
    ///   frames 31-47) takes ONE unit per -1 off a stack of four: the storage's stack is sent again
    ///   with 3, 2, 1 (itd) and the fourth goes (itc).
    ///
    ///   an arrival lands on an identical stack when there is one: the same bin sends ivj 2, 3, 4 on
    ///   the bag's stack from the second unit on. Identical means the same template and the same
    ///   effects; that is the inference behind "the same stack", as it is the bank's.
    /// </para>
    /// <para>
    /// Two tables. The haven bag chest keeps its own, HavenBagChest, one row per stack with the
    /// character that owns it. Everything else shares StorageItems, where a storage is named by a
    /// text key -- "house:map:door:chest", "bin:map:element", "guild:guild:tab" -- so a new storage
    /// is a new key and not a new table. Created at start with CREATE TABLE IF NOT EXISTS:
    /// datos/world.zip, the base CI tests against, has never heard of it.
    /// </para>
    /// <para>
    /// A stack is in the bag or in the storage, never both: every move takes it out of one table
    /// and puts it in the other in ONE transaction. Only bag stacks go in; a worn item is refused,
    /// as the bank refuses it.
    /// </para>
    /// <para>
    /// Thread-safety: every move takes the storage's lock. Bins are public and a guild chest is
    /// the whole guild's, so two players may take the last unit of the same stack at the same
    /// moment; with the lock one of them gets it and the other gets nothing.
    /// </para>
    /// </remarks>
    public static class StorageStacks
    {
        /// <summary>Where a storage lives: its table, the column naming its owner, and the owner.</summary>
        public readonly record struct Place(string Table, string OwnerColumn, object Owner)
        {
            /// <summary>One string per storage, for its lock and for finding who else has it open.</summary>
            public string Key => $"{Table}:{Owner}";

            public override string ToString() => Key;
        }

        public const string SharedTable = "StorageItems";

        /// <summary>A chest inside a house, for the house of that door.</summary>
        public static Place HouseChest(long doorMapId, int doorElementId, int chestElementId)
            => new Place(SharedTable, "Storage", $"house:{doorMapId}:{doorElementId}:{chestElementId}");

        /// <summary>The prefix every chest of one house shares, to empty them all when it is sold.</summary>
        public static string HouseChestsPrefix(long doorMapId, int doorElementId)
            => $"house:{doorMapId}:{doorElementId}:";

        /// <summary>A bin: one per element, public.</summary>
        public static Place Bin(long mapId, int elementId)
            => new Place(SharedTable, "Storage", $"bin:{mapId}:{elementId}");

        /// <summary>One tab of a guild's chest.</summary>
        public static Place GuildChest(long guildId, int tab)
            => new Place(SharedTable, "Storage", $"guild:{guildId}:{tab}");

        /// <summary>A character's haven bag chest, in the table it always had.</summary>
        public static Place HavenBag(long characterId)
            => new Place("HavenBagChest", "CharacterId", characterId);

        /// <summary>What went in: the storage's stack with its new total, and what stays in the bag.</summary>
        public sealed class Deposit
        {
            public HavenBagStore.StoredItem InStorage { get; init; } = new HavenBagStore.StoredItem();

            /// <summary>False when the units joined a stack that was already there.</summary>
            public bool NewStack { get; init; }

            public long FromUid { get; init; }
            public int Moved { get; init; }

            /// <summary>What is left of the bag's stack. Zero: it went whole.</summary>
            public int LeftInBag { get; init; }
        }

        /// <summary>What came out: the bag's stack with its new total, and what stays in the storage.</summary>
        public sealed class Withdrawal
        {
            public HavenBagStore.StoredItem InBag { get; init; } = new HavenBagStore.StoredItem();

            /// <summary>True when the units joined a stack that was already in the bag.</summary>
            public bool Merged { get; init; }

            public long FromUid { get; init; }
            public int Moved { get; init; }

            /// <summary>What stays of the storage's stack, whole, or null when it went entirely.</summary>
            public HavenBagStore.StoredItem? LeftInStorage { get; init; }
        }

        private static readonly ConcurrentDictionary<string, object> _locks = new();
        private static readonly object _tablesLock = new object();
        private static volatile bool _tablesReady;

        private static object LockOf(Place place) => _locks.GetOrAdd(place.Key, _ => new object());

        // ─── Tables ─────────────────────────────────────────────────────────────

        public static void Initialize()
        {
            EnsureTables();
            Console.WriteLine($"[Storage] {Count()} stack(s) in house chests, bins and guild chests.");
        }

        /// <summary>
        /// Creates StorageItems if it is not there, and keeps the uid dispenser above every uid
        /// stored outside CharacterItems. Called at start and, for nothing, before every read and
        /// write: a test may reach a storage before the server's own start-up has.
        /// </summary>
        /// <remarks>
        /// The dispenser starts above the highest uid in CharacterItems and nothing else
        /// (<see cref="DatabaseManager.KeepUidsAbove"/>). A stack that went into a chest with the
        /// highest uid of all would otherwise have its number handed out again on the next start,
        /// and the item created with it would share a uid with the one in the chest. The haven bag
        /// chest had exactly that hole; its table is protected here as well.
        /// </remarks>
        public static void EnsureTables()
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
                            CREATE TABLE IF NOT EXISTS StorageItems (
                                Uid      INTEGER PRIMARY KEY,
                                Storage  TEXT NOT NULL,
                                Gid      INTEGER NOT NULL,
                                Quantity INTEGER NOT NULL DEFAULT 1,
                                Effects  TEXT);

                            CREATE INDEX IF NOT EXISTS StorageItemsByStorage ON StorageItems (Storage);

                            CREATE TABLE IF NOT EXISTS HavenBagChest (
                                Uid         INTEGER PRIMARY KEY,
                                CharacterId INTEGER NOT NULL,
                                Gid         INTEGER NOT NULL,
                                Quantity    INTEGER NOT NULL DEFAULT 1,
                                Effects     TEXT);";
                        command.ExecuteNonQuery();
                    }

                    ProtectUids(connection);
                    _tablesReady = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Storage] The tables could not be created: {ex.Message}");
                }
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>Tells the uid dispenser the highest uid kept in these tables. See <see cref="EnsureTables"/>.</summary>
        internal static void ProtectUids()
        {
            using var connection = Open();
            ProtectUids(connection);
        }

        private static void ProtectUids(SqliteConnection connection)
        {
            foreach (string table in new[] { SharedTable, "HavenBagChest" })
            {
                using var highest = connection.CreateCommand();
                highest.CommandText = $"SELECT MAX(Uid) FROM {table};";
                if (highest.ExecuteScalar() is long max) DatabaseManager.KeepUidsAbove(max);
            }
        }

        private static long Count()
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {SharedTable};";
                return command.ExecuteScalar() is long n ? n : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ─── Reading ────────────────────────────────────────────────────────────

        /// <summary>Every stack in this storage, oldest uid first.</summary>
        public static List<HavenBagStore.StoredItem> ItemsOf(Place place)
        {
            EnsureTables();
            var items = new List<HavenBagStore.StoredItem>();
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT Uid, Gid, Quantity, Effects FROM {place.Table} " +
                                      $"WHERE {place.OwnerColumn} = $o ORDER BY Uid;";
                command.Parameters.AddWithValue("$o", place.Owner);
                using var reader = command.ExecuteReader();
                while (reader.Read()) items.Add(Read(reader));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Could not read {place}: {ex.Message}");
            }
            return items;
        }

        /// <summary>One stack of this storage, or null.</summary>
        public static HavenBagStore.StoredItem? Find(Place place, long uid)
        {
            foreach (var item in ItemsOf(place))
            {
                if (item.Uid == uid) return item;
            }
            return null;
        }

        public static bool Holds(Place place, long uid) => Find(place, uid) != null;

        private static HavenBagStore.StoredItem Read(SqliteDataReader reader) => new HavenBagStore.StoredItem
        {
            Uid = reader.GetInt64(0),
            Gid = reader.GetInt32(1),
            Quantity = reader.IsDBNull(2) ? 1 : reader.GetInt32(2),
            Effects = reader.IsDBNull(3) ? "" : reader.GetString(3),
        };

        // ─── Moving ─────────────────────────────────────────────────────────────

        /// <summary>
        /// From the bag to the storage: <paramref name="quantity"/> units of the bag stack
        /// <paramref name="uid"/>, or the whole stack if it has fewer. Null when nothing moved.
        /// </summary>
        public static Deposit? Put(Place place, long characterId, long uid, int quantity)
        {
            if (uid == 0 || quantity <= 0) return null;
            EnsureTables();

            lock (LockOf(place))
            {
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

                    long into = SameStack(connection, transaction,
                        $"SELECT Uid, Quantity FROM {place.Table} WHERE {place.OwnerColumn} = $o AND Gid = $gid " +
                        "AND IFNULL(Effects, '') = $e ORDER BY Uid LIMIT 1;", place.Owner, gid, effects, out int there);

                    bool fresh = into == 0;
                    using (var write = connection.CreateCommand())
                    {
                        write.Transaction = transaction;
                        if (fresh)
                        {
                            into = DatabaseManager.NextItemUid();
                            write.CommandText = $"INSERT INTO {place.Table} (Uid, {place.OwnerColumn}, Gid, Quantity, Effects) " +
                                                "VALUES ($uid, $o, $gid, $n, $e);";
                            write.Parameters.AddWithValue("$gid", gid);
                            write.Parameters.AddWithValue("$e", effects);
                        }
                        else
                        {
                            write.CommandText = $"UPDATE {place.Table} SET Quantity = Quantity + $n " +
                                                $"WHERE Uid = $uid AND {place.OwnerColumn} = $o;";
                        }
                        write.Parameters.AddWithValue("$uid", into);
                        write.Parameters.AddWithValue("$o", place.Owner);
                        write.Parameters.AddWithValue("$n", moving);
                        write.ExecuteNonQuery();
                    }

                    transaction.Commit();

                    return new Deposit
                    {
                        InStorage = new HavenBagStore.StoredItem
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
                    Console.WriteLine($"[Storage] Could not put {uid} in {place}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// From the storage to the bag: <paramref name="quantity"/> units of the storage's stack
        /// <paramref name="uid"/>, or the whole stack if it has fewer. Null when nothing moved.
        /// </summary>
        public static Withdrawal? Take(Place place, long characterId, long uid, int quantity)
        {
            if (uid == 0 || quantity <= 0) return null;
            EnsureTables();

            lock (LockOf(place))
            {
                try
                {
                    using var connection = Open();

                    int gid, have;
                    string effects;
                    using (var read = connection.CreateCommand())
                    {
                        read.CommandText = $"SELECT Gid, Quantity, Effects FROM {place.Table} " +
                                           $"WHERE Uid = $uid AND {place.OwnerColumn} = $o;";
                        read.Parameters.AddWithValue("$uid", uid);
                        read.Parameters.AddWithValue("$o", place.Owner);
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
                            ? $"DELETE FROM {place.Table} WHERE Uid = $uid AND {place.OwnerColumn} = $o;"
                            : $"UPDATE {place.Table} SET Quantity = Quantity - $n WHERE Uid = $uid AND {place.OwnerColumn} = $o;";
                        take.Parameters.AddWithValue("$uid", uid);
                        take.Parameters.AddWithValue("$o", place.Owner);
                        if (moving < have) take.Parameters.AddWithValue("$n", moving);
                        if (take.ExecuteNonQuery() != 1) return null;
                    }

                    long into = SameStack(connection, transaction,
                        "SELECT Uid, Quantity FROM CharacterItems WHERE CharacterId = $o AND Gid = $gid " +
                        "AND Position = " + Equipment.Bag + " AND IFNULL(Effects, '') = $e ORDER BY Uid LIMIT 1;",
                        characterId, gid, effects, out int there);

                    bool merged = into != 0;
                    using (var write = connection.CreateCommand())
                    {
                        write.Transaction = transaction;
                        if (!merged)
                        {
                            into = DatabaseManager.NextItemUid();
                            write.CommandText = "INSERT INTO CharacterItems (CharacterId, Uid, Gid, Quantity, Position, Effects) " +
                                                "VALUES ($c, $uid, $gid, $n, $pos, $e);";
                            write.Parameters.AddWithValue("$gid", gid);
                            write.Parameters.AddWithValue("$pos", Equipment.Bag);
                            write.Parameters.AddWithValue("$e", effects);
                        }
                        else
                        {
                            write.CommandText = "UPDATE CharacterItems SET Quantity = Quantity + $n WHERE Uid = $uid AND CharacterId = $c;";
                        }
                        write.Parameters.AddWithValue("$c", characterId);
                        write.Parameters.AddWithValue("$uid", into);
                        write.Parameters.AddWithValue("$n", moving);
                        write.ExecuteNonQuery();
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
                        LeftInStorage = moving >= have
                            ? null
                            : new HavenBagStore.StoredItem { Uid = uid, Gid = gid, Quantity = have - moving, Effects = effects },
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Storage] Could not take {uid} out of {place}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Empties every storage whose key starts with <paramref name="prefix"/>, in the shared
        /// table, and returns what was in them. What a house's chests hand over when it is sold.
        /// </summary>
        public static List<HavenBagStore.StoredItem> TakeAll(string prefix)
        {
            EnsureTables();
            var taken = new List<HavenBagStore.StoredItem>();
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = $"SELECT Uid, Gid, Quantity, Effects FROM {SharedTable} " +
                                       "WHERE substr(Storage, 1, length($p)) = $p ORDER BY Uid;";
                    read.Parameters.AddWithValue("$p", prefix);
                    using var reader = read.ExecuteReader();
                    while (reader.Read()) taken.Add(Read(reader));
                }
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {SharedTable} WHERE substr(Storage, 1, length($p)) = $p;";
                    delete.Parameters.AddWithValue("$p", prefix);
                    delete.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Could not empty {prefix}*: {ex.Message}");
                taken.Clear();
            }
            return taken;
        }

        /// <summary>The stack an arrival joins: its uid and how many it has, or zero.</summary>
        private static long SameStack(SqliteConnection connection, SqliteTransaction transaction, string sql,
                                      object owner, int gid, string effects, out int quantity)
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
    }
}
