using System.Net.Sockets;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network;

/// <summary>Adapts character storage, sessions and existing feature handlers to the entry lifecycle.</summary>
internal sealed class GameEntryBackend : IGameEntryBackend
{
    private static readonly GameEntryMapInitialization MapInitialization = new(new MapBackend());

    public bool BindTicket(GameSession session, IdentificationRequest request)
        => GameEntryDispatch.BindTicket(session, request);

    public async Task CloseOtherSessionsAsync(GameSession session)
    {
        int replaced = await SessionRegistry.CloseOthersOfAsync(session, TimeSpan.FromSeconds(10));
        if (replaced > 0)
            Console.WriteLine($"[Entry] Account {session.AccountId} came in again: {replaced} older connection(s) closed.");
    }

    public IReadOnlyList<DatabaseManager.DbCharacter> GetCharacters(GameSession session)
        => DatabaseManager.GetCharactersByAccountId(session.AccountId, session.ServerId);

    public bool CanResumeFight(long characterId) => FightHandler.FightToRejoin(characterId) != null;

    public bool SelectCharacter(GameSession session, long characterId)
        => CharacterSelectionHandler.SelectCharacterOnServer(characterId, session);

    public bool ResumeFight(GameSession session, long characterId)
    {
        var fight = FightHandler.FightToRejoin(characterId);
        if (fight == null || !SelectCharacter(session, characterId)) return false;
        FightHandler.RejoinState(fight);
        return true;
    }

    public async Task AbandonFightAsync(long characterId)
    {
        var fight = FightHandler.FightToRejoin(characterId);
        if (fight != null) await FightHandler.AbandonFromOutsideAsync(fight, characterId);
    }

    public Task SendWelcomeAsync(GameSession session, IReadOnlyList<DatabaseManager.DbCharacter> characters, bool withList)
        => ConnectionProtocol.SendWelcomeAsync(session, characters, withList);

    public Task SendCharacterListAsync(GameSession session, IReadOnlyList<DatabaseManager.DbCharacter> characters, bool withInitialization)
        => withInitialization
            ? ConnectionProtocol.SendCharacterListAsync(session, characters)
            : GameEntryMessages.SendAsync(session,
                ConnectionProtocol.Push(Op.Kvi, ConnectionProtocol.BuildCharactersList(characters)));

    public DatabaseManager.DbCharacter? FindCharacter(long characterId)
        => DatabaseManager.GetCharacterById(characterId);

    public void LoadCharacterState(GameSession session, DatabaseManager.DbCharacter character)
    {
        Managers.Equipment.LoadFrom(character.Id);
        Managers.SpellChoices.LoadFrom(character.Id);
        Managers.Quests.LoadFrom(character.Id);
        Managers.Achievements.LoadFrom(character.Id);
        Managers.Emotes.LoadFrom(character.Id);
        session.State.ElementsUsed = DatabaseManager.LoadElementsUsed(character.Id);
    }

