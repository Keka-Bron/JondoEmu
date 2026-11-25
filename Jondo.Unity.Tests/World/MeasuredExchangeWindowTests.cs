using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// «Trocar» (action 2) opened the exchange window on the official server (jyp, and nothing else),
    /// packet captures 2026-11-21 to 11-24. What each one trades was not measured.
    /// </summary>
    public sealed class MeasuredExchangeWindowTests
    {
        [Theory]
        [InlineData(1080)]
        [InlineData(94)]
        [InlineData(7913)]
        [InlineData(53)]
        [InlineData(2142)]
        [InlineData(91)]
        [InlineData(90)]
        [InlineData(99)]
        [InlineData(59)]
        public void Trocar_opens_the_exchange_window(int npc)
        {
            Assert.True(NpcBarterHandler.Barters(npc));
        }

        [Fact]
        public void Trocar_is_action_2()
        {
            Assert.Equal(2, Npcs.Exchange);
        }
    }
}
