using Jondo.Unity.Launcher;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

public sealed class LauncherActivityCacheTests
{
    [Fact]
    public void LocalClientIsActiveUntilItsProcessStops()
    {
        const long accountId = long.MaxValue - 271;

        LauncherService.MarkLocalClientStopped(accountId);
        Assert.False(LauncherService.IsActive(accountId));

        try
        {
            LauncherService.MarkLocalClientStarted(accountId);
            Assert.True(LauncherService.IsActive(accountId));
        }
        finally
        {
            LauncherService.MarkLocalClientStopped(accountId);
        }

        Assert.False(LauncherService.IsActive(accountId));
    }
}
