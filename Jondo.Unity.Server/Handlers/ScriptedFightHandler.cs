using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The fights written in npc_fights.json, started the same way whatever starts them: an NPC's
    /// reply, or an element of the map used (a row with «element»). A row may chain to the next
    /// fight («then»): the Xelorium's old chest ambushes whoever opens it, first in waves and then,
    /// once those are beaten, again with a Krosmoglob at his side. The next fight starts when the
    /// character is back on the map after winning; a lost fight ends the chain, and the element
    /// starts it over.
    /// </summary>
    public static class ScriptedFightHandler
    {
        /// <summary>A chained fight waiting for the one before it to end, per character.</summary>
        private sealed record Pending(long FightId, NpcFights.Fight Next, long MapId, bool Ready);

        private static readonly ConcurrentDictionary<long, Pending> _pending = new();

        /// <summary>
        /// Starts <paramref name="fight"/> for the session's character on <paramref name="mapId"/>:
        /// the monsters (at his level when the row says so), what the fight is to carry (incarnation,
        /// waves, quest rules), and, when the fight did start and chains to another, the next one
        /// set aside for when this one is won. Whether a fight started.
        /// </summary>
        public static async Task<bool> StartAsync(NetworkStream stream, NpcFights.Fight fight, long mapId)
        {
            IReadOnlyList<(int Monster, int Grade)> AtHisLevel(IReadOnlyList<(int Monster, int Grade)> members) => fight.ByLevel
                ? members.Select(m => (m.Monster, MobSpawnManager.GradeForLevel(m.Monster, SessionContext.State.CharacterLevel))).ToList()
                : members;
            // His allies too: the bodyguard of «La bande des Toxines» has a grade every 20 levels.
            var group = MobSpawnManager.ComposeOffMap(AtHisLevel(fight.Monsters));
            if (group == null) return false;
            long characterId = GameState.CharacterId;
            // The monster he fights as: the Entrenador of «Barnabé en el espacio».
            if (fight.Incarnates != 0)
                FightHandler.IncarnateInNextFight(characterId, fight.Incarnates, fight.IncarnationGrade, fight.IncarnationInvulnerable);
            // And the waves that come in later (Dark Vlad's chafers), at his level as the first ones are.
            if (fight.Waves.Count > 0)
                FightHandler.WavesInNextFight(characterId, fight.ByLevel
                    ? fight.Waves.Select(w => w with { Monsters = AtHisLevel(w.Monsters) }).ToList()
                    : fight.Waves);
            // And what the quest adds to it: the Yokai Nana lent, Kuatinbe's Pandasiman.
            if (!fight.Rules.IsEmpty) FightHandler.RulesInNextFight(characterId, fight.Rules);
            long before = SessionContext.State.FightId;
            await FightHandler.InitiateFightFromMobCollision(stream, group, mapId,
                arenaMapId: fight.ArenaMapId, allies: MobSpawnManager.ComposeMembers(AtHisLevel(fight.Allies)),
                alliesMustLive: fight.AlliesMustLive,
                winMapId: fight.WinMapId, winCellId: fight.WinCellId, winMarks: fight.Marks,
                alliesPlayedBy: fight.AlliesPlayed ? characterId : 0,
                defeatFree: fight.DefeatFree, solo: fight.Solo,
                loseMapId: fight.LoseMapId, loseCellId: fight.LoseCellId,
                startCells: fight.Start);
            // Taken by the fight when it was built; one that did not start leaves none for the next.
            FightHandler.TakeNextIncarnation(characterId);
            FightHandler.TakeNextWaves(characterId);
            FightHandler.TakeNextRules(characterId);
            long started = SessionContext.State.FightId;
            if (started == 0 || started == before) return false;
            if (fight.Then != null)
                _pending[characterId] = new Pending(started, fight.Then, fight.WinMapId != 0 ? fight.WinMapId : mapId, false);
            else
                _pending.TryRemove(characterId, out _);
            return true;
        }

        /// <summary>
        /// An element of the map used: the fight it starts for this character (a row with
        /// «element», its criterion met and its chain not yet behind him). Whether it started one.
        /// </summary>
        public static async Task<bool> TryElementAsync(NetworkStream stream, long mapId, int elementId)
        {
            var used = SessionContext.State.ElementsUsed;
            var fight = NpcFights.ForElement(mapId, elementId, used.Contains);
            if (fight == null) return false;
            Console.WriteLine($"[ScriptedFight] Element {elementId} of {mapId} starts its fight for {SessionContext.State.CharacterName}.");
            return await StartAsync(stream, fight, mapId);
        }

        /// <summary>
        /// A fight is over for <paramref name="characterId"/>: the chained fight it was waiting for
        /// is ready if he won, and dropped if he lost.
        /// </summary>
        public static void FightEnded(long characterId, long fightId, bool won)
        {
            if (!_pending.TryGetValue(characterId, out var pending) || pending.FightId != fightId) return;
            if (won) _pending[characterId] = pending with { Ready = true };
            else _pending.TryRemove(characterId, out _);
        }

        /// <summary>
        /// The character is back on a map: the chained fight that was ready starts here, if this
        /// is the map it was to be fought on; anywhere else, the chain is left.
        /// </summary>
        public static async Task OnMapLoadedAsync(NetworkStream stream, long characterId, long mapId)
        {
            if (!_pending.TryGetValue(characterId, out var pending) || !pending.Ready) return;
            _pending.TryRemove(characterId, out _);
            if (pending.MapId != mapId) return;
            Console.WriteLine($"[ScriptedFight] The next fight of the chain starts on {mapId}.");
            await StartAsync(stream, pending.Next, mapId);
        }

        /// <summary>Whether a chained fight waits for this character, ready or not; for the tests.</summary>
        internal static bool Waits(long characterId, out bool ready)
        {
            ready = _pending.TryGetValue(characterId, out var pending) && pending.Ready;
            return _pending.ContainsKey(characterId);
        }

        /// <summary>For the tests: sets a chained fight aside as <see cref="StartAsync"/> does.</summary>
        internal static void Chain(long characterId, long fightId, NpcFights.Fight next, long mapId)
            => _pending[characterId] = new Pending(fightId, next, mapId, false);

        /// <summary>For the tests: forgets every chain.</summary>
        internal static void Clear() => _pending.Clear();
    }
}
