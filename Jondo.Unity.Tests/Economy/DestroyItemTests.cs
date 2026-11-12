using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    [Collection("MapManager")]
    public class DestroyItemTests
    {
        /// <summary>A client request on root 2, with its id beside the message.</summary>
        private static byte[] Request(string op, byte[] body, long id)
            => Pb.New().Msg(2, Pb.New().Bytes(1, Pb.New().Str(1, "type.ankama.com/" + op).Bytes(2, body).Build())
                                       .Var(2, id)).Build();

        private static byte[] Destroy(long uid, int quantity)
            => Request(Op.Iuw, Pb.New().Msg(2, Pb.New().Var(2, uid).Var(3, quantity)).Build(), 1);

        /// <summary>
        /// Part of a stack destroyed: the client is told the stack's new total (ivj). An iua would be
        /// added to the stack it shows, ten destroyed down to seven showing seventeen.
        /// </summary>
        [Fact]
        public async Task Part_of_a_stack_tells_the_new_total_and_the_rest_tells_it_is_gone()
        {
            const long character = 990000016, uid = 990000017;
            await using var pipe = await ClientPipe.OpenAsync(990000015, character, 1);
            using (SessionContext.Push(pipe.Session))
            {
                Assert.True(DatabaseManager.InsertCharacterItem(uid, character, 999999, 10, Equipment.Bag, null));
                try
                {
                    Equipment.Add(uid, 999999, 10, Equipment.Bag, null);

                    await DestroyItemHandler.DestroyAsync(pipe.ToClient, Destroy(uid, 3));
                    Assert.Equal(ConnectionProtocol.BuildItemQuantity(uid, 7), await pipe.Next(Op.Ivj));
                    await pipe.Next(Op.Iun);
                    Assert.Equal(7, Equipment.ByUid(uid)?.Quantity ?? 0);

                    await DestroyItemHandler.DestroyAsync(pipe.ToClient, Destroy(uid, 7));
                    Assert.Equal(ConnectionProtocol.BuildItemGone(uid), await pipe.Next(Op.Ium));
                    await pipe.Next(Op.Iun);
                    Assert.Null(Equipment.ByUid(uid));
                    Assert.DoesNotContain(DatabaseManager.LoadInventory(character), i => i.Uid == uid);
                }
                finally { DatabaseManager.DestroyCharacterItem(character, uid, 0); }
            }
        }
    }
}
