using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The marketplaces, against the five captures that open one: equipment (E: open, browse, buy,
    /// sell), runes (R: a lot of each size), creatures, souls and cosmetics. The messages byte for
    /// byte; the rest, what the book of lots, the bags, the kamas and the bank do.
    /// </summary>
    [Collection("forgemagic")]
    public class MarketplaceTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private const int EquipmentHouse = 1262, RuneHouse = 1264;

        /// <summary>The settings of E, frames 8 and 234, after their f1 and f3.</summary>
        private const string EquipmentSettings =
            "08ffffffffffffffffff0110c80118c40520ee092d0000004030a0053d0000803f4a2b0102030405060708090a0b" +
            "8f021011910252920213930294021595021697011797029802d901a901f001725205010a64e807";

        private static Marketplaces.House House(int id)
        {
            Marketplaces.Initialize();
            Assert.True(Marketplaces.TryGet(id, out var house));
            return house;
        }

        // ─── The data ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void The_seven_marketplaces_are_the_clients_and_the_captures()
        {
            Marketplaces.Initialize();
            Assert.Equal(new[] { 1261, 1262, 1263, 1264, 1265, 1266, 1267 }, Marketplaces.All.Select(h => h.Id).OrderBy(i => i));
            Assert.Equal(355, Marketplaces.Skill);

            // The item types of each kdw, in the capture's order.
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 271, 16, 17, 273, 82, 274, 19, 275, 276, 21, 277,
                                 22, 151, 23, 279, 280, 217, 169, 240, 114 }, House(EquipmentHouse).ItemTypes);
            Assert.Equal(new[] { 258, 211, 233, 26, 189, 78 }, House(RuneHouse).ItemTypes);
            Assert.Equal(new[] { 209, 18, 99, 323, 197, 326, 121, 331, 332, 301, 333, 206 }, House(1265).ItemTypes);
            Assert.Equal(new[] { 259, 83, 327, 328 }, House(1266).ItemTypes);
            Assert.Equal(new[] { 113, 324, 246, 247, 199, 248, 249, 250, 251, 299, 252, 300 }, House(1267).ItemTypes);

            // Resources and consumables: no capture, their category's leftovers. None taken twice.
            var all = Marketplaces.All.SelectMany(h => h.ItemTypes).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.Contains(34, House(1261).ItemTypes);     // Cereal
            Assert.Contains(12, House(1263).ItemTypes);     // Pócima
            Assert.False(House(1261).Measured);
            Assert.True(House(EquipmentHouse).Measured);

            // The counters the captures open, each on its own marketplace.
            foreach (var (element, id) in new[] { (522691, 1262), (522693, 1264), (522695, 1265), (522696, 1266), (523752, 1267) })
            {
                Assert.True(Marketplaces.TryGetByElement(element, out var house));
                Assert.Equal(id, house.Id);
            }
            // Every kind has somewhere to be opened, and the three the cities all have, in each.
            foreach (var house in Marketplaces.All)
                Assert.True(Marketplaces.CountersOf(house.Id) > 0, house.Name);
            Assert.Equal(9, Marketplaces.CountersOf(1261));
            Assert.Equal(8, Marketplaces.CountersOf(1262));
            Assert.Equal(8, Marketplaces.CountersOf(1263));
        }

        [Fact]
        public void The_lot_sizes_and_the_tax_are_the_captures()
        {
            var house = House(EquipmentHouse);
            Assert.Equal(new[] { 1, 10, 100, 1000 }, house.Lots);
            Assert.Equal(2, house.LotIndex(100));
            Assert.Equal(-1, house.LotIndex(7));

            // E 253-254: 999 on sale, 66,139,715 kamas before and 66,139,695 after.
            Assert.Equal(66139715 - 66139695, house.Tax(999));
            Assert.Equal(20, house.Tax(1000));
            Assert.Equal(1, house.Tax(1));
            // The client's own reckoning (AuctionHouseSell.UpdateTax): to the nearest, halves to
            // even, never under 1.
            Assert.Equal(435, house.Tax(21760));     // 435.2
            Assert.Equal(20, house.Tax(1025));       // 20.5
            Assert.Equal(22, house.Tax(1075));       // 21.5
            Assert.Equal(1, house.Tax(10));          // 0.2
            // A new price: the whole tax on a dearer one, 1 % on a cheaper or the same one.
            Assert.Equal(40, house.ModificationTax(1000, 2000));
            Assert.Equal(9, house.ModificationTax(1000, 900));
            Assert.Equal(10, house.ModificationTax(1000, 1000));
            Assert.Equal(672, house.HoursOnSale);
            Assert.Equal(2419200, (long)house.OnSale.TotalSeconds);
        }

        // ─── The messages ───────────────────────────────────────────────────────────────────

        [Fact]
        public void Opening_to_buy_and_to_sell_is_the_capture()
        {
            var house = House(EquipmentHouse);
            Assert.Equal(Hex("0a55" + EquipmentSettings), MarketplaceProtocol.BuildBuyerOpened(house));
            Assert.Equal(Hex("1a55" + EquipmentSettings),
                         MarketplaceProtocol.BuildSellerOpened(house, Array.Empty<(MarketplaceListings.Listing, long)>()));
            // R 6: the runes' counter, the same but for f4 and f9.
            Assert.Equal(Hex("0a3408ffffffffffffffffff0110c80118c40520f0092d0000004030a0053d0000803f4a0a8202d301e9011abd014e5205010a64e807"),
                         MarketplaceProtocol.BuildBuyerOpened(House(RuneHouse)));
            // Closing, R 309.
            Assert.Equal(Hex("180b"), ConnectionProtocol.BuildShopClosed());
        }

        [Fact]
        public void Browsing_is_the_capture()
        {
            // E 43 and 41: a type with nothing on sale, and one with two items.
            Assert.Equal(Hex("1072"), MarketplaceProtocol.BuildTypeItems(114, new int[0]));
            Assert.Equal(Hex("0a05d6cc019f0b1015"), MarketplaceProtocol.BuildTypeItems(21, new[] { 26198, 1439 }));

            // E 200: the keh that stops following.
            Assert.Equal(Hex("081010b154"), MarketplaceProtocol.BuildItemOffers(16, 10801, Array.Empty<MarketplaceListings.OfferView>()));

            // E 205: ring 853, three offers, one per roll.
            var offers = new[]
            {
                Offer(3316, 853, 9, new[] { new Effect(123, 7, 0, 0) }, 850, 0, 0, 0),
                Offer(147239, 853, 9, new[] { new Effect(125, 34, 0, 0), new Effect(123, 5, 0, 0) }, 2599, 0, 0, 0),
                Offer(3315, 853, 9, new[] { new Effect(123, 6, 0, 0), new Effect(124, 2, 0, 0) }, 2000, 0, 0, 0),
            };
            Assert.Equal(Hex("080910d5061a1508f41922042007587b28d5063205d20600000040091a1c08a7fe0822042022587d22042005587b" +
                             "28d5063205a71400000040091a1b08f31922042006587b22042002587c28d5063205d00f0000004009"),
                         MarketplaceProtocol.BuildItemOffers(9, 853, offers));

            // R 49: rune 10057, one offer with a price for every lot size but the last.
            Assert.Equal(Hex("084e10c94e1a1b08abb40222052001589b0628c94e3209b620c3df02ffb41800404e"),
                         MarketplaceProtocol.BuildItemOffers(78, 10057, new[] { Offer(39467, 10057, 78, new[] { new Effect(795, 1, 0, 0) }, 4150, 44995, 399999, 0) }));
        }

        [Fact]
        public void Buying_is_the_capture()
        {
            // E 214-219: the offer's last lot bought.
            Assert.Equal(Hex("08d506100920f419"), MarketplaceProtocol.BuildOfferRemoved(853, 9, 3316));
            Assert.Equal(Hex("08c3ecc41f"), ConnectionProtocol.BuildKamas(66139715));
            Assert.Equal(Hex("10fc01220338353322093533363833373834352201312203383530"),
                         MarketplaceProtocol.BuildPurchaseNotice(853, 536837845, 1, 850));
            Assert.Equal(Hex("10f4192001"), MarketplaceProtocol.BuildBought(3316));

            // R 79: a lot of one of rune 1523 bought at 198; the next one of one is 199.
            Assert.Equal(Hex("120ac701e61080aa01fba60d18c93222042005587d28f30b304e"),
                         MarketplaceProtocol.BuildOfferUpdated(Offer(6473, 1523, 78, new[] { new Effect(125, 5, 0, 0) }, 199, 2150, 21760, 217979)));
        }

        [Fact]
        public void Selling_is_the_capture()
        {
            // E 255-257: the ring on sale for 999, listing 3,386,955, offer 198,567.
            var listing = new MarketplaceListings.Listing
            {
                Id = 3386955, Gid = 853, ItemType = 9, Quantity = 1, Effects = "[[123,7,0,0]]", Price = 999,
            };
            Assert.Equal(Hex("0a1008cbdcce0112042007587b18d506200110e7072080d49301"),
                         MarketplaceProtocol.BuildListed(listing, 2419200));
            var offer = Offer(198567, 853, 9, new[] { new Effect(123, 7, 0, 0) }, 999, 0, 0, 0);
            Assert.Equal(Hex("0a042007587b100918d5062205e70700000028a78f0c"), MarketplaceProtocol.BuildOfferAdded(offer));
            Assert.Equal(Hex("1205e70700000018a78f0c22042007587b28d5063009"), MarketplaceProtocol.BuildOfferUpdated(offer));

            // E 243: what a seller is told of ring 853 before pricing it.
            Assert.Equal(Hex("18d50620f9022a070a05d00f000000"), MarketplaceProtocol.BuildSellerPrices(853, 377, new long[] { 2000, 0, 0, 0 }));
        }

        private static MarketplaceListings.OfferView Offer(int id, int gid, int type, Effect[] effects, params long[] prices)
            => new MarketplaceListings.OfferView { Id = id, Gid = gid, ItemType = type, Effects = effects, Prices = prices };

        // ─── The logic ──────────────────────────────────────────────────────────────────────

        private sealed class Scene : IDisposable
        {
            public readonly List<(GameSession To, string Opcode, byte[] Body)> Sent = new();
            private readonly List<GameSession> _sessions = new();

            public Scene()
            {
                Marketplaces.Initialize();
                MarketplaceListings.Initialize();
                MarketplaceHandler.Sent = (to, op, body) => { lock (Sent) Sent.Add((to, op, body)); };
                var now = DateTime.UtcNow;
                MarketplaceHandler.Clock = () => now;
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                DatabaseManager.MoveScrollsOutOfTheBase(connection);
            }

            public GameSession Player(long id, long account, long kamas)
            {
                MarketplaceListings.EraseAccount(account);
                var session = GameSession.SinSocket();
                session.BindAccount(account, 1);
                session.State.CharacterId = id;
                session.State.MapId = 212600837;
                session.State.Kamas = kamas;
                session.EnterWorld();
                Assert.True(SessionRegistry.Register(session));
                _sessions.Add(session);
                return session;
            }

            public long Give(GameSession session, int gid, int quantity, string? effects = null)
            {
                long uid = DatabaseManager.NextItemUid();
                using (SessionContext.Push(session))
                {
                    Assert.True(DatabaseManager.InsertCharacterItem(uid, session.CharacterId, gid, quantity, Server.Managers.Equipment.Bag, effects));
                    Server.Managers.Equipment.Add(uid, gid, quantity, Server.Managers.Equipment.Bag, effects);
                }
                return uid;
            }

            public List<(string Opcode, byte[] Body)> To(GameSession session)
            {
                lock (Sent) return Sent.Where(s => s.To == session).Select(s => (s.Opcode, s.Body)).ToList();
            }

            public void Dispose()
            {
                MarketplaceHandler.Sent = null;
                MarketplaceHandler.Clock = () => DateTime.UtcNow;
                foreach (var session in _sessions)
                {
                    using (SessionContext.Push(session))
                        foreach (var item in Server.Managers.Equipment.All.ToList())
                            DatabaseManager.DestroyCharacterItem(session.CharacterId, item.Uid, 0);
                    MarketplaceListings.EraseAccount(session.AccountId);
                    SessionRegistry.Unregister(session);
                }
            }
        }

        private static async Task As(GameSession session, Func<Task> action)
        {
            using (SessionContext.Push(session)) await action();
        }

        private static byte[] Iov(int action) => ConnectionProtocol.Push(Op.Iov,
            Pb.New().Var(1, action).Var(2, 212600837).Var(3, MarketplaceProtocol.NoNpc).Build());

        private static async Task OpenToSell(GameSession session, int element)
        {
            await As(session, () => MarketplaceHandler.OpenAsync(element, Marketplaces.Skill));
            await As(session, async () => Assert.True(await MarketplaceHandler.ModeAsync(Iov(MarketplaceProtocol.SellAction))));
        }

        private static Task Sell(GameSession session, long price, long uid, int lot)
            => As(session, () => MarketplaceHandler.SellAsync(ConnectionProtocol.Push(Op.Kge,
                Pb.New().Var(1, price).Var(2, uid).Var(3, lot).Build())));

        private static Task Buy(GameSession session, int offer, long price, int lot)
            => As(session, () => MarketplaceHandler.BuyAsync(ConnectionProtocol.Push(Op.Kbm,
                Pb.New().Var(1, offer).Var(2, price).Var(3, lot).Build())));

        private static Task Follow(GameSession session, int gid)
            => As(session, () => MarketplaceHandler.ItemAsync(ConnectionProtocol.Push(Op.Keh,
                Pb.New().Var(1, gid).Var(2, 1).Build())));

        private static List<Server.Managers.Equipment.Item> Bag(GameSession session)
        {
            using (SessionContext.Push(session)) return Server.Managers.Equipment.All.ToList();
        }

        /// <summary>
        /// A lot goes on sale: out of the bag, the tax paid, kes to the seller. Taken back, it
        /// returns to the bag, the tax does not.
        /// </summary>
        [Fact]
        public async Task A_lot_on_sale_leaves_the_bag_and_comes_back_when_taken_back()
        {
            using var scene = new Scene();
            var seller = scene.Player(9_000_000_101, 9_000_000_101, 1_000);
            long ring = scene.Give(seller, 853, 1, "[[123,7,0,0]]");

            await OpenToSell(seller, 522691);
            Assert.True(seller.State.Marketplace!.Selling);
            await Sell(seller, 999, ring, 1);

            Assert.Equal(1_000 - 20, seller.State.Kamas);
            Assert.Empty(Bag(seller));
            Assert.Null(HavenBagStore.FromInventory(seller.CharacterId, ring));
            var listing = Assert.Single(MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId));
            Assert.Equal((853, 1, 999L), (listing.Gid, listing.Quantity, listing.Price));

            // E 254-259, in its order: ivf with what the tax left, kes with the whole 672 hours,
            // the ium of the stack, iun. Nobody follows the ring, so no kfi or kgp.
            var sold = scene.To(seller).SkipWhile(s => s.Opcode != Op.Ivf).ToList();
            Assert.Equal(new[] { Op.Ivf, Op.Kes, Op.Ium, Op.Iun }, sold.Select(s => s.Opcode));
            Assert.Equal(ConnectionProtocol.BuildKamas(980), sold[0].Body);
            Assert.Equal(MarketplaceProtocol.BuildListed(listing, 2419200), sold[1].Body);
            Assert.Equal(ConnectionProtocol.BuildItemGone(ring), sold[2].Body);

            // Taken back: the kcr the client sends for what it drags out of an exchange.
            await As(seller, async () => Assert.True(await MarketplaceHandler.WithdrawAsync(ConnectionProtocol.Push(Op.Kcr,
                Pb.New().Var(1, -1).Var(2, listing.Id).Build()))));
            Assert.Empty(MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId));
            var back = Assert.Single(Bag(seller));
            Assert.Equal((853, 1), (back.Template, back.Quantity));
            Assert.Equal(new[] { new Effect(123, 7, 0, 0) }.Select(e => (e.Effect, e.Value)), back.Effects.Select(e => (e.Effect, e.Value)));
            Assert.NotNull(HavenBagStore.FromInventory(seller.CharacterId, back.Uid));
            Assert.Equal(980, seller.State.Kamas);
            // And ken, which takes the lot off the seller's list.
            Assert.Equal(MarketplaceProtocol.BuildSellerRemoved(listing.Id), scene.To(seller).Last().Body);
            Assert.Equal(Op.Ken, scene.To(seller).Last().Opcode);
        }

        /// <summary>
        /// A lot of ten runes bought: it lands in the buyer's bag, the kamas leave him and reach the
        /// seller's bank, and the offer's price for ten is the next one.
        /// </summary>
        [Fact]
        public async Task A_lot_bought_goes_to_the_buyer_and_its_price_to_the_sellers_bank()
        {
            using var scene = new Scene();
            var seller = scene.Player(9_000_000_111, 9_000_000_111, 100_000);
            var buyer = scene.Player(9_000_000_112, 9_000_000_112, 5_000);
            long runes = scene.Give(seller, 1523, 121, "[[125,5,0,0]]");
            long bankBefore = Bank.KamasOf(seller.AccountId);

            await OpenToSell(seller, 522693);
            await Sell(seller, 198, runes, 1);
            await Sell(seller, 2150, runes, 10);
            await Sell(seller, 2200, runes, 10);
            await Sell(seller, 21760, runes, 100);
            Assert.Equal(121 - 121, Bag(seller).Sum(i => i.Quantity));
            Assert.Equal(100_000 - 4 - 43 - 44 - 435, seller.State.Kamas);

            // The buyer sees one offer, the cheapest lot of each size.
            await As(buyer, () => MarketplaceHandler.OpenAsync(522693, Marketplaces.Skill));
            await Follow(buyer, 1523);
            var offers = MarketplaceListings.OffersOf(House(RuneHouse), 1523);
            var offer = Assert.Single(offers);
            Assert.Equal(new long[] { 198, 2150, 21760, 0 }, offer.Prices);
            Assert.Contains(scene.To(buyer), s => s.Opcode == Op.Kbt
                && s.Body.SequenceEqual(MarketplaceProtocol.BuildItemOffers(78, 1523, offers)));

            await Buy(buyer, offer.Id, 2150, 10);

            Assert.Equal(5_000 - 2150, buyer.State.Kamas);
            var got = Assert.Single(Bag(buyer));
            Assert.Equal((1523, 10), (got.Template, got.Quantity));
            Assert.Equal(bankBefore + 2150, Bank.KamasOf(seller.AccountId));
            Assert.Equal(new long[] { 198, 2200, 21760, 0 }, MarketplaceListings.OffersOf(House(RuneHouse), 1523).Single().Prices);

            // What the buyer was told, in the capture's order: kgp, ivf, iua, iun, lqn 252, kcx.
            var told = scene.To(buyer).SkipWhile(s => s.Opcode != Op.Kgp).Select(s => s.Opcode).ToList();
            Assert.Equal(new[] { Op.Kgp, Op.Ivf, Op.Iua, Op.Iun, Op.Lqn, Op.Kcx }, told);
            Assert.Contains(scene.To(buyer), s => s.Opcode == Op.Lqn
                && s.Body.SequenceEqual(MarketplaceProtocol.BuildPurchaseNotice(1523, got.Uid, 10, 2150)));
            // And the seller, who is here, that his bank got it: lqn 65, "venta" a link to the
            // sales history, and the history itself, sold for 2150.
            Assert.Contains(scene.To(seller), s => s.Opcode == Op.Lqn
                && s.Body.SequenceEqual(ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, InfoMessages.MarketplaceSoldLinked, "2150", "", "1523", "10")));
            var history = MarketplaceListings.HistoryOf(seller.AccountId, DateTime.UtcNow.AddDays(-1));
            var line = Assert.Single(history);
            Assert.Equal((RuneHouse, 1523, 10, 2150L, true), (line.House, line.Gid, line.Quantity, line.Kamas, line.Sold));
            Assert.Equal(MarketplaceProtocol.BuildSalesHistory(history), scene.To(seller).Last(s => s.Opcode == Op.Las).Body);

            // The second lot of ten is the last one: bought, the price for ten is none.
            await Buy(buyer, offer.Id, 2200, 10);
            Assert.Equal(20, Bag(buyer).Single().Quantity);
            Assert.Equal(new long[] { 198, 0, 21760, 0 }, MarketplaceListings.OffersOf(House(RuneHouse), 1523).Single().Prices);
        }

        /// <summary>Too few kamas buys nothing, and a price no longer on offer neither.</summary>
        [Fact]
        public async Task Too_few_kamas_or_a_stale_price_buys_nothing()
        {
            using var scene = new Scene();
            var seller = scene.Player(9_000_000_121, 9_000_000_121, 10_000);
            var buyer = scene.Player(9_000_000_122, 9_000_000_122, 100);
            long runes = scene.Give(seller, 1525, 1, "[[123,1,0,0]]");
            await OpenToSell(seller, 522693);
            await Sell(seller, 198, runes, 1);
            var offer = MarketplaceListings.OffersOf(House(RuneHouse), 1525).Single();

            await As(buyer, () => MarketplaceHandler.OpenAsync(522693, Marketplaces.Skill));
            await Buy(buyer, offer.Id, 198, 1);
            Assert.Equal(100, buyer.State.Kamas);
            Assert.Empty(Bag(buyer));
            Assert.Single(MarketplaceListings.OfAccount(RuneHouse, seller.AccountId));
            Assert.Contains(scene.To(buyer), s => s.Opcode == Op.Lqn
                && s.Body.SequenceEqual(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.MarketplaceCannotAfford)));

            buyer.State.Kamas = 1_000;
            await Buy(buyer, offer.Id, 150, 1);      // not the price it is on sale for
            Assert.Equal(1_000, buyer.State.Kamas);
            Assert.Single(MarketplaceListings.OfAccount(RuneHouse, seller.AccountId));
            Assert.Contains(scene.To(buyer), s => s.Opcode == Op.Lqn
                && s.Body.SequenceEqual(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.MarketplaceSoldOut)));
        }

        /// <summary>
        /// Only the lot sizes of the marketplace, only from a stack that has them, only of the types
        /// it takes, and only when the tax can be paid.
        /// </summary>
        [Fact]
        public async Task A_lot_is_refused_when_its_size_type_or_tax_is_wrong()
        {
            using var scene = new Scene();
            var seller = scene.Player(9_000_000_131, 9_000_000_131, 10);
            long runes = scene.Give(seller, 1523, 5, "[[125,5,0,0]]");

            await OpenToSell(seller, 522693);
            await Sell(seller, 100, runes, 10);    // five in the stack
            await Sell(seller, 100, runes, 3);     // no lot of three
            Assert.Empty(MarketplaceListings.OfAccount(RuneHouse, seller.AccountId));

            await Sell(seller, 5_000, runes, 1);   // a tax of 100 with 10 kamas
            Assert.Empty(MarketplaceListings.OfAccount(RuneHouse, seller.AccountId));
            Assert.Contains(scene.To(seller), s => s.Opcode == Op.Lqn
                && s.Body.SequenceEqual(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.MarketplaceCannotPayTax)));

            // A rune at the equipment counter.
            await OpenToSell(seller, 522691);
            await Sell(seller, 100, runes, 1);
            Assert.Empty(MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId));
            Assert.Contains(scene.To(seller), s => s.Opcode == Op.Lqn
                && s.Body.SequenceEqual(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.MarketplaceWrongCategory)));
            Assert.Equal(5, Bag(seller).Single().Quantity);
            Assert.Equal(10, seller.State.Kamas);
        }

        /// <summary>
        /// Browsing: a type lists the items on sale in it, an item its offers, and the lot is on
        /// sale at every counter of its kind, whatever the city.
        /// </summary>
        [Fact]
        public async Task Browsing_by_type_finds_what_is_on_sale_at_any_counter_of_the_kind()
        {
            using var scene = new Scene();
            var seller = scene.Player(9_000_000_141, 9_000_000_141, 10_000);
            var buyer = scene.Player(9_000_000_142, 9_000_000_142, 10_000);
            long ring = scene.Give(seller, 853, 1, "[[123,7,0,0]]");
            await OpenToSell(seller, 522691);          // Bonta's equipment
            await Sell(seller, 999, ring, 1);

            Assert.True(Marketplaces.TryGetByElement(515221, out var astrub));   // Astrub's equipment
            Assert.Equal(EquipmentHouse, astrub.Id);
            await As(buyer, () => MarketplaceHandler.OpenAsync(515221, Marketplaces.Skill));
            await As(buyer, () => MarketplaceHandler.TypeAsync(ConnectionProtocol.Push(Op.Kdk, Pb.New().Var(2, 1).Var(4, 9).Build())));
            var kda = scene.To(buyer).Last(s => s.Opcode == Op.Kda).Body;
            Assert.Contains(853, TypeItems(kda));
            Assert.Contains(853, MarketplaceListings.ItemsOf(EquipmentHouse, 9));
            Assert.DoesNotContain(853, MarketplaceListings.ItemsOf(EquipmentHouse, 16));

            // A type the marketplace does not take is not answered.
            int before = scene.To(buyer).Count;
            await As(buyer, () => MarketplaceHandler.TypeAsync(ConnectionProtocol.Push(Op.Kdk, Pb.New().Var(2, 1).Var(4, 78).Build())));
            Assert.Equal(before, scene.To(buyer).Count);

            // Following, then not: the bare kbt once, however many times the client says it.
            await Follow(buyer, 853);
            var unfollow = ConnectionProtocol.Push(Op.Keh, Pb.New().Var(1, 853).Build());
            await As(buyer, () => MarketplaceHandler.ItemAsync(unfollow));
            await As(buyer, () => MarketplaceHandler.ItemAsync(unfollow));
            Assert.Single(scene.To(buyer), s => s.Opcode == Op.Kbt && s.Body.SequenceEqual(Hex("080910d506")));
        }

        private static List<int> TypeItems(byte[] kda)
        {
            var items = new List<int>();
            foreach (var f in ProtoMessage.Parse(kda).Fields)
            {
                if (f.FieldNumber != 1 || f.WireType != 2) continue;
                int i = 0;
                while (i < f.BytesValue.Length)
                {
                    long v = 0; int shift = 0;
                    byte b;
                    do { b = f.BytesValue[i++]; v |= (long)(b & 0x7f) << shift; shift += 7; } while ((b & 0x80) != 0);
                    items.Add((int)v);
                }
            }
            return items;
        }

        /// <summary>A lot whose 672 hours are up goes off sale and into its seller's bank, as the game sends unsold lots.</summary>
        [Fact]
        public async Task A_lot_out_of_time_goes_back_to_its_seller()
        {
            using var scene = new Scene();
            var seller = scene.Player(9_000_000_151, 9_000_000_151, 10_000);
            ClearBank(seller.AccountId);
            long ring = scene.Give(seller, 853, 1, "[[123,7,0,0]]");
            await OpenToSell(seller, 522691);
            await Sell(seller, 999, ring, 1);
            Assert.Single(MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId));

            var listed = MarketplaceHandler.Clock();
            MarketplaceHandler.Clock = () => listed.AddHours(671);
            Assert.Equal(0, await MarketplaceHandler.SweepExpiredAsync());
            MarketplaceHandler.Clock = () => listed.AddHours(672);
            Assert.Equal(1, await MarketplaceHandler.SweepExpiredAsync());

            Assert.Empty(MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId));
            try
            {
                Assert.Empty(Bag(seller));
                var banked = Assert.Single(Bank.ItemsOf(seller.AccountId));
                Assert.Equal(853, banked.Gid);
                Assert.Equal(1, banked.Quantity);
            }
            finally { ClearBank(seller.AccountId); }
        }

        /// <summary>
        /// A new price: the tax of the client's own reckoning paid, the lot the same one at its new
        /// price -- ken then kes to the seller -- and the offer's price for its lot size with it.
        /// </summary>
        [Fact]
        public async Task A_lot_on_sale_changes_price_and_pays_the_clients_tax()
        {
            using var scene = new Scene();
            var seller = scene.Player(9_000_000_161, 9_000_000_161, 1_000);
            var buyer = scene.Player(9_000_000_162, 9_000_000_162, 10_000);
            long ring = scene.Give(seller, 853, 1, "[[123,7,0,0]]");
            await OpenToSell(seller, 522691);
            await Sell(seller, 999, ring, 1);
            var listing = Assert.Single(MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId));
            await As(buyer, () => MarketplaceHandler.OpenAsync(522691, Marketplaces.Skill));
            await Follow(buyer, 853);

            // Cheaper: 1 % of 800.
            await ChangePrice(seller, listing.Id, 800, 1);
            Assert.Equal(1_000 - 20 - 8, seller.State.Kamas);
            var cheaper = Assert.Single(MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId));
            Assert.Equal((listing.Id, 800L, listing.ExpiresUtc), (cheaper.Id, cheaper.Price, cheaper.ExpiresUtc));
            var told = scene.To(seller).SkipWhile(s => s.Opcode != Op.Ken).ToList();
            Assert.Equal(new[] { Op.Ken, Op.Kes }, told.Select(s => s.Opcode));
            Assert.Equal(MarketplaceProtocol.BuildSellerRemoved(listing.Id), told[0].Body);
            Assert.Equal(MarketplaceProtocol.BuildListed(cheaper, cheaper.SecondsLeft(MarketplaceHandler.Clock())), told[1].Body);
            Assert.Equal(ConnectionProtocol.BuildKamas(972), scene.To(seller).Last(s => s.Opcode == Op.Ivf).Body);
            var offer = MarketplaceListings.OffersOf(House(EquipmentHouse), 853).Single();
            Assert.Equal(800, offer.Prices[0]);
            Assert.Contains(scene.To(buyer), s => s.Opcode == Op.Kgp && s.Body.SequenceEqual(MarketplaceProtocol.BuildOfferUpdated(offer)));

            // Dearer: the whole 2 % of 1500.
            await ChangePrice(seller, listing.Id, 1500, 1);
            Assert.Equal(972 - 30, seller.State.Kamas);
            Assert.Equal(1500, MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId).Single().Price);

            // Not with a tax it cannot pay, not the same price, not a lot size it does not have,
            // not somebody else's lot.
            seller.State.Kamas = 10;
            await ChangePrice(seller, listing.Id, 5_000, 1);
            Assert.Contains(scene.To(seller), s => s.Opcode == Op.Lqn
                && s.Body.SequenceEqual(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.MarketplaceCannotPayTax)));
            await ChangePrice(seller, listing.Id, 1500, 1);
            await ChangePrice(seller, listing.Id, 900, 10);
            await OpenToSell(buyer, 522691);
            await ChangePrice(buyer, listing.Id, 900, 1);
            Assert.Equal(10, seller.State.Kamas);
            Assert.Equal(10_000, buyer.State.Kamas);
            Assert.Equal(1500, MarketplaceListings.OfAccount(EquipmentHouse, seller.AccountId).Single().Price);
        }

        /// <summary>
        /// The sales history: a lot sold and one that came back unsold, as las carries them, sent
        /// to a connected seller as they happen and to whoever opens the window (lar).
        /// </summary>
        [Fact]
        public async Task The_sales_history_has_what_was_sold_and_what_came_back()
        {
            using var scene = new Scene();
            var seller = scene.Player(9_000_000_171, 9_000_000_171, 100_000);
            var buyer = scene.Player(9_000_000_172, 9_000_000_172, 100_000);
            ClearBank(seller.AccountId);
            try
            {
                long runes = scene.Give(seller, 1523, 11, "[[125,5,0,0]]");
                await OpenToSell(seller, 522693);
                await Sell(seller, 2150, runes, 10);
                await Sell(seller, 198, runes, 1);
                var ten = MarketplaceListings.OfAccount(RuneHouse, seller.AccountId).First(l => l.Quantity == 10);
                var one = MarketplaceListings.OfAccount(RuneHouse, seller.AccountId).First(l => l.Quantity == 1);

                // Bought while the seller's window is open: it loses the lot, and the history comes.
                await As(buyer, () => MarketplaceHandler.OpenAsync(522693, Marketplaces.Skill));
                await Buy(buyer, MarketplaceListings.OffersOf(House(RuneHouse), 1523).Single().Id, 2150, 10);
                Assert.Contains(scene.To(seller), s => s.Opcode == Op.Ken && s.Body.SequenceEqual(MarketplaceProtocol.BuildSellerRemoved(ten.Id)));

                // The other one runs out of time.
                var listed = MarketplaceHandler.Clock();
                MarketplaceHandler.Clock = () => listed.AddHours(672);
                Assert.Equal(1, await MarketplaceHandler.SweepExpiredAsync());
                Assert.Contains(scene.To(seller), s => s.Opcode == Op.Ken && s.Body.SequenceEqual(MarketplaceProtocol.BuildSellerRemoved(one.Id)));

                var history = MarketplaceListings.HistoryOf(seller.AccountId, listed.AddDays(-1));
                Assert.Equal(new[] { (1, 198L, false), (10, 2150L, true) }, history.Select(h => (h.Quantity, h.Kamas, h.Sold)));
                byte[] las = MarketplaceProtocol.BuildSalesHistory(history);
                Assert.Equal(las, scene.To(seller).Last(s => s.Opcode == Op.Las).Body);

                // Asked for by the window.
                await As(seller, () => MarketplaceHandler.SalesHistoryAsync());
                Assert.Equal(las, scene.To(seller).Last(s => s.Opcode == Op.Las).Body);
                Assert.Equal(las, MarketplaceHandler.SalesHistoryAtEntry(seller.CharacterId, seller.AccountId));
            }
            finally { ClearBank(seller.AccountId); }
        }

        /// <summary>ken and las as the client's frame reads them.</summary>
        [Fact]
        public void The_seller_messages_are_what_the_client_reads()
        {
            Assert.Equal(Hex("08cbdcce01"), MarketplaceProtocol.BuildSellerRemoved(3386955));

            var at = new DateTime(2026, 9, 27, 8, 30, 0, DateTimeKind.Utc);
            var las = ProtoMessage.Parse(MarketplaceProtocol.BuildSalesHistory(new[]
            {
                new MarketplaceListings.HistoryEntry { House = 1264, Gid = 1523, Quantity = 10, Effects = "[[125,5,0,0]]", Kamas = 2150, Sold = true, AtUtc = at },
                new MarketplaceListings.HistoryEntry { House = 1262, Gid = 853, Quantity = 1, Kamas = 999, Sold = false, AtUtc = at },
            }));
            var lines = las.Fields.Where(f => f.FieldNumber == 3).Select(f => ProtoMessage.Parse(f.BytesValue)).ToList();
            Assert.Equal(2, lines.Count);
            Assert.DoesNotContain(las.Fields, f => f.FieldNumber != 3);

            var sold = lines[0];
            Assert.Equal(2150, sold.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal("2026-09-27T08:30:00Z", System.Text.Encoding.UTF8.GetString(sold.Fields.Single(f => f.FieldNumber == 3).BytesValue));
            Assert.DoesNotContain(sold.Fields, f => f.FieldNumber == 4);                  // 0, sold
            Assert.Equal(1264, sold.Fields.Single(f => f.FieldNumber == 6).VarIntValue);
            var item = ProtoMessage.Parse(sold.Fields.Single(f => f.FieldNumber == 5).BytesValue);
            Assert.Equal(1523, item.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(10, item.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Single(item.Fields, f => f.FieldNumber == 4);

            Assert.Equal(1, lines[1].Fields.Single(f => f.FieldNumber == 4).VarIntValue);   // unsold
            Assert.True(DateTime.TryParse("2026-09-27T08:30:00Z", System.Globalization.CultureInfo.GetCultureInfo("es-ES"),
                                          System.Globalization.DateTimeStyles.None, out var parsed));
            Assert.Equal(at, parsed.ToUniversalTime());
        }

        private static Task ChangePrice(GameSession session, int listing, long price, int lot)
            => As(session, () => MarketplaceHandler.ChangePriceAsync(ConnectionProtocol.Push(Op.Kch,
                Pb.New().Var(1, listing).Var(2, price).Var(3, lot).Build())));

        /// <summary>A test account's bank emptied, before and after: a stack left over would be stacked onto.</summary>
        private static void ClearBank(long accountId)
        {
            Bank.Initialize();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(Jondo.Unity.Server.DatabaseManager.WorldConnectionString);
            connection.Open();
            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM BankItems WHERE AccountId = $a;";
            clear.Parameters.AddWithValue("$a", accountId);
            clear.ExecuteNonQuery();
        }
    }
}
