using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.World.Maps;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    public sealed class HouseTemplate
    {
        public int TypeId { get; init; }
        public long DefaultPrice { get; init; }
        public int NameId { get; init; }
        public int DescriptionId { get; init; }
        public int GfxId { get; init; }
        public int RoomCount { get; init; }
    }

    /// <summary>A persistent, server-owned dwelling attached to one exterior door.</summary>
    public sealed class HouseDefinition
    {
        public int Id { get; init; }
        public int HouseTypeId { get; init; }
        public int NameId { get; init; }
        public int DescriptionId { get; init; }
        public int TemplateGfxId { get; init; }
        public int RoomCount { get; init; }
        public long MapId { get; init; }
        public int CellId { get; init; }
        public int ExteriorElementId { get; init; }
        public int ExteriorGfxId { get; init; }
        public long InteriorMapId { get; init; }
        public long OwnerAccountId { get; init; }
        public long Price { get; init; }
        public bool Locked { get; init; }
        public bool VisitAllowed { get; init; }
        public bool Listed { get; init; }
        public bool GuildHouse { get; init; }
        public string AccessCodeHash { get; init; } = "";
        public string ExtraJson { get; init; } = "{}";

        public bool IsOwned => OwnerAccountId > 0;
        public bool IsForSale => !IsOwned || Listed;
    }

    public enum HousePurchaseResult
    {
        Success,
        UnknownHouse,
        InvalidSession,
        AlreadyOwned,
        NotForSale,
        InsufficientKamas,
        SecondHandUnsupported,
        OfferChanged,
        ConcurrentChange,
    }

    /// <summary>
    /// Owns the mutable house catalog. The Unity client only supplies static house types; the
    /// door-to-instance relation, price, owner and access policy live here and survive restarts.
    /// </summary>
    public static class HouseManager
    {
        // There is no official placement -> template join in the client. New emulator-owned
        // instances therefore use the deterministic lowest positive-price official template so
        // lpx.f4 is always a model the client can resolve. An admin can assign the precise type
        // later without losing the stable instance id or owner; non-zero assignments are kept.
        private const long FallbackPrice = 1_000_000;

        private static readonly object Gate = new object();
        private static IReadOnlyDictionary<int, HouseDefinition> _byId =
            new Dictionary<int, HouseDefinition>();
        private static IReadOnlyDictionary<(long MapId, int ElementId), HouseDefinition> _byDoor =
            new Dictionary<(long, int), HouseDefinition>();
        private static IReadOnlyDictionary<int, HouseTemplate> _templates =
            new Dictionary<int, HouseTemplate>();

        public static int Count => _byId.Count;
        public static int TemplateCount => _templates.Count;
        public static IEnumerable<HouseDefinition> All => _byId.Values;
        public static IEnumerable<HouseDefinition> OnMap(long mapId)
        {
            foreach (var house in _byId.Values)
                if (house.MapId == mapId) yield return house;
        }

        public static void Initialize()
        {
            HouseTemplate fallbackTemplate = ImportOfficialTemplates();
            MaterializeWorldDoors(fallbackTemplate);
            LoadFromDatabase();
            Console.WriteLine($"[Houses] {_byId.Count} persistent instances, " +
                              $"{_templates.Count} official client templates.");
        }

        public static bool TryGet(int id, out HouseDefinition? house)
            => _byId.TryGetValue(id, out house!);

        public static bool TryGetTemplate(int typeId, out HouseTemplate? template)
            => _templates.TryGetValue(typeId, out template!);

        public static bool TryGetByDoor(long mapId, int elementId, out HouseDefinition? house)
            => _byDoor.TryGetValue((mapId, elementId), out house!);

        public static bool TryResolveDoor(long mapId, int elementId, int requestedHouseId,
                                          out HouseDefinition? house)
        {
            if (!TryGetByDoor(mapId, elementId, out house) || house == null) return false;
            // Proto3 omits a zero. A non-zero dwelling number is authoritative and may not name
            // another house behind the same or a forged door.
            return requestedHouseId == 0 || requestedHouseId == house.Id;
        }

        public static bool CanEnter(HouseDefinition house, long accountId)
        {
            if (!house.IsOwned) return true;
            if (accountId > 0 && house.OwnerAccountId == accountId) return true;
            return house.VisitAllowed && !house.Locked && string.IsNullOrEmpty(house.AccessCodeHash);
        }

        public static bool IsDoorActionVisible(long mapId, int elementId, int skillId, long accountId)
        {
            if (!TryGetByDoor(mapId, elementId, out var house) || house == null) return false;
            if (skillId == Houses.EnterSkill) return CanEnter(house, accountId);
            if (skillId == Houses.BuySkill)
                return CanPurchaseFirstHand(house, accountId);
            return false;
        }

        /// <summary>
        /// The first-hand transfer is fully evidenced and atomic.  A listed owned house is not
        /// buyable yet: the current captures do not show whether its proceeds go to a character,
        /// account bank or another seller balance, so replacing its owner would destroy value.
        /// </summary>
        public static bool CanPurchaseFirstHand(HouseDefinition house, long accountId)
            => accountId > 0 && !house.IsOwned && house.Price > 0;

        /// <summary>
        /// A roleplay interaction may originate on the element cell or any of its eight adjacent
        /// cells.  In the isometric coordinate system those are exactly the cells whose X and Y
        /// deltas are both at most one; this rejects forged same-map clicks from farther away.
        /// </summary>
        public static bool IsWithinInteractionRange(int characterCellId, int elementCellId)
        {
            if (!MapGeometry.IsValid(characterCellId) || !MapGeometry.IsValid(elementCellId))
                return false;

            var character = MapGeometry.CellToPoint(characterCellId);
            var element = MapGeometry.CellToPoint(elementCellId);
            return Math.Abs(character.X - element.X) <= 1 &&
                   Math.Abs(character.Y - element.Y) <= 1;
        }

        /// <summary>
        /// Atomically transfers a first-hand house and deducts the character's kamas. Both rows
        /// are updated in one SQLite transaction so two simultaneous buyers cannot both succeed.
        /// Owned listings are rejected until the seller-credit destination is evidenced.
        /// </summary>
        public static HousePurchaseResult TryPurchase(int houseId, long mapId, int elementId,
                                                       long accountId, long characterId,
                                                       long expectedOwnerAccountId,
                                                       bool expectedListed,
                                                       long expectedPrice,
                                                       out long paid,
                                                       out long remainingKamas)
        {
            paid = 0;
            remainingKamas = 0;
            if (accountId <= 0 || characterId <= 0) return HousePurchaseResult.InvalidSession;

            lock (Gate)
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                long owner;
                long price;
                bool listed;
                using (var query = connection.CreateCommand())
                {
                    query.Transaction = transaction;
                    query.CommandText = @"
                        SELECT OwnerAccountId, Price, Listed
                        FROM Houses
                        WHERE Id=$id AND MapId=$map AND ExteriorElementId=$element;";
                    query.Parameters.AddWithValue("$id", houseId);
                    query.Parameters.AddWithValue("$map", mapId);
                    query.Parameters.AddWithValue("$element", elementId);
                    using var reader = query.ExecuteReader();
                    if (!reader.Read()) return HousePurchaseResult.UnknownHouse;
                    owner = reader.GetInt64(0);
                    price = Math.Max(0, reader.GetInt64(1));
                    listed = reader.GetInt32(2) != 0;
                }

                if (owner == accountId) return HousePurchaseResult.AlreadyOwned;
                if (owner != expectedOwnerAccountId || listed != expectedListed ||
                    price != expectedPrice)
                    return HousePurchaseResult.OfferChanged;
                // Do not silently overwrite an owner.  The client proves second-hand listings
                // exist, but not where the seller must be credited.  Reject inside the same
                // transaction until that economic side is captured and implemented.
                if (owner > 0) return HousePurchaseResult.SecondHandUnsupported;
                if (price <= 0) return HousePurchaseResult.NotForSale;

                using (var debit = connection.CreateCommand())
                {
                    debit.Transaction = transaction;
                    debit.CommandText = @"
                        UPDATE Characters SET Kamas=Kamas-$price
                        WHERE Id=$character AND AccountId=$account AND Kamas >= $price
                        RETURNING Kamas;";
                    debit.Parameters.AddWithValue("$price", price);
                    debit.Parameters.AddWithValue("$character", characterId);
                    debit.Parameters.AddWithValue("$account", accountId);
                    object? balance = debit.ExecuteScalar();
                    if (balance == null || balance == DBNull.Value)
                        return HousePurchaseResult.InsufficientKamas;
                    remainingKamas = Convert.ToInt64(balance);
                }

                using (var claim = connection.CreateCommand())
                {
                    claim.Transaction = transaction;
                    claim.CommandText = @"
                        UPDATE Houses
                        SET OwnerAccountId=$account, Listed=0, Locked=0, VisitAllowed=1,
                            UpdatedUtc=$now
                        WHERE Id=$id AND MapId=$map AND ExteriorElementId=$element
                          AND OwnerAccountId=$expectedOwner AND Listed=$expectedListed
                          AND Price=$expectedPrice;";
                    claim.Parameters.AddWithValue("$account", accountId);
                    claim.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                    claim.Parameters.AddWithValue("$id", houseId);
                    claim.Parameters.AddWithValue("$map", mapId);
                    claim.Parameters.AddWithValue("$element", elementId);
                    claim.Parameters.AddWithValue("$expectedOwner", owner);
                    claim.Parameters.AddWithValue("$expectedListed", listed ? 1 : 0);
                    claim.Parameters.AddWithValue("$expectedPrice", price);
                    if (claim.ExecuteNonQuery() != 1) return HousePurchaseResult.ConcurrentChange;
                }

                transaction.Commit();
                paid = price;
                LoadFromDatabase();
                return HousePurchaseResult.Success;
            }
        }

        private static HouseTemplate ImportOfficialTemplates()
        {
            string path = Paths.HouseTemplatesJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Houses] Missing {path}; run tools/extraer_casas.py.");
                return new HouseTemplate { TypeId = 215, DefaultPrice = FallbackPrice };
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            string version = document.RootElement.TryGetProperty("clientVersion", out var v)
                ? v.GetString() ?? "3.6.10.10"
                : "3.6.10.10";
            string source = document.RootElement.TryGetProperty("source", out var s)
                ? s.GetString() ?? "HousesDataRoot"
                : "HousesDataRoot";
            if (!document.RootElement.TryGetProperty("houses", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("house template catalog has no houses array");

            HouseTemplate? fallback = null;
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO HouseTemplates
                    (TypeId, DefaultPrice, NameId, DescriptionId, GfxId, RoomCount, ClientVersion, Source)
                VALUES ($type, $price, $name, $description, $gfx, $rooms, $version, $source)
                ON CONFLICT(TypeId) DO UPDATE SET
                    DefaultPrice=excluded.DefaultPrice, NameId=excluded.NameId,
                    DescriptionId=excluded.DescriptionId, GfxId=excluded.GfxId,
                    RoomCount=excluded.RoomCount, ClientVersion=excluded.ClientVersion,
                    Source=excluded.Source;";
            foreach (string name in new[]
                     { "$type", "$price", "$name", "$description", "$gfx", "$rooms", "$version", "$source" })
                command.Parameters.Add(new SqliteParameter(name, SqliteType.Integer));
            command.Parameters["$version"].SqliteType = SqliteType.Text;
            command.Parameters["$source"].SqliteType = SqliteType.Text;

            foreach (var row in rows.EnumerateArray())
            {
                int typeId = row.GetProperty("typeId").GetInt32();
                long price = row.GetProperty("defaultPrice").GetInt64();
                if (typeId <= 0 || price < 0) continue;
                command.Parameters["$type"].Value = typeId;
                command.Parameters["$price"].Value = price;
                command.Parameters["$name"].Value = row.GetProperty("nameId").GetInt32();
                command.Parameters["$description"].Value = row.GetProperty("descriptionId").GetInt32();
                command.Parameters["$gfx"].Value = row.GetProperty("gfxId").GetInt32();
                command.Parameters["$rooms"].Value = row.GetProperty("roomCount").GetInt32();
                command.Parameters["$version"].Value = version;
                command.Parameters["$source"].Value = source;
                command.ExecuteNonQuery();
                var template = new HouseTemplate
                {
                    TypeId = typeId,
                    DefaultPrice = price,
                    NameId = row.GetProperty("nameId").GetInt32(),
                    DescriptionId = row.GetProperty("descriptionId").GetInt32(),
                    GfxId = row.GetProperty("gfxId").GetInt32(),
                    RoomCount = row.GetProperty("roomCount").GetInt32(),
                };
                if (price > 0 && (fallback == null || price < fallback.DefaultPrice ||
                    (price == fallback.DefaultPrice && typeId < fallback.TypeId)))
                    fallback = template;
            }
            transaction.Commit();
            return fallback ?? throw new InvalidDataException(
                "house template catalog has no positive-price template");
        }

        private static void MaterializeWorldDoors(HouseTemplate fallback)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO Houses
                    (HouseTypeId, NameId, DescriptionId, TemplateGfxId, RoomCount,
                     MapId, CellId, ExteriorElementId, ExteriorGfxId, InteriorMapId,
                     OwnerAccountId, Price, Locked, VisitAllowed, Listed, GuildHouse,
                     AccessCodeHash, UpdatedUtc, ExtraJson)
                SELECT $type, $name, $description, $templateGfx, $rooms,
                       $map, $cell, $element, $gfx, $interior,
                       0, $price, 0, 1, 0, 0, '', $now, $extra
                WHERE NOT EXISTS (
                    SELECT 1 FROM Houses WHERE MapId=$map AND ExteriorElementId=$element
                );

                UPDATE Houses
                SET CellId=$cell, ExteriorGfxId=$gfx, InteriorMapId=$interior
                WHERE MapId=$map AND ExteriorElementId=$element;

                UPDATE Houses
                SET HouseTypeId=$type, NameId=$name, DescriptionId=$description,
                    TemplateGfxId=$templateGfx, RoomCount=$rooms,
                    UpdatedUtc=$now
                WHERE MapId=$map AND ExteriorElementId=$element AND HouseTypeId=0;";
            command.Parameters.Add("$type", SqliteType.Integer);
            command.Parameters.Add("$name", SqliteType.Integer);
            command.Parameters.Add("$description", SqliteType.Integer);
            command.Parameters.Add("$templateGfx", SqliteType.Integer);
            command.Parameters.Add("$rooms", SqliteType.Integer);
            command.Parameters.Add("$map", SqliteType.Integer);
            command.Parameters.Add("$cell", SqliteType.Integer);
            command.Parameters.Add("$element", SqliteType.Integer);
            command.Parameters.Add("$gfx", SqliteType.Integer);
            command.Parameters.Add("$interior", SqliteType.Integer);
            command.Parameters.Add("$price", SqliteType.Integer);
            command.Parameters.Add("$now", SqliteType.Text);
            command.Parameters.AddWithValue("$extra",
                "{\"placementSource\":\"server-world-catalog\"," +
                "\"templateAssignment\":\"emulator-fallback-lowest-positive-price\"}");

            int before = CountRows(connection, transaction);
            foreach (var door in Houses.All)
            {
                command.Parameters["$map"].Value = door.MapId;
                command.Parameters["$cell"].Value = door.Cell;
                command.Parameters["$element"].Value = door.ElementId;
                command.Parameters["$gfx"].Value = door.Gfx;
                command.Parameters["$interior"].Value = door.InteriorMapId;
                command.Parameters["$type"].Value = fallback.TypeId;
                command.Parameters["$name"].Value = fallback.NameId;
                command.Parameters["$description"].Value = fallback.DescriptionId;
                command.Parameters["$templateGfx"].Value = fallback.GfxId;
                command.Parameters["$rooms"].Value = fallback.RoomCount;
                command.Parameters["$price"].Value = fallback.DefaultPrice;
                command.Parameters["$now"].Value = DateTime.UtcNow.ToString("O");
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            int added = Math.Max(0, CountRows(connection, null) - before);
            if (added > 0) Console.WriteLine($"[Houses] Materialized {added} new door instance(s).");
        }

        private static int CountRows(SqliteConnection connection, SqliteTransaction? transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM Houses WHERE ExteriorElementId > 0;";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }

        private static void LoadFromDatabase()
        {
            var houses = new Dictionary<int, HouseDefinition>();
            var doors = new Dictionary<(long, int), HouseDefinition>();
            var templates = new Dictionary<int, HouseTemplate>();
            var activeDoors = new HashSet<(long MapId, int ElementId)>();
            foreach (var door in Houses.All)
                activeDoors.Add((door.MapId, door.ElementId));
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT Id, HouseTypeId, NameId, DescriptionId, TemplateGfxId, RoomCount,
                           MapId, CellId, ExteriorElementId, ExteriorGfxId, InteriorMapId,
                           OwnerAccountId, Price, Locked, VisitAllowed, Listed, GuildHouse,
                           AccessCodeHash, ExtraJson
                    FROM Houses WHERE ExteriorElementId > 0 ORDER BY Id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var house = new HouseDefinition
                    {
                        Id = reader.GetInt32(0), HouseTypeId = reader.GetInt32(1),
                        NameId = reader.GetInt32(2), DescriptionId = reader.GetInt32(3),
                        TemplateGfxId = reader.GetInt32(4), RoomCount = reader.GetInt32(5),
                        MapId = reader.GetInt64(6), CellId = reader.GetInt32(7),
                        ExteriorElementId = reader.GetInt32(8), ExteriorGfxId = reader.GetInt32(9),
                        InteriorMapId = reader.GetInt64(10), OwnerAccountId = reader.GetInt64(11),
                        Price = reader.GetInt64(12), Locked = reader.GetInt32(13) != 0,
                        VisitAllowed = reader.GetInt32(14) != 0, Listed = reader.GetInt32(15) != 0,
                        GuildHouse = reader.GetInt32(16) != 0,
                        AccessCodeHash = reader.IsDBNull(17) ? "" : reader.GetString(17),
                        ExtraJson = reader.IsDBNull(18) ? "{}" : reader.GetString(18),
                    };
                    // Keep historical/owned rows in SQLite when a client update removes an
                    // element or stronger world-graph evidence reclassifies it, but never expose
                    // a house wrapper or action for a door absent from the active pinned catalog.
                    if (!activeDoors.Contains((house.MapId, house.ExteriorElementId))) continue;
                    houses[house.Id] = house;
                    doors[(house.MapId, house.ExteriorElementId)] = house;
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT TypeId, DefaultPrice, NameId, DescriptionId, GfxId, RoomCount
                    FROM HouseTemplates ORDER BY TypeId;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var template = new HouseTemplate
                    {
                        TypeId = reader.GetInt32(0), DefaultPrice = reader.GetInt64(1),
                        NameId = reader.GetInt32(2), DescriptionId = reader.GetInt32(3),
                        GfxId = reader.GetInt32(4), RoomCount = reader.GetInt32(5),
                    };
                    templates[template.TypeId] = template;
                }
            }

            _byId = houses;
            _byDoor = doors;
            _templates = templates;
        }
    }
}
