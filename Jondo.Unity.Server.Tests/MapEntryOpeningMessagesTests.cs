using System.Globalization;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class MapEntryOpeningMessagesTests
{
    internal static MapEntryOpeningMessages Empty { get; } = new(_ => false, _ => 0, _ => [], (_, _) => false, () => []);

    [Theory]
    [InlineData(false, 5, 0)]
    [InlineData(true, 5, 5)]
    [InlineData(true, 2, 2)]
    [InlineData(true, 0, 0)]
    public void Contributions_use_the_characters_weekly_allowance_and_guildless_characters_have_zero(bool guild, int left, int expected)
    {
        var own = new Recipient();
        int reads = 0;
        var opening = new MapEntryOpeningMessages(id => { Assert.Equal(81_001, id); return guild; },
            id => { Assert.Equal(81_001, id); reads++; return left; }, _ => [], (_, _) => false, () => []);

        var messages = opening.Snapshot(own.Session, GameContext.RolePlay);

        Assert.Equal(expected, Assert.IsType<GuildContributionLeftEvent>(messages[1]).ContributionLeft);
        Assert.Equal(guild ? 1 : 0, reads);
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("online-mutual", 2)]
    [InlineData("offline-mutual", 0)]
    [InlineData("one-way", 0)]
    [InlineData("duplicates", 1)]
    public void Friend_notice_counts_distinct_online_mutual_accounts(string scenario, int expected)
    {
        long[] contacts = scenario == "none" ? [] : [91_001, 91_002];
        long[] online = scenario == "offline-mutual" ? [] : [91_001, 91_002];
        if (scenario == "duplicates")
        {
            contacts = [91_001, 91_001, 71_001];
            online = [91_001, 91_001, 71_001];
        }
        var own = new Recipient();
        var opening = new MapEntryOpeningMessages(_ => false, _ => throw new Exception("Guildless allowance read"),
            account => { Assert.Equal(71_001, account); return contacts; },
            (other, account) => { Assert.Equal(71_001, account); return scenario != "one-way"; }, () => online);

        var notice = Assert.IsType<TextInformationEvent>(opening.Snapshot(own.Session, GameContext.RolePlay)[2]);

        Assert.Equal(TextInformationType.TextInformationMessage, notice.MessageType);
        Assert.Equal(197, notice.MessageId);
        Assert.Equal(new[] { expected.ToString(CultureInfo.InvariantCulture) }, notice.Parameters);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void Friend_notice_formats_the_count_invariantly(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(new[] { "1234" }, EntryInformationMessages.OnlineMutualFriends(1234).Parameters);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(GameContext.RolePlay)]
    [InlineData(GameContext.Fight)]
    public async Task Opening_precedes_the_map_and_channel_updates_remain_after_community(GameContext context)
    {
        var sequence = new List<object>();
        var own = new Recipient(send: frame => { sequence.Add(frame.Content); return Task.CompletedTask; });
        // Deliberately disagree with the explicit context: entry resolution owns it.
        own.Session.State.IsInFight = context == GameContext.RolePlay;
        int sent = await GameEntryMapMessagesTests.Default.SendAsync(own.Session, Empty, context, 91_001);

        Assert.Equal(context == GameContext.RolePlay ? 34 : 32, sent);
        Assert.Equal(new PongEvent(true), Assert.IsType<PongEvent>(sequence[0]));
        Assert.Equal(new GuildContributionLeftEvent(0), Assert.IsType<GuildContributionLeftEvent>(sequence[1]));
        var notice = Assert.IsType<TextInformationEvent>(sequence[2]);
        Assert.Equal(197, notice.MessageId);
        Assert.Equal(new[] { "0" }, notice.Parameters);
        Assert.IsType<ContextDestroyEvent>(sequence[3]);
        Assert.Equal(new ContextCreationEvent(context), Assert.IsType<ContextCreationEvent>(sequence[4]));
        int community = sequence.FindIndex(message => message is CommunityChannelEvent);
        Assert.Equal(ShortcutBar.Spell, Assert.IsType<ShortcutBarContentEvent>(sequence[community - 2]).BarType);
        Assert.Equal(ShortcutBar.General, Assert.IsType<ShortcutBarContentEvent>(sequence[community - 1]).BarType);
        Assert.All(sequence.Skip(community + 1).Take(20), message => Assert.IsType<ChannelUpdateEvent>(message));
        Assert.Equal(new BakApiKeyEvent(""), sequence[^1]);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Fact]
    public async Task Explicit_owner_and_one_snapshot_supply_values_despite_another_ambient_session_and_transport_mutation()
    {
        int left = 3, guildReads = 0, allowanceReads = 0, contactReads = 0, onlineReads = 0, reciprocalReads = 0;
        var contacts = new List<long> { 91_001 };
        var online = new List<long> { 91_001 };
        var own = new Recipient(send: _ => { left = 0; contacts.Clear(); online.Clear(); return Task.CompletedTask; });
        var other = new Recipient(71_002, 81_002);
        using var scope = SessionContext.Push(other.Session);
        var opening = new MapEntryOpeningMessages(
            id => { guildReads++; Assert.Equal(81_001, id); return true; },
            id => { allowanceReads++; Assert.Equal(81_001, id); return left; },
            id => { contactReads++; Assert.Equal(71_001, id); return contacts; },
            (friend, owner) => { reciprocalReads++; Assert.Equal(91_001, friend); Assert.Equal(71_001, owner); return true; },
            () => { onlineReads++; return online; });

        await GameEntryMapMessagesTests.Default.SendAsync(own.Session,
            opening, GameContext.RolePlay, 91_001);

        Assert.Equal(3, Assert.IsType<GuildContributionLeftEvent>(own.Codec.Frames[1].Content).ContributionLeft);
        Assert.Equal(new[] { "1" }, Assert.IsType<TextInformationEvent>(own.Codec.Frames[2].Content).Parameters);
        Assert.Equal((1, 1, 1, 1, 1), (guildReads, allowanceReads, contactReads, onlineReads, reciprocalReads));
        Assert.Empty(other.Codec.Frames);
        Assert.Same(other.Session, SessionContext.Actual);
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Theory]
    [InlineData("guild")]
    [InlineData("allowance")]
    [InlineData("contacts")]
    [InlineData("online")]
    [InlineData("reciprocal")]
    public async Task Source_failure_propagates_before_sending_a_partial_opening(string source)
    {
        var failure = new IOException("map opening source failed");
        T Read<T>(string stage, T value) => stage == source ? throw failure : value;
        var opening = new MapEntryOpeningMessages(_ => Read("guild", true), _ => Read("allowance", 3),
            _ => Read("contacts", new long[] { 91_001 }), (_, _) => Read("reciprocal", true),
            () => Read("online", new long[] { 91_001 }));
        var own = new Recipient();

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => GameEntryMapMessagesTests.Default.SendAsync(own.Session,
            opening, GameContext.RolePlay, 91_001)));

        Assert.Empty(own.Codec.Frames);
        Assert.Empty(own.Delivered);
    }

    public static IEnumerable<object[]> DeliveryFailures()
        => Enumerable.Range(0, 5).SelectMany(index => new[] { new object[] { index, true }, new object[] { index, false } });

    [Theory]
    [MemberData(nameof(DeliveryFailures))]
    public async Task Delivery_failure_stops_at_the_failed_opening_event_without_retry_or_legacy_fallback(int index, bool codecFailure)
    {
        var failure = new IOException("opening delivery failed");
        int transports = 0;
        var own = new Recipient(codecFailure: codecFailure ? frame => EventIndex(frame.Content) == index ? failure : null : null,
            send: _ => { transports++; return !codecFailure && transports == index + 1 ? Task.FromException(failure) : Task.CompletedTask; });
        var other = new Recipient(71_002, 81_002);
        using var scope = SessionContext.Push(other.Session);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => GameEntryMapMessagesTests.Default.SendAsync(own.Session,
            Empty, GameContext.Fight, 91_001)));

        Assert.Equal(index + 1, own.Codec.Frames.Count);
        Assert.Equal(index, own.Delivered.Count);
        Assert.Equal(codecFailure ? index : index + 1, transports);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Empty(other.Codec.Frames);
        Assert.Same(other.Session, SessionContext.Actual);
    }

    private static int EventIndex(GamePayload message) => message switch
    {
        PongEvent => 0, GuildContributionLeftEvent => 1, TextInformationEvent => 2,
        ContextDestroyEvent => 3, ContextCreationEvent => 4,
        _ => throw new Exception("Unexpected event after the opening failed"),
    };

    private sealed class Recipient
    {
        internal RecordingCodec Codec { get; }
        internal List<GamePayload> Delivered { get; } = [];
        internal GameSession Session { get; }

        internal Recipient(long accountId = 71_001, long characterId = 81_001,
            Func<GameFrame, Exception?>? codecFailure = null, Func<GameFrame, Task>? send = null)
        {
            Codec = new(codecFailure);
            Session = new(Codec, async _ =>
            {
                var frame = Codec.Frames[^1];
                if (send != null) await send(frame);
                Delivered.Add(frame.Content);
            });
            Session.BindAccount(accountId, 1);
            Session.State.CharacterId = characterId;
        }
    }

    private sealed class RecordingCodec(Func<GameFrame, Exception?>? failure) : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal int Decodes { get; private set; }
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (failure?.Invoke(frame) is { } error) throw error;
            return []; // An opaque transport token; these tests never serialize messages.
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Decodes++;
            throw new InvalidOperationException("Unexpected decode in semantic map entry");
        }
    }
}
