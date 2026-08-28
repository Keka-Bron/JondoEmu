using System;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The message that closes a window, and the reason it carries.
    /// </summary>
    /// <remarks>
    /// The X of an NPC conversation did not close it, and the packets say exactly why. The client
    /// pressed it three times — three <c>kla</c> in the traffic — and the server answered each one
    /// with a <c>kld</c>. So the button was heard and answered; what went out was the wrong reason.
    ///
    /// Ours said 10. In the captures, 98 of the <c>kld</c> that close a conversation say 1, and the
    /// code already had both numbers as named constants. The branch that should have used the right
    /// one was never reached: a second, earlier branch caught every <c>kla</c> first and sent it to
    /// the zaap, which uses the default.
    ///
    /// Two tests, because the bug had two halves: the number, and who gets asked.
    /// </remarks>
    public class DialogueCloseTests
    {
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        [Fact]
        public void Closing_a_conversation_says_reason_one()
        {
            // 0801. Measured: of the captured kld frames, 98 carry f1 = 1 and they are the ones
            // that follow an ios, which is the server offering replies — that is, a conversation
            // being closed.
            Assert.Equal("0801",
                Hex(ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason)));
        }

        [Fact]
        public void The_default_reason_is_not_the_conversation_one()
        {
            // 080a, which is what the zaap sends and what the conversation was sending by mistake.
            // Pinned so the two cannot quietly become the same number: if they did, this test would
            // pass while saying nothing, and the next person would have no way to tell that the
            // distinction ever mattered.
            Assert.Equal("080a", Hex(ConnectionProtocol.BuildDialogClosed()));
            Assert.NotEqual(ConnectionProtocol.NpcDialogCloseReason, 10);
        }
    }
}
