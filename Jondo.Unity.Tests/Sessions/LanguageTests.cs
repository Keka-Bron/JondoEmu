using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// The language a client was launched with follows it to the session, and every command has
    /// something to say in all three.
    /// </summary>
    /// <remarks>
    /// These two arrived as startup guards in PR #15. They are pure code checks — no data file
    /// decides whether a catalogue has the same keys in three languages — so they live here, where
    /// they run in a second under `dotnet test`, and not on every single boot. Same rule that took
    /// the collision formula and the fight sheet out of the startup guard.
    /// </remarks>
    public class LanguageTests
    {
        [Fact]
        public void The_second_socket_keeps_the_language_the_first_one_chose()
        {
            // A session is bound over two sockets: one issues the ticket, another redeems it. The
            // language is chosen on the first and has to survive to the second, or every reply the
            // player gets comes back in the wrong language.
            SessionRegistry.AssertLanguageFollowsTicket();
        }

        [Fact]
        public void Every_command_text_exists_in_all_three_languages()
        {
            // A missing key does not throw when it is looked up: it falls back, and the player gets
            // one line in Spanish in the middle of a French conversation. Nobody reports that.
            CommandTexts.AssertCatalogs();
        }
    }
}
