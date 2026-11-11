using System.Globalization;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ArenaPlayerInformationTests
{
    private static readonly GameCodec Wire = new();
    private static readonly DateTime Now = new(2026, 11, 11, 12, 34, 56, 789, DateTimeKind.Utc);

    [Fact]
    public void Standings_keep_current_and_best_leagues_distinct_actual_ratings_placement_and_unequal_counters()
    {
        KoliseoLadder.Standing[] source = [
            new() { Mode = 0, Rating = 285 },
            new() { Mode = 1, Rating = 645, League = 7, BestLeague = 9, PlacementLeft = 0,
                SeasonWins = 17, SeasonFights = 31, DayWins = 2, DayFights = 5 },
            new() { Mode = 2, Rating = 1234, League = 14, BestLeague = 18, PlacementLeft = 0,
                SeasonWins = 43, SeasonFights = 67, DayWins = 3, DayFights = 8 },
            new() { Mode = 3, Rating = 420, PlacementLeft = 2,
                SeasonWins = 1, SeasonFights = 3, DayWins = 0, DayFights = 1 }];
        var actual = ArenaPlayerInformationMessages.Snapshot(source, Now.AddMinutes(5), Now);
        Assert.Equal("2026-11-11T12:39:56.789Z", actual.BanEndDate);
        ArenaRank[] expected = [
            new(ArenaType.OneVsOne, 0, -1, 5, 0, 0, 0, new(-1, 285, 0), 0),
            new(ArenaType.TwoVsTwo, 0, 9, 0, 17, 31, 2, new(7, 645, 0), 5),
            new(ArenaType.ThreeVsThree, 0, 18, 0, 43, 67, 3, new(14, 1234, 0), 8),
            new(ArenaType.Event, 0, -1, 2, 1, 3, 0, new(-1, 420, 0), 1)];
        Assert.Equal(expected, actual.ArenaRanks);
        // The snapshot is detached from mutable source standings.
        source[1].Rating = 999;
        source[1].BestLeague = 10;
        Assert.Equal(expected, actual.ArenaRanks);
    }

    [Theory]
    [InlineData(-1, "1970-01-01T00:00:00.000Z")]
    [InlineData(0, "1970-01-01T00:00:00.000Z")]
    [InlineData(1, "2026-11-11T12:34:56.790Z")]
    public void Penalty_expiry_uses_the_explicit_clock_and_exact_expiry_is_no_longer_a_ban(int milliseconds, string expected)
    {
        var actual = ArenaPlayerInformationMessages.Snapshot([], Now.AddMilliseconds(milliseconds), Now);
        Assert.Equal(expected, actual.BanEndDate);
        Assert.Empty(actual.ArenaRanks);
        Assert.True(DateTime.TryParse(actual.BanEndDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
    }

    [Fact]
    public void No_penalty_uses_a_parseable_past_date_and_formatting_is_independent_of_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("1970-01-01T00:00:00.000Z", ArenaPlayerInformationMessages.Snapshot([], null, Now).BanEndDate);
            Assert.Equal("2026-11-11T12:39:56.789Z", ArenaPlayerInformationMessages.Snapshot([], Now.AddMinutes(5), Now).BanEndDate);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Unsupported_modes_fail_construction(int mode)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            ArenaPlayerInformationMessages.Snapshot([new() { Mode = mode }], null, Now));

    [Fact]
    public void Penalty_store_uses_the_supplied_instant_and_clears_at_expiry()
    {
        const long characterId = 81_190_001;
        try
        {
            KoliseoOffers.Ban(characterId, Now.AddMinutes(5));
            Assert.Equal(Now.AddMinutes(5), KoliseoOffers.BannedUntil(characterId, Now));
            Assert.Null(KoliseoOffers.BannedUntil(characterId, Now.AddMinutes(5)));
            Assert.Null(KoliseoOffers.BannedUntil(characterId, Now));
        }
        finally
        {
            KoliseoOffers.Ban(characterId, DateTime.UnixEpoch);
            KoliseoOffers.BannedUntil(characterId, Now);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Return_uses_owning_character_level_clock_and_codec_and_retains_order_and_signed_correlation(int uid)
    {
        var backend = new Backend();
        var service = new ArenaPlayerInformationService(backend);
        var first = Recipient(71_001, 57);
        var second = Recipient(71_002, 150);
        backend.Sources[first.Session.CharacterId] = [new() { Mode = 1, Rating = 645, League = 7, BestLeague = 9,
            PlacementLeft = 0, SeasonWins = 17, SeasonFights = 31, DayWins = 2, DayFights = 5 }];
        backend.Sources[second.Session.CharacterId] = [];
        backend.Bans[first.Session.CharacterId] = Now.AddMinutes(5);

        using (SessionContext.Push(second.Session))
        using (await first.Session.EnterTurnAsync())
        {
            await service.ReturnAsync(first.Session, uid).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(second.Session, SessionContext.Actual);
        }
        Assert.Empty(second.Packets);
        Assert.Equal(0, second.Codec.Encodes);
        using (SessionContext.Push(first.Session)) await service.ReturnAsync(second.Session, uid);

        Assert.Equal(new[] { "leave:71001", "clock", "standings:71001:57", "ban:71001", "last-mode:71001",
            "leave:71002", "clock", "standings:71002:150", "ban:71002", "last-mode:71002" }, backend.Calls);
        Assert.All(backend.BanInstants, instant => Assert.Equal(Now, instant));
        var snapshot = Assert.IsType<ArenaUpdatePlayerInformationEvent>(Wire.Decode(first.Packets[0]).Content);
        Assert.Equal("2026-11-11T12:39:56.789Z", snapshot.BanEndDate);
        Assert.Equal(new ArenaRank(ArenaType.TwoVsTwo, 0, 9, 0, 17, 31, 2, new(7, 645, 0), 5), Assert.Single(snapshot.ArenaRanks));
        var empty = Assert.IsType<ArenaUpdatePlayerInformationEvent>(Wire.Decode(second.Packets[0]).Content);
        Assert.Empty(empty.ArenaRanks);
        Assert.Equal("1970-01-01T00:00:00.000Z", empty.BanEndDate);
        foreach (var target in new[] { first, second })
        {
            Assert.Equal(3, target.Packets.Count);
            Assert.Null(Wire.ReadEnvelope(target.Packets[0]).Uid);
            var response = Wire.ReadEnvelope(target.Packets[1]);
            Assert.Equal(GameMessageKind.Response, response.Kind);
            Assert.Equal(uid, response.Uid);
            Assert.Empty(response.Content.Value);
            Assert.Equal(GameMessageKind.Event, Wire.ReadEnvelope(target.Packets[2]).Kind);
            Assert.Null(Wire.ReadEnvelope(target.Packets[2]).Uid);
            // Only the two untouched legacy sends are compared as bytes.
            Assert.Equal(ConnectionProtocol.Answer(Op.Lsr, null, uid), target.Packets[1]);
            Assert.Equal(ConnectionProtocol.Push(Op.Lsx, KoliseoHandler.BuildLeftQueue(target.Session.CharacterId == 71_001 ? 1 : 2)),
                target.Packets[2]);
            Assert.Equal(1, target.Codec.Encodes);
            Assert.Equal(0, target.Codec.Decodes);
        }
    }

    [Fact]
    public async Task Return_without_a_request_omits_only_the_correlated_response()
    {
        var target = Recipient(71_002, 150);
        await new ArenaPlayerInformationService(new Backend()).ReturnAsync(target.Session);
        Assert.Equal(2, target.Packets.Count);
        Assert.IsType<ArenaUpdatePlayerInformationEvent>(Wire.Decode(target.Packets[0]).Content);
        Assert.All(target.Packets, packet => Assert.Equal(GameMessageKind.Event, Wire.ReadEnvelope(packet).Kind));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Return_transport_failure_propagates_and_stops_at_the_failing_send(int failAt)
    {
        var failure = new IOException("arena transport failure");
        var backend = new Backend();
        int sends = 0;
        var target = Recipient(71_001, 57, _ => ++sends == failAt ? Task.FromException(failure) : Task.CompletedTask);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
            new ArenaPlayerInformationService(backend).ReturnAsync(target.Session, -1)));
        Assert.Equal(failAt, sends);
        Assert.Equal(failAt == 3, backend.Calls.Contains("last-mode:71001"));
        Assert.Equal(1, target.Codec.Encodes);
    }

    [Fact]
    public async Task Source_failure_is_not_reclassified_or_retried_and_sends_nothing()
    {
        var failure = new InvalidOperationException("arena source failure");
        var backend = new Backend { SourceFailure = failure };
        var target = Recipient(71_001, 57);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArenaPlayerInformationService(backend).ReturnAsync(target.Session, -1)));
        Assert.Empty(target.Packets);
        Assert.Equal(0, target.Codec.Encodes);
        Assert.Equal(new[] { "leave:71001", "clock", "standings:71001:57" }, backend.Calls);
    }

    [Fact]
    public async Task Codec_failure_is_not_reclassified_as_a_source_or_transport_failure()
    {
        var failure = new GameConversionException("arena codec failure");
        var backend = new Backend();
        int sends = 0;
        var session = new GameSession(new FailingCodec(failure), _ => { sends++; return Task.CompletedTask; });
        session.State.CharacterId = 71_001;
        Assert.Same(failure, await Assert.ThrowsAsync<GameConversionException>(() =>
            new ArenaPlayerInformationService(backend).ReturnAsync(session, -1)));
        Assert.Equal(0, sends);
        Assert.DoesNotContain("last-mode:71001", backend.Calls);
    }

    [Fact]
    public async Task Initial_entry_builds_once_after_selection_without_captured_emote_grants()
    {
        var target = Recipient(71_001, 57);
        var backend = new Backend();
        backend.Sources[target.Session.CharacterId] = [new() { Mode = 2, Rating = 1234, League = 14,
            BestLeague = 18, PlacementLeft = 0, SeasonWins = 43, SeasonFights = 67, DayWins = 3, DayFights = 8 }];
        var service = new ArenaPlayerInformationService(backend);
        var initial = new GameEntryInitialMessages([], [], target.Session.State, new([], _ => []), 0, 0,
            () => new([]), () => service.Snapshot(target.Session.CharacterId, target.Session.State.CharacterLevel), () => new([]),
            () => GameEntryInitialMessagesTests.Selection(target.Session.CharacterId),
            () => InventoryMessages.FromCatalogue.Snapshot(target.Session.State), () => new([]), () => new(81_001),
            () => EntryInformationMessages.PreviousConnection(target.Session.State.PreviousVisit), AfterSelectionMessagesTests.Empty,
            InitialPresentationMessagesTests.Empty);
        await initial.SendAsync(target.Session);
        var names = target.Packets.Select(packet => Name(Wire.ReadEnvelope(packet).Alias)).ToList();
        int index = names.IndexOf(nameof(ArenaUpdatePlayerInformationEvent));
        Assert.Equal(nameof(CharacterSelectionEvent), names[index - 1]);
        Assert.DoesNotContain(nameof(EmoteAddedEvent), names);
        Assert.DoesNotContain(nameof(EmotesEvent), names);
        Assert.Equal(1, names.Count(name => name == nameof(ArenaUpdatePlayerInformationEvent)));
        Assert.Equal(new[] { "clock", "standings:71001:57", "ban:71001" }, backend.Calls);
        var actual = Assert.IsType<ArenaUpdatePlayerInformationEvent>(Wire.Decode(target.Packets[index]).Content);
        Assert.Equal(new ArenaRank(ArenaType.ThreeVsThree, 0, 18, 0, 43, 67, 3, new(14, 1234, 0), 8),
            Assert.Single(actual.ArenaRanks));
    }

    private static string Name(string alias) => GameBindings.StableName(alias) ?? alias;
    private static (GameSession Session, List<byte[]> Packets, GameMessagingTests.CountingCodec Codec) Recipient(
        long id, int level, Func<byte[], Task>? send = null)
    {
        var packets = new List<byte[]>();
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, send ?? (packet => { packets.Add(packet); return Task.CompletedTask; }));
        session.State.CharacterId = id;
        session.State.CharacterLevel = level;
        return (session, packets, codec);
    }

    private sealed class Backend : IArenaPlayerInformationBackend
    {
        internal readonly Dictionary<long, IReadOnlyList<KoliseoLadder.Standing>> Sources = [];
        internal readonly Dictionary<long, DateTime> Bans = [];
        internal readonly List<string> Calls = [];
        internal readonly List<DateTime> BanInstants = [];
        internal Exception? SourceFailure;
        public DateTime UtcNow { get { Calls.Add("clock"); return Now; } }
        public IReadOnlyList<KoliseoLadder.Standing> Standings(long characterId, int level)
        {
            Calls.Add($"standings:{characterId}:{level}");
            if (SourceFailure != null) throw SourceFailure;
            return Sources.GetValueOrDefault(characterId) ?? [];
        }
        public DateTime? BannedUntil(long characterId, DateTime nowUtc)
        {
            Calls.Add($"ban:{characterId}");
            BanInstants.Add(nowUtc);
            return Bans.TryGetValue(characterId, out var ban) ? ban : null;
        }
        public void LeaveQueue(long characterId) => Calls.Add($"leave:{characterId}");
        public int LastMode(long characterId)
        {
            Calls.Add($"last-mode:{characterId}");
            return characterId == 71_001 ? 1 : 2;
        }
    }

    private sealed class FailingCodec(GameConversionException failure) : IGameCodec
    {
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("No decode expected.");
        public byte[] Encode(GameFrame message) => throw failure;
    }
}
