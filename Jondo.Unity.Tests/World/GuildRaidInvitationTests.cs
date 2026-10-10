using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Invitations into a guild raid, the way an outsider comes in: the raid's guild invites, the
    /// invited player answers from the client's popup, with the result codes its raid frame
    /// translates. Read from the client; no capture has a raid.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidInvitationTests : IDisposable
    {
        private readonly string _file;
        private readonly List<GameSession> _sessions = new();

        public GuildRaidInvitationTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raidinvite-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidBoard.Forget();
        }

        public void Dispose()
        {
            foreach (var session in _sessions) SessionRegistry.Unregister(session);
            GuildRaidBoard.Forget();
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private GameSession Online(long id)
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = id;
            Assert.True(SessionRegistry.Register(session));
            _sessions.Add(session);
            return session;
        }

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        /// <summary>Jondo (7001, 7002) has a raid; 7100 is in no guild.</summary>
        private static (GuildRaidBoard.Raid Raid, long Outsider) Setup()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.SpendGuildKamas(guild.Id, -1000);
            GuildStore.Join(7002, guild.Id);
            var raid = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            GuildRaidBoard.Join(7002, raid.Uuid);
            return (raid, 7100);
        }

        /// <summary>
        /// The invitation's checks, in the client's codes: no raid, already in it, no such character,
        /// fighting; then it goes, and the invited player answers.
        /// </summary>
        [Fact]
        public void An_outsider_is_invited_and_comes_in()
        {
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(GuildRaidBoard.InviteResult.RequireRaid, GuildRaidBoard.Invite(9999, 7100, now).Result);
            var (raid, outsider) = Setup();
            Assert.Equal(GuildRaidBoard.InviteResult.TargetIsInYourRaid, GuildRaidBoard.Invite(7001, 7002, now).Result);
            Assert.Equal(GuildRaidBoard.InviteResult.TargetNotFound, GuildRaidBoard.Invite(7001, 987_654_321_000, now).Result);

            var session = Online(outsider);
            session.State.IsInFight = true;
            Assert.Equal(GuildRaidBoard.InviteResult.TargetIsOccupied, GuildRaidBoard.Invite(7001, outsider, now).Result);
            session.State.IsInFight = false;
            Assert.Equal(GuildRaidBoard.InviteResult.Done, GuildRaidBoard.Invite(7001, outsider, now).Result);

            var (result, joined) = GuildRaidBoard.AnswerInvitation(outsider, true, now);
            Assert.Equal(GuildRaidBoard.InvitationAnswerResult.Done, result);
            Assert.Same(raid, joined);
            Assert.True(raid.Has(outsider));
            Assert.Equal(2, raid.ParticipantOf(outsider).Group);
            Assert.Same(raid, GuildRaidBoard.OfParticipant(outsider));
            Assert.Equal(GuildRaidBoard.InvitationAnswerResult.NoPendingInvitation, GuildRaidBoard.AnswerInvitation(outsider, true, now).Result);

            // An outsider in the raid cannot invite: only its guild does.
            Assert.Equal(GuildRaidBoard.InviteResult.InvitationImpossibleForExtern, GuildRaidBoard.Invite(outsider, 7003, now).Result);
        }

        /// <summary>A refusal drops the invitation; one left too long has lapsed.</summary>
        [Fact]
        public void A_refused_or_lapsed_invitation_is_gone()
        {
            var now = DateTimeOffset.UtcNow;
            var (raid, outsider) = Setup();
            Online(outsider);

            GuildRaidBoard.Invite(7001, outsider, now);
            Assert.Equal(GuildRaidBoard.InvitationAnswerResult.Done, GuildRaidBoard.AnswerInvitation(outsider, false, now).Result);
            Assert.False(raid.Has(outsider));

            GuildRaidBoard.Invite(7001, outsider, now);
            Assert.Equal(GuildRaidBoard.InvitationAnswerResult.NoPendingInvitation,
                         GuildRaidBoard.AnswerInvitation(outsider, true, now + GuildRaidBoard.InvitationWindow + TimeSpan.FromSeconds(1)).Result);
        }

        /// <summary>
        /// The popup (ibo): f2 the raid's guild, f3 the raid, f4 who invites. The answer (hzb): on
        /// success f3 the raid in constitution, case 3 of its oneof, which the client takes as his.
        /// </summary>
        [Fact]
        public void The_invitation_frames()
        {
            var (raid, _) = Setup();
            var popup = Fields(GuildRaidProtocol.BuildInvitation(raid, 7001));
            Assert.Equal(Raids.Gigalodon, popup.Single(f => f.FieldNumber == 3).VarIntValue);
            Assert.Equal("Jondo", System.Text.Encoding.UTF8.GetString(
                Fields(popup.Single(f => f.FieldNumber == 2).BytesValue).Single(f => f.FieldNumber == 3).BytesValue));
            Assert.Equal(7001, Fields(popup.Single(f => f.FieldNumber == 4).BytesValue).Single(f => f.FieldNumber == 3).VarIntValue);

            var answer = Fields(GuildRaidProtocol.BuildInvitationAnswerResult(GuildRaidBoard.InvitationAnswerResult.Done, raid));
            Assert.Contains(Fields(answer.Single(f => f.FieldNumber == 3).BytesValue), f => f.FieldNumber == 3);
            Assert.Equal(2, Fields(GuildRaidProtocol.BuildInvitationAnswerResult(GuildRaidBoard.InvitationAnswerResult.RaidFull, raid))
                .Single().VarIntValue);
        }
    }
}
