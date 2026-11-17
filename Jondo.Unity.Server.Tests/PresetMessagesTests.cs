using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class PresetMessagesTests
{
    [Fact]
    public void Snapshot_uses_owner_bag_filters_missing_items_and_detaches_maps_slots_effects_and_looks()
    {
        var backend = new Backend();
        var owner = GameSession.SinSocket();
        owner.State.CharacterId = 71_001;
        owner.State.ScrolledStrength = 100;
        owner.State.ScrolledWisdom = 70;
        var item = new Equipment.Item { Uid = int.MaxValue, Template = 70, Position = Equipment.Bag };
        item.Effects.AddRange([new(118, 0, 0, 0), new(988, 0, 0, 0, "Ada"), new(-1, 9, 0, 0)]);
        owner.State.EquipmentItems[item.Uid] = item;
        var preset = new CharacterPresets.Preset
        {
            Uuid = "z-first", Name = "Saved", Icon = 17, Created = "original-date-text", Outfit = "owned-outfit",
            Items = new() { [0] = item.Uid, [1] = 91_999, [Equipment.Bag] = item.Uid },
            Points = new() { [Stat.Strength] = 50 }, Spells = new() { [0] = [0, 18], [91_001] = [] },
        };
        var rows = new List<CharacterPresets.Preset> { preset, new() { Uuid = "a-second", Outfit = "foreign-outfit" } };
        var ambient = GameSession.SinSocket();
        ambient.State.ScrolledStrength = 999;
        using var context = SessionContext.Push(ambient);
        var snapshot = new PresetMessages(backend, Effects()).Snapshot(owner, new() { Id = 71_001, Level = 137 }, rows);
        Assert.Same(ambient, SessionContext.Actual);
        Assert.Equal(new[] { "z-first", "a-second" }, snapshot.CharacterPresetInfoById.Keys);
        var entry = snapshot.CharacterPresetInfoById["z-first"];
        Assert.Equal("original-date-text", entry.LastModificationDate);
        Assert.Equal("owned-outfit", entry.OutfitUuid);
        Assert.Equal("", snapshot.CharacterPresetInfoById["a-second"].OutfitUuid);
        Assert.False(entry.IsFavorite);
        Assert.Equal(new PresetCharacteristicsDetails(100, 50, 0, 0, null, 30), entry.CharacteristicsInfo!.CharacteristicDetailsById[Stat.Strength]);
        Assert.Equal(900, entry.CharacteristicsInfo.CharacteristicDetailsById[Stat.Pods].FromBonus);
        Assert.Equal(9137, entry.CharacteristicsInfo.CharacteristicDetailsById[Stat.Pods].FromBase);
        Assert.Equal(7, entry.CharacteristicsInfo.CharacteristicDetailsById[Stat.Pods].FromEquipment);
        Assert.Equal(0, entry.CharacteristicsInfo.CharacteristicDetailsById.First().Key);
        Assert.All(entry.CharacteristicsInfo.CharacteristicDetailsById.Values, value => Assert.Null(value.Limit));
        var equipped = Assert.Single(entry.EquipmentByPosition);
        Assert.Equal(0, equipped.Key);
        Assert.Equal(70, equipped.Value.Gid);
        Assert.Equal(int.MaxValue, equipped.Value.Uid);
        Assert.Equal(new ObjectEffect[] { new(118, new ObjectEffectCaseValueInt(0)), new(988, new ObjectEffectCaseValueString("Ada")) }, equipped.Value.Effects);
        Assert.Equal(new[] { 0, 18 }, entry.SpellPreset!.SpellPositionsById[0].Positions);
        Assert.Empty(entry.SpellPreset.SpellPositionsById[91_001].Positions);
        Assert.Empty(snapshot.ForgettableSpellPresetInfoById);

        rows.Clear();
        preset.Name = "Changed";
        preset.Items.Clear();
        preset.Points.Clear();
        preset.Spells[0].Clear();
        item.Effects.Clear();
        item.Template = 999;
        owner.State.ScrolledStrength = 999;
        backend.Colors.Clear();
        Assert.Equal("Saved", entry.Name);
        Assert.Equal(70, equipped.Value.Gid);
        Assert.Equal(2, equipped.Value.Effects.Count);
        Assert.Equal(new[] { 0, 18 }, entry.SpellPreset.SpellPositionsById[0].Positions);
        Assert.Equal(100, entry.CharacteristicsInfo.CharacteristicDetailsById[Stat.Strength].FromInitialBonus);
        Assert.Equal(new[] { 0x01112233 }, entry.PresetLook!.LookWithoutOutfit!.Colors);
    }

    [Theory]
    [InlineData((long)int.MaxValue + 1, false)]
    [InlineData(1, true)]
    public void Out_of_range_item_uids_or_characteristic_columns_fail_before_delivery(long uid, bool columnOverflow)
    {
        var owner = GameSession.SinSocket();
        owner.State.EquipmentItems[uid] = new() { Uid = uid, Template = 70 };
        var backend = new Backend { Overflow = columnOverflow };
        Assert.Throws<OverflowException>(() => new PresetMessages(backend, Effects()).Snapshot(owner, new() { Level = 137 },
            [new() { Uuid = "key", Items = new() { [0] = uid } }]));
    }

    [Fact]
    public void Empty_list_is_a_replacement_without_querying_nested_sources()
    {
        var backend = new Backend { Fail = true };
        var snapshot = new PresetMessages(backend, Effects()).Snapshot(GameSession.SinSocket(), new(), []);
        Assert.Empty(snapshot.CharacterPresetInfoById);
        Assert.Empty(snapshot.ForgettableSpellPresetInfoById);
    }

    [Fact]
    public void Nested_source_failure_propagates_and_restores_ambient_owner()
    {
        var ambient = GameSession.SinSocket();
        using var context = SessionContext.Push(ambient);
        Assert.Throws<IOException>(() => new PresetMessages(new Backend { Fail = true }, Effects())
            .Snapshot(GameSession.SinSocket(), new(), [new() { Uuid = "key" }]));
        Assert.Same(ambient, SessionContext.Actual);
    }

    private static ItemEffectMessages Effects() => new((_, value, _, _) => (EffectFields.AsNumber, value, 0, 0));

    private sealed class Backend : IPresetMessagesBackend
    {
        internal bool Overflow, Fail;
        internal readonly List<int> Colors = [0x01112233];
        public PresetLook Looks(DbCharacter character, IReadOnlyList<(int Position, Equipment.Item Item)> placed)
        {
            Assert.Equal(character.Id, SessionContext.Current.CharacterId);
            if (Fail) throw new IOException("preset source failed");
            return new(new(Colors.ToArray(), 0, 1, [150], placed.Select(row => row.Item.Template).ToArray(), []), new([], 3, 639, [], [], []));
        }
        public IReadOnlyDictionary<int, long> Bonuses(IReadOnlyList<(int Position, Equipment.Item Item)> placed)
            => new Dictionary<int, long> { [Stat.Strength] = Overflow ? (long)int.MaxValue + 1 : 30, [Stat.Pods] = 7 };
        public long BaseValue(int id, int level, GameSession owner)
        {
            Assert.Same(owner, SessionContext.Actual);
            return id == Stat.Pods ? 9000 + level : 0;
        }
        public bool HasOutfit(long characterId, string uuid) => uuid == "owned-outfit";
    }
}
