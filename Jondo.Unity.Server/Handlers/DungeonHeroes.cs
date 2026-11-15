using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The master's heroes at a dungeon's door (Managers.Heroes): they follow him in
    /// (HeroPresence.MasterMovedAsync), so each of them needs a way in as he would alone.
    /// </summary>
    /// <remarks>
    /// A hero goes in by his own keyring while this week's free entry is his, else by a key: his
    /// own first, then one of his master's, then a spare one of another hero's. All or nothing:
    /// when the group's bags do not hold a way in for every one of them, nobody goes in, nothing
    /// is spent, and the master is told how many keys are missing.
    /// </remarks>
    public static partial class DungeonHandler
    {
        /// <summary>One of the group as the planning sees him: his keyring usable here, and how many of the dungeon's keys he has.</summary>
        internal readonly record struct Member(long Id, bool Keyring, int Keys);

        /// <summary>How a hero goes in: by his keyring, or by one key out of someone's bag.</summary>
        internal readonly record struct HeroWay(Heroes.Hero Hero, bool Keyring, long PaidBy);

        /// <summary>
        /// Whose bag pays each hero's way in -- zero for his keyring --, or null when the group is
        /// short, with how many keys it is short of. <paramref name="master"/>'s keys are those left
        /// once his own way in is set aside.
        /// </summary>
        internal static Dictionary<long, long>? PlanForHeroes(Member master, IReadOnlyList<Member> heroes, out int missing)
        {
            var spare = new Dictionary<long, int> { [master.Id] = master.Keys };
            foreach (var hero in heroes) spare[hero.Id] = hero.Keys;

            var plan = new Dictionary<long, long>();
            var waiting = new List<long>();
            foreach (var hero in heroes)
            {
                if (hero.Keyring) plan[hero.Id] = 0;
                else if (spare[hero.Id] > 0) { spare[hero.Id]--; plan[hero.Id] = hero.Id; }
                else waiting.Add(hero.Id);
            }

            // The master's spares first, as the mules are his; then whatever another hero carries over.
            var payers = new List<long> { master.Id };
            payers.AddRange(heroes.Select(h => h.Id));
            missing = 0;
            foreach (long hero in waiting)
            {
                long payer = payers.FirstOrDefault(p => spare[p] > 0, 0);
                if (payer == 0) { missing++; continue; }
                spare[payer]--;
                plan[hero] = payer;
            }
            return missing == 0 ? plan : null;
        }

        /// <summary>The heroes who will follow the master into the dungeon: his, out of any fight.</summary>
        private static List<Heroes.Hero> Following(long masterId)
            => Heroes.On ? Heroes.Of(masterId).Where(h => !h.Session.State.IsInFight).ToList() : new List<Heroes.Hero>();

        /// <summary>How many of the dungeon's keys the bag of the session in context holds.</summary>
        private static int KeysInBag(DungeonManager.Dungeon dungeon)
            => dungeon.Required.Select(r => r.Item).Distinct().Sum(Equipment.HowMany);

        /// <summary>
        /// A way in for each hero following the master, or null when the group is short -- the
        /// master told so, in red, and nothing spent. <paramref name="masterSpends"/>: whether the
        /// master's own way in takes one of his keys.
        /// </summary>
        private static async Task<List<HeroWay>?> WaysForHeroesAsync(NetworkStream stream, DungeonManager.Dungeon dungeon,
                                                                     bool masterSpends, DateTime now)
        {
            var state = SessionContext.State;
            var heroes = Following(state.CharacterId);
            if (heroes.Count == 0) return new List<HeroWay>();

            // A door that asks for no key lets them all in as it lets him.
            if (dungeon.Required.Count == 0)
                return heroes.Select(h => new HeroWay(h, false, 0)).ToList();

            var master = new Member(state.CharacterId, false, Math.Max(0, KeysInBag(dungeon) - (masterSpends ? 1 : 0)));
            var members = new List<Member>();
            foreach (var hero in heroes)
            {
                using (SessionContext.Push(hero.Session))
                {
                    bool keyring = dungeon.OnKeyring && FindInBag(Keyring, 1) != 0
                                   && DungeonKeyring.FreeEntryLeft(hero.CharacterId, dungeon.Id, now);
                    members.Add(new Member(hero.CharacterId, keyring, KeysInBag(dungeon)));
                }
            }

            var plan = PlanForHeroes(master, members, out int missing);
            if (plan == null)
            {
                Console.WriteLine($"[Dungeon] {dungeon.Name}: {state.CharacterName}'s {heroes.Count} hero(es) are " +
                                  $"{missing} key(s) short ({Wanted(dungeon)}). Nobody goes in.");
                await TellAsync(stream, CommandTexts.Get("dungeon.heroes_missing_keys", missing));
                return null;
            }
            return heroes.Select(h => new HeroWay(h, plan[h.CharacterId] == 0, plan[h.CharacterId])).ToList();
        }

        /// <summary>
        /// What lets each hero in, spent: his keyring's free entry, or a key out of the bag the
        /// plan named -- the master's told to his client, a hero's straight off his bag, who has
        /// no client. False when a key the plan counted on is gone by now.
        /// </summary>
        private static async Task<bool> SpendForHeroesAsync(NetworkStream stream, DungeonManager.Dungeon dungeon,
                                                            List<HeroWay> ways, DateTime now)
        {
            long masterId = SessionContext.State.CharacterId;
            foreach (var way in ways)
            {
                if (way.Keyring)
                {
                    DungeonKeyring.SpendFreeEntry(way.Hero.CharacterId, dungeon.Id, now);
                    continue;
                }
                if (way.PaidBy == 0) continue;

                if (way.PaidBy == masterId)
                {
                    if (!TryTakeTheKey(dungeon, EntryMethod.Key, false, out long uid, out _) || uid == 0
                        || !await ItemUseHandler.SpendOneAsync(stream, uid, weigh: false))
                        return false;
                }
                else
                {
                    var payer = Heroes.AsHero(way.PaidBy);
                    if (payer == null) return false;
                    using (SessionContext.Push(payer.Session))
                    {
                        if (!TryTakeTheKey(dungeon, EntryMethod.Key, false, out long uid, out _) || uid == 0
                            || !DatabaseManager.DestroyCharacterItem(payer.CharacterId, uid, 1))
                            return false;
                        Equipment.Remove(uid, 1);
                    }
                }
                Console.WriteLine($"[Dungeon] {dungeon.Name}: {way.Hero.Session.State.CharacterName} goes in on a key of {way.PaidBy}.");
            }
            return true;
        }
    }
}
