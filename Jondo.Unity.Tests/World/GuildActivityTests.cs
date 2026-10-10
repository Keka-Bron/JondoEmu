using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A guild's weekly activity: the tier it chooses, the points and tokens a contribution gives,
    /// the milestones' experience, and the frames that tell the client, against the captures.
    /// </summary>
    [Collection("guild raids")]
    public class GuildActivityTests : IDisposable
    {
        private readonly string _file;

        /// <summary>A Wednesday, and the Wednesday after: two weeks of the game.</summary>
        private static readonly DateTimeOffset ThisWeek = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset NextWeek = ThisWeek.AddDays(7);

        public GuildActivityTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-actividad-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        [Fact]
        public void The_tiers_are_the_client_s()
        {
            Assert.Equal(5, GuildActivity.Tiers.Count);
            var first = GuildActivity.TierOf(1);
            Assert.Equal(new long[] { 5000, 10000, 16000, 25000 }, first.Milestones.Select(m => m.ActivityPoints));
            Assert.Equal(40, first.Milestones[0].Experience);
            Assert.Equal(500000, GuildActivity.TierOf(5).MaxPoints);
        }

        /// <summary>
        /// The first tier holds at once; a change holds from next week, and choosing this week's
        /// again takes it back. The tier stays from week to week; the points start again.
        /// </summary>
        [Fact]
        public void A_change_of_tier_waits_for_next_week()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            Assert.False(GuildActivity.Of(guild.Id, ThisWeek).HasActivity);

            var week = GuildActivity.Choose(guild.Id, 1, Array.Empty<byte>(), ThisWeek);
            Assert.Equal(1, week.ActivityId);
            Assert.Equal(0, week.PendingActivityId);

            week = GuildActivity.Choose(guild.Id, 2, Array.Empty<byte>(), ThisWeek);
            Assert.Equal(1, week.ActivityId);
            Assert.Equal(2, week.PendingActivityId);
            Assert.Equal(2, GuildActivity.Of(guild.Id, ThisWeek).PendingActivityId);

            GuildActivity.Credit(guild, 7001, 10, 100, ThisWeek);
            var next = GuildActivity.Of(guild.Id, NextWeek);
            Assert.Equal(2, next.ActivityId);
            Assert.Equal(0, next.PendingActivityId);
            Assert.Equal(0, next.Points);

            GuildActivity.Choose(guild.Id, 3, Array.Empty<byte>(), NextWeek);
            Assert.Equal(0, GuildActivity.Choose(guild.Id, 2, Array.Empty<byte>(), NextWeek).PendingActivityId);
            Assert.Null(GuildActivity.Choose(guild.Id, 9, Array.Empty<byte>(), NextWeek));
        }

        /// <summary>
        /// A member's tokens stop at 250 a week; the guild's points stop at the tier's last
        /// milestone, each milestone passed giving its experience -- and the level follows.
        /// </summary>
        [Fact]
        public void Activity_gives_tokens_points_and_milestones()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildActivity.Choose(guild.Id, 1, Array.Empty<byte>(), ThisWeek);

            var first = GuildActivity.Credit(guild, 7001, 240, 4900, ThisWeek);
            Assert.Equal(240, first.Tokens);
            Assert.Equal(0, first.Experience);

            var second = GuildActivity.Credit(guild, 7001, 30, 5200, ThisWeek);
            Assert.Equal(10, second.Tokens);                        // the cap of 250
            Assert.Equal(44, second.Experience);                     // 5,000 and 10,000 passed
            Assert.Equal(250, GuildActivity.TokensOf(7001, ThisWeek));

            var third = GuildActivity.Credit(guild, 7001, 10, 100000, ThisWeek);
            Assert.Equal(0, third.Tokens);
            Assert.Equal(25000 - 10100, third.Points);              // up to the last milestone
            Assert.Equal(8, third.Experience);
            Assert.Equal(2, third.Level);                            // 52 experience: level 2
            Assert.Equal(2, GuildStore.GuildOf(7001).Level);
            Assert.Equal(0, GuildActivity.TokensOf(7001, NextWeek));
        }

        /// <summary>A guild with no tier gathers no points, though its member gets his tokens.</summary>
        [Fact]
        public void Without_a_tier_there_are_no_points()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            var credited = GuildActivity.Credit(guild, 7001, 10, 100, ThisWeek);
            Assert.Equal(10, credited.Tokens);
            Assert.Equal(0, credited.Points);
        }

        /// <summary>
        /// The leader's jet chooses tier 1 and is answered with the jdb; the jfp then finds it. The
        /// capture's jet: "f1 1, f4 {}".
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task The_leader_chooses_the_tier()
        {
            GuildStore.Create(7003, "Jondo", 165, 8, 16744448, 9476018);
            await using var wire = await global::Jondo.Unity.Tests.Combat.PortalTests.Wire.Open(7003);
            using (SessionContext.Push(wire.Session))
            {
                await Jondo.Unity.Server.Handlers.GuildHandler.ChooseTierAsync(wire.Session.Stream!,
                    ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Jet, new byte[] { 0x08, 0x01, 0x22, 0x00 }));
                await Jondo.Unity.Server.Handlers.GuildHandler.WeekAsync(wire.Session.Stream!,
                    ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Jfp, Array.Empty<byte>()));
            }

            var frames = await wire.Drain();
            Assert.Equal(new[] { "jdb", "jff" }, frames.Select(f => f.Op));
            Assert.Equal("120b0a00120508011" + "8fa011a00", Hex(frames[0].Payload));
            Assert.Equal("12070a05080118fa01", Hex(frames[1].Payload));
        }

        /// <summary>
        /// The week as the jfp's jff says it, and as the jet's jdb says it, byte for byte against
        /// the captures: after one contribution, and right after choosing tier 1 (missions apart).
        /// </summary>
        [Fact]
        public void The_week_frames_are_the_capture()
        {
            var week = new GuildActivity.Week(1, "2026-08-11", 1, 0, 100, Array.Empty<byte>(), null);
            Assert.Equal("120b0a090801106418fa01200a", Hex(GuildProtocol.BuildWeek(week, 250, 10)));

            var chosen = week with { Points = 0 };
            Assert.Equal("120b0a00120508011" + "8fa011a00", Hex(GuildProtocol.BuildTierChosen(chosen, 250, 0)));

            // A change waiting for next week goes in f5.
            var waiting = week with { NextActivityId = 3 };
            var block = ProtoMessage.Parse(GuildProtocol.WeekBlock(waiting, 250, 10).Build()).Fields;
            Assert.Equal(3, block.Single(f => f.FieldNumber == 5).VarIntValue);
        }
    }
}
