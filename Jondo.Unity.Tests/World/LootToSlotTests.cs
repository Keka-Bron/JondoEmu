using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A curse a fight drops goes to its slot once the character is back on a map, not to the bag:
    /// Halouine's Zombie and the Malédiction du Ballotin stayed in the bag as items, doing nothing.
    /// </summary>
    [Collection("forgemagic")]
    public sealed class LootToSlotTests
    {
        private const long Account = 8_900_000_094;
        private const int BallotinCurse = 9491;

        [Fact]
        public async Task A_dropped_curse_goes_to_a_curse_slot_once_out_of_the_fight()
        {
            if (DatabaseManager.GetItemTemplateType(BallotinCurse) != BonusSlots.CurseType) return;   // no item data

            long character = DatabaseManager.CreateCharacter(Account, 1, "Maldito-Uno", 9, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 191105026, "Maldito-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    var state = pipe.Session.State;
                    state.PendingSlotItems.Add(BallotinCurse);

                    // Still fighting: it waits.
                    state.IsInFight = true;
                    await BonusItemHandler.PlacePendingAsync(pipe.ToClient);
                    Assert.Single(state.PendingSlotItems);
                    Assert.DoesNotContain(Equipment.All, i => i.Template == BallotinCurse);

                    // Back on the map: in a curse slot, not the bag.
                    state.IsInFight = false;
                    await BonusItemHandler.PlacePendingAsync(pipe.ToClient);
                    Assert.Empty(state.PendingSlotItems);
                    var curse = Assert.Single(Equipment.All, i => i.Template == BallotinCurse);
                    Assert.Contains(curse.Position, BonusSlots.SlotsFor(BonusSlots.CurseType));
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }
    }
}
