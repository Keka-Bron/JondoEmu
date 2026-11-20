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
    /// The heroes stand beside their master and follow him. A
    /// hero has no client: his actor is the server's to put on the map (jsn), to walk (jsj, the
    /// master's own walk, ending a few steps behind him) and to take off (kmu), and every map's
    /// actor list carries the heroes of the people on it (MapInformationMessages).
    /// </remarks>
    internal static class HeroPresence
    {
        /// <summary>A hero drawn on a map: who, where, facing where, and his account.</summary>
        internal readonly record struct Shown(DbCharacter Character, int Cell, int Facing, long AccountId);

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
            try { await MapPresenceService.Live.RemoveAsync(SessionRegistry.OnMap(mapId), heroId); }
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
                // What his last fight set aside, his level, and the zone and the map he reached, as
                // a player's on reaching a map.
                long map = master.State.MapId;
                try { await Achievements.ForHeroAsync(master, hero.Session, map, MapManager.GetMapInfo(map)?.SubAreaId ?? 0); }
                catch (Exception ex) { Program.LogDebug($"[Heroes] {hero.CharacterId}'s achievements not looked at: {ex.Message}"); }
            }
        }

        /// <summary>
        /// The master walked: each hero walks after him to a free cell beside where he stops, by
        /// his own shortest way there, every cell of it. The client's walk names only the cells it
        /// turns on, so the master's own path, cut short, sent the heroes back to where he set off.
        /// </summary>
        public static async Task MasterWalkedAsync(GameSession master, IReadOnlyList<int> cells)
        {
            if (cells.Count == 0) return;
            long map = master.State.MapId;
            if (!MapManager.WalkableCells.TryGetValue(map, out var cellsOfMap)) return;
            var walkable = new HashSet<int>(cellsOfMap);
            int end = cells[^1];
            var taken = new HashSet<int> { end };
            foreach (var hero in Heroes.Of(master.CharacterId))
            {
                var state = hero.Session.State;
                if (state.IsInFight || state.MapId != map) continue;
                int target = NearestFree(end, walkable, taken);
                taken.Add(target);
                if (target == state.CellId) continue;
                var path = Jondo.Unity.World.Maps.MapGeometry.FindShortestPath(state.CellId, target, walkable);
                if (path.Count < 2) path = new List<int> { state.CellId, target };
                state.CellId = target;
                state.Orientation = master.State.Orientation;
                try { await master.Map.SendAsync(new GameMapMovementEvent(path, hero.CharacterId, false, Pace: 0)); }
                catch (Exception ex) { Program.LogDebug($"[Heroes] {hero.CharacterId} could not follow: {ex.Message}"); }
            }
        }

        /// <summary>The walkable cell nearest a cell, past it and not taken; the cell itself when none is.</summary>
        private static int NearestFree(int cell, HashSet<int> walkable, HashSet<int> taken)
        {
            int best = cell, bestDistance = int.MaxValue;
            foreach (int candidate in walkable)
            {
                if (taken.Contains(candidate)) continue;
                int distance = Jondo.Unity.World.Maps.MapGeometry.Distance(cell, candidate);
                if (distance < 1 || distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>A hero's actor drawn on his map for everybody on it, his master too (jsn).</summary>
        private static async Task DrawAsync(GameSession master, Heroes.Hero hero)
        {
            var state = hero.Session.State;
            var character = DatabaseManager.GetCharacterById(hero.CharacterId);
            if (character == null || state.IsInFight || master.State.IsInFight) return;
            try
            {
                await SessionRegistry.BroadcastToMapAsync(state.MapId,
                    MapActorMessages.Refreshed(character, state.CellId, state.Orientation, master.AccountId), Guid.Empty);
            }
            catch (Exception ex) { Program.LogDebug($"[Heroes] {hero.CharacterId} could not be drawn: {ex.Message}"); }
        }

        /// <summary>The walkable cell nearest the master's that neither he nor another hero of his stands on.</summary>
        private static int BesideOf(GameSession master, long heroId)
        {
            int cell = master.State.CellId;
            if (!MapManager.WalkableCells.TryGetValue(master.State.MapId, out var cellsOfMap)) return cell;
            var walkable = new HashSet<int>(cellsOfMap);
            var taken = Heroes.Of(master.CharacterId).Where(h => h.CharacterId != heroId && h.Session.State.MapId == master.State.MapId)
                              .Select(h => h.Session.State.CellId).ToHashSet();
            taken.Add(cell);
            return NearestFree(cell, walkable, taken);
        }
    }
}
