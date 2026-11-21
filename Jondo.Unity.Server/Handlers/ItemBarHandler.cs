using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The item bar's three requests (ItemShortcuts), the same three as the spell bar's with the
    /// bar number 0 left out, as proto3 leaves zeros out:
    /// <code>
    ///   itz { 2 shortcut }           -> ivk, the same body       a slot set
    ///   iul { 2 slot }               -> ivr { 2 slot }           a slot emptied
    ///   iuv { 1 slot, 2 slot }       -> ivk / ivr per slot       two slots swapped
    /// </code>
    /// The first two MEASURED with a build in «Conjuntos/crear nuevo conjunto…» (slot 18); the
    /// swap is the spell bar's, which the client sends for both.
    /// </summary>
    public static class ItemBarHandler
    {
        private static long Me => SessionContext.State.CharacterId;

        /// <summary>itz on the item bar: kept and echoed, or emptied when it holds nothing.</summary>
        public static async Task SetAsync(NetworkStream stream, byte[] itz)
        {
            var shortcut = ProtoMessage.Parse(itz).Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2)?.BytesValue;
            if (shortcut == null) return;
            int slot = ItemShortcuts.SlotOf(shortcut);
            if (!ItemShortcuts.HoldsSomething(shortcut))
            {
                await RemoveAsync(stream, slot);
                return;
            }
            ItemShortcuts.Put(Me, slot, shortcut);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ivk, itz));
        }

        /// <summary>iul on the item bar.</summary>
        public static async Task RemoveAsync(NetworkStream stream, int slot)
        {
            ItemShortcuts.Remove(Me, slot);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivr, ConnectionProtocol.BuildShortcutRemoved(slot, ItemShortcuts.Bar)));
        }

        /// <summary>iuv on the item bar.</summary>
        public static async Task SwapAsync(NetworkStream stream, int first, int second)
        {
            if (first == second) return;
            var (atFirst, atSecond) = ItemShortcuts.Swap(Me, first, second);
            await WriteSlotAsync(stream, first, atFirst);
            await WriteSlotAsync(stream, second, atSecond);
        }

        /// <summary>
        /// The bar's «vaciar» (iuz, the client's UIActionBar.ClearBarAction): every slot of that bar
        /// emptied, each with its ivr. The bar is the message's number, 1 the spells' and left out
        /// for the items'. The weapon stays: its slot is the fist's, on every bar. INFERRED: no
        /// capture presses it; each slot is answered as iul answers one.
        /// </summary>
        public static async Task ClearAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iuz = ConnectionProtocol.ReadPayload(payload, "iuz");
            if (iuz == null) return;
            long bar = ProtoMessage.Parse(iuz).Fields.Where(f => f.WireType == 0).Select(f => (long)f.VarIntValue).DefaultIfEmpty(0).Max();

            if (bar == ConnectionProtocol.SpellBar)
            {
                foreach (int slot in SpellChoices.Bar.Keys.OrderBy(s => s).ToList())
                {
                    SpellChoices.RemoveFromBar(slot);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Ivr, ConnectionProtocol.BuildShortcutRemoved(slot, ConnectionProtocol.SpellBar)));
                }
                return;
            }

            foreach (int slot in ItemShortcuts.Of(Me).Keys.ToList())
                await RemoveAsync(stream, slot);
        }

        private static Task WriteSlotAsync(NetworkStream stream, int slot, byte[]? shortcut)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, shortcut == null
                ? ConnectionProtocol.Push(Op.Ivr, ConnectionProtocol.BuildShortcutRemoved(slot, ItemShortcuts.Bar))
                : ConnectionProtocol.Push(Op.Ivk, Pb.New().Bytes(2, shortcut).Build()));
    }
}
