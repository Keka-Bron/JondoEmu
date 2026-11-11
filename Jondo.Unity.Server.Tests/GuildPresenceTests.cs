using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GuildPresenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Presence_keeps_roster_before_opt_in_login_warnings_and_excludes_self_and_offline(bool online)
    {
        byte[] roster = [1, 2, 3];
        var self = new StatusRecipient();
        var warned = new StatusRecipient();
        var quiet = new StatusRecipient();
        warned.Session.State.WarnGuildConnections = true;
        quiet.Session.State.WarnGuildConnections = false;
        var backend = new Backend();
        backend.People.AddRange([new() { CharacterId = 71_001 }, new() { CharacterId = 71_002 },
            new() { CharacterId = 71_003 }, new() { CharacterId = 71_004 }]);
        backend.Sessions[71_001] = self.Session;
        backend.Sessions[71_002] = warned.Session;
        backend.Sessions[71_004] = quiet.Session;
        using var scope = SessionContext.Push(self.Session);
        using var turn = await self.Session.EnterTurnAsync();
        await new GuildPresenceService(backend).BroadcastAsync(91_001, 71_001, online, roster).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(self.Packets);
        Assert.Same(roster, warned.Packets[0]);
        Assert.Same(roster, Assert.Single(quiet.Packets));
        Assert.Empty(quiet.Codec.Frames);
        if (online)
        {
            Assert.Equal(2, warned.Packets.Count);
            var frame = Assert.Single(warned.Codec.Frames);
            Assert.Null(frame.Uid);
            Assert.Equal(new GuildMemberOnlineStatusEvent(71_001, true), frame.Content);
        }
        else { Assert.Single(warned.Packets); Assert.Empty(warned.Codec.Frames); }
        Assert.Same(self.Session, SessionContext.Actual);
        Assert.Equal(0, warned.Codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Roster_or_warning_failure_stops_later_recipients_without_retry(bool warningFailure)
    {
        var failure = new IOException("presence delivery failed");
        int sends = 0;
        var first = new StatusRecipient(_ => ++sends == (warningFailure ? 2 : 1) ? Task.FromException(failure) : Task.CompletedTask);
        first.Session.State.WarnGuildConnections = true;
        var later = new StatusRecipient();
        var backend = new Backend();
        backend.People.AddRange([new() { CharacterId = 71_002 }, new() { CharacterId = 71_003 }]);
        backend.Sessions[71_002] = first.Session;
        backend.Sessions[71_003] = later.Session;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new GuildPresenceService(backend)
            .BroadcastAsync(91_001, 71_001, true, [1, 2, 3])));
        Assert.Equal(warningFailure ? 2 : 1, sends);
        Assert.Empty(later.Packets);
    }

    private sealed class Backend : IGuildPresenceBackend
    {
        internal readonly List<GuildStore.Member> People = [];
        internal readonly Dictionary<long, GameSession> Sessions = [];
        public IReadOnlyList<GuildStore.Member> Members(long guildId) { Assert.Equal(91_001, guildId); return People; }
        public GameSession? FindSession(long characterId) => Sessions.GetValueOrDefault(characterId);
    }
}
