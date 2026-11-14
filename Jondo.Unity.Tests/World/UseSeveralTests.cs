using System;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// «Utiliser plusieurs», ivs { f1 uid, f2 how many }: nothing answered it, and the 44 purses
    /// asked to open at once stayed shut. Each one is used as a double click uses it.
    /// </summary>
    [Collection("forgemagic")]
    public sealed class UseSeveralTests
    {
        private const long Account = 8_900_000_091;
        private const int Purse1000 = 8815;

        private static byte[] Ivs(long uid, long count)
            => ConnectionProtocol.Push(Op.Ivs, Pb.New().Var(1, uid).Var(2, count).Build());

        [Fact]
        public async Task Several_purses_open_at_once_and_no_more_than_the_stack()
        {
            if (DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(Purse1000)).Count == 0)
                return;   // no item data

            long character = DatabaseManager.CreateCharacter(Account, 1, "Varias-Bolsas", 9, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_091, "Varias-Bolsas");
                using (SessionContext.Push(pipe.Session))
                {
                    long uid = DatabaseManager.NextItemUid();
                    Assert.True(DatabaseManager.InsertCharacterItem(uid, character, Purse1000, 5, Equipment.Bag, ""));
                    Equipment.Add(uid, Purse1000, 5, Equipment.Bag, "");
                    long kamas = pipe.Session.State.Kamas;

                    // Three of five: two left, three thousand kamas.
                    await ItemUseHandler.UseSeveralAsync(pipe.ToClient, Ivs(uid, 3));
                    Assert.Equal(2, Equipment.ByUid(uid)!.Quantity);
                    Assert.Equal(kamas + 3000, pipe.Session.State.Kamas);

                    // Ten asked of the two left: both, and the stack is gone.
                    await ItemUseHandler.UseSeveralAsync(pipe.ToClient, Ivs(uid, 10));
                    Assert.Null(Equipment.ByUid(uid));
                    Assert.Equal(kamas + 5000, pipe.Session.State.Kamas);
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }
    }
}
