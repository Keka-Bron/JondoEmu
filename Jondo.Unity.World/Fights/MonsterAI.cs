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

        /// <summary>Cuántas veces se lanza el hechizo en este turno.</summary>
        public int CastCount { get; set; }

        /// <summary>
        /// Casilla desde la que se lanza. Hace falta para el registro: cuando el monstruo ataca y
        /// luego huye, su CellId ya es el de destino y la traza daba distancias falsas.
        /// </summary>
        public int CastFromCell { get; set; } = -1;

        /// <summary>
        /// Si el hechizo se lanzó ANTES de moverse. Pasa cuando el monstruo ya tenía al objetivo
        /// a tiro y luego huye (por debajo del 30 % de vida). Sin esto, FightHandler mandaba
        /// siempre el movimiento primero y el lanzamiento después, de modo que en pantalla —y en
        /// los registros— parecía que el bicho había disparado desde la casilla a la que huyó:
        /// atacaba legalmente a 6 casillas, escapaba a 10 y el ataque se veía a 10.
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

            /// <summary>Si el hechizo exige línea de visión sobre el objetivo.</summary>
            public bool NeedsLineOfSight { get; set; } = true;
        }

        public static MonsterTurnResult ExecuteTurn(
            Fighter monster,
            List<Fighter> allFighters,
            HashSet<int> arenaWalkableCells = null,
            Func<int, AISpellData>? spellDataFetcher = null,
            HashSet<int> losBlockers = null)
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

            // Sin hechizo inventado de reserva. Antes había aquí uno de alcance 1-6 y daño
            // 8+nivel/2, que es la razón real de que los monstruos no se movieran nunca: con
            // seis casillas de alcance siempre tenían al objetivo a tiro desde su posición de
            // salida. Si un monstruo no tiene hechizos utilizables, se queda quieto y pasa turno.
            if (availableSpells.Count == 0)
            {
                return result;
            }

            // Primero los de más alcance efectivo por PA, para que el monstruo elija el hechizo
            // que puede lanzar sin moverse antes que el cuerpo a cuerpo.
            availableSpells = availableSpells
                .OrderByDescending(s => (s.BaseDamageMin + s.BaseDamageMax) / 2)
                .ToList();

            // 2. Flee mode evaluation (< 30% HP)
            bool fleeMode = ((double)monster.CurrentHP / monster.MaxHP) < 0.30;

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
                .OrderBy(p => p.CurrentHP) // Rematar (lowest absolute HP)
                .ThenBy(p => (double)p.CurrentHP / p.MaxHP) // Herido (lowest HP %)
                .ThenBy(p => CountAlliesNear(p, allFighters)) // Target aislado
                .ThenBy(p => MapGeometry.Distance(monster.CellId, p.CellId))
                .FirstOrDefault();
        }

        private static int CountAlliesNear(Fighter target, List<Fighter> allFighters)
        {
            return allFighters.Count(f => f.TeamId == target.TeamId && f.IsAlive && f.Id != target.Id && MapGeometry.Distance(target.CellId, f.CellId) <= 3);
        }

        /// <summary>
        /// Repite el mismo hechizo mientras al monstruo le queden PA y no supere el límite de
        /// lanzamientos por turno. Antes lanzaba una sola vez y pasaba turno con los PA a medias:
        /// el pío, con 4 PA y un hechizo de 2, se dejaba la mitad del turno sin usar.
        /// </summary>
        private static int RepeatCasts(Fighter monster, Fighter target, List<AISpellData> spells,
                                       HashSet<int> losBlockers, int spellId)
        {
            var spell = spells.FirstOrDefault(s => s.SpellId == spellId);
            if (spell == null) return 0;

            int extra = 0;
            int limite = spell.MaxCastPerTurn > 0 ? spell.MaxCastPerTurn : int.MaxValue;
            while (extra + 1 < limite && monster.CurrentAP >= spell.APCost && target.IsAlive)
            {
                var repetido = TryCastBestSpell(monster, target, new List<AISpellData> { spell }, losBlockers);
                if (repetido.SpellId == 0) break;
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
                    // Un hechizo que exige línea de visión no atraviesa muros. Esto es lo que
                    // hacía que el pío disparase desde el otro lado del murete de la arena.
                    if (spell.NeedsLineOfSight &&
                        !MapGeometry.HasLineOfSight(monster.CellId, target.CellId, losBlockers))
                    {
                        continue;
                    }
                    int baseDmg = (spell.BaseDamageMin + spell.BaseDamageMax) / 2;
                    ElementType elem = (ElementType)spell.Element;
                    int statVal = monster.GetStatForElement(elem);
                    int targetResPct = target.GetResPctForElement(elem);

                    // Solo una estimación para decidir; quien aplica el daño de verdad y manda los
                    // paquetes es FightHandler.ApplySpellEffectsAsync, el mismo camino que usa el
                    // jugador. Antes se restaba aquí la vida y luego se volvía a calcular fuera.
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
        /// Casilla desde la que el monstruo puede lanzar gastando los mínimos PM. Se comprueba el
        /// alcance Y la línea de visión: sin lo segundo el monstruo se plantaba detrás de un muro
        /// y disparaba igual.
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
