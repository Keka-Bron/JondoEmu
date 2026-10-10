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
    /// The guild raids' weekly frieze: a raid's best score of the week unlocks the steps of its
    /// frieze, and the player claims the steps of one raid a week from the rewards screen. Read from
    /// the client; no capture has a raid.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidRewardsTests : IDisposable
    {
        private readonly string _file;
        private static readonly DateTimeOffset Wednesday = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        public GuildRaidRewardsTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raidrewards-{Guid.NewGuid():N}.db");
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

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        private static List<ProtoField> Inner(List<ProtoField> fields, int number)
            => Fields(fields.First(f => f.FieldNumber == number && f.WireType == 2).BytesValue);

        /// <summary>The week opens at the measured weekly reset, Tuesday 05:00 UTC.</summary>
        [Fact]
        public void The_week_opens_at_the_Tuesday_reset()
        {
            Assert.Equal("2026-10-06", GuildRaidRewards.WeekOf(Wednesday));
            Assert.Equal("2026-10-06", GuildRaidRewards.WeekOf(new DateTimeOffset(2026, 10, 6, 5, 0, 0, TimeSpan.Zero)));
            Assert.Equal("2026-09-29", GuildRaidRewards.WeekOf(new DateTimeOffset(2026, 10, 6, 4, 59, 0, TimeSpan.Zero)));
        }

        /// <summary>
        /// A run unlocks the steps its score reaches that the week's best had not; a worse run
        /// unlocks nothing. The Gigalodón's frieze starts at 1,000 and 2,000 points.
        /// </summary>
        [Fact]
        public void A_run_unlocks_the_steps_its_score_reaches()
        {
            Assert.Equal(new[] { 38, 39 }, GuildRaidRewards.RecordRun(7001, Raids.Gigalodon, 2500, Wednesday));
            Assert.Empty(GuildRaidRewards.RecordRun(7001, Raids.Gigalodon, 1500, Wednesday));
            Assert.Equal(new[] { 40 }, GuildRaidRewards.RecordRun(7001, Raids.Gigalodon, 5000, Wednesday));
            Assert.Equal(5000, GuildRaidRewards.BestScores(7001, Wednesday)[Raids.Gigalodon]);
            Assert.Empty(GuildRaidRewards.BestScores(7001, Wednesday.AddDays(7)));       // a new week
        }

        /// <summary>
        /// Claiming pays the unlocked steps of one raid; the same raid can be claimed again as more
        /// unlock, another raid that week cannot.
        /// </summary>
        [Fact]
        public async Task The_steps_of_one_raid_a_week_are_claimed()
        {
            Assert.Equal(GuildRaidRewards.ClaimResult.InvalidRaid, (await GuildRaidRewards.ClaimAsync(null, 7001, 99, Wednesday)).Result);
            Assert.Equal(GuildRaidRewards.ClaimResult.NoRewardToClaim, (await GuildRaidRewards.ClaimAsync(null, 7001, Raids.Gigalodon, Wednesday)).Result);

            GuildRaidRewards.RecordRun(7001, Raids.Gigalodon, 2000, Wednesday);
            GuildRaidRewards.RecordRun(7001, Raids.EternalGardens, 4000, Wednesday);
            Assert.Equal(new[] { 27, 28, 38, 39 }, GuildRaidRewards.Pending(7001, Wednesday));

            var (result, paid) = await GuildRaidRewards.ClaimAsync(null, 7001, Raids.Gigalodon, Wednesday);
            Assert.Equal(GuildRaidRewards.ClaimResult.Done, result);
            Assert.Equal(new[] { 38, 39 }, paid);
            Assert.Empty(GuildRaidRewards.Pending(7001, Wednesday));
            Assert.Equal(GuildRaidRewards.ClaimResult.RewardsAlreadyClaimed, (await GuildRaidRewards.ClaimAsync(null, 7001, Raids.Gigalodon, Wednesday)).Result);
            Assert.Equal(GuildRaidRewards.ClaimResult.RewardsAlreadyClaimed, (await GuildRaidRewards.ClaimAsync(null, 7001, Raids.EternalGardens, Wednesday)).Result);

            GuildRaidRewards.RecordRun(7001, Raids.Gigalodon, 5000, Wednesday);
            Assert.Equal(new[] { 40 }, GuildRaidRewards.Pending(7001, Wednesday));
            Assert.Equal(new[] { 40 }, (await GuildRaidRewards.ClaimAsync(null, 7001, Raids.Gigalodon, Wednesday)).Rewards);
            var claimed = GuildRaidRewards.Claimed(7001, Wednesday).Value;
            Assert.Equal(Raids.Gigalodon, claimed.RaidId);
            Assert.Equal(new[] { 38, 39, 40 }, claimed.Rewards);
        }

        /// <summary>
        /// The player's raids (hxm): f3 per raid {f1 the steps unlocked, f2 the best score}, f4 the
        /// raid he is in (case 3 in constitution, with f1 its guild and f5 its uuid), f5 what he
        /// claimed {f2 the steps, f3 the raid}. Nothing at all for a player with no raid.
        /// </summary>
        [Fact]
        public void The_player_s_raids_frame()
        {
            var now = DateTimeOffset.UtcNow;
            Assert.Empty(GuildRaidProtocol.BuildPlayerRaids(Array.Empty<GuildRaidBoard.Raid>(),
                new Dictionary<int, long>(), null, null));

            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.SpendGuildKamas(guild.Id, -1000);
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            var frame = Fields(GuildRaidProtocol.BuildPlayerRaids(Array.Empty<GuildRaidBoard.Raid>(),
                new Dictionary<int, long> { [Raids.Gigalodon] = 2000 }, raid, (Raids.Gigalodon, new List<int> { 38 })));

            var result = Inner(Inner(frame, 3), 2);
            Assert.Equal(new long[] { 38, 39 }, result.Where(f => f.FieldNumber == 1).Select(f => f.VarIntValue));
            Assert.Equal(2000, result.Single(f => f.FieldNumber == 2).VarIntValue);

            var current = Inner(frame, 4);
            Assert.Equal(raid.Uuid, System.Text.Encoding.UTF8.GetString(current.Single(f => f.FieldNumber == 5).BytesValue));
            Assert.Contains(current, f => f.FieldNumber == 3);
            Assert.Equal("Jondo", System.Text.Encoding.UTF8.GetString(Inner(current, 1).Single(f => f.FieldNumber == 3).BytesValue));

            var claimed = Inner(frame, 5);
            Assert.Equal(38, claimed.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(Raids.Gigalodon, claimed.Single(f => f.FieldNumber == 3).VarIntValue);
        }

        /// <summary>
        /// A raid's end moves every participant's frieze on, and the final score (hyg f7) lists the
        /// steps it unlocked for him, each in state 1, "to claim".
        /// </summary>
        [Fact]
        public async Task The_end_of_a_raid_unlocks_its_steps()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.SpendGuildKamas(guild.Id, -1000);
            GuildStore.Join(7002, guild.Id);
            var board = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            GuildRaidBoard.Join(7002, board.Uuid);
            var raid = await GuildRaidManager.LaunchAsync(board);
            raid.Set(RaidInstance.ScoreVariable, 5500);
            await GuildRaidManager.FinishAsync(raid, RaidInstance.Ending.TimeUp);

            var now = DateTimeOffset.UtcNow;
            Assert.Equal(5500, GuildRaidRewards.BestScores(7002, now)[Raids.Gigalodon]);
            Assert.Equal(new[] { 38, 39, 40 }, GuildRaidRewards.Pending(7001, now));
            Assert.Single(GuildRaidBoard.FinishedOf(7002, now));

            var steps = Fields(GuildRaidProtocol.BuildRunningFinished(60, 5500, null, false, new[] { 38, 39 }))
                .Where(f => f.FieldNumber == 7).Select(f => Fields(f.BytesValue)).ToList();
            Assert.Equal(new long[] { 38, 39 }, steps.Select(e => e.Single(f => f.FieldNumber == 1).VarIntValue));
            Assert.All(steps, e => Assert.Equal(GuildRaidProtocol.RewardToClaim, e.Single(f => f.FieldNumber == 2).VarIntValue));
        }
    }
}
