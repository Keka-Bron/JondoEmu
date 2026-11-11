using System.Globalization;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class AlterationSnapshotTests
{
    private static readonly GameCodec Wire = new();
    private static readonly DateTime Now = DateTime.Parse("2026-01-15T23:30:00Z", CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    private static readonly long NowMs = new DateTimeOffset(Now).ToUnixTimeMilliseconds();

    [Theory]
    [InlineData(Alterations.Kind.Lasting, AlterationExpirationType.Infinite, 0L)]
    [InlineData(Alterations.Kind.Timed, AlterationExpirationType.Date, 4_294_967_313L)]
    [InlineData(Alterations.Kind.Fights, AlterationExpirationType.FightCount, 3L)]
    [InlineData(Alterations.Kind.Victories, AlterationExpirationType.FightsWonCount, 3L)]
    public void Expiration_kinds_timestamps_and_missing_definitions_are_preserved(
        Alterations.Kind kind, AlterationExpirationType expectedKind, long expectedValue)
    {
        var active = new Alterations.Active { Id = 81_001, FromMs = 4_294_967_311L, Kind = kind,
            UntilMs = 4_294_967_313L, Left = 3, Total = 9 };
        var builder = new AlterationMessages(_ => null);
        var result = Assert.Single(builder.Snapshot([active], Now).Alterations);
        Assert.Equal(active.FromMs, result.CreationTime);
        Assert.Equal(81_001, result.AlterationId);
        Assert.Equal(expectedKind, result.ExpirationType);
        Assert.Equal(expectedValue, result.ExpirationValue);
        Assert.Empty(result.Effects);
        Assert.Equal(3, active.Left);
        Assert.Empty(builder.Snapshot([], Now).Alterations);
    }

    [Fact]
    public void Effects_keep_reverse_catalogue_order_scalar_fallbacks_unset_values_and_zero_remaining_counters()
    {
        var builder = WithEffects(E(91_001, -17), E(91_002, 0, 23), E(91_003, 0, 0, 31),
            E(91_004), E(AlterationCatalogue.FightsLeft, 999));
        var active = new Alterations.Active { Id = 81_001, FromMs = NowMs, Kind = Alterations.Kind.Fights, Left = 0, Total = 7 };
        var actual = builder.Entry(active, Now);
        Assert.Equal(new ObjectEffect[] {
            new(AlterationCatalogue.FightsLeft, new ObjectEffectCaseValueDice(new(0, 7, 7))),
            new(91_004, new ObjectEffectCaseNone()),
            new(91_003, new ObjectEffectCaseValueInt(31)),
            new(91_002, new ObjectEffectCaseValueInt(23)),
            new(91_001, new ObjectEffectCaseValueInt(-17)) }, actual.Effects);
        Assert.Equal(0, actual.ExpirationValue);
        Assert.Equal(0, active.Left);
        Assert.Equal(7, active.Total);
    }

    [Theory]
    [InlineData(-1000L, 0L)]
    [InlineData(59_999L, 0L)]
    [InlineData(60_000L, 1L)]
    [InlineData(119_999L, 1L)]
    [InlineData(120_000L, 2L)]
    [InlineData(3_600_000L, 10L)]
    public void Timed_minutes_truncate_at_boundaries_and_clamp_without_filtering_or_mutating_expired_entries(long remainingMs, long minutes)
    {
        var active = new Alterations.Active { Id = 81_001, Kind = Alterations.Kind.Timed, UntilMs = NowMs + remainingMs };
        var result = Assert.Single(WithEffects(E(2867, 150, 10, 7)).Snapshot([active], Now).Alterations);
        Assert.Equal(active.UntilMs, result.ExpirationValue);
        Assert.Equal(new ObjectEffect(2867, new ObjectEffectCaseValueDice(new(150, minutes, 7))), Assert.Single(result.Effects));
        Assert.Equal(NowMs + remainingMs, active.UntilMs);
    }

    [Fact]
    public void Minute_values_remain_int64_and_counted_effects_use_catalogue_totals_when_not_count_limited()
    {
        long minutes = (long)int.MaxValue + 17;
        var timed = new Alterations.Active { Id = 81_001, Kind = Alterations.Kind.Timed, UntilMs = NowMs + minutes * 60_000L };
        Assert.Equal(new ObjectEffect(2869, new ObjectEffectCaseValueDice(new(25, minutes, 0))),
            Assert.Single(WithEffects(E(2869, 25)).Entry(timed, Now).Effects));
        var lasting = new Alterations.Active { Id = 81_001, Kind = Alterations.Kind.Lasting };
        Assert.Equal(new ObjectEffect(AlterationCatalogue.VictoriesLeft, new ObjectEffectCaseValueDice(new(7, 7, 7))),
            Assert.Single(WithEffects(E(AlterationCatalogue.VictoriesLeft, 3, 7, 5)).Entry(lasting, Now).Effects));
    }

    [Theory]
    [InlineData("2026-01-15T23:30:00Z", 16, 1, 30, 0)]
    [InlineData("2026-07-15T23:30:00Z", 16, 7, 30, 1)]
    [InlineData("2026-03-29T00:59:00Z", 29, 3, 59, 1)]
    [InlineData("2026-03-29T01:00:00Z", 29, 3, 0, 3)]
    public void Expiry_dates_use_Paris_winter_summer_and_DST_boundaries(string utc, int day, int month, int minute, int hour)
    {
        var date = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);
        var active = new Alterations.Active { Id = 81_001, Kind = Alterations.Kind.Timed, UntilMs = date.ToUnixTimeMilliseconds() };
        Assert.Equal(new ObjectEffect(AlterationCatalogue.Expiry,
            new ObjectEffectCaseValueDate(new(day, 656, month, minute, hour))),
            Assert.Single(WithEffects(E(AlterationCatalogue.Expiry, 999)).Entry(active, Now).Effects));
    }

    [Fact]
    public void Non_timed_expiry_effect_uses_the_explicit_clock_and_catalogue_duration()
    {
        var active = new Alterations.Active { Id = 81_001, Kind = Alterations.Kind.Lasting };
        Assert.Equal(new ObjectEffect(AlterationCatalogue.Expiry, new ObjectEffectCaseValueDate(new(16, 656, 1, 0, 2))),
            Assert.Single(WithEffects(E(AlterationCatalogue.Expiry, 0, 90)).Entry(active, Now).Effects));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Scalar_int32_boundaries_are_preserved(int value)
    {
        var active = new Alterations.Active { Id = 81_001, Kind = Alterations.Kind.Lasting };
        Assert.Equal(new ObjectEffect(91_001, new ObjectEffectCaseValueInt(value)),
            Assert.Single(WithEffects(E(91_001, value)).Entry(active, Now).Effects));
    }

    [Fact]
    public void Unrepresentable_scalar_dice_and_catalogue_counts_fail_instead_of_narrowing_silently()
    {
        var active = new Alterations.Active { Id = 81_001, Kind = Alterations.Kind.Lasting };
        Equipment.ItemEffect[] effects = [E(91_001, (long)int.MaxValue + 1), E(91_001, (long)int.MinValue - 1),
            E(2867, (long)int.MaxValue + 1), E(2867, 0, 0, (long)int.MaxValue + 1),
            E(AlterationCatalogue.FightsLeft, (long)int.MaxValue + 1)];
        foreach (var effect in effects)
            Assert.Throws<OverflowException>(() => WithEffects(effect).Entry(active, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => WithEffects().Entry(
            new Alterations.Active { Id = 81_001, Kind = (Alterations.Kind)99 }, Now));
    }

    [Fact]
    public async Task Requested_snapshots_use_the_owning_session_source_clock_and_codec_with_empty_lists_preserved()
    {
        var first = Recipient(71_001);
        var second = Recipient(71_002);
        var backend = new Backend();
        backend.Sources[first.Session.CharacterId] = [Active(81_001, Alterations.Kind.Fights, 3)];
        backend.Sources[second.Session.CharacterId] = [];
        var service = new AlterationSnapshotService(new(_ => null), backend);
        using (SessionContext.Push(second.Session))
        {
            await service.ListAsync(first.Session);
            Assert.Same(second.Session, SessionContext.Actual);
        }
        using (SessionContext.Push(first.Session)) await service.ListAsync(second.Session);
        var actual = Assert.Single(Assert.IsType<AlterationsEvent>(Assert.Single(first.Frames).Content).Alterations);
        Assert.Equal(81_001, actual.AlterationId);
        Assert.Equal(3, actual.ExpirationValue);
        Assert.Empty(Assert.IsType<AlterationsEvent>(Assert.Single(second.Frames).Content).Alterations);
        Assert.Equal(new[] { "clock", "of:71001", "clock", "of:71002" }, backend.Calls);
        foreach (var target in new[] { first, second })
        {
            Assert.Equal(1, target.Codec.Encodes);
            Assert.Equal(0, target.Codec.Decodes);
            Assert.Null(Assert.Single(target.Frames).Uid);
        }
    }

    [Fact]
    public async Task Post_fight_snapshot_includes_spent_zero_entries_before_slots_losses_and_caller_characteristics()
    {
        var target = Recipient(71_001);
        var ambient = Recipient(71_002);
        var backend = new Backend();
        var spent = Active(81_001, Alterations.Kind.Fights, 0);
        var remaining = Active(81_002, Alterations.Kind.Victories, 2);
        backend.Listed = [spent, remaining];
        backend.Spent = [spent];
        backend.Expired = [Active(81_003, Alterations.Kind.Timed)];
        backend.OnSlots = session =>
        {
            Assert.Same(target.Session, session);
            var snapshot = Assert.IsType<AlterationsEvent>(Assert.Single(target.Frames).Content);
            Assert.Equal(new long[] { 81_001, 81_002 }, snapshot.Alterations.Select(active => active.AlterationId));
            Assert.Equal(new long[] { 0, 2 }, snapshot.Alterations.Select(active => active.ExpirationValue));
            return Task.CompletedTask;
        };
        var service = new AlterationSnapshotService(new(_ => null), backend);
        using (SessionContext.Push(ambient.Session))
        using (await target.Session.EnterTurnAsync())
        {
            await service.AfterFightAsync(target.Session, won: true).WaitAsync(TimeSpan.FromSeconds(5));
            backend.Calls.Add("characteristics");
            Assert.Same(ambient.Session, SessionContext.Actual);
        }
        Assert.Equal(new[] { "clock", "expire:71001", "count:71001:True", "slots", "lost:81003", "lost:81001", "characteristics" }, backend.Calls);
        Assert.Empty(ambient.Frames);
        Assert.Equal(1, target.Codec.Encodes);
        Assert.Equal(0, target.Codec.Decodes);
        Assert.Equal(0, spent.Left);
    }

    [Fact]
    public async Task Snapshot_transport_failure_stops_post_fight_hooks_and_restores_context_without_retry()
    {
        var failure = new IOException("test snapshot transport failure");
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, _ => Task.FromException(failure));
        session.State.CharacterId = 71_001;
        var backend = new Backend();
        var service = new AlterationSnapshotService(new(_ => null), backend);
        var ambient = Recipient(71_002);
        using (SessionContext.Push(ambient.Session))
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.AfterFightAsync(session, won: false)));
            Assert.Same(ambient.Session, SessionContext.Actual);
        }
        Assert.Equal(new[] { "clock", "expire:71001", "count:71001:False" }, backend.Calls);
        Assert.Equal(1, codec.Encodes);
    }

    [Fact]
    public async Task Slot_backend_failure_is_propagated_after_one_snapshot_and_before_loss_notifications()
    {
        var failure = new InvalidOperationException("test slot backend failure");
        var backend = new Backend { OnSlots = _ => Task.FromException(failure), Spent = [Active(81_001)] };
        var target = Recipient(71_001);
        var service = new AlterationSnapshotService(new(_ => null), backend);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.AfterFightAsync(target.Session, won: true)));
        Assert.Equal(new[] { "clock", "expire:71001", "count:71001:True", "slots" }, backend.Calls);
        Assert.Single(target.Frames);
        Assert.Equal(1, target.Codec.Encodes);
    }

    [Fact]
    public async Task Initial_entry_sends_the_populated_snapshot_once_before_experience_modifiers()
    {
        var target = Recipient(71_001);
        var builder = WithEffects(E(91_001));
        var active = Active(81_001, Alterations.Kind.Victories, 0);
        int snapshots = 0;
        var initial = new GameEntryInitialMessages([], [], target.Session.State, new([], _ => []), 0, 0,
            () => { snapshots++; return builder.Snapshot([active], Now); }, () => new("1970-01-01T00:00:00.000Z", []), () => new([]),
            () => GameEntryInitialMessagesTests.Selection(target.Session.CharacterId),
            () => InventoryMessages.FromCatalogue.Snapshot(target.Session.State), () => new([]), () => new(81_001),
            () => EntryInformationMessages.PreviousConnection(target.Session.State.PreviousVisit), AfterSelectionMessagesTests.Empty,
            InitialPresentationMessagesTests.Empty);
        await initial.SendAsync(target.Session);
        Assert.Equal(1, snapshots);
        var snapshot = Assert.Single(target.Frames.Select(frame => frame.Content).OfType<AlterationsEvent>());
        int index = target.Frames.FindIndex(frame => frame.Content is AlterationsEvent);
        Assert.IsType<ServerExperienceModifierEvent>(target.Frames[index + 1].Content);
        var actual = Assert.Single(snapshot.Alterations);
        Assert.Equal(81_001, actual.AlterationId);
        Assert.Equal(AlterationExpirationType.FightsWonCount, actual.ExpirationType);
        Assert.Equal(0, actual.ExpirationValue);
        Assert.Equal(new ObjectEffect(91_001, new ObjectEffectCaseNone()), Assert.Single(actual.Effects));
    }

    private static Equipment.ItemEffect E(int id, long value = 0, long num = 0, long side = 0) => new(id, value, num, side);
    private static AlterationMessages WithEffects(params Equipment.ItemEffect[] effects)
        => new(id => new AlterationCatalogue.Definition(id, 1, 1, "", effects));
    private static Alterations.Active Active(int id, Alterations.Kind kind = Alterations.Kind.Lasting, int left = 0)
        => new() { Id = id, FromMs = NowMs, Kind = kind, Left = left, Total = 7, UntilMs = NowMs - 1 };

    private static (GameSession Session, List<GameFrame> Frames, GameMessagingTests.CountingCodec Codec) Recipient(long id)
    {
        var frames = new List<GameFrame>();
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, packet => { frames.Add(Wire.Decode(packet)); return Task.CompletedTask; });
        session.State.CharacterId = id;
        return (session, frames, codec);
    }

    private sealed class Backend : IAlterationSnapshotBackend
    {
        internal List<string> Calls { get; } = [];
        internal Dictionary<long, IReadOnlyList<Alterations.Active>> Sources { get; } = [];
        internal IReadOnlyList<Alterations.Active> Listed = [], Spent = [], Expired = [];
        internal Func<GameSession, Task> OnSlots = _ => Task.CompletedTask;
        public DateTime UtcNow { get { Calls.Add("clock"); return Now; } }
        public IReadOnlyList<Alterations.Active> Of(long characterId)
        {
            Calls.Add($"of:{characterId}");
            return Sources[characterId];
        }
        public IReadOnlyList<Alterations.Active> Expire(long characterId, DateTime nowUtc)
        {
            Assert.Equal(Now, nowUtc);
            Calls.Add($"expire:{characterId}");
            return Expired;
        }
        public (IReadOnlyList<Alterations.Active> Listed, IReadOnlyList<Alterations.Active> Spent) CountFight(long characterId, bool won)
        {
            Calls.Add($"count:{characterId}:{won}");
            return (Listed, Spent);
        }
        public Task RefreshSlotsAsync(GameSession session, bool won)
        {
            Assert.Same(session, SessionContext.Actual);
            Calls.Add("slots");
            return OnSlots(session);
        }
        public Task TellLostAsync(GameSession session, Alterations.Active active, DateTime nowUtc)
        {
            Assert.Same(session, SessionContext.Actual);
            Assert.Equal(Now, nowUtc);
            Calls.Add($"lost:{active.Id}");
            return Task.CompletedTask;
        }
    }
}
