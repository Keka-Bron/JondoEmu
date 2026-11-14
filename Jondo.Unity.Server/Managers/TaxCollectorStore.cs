using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The alliances' tax collectors and what each one carries, stored in world.db.
    /// </summary>
    /// <remarks>
    /// A tax collector belongs to an ALLIANCE since the alliance rework: the potions that call one
    /// ask for the alliance right 10 («Poner un recaudador», criterion Ox=10), its map actor carries
    /// the alliance block, and the alliance's sheet (mcx) lists them in its f1. One stands on a map
    /// at most: the client keys its tax collectors by their map (the lgl's f1 is the key the kdc's
    /// handler stores the view under), and «ui.social.alreadyTaxCollectorOnMap» is one of the
    /// placement's refusals.
    /// </remarks>
    public static class TaxCollectorStore
    {
        /// <summary>A tax collector standing on a map.</summary>
        public sealed class TaxCollector
        {
            public long Id { get; init; }

            /// <summary>What the client knows it by: the actor's f3, the lea's f12, the kbu and the hlc.</summary>
            public string Uuid { get; init; } = "";

            public long AllianceId { get; init; }

            /// <summary>Who placed it, the lea's f1 and f6 and the actor's f4.</summary>
            public long OwnerId { get; init; }
            public string OwnerName { get; init; } = "";

            public long MapId { get; init; }
            public int SubAreaId { get; init; }
            public int CellId { get; init; }
            public int Direction { get; init; }

            /// <summary>Its names, ids of the client's TaxCollectorFirstnames and TaxCollectorNames.</summary>
            public int FirstNameId { get; init; }
            public int LastNameId { get; init; }

            /// <summary>When it was placed, in Unix seconds: the lea's f8.</summary>
            public long PlacedUtcS { get; init; }

            /// <summary>
            /// Its id among the map's actors. Negative like every actor that is not a person, and
            /// in a range of its own so that it never meets an NPC's (-20000 down) or a group's.
            /// </summary>
            public long ContextId => ContextBase - Id;
        }

        /// <summary>Where the tax collectors' actor ids start, going down.</summary>
        public const long ContextBase = -40000;

        public static void EnsureTables(SqliteConnection world)
        {
            var create = world.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS TaxCollectors (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Uuid TEXT NOT NULL,
                    AllianceId INTEGER NOT NULL,
                    OwnerId INTEGER NOT NULL,
                    OwnerName TEXT NOT NULL DEFAULT '',
                    MapId INTEGER NOT NULL,
                    SubAreaId INTEGER NOT NULL DEFAULT 0,
                    CellId INTEGER NOT NULL DEFAULT 0,
                    Direction INTEGER NOT NULL DEFAULT 1,
                    FirstNameId INTEGER NOT NULL DEFAULT 0,
                    LastNameId INTEGER NOT NULL DEFAULT 0,
                    PlacedUtcS INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IdxTaxCollectorsMap ON TaxCollectors (MapId);
                CREATE UNIQUE INDEX IF NOT EXISTS IdxTaxCollectorsUuid ON TaxCollectors (Uuid);
                CREATE TABLE IF NOT EXISTS TaxCollectorItems (
                    TaxCollectorId INTEGER NOT NULL,
                    ItemId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (TaxCollectorId, ItemId)
                );
                CREATE TABLE IF NOT EXISTS TaxCollectorSpells (
                    TaxCollectorId INTEGER NOT NULL,
                    Slot INTEGER NOT NULL,
                    SpellLevelId INTEGER NOT NULL,
                    PRIMARY KEY (TaxCollectorId, Slot)
                );
                CREATE TABLE IF NOT EXISTS AllianceTaxCollectorSpells (
                    AllianceId INTEGER NOT NULL,
                    Slot INTEGER NOT NULL,
                    SpellLevelId INTEGER NOT NULL,
                    PRIMARY KEY (AllianceId, Slot)
                );
                CREATE TABLE IF NOT EXISTS TaxCollectorAbsences (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OwnerId INTEGER NOT NULL,
                    Kind INTEGER NOT NULL,
                    FirstNameId INTEGER NOT NULL DEFAULT 0,
                    LastNameId INTEGER NOT NULL DEFAULT 0,
                    MapId INTEGER NOT NULL DEFAULT 0,
                    SubAreaId INTEGER NOT NULL DEFAULT 0,
                    WhoId INTEGER NOT NULL DEFAULT 0,
                    WhoName TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS IdxTaxCollectorAbsencesOwner ON TaxCollectorAbsences (OwnerId);";
            create.ExecuteNonQuery();
        }

        /// <summary>A connection string a test can point at a temp database; null uses world.db.</summary>
        internal static string ConnectionStringOverride
        {
            get => _override;
            set { _override = value; _byMap = null; }
        }
        private static string _override;

        private static SqliteConnection Open()
        {
            var conexion = new SqliteConnection(_override ?? DatabaseManager.WorldConnectionString);
            conexion.Open();
            EnsureTables(conexion);
            return conexion;
        }

        /// <summary>
        /// Every tax collector by its map, read once: a map's load asks for its tax collector, and
        /// that is every map change of every player.
        /// </summary>
        private static ConcurrentDictionary<long, TaxCollector> _byMap;
        private static readonly object _loading = new();

        private static ConcurrentDictionary<long, TaxCollector> ByMap
        {
            get
            {
                var loaded = _byMap;
                if (loaded != null) return loaded;
                lock (_loading)
                {
                    if (_byMap != null) return _byMap;
                    var all = new ConcurrentDictionary<long, TaxCollector>();
                    using var conexion = Open();
                    var select = conexion.CreateCommand();
                    select.CommandText = $"SELECT {Columns} FROM TaxCollectors;";
                    using var lector = select.ExecuteReader();
                    while (lector.Read())
                    {
                        var tc = Read(lector);
                        all[tc.MapId] = tc;
                    }
                    _byMap = all;
                    return all;
                }
            }
        }

        private const string Columns = @"Id, Uuid, AllianceId, OwnerId, OwnerName, MapId, SubAreaId, CellId, Direction,
                                         FirstNameId, LastNameId, PlacedUtcS";

        private static TaxCollector Read(SqliteDataReader lector) => new TaxCollector
        {
            Id = lector.GetInt64(0),
            Uuid = lector.GetString(1),
            AllianceId = lector.GetInt64(2),
            OwnerId = lector.GetInt64(3),
            OwnerName = lector.GetString(4),
            MapId = lector.GetInt64(5),
            SubAreaId = lector.GetInt32(6),
            CellId = lector.GetInt32(7),
            Direction = lector.GetInt32(8),
            FirstNameId = lector.GetInt32(9),
            LastNameId = lector.GetInt32(10),
            PlacedUtcS = lector.GetInt64(11),
        };

        /// <summary>Places a tax collector and returns it; null when its map already has one.</summary>
        public static TaxCollector Place(long allianceId, long ownerId, string ownerName, long mapId, int subAreaId,
                                         int cellId, int direction, int firstNameId, int lastNameId)
        {
            if (OnMap(mapId) != null) return null;
            using var conexion = Open();
            long placed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string uuid = Guid.NewGuid().ToString();

            var insert = conexion.CreateCommand();
            insert.CommandText = @"
                INSERT INTO TaxCollectors (Uuid, AllianceId, OwnerId, OwnerName, MapId, SubAreaId, CellId, Direction,
                                           FirstNameId, LastNameId, PlacedUtcS)
                VALUES ($uuid, $alliance, $owner, $ownerName, $map, $subarea, $cell, $dir, $first, $last, $placed);
                SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$uuid", uuid);
            insert.Parameters.AddWithValue("$alliance", allianceId);
            insert.Parameters.AddWithValue("$owner", ownerId);
            insert.Parameters.AddWithValue("$ownerName", ownerName ?? "");
            insert.Parameters.AddWithValue("$map", mapId);
            insert.Parameters.AddWithValue("$subarea", subAreaId);
            insert.Parameters.AddWithValue("$cell", cellId);
            insert.Parameters.AddWithValue("$dir", direction);
            insert.Parameters.AddWithValue("$first", firstNameId);
            insert.Parameters.AddWithValue("$last", lastNameId);
            insert.Parameters.AddWithValue("$placed", placed);
            long id;
            try { id = Convert.ToInt64(insert.ExecuteScalar()); }
            catch (SqliteException) { return null; }

            var tc = new TaxCollector
            {
                Id = id, Uuid = uuid, AllianceId = allianceId, OwnerId = ownerId, OwnerName = ownerName ?? "",
                MapId = mapId, SubAreaId = subAreaId, CellId = cellId, Direction = direction,
                FirstNameId = firstNameId, LastNameId = lastNameId, PlacedUtcS = placed,
            };
            ByMap[mapId] = tc;
            return tc;
        }

        /// <summary>The tax collector standing on a map, or null.</summary>
        public static TaxCollector OnMap(long mapId) => ByMap.TryGetValue(mapId, out var tc) ? tc : null;

        public static TaxCollector ByUuid(string uuid)
            => string.IsNullOrEmpty(uuid) ? null : ByMap.Values.FirstOrDefault(t => t.Uuid == uuid);

        /// <summary>The tax collector whose actor id this is, or null.</summary>
        public static TaxCollector ByContextId(long contextId)
            => ByMap.Values.FirstOrDefault(t => t.ContextId == contextId);

        /// <summary>
        /// The tax collectors a fight on this map gathers for: those of its territory (subarea),
        /// split by the dungeon. The client's guide (1149321): «pueden acumular recursos en cada
        /// combate efectuado en el territorio en el que se encuentren. Si se coloca a un recaudador
        /// en una mazmorra, solo se contarán los combates dentro de esa mazmorra, mientras que, si
        /// está en el exterior, solo se contarán los combates fuera de la mazmorra».
        /// </summary>
        public static List<TaxCollector> GatheringFor(long fightMapId, int subAreaId)
        {
            var dungeon = DungeonManager.OfRoom(fightMapId);
            return ByMap.Values
                .Where(t => dungeon != null
                    ? DungeonManager.OfRoom(t.MapId)?.Id == dungeon.Id
                    : t.SubAreaId == subAreaId && DungeonManager.OfRoom(t.MapId) == null)
                .OrderBy(t => t.Id)
                .ToList();
        }

        /// <summary>Every tax collector of the server, oldest first.</summary>
        public static List<TaxCollector> All() => ByMap.Values.OrderBy(t => t.Id).ToList();

        /// <summary>An alliance's tax collectors, oldest first.</summary>
        public static List<TaxCollector> OfAlliance(long allianceId)
            => ByMap.Values.Where(t => t.AllianceId == allianceId).OrderBy(t => t.Id).ToList();

        /// <summary>
        /// Takes a tax collector off its map, with everything it carried and its spells. Its
        /// equipment is the caller's to send somewhere first (<see cref="EquipmentOf"/>).
        /// </summary>
        public static void Remove(TaxCollector tc)
        {
            if (tc == null) return;
            lock (_takeLock) Delete(tc);
        }

        private static readonly object _takeLock = new object();

        /// <summary>
        /// Takes a tax collector off its map and hands over what it carried, once: false, and
        /// nothing, when somebody else took it first. Two members collecting the same one at the
        /// same moment both read its bag before either removed it, and both were given all of it.
        /// </summary>
        public static bool TryTake(TaxCollector tc, out List<(int ItemId, int Quantity)> items)
        {
            items = new List<(int, int)>();
            if (tc == null) return false;
            lock (_takeLock)
            {
                if (!ByMap.TryGetValue(tc.MapId, out var standing) || standing.Id != tc.Id) return false;
                items = Items(tc);
                Delete(tc);
                return true;
            }
        }

        private static void Delete(TaxCollector tc)
        {
            using var conexion = Open();
            var delete = conexion.CreateCommand();
            delete.CommandText = @"DELETE FROM TaxCollectors WHERE Id = $id;
                                   DELETE FROM TaxCollectorItems WHERE TaxCollectorId = $id;
                                   DELETE FROM TaxCollectorSpells WHERE TaxCollectorId = $id;";
            delete.Parameters.AddWithValue("$id", tc.Id);
            delete.ExecuteNonQuery();
            ByMap.TryRemove(tc.MapId, out _);
        }

        /// <summary>Adds to what a tax collector carries.</summary>
        public static void AddItem(TaxCollector tc, int itemId, int quantity)
        {
            if (tc == null || quantity <= 0) return;
            using var conexion = Open();
            var upsert = conexion.CreateCommand();
            upsert.CommandText = @"
                INSERT INTO TaxCollectorItems (TaxCollectorId, ItemId, Quantity) VALUES ($tc, $item, $qty)
                ON CONFLICT (TaxCollectorId, ItemId) DO UPDATE SET Quantity = Quantity + $qty;";
            upsert.Parameters.AddWithValue("$tc", tc.Id);
            upsert.Parameters.AddWithValue("$item", itemId);
            upsert.Parameters.AddWithValue("$qty", quantity);
            upsert.ExecuteNonQuery();
        }

        /// <summary>What a tax collector carries, by item, in the order it was gathered.</summary>
        public static List<(int ItemId, int Quantity)> Items(TaxCollector tc)
        {
            var items = new List<(int, int)>();
            if (tc == null) return items;
            using var conexion = Open();
            var select = conexion.CreateCommand();
            select.CommandText = "SELECT ItemId, Quantity FROM TaxCollectorItems WHERE TaxCollectorId = $tc AND Quantity > 0 ORDER BY rowid;";
            select.Parameters.AddWithValue("$tc", tc.Id);
            using var lector = select.ExecuteReader();
            while (lector.Read()) items.Add((lector.GetInt32(0), lector.GetInt32(1)));
            return items;
        }
    
        // ─── Spells ─────────────────────────────────────────────────────────────

        /// <summary>
        /// How many spells a tax collector fights with: the six slots of the equipment window's
        /// gd_activeSpells (TaxCollectorEquipment.UpdateActiveSpells builds a list of six).
        /// </summary>
        public const int SpellSlots = 6;

        /// <summary>A tax collector's spells, as SpellLevels ids by slot.</summary>
        public static SortedDictionary<int, int> SpellsOf(TaxCollector tc)
            => tc == null ? new SortedDictionary<int, int>() : ReadSlots("TaxCollectorSpells", "TaxCollectorId", tc.Id);

        /// <summary>An alliance's default spells, the ones each new tax collector starts with.</summary>
        public static SortedDictionary<int, int> PresetOf(long allianceId)
            => ReadSlots("AllianceTaxCollectorSpells", "AllianceId", allianceId);

        public static void SetSpells(TaxCollector tc, IReadOnlyDictionary<int, int> spells)
        {
            if (tc != null) WriteSlots("TaxCollectorSpells", "TaxCollectorId", tc.Id, spells);
        }

        public static void SetPreset(long allianceId, IReadOnlyDictionary<int, int> spells)
            => WriteSlots("AllianceTaxCollectorSpells", "AllianceId", allianceId, spells);

        private static SortedDictionary<int, int> ReadSlots(string table, string ownerColumn, long owner)
        {
            var slots = new SortedDictionary<int, int>();
            using var conexion = Open();
            var select = conexion.CreateCommand();
            select.CommandText = $"SELECT Slot, SpellLevelId FROM {table} WHERE {ownerColumn} = $o ORDER BY Slot;";
            select.Parameters.AddWithValue("$o", owner);
            using var lector = select.ExecuteReader();
            while (lector.Read()) slots[lector.GetInt32(0)] = lector.GetInt32(1);
            return slots;
        }

        private static void WriteSlots(string table, string ownerColumn, long owner, IReadOnlyDictionary<int, int> spells)
        {
            using var conexion = Open();
            using var transaction = conexion.BeginTransaction();
            var delete = conexion.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table} WHERE {ownerColumn} = $o;";
            delete.Parameters.AddWithValue("$o", owner);
            delete.ExecuteNonQuery();
            foreach (var (slot, spellLevel) in spells)
            {
                if (slot < 0 || slot >= SpellSlots || spellLevel <= 0) continue;
                var insert = conexion.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO {table} ({ownerColumn}, Slot, SpellLevelId) VALUES ($o, $s, $l);";
                insert.Parameters.AddWithValue("$o", owner);
                insert.Parameters.AddWithValue("$s", slot);
                insert.Parameters.AddWithValue("$l", spellLevel);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        // ─── Equipment ──────────────────────────────────────────────────────────

        /// <summary>Where a tax collector's equipment is kept: a storage of its own.</summary>
        public static StorageStacks.Place EquipmentPlace(TaxCollector tc)
            => new StorageStacks.Place(StorageStacks.SharedTable, "Storage", EquipmentPrefix(tc) + "equipment");

        /// <summary>What every storage of a tax collector starts with, to empty them all when it goes.</summary>
        public static string EquipmentPrefix(TaxCollector tc) => $"taxcollector:{tc.Id}:";

        /// <summary>What a tax collector wears.</summary>
        public static List<HavenBagStore.StoredItem> EquipmentOf(TaxCollector tc)
            => tc == null ? new List<HavenBagStore.StoredItem>() : StorageStacks.ItemsOf(EquipmentPlace(tc));

        // ─── While its owner was away ───────────────────────────────────────────

        /// <summary>
        /// What happened to one of a character's tax collectors while he was not connected, told
        /// at his next entry (hkx): collected by somebody (2) or beaten (3), as fgv.bexb words them.
        /// </summary>
        public sealed record Absence(int Kind, int FirstNameId, int LastNameId, long MapId, int SubAreaId,
                                     long WhoId, string WhoName);

        public const int AbsenceCollected = 2;
        public const int AbsenceDefeated = 3;

        public static void RecordAbsence(long ownerId, Absence absence)
        {
            using var conexion = Open();
            var insert = conexion.CreateCommand();
            insert.CommandText = @"
                INSERT INTO TaxCollectorAbsences (OwnerId, Kind, FirstNameId, LastNameId, MapId, SubAreaId, WhoId, WhoName)
                VALUES ($owner, $kind, $first, $last, $map, $subarea, $who, $whoName);";
            insert.Parameters.AddWithValue("$owner", ownerId);
            insert.Parameters.AddWithValue("$kind", absence.Kind);
            insert.Parameters.AddWithValue("$first", absence.FirstNameId);
            insert.Parameters.AddWithValue("$last", absence.LastNameId);
            insert.Parameters.AddWithValue("$map", absence.MapId);
            insert.Parameters.AddWithValue("$subarea", absence.SubAreaId);
            insert.Parameters.AddWithValue("$who", absence.WhoId);
            insert.Parameters.AddWithValue("$whoName", absence.WhoName ?? "");
            insert.ExecuteNonQuery();
        }

        /// <summary>What happened while he was away, and forgotten once read.</summary>
        public static List<Absence> TakeAbsences(long ownerId)
        {
            var absences = new List<Absence>();
            using var conexion = Open();
            var select = conexion.CreateCommand();
            select.CommandText = @"SELECT Kind, FirstNameId, LastNameId, MapId, SubAreaId, WhoId, WhoName
                                   FROM TaxCollectorAbsences WHERE OwnerId = $owner ORDER BY Id;";
            select.Parameters.AddWithValue("$owner", ownerId);
            using (var lector = select.ExecuteReader())
            {
                while (lector.Read())
                {
                    absences.Add(new Absence(lector.GetInt32(0), lector.GetInt32(1), lector.GetInt32(2),
                                             lector.GetInt64(3), lector.GetInt32(4), lector.GetInt64(5), lector.GetString(6)));
                }
            }
            if (absences.Count > 0)
            {
                var delete = conexion.CreateCommand();
                delete.CommandText = "DELETE FROM TaxCollectorAbsences WHERE OwnerId = $owner;";
                delete.Parameters.AddWithValue("$owner", ownerId);
                delete.ExecuteNonQuery();
            }
            return absences;
        }
    }
}
