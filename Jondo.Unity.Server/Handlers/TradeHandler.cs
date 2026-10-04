using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>A trade between two players, from the asking to the window closing.</summary>
    public sealed class Trade
    {
        public long SourceId { get; init; }
        public long TargetId { get; init; }
        public bool Accepted { get; set; }

        /// <summary>What each side lays down: its stacks and how many of each, in the order laid.</summary>
        public Dictionary<long, List<(long Uid, int Quantity)>> Offers { get; } = new();

        /// <summary>The kamas each side puts in.</summary>
        public Dictionary<long, long> Kamas { get; } = new();

        /// <summary>Who has pressed "ready" on what is on the table now.</summary>
        public HashSet<long> Ready { get; } = new();

        public bool Ended { get; set; }

        /// <summary>One of the two at a time: both write here, each from their own connection.</summary>
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        public long Other(long me) => me == SourceId ? TargetId : SourceId;

        public List<(long Uid, int Quantity)> OfferOf(long who)
        {
            if (!Offers.TryGetValue(who, out var offer)) Offers[who] = offer = new List<(long, int)>();
            return offer;
        }

        public long KamasOf(long who) => Kamas.TryGetValue(who, out long k) ? k : 0;
    }

    /// <summary>
    /// Trading with another player: asking, accepting or refusing, laying down stacks and kamas,
    /// ready, and the goods changing hands.
    /// </summary>
    /// <remarks>
    /// ─── What is measured ─────────────────────────────────────────────────────────────────
    ///
    /// Intercambio/ holds both sides: "intercambio completo mandando peticion a otro-exito" asks,
    /// "peticion recibida de intercambio rechazada-luego peticion recibida aceptada e intercambio
    /// completo exitoso" is asked, refuses once and accepts the second time.
    ///
    ///   ask      C keu { f2: whom }             S kfz { f1: asker, f2: asked, f4: 1 } to both
    ///   refuse   C kla                          S khd { f3: 11 }
    ///   accept   C kgi                          S kbg { both sides' pods, f6 asker, f7 asked }
    ///   lay      C kcr { f1: how many, f2: uid } S kfb { f1 { f1: 63, f5: item }, f2: the other's }
    ///   kamas    C kee { f1: kamas }            S ket { f1: kamas, f3: the other's }
    ///   ready    C kep { f1: 1, f2: step }      S kgt { f3: 1, f4: who } -- the first to be ready
    ///   done     when the second is ready, in this order, each seeing what is theirs:
    ///            the asker's stacks change hands, then the asked's -- ium (a whole stack gone) or
    ///            ivj (what is left of it) to the one giving; iua (a new stack, a NEW uid) or ivj
    ///            (stacked onto one of the same) to the one receiving -- then kgt "not ready" for
    ///            the asker and the asked, keq for the asker's pods and the asked's (f2 on the
    ///            other one's) with iun behind one's own, and khd { f1: 1, f3: 11 }.
    ///
    /// Not captured, and so inferred: taking a stack back (kfs, the workshop's "leaves" message,
    /// the same family as the kfb both use), changing how many (kfb again with the new count),
    /// the kamas changing hands (both captures traded 5,000 for 5,000; ivf, as a commission's
    /// payment), and a change on the table taking back both "ready"s (the game's rule). The ihv
    /// and lqn 549 of the captures -- the item leaving the owner's equipment presets -- are left
    /// out: this server keeps no presets.
    /// </remarks>
    public static class TradeHandler
    {
        private static Task SendAsync(GameSession? session, string opcode, byte[] body)
            => session == null ? Task.CompletedTask : session.SendAsync(ConnectionProtocol.Push(opcode, body));

        private static Task SendBothAsync(GameSession? a, GameSession? b, string opcode, byte[] body)
            => Task.WhenAll(SendAsync(a, opcode, body), SendAsync(b, opcode, body));

        /// <summary>What a character can carry: a thousand, and five a point of strength.</summary>
        private static long Capacity(GameSession session)
            => CommissionHandler.As(session, () => 1000 + 5L * session.State.TotalStrength);

        // ─── Asking ─────────────────────────────────────────────────────────────────────────

        /// <summary>keu: asking someone on the map to trade.</summary>
        public static async Task RequestAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? keu = ConnectionProtocol.ReadPayload(payload, Op.Keu);
            if (keu == null) return;
            long target = VarOf(keu, 2);

            var me = SessionContext.Current;
            var other = SessionRegistry.FindByCharacter(target);
            if (other == null || !other.IsInWorld || target == me.CharacterId || other.MapId != me.MapId)
            {
                Console.WriteLine($"[Trade] {me.CharacterId} asks {target}, who is not on this map.");
                return;
            }
            if (Busy(me) || Busy(other))
            {
                Console.WriteLine($"[Trade] {me.CharacterId} asks {target}: one of the two is busy.");
                return;
            }

            var trade = new Trade { SourceId = me.CharacterId, TargetId = target };
            me.State.Trade = trade;
            other.State.Trade = trade;

            byte[] kfz = TradeProtocol.BuildRequested(me.CharacterId, target);
            await SendBothAsync(me, other, Op.Kfz, kfz);
            Console.WriteLine($"[Trade] {me.CharacterId} asks {target} to trade.");
        }

        private static bool Busy(GameSession session)
            => session.State.Trade != null || session.State.Commission != null || session.State.IsInFight;

        /// <summary>kgi: the one asked accepts.</summary>
        /// <returns>False when no trade waits for this character.</returns>
        public static async Task<bool> AcceptAsync()
        {
            var me = SessionContext.Current;
            var trade = me.State.Trade;
            if (trade == null) return false;
            if (trade.Accepted || trade.TargetId != me.CharacterId) return true;

            var source = SessionRegistry.FindByCharacter(trade.SourceId);
            if (source == null || source.MapId != me.MapId)
            {
                await EndAsync(trade, done: false);
                return true;
            }

            trade.Accepted = true;
            byte[] kbg = TradeProtocol.BuildStarted(source.CharacterId, Capacity(source), 0,
                                                    me.CharacterId, Capacity(me), 0);
            await SendBothAsync(source, me, Op.Kbg, kbg);
            Console.WriteLine($"[Trade] {me.CharacterId} accepts the trade with {source.CharacterId}.");
            return true;
        }

        // ─── The table ──────────────────────────────────────────────────────────────────────

        /// <summary>kcr in a trade: a stack laid down, more or fewer of it, or taken back.</summary>
        /// <returns>False when this character is in no open trade.</returns>
        public static async Task<bool> MoveAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var trade = me.State.Trade;
            if (trade == null || !trade.Accepted) return false;
            byte[]? kcr = ConnectionProtocol.ReadPayload(payload, Op.Kcr);
            if (kcr == null) return true;
            int delta = (int)VarOf(kcr, 1);
            long uid = VarOf(kcr, 2);

            await trade.Gate.WaitAsync();
            try
            {
                if (trade.Ended) return true;
                var other = SessionRegistry.FindByCharacter(trade.Other(me.CharacterId));
                var item = Equipment.ByUid(uid);
                var offer = trade.OfferOf(me.CharacterId);
                int index = offer.FindIndex(o => o.Uid == uid);
                int had = index >= 0 ? offer[index].Quantity : 0;
                int now = item == null || item.Position != Equipment.Bag ? 0 : Math.Clamp(had + delta, 0, item.Quantity);
                if (now == had) return true;

                if (now == 0)
                {
                    offer.RemoveAt(index);
                    await SendAsync(me, Op.Kfs, WorkshopProtocol.BuildRemoved(uid));
                    await SendAsync(other, Op.Kfs, WorkshopProtocol.BuildRemoved(uid, remote: true));
                }
                else
                {
                    if (index >= 0) offer[index] = (uid, now);
                    else offer.Add((uid, now));
                    await SendAsync(me, Op.Kfb, TradeProtocol.BuildLaid(item!.Template, item.Effects, now, uid, remote: false));
                    await SendAsync(other, Op.Kfb, TradeProtocol.BuildLaid(item.Template, item.Effects, now, uid, remote: true));
                }
                await NobodyReadyAsync(trade, me, other);
            }
            finally { trade.Gate.Release(); }
            return true;
        }

        /// <summary>kee in a trade: the kamas this side puts in.</summary>
        /// <returns>False when this character is in no open trade.</returns>
        public static async Task<bool> KamasAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var trade = me.State.Trade;
            if (trade == null || !trade.Accepted) return false;
            byte[]? kee = ConnectionProtocol.ReadPayload(payload, Op.Kee);
            if (kee == null) return true;

            await trade.Gate.WaitAsync();
            try
            {
                if (trade.Ended) return true;
                long kamas = Math.Clamp(VarOf(kee, 1), 0, me.State.Kamas);
                trade.Kamas[me.CharacterId] = kamas;
                var other = SessionRegistry.FindByCharacter(trade.Other(me.CharacterId));
                await SendAsync(me, Op.Ket, TradeProtocol.BuildKamas(kamas, remote: false));
                await SendAsync(other, Op.Ket, TradeProtocol.BuildKamas(kamas, remote: true));
                await NobodyReadyAsync(trade, me, other);
            }
            finally { trade.Gate.Release(); }
            return true;
        }

        /// <summary>Whatever changes on the table, whoever was ready is no longer.</summary>
        private static async Task NobodyReadyAsync(Trade trade, GameSession me, GameSession? other)
        {
            foreach (long who in trade.Ready.ToList())
            {
                trade.Ready.Remove(who);
                await SendBothAsync(me, other, Op.Kgt, WorkshopProtocol.BuildReady(false, who));
            }
        }

        /// <summary>kep in a trade: ready, or not. The second "ready" makes the trade.</summary>
        /// <returns>False when this character is in no open trade.</returns>
        public static async Task<bool> ReadyAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var trade = me.State.Trade;
            if (trade == null || !trade.Accepted) return false;
            byte[]? kep = ConnectionProtocol.ReadPayload(payload, Op.Kep);
            if (kep == null) return true;
            bool ready = VarOf(kep, 1) != 0;

            bool both;
            await trade.Gate.WaitAsync();
            try
            {
                if (trade.Ended) return true;
                var other = SessionRegistry.FindByCharacter(trade.Other(me.CharacterId));
                if (ready) trade.Ready.Add(me.CharacterId);
                else trade.Ready.Remove(me.CharacterId);

                both = trade.Ready.Contains(trade.SourceId) && trade.Ready.Contains(trade.TargetId);
                if (!both)
                {
                    await SendBothAsync(me, other, Op.Kgt, WorkshopProtocol.BuildReady(ready, me.CharacterId));
                    return true;
                }
            }
            finally { trade.Gate.Release(); }

            await ExecuteAsync(trade);
            return true;
        }

        // ─── Done ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Both ready: the stacks and the kamas change hands, the asker's first, and the window
        /// closes. Anything no longer there -- a stack gone, kamas spent -- calls the trade off.
        /// </summary>
        private static async Task ExecuteAsync(Trade trade)
        {
            await trade.Gate.WaitAsync();
            try
            {
                if (trade.Ended) return;
                var source = SessionRegistry.FindByCharacter(trade.SourceId);
                var target = SessionRegistry.FindByCharacter(trade.TargetId);
                if (source == null || target == null || !StillThere(trade, source) || !StillThere(trade, target))
                {
                    await EndHeldAsync(trade, done: false);
                    return;
                }
                trade.Ended = true;

                await HandOverAsync(trade, source, target);
                await HandOverAsync(trade, target, source);

                long fromSource = trade.KamasOf(source.CharacterId), fromTarget = trade.KamasOf(target.CharacterId);
                if (fromSource != fromTarget)
                {
                    foreach (var (giver, receiver, amount) in new[] { (source, target, fromSource), (target, source, fromTarget) })
                    {
                        if (amount <= 0) continue;
                        CommissionHandler.As(giver, () => { giver.State.Kamas -= amount; DatabaseManager.SaveCurrentCharacter(); return 0; });
                        CommissionHandler.As(receiver, () => { receiver.State.Kamas += amount; DatabaseManager.SaveCurrentCharacter(); return 0; });
                    }
                    await SendAsync(source, Op.Ivf, ConnectionProtocol.BuildKamas(source.State.Kamas));
                    await SendAsync(target, Op.Ivf, ConnectionProtocol.BuildKamas(target.State.Kamas));
                }

                await SendBothAsync(source, target, Op.Kgt, WorkshopProtocol.BuildReady(false, source.CharacterId));
                await SendBothAsync(source, target, Op.Kgt, WorkshopProtocol.BuildReady(false, target.CharacterId));
                foreach (var viewer in new[] { source, target })
                {
                    foreach (var side in new[] { source, target })
                    {
                        long capacity = Capacity(side);
                        bool mine = side == viewer;
                        await SendAsync(viewer, Op.Keq, TradeProtocol.BuildWeight(capacity, 0, remote: !mine));
                        if (mine) await SendAsync(viewer, Op.Iun, ConnectionProtocol.BuildPods(0, capacity));
                    }
                }

                source.State.Trade = null;
                target.State.Trade = null;
                await SendBothAsync(source, target, Op.Khd, TradeProtocol.BuildClosed(done: true));
                Console.WriteLine($"[Trade] {source.CharacterId} and {target.CharacterId} trade: " +
                                  $"{trade.OfferOf(source.CharacterId).Count} stack(s) and {fromSource} kamas for " +
                                  $"{trade.OfferOf(target.CharacterId).Count} and {fromTarget}.");
            }
            finally { trade.Gate.Release(); }
        }

        /// <summary>Whether one side still has everything it laid down, and the kamas it put in.</summary>
        private static bool StillThere(Trade trade, GameSession side)
            => CommissionHandler.As(side, () =>
                side.State.Kamas >= trade.KamasOf(side.CharacterId)
                && trade.OfferOf(side.CharacterId).All(o =>
                    Equipment.ByUid(o.Uid) is { } item && item.Position == Equipment.Bag && item.Quantity >= o.Quantity));

        /// <summary>One side's stacks to the other: gone from the giver's bag, into the receiver's.</summary>
        private static async Task HandOverAsync(Trade trade, GameSession giver, GameSession receiver)
        {
            foreach (var (uid, quantity) in trade.OfferOf(giver.CharacterId))
            {
                var given = CommissionHandler.As(giver, () => Give(uid, quantity));
                if (given == null) continue;
                await SendAsync(giver, given.Value.Opcode, given.Value.Body);

                var received = CommissionHandler.As(receiver, () => Receive(given.Value.Gid, quantity, given.Value.Effects));
                if (received != null) await SendAsync(receiver, received.Value.Opcode, received.Value.Body);
            }
        }

        /// <summary>
        /// A stack, or part of it, out of this character's bag: ium when it all goes, ivj with what
        /// is left when it does not. A marketplace listing takes its lot out the same way.
        /// </summary>
        internal static (string Opcode, byte[] Body, int Gid, string Effects)? Give(long uid, int quantity)
        {
            long me = SessionContext.State.CharacterId;
            var stored = HavenBagStore.FromInventory(me, uid);
            if (stored == null || stored.Quantity < quantity) return null;
            if (!DatabaseManager.DestroyCharacterItem(me, uid, quantity)) return null;
            Equipment.Remove(uid, quantity);

            int left = stored.Quantity - quantity;
            return left <= 0
                ? (Op.Ium, ConnectionProtocol.BuildItemGone(uid), stored.Gid, stored.Effects)
                : (Op.Ivj, TradeProtocol.BuildQuantity(uid, left), stored.Gid, stored.Effects);
        }

        /// <summary>
        /// A stack into this character's bag: onto one of the same item with the same effects if
        /// there is one (ivj), else as a new stack with a new uid (iua), as the captures show. The
        /// uid is the stack it ended in. A lot bought at a marketplace arrives the same way.
        /// </summary>
        internal static (string Opcode, byte[] Body, long Uid)? Receive(int gid, int quantity, string effects)
        {
            long me = SessionContext.State.CharacterId;
            var parsed = Equipment.ParseEffects(effects);
            var same = Equipment.All.FirstOrDefault(i => i.Template == gid && i.Position == Equipment.Bag
                                                         && i.Effects.SequenceEqual(parsed));
            if (same != null)
            {
                var stored = HavenBagStore.FromInventory(me, same.Uid);
                int now = same.Quantity + quantity;
                if (stored != null && DatabaseManager.UpdateCharacterItem(me, same.Uid, now, stored.Effects))
                {
                    Equipment.Add(same.Uid, gid, quantity, Equipment.Bag, null);
                    return (Op.Ivj, TradeProtocol.BuildQuantity(same.Uid, now), same.Uid);
                }
            }

            long uid = DatabaseManager.NextItemUid();
            if (!DatabaseManager.InsertCharacterItem(uid, me, gid, quantity, Equipment.Bag, effects)) return null;
            Equipment.Add(uid, gid, quantity, Equipment.Bag, effects);

            var legacy = new PlayerItem { Uid = uid, ItemId = gid, Quantity = quantity, Position = Equipment.Bag, RawEffects = effects };
            foreach (var effect in parsed)
            {
                legacy.Effects.TryGetValue(effect.Effect, out int had);
                legacy.Effects[effect.Effect] = had + (int)effect.Value;
            }
            GameState.AddInventoryItem(legacy);

            var arrived = new HavenBagStore.StoredItem { Uid = uid, Gid = gid, Quantity = quantity, Effects = effects };
            return (Op.Iua, ConnectionProtocol.BuildItemArrived(3, arrived), uid);
        }

        // ─── Calling it off ─────────────────────────────────────────────────────────────────

        /// <summary>kla from either of the two: a refusal before accepting, a cancel after.</summary>
        /// <returns>False when this character has no trade.</returns>
        public static async Task<bool> CloseAsync()
        {
            var trade = SessionContext.Current.State.Trade;
            if (trade == null) return false;
            await EndAsync(trade, done: false);
            return true;
        }

        /// <summary>A character leaves -- the map, the game -- in the middle of one.</summary>
        public static async Task AbandonAsync(GameSession session)
        {
            var trade = session.State.Trade;
            if (trade != null) await EndAsync(trade, done: false);
        }

        /// <summary>The window closes for both, nothing having changed hands.</summary>
        public static async Task EndAsync(Trade trade, bool done)
        {
            await trade.Gate.WaitAsync();
            try { await EndHeldAsync(trade, done); }
            finally { trade.Gate.Release(); }
        }

        /// <summary><see cref="EndAsync"/> with the trade's gate already held.</summary>
        private static async Task EndHeldAsync(Trade trade, bool done)
        {
            if (trade.Ended) return;
            trade.Ended = true;
            var source = SessionRegistry.FindByCharacter(trade.SourceId);
            var target = SessionRegistry.FindByCharacter(trade.TargetId);
            foreach (var session in new[] { source, target })
                if (session?.State.Trade == trade) session.State.Trade = null;
            await SendBothAsync(source, target, Op.Khd, TradeProtocol.BuildClosed(done));
            Console.WriteLine($"[Trade] {trade.SourceId} and {trade.TargetId}: " +
                              (trade.Accepted ? "called off." : "refused or withdrawn."));
        }

        private static long VarOf(byte[] body, int field)
        {
            foreach (var f in ProtoMessage.Parse(body).Fields)
                if (f.FieldNumber == field && f.WireType == 0) return f.VarIntValue;
            return 0;
        }
    }
}
