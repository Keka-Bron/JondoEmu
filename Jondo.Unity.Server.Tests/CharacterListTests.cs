using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class CharacterListTests
{
    [Fact]
    public void Snapshot_preserves_identity_presence_and_detaches_recursive_looks()
    {
        var colors = new List<int> { 0x01112233 };
        var skins = new List<int> { 110, 2172 };
        var children = new List<SubEntityInformation>
        {
            new(new(colors, 3, 2, [55], skins,
                [new(new([], 3, 77, [80], [], []), 7, SubEntityBindingPointCategory.Unknown9)]),
                2, SubEntityBindingPointCategory.HookPointCategoryMountDriver),
        };
        var resolved = new EntityLook([0x01445566], 3, 200, [120], [800], children);
        var source = new List<DbCharacter>
        {
            new() { Id = 9_000_000_001, Name = "Sélection ☀", Breed = 18, Sex = 1, Level = 57,
                LastConnection = "2026-11-12T09:00:00Z" },
            new() { Id = 9_000_000_002, Name = "Zero", Breed = 1, Sex = 0, Level = 0 },
        };
        var snapshot = new CharacterListMessages(_ => resolved).Snapshot(source);
        source[0].Name = "Changed";
        source.Clear();
        colors.Clear();
        skins.Clear();
        children.Clear();

        Assert.Equal(new long[] { 9_000_000_001, 9_000_000_002 }, snapshot.Characters.Select(character => character.Id));
        var basic = Basic(snapshot.Characters[0]);
        Assert.Equal("Sélection ☀", basic.Name);
        Assert.Equal(57, basic.Level);
        var appearance = Appearance(snapshot.Characters[0]);
        Assert.Equal(18, appearance.BreedId);
        Assert.Empty(appearance.LastConnectionDate);
        Assert.Empty(appearance.LastDisconnectionDate);
        Assert.Equal(Gender.Female, Identity(appearance).Gender);
        Assert.IsType<CharacterBaseInformationComplementCaseNone>(Identity(appearance).ComplementCase);
        Assert.Equal(3, appearance.Look.LookType);
        Assert.Equal(200, appearance.Look.Bones);
        Assert.Equal(new[] { 0x01445566 }, appearance.Look.Colors);
        Assert.Equal(new[] { 120 }, appearance.Look.Scales);
        Assert.Equal(new[] { 800 }, appearance.Look.Skins);
        var rider = Assert.Single(appearance.Look.SubEntities);
        Assert.Equal(2, rider.BindingPointIndex);
        Assert.Equal(SubEntityBindingPointCategory.HookPointCategoryMountDriver, rider.BindingPointCategory);
        Assert.Equal(2, rider.SubEntityLook.Bones);
        Assert.Equal(new[] { 0x01112233 }, rider.SubEntityLook.Colors);
        Assert.Equal(new[] { 110, 2172 }, rider.SubEntityLook.Skins);
        Assert.Equal(new[] { 55 }, rider.SubEntityLook.Scales);
        var pet = Assert.Single(rider.SubEntityLook.SubEntities);
        Assert.Equal((77, 7, SubEntityBindingPointCategory.Unknown9),
            (pet.SubEntityLook.Bones, pet.BindingPointIndex, pet.BindingPointCategory));
        Assert.Equal(new[] { 80 }, pet.SubEntityLook.Scales);
        Assert.Equal("Zero", Basic(snapshot.Characters[1]).Name);
        Assert.Null(Basic(snapshot.Characters[1]).Level);
        Assert.Equal(1, Appearance(snapshot.Characters[1]).BreedId);
        Assert.Equal(Gender.Male, Identity(Appearance(snapshot.Characters[1])).Gender);
    }

    [Fact]
    public void Sorting_compares_instants_and_keeps_ties_invalid_and_missing_dates_stable()
    {
        DbCharacter Character(long id, string date) => new()
        { Id = id, Name = $"Character {id}", LastConnection = date };
        var source = new[]
        {
            Character(1, ""), Character(2, "2026-11-12T10:00:00+02:00"),
            Character(3, "invalid"), Character(4, "2026-11-12T09:30:00+01:00"),
            Character(5, "2026-11-12T08:00:00Z"), Character(6, ""),
        };
        var messages = new CharacterListMessages(_ => new([], 3, 1, [], [], []));
        Assert.Equal(new long[] { 4, 2, 5, 1, 3, 6 }, messages.Snapshot(source).Characters.Select(character => character.Id));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, source.Select(character => character.Id));
        Assert.Empty(messages.Snapshot([]).Characters);
    }

    [Fact]
    public async Task Refresh_uses_owner_context_codec_and_account_and_reads_gifts_after_the_list()
    {
        var own = new Recipient(71_001);
        var wrong = new Recipient(71_002);
        int reads = 0;
        var messages = new CharacterListMessages(character =>
        {
            Assert.Same(own.Session, SessionContext.Actual);
            Assert.Equal(91_001, character.Id);
            reads++;
            return new([], 3, 23, [], [], []);
        });
        var gifts = new GiftService(account =>
        {
            Assert.Equal(71_001, account);
            Assert.Same(own.Session, SessionContext.Actual);
            Assert.Equal(4, own.Delivered);
            Assert.IsType<CharacterListEvent>(own.Codec.Frames[^1].Content);
        }, account =>
        {
            Assert.Equal(71_001, account);
            return [new() { Id = 81_001, Gid = 82_001, Quantity = 3, Title = "Owned gift" }];
        }, () => throw new Exception("No broadcast expected"), _ => throw new Exception("No log expected"));
        using var context = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();
        await new CharacterListService(messages, gifts).SendAsync(own.Session,
            [new() { Id = 91_001, Name = "Own", Level = 12 }]).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, reads);
        Assert.Equal(new GamePayload[] { new QueueStatusEvent(1, 1), new QueueStatusEvent(1, 0),
            new QueueStatusEvent(0, 0) }, own.Codec.Frames.Take(3).Select(frame => frame.Content));
        var list = Assert.IsType<CharacterListEvent>(own.Codec.Frames[3].Content);
        Assert.Equal(23, Appearance(Assert.Single(list.Characters)).Look.Bones);
        var gift = Assert.Single(Assert.IsType<GameActionItemListEvent>(own.Codec.Frames[4].Content).Actions);
        Assert.Equal(81_001, Assert.Single(gift.ItemUids));
        Assert.Equal(5, own.Delivered);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Empty(wrong.Codec.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Fact]
    public async Task Empty_refresh_still_sends_queue_list_and_the_owning_accounts_gifts()
    {
        var own = new Recipient(71_001);
        int reads = 0;
        var service = new CharacterListService(new(_ => throw new Exception("Empty list resolves no look")),
            new(_ => { }, account => { Assert.Equal(71_001, account); reads++; return []; }, () => [], _ => { }));
        await service.SendAsync(own.Session, []);
        Assert.Equal(1, reads);
        Assert.Equal(5, own.Delivered);
        Assert.Empty(Assert.IsType<CharacterListEvent>(own.Codec.Frames[3].Content).Characters);
        Assert.Empty(Assert.IsType<GameActionItemListEvent>(own.Codec.Frames[4].Content).Actions);
    }

    [Fact]
    public async Task List_only_delivery_does_not_read_gifts_or_emit_queue_events()
    {
        var own = new Recipient(71_001);
        var service = new CharacterListService(new(_ => new([], 3, 23, [], [], [])),
            new(_ => throw new Exception("No gifts on fight continuation"), _ => [], () => [], _ => { }));
        await service.SendAsync(own.Session, [new() { Id = 91_001, Name = "Resume", Level = 57 }], false);
        var list = Assert.IsType<CharacterListEvent>(Assert.Single(own.Codec.Frames).Content);
        Assert.Equal(91_001, Assert.Single(list.Characters).Id);
        Assert.Equal(1, own.Delivered);
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Fact]
    public async Task Production_backend_uses_the_owning_codec_for_list_only_delivery()
    {
        var own = new Recipient(71_001);
        await new GameEntryBackend().SendCharacterListAsync(own.Session, [], withInitialization: false);
        Assert.Empty(Assert.IsType<CharacterListEvent>(Assert.Single(own.Codec.Frames).Content).Characters);
        Assert.Equal(1, own.Delivered);
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_source_failure_stops_before_gifts_without_partial_list_or_retry(bool invalidGender)
    {
        var own = new Recipient(71_001);
        var failure = new IOException("Look source failed");
        int reads = 0;
        var service = new CharacterListService(new(_ =>
        {
            reads++;
            if (reads == 1) return new([], 3, 1, [], [], []);
            throw failure;
        }),
            new(_ => throw new Exception("No gifts after source failure"), _ => [], () => [], _ => { }));
        var characters = new[]
        {
            new DbCharacter { Name = "Valid", Sex = 0 },
            new DbCharacter { Name = "Invalid", Sex = invalidGender ? 2 : 0 },
        };
        if (invalidGender)
            await Assert.ThrowsAsync<GameConversionException>(() => service.SendAsync(own.Session, characters));
        else
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.SendAsync(own.Session, characters)));
        Assert.Equal(invalidGender ? 1 : 2, reads);
        Assert.Equal(3, own.Delivered);
        Assert.All(own.Codec.Frames, frame => Assert.IsType<QueueStatusEvent>(frame.Content));
    }

    public static IEnumerable<object[]> Failures()
        => Enumerable.Range(0, 5).SelectMany(index => new[] { new object[] { index, false }, new object[] { index, true } });

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Codec_and_transport_failures_stop_at_the_failed_event_without_retry(int index, bool transport)
    {
        var failure = new IOException("Delivery failed");
        var own = new Recipient(71_001);
        var wrong = new Recipient(71_002);
        own.Codec.BeforeEncode = frame =>
        {
            if (!transport && own.Codec.Frames.Count == index + 1) throw failure;
        };
        own.AfterSend = () => { if (transport && own.Delivered == index + 1) throw failure; };
        int giftReads = 0;
        var service = new CharacterListService(new(_ => new([], 3, 1, [], [], [])),
            new(_ => { }, _ => { giftReads++; return []; }, () => [], _ => { }));
        using var context = SessionContext.Push(wrong.Session);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => service.SendAsync(own.Session, [])));
        Assert.Equal(index + 1, own.Codec.Frames.Count);
        Assert.Equal(transport ? index + 1 : index, own.Delivered);
        Assert.Equal(index == 4 ? 1 : 0, giftReads);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    private static CharacterBasicInformation Basic(Character character)
        => Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(character.ComplementCase).Value;
    private static CharacterLook Appearance(Character character)
        => Assert.IsType<CharacterBasicInformationComplementCaseCharacterLook>(Basic(character).ComplementCase).Value;
    private static CharacterBaseInformation Identity(CharacterLook appearance)
        => Assert.IsType<CharacterLookComplementCaseBaseInformation>(appearance.ComplementCase).Value;

    private sealed class Recipient
    {
        internal RecordingCodec Codec { get; } = new();
        internal GameSession Session { get; }
        internal int Delivered;
        internal Action? AfterSend;
        internal Recipient(long account)
        {
            Session = new(Codec, _ => { Delivered++; AfterSend?.Invoke(); return Task.CompletedTask; });
            Session.BindAccount(account, 290);
        }
    }

    private sealed class RecordingCodec : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal Action<GameFrame>? BeforeEncode;
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); BeforeEncode?.Invoke(frame); return [0x42]; }
        public GameFrame Decode(ReadOnlySpan<byte> payload) { Decodes++; throw new Exception("Unexpected list decode"); }
    }
}
