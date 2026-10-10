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
    /// The guild window's Raids tab: buying from the guild shop, joining, leaving, the captain, the
    /// note and the block list, with the result codes the client's raid frame translates. Read
    /// from the client; no capture has a raid.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidBoardTests : IDisposable
    {
        private readonly string _file;

        public GuildRaidBoardTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raidboard-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidBoard.Forget();
            GuildRaidManager.Forget();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(MinPlayers, null);
            GuildRaidManager.Forget();
            GuildRaidBoard.Forget();
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private const string MinPlayers = "JONDO_RAID_MIN_PLAYERS";

        /// <summary>A guild founded by 7001 with these kamas, and 7002 and 7003 as plain members.</summary>
        private static GuildStore.Guild Guild(long kamas = 1000)
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.SpendGuildKamas(guild.Id, -kamas);
            GuildStore.Join(7002, guild.Id);
            GuildStore.Join(7003, guild.Id);
            return guild;
        }

        [Fact]
        public void The_client_s_data_defines_both_raids()
        {
            var gardens = GuildRaidCatalogue.Of(Raids.EternalGardens);
            var abyss = GuildRaidCatalogue.Of(Raids.Gigalodon);
            Assert.Equal((480, 8, 16, TimeSpan.FromHours(2)), (gardens.Price, gardens.MinPlayers, gardens.MaxPlayers, gardens.Duration));
            Assert.Equal((360, 8, 12, TimeSpan.FromHours(1)), (abyss.Price, abyss.MinPlayers, abyss.MaxPlayers, abyss.Duration));
            Assert.Equal(new[] { 2 }, abyss.Groups);
            Assert.Equal(21, GuildRaidCatalogue.GoalsOf(Raids.Gigalodon).Count);
        }

        /// <summary>
        /// Buying needs a guild, the right "Administrar las raids" and the guild kamas; the buyer is
        /// the captain, and may not buy another while his is not over.
        /// </summary>
        [Fact]
        public void Buying_a_raid_from_the_guild_shop()
        {
            Assert.Equal(GuildRaidBoard.PurchaseResult.GuildRequired, GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Result);

            var guild = Guild(kamas: 100);
            Assert.Equal(GuildRaidBoard.PurchaseResult.UnknownRaid, GuildRaidBoard.Purchase(7001, 99).Result);
            Assert.Equal(GuildRaidBoard.PurchaseResult.MissingRight, GuildRaidBoard.Purchase(7002, Raids.Gigalodon).Result);
            Assert.Equal(GuildRaidBoard.PurchaseResult.NotEnoughMoney, GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Result);
            Assert.Equal(GuildRaidBoard.PurchaseResult.InvalidGroup, GuildRaidBoard.Purchase(7001, Raids.Gigalodon, group: 1).Result);

            GuildStore.SpendGuildKamas(guild.Id, -400);
            var (result, raid) = GuildRaidBoard.Purchase(7001, Raids.Gigalodon);
            Assert.Equal(GuildRaidBoard.PurchaseResult.Done, result);
            Assert.Equal(140, GuildStore.GuildOf(7001).GuildKamas);                 // 500 - 360
            Assert.Equal(GuildRaidBoard.State.Constitution, raid.State);
            Assert.Equal(7001, raid.Captain.CharacterId);
            Assert.Equal(2, raid.Captain.Group);
            Assert.Equal(7001, raid.Note.AuthorId);

            Assert.Equal(GuildRaidBoard.PurchaseResult.CaptainHasActiveRaid, GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Result);
            Assert.Same(raid, GuildRaidBoard.OfGuild(guild.Id).Single());
        }

        [Fact]
        public void Joining_and_leaving()
        {
            Guild();
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;

            Assert.Equal(GuildRaidBoard.JoinResult.NoRaidInConstitution, GuildRaidBoard.Join(7002, "no such raid"));
            Assert.Equal(GuildRaidBoard.JoinResult.Done, GuildRaidBoard.Join(7002, raid.Uuid));
            Assert.Equal(GuildRaidBoard.JoinResult.AlreadyInRaid, GuildRaidBoard.Join(7002, raid.Uuid));
            Assert.Equal(GuildRaidBoard.JoinResult.InvalidGroup, GuildRaidBoard.Join(7003, raid.Uuid, group: 1));
            Assert.Equal(GuildRaidBoard.JoinResult.GuildRequired, GuildRaidBoard.Join(9999, raid.Uuid));
            Assert.Equal(new long[] { 7001, 7002 }, raid.Participants.Select(p => p.CharacterId));

            Assert.Equal(GuildRaidBoard.LeaveResult.ImpossibleWhileCaptain, GuildRaidBoard.Leave(7001).Result);
            Assert.Equal(GuildRaidBoard.LeaveResult.Done, GuildRaidBoard.Leave(7002).Result);
            Assert.Equal(GuildRaidBoard.LeaveResult.RequireRaid, GuildRaidBoard.Leave(7002).Result);
        }

        /// <summary>The raid's maximum is the client's: twelve in the Abyss.</summary>
        [Fact]
        public void A_full_raid_takes_nobody_else()
        {
            var guild = Guild();
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            for (long id = 8000; id < 8011; id++)
            {
                GuildStore.Join(id, guild.Id);
                Assert.Equal(GuildRaidBoard.JoinResult.Done, GuildRaidBoard.Join(id, raid.Uuid));
            }
            Assert.Equal(12, raid.Participants.Count);
            Assert.Equal(GuildRaidBoard.JoinResult.RaidOrGroupFull, GuildRaidBoard.Join(7002, raid.Uuid));
        }

        [Fact]
        public void The_captain_hands_on_his_captaincy_and_writes_the_note()
        {
            Guild();
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            GuildRaidBoard.Join(7002, raid.Uuid);

            Assert.Equal(GuildRaidBoard.CaptainResult.NotCaptainAndMissingRight, GuildRaidBoard.UpdateCaptain(7002, raid.Uuid, 7002));
            Assert.Equal(GuildRaidBoard.CaptainResult.TargetNotInRaid, GuildRaidBoard.UpdateCaptain(7001, raid.Uuid, 7003));
            Assert.Equal(GuildRaidBoard.CaptainResult.TargetIsAlreadyCaptain, GuildRaidBoard.UpdateCaptain(7001, raid.Uuid, 7001));
            Assert.Equal(GuildRaidBoard.CaptainResult.Done, GuildRaidBoard.UpdateCaptain(7001, raid.Uuid, 7002));
            Assert.Equal(7002, raid.Captain.CharacterId);
            Assert.Single(raid.Participants, p => p.IsCaptain);

            // Now the old captain may leave.
            Assert.Equal(GuildRaidBoard.LeaveResult.Done, GuildRaidBoard.Leave(7001).Result);

            Assert.False(GuildRaidBoard.UpdateNote(7003, raid.Uuid, "no"));
            Assert.True(GuildRaidBoard.UpdateNote(7002, raid.Uuid, "Sábado 21h"));
            Assert.Equal(("Sábado 21h", 7002L), (raid.Note.Text, raid.Note.AuthorId));
        }

        [Fact]
        public void The_captain_removes_and_bars_a_participant()
        {
            Guild();
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            GuildRaidBoard.Join(7002, raid.Uuid);

            Assert.Equal(GuildRaidBoard.RemoveResult.NotCaptain, GuildRaidBoard.RemoveParticipant(7002, raid.Uuid, 7001, false));
            Assert.Equal(GuildRaidBoard.RemoveResult.TargetNotInRaid, GuildRaidBoard.RemoveParticipant(7001, raid.Uuid, 7003, false));
            Assert.Equal(GuildRaidBoard.RemoveResult.Done, GuildRaidBoard.RemoveParticipant(7001, raid.Uuid, 7002, block: true));
            Assert.Equal(GuildRaidBoard.JoinResult.IsInBlockList, GuildRaidBoard.Join(7002, raid.Uuid));

            Assert.True(GuildRaidBoard.Unblock(7001, raid.Uuid, 7002));
            Assert.Equal(GuildRaidBoard.JoinResult.Done, GuildRaidBoard.Join(7002, raid.Uuid));
        }

        /// <summary>What the board keeps survives a restart: participants, captain, note and block list.</summary>
        [Fact]
        public void The_board_is_kept_in_the_database()
        {
            Guild();
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            GuildRaidBoard.Join(7002, raid.Uuid);
            GuildRaidBoard.Join(7003, raid.Uuid);
            GuildRaidBoard.RemoveParticipant(7001, raid.Uuid, 7003, block: true);
            GuildRaidBoard.UpdateNote(7001, raid.Uuid, "nota");

            GuildRaidBoard.Forget();
            var again = GuildRaidBoard.Find(raid.Uuid);
            Assert.NotSame(raid, again);
            Assert.Equal(new long[] { 7001, 7002 }, again.Participants.Select(p => p.CharacterId));
            Assert.Equal(7001, again.Captain.CharacterId);
            Assert.Equal("nota", again.Note.Text);
            Assert.Contains(7003L, again.Blocked);
        }

        // ─── Starting ───────────────────────────────────────────────────────────

        /// <summary>
        /// With the server's minimum below the client's -- whose start button needs its own eight --
        /// a raid with the server's minimum, everyone connected, starts by itself, nobody accepted
        /// yet; at the game's minimum it never does.
        /// </summary>
        [Fact]
        public void A_lower_minimum_starts_the_raid_by_itself()
        {
            Guild();
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            var now = DateTimeOffset.UtcNow;
            using var online = Online(7001);

            Assert.False(GuildRaidBoard.AutoStart(raid, now));                  // the game's eight
            try
            {
                Jondo.Unity.Server.ServerSettings.UseForTests(new Jondo.Unity.Server.ServerSettings { RaidMinPlayers = 2 });
                Assert.False(GuildRaidBoard.AutoStart(raid, now));              // one of two
                Jondo.Unity.Server.ServerSettings.UseForTests(new Jondo.Unity.Server.ServerSettings { RaidMinPlayers = 1 });
                Assert.True(GuildRaidBoard.AutoStart(raid, now));
                Assert.Equal(GuildRaidBoard.State.Starting, raid.State);
                Assert.Empty(raid.Accepted);                                    // the captain is asked too
                Assert.False(GuildRaidBoard.AutoStart(raid, now));              // already starting

                Assert.Equal(GuildRaidBoard.StartOutcome.Everyone, GuildRaidBoard.Answer(7001, true).Outcome);
            }
            finally
            {
                Jondo.Unity.Server.ServerSettings.UseForTests(new Jondo.Unity.Server.ServerSettings());
            }
        }

        /// <summary>The three of the guild in one Abyss raid, captained by 7001.</summary>
        private static GuildRaidBoard.Raid RaidOfThree()
        {
            Guild();
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            GuildRaidBoard.Join(7002, raid.Uuid);
            GuildRaidBoard.Join(7003, raid.Uuid);
            return raid;
        }

        /// <summary>These characters connected, with no socket, until the returned scope is disposed.</summary>
        private static IDisposable Online(params long[] ids)
        {
            var sessions = ids.Select(id =>
            {
                var session = GameSession.SinSocket();
                session.State.CharacterId = id;
                Assert.True(SessionRegistry.Register(session));
                return session;
            }).ToList();
            return new Scope(() => sessions.ForEach(s => SessionRegistry.Unregister(s)));
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action _end;
            public Scope(Action end) => _end = end;
            public void Dispose() => _end();
        }

        /// <summary>
        /// Only the captain starts, with the raid's minimum of players -- the client's eight, unless
        /// JONDO_RAID_MIN_PLAYERS lowers it -- and all of them connected; otherwise the client is
        /// told who is missing. Started, it waits a minute for the rest, and nobody else joins.
        /// </summary>
        [Fact]
        public void Only_the_captain_starts_with_everyone_there()
        {
            var raid = RaidOfThree();
            var now = DateTimeOffset.UtcNow;

            Assert.Equal(GuildRaidBoard.StartError.NotCaptain, GuildRaidBoard.Start(7002, now).Error);
            Assert.Equal(GuildRaidBoard.StartError.MalformedRaid, GuildRaidBoard.Start(7001, now).Error);
            Assert.Equal(GuildRaidBoard.StartError.NoRaidInConstitution, GuildRaidBoard.Start(9999, now).Error);

            Environment.SetEnvironmentVariable(MinPlayers, "3");
            var (error, _, offline) = GuildRaidBoard.Start(7001, now);
            Assert.Equal(GuildRaidBoard.StartError.Done, error);
            Assert.Equal(new long[] { 7001, 7002, 7003 }, offline);
            Assert.Equal(GuildRaidBoard.State.Constitution, raid.State);

            using (Online(7001, 7002, 7003))
            {
                (error, _, offline) = GuildRaidBoard.Start(7001, now);
                Assert.Equal(GuildRaidBoard.StartError.Done, error);
                Assert.Null(offline);
                Assert.Equal(GuildRaidBoard.State.Starting, raid.State);
                Assert.Equal(now + TimeSpan.FromMinutes(1), raid.StartDeadline);
                Assert.Equal(new long[] { 7002, 7003 }, GuildRaidBoard.NotAccepted(raid));

                GuildStore.Join(7004, raid.GuildId);
                Assert.Equal(GuildRaidBoard.JoinResult.RaidIsStarting, GuildRaidBoard.Join(7004, raid.Uuid));
                Assert.Equal(GuildRaidBoard.RemoveResult.RaidIsStarting, GuildRaidBoard.RemoveParticipant(7001, raid.Uuid, 7002, false));
            }

            // The variable only lowers the client's minimum, never raises it.
            Environment.SetEnvironmentVariable(MinPlayers, "50");
            Assert.Equal(8, GuildRaidBoard.MinPlayersOf(GuildRaidCatalogue.Of(Raids.Gigalodon)));
        }

        /// <summary>
        /// A refusal calls the start off; time running out calls it off and names who did not
        /// answer; the last acceptance makes it run, once.
        /// </summary>
        [Fact]
        public void The_participants_answer_the_start()
        {
            var raid = RaidOfThree();
            Environment.SetEnvironmentVariable(MinPlayers, "3");
            using var online = Online(7001, 7002, 7003);

            GuildRaidBoard.Start(7001, DateTimeOffset.UtcNow);
            Assert.Equal(GuildRaidBoard.StartOutcome.Waiting, GuildRaidBoard.Answer(7002, true).Outcome);
            Assert.Equal(GuildRaidBoard.StartOutcome.Refused, GuildRaidBoard.Answer(7003, false).Outcome);
            Assert.Equal(GuildRaidBoard.State.Constitution, raid.State);
            Assert.Empty(raid.Accepted);

            GuildRaidBoard.Start(7001, DateTimeOffset.UtcNow);
            GuildRaidBoard.Answer(7002, true);
            Assert.Null(GuildRaidBoard.ExpireStart(raid, raid.StartDeadline.AddSeconds(-1)));   // another start's
            Assert.Equal(new long[] { 7003 }, GuildRaidBoard.ExpireStart(raid, raid.StartDeadline));
            Assert.Equal(GuildRaidBoard.State.Constitution, raid.State);

            GuildRaidBoard.Start(7001, DateTimeOffset.UtcNow);
            var deadline = raid.StartDeadline;
            Assert.Equal(GuildRaidBoard.StartOutcome.Waiting, GuildRaidBoard.Answer(7003, true).Outcome);
            Assert.Equal(GuildRaidBoard.StartOutcome.Everyone, GuildRaidBoard.Answer(7002, true).Outcome);
            Assert.Equal(GuildRaidBoard.State.Running, raid.State);
            Assert.Equal(GuildRaidBoard.StartOutcome.NotStarting, GuildRaidBoard.Answer(7002, true).Outcome);
            Assert.Null(GuildRaidBoard.ExpireStart(raid, deadline));
        }

        /// <summary>
        /// Launched, the raid runs as an instance tied to its uuid: every participant belongs to it,
        /// inside or not yet. Over, the board keeps its score and the tab shows it finished.
        /// </summary>
        [Fact]
        public async Task A_launched_raid_runs_and_finishes_on_the_board()
        {
            var raid = RaidOfThree();
            var running = await GuildRaidManager.LaunchAsync(raid);

            Assert.Equal(raid.Uuid, running.Uuid);
            Assert.Equal(GuildRaidBoard.State.Running, raid.State);
            Assert.Same(running, GuildRaidManager.RunningOf(7003));
            Assert.Same(running, GuildRaidManager.RunningByUuid(raid.Uuid));
            Assert.Null(GuildRaidManager.RaidOf(7003));                 // offline: not taken in
            Assert.Null(GuildRaidManager.RunningOf(9999));
            Assert.Equal(TimeSpan.FromHours(1), running.EndsUtc - running.StartedUtc);

            var tab = Inner(Fields(GuildRaidProtocol.BuildGuildRaidShow(GuildRaidBoard.VisibleTo(7002))), 1);
            var entry = Inner(tab, 2);                                     // the running raids' map
            var block = Inner(entry, 2);
            Assert.Equal(Raids.Gigalodon, block.First(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(raid.Uuid, System.Text.Encoding.UTF8.GetString(block.First(f => f.FieldNumber == 4).BytesValue));
            Assert.Contains(Fields(GuildRaidProtocol.BuildRaidUpdated(raid)), f => f.FieldNumber == 1);

            running.Set(RaidInstance.ScoreVariable, 4321);
            await GuildRaidManager.FinishAsync(running, RaidInstance.Ending.Captain);
            Assert.Equal(GuildRaidBoard.State.Finished, raid.State);
            Assert.Equal(4321, raid.Score);
            Assert.Null(GuildRaidManager.RunningOf(7003));
            Assert.Empty(GuildRaidBoard.VisibleTo(7002));
            var over = Fields(GuildRaidProtocol.BuildRaidUpdated(raid));
            Assert.Equal(4321, Fields(over.First(f => f.FieldNumber == 4).BytesValue).First(f => f.FieldNumber == 6).VarIntValue);
        }

        /// <summary>
        /// The start's frames: the answer's oneof (f2 success, f3 who is not connected, f4 an
        /// error), the prompt with the raid and its deadline, and the two ways it is called off.
        /// </summary>
        [Fact]
        public void The_start_frames_are_the_client_s()
        {
            var raid = RaidOfThree();
            Assert.Equal(raid.Uuid, System.Text.Encoding.UTF8.GetString(
                Inner(Fields(GuildRaidProtocol.BuildStartResponse(GuildRaidBoard.StartError.Done, raid.Uuid, null)), 2)[0].BytesValue));
            Assert.Equal(3, Fields(GuildRaidProtocol.BuildStartResponse(GuildRaidBoard.StartError.MalformedRaid, raid.Uuid, null))
                .Single(f => f.FieldNumber == 4).VarIntValue);
            var offline = Inner(Fields(GuildRaidProtocol.BuildStartResponse(GuildRaidBoard.StartError.Done, raid.Uuid, new long[] { 7002, 7003 })), 3);
            Assert.Equal(new long[] { 7002, 7003 },
                offline.Select(hxd => Inner(Fields(hxd.BytesValue), 1).First(c => c.FieldNumber == 3).VarIntValue));

            raid.StartDeadline = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
            var prompt = Fields(GuildRaidProtocol.BuildStartPrompt(raid));
            Assert.Equal(Raids.Gigalodon, prompt.First(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(raid.StartDeadline, DateTimeOffset.Parse(System.Text.Encoding.UTF8.GetString(prompt.First(f => f.FieldNumber == 2).BytesValue)));

            Assert.Contains(Fields(GuildRaidProtocol.BuildStartRefused(7003)), f => f.FieldNumber == 3);
            var timedOut = Inner(Fields(GuildRaidProtocol.BuildStartTimedOut(new long[] { 7002, 7003 })), 4);
            Assert.Equal(2, timedOut.Count(f => f.FieldNumber == 1));
            Assert.Equal(2, timedOut.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Empty(GuildRaidProtocol.BuildStartAnswerResult(0));
        }

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        private static List<ProtoField> Inner(List<ProtoField> fields, int number)
            => Fields(fields.First(f => f.FieldNumber == number && f.WireType == 2).BytesValue);

        /// <summary>
        /// The tab's list (ice): f1 the success case, whose f1 maps the uuid to the raid in
        /// constitution -- its note, its participants with their group and the captain's mark, the
        /// player's card under f6.f1 with the id in f3, and the raid's id in f3.
        /// </summary>
        [Fact]
        public void The_tab_lists_each_raid_by_its_uuid()
        {
            Guild();
            Assert.Equal("0a00", Convert.ToHexString(GuildRaidProtocol.BuildGuildRaidShow(GuildRaidBoard.VisibleTo(7001))).ToLowerInvariant());

            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            GuildRaidBoard.Join(7002, raid.Uuid);

            var entry = Inner(Inner(Fields(GuildRaidProtocol.BuildGuildRaidShow(GuildRaidBoard.VisibleTo(7002))), 1), 1);
            Assert.Equal(raid.Uuid, System.Text.Encoding.UTF8.GetString(entry.First(f => f.FieldNumber == 1).BytesValue));
            var block = Inner(entry, 2);
            Assert.Equal(Raids.Gigalodon, block.First(f => f.FieldNumber == 3).VarIntValue);
            var participants = block.Where(f => f.FieldNumber == 2).Select(f => Fields(f.BytesValue)).ToList();
            Assert.Equal(2, participants.Count);
            Assert.Equal(1, participants[0].First(f => f.FieldNumber == 4).VarIntValue);           // the captain
            Assert.DoesNotContain(participants[1], f => f.FieldNumber == 4);
            Assert.All(participants, p => Assert.Equal(2, p.First(f => f.FieldNumber == 3).VarIntValue));
            var card = Inner(Inner(participants[1], 6), 1);
            Assert.Equal(7002, card.First(f => f.FieldNumber == 3).VarIntValue);

            // The raid changed: f2 its uuid and f5 the raid in constitution.
            var updated = Fields(GuildRaidProtocol.BuildRaidUpdated(raid));
            Assert.Equal(raid.Uuid, System.Text.Encoding.UTF8.GetString(updated.First(f => f.FieldNumber == 2).BytesValue));
            Assert.Contains(updated, f => f.FieldNumber == 5);
        }
    }
}
