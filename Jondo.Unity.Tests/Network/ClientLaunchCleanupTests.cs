using System.Threading;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ClientLaunchRegistryCollection
    {
        public const string Name = "Client launch registry";
    }

    [Collection(ClientLaunchRegistryCollection.Name)]
    public class ClientLaunchCleanupTests
    {
        private static long _nextAccountId = 9_000_000_000;

        [Fact]
        public void Disconnect_releases_the_launch_owned_by_that_game_socket()
        {
            long accountId = Interlocked.Increment(ref _nextAccountId);
            var launch = ClientLaunchRegistry.Register(
                accountId, "token", Guid.NewGuid().ToString("N"), "fr");
            var session = BoundSession(accountId, launch.InstanceId);

            try
            {
                Assert.True(SessionRegistry.Register(session));
                Assert.True(ClientLaunchRegistry.IsActive(accountId));

                Assert.True(SessionRegistry.Unregister(session));

                Assert.False(ClientLaunchRegistry.IsActive(accountId));
            }
            finally
            {
                SessionRegistry.Unregister(session);
                ClientLaunchRegistry.RemoveByAccount(accountId);
            }
        }

        [Fact]
        public void Late_disconnect_cannot_release_a_newer_launch_of_the_same_account()
        {
            long accountId = Interlocked.Increment(ref _nextAccountId);
            var oldLaunch = ClientLaunchRegistry.Register(
                accountId, "old-token", Guid.NewGuid().ToString("N"), "fr");
            var oldSession = BoundSession(accountId, oldLaunch.InstanceId);

            try
            {
                Assert.True(SessionRegistry.Register(oldSession));
                ClientLaunchRegistry.RemoveByAccount(accountId);
                var newLaunch = ClientLaunchRegistry.Register(
                    accountId, "new-token", Guid.NewGuid().ToString("N"), "fr");

                Assert.True(SessionRegistry.Unregister(oldSession));

                Assert.True(ClientLaunchRegistry.TryGetByAccount(accountId, out var active));
                Assert.Equal(newLaunch.InstanceId, active?.InstanceId);
            }
            finally
            {
                SessionRegistry.Unregister(oldSession);
                ClientLaunchRegistry.RemoveByAccount(accountId);
            }
        }

        [Fact]
        public void Zaap_handshake_without_a_bound_game_socket_expires()
        {
            long accountId = Interlocked.Increment(ref _nextAccountId);
            string hash = Guid.NewGuid().ToString("N");
            var launch = ClientLaunchRegistry.Register(accountId, "token", hash, "fr");

            try
            {
                Assert.True(ClientLaunchRegistry.TryConnect(
                    launch.InstanceId, hash, out string gameSession));
                Assert.True(ClientLaunchRegistry.TryGetByGameSession(gameSession, out _));

                Assert.Equal(1, ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.Zero));

                Assert.False(ClientLaunchRegistry.IsActive(accountId));
                Assert.False(ClientLaunchRegistry.TryGetByGameSession(gameSession, out _));
            }
            finally
            {
                ClientLaunchRegistry.RemoveByAccount(accountId);
            }
        }

        [Fact]
        public void Bound_game_socket_prevents_launch_expiration()
        {
            long accountId = Interlocked.Increment(ref _nextAccountId);
            var launch = ClientLaunchRegistry.Register(
                accountId, "token", Guid.NewGuid().ToString("N"), "fr");
            var session = BoundSession(accountId, launch.InstanceId);

            try
            {
                Assert.True(SessionRegistry.Register(session));

                Assert.Equal(0, ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.Zero));
                Assert.True(ClientLaunchRegistry.IsActive(accountId));
            }
            finally
            {
                SessionRegistry.Unregister(session);
                ClientLaunchRegistry.RemoveByAccount(accountId);
            }
        }

        private static GameSession BoundSession(long accountId, int launchInstanceId)
        {
            var ticket = SessionRegistry.Issue(
                accountId, serverId: 1, language: "fr",
                launchInstanceId: launchInstanceId);
            var redeemed = SessionRegistry.Redeem(ticket.Value);
            Assert.NotNull(redeemed);

            var session = GameSession.SinSocket();
            session.BindAccount(
                redeemed.AccountId, redeemed.ServerId, redeemed.Language,
                redeemed.LaunchInstanceId);
            return session;
        }
    }
}
