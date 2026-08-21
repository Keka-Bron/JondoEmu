using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.World.Fights
{
    public class MonsterTurnResult
    {
        public List<int> PathCells { get; set; } = new List<int>();
        public long TargetFighterId { get; set; }
        public int TargetCellId { get; set; }
        public int SpellId { get; set; }
        public int DamageDealt { get; set; }

        /// <summary>How many times the spell is cast during this turn.</summary>
        public int CastCount { get; set; }

        /// <summary>
        /// The cell the spell is cast from. Needed for logging: when the monster attacks and then
        /// flees, its CellId is already the destination one and the trace reported bogus distances.
        /// </summary>
        public int CastFromCell { get; set; } = -1;

        /// <summary>
        /// Whether the spell was cast BEFORE moving. This happens when the monster already had the
        /// target in range and then flees (below 30% HP). Without this, FightHandler always sent
        /// the movement first and the cast afterwards, so on screen -- and in the logs -- it looked
        /// as if the creature had fired from the cell it fled to: it attacked legally at 6 cells,
        /// escaped to 10, and the attack appeared to happen at 10.
        /// </summary>
        public bool CastBeforeMove { get; set; }
    }

    public static class MonsterAI
    {
        public class AISpellData
        {
            public int SpellId { get; set; }
            public int APCost { get; set; } = 3;
            public int MinRange { get; set; } = 1;
            public int MaxRange { get; set; } = 2;
            public int BaseDamageMin { get; set; } = 5;
            public int BaseDamageMax { get; set; } = 10;
            public int Element { get; set; } = 0;
            public int MaxCastPerTurn { get; set; } = 2;

            /// <summary>Whether the spell requires line of sight to the target.</summary>
            public bool NeedsLineOfSight { get; set; } = true;
        }

        public static MonsterTurnResult ExecuteTurn(
            Fighter monster,
            List<Fighter> allFighters,
            HashSet<int> arenaWalkableCells = null,
            Func<int, AISpellData>? spellDataFetcher = null,
            HashSet<int> losBlockers = null,
            IReadOnlyDictionary<int, int>? spellPriorities = null,
            double? fleeBelowHpPercent = null)
        {
            var result = new MonsterTurnResult();
            var players = allFighters.Where(f => f.TeamId == 0 && f.IsAlive).ToList();
            if (players.Count == 0 || !monster.IsAlive) return result;

            // 1. Load spells
            var availableSpells = new List<AISpellData>();
            if (monster.SpellIds != null && monster.SpellIds.Count > 0 && spellDataFetcher != null)
            {
                foreach (int id in monster.SpellIds)
                {
                    var s = spellDataFetcher(id);
                    if (s != null && s.APCost <= monster.CurrentAP) availableSpells.Add(s);
                }
            }

            // No made-up fallback spell. There used to be one here with range 1-6 and damage
            // 8+level/2, and that is the real reason monsters never moved: with six cells of
            // range they always had the target in reach from their starting position. If a
            // monster has no usable spell, it stays put and passes the turn.
            if (availableSpells.Count == 0)
            {
                return result;
            }

            // Best effective range per AP first, so the monster picks the spell it can cast
            // without moving over the melee one.
            availableSpells = availableSpells
                // A higher externally configured priority wins; absent data retains the
                // measured generic damage ordering. No monster id is hard-coded here.
                .OrderByDescending(s => spellPriorities != null && spellPriorities.TryGetValue(s.SpellId, out int priority) ? priority : 0)
                .ThenByDescending(s => (s.BaseDamageMin + s.BaseDamageMax) / 2)
                .ToList();

            // 2. Flee mode evaluation (< 30% HP)
            double fleeThreshold = fleeBelowHpPercent ?? 0.30;
            bool fleeMode = ((double)monster.CurrentHP / monster.MaxHP) < fleeThreshold;

            // 3. Target Selection
            var target = EvaluateBestTarget(monster, players, allFighters);
            if (target == null) return result;
            result.TargetFighterId = target.Id;

            var occupiedCells = new HashSet<int>(allFighters.Where(f => f.IsAlive && f.Id != monster.Id).Select(f => f.CellId));

            // 4. Attack Phase Before Move
            var castResult = TryCastBestSpell(monster, target, availableSpells, losBlockers);
            if (castResult.SpellId != 0)
            {
                result.SpellId = castResult.SpellId;
                result.TargetCellId = target.CellId;
                result.DamageDealt = castResult.DamageDealt;
                result.CastBeforeMove = true;
                result.CastFromCell = monster.CellId;
                result.CastCount = 1 + RepeatCasts(monster, target, availableSpells, losBlockers, castResult.SpellId);
            }

            // 5. Movement Phase
            if (!fleeMode && result.SpellId == 0 && monster.CurrentMP > 0)
            {
                // Find reachable cell within MP that gets monster into spell range
                int bestCell = FindBestTacticalCell(monster, target, availableSpells, arenaWalkableCells, occupiedCells, losBlockers);
                if (bestCell != monster.CellId)
                {
                    var path = MapGeometry.FindShortestPath(monster.CellId, bestCell, arenaWalkableCells, occupiedCells);
                    if (path.Count > 1)
                    {
                        int steps = Math.Min(path.Count - 1, monster.CurrentMP);
                        var actualPath = path.Take(steps + 1).ToList();

                        monster.AccumulatedMpLoss += steps;
                        monster.CurrentMP -= steps;
                        monster.CellId = actualPath.Last();

                        result.PathCells = actualPath;
                    }
                }

                // Try attack again after moving
                if (result.SpellId == 0)
                {
                    var postMoveCast = TryCastBestSpell(monster, target, availableSpells, losBlockers);
                    if (postMoveCast.SpellId != 0)
                    {
                        result.SpellId = postMoveCast.SpellId;
                        result.TargetCellId = target.CellId;
                        result.DamageDealt = postMoveCast.DamageDealt;
                        result.CastBeforeMove = false;
                        result.CastFromCell = monster.CellId;
                        result.CastCount = 1 + RepeatCasts(monster, target, availableSpells, losBlockers, postMoveCast.SpellId);
                    }
                }
            }
            else if (fleeMode && monster.CurrentMP > 0)
            {
                // Move to maximize distance from players
                int fleeCell = FindFleeCell(monster, players, arenaWalkableCells, occupiedCells);
                if (fleeCell != monster.CellId)
                {
                    var path = MapGeometry.FindShortestPath(monster.CellId, fleeCell, arenaWalkableCells, occupiedCells);
                    if (path.Count > 1)
                    {
                        int steps = Math.Min(path.Count - 1, monster.CurrentMP);
                        var actualPath = path.Take(steps + 1).ToList();

                        monster.AccumulatedMpLoss += steps;
                        monster.CurrentMP -= steps;
                        monster.CellId = actualPath.Last();

                        result.PathCells = actualPath;
                    }
                }
            }

            return result;
        }

        private static Fighter EvaluateBestTarget(Fighter monster, List<Fighter> players, List<Fighter> allFighters)
        {
            return players
                .OrderBy(p => p.CurrentHP) // Finish off (lowest absolute HP)
                .ThenBy(p => (double)p.CurrentHP / p.MaxHP) // Wounded (lowest HP %)
                .ThenBy(p => CountAlliesNear(p, allFighters)) // Isolated target
                .ThenBy(p => MapGeometry.Distance(monster.CellId, p.CellId))
                .FirstOrDefault();
        }

        private static int CountAlliesNear(Fighter target, List<Fighter> allFighters)
        {
            return allFighters.Count(f => f.TeamId == target.TeamId && f.IsAlive && f.Id != target.Id && MapGeometry.Distance(target.CellId, f.CellId) <= 3);
        }

        /// <summary>
        /// Repeats the same spell while the monster has AP left and stays under the per-turn cast
        /// limit. It used to cast only once and end the turn with AP to spare: the piou, with 4 AP
        /// and a 2 AP spell, wasted half of its turn.
        /// </summary>
        private static int RepeatCasts(Fighter monster, Fighter target, List<AISpellData> spells,
                                       HashSet<int> losBlockers, int spellId)
        {
            var spell = spells.FirstOrDefault(s => s.SpellId == spellId);
            if (spell == null) return 0;

            int extra = 0;
            int limit = spell.MaxCastPerTurn > 0 ? spell.MaxCastPerTurn : int.MaxValue;
            while (extra + 1 < limit && monster.CurrentAP >= spell.APCost && target.IsAlive)
            {
                var repeated = TryCastBestSpell(monster, target, new List<AISpellData> { spell }, losBlockers);
                if (repeated.SpellId == 0) break;
                extra++;
            }
            return extra;
        }

        private static (int SpellId, int DamageDealt) TryCastBestSpell(
            Fighter monster, Fighter target, List<AISpellData> spells, HashSet<int> losBlockers)
        {
            int dist = MapGeometry.Distance(monster.CellId, target.CellId);

            foreach (var spell in spells)
            {
                if (dist >= spell.MinRange && dist <= spell.MaxRange && monster.CurrentAP >= spell.APCost)
                {
                    // A spell that requires line of sight does not go through walls. This is what
                    // made the piou fire from the far side of the arena's low wall.
                    if (spell.NeedsLineOfSight &&
                        !MapGeometry.HasLineOfSight(monster.CellId, target.CellId, losBlockers))
                    {
                        continue;
                    }
                    int baseDmg = (spell.BaseDamageMin + spell.BaseDamageMax) / 2;
                    ElementType elem = (ElementType)spell.Element;
                    int statVal = monster.GetStatForElement(elem);
                    int targetResPct = target.GetResPctForElement(elem);

                    // Just an estimate to make the decision; the real damage is applied and the
                    // packets are sent by FightHandler.ApplySpellEffectsAsync, the same path the
                    // player goes through. HP used to be subtracted here and then recomputed
                    // outside.
                    int damage = DamageCalculator.CalculateDamage(
                        baseDamage: baseDmg,
                        element: elem,
                        statValue: statVal,
                        power: 0,
                        flatElementDamage: 0,
                        flatDamage: 0,
                        targetResPct: targetResPct,
                        targetFlatRes: 0
                    );

                    monster.AccumulatedApLoss += spell.APCost;
                    monster.CurrentAP -= spell.APCost;

                    return (spell.SpellId, damage);
                }
            }

            return (0, 0);
        }

        /// <summary>
        /// The cell the monster can cast from while spending the fewest MP. Both range AND line of
        /// sight are checked: without the latter the monster would park itself behind a wall and
        /// fire anyway.
        /// </summary>
        private static int FindBestTacticalCell(Fighter monster, Fighter target, List<AISpellData> spells,
                                                HashSet<int> walkable, HashSet<int> occupied, HashSet<int> losBlockers)
        {
            int bestCell = monster.CellId;
            int minMpSpent = 999;

            var reachable = GetReachableCells(monster.CellId, monster.CurrentMP, walkable, occupied);

            foreach (int c in reachable)
            {
                int distToTarget = MapGeometry.Distance(c, target.CellId);
                int mpSpent = MapGeometry.Distance(monster.CellId, c);

                bool canCastAny = spells.Any(s =>
                    distToTarget >= s.MinRange && distToTarget <= s.MaxRange &&
                    s.APCost <= monster.CurrentAP &&
                    (!s.NeedsLineOfSight || MapGeometry.HasLineOfSight(c, target.CellId, losBlockers)));

                if (canCastAny)
                {
                    if (mpSpent < minMpSpent)
                    {
                        minMpSpent = mpSpent;
                        bestCell = c;
                    }
                }
            }

            if (bestCell == monster.CellId)
            {
                // Fallback: move as close as possible to target
                bestCell = reachable.OrderBy(c => MapGeometry.Distance(c, target.CellId)).FirstOrDefault(monster.CellId);
            }

            return bestCell;
        }

        private static int FindFleeCell(Fighter monster, List<Fighter> enemies, HashSet<int> walkable, HashSet<int> occupied)
        {
            var reachable = GetReachableCells(monster.CellId, monster.CurrentMP, walkable, occupied);
            return reachable
                .OrderByDescending(c => enemies.Min(e => MapGeometry.Distance(c, e.CellId)))
                .FirstOrDefault(monster.CellId);
        }

        private static List<int> GetReachableCells(int startCell, int maxMp, HashSet<int> walkable, HashSet<int> occupied)
        {
            var result = new List<int> { startCell };
            var queue = new Queue<(int Cell, int Dist)>();
            var visited = new HashSet<int> { startCell };
            queue.Enqueue((startCell, 0));

            while (queue.Count > 0)
            {
                var (curr, dist) = queue.Dequeue();
                if (dist >= maxMp) continue;

                foreach (int n in MapGeometry.GetNeighbors(curr))
                {
                    if (visited.Contains(n)) continue;
                    if (walkable != null && !walkable.Contains(n)) continue;
                    if (occupied != null && occupied.Contains(n)) continue;

                    visited.Add(n);
                    result.Add(n);
                    queue.Enqueue((n, dist + 1));
                }
            }

            return result;
        }
    }
}
