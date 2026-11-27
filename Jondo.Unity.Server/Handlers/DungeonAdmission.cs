using Jondo.Unity.Server.Mods;
using System.Net.Sockets;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Handlers;

public static partial class DungeonHandler
{
    private sealed record Admission(IReadOnlyList<ICompanion>? Followers, IReadOnlySet<long> KeyedFollowers)
    {
        public IEnumerable<long> FollowerIds => Followers?.Select(companion => companion.CharacterId) ?? [];

        public Task<int> MoveAsync(long room, int cell = TeleportHandler.MapCentre)
            => Followers is { Count: > 0 }
                ? TeleportHandler.MoveToMapAsync(SessionContext.Current, room, cell, Followers)
                : TeleportHandler.ToMapAsync(SessionContext.Current, room, cell, Followers);
    }

    // The caller holds the master's session turn. Take inventory snapshots, then validate and
    // commit the complete bill before sending output or moving any character.
    private static async Task<Admission?> CommitAdmissionAsync(NetworkStream stream, DungeonManager.Dungeon dungeon,
        long room, long masterKey, bool masterKeyring, int guardian = 0, bool weigh = false,
        bool warnOnFailure = true, int? itemCell = null)
    {
        var master = SessionContext.Current;
        var gameplay = ServerModHooks.Get<IDungeonPartyPolicy>();
        IReadOnlyList<ICompanion>? followers = gameplay?.DungeonFollowers(master).ToArray();
        if (gameplay == null || followers is not { Count: > 0 })
        {
            if (itemCell is int cell && await TeleportHandler.ToMapAsync(master, room, cell, followers) < 0)
                return null;
            if (masterKey != 0 && !await ItemUseHandler.SpendOneAsync(master, masterKey, weigh))
            {
                if (itemCell != null)
                    Console.WriteLine($"[Dungeon] {dungeon.Name}: the item that led in could not be spent.");
                else
                {
                    if (warnOnFailure) await TellAsync(stream, CommandTexts.Get("dungeon.missing_item"));
                    return null;
                }
            }
            if (masterKeyring) DungeonKeyring.SpendFreeEntry(master.CharacterId, dungeon.Id, DateTime.Now);
            return new(followers, new HashSet<long>());
        }

        if (!await TeleportHandler.CanTeleportAsync(master, room)) return null;
        var now = DateTime.Now;
        var minimumByTemplate = dungeon.Required.GroupBy(requirement => requirement.Item)
            .ToDictionary(group => group.Key, group => Math.Max(1, group.Min(requirement => requirement.Count)));
        var inventories = new Dictionary<long, AdmissionInventory>();
        var leader = new AdmissionInventory(master, dungeon, minimumByTemplate, now, guardian: 0);
        inventories.Add(master.CharacterId, leader);
        var members = new List<DungeonFollowerOptions>();
        bool reserved = masterKey == 0 || leader.Reserve(masterKey);
        foreach (var follower in followers)
        {
            if (follower.Session is not GameSession session || !MatchesController(follower, session, master)
                || inventories.ContainsKey(follower.CharacterId))
                return null;
            var inventory = new AdmissionInventory(session, dungeon, minimumByTemplate, now, guardian);
            inventories.Add(session.CharacterId, inventory);
            members.Add(new(session.CharacterId, inventory.Keys, inventory.HasKeyring, inventory.WantsKey));
        }

        bool requiresKey = dungeon.Required.Count != 0;
        var plan = gameplay.PlanDungeonAdmission(new(master.CharacterId, leader.Keys, requiresKey, members.AsReadOnly()));
        if (plan.MissingKeys > 0)
        {
            await TellAsync(stream, CommandTexts.Get("dungeon.party_missing_keys", plan.MissingKeys));
            return null;
        }

        var keyrings = new List<long>();
        var keyed = new HashSet<long>();
        if (masterKeyring) keyrings.Add(master.CharacterId);
        if (reserved && (!masterKeyring || leader.HasKeyring) && plan.MissingKeys == 0
            && TryReserveFollowers(plan.Followers, requiresKey, master.CharacterId, inventories, keyrings, keyed)
            && ServerModHooks.Get<IDungeonPartyPolicy>() == gameplay
            && followers.All(follower => inventories.TryGetValue(follower.CharacterId, out var inventory)
                && MatchesController(follower, inventory.Session, master)
                && gameplay.ContainsFollower(follower)))
        {
            var bills = inventories.Values.SelectMany(inventory => inventory.Debits.Select(debit =>
                (inventory.Session.CharacterId, Uid: debit.Key, Quantity: debit.Value))).ToArray();
            if (DatabaseManager.SpendDungeonAdmission(dungeon.Id, now, bills, keyrings))
            {
                foreach (var bill in bills)
                {
                    using var context = SessionContext.Push(inventories[bill.CharacterId].Session);
                    Equipment.Remove(bill.Uid, bill.Quantity);
                }
                foreach (var debit in leader.Debits)
                {
                    var item = leader.Items[debit.Key];
                    await ItemUseHandler.ReportSpendAsync(master, item.Template, item.Uid,
                        item.Quantity - debit.Value, debit.Value, weigh);
                }
                return new(followers, keyed);
            }
        }

        if (warnOnFailure) await TellAsync(stream, CommandTexts.Get("dungeon.missing_item"));
        return null;
    }

