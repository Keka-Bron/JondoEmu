using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// A simulated player's equipment: what it should wear (<see cref="Outfitter"/>), bought at the
    /// marketplace when it is on sale, crafted at a workshop when it is not, put on, and what it no
    /// longer wears sold, with what it gathered and looted. All of it over its socket, at the
    /// counters and benches of the world, as a player does it.
    /// </summary>
    /// <remarks>
    /// Asked for on 2026-11-08. A bot is born a craftsman of the equipment trades at its own level,
    /// as it is born at a level drawn for it (<see cref="RaiseTradesOffline"/>), so that it can make
    /// what it wears. What a recipe needs it buys at the marketplace when someone sells it -- a
    /// player's lots among them --; what nobody sells it brings, paying for it what the item is worth,
    /// as the resources it gathered and looted while nobody was looking.
    /// </remarks>
    internal sealed partial class SimulatedPlayer
    {
        public const int EquipmentHouse = 1262, ResourceHouse = 1261, ConsumableHouse = 1263;

        /// <summary>The trades of what the bots wear: jeweller, shoemaker, tailor, smith, carver and the shield maker.</summary>
        internal static readonly int[] Trades = { 16, 15, 27, 11, 13, 60 };

        /// <summary>When it next looks at what it wears; at once after a level.</summary>
        private DateTime _nextGearAt = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        private int _gearedLevel;

        /// <summary>The most pieces it crafts on one errand, and lots it puts on sale.</summary>
        private const int CraftsPerErrand = 9, SalesPerErrand = 6;

        private Outfitter.Element MyElement => (Outfitter.Element)(_number % Elements.Length);

        /// <summary>Its trades raised to its level before it comes in, the way its level was drawn.</summary>
        internal static void RaiseTradesOffline(long character, int level)
        {
            var jobs = DatabaseManager.LoadJobExperience(character);
            long floor = JobExperience.Floor(Math.Clamp(level, 1, JobExperience.MaxLevel));
            foreach (int job in Trades)
            {
                if (jobs.TryGetValue(job, out long had) && had >= floor) continue;
                DatabaseManager.SaveJobExperience(character, job, floor);
            }
        }

        // ─── The errand ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Now and then, and after every level: the outfit of its level, what is missing of it bought
        /// or crafted, put on, the rest sold, and back where it was.
        /// </summary>
        private async Task<bool> GearUpAsync(GameSession session, CancellationToken stop)
        {
            int level = session.State.CharacterLevel;
            if (DateTime.UtcNow < _nextGearAt && level == _gearedLevel) return false;
            _nextGearAt = DateTime.UtcNow + TimeSpan.FromMinutes(_dice.Next(20, 45));
            _gearedLevel = level;

            try
            {
                await ErrandAsync(session, level, stop);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SimulatedPlayers.Journal($"{_name}'s errand broke: {ex}");
            }
            return true;
        }

        private async Task ErrandAsync(GameSession session, int level, CancellationToken stop)
        {
            var plan = PlanFor(session);
            var missing = plan.Slots.Where(s => WornIn(session, s.Key)?.Template != s.Value.Gid
                                                && InBag(session, s.Value.Gid) == null)
                                    .ToList();
            long origin = session.State.MapId;
            long kamas = session.State.Kamas;

            // What is on sale, bought; the rest crafted.
            var buy = missing.Where(m => CheapestOffer(EquipmentHouse, m.Value.Gid, 1, Budget(session)) != null).ToList();
            if (buy.Count > 0) await AtCounterAsync(session, EquipmentHouse, () => BuyEachAsync(session, buy.Select(b => b.Value.Gid)), stop);
            var craft = missing.Where(m => InBag(session, m.Value.Gid) == null && RecipeManager.TryGetByResult(m.Value.Gid, out var r)
                                           && CostToBring(session, r) <= Budget(session))
                               .Take(CraftsPerErrand).ToList();
            if (craft.Count > 0) await CraftEachAsync(session, craft.Select(c => c.Value.Gid).ToList(), stop);

            await PutOnAsync(session, plan);
            int listed = await SellAsync(session, plan, stop);

            if (session.State.MapId != origin && !session.State.IsInFight)
            {
                if (!await GoToAsync(session, origin, stop)) await GoToAsync(session, m => AreaOf(m) == _range, stop);
            }
            int worn = plan.Slots.Count(s => WornIn(session, s.Key)?.Template == s.Value.Gid);
            SimulatedPlayers.Journal($"{_name} (level {level}, {MyElement}): wears {worn} of the {plan.Slots.Count} planned pieces; " +
                                     $"{buy.Count} to buy, {craft.Count} to craft, {listed} lot(s) put on sale; " +
                                     $"kamas {kamas} -> {session.State.Kamas}.");
        }

        /// <summary>
        /// What it should wear: the plan of its level and element over what it can craft -- its
        /// trades reach the recipe -- and what is on sale within its means.
        /// </summary>
        private Outfitter.Plan PlanFor(GameSession session)
        {
            int level = session.State.CharacterLevel;
            var pool = new List<Outfitter.Piece>();
            foreach (var piece in Outfitter.Craftable.Value)
            {
                if (piece.Level > level || !RecipeManager.TryGetByResult(piece.Gid, out var recipe)) continue;
                if (session.State.JobLevel(recipe.JobId) >= recipe.ResultLevel) pool.Add(piece);
            }
            long budget = Budget(session);
            if (Marketplaces.TryGet(EquipmentHouse, out var house))
            {
                foreach (int type in Outfitter.WornTypes)
                    foreach (int gid in MarketplaceListings.ItemsOf(EquipmentHouse, type))
                        if (Outfitter.PieceOf(gid) is { } piece && piece.Level <= level
                            && CheapestOffer(EquipmentHouse, gid, 1, budget) != null)
                            pool.Add(piece);
            }
            // What it already has counts, whatever its roll.
            foreach (var item in Bag(session))
                if (Outfitter.PieceOf(item.Template) is { } piece && piece.Level <= level) pool.Add(piece);
            return Outfitter.Best(level, MyElement, pool);
        }

        /// <summary>What it spends at most on one thing: a third of its kamas.</summary>
        private static long Budget(GameSession session) => Math.Max(0, session.State.Kamas / 3);

        // ─── What it has ─────────────────────────────────────────────────────────────────────

        /// <summary>Its items, read off the server as a client reads its inventory.</summary>
        private static List<Equipment.Item> Items(GameSession session)
        {
            for (int tries = 0; tries < 3; tries++)
            {
                try { return session.State.EquipmentItems.Values.ToList(); }
                catch (InvalidOperationException) { }
            }
            return new List<Equipment.Item>();
        }

        private static IEnumerable<Equipment.Item> Bag(GameSession session)
            => Items(session).Where(i => i.Position == Equipment.Bag);

        private static Equipment.Item? InBag(GameSession session, int gid)
            => Bag(session).FirstOrDefault(i => i.Template == gid);

        private static Equipment.Item? WornIn(GameSession session, int slot)
            => Items(session).FirstOrDefault(i => i.Position == slot);

        private static int HowMany(GameSession session, int gid) => Bag(session).Where(i => i.Template == gid).Sum(i => i.Quantity);

        /// <summary>Puts the plan on: each planned piece from the bag to its slot (iuk).</summary>
        private async Task PutOnAsync(GameSession session, Outfitter.Plan plan)
        {
            foreach (var (slot, piece) in plan.Slots)
            {
                if (WornIn(session, slot)?.Template == piece.Gid) continue;
                var item = InBag(session, piece.Gid);
                if (item == null) continue;
                await SendAsync(Op.Iuk, Pb.New().Var(1, 1).Var(2, item.Uid).Var(3, slot).Build());
                await Task.Delay(400);
            }
        }

        // ─── The marketplace ─────────────────────────────────────────────────────────────────

        /// <summary>A marketplace's counter on a map: its element and its skill's instance.</summary>
        private static (Interactives.Element Element, int Instance)? CounterOn(long map, int house)
        {
            foreach (var interactive in InteractiveRegistry.OnMap(map))
                foreach (var action in interactive.Actions)
                    if (action.Kind == InteractiveActionKind.Marketplace
                        && Marketplaces.TryGetByElement(interactive.Element.Id, out var h) && h.Id == house)
                        return (interactive.Element, action.SkillInstanceId);
            return null;
        }

        /// <summary>To the nearest counter of a marketplace, opened (iwo), the work done, and closed (kla).</summary>
        private async Task<bool> AtCounterAsync(GameSession session, int house, Func<Task> work, CancellationToken stop)
        {
            if (!await GoToAsync(session, m => CounterOn(m, house) != null, stop))
            {
                SimulatedPlayers.Journal($"{_name} found no way from {session.State.MapId} to a counter of marketplace {house}.");
                return false;
            }
            var counter = CounterOn(session.State.MapId, house);
            if (counter == null) return false;
            await WalkNextToAsync(session, counter.Value.Element.Cell, stop);
            await SendAsync(Op.Iwo, Pb.New().Var(1, counter.Value.Instance).Var(2, counter.Value.Element.Id).Build());
            for (int waited = 0; waited < 30 && session.State.Marketplace == null; waited++) await Task.Delay(100, stop);
            if (session.State.Marketplace == null)
            {
                SimulatedPlayers.Journal($"{_name}: the counter of marketplace {house} on {session.State.MapId} did not open.");
                return false;
            }
            try { await work(); }
            finally
            {
                await SendAsync(Op.Kla);
                await Task.Delay(300, stop);
            }
            return true;
        }

        /// <summary>The cheapest offer of an item in lots of a size, within a price; null when there is none.</summary>
        private static (int Offer, long Price)? CheapestOffer(int houseId, int gid, int lot, long most)
        {
            if (!Marketplaces.TryGet(houseId, out var house)) return null;
            int index = house.LotIndex(lot);
            if (index < 0) return null;
            (int, long)? best = null;
            foreach (var offer in MarketplaceListings.OffersOf(house, gid))
            {
                if (offer.Gone || index >= offer.Prices.Count) continue;
                long price = offer.Prices[index];
                if (price <= 0 || price > most) continue;
                if (best == null || price < best.Value.Item2) best = (offer.Id, price);
            }
            return best;
        }

        /// <summary>Each of these items bought, a lot of one, the cheapest (kbm).</summary>
        private async Task BuyEachAsync(GameSession session, IEnumerable<int> gids)
        {
            foreach (int gid in gids)
            {
                var offer = CheapestOffer(EquipmentHouse, gid, 1, Budget(session));
                if (offer == null) continue;
                await SendAsync(Op.Kbm, Pb.New().Var(1, offer.Value.Offer).Var(2, offer.Value.Price).Var(3, 1).Build());
                await Task.Delay(500);
            }
        }

        /// <summary>
        /// So many of an item bought where it is sold, lot by lot, each lot the largest that does not
        /// go over what is wanted, or the smallest there is; what is still missing is returned.
        /// </summary>
        private async Task<int> BuyLotsAsync(GameSession session, int houseId, int gid, int wanted)
        {
            if (!Marketplaces.TryGet(houseId, out var house)) return wanted;
            for (int bought = 0; wanted > 0 && bought < 10; bought++)
            {
                long most = Math.Max(0, Budget(session) / 4);
                var lots = house.Lots.OrderByDescending(l => l).ToList();
                (int Offer, long Price)? offer = null;
                int size = 0;
                foreach (int lot in lots.Where(l => l <= wanted).Concat(lots.Where(l => l > wanted).OrderBy(l => l)))
                {
                    offer = CheapestOffer(houseId, gid, lot, most);
                    if (offer != null) { size = lot; break; }
                }
                if (offer == null) break;
                int had = HowMany(session, gid);
                await SendAsync(Op.Kbm, Pb.New().Var(1, offer.Value.Offer).Var(2, offer.Value.Price).Var(3, size).Build());
                await Task.Delay(500);
                int got = HowMany(session, gid) - had;
                if (got <= 0) break;
                wanted -= got;
            }
            return Math.Max(0, wanted);
        }

        /// <summary>The marketplace that takes an item of this type, among the three of Astrub.</summary>
        private static int HouseFor(int gid)
        {
            int type = Forgemagic.TemplateOf(gid)?.Type ?? -1;
            foreach (int id in new[] { ResourceHouse, ConsumableHouse, EquipmentHouse })
                if (Marketplaces.TryGet(id, out var house) && house.Accepts(type)) return id;
            return 0;
        }

        // ─── Crafting ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// These pieces crafted: their ingredients bought where they are sold and brought when they
        /// are not, then each at a workshop of its recipe's skill.
        /// </summary>
        private async Task CraftEachAsync(GameSession session, List<int> gids, CancellationToken stop)
        {
            var recipes = gids.Select(g => RecipeManager.TryGetByResult(g, out var r) ? r : null)
                              .Where(r => r != null).Cast<RecipeDefinition>().ToList();
            var needed = new Dictionary<int, int>();
            foreach (var recipe in recipes)
                foreach (var ingredient in recipe.Ingredients)
                    needed[ingredient.ItemId] = needed.GetValueOrDefault(ingredient.ItemId) + ingredient.Quantity;

            var missing = needed.ToDictionary(n => n.Key, n => Math.Max(0, n.Value - HowMany(session, n.Key)))
                                .Where(n => n.Value > 0).ToDictionary(n => n.Key, n => n.Value);
            foreach (var house in missing.Keys.GroupBy(HouseFor).Where(g => g.Key != 0))
            {
                await AtCounterAsync(session, house.Key, async () =>
                {
                    foreach (int gid in house) missing[gid] = await BuyLotsAsync(session, house.Key, gid, missing[gid]);
                }, stop);
            }
            await BringAsync(session, missing.Where(m => m.Value > 0));

            foreach (var bySkill in recipes.GroupBy(r => r.SkillId))
            {
                await AtBenchAsync(session, bySkill.Key, async () =>
                {
                    foreach (var recipe in bySkill)
                    {
                        if (recipe.Ingredients.Any(i => HowMany(session, i.ItemId) < i.Quantity))
                        {
                            SimulatedPlayers.Journal($"{_name} lacks the ingredients of {recipe.ResultId}.");
                            continue;
                        }
                        int had = HowMany(session, recipe.ResultId);
                        await SendAsync(Op.Kew, Pb.New().Var(2, recipe.ResultId).Build());
                        await Task.Delay(400);
                        await SendAsync(Op.Kep, Pb.New().Var(1, 1).Build());
                        await Task.Delay(900);
                        if (HowMany(session, recipe.ResultId) <= had)
                            SimulatedPlayers.Journal($"{_name} could not craft {recipe.ResultId} (recipe level {recipe.ResultLevel}, " +
                                                     $"job {recipe.JobId} at {session.State.JobLevel(recipe.JobId)}).");
                    }
                }, stop);
            }
        }

        /// <summary>What the ingredients of a recipe it lacks would cost it to bring, at their own price.</summary>
        private static long CostToBring(GameSession session, RecipeDefinition recipe)
            => recipe.Ingredients.Sum(i => Math.Max(0, i.Quantity - HowMany(session, i.ItemId)) * Outfitter.BasePrice(i.ItemId));

        /// <summary>
        /// What nobody sells, brought into the bag: the resources it gathered and looted while
        /// nobody was looking. It pays for them what they are worth, so they are not free.
        /// </summary>
        private async Task BringAsync(GameSession session, IEnumerable<KeyValuePair<int, int>> items)
        {
            var stream = session.Stream;
            if (stream == null) return;
            using (SessionContext.Push(session))
            {
                foreach (var (gid, quantity) in items)
                {
                    if (!await Equipment.GiveAsync(stream, gid, quantity)) continue;
                    session.State.Kamas = Math.Max(0, session.State.Kamas - Outfitter.BasePrice(gid) * quantity);
                }
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(session.State.Kamas)));
                DatabaseManager.SaveCurrentCharacter();
            }
        }

        /// <summary>A workshop's bench of a skill on a map: its element and its skill's instance.</summary>
        private static (Interactives.Element Element, int Instance)? BenchOn(long map, int skill)
        {
            foreach (var interactive in InteractiveRegistry.OnMap(map))
                foreach (var action in interactive.Actions)
                    if (action.Kind == InteractiveActionKind.Workshop && action.SkillId == skill)
                        return (interactive.Element, action.SkillInstanceId);
            return null;
        }

        /// <summary>To the nearest bench of a skill, opened (iwo), the work done, and closed (kla).</summary>
        private async Task<bool> AtBenchAsync(GameSession session, int skill, Func<Task> work, CancellationToken stop)
        {
            if (!await GoToAsync(session, m => BenchOn(m, skill) != null, stop))
            {
                SimulatedPlayers.Journal($"{_name} found no way from {session.State.MapId} to a bench of skill {skill}.");
                return false;
            }
            var bench = BenchOn(session.State.MapId, skill);
            if (bench == null) return false;
            await WalkNextToAsync(session, bench.Value.Element.Cell, stop);
            await SendAsync(Op.Iwo, Pb.New().Var(1, bench.Value.Instance).Var(2, bench.Value.Element.Id).Build());
            for (int waited = 0; waited < 30 && session.State.Workshop == null; waited++) await Task.Delay(100, stop);
            if (session.State.Workshop == null)
            {
                SimulatedPlayers.Journal($"{_name}: the bench of skill {skill} on {session.State.MapId} did not open.");
                return false;
            }
            try { await work(); }
            finally
            {
                await SendAsync(Op.Kla);
                await Task.Delay(300, stop);
            }
            return true;
        }

        // ─── Selling ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What an item is worth to it, a unit: what it sells for here, or what its ingredients are
        /// worth and a little more, or the price of its template; ten kamas a level at the least.
        /// </summary>
        internal static long ValueOf(int gid, int depth = 0)
        {
            long average = MarketplaceListings.AveragePrice(gid);
            if (average > 0) return average;
            int level = Forgemagic.TemplateOf(gid)?.Level ?? 1;
            long floor = Math.Max(1, 10L * level);
            if (depth < 2 && RecipeManager.TryGetByResult(gid, out var recipe))
                return Math.Max(floor, recipe.Ingredients.Sum(i => ValueOf(i.ItemId, depth + 1) * i.Quantity) * 11 / 10);
            return Math.Max(depth == 0 ? floor : 1, 2 * Outfitter.BasePrice(gid));
        }

        /// <summary>
        /// What it no longer wears, and the resources it has ten or more of, put on sale: at the
        /// equipment counter the one, at the resources counter the other (iov «Vender», then kge).
        /// </summary>
        private async Task<int> SellAsync(GameSession session, Outfitter.Plan plan, CancellationToken stop)
        {
            int before = MarketplaceListings.OfAccount(EquipmentHouse, _accountId).Count
                         + MarketplaceListings.OfAccount(ResourceHouse, _accountId).Count;
            var planned = plan.Slots.Values.Select(p => p.Gid).ToHashSet();
            var gear = Bag(session).Where(i => Outfitter.SlotsOf(Forgemagic.TemplateOf(i.Template)?.Type ?? -1).Count > 0
                                               && !planned.Contains(i.Template))
                                   .Take(SalesPerErrand).Select(i => (i.Uid, i.Template, Lot: 1)).ToList();
            if (gear.Count > 0) await AtCounterAsync(session, EquipmentHouse, () => ListAsync(session, EquipmentHouse, gear), stop);

            var resources = new List<(long Uid, int Template, int Lot)>();
            if (Marketplaces.TryGet(ResourceHouse, out var house))
            {
                foreach (var stack in Bag(session).Where(i => i.Quantity >= 10
                                                             && house.Accepts(Forgemagic.TemplateOf(i.Template)?.Type ?? -1)))
                {
                    if (resources.Count >= SalesPerErrand) break;
                    resources.Add((stack.Uid, stack.Template, 10));
                }
            }
            if (resources.Count > 0) await AtCounterAsync(session, ResourceHouse, () => ListAsync(session, ResourceHouse, resources), stop);
            return MarketplaceListings.OfAccount(EquipmentHouse, _accountId).Count
                   + MarketplaceListings.OfAccount(ResourceHouse, _accountId).Count - before;
        }

        /// <summary>The marketplace to selling, and these lots on sale at what they are worth.</summary>
        private async Task ListAsync(GameSession session, int houseId, List<(long Uid, int Template, int Lot)> lots)
        {
            if (!Marketplaces.TryGet(houseId, out var house)) return;
            await SendAsync(Op.Iov, Pb.New().Var(1, MarketplaceProtocol.SellAction).Var(3, MarketplaceProtocol.NoNpc).Build());
            await Task.Delay(500);
            foreach (var (uid, template, lot) in lots)
            {
                if (house.MaxListings > 0 && MarketplaceListings.OfAccount(houseId, _accountId).Count >= house.MaxListings) break;
                long price = ValueOf(template) * lot;
                if (house.Tax(price) > session.State.Kamas) continue;
                await SendAsync(Op.Kge, Pb.New().Var(1, price).Var(2, uid).Var(3, lot).Build());
                await Task.Delay(400);
            }
        }
    }
}
