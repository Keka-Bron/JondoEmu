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
        public void The_rest_of_the_adventurer_set_is_still_worn()
        {
            // The keyring must not have been added by turning everything else into bag items: the
            // six pieces of set 5 go into slots 0, 2, 3, 4, 6 and 7.
            var worn = CharacterCreationHandler.StarterItems
                                               .Where(item => item.Gid != DungeonHandler.Keyring)
                                               .ToList();

            Assert.Equal(6, worn.Count);
            Assert.All(worn, item => Assert.NotEqual(Equipment.Bag, item.Slot));
            Assert.Equal(new[] { 0, 2, 3, 4, 6, 7 }, worn.Select(i => i.Slot).OrderBy(s => s));
        }
    }
}
