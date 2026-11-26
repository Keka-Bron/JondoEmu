using System;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// A quest step that asks for a cleared map: once its last group falls, the map stays empty for a
    /// while (so the quest's element can be used) and then gets its groups back.
    /// </summary>
    public sealed class MapClearedHoldTests
    {
        [Fact]
        public async Task A_cleared_map_stays_empty_and_is_released_later()
        {
            const long map = 999_000_123;   // no groups written there
            MobSpawnManager.LeaveClearedFor(map, TimeSpan.FromMilliseconds(150));
            Assert.False(MobSpawnManager.HasGroups(map));
            await Task.Delay(600);
            Assert.False(MobSpawnManager.HasGroups(map));
            Assert.Equal(0, MobSpawnManager.LoadedGroups(map));
        }
    }
}
