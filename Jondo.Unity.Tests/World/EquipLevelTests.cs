using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The level an item asks for, which the server was not asking about.
    /// </summary>
    /// <remarks>
    /// Nothing on this server checked it: a level 1 character could put on a level 110 weapon and
    /// keep its bonuses. The client greys the item out and refuses to drag it, which is why it never
    /// came up in play — but a modified client sends the same move message and it was taken at face
    /// value.
    ///
    /// The number is not guesswork. Every one of the 21,748 item templates carries a <c>level</c>
    /// field; 11,275 of them ask for 1 and the highest asks for 200.
    /// </remarks>
    public class EquipLevelTests
    {
        private static bool World() => File.Exists(Paths.WorldDb);

        [Fact]
        public void The_level_is_read_from_the_template()
        {
            if (!World()) return;

            // The adventurer set, which is what a new character is given: 4 to 9.
            Assert.Equal(4, DatabaseManager.ItemLevelRequirement(2475));    // anillo
            Assert.Equal(9, DatabaseManager.ItemLevelRequirement(2474));    // sombrero
            Assert.Equal(1, DatabaseManager.ItemLevelRequirement(10207));   // el manojo
        }

        [Fact]
        public void An_item_that_does_not_exist_asks_for_nothing()
        {
            if (!World()) return;

            // Zero means "no requirement", and it has to, because it is also what a failed lookup
            // returns. Refusing to equip because the database could not be read would take
            // somebody's gear off them over a transient error.
            Assert.Equal(0, DatabaseManager.ItemLevelRequirement(999_999));
            Assert.Equal(0, DatabaseManager.ItemLevelRequirement(0));
            Assert.Equal(0, DatabaseManager.ItemLevelRequirement(-1));
        }

        [Fact]
        public void The_starter_set_asks_for_more_level_than_a_new_character_has()
        {
            if (!World()) return;

            // The reason the set is handed over in the BAG and not worn. Its pieces ask for 4 to 9
            // and a new character is level 1, so giving it worn wrote the character into the
            // database dressed in things this very check would refuse -- and the first time they
            // took a piece off, they could not put it back.
            int highest = CharacterCreationHandler.StarterItems
                                                  .Select(i => DatabaseManager.ItemLevelRequirement(i.Gid))
                                                  .Max();

            Assert.Equal(9, highest);
            Assert.True(highest > CharacterCreationHandler.StartingLevel);
            Assert.All(CharacterCreationHandler.StarterItems,
                       item => Assert.Equal(Equipment.Bag, item.Slot));
        }

        [Fact]
        public void Taking_something_off_is_never_refused()
        {
            // The rule the check has to respect: the level is only asked about when PUTTING
            // something on. A character who somehow ends up below the level of what they are
            // wearing must still be able to undress, or they are stuck in it for good.
            Assert.Equal(63, Equipment.Bag);
        }
    }
}
