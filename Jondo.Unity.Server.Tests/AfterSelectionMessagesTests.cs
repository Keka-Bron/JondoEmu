using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class AfterSelectionMessagesTests
{
    private static readonly GameCodec Wire = new();
    private static readonly ItemEffectMessages Effects = new((_, value, _, _) => (EffectFields.AsNumber, value, 0, 0));
    internal static AfterSelectionMessages Empty { get; } = new(_ => 0, _ => [], _ => 0, Effects);
    private static readonly Type[] Migrated = [typeof(TagStoragesRefreshEvent), typeof(SetUpdateEvent),
        typeof(InventoryWeightEvent), typeof(SpouseStatusEvent), typeof(StorageKamasUpdateEvent),
        typeof(BonusesEvent), typeof(CharacterLoadingCompleteEvent)];

    [Fact]
    public void Unsupported_features_have_explicit_empty_and_unmarried_initial_policies()
    {
        Assert.Empty(AfterSelectionMessages.Tags().TagInformation);
        Assert.False(AfterSelectionMessages.Spouse().HasSpouse);
        Assert.Empty(AfterSelectionMessages.Bonuses().Bonuses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    [InlineData(1)]
    public void Weight_uses_all_owned_items_and_matches_the_inventory_minimum_quantity_policy(int quantity)
    {
        var state = new SessionState { StatStrength = 17, ScrolledStrength = 23 };
        state.EquipmentItems.Add(71_001, Item(71_001, 81_001, 0, 2));
        state.EquipmentItems.Add(71_002, Item(71_002, 81_002, Equipment.Bag, 7));
        state.EquipmentItems.Add(71_003, Item(71_003, 81_003, -1, quantity));
        var messages = new AfterSelectionMessages(id => id == 81_001 ? 3 : id == 81_002 ? 5 : 11,
            _ => throw new Exception("Unexpected sets"), _ => throw new Exception("Unexpected storage"), Effects);
        Assert.Equal(new InventoryWeightEvent(52, 1200), messages.Weight(state));
        state.EquipmentItems.Clear();
        Assert.Equal(new InventoryWeightEvent(0, 1200), messages.Weight(state));
    }

    [Theory]
    [InlineData(int.MaxValue, 2, 0, 0)]
    [InlineData(0, 1, int.MaxValue, 0)]
    [InlineData(0, 1, int.MaxValue, int.MaxValue)]
    [InlineData(0, 1, int.MinValue, int.MinValue)]
    public void Weight_and_capacity_reject_overflow_instead_of_wrapping(int weight, int quantity, int strength, int scrolled)
    {
        var state = new SessionState { StatStrength = strength, ScrolledStrength = scrolled };
        state.EquipmentItems.Add(71_001, Item(71_001, 81_001, Equipment.Bag, quantity));
        Assert.Throws<OverflowException>(() => new AfterSelectionMessages(_ => weight, _ => [], _ => 0, Effects).Weight(state));
    }

    [Fact]
    public void Sets_use_only_worn_templates_in_slot_order_and_detach_identifiers_and_effects()
    {
        var state = new SessionState();
        state.EquipmentItems.Add(71_003, Item(71_003, 81_003, 15));
        state.EquipmentItems.Add(71_001, Item(71_001, 81_001, 0, 99));
        state.EquipmentItems.Add(71_002, Item(71_002, 81_002, 2));
        state.EquipmentItems.Add(71_004, Item(71_004, 81_004, Equipment.Bag));
        state.EquipmentItems.Add(71_005, Item(71_005, 81_005, 16));
        state.EquipmentItems.Add(71_006, Item(71_006, 81_006, -1));
        int[]? seen = null;
        var templates = new List<int> { 81_001, 81_002 };
        var bonuses = new List<(int, long)> { (91_001, -17), (91_002, 0), (-1, 99) };
        var messages = new AfterSelectionMessages(_ => 0, ids =>
        {
            seen = ids.ToArray();
            return [new(51_001, templates, bonuses), new(51_002, [81_003], [])];
        }, _ => 0, Effects);
        var snapshots = messages.Sets(state);
        Assert.Equal(new[] { 81_001, 81_002, 81_003 }, seen);
        templates.Clear();
        bonuses.Clear();
        state.EquipmentItems.Clear();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(51_001, snapshots[0].SetId);
        Assert.Equal(new[] { 81_001, 81_002 }, snapshots[0].ObjectsUid);
        Assert.Equal(new ObjectEffect[] { new(91_001, new ObjectEffectCaseValueInt(-17)),
            new(91_002, new ObjectEffectCaseValueInt(0)) }, snapshots[0].Effects);
        Assert.Equal(51_002, snapshots[1].SetId);
        Assert.Equal(new[] { 81_003 }, snapshots[1].ObjectsUid);
        Assert.Empty(snapshots[1].Effects);
        Assert.Empty(Empty.Sets(state));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(9_000_000_001L)]
    [InlineData(long.MaxValue)]
    public void Storage_reads_the_explicit_account_and_keeps_int64_without_adding_carried_currency(long balance)
    {
        long seen = 0;
        var messages = new AfterSelectionMessages(_ => 0, _ => [], account => { seen = account; return balance; }, Effects);
        Assert.Equal(new StorageKamasUpdateEvent(balance), messages.Storage(8_000_000_001));
        Assert.Equal(8_000_000_001, seen);
    }

    [Fact]
    public async Task Populated_entry_uses_owned_sources_codec_turn_and_explicit_positions()
    {
        var own = new Recipient();
        own.Session.BindAccount(8_000_000_001, 1);
        own.Session.State.StatStrength = 13;
        own.Session.State.EquipmentItems.Add(71_001, Item(71_001, 81_001, 0, 2));
        var wrong = new Recipient();
        wrong.Session.BindAccount(8_000_000_002, 1);
        wrong.Session.State.StatStrength = 999;
        var reads = new List<string>();
        var messages = new AfterSelectionMessages(id => { Assert.Equal(81_001, id); reads.Add("weight"); return 7; },
            templates => { Assert.Equal(new[] { 81_001 }, templates); reads.Add("sets"); return [new(51_001, [81_001], [])]; },
            account => { Assert.Equal(own.Session.AccountId, account); reads.Add("storage"); return 9_000_000_001; }, Effects);
        using var context = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();
        int sent = await Initial(own.Session.State, messages).SendAsync(own.Session)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(own.Packets.Count, sent);
        Assert.Equal(new[] { "sets", "weight", "storage" }, reads);
        Assert.Equal(34, own.Codec.Frames.Count);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Empty(wrong.Packets);
        Assert.Same(wrong.Session, SessionContext.Actual);
        var events = own.Codec.Frames.Select(frame => frame.Content).ToArray();
        Assert.Equal(new InventoryWeightEvent(14, 1065), Assert.Single(events.OfType<InventoryWeightEvent>()));
        Assert.Equal(new StorageKamasUpdateEvent(9_000_000_001), Assert.Single(events.OfType<StorageKamasUpdateEvent>()));
        string[] names = own.Packets.Select(Name).ToArray();
        int inventory = Array.IndexOf(names, nameof(InventoryContentEvent));
        Assert.Equal(new[] { nameof(InventoryContentEvent), nameof(TagStoragesRefreshEvent), nameof(SetUpdateEvent), nameof(JobDescriptionEvent) }, names.Skip(inventory).Take(4));
        int weight = Array.IndexOf(names, nameof(InventoryWeightEvent));
        Assert.Equal(nameof(AlignmentRankUpdateEvent), names[weight - 1]);
        Assert.Equal(nameof(SequenceNumberEvent), names[weight + 1]);
        int storage = Array.IndexOf(names, nameof(StorageKamasUpdateEvent));
        Assert.Equal(nameof(HavenBagRoomUpdateEvent), names[storage - 1]);
        Assert.Equal(new[] { nameof(StorageKamasUpdateEvent), nameof(ConfigurationEvent), nameof(BonusesEvent),
            nameof(CharacterLoadingCompleteEvent), nameof(CharacterAppearanceCollectionEvent) }, names.Skip(storage).Take(5));
    }

    [Fact]
    public async Task Every_set_is_captured_before_the_first_set_send_can_change_the_source()
    {
        var templates = new List<int> { 81_002 };
        var bonuses = new List<(int, long)> { (91_001, 23) };
        var messages = new AfterSelectionMessages(_ => 0,
            _ => [new(51_001, [81_001], []), new(51_002, templates, bonuses)], _ => 0, Effects);
        var own = new Recipient(frame =>
        {
            if (frame.Content is SetUpdateEvent { SetId: 51_001 }) { templates.Clear(); bonuses.Clear(); }
        });
        await Initial(own.Session.State, messages).SendAsync(own.Session);
        var sets = own.Codec.Frames.Select(frame => frame.Content).OfType<SetUpdateEvent>().ToArray();
        Assert.Equal(new[] { 81_002 }, sets[1].ObjectsUid);
        Assert.Equal(new ObjectEffect(91_001, new ObjectEffectCaseValueInt(23)), Assert.Single(sets[1].Effects));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Set_multiplicity_controls_the_number_of_owned_set_snapshots(int setCount)
    {
        var messages = new AfterSelectionMessages(_ => 0, _ => Enumerable.Range(0, setCount)
            .Select(index => new ItemSets.EquippedSet(51_001 + index, [81_001 + index], [(91_001, index)])).ToArray(),
            _ => 9_000_000_001, Effects);
        var own = new Recipient();
        int sent = await Initial(own.Session.State, messages).SendAsync(own.Session);
        Assert.Equal(33 + setCount, sent);
        Assert.Equal(sent, own.Codec.Frames.Count);
        Assert.Equal(sent, own.Packets.Count);
        var sets = own.Codec.Frames.Select(frame => frame.Content).OfType<SetUpdateEvent>().ToArray();
        Assert.Equal(Enumerable.Range(0, setCount).Select(index => 51_001 + index), sets.Select(set => set.SetId));
        Assert.Equal(Enumerable.Range(0, setCount).Select(index => 81_001 + index), sets.SelectMany(set => set.ObjectsUid));
    }

    [Fact]
    public async Task Unrepresentable_later_set_effect_fails_before_sending_any_partial_set_snapshot()
    {
        var messages = new AfterSelectionMessages(_ => 0,
            _ => [new(51_001, [81_001], [(91_001, 1)]), new(51_002, [81_002], [(91_001, (long)int.MaxValue + 1)])],
            _ => 0, Effects);
        var own = new Recipient();
        await Assert.ThrowsAsync<OverflowException>(() => Initial(own.Session.State, messages)
            .SendAsync(own.Session));
        Assert.IsType<TagStoragesRefreshEvent>(own.Codec.Frames.Last().Content);
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is SetUpdateEvent or JobDescriptionEvent);
    }

    [Theory]
    [InlineData("sets")]
    [InlineData("weight")]
    [InlineData("storage")]
    public async Task Source_failure_stops_at_its_boundary_without_retry_or_later_sends(string stage)
    {
        var failure = new IOException("after-selection source failed");
        int reads = 0;
        var own = new Recipient();
        own.Session.State.EquipmentItems.Add(71_001, Item(71_001, 81_001, 0));
        var messages = new AfterSelectionMessages(_ => { if (stage == "weight") { reads++; throw failure; } return 1; },
            _ => { if (stage == "sets") { reads++; throw failure; } return []; },
            _ => { reads++; throw failure; }, Effects);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Initial(own.Session.State, messages)
            .SendAsync(own.Session)));
        Assert.Equal(1, reads);
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is StorageKamasUpdateEvent or BonusesEvent or CharacterLoadingCompleteEvent);
        if (stage == "sets") Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is JobDescriptionEvent);
        if (stage == "weight") Assert.DoesNotContain(own.Packets, packet => Name(packet) == nameof(SequenceNumberEvent));
    }

    public static IEnumerable<object[]> DeliveryFailures => Migrated.SelectMany(type =>
        new[] { new object[] { type, false }, new object[] { type, true } });

    [Theory]
    [MemberData(nameof(DeliveryFailures))]
    public async Task Each_migrated_event_propagates_codec_and_transport_failures_without_retry(Type failedType, bool transport)
    {
        var failure = new IOException("after-selection delivery failed");
        var own = new Recipient(failure: frame => frame.Content.GetType() == failedType && !transport ? failure : null,
            send: packet => Name(packet) == failedType.Name && transport ? Task.FromException(failure) : Task.CompletedTask);
        var messages = new AfterSelectionMessages(_ => 0, _ => [new(51_001, [81_001], [])], _ => 0, Effects);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Initial(own.Session.State, messages)
            .SendAsync(own.Session)));
        Assert.Equal(1, own.Codec.Frames.Count(frame => frame.Content.GetType() == failedType));
        Assert.Equal(failedType, own.Codec.Frames.Last().Content.GetType());
        Assert.Equal(transport ? 1 : 0, own.Packets.Count(packet => Name(packet) == failedType.Name));
        Assert.DoesNotContain(own.Packets, packet => Name(packet) == nameof(CharacterAppearanceCollectionEvent));
        Assert.Equal(0, own.Codec.Decodes);
    }

    internal static GameEntryInitialMessages Initial(SessionState state, AfterSelectionMessages messages)
        => new([], [], state, new([], _ => []), 0, 0, () => new([]), () => new("1970-01-01T00:00:00.000Z", []),
            () => new([]), () => GameEntryInitialMessagesTests.Selection(), () => InventoryMessages.FromCatalogue.Snapshot(state),
            () => new([]), () => new(81_001), () => null, messages, InitialPresentationMessagesTests.Empty);

    private static Equipment.Item Item(long uid, int template, int position, int quantity = 1)
        => new() { Uid = uid, Template = template, Position = position, Quantity = quantity };
    private static string Name(byte[] packet) => GameBindings.StableName(Wire.ReadEnvelope(packet).Alias)!;
    private sealed class Recipient
    {
        internal List<byte[]> Packets { get; } = [];
        internal RecordingCodec Codec { get; }
        internal GameSession Session { get; }
        internal Recipient(Action<GameFrame>? encoded = null, Func<GameFrame, Exception?>? failure = null, Func<byte[], Task>? send = null)
        {
            Codec = new(encoded, failure);
            Session = new(Codec, async packet => { Packets.Add(packet); if (send != null) await send(packet); });
        }
    }
    private sealed class RecordingCodec(Action<GameFrame>? encoded, Func<GameFrame, Exception?>? failure) : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal int Decodes { get; private set; }
        public GameFrame Decode(ReadOnlySpan<byte> bytes) { Decodes++; throw new Exception("Unexpected decode"); }
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (failure?.Invoke(frame) is { } error) throw error;
            encoded?.Invoke(frame);
            return Wire.Encode(frame);
        }
    }
}
