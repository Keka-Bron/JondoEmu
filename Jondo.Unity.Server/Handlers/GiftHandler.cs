using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The shop's gifts: the list, and handing one or all of them to a character.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list (jtg) closes the character list in every capture, and the real server sends it
    /// again in the world. f3 is one gift each:
    /// </para>
    /// <code>
    ///   jtg { f3 [ { f1 { f2 title, f3 [ { 2 quantity, 3 item, 4 effects } ], f6 description },
    ///               f2 [ id ] } ] }
    /// </code>
    /// <para>
    /// The gifts window (WebGiftUi) hands one out with jwm { 1 id, 2 character } and all of them
    /// with jth { 2 character }. «abrir tienda-navegar-obtener recompensas de una en una-luego de
    /// golpe en boton» does both: each gift is answered jwj { 2 id, 3 true }, the item's iua and
    /// the bag's weight, in that order, and the client takes the gift off its list by the id.
    /// </para>
    /// </remarks>
    public static class GiftHandler
    {
        /// <summary>jwm: one gift to one of the account's characters.</summary>
        public static async Task ClaimAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jwm = ConnectionProtocol.ReadPayload(payload, "jwm");
            if (jwm == null) return;
            var fields = ProtoMessage.Parse(jwm).Fields;
            int giftId = (int)(fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 0)?.VarIntValue ?? 0);
            long character = fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
            await HandOutAsync(stream, giftId, character);
        }

        /// <summary>jth: every gift to one of the account's characters.</summary>
        public static async Task ClaimAllAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jth = ConnectionProtocol.ReadPayload(payload, "jth");
            if (jth == null) return;
            long character = ProtoMessage.Parse(jth).Fields
                .FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
            foreach (var gift in Gifts.Of(SessionContext.Current.AccountId))
                await HandOutAsync(stream, gift.Id, character);
        }

        /// <summary>
        /// The account's characters that are playing get the list again: a gift just granted shows
        /// in the window without logging out.
        /// </summary>
        public static Task TellAsync(long accountId) => GiftService.Live.BroadcastAsync(accountId);

        private static async Task HandOutAsync(NetworkStream stream, int giftId, long character)
        {
            long account = SessionContext.Current.AccountId;
            if (giftId <= 0 || character <= 0 || !DatabaseManager.CharacterBelongsToAccount(character, account))
            {
                await Write(stream, "jwj", Pb.New().Var(2, giftId).Build());
                return;
            }

            var gift = Gifts.Take(account, giftId);
            if (gift == null)
            {
                await Write(stream, "jwj", Pb.New().Var(2, giftId).Build());
                return;
            }

            await Write(stream, "jwj", Pb.New().Var(2, gift.Id).Var(3, 1).Build());
            bool playing = SessionContext.Current.IsInWorld && SessionContext.State.CharacterId == character;
            if (playing)
            {
                await WorkshopHandler.GiveAsync(stream, gift.Gid, gift.Quantity);
                await ItemUseHandler.WeighAsync(stream);
            }
            else
            {
                Store(character, gift.Gid, gift.Quantity);
            }
            Console.WriteLine($"[Gifts] Gift {gift.Id} ({gift.Quantity} x {gift.Gid}) handed to character {character}.");
        }

        /// <summary>A gift for a character who is not playing: straight into his bag, rolled as the drops are.</summary>
        private static void Store(long character, int gid, int quantity)
        {
            var template = Forgemagic.TemplateOf(gid);
            if (template == null) return;
            var dice = new Random();
            int rows = Forgemagic.Stacks(template) ? 1 : quantity;
            int each = Forgemagic.Stacks(template) ? quantity : 1;
            for (int i = 0; i < rows; i++)
            {
                string effects = Forgemagic.Serialize(Forgemagic.Roll(template, dice));
                DatabaseManager.InsertCharacterItem(DatabaseManager.NextItemUid(), character, gid, each, Equipment.Bag, effects);
            }
        }

        private static Task Write(NetworkStream stream, string opcode, byte[] message)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(opcode, message));
    }
}
