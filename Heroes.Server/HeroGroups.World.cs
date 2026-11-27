using Jondo.Unity.ServerMods;

namespace Heroes.Server;

internal sealed partial class HeroGroups
{
    public async Task<string> SwitchAsync(ICharacterSession master, long heroId)
    {
        using var turn = await master.EnterTurnAsync();
        long formerId = master.CharacterId;
        if (!master.IsInWorld || Find(heroId) is not { } hero || hero.ControllerId != formerId)
            return "hero";
        if (master.IsInFight || hero.Session.IsInFight) return "fight";
        if (!companions.OwnsCharacter(master, heroId)) return "account";

        var group = Of(formerId);
        long lootTo = LootTo(formerId);
        var position = master.Position;
        bool reload = settings.ReloadMapOnSwitch;
        long[] held = parties.IsInParty(formerId)
            ? group.Select(h => h.CharacterId).Prepend(formerId).ToArray()
            : [];
        lock (_gate) _partyHeld.UnionWith(held);
        int place = -1;
        try
        {
            await companions.SaveAndRemoveActorAsync(master, reload);
            place = HandOver(formerId, heroId);
            if (place < 0 || !await companions.SwitchCharacterAsync(master, heroId, position, reload))
            {
                if (place >= 0 && master.CharacterId == formerId)
                {
                    lock (_gate)
                        if (_byMaster.TryGetValue(heroId, out var remaining)) Reassign(heroId, formerId, remaining);
                    if (Connect(master, heroId) == "ok")
                    {
                        PlaceAt(formerId, heroId, group.Count - 1 - place);
                        SetAutoPass(formerId, heroId, hero.AutoPass);
                    }
                    if (lootTo != 0) SetLootTo(formerId, lootTo);
                }
                await movement.DrawFollowerAsync(master, master);
                return "hero";
            }

            return "ok";
        }
        finally
        {
            try
            {
                // Complete the group handover even if sending initial state to the client fails.
                if (place >= 0 && master.CharacterId == heroId)
                {
                    if (Connect(master, formerId) == "ok") PlaceAt(heroId, formerId, place);
                    if (lootTo != 0) SetLootTo(heroId, lootTo);
                }
            }
            finally { lock (_gate) _partyHeld.ExceptWith(held); }
            await RefreshPartyAfterSwitchAsync(master, formerId, held);
        }
    }

    private int HandOver(long masterId, long heroId)
    {
        Member taken;
        int place;
        lock (_gate)
        {
            if (_transitioning.Contains(heroId) || !_byHero.TryGetValue(heroId, out var hero)
                || hero.ControllerId != masterId) return -1;
            taken = hero;
            var list = _byMaster[masterId];
            int at = list.IndexOf(taken);
            var others = list.Skip(at + 1).Concat(list.Take(at)).ToList();
            place = list.Count - 1 - at;
            _lootTo.Remove(masterId);
            _byHero.Remove(heroId);
            _transitioning.Add(heroId);
            Reassign(masterId, heroId, others);
        }

        SaveDetached(taken);
        return place;
    }

    private void Reassign(long from, long to, List<Member> members)
    {
        _byMaster.Remove(from);
        foreach (var other in members) other.ControllerId = to;
        if (members.Count > 0) _byMaster[to] = members;
    }

    private void PlaceAt(long masterId, long heroId, int place)
    {
        lock (_gate)
        {
            if (!_byHero.TryGetValue(heroId, out var hero) || hero.ControllerId != masterId) return;
            var list = _byMaster[masterId];
            list.Remove(hero);
            list.Insert(Math.Clamp(place, 0, list.Count), hero);
        }
    }

    public long PartyInviteRecipient(string characterName) => Named(characterName)?.ControllerId ?? 0;

    public bool OwnsParty(long characterId, IReadOnlyList<long> members)
        => members.All(member => member == characterId || Find(member)?.ControllerId == characterId);

    public bool ReleaseKickedFollower(long leaderId, long memberId)
    {
        if (Find(memberId)?.ControllerId != leaderId) return false;
        if (Disconnect(leaderId, memberId))
            log($"{leaderId} lets {memberId} go from the party.");
        return true;
    }

    public bool CanLeadParty(long characterId) => Find(characterId) == null;
    public long ChoosePartyLeader(IReadOnlyList<long> members) => members.FirstOrDefault(CanLeadParty, members[0]);
    public bool HasPartyLeader(IReadOnlyList<long> members) => members.Any(CanLeadParty);

    public (long Target, ICharacterSession? AutoAcceptingSession) ResolveTradeTarget(long requester, long target)
    {
        var hero = Find(target);
        if (hero == null) return (target, null);
        if (hero.ControllerId == requester) return (target, hero.Session);
        log($"{requester} asks {target} to trade. The request goes to {hero.ControllerId}.");
        return (hero.ControllerId, null);
    }

