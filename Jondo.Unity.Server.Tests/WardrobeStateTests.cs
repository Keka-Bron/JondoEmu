using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class WardrobeStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_burst_uses_owner_codec_and_turn_in_order_with_detached_live_and_saved_looks(bool fighting)
    {
        var codec = new RecordingCodec();
        var owner = new GameSession(codec, _ => Task.CompletedTask);
        owner.BindAccount(71_901, 1);
        owner.State.CharacterId = 71_902;
        owner.State.IsInFight = fighting;
        var ambient = GameSession.SinSocket();
        using var context = SessionContext.Push(ambient);
        using var turn = await owner.EnterTurnAsync();
        var backend = new Backend(owner);
        var service = new WardrobeStateService(backend, new(backend));
        await service.SendOwnedAsync(owner).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { typeof(TitlesAndOrnamentsEvent), typeof(CosmeticInventoryLoadedEvent), typeof(OutfitsLoadedEvent),
            typeof(OutfitAuraChangedEvent), typeof(TitleSelectedEvent), typeof(OrnamentSelectedEvent),
            typeof(GameRolePlayShowActorsEvent), typeof(OutfitLookUpdatedEvent) }, codec.Frames.Select(frame => frame.Content.GetType()));
        Assert.Equal(!fighting, backend.IncludeMap);
        Assert.Equal(new[] { 31, 17 }, Assert.IsType<TitlesAndOrnamentsEvent>(codec.Frames[0].Content).Titles);
        var list = Assert.IsType<OutfitsLoadedEvent>(codec.Frames[2].Content);
        Assert.Same(list.Outfits[0], list.Current);
        Assert.Equal(new[] { "live", "saved" }, list.Outfits.Select(outfit => outfit.Uuid));
        Assert.Equal(new[] { 616, 617 }, list.Outfits.Select(outfit => outfit.HeadId));
        Assert.Equal(new[] { 26, 110 }, list.Outfits.Select(outfit => outfit.BodyId));
        Assert.Null(list.Current!.TitleId);
        Assert.Null(list.Current.OrnamentId);
        Assert.Null(list.Current.AuraEmoteId);
        var saved = list.Outfits[1];
        Assert.Equal(77, saved.TitleId);
        Assert.Equal(135, saved.OrnamentId);
        Assert.Equal(206, saved.AuraEmoteId);
        Assert.Equal("2026-08-09T18:20:27.0000000Z", saved.LastModified);
        Assert.Equal(new[] { ObjectSlot.Hat, ObjectSlot.Cape }, saved.HiddenSlots);
        Assert.Equal(9256, Assert.Single(saved.Objects).Value.Gid);
        Assert.Null(Assert.Single(saved.Objects).Value.Skin);
        backend.Colors.Clear();
        backend.Garments.Clear();
        backend.OutfitRows.Clear();
        backend.ChildSkins.Clear();
        Assert.Equal(new[] { 0x112233, -1 }, saved.Colors!.Colors);
        Assert.Single(saved.Objects);
        Assert.Equal(new[] { 719 }, Assert.Single(saved.EntityLook!.SubEntities).SubEntityLook.Skins);
        Assert.Equal(0, owner.UnoCadaVez.CurrentCount);
        Assert.Same(ambient, SessionContext.Actual);
        Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
        await service.SendOwnedAsync(owner);
        var empty = Assert.IsType<OutfitsLoadedEvent>(codec.Frames[10].Content);
        Assert.Empty(empty.Outfits);
        Assert.Null(empty.Current);
    }

    [Fact]
    public async Task Missing_character_still_sends_ownership_cosmetics_and_empty_presentation_notifications()
    {
        var codec = new RecordingCodec();
        var owner = new GameSession(codec, _ => Task.CompletedTask);
        var backend = new Backend(owner) { Missing = true };
        await new WardrobeStateService(backend, new(backend)).SendOwnedAsync(owner);
        Assert.Equal(new[] { typeof(TitlesAndOrnamentsEvent), typeof(CosmeticInventoryLoadedEvent),
            typeof(TitleSelectedEvent), typeof(OrnamentSelectedEvent) }, codec.Frames.Select(frame => frame.Content.GetType()));
    }

    [Theory]
    [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)] [InlineData(4, true)]
    [InlineData(5, true)] [InlineData(6, true)] [InlineData(7, true)] [InlineData(8, true)]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)]
    [InlineData(5, false)] [InlineData(6, false)] [InlineData(7, false)] [InlineData(8, false)]
    public async Task Failed_encode_or_send_stops_the_burst_and_restores_context(int stop, bool encode)
    {
        var failure = new IOException("wardrobe delivery");
        var codec = new RecordingCodec { Stop = encode ? stop : 0, Failure = failure };
        int sent = 0;
        var owner = new GameSession(codec, _ => ++sent == stop ? throw failure : Task.CompletedTask);
        var ambient = GameSession.SinSocket();
        using var context = SessionContext.Push(ambient);
        using var turn = await owner.EnterTurnAsync();
        var backend = new Backend(owner);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new WardrobeStateService(backend, new(backend))
            .SendOwnedAsync(owner).WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(stop, codec.Frames.Count);
        Assert.Equal(encode ? stop - 1 : stop, sent);
        Assert.Equal(0, owner.UnoCadaVez.CurrentCount);
        Assert.Same(ambient, SessionContext.Actual);
    }

    [Fact]
    public async Task Source_failure_stops_before_outfits_and_restores_owner_context()
    {
        var codec = new RecordingCodec();
        var owner = new GameSession(codec, _ => Task.CompletedTask);
        var backend = new Backend(owner) { Fail = true };
        var ambient = GameSession.SinSocket();
        using var context = SessionContext.Push(ambient);
        await Assert.ThrowsAsync<IOException>(() => new WardrobeStateService(backend, new(backend)).SendOwnedAsync(owner));
        Assert.Equal(2, codec.Frames.Count);
        Assert.Same(ambient, SessionContext.Actual);
    }

    [Fact]
    public void Cosmetics_copy_unlocks_and_effects_clamp_quantity_and_check_uid_width()
    {
        var ids = new List<int> { 616, 617 };
        var rows = new List<CosmeticCollection.Entry> { new() { Gid = 9256, Uid = int.MaxValue, Quantity = 0,
            Effects = "[[118,0,0,0],[988,0,0,0,\"Ada\"],[-1,7,0,0]]" } };
        var messages = new CosmeticCollectionMessages(new((_, value, _, _) => (EffectFields.AsNumber, value, 0, 0)));
        var result = messages.Snapshot(ids, [24], [110], rows);
        ids.Clear(); rows.Clear();
        Assert.Equal(new[] { 616, 617 }, result.Heads);
        Assert.Equal(new[] { 24 }, result.Idles);
        Assert.Equal(new[] { 110 }, result.Bodies);
        var item = Assert.Single(result.Objects);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(int.MaxValue, item.Uid);
        Assert.Equal(new ObjectEffect[] { new(118, new ObjectEffectCaseValueInt(0)), new(988, new ObjectEffectCaseValueString("Ada")) }, item.Effects);
        Assert.Empty(item.StorageQuantities);
        Assert.Throws<OverflowException>(() => messages.Snapshot([], [], [], [new() { Uid = (long)int.MaxValue + 1 }]));
        Assert.Throws<OverflowException>(() => WardrobeMessages.Owned([(long)int.MaxValue + 1], []));
    }

    [Fact]
    public void Missing_saved_head_body_idle_and_colors_use_the_existing_fallback_policy()
    {
        var owner = GameSession.SinSocket();
        using var context = SessionContext.Push(owner);
        var backend = new Backend(owner);
        backend.Colors.Clear();
        var character = new Jondo.Unity.Server.DbCharacter { Id = owner.CharacterId, HeadId = 616, Breed = 11 };
        var outfit = new Outfits.Outfit("saved", 99, "", -1, false, DateTime.UnixEpoch,
            new(0, 0, 0, [], 0, 0, []));
        var result = new WardrobeMessages(backend).Outfit(character, outfit);
        Assert.Equal(616, result.HeadId);
        Assert.Equal(BodyTable.BaseOf(11, 0), result.BodyId);
        Assert.Equal(IdleTable.Plain, result.IdleId);
        Assert.Null(result.Colors);
        Assert.Empty(result.HiddenSlots);
        Assert.Empty(result.Objects);
        Assert.Null(result.TitleId);
    }

    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal int Stop;
        internal IOException? Failure;
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (Frames.Count == Stop) throw Failure!;
            return [0x77, checked((byte)Frames.Count)];
        }
        public GameFrame Decode(ReadOnlySpan<byte> packet) => throw new InvalidOperationException("No wardrobe reparsing.");
    }

    private sealed class Backend(GameSession owner) : IWardrobeStateBackend, IWardrobeMessagesBackend
    {
        internal bool Missing, Fail;
        internal bool? IncludeMap;
        internal readonly List<long> Colors = [0x112233, -1];
        internal readonly List<int> ChildSkins = [719];
        internal readonly List<Wardrobe.Worn> Garments = [new(10, 9256000, 9256, true), new(9, 0, 0, true)];
        internal readonly List<Outfits.Outfit> OutfitRows = [];
        public TitlesAndOrnamentsEvent Owned()
        {
            Assert.Same(owner, SessionContext.Actual);
            return WardrobeMessages.Owned([31, 17], [135]);
        }
        public CosmeticInventoryLoadedEvent Cosmetics(long accountId)
        {
            Assert.Equal(owner.AccountId, accountId);
            return new([], [], [], []);
        }
        public DbCharacter? Character(long characterId)
        {
            Assert.Equal(owner.CharacterId, characterId);
            return Missing ? null : new() { Id = characterId, Breed = 11, HeadId = 616 };
        }
        public IReadOnlyList<Outfits.Outfit> Outfits(long characterId)
        {
            if (Fail) throw new IOException("wardrobe source");
            if (OutfitRows.Count == 0 && IncludeMap == null)
            {
                OutfitRows.Add(new("live", 99, "", -1, false, new(2026, 8, 9, 18, 20, 27, DateTimeKind.Utc), null));
                OutfitRows.Add(new("saved", 101, "Distinct", 17, true, new(2026, 8, 9, 18, 20, 27, DateTimeKind.Utc),
                    new(617, 110, 24, Colors, 77, 135, Garments, 206)));
            }
            return OutfitRows;
        }
        public async Task RefreshAsync(GameSession session, DbCharacter character, bool includeMap)
        {
            Assert.Same(owner, session);
            IncludeMap = includeMap;
            await session.SendAsync(new GameRolePlayShowActorsEvent([]));
        }
        public OutfitLookUpdatedEvent Look(DbCharacter character) => new("owned-id", new([], 3, 1, [], [], []));
        public Outfits.Look Worn(DbCharacter character) => new(616, 26, 3, Colors, 0, 0, [], 0);
        IReadOnlyList<long> IWardrobeMessagesBackend.Colors(DbCharacter character, Outfits.Look look) => Colors;
        EntityLook IWardrobeMessagesBackend.Look(DbCharacter character, Outfits.Look look, bool kept)
        {
            Assert.Same(owner, SessionContext.Actual);
            return new([], 3, 1, [125], [], [new(new([], 3, 77, [], ChildSkins, []), 0, SubEntityBindingPointCategory.HookPointCategoryPet)]);
        }
    }
}
