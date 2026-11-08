using System.Net;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Bots;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Diagnostics
{
    /// <summary>
    /// The simulated players log nothing: a hundred and fifty of them filled the server window with
    /// their packets, steps and blows. What they do is dropped by flow and by session.
    /// </summary>
    public class QuietBotsTests
    {
        [Fact]
        public async Task A_quiet_flow_is_quiet_and_its_caller_is_not()
        {
            Assert.False(ConsoleLogBuffer.Quiet);

            bool inside = await Task.Run(async () =>
            {
                ConsoleLogBuffer.QuietFromHere();
                await Task.Yield();
                return ConsoleLogBuffer.Quiet;
            });

            Assert.True(inside);
            Assert.False(ConsoleLogBuffer.Quiet);
        }

        [Fact]
        public void A_bots_session_is_quiet_out_of_a_fight_and_a_players_is_not()
        {
            var bot = GameSession.SinSocket();
            bot.Quiet = true;
            using (SessionContext.Push(bot)) Assert.True(ConsoleLogBuffer.Quiet);

            using (SessionContext.Push(GameSession.SinSocket())) Assert.False(ConsoleLogBuffer.Quiet);
        }

        [Fact]
        public void A_timer_among_bots_is_quiet_while_it_lasts()
        {
            using (ConsoleLogBuffer.QuietWhile(true)) Assert.True(ConsoleLogBuffer.Quiet);
            Assert.False(ConsoleLogBuffer.Quiet);
            using (ConsoleLogBuffer.QuietWhile(false)) Assert.False(ConsoleLogBuffer.Quiet);
            Assert.False(SimulatedPlayers.OnlyBots(new long[0]));
        }

        [Fact]
        public void A_socket_nobody_dialled_is_not_a_bots()
        {
            Assert.False(SimulatedPlayers.IsBotSocket(new IPEndPoint(IPAddress.Loopback, 1)));
            Assert.False(SimulatedPlayers.IsBotSocket(null));
        }
    }
}
