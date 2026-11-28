using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The capital characteristic letters of a criterion (the client's CharacteristicCriterion) ask
    /// the whole characteristic, points and scrolls together; the small ones only what the scrolls gave.
    /// </summary>
    /// <remarks>
    /// Cilia's replies in quest 1561 open by «CS&gt;…», «CI&gt;…» and the like: nobody answered those
    /// letters, and an unknown term let every reply through to everybody.
    /// </remarks>
    public class CharacteristicCriteriaTests
    {
        private const long Map = 191105026, Character = 8_900_115_610;

        private static GameSession Session()
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = Character;
            session.State.MapId = Map;
            session.State.CharacterLevel = 100;
            session.State.StatStrength = 100;
            session.State.ScrolledStrength = 50;
            session.State.StatIntelligence = 20;
            session.State.StatWisdom = 0;
            session.State.ScrolledWisdom = 10;
            return session;
        }

        [Fact]
        public void A_dialogue_reply_asks_the_whole_characteristic()
        {
            using var context = SessionContext.Push(Session());

            Assert.True(PlayerCriteria.Met("CS>149", Map));
            Assert.False(PlayerCriteria.Met("CS>150", Map));
            Assert.True(PlayerCriteria.Met("CI<21", Map));
            Assert.False(PlayerCriteria.Met("CI>20", Map));
            Assert.True(PlayerCriteria.Met("CW=10", Map));
            Assert.False(PlayerCriteria.Met("CA>0", Map));
        }

        [Fact]
        public void The_small_letters_still_count_only_the_scrolls()
        {
            using var context = SessionContext.Push(Session());

            Assert.Equal(50, ItemCriteria.ScrolledBy("cs", SessionContext.State));
            Assert.Equal(150, ItemCriteria.TotalBy("CS", SessionContext.State));
            Assert.Null(ItemCriteria.TotalBy("cs", SessionContext.State));
            Assert.True(ItemCriteria.Holds("cs=50&CS=150"));
            Assert.False(ItemCriteria.Holds("CS>150"));
        }
    }
}
