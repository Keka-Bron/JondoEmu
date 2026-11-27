using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Mods;

internal sealed partial class CharacterWorld : ICharacterWorld, ICompanionWorld, IPartyWorld, IMovementWorld, IQuestWorld
{
    public ICharacterSession? FindSelectedSession(long accountId) => ControlApi.InTheWorld(accountId);

    public bool OwnsCharacter(ICharacterSession master, long characterId)
        => DatabaseManager.CharacterBelongsToAccount(characterId, master.AccountId);

    public bool IsClientOnline(long characterId) => SessionRegistry.FindByCharacter(characterId) != null;

    public ICharacterSession? LoadFollower(ICharacterSession master, long characterId)
    {
        var owner = (GameSession)master;
        var session = GameSession.SinSocket();
        session.BindAccount(owner.AccountId, owner.ServerId, owner.State.Language);
        using var context = SessionContext.Push(session);
        return CharacterSelectionHandler.LoadForCompanion(characterId) ? session : null;
    }

    public void SaveFollower(ICharacterSession follower)
    {
        var session = (GameSession)follower;
        using var context = SessionContext.Push(session);
        session.State.IsInFight = false;
        DatabaseManager.SaveCurrentCharacter();
    }

    public void NotifyPresence(long characterId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GuildHandler.TellConnectionAsync(characterId, SessionRegistry.IsOnline(characterId));
                await AllianceHandler.NotifyCharacterConnectionAsync(characterId);
            }
            catch (Exception ex) { Program.LogDebug($"[Character world] {characterId}'s guild and alliance not told: {ex.Message}"); }
        });
    }

    public async Task SaveAndRemoveActorAsync(ICharacterSession master, bool reloadMap)
    {
        var session = (GameSession)master;
        using var context = SessionContext.Push(session);
        DatabaseManager.SaveCurrentCharacter();
        await MapPresenceService.Live.RemoveAsync(session.Map, session.CharacterId, reloadMap ? session.Id : Guid.Empty);
    }

    public async Task<bool> SwitchCharacterAsync(ICharacterSession master, long characterId, CharacterPosition position, bool reload)
    {
        var session = (GameSession)master;
        using var context = SessionContext.Push(session);
        var backend = new GameEntryBackend();
        long formerId = session.CharacterId;
        if (!backend.SelectCharacter(session, characterId)) return false;

        var state = session.State;
        // Where the controller stood: use the target position supplied by the caller.
        state.MapId = position.MapId;
        state.CellId = position.CellId;
        state.Orientation = position.Orientation;
        var character = backend.FindCharacter(characterId);
        if (character == null) return false;
        backend.LoadCharacterState(session, character);
        DatabaseManager.SaveCurrentCharacter();

        if (!reload) ZaapDiscoveries.Discover(characterId, position.MapId);
        await backend.SendInitialStateAsync(session, character);
        await backend.AnnounceMapPresenceAsync(session, character);
        // The map is not loaded again: the actors on it are only
        // told anew -- the one played before gone, the one played now drawn where he stood --,
        // which is what makes a switch quick. The whole map again when the setting asks for it.
        if (reload) await backend.SendMapInitializationAsync(session, character);
        else
            await session.SendAsync(MapActorMessages.Refreshed(character, position.CellId, position.Orientation, session.AccountId));
        // His look told as a change of gear tells it: an inventory left open kept drawing the
        // one played before over the new one's bag.
        await session.SendAsync(ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character)));
        // His journal, the quests he follows, the NPCs' marks his quests put on this map and his
        // achievements: the map's block sends them, and with no map loaded again the client kept
        // showing the journal and the achievements of the one played before until the next login.
        if (!reload)
        {
            await Quests.SendSwitchedJournalAsync(session, formerId);
            await Quests.SendMarksAsync(session, position.MapId);
            await Achievements.SendListAsync(session);
        }

        // The wardrobe's draft belongs to the one played before. His wardrobe, spells and bar
        // come with the map's block; with no map loaded again they are sent here, or the
        // client keeps the former character's outfits and spells. His look went just above.
        session.State.IsWardrobeDraftLoaded = false;
        if (!reload)
        {
            await WardrobeStateService.Live.SendOwnedAsync(session);
            await session.SendAsync(SpellMessages.Live.Snapshot(session));
            await ShortcutBarMessages.Live.SendSpellAsync(session);
        }
        return true;
    }

    public int? PartyOf(long characterId) => Parties.Of(characterId)?.Id;
    public bool IsInParty(long characterId) => Parties.IsInParty(characterId);
    public Task AddPartyMemberAsync(ICharacterSession master, long followerId, int? partyId)
        => PartyHandler.AddMemberAsync((GameSession)master, followerId, partyId);
    public Task RemovePartyMemberAsync(long characterId) => PartyHandler.RemoveMemberAsync(characterId);
    public (int Id, long Leader)? JoinedParty(long characterId)
        => Parties.IsInParty(characterId) && Parties.Of(characterId) is { } party ? (party.Id, party.LeaderId) : null;
    public Task PromotePartyLeaderAsync(ICharacterSession session, int partyId)
        => PartyHandler.PromoteAndNotifyAsync((GameSession)session, partyId);
    public Task SendPartyAsync(ICharacterSession session, int partyId)
        => PartyHandler.SendPartyAsync((GameSession)session, partyId);

    public IReadOnlyCollection<int>? WalkableCells(long mapId)
        => MapManager.WalkableCells.GetValueOrDefault(mapId);
    public int CellDistance(int from, int to) => MapGeometry.Distance(from, to);
    public IReadOnlyList<int> FindPath(int from, int to, HashSet<int> walkable)
        => MapGeometry.FindShortestPath(from, to, walkable);

    public async Task DrawFollowerAsync(ICharacterSession master, ICharacterSession follower)
    {
        var position = follower.Position;
        var character = DatabaseManager.GetCharacterById(follower.CharacterId);
        if (character == null || follower.IsInFight || master.IsInFight) return;
        try
        {
            await SessionRegistry.BroadcastToMapAsync(position.MapId,
                MapActorMessages.Refreshed(character, position.CellId, position.Orientation, master.AccountId), Guid.Empty);
        }
        catch (Exception ex) { Program.LogDebug($"[Character world] {follower.CharacterId} could not be drawn: {ex.Message}"); }
    }

    public async Task RemoveActorAsync(long mapId, long characterId)
    {
        if (mapId <= 0) return;
        try { await MapPresenceService.Live.RemoveAsync(SessionRegistry.OnMap(mapId), characterId); }
        catch (Exception ex) { Program.LogDebug($"[Character world] {characterId} could not be taken off map {mapId}: {ex.Message}"); }
    }

    public Task WalkFollowerAsync(ICharacterSession master, long characterId, IReadOnlyList<int> path)
        => ((GameSession)master).Map.SendAsync(new GameMapMovementEvent(path, characterId, false));

    public void DiscoverZaap(long characterId, long mapId) => ZaapDiscoveries.Discover(characterId, mapId);

    public Task UpdateAchievementsAsync(ICharacterSession master, ICharacterSession follower)
    {
        long map = master.Position.MapId;
        return Achievements.ForCompanionAsync((GameSession)master, (GameSession)follower, map,
            MapManager.GetMapInfo(map)?.SubAreaId ?? 0);
    }
}
