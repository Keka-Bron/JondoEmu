using System;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Security
{
    /// <summary>
    /// Freeing an account whose client is gone, without freeing one whose client is not.
    /// </summary>
    /// <remarks>
    /// A launch that had finished the Thrift handshake was immortal. The sweep skipped anything
    /// with an entry in the game-session table, that entry is written at handshake time, and
    /// nothing ever removed it — so a client that died AFTER the handshake, with its launcher
    /// closed too, left the account marked busy until the server was restarted, and every later
    /// attempt was refused with "cuenta-ya-abierta".
    ///
    /// The tempting fix is to free the launch when the game socket closes, and it opens two holes
    /// instead of closing one. Going back to character selection closes that socket — it is the
    /// back arrow, not quitting — so the account is freed while the client is still running: launch
    /// it again, and again, for as many simultaneous clients of one account as you like. And since
    /// the per-IP count is taken over the same table, it never climbs past one and the eight-client
    /// cap stops existing. Neither shows up in the admin panel, which reads that table too.
    ///
    /// So the rule here is: a launch is released only when it has been quiet for the timeout AND
    /// there is no connected socket for that account. Both, not either.
    ///
    /// This class and <c>GoingBackTokenTests</c> share a collection, and they have to: both empty and
    /// refill the SAME static launch registry —which is static because it has to
    /// be— and xUnit runs the classes in parallel. Here they crossed without breaking by luck of
    /// timing; in continuous integration they did not. Green at d703636 and red at the next commit,
    /// which is exactly the one that added the second class.
    /// </remarks>
    [Collection("ClientLaunchRegistry")]
    public class StaleLaunchTests : IDisposable
    {
        private const long Account = 90_001;

        public StaleLaunchTests() => ClientLaunchRegistry.ForgetEverything();
        public void Dispose() => ClientLaunchRegistry.ForgetEverything();

        private static ClientLaunchRegistry.Launch Launch(string hash)
            => ClientLaunchRegistry.Register(Account, "", hash, "es");

        [Fact]
        public void A_launch_that_just_started_is_left_alone()
        {
            Launch(Guid.NewGuid().ToString("N"));

            Assert.Equal(0, ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void One_that_has_gone_quiet_is_released()
        {
            // A zero timeout is "everything is stale", which is the state a dead client reaches
            // after five minutes. With no connected socket for the account, it goes.
            Launch(Guid.NewGuid().ToString("N"));

            Assert.Equal(1, ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.Zero));
        }

        [Fact]
        public void Even_after_the_handshake_it_can_still_be_released()
        {
            // The bug itself. Before, reaching the handshake made the launch immortal: the sweep
            // skipped anything present in the game-session table and nothing ever took it out.
            string hash = Guid.NewGuid().ToString("N");
            var launch = Launch(hash);

            Assert.True(ClientLaunchRegistry.TryConnect(launch.InstanceId, hash, out string session));
            Assert.NotEqual("", session);

            Assert.Equal(1, ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.Zero));
        }

        [Fact]
        public void Resolving_its_session_counts_as_a_sign_of_life()
        {
            // What a living client does over and over. After it, the launch is fresh again and a
            // sweep with a real timeout must not touch it.
            string hash = Guid.NewGuid().ToString("N");
            var launch = Launch(hash);
            ClientLaunchRegistry.TryConnect(launch.InstanceId, hash, out string session);

            Assert.True(ClientLaunchRegistry.TryGetByGameSession(session, out var found));
            Assert.Equal(Account, found!.AccountId);

            Assert.Equal(0, ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void And_the_account_is_free_again_afterwards()
        {
            // The point of the whole thing: once released, the player can get back in. Before this
            // that took a server restart.
            string hash = Guid.NewGuid().ToString("N");
            Launch(hash);
            ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.Zero);

            var second = ClientLaunchRegistry.Register(Account, "", Guid.NewGuid().ToString("N"), "es");
            Assert.NotNull(second);
            Assert.Equal(Account, second.AccountId);
        }

        /// <summary>A launch through the handshake, then released by the sweep: its session.</summary>
        private static (ClientLaunchRegistry.Launch Launch, string Session) Released(string hash)
        {
            var launch = Launch(hash);
            Assert.True(ClientLaunchRegistry.TryConnect(launch.InstanceId, hash, out string session));
            Assert.Equal(1, ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.Zero));
            return (launch, session);
        }

        [Fact]
        public void A_released_client_that_comes_back_with_its_session_gets_its_launch_back()
        {
            // Back at the title screen, the client sends nothing until «Jouer»: the sweep released it,
            // and «Jouer» hung on an unknown session until the game was started again.
            var (launch, session) = Released(Guid.NewGuid().ToString("N"));

            Assert.True(ClientLaunchRegistry.TryGetByGameSession(session, out var found));
            Assert.Same(launch, found);
            Assert.True(ClientLaunchRegistry.TryGetByAccount(Account, out _));

            // And the account is busy again: no second client of it.
            Assert.Throws<InvalidOperationException>(
                () => ClientLaunchRegistry.Register(Account, "", Guid.NewGuid().ToString("N"), "es"));
        }

        [Fact]
        public void Not_once_the_account_has_been_launched_again()
        {
            var (_, session) = Released(Guid.NewGuid().ToString("N"));
            var second = ClientLaunchRegistry.Register(Account, "", Guid.NewGuid().ToString("N"), "es");

            Assert.False(ClientLaunchRegistry.TryGetByGameSession(session, out _));
            Assert.True(ClientLaunchRegistry.TryGetByAccount(Account, out var current));
            Assert.Same(second, current);
        }

        [Fact]
        public void Nor_once_its_launcher_has_said_it_exited()
        {
            string hash = Guid.NewGuid().ToString("N");
            var (launch, session) = Released(hash);
            ClientLaunchRegistry.RemoveByAccount(Account, launch.InstanceId, hash);

            Assert.False(ClientLaunchRegistry.TryGetByGameSession(session, out _));
        }

        [Fact]
        public void A_second_launch_of_a_live_account_is_still_refused()
        {
            // The guard that must survive all of this. Without releasing anything, the same
            // account cannot be launched twice.
            Launch(Guid.NewGuid().ToString("N"));

            Assert.Throws<InvalidOperationException>(
                () => ClientLaunchRegistry.Register(Account, "", Guid.NewGuid().ToString("N"), "es"));
        }
    }
}
