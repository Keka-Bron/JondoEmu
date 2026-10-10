using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Gigalodón's floors: the lifts and passages read off the client's maps, the Luminarium's
    /// board of lantern fish, and the key fragments that open the last floor (the guides).
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidFloorsTests : IDisposable
    {
        private readonly string _file;

        public GuildRaidFloorsTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raidfloors-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidBoard.Forget();
            GuildRaidManager.Forget();
        }

        public void Dispose()
        {
            GuildRaidManager.Forget();
            GuildRaidBoard.Forget();
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        /// <summary>A raid of the Abyss with 7001 and 7002 inside, set up by hand.</summary>
        private static RaidInstance Inside()
        {
            var raid = new RaidInstance(77, Raids.Gigalodon, 1, 7001, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.Add(7001);
            raid.Add(7002);
            GuildRaidManager.Remember(raid);
            return raid;
        }

        /// <summary>A press switches the fish and its neighbours; a shuffled board is never solved already.</summary>
        [Fact]
        public void A_press_switches_the_fish_and_its_neighbours()
        {
            var lit = new bool[16];
            GuildRaidLuminarium.Press(lit, 5);                        // row 1, column 1
            Assert.Equal(new[] { 1, 4, 5, 6, 9 }, Enumerable.Range(0, 16).Where(i => lit[i]));
            GuildRaidLuminarium.Press(lit, 0);                        // a corner switches itself and two neighbours
            Assert.Equal(new[] { 0, 5, 6, 9 }, Enumerable.Range(0, 16).Where(i => lit[i]));
            Assert.False(GuildRaidLuminarium.Shuffled(new Random(3)).All(l => l));
        }

        /// <summary>
        /// The Mureine gives the second fragment and the Exécrabe the third; the first and the fourth
        /// come by chance. With the four the last floor opens.
        /// </summary>
        [Fact]
        public async Task The_four_fragments_open_the_last_floor()
        {
            var raid = Inside();
            var fighters = new long[] { 7001, 7002 };
            await GuildRaidManager.AwardFragmentsAsync(raid, fighters, new Dictionary<int, int> { [8333] = 1 }, _ => false);
            await GuildRaidManager.AwardFragmentsAsync(raid, fighters, new Dictionary<int, int> { [8332] = 1 }, _ => false);
            Assert.Equal(new[] { 2, 3 }, raid.Fragments.OrderBy(f => f));
            Assert.False(raid.IsOpen(6));

            await GuildRaidManager.AwardFragmentsAsync(raid, fighters, new Dictionary<int, int> { [GuildRaidManager.KrakHaine] = 1 }, _ => true);
            Assert.Equal(new[] { 1, 2, 3, 4 }, raid.Fragments.OrderBy(f => f));
            Assert.True(raid.IsOpen(6));
            Assert.Equal(1, raid.GoalAt(32));

            Assert.Equal(0.01, GuildRaidManager.FirstFragmentChance(4_999));
            Assert.Equal(0.05, GuildRaidManager.FirstFragmentChance(5_000));
            Assert.Equal(0.10, GuildRaidManager.FirstFragmentChance(7_000));
            Assert.Equal(0.20, GuildRaidManager.FirstFragmentChance(10_001));
        }
    }
}
