using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

[Collection("ClientLaunchRegistry")]
public sealed class LaunchCleanupTests : IDisposable
{
    private readonly List<ClientLaunchRegistry.Launch> _launches = new();

    private ClientLaunchRegistry.Launch Launch(long account)
    {
        var launch = ClientLaunchRegistry.Register(account, "cleanup-token-" + account,
            Guid.NewGuid().ToString("N"), "en", "cleanup-tests");
        _launches.Add(launch);
        return launch;
    }

    public void Dispose()
    {
        foreach (var launch in _launches) ClientLaunchRegistry.Remove(launch);
    }

    private static ControlApi.Respuesta Finish(string token, int instanceId, string hash)
        => ControlApi.Responder(Contract.Prefijo + "fin-de-lanzamiento", "POST",
            JsonSerializer.Serialize(new { token, instancia = instanceId, hash }))!.Value;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_launch_tokens_fail_before_client_discovery_or_network_requests(string? token)
    {
        var result = LauncherService.LaunchClient(token);
        Assert.False(result.Success);
        Assert.Equal(Jondo.Unity.Launcher.UI.LauncherPreferences.Textos.SessionExpiredError, result.Message);
    }

    [Fact]
    public void Concurrent_exit_and_failure_cleanup_release_only_the_launched_account_once()
    {
        var first = Launch(98_001);
        var second = Launch(98_002);
        int notifications = 0;
        var release = LauncherService.LaunchCleanup(first.AccountId, first.InstanceId, first.LauncherToken, first.Hash,
            (token, instanceId, hash) =>
            {
                Interlocked.Increment(ref notifications);
                Assert.Equal(first.LauncherToken, token);
                Assert.Equal(first.InstanceId, instanceId);
                Assert.Equal(first.Hash, hash);
                Assert.Equal(200, Finish(token, instanceId, hash).Codigo);
            });

        Parallel.For(0, 32, _ => release());

        Assert.Equal(1, notifications);
        Assert.False(ClientLaunchRegistry.TryGetByAccount(first.AccountId, out _));
        Assert.False(ClientLaunchRegistry.TryConnect(first.InstanceId, first.Hash, out _));
        Assert.True(ClientLaunchRegistry.TryGetByAccount(second.AccountId, out var remaining));
        Assert.Same(second, remaining);
    }

    [Fact]
    public void Delayed_cleanup_and_stale_registry_references_cannot_remove_a_new_launch()
    {
        var first = Launch(98_003);
        Assert.Equal(200, Finish(first.LauncherToken, first.InstanceId, first.Hash).Codigo);
        var replacement = Launch(first.AccountId);

        Assert.Equal(200, Finish(first.LauncherToken, first.InstanceId, first.Hash).Codigo);
        ClientLaunchRegistry.Remove(first);

        Assert.True(ClientLaunchRegistry.TryGetByAccount(first.AccountId, out var remaining));
        Assert.Same(replacement, remaining);
        Assert.True(ClientLaunchRegistry.TryConnect(replacement.InstanceId, replacement.Hash, out _));
    }

    [Fact]
    public void A_token_cannot_release_another_accounts_instance()
    {
        var first = Launch(98_004);
        var second = Launch(98_005);

        Assert.Equal(200, Finish(first.LauncherToken, second.InstanceId, second.Hash).Codigo);

        Assert.True(ClientLaunchRegistry.TryGetByAccount(first.AccountId, out _));
        Assert.True(ClientLaunchRegistry.TryGetByAccount(second.AccountId, out _));
    }

    [Fact]
    public void A_release_without_an_instance_is_rejected()
    {
        var launch = Launch(98_006);
        Assert.Equal(400, Finish(launch.LauncherToken, 0, launch.Hash).Codigo);
        Assert.True(ClientLaunchRegistry.TryGetByAccount(launch.AccountId, out _));
    }

    [Fact]
    public void A_reused_instance_number_still_requires_the_new_launch_hash()
    {
        var launch = Launch(98_007);
        // After a server restart the account token and numeric instance can match an old launch.
        string oldHash = Guid.NewGuid().ToString("N");
        Assert.Equal(200, Finish(launch.LauncherToken, launch.InstanceId, oldHash).Codigo);
        Assert.Equal(400, Finish(launch.LauncherToken, launch.InstanceId, "").Codigo);
        Assert.True(ClientLaunchRegistry.TryGetByAccount(launch.AccountId, out var remaining));
        Assert.Same(launch, remaining);
        Assert.Equal(200, Finish(launch.LauncherToken, launch.InstanceId, launch.Hash).Codigo);
        Assert.False(ClientLaunchRegistry.TryGetByAccount(launch.AccountId, out _));
    }

    [Fact]
    public async Task Removal_waits_for_an_inflight_handshake_and_removes_its_session()
    {
        var launch = Launch(98_008);
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishHandshake = new ManualResetEventSlim();
        string session = "controlled-session-" + Guid.NewGuid().ToString("N");
        Task<bool> connect = Task.Factory.StartNew(() => ClientLaunchRegistry.TryConnect(
            launch.InstanceId, launch.Hash, out _, () =>
            {
                creating.SetResult();
                if (!finishHandshake.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return session;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task remove = Task.CompletedTask;
        try
        {
            await creating.Task.WaitAsync(TimeSpan.FromSeconds(10));
            remove = Task.Factory.StartNew(() =>
            {
                removing.SetResult();
                ClientLaunchRegistry.Remove(launch);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await removing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(remove, await Task.WhenAny(remove, Task.Delay(100)));
        }
        finally
        {
            finishHandshake.Set();
            await Task.WhenAll(connect, remove).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(await connect);
        Assert.False(ClientLaunchRegistry.TryGetByGameSession(session, out _));
        Assert.False(ClientLaunchRegistry.TryConnect(launch.InstanceId, launch.Hash, out _));
    }
}
