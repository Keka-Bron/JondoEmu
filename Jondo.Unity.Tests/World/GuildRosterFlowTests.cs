using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("guild raids")]
public sealed class GuildRosterFlowTests : IDisposable
{
    private const long Account = 8_196_101;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"jondo-roster-{Guid.NewGuid():N}.db");
    private readonly long _character;
    public GuildRosterFlowTests()
    {
        GuildStore.ConnectionStringOverride = $"Data Source={_file}";
        _character = DatabaseManager.CreateCharacter(Account, 1, "Roster-Owner", 12, 1, 1,
            new long[] { -1, -1, -1, -1, -1 }, 191105026, 200, 0, 0, Array.Empty<(int, int)>());
        Assert.True(_character > 0);
    }
    public void Dispose()
    {
        HavenBagStore.SetShared(_character, 0);
        DatabaseManager.DeleteCharacter(_character, Account);
        GuildStore.ConnectionStringOverride = null;
        TestDatabases.ReleasePool(_file);
        File.Delete(_file);
    }

    [Fact]
    public async Task Entry_and_window_send_populated_detached_rosters_through_the_explicit_owners_framed_codec()
    {
        var guild = GuildStore.Create(_character, "Semantic roster", 101, 202, 303, 404);
        var rank = GuildStore.CreateRank(guild.Id, "Custom", 91_001, 2);
        GuildStore.SetRank(_character, rank.Id);
        GuildStore.SetNote(_character, "stored note", 8_000_000_003);
        GuildStore.Contribute(_character, guild.Id);
        HavenBagStore.SetShared(_character, HavenBagStore.SharedWithGuild);
        var codec = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(Account, _character, 8_196_201, codec: codec);
        pipe.Session.State.ChatStatus = 4;
        pipe.Session.State.ChatStatusMessage = "busy";
        pipe.Session.State.IsInFight = true;
        var ambient = GameSession.SinSocket();
        using var context = SessionContext.Push(ambient);
        using var turn = await pipe.Session.EnterTurnAsync();
        await GuildHandler.SendMembershipAtEntryAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Collection(codec.Frames,
            frame => Assert.IsType<GuildRanksEvent>(frame.Content),
            frame => Assert.IsType<GuildMembershipEvent>(frame.Content),
            frame => Assert.IsType<GuildGeneralInformationEvent>(frame.Content),
            frame => Assert.IsType<GuildMembersEvent>(frame.Content));
        for (int i = 1; i <= 4; i++) Assert.Equal(RecordingCodec.Token(i), await Packet(pipe));
        var roster = Assert.IsType<GuildMembersEvent>(codec.Frames[3].Content);
        var member = Assert.Single(roster.Members);
        var social = Social(member);
        Assert.Equal(Account, social.AccountId);
        Assert.Equal(GuildStore.Members(guild.Id)[0].JoinedUtcMs, social.EnrollmentDate);
        Assert.Equal(Gender.Female, social.Gender);
        Assert.Equal(CharacterState.InFight, social.State);
        Assert.Equal(rank.Id, social.RankId);
        Assert.Equal(new CharacterStatus(CharacterStatusStatus.Private, "busy"), social.Status);
        Assert.Equal(new GuildMemberHistory(10, 10), social.GuildMember!.History);
        Assert.True(social.GuildMember.HavenBagShared);
        Assert.Equal(new PlayerNote("stored note", 8_000_000_003), social.GuildMember.Note);
        GuildStore.SetNote(_character, "new note", 8_000_000_004);
        await GuildHandler.MembersAsync(pipe.Session, ConnectionProtocol.Push(Op.Jml, Pb.New().Var(1, 1).Build()));
        Assert.Equal(RecordingCodec.Token(5), await Packet(pipe));
        Assert.Equal("stored note", social.GuildMember.Note!.Content);
        Assert.Equal("new note", Social(Assert.Single(Assert.IsType<GuildMembersEvent>(codec.Frames[4].Content).Members)).GuildMember!.Note!.Content);
        await GuildHandler.MembersAsync(pipe.Session, ConnectionProtocol.Push(Op.Jml, []));
        Assert.Equal(5, codec.Frames.Count);
        Assert.Same(ambient, SessionContext.Actual);
        Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
        Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.False(pipe.FromServer.DataAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Window_roster_failure_propagates_without_retry_and_restores_the_context(bool encode)
    {
        GuildStore.Create(_character, "Failed roster", 101, 202, 303, 404);
        var failure = new IOException("roster delivery");
        var codec = new RecordingCodec { Failure = encode ? failure : null };
        int sends = 0;
        var owner = new GameSession(codec, _ => { sends++; return Task.FromException(failure); });
        owner.BindAccount(Account, 1);
        owner.State.CharacterId = _character;
        var ambient = GameSession.SinSocket();
        using var context = SessionContext.Push(ambient);
        using var turn = await owner.EnterTurnAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => GuildHandler.MembersAsync(owner,
            ConnectionProtocol.Push(Op.Jml, Pb.New().Var(1, 1).Build()))));
        Assert.IsType<GuildMembersEvent>(Assert.Single(codec.Frames).Content);
        Assert.Equal(encode ? 0 : 1, sends);
        Assert.Same(ambient, SessionContext.Actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_refresh_uses_each_recipient_codec_and_stops_after_failure_without_undoing_departure(bool fail)
    {
        const long other = 8_196_002, departed = 8_196_003;
        var guild = GuildStore.Create(_character, "Refresh roster", 101, 202, 303, 404);
        GuildStore.Join(other, guild.Id);
        GuildStore.Join(departed, guild.Id);
        var firstCodec = new RecordingCodec { Failure = fail ? new IOException("guild refresh") : null };
        var secondCodec = new RecordingCodec();
        await using var first = await ClientPipe.OpenAsync(Account, _character, 8_196_201, codec: firstCodec);
        await using var second = await ClientPipe.OpenAsync(8_196_102, other, 8_196_201, codec: secondCodec);
        using var context = SessionContext.Push(first.Session);
        using var sourceTurn = await first.Session.EnterTurnAsync();
        using var recipientTurn = await second.Session.EnterTurnAsync();
        var operation = GuildHandler.KickAsync(_character, departed);
        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => operation);
            Assert.IsType<GuildMembersEvent>(Assert.Single(firstCodec.Frames).Content);
            Assert.Empty(secondCodec.Frames);
        }
        else
        {
            Assert.Null(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            foreach (var pipe in new[] { first, second })
            {
                Assert.Equal(RecordingCodec.Token(1), await Packet(pipe));
                Assert.Equal(RecordingCodec.Token(2), await Packet(pipe));
            }
            Assert.Same(firstCodec.Frames[0].Content, secondCodec.Frames[0].Content);
            Assert.IsType<GuildMembersEvent>(firstCodec.Frames[0].Content);
            Assert.Equal(2, Assert.IsType<GuildGeneralInformationEvent>(firstCodec.Frames[1].Content).MembersCount);
        }
        Assert.Null(GuildStore.GuildOf(departed));
    }

    private static SocialMember Social(Character member) => Assert.IsType<CharacterBasicInformationComplementCaseSocialMember>(
        Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(member.ComplementCase).Value.ComplementCase).Value;
    private static Task<byte[]> Packet(ClientPipe pipe) => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal Exception? Failure;
        internal static byte[] Token(int index) => [0x7c, checked((byte)index)];
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); if (Failure != null) throw Failure; return Token(Frames.Count); }
        public GameFrame Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("Roster delivery must not project bytes.");
    }
}
