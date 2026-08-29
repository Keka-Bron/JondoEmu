using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Everybody gets the keyring: the characters that already existed and the ones made from now on.
    /// </summary>
    /// <remarks>
    /// In the real game the tutorial hands it over — translation 1111691, "Toma este manojo de
    /// llaves mágicas: te abrirá las puertas de las mazmorras" — and this server does not run that
    /// tutorial. Without it a player can only enter a dungeon by crafting its loose key, so 107 of
    /// the 187 dungeons stay shut for a reason that has nothing to do with the game.
    ///
    /// Two halves, and both are in the database rather than handed out on login: the starter set
    /// for new characters, and a sweep at boot for the ones already there.
    /// </remarks>
    public class KeyringGrantTests
    {
        [Fact]
        public void A_new_character_starts_with_it_in_the_bag()
        {
            // In the BAG and not worn. Everything else in the adventurer set goes into a slot;
            // this one is a quest item that is never equipped and never spent.
            var starter = CharacterCreationHandler.StarterItems;

            Assert.Contains(starter, item => item.Gid == DungeonHandler.Keyring);
            Assert.Equal(Equipment.Bag,
                         starter.First(item => item.Gid == DungeonHandler.Keyring).Slot);
        }

        [Fact]
        public void And_it_is_the_item_the_dungeon_door_actually_looks_for()
        {
            // The one thing that could quietly break this: granting a different number from the one
            // TryTakeTheKey searches the bag for. They are the same constant, and this says so.
            Assert.Equal(10207, DungeonHandler.Keyring);
        }

        [Fact]
        public void Everything_a_new_character_gets_goes_into_the_bag()
        {
            // Including the adventurer set, which used to arrive already worn. Its six pieces ask
            // for level 4 to 9 and a new character is level 1, and writing them straight into the
            // database dressed the character in things the equip check would refuse -- so the
            // first time they took a piece off they could not put it back.
            //
            // A gift rather than a uniform: it sits in the bag and each piece goes on when the
            // level says so.
            var starter = CharacterCreationHandler.StarterItems;

            Assert.Equal(7, starter.Count);
            Assert.All(starter, item => Assert.Equal(Equipment.Bag, item.Slot));
        }

        [Fact]
        public void The_six_pieces_of_the_set_are_all_still_there()
        {
            // Moving them into the bag must not have lost any: set 5 is the amulet, ring, belt,
            // boots, hat and cloak.
            var set = CharacterCreationHandler.StarterItems
                                              .Where(item => item.Gid != DungeonHandler.Keyring)
                                              .Select(item => item.Gid)
                                              .OrderBy(gid => gid)
                                              .ToArray();

            Assert.Equal(new[] { 2473, 2474, 2475, 2476, 2477, 2478 }, set);
        }
    }
}
