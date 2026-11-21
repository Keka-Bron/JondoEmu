using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class InitialPresentationMessagesTests
{
    private static readonly GameCodec Wire = new();
    internal static InitialPresentationMessages Empty { get; } = new(
        () => new(0), () => new(null), () => new(null), () => new(null), () => new("", null));

    [Fact]
    public void Live_configuration_is_explicit_server_policy_with_distinct_tokens_and_rewards()
    {
        var policy = InitialPresentationMessages.GuildPolicy();
        Assert.Equal(new GuildActivityConfiguration(32441, 12, 3, 2, 32442, 100), policy.Activity);
        Assert.Equal(86_400_000, policy.ActivityDelayMs);
        Assert.Equal(new GuildContributionConfiguration(10000, 10, 100, 10), policy.Contribution);
        Assert.Equal(-1, InitialPresentationMessages.AllowedEmblemCategories);
        Assert.Equal(1, InitialPresentationMessages.PaletteSlots);
    }

    [Fact]
    public void Palette_uses_RGB_slots_and_keeps_holes_inheritance_order_and_last_assignment()
    {
        Assert.Equal(new[] { 0x112233, -1, 0x556677 },
            InitialPresentationMessages.Palette([0x03556677, 0x01112233]));
        Assert.Equal(new[] { 0x112233, -1, 0xabcdef },
            InitialPresentationMessages.Palette([0x01112233, -1, 0x03abcdef]));
        Assert.Equal(new[] { 0, 0x112233, -1 }, InitialPresentationMessages.Palette([0, 0x112233, -1]));
        Assert.Equal(new[] { 0x556677 }, InitialPresentationMessages.Palette([0x01112233, 0x01556677]));
        Assert.Empty(InitialPresentationMessages.Palette([]));
        Assert.Throws<OverflowException>(() => InitialPresentationMessages.Palette([(long)uint.MaxValue + 1]));
    }

    [Fact]
    public void Live_palette_reads_the_selected_characters_persisted_colors_and_refreshes_without_capture_data()
    {
        var character = new Jondo.Unity.Server.DbCharacter { Id = 71_001,
            Breed = 18, Sex = 1, Colors = new(0x112233, null, 0xABCDEF, null, 0, 0xFFFFFF) };
        var messages = InitialPresentationMessages.Live(character);
        using var context = SessionContext.Push(GameSession.SinSocket());
        Assert.Equal(-1, messages.Capabilities().GuildEmblemSymbolCategory);
        var colors = messages.Colors().CharacterColors!;
        Assert.Equal(1, colors.SlotsUnlocked);
        Assert.Equal(Jondo.Unity.Server.Managers.ColorPaletteManager.TotalSlots, colors.SlotsTotal);
        var defaults = Jondo.Unity.Server.Managers.BreedLookCatalog.Get(character.Breed, character.Sex)!.Colors;
        Assert.Equal(new[] { 0x112233, (int)defaults[1], 0xABCDEF, (int)defaults[3], 0, 0xFFFFFF },
            Assert.Single(colors.ColorPalettes).Colors);
        character.Colors = new(0x556677, null, null, null, null, null);
        Assert.Equal(new[] { 0x556677, (int)defaults[1], (int)defaults[2], (int)defaults[3],
            (int)defaults[4], (int)defaults[5] }, Assert.Single(messages.Colors().CharacterColors!.ColorPalettes).Colors);
        Assert.Equal(0x112233, colors.ColorPalettes[0].Colors[0]);
        Assert.Equal(0xABCDEF, colors.ColorPalettes[0].Colors[2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(206)]
    [InlineData(int.MaxValue)]
    public void Shared_legacy_writer_also_omits_the_unused_ornament_value(int value)
        => Assert.Empty(ConnectionProtocol.BuildOrnamentUpdated(value));

    [Fact]
    public void Aura_carries_the_main_aura_and_none_is_absent()
    {
        // The client reads lym's tag 1 as the main aura: «Activer automatiquement mon aura de
        // face» shows nothing without it.
        Assert.Empty(ConnectionProtocol.BuildAura(0));
        Assert.Equal(new byte[] { 0x08, 0xce, 0x01 }, ConnectionProtocol.BuildAura(206));
        Assert.Null(InitialPresentationMessages.Aura(0).AuraEmoteId);
        Assert.Equal(206, InitialPresentationMessages.Aura(206).AuraEmoteId);
        Assert.Null(Empty.Aura().AuraEmoteId);
    }

    [Fact]
    public void Color_and_recursive_look_snapshots_are_detached_and_keep_meaningful_absence()
    {
        var colors = new List<int> { -1, 0, 0x112233 };
        var palettes = new List<ColorPalette> { new(colors), new([]) };
        var skins = new List<int> { 81_001, 81_002 };
        var child = new EntityLook(colors, 3, 2, [55], skins, []);
        var children = new List<SubEntityInformation> { new(child, 2, SubEntityBindingPointCategory.HookPointCategoryMountDriver) };
        var root = new EntityLook(colors, 3, 71_001, [100], skins, children);
        var messages = new InitialPresentationMessages(() => new(int.MinValue),
            () => new(new(4, palettes, 13)), () => new(null), () => new(null), () => new("owned-look", root));
        var appearance = messages.Colors();
        var look = messages.Look();
        colors.Clear();
        skins.Clear();
        children.Clear();
        palettes.Clear();
        Assert.Equal(int.MinValue, messages.Capabilities().GuildEmblemSymbolCategory);
        Assert.Equal(4, appearance.CharacterColors!.SlotsUnlocked);
        Assert.Equal(13, appearance.CharacterColors.SlotsTotal);
        Assert.Equal(new[] { -1, 0, 0x112233 }, appearance.CharacterColors.ColorPalettes[0].Colors);
        Assert.Empty(appearance.CharacterColors.ColorPalettes[1].Colors);
        Assert.Equal("owned-look", look.OutfitId);
        Assert.Equal(new[] { 81_001, 81_002 }, look.Look!.Skins);
        Assert.Equal(new[] { -1, 0, 0x112233 }, Assert.Single(look.Look.SubEntities).SubEntityLook.Colors);
        Assert.Null(Empty.Colors().CharacterColors);
        Assert.Null(Empty.Configuration().GuildConfiguration);
        Assert.Null(Empty.Look().Look);
    }

    [Fact]
    public async Task Seven_events_use_owned_sources_codec_context_and_positions_without_any_manifest()
    {
        var own = new Recipient();
        var wrong = new Recipient();
        var reads = new List<string>();
        var policy = new GuildConfiguration(new(81_001, 7, 2, 5, 81_002, 97), 9_000_000_001,
            new(8_000_000_001, 11, 23, 37));
        void Read(string name) { reads.Add(name); Assert.Same(own.Session, SessionContext.Actual); }
        var messages = new InitialPresentationMessages(
            () => { Read("capabilities"); return new(unchecked((int)0x80000001)); },
            () => { Read("colors"); return new(new(2, [new([-1, 0x112233])], 17)); },
            () => { Read("configuration"); return new(policy); },
            () => { Read("aura"); return new(206); },
            () => { Read("look"); return new("owner-look", new([0x01112233], 3, 71_001, [55], [81_001], [])); });
        using var context = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();
        int sent = await Initial(own.Session.State, messages).SendAsync(own.Session).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(32, sent);
        Assert.Equal(new[] { "capabilities", "configuration", "colors", "aura", "look" }, reads);
        Assert.Equal(sent, own.Codec.Frames.Count);
        Assert.Equal(sent, own.Packets.Count);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Empty(wrong.Packets);
        Assert.Same(wrong.Session, SessionContext.Actual);
        var events = own.Codec.Frames.Select(frame => frame.Content).ToArray();
        Assert.Equal(policy, Assert.Single(events.OfType<ConfigurationEvent>()).GuildConfiguration);
        Assert.Equal(206, Assert.Single(events.OfType<OutfitAuraChangedEvent>()).AuraEmoteId);
        Assert.Equal(unchecked((int)0x80000001), Assert.Single(events.OfType<CharacterCapabilitiesEvent>()).GuildEmblemSymbolCategory);
        int sequence = Array.FindIndex(events, item => item is SequenceNumberEvent);
        Assert.IsType<InventoryWeightEvent>(events[sequence - 1]);
        Assert.IsType<TextInformationEvent>(events[sequence + 1]);
        int capabilities = Array.FindIndex(events, item => item is CharacterCapabilitiesEvent);
        Assert.IsType<ServerExperienceModifierEvent>(events[capabilities - 1]);
        Assert.IsType<HavenBagPackListEvent>(events[capabilities + 1]);
        int configuration = Array.FindIndex(events, item => item is ConfigurationEvent);
        Assert.IsType<StorageKamasUpdateEvent>(events[configuration - 1]);
        Assert.Equal(new[] { typeof(ConfigurationEvent), typeof(BonusesEvent), typeof(CharacterLoadingCompleteEvent),
            typeof(CharacterAppearanceCollectionEvent), typeof(OutfitAuraChangedEvent), typeof(OrnamentSelectedEvent), typeof(OutfitLookUpdatedEvent) },
            events.Skip(configuration).Select(item => item.GetType()));
        Assert.DoesNotContain(events, item => item is QuestObjectiveUpdatedEvent or TitleSelectionErrorEvent or CosmeticInventoryLoadedEvent);
    }

    public static IEnumerable<object[]> DeliveryFailures => new[] { typeof(SequenceNumberEvent), typeof(CharacterCapabilitiesEvent),
        typeof(ConfigurationEvent), typeof(CharacterAppearanceCollectionEvent), typeof(OutfitAuraChangedEvent), typeof(OrnamentSelectedEvent), typeof(OutfitLookUpdatedEvent) }
        .SelectMany(type => new[] { new object[] { type, false }, new object[] { type, true } });

    [Theory]
    [MemberData(nameof(DeliveryFailures))]
    public async Task Every_new_event_propagates_codec_and_transport_errors_once_and_restores_context(Type type, bool transport)
    {
        var failure = new IOException("presentation delivery failed");
        var own = new Recipient(codecFailure: frame => !transport && frame.Content.GetType() == type ? failure : null,
            send: packet => transport && GameBindings.StableName(Wire.ReadEnvelope(packet).Alias) == type.Name ? Task.FromException(failure) : Task.CompletedTask);
        var wrong = new Recipient();
        using var context = SessionContext.Push(wrong.Session);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Initial(own.Session.State, Empty).SendAsync(own.Session)));
        Assert.Equal(type, own.Codec.Frames.Last().Content.GetType());
        Assert.Single(own.Codec.Frames, frame => frame.Content.GetType() == type);
        Assert.Equal(transport ? 1 : 0, own.Packets.Count(packet => GameBindings.StableName(Wire.ReadEnvelope(packet).Alias) == type.Name));
        Assert.Same(wrong.Session, SessionContext.Actual);
        Assert.Empty(wrong.Packets);
        Assert.Equal(0, own.Codec.Decodes);
    }

    [Theory]
    [InlineData("capabilities")]
    [InlineData("configuration")]
    [InlineData("colors")]
    [InlineData("aura")]
    [InlineData("look")]
    public async Task Source_failure_stops_before_its_send_without_retry(string source)
    {
        var failure = new IOException("presentation source failed");
        int reads = 0;
        void Read(string name) { if (name == source) { reads++; throw failure; } }
        var messages = new InitialPresentationMessages(
            () => { Read("capabilities"); return new(0); }, () => { Read("colors"); return new(null); },
            () => { Read("configuration"); return new(null); }, () => { Read("aura"); return new(null); },
            () => { Read("look"); return new("", null); });
        var own = new Recipient();
        var wrong = new Recipient();
        using var context = SessionContext.Push(wrong.Session);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Initial(own.Session.State, messages).SendAsync(own.Session)));
        Assert.Equal(1, reads);
        Assert.Same(wrong.Session, SessionContext.Actual);
        Type absent = source switch { "capabilities" => typeof(CharacterCapabilitiesEvent), "configuration" => typeof(ConfigurationEvent),
            "colors" => typeof(CharacterAppearanceCollectionEvent), "aura" => typeof(OutfitAuraChangedEvent),
            _ => typeof(OutfitLookUpdatedEvent) };
        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content.GetType() == absent);
    }

    private static GameEntryInitialMessages Initial(SessionState state, InitialPresentationMessages messages) => new([], [], state,
        new([], _ => []), 0, 0, () => new([]), () => new("1970-01-01T00:00:00.000Z", []), () => new([]),
        () => GameEntryInitialMessagesTests.Selection(), () => new(0, []), () => new([]), () => null, () => null,
        AfterSelectionMessagesTests.Empty, messages);

    private sealed class Recipient
    {
        internal List<byte[]> Packets { get; } = [];
        internal Codec Codec { get; }
        internal GameSession Session { get; }
        internal Recipient(Func<GameFrame, Exception?>? codecFailure = null, Func<byte[], Task>? send = null)
        {
            Codec = new(codecFailure);
            Session = new(Codec, async packet => { Packets.Add(packet); if (send != null) await send(packet); });
        }
    }
    private sealed class Codec(Func<GameFrame, Exception?>? failure) : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal int Decodes { get; private set; }
        public GameFrame Decode(ReadOnlySpan<byte> payload) { Decodes++; throw new Exception("Unexpected decode"); }
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (failure?.Invoke(frame) is { } error) throw error;
            return Wire.Encode(frame);
        }
    }
}
