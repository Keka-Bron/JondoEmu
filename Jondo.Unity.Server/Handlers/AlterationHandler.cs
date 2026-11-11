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
    /// <summary>
    /// The alterations window, kept current: what is listed on entering the world, set, lost, and
    /// counted down at the end of each fight, with the bonus slots' items beside them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real server's end of a fight, in the larva race and every class capture: jyg, jxo, the whole
    /// list (lzp) with the counts already brought down, each bonus slot item that changed (iuj), and for
    /// every alteration spent its lzo and «Has perdido la alteración» (info 1665); the kub comes after,
    /// without what was lost. A mutation spent at the end of its fight goes with an ium.
    /// </para>
    /// <para>
    /// The timed ones end on the clock: the heartbeat (kqo, every five seconds) looks, outside a fight,
    /// for the ones whose date has come.
    /// </para>
    /// </remarks>
    public static class AlterationHandler
    {
        private static readonly AlterationSnapshotService Snapshots = new(AlterationMessages.FromCatalogue, new SnapshotBackend());

        /// <summary>The list asked for on entering the world (lzh): every alteration (lzp).</summary>
        public static Task ListAsync(GameSession session) => Snapshots.ListAsync(session);

        /// <summary>An alteration set on whoever is in session: lzs. Null when the client does not know it.</summary>
        public static async Task<Alterations.Active?> GainAsync(NetworkStream stream, int alteration, long untilMs = 0)
        {
            var now = DateTime.UtcNow;
            var active = Alterations.Gain(SessionContext.State.CharacterId, alteration, now, untilMs);
            if (active == null)
            {
                Console.WriteLine($"[Alterations] Alteration {alteration} is not in the client's data.");
                return null;
            }
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lzs, AlterationProtocol.BuildSet(active, now)));
            return active;
        }

        /// <summary>
        /// An alteration set on a character by someone else's doing -- a raid fragment found in a fight
        /// --: stored, and told to the character if he is connected.
        /// </summary>
        public static async Task GainForAsync(long characterId, int alteration, long untilMs = 0)
        {
            var now = DateTime.UtcNow;
            var active = Alterations.Gain(characterId, alteration, now, untilMs);
            if (active == null) return;
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session != null)
                await session.SendAsync(ConnectionProtocol.Push(Op.Lzs, AlterationProtocol.BuildSet(active, now)));
        }

        /// <summary>An alteration taken from whoever is in session: lzo and info 1665. False when he did not have it.</summary>
        public static async Task<bool> LoseAsync(NetworkStream stream, int alteration)
        {
            var gone = Alterations.Lose(SessionContext.State.CharacterId, alteration);
            if (gone == null) return false;
            await TellLostAsync(stream, gone, DateTime.UtcNow);
            return true;
        }

        /// <summary>Alterations taken from a character by someone else's doing: the raid's fragments when it closes.</summary>
        public static async Task LoseForAsync(long characterId, IEnumerable<int> alterations)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            var now = DateTime.UtcNow;
            foreach (int alteration in alterations)
            {
                var gone = Alterations.Lose(characterId, alteration);
                if (gone == null || session == null) continue;
                await session.SendAsync(ConnectionProtocol.Push(Op.Lzo, AlterationProtocol.BuildLost(gone, now)));
                await session.SendAsync(Lost(gone.Id));
            }
        }

        /// <summary>
        /// One fight over for whoever is in session, before his kub: the list with the counts brought
        /// down, the bonus slots' items, and what was spent.
        /// </summary>
        public static Task AfterFightAsync(GameSession session, bool won) => Snapshots.AfterFightAsync(session, won);

        /// <summary>
        /// The heartbeat's look at the clock: the timed alterations whose date has come, lost, and the
        /// sheet again if they gave something. Not in a fight, whose sheet is another.
        /// </summary>
        public static async Task TickAsync(NetworkStream stream)
        {
            var me = SessionContext.State;
            if (me.CharacterId == 0 || me.IsInFight || me.FightId != 0) return;

            var now = DateTime.UtcNow;
            var expired = Alterations.Expire(me.CharacterId, now);

            // And the bonus slots' items that count minutes, a perlequín's flavour, whose end has come.
            long seconds = new DateTimeOffset(now).ToUnixTimeSeconds();
            var ended = Equipment.All.Where(i => BonusSlots.IsBonusSlot(i.Position))
                                     .Where(i => BonusSlots.EndsAt(i.Effects) is > 0 and var end && end <= seconds)
                                     .ToList();
            foreach (var item in ended) await RemoveSlotItemAsync(stream, me.CharacterId, item);

            if (expired.Count == 0 && ended.Count == 0) return;
            foreach (var gone in expired) await TellLostAsync(stream, gone, now);
            if (ended.Count > 0 || expired.Any(GivesCharacteristics))
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
        }

        /// <summary>
        /// The candy rule: no two candies' effects at once. What a candy left -- the boost food in its
        /// slot, a «Golosina» alteration -- goes before another candy's comes, as the new one takes its
        /// place.
        /// </summary>
        internal static async Task DropCandyEffectsAsync(NetworkStream stream)
        {
            long character = SessionContext.State.CharacterId;
            foreach (var item in Equipment.All.Where(i => i.Position == BonusSlots.BoostFood).ToList())
                await RemoveSlotItemAsync(stream, character, item);

            foreach (var active in Alterations.Of(character).Where(a => AlterationCatalogue.IsCandy(a.Id)))
                await LoseAsync(stream, active.Id);
        }

        /// <summary>Whether an alteration moves any characteristic, which is when its loss needs a new kub.</summary>
        internal static bool GivesCharacteristics(Alterations.Active active)
            => AlterationCatalogue.Of(active.Id)?.Effects
                   .Any(e => EffectTable.TryGet(e.Effect, out var what) && what.Characteristic != 0 && e.Sheet != 0)
               ?? false;

        /// <summary>
        /// The bonus slots' items, one fight on: iuj with the count one fewer, or ium when it reaches
        /// zero.
        /// </summary>
        private static async Task CountSlotsDownAsync(NetworkStream stream, long character, bool won)
        {
            bool redraw = false;
            foreach (var item in Equipment.All.Where(i => BonusSlots.IsBonusSlot(i.Position)).ToList())
            {
                var counted = BonusSlots.CountDown(item.Effects, won);
                if (counted == null) continue;

                var (effects, isSpent) = counted.Value;
                if (isSpent && item.Position == Mutations.Slot)
                {
                    // A mutation spent gives the character back his spells, look and sheet.
                    await MutationHandler.EndAsync(stream);
                    continue;
                }
                if (isSpent)
                {
                    if (SlotLooks.ChangesTheLook(item.Effects)) redraw = true;
                    await RemoveSlotItemAsync(stream, character, item);
                    continue;
                }
                if (!Equipment.Rewrite(item, item.Quantity, effects)) continue;
                Equipment.RememberWorn(item.Uid, item.Position, item.Effects);
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Iuj, AlterationProtocol.BuildSlotItemChanged(item)));
            }
            // A follower or a disguise gone: the others on his map see him without it.
            if (redraw) await SlotLookHandler.RedrawAsync(stream);
        }

        /// <summary>A bonus slot's item gone: out of the base, the bag, the sheet's cache, and the client (ium).</summary>
        internal static async Task RemoveSlotItemAsync(NetworkStream stream, long character, Equipment.Item item)
        {
            if (!TakeSlotItem(character, item)) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(item.Uid)));
        }

        /// <summary>A bonus slot's item out of the base, the bag and the sheet's cache, without telling the client.</summary>
        internal static bool TakeSlotItem(long character, Equipment.Item item)
        {
            if (!DatabaseManager.DestroyCharacterItem(character, item.Uid, item.Quantity)) return false;
            Equipment.Remove(item.Uid, item.Quantity);
            Equipment.RememberWorn(item.Uid, Equipment.Bag, (string?)null);
            SlotLooks.Current();
            return true;
        }

        private static async Task TellLostAsync(NetworkStream stream, Alterations.Active gone, DateTime now)
        {
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lzo, AlterationProtocol.BuildLost(gone, now)));
            await WriteAsync(stream, Lost(gone.Id));
        }

        /// <summary>«Has perdido la alteración «$alteration{0}»» (info 1665), the alteration its parameter.</summary>
        private static byte[] Lost(int alteration)
            => ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.AlterationLost, alteration.ToString()));

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        private sealed class SnapshotBackend : IAlterationSnapshotBackend
        {
            public DateTime UtcNow => DateTime.UtcNow;
            public IReadOnlyList<Alterations.Active> Of(long characterId) => Alterations.Of(characterId);
            public IReadOnlyList<Alterations.Active> Expire(long characterId, DateTime nowUtc) => Alterations.Expire(characterId, nowUtc);
            public (IReadOnlyList<Alterations.Active> Listed, IReadOnlyList<Alterations.Active> Spent) CountFight(long characterId, bool won)
                => Alterations.CountFight(characterId, won);

            public async Task RefreshSlotsAsync(GameSession session, bool won)
            {
                var stream = StreamOf(session);
                await SlotLookHandler.TellTakenOffAsync(stream);
                await CountSlotsDownAsync(stream, session.CharacterId, won);
            }

            public Task TellLostAsync(GameSession session, Alterations.Active active, DateTime nowUtc)
                => AlterationHandler.TellLostAsync(StreamOf(session), active, nowUtc);

            private static NetworkStream StreamOf(GameSession session)
                => session.Stream ?? throw new InvalidOperationException("Alteration feature hooks require a connected game session.");
        }
    }
}
