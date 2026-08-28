using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    public sealed class EquipmentMoveRulesTests
    {
        [Fact]
        public void Corbalame_requires_level_110_in_the_weapon_slot()
        {
            Assert.Equal(Equipment.MoveRejection.LevelTooLow,
                Equipment.ValidateTemplateMove(itemLevel: 110, itemType: 6,
                                               position: 1, characterLevel: 1));
            Assert.Equal(Equipment.MoveRejection.None,
                Equipment.ValidateTemplateMove(itemLevel: 110, itemType: 6,
                                               position: 1, characterLevel: 110));
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(6, 1)]
        [InlineData(9, 2)]
        [InlineData(9, 4)]
        [InlineData(10, 3)]
        [InlineData(11, 5)]
        [InlineData(16, 6)]
        [InlineData(17, 7)]
        [InlineData(18, 8)]
        [InlineData(23, 9)]
        [InlineData(23, 14)]
        [InlineData(82, 15)]
        public void Wearable_types_fit_their_slots(int itemType, int position)
        {
            Assert.Equal(Equipment.MoveRejection.None,
                Equipment.ValidateTemplateMove(itemLevel: 1, itemType, position, characterLevel: 200));
        }

        [Theory]
        [InlineData(6, 0)]
        [InlineData(1, 1)]
        [InlineData(11, 4)]
        [InlineData(9, 5)]
        [InlineData(82, 14)]
        [InlineData(23, 15)]
        [InlineData(24, 3)]
        [InlineData(6, 16)]
        public void Wrong_or_non_equipment_slots_are_rejected(int itemType, int position)
        {
            Assert.Equal(Equipment.MoveRejection.WrongSlot,
                Equipment.ValidateTemplateMove(itemLevel: 1, itemType, position, characterLevel: 200));
        }

        [Fact]
        public void Taking_an_item_off_is_never_blocked_by_its_level()
        {
            Assert.Equal(Equipment.MoveRejection.None,
                Equipment.ValidateTemplateMove(itemLevel: 200, itemType: 6,
                                               position: Equipment.Bag, characterLevel: 1));
        }
    }
}