    private async Task RefreshPartyAfterSwitchAsync(ICharacterSession session, long formerId, IReadOnlyCollection<long> held)
    {
        long current = session.CharacterId;
        foreach (long id in held)
            if (id != current && Find(id) == null && parties.PartyOf(id) != null)
                await parties.RemovePartyMemberAsync(id);

        if (parties.JoinedParty(current) is { } party)
        {
            if (party.Leader == formerId || !CanLeadParty(party.Leader))
                await parties.PromotePartyLeaderAsync(session, party.Id);
            await parties.SendPartyAsync(session, party.Id);
        }
        await JoinPartyAsync(session);
    }

    public IEnumerable<ICompanion> VisibleFollowers(ICharacterSession master, long mapId)
    {
        if (master.CharacterId <= 0 || master.IsInFight) yield break;
        foreach (var hero in Of(master.CharacterId))
            if (!hero.Session.IsInFight && hero.Session.Position.MapId == mapId)
                yield return hero;
    }

    public async Task LeavePartyAsync(int partyId, long masterId)
    {
        foreach (var hero in Of(masterId))
            if (parties.PartyOf(hero.CharacterId) == partyId)
                await parties.RemovePartyMemberAsync(hero.CharacterId);
    }

    public async Task JoinPartyAsync(ICharacterSession master)
    {
        foreach (var hero in Of(master.CharacterId))
        {
            int? party = parties.PartyOf(hero.CharacterId);
            if (party == null || party != parties.PartyOf(master.CharacterId))
            {
                if (party != null) await parties.RemovePartyMemberAsync(hero.CharacterId);
                await AddFollowerToPartyAsync(master, hero.CharacterId);
            }
        }
    }

    private Task AddFollowerToPartyAsync(ICharacterSession master, long followerId)
    {
        if (parties.PartyOf(followerId) != null) return Task.CompletedTask;
        var party = parties.JoinedParty(master.CharacterId);
        // Followers wait while the master has an unanswered invitation.
        return party == null && parties.PartyOf(master.CharacterId) != null
            ? Task.CompletedTask : parties.AddPartyMemberAsync(master, followerId, party?.Id);
    }

    private async Task ArriveAsync(ICharacterSession master, ICompanion hero)
    {
        var position = master.Position;
        var cells = movement.WalkableCells(position.MapId);
        if (cells != null)
        {
            var taken = Of(master.CharacterId)
                .Where(h => h.CharacterId != hero.CharacterId && h.Session.Position.MapId == position.MapId)
                .Select(h => h.Session.Position.CellId).ToHashSet();
            taken.Add(position.CellId);
            position = position with { CellId = NearestFree(position.CellId, cells, taken) };
        }
        hero.Session.Position = position;
        movement.DiscoverZaap(hero.CharacterId, position.MapId);
        await movement.DrawFollowerAsync(master, hero.Session);
    }

    public Task MasterMovedAsync(ICharacterSession master, long mapLeft)
        => MoveFollowersAsync(master, mapLeft, Of(master.CharacterId).Where(hero => !hero.Session.IsInFight));

    private async Task MoveFollowersAsync(ICharacterSession master, long mapLeft, IEnumerable<ICompanion> followers)
    {
        foreach (var hero in followers)
        {
            if (mapLeft > 0 && mapLeft != master.Position.MapId)
                await movement.RemoveActorAsync(mapLeft, hero.CharacterId);
            await ArriveAsync(master, hero);
            try { await movement.UpdateAchievementsAsync(master, hero.Session); }
            catch (Exception ex) { log($"{hero.CharacterId}'s achievements could not be updated: {ex.Message}"); }
        }
    }

    public async Task MasterWalkedAsync(ICharacterSession master, IReadOnlyList<int> cells)
    {
        if (cells.Count == 0) return;
        var position = master.Position;
        var cellsOfMap = movement.WalkableCells(position.MapId);
        if (cellsOfMap == null) return;
        var walkable = new HashSet<int>(cellsOfMap);
        int end = cells[^1];
        var taken = new HashSet<int> { end };
        foreach (var hero in Of(master.CharacterId))
        {
            var current = hero.Session.Position;
            if (hero.Session.IsInFight || current.MapId != position.MapId) continue;
            int target = NearestFree(end, walkable, taken);
            taken.Add(target);
            if (target == current.CellId) continue;
            var path = movement.FindPath(current.CellId, target, walkable);
            if (path.Count < 2) path = [current.CellId, target];
            hero.Session.Position = position with { CellId = target };
            try { await movement.WalkFollowerAsync(master, hero.CharacterId, path); }
            catch (Exception ex) { log($"{hero.CharacterId} could not follow: {ex.Message}"); }
        }
    }

    private int NearestFree(int cell, IEnumerable<int> walkable, HashSet<int> taken)
    {
        int best = cell, bestDistance = int.MaxValue;
        foreach (int candidate in walkable)
        {
            if (taken.Contains(candidate)) continue;
            int distance = movement.CellDistance(cell, candidate);
            if (distance < 1 || distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }
}
