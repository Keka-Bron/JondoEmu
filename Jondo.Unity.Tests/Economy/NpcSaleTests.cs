using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Selling to a vendor, kem { f2: { f1 how many, f2 uid } }: nothing answered it, and the item
    /// stayed in the bag. The vendor pays a tenth of the item's own price a unit, rounded up.
    /// </summary>
    [Collection("forgemagic")]
    public sealed class NpcSaleTests
    {
        private const long Account = 8_900_000_092;

        /// <summary>A level 189 item, 18900 kamas in its template.</summary>
        private const int Item = 9181;

        private static byte[] Kem(params (long Quantity, long Uid)[] sales)
        {
            var kem = Pb.New();
            foreach (var (quantity, uid) in sales) kem.Msg(2, Pb.New().Var(1, quantity).Var(2, uid));
            return ConnectionProtocol.Push(Op.Kem, kem.Build());
        }

        [Fact]
        public void A_vendor_buys_back_at_a_tenth_of_the_item_s_price()
        {
            long price = NpcShops.TemplatePriceOf(Item);
            if (price == 0) return;   // no item data
            Assert.Equal(18900, price);
            Assert.Equal(1890, NpcShops.BuyBackOf(Item, 1));
            Assert.Equal(3 * 1890, NpcShops.BuyBackOf(Item, 3));

            // Rounded up a unit, as the client shows: the Fée d'artifice at 1 (9380, price 1),
            // the Blé at 0 (289, price 0).
            Assert.Equal(2, NpcShops.BuyBackOf(9380, 2));
            Assert.Equal(0, NpcShops.BuyBackOf(289, 200));
        }

        [Fact]
        public async Task Selling_takes_the_stack_and_pays_the_kamas()
        {
            if (!Npcs.AllSpawns.Any()) Npcs.Initialize();
            if (NpcShops.Count == 0) NpcShops.Initialize();
            var vendor = Npcs.AllSpawns.FirstOrDefault(s => NpcShops.Sells(s.NpcId) && TokenShops.Of(s.NpcId) == null
                                                      && string.IsNullOrEmpty(s.Criterion));
            if (vendor == null || NpcShops.TemplatePriceOf(Item) == 0) return;   // no world data

            long character = DatabaseManager.CreateCharacter(Account, 1, "Vende-Algo", 9, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, vendor.MapId, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, vendor.MapId, "Vende-Algo");
                using (SessionContext.Push(pipe.Session))
                {
                    long uid = DatabaseManager.NextItemUid();
                    Assert.True(DatabaseManager.InsertCharacterItem(uid, character, Item, 3, Equipment.Bag, ""));
                    Equipment.Add(uid, Item, 3, Equipment.Bag, "");
                    long kamas = pipe.Session.State.Kamas;

                    // No vendor open: nothing goes.
                    await NpcHandler.SellAsync(pipe.ToClient, Kem((1, uid)));
                    Assert.Equal(3, Equipment.ByUid(uid)!.Quantity);

                    pipe.Session.State.OpenNpcShopId = vendor.ContextualId;
                    pipe.Session.State.OpenNpcShopNpcId = vendor.NpcId;

                    // One of three: two left, 1890 kamas.
                    await NpcHandler.SellAsync(pipe.ToClient, Kem((1, uid)));
                    Assert.Equal(ConnectionProtocol.BuildItemQuantity(uid, 2), await pipe.Next(Op.Ivj));
                    await pipe.Next(Op.Lqn);
                    Assert.Equal(ConnectionProtocol.BuildKamas(kamas + 1890), await pipe.Next(Op.Ivf));
                    await pipe.Next(Op.Iun);

                    // More than is left: the two, and the stack is gone.
                    await NpcHandler.SellAsync(pipe.ToClient, Kem((5, uid)));
                    Assert.Equal(ConnectionProtocol.BuildItemGone(uid), await pipe.Next(Op.Ium));
                    Assert.Null(Equipment.ByUid(uid));
                    Assert.Equal(kamas + 3 * 1890, pipe.Session.State.Kamas);
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }
    }
}
