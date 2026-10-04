using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>What one character has open at a marketplace: which one, how, and what it follows.</summary>
    public sealed class MarketplaceWindow
    {
        private readonly object _gate = new object();
        private readonly HashSet<int> _types = new HashSet<int>();
        private readonly HashSet<int> _items = new HashSet<int>();

        public int House { get; init; }
        public int ElementId { get; init; }

        /// <summary>Open to sell (kby) rather than to buy (kdw).</summary>
        public bool Selling { get; set; }

        public bool FollowType(int type) { lock (_gate) return _types.Add(type); }
        public bool UnfollowType(int type) { lock (_gate) return _types.Remove(type); }
        public bool FollowItem(int gid) { lock (_gate) return _items.Add(gid); }
        public bool UnfollowItem(int gid) { lock (_gate) return _items.Remove(gid); }
        public bool Follows(int gid) { lock (_gate) return _items.Contains(gid); }
        public bool FollowsType(int type) { lock (_gate) return _types.Contains(type); }

        /// <summary>A change of mode starts over: the client follows again what it wants.</summary>
        public void Forget() { lock (_gate) { _types.Clear(); _items.Clear(); } }
    }

    /// <summary>
    /// The marketplaces: opening one at its counter, browsing, buying a lot, putting one on sale,
    /// changing its price, taking it back, the seller being paid into the bank, and the seller's
    /// sales history.
    /// </summary>
    /// <remarks>
    /// ─── What is measured ─────────────────────────────────────────────────────────────────
    ///
    /// Five captures open one: Interactivos varios/ "abrir mercadillo equipables-navegar items-
    /// comprar-poner en venta" (E), "abrir mercadillo de runas y comprar-lote 1-lote 10-lote 100-
    /// lote 1000" (R), "abrir mercadillo de criaturas" (C), "zaapi ... mercadillo almas-abrir
    /// mercadillo almas-comprar piedra" (S), and Apariencias/ "entrar a mercadillo cosmeticos-
    /// comprar anillo objevivo" (K). Frame numbers are hilo.tramas'.
    ///
    ///   open     C iwo { instance, element }        S iwn { 1, element, 355, who } (to the map),
    ///                                                 kdw { settings }, ivx, hlm {}       E 6-10
    ///   type     C kdk { f2: 1, f4: type }           S kda { f1: items, f2: type }       E 16-17
    ///            C kdk { f4: type }                  (nothing)                           E 336
    ///   item     C keh { f1: item, f2: 1 }           S kbt { type, item, offers }        E 193-194
    ///            C keh { f1: item } twice            S kbt { type, item } once           E 198-200
    ///   buy      C kbm { offer, price, lot }         S kgv (offer gone) or kgp (offer left), ivf,
    ///                                                 iua or ivj, iun, lqn 252, kcx     E 213-219,
    ///                                                                                    R 78-174
    ///   to sell  C iov { 5, map, -1 }                S khd { f3: 11 }, ivx, hlm, kby { f3:
    ///                                                 settings }, ivx, hlm                E 228-236
    ///   price    C kbz { item }                      S kcq { item, average, lowest }     E 241-243
    ///   sell     C kge { price, uid, lot }           S ivf (the tax), kes, kfi, kgp, ium, iun
    ///                                                                                    E 253-259
    ///   to buy   C iov { 6, map, -1 }                S khd, ivx, hlm, kdw, ivx, hlm      E 270-276
    ///   close    C kla                               S khd { f3: 11 }, ivx, hlm          R 308-311
    ///
    /// The ring of E sells for 999 and costs 20 in tax: 66,139,715 kamas before, 66,139,695 after.
    ///
    /// ─── What is not, and is inferred ─────────────────────────────────────────────────────
    ///
    /// No capture takes a lot back, sells one of the player's, lets one run out of time, lists a
    /// seller's lots in kby, changes a price, shows a sales history, or refuses anything. What
    /// follows is read off the client's own code (tools/cliente: the handlers of its marketplace
    /// frame, emz, and the windows that raise its requests) or inferred, and says which:
    ///
    ///   * Taking back: kcr { f1: -n, f2: the listing } in sell mode, the move the client makes
    ///     for everything it drags out of an exchange (its sender, eoy, is the one that sends kge).
    ///     The lot returns to the bag, and ken { f1: the listing } takes it off the seller's list:
    ///     the frame removes from that list the lot whose id is ken's f1 and redraws it.
    ///   * A new price: kch { f1: the listing, f2: price, f3: how many }, raised by the sell
    ///     window's OnConfirmModifyObject, one per lot. Its tax is the window's own reckoning (see
    ///     <see cref="Marketplaces.House.ModificationTax"/>); the answer is ivf, then ken and kes,
    ///     since the frame appends whatever a kes brings and never replaces a lot in place.
    ///   * A sale: the price goes to the seller's bank, connected or not, and a connected seller
    ///     reads lqn 65 "Banco: + {0} kamas ({{salesHistory::venta}}: {3} {2})", whose "venta"
    ///     opens the sales history; an open sell window loses the lot with a ken.
    ///   * The sales history: las { f3: a laq per lot sold or come back unsold }, the whole of it
    ///     every time -- the frame rebuilds the window's lists from each las. Sent at world entry,
    ///     to a connected seller after each of his lots is sold or runs out, and in answer to lar,
    ///     the empty request the window sends when it opens. How far back it goes is INFERRED.
    ///   * Time: after the marketplace's 672 hours a lot goes back to its seller's bank, as the
    ///     real game sends unsold lots there (text 67), and into his sales history as unsold.
    ///   * Refusals: the client's own texts, lqn type 1 (see <see cref="InfoMessages"/>).
    ///   * The items of a type appearing or going away are not pushed to whoever follows it: no
    ///     capture shows a kda after the first; a new kdk gets the list as it is.
    /// </remarks>
    public static class MarketplaceHandler
    {
        /// <summary>For tests: every message a marketplace sends, by whom it went to.</summary>
        internal static Action<GameSession, string, byte[]>? Sent;

        /// <summary>For tests: what "now" is.</summary>
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;

        public static bool IsOpen => SessionContext.State.Marketplace != null;

        private static Task SendAsync(GameSession? session, string opcode, byte[] body)
        {
            if (session == null) return Task.CompletedTask;
            Sent?.Invoke(session, opcode, body);
            return session.SendAsync(ConnectionProtocol.Push(opcode, body));
        }

        private static long Capacity(GameSession session) => 1000 + 5L * session.State.TotalStrength;

        private static MarketplaceWindow? WindowOf(GameSession session, out Marketplaces.House house)
        {
            house = null!;
            var window = session.State.Marketplace;
            if (window == null || !Marketplaces.TryGet(window.House, out house)) return null;
            return window;
        }

        // ─── Opening and closing ────────────────────────────────────────────────────────────

        /// <summary>A counter clicked: the marketplace opens to buy.</summary>
        public static async Task OpenAsync(int elementId, int skillId)
        {
            var me = SessionContext.Current;
            if (!Marketplaces.TryGetByElement(elementId, out var house))
            {
                Console.WriteLine($"[Marketplaces] Element {elementId} is no counter.");
                return;
            }
            if (me.State.Trade != null || me.State.Commission != null || me.State.IsInFight)
            {
                Console.WriteLine($"[Marketplaces] {me.CharacterId} is busy; {house.Name} not opened.");
                return;
            }

            await SweepExpiredAsync();

            me.State.Marketplace = new MarketplaceWindow { House = house.Id, ElementId = elementId };
            byte[] iwn = ConnectionProtocol.BuildElementInUse(elementId, skillId, me.CharacterId);
            Sent?.Invoke(me, Op.Iwn, iwn);
            await SessionRegistry.BroadcastToMapAsync(me.MapId, ConnectionProtocol.Push(Op.Iwn, iwn));
            await SendAsync(me, Op.Kdw, MarketplaceProtocol.BuildBuyerOpened(house));
            await InventoryAsync(me);
            Console.WriteLine($"[Marketplaces] {me.CharacterId} opens {house.Name} ({house.Id}) at element {elementId}.");
        }

        /// <summary>
        /// iov with no NPC and a marketplace open: its "Vender" (5) or "Comprar" (6) button.
        /// </summary>
        /// <returns>False when it is some NPC's action and not this.</returns>
        public static async Task<bool> ModeAsync(byte[] payload)
        {
            var me = SessionContext.Current;
            var window = WindowOf(me, out var house);
            if (window == null) return false;
            byte[]? iov = ConnectionProtocol.ReadPayload(payload, Op.Iov);
            if (iov == null) return false;
            long action = VarOf(iov, 1), npc = VarOf(iov, 3);
            if (npc != MarketplaceProtocol.NoNpc) return false;
            if (action != MarketplaceProtocol.SellAction && action != MarketplaceProtocol.BuyAction) return false;

            await SwitchAsync(me, window, house, action == MarketplaceProtocol.SellAction);
            return true;
        }

        /// <summary>khd, ivx, hlm, then the window in its new mode, ivx, hlm: E 228-236 and 270-276.</summary>
        private static async Task SwitchAsync(GameSession me, MarketplaceWindow window, Marketplaces.House house, bool selling)
        {
            window.Forget();
            window.Selling = selling;
            await SendAsync(me, Op.Khd, ConnectionProtocol.BuildShopClosed());
            await InventoryAsync(me);
            if (selling) await SendAsync(me, Op.Kby, SellerWindow(me, house));
            else await SendAsync(me, Op.Kdw, MarketplaceProtocol.BuildBuyerOpened(house));
            await InventoryAsync(me);
            Console.WriteLine($"[Marketplaces] {me.CharacterId} switches {house.Name} to {(selling ? "selling" : "buying")}.");
        }

        private static byte[] SellerWindow(GameSession me, Marketplaces.House house)
        {
            var now = Clock();
            var mine = MarketplaceListings.OfAccount(house.Id, AccountOf(me))
                                          .Select(l => (l, l.SecondsLeft(now)));
            return MarketplaceProtocol.BuildSellerOpened(house, mine);
        }

        /// <summary>kla with a marketplace open: khd, ivx, hlm (R 308-311).</summary>
        public static async Task CloseAsync()
        {
            var me = SessionContext.Current;
            me.State.Marketplace = null;
            await SendAsync(me, Op.Khd, ConnectionProtocol.BuildShopClosed());
            await InventoryAsync(me);
        }

        /// <summary>Nothing stays open across a map change.</summary>
        public static void Forget() => SessionContext.State.Marketplace = null;

        private static async Task InventoryAsync(GameSession me)
        {
            await SendAsync(me, Op.Ivx, CommissionHandler.As(me, ConnectionProtocol.BuildInventory));
            await SendAsync(me, Op.Hlm, Array.Empty<byte>());
        }

        // ─── Browsing ───────────────────────────────────────────────────────────────────────

        /// <summary>kdk: follow an item type, answered with what is on sale of it; or stop, unanswered.</summary>
        public static async Task TypeAsync(byte[] payload)
        {
            var me = SessionContext.Current;
            var window = WindowOf(me, out var house);
            byte[]? kdk = ConnectionProtocol.ReadPayload(payload, Op.Kdk);
            if (window == null || kdk == null) return;
            int type = (int)VarOf(kdk, 4);
            if (VarOf(kdk, 2) == 0)
            {
                window.UnfollowType(type);
                return;
            }
            if (!house.Accepts(type)) return;
            window.FollowType(type);
            await SendAsync(me, Op.Kda, MarketplaceProtocol.BuildTypeItems(type, MarketplaceListings.ItemsOf(house.Id, type).ToList()));
        }

        /// <summary>
        /// keh: follow an item, answered with its offers. Stopping is answered with a bare kbt
        /// only when the item was followed: the client sends it twice and E 198-200 answers once.
        /// </summary>
        public static async Task ItemAsync(byte[] payload)
        {
            var me = SessionContext.Current;
            var window = WindowOf(me, out var house);
            byte[]? keh = ConnectionProtocol.ReadPayload(payload, Op.Keh);
            if (window == null || keh == null) return;
            int gid = (int)VarOf(keh, 1);
            int type = Forgemagic.TemplateOf(gid)?.Type ?? 0;
            if (VarOf(keh, 2) == 0)
            {
                if (window.UnfollowItem(gid))
                    await SendAsync(me, Op.Kbt, MarketplaceProtocol.BuildItemOffers(type, gid, Array.Empty<MarketplaceListings.OfferView>()));
                return;
            }
            window.FollowItem(gid);
            // Nothing past its time is shown: a lot can run out while the window is open.
            await SweepExpiredAsync();
            await SendAsync(me, Op.Kbt, MarketplaceProtocol.BuildItemOffers(type, gid, MarketplaceListings.OffersOf(house, gid)));
        }

        /// <summary>kbz: an item's prices for whoever is about to sell it (kcq).</summary>
        public static async Task PriceAsync(byte[] payload)
        {
            var me = SessionContext.Current;
            var window = WindowOf(me, out var house);
            byte[]? kbz = ConnectionProtocol.ReadPayload(payload, Op.Kbz);
            if (window == null || kbz == null) return;
            int gid = (int)VarOf(kbz, 1);
            await SendAsync(me, Op.Kcq, MarketplaceProtocol.BuildSellerPrices(
                gid, MarketplaceListings.AveragePrice(gid), MarketplaceListings.LowestPrices(house, gid)));
        }

        // ─── Buying ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// kbm: the cheapest lot of that size in that offer, if its price is still the one shown.
        /// The kamas leave the buyer, the lot goes into his bag, and its price into the seller's
        /// bank.
        /// </summary>
        public static async Task BuyAsync(byte[] payload)
        {
            var me = SessionContext.Current;
            var window = WindowOf(me, out var house);
            byte[]? kbm = ConnectionProtocol.ReadPayload(payload, Op.Kbm);
            if (window == null || kbm == null || window.Selling) return;
            int offerId = (int)VarOf(kbm, 1);
            long price = VarOf(kbm, 2);
            int lot = (int)VarOf(kbm, 3);
            if (price <= 0 || house.LotIndex(lot) < 0) return;

            if (me.State.Kamas < price)
            {
                await SendAsync(me, Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Warning, InfoMessages.MarketplaceCannotAfford));
                return;
            }

            var now = Clock();
            var bought = MarketplaceListings.Buy(house, offerId, lot, price, me.CharacterId, now, out var refusal);
            if (bought == null)
            {
                Console.WriteLine($"[Marketplaces] {me.CharacterId} cannot buy offer {offerId} x{lot} at {price}: {refusal}.");
                await SendAsync(me, Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Warning, InfoMessages.MarketplaceSoldOut));
                return;
            }
            var (listing, offer) = bought.Value;

            me.State.Kamas -= listing.Price;
            DatabaseManager.SaveCurrentCharacter();
            var received = TradeHandler.Receive(listing.Gid, listing.Quantity, listing.Effects);
            if (received == null)
            {
                me.State.Kamas += listing.Price;
                DatabaseManager.SaveCurrentCharacter();
                MarketplaceListings.Restore(listing, me.CharacterId);
                Console.WriteLine($"[Marketplaces] Listing {listing.Id} could not reach {me.CharacterId}'s bag; back on sale.");
                return;
            }

            await OfferChangedAsync(house, offer, added: false);
            await SendAsync(me, Op.Ivf, ConnectionProtocol.BuildKamas(me.State.Kamas));
            await SendAsync(me, received.Value.Opcode, received.Value.Body);
            await SendAsync(me, Op.Iun, ConnectionProtocol.BuildPods(0, Capacity(me)));
            await SendAsync(me, Op.Lqn, MarketplaceProtocol.BuildPurchaseNotice(
                listing.Gid, received.Value.Uid, listing.Quantity, listing.Price));
            await SendAsync(me, Op.Kcx, MarketplaceProtocol.BuildBought(offer.Id));

            await PaySellerAsync(listing);
            Console.WriteLine($"[Marketplaces] {me.CharacterId} buys {listing.Gid} x{listing.Quantity} for " +
                              $"{listing.Price} from account {listing.SellerAccountId} (listing {listing.Id}).");
        }

        /// <summary>The price of a sold lot into its seller's bank, and a word to him if he is here.</summary>
        private static async Task PaySellerAsync(MarketplaceListings.Listing listing)
        {
            if (!await Bank.AddKamasAsync(listing.SellerAccountId, listing.Price))
            {
                Console.WriteLine($"[Marketplaces] The bank of account {listing.SellerAccountId} refused " +
                                  $"the {listing.Price} kamas of listing {listing.Id}.");
                return;
            }
            var seller = SessionRegistry.FindByCharacter(listing.SellerCharacterId);
            if (seller == null || !seller.IsInWorld) return;
            await SendAsync(seller, Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                InfoMessages.Info, InfoMessages.MarketplaceSoldLinked,
                listing.Price.ToString(), "", listing.Gid.ToString(), listing.Quantity.ToString()));
            await TellSellerAsync(listing);
        }

        // ─── Selling ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// kge: a lot of a stack in the bag goes on sale. The tax is paid now; the lot leaves the
        /// bag and lives in the marketplace until someone buys it or its seller takes it back.
        /// </summary>
        public static async Task SellAsync(byte[] payload)
        {
            var me = SessionContext.Current;
            var window = WindowOf(me, out var house);
            byte[]? kge = ConnectionProtocol.ReadPayload(payload, Op.Kge);
            if (window == null || kge == null || !window.Selling) return;
            long price = VarOf(kge, 1);
            long uid = VarOf(kge, 2);
            int lot = (int)VarOf(kge, 3);

            var item = Equipment.ByUid(uid);
            if (item == null || item.Position != Equipment.Bag || price <= 0 || house.LotIndex(lot) < 0
                || item.Quantity < lot)
            {
                Console.WriteLine($"[Marketplaces] {me.CharacterId} cannot put {uid} x{lot} on sale at {price}.");
                return;
            }

            var template = Forgemagic.TemplateOf(item.Template);
            if (template == null || !house.Accepts(template.Type))
            {
                await SendAsync(me, Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Warning, InfoMessages.MarketplaceWrongCategory));
                return;
            }
            if (house.MaxItemLevel > 0 && template.Level > house.MaxItemLevel)
            {
                Console.WriteLine($"[Marketplaces] {item.Template} is level {template.Level}, over {house.Name}'s {house.MaxItemLevel}.");
                return;
            }

            long account = AccountOf(me);
            if (house.MaxListings > 0 && MarketplaceListings.OfAccount(house.Id, account).Count >= house.MaxListings)
            {
                await SendAsync(me, Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Warning, InfoMessages.MarketplaceTooManyListings));
                return;
            }

            long tax = house.Tax(price);
            if (me.State.Kamas < tax)
            {
                await SendAsync(me, Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Warning, InfoMessages.MarketplaceCannotPayTax));
                return;
            }

            var given = TradeHandler.Give(uid, lot);
            if (given == null) return;
            var added = MarketplaceListings.Add(house, me.CharacterId, account, given.Value.Gid, template.Type,
                                                lot, given.Value.Effects, price, Clock());
            if (added == null)
            {
                // Could not be written: the lot goes back where it was.
                var back = TradeHandler.Receive(given.Value.Gid, lot, given.Value.Effects);
                await SendAsync(me, given.Value.Opcode, given.Value.Body);
                if (back != null) await SendAsync(me, back.Value.Opcode, back.Value.Body);
                return;
            }
            var (listing, offer, isNew) = added.Value;

            me.State.Kamas -= tax;
            DatabaseManager.SaveCurrentCharacter();

            await SendAsync(me, Op.Ivf, ConnectionProtocol.BuildKamas(me.State.Kamas));
            await SendAsync(me, Op.Kes, MarketplaceProtocol.BuildListed(listing, listing.SecondsLeft(Clock())));
            await OfferChangedAsync(house, offer, added: isNew);
            await SendAsync(me, given.Value.Opcode, given.Value.Body);
            await SendAsync(me, Op.Iun, ConnectionProtocol.BuildPods(0, Capacity(me)));
            Console.WriteLine($"[Marketplaces] {me.CharacterId} puts {listing.Gid} x{lot} on sale in {house.Name} " +
                              $"for {price} (tax {tax}), listing {listing.Id}.");
        }

        /// <summary>
        /// kcr in sell mode on one of the account's listings: the lot comes back to the bag.
        /// INFERRED, see the remarks.
        /// </summary>
        /// <returns>False when no marketplace is open to sell, so that the kcr goes elsewhere.</returns>
        public static async Task<bool> WithdrawAsync(byte[] payload)
        {
            var me = SessionContext.Current;
            var window = WindowOf(me, out var house);
            if (window == null || !window.Selling) return false;
            byte[]? kcr = ConnectionProtocol.ReadPayload(payload, Op.Kcr);
            if (kcr == null) return true;
            long id = VarOf(kcr, 2);
            if (id <= 0 || id > int.MaxValue) return true;

            var taken = MarketplaceListings.Withdraw(house, AccountOf(me), (int)id);
            if (taken == null)
            {
                Console.WriteLine($"[Marketplaces] {me.CharacterId} cannot take back listing {id}.");
                return true;
            }
            var (listing, offer) = taken.Value;

            var received = TradeHandler.Receive(listing.Gid, listing.Quantity, listing.Effects);
            if (received == null)
            {
                Console.WriteLine($"[Marketplaces] Listing {listing.Id} could not reach {me.CharacterId}'s bag.");
                await ReturnAsync(listing);
            }
            else
            {
                await SendAsync(me, received.Value.Opcode, received.Value.Body);
                await SendAsync(me, Op.Iun, ConnectionProtocol.BuildPods(0, Capacity(me)));
            }
            await OfferChangedAsync(house, offer, added: false);
            await SendAsync(me, Op.Ken, MarketplaceProtocol.BuildSellerRemoved(listing.Id));
            Console.WriteLine($"[Marketplaces] {me.CharacterId} takes back listing {listing.Id} ({listing.Gid} x{listing.Quantity}).");
            return true;
        }

        /// <summary>
        /// kch: a new price for one of the seller's lots, the tax paid now. See the remarks: read
        /// off the client, no capture changes a price.
        /// </summary>
        public static async Task ChangePriceAsync(byte[] payload)
        {
            var me = SessionContext.Current;
            var window = WindowOf(me, out var house);
            byte[]? kch = ConnectionProtocol.ReadPayload(payload, Op.Kch);
            if (window == null || kch == null || !window.Selling) return;
            long id = VarOf(kch, 1);
            long price = VarOf(kch, 2);
            int lot = (int)VarOf(kch, 3);
            if (id <= 0 || id > int.MaxValue || price <= 0) return;

            long account = AccountOf(me);
            var listing = MarketplaceListings.OfAccount(house.Id, account).FirstOrDefault(l => l.Id == id);
            if (listing == null || (lot > 0 && lot != listing.Quantity) || listing.Price == price)
            {
                Console.WriteLine($"[Marketplaces] {me.CharacterId} cannot price listing {id} x{lot} at {price}.");
                return;
            }

            long tax = house.ModificationTax(listing.Price, price);
            if (me.State.Kamas < tax)
            {
                await SendAsync(me, Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Warning, InfoMessages.MarketplaceCannotPayTax));
                return;
            }

            var changed = MarketplaceListings.ChangePrice(house, account, listing.Id, price);
            if (changed == null) return;
            var (before, after, offer) = changed.Value;

            me.State.Kamas -= tax;
            DatabaseManager.SaveCurrentCharacter();

            await SendAsync(me, Op.Ivf, ConnectionProtocol.BuildKamas(me.State.Kamas));
            await SendAsync(me, Op.Ken, MarketplaceProtocol.BuildSellerRemoved(after.Id));
            await SendAsync(me, Op.Kes, MarketplaceProtocol.BuildListed(after, after.SecondsLeft(Clock())));
            await OfferChangedAsync(house, offer, added: false);
            Console.WriteLine($"[Marketplaces] {me.CharacterId} prices listing {after.Id} ({after.Gid} x{after.Quantity}) " +
                              $"at {price}, was {before.Price} (tax {tax}).");
        }

        // ─── The sales history ──────────────────────────────────────────────────────────────

        /// <summary>lar: the sales history window opened, and gets the account's history.</summary>
        public static async Task SalesHistoryAsync()
        {
            var me = SessionContext.Current;
            await SendAsync(me, Op.Las, SalesHistoryOf(AccountOf(me)));
        }

        /// <summary>An account's sales history as las carries it, as far back as it is kept.</summary>
        public static byte[] SalesHistoryOf(long accountId)
            => MarketplaceProtocol.BuildSalesHistory(
                MarketplaceListings.HistoryOf(accountId, Clock() - MarketplaceListings.HistoryDepth));

        /// <summary>The las a character is given at world entry: null when there is nothing in it.</summary>
        public static byte[]? SalesHistoryAtEntry(long characterId, long sessionAccountId)
        {
            long account = DatabaseManager.AccountIdOfCharacter(characterId);
            if (account <= 0) account = sessionAccountId;
            var lines = MarketplaceListings.HistoryOf(account, Clock() - MarketplaceListings.HistoryDepth);
            return lines.Count == 0 ? null : MarketplaceProtocol.BuildSalesHistory(lines);
        }

        /// <summary>
        /// One of a seller's lots left the book while he is here: an open sell window of that
        /// marketplace loses it (ken), and his sales history is sent again.
        /// </summary>
        private static async Task TellSellerAsync(MarketplaceListings.Listing listing)
        {
            var seller = SessionRegistry.FindByCharacter(listing.SellerCharacterId);
            if (seller == null || !seller.IsInWorld) return;
            var window = seller.State.Marketplace;
            if (window != null && window.Selling && window.House == listing.House)
                await SendAsync(seller, Op.Ken, MarketplaceProtocol.BuildSellerRemoved(listing.Id));
            await SendAsync(seller, Op.Las, SalesHistoryOf(listing.SellerAccountId));
        }

        // ─── Everybody else ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// An offer changed: whoever follows its item in that marketplace is told -- kgv when it is
        /// gone, kfi when it is new, and kgp with its prices otherwise and after a kfi (E 256-257).
        /// </summary>
        private static async Task OfferChangedAsync(Marketplaces.House house, MarketplaceListings.OfferView offer, bool added)
        {
            foreach (var session in SessionRegistry.InWorld())
            {
                var window = session.State.Marketplace;
                if (window == null || window.House != house.Id || !window.Follows(offer.Gid)) continue;
                if (offer.Gone)
                {
                    await SendAsync(session, Op.Kgv, MarketplaceProtocol.BuildOfferRemoved(offer.Gid, offer.ItemType, offer.Id));
                    continue;
                }
                if (added) await SendAsync(session, Op.Kfi, MarketplaceProtocol.BuildOfferAdded(offer));
                await SendAsync(session, Op.Kgp, MarketplaceProtocol.BuildOfferUpdated(offer));
            }
        }

        /// <summary>
        /// The lots whose time on sale is over go back to their sellers, into the account's bank, as
        /// the game sends unsold lots there; to the bag only if the bank cannot take them.
        /// </summary>
        public static async Task<int> SweepExpiredAsync()
        {
            var expired = MarketplaceListings.TakeExpired(Clock());
            foreach (var (listing, offer) in expired)
            {
                await ReturnAsync(listing);
                if (Marketplaces.TryGet(listing.House, out var house)) await OfferChangedAsync(house, offer, added: false);
                await TellSellerAsync(listing);
                Console.WriteLine($"[Marketplaces] Listing {listing.Id} ({listing.Gid} x{listing.Quantity}) ran out of " +
                                  $"time; back to the bank of account {listing.SellerAccountId}.");
            }
            return expired.Count;
        }

        /// <summary>A lot back to its seller, whether he is connected or not: his bank first.</summary>
        private static async Task ReturnAsync(MarketplaceListings.Listing listing)
        {
            if (await Bank.AddItemAsync(listing.SellerAccountId, listing.Gid, listing.Quantity, listing.Effects)) return;

            var seller = SessionRegistry.FindByCharacter(listing.SellerCharacterId);
            if (seller != null && seller.IsInWorld)
            {
                var received = CommissionHandler.As(seller, () => TradeHandler.Receive(listing.Gid, listing.Quantity, listing.Effects));
                if (received != null)
                {
                    await SendAsync(seller, received.Value.Opcode, received.Value.Body);
                    await SendAsync(seller, Op.Iun, ConnectionProtocol.BuildPods(0, Capacity(seller)));
                    return;
                }
            }
            long uid = DatabaseManager.NextItemUid();
            if (!DatabaseManager.InsertCharacterItem(uid, listing.SellerCharacterId, listing.Gid, listing.Quantity,
                                                     Equipment.Bag, listing.Effects))
                Console.WriteLine($"[Marketplaces] Listing {listing.Id} could not be given back to {listing.SellerCharacterId}.");
        }

        /// <summary>The seller's account: Characters.AccountId, the session's when the row is missing.</summary>
        private static long AccountOf(GameSession session)
        {
            long account = DatabaseManager.AccountIdOfCharacter(session.CharacterId);
            return account > 0 ? account : session.AccountId;
        }

        private static long VarOf(byte[] body, int field)
        {
            foreach (var f in ProtoMessage.Parse(body).Fields)
                if (f.FieldNumber == field && f.WireType == 0) return f.VarIntValue;
            return 0;
        }
    }
}
