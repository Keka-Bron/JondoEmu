using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// What a character carries goes out with every pods refresh: the purses of kamas, ten pods
    /// each, fifty of three kinds, and the bag read «0 pods sur 1 000». Every refresh but the
    /// world entry's sent zero.
    /// </summary>
    public class CarriedPodsTests
    {
        private const int Purse1000 = 8815, Purse5000 = 8814;

        [Fact]
        public void The_bag_weighs_what_it_holds()
        {
            int weight = DatabaseManager.GetItemRealWeight(Purse1000);
            if (weight == 0) return;   // no item data

            var state = new SessionState();
            state.EquipmentItems[1] = new Equipment.Item { Uid = 1, Template = Purse1000, Quantity = 50 };
            state.EquipmentItems[2] = new Equipment.Item { Uid = 2, Template = Purse5000, Quantity = 3 };

            Assert.Equal(50L * weight + 3L * DatabaseManager.GetItemRealWeight(Purse5000), ConnectionProtocol.Carried(state));
            Assert.Equal(0, ConnectionProtocol.Carried(new SessionState()));
        }

        /// <summary>
        /// What a character can carry: a thousand, five a level since the first -- the client's
        /// level-up window shows «Pods +5» --, five a level of each job, five a point of strength.
        /// It was the thousand and the strength alone.
        /// </summary>
        [Fact]
        public void The_capacity_grows_with_the_levels_the_jobs_and_strength()
        {
            var state = new SessionState { CharacterLevel = 53, StatStrength = 10 };
            state.Jobs[24] = new JobExperience.Progress { JobId = 24, Experience = JobExperience.Floor(10) };
            state.Jobs[26] = new JobExperience.Progress { JobId = 26, Experience = 0 };

            Assert.Equal(1000 + 5 * 52 + 5 * 9 + 5 * 10, ConnectionProtocol.Capacity(state));
            Assert.Equal(1000, ConnectionProtocol.Capacity(new SessionState()));   // a new character
        }
    }
}
