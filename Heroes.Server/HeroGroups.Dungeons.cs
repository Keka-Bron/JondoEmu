using Jondo.Unity.ServerMods;

namespace Heroes.Server;

internal sealed partial class HeroGroups
{
    public bool ContainsFollower(ICompanion follower) => Find(follower.CharacterId) == follower;

    public bool CanResumeDungeon(IReadOnlyList<ICompanion> followers, long room, Func<long, long> savedRoom)
        => followers.All(hero => savedRoom(hero.CharacterId) == room);

    public IReadOnlyList<ICompanion> DungeonFollowers(ICharacterSession master)
        => Of(master.CharacterId).Where(hero => !hero.Session.IsInFight).ToArray();

    public DungeonAdmissionPlan PlanDungeonAdmission(DungeonAdmissionRequest request)
    {
        var followers = request.Followers;
        if (!request.RequiresKey)
            return new(followers.Select(hero => new DungeonFollowerAdmission(hero.CharacterId, DungeonFollowerEntry.Free)).ToArray());

        var spare = new Dictionary<long, int> { [request.LeaderId] = request.LeaderKeys };
        foreach (var hero in followers) spare.Add(hero.CharacterId, hero.Keys);

        var ways = new Dictionary<long, DungeonFollowerAdmission>();
        var waiting = new List<DungeonFollowerOptions>();
        foreach (var hero in followers.OrderBy(hero => hero.WantsKey))
        {
            long id = hero.CharacterId;
            if (hero.HasKeyring && !hero.WantsKey)
                ways.Add(id, new(id, DungeonFollowerEntry.Keyring));
            else if (spare[id] > 0)
            {
                spare[id]--;
                ways.Add(id, new(id, DungeonFollowerEntry.Key, id));
            }
            else
                waiting.Add(hero);
        }

        // Reserve everyone's own entry before lending keys. The master lends first.
        var payers = followers.Select(hero => hero.CharacterId).Prepend(request.LeaderId).ToArray();
        int missing = 0;
        foreach (var hero in waiting)
        {
            long id = hero.CharacterId;
            long payer = payers.FirstOrDefault(id => spare[id] > 0);
            if (payer != 0)
            {
                spare[payer]--;
                ways.Add(id, new(id, DungeonFollowerEntry.Key, payer));
            }
            else if (hero.HasKeyring)
                ways.Add(id, new(id, DungeonFollowerEntry.Keyring));
            else
                missing++;
        }
        return missing == 0 ? new(followers.Select(hero => ways[hero.CharacterId]).ToArray()) : new([], missing);
    }

    public Task MoveDungeonFollowersAsync(ICharacterSession master, long mapLeft, IReadOnlyList<ICompanion> admitted)
        => MoveFollowersAsync(master, mapLeft, admitted);
}
