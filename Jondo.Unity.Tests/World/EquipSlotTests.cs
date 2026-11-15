using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A slot takes its own kind of item, and a bonus slot takes nothing from the client.
    /// </summary>
    /// <remarks>
    /// The slot is the client's number and nothing looked at it: a resource went on the neck, a
    /// ring on the head, and anything into a bonus slot -- which is never emptied to make room --,
    /// each counted on the sheet.
    /// </remarks>
    [Collection("forgemagic")]
    public sealed class EquipSlotTests
    {
        /// <summary>An amulet (type 1) and a resource (type 15).</summary>
        private const int Amulet = 70, Resource = 17626;

        [Fact]
        public void An_amulet_goes_on_the_neck_and_nowhere_else_worn()
        {
            Assert.True(Equipment.MayGoIn(Amulet, 0));
            for (int slot = 1; slot <= Equipment.LastWornSlot; slot++)
                Assert.False(Equipment.MayGoIn(Amulet, slot), $"slot {slot}");
        }

        [Fact]
        public void A_resource_is_worn_nowhere()
        {
            for (int slot = 0; slot <= Equipment.LastWornSlot; slot++)
                Assert.False(Equipment.MayGoIn(Resource, slot), $"slot {slot}");
        }

        [Fact]
        public void A_bonus_slot_takes_nothing_from_a_move()
        {
            for (int slot = BonusSlots.First; slot <= BonusSlots.Last; slot++)
            {
                Assert.False(Equipment.MayGoIn(Amulet, slot), $"slot {slot}");
                Assert.False(Equipment.MayGoIn(Resource, slot), $"slot {slot}");
            }
        }

        [Fact]
        public void A_position_that_adds_nothing_to_the_sheet_is_left_alone()
        {
            // The capture nets' slot, and the bag.
            Assert.True(Equipment.MayGoIn(Resource, RideCapture.ConsumableSlot));
            Assert.True(Equipment.MayGoIn(Amulet, Equipment.Bag));
        }
    }
}
