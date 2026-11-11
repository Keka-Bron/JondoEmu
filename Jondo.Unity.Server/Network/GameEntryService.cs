using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IGameEntryBackend
{
    bool BindTicket(GameSession session, IdentificationRequest request);
    Task CloseOtherSessionsAsync(GameSession session);
    IReadOnlyList<DatabaseManager.DbCharacter> GetCharacters(GameSession session);
    bool CanResumeFight(long characterId);
    bool SelectCharacter(GameSession session, long characterId);
    bool ResumeFight(GameSession session, long characterId);
    Task AbandonFightAsync(long characterId);
    Task SendWelcomeAsync(GameSession session, IReadOnlyList<DatabaseManager.DbCharacter> characters, bool withList);
    Task SendCharacterListAsync(GameSession session, IReadOnlyList<DatabaseManager.DbCharacter> characters, bool withInitialization);
    DatabaseManager.DbCharacter? FindCharacter(long characterId);
    void LoadCharacterState(GameSession session, DatabaseManager.DbCharacter character);
    Task AnnounceMapPresenceAsync(GameSession session, DatabaseManager.DbCharacter character);
    Task SendInitialStateAsync(GameSession session, DatabaseManager.DbCharacter character);
    Task RestoreGuildRaidAsync(long characterId);
    Task NotifyGuildConnectionAsync(long characterId);
    Task NotifyFriendsAsync(long accountId);
    Task RestoreRemodelAsync(GameSession session, DatabaseManager.DbCharacter character);
    Task SendMapInitializationAsync(GameSession session, DatabaseManager.DbCharacter character);
    Task<bool> TrySendFightMapAsync(GameSession session, GameRequest request);
    Task SendMapInformationAsync(GameSession session, DatabaseManager.DbCharacter character);
    Task TickAlterationsAsync(GameSession session);
    Task SendReturnToSelectionAsync(GameSession session);
    void PrepareReturnToSelection(GameSession session);
    Task RemoveFromMapAsync(GameSession session);
    string WelcomeMessage { get; }
}

internal enum GameEntryResult { Unhandled, Handled, Close }

/// <summary>One entry lifecycle per session: identification, selection and map readiness.</summary>
/// <remarks>The caller owns the session turn. Legacy operations run under this session's context.</remarks>
internal sealed class GameEntryService(GameSession session, IGameEntryBackend backend)
{
    // The map block goes out once per entry into the world. kqo, which used to trigger it,
    // turns out to be a heartbeat that repeats every five seconds.
    private bool _hasSentMapInitialization;

    public async Task<GameEntryResult> HandleAsync(GameFrame frame)
    {
        if (frame.Content is not GameRequest request || request is not (IdentificationRequest
            or CharacterListRequest or CharacterSelectionRequest or CharacterFirstSelectionRequest
            or CharacterForceSelectionReadyRequest or SequenceNumberRequest or ContextCreationRequest
             or ContextReadyRequest or MapInformationRequest or FightMapRequest or PingRequest or ChatChannelSubscriptionRequest))
            return GameEntryResult.Unhandled;
        if (frame.Uid is null || !GameEntryDispatch.IsAllowed(session, request)) return GameEntryResult.Close;

        using var context = SessionContext.Push(session);
        switch (request)
        {
            case ChatChannelSubscriptionRequest subscription:
                await ChatChannelMessages.SubscribeAsync(session, subscription);
                return GameEntryResult.Handled;
            case IdentificationRequest identification:
                return await IdentifyAsync(identification);
            case CharacterListRequest:
                await SendCharacterListAsync();
                return GameEntryResult.Handled;
            case CharacterSelectionRequest selected:
                return await SelectAsync(selected.CharacterId);
            case CharacterFirstSelectionRequest first:
                // First selection's DoTutorial is decoded, not mistaken for a success flag.
                // Keep the existing saved-map entry policy.
                return await SelectAsync(first.CharacterId);
            case CharacterForceSelectionReadyRequest:
                return await ResumeAsync();
            case SequenceNumberRequest:
                // lqc is the client saying it has already digested the first block. This is where
                // it is time to give it the map.
                //
                // Before, we waited for the first kqo, which is the heartbeat and arrives every five seconds:
                // in the real client's log 4.8 s passed between choosing a character and receiving
                // the map. That gap is the flash of empty Incarnam before the fade to black:
                // the client is already in the world, does not yet know on which map, and meanwhile
                // shows its default scene, which is Incarnam's. That is why its
                // music also played on the character screen.
                await SendMapInitializationOnceAsync();
                return GameEntryResult.Handled;
            case PingRequest ping:
                // kqo is a heartbeat, not a request for the map. The client sends it every five
                // seconds for as long as it is in the world and the real server answers it with
                // kqy alone: twenty-four in a row, 5.000 ms apart, in the tutorial capture.
                //
                // Answering it with the map block is what made the client reload the world over
                // and over: the block carries jru, and jru means "load this map". So the block
                // goes out on the first kqo of the entry and the heartbeat gets its own answer
                // from then on. The block already opens with a kqy of its own, which is why the
                // first one is not answered twice.
                // The lqc has usually sent it already, so this does nothing; it stays here because
                // not everything that connects sends lqc —the test client, for one—
                // and without a map there is no world.
                if (!await SendMapInitializationOnceAsync())
                {
                    await session.SendAsync(new PongEvent(ping.Quiet));
                    await backend.TickAlterationsAsync(session);
                }
                return GameEntryResult.Handled;
            case ContextCreationRequest:
                return GameEntryResult.Handled;
            case ContextReadyRequest ready:
                if (await backend.TrySendFightMapAsync(session, request)) return GameEntryResult.Handled;
                return ready.MapId == session.MapId ? GameEntryResult.Handled : GameEntryResult.Close;
            case FightMapRequest:
                return await backend.TrySendFightMapAsync(session, request)
                    ? GameEntryResult.Handled : GameEntryResult.Unhandled;
            case MapInformationRequest map:
                if (await backend.TrySendFightMapAsync(session, request)) return GameEntryResult.Handled;
                if (map.MapId != session.MapId) return GameEntryResult.Close;
                // While fighting, sending the surface map's actors would take the client out of the fight.
                if (!session.State.IsInFight && backend.FindCharacter(session.CharacterId) is { } character)
                    await backend.SendMapInformationAsync(session, character);
                return GameEntryResult.Handled;
            default:
                return GameEntryResult.Unhandled;
        }
    }

