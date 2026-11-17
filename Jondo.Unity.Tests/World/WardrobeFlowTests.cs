using Jondo.Unity.Protocol;
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
public sealed class WardrobeFlowTests : IDisposable
{
    private const long Account = 8_977_000_001, OtherAccount = Account + 1, Map = 990_977_001;
    private readonly long _owner, _other;

    public WardrobeFlowTests()
    {
        _owner = Create(Account, "Wardrobe-Owner");
        _other = Create(OtherAccount, "Wardrobe-Other");
        CosmeticCollection.Forget(Account);
        CosmeticCollection.Forget(OtherAccount);
        AppearanceUnlocks.Forget(Account);
        AppearanceUnlocks.Forget(OtherAccount);
    }

    public void Dispose()
    {
        CosmeticCollection.Forget(Account);
        CosmeticCollection.Forget(OtherAccount);
        AppearanceUnlocks.Forget(Account);
        AppearanceUnlocks.Forget(OtherAccount);
        DatabaseManager.DeleteCharacter(_owner, Account);
        DatabaseManager.DeleteCharacter(_other, OtherAccount);
    }

    [Fact]
    public async Task Live_entry_uses_explicit_owner_and_recipient_codecs_and_detached_repeat_snapshots()
    {
        var character = DatabaseManager.GetCharacterById(_owner)!;
        Wardrobe.SaveTitle(_owner, 77);
        Wardrobe.SaveOrnament(_owner, 135);
        var saved = Outfits.Create(_owner, "Saved", 17, Outfits.Worn(character) with { Head = 617, Body = 110, Idle = 24 });
        Assert.NotNull(saved);
        AppearanceUnlocks.Add(Account, AppearanceUnlocks.Kind.Head, [616, 617]);
        AppearanceUnlocks.Add(OtherAccount, AppearanceUnlocks.Kind.Head, [999]);
        Assert.NotNull(CosmeticCollection.Add(Account, 9256, "[[118,7,0,0]]"));
        Assert.NotNull(CosmeticCollection.Add(OtherAccount, 9257, ""));
        var codec = new RecordingCodec();
        var otherCodec = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(Account, _owner, Map, codec: codec);
        await using var other = await ClientPipe.OpenAsync(OtherAccount, _other, Map, codec: otherCodec);
        using var context = SessionContext.Push(other.Session);
        using var turn = await pipe.Session.EnterTurnAsync();
        await WardrobeHandler.SendOwnedAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 1; i <= 8; i++) Assert.Equal(new byte[] { 0x78, (byte)i }, await Packet(pipe));
        Assert.Equal(new byte[] { 0x78, 1 }, await Packet(other));
        var collection = Assert.IsType<CosmeticInventoryLoadedEvent>(codec.Frames[1].Content);
        Assert.Equal(new[] { 616, 617 }, collection.Heads);
        Assert.Equal(9256, Assert.Single(collection.Objects).Gid);
        var list = Assert.IsType<OutfitsLoadedEvent>(codec.Frames[2].Content);
        Assert.Equal(new[] { ConnectionProtocol.OutfitIdOf(_owner), saved.Uuid }, list.Outfits.Select(row => row.Uuid));
        Assert.Equal(character.HeadId, list.Current!.HeadId);
        Assert.Equal(617, list.Outfits[1].HeadId);
        Assert.Equal(77, list.Current.TitleId);
        Assert.Equal(135, list.Current.OrnamentId);
        Assert.IsType<TitleSelectedEvent>(codec.Frames[4].Content);
        Assert.IsType<OrnamentSelectedEvent>(codec.Frames[5].Content);
        Assert.IsType<GameRolePlayShowActorsEvent>(Assert.Single(otherCodec.Frames).Content);
        Assert.Equal(ConnectionProtocol.LookIdOf(_owner), Assert.IsType<OutfitLookUpdatedEvent>(codec.Frames[7].Content).OutfitId);
        Assert.All(codec.Frames.Concat(otherCodec.Frames), frame => Assert.Null(frame.Uid));
        Assert.Same(other.Session, SessionContext.Actual);
        Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);

        CosmeticCollection.Forget(Account);
        AppearanceUnlocks.Forget(Account);
        Assert.True(Outfits.Delete(_owner, saved.Uuid));
        pipe.Session.State.FightId = 97;
        pipe.Session.State.IsInFight = true;
        await WardrobeHandler.SendOwnedAsync(pipe.Session);
        for (int i = 9; i <= 16; i++) Assert.Equal(new byte[] { 0x78, (byte)i }, await Packet(pipe));
        Assert.Empty(Assert.IsType<CosmeticInventoryLoadedEvent>(codec.Frames[9].Content).Objects);
        Assert.Empty(Assert.IsType<CosmeticInventoryLoadedEvent>(codec.Frames[9].Content).Heads);
        Assert.Single(Assert.IsType<OutfitsLoadedEvent>(codec.Frames[10].Content).Outfits);
        Assert.Single(otherCodec.Frames); // A fighting source does not refresh the roleplay map.
        Assert.Equal(2, list.Outfits.Count);
        Assert.Single(collection.Objects);
    }

    [Fact]
    public async Task Real_codec_frames_preserve_entry_order_empty_cosmetics_and_head_policy_in_windows()
    {
        var codec = new GameCodec();
        await using var pipe = await ClientPipe.OpenAsync(Account, _owner, Map);
        var ambient = GameSession.SinSocket();
        ambient.State.CharacterId = _other;
        using var context = SessionContext.Push(ambient);
        await WardrobeHandler.SendOwnedAsync(pipe.Session);
        var frames = new List<GameFrame>();
        var aliases = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            var packet = await Packet(pipe);
            frames.Add(codec.Decode(packet));
            aliases.Add(codec.ReadEnvelope(packet).Alias);
        }
        Assert.Equal(new[] { "hhy", "lwt", "lyt", "lym", "hid", "hif", "jsn", "lxc" }, aliases);
        Assert.All(frames, frame => Assert.Null(frame.Uid));
        Assert.Empty(Assert.IsType<CosmeticInventoryLoadedEvent>(frames[1].Content).Objects);
        var outfit = Assert.Single(Assert.IsType<OutfitsLoadedEvent>(frames[2].Content).Outfits);
        Assert.Equal(1, outfit.HeadId);
        Assert.NotNull(outfit.Colors);
        Assert.NotNull(outfit.EntityLook);
        // Shared legacy window/action adapters use the same corrected semantic outfit.
        using (SessionContext.Push(pipe.Session))
        {
            var character = DatabaseManager.GetCharacterById(_owner)!;
            var stored = new Outfits.Outfit("distinct", 99, "Saved", -1, false, DateTime.UtcNow,
                Outfits.Worn(character) with { Head = 617 });
            var field = ProtoMessage.Parse(ConnectionProtocol.BuildOutfit(character, stored)).Fields.Single(f => f.FieldNumber == 11);
            Assert.Equal(617L, field.VarIntValue);
        }
        Assert.Same(ambient, SessionContext.Actual);
        await CosmeticCollectionHandler.SendAsync(pipe.Session);
        Assert.Empty(Assert.IsType<CosmeticInventoryLoadedEvent>(codec.Decode(await Packet(pipe)).Content).Heads);
    }

    [Fact]
    public async Task Save_persists_explicit_owner_before_notification_failure_and_stops_before_acknowledgement()
    {
        var failure = new IOException("title notification");
        var codec = new RecordingCodec { Failure = failure };
        var owner = new GameSession(codec, _ => throw new InvalidOperationException("Encoding failed first."));
        owner.State.CharacterId = _owner;
        owner.State.IsWardrobeDraftLoaded = true;
        owner.State.WardrobeDraftTitle = 77;
        owner.State.WardrobeDraftOrnament = 135;
        var ambient = GameSession.SinSocket();
        ambient.State.CharacterId = _other;
        using var context = SessionContext.Push(ambient);
        using var turn = await owner.EnterTurnAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WardrobeHandler.SaveAsync(owner,
            ConnectionProtocol.Push(Op.Lxs, []))));
        Assert.IsType<TitleSelectedEvent>(Assert.Single(codec.Frames).Content);
        Assert.Equal((77, 135), Wardrobe.Of(_owner));
        Assert.Equal((0, 0), Wardrobe.Of(_other));
        Assert.Same(ambient, SessionContext.Actual);
        Assert.Equal(0, owner.UnoCadaVez.CurrentCount);
    }

    [Fact]
    public async Task Save_orders_typed_notifications_before_the_correlated_legacy_acknowledgement()
    {
        await using var pipe = await ClientPipe.OpenAsync(Account, _owner, Map);
        pipe.Session.State.IsWardrobeDraftLoaded = true;
        pipe.Session.State.WardrobeDraftTitle = 77;
        pipe.Session.State.WardrobeDraftOrnament = 135;
        var ambient = GameSession.SinSocket();
        ambient.State.CharacterId = _other;
        using var context = SessionContext.Push(ambient);
        using var turn = await pipe.Session.EnterTurnAsync();
        var request = Pb.New().Msg(2, Pb.New().Msg(1, Pb.New()
            .Str(1, "type.ankama.com/" + Op.Lxs).Bytes(2, [])).Var(2, -71)).Build();
        await WardrobeHandler.SaveAsync(pipe.Session, request).WaitAsync(TimeSpan.FromSeconds(5));
        var codec = new GameCodec();
        foreach (string alias in new[] { "hid", "hif", "jsn", "lxc" })
        {
            var packet = await Packet(pipe);
            Assert.Equal(alias, codec.ReadEnvelope(packet).Alias);
            Assert.Null(codec.Decode(packet).Uid);
            if (alias is "hid" or "hif") Assert.Empty(codec.ReadEnvelope(packet).Content.Value);
        }
        var answer = codec.ReadEnvelope(await Packet(pipe));
        Assert.Equal("lyu", answer.Alias);
        Assert.Equal(-71, answer.Uid);
        Assert.Equal(GameMessageKind.Response, answer.Kind);
        Assert.Equal((77, 135), Wardrobe.Of(_owner));
        Assert.Equal((0, 0), Wardrobe.Of(_other));
        Assert.Same(ambient, SessionContext.Actual);
    }

    private static long Create(long account, string name)
    {
        long id = DatabaseManager.CreateCharacter(account, 1, name, 11, 0, 1, [-1, -1, -1, -1, -1], 191105026, 200, 0, 0, []);
        Assert.True(id > 0);
        return id;
    }
    private static async Task<byte[]> Packet(ClientPipe pipe)
        => (await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)))!;
    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal IOException? Failure;
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (Failure != null) throw Failure;
            return [0x78, checked((byte)Frames.Count)];
        }
        public GameFrame Decode(ReadOnlySpan<byte> packet) => throw new InvalidOperationException("No wardrobe reparsing.");
    }
}
