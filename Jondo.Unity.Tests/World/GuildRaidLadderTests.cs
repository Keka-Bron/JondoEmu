using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The guild raids' weekly ladder: the raids tab of the ladder window, the reward each place
    /// earns -- chosen as the client's tab chooses it -- and handing it out once the week closed.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidLadderTests : IDisposable
    {
        private readonly string _file;

        public GuildRaidLadderTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raidladder-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        /// <summary>
        /// One position, a range of positions, or a share of the guilds that scored: the Gigalodón's
        /// first place is reward 2, places 4 to 10 reward 5, 11 to 50 reward 6, then the best half
        /// reward 7 and everyone else reward 13.
        /// </summary>
        [Fact]
        public void Each_place_earns_the_reward_the_client_shows()
        {
            int? Of(int place, int scored) => GuildRaidLadder.RewardFor(Raids.Gigalodon, place, scored)?.Id;
            Assert.Equal(2, Of(1, 3));
            Assert.Equal(4, Of(3, 3));
            Assert.Equal(5, Of(7, 200));
            Assert.Equal(6, Of(50, 200));
            Assert.Equal(7, Of(51, 200));
            Assert.Equal(13, Of(150, 200));
            Assert.Null(Of(0, 10));
        }

        /// <summary>The ladder's week is the frieze's: it opens at the measured reset, Tuesday 05:00 UTC.</summary>
        [Fact]
        public void The_ladder_week_opens_at_the_reset()
        {
            var tuesdayEarly = new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero);
            Assert.Equal("2026-09-29", GuildStore.WeekOf(tuesdayEarly));
            Assert.Equal(GuildRaidRewards.WeekOf(tuesdayEarly.AddHours(2)), GuildStore.WeekOf(tuesdayEarly.AddHours(2)));
        }

        /// <summary>
        /// The tab's answer (hwu): per raid, f1 this week's ladder {f1 guilds that scored, f2 lines
        /// with place, score, duration in ms and guild}, and f4 the player's guild's line.
        /// </summary>
        [Fact]
        public void The_tab_gets_each_raid_s_ladder()
        {
            var now = DateTimeOffset.UtcNow;
            var jondo = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            var other = GuildStore.Create(7100, "Otro", 165, 8, 16744448, 9476018);
            GuildStore.RecordRaidScore(other.Id, Raids.Gigalodon, 9000, now, 1_800_000);
            GuildStore.RecordRaidScore(jondo.Id, Raids.Gigalodon, 4000, now, 3_600_000);

            var frame = Fields(GuildRaidProtocol.BuildRaidLadder(jondo.Id, now));
            var thisWeek = frame.Where(f => f.FieldNumber == 1).Select(f => Fields(f.BytesValue))
                                .Single(e => e.First(f => f.FieldNumber == 1).VarIntValue == Raids.Gigalodon);
            var ladder = Fields(thisWeek.First(f => f.FieldNumber == 2).BytesValue);
            Assert.Equal(2, ladder.First(f => f.FieldNumber == 1).VarIntValue);
            var lines = ladder.Where(f => f.FieldNumber == 2).Select(f => Fields(f.BytesValue)).ToList();
            Assert.Equal(new long[] { 1, 2 }, lines.Select(l => l.First(f => f.FieldNumber == 1).VarIntValue));
            Assert.Equal(9000, lines[0].First(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(1_800_000, lines[0].First(f => f.FieldNumber == 4).VarIntValue);

            var mine = frame.Where(f => f.FieldNumber == 4).Select(f => Fields(f.BytesValue)).Single();
            Assert.Equal(Raids.Gigalodon, mine.First(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(2, Fields(mine.First(f => f.FieldNumber == 2).BytesValue).First(f => f.FieldNumber == 1).VarIntValue);
        }

        /// <summary>
        /// At equal score the faster run ranks first: the Sanctuary's 50,000 is a ceiling, and its
        /// ties are broken by time (the guide's comments).
        /// </summary>
        [Fact]
        public void Ties_are_broken_by_time()
        {
            var now = DateTimeOffset.UtcNow;
            var slow = GuildStore.Create(7001, "Lenta", 165, 8, 16744448, 9476018);
            var fast = GuildStore.Create(7100, "Rapida", 165, 8, 16744448, 9476018);
            GuildStore.RecordRaidScore(slow.Id, Raids.EternalGardens, 50_000, now, 7_000_000);
            GuildStore.RecordRaidScore(fast.Id, Raids.EternalGardens, 50_000, now.AddMinutes(5), 5_000_000);
            Assert.Equal(new[] { "Rapida", "Lenta" }, GuildStore.Ladder(Raids.EternalGardens, now).Select(r => r.Name));
        }

        /// <summary>
        /// Last week's first place: the player who scored gets the reward once, and his guild its
        /// experience once.
        /// </summary>
        [Fact]
        public async Task Last_week_s_place_is_paid_once()
        {
            var now = DateTimeOffset.UtcNow;
            var lastWeek = now.AddDays(-7);
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.Join(7002, guild.Id);
            GuildStore.RecordRaidScore(guild.Id, Raids.Gigalodon, 9000, lastWeek, 1_000);
            GuildRaidRewards.RecordRun(7001, Raids.Gigalodon, 9000, lastWeek);
            GuildRaidRewards.RecordRun(7002, Raids.Gigalodon, 9000, lastWeek);

            var paid = await GuildRaidLadder.DeliverAsync(null, 7001, now);
            Assert.Equal(2, paid.Single().Reward.Id);
            Assert.Empty(await GuildRaidLadder.DeliverAsync(null, 7001, now));
            Assert.Single(await GuildRaidLadder.DeliverAsync(null, 7002, now));
            Assert.Equal(20, GuildStore.GuildOf(7001).Experience);
            Assert.Empty(await GuildRaidLadder.DeliverAsync(null, 7003, now));       // no guild, no score

            var chat = Fields(GuildRaidProtocol.BuildLadderRewardsObtained(paid));
            var entry = Fields(chat.Single(f => f.FieldNumber == 1).BytesValue);
            Assert.Equal(Raids.Gigalodon, entry.Single(f => f.FieldNumber == 4).VarIntValue);
            Assert.Equal(184, entry.Single(f => f.FieldNumber == 3).VarIntValue);
        }
    }
}
