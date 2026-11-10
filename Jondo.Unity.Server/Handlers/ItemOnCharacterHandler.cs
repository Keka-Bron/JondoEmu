using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// An item used on another character: the client sends <c>itm { 1: the target's character id,
    /// 2: the item's uid }</c> once an item of the shortcut bar has been picked and a player clicked
    /// (read from the real client, «Los mechones de la gloria»).
    /// </summary>
    /// <remarks>
    /// The one such item quests need today is the iron pair of scissors (19740) of «Los mechones
    /// de la gloria»: cutting a lock off a player of a class gives that class's lock of hair. Its
    /// nineteen effects come in the classes' own order, Féca to Forgelance, one lock each; the
    /// locks are the client's «Bola de pelos de …» quest items, listed below by class.
    /// </remarks>
    public static class ItemOnCharacterHandler
    {
        /// <summary>«Par de tijeras de hierro».</summary>
        public const int IronScissors = 19740;

        /// <summary>Each class's lock of hair, by breed id: «Bola de pelos de feca» to «… de forjalanza».</summary>
        internal static readonly IReadOnlyDictionary<int, int> LockOfHair = new Dictionary<int, int>
        {
            [1] = 6679, [2] = 6680, [3] = 6681, [4] = 6682, [5] = 6683, [6] = 6684, [7] = 6685,
            [8] = 6686, [9] = 6687, [10] = 6688, [11] = 6689, [12] = 19741, [13] = 19742,
            [14] = 19743, [15] = 19744, [16] = 19745, [17] = 19746, [18] = 19747, [20] = 27703,
        };

        public static async Task UseAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? itm = ConnectionProtocol.ReadPayload(payload, Op.Itm);
            if (itm == null) return;

            long target = 0, uid = 0;
            foreach (var field in ProtoMessage.Parse(itm).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) target = field.VarIntValue;
                else if (field.FieldNumber == 2) uid = field.VarIntValue;
            }

            var item = Equipment.ByUid(uid);
            if (item == null || item.Quantity <= 0) return;
            if (item.Template != IronScissors)
            {
                Console.WriteLine($"[Items] Item {item.Template} used on character {target}: no such use.");
                return;
            }

            // Somebody standing on the same map, and somebody else: a lock is cut off a player one
            // is beside, the way the client lets the scissors aim.
            var state = SessionContext.State;
            var other = SessionRegistry.FindByCharacter(target)?.State;
            if (other == null || target == state.CharacterId || other.MapId != state.MapId)
            {
                Console.WriteLine($"[Items] Scissors on character {target}: not here.");
                return;
            }
            if (!LockOfHair.TryGetValue(other.Breed, out int lockOfHair))
            {
                Console.WriteLine($"[Items] Scissors on character {target}: no lock for class {other.Breed}.");
                return;
            }

            if (!await Equipment.GiveAsync(stream, lockOfHair, 1)) return;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    InfoMessages.ItemGained, "1", lockOfHair.ToString(CultureInfo.InvariantCulture))));
            Console.WriteLine($"[Items] {state.CharacterName} cuts a lock of hair off {target} (class {other.Breed}): item {lockOfHair}.");
        }
    }
}
