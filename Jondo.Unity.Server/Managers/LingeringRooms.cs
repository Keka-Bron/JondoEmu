using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The rooms of a dungeon that must not be lingered in (content/world/dungeons.json
    /// «lingering»): whoever comes in is warned with a curse of the dungeon's, and whoever is still
    /// in them after a while gets a worse one, which stays when he leaves. The Dragon Pig's Den's
    /// first two rooms: «Efluvios nauseabundos» on coming in -- «Si respiras mucho tiempo los gases
    /// de esta sala, sufrirás las consecuencias» --, then «Gas mefistofélico» and «Gas mefistofélico
    /// potente», -3 and -5 range and -30 and -50 AP dodge for four fights. «Il vous faut traverser
    /// ces deux premières salles très rapidement sinon vous obtiendrez une malédiction assez
    /// gênante : Gaz Méphitique» (Dofus pour les noobs, «Antre du Dragon Cochon»).
    /// </summary>
    /// <remarks>
    /// Checked on every map the character loads and on the heartbeat the client sends every five
    /// seconds (AlterationHandler.TickAsync). The warning goes when he leaves the rooms; a curse he
    /// was given stays, and counts its fights down as any other.
    /// </remarks>
    public static class LingeringRooms
    {
        /// <summary>A dungeon's rooms not to linger in, its warning, and its curses by time.</summary>
        public sealed class Rule
        {
            public HashSet<long> Rooms { get; } = new();
            public int Warning { get; set; }

            /// <summary>The curses by the seconds spent in the rooms, the longest last.</summary>
            public List<(int Seconds, int Item)> Steps { get; } = new();
        }

        /// <summary>Since when each character is in a dungeon's rooms, and how many steps he has been given.</summary>
        private static readonly ConcurrentDictionary<long, (int Dungeon, DateTime Since, int Step)> _inside = new();

        /// <summary>The rule of the dungeon whose rooms these are, if any.</summary>
        internal static (int Dungeon, Rule Rule)? RuleOf(long mapId)
        {
            if (!DungeonManager.IsLoaded) return null;
            foreach (var dungeon in DungeonManager.All.Values)
                if (dungeon.Lingering is { } rule && rule.Rooms.Contains(mapId)) return (dungeon.Id, rule);
            return null;
        }

        /// <summary>
        /// The step reached after so many seconds: 0 none, 1 the first curse, and so on.
        /// </summary>
        internal static int StepAfter(Rule rule, double seconds)
            => rule.Steps.Count(s => seconds >= s.Seconds);

        /// <summary>A map loaded: coming into the rooms warns, leaving them takes the warning back.</summary>
        public static async Task OnMapEnteredAsync(NetworkStream stream, long characterId, long mapId)
        {
            if (characterId == 0) return;
            if (RuleOf(mapId) is not { } found)
            {
                if (_inside.TryRemove(characterId, out var left) && DungeonManager.Get(left.Dungeon)?.Lingering is { } was)
                    await TakeIfHeldAsync(stream, was.Warning);
                return;
            }

            if (_inside.TryGetValue(characterId, out var there) && there.Dungeon == found.Dungeon)
            {
                await CheckAsync(stream, characterId, mapId);
                return;
            }
            _inside[characterId] = (found.Dungeon, DateTime.UtcNow, 0);
            if (found.Rule.Warning != 0 && Equipment.HowMany(found.Rule.Warning) == 0)
            {
                await QuestFollowers.GiveAsync(stream, found.Rule.Warning, 1);
                Console.WriteLine($"[Dungeon] {characterId} comes into the rooms of dungeon {found.Dungeon} not to linger in: " +
                                  $"warned with {found.Rule.Warning}.");
            }
        }

        /// <summary>The heartbeat: still in the rooms, the curse that the time spent in them has earned.</summary>
        public static async Task CheckAsync(NetworkStream stream, long characterId, long mapId)
        {
            if (!_inside.TryGetValue(characterId, out var there)) return;
            if (RuleOf(mapId) is not { } found || found.Dungeon != there.Dungeon)
            {
                await OnMapEnteredAsync(stream, characterId, mapId);
                return;
            }

            int step = StepAfter(found.Rule, (DateTime.UtcNow - there.Since).TotalSeconds);
            if (step <= there.Step) return;
            _inside[characterId] = there with { Step = step };

            await TakeIfHeldAsync(stream, found.Rule.Warning);
            foreach (var (_, earlier) in found.Rule.Steps.Take(step - 1)) await TakeIfHeldAsync(stream, earlier);
            int curse = found.Rule.Steps[step - 1].Item;
            if (Equipment.HowMany(curse) == 0) await QuestFollowers.GiveAsync(stream, curse, 1);
            Console.WriteLine($"[Dungeon] {characterId} lingered in the rooms of dungeon {found.Dungeon}: cursed with {curse}.");
        }

        private static async Task TakeIfHeldAsync(NetworkStream stream, int template)
        {
            if (template != 0 && Equipment.HowMany(template) > 0) await QuestFollowers.TakeAsync(stream, template, 1);
        }

        /// <summary>For the tests: forget who is where.</summary>
        internal static void Forget() => _inside.Clear();
    }
}
