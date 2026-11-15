using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The heroes on the map, at their master's side: drawn for everybody on it, walking after
    /// him, going with him from one map to the next, and gone while they fight.
    /// </summary>
    /// <remarks>
    /// As Auralis's heroes stand beside their master and follow him («Mes héros me suivent»). A
    /// hero has no client: his actor is the server's to put on the map (jsn), to walk (jsj, the
    /// master's own walk, ending a few steps behind him) and to take off (kmu), and every map's
    /// actor list carries the heroes of the people on it (ConnectionProtocol.BuildMapActors).
    /// </remarks>
    internal static class HeroPresence
    {
        /// <summary>A hero drawn on a map: who, where, facing where, and his account.</summary>
        internal readonly record struct Shown(DatabaseManager.DbCharacter Character, int Cell, int Facing, long AccountId);

        /// <summary>The heroes standing on a map: those of the people on it, out of any fight.</summary>
        public static IEnumerable<Shown> On(long mapId)
        {
            foreach (var master in SessionRegistry.OnMap(mapId).ToList())
            {
                if (master.CharacterId <= 0 || master.State.IsInFight) continue;
                foreach (var hero in Heroes.Of(master.CharacterId))
                {
                    var state = hero.Session.State;
                    if (state.IsInFight || state.MapId != mapId) continue;
                    var character = DatabaseManager.GetCharacterById(hero.CharacterId);
                    if (character != null) yield return new Shown(character, state.CellId, state.Orientation, master.AccountId);
                }
            }
        }

        /// <summary>A hero put at his master's side on the master's map, and drawn there for everybody.</summary>
        public static async Task ArriveAsync(GameSession master, Heroes.Hero hero)
        {
            var state = hero.Session.State;
            state.MapId = master.State.MapId;
            state.CellId = BesideOf(master, hero.CharacterId);
            state.Orientation = master.State.Orientation;
            await DrawAsync(master, hero);
        }

        /// <summary>A hero taken off the map he stands on, for everybody on it.</summary>
        public static Task LeaveAsync(Heroes.Hero hero)
            => LeaveAsync(hero.Session.State.MapId, hero.CharacterId);

        private static async Task LeaveAsync(long mapId, long heroId)
        {
            if (mapId <= 0) return;
            try { await SessionRegistry.RemoveFromMapAsync(mapId, heroId, Guid.Empty); }
            catch (Exception ex) { Program.LogDebug($"[Heroes] {heroId} could not be taken off map {mapId}: {ex.Message}"); }
        }

        /// <summary>The master's heroes off the map he left, and at his side on the one he reached.</summary>
        public static async Task MasterMovedAsync(GameSession master, long mapLeft)
        {
            foreach (var hero in Heroes.Of(master.CharacterId))
            {
                if (hero.Session.State.IsInFight) continue;
                if (mapLeft > 0 && mapLeft != master.State.MapId) await LeaveAsync(mapLeft, hero.CharacterId);
                await ArriveAsync(master, hero);
            }
        }

        /// <summary>
        /// The master walked: each hero walks his walk after him and stops short of its end -- the
        /// first one step behind, the next two --, all on the cells the master walked.
        /// </summary>
        public static async Task MasterWalkedAsync(GameSession master, IReadOnlyList<int> cells)
        {
            if (cells.Count == 0) return;
            int behind = 1;
            foreach (var hero in Heroes.Of(master.CharacterId))
            {
                var state = hero.Session.State;
                if (state.IsInFight || state.MapId != master.State.MapId) continue;
                int stop = Math.Max(0, cells.Count - 1 - behind);
                var path = new List<int> { state.CellId };
                for (int i = 0; i <= stop; i++) if (path[^1] != cells[i]) path.Add(cells[i]);
                behind++;
                if (path.Count < 2) continue;
                state.CellId = path[^1];
                state.Orientation = master.State.Orientation;
                try { await master.Map.SendAsync(new GameMapMovementEvent(path, hero.CharacterId, false, Pace: 0)); }
                catch (Exception ex) { Program.LogDebug($"[Heroes] {hero.CharacterId} could not follow: {ex.Message}"); }
            }
        }

        /// <summary>A hero's actor drawn on his map for everybody on it, his master too (jsn).</summary>
        private static async Task DrawAsync(GameSession master, Heroes.Hero hero)
        {
            var state = hero.Session.State;
            var character = DatabaseManager.GetCharacterById(hero.CharacterId);
            if (character == null || state.IsInFight || master.State.IsInFight) return;
            try
            {
                await SessionRegistry.BroadcastToMapAsync(state.MapId, ConnectionProtocol.Push(Op.Jsn,
                    ConnectionProtocol.BuildActorRefreshed(character, state.CellId, state.Orientation, master.AccountId)), Guid.Empty);
            }
            catch (Exception ex) { Program.LogDebug($"[Heroes] {hero.CharacterId} could not be drawn: {ex.Message}"); }
        }

        /// <summary>
        /// A walkable cell next to the master's, one no other hero of his stands on; his own when
        /// none is free. The cells around a cell are ±14, ±1 and ±28 away on the map's grid.
        /// </summary>
        private static int BesideOf(GameSession master, long heroId)
        {
            int cell = master.State.CellId;
            if (!MapManager.WalkableCells.TryGetValue(master.State.MapId, out var walkable)) return cell;
            var taken = Heroes.Of(master.CharacterId).Where(h => h.CharacterId != heroId && h.Session.State.MapId == master.State.MapId)
                              .Select(h => h.Session.State.CellId).ToHashSet();
            foreach (int step in new[] { 14, -14, 1, -1, 15, -15, 13, -13, 28, -28 })
            {
                int next = cell + step;
                if (walkable.Contains(next) && !taken.Contains(next)) return next;
            }
            return cell;
        }
    }
}
