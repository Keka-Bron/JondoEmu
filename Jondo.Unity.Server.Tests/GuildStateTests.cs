using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GuildStateTests
{
    private static readonly GameCodec Wire = new();
    private static GuildStore.Guild Guild(long id = 81_001) => new()
    {
        Id = id, Name = "Trésor", Level = 4, Experience = 181, GuildKamas = 8_000_000_003,
        FoundedUtc = "2026-11-11T12:34:56.789Z", EmblemSymbol = 91_001,
        EmblemSymbolColor = 91_002, EmblemBackground = 0x123456, EmblemSymbolRgb = 0xabcdef,
    };

    [Fact]
    public void Identity_and_membership_use_distinct_emblem_values_actual_rank_and_contribution_and_detach_from_source()
    {
        var guild = Guild();
        var actual = GuildStateMessages.Membership(guild, 82_005, 73);
        guild.Level = 20;
        guild.Experience = 999;
        Assert.Equal(new GuildMembershipEvent(new GuildInformation(81_001, "Trésor", 4,
            new GuildSocialInformation(new SocialEmblem(91_001, 91_002, 0x123456, 0xabcdef))),
            82_005, 73, 0), actual);
    }

    [Theory]
    [InlineData(1, 0, 50)]
    [InlineData(2, 50, 100)]
    [InlineData(4, 150, 210)]
    [InlineData(19, 1690, 1890)]
    [InlineData(20, 1890, 1890)]
    public void Header_uses_level_curve_including_the_capped_next_floor_and_retains_int64_totals(int level, long floor, long next)
    {
        var guild = Guild();
        guild.Level = level;
        guild.Experience = long.MaxValue;
        var header = GuildStateMessages.Header(guild, 17, 83_003);
        guild.Experience = 0;
        guild.GuildKamas = 0;
        Assert.Equal(new GuildGeneralInformationEvent("2026-11-11T12:34:56.789Z", 83_003,
            level, 0, floor, long.MaxValue, 8_000_000_003, next, 17), header);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(83_003)]
    public void Hall_presence_and_empty_counts_are_explicit(int? hall)
    {
        var guild = Guild();
        guild.Experience = 0;
        guild.GuildKamas = 0;
        var actual = GuildStateMessages.Header(guild, 0, hall);
        Assert.Equal(hall, actual.HallId);
        Assert.Equal(0, actual.MembersCount);
        Assert.Equal(0, actual.Score);
        Assert.Equal(0, actual.Experience);
        Assert.Equal(0, actual.GuildKamas);
    }

    [Theory]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData((long)int.MinValue - 1)]
    public void Guild_identity_and_member_totals_fail_instead_of_narrowing(long value)
    {
        Assert.Throws<OverflowException>(() => GuildStateMessages.Information(Guild(value)));
        Assert.Throws<OverflowException>(() => GuildStateMessages.Membership(Guild(), 1, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Representable_identity_and_membership_scalar_boundaries_are_retained(int value)
    {
        var actual = GuildStateMessages.Membership(Guild(value), value, value);
        Assert.Equal(value, actual.GuildInformation!.Id);
        Assert.Equal(value, actual.RankId);
        Assert.Equal(value, actual.GuildTokens);
    }

    [Fact]
    public async Task Header_delivery_uses_owning_codec_existing_turn_and_fresh_hall_and_member_count()
    {
        var target = Recipient();
        var other = Recipient();
        var backend = new Backend();
        var service = new GuildStateService(backend);
        using (SessionContext.Push(other.Session))
        using (await target.Session.EnterTurnAsync())
        {
            await service.SendHeaderAsync(target.Session, Guild()).WaitAsync(TimeSpan.FromSeconds(5));
            backend.Hall = 83_003;
            backend.People.Add(new() { CharacterId = 71_001 });
            await service.SendHeaderAsync(target.Session, Guild()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(other.Session, SessionContext.Actual);
        }
        Assert.Collection(target.Packets,
            packet => { var header = Header(packet); Assert.Null(header.HallId); Assert.Equal(0, header.MembersCount); },
            packet => { var header = Header(packet); Assert.Equal(83_003, header.HallId); Assert.Equal(1, header.MembersCount); });
        Assert.Empty(other.Packets);
        Assert.Equal(0, other.Codec.Encodes);
        Assert.Equal(2, target.Codec.Encodes);
        Assert.Equal(0, target.Codec.Decodes);
        Assert.Equal(new[] { "members:81001", "hall:81001", "members:81001", "hall:81001" }, backend.Calls);
    }

    [Fact]
    public void Membership_uses_the_requested_members_contribution_total()
    {
        var backend = new Backend();
        backend.Totals[71_001] = 10;
        backend.Totals[71_002] = 70;
        var service = new GuildStateService(backend);
        Assert.Equal(10, service.Membership(Guild(), 82_001, 71_001).GuildTokens);
        Assert.Equal(70, service.Membership(Guild(), 82_005, 71_002).GuildTokens);
        Assert.Equal(new[] { "contributed:71001", "contributed:71002" }, backend.Calls);
    }

    [Fact]
    public async Task Broadcast_captures_one_snapshot_and_encodes_for_each_online_recipient_under_another_context()
    {
        var guild = Guild();
        var backend = new Backend { Hall = 83_003 };
        var first = Recipient(_ => { guild.Experience = 999; backend.People.Clear(); backend.Hall = null; return Task.CompletedTask; });
        var second = Recipient();
        var other = Recipient();
        backend.People.AddRange([new() { CharacterId = 71_001 }, new() { CharacterId = 71_002 }, new() { CharacterId = 71_003 }]);
        backend.Sessions[71_001] = first.Session;
        backend.Sessions[71_003] = second.Session;
        using (SessionContext.Push(other.Session))
        using (await first.Session.EnterTurnAsync())
            await new GuildStateService(backend).TellHeaderAsync(guild).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var recipient in new[] { first, second })
        {
            var header = Header(Assert.Single(recipient.Packets));
            Assert.Equal(181, header.Experience);
            Assert.Equal(3, header.MembersCount); // includes the offline member
            Assert.Equal(83_003, header.HallId);
            Assert.Equal(1, recipient.Codec.Encodes);
            Assert.Equal(0, recipient.Codec.Decodes);
        }
        Assert.Empty(other.Packets);
        Assert.Equal(new[] { "members:81001", "hall:81001", "session:71001", "session:71002", "session:71003" }, backend.Calls);
    }

    [Theory]
    [InlineData("members:81001")]
    [InlineData("hall:81001")]
    public async Task Source_failure_sends_nothing_and_is_not_retried(string failAt)
    {
        var failure = new InvalidOperationException("guild source failed");
        var backend = new Backend { Failure = failure, FailAt = failAt };
        var target = Recipient();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GuildStateService(backend).SendHeaderAsync(target.Session, Guild())));
        Assert.Empty(target.Packets);
        Assert.Equal(0, target.Codec.Encodes);
        Assert.Equal(1, backend.Calls.Count(call => call == failAt));
    }

    [Fact]
    public void Membership_source_and_conversion_failures_remain_distinct()
    {
        var failure = new IOException("contribution source failed");
        var backend = new Backend { Failure = failure, FailAt = "contributed:71001" };
        var service = new GuildStateService(backend);
        Assert.Same(failure, Assert.Throws<IOException>(() => service.Membership(Guild(), 1, 71_001)));
        backend.FailAt = "";
        backend.Totals[71_001] = (long)int.MaxValue + 1;
        Assert.Throws<OverflowException>(() => service.Membership(Guild(), 1, 71_001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broadcast_codec_or_transport_failure_stops_later_recipients_without_retries(bool codecFailure)
    {
        Exception failure = codecFailure ? new GameConversionException("guild codec failed") : new IOException("guild transport failed");
        int sends = 0;
        var first = new GameSession(codecFailure ? new FailingCodec(failure) : Wire, _ => { sends++; return Task.FromException(failure); });
        var second = Recipient();
        var backend = new Backend();
        backend.People.AddRange([new() { CharacterId = 71_001 }, new() { CharacterId = 71_002 }]);
        backend.Sessions[71_001] = first;
        backend.Sessions[71_002] = second.Session;
        var thrown = await Record.ExceptionAsync(() => new GuildStateService(backend).TellHeaderAsync(Guild()));
        Assert.Same(failure, thrown);
        Assert.Equal(codecFailure ? 0 : 1, sends);
        Assert.Empty(second.Packets);
        Assert.Equal(0, second.Codec.Encodes);
        Assert.DoesNotContain("session:71002", backend.Calls);
    }

    private static GuildGeneralInformationEvent Header(byte[] packet)
    {
        var frame = Wire.Decode(packet);
        Assert.Null(frame.Uid);
        return Assert.IsType<GuildGeneralInformationEvent>(frame.Content);
    }

    private static (GameSession Session, List<byte[]> Packets, GameMessagingTests.CountingCodec Codec) Recipient(Func<byte[], Task>? after = null)
    {
        var packets = new List<byte[]>();
        var codec = new GameMessagingTests.CountingCodec();
        return (new GameSession(codec, packet => { packets.Add(packet); return after?.Invoke(packet) ?? Task.CompletedTask; }), packets, codec);
    }

    private sealed class Backend : IGuildStateBackend
    {
        internal readonly List<GuildStore.Member> People = [];
        internal readonly Dictionary<long, long> Totals = [];
        internal readonly Dictionary<long, GameSession> Sessions = [];
        internal readonly List<string> Calls = [];
        internal int? Hall;
        internal string FailAt = "";
        internal Exception? Failure;
        private void Call(string name) { Calls.Add(name); if (name == FailAt) throw Failure!; }
        public int? HallId(long guildId) { Call($"hall:{guildId}"); return Hall; }
        public long ContributedBy(long characterId) { Call($"contributed:{characterId}"); return Totals.GetValueOrDefault(characterId); }
        public IReadOnlyList<GuildStore.Member> Members(long guildId) { Call($"members:{guildId}"); return People; }
        public GameSession? FindSession(long characterId) { Call($"session:{characterId}"); return Sessions.GetValueOrDefault(characterId); }
    }

    private sealed class FailingCodec(Exception failure) : IGameCodec
    {
        public byte[] Encode(GameFrame frame) => throw failure;
        public GameFrame Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("No decoding expected.");
    }
}
