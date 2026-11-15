using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class CharacteristicsMessagesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:34:56Z");
    private static readonly CharacteristicCatalogue Catalogue = CharacteristicCatalogue.Default;

    private static CharacteristicsMessages Service(Backend backend, CharacteristicCatalogue? catalogue = null, TimeProvider? clock = null)
        => new(backend, catalogue ?? Catalogue, clock ?? new Clock(() => Now));

    [Fact]
    public async Task Owned_snapshot_preserves_contributions_derived_totals_cases_widths_and_policy_defaults()
    {
        var own = new Recipient();
        var state = own.Session.State;
        state.CharacterLevel = 154;
        state.Experience = 6_000_000_123;
        state.Kamas = 9_000_000_321;
        state.CharacterRemainingPoints = 47;
        state.StatStrength = 41; state.ScrolledStrength = 17;
        state.StatVitality = 31; state.ScrolledVitality = 19;
        state.StatWisdom = 60; state.ScrolledWisdom = 30;
        state.StatChance = 21; state.ScrolledChance = 9;
        state.StatAgility = 22; state.ScrolledAgility = 8;
        state.StatIntelligence = 12; state.ScrolledIntelligence = 3;
        state.RegenerationStartedUtc = Now.AddMinutes(-2).UtcDateTime;
        var backend = new Backend
        {
            Equipment = { [10] = 23, [12] = 25, [13] = 13, [14] = 15, [15] = 11, [40] = 31, [1] = 2, [23] = 1 },
            Alterations = { [10] = -3, [12] = 15, [13] = 7, [14] = 5, [15] = 4, [40] = 5, [44] = 50, [1] = 1, [23] = -2 },
        };
        var wrong = new Recipient(81_002);
        wrong.Session.State.StatStrength = 999;
        using var context = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();

        await Service(backend).SendAsync(own.Session).WaitAsync(TimeSpan.FromSeconds(5));

        var stats = Sheet(Assert.Single(own.Codec.Frames));
        Assert.Equal((6_700_000_000L, 5_500_000_000L, 6_000_000_123L, 9_000_000_321L),
            (stats.ExperienceNextLevelFloor, stats.ExperienceLevelFloor, stats.Experience, stats.Kamas));
        Assert.Equal(5, stats.CriticalHitWeapon);
        Assert.Equal(0, stats.ExperienceBonusLimit);
        Assert.Empty(stats.SpellModifiers);
        Assert.Empty(stats.GlobalSpellModifiers);
        Assert.Equal(Catalogue.Ids, stats.Characteristics.Select(value => value.CharacteristicId));
        Assert.Equal(new CharacterCharacteristicDetailed(41, 17, -3, 0, 23, 0), Detail(stats, 10));
        Assert.Equal(new CharacterCharacteristicDetailed(31, 19, 0, 0, 0, 0), Detail(stats, 11));
        Assert.Equal(new CharacterCharacteristicDetailed(820, 0, 0, 0, 0, 0), Detail(stats, 0));
        Assert.Equal(new CharacterCharacteristicDetailed(1390, 0, 5, 0, 31, 0), Detail(stats, 40));
        Assert.Equal(new CharacterCharacteristicDetailed(208, 0, 50, 0, 0, 0), Detail(stats, 44));
        Assert.Equal(47, Detail(stats, 3).Base);
        foreach (int id in new[] { 27, 28, 82, 83 }) Assert.Equal(13, Detail(stats, id).Base);
        foreach (int id in new[] { 78, 79 }) Assert.Equal(5, Detail(stats, id).Base);
        Assert.Equal(105, Detail(stats, 48).Base);
        Assert.Equal(10, Detail(stats, 75).Base);
        Assert.Equal(100, Detail(stats, 107).Base);
        Assert.Equal(100, Detail(stats, 120).Base);
        Assert.Equal(-37, Detail(stats, 97).Base);
        Assert.Equal(new CharacterCharacteristicDetailedUsable(7, 0, 3, 0, 0, 0, 0), Usable(stats, 1));
        Assert.Equal(new CharacterCharacteristicDetailedUsable(3, 0, -1, 0, 0, 0, 0), Usable(stats, 23));
        Assert.Equal(8000, Value(stats, 29));
        Assert.Equal(10000, Value(stats, 47));
        Assert.Equal(0, Value(stats, 96));
        Assert.Equal(new AlignmentInformation(7, Alignment.Neutral, new(0, AttackableStatus.NonAttackable, 500, 0), 0, 154), stats.AlignmentInformation);
        Assert.Equal(new[] { own.Session }, backend.Owners.Distinct());
        Assert.All(backend.Characters, id => Assert.Equal(81_001, id));
        Assert.Equal(state.RegenerationStartedUtc, Assert.Single(backend.MissingAt));
        Assert.Equal(new[] { 154, 154 }, backend.Levels);
        Assert.Empty(wrong.Codec.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
        Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
        Assert.Null(own.Codec.Frames[0].Uid);
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Theory]
    [InlineData(2, false, 0, Alignment.Neutral, 0, 500, 0)]
    [InlineData(3, false, 907, Alignment.Angel, 0, 1500, 500)]
    [InlineData(3, true, 907, Alignment.Angel, 2, 1500, 500)]
    [InlineData(4, true, 17500, Alignment.Evil, 10, 20000, 17500)]
    public void Alignment_preserves_side_pvp_displayed_grade_and_thresholds_with_capped_character_level(
        int side, bool pvp, int honor, Alignment expectedSide, int grade, int nextFloor, int floor)
    {
        var own = new Recipient();
        own.Session.State.CharacterLevel = 220;
        var backend = new Backend { CurrentAlignment = new(side, 0, 0, pvp, honor) };
        var stats = Service(backend).Snapshot(own.Session).Stats;
        Assert.Equal(new AlignmentInformation(7, expectedSide,
            new(honor, pvp ? AttackableStatus.PvpEnabledAttackable : AttackableStatus.NonAttackable, nextFloor, floor), grade, 200),
            stats.AlignmentInformation);
        Assert.Equal(1050, Detail(stats, 0).Base);
    }

    [Fact]
    public void Mutation_overrides_the_sheet_without_changing_sources_and_removal_restores_human_contributions()
    {
        var own = new Recipient();
        own.Session.State.StatStrength = 41;
        own.Session.State.ScrolledStrength = 17;
        var backend = new Backend
        {
            Equipment = { [10] = 23, [1] = 2 }, Alterations = { [10] = 5, [1] = 1 },
            Monster = new() { LifePoints = 10000, ActionPoints = 4, MovementPoints = 5,
                Strength = 111, Wisdom = 222, Chance = 333, Agility = 444, Intelligence = 555 },
        };
        var service = Service(backend);
        var mutant = service.Snapshot(own.Session).Stats;
        backend.Monster = null; // A removed mutation or an unavailable grade uses the human sheet.
        var human = service.Snapshot(own.Session).Stats;
        Assert.Equal(new CharacterCharacteristicDetailed(111, 0, 0, 0, 0, 0), Detail(mutant, 10));
        Assert.Equal(0, Detail(mutant, 11).Base);
        Assert.Equal(222, Detail(mutant, 12).Base);
        Assert.Equal(333, Detail(mutant, 13).Base);
        Assert.Equal(444, Detail(mutant, 14).Base);
        Assert.Equal(555, Detail(mutant, 15).Base);
        Assert.Equal(10000, Detail(mutant, 0).Base);
        Assert.Equal(new CharacterCharacteristicDetailedUsable(4, 0, 0, 0, 0, 0, 0), Usable(mutant, 1));
        Assert.Equal(new CharacterCharacteristicDetailedUsable(5, 0, 0, 0, 0, 0, 0), Usable(mutant, 23));
        Assert.Equal(1000, Detail(mutant, 40).Base);
        Assert.Equal(0, Detail(mutant, 44).Base);
        Assert.Equal(0, Detail(mutant, 27).Base);
        Assert.Equal(100, Detail(mutant, 48).Base);
        Assert.Equal(new CharacterCharacteristicDetailed(41, 17, 5, 0, 23, 0), Detail(human, 10));
        Assert.Equal(1430, Detail(human, 40).Base);
        Assert.Equal(3, Usable(human, 1).Equipment);
        Assert.Equal(17, own.Session.State.ScrolledStrength);
        Assert.Equal(23, backend.Equipment[10]);
        Assert.Equal(5, backend.Alterations[10]);
    }

    [Fact]
    public void Catalogue_detaches_definitions_preserves_order_and_rejects_duplicate_ids()
    {
        var rows = new List<CharacteristicDefinition> { new(96, CharacteristicKind.Value), new(0),
            new(1, CharacteristicKind.Usable, 6), new(97), new(107, BaseValue: 100) };
        var catalogue = new CharacteristicCatalogue(rows);
        rows.Clear();
        Assert.Equal(new[] { 96, 0, 1, 97, 107 }, catalogue.Ids);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)catalogue.Ids)[0] = 999);
        Assert.Equal(CharacteristicKind.Value, catalogue.KindOf(96));
        Assert.Equal(CharacteristicKind.Usable, catalogue.KindOf(1));
        Assert.Equal(CharacteristicKind.Detailed, catalogue.KindOf(97));
        Assert.Equal(CharacteristicKind.Detailed, catalogue.KindOf(999));
        Assert.Equal(0, catalogue.BaseValueOf(999));
        Assert.Throws<ArgumentException>(() => new CharacteristicCatalogue([new(96), new(96, CharacteristicKind.Value)]));
        var snapshot = Service(new(), catalogue).Snapshot(new Recipient().Session).Stats;
        Assert.Equal(catalogue.Ids, snapshot.Characteristics.Select(value => value.CharacteristicId));
        Assert.Equal(0, Value(snapshot, 96));
        Assert.Equal(6, Usable(snapshot, 1).Base);
        Assert.Equal(-37, Detail(snapshot, 97).Base);
        Assert.Equal(100, Detail(snapshot, 107).Base);
    }

    [Fact]
    public void Default_sheet_is_complete_without_initialization_and_preserves_order_cases_and_fixed_bases()
    {
        var stats = Service(new()).Snapshot(new Recipient().Session).Stats;
        // Pin the former file's complete sequence, including zero IDs and gaps.
        Assert.Equal(new[] { 0, 1, 3, 5, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
            26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49,
            50, 51, 52, 53, 54, 55, 56, 57, 58, 69, 70, 71, 72, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83,
            84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105,
            106, 107, 108, 109, 110, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133,
            134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 150, 158, 200 },
            stats.Characteristics.Select(value => value.CharacteristicId));
        Assert.Equal(120, stats.Characteristics.Count);
        Assert.Equal(new[] { 1, 23 }, stats.Characteristics.Where(value => value.CharacteristicCase is CharacterCharacteristicCaseUsable)
            .Select(value => value.CharacteristicId));
        Assert.Equal(new[] { 29, 47, 96 }, stats.Characteristics.Where(value => value.CharacteristicCase is CharacterCharacteristicCaseValue)
            .Select(value => value.CharacteristicId));
        var fixedBases = new Dictionary<int, int> { [0] = 55, [26] = 1, [40] = 1000, [48] = 100, [75] = 10, [97] = -37,
            [107] = 100, [120] = 100, [121] = 100, [122] = 100, [123] = 100, [124] = 100, [125] = 100,
            [141] = 100, [142] = 100, [143] = 100, [150] = 100 };
        var detailed = stats.Characteristics.Where(value => value.CharacteristicId is not (1 or 23 or 29 or 47 or 96)).ToArray();
        Assert.Equal(115, detailed.Length);
        Assert.All(detailed, value => Assert.Equal(new CharacterCharacteristicDetailed(
            fixedBases.GetValueOrDefault(value.CharacteristicId), 0, 0, 0, 0, 0), Detail(stats, value.CharacteristicId)));
        Assert.Equal(new CharacterCharacteristicDetailedUsable(6, 0, 0, 0, 0, 0, 0), Usable(stats, 1));
        Assert.Equal(new CharacterCharacteristicDetailedUsable(3, 0, 0, 0, 0, 0, 0), Usable(stats, 23));
        Assert.Equal(8000, Value(stats, 29));
        Assert.Equal(10000, Value(stats, 47));
        Assert.Equal(0, Value(stats, 96));
    }

    [Theory]
    [InlineData(0, 0, 0, "2200")]
    [InlineData(10, 41, 7, "080A220410293807")]
    [InlineData(1, 6, 2, "08012A0408062802")]
    [InlineData(23, 3, 0, "08172A020803")]
    [InlineData(29, 8000, 0, "081D120310C03E")]
    [InlineData(47, 10000, 0, "082F120310904E")]
    [InlineData(96, 0, 0, "08601200")]
    [InlineData(96, 3, 7, "08601202100A")]
    [InlineData(999, 41, 7, "08E707220410293807")]
    public void Tax_collector_entries_use_the_shared_kinds_and_preserve_legacy_bytes(int id, long value, long bonus, string expected)
        => Assert.Equal(Convert.FromHexString(expected), TaxCollectorProtocol.Characteristic(id, value, bonus).Build());

    [Fact]
    public void Snapshot_is_detached_and_next_snapshot_reads_current_owner_state_and_sources()
    {
        var own = new Recipient();
        own.Session.State.StatStrength = 41;
        own.Session.State.ScrolledStrength = 17;
        var backend = new Backend { Equipment = { [10] = 23 }, Alterations = { [10] = 5 } };
        var service = Service(backend);
        var first = service.Snapshot(own.Session);
        own.Session.State.StatStrength = 7;
        own.Session.State.ScrolledStrength = 0;
        backend.Equipment.Clear();
        backend.Alterations.Clear();
        var second = service.Snapshot(own.Session);
        Assert.Equal(new CharacterCharacteristicDetailed(41, 17, 5, 0, 23, 0), Detail(first.Stats, 10));
        Assert.Equal(new CharacterCharacteristicDetailed(7, 0, 0, 0, 0, 0), Detail(second.Stats, 10));
        Assert.NotSame(first.Stats.Characteristics, second.Stats.Characteristics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_life_uses_the_regeneration_start_or_one_explicit_current_instant(bool started)
    {
        var own = new Recipient();
        own.Session.State.RegenerationStartedUtc = started ? Now.AddHours(-1).UtcDateTime : default;
        int reads = 0;
        var backend = new Backend();
        var clock = new Clock(() => { reads++; return Now; });
        var stats = Service(backend, new([new(97)]), clock).Snapshot(own.Session).Stats;
        Assert.Equal(started ? Now.AddHours(-1).UtcDateTime : Now.UtcDateTime, Assert.Single(backend.MissingAt));
        Assert.Equal(started ? 0 : 1, reads);
        Assert.Equal(-37, Detail(stats, 97).Base);
        Assert.Equal(started ? Now.AddHours(-1).UtcDateTime : default, own.Session.State.RegenerationStartedUtc);
    }

    [Theory]
    [InlineData("equipment")]
    [InlineData("alterations")]
    [InlineData("mutation")]
    [InlineData("alignment")]
    [InlineData("quests")]
    [InlineData("energy")]
    [InlineData("life")]
    [InlineData("next-xp")]
    [InlineData("xp")]
    [InlineData("clock")]
    public async Task Source_failures_propagate_before_encoding_or_delivery_and_restore_context(string stage)
    {
        var failure = new IOException("characteristics source failed");
        var backend = new Backend { FailStage = stage, Failure = failure };
        var service = new CharacteristicsMessages(backend, Catalogue, new Clock(() => stage == "clock" ? throw failure : Now));
        var own = new Recipient();
        var wrong = new Recipient(81_002);
        using var context = SessionContext.Push(wrong.Session);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.SendAsync(own.Session)));
        Assert.Empty(own.Codec.Frames);
        Assert.Equal(0, own.Delivered);
        Assert.Empty(wrong.Codec.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Theory]
    [InlineData("detailed")]
    [InlineData("usable")]
    [InlineData("derived")]
    [InlineData("long-total")]
    public async Task Out_of_range_contributions_fail_before_any_send_instead_of_wrapping(string kind)
    {
        var own = new Recipient();
        var backend = new Backend();
        CharacteristicCatalogue catalogue;
        if (kind == "usable")
        {
            catalogue = new([new(1, CharacteristicKind.Usable, 6)]);
            backend.Equipment[1] = int.MaxValue;
            backend.Alterations[1] = 1;
        }
        else if (kind == "detailed")
        {
            catalogue = new([new(10)]);
            backend.Equipment[10] = (long)int.MaxValue + 1;
        }
        else
        {
            catalogue = new([new(40, BaseValue: 1000)]);
            own.Session.State.StatStrength = int.MaxValue;
            if (kind == "long-total") backend.Equipment[10] = long.MaxValue;
        }
        await Assert.ThrowsAsync<OverflowException>(() => Service(backend, catalogue).SendAsync(own.Session));
        Assert.Empty(own.Codec.Frames);
        Assert.Equal(0, own.Delivered);
    }

    [Fact]
    public void Value_case_keeps_int64_totals_while_detailed_contributions_accept_int32_boundaries()
    {
        var own = new Recipient();
        own.Session.State.StatStrength = int.MaxValue;
        var backend = new Backend { Equipment = { [10] = int.MinValue }, Alterations = { [10] = int.MaxValue } };
        var stats = Service(backend, new([new(10), new(40, CharacteristicKind.Value, 1000)])).Snapshot(own.Session).Stats;
        Assert.Equal(new CharacterCharacteristicDetailed(int.MaxValue, 0, int.MaxValue, 0, int.MinValue, 0), Detail(stats, 10));
        Assert.Equal(10_737_419_230L, Value(stats, 40));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codec_and_transport_failures_propagate_once_after_a_complete_snapshot(bool transport)
    {
        var failure = new IOException("characteristics delivery failed");
        var own = new Recipient(failure: _ => transport ? null : failure,
            send: _ => transport ? Task.FromException(failure) : Task.CompletedTask);
        var wrong = new Recipient(81_002);
        var backend = new Backend();
        using var context = SessionContext.Push(wrong.Session);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Service(backend).SendAsync(own.Session)));
        Sheet(Assert.Single(own.Codec.Frames));
        Assert.Equal(transport ? 1 : 0, own.SendAttempts);
        Assert.Equal(0, own.Delivered);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Single(backend.Reads, stage => stage == "next-xp");
        Assert.Empty(wrong.Codec.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Theory]
    [InlineData(GameContext.RolePlay)]
    [InlineData(GameContext.Fight)]
    public async Task Map_regeneration_policy_is_observed_by_the_next_characteristics_snapshot(GameContext context)
    {
        var own = new Recipient();
        own.Session.State.StatStrength = 41;
        own.Session.State.RegenerationStartedUtc = Now.AddHours(-1).UtcDateTime;
        var backend = new Backend { MissingValue = at => at == Now.UtcDateTime ? 3 : 37 };
        var characteristics = Service(backend);
        var wrong = new Recipient(81_002);
        using var scope = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();

        var initial = characteristics.Snapshot(own.Session).Stats;
        own.Session.State.StatStrength = 73;
        var map = new GameEntryMapMessages(new Clock(() => Now), () => 5,
            GameEntryMapMessagesTests.EmptySpells, GameEntryMapMessagesTests.EmptyBars);
        await map.SendAsync(own.Session, MapEntryOpeningMessagesTests.Empty,
            context, 9_000_000_001).WaitAsync(TimeSpan.FromSeconds(5));
        var current = characteristics.Snapshot(own.Session).Stats;

        Assert.Equal(41, Detail(initial, 10).Base);
        Assert.Equal(73, Detail(current, 10).Base);
        Assert.Equal(-37, Detail(initial, 97).Base);
        Assert.Equal(context == GameContext.RolePlay ? -3 : -37, Detail(current, 97).Base);
        Assert.Equal(new[] { Now.AddHours(-1).UtcDateTime,
            context == GameContext.RolePlay ? Now.UtcDateTime : Now.AddHours(-1).UtcDateTime }, backend.MissingAt);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
        Assert.Empty(wrong.Codec.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    private static CharacterCharacteristics Sheet(GameFrame frame) => Assert.IsType<CharacterCharacteristicsEvent>(frame.Content).Stats;
    private static CharacterCharacteristicCase Case(CharacterCharacteristics stats, int id)
        => Assert.Single(stats.Characteristics, value => value.CharacteristicId == id).CharacteristicCase;
    private static CharacterCharacteristicDetailed Detail(CharacterCharacteristics stats, int id)
        => Assert.IsType<CharacterCharacteristicCaseDetailed>(Case(stats, id)).Value;
    private static CharacterCharacteristicDetailedUsable Usable(CharacterCharacteristics stats, int id)
        => Assert.IsType<CharacterCharacteristicCaseUsable>(Case(stats, id)).Value;
    private static long Value(CharacterCharacteristics stats, int id)
        => Assert.IsType<CharacterCharacteristicCaseValue>(Case(stats, id)).Value.Total;

    private sealed class Backend : ICharacteristicsMessagesBackend
    {
        internal Dictionary<int, long> Equipment = [], Alterations = [];
        internal MonsterGradeStats? Monster;
        internal Alignments.Alignment CurrentAlignment = new(Alignments.Neutral, 0, 0);
        internal List<GameSession> Owners = [];
        internal List<long> Characters = [];
        internal List<int> Levels = [];
        internal List<DateTime> MissingAt = [];
        internal List<string> Reads = [];
        internal Func<DateTime, int>? MissingValue;
        internal string? FailStage;
        internal Exception? Failure;
        private void Read(string stage)
        {
            Reads.Add(stage);
            if (stage == FailStage) throw Failure!;
        }
        private void Owner(GameSession session) { Owners.Add(session); Assert.Same(session, SessionContext.Actual); }
        public long LevelFloor(int level) { Read("xp"); Levels.Add(level); return 5_500_000_000; }
        public long NextLevelFloor(int level) { Read("next-xp"); Levels.Add(level); return 6_700_000_000; }
        public IReadOnlyDictionary<int, long> EquipmentBonuses(GameSession session) { Read("equipment"); Owner(session); return Equipment; }
        public IReadOnlyDictionary<int, long> AlterationBonuses(long characterId) { Read("alterations"); Characters.Add(characterId); return Alterations; }
        public MonsterGradeStats? MutationSheet(GameSession session) { Read("mutation"); Owner(session); return Monster; }
        public Alignments.Alignment Alignment(long characterId) { Read("alignment"); Characters.Add(characterId); return CurrentAlignment; }
        public int AlignmentQuestNumber(GameSession session) { Read("quests"); Owner(session); return 7; }
        public int Energy(long characterId) { Read("energy"); Characters.Add(characterId); return 8000; }
        public int MissingLife(long characterId, DateTime atUtc) { Read("life"); Characters.Add(characterId); MissingAt.Add(atUtc); return MissingValue?.Invoke(atUtc) ?? 37; }
    }

    private sealed class Clock(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }

    private sealed class Recipient
    {
        internal RecordingCodec Codec { get; }
        internal GameSession Session { get; }
        internal int SendAttempts, Delivered;
        internal Recipient(long character = 81_001, Func<GameFrame, Exception?>? failure = null, Func<GameFrame, Task>? send = null)
        {
            Codec = new(failure);
            Session = new(Codec, async _ =>
            {
                SendAttempts++;
                if (send != null) await send(Codec.Frames[^1]);
                Delivered++;
            });
            Session.BindAccount(character + 10_000, 1);
            Session.State.CharacterId = character;
        }
    }

    private sealed class RecordingCodec(Func<GameFrame, Exception?>? failure) : IGameCodec
    {
        internal List<GameFrame> Frames = [];
        internal int Decodes;
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (failure?.Invoke(frame) is { } error) throw error;
            return []; // An opaque token, deliberately not a protobuf envelope.
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Decodes++;
            throw new InvalidOperationException("Unexpected decode in semantic characteristics delivery");
        }
    }
}
