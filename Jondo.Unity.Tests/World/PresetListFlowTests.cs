using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("forgemagic")]
public sealed class PresetListFlowTests : IDisposable
{
    private const long Account = 8_976_000_001, Map = 191105026;
    // Keep live pipes off the shared map so parallel tests cannot broadcast to them.
    private const long TestMap = 8_976_100_001;
    private readonly long _owner, _other;

    public PresetListFlowTests()
    {
        _owner = CreateCharacter("Preset-Owner");
        _other = CreateCharacter("Preset-Other");
    }

    public void Dispose()
    {
        foreach (long id in new[] { _owner, _other })
        {
            CharacterPresets.Forget(id);
            DatabaseManager.DeleteCharacter(id, Account);
        }
    }

    [Fact]
    public async Task Live_list_uses_owner_codec_saved_order_and_bag_with_detached_repeat_snapshots()
    {
        var first = Save(_owner, "First", "original-date-text");
        var ownOutfit = Assert.Single(Outfits.Of(_owner));
        var foreignOutfit = Assert.Single(Outfits.Of(_other));
        first.Outfit = ownOutfit.Uuid;
        first.Items[0] = int.MaxValue;
        first.Items[1] = 91_999; // missing from this owner's bag
        first.Items[Equipment.Bag] = int.MaxValue; // not a preset equipment position
        first.Points[Stat.Strength] = 50;
        first.Spells[0] = [0, 18];
        first.Spells[91_001] = [];
        Assert.True(CharacterPresets.Replace(_owner, first));
        var second = Save(_owner, "Second", "second-date");
        second.Outfit = foreignOutfit.Uuid;
        Assert.True(CharacterPresets.Replace(_owner, second));
        Save(_other, "Other character", "other-date");
        CharacterPresets.ForEdit(_owner, first.Uuid)!.Name = "Unsaved draft";

        var codec = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(Account, _owner, TestMap, codec: codec);
        var item = new Equipment.Item { Uid = int.MaxValue, Template = 70, Position = Equipment.Bag };
        item.Effects.Add(new(118, 30, 0, 0));
        pipe.Session.State.EquipmentItems[item.Uid] = item;
        pipe.Session.State.ScrolledStrength = 100;
        var wrongCodec = new RecordingCodec();
        var wrong = new GameSession(wrongCodec, _ => throw new InvalidOperationException("Wrong preset recipient."));
        wrong.State.CharacterId = _other;
        wrong.State.ScrolledStrength = 999;
        wrong.State.EquipmentItems[item.Uid] = new() { Uid = item.Uid, Template = 999 };
        using var context = SessionContext.Push(wrong);
        using var turn = await pipe.Session.EnterTurnAsync();
        await PresetHandler.SendListAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x76, 1 }, await Packet(pipe));
        var snapshot = Assert.IsType<PresetListEvent>(Assert.Single(codec.Frames).Content);
        Assert.Equal(new[] { first.Uuid, second.Uuid }, snapshot.CharacterPresetInfoById.Keys);
        var entry = snapshot.CharacterPresetInfoById[first.Uuid];
        Assert.Equal("First", entry.Name);
        Assert.Equal("original-date-text", entry.LastModificationDate);
        Assert.Equal(ownOutfit.Uuid, entry.OutfitUuid);
        Assert.Equal("", snapshot.CharacterPresetInfoById[second.Uuid].OutfitUuid);
        Assert.Equal(first.Uuid, entry.Uuid);
        Assert.Equal(17, entry.SymbolId);
        Assert.Equal(0, Assert.Single(entry.EquipmentByPosition).Key);
        Assert.Equal(70, entry.EquipmentByPosition[0].Gid);
        Assert.Equal(int.MaxValue, entry.EquipmentByPosition[0].Uid);
        Assert.Equal(new ObjectEffect(118, new ObjectEffectCaseValueInt(30)), Assert.Single(entry.EquipmentByPosition[0].Effects));
        Assert.Equal(new PresetCharacteristicsDetails(100, 50, 0, 0, null, 30), entry.CharacteristicsInfo!.CharacteristicDetailsById[Stat.Strength]);
        Assert.Equal(900, entry.CharacteristicsInfo.CharacteristicDetailsById[Stat.Pods].FromBonus);
        Assert.Equal(0, entry.PresetLook!.LookWithoutOutfit!.LookType);
        Assert.Equal(3, entry.PresetLook.LookWithOutfit!.LookType);
        Assert.Equal(new[] { 0, 18 }, entry.SpellPreset!.SpellPositionsById[0].Positions);
        Assert.Empty(entry.SpellPreset.SpellPositionsById[91_001].Positions);
        Assert.Empty(snapshot.ForgettableSpellPresetInfoById);

        item.Effects.Clear();
        item.Template = 999;
        pipe.Session.State.ScrolledStrength = 7;
        Assert.True(CharacterPresets.Delete(_owner, first.Uuid));
        await PresetHandler.SendListAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x76, 2 }, await Packet(pipe));
        Assert.Equal(second.Uuid, Assert.Single(Assert.IsType<PresetListEvent>(codec.Frames[1].Content).CharacterPresetInfoById).Key);
        Assert.Equal(2, snapshot.CharacterPresetInfoById.Count);
        Assert.Single(entry.EquipmentByPosition[0].Effects);
        Assert.Equal(70, entry.EquipmentByPosition[0].Gid);
        Assert.Equal(100, entry.CharacteristicsInfo.CharacteristicDetailsById[Stat.Strength].FromInitialBonus);
        Assert.Empty(wrongCodec.Frames);
        Assert.Same(wrong, SessionContext.Actual);
        Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
        Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_framed_lists_deliver_empty_replacements_and_populated_owner_data(bool populated)
    {
        var preset = populated ? Save(_owner, "Framed", "2026-08-09T18:20:27.527350937Z") : null;
        await using var pipe = await ClientPipe.OpenAsync(Account, _owner, TestMap);
        if (preset != null)
        {
            preset.Items[0] = 0;
            preset.Spells[0] = [0, 18];
            Assert.True(CharacterPresets.Replace(_owner, preset));
            pipe.Session.State.EquipmentItems[0] = new() { Uid = 0, Template = 70, Position = Equipment.Bag };
        }
        var wrong = GameSession.SinSocket();
        wrong.State.CharacterId = _other;
        using var context = SessionContext.Push(wrong);
        await PresetHandler.SendListAsync(pipe.Session);
        var codec = new GameCodec();
        byte[] packet = await Packet(pipe);
        Assert.Equal("ihb", codec.ReadEnvelope(packet).Alias);
        var frame = codec.Decode(packet);
        Assert.Null(frame.Uid);
        var list = Assert.IsType<PresetListEvent>(frame.Content);
        Assert.Empty(list.ForgettableSpellPresetInfoById);
        if (preset == null) Assert.Empty(list.CharacterPresetInfoById);
        else
        {
            var entry = Assert.Single(list.CharacterPresetInfoById);
            Assert.Equal(preset.Uuid, entry.Key);
            Assert.Equal("Framed", entry.Value.Name);
            Assert.Equal(preset.Created, entry.Value.LastModificationDate);
            Assert.Equal(0, entry.Value.EquipmentByPosition[0].Uid);
            Assert.Equal(70, entry.Value.EquipmentByPosition[0].Gid);
            Assert.Equal(new[] { 0, 18 }, entry.Value.SpellPreset!.SpellPositionsById[0].Positions);
            Assert.All(entry.Value.CharacteristicsInfo!.CharacteristicDetailsById.Values, value => Assert.Null(value.Limit));
        }
        Assert.Equal(populated, CharacterPresets.Delete(_owner, preset?.Uuid ?? "missing"));
        await PresetHandler.SendListAsync(pipe.Session);
        Assert.Empty(Assert.IsType<PresetListEvent>(codec.Decode(await Packet(pipe)).Content).CharacterPresetInfoById);
        Assert.Same(wrong, SessionContext.Actual);
    }

    [Fact]
    public async Task Missing_character_omits_delivery_even_with_a_valid_ambient_character()
    {
        var codec = new RecordingCodec();
        var missing = new GameSession(codec, _ => throw new InvalidOperationException("Missing character must not send."));
        var ambient = GameSession.SinSocket();
        ambient.State.CharacterId = _owner;
        using var context = SessionContext.Push(ambient);
        await PresetHandler.SendListAsync(missing);
        Assert.Empty(codec.Frames);
        Assert.Same(ambient, SessionContext.Actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codec_and_transport_failures_propagate_without_reacquiring_the_owners_turn(bool codecFailure)
    {
        var failure = new IOException("preset delivery failed");
        var codec = new RecordingCodec { Failure = codecFailure ? failure : null };
        int sends = 0;
        var owner = new GameSession(codec, _ => { sends++; throw failure; });
        owner.State.CharacterId = _owner;
        var ambient = GameSession.SinSocket();
        using var context = SessionContext.Push(ambient);
        using var turn = await owner.EnterTurnAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => PresetHandler.SendListAsync(owner).WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Single(codec.Frames);
        Assert.Equal(codecFailure ? 0 : 1, sends);
        Assert.Equal(0, owner.UnoCadaVez.CurrentCount);
        Assert.Same(ambient, SessionContext.Actual);
    }

    [Fact]
    public async Task Invalid_item_uid_fails_before_encoding_and_preserves_saved_state()
    {
        var preset = Save(_owner, "Overflow", "saved-date");
        long uid = (long)int.MaxValue + 1;
        preset.Items[0] = uid;
        Assert.True(CharacterPresets.Replace(_owner, preset));
        var codec = new RecordingCodec();
        var owner = new GameSession(codec, _ => throw new InvalidOperationException("Invalid snapshot must not send."));
        owner.State.CharacterId = _owner;
        owner.State.EquipmentItems[uid] = new() { Uid = uid, Template = 70 };
        var ambient = GameSession.SinSocket();
        using var context = SessionContext.Push(ambient);
        await Assert.ThrowsAsync<OverflowException>(() => PresetHandler.SendListAsync(owner));
        Assert.Empty(codec.Frames);
        Assert.Equal(uid, CharacterPresets.Find(_owner, preset.Uuid)!.Items[0]);
        Assert.Same(ambient, SessionContext.Actual);
    }

    private static long CreateCharacter(string name)
    {
        long id = DatabaseManager.CreateCharacter(Account, 1, name, 8, 0, 1, [-1, -1, -1, -1, -1], Map, 200, 0, 0, []);
        Assert.True(id > 0);
        return id;
    }
    private static CharacterPresets.Preset Save(long characterId, string name, string date)
    {
        var preset = CharacterPresets.Create(characterId, new() { Name = name, Icon = 17 });
        Assert.NotNull(preset);
        preset.Created = date;
        Assert.True(CharacterPresets.Replace(characterId, preset));
        return preset;
    }
    private static async Task<byte[]> Packet(ClientPipe pipe)
        => (await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)))!;
    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal IOException? Failure;
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); if (Failure != null) throw Failure; return [0x76, checked((byte)Frames.Count)]; }
        public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("Preset lists must not reparse packets."); }
    }
}