    public Task AnnounceMapPresenceAsync(GameSession session, DatabaseManager.DbCharacter character)
        => SessionRegistry.BroadcastToMapAsync(session.MapId,
            ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(character,
                session.State.CellId, session.State.Orientation, session.AccountId)), session.Id);

    public Task SendInitialStateAsync(GameSession session, DatabaseManager.DbCharacter character)
        => WorldEntry.SendInitialStateAsync(session, character);

    public Task RestoreGuildRaidAsync(long characterId)
        => GuildRaidHandler.OnEnterWorldAsync(characterId);

    public Task NotifyGuildConnectionAsync(long characterId)
        => GuildHandler.TellConnectionAsync(characterId, online: true);

    public Task NotifyFriendsAsync(long accountId)
        => FriendsHandler.AnnouncePresenceAsync(accountId);

    public Task RestoreRemodelAsync(GameSession session, DatabaseManager.DbCharacter character)
        => RemodelHandler.OnEnterWorldAsync(StreamOf(session), character);

    public Task SendMapInitializationAsync(GameSession session, DatabaseManager.DbCharacter character)
    {
        // The character and the map go in the trace on purpose: when two clients enter at
        // once, it is the first thing to look at to know whether they have crossed.
        Console.WriteLine($"[Entry] Sending map initialization: {character.Name} on map {session.MapId}.");
        return MapInitialization.SendAsync(session, character);
    }

    private sealed class MapBackend : IGameEntryMapBackend
    {
        public Task SendMapAsync(GameSession session, DatabaseManager.DbCharacter character)
            => WorldEntry.SendMapAsync(session, character, session.MapId,
                session.State.IsInFight ? FightHandler.FightOf(session.CharacterId) : null);
        // Available titles and ornaments, and which one is worn.
        public Task SendWardrobeAsync(GameSession session) => WardrobeHandler.SendOwnedAsync(StreamOf(session), session.AccountId);
        public Task SendHousesAsync(GameSession session) => HouseHandler.SendAccountHousesAsync(StreamOf(session));
        public Task SendJournalAsync(GameSession session) => Managers.Quests.SendJournalAsync(StreamOf(session), box: true);
        public Task SendAchievementsAsync(GameSession session) => Managers.Achievements.SendListAsync(StreamOf(session));
        public Task SendPresetsAsync(GameSession session) => PresetHandler.SendListAsync(StreamOf(session));
        public Task SendMarksAsync(GameSession session) => Managers.Quests.SendMarksAsync(StreamOf(session), session.MapId);
    }

    public async Task<bool> TrySendFightMapAsync(GameSession session, GameRequest request)
    {
        var stream = StreamOf(session);
        if (request is FightMapRequest or ContextReadyRequest)
        {
            // A spectator's client has the arena: the fight as it stands, once.
            if (FightHandler.PendingSpectatorBoard() is { } watched)
            {
                await FightHandler.InTurnAsync(watched, () => FightHandler.SendSpectatorBoardAsync(stream, watched));
                return true;
            }
            // Back into a fight already running: the board as it is now, once. Placement
            // goes through preparation below, from scratch, as the capture does.
            if (FightHandler.PendingResume() is { } resumed)
            {
                await FightHandler.InTurnAsync(resumed, () => FightHandler.ResumeForOneAsync(stream, resumed));
                return true;
            }
        }

        if ((request is MapInformationRequest or ContextReadyRequest)
            && FightHandler.PendingPreparation() is { } prepared)
        {
            long requestedMap = request switch
            {
                MapInformationRequest map => map.MapId,
                ContextReadyRequest ready => ready.MapId,
                _ => 0,
            };
            if (requestedMap != 0 && requestedMap != prepared.MapId)
            {
                // A late request for the room left behind must not consume placement
                // or fall through to the entry service's roleplay-map validation.
                Program.LogDebug($"[Fight] The client asks for a map other than fight #{prepared.FightId}'s; " +
                                 "the placement waits for the fight's own.");
                return true;
            }

            // In a fight, who is on the map is not sent with a jss: it is the preparation's jxg,
            // and only when the client asks for them. That is the capture's
            // order, and sending them before the map change makes it discard them.
            //
            // And it asks for them with kmv, not with jrh. On loading a normal map the client sends
            // both, so hooking the jrh was enough there; but on entering a fight it sends
            // ijm and kmv and nothing else, and kmv was on the list of messages ignored without
            // a word. That is why the fight appeared in the server log and on screen
            // nothing happened.
            await FightHandler.InTurnAsync(prepared, async () =>
            {
                await FightHandler.SendPreparationAsync(stream, prepared);
                await session.SendAsync(new AreaFightModifierUpdateEvent([]));
            });
            return true;
        }
        return false;
    }

    public async Task SendMapInformationAsync(GameSession session, DatabaseManager.DbCharacter character)
    {
        var stream = StreamOf(session);
        var state = session.State;
        // The client asks who is on the map. Without an answer it draws an empty map:
        // no avatar, no NPCs, no monsters.
        //
        // The green marks go out FIRST, before the actor list. That is not a style
        // choice: in all 20 captures that load a map, iom comes before jss and
        // never after -- empty ones and full ones alike, "iom (0)" then "jss
        // (3589)", "iom (96)" then "jss (3895)". This server sent it after the
        // actors were complete, and the client drew nothing, which is exactly what
        // it looks like when the marks arrive for a map the client considers
        // finished. Same frames, same contents, wrong side of the actor list.
        //
        // Nothing open survives a map change either: otherwise the X of the new
        // map's zaap is taken by a conversation the player left behind.
        NpcHandler.Forget();
        WorkshopHandler.Forget();
        MarketplaceHandler.Forget();
        await DreamHandler.OnMapLoadedAsync(stream);
        AnomalyHandler.BeforeMapLoaded(session.MapId);
        await Managers.Quests.SendMarksAsync(stream, session.MapId);

        byte[] actors = ConnectionProtocol.Push(Op.Jss,
            ConnectionProtocol.BuildMapActors(session.MapId, character, state.CellId, state.Orientation, session.AccountId));
        await GameEntryMessages.SendAsync(session, actors);
        await FightHandler.SendFightCountAsync(stream, session.MapId);

        // In every capture that loads a map lva comes immediately after jss, and
        // without it the client never counts the map as loaded: two seconds later
        // it asks again with knm, kno and kny and goes round once more.
        // Inside the haven bag the furniture and permissions come between jss and lva.
        if (Managers.Merkasako.IsHavenBag(session.MapId))
            await MerkasakoHandler.SendFurnitureAsync(stream);
        await session.SendAsync(new AreaFightModifierUpdateEvent([]));

        // Map loads reset raid stagings and the feature-specific displays in the client.
        await Managers.GuildRaidStagings.OnMapLoadedAsync(stream, session.CharacterId, session.MapId);
        await TravelersTowerHandler.OnMapLoadedAsync(stream, session.MapId);
        await OsavoraSeasonHandler.OnMapLoadedAsync(stream, session.MapId);
        await AnomalyHandler.OnMapLoadedAsync(stream, session.MapId);
        await GargandiasHandler.OnMapLoadedAsync(stream, session.MapId);
        await GuildHallHandler.OnMapLoadedAsync(stream, session.CharacterId, session.MapId);

        // Objectives met by stepping on a map or a zone belong here, not in MapLoadHandler.
        // The kkr that handler receives only arrives on the world's initial load; walking
        // from one map to another does not send it. Four map changes in the log —154011397,
        // 154010885, 154010884, 154010883— never called the marks, leaving quest givers
        // without their exclamation marks. All five ways of reaching a map pass here,
        // because the client always asks for the actors.
        var map = MapManager.GetMapInfo(session.MapId);
        await Managers.Quests.OnMapEnteredAsync(stream, session.MapId, map?.SubAreaId ?? 0);
        await Managers.Achievements.OnMapEnteredAsync(stream, session.MapId, map?.SubAreaId ?? 0);
        await ConquestHandler.OnMapEnteredAsync(stream, session.CharacterId, map?.SubAreaId ?? 0);
        await PhantomHandler.OnMapLoadedAsync(stream);
        await Managers.MapStagings.ShowAsync(stream, Managers.MapStagings.OnArrival(session.MapId));
        Console.WriteLine($"[Entry] Actors of map {session.MapId} sent: {character.Name} on cell {state.CellId}.");
    }

    public Task TickAlterationsAsync(GameSession session) => AlterationHandler.TickAsync(StreamOf(session));

    public Task SendReturnToSelectionAsync(GameSession session)
        => session.SendAsync(ConnectionProtocol.Push(Op.Kqr, BuildReturnToken(session.AccountId)));

    public void PrepareReturnToSelection(GameSession session)
    {
        // If he leaves while in a fight, return him to the surface map. The fight itself stays,
        // as when the socket dies: the next character list carries kvd and he can come back.
        // LeaveFight -- which throws the fight away -- is only for a fight he could not rejoin.
        if (CanResumeFight(session.CharacterId))
        {
            FightHandler.BackToRoleplayMap();
            session.State.IsInFight = false;
            session.State.FightId = 0;
        }
        else FightHandler.LeaveFight();
    }

    public Task RemoveFromMapAsync(GameSession session)
        => SessionRegistry.RemoveFromMapAsync(session.MapId, session.CharacterId, session.Id);

    /// <summary>Reply to the "go back" request: a session id and a one.</summary>
    /// <remarks>
    /// THE ID IS REGISTERED BEFORE SENDING IT, and this was what left «Cambiar de
    /// servidor» hanging.
    ///
    /// The shape was right: the real server sends here a GUID WITH HYPHENS, 36 characters
    /// —«desde world a eleccion servidor.pcapng» opens with
    /// <c>kqr (40) 0a24 00000000-0000-4000-8000-00000000000a 2001</c>— and we sent
    /// one just like it. What was missing is that nobody knew ours afterwards.
    ///
    /// What the client does with it is measured in the player's log: it closes the
    /// connection, opens another, and presents THAT SAME id as its identity. The last one we sent
    /// was b52b…16eb and it is exactly the one that came back and we rejected, with
    /// «The presented token does not match any account». All the tokens this server
    /// minted were 32 characters —Guid "N" or sixteen bytes in hexadecimal— so an
    /// id of 36 could not match any of them by definition.
    ///
    /// Registering it opens nothing: the server mints it, it goes through that account's
    /// already authenticated socket, and it is good for the same as the game token already handed out.
    /// </remarks>
    private static byte[] BuildReturnToken(long accountId)
    {
        string token = Guid.NewGuid().ToString();
        if (accountId > 0) ClientLaunchRegistry.RegisterToken(accountId, token);
        return Pb.New().Str(1, token).Var(4, 1).Build();
    }

    private static NetworkStream StreamOf(GameSession session)
        => session.Stream ?? throw new InvalidOperationException("World entry requires a connected game session.");

    public string WelcomeMessage => ServerSettings.Current.WelcomeMessage;
}
