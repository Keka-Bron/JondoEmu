using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    public class WindowCloseRoutingTests
    {
        [Fact]
        public void An_open_zaap_takes_priority_over_stale_npc_dialogue_state()
        {
            Assert.Equal(GameNodeProxy.CloseTarget.Zaap,
                         GameNodeProxy.ResolveCloseTarget(false, false, true));
        }

        [Fact]
        public void A_plain_dialogue_close_reaches_the_npc_handler()
        {
            Assert.Equal(GameNodeProxy.CloseTarget.NpcDialogue,
                         GameNodeProxy.ResolveCloseTarget(false, false, false));
        }
    }
}
