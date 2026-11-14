using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using JobExperience = Jondo.Unity.Server.Managers.JobExperience;
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

        [Fact]
        public async Task Typed_allocation_entry_and_sheet_keep_owned_weight_omega_levels_and_job_capacity()
        {
            var frames = new List<GameFrame>();
            var codec = new GameCodec();
            var session = new GameSession(codec, bytes => { frames.Add(codec.Decode(bytes)); return Task.CompletedTask; });
            session.BindAccount(8_967_000_001, 1);
            session.State.CharacterId = 9_967_000_001;
            session.State.CharacterLevel = 354;
            session.State.Breed = 1;
            session.State.ScrolledStrength = 20;
            session.State.Jobs[24] = new JobExperience.Progress { JobId = 24, Experience = JobExperience.Floor(10) };
            session.State.EquipmentItems[1] = new Equipment.Item { Uid = 1, Template = Purse1000, Quantity = 3, Position = Equipment.Bag };
            session.EnterWorld();
            var wrong = GameSession.SinSocket();
            wrong.State.StatStrength = 999;
            using var context = SessionContext.Push(wrong);

            Assert.True(await new CharacteristicsHandler(_ => { }, CharacteristicsMessages.Live)
                .SpendAsync(session, new(0, 0, 0, 0, 0, 10)));

            int itemWeight = DatabaseManager.GetItemRealWeight(Purse1000);
            Assert.True(itemWeight > 0);
            var weight = Assert.IsType<InventoryWeightEvent>(frames[0].Content);
            Assert.Equal(new InventoryWeightEvent(3 * itemWeight, 2960), weight);
            Assert.Equal(weight, AfterSelectionMessages.Live.Weight(session.State));
            var sheet = Assert.IsType<CharacterCharacteristicsEvent>(frames[1].Content).Stats;
            Assert.Equal(2960, Assert.IsType<CharacterCharacteristicCaseDetailed>(sheet.Characteristics
                .Single(stat => stat.CharacteristicId == Stat.Pods).CharacteristicCase).Value.Base);
            Assert.Same(wrong, SessionContext.Actual);
        }
    }
}
