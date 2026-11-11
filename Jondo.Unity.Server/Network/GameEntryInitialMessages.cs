using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network;

/// <summary>
/// Initial state constructed directly from server, account and character state.
/// </summary>
internal sealed class GameEntryInitialMessages(
    IEnumerable<(int Room, int Theme)> rooms,
    IEnumerable<int> packs,
    SessionState characterState,
    ProfessionMessages professions,
    int alignmentRank,
    int experienceLimit,
    Func<AlterationsEvent> alterations,
    Func<ArenaUpdatePlayerInformationEvent> arena,
    Func<GuildRanksEvent> guildRanks,
    Func<CharacterSelectionEvent> selection,
    Func<InventoryContentEvent> inventory,
    Func<ZaapKnownListEvent> zaaps,
    Func<AlmanachCalendarDateEvent?> calendar,
    Func<TextInformationEvent?> previousConnection,
    AfterSelectionMessages afterSelection,
    InitialPresentationMessages presentation)
{
    internal async Task<int> SendAsync(GameSession session)
    {
        using var context = SessionContext.Push(session);
        int sent = 0;
        var roomDispatch = new HavenBagRoomUpdateEvent(
            rooms.Select(room => new HavenBagRoomPreview(room.Theme, room.Room)).ToArray(),
            HavenBagRoomAction.HavenBagRoomDispatch);

        await SendAsync(new QueueStatusEvent(Position: 0, Total: 0));
        await SendAsync(roomDispatch);

        // Entry reports no experience gain. Notifications start with no acknowledged flags
        // until Jondo owns persisted notification state; neither value comes from the capture.
        await SendAsync(new CharacterExperienceGainEvent(0));
        await SendAsync(new NotificationsEvent([]));
        await SendAsync(selection());
        // The captured standings were someone else's. Keep this position after
        // selection, using the character's own leagues and penalty. The captured
        // emote additions are reference-only; actual ownership travels at map entry.
        await SendAsync(arena());

        await SendAsync(inventory());
        await SendAsync(AfterSelectionMessages.Tags());
        // Capture all set messages before the first send: transport continuations cannot
        // change the later sets in this snapshot. There is no invented set-zero clear.
        foreach (var set in afterSelection.Sets(characterState)) await SendAsync(set);
        await SendAsync(professions.Descriptions(characterState));
        var experiences = professions.Experiences(characterState);
        await SendAsync(experiences);
        // Log zero too: a missing catalogue must be visible instead of silently
        // presenting a character with no professions while keeping saved progress.
        Console.WriteLine($"[World] {experiences.Experiences.Count} jobs sent, " +
            $"{experiences.Experiences.Count(job => job.JobLevel > 1 || job.JobXp > 0)} with progress.");
        // The captured artisan settings had minimum levels 150 and 69. Use this
        // character's settings, including the directory defaults for untouched jobs.
        await SendAsync(ArtisanHandler.Settings(characterState, professions.DirectoryJobs));
        // The recorded guild had renamed its leader to «rey». Use this guild's
        // actual ranks, or the server's default template for an unguilded character.
        await SendAsync(guildRanks());

        await SendAsync(ChatChannelMessages.Snapshot(characterState));
        await SendAsync(zaaps());
        // The captured alignment rank was 17, «Maître des Illusions». This rank
        // comes from this character's quests instead.
        await SendAsync(new AlignmentRankUpdateEvent(alignmentRank));

        await SendAsync(afterSelection.Weight(characterState));
        await SendAsync(new SequenceNumberEvent());
        await SendAsync(EntryInformationMessages.InitialWarning());
        if (previousConnection() is { } previous) await SendAsync(previous);
        await SendAsync(AfterSelectionMessages.Spouse());
        // The recording carried its owner's 763, 764, 766 and quest alteration 102.
        // This snapshot is the selected character's state, including an empty list.
        await SendAsync(alterations());
        // The captured limit was 100 for everyone. Keep Jondo's experience policy
        // and this character's persisted limit, including zero.
        await SendAsync(ExperienceLimitHandler.Settings(experienceLimit));

        await SendAsync(presentation.Capabilities());
        if (calendar() is { } date) await SendAsync(date);
        // The capture had eight themes and rooms in themes 32, 4, 1 and 1.
        // Jondo supplies every supported theme and this character's own rooms.
        await SendAsync(new HavenBagPackListEvent(packs.ToArray()));
        // Preserve both initial room dispatches while migrating the sequence.
        await SendAsync(roomDispatch);
        await SendAsync(afterSelection.Storage(session.AccountId));
        await SendAsync(presentation.Configuration());
        await SendAsync(AfterSelectionMessages.Bonuses());
        // Loading-complete belongs before the remaining appearance tail, rather than
        // the end of world/map initialization. Its position is server-owned too.
        await SendAsync(new CharacterLoadingCompleteEvent());
        await SendAsync(presentation.Colors());
        await SendAsync(InitialPresentationMessages.Aura());
        await SendAsync(InitialPresentationMessages.Ornament());
        // OutfitLookUpdated tag 3 is unused under the same omission policy. The
        // typed look contains current data, with no opaque capture extras.
        // See docs/populated-unmapped-fields.md#after-selection-omission-policy.
        await SendAsync(presentation.Look());
        return sent;

        async Task SendAsync(GameEvent message)
        {
            await session.SendAsync(message);
            sent++;
        }
    }
}
