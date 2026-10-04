using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using ItemEffect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What is on sale in the marketplaces: every lot, who sells it, and the offers buyers see.
    /// Shared by every session and every counter of a kind (see <see cref="Marketplaces"/>).
    /// </summary>
    /// <remarks>
    /// ─── Listings and offers ──────────────────────────────────────────────────────────────
    ///
    /// A LISTING is one lot one seller put on sale: an item with its effects, a lot size, a
    /// price. The seller sees it by its own id, the f1 of kes's item: 3,386,955 for the ring
    /// of the equipment capture, whose uid in the bag had been 536,837,845.
    ///
    /// An OFFER is what the buyer sees: every listing of one item with the SAME effects, and
    /// for each lot size the cheapest of them. The runes capture shows it lot by lot: offer
    /// 6473 of rune 1523 at [198, 2150, 21760, 217979], a lot of 1 bought for 198, and the kgp
    /// that follows says [199, 2150, 21760, 217979] -- the next lot of 1 was 199, under the
    /// same offer id. An item with other effects is another offer: the equipment capture's
    /// ring 853 has three, 3316, 147239 and 3315, one per roll.
    ///
    /// Offer ids are this process's own, handed out as offers appear. Nothing persists them
    /// and nothing needs to: a client only holds one between a kbt and the kbm it answers.
    ///
    /// ─── Persistence ──────────────────────────────────────────────────────────────────────
    ///
    /// MarketplaceListings and MarketplaceSales in world.db, created at start. A lot leaves the
    /// seller's inventory when it goes on sale, so its row here is the only place it exists
    /// until someone buys it, its seller takes it back or its time runs out.
    ///
    /// MarketplaceHistory is the seller's side of it: every lot of an account that was sold or
    /// that came back unsold, for the client's sales history window (see <see cref="HistoryOf"/>).
    /// MarketplaceSales stays the buyers' side, the one average prices are read from.
    /// </remarks>
    public static class MarketplaceListings
    {
        public sealed class Listing
        {
            public int Id { get; init; }
            public int House { get; init; }
            public long SellerCharacterId { get; init; }
            public long SellerAccountId { get; init; }
            public int Gid { get; init; }
            public int ItemType { get; init; }

            /// <summary>The lot size: 1, 10, 100 or 1000 of the item.</summary>
            public int Quantity { get; init; }

            /// <summary>The effects as CharacterItems stores them.</summary>
            public string Effects { get; init; } = "";

            /// <summary>The price of the whole lot.</summary>
            public long Price { get; init; }

            public DateTime ListedUtc { get; init; }
            public DateTime ExpiresUtc { get; init; }

            /// <summary>
            /// The seconds it has left on sale, rounded up: a lot just put on sale has the whole
            /// 2,419,200 of E's kes, not one less for the milliseconds since.
            /// </summary>
            public long SecondsLeft(DateTime nowUtc)
                => Math.Max(0, (long)Math.Ceiling((ExpiresUtc - nowUtc).TotalSeconds));
        }

        /// <summary>An offer as a buyer sees it at one moment: what a kbt, kgp or kfi carries.</summary>
        public sealed class OfferView
        {
            public int Id { get; init; }
            public int Gid { get; init; }
            public int ItemType { get; init; }
            public IReadOnlyList<ItemEffect> Effects { get; init; } = Array.Empty<ItemEffect>();

            /// <summary>The cheapest lot of each size, in the marketplace's lot order; 0 for none.</summary>
            public IReadOnlyList<long> Prices { get; init; } = Array.Empty<long>();

            /// <summary>No listing left: the offer is gone.</summary>
            public bool Gone { get; init; }
        }

        /// <summary>
        /// One line of a seller's sales history: a lot sold, or come back unsold, and when. What
        /// a laq of the client's las carries.
        /// </summary>
        public sealed class HistoryEntry
        {
            public int House { get; init; }
            public int Gid { get; init; }
            public int Quantity { get; init; }
            public string Effects { get; init; } = "";

            /// <summary>What the lot was on sale for: the kamas of a sale.</summary>
            public long Kamas { get; init; }

            public bool Sold { get; init; }
            public DateTime AtUtc { get; init; }
        }

        /// <summary>How far back a history goes when it is sent. INFERRED: no capture has one.</summary>
        public static readonly TimeSpan HistoryDepth = TimeSpan.FromDays(30);

        /// <summary>The most lines a history is sent with, the newest. INFERRED as well.</summary>
        public const int HistoryLimit = 1000;

        private sealed class Offer
        {
            public int Id { get; init; }
            public int House { get; init; }
            public int Gid { get; init; }
            public int ItemType { get; init; }
            public string EffectsKey { get; init; } = "";
            public IReadOnlyList<ItemEffect> Effects { get; init; } = Array.Empty<ItemEffect>();
            public List<Listing> Listings { get; } = new List<Listing>();
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, Listing> _listings = new Dictionary<int, Listing>();
        private static readonly Dictionary<(int House, int Gid, string Effects), Offer> _offers =
            new Dictionary<(int, int, string), Offer>();
        private static readonly Dictionary<int, Offer> _offersById = new Dictionary<int, Offer>();
        private static readonly Dictionary<int, int> _offerOfListing = new Dictionary<int, int>();
        private static int _lastOffer;
        private static bool _loaded;
        private static bool _schema;

        public static int Count { get { lock (Gate) { Load(); return _listings.Count; } } }

        /// <summary>Creates the tables and reads what is on sale. At start, and again for tests.</summary>
        public static void Initialize()
        {
            lock (Gate)
            {
                _loaded = false;
                Load();
            }
            Console.WriteLine($"[Marketplaces] {_listings.Count} lot(s) on sale in {_offersById.Count} offer(s).");
        }

        private static void Load()
        {
            if (_loaded) return;
            _listings.Clear();
            _offers.Clear();
            _offersById.Clear();
            _offerOfListing.Clear();
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT Id, House, SellerCharacterId, SellerAccountId, Gid, ItemType, Quantity, " +
                    "Effects, Price, ListedAt, ExpiresAt FROM MarketplaceListings ORDER BY Id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var listing = new Listing
                    {
                        Id = reader.GetInt32(0),
                        House = reader.GetInt32(1),
                        SellerCharacterId = reader.GetInt64(2),
                        SellerAccountId = reader.GetInt64(3),
                        Gid = reader.GetInt32(4),
                        ItemType = reader.GetInt32(5),
                        Quantity = reader.GetInt32(6),
                        Effects = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        Price = reader.GetInt64(8),
                        ListedUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9)).UtcDateTime,
                        ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(10)).UtcDateTime,
                    };
                    Index(listing);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] Could not read the listings: {ex.Message}");
            }
            _loaded = true;
        }

        /// <summary>
        /// The three tables. Created here and not in DatabaseManager's migration because a world.db
        /// fresh out of datos/world.zip -- the CI's -- has never met them, and whatever touches
        /// the marketplaces first must find them.
        /// </summary>
        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            if (_schema) return connection;
            _schema = true;
            using var create = connection.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS MarketplaceListings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    House INTEGER NOT NULL,
                    SellerCharacterId INTEGER NOT NULL,
                    SellerAccountId INTEGER NOT NULL,
                    Gid INTEGER NOT NULL,
                    ItemType INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL,
                    Effects TEXT,
                    Price INTEGER NOT NULL,
                    ListedAt INTEGER NOT NULL,
                    ExpiresAt INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_marketplace_listings_seller
                    ON MarketplaceListings(SellerAccountId, House);
                CREATE TABLE IF NOT EXISTS MarketplaceSales (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    House INTEGER NOT NULL,
                    Gid INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL,
                    Price INTEGER NOT NULL,
                    SellerAccountId INTEGER NOT NULL,
                    BuyerCharacterId INTEGER NOT NULL,
                    SoldAt INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_marketplace_sales_gid ON MarketplaceSales(Gid);
                CREATE TABLE IF NOT EXISTS MarketplaceHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SellerAccountId INTEGER NOT NULL,
                    House INTEGER NOT NULL,
                    Gid INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL,
                    Effects TEXT,
                    Kamas INTEGER NOT NULL,
                    Sold INTEGER NOT NULL,
                    At INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_marketplace_history_seller
                    ON MarketplaceHistory(SellerAccountId, At);";
            create.ExecuteNonQuery();
            return connection;
        }

        private static string KeyOf(string effects)
            => Forgemagic.Serialize(Equipment.ParseEffects(effects));

        private static Offer Index(Listing listing)
        {
            string key = KeyOf(listing.Effects);
            var at = (listing.House, listing.Gid, key);
            if (!_offers.TryGetValue(at, out var offer))
            {
                offer = new Offer
                {
                    Id = ++_lastOffer,
                    House = listing.House,
                    Gid = listing.Gid,
                    ItemType = listing.ItemType,
                    EffectsKey = key,
                    Effects = Equipment.ParseEffects(listing.Effects),
                };
                _offers[at] = offer;
                _offersById[offer.Id] = offer;
            }
            offer.Listings.Add(listing);
            _listings[listing.Id] = listing;
            _offerOfListing[listing.Id] = offer.Id;
            return offer;
        }

        /// <summary>Takes a listing out of memory; says what its offer looks like after.</summary>
        private static OfferView Unindex(Listing listing, IReadOnlyList<int> lots)
        {
            _listings.Remove(listing.Id);
            if (!_offerOfListing.Remove(listing.Id, out int offerId) || !_offersById.TryGetValue(offerId, out var offer))
                return new OfferView { Gid = listing.Gid, ItemType = listing.ItemType, Gone = true };
            offer.Listings.RemoveAll(l => l.Id == listing.Id);
            if (offer.Listings.Count == 0)
            {
                _offersById.Remove(offer.Id);
                _offers.Remove((offer.House, offer.Gid, offer.EffectsKey));
            }
            return View(offer, lots);
        }

        private static OfferView View(Offer offer, IReadOnlyList<int> lots)
        {
            var prices = new long[lots.Count];
            foreach (var listing in offer.Listings)
            {
                int i = IndexOf(lots, listing.Quantity);
                if (i < 0) continue;
                if (prices[i] == 0 || listing.Price < prices[i]) prices[i] = listing.Price;
            }
            return new OfferView
            {
                Id = offer.Id,
                Gid = offer.Gid,
                ItemType = offer.ItemType,
                Effects = offer.Effects,
                Prices = prices,
                Gone = offer.Listings.Count == 0,
            };
        }

        private static int IndexOf(IReadOnlyList<int> lots, int quantity)
        {
            for (int i = 0; i < lots.Count; i++) if (lots[i] == quantity) return i;
            return -1;
        }

        // ─── What a buyer sees ──────────────────────────────────────────────────────────────

        /// <summary>The items of one type with something on sale in a marketplace, by id.</summary>
        public static IReadOnlyList<int> ItemsOf(int house, int itemType)
        {
            lock (Gate)
            {
                Load();
                return _offersById.Values
                    .Where(o => o.House == house && o.ItemType == itemType)
                    .Select(o => o.Gid).Distinct().OrderBy(g => g).ToList();
            }
        }

        /// <summary>Every offer of one item in a marketplace, oldest first.</summary>
        public static IReadOnlyList<OfferView> OffersOf(Marketplaces.House house, int gid)
        {
            lock (Gate)
            {
                Load();
                return _offersById.Values
                    .Where(o => o.House == house.Id && o.Gid == gid)
                    .OrderBy(o => o.Id)
                    .Select(o => View(o, house.Lots)).ToList();
            }
        }

        /// <summary>
        /// The lowest price of each lot size of an item, over every offer of it: kcq's f5. The
        /// equipment capture asks for ring 853 while its offers are 2599 and 2000 for one, and
        /// gets [2000, 0, 0, 0].
        /// </summary>
        public static IReadOnlyList<long> LowestPrices(Marketplaces.House house, int gid)
        {
            var lowest = new long[house.Lots.Count];
            foreach (var offer in OffersOf(house, gid))
            {
                for (int i = 0; i < lowest.Length; i++)
                {
                    long p = offer.Prices[i];
                    if (p > 0 && (lowest[i] == 0 || p < lowest[i])) lowest[i] = p;
                }
            }
            return lowest;
        }

        /// <summary>
        /// The average price of one of an item, over every sale recorded: kcq's f4. INFERRED: the
        /// capture's 377 is the real server's own statistic, and how it reckons it is not on the
        /// wire. Zero, and left out of kcq, when the item has never sold here.
        /// </summary>
        public static long AveragePrice(int gid)
        {
            try
            {
                lock (Gate)
                {
                    using var connection = Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT SUM(Price), SUM(Quantity) FROM MarketplaceSales WHERE Gid = $g;";
                    command.Parameters.AddWithValue("$g", gid);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1)) return 0;
                    long price = reader.GetInt64(0), quantity = reader.GetInt64(1);
                    return quantity > 0 ? price / quantity : 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] Could not read the sales of {gid}: {ex.Message}");
                return 0;
            }
        }

        // ─── Selling ────────────────────────────────────────────────────────────────────────

        /// <summary>The lots an account has on sale in a marketplace, oldest first.</summary>
        public static IReadOnlyList<Listing> OfAccount(int house, long accountId)
        {
            lock (Gate)
            {
                Load();
                return _listings.Values.Where(l => l.House == house && l.SellerAccountId == accountId)
                                       .OrderBy(l => l.Id).ToList();
            }
        }

        /// <summary>
        /// Puts a lot on sale, written before it is known: nothing is in memory that the database
        /// does not have. Null when it could not be written.
        /// </summary>
        public static (Listing Listing, OfferView Offer, bool NewOffer)? Add(
            Marketplaces.House house, long sellerCharacterId, long sellerAccountId,
            int gid, int itemType, int quantity, string effects, long price, DateTime nowUtc)
        {
            lock (Gate)
            {
                Load();
                var listed = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                var expires = listed + house.OnSale;
                int id;
                try
                {
                    using var connection = Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "INSERT INTO MarketplaceListings (House, SellerCharacterId, SellerAccountId, Gid, " +
                        "ItemType, Quantity, Effects, Price, ListedAt, ExpiresAt) VALUES ($h, $c, $a, $g, $t, " +
                        "$q, $e, $p, $l, $x); SELECT last_insert_rowid();";
                    command.Parameters.AddWithValue("$h", house.Id);
                    command.Parameters.AddWithValue("$c", sellerCharacterId);
                    command.Parameters.AddWithValue("$a", sellerAccountId);
                    command.Parameters.AddWithValue("$g", gid);
                    command.Parameters.AddWithValue("$t", itemType);
                    command.Parameters.AddWithValue("$q", quantity);
                    command.Parameters.AddWithValue("$e", effects ?? "");
                    command.Parameters.AddWithValue("$p", price);
                    command.Parameters.AddWithValue("$l", new DateTimeOffset(listed).ToUnixTimeSeconds());
                    command.Parameters.AddWithValue("$x", new DateTimeOffset(expires).ToUnixTimeSeconds());
                    id = Convert.ToInt32(command.ExecuteScalar());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Marketplaces] Could not put {gid} x{quantity} on sale: {ex.Message}");
                    return null;
                }

                var listing = new Listing
                {
                    Id = id,
                    House = house.Id,
                    SellerCharacterId = sellerCharacterId,
                    SellerAccountId = sellerAccountId,
                    Gid = gid,
                    ItemType = itemType,
                    Quantity = quantity,
                    Effects = effects ?? "",
                    Price = price,
                    ListedUtc = listed,
                    ExpiresUtc = expires,
                };
                bool isNew = !_offers.ContainsKey((house.Id, gid, KeyOf(listing.Effects)));
                var offer = Index(listing);
                return (listing, View(offer, house.Lots), isNew);
            }
        }

        /// <summary>
        /// Takes one of an account's lots off sale, to go back to its seller. Null when it is not
        /// theirs, not in this marketplace, or already gone.
        /// </summary>
        public static (Listing Listing, OfferView Offer)? Withdraw(Marketplaces.House house, long accountId, int listingId)
        {
            lock (Gate)
            {
                Load();
                if (!_listings.TryGetValue(listingId, out var listing)) return null;
                if (listing.House != house.Id || listing.SellerAccountId != accountId) return null;
                if (!Delete(listing.Id)) return null;
                return (listing, Unindex(listing, house.Lots));
            }
        }

        /// <summary>
        /// A new price for one of an account's lots, written before it is known. The lot keeps its
        /// id, its lot size and its time on sale. Null when it is not theirs, not in this
        /// marketplace, gone, or could not be written.
        /// </summary>
        public static (Listing Before, Listing After, OfferView Offer)? ChangePrice(
            Marketplaces.House house, long accountId, int listingId, long newPrice)
        {
            lock (Gate)
            {
                Load();
                if (newPrice <= 0 || !_listings.TryGetValue(listingId, out var before)) return null;
                if (before.House != house.Id || before.SellerAccountId != accountId) return null;
                try
                {
                    using var connection = Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE MarketplaceListings SET Price = $p WHERE Id = $i;";
                    command.Parameters.AddWithValue("$p", newPrice);
                    command.Parameters.AddWithValue("$i", listingId);
                    if (command.ExecuteNonQuery() == 0) return null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Marketplaces] Could not change the price of listing {listingId}: {ex.Message}");
                    return null;
                }

                var after = new Listing
                {
                    Id = before.Id,
                    House = before.House,
                    SellerCharacterId = before.SellerCharacterId,
                    SellerAccountId = before.SellerAccountId,
                    Gid = before.Gid,
                    ItemType = before.ItemType,
                    Quantity = before.Quantity,
                    Effects = before.Effects,
                    Price = newPrice,
                    ListedUtc = before.ListedUtc,
                    ExpiresUtc = before.ExpiresUtc,
                };
                _listings[after.Id] = after;
                if (_offerOfListing.TryGetValue(after.Id, out int offerId) && _offersById.TryGetValue(offerId, out var offer))
                {
                    int at = offer.Listings.FindIndex(l => l.Id == after.Id);
                    if (at >= 0) offer.Listings[at] = after;
                    return (before, after, View(offer, house.Lots));
                }
                return (before, after, new OfferView { Gid = after.Gid, ItemType = after.ItemType, Gone = true });
            }
        }

        // ─── Buying ─────────────────────────────────────────────────────────────────────────

        public enum Refusal { None, NoSuchOffer, PriceChanged }

        /// <summary>
        /// Buys the cheapest lot of a size in an offer, if its price is still the one the buyer
        /// was shown. The lot leaves the book here and the sale is recorded; paying and handing
        /// the item over is the caller's.
        /// </summary>
        public static (Listing Listing, OfferView Offer)? Buy(Marketplaces.House house, int offerId, int lot,
                                                              long price, long buyerCharacterId, DateTime nowUtc,
                                                              out Refusal refusal)
        {
            refusal = Refusal.None;
            lock (Gate)
            {
                Load();
                if (!_offersById.TryGetValue(offerId, out var offer) || offer.House != house.Id)
                {
                    refusal = Refusal.NoSuchOffer;
                    return null;
                }
                var cheapest = offer.Listings.Where(l => l.Quantity == lot && l.ExpiresUtc > nowUtc)
                                             .OrderBy(l => l.Price).ThenBy(l => l.Id).FirstOrDefault();
                if (cheapest == null)
                {
                    refusal = Refusal.NoSuchOffer;
                    return null;
                }
                if (cheapest.Price != price)
                {
                    refusal = Refusal.PriceChanged;
                    return null;
                }
                if (!Delete(cheapest.Id))
                {
                    refusal = Refusal.NoSuchOffer;
                    return null;
                }
                Record(cheapest, buyerCharacterId, nowUtc);
                Remember(cheapest, sold: true, nowUtc);
                return (cheapest, Unindex(cheapest, house.Lots));
            }
        }

        /// <summary>
        /// Puts a lot back on sale under its own id, when the buyer could not be handed it after
        /// all. The sale it had recorded goes too.
        /// </summary>
        public static void Restore(Listing listing, long buyerCharacterId)
        {
            lock (Gate)
            {
                try
                {
                    using var connection = Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "INSERT INTO MarketplaceListings (Id, House, SellerCharacterId, SellerAccountId, Gid, " +
                        "ItemType, Quantity, Effects, Price, ListedAt, ExpiresAt) VALUES ($i, $h, $c, $a, $g, " +
                        "$t, $q, $e, $p, $l, $x); " +
                        "DELETE FROM MarketplaceSales WHERE Id = (SELECT MAX(Id) FROM MarketplaceSales " +
                        "WHERE Gid = $g AND BuyerCharacterId = $b AND Price = $p); " +
                        "DELETE FROM MarketplaceHistory WHERE Id = (SELECT MAX(Id) FROM MarketplaceHistory " +
                        "WHERE SellerAccountId = $a AND Gid = $g AND Kamas = $p AND Sold = 1);";
                    command.Parameters.AddWithValue("$i", listing.Id);
                    command.Parameters.AddWithValue("$h", listing.House);
                    command.Parameters.AddWithValue("$c", listing.SellerCharacterId);
                    command.Parameters.AddWithValue("$a", listing.SellerAccountId);
                    command.Parameters.AddWithValue("$g", listing.Gid);
                    command.Parameters.AddWithValue("$t", listing.ItemType);
                    command.Parameters.AddWithValue("$q", listing.Quantity);
                    command.Parameters.AddWithValue("$e", listing.Effects);
                    command.Parameters.AddWithValue("$p", listing.Price);
                    command.Parameters.AddWithValue("$l", new DateTimeOffset(listing.ListedUtc).ToUnixTimeSeconds());
                    command.Parameters.AddWithValue("$x", new DateTimeOffset(listing.ExpiresUtc).ToUnixTimeSeconds());
                    command.Parameters.AddWithValue("$b", buyerCharacterId);
                    command.ExecuteNonQuery();
                    Index(listing);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Marketplaces] Could not put listing {listing.Id} back on sale: {ex.Message}");
                }
            }
        }

        // ─── Time ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The lots whose time on sale is over, off the book, each with what its offer looks like
        /// after. The marketplace's own 672 hours: kdw's f6, and the 2,419,200 seconds of kes.
        /// </summary>
        public static IReadOnlyList<(Listing Listing, OfferView Offer)> TakeExpired(DateTime nowUtc)
        {
            var gone = new List<(Listing, OfferView)>();
            lock (Gate)
            {
                Load();
                foreach (var listing in _listings.Values.Where(l => l.ExpiresUtc <= nowUtc).ToList())
                {
                    if (!Marketplaces.TryGet(listing.House, out var house)) continue;
                    if (!Delete(listing.Id)) continue;
                    Remember(listing, sold: false, nowUtc);
                    gone.Add((listing, Unindex(listing, house.Lots)));
                }
            }
            return gone;
        }

        // ─── A seller's history ─────────────────────────────────────────────────────────────

        /// <summary>
        /// An account's sales history, newest first: what was sold and what came back unsold since
        /// <paramref name="sinceUtc"/>, at most <see cref="HistoryLimit"/> lines.
        /// </summary>
        public static IReadOnlyList<HistoryEntry> HistoryOf(long accountId, DateTime sinceUtc)
        {
            var lines = new List<HistoryEntry>();
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT House, Gid, Quantity, Effects, Kamas, Sold, At FROM MarketplaceHistory " +
                    "WHERE SellerAccountId = $a AND At >= $s ORDER BY At DESC, Id DESC LIMIT $n;";
                command.Parameters.AddWithValue("$a", accountId);
                command.Parameters.AddWithValue("$s", new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$n", HistoryLimit);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    lines.Add(new HistoryEntry
                    {
                        House = reader.GetInt32(0),
                        Gid = reader.GetInt32(1),
                        Quantity = reader.GetInt32(2),
                        Effects = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Kamas = reader.GetInt64(4),
                        Sold = reader.GetInt64(5) != 0,
                        AtUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)).UtcDateTime,
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] Could not read the sales history of account {accountId}: {ex.Message}");
            }
            return lines;
        }

        /// <summary>For tests: an account's lots and sales, gone from the book and the database.</summary>
        internal static void EraseAccount(long accountId)
        {
            lock (Gate)
            {
                Load();
                using (var connection = Open())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM MarketplaceListings WHERE SellerAccountId = $a; " +
                                          "DELETE FROM MarketplaceSales WHERE SellerAccountId = $a; " +
                                          "DELETE FROM MarketplaceHistory WHERE SellerAccountId = $a;";
                    command.Parameters.AddWithValue("$a", accountId);
                    command.ExecuteNonQuery();
                }
                foreach (var listing in _listings.Values.Where(l => l.SellerAccountId == accountId).ToList())
                {
                    if (Marketplaces.TryGet(listing.House, out var house)) Unindex(listing, house.Lots);
                    else _listings.Remove(listing.Id);
                }
            }
        }

        // ─── The database ───────────────────────────────────────────────────────────────────

        private static bool Delete(int listingId)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM MarketplaceListings WHERE Id = $i;";
                command.Parameters.AddWithValue("$i", listingId);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] Could not take listing {listingId} off sale: {ex.Message}");
                return false;
            }
        }

        /// <summary>A line of the seller's history: the lot, sold or back unsold, and when.</summary>
        private static void Remember(Listing listing, bool sold, DateTime nowUtc)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO MarketplaceHistory (SellerAccountId, House, Gid, Quantity, Effects, Kamas, Sold, At) " +
                    "VALUES ($a, $h, $g, $q, $e, $k, $s, $t);";
                command.Parameters.AddWithValue("$a", listing.SellerAccountId);
                command.Parameters.AddWithValue("$h", listing.House);
                command.Parameters.AddWithValue("$g", listing.Gid);
                command.Parameters.AddWithValue("$q", listing.Quantity);
                command.Parameters.AddWithValue("$e", listing.Effects ?? "");
                command.Parameters.AddWithValue("$k", listing.Price);
                command.Parameters.AddWithValue("$s", sold ? 1 : 0);
                command.Parameters.AddWithValue("$t", new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] Could not remember listing {listing.Id} in its seller's history: {ex.Message}");
            }
        }

        private static void Record(Listing listing, long buyerCharacterId, DateTime nowUtc)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO MarketplaceSales (House, Gid, Quantity, Price, SellerAccountId, BuyerCharacterId, " +
                    "SoldAt) VALUES ($h, $g, $q, $p, $a, $b, $t);";
                command.Parameters.AddWithValue("$h", listing.House);
                command.Parameters.AddWithValue("$g", listing.Gid);
                command.Parameters.AddWithValue("$q", listing.Quantity);
                command.Parameters.AddWithValue("$p", listing.Price);
                command.Parameters.AddWithValue("$a", listing.SellerAccountId);
                command.Parameters.AddWithValue("$b", buyerCharacterId);
                command.Parameters.AddWithValue("$t", new DateTimeOffset(nowUtc).ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] Could not record the sale of listing {listing.Id}: {ex.Message}");
            }
        }
    }
}
