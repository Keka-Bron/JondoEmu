using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Jondo.Unity.Server.Managers;
using ItemEffect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The messages of a marketplace, as the five captures that open one have them: equipment
    /// (open, browse, buy, switch to selling, sell, switch back), runes (one lot of each size
    /// bought), creatures, souls and cosmetics. Frame numbers in the remarks of
    /// <see cref="Handlers.MarketplaceHandler"/>.
    /// </summary>
    public static class MarketplaceProtocol
    {
        /// <summary>iov f1: the "Vender" button of a marketplace, NpcActions row 5.</summary>
        public const int SellAction = 5;

        /// <summary>iov f1: the "Comprar" button, NpcActions row 6.</summary>
        public const int BuyAction = 6;

        /// <summary>iov f3 when the action is a counter's and not an NPC's.</summary>
        public const long NoNpc = -1;

        /// <summary>
        /// A marketplace's settings, the f1 of kdw and the f3 of kby:
        /// { f1: -1, f2: 200, f3: 708, f4: the marketplace, f5: float 2.0, f6: 672, f7: float 1.0,
        ///   f9: packed item types, f10: packed lot sizes }.
        /// </summary>
        public static Pb Settings(Marketplaces.House house)
        {
            var kfw = Pb.New()
                .VarIfNotZero(1, house.NpcContextualId)
                .VarIfNotZero(2, house.MaxItemLevel)
                .VarIfNotZero(3, house.MaxListings)
                .VarIfNotZero(4, house.Id);
            Float(kfw, 5, house.TaxPercentage);
            kfw.VarIfNotZero(6, house.HoursOnSale);
            Float(kfw, 7, house.TaxModificationPercentage);
            kfw.VarIfNotZero(8, house.Unknown8);
            if (house.ItemTypes.Count > 0) kfw.Packed(9, Longs(house.ItemTypes));
            if (house.Lots.Count > 0) kfw.Packed(10, Longs(house.Lots));
            return kfw;
        }

        /// <summary>The marketplace opens to buy (kdw): { f1: settings }.</summary>
        public static byte[] BuildBuyerOpened(Marketplaces.House house)
            => Pb.New().Msg(1, Settings(house)).Build();

        /// <summary>
        /// The marketplace opens to sell (kby): { f1 (repeated): the account's listings here,
        /// f3: settings }. Measured with no listing; the shape of one is the client's own kbw,
        /// { f2: the item as <see cref="ListedItem"/>, f3: price, f4: seconds left }, INFERRED
        /// from kes, which carries the same three things for a new one.
        /// </summary>
        public static byte[] BuildSellerOpened(Marketplaces.House house,
                                               IEnumerable<(MarketplaceListings.Listing Listing, long SecondsLeft)> listings)
        {
            var kby = Pb.New();
            foreach (var (listing, left) in listings)
            {
                kby.Msg(1, Pb.New()
                    .Msg(2, ListedItem(listing))
                    .VarIfNotZero(3, listing.Price)
                    .VarIfNotZero(4, left));
            }
            return kby.Msg(3, Settings(house)).Build();
        }

        /// <summary>The items of one type on sale (kda): { f1: packed items, f2: type }; f2 alone when none.</summary>
        public static byte[] BuildTypeItems(int itemType, IReadOnlyCollection<int> items)
        {
            var kda = Pb.New();
            if (items.Count > 0) kda.Packed(1, Longs(items));
            return kda.Var(2, itemType).Build();
        }

        /// <summary>
        /// One item's offers (kbt): { f1: type, f2: item, f3 (repeated) { f1: offer, f4: effects,
        /// f5: item, f6: packed price of each lot size, 0 for none, f8: type } }. Without offers it
        /// is f1 and f2 alone, which is also the answer to the keh that stops following.
        /// </summary>
        public static byte[] BuildItemOffers(int itemType, int gid, IEnumerable<MarketplaceListings.OfferView> offers)
        {
            var kbt = Pb.New().Var(1, itemType).Var(2, gid);
            foreach (var offer in offers)
            {
                var body = Pb.New().Var(1, offer.Id);
                ConnectionProtocol.AddEffects(body, 4, offer.Effects);
                body.Var(5, offer.Gid).Packed(6, offer.Prices).Var(8, offer.ItemType);
                kbt.Msg(3, body);
            }
            return kbt.Build();
        }

        /// <summary>
        /// An offer of a followed item changed (kgp): { f2: packed prices, f3: offer, f4: effects,
        /// f5: item, f6: type }.
        /// </summary>
        public static byte[] BuildOfferUpdated(MarketplaceListings.OfferView offer)
        {
            var kgp = Pb.New().Packed(2, offer.Prices).Var(3, offer.Id);
            ConnectionProtocol.AddEffects(kgp, 4, offer.Effects);
            return kgp.Var(5, offer.Gid).Var(6, offer.ItemType).Build();
        }

        /// <summary>
        /// An offer of a followed item appeared (kfi): { f1: effects, f2: type, f3: item, f4: packed
        /// prices, f5: offer }.
        /// </summary>
        public static byte[] BuildOfferAdded(MarketplaceListings.OfferView offer)
        {
            var kfi = ConnectionProtocol.AddEffects(Pb.New(), 1, offer.Effects);
            return kfi.Var(2, offer.ItemType).Var(3, offer.Gid).Packed(4, offer.Prices).Var(5, offer.Id).Build();
        }

        /// <summary>An offer of a followed item is gone (kgv): { f1: item, f2: type, f4: offer }.</summary>
        public static byte[] BuildOfferRemoved(int gid, int itemType, int offerId)
            => Pb.New().Var(1, gid).Var(2, itemType).Var(4, offerId).Build();

        /// <summary>A purchase went through (kcx): { f2: offer, f4: true }.</summary>
        public static byte[] BuildBought(int offerId)
            => Pb.New().Var(2, offerId).Var(4, 1).Build();

        /// <summary>
        /// The seller's lot is on sale (kes): { f1: the item, f2: price, f4: seconds on sale }.
        /// </summary>
        public static byte[] BuildListed(MarketplaceListings.Listing listing, long secondsLeft)
            => Pb.New().Msg(1, ListedItem(listing)).VarIfNotZero(2, listing.Price).VarIfNotZero(4, secondsLeft).Build();

        /// <summary>
        /// One of the seller's lots is off sale (ken): { f1: the listing }. Read off the client:
        /// the marketplace's frame (emz) takes a ken by removing from the seller's list the lot
        /// whose id is its f1, and redraws the list; its f2 is not read there, so it is not sent.
        /// </summary>
        public static byte[] BuildSellerRemoved(int listingId) => Pb.New().Var(1, listingId).Build();

        /// <summary>
        /// A seller's sales history (las): every line in f3, newest first. f1 and f2 are not read
        /// by the frame that takes it (emz), so they do not travel.
        /// </summary>
        /// <remarks>
        /// Read off the client, no capture has one: the frame turns each laq of f3 that has an item
        /// into a line of the sales history window --
        ///
        ///   laq { f1: kamas, f3: the date, as text, f4: 0 sold / 1 unsold,
        ///         f5 { f1: item, f2: how many, f4: effects }, f6: the marketplace }
        ///
        /// The date goes through DateTime.Parse and then ToLocalTime, so it is sent in UTC; the
        /// window adds up the kamas of the sold lines only, and f6 is looked up in the client's
        /// AuctionHousesDataRoot, whose ids the marketplaces are (1261 to 1267).
        /// </remarks>
        public static byte[] BuildSalesHistory(IEnumerable<MarketplaceListings.HistoryEntry> lines)
        {
            var las = Pb.New();
            foreach (var line in lines)
            {
                var item = Pb.New().Var(1, line.Gid).VarIfNotZero(2, line.Quantity);
                ConnectionProtocol.AddEffects(item, 4, Equipment.ParseEffects(line.Effects));
                las.Msg(3, Pb.New()
                    .VarIfNotZero(1, line.Kamas)
                    .Str(3, DateTime.SpecifyKind(line.AtUtc, DateTimeKind.Utc)
                                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                    .VarIfNotZero(4, line.Sold ? 0 : 1)
                    .Msg(5, item)
                    .Var(6, line.House));
            }
            return las.Build();
        }

        /// <summary>
        /// A listed lot as kes and kby carry it: { f1: the listing, f2: effects, f3: item, f4: how
        /// many }. Not the order of an inventory item, which puts the item first and the uid last.
        /// </summary>
        public static Pb ListedItem(MarketplaceListings.Listing listing)
        {
            var kfa = Pb.New().Var(1, listing.Id);
            ConnectionProtocol.AddEffects(kfa, 2, Equipment.ParseEffects(listing.Effects));
            return kfa.Var(3, listing.Gid).Var(4, listing.Quantity);
        }

        /// <summary>
        /// The prices of an item for whoever sells it (kcq): { f3: item, f4: average price, f5 {
        /// f1: packed lowest price of each lot size } }.
        /// </summary>
        public static byte[] BuildSellerPrices(int gid, long average, IReadOnlyList<long> lowest)
            => Pb.New().Var(3, gid).VarIfNotZero(4, average).Msg(5, Pb.New().Packed(1, lowest)).Build();

        /// <summary>
        /// What was bought, in the chat (lqn, type 0, text 252): "$quantity{2} x {{item,{0},{1}}}
        /// ($quantity{3} kamas)" -- the item, the uid of what the buyer now holds, how many, and the
        /// price.
        /// </summary>
        public static byte[] BuildPurchaseNotice(int gid, long uid, int quantity, long price)
            => ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, InfoMessages.Purchase,
                gid.ToString(), uid.ToString(), quantity.ToString(), price.ToString());

        private static void Float(Pb pb, int field, float value)
        {
            if (value == 0f) return;       // proto3 leaves a zero out, floats too
            var bytes = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            pb.Fixed32(field, bytes);
        }

        private static IEnumerable<long> Longs(IEnumerable<int> values)
        {
            foreach (int v in values) yield return v;
        }
    }
}