    private async Task<GameEntryResult> IdentifyAsync(IdentificationRequest request)
    {
        // The ticket binds the account, server and launch language before any character is read.
        if (!backend.BindTicket(session, request)) return GameEntryResult.Close;
        // The same account in another connection goes, and its character is saved,
        // before this one reads its characters.
        await backend.CloseOtherSessionsAsync(session);
        var characters = backend.GetCharacters(session);
        // One of them still in a fight: the burst stops short of the list, and
        // the list goes out with the kvd behind it when the client asks (kvc).
        // Sending the list inside the burst and the kvd after it -- behind the
        // jtg -- put the client on the character screen anyway.
        bool resumable = characters.Any(c => backend.CanResumeFight(c.Id));
        await backend.SendWelcomeAsync(session, characters, withList: !resumable);
        return GameEntryResult.Handled;
    }

    private async Task SendCharacterListAsync()
    {
        // On an ordinary login the client already has the list. When a character is still in a
        // fight the list is followed by the empty kvd: "do not stop here". The client answers kwb.
        var characters = backend.GetCharacters(session);
        bool resumable = characters.Any(c => backend.CanResumeFight(c.Id));
        await backend.SendCharacterListAsync(session, characters, withInitialization: !resumable);
        if (resumable) await session.SendAsync(new CharacterForceSelectionEvent());
    }

    private async Task<GameEntryResult> SelectAsync(long characterId)
    {
        // The client chooses the id; selection must check the owning account and ticket's server.
        // kvl is the same step immediately after creation, without stopping at the character list.
        if (!backend.SelectCharacter(session, characterId)) return GameEntryResult.Close;

        // Picking a character that is still in a fight the ordinary way -- not the
        // kwb of "go on then" -- is turning the fight down: he gives it up, the
        // others get his surrender, and he enters the world where he left it.
        await backend.AbandonFightAsync(session.CharacterId);
        return await EnterWorldAsync() ? GameEntryResult.Handled : GameEntryResult.Close;
    }

    private async Task<GameEntryResult> ResumeAsync()
    {
        // Automatic force-selection continuation: the empty request names nobody. Select only
        // an account-owned character on this server whose fight is still resumable.
        var character = backend.GetCharacters(session).FirstOrDefault(c => backend.CanResumeFight(c.Id));
        if (character == null || !backend.ResumeFight(session, character.Id)) return GameEntryResult.Close;
        return await EnterWorldAsync() ? GameEntryResult.Handled : GameEntryResult.Close;
    }

    public async Task<bool> EnterWorldAsync()
    {
        if (!session.IsAuthenticated || !session.HasCharacter || session.IsInWorld) return false;

        using var context = SessionContext.Push(session);
        var character = backend.FindCharacter(session.CharacterId);
        if (character == null)
        {
            Console.WriteLine($"[Entry] Character {session.CharacterId} is not in the database.");
            return false;
        }

        backend.LoadCharacterState(session, character);
        _hasSentMapInitialization = false;
        session.EnterWorld();

        // A returning fighter is not arriving on the roleplay map.
        if (!session.State.IsInFight)
            await backend.AnnounceMapPresenceAsync(session, character);

        await backend.SendInitialStateAsync(session, character);
        await backend.RestoreGuildRaidAsync(character.Id);
        await backend.NotifyGuildConnectionAsync(character.Id);
        await backend.NotifyFriendsAsync(session.AccountId);
        await backend.RestoreRemodelAsync(session, character);

        string welcome = backend.WelcomeMessage;
        if (!string.IsNullOrWhiteSpace(welcome))
            await session.SendAsync(new TextInformationEvent(TextInformationType.TextInformationMessage,
                InfoMessages.FreeText, new[] { welcome.Trim() }));

        return true;
    }

    private async Task<bool> SendMapInitializationOnceAsync()
    {
        if (_hasSentMapInitialization || !session.IsInWorld) return false;
        var character = backend.FindCharacter(session.CharacterId);
        if (character == null) return false;

        // The block carries a map-load instruction: repeating it reloads the world again and again.
        await backend.SendMapInitializationAsync(session, character);
        _hasSentMapInitialization = true;
        return true;
    }

    public void OnMapChange()
    {
        // Entry initialization belongs to entering the world, not to ordinary map travel.
        _hasSentMapInitialization = true;
    }

    public async Task ReturnToSelectionAsync()
    {
        using var context = SessionContext.Push(session);
        await backend.SendReturnToSelectionAsync(session);
        backend.PrepareReturnToSelection(session);
        if (session.IsInWorld)
        {
            await backend.RemoveFromMapAsync(session);
            session.LeaveWorld();
        }
        _hasSentMapInitialization = false;
    }
}