    private static bool MatchesController(ICompanion follower, GameSession session, GameSession master)
        => ReferenceEquals(follower.Session, session) && follower.ControllerId == master.CharacterId
            && session.CharacterId == follower.CharacterId && session.AccountId == master.AccountId
            && !session.HasTransport && !session.State.IsInFight;

    private static bool TryReserveFollowers(IReadOnlyList<DungeonFollowerAdmission> ways, bool requiresKey,
        long masterId, Dictionary<long, AdmissionInventory> inventories, List<long> keyrings, HashSet<long> keyed)
    {
        if (ways.Count != inventories.Count - 1) return false;
        var seen = new HashSet<long>();
        foreach (var way in ways)
        {
            if (way.CharacterId == masterId || !seen.Add(way.CharacterId)
                || !inventories.TryGetValue(way.CharacterId, out var follower))
                return false;
            switch (way.Entry)
            {
                case DungeonFollowerEntry.Free when !requiresKey && way.PayerId == 0:
                    break;
                case DungeonFollowerEntry.Keyring when requiresKey && way.PayerId == 0 && follower.HasKeyring:
                    keyrings.Add(way.CharacterId);
                    break;
                case DungeonFollowerEntry.Key when requiresKey && inventories.TryGetValue(way.PayerId, out var payer)
                    && payer.ReserveKey():
                    keyed.Add(way.CharacterId);
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private sealed class AdmissionInventory
    {
        private readonly DungeonManager.Dungeon _dungeon;
        private readonly IReadOnlyDictionary<int, int> _minimumByTemplate;
        public GameSession Session { get; }
        public Dictionary<long, (long Uid, int Template, int Quantity)> Items { get; }
        public Dictionary<long, int> Debits { get; } = new();
        public bool HasKeyring { get; }
        public bool WantsKey { get; }

        public AdmissionInventory(GameSession session, DungeonManager.Dungeon dungeon,
            IReadOnlyDictionary<int, int> minimumByTemplate, DateTime now, int guardian)
        {
            Session = session;
            _dungeon = dungeon;
            _minimumByTemplate = minimumByTemplate;
            using var context = SessionContext.Push(session);
            Items = Equipment.All.Where(item => _minimumByTemplate.ContainsKey(item.Template))
                .ToDictionary(item => item.Uid, item => (item.Uid, item.Template, item.Quantity));
            HasKeyring = dungeon.OnKeyring && FindInBag(Keyring, 1) != 0
                && DungeonKeyring.FreeEntryLeft(session.CharacterId, dungeon.Id, now);
            WantsKey = guardian != 0 && dungeon.Required.Any(r => Quests.AwaitsKey(guardian, r.Item));
        }

        public int Keys => Items.Values.Where(item => _minimumByTemplate.ContainsKey(item.Template))
            .Sum(item => Math.Max(0, Remaining(item.Uid) - _minimumByTemplate[item.Template] + 1));

        private int Remaining(long uid) => Items[uid].Quantity - Debits.GetValueOrDefault(uid);

        public bool Reserve(long uid)
        {
            if (!Items.ContainsKey(uid))
            {
                // The master's teleport item can be distinct from the door's loose keys.
                using var context = SessionContext.Push(Session);
                if (Equipment.ByUid(uid) is not { } item) return false;
                Items.Add(uid, (uid, item.Template, item.Quantity));
            }
            if (Remaining(uid) <= 0) return false;
            Debits[uid] = Debits.GetValueOrDefault(uid) + 1;
            return true;
        }

        public bool ReserveKey()
        {
            foreach (var (template, minimum) in _dungeon.Required)
                foreach (var item in Items.Values)
                    if (item.Template == template && Remaining(item.Uid) >= Math.Max(1, minimum))
                        return Reserve(item.Uid);
            return false;
        }
    }
}
