using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// The cache of what a character is wearing, which is what the COMBAT sheet is built from.
    /// </summary>
    /// <remarks>
    /// Not the characteristic sheet — that one is <c>Equipment.Bonuses()</c> over the real
    /// inventory and was never stale. This dictionary feeds <c>StatsHandler.GetEquipBonus</c>, so
    /// what it says is the player's maximum health and initiative in a fight.
    /// </remarks>
    public class EquipmentCacheTests
    {
        [Fact]
        public void Moving_an_item_updates_and_removes_its_cached_bonuses()
        {
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                const long uid = 123456;
                Equipment.Add(uid, template: 1, quantity: 1, Equipment.Bag,
                    "[[111,3,0,0],[128,2,0,0]]");

                Assert.True(Equipment.Move(uid, 0));
                Equipment.RememberWorn(uid, 0, Equipment.ByUid(uid)!.Effects);

                var worn = session.State.GetEquippedItemsCopy();
                Assert.Equal(0, worn[uid].Slot);
                Assert.Equal(3, worn[uid].Stats[111]);
                Assert.Equal(2, worn[uid].Stats[128]);

                Assert.True(Equipment.Move(uid, Equipment.Bag));
                Equipment.RememberWorn(uid, Equipment.Bag, Equipment.ByUid(uid)!.Effects);

                Assert.DoesNotContain(uid, session.State.GetEquippedItemsCopy());
            }
        }

        [Fact]
        public void A_weapon_s_damage_is_no_characteristic_of_the_same_number()
        {
            // Effect 96 is a weapon's water damage and characteristic 96 the shield: the Hacha Jazo's
            // «46 a 60 de daños de agua» put its wearer behind 60 points of shield every fight.
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                const long uid = 888_096;
                Equipment.Add(uid, template: 14289, quantity: 1, 1, "[[96,0,46,60],[125,350,0,0]]");
                Equipment.RememberWorn(uid, 1, Equipment.ByUid(uid)!.Effects);

                Assert.Equal(0, Jondo.Unity.Server.Handlers.StatsHandler.GetEquipBonus(EffectEngine.ShieldCharacteristic));
                Assert.Equal(350, Jondo.Unity.Server.Handlers.StatsHandler.GetEquipBonus(11));
            }
        }

        [Fact]
        public void Moving_an_item_moves_it_in_the_session_s_older_list_too()
        {
            // An equip from the client went through Equipment and the base only, and the login's
            // list kept the hammer in the bag: the hit read the hand off it and went out as a punch.
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                const long uid = 555_001;
                Equipment.Add(uid, template: 18018, quantity: 1, Equipment.Bag, "[[100,0,52,60]]");
                session.State.AddInventoryItem(new Jondo.Unity.Server.PlayerItem { Uid = uid, ItemId = 18018, Quantity = 1, Position = Equipment.Bag });

                Assert.True(Equipment.Move(uid, 1));

                Assert.Equal(1, session.State.GetInventoryItem(uid)!.Position);
                Assert.Equal(18018, Equipment.InSlot(1)!.Template);
            }
        }

        [Fact]
        public void A_slot_in_the_bag_is_not_worn_however_low_its_number()
        {
            // The rule the three writers of this cache used to disagree on. Two of them called
            // anything from 0 to 62 "worn", so an item parked in slot 35 counted towards combat
            // stats at login and stopped counting the moment it was moved — the same item, two
            // answers, depending on nothing the player did.
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                const long uid = 654321;
                Equipment.Add(uid, template: 1, quantity: 1, 35, "[[111,9,0,0]]");
                Equipment.RememberWorn(uid, 35, Equipment.ByUid(uid)!.Effects);

                Assert.DoesNotContain(uid, session.State.GetEquippedItemsCopy());
                Assert.False(Equipment.IsWorn(35));
            }
        }

        [Fact]
        public void An_item_nothing_is_known_about_leaves_a_worn_slot_alone()
        {
            // Being told "this uid is now in slot 0" without knowing what it is must not blank the
            // entry: Equipment.LoadFrom swallows its own exception, so an inventory that failed to
            // read leaves Items empty while the cache is already full from character selection.
            // Writing an empty entry there would strip the stats of gear still being worn.
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                const long uid = 777;
                Equipment.Add(uid, template: 1, quantity: 1, 0, "[[111,4,0,0]]");
                Equipment.RememberWorn(uid, 0, Equipment.ByUid(uid)!.Effects);
                Assert.Equal(4, session.State.GetEquippedItemsCopy()[uid].Stats[111]);

                Equipment.RememberWorn(uid, 0, (System.Collections.Generic.IEnumerable<Equipment.ItemEffect>?)null);
                Assert.Equal(4, session.State.GetEquippedItemsCopy()[uid].Stats[111]);

                // But an unknown item leaving a worn slot is still forgotten, which is the case
                // that would otherwise let somebody fight in stats they had taken off.
                Equipment.RememberWorn(uid, Equipment.Bag, (System.Collections.Generic.IEnumerable<Equipment.ItemEffect>?)null);
                Assert.DoesNotContain(uid, session.State.GetEquippedItemsCopy());
            }
        }
    }
}
