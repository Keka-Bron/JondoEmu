using System.Globalization;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class EntryInformationMessagesTests
{
    private static readonly GameCodec Wire = new();

    [Fact]
    public void Initial_warning_is_the_parameterless_error_notice_and_first_visit_has_no_connection_notice()
    {
        var warning = EntryInformationMessages.InitialWarning();
        Assert.Equal(TextInformationType.TextInformationError, warning.MessageType);
        Assert.Equal(89, warning.MessageId);
        Assert.Empty(warning.Parameters);
        Assert.Null(EntryInformationMessages.PreviousConnection(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Missing_address_selects_193_and_five_ordered_zero_padded_parameters(string? ip)
    {
        var notice = EntryInformationMessages.PreviousConnection(new()
        {
            When = new DateTimeOffset(2026, 1, 2, 3, 4, 59, TimeSpan.FromHours(-7)), Ip = ip!,
        });
        Assert.NotNull(notice);
        Assert.Equal(TextInformationType.TextInformationMessage, notice.MessageType);
        Assert.Equal(193, notice.MessageId);
        Assert.Equal(new[] { "2026", "01", "02", "03", "04" }, notice.Parameters);
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("2001:db8::7")]
    [InlineData(" 203.0.113.7 ")]
    public void Present_address_selects_152_and_is_preserved_exactly_as_the_sixth_parameter(string ip)
    {
        var notice = EntryInformationMessages.PreviousConnection(new()
        {
            When = new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero), Ip = ip,
        });
        Assert.NotNull(notice);
        Assert.Equal(TextInformationType.TextInformationMessage, notice.MessageType);
        Assert.Equal(152, notice.MessageId);
        Assert.Equal(new[] { "2026", "01", "02", "03", "04", ip }, notice.Parameters);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void Formatting_is_invariant_and_retains_the_stored_offsets_calendar_even_across_a_UTC_day_boundary(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var notice = EntryInformationMessages.PreviousConnection(new()
            {
                When = new DateTimeOffset(42, 1, 2, 0, 4, 0, TimeSpan.FromHours(14)),
            });
            Assert.NotNull(notice);
            Assert.Equal(new[] { "0042", "01", "02", "00", "04" }, notice.Parameters);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(false, false, 32)]
    [InlineData(false, true, 33)]
    [InlineData(true, false, 33)]
    [InlineData(true, true, 34)]
    public async Task Empty_reference_sequence_sends_the_owned_notices_and_counts_both_optional_sources(bool returning, bool calendar, int expected)
    {
        var own = new Recipient();
        var wrong = new Recipient();
        own.Session.State.PreviousVisit = returning ? Visit("203.0.113.7") : null;
        own.Session.State.ClientIp = "198.51.100.8";
        wrong.Session.State.PreviousVisit = Visit("2001:db8::99");
        using var context = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();

        int sent = await Initial(own.Session.State, calendar: calendar).SendAsync(own.Session).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expected, sent);
        Assert.Equal(expected, own.Packets.Count);
        Assert.Equal(expected, own.Codec.Frames.Count);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        var notices = own.Codec.Frames.Select(frame => frame.Content).OfType<TextInformationEvent>().ToArray();
        Assert.Equal(returning ? 2 : 1, notices.Length);
        Assert.Equal(89, notices[0].MessageId);
        if (returning)
        {
            Assert.Equal(152, notices[1].MessageId);
            Assert.Equal(new[] { "2026", "01", "02", "03", "04", "203.0.113.7" }, notices[1].Parameters);
        }
        Assert.Equal(calendar ? 1 : 0, own.Codec.Frames.Count(frame => frame.Content is AlmanachCalendarDateEvent));
        Assert.Empty(wrong.Packets);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notices_follow_sequence_number_and_precede_spouse_and_alterations(bool returning)
    {
        var own = new Recipient();
        own.Session.State.PreviousVisit = returning ? Visit("") : null;
        int reads = 0;
        var initial = Initial(own.Session.State, () =>
        {
            reads++;
            return EntryInformationMessages.PreviousConnection(own.Session.State.PreviousVisit);
        });
        int sent = await initial.SendAsync(own.Session);
        var sequence = own.Packets.Select(packet => Name(packet)).ToList();
        int index = sequence.IndexOf(nameof(SequenceNumberEvent));
        Assert.Equal(returning
            ? new[] { nameof(SequenceNumberEvent), nameof(TextInformationEvent), nameof(TextInformationEvent), nameof(SpouseStatusEvent), nameof(AlterationsEvent) }
            : [nameof(SequenceNumberEvent), nameof(TextInformationEvent), nameof(SpouseStatusEvent), nameof(AlterationsEvent)],
            sequence.Skip(index).Take(returning ? 5 : 4));
        Assert.Equal(1, reads);
        Assert.Equal(own.Packets.Count, sent);
        Assert.Equal(returning ? 34 : 33, own.Codec.Frames.Count);
        var notices = own.Codec.Frames.Select(frame => frame.Content).OfType<TextInformationEvent>().ToArray();
        Assert.Equal(returning ? new[] { 89, 193 } : [89], notices.Select(notice => notice.MessageId));
    }

    [Fact]
    public void Map_notice_reports_the_current_online_mutual_friend_count()
    {
        var notice = EntryInformationMessages.OnlineMutualFriends(2);
        Assert.Equal(TextInformationType.TextInformationMessage, notice.MessageType);
        Assert.Equal(197, notice.MessageId);
        Assert.Equal(new[] { "2" }, notice.Parameters);
    }

    [Theory]
    [InlineData("source", 193)]
    [InlineData("codec", 89)]
    [InlineData("codec", 193)]
    [InlineData("transport", 89)]
    [InlineData("transport", 193)]
    public async Task Notice_failure_stops_before_spouse_and_alterations_without_retry(string stage, int failedId)
    {
        var failure = new IOException("test entry notice failure");
        int reads = 0;
        var transportNotices = new List<int>();
        var own = new Recipient(packet =>
        {
            if (Name(packet) != nameof(TextInformationEvent)) return Task.CompletedTask;
            int id = Assert.IsType<TextInformationEvent>(Wire.Decode(packet).Content).MessageId;
            transportNotices.Add(id);
            return stage == "transport" && id == failedId ? Task.FromException(failure) : Task.CompletedTask;
        });
        own.Codec.Failure = frame => stage == "codec" && frame.Content is TextInformationEvent notice && notice.MessageId == failedId ? failure : null;
        var initial = Initial(own.Session.State, () =>
        {
            reads++;
            if (stage == "source") throw failure;
            return EntryInformationMessages.PreviousConnection(Visit(""));
        });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => initial.SendAsync(own.Session)));
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is SpouseStatusEvent);
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is AlterationsEvent or ServerExperienceModifierEvent);
        Assert.Equal(failedId == 89 ? 0 : 1, reads);
        var attempted = own.Codec.Frames.Select(frame => frame.Content).OfType<TextInformationEvent>().ToArray();
        int[] expectedAttempts = stage == "source" || failedId == 89 ? [89] : [89, 193];
        Assert.Equal(expectedAttempts, attempted.Select(notice => notice.MessageId));
        Assert.Equal(stage == "codec" ? expectedAttempts[..^1] : expectedAttempts, transportNotices);
        Assert.Equal(0, own.Codec.Decodes);
    }

    private static DatabaseManager.LastVisit Visit(string ip)
        => new() { When = new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.FromHours(-7)), Ip = ip };

    private static GameEntryInitialMessages Initial(SessionState state, Func<TextInformationEvent?>? previous = null, bool calendar = true)
        => new([], [], state, new([], _ => []), 0, 0, () => new([]), () => new("1970-01-01T00:00:00.000Z", []),
            () => new([]), () => GameEntryInitialMessagesTests.Selection(), () => InventoryMessages.FromCatalogue.Snapshot(state),
            () => new([]), () => calendar ? new(81_001) : null,
            previous ?? (() => EntryInformationMessages.PreviousConnection(state.PreviousVisit)), AfterSelectionMessagesTests.Empty,
            InitialPresentationMessagesTests.Empty);

    private static string Name(byte[] packet) => GameBindings.StableName(Wire.ReadEnvelope(packet).Alias)!;

    private sealed class NoticeCodec : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal Func<GameFrame, Exception?>? Failure { get; set; }
        internal int Decodes;
        public GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Decodes++;
            throw new InvalidOperationException("No production decode expected.");
        }
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (Failure?.Invoke(frame) is { } failure) throw failure;
            return Wire.Encode(frame);
        }
    }

    private sealed class Recipient
    {
        internal List<byte[]> Packets { get; } = [];
        internal NoticeCodec Codec { get; } = new();
        internal GameSession Session { get; }
        internal Recipient(Func<byte[], Task>? send = null)
            => Session = new(Codec, packet => { Packets.Add(packet); return send?.Invoke(packet) ?? Task.CompletedTask; });
    }
}
