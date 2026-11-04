using System.Collections.Concurrent;
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
    /// The disguises and followers of the bonus slots (<see cref="SlotLooks"/>) coming and going:
    /// the character drawn again for him and his map, and the items that end on their own terms.
    /// </summary>
    /// <remarks>
    /// The ends are the items' own words. «Desaparece al desplazarse» (760, the «Basuraje» and the
    /// rest of the decor camouflages) goes with the first step. The «Camuflaje» lasts «hasta que
    /// entres en algún combate»: a roleplay buffer's item that does not count fights (811) is taken
    /// off as the fight is built, and the client is told with the fight's end. One that counts them
    /// goes down with them as every bonus slot does (AlterationHandler).
    /// </remarks>
    public static class SlotLookHandler
    {
        /// <summary>The uids taken off for a fight, by character, to tell the client when it ends.</summary>
        private static readonly ConcurrentDictionary<long, List<long>> _takenOff = new();

        /// <summary>The character drawn again (jsn), for him and for whoever shares his map.</summary>
        public static async Task RedrawAsync(NetworkStream stream)
        {
            var me = SessionContext.State;
            var character = DatabaseManager.GetCharacterById(me.CharacterId);
            if (character == null) return;

            byte[] jsn = ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                character, me.CellId, me.Orientation, SessionContext.Current.AccountId));
            await WriteAsync(stream, jsn);
            await SessionRegistry.BroadcastToMapAsync(me.MapId, jsn, SessionContext.Current.Id);
        }

        /// <summary>A step about to be taken: what disappears on moving goes first.</summary>
        public static async Task OnMovingAsync(NetworkStream stream)
        {
            var gone = Equipment.All.Where(i => BonusSlots.IsBonusSlot(i.Position)
                                                && i.Effects.Any(e => e.Effect == SlotLooks.DisappearsOnMoving))
                                    .ToList();
            long me = SessionContext.State.CharacterId;

            // And an aura shown from the attitudes: the step takes it off, as in the capture.
            bool auraGone = Wardrobe.StopShowing(me);
            if (gone.Count == 0)
            {
                if (auraGone) await RedrawAsync(stream);
                return;
            }

            foreach (var item in gone) await AlterationHandler.RemoveSlotItemAsync(stream, me, item);
            await RedrawAsync(stream);
        }

        /// <summary>
        /// A fight being built for the character in session: the roleplay buffer's item that does
        /// not count fights is taken off, quietly; <see cref="TellTakenOffAsync"/> says so at its end.
        /// </summary>
        public static void TakeOffForFight()
        {
            long me = SessionContext.State.CharacterId;
            var item = Equipment.InSlot(SlotLooks.RoleplayBuffer);
            if (item == null || item.Effects.Any(e => e.Effect == AlterationCatalogue.FightsLeft)) return;
            if (!AlterationHandler.TakeSlotItem(me, item)) return;
            _takenOff.AddOrUpdate(me, _ => new List<long> { item.Uid }, (_, list) => { lock (list) list.Add(item.Uid); return list; });
        }

        /// <summary>The items <see cref="TakeOffForFight"/> took off, gone from the client's inventory (ium).</summary>
        public static async Task TellTakenOffAsync(NetworkStream stream)
        {
            if (!_takenOff.TryRemove(SessionContext.State.CharacterId, out var uids)) return;
            List<long> copy;
            lock (uids) copy = uids.ToList();
            foreach (long uid in copy)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid)));
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
