using System.Text.Json;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GameEntryInitialMessagesTests
{
    private static readonly GameCodec Wire = new();
    private static readonly string[] DirectNames = [nameof(QueueStatusEvent), nameof(HavenBagRoomUpdateEvent),
        nameof(JobCrafterDirectorySettingsEvent), nameof(AlignmentRankUpdateEvent),
        nameof(ServerExperienceModifierEvent), nameof(HavenBagPackListEvent),
        nameof(JobDescriptionEvent), nameof(JobExperiencesUpdateEvent), nameof(AlterationsEvent), nameof(ArenaUpdatePlayerInformationEvent), nameof(GuildRanksEvent),
        nameof(CharacterExperienceGainEvent), nameof(NotificationsEvent), nameof(CharacterSelectionEvent), nameof(InventoryContentEvent),
        nameof(ChatChannelsEnabledEvent), nameof(ZaapKnownListEvent), nameof(AlmanachCalendarDateEvent), nameof(TextInformationEvent),
        nameof(TagStoragesRefreshEvent), nameof(SetUpdateEvent), nameof(InventoryWeightEvent), nameof(SpouseStatusEvent),
        nameof(StorageKamasUpdateEvent), nameof(BonusesEvent), nameof(CharacterLoadingCompleteEvent),
        nameof(SequenceNumberEvent), nameof(CharacterCapabilitiesEvent), nameof(ConfigurationEvent),
        nameof(CharacterAppearanceCollectionEvent), nameof(OutfitAuraChangedEvent), nameof(OrnamentSelectedEvent), nameof(OutfitLookUpdatedEvent)];
    private static readonly string[] DirectSequence = [nameof(QueueStatusEvent), nameof(HavenBagRoomUpdateEvent),
        nameof(CharacterExperienceGainEvent), nameof(NotificationsEvent), nameof(CharacterSelectionEvent),
        nameof(ArenaUpdatePlayerInformationEvent), nameof(InventoryContentEvent), nameof(TagStoragesRefreshEvent),
        nameof(JobDescriptionEvent), nameof(JobExperiencesUpdateEvent),
        nameof(JobCrafterDirectorySettingsEvent), nameof(GuildRanksEvent), nameof(ChatChannelsEnabledEvent), nameof(ZaapKnownListEvent), nameof(AlignmentRankUpdateEvent),
        nameof(InventoryWeightEvent), nameof(SequenceNumberEvent), nameof(TextInformationEvent), nameof(SpouseStatusEvent),
        nameof(AlterationsEvent), nameof(ServerExperienceModifierEvent), nameof(CharacterCapabilitiesEvent), nameof(AlmanachCalendarDateEvent), nameof(HavenBagPackListEvent), nameof(HavenBagRoomUpdateEvent),
        nameof(StorageKamasUpdateEvent), nameof(ConfigurationEvent), nameof(BonusesEvent), nameof(CharacterLoadingCompleteEvent),
        nameof(CharacterAppearanceCollectionEvent), nameof(OutfitAuraChangedEvent), nameof(OrnamentSelectedEvent), nameof(OutfitLookUpdatedEvent)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_sequence_keeps_both_room_dispatches_and_the_optional_previous_visit(bool returning)
    {
        var previous = returning ? new DatabaseManager.LastVisit { When = DateTimeOffset.Parse("2026-11-11T09:08:00+02:00") } : null;
        var (packets, codec, sent) = await CaptureAsync(EmptyState(previous));
        var expected = DirectSequence.ToList();
        if (returning) expected.Insert(expected.IndexOf(nameof(TextInformationEvent)) + 1, nameof(TextInformationEvent));
        Assert.Equal(expected, packets.Select(MessageName));
        Assert.Equal(packets.Count, sent);
        Assert.Equal(returning ? 34 : 33, codec.Encodes);
        Assert.Equal(0, codec.Decodes);
        Assert.DoesNotContain(nameof(QuestObjectiveUpdatedEvent), packets.Select(MessageName));
        Assert.DoesNotContain(nameof(TitleSelectionErrorEvent), packets.Select(MessageName));
    }

    [Fact]
    public async Task Empty_equipment_keeps_arena_after_selection_and_professions_after_storage_without_captured_sets()
    {
        var (packets, _, _) = await CaptureAsync(EmptyState());
        var names = packets.Select(MessageName).ToList();
        int arena = names.IndexOf(nameof(ArenaUpdatePlayerInformationEvent));
        int professions = names.IndexOf(nameof(JobDescriptionEvent));
        Assert.Equal(nameof(CharacterSelectionEvent), names[arena - 1]);
        Assert.Equal(nameof(TagStoragesRefreshEvent), names[professions - 1]);
        Assert.DoesNotContain(nameof(SetUpdateEvent), names);
        Assert.Equal(nameof(JobExperiencesUpdateEvent), names[professions + 1]);
        Assert.Equal(nameof(JobCrafterDirectorySettingsEvent), names[professions + 2]);
        Assert.DoesNotContain(nameof(EmoteAddedEvent), names);
        Assert.DoesNotContain(nameof(EmotesEvent), names);
    }

    [Theory]
    [InlineData(0, 0, false, 0)]
    [InlineData(23, 47, true, 83)]
    public async Task State_construction_sends_expected_semantic_messages_through_the_owning_session(
        int rank, int limit, bool free, int minLevel)
    {
        const int configuredJob = 81_001, untouchedJob = 81_002;
        var state = new SessionState();
        state.CrafterSettings[configuredJob] = new(minLevel, free, true);
        // Base job always has minimum zero, even if a stored setting says otherwise.
        state.CrafterSettings[WorkshopHandler.BaseJob] = new(99, false, false);
        int firstTheme = 2001 + rank, lastTheme = 2007 + rank;
        var initial = new GameEntryInitialMessages([(0, firstTheme), (3, lastTheme)], [lastTheme, firstTheme], state,
            new([WorkshopHandler.BaseJob, configuredJob, untouchedJob], _ => []), rank, limit, () => new([]), EmptyArena, () => new([]), () => Selection(),
            () => InventoryMessages.FromCatalogue.Snapshot(state), () => new([]), () => new(81_001),
            () => EntryInformationMessages.PreviousConnection(state.PreviousVisit), AfterSelectionMessagesTests.Empty, InitialPresentationMessagesTests.Empty);
        var wrongPackets = new List<byte[]>();
        var wrongCodec = new GameMessagingTests.CountingCodec();
        var wrongSession = new GameSession(wrongCodec, packet => { wrongPackets.Add(packet); return Task.CompletedTask; });
        wrongSession.State.CrafterSettings[configuredJob] = new(199, !free, false);

        List<byte[]> packets;
        using (SessionContext.Push(wrongSession))
        {
            var result = await CaptureAsync(initial);
            packets = result.Packets;
            Assert.Equal(33, result.Sent);
            Assert.Equal(33, result.Codec.Encodes);
            Assert.Equal(0, result.Codec.Decodes);
            Assert.Same(wrongSession, SessionContext.Actual);
        }
        Assert.Empty(wrongPackets);
        Assert.Equal(0, wrongCodec.Encodes);
        Assert.Equal(DirectSequence, packets.Select(MessageName));

        // Expectations describe the character's state independently of the
        // production factories and the protocol's protobuf representation.
        var rooms = new HavenBagRoomUpdateEvent(
            [new HavenBagRoomPreview(firstTheme, 0), new HavenBagRoomPreview(lastTheme, 3)],
            HavenBagRoomAction.HavenBagRoomDispatch);
        GameEvent[] expected = [new QueueStatusEvent(0, 0), rooms,
            new CharacterExperienceGainEvent(0), new NotificationsEvent([]), Selection(), EmptyArena(), new InventoryContentEvent(0, []), new TagStoragesRefreshEvent([]),
            new JobDescriptionEvent([new JobDescription(configuredJob, []), new JobDescription(untouchedJob, [])]),
            new JobExperiencesUpdateEvent([
                new JobExperience(configuredJob, 20, 1, 0, 0), new JobExperience(untouchedJob, 20, 1, 0, 0)]),
            new JobCrafterDirectorySettingsEvent([
                new JobCrafterDirectorySettings(WorkshopHandler.BaseJob, false, 0),
                new JobCrafterDirectorySettings(configuredJob, free, minLevel),
                new JobCrafterDirectorySettings(untouchedJob, true, 1)]),
            new GuildRanksEvent([]), DefaultChannels(), new ZaapKnownListEvent([]), new AlignmentRankUpdateEvent(rank),
            new InventoryWeightEvent(0, 1000), new SequenceNumberEvent(), new TextInformationEvent(TextInformationType.TextInformationError, 89, []), new SpouseStatusEvent(false),
            new AlterationsEvent([]),
            new ServerExperienceModifierEvent(105, 105, limit),
            new CharacterCapabilitiesEvent(0), new AlmanachCalendarDateEvent(81_001), new HavenBagPackListEvent([lastTheme, firstTheme]), rooms,
            new StorageKamasUpdateEvent(0), new ConfigurationEvent(null), new BonusesEvent([]), new CharacterLoadingCompleteEvent(),
            new CharacterAppearanceCollectionEvent(null), new OutfitAuraChangedEvent(), new OrnamentSelectedEvent(), new OutfitLookUpdatedEvent("", null)];
        for (int i = 0; i < packets.Count; i++) AssertEvent(expected[i], packets[i]);
    }

    [Fact]
    public async Task Initial_state_sends_thirty_three_events_with_available_calendar_and_no_previous_visit()
    {
        var (packets, _, sent) = await CaptureAsync(EmptyState());
        Assert.Equal(33, sent);
        Assert.Equal(DirectSequence, packets.Select(MessageName));
        var rooms = new HavenBagRoomUpdateEvent([], HavenBagRoomAction.HavenBagRoomDispatch);
        GameEvent[] expected = [new QueueStatusEvent(0, 0), rooms,
            new CharacterExperienceGainEvent(0), new NotificationsEvent([]), Selection(), EmptyArena(), new InventoryContentEvent(0, []), new TagStoragesRefreshEvent([]),
            new JobDescriptionEvent([]), new JobExperiencesUpdateEvent([]),
            new JobCrafterDirectorySettingsEvent([]), new GuildRanksEvent([]), DefaultChannels(), new ZaapKnownListEvent([]), new AlignmentRankUpdateEvent(0),
            new InventoryWeightEvent(0, 1000), new SequenceNumberEvent(), new TextInformationEvent(TextInformationType.TextInformationError, 89, []), new SpouseStatusEvent(false),
            new AlterationsEvent([]),
            new ServerExperienceModifierEvent(105, 105, 0), new CharacterCapabilitiesEvent(0), new AlmanachCalendarDateEvent(81_001), new HavenBagPackListEvent([]), rooms,
            new StorageKamasUpdateEvent(0), new ConfigurationEvent(null), new BonusesEvent([]), new CharacterLoadingCompleteEvent(),
            new CharacterAppearanceCollectionEvent(null), new OutfitAuraChangedEvent(), new OrnamentSelectedEvent(), new OutfitLookUpdatedEvent("", null)];
        for (int i = 0; i < packets.Count; i++) AssertEvent(expected[i], packets[i]);
    }

    [Fact]
    public async Task Send_failure_stops_the_sequence_without_a_legacy_retry()
    {
        var failure = new IOException("test transport failure");
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, _ => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => EmptyState().SendAsync(session)));
        Assert.Equal(1, codec.Encodes);
        Assert.Equal(0, codec.Decodes);
    }

    [Fact]
    public async Task Entry_gain_notifications_and_selection_are_sent_once_from_policy_and_the_selected_snapshot()
    {
        var state = new SessionState { Experience = 9_000_000, CharacterId = 71_001 };
        int selections = 0;
        var initial = new GameEntryInitialMessages([], [], state, new([], _ => []), 0, 0,
            () => new([]), EmptyArena, () => new([]), () => { selections++; return Selection(state.CharacterId); },
            () => InventoryMessages.FromCatalogue.Snapshot(state), () => new([]), () => new(81_001),
            () => EntryInformationMessages.PreviousConnection(state.PreviousVisit), AfterSelectionMessagesTests.Empty, InitialPresentationMessagesTests.Empty);
        var wrongSession = new GameSession(new GameMessagingTests.CountingCodec(), _ => Task.CompletedTask);
        wrongSession.State.CharacterId = 81_002;
        wrongSession.State.Experience = 42;
        using var scope = SessionContext.Push(wrongSession);

        var (packets, codec, sent) = await CaptureAsync(initial);
        var frames = packets.Select(packet => Wire.ReadEnvelope(packet)).ToArray();
        var direct = packets.Where(packet => IsDirect(Wire.ReadEnvelope(packet).Alias)).Select(packet => Wire.Decode(packet)).ToArray();
        Assert.Equal(0, Assert.Single(direct.Select(frame => frame.Content).OfType<CharacterExperienceGainEvent>()).CharacterExperience);
        Assert.Empty(Assert.Single(direct.Select(frame => frame.Content).OfType<NotificationsEvent>()).Flags);
        Assert.Equal(71_001, Assert.Single(direct.Select(frame => frame.Content).OfType<CharacterSelectionEventCaseSuccess>()).Value.Character.Id);
        Assert.Equal(new[] { nameof(QueueStatusEvent), nameof(HavenBagRoomUpdateEvent), nameof(CharacterExperienceGainEvent),
            nameof(NotificationsEvent), nameof(CharacterSelectionEvent), nameof(ArenaUpdatePlayerInformationEvent) },
            frames.Take(6).Select(frame => MessageName(frame.Alias)));
        Assert.All(direct, frame => Assert.Null(frame.Uid));
        Assert.Equal(1, selections);
        Assert.Equal(33, codec.Encodes);
        Assert.Equal(0, codec.Decodes);
        Assert.Equal(packets.Count, sent);
        Assert.Same(wrongSession, SessionContext.Actual);
    }

    [Fact]
    public async Task Inventory_is_built_once_from_owned_state_after_arena_and_before_storage_refresh()
    {
        var state = new SessionState { Kamas = 9_000_000_000 };
        state.EquipmentItems.Add(71_001, InventoryMessagesTests.Item(71_001, 81_001, 0, 7));
        int snapshots = 0;
        var initial = WithInventory(() => { snapshots++; return InventoryMessages.FromCatalogue.Snapshot(state); }, state);
        var wrong = GameSession.SinSocket();
        wrong.State.Kamas = 42;
        wrong.State.EquipmentItems.Add(71_002, InventoryMessagesTests.Item(71_002, 81_002, 63, 1));
        using var context = SessionContext.Push(wrong);

        var (packets, codec, _) = await CaptureAsync(initial);
        var names = packets.Select(MessageName).ToList();
        int index = names.IndexOf(nameof(InventoryContentEvent));
        Assert.Equal(nameof(ArenaUpdatePlayerInformationEvent), names[index - 1]);
        Assert.Equal(nameof(TagStoragesRefreshEvent), names[index + 1]);
        var frame = Wire.Decode(packets[index]);
        var inventory = Assert.IsType<InventoryContentEvent>(frame.Content);
        Assert.Equal(9_000_000_000, inventory.Kamas);
        var item = Assert.Single(inventory.Objects);
        Assert.Equal(0, item.Position);
        Assert.Equal(71_001, item.Item.Uid);
        Assert.Equal(81_001, item.Item.Gid);
        Assert.Equal(7, item.Item.Quantity);
        Assert.Null(frame.Uid);
        Assert.Equal(1, snapshots);
        Assert.Equal(1, names.Count(name => name == nameof(InventoryContentEvent)));
        Assert.Equal(33, codec.Encodes);
        Assert.Equal(0, codec.Decodes);
        Assert.Same(wrong, SessionContext.Actual);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("codec")]
    [InlineData("transport")]
    public async Task Inventory_entry_failure_stops_before_storage_and_professions_without_retry(string stage)
    {
        var failure = new IOException("test inventory entry failure");
        int snapshots = 0, inventorySends = 0;
        var initial = WithInventory(() =>
        {
            snapshots++;
            if (stage == "snapshot") throw failure;
            return new(123, []);
        });
        var codec = new InventoryFailureCodec(stage == "codec" ? failure : null);
        var session = new GameSession(codec, packet =>
        {
            if (MessageName(packet) != nameof(InventoryContentEvent)) return Task.CompletedTask;
            inventorySends++;
            return Task.FromException(failure);
        });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => initial.SendAsync(session)));
        Assert.Equal(1, snapshots);
        Assert.Equal(stage == "snapshot" ? 6 : 7, codec.Frames.Count);
        Assert.DoesNotContain(codec.Frames, frame => frame.Content is JobDescriptionEvent or TagStoragesRefreshEvent);
        Assert.Equal(stage == "transport" ? 1 : 0, inventorySends);
    }

    [Fact]
    public async Task Environment_sources_keep_their_positions_and_missing_calendar_does_not_send_a_fabricated_date()
    {
        var state = new SessionState { ActiveChatChannels = [ChatChannel.Private] };
        var environment = new EntryEnvironmentMessages(() => [9_000_000_001, 71_001], () => null, _ => { });
        var initial = new GameEntryInitialMessages([], [], state, new([], _ => []), 0, 0,
            () => new([]), EmptyArena, () => new([]), () => Selection(), () => new(0, []), environment.Zaaps, environment.Calendar,
            () => EntryInformationMessages.PreviousConnection(state.PreviousVisit), AfterSelectionMessagesTests.Empty, InitialPresentationMessagesTests.Empty);
        var (packets, _, count) = await CaptureAsync(initial);
        var names = packets.Select(MessageName).ToArray();
        int channels = Array.IndexOf(names, nameof(ChatChannelsEnabledEvent));
        Assert.Equal(nameof(GuildRanksEvent), names[channels - 1]);
        Assert.Equal(new[] { ChatChannel.Private }, Assert.IsType<ChatChannelsEnabledEvent>(Wire.Decode(packets[channels]).Content).ActiveChannels);
        Assert.Equal(new long[] { 9_000_000_001, 71_001 }, Assert.IsType<ZaapKnownListEvent>(Wire.Decode(packets[channels + 1]).Content).Destinations);
        Assert.Equal(nameof(AlignmentRankUpdateEvent), names[channels + 2]);
        Assert.DoesNotContain(nameof(AlmanachCalendarDateEvent), names);
        Assert.Equal(32, packets.Count(packet => IsDirect(Wire.ReadEnvelope(packet).Alias)));
        Assert.Equal(count, packets.Count);
        Assert.Equal(nameof(CharacterCapabilitiesEvent), names[Array.IndexOf(names, nameof(HavenBagPackListEvent)) - 1]);
    }

    [Theory]
    [InlineData(false, 13)]
    [InlineData(true, 22)]
    public async Task Environment_source_failure_stops_before_later_state_without_retry(bool calendarFailure, int sentBeforeFailure)
    {
        var failure = new IOException("environment source failed");
        int reads = 0;
        var initial = new GameEntryInitialMessages([], [], new(), new([], _ => []), 0, 0,
            () => new([]), EmptyArena, () => new([]), () => Selection(), () => new(0, []),
            () => { if (!calendarFailure) { reads++; throw failure; } return new([]); },
             () => { reads++; throw failure; }, () => null, AfterSelectionMessagesTests.Empty, InitialPresentationMessagesTests.Empty);
        var recipient = new StatusRecipient();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => initial.SendAsync(recipient.Session)));
        Assert.Equal(sentBeforeFailure, recipient.Packets.Count);
        Assert.Equal(1, reads);
        Assert.DoesNotContain(recipient.Codec.Frames, frame => frame.Content is HavenBagPackListEvent);
    }

    private sealed class InventoryFailureCodec(Exception? failure) : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("No production decode expected.");
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (frame.Content is InventoryContentEvent && failure != null) throw failure;
            return Wire.Encode(frame);
        }
    }

    private static GameEntryInitialMessages WithInventory(Func<InventoryContentEvent> inventory, SessionState? state = null)
        => new([], [], state ?? new SessionState(), new([], _ => []), 0, 0,
            () => new([]), EmptyArena, () => new([]), () => Selection(), inventory, () => new([]), () => new(81_001),
            () => EntryInformationMessages.PreviousConnection(state?.PreviousVisit), AfterSelectionMessagesTests.Empty, InitialPresentationMessagesTests.Empty);

    [Theory]
    [InlineData(nameof(CharacterExperienceGainEvent), 3)]
    [InlineData(nameof(NotificationsEvent), 4)]
    [InlineData(nameof(CharacterSelectionEvent), 5)]
    public async Task Failure_in_a_new_direct_send_stops_before_arena_or_legacy_without_retry(string failedMessage, int attempts)
    {
        var failure = new IOException("test entry send failure");
        var codec = new GameMessagingTests.CountingCodec();
        var names = new List<string>();
        var session = new GameSession(codec, packet =>
        {
            string name = MessageName(packet);
            names.Add(name);
            return name == failedMessage ? Task.FromException(failure) : Task.CompletedTask;
        });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => EmptyState().SendAsync(session)));
        Assert.Equal(attempts, codec.Encodes);
        Assert.Equal(0, codec.Decodes);
        Assert.Equal(1, names.Count(name => name == failedMessage));
        Assert.DoesNotContain(nameof(ArenaUpdatePlayerInformationEvent), names);
    }

    private static ArenaUpdatePlayerInformationEvent EmptyArena() => new("1970-01-01T00:00:00.000Z", []);

    internal static CharacterSelectionEventCaseSuccess Selection(long id = 71_001)
        => CharacterSelectionMessages.Success(new() { Id = id, Name = "Selected", Level = 1, LastConnection = "" },
            new EntityLook([], 3, 1, [], [], []));

    private static GameEntryInitialMessages EmptyState(DatabaseManager.LastVisit? previous = null) => new([], [], new SessionState(), new([], _ => []), 0, 0,
        () => new([]), EmptyArena, () => new([]), () => Selection(), () => new(0, []), () => new([]), () => new(81_001),
        () => EntryInformationMessages.PreviousConnection(previous), AfterSelectionMessagesTests.Empty, InitialPresentationMessagesTests.Empty);

    private static ChatChannelsEnabledEvent DefaultChannels() => new(
        [ChatChannel.General, ChatChannel.Team, ChatChannel.Guild, ChatChannel.Alliance, ChatChannel.Group,
            ChatChannel.Admin, ChatChannel.Private, ChatChannel.Information, ChatChannel.Fight, ChatChannel.Arena],
        [ChatChannel.Trade, ChatChannel.Recruitment, ChatChannel.Noob, ChatChannel.Promotional, ChatChannel.Community,
            ChatChannel.Unknown15, ChatChannel.Unknown16, ChatChannel.Unknown17, ChatChannel.Unknown18, ChatChannel.Unknown19]);

    private static async Task<(List<byte[]> Packets, GameMessagingTests.CountingCodec Codec, int Sent)> CaptureAsync(
        GameEntryInitialMessages initial)
    {
        var packets = new List<byte[]>();
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, packet => { packets.Add(packet); return Task.CompletedTask; });
        int sent = await initial.SendAsync(session);
        return (packets, codec, sent);
    }

    private static bool IsDirect(string opcode) => DirectNames.Contains(MessageName(opcode));

    private static string MessageName(string opcode) => GameBindings.StableName(opcode) ?? opcode;

    private static string MessageName(byte[] packet) => MessageName(Wire.ReadEnvelope(packet).Alias);

    private static void AssertEvent(GameEvent expected, byte[] packet)
    {
        var frame = Wire.Decode(packet);
        Assert.Null(frame.Uid);
        switch (expected)
        {
            case TagStoragesRefreshEvent tags:
                Assert.Equal(tags.TagInformation, Assert.IsType<TagStoragesRefreshEvent>(frame.Content).TagInformation);
                break;
            case BonusesEvent bonuses:
                Assert.Equal(bonuses.Bonuses, Assert.IsType<BonusesEvent>(frame.Content).Bonuses);
                break;
            case TextInformationEvent notice:
                var actualNotice = Assert.IsType<TextInformationEvent>(frame.Content);
                Assert.Equal(notice.MessageType, actualNotice.MessageType);
                Assert.Equal(notice.MessageId, actualNotice.MessageId);
                Assert.Equal(notice.Parameters, actualNotice.Parameters);
                break;
            case ChatChannelsEnabledEvent channels:
                var actualChannels = Assert.IsType<ChatChannelsEnabledEvent>(frame.Content);
                Assert.Equal(channels.ActiveChannels, actualChannels.ActiveChannels);
                Assert.Equal(channels.DisabledChannels, actualChannels.DisabledChannels);
                break;
            case ZaapKnownListEvent zaaps:
                Assert.Equal(zaaps.Destinations, Assert.IsType<ZaapKnownListEvent>(frame.Content).Destinations);
                break;
            case InventoryContentEvent inventory:
                Assert.Equal(JsonSerializer.Serialize(inventory),
                    JsonSerializer.Serialize(Assert.IsType<InventoryContentEvent>(frame.Content)));
                break;
            case NotificationsEvent notifications:
                Assert.Equal(notifications.Flags, Assert.IsType<NotificationsEvent>(frame.Content).Flags);
                break;
            case CharacterSelectionEventCaseSuccess selection:
                Assert.Equal(JsonSerializer.Serialize(selection),
                    JsonSerializer.Serialize(Assert.IsType<CharacterSelectionEventCaseSuccess>(frame.Content)));
                break;
            case HavenBagRoomUpdateEvent rooms:
                var actualRooms = Assert.IsType<HavenBagRoomUpdateEvent>(frame.Content);
                Assert.Equal(rooms.Action, actualRooms.Action);
                Assert.Equal(rooms.Preview.ToArray(), actualRooms.Preview.ToArray());
                break;
            case HavenBagPackListEvent packs:
                Assert.Equal(packs.PacksId.ToArray(), Assert.IsType<HavenBagPackListEvent>(frame.Content).PacksId.ToArray());
                break;
            case JobCrafterDirectorySettingsEvent settings:
                Assert.Equal(settings.Settings.ToArray(),
                    Assert.IsType<JobCrafterDirectorySettingsEvent>(frame.Content).Settings.ToArray());
                break;
            case JobExperiencesUpdateEvent experiences:
                Assert.Equal(experiences.Experiences.ToArray(),
                    Assert.IsType<JobExperiencesUpdateEvent>(frame.Content).Experiences.ToArray());
                break;
            case JobDescriptionEvent descriptions:
                var actualJobs = Assert.IsType<JobDescriptionEvent>(frame.Content).Jobs;
                Assert.Equal(descriptions.Jobs.Count, actualJobs.Count);
                for (int i = 0; i < actualJobs.Count; i++)
                {
                    Assert.Equal(descriptions.Jobs[i].JobId, actualJobs[i].JobId);
                    Assert.Equal(descriptions.Jobs[i].Skills.ToArray(), actualJobs[i].Skills.ToArray());
                }
                break;
            case AlterationsEvent alterations:
                Assert.Empty(alterations.Alterations);
                Assert.Empty(Assert.IsType<AlterationsEvent>(frame.Content).Alterations);
                break;
            case ArenaUpdatePlayerInformationEvent arena:
                var actualArena = Assert.IsType<ArenaUpdatePlayerInformationEvent>(frame.Content);
                Assert.Equal(arena.BanEndDate, actualArena.BanEndDate);
                Assert.Equal(arena.ArenaRanks.ToArray(), actualArena.ArenaRanks.ToArray());
                break;
            case GuildRanksEvent ranks:
                Assert.Empty(ranks.Ranks);
                Assert.Empty(Assert.IsType<GuildRanksEvent>(frame.Content).Ranks);
                break;
            default:
                Assert.Equal(expected, frame.Content);
                break;
        }
    }
}
