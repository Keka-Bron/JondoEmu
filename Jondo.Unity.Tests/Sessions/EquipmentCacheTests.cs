using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
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
                EquipmentHandler.RefreshCachedBonuses(uid, 0);

                var worn = session.State.GetEquippedItemsCopy();
                Assert.Equal(0, worn[uid].Slot);
                Assert.Equal(3, worn[uid].Stats[111]);
                Assert.Equal(2, worn[uid].Stats[128]);

                Assert.True(Equipment.Move(uid, Equipment.Bag));
                EquipmentHandler.RefreshCachedBonuses(uid, Equipment.Bag);

                Assert.DoesNotContain(uid, session.State.GetEquippedItemsCopy());
            }
        }
    }
}
