using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The Tymador's bomb walls and combos, played on the board and checked against his class
    /// sheet (Translations 1010378, «Las bombas») and the spells' own texts: walls of every
    /// length, one and several fighters in them, walking, pushing and starting a turn inside,
    /// Kabúm's shelter, and every spell that hands a bomb combos, with what each combo adds to
    /// an explosion and, halved, to a wall.
    /// </summary>
    public static partial class ControlApi
    {
        private static class Ty
        {
            public const int Explobomba = 13444, Tornabombas = 13435, Detonador = 13432, Imantacion = 13437,
                             Jugarreta = 13439, Polvo = 13441, UltimoAliento = 13446, Kabum = 13450,
                             Mosquete = 13473, Receptacion = 13477, Bombardeo = 14415;

            /// <summary>Polvo's mark on its bomb, and the state that makes it unmovable.</summary>
            public const int PolvoState = 2511, Unmovable = 97;

            /// <summary>Jugarreta's «Pesante».</summary>
            public const int Pesante = 7;

            /// <summary>«Bonus de combo», I to XV, as the class sheet prints it.</summary>
            public static readonly int[] SheetPercent = { 0, 0, 20, 40, 60, 80, 100, 120, 140, 160, 190, 220, 250, 280, 320, 360 };

            /// <summary>"[Combo] -5 is at level 7, +120%: 30 becomes 66."</summary>
            public static readonly Regex ComboLine = new(@"\[Combo\] (-?\d+) is at level (\d+), \+(\d+)%");
        }

        private static List<Fighter> BombsOf(FightInstance fight, Fighter me)
            => fight.Todos.Where(f => f.IsAlive && f.EsInvocado && f.Invocador == me.Id && FightHandler.EsBomba(f.MonsterId)).ToList();

        private static List<Fighter> FoesOf(FightInstance fight, Fighter me)
            => fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado).ToList();

        private static HashSet<int> WallCellsOf(FightInstance fight)
            => fight.Glifos.Where(Managers.BombWalls.IsWall).SelectMany(g => g.Casillas).ToHashSet();

        private static int Level(Fighter bomb) => Managers.Combo.LevelOf(bomb);

        /// <summary>Whether <paramref name="from"/> sees <paramref name="to"/>, the given cells blocking too (a bomb about to stand there).</summary>
        private static bool SeesPast(FightInstance fight, Fighter me, int from, int to, params int[] blocking)
        {
            var opaque = MapManager.GetLosBlockers(fight.ArenaMapId);
            return MapGeometry.HasLineOfSight(from, to, c => blocking.Contains(c) || (opaque?.Contains(c) ?? false)
                                                            || fight.Todos.Any(f => f.IsAlive && f != me && f.CellId == c));
        }

        /// <summary>
        /// Two cells <paramref name="distance"/> apart on one row of the grid, for two bombs: the
        /// ground between walkable and free, both in the bombs' reach (1 to 6) and in sight -- the
        /// far one past the near -- from where the Tymador is put; null when the board has none.
        /// </summary>
        private static (int A, int B, int Dx, int Dy, List<int> Between)? ToWallRow(GameSession session, FightInstance fight,
                                                                                 Fighter me, int distance)
        {
            var board = FightHandler.BoardOf(fight);
            (int, int, int, int, List<int>)? From(int s)
            {
                foreach (int a in Enumerable.Range(0, 560).Where(c => c != s && FreeCell(fight, c) && MapGeometry.Distance(c, s) is >= 1 and <= 6)
                                            .OrderBy(c => MapGeometry.Distance(c, s)))
                {
                    if (!SeesPast(fight, me, s, a)) continue;
                    foreach (var (dx, dy) in Rows)
                    {
                        int b = Along(a, dx, dy, distance);
                        if (b < 0 || b == s || !FreeCell(fight, b) || MapGeometry.Distance(b, s) is < 1 or > 6 || !SeesPast(fight, me, s, b, a)) continue;
                        var between = Enumerable.Range(1, distance - 1).Select(k => Along(a, dx, dy, k)).ToList();
                        if (between.Any(c => c < 0 || c == s || !board.Walkable(c) || !FreeCell(fight, c))) continue;
                        return (a, b, dx, dy, between);
                    }
                }
                return null;
            }
            foreach (int s in new[] { me.CellId }.Concat(Enumerable.Range(0, 560).Where(c => FreeCell(fight, c))
                                                                     .OrderBy(c => MapGeometry.Distance(c, me.CellId)).Take(150)))
            {
                var row = From(s);
                if (row == null) continue;
                if (s != me.CellId) PutAt(session, fight, me, s);
                if (me.CellId != s) continue;
                return row;
            }
            return null;
        }

        /// <summary>Two Explobombas on a row's ends, the turn refreshed between them; whether both stand.</summary>
        private static bool TwoBombs(GameSession session, FightInstance fight, Fighter me, int a, int b, int spell = Ty.Explobomba)
        {
            CastAt(session, spell, a);
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, spell, b);
            FreshTurn(session, fight, me, keep: true);
            var bombs = BombsOf(fight, me);
            return bombs.Any(x => x.CellId == a) && bombs.Any(x => x.CellId == b);
        }

        /// <summary>The Tymador put two cells or more from every given cell, so that nobody walking there is tackled by him.</summary>
        private static void StepAside(GameSession session, FightInstance fight, Fighter me, IEnumerable<int> from)
        {
            var away = from.ToList();
            if (away.All(c => MapGeometry.Distance(c, me.CellId) > 2)) return;
            int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && away.All(x => MapGeometry.Distance(x, c) > 2))
                                 .OrderBy(c => MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();
            if (spot >= 0) PutAt(session, fight, me, spot);
        }

        /// <summary>The combo lines a cast logged, each against the sheet's table: true when every one agrees.</summary>
        private static void CheckComboLines(Scenario sc, IEnumerable<string> lines, Fighter me, string rule, Func<long, int, int>? expected = null)
        {
            foreach (string line in lines)
            {
                var m = Ty.ComboLine.Match(line);
                if (!m.Success) continue;
                long who = long.Parse(m.Groups[1].Value);
                int level = int.Parse(m.Groups[2].Value), percent = int.Parse(m.Groups[3].Value);
                int want = expected?.Invoke(who, level) ?? (level is >= 1 and <= 15 ? Ty.SheetPercent[level] : -1);
                sc.Check(rule, percent == want, $"{(who == me.Id ? "the wall" : $"bomb {who}")} at combo {level}: +{percent}% (sheet: +{want}%)");
            }
        }

        // ─── The walls ────────────────────────────────────────────────────────────

        private static void TymadorWalls(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            // «Cuando hay al menos 2 bombas alineadas y espaciadas de 2 a 6 casillas máximo, forman
            //  automáticamente un muro de bombas.» Spaced: the cells BETWEEN them, as the captures
            // measured it (BombWalls.MinGap/MaxGap) -- side by side and seven between, none.
            for (int distance = 1; distance <= 8; distance++)
            {
                FreshTurn(session, fight, me, keep: false, withoutSummons: true);
                if (ToWallRow(session, fight, me, distance) is not { } r)
                {
                    sc.Note($"no row on this board for two bombs {distance} apart");
                    continue;
                }
                if (!TwoBombs(session, fight, me, r.A, r.B))
                {
                    sc.Check($"two Explobombas go down {distance} apart", false, $"bombs on {Cells(BombsOf(fight, me).Select(b => b.CellId))}, wanted {r.A} and {r.B}");
                    continue;
                }
                var cells = WallCellsOf(fight);
                int gap = distance - 1;
                if (distance is >= Managers.BombWalls.MinGap and <= Managers.BombWalls.MaxGap)
                {
                    bool exact = cells.SetEquals(r.Between);
                    sc.Check($"{gap} cell(s) between two bombs: a wall over exactly those", exact,
                             $"bombs on {r.A} and {r.B}; between {Cells(r.Between)}; wall on {Cells(cells.OrderBy(c => c))}");
                    if (exact) WalkAFoeIn(session, fight, me, sc, r.Between, $"a foe walking into a {gap}-cell wall is hurt");
                }
                else
                {
                    sc.Check(distance == 1 ? "two bombs side by side raise no wall" : $"{gap} cells between two bombs is too far: no wall",
                             cells.Count == 0, $"bombs on {r.A} and {r.B}; wall on {Cells(cells.OrderBy(c => c))}");
                }
            }

            // «Un muro puede estar formado por tres bombas como mucho.» Three in a row: a wall on
            // each stretch, the middle bomb holding both.
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            if (ToWallRow(session, fight, me, 6) is { } three)
            {
                int middle = three.Between[2];
                bool ends = TwoBombs(session, fight, me, three.A, three.B);
                if (ends && FightHandler.BoardOf(fight).Sees(me.CellId, middle)) CastAt(session, Ty.Explobomba, middle);
                var cells = WallCellsOf(fight);
                var wanted = three.Between.Where(c => c != middle).ToHashSet();
                sc.Check("three bombs in a row: a wall on each stretch, the middle bomb holding both",
                         BombsOf(fight, me).Count(b => b.CellId == three.A || b.CellId == middle || b.CellId == three.B) == 3 && cells.SetEquals(wanted),
                         $"bombs on {Cells(BombsOf(fight, me).Select(b => b.CellId))}; wall on {Cells(cells.OrderBy(c => c))}, wanted {Cells(wanted.OrderBy(c => c))}");
            }
            else sc.Note("no row on this board for three bombs");

            // «Una entidad que se desplace en el muro o entre en él sufrirá daños.» Walking, every
            // step taken in it: «inflige daños por cada PM que esta entidad consuma en el muro».
            // A five-cell wall, walked in its middle: no step leaves a cell beside a bomb.
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            if (ToWallRow(session, fight, me, 6) is { } walk && TwoBombs(session, fight, me, walk.A, walk.B) && WallCellsOf(fight).SetEquals(walk.Between))
            {
                StepAside(session, fight, me, walk.Between);
                int first = walk.Between[1], last = walk.Between[3];
                var foe = FoesOf(fight, me).OrderBy(f => MapGeometry.Distance(f.CellId, first)).FirstOrDefault();
                int beside = MapGeometry.GetNeighbors(first).Where(c => FreeCell(fight, c) && !walk.Between.Contains(c) && MapGeometry.Distance(c, me.CellId) > 1)
                                        .DefaultIfEmpty(-1).First();
                if (foe != null && beside >= 0)
                {
                    PutAt(session, fight, foe, beside);
                    var lines = RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, foe, first));
                    lines.AddRange(RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, foe, last)));
                    int hits = lines.Count(l => l.Contains($"[Fight] {foe.Id} steps on glyph"));
                    var walks = lines.Where(l => l.Contains($"{foe.Id}") && (l.Contains("walk") || l.Contains("moves") || l.Contains("tackl")))
                                     .Select(l => l.Length > 140 ? l[^140..] : l);
                    sc.Check("walking in a wall hurts at every step taken in it: three steps, three blows", foe.CellId == last && hits == 3,
                             $"foe {foe.Id} from {beside} into {first} and along to {foe.CellId} (wall {Cells(walk.Between)}): {hits} hit(s); " +
                             string.Join(" | ", walks).DefaultIfEmptyText("no walk lines"));
                }
                else sc.Note("no foe or free cell beside the wall for the walk");

                // «Desplazar una entidad a un muro detendrá su desplazamiento y le infligirá daños. Si
                //  esta entidad ya ha sufrido los efectos del muro durante su turno y vuelven a mandarla
                //  a él, su desplazamiento no se detendrá ni sufrirá los daños.»
                FreshTurn(session, fight, me, keep: true);
                PushedIntoTheWall(session, fight, me, sc, walk.Between, walk.Dx, walk.Dy);
            }
            else sc.Note("no five-cell wall for the walk and the pushes");

            // «...incluso si empieza su turno en el interior.» Several fighters in one wall, each
            // caught at the start of his own turn.
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            if (ToWallRow(session, fight, me, 4) is { } held && TwoBombs(session, fight, me, held.A, held.B) && WallCellsOf(fight).SetEquals(held.Between))
            {
                StepAside(session, fight, me, held.Between);
                var foes = FoesOf(fight, me).OrderBy(f => MapGeometry.Distance(f.CellId, held.Between[1])).Take(held.Between.Count).ToList();
                var places = foes.Select((f, i) => (f, held.Between[i])).ToArray();
                if (foes.Count >= 2 && PutAll(session, fight, places))
                {
                    var lives = foes.ToDictionary(f => f.Id, f => f.CurrentHP);
                    bool back = PassTurn(session, fight, me);
                    sc.Check($"{foes.Count} foes starting their turn in the wall are each hurt", back && foes.All(f => !f.IsAlive || f.CurrentHP < lives[f.Id]),
                             string.Join(", ", foes.Select(f => $"{f.Id}: {lives[f.Id]} -> {f.CurrentHP}")) + (back ? "" : " (the turn did not come back)"));
                }
                else sc.Note(foes.Count < 2 ? "fewer than two foes left for the wall" : "the foes could not all be put in the wall");
            }
            else sc.Note("no three-cell wall for the foes to start their turn in");

            // «Los hechizos Kabúm e Impostura permiten aplicar el estado Kabúm al lanzador y a sus
            //  aliados, que los protege de los daños [...] de los muros.» The wall catches its own
            // Rogue (BombWalls.Catches) -- but not under Kabúm.
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            if (ToWallRow(session, fight, me, 4) is { } own && TwoBombs(session, fight, me, own.A, own.B) && WallCellsOf(fight).SetEquals(own.Between))
            {
                int into = own.Between[1];
                int beside = MapGeometry.GetNeighbors(into).Where(c => FreeCell(fight, c) && !own.Between.Contains(c)).DefaultIfEmpty(-1).First();
                if (beside >= 0)
                {
                    PutAt(session, fight, me, beside);
                    int life = me.CurrentHP;
                    RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, me, into));
                    sc.Check("the Tymador walking into his own wall is hurt", me.CurrentHP < life, $"life {life} -> {me.CurrentHP}");
                    FreshTurn(session, fight, me, keep: true);
                    PutAt(session, fight, me, beside);
                    CastAt(session, Ty.Kabum, me.CellId);
                    bool kabum = me.Buffs.TieneEstado(Managers.BombWalls.KabumState);
                    life = me.CurrentHP;
                    RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, me, into));
                    sc.Check("under Kabúm he walks into it unhurt", kabum && me.CellId == into && me.CurrentHP == life,
                             $"Kabúm {(kabum ? "on" : "not on")}; on {me.CellId}; life {life} -> {me.CurrentHP}");
                }
                else sc.Note("no free cell beside the wall for the Tymador");
            }
            else sc.Note("no three-cell wall for Kabúm");
        }

        /// <summary>A foe walked into a wall from a cell beside it, away from the Tymador: whether he was hurt.</summary>
        private static void WalkAFoeIn(GameSession session, FightInstance fight, Fighter me, Scenario sc, List<int> wall, string rule)
        {
            StepAside(session, fight, me, wall);
            int into = wall.Where(c => !fight.Todos.Any(f => f.IsAlive && f.CellId == c))
                           .OrderBy(c => MapGeometry.Distance(c, wall[wall.Count / 2])).DefaultIfEmpty(-1).First();
            var foe = FoesOf(fight, me).OrderBy(f => MapGeometry.Distance(f.CellId, Math.Max(into, 0))).FirstOrDefault();
            if (into < 0 || foe == null) { sc.Note(foe == null ? "no foe left to walk into the wall" : "no free cell of the wall"); return; }
            int beside = MapGeometry.GetNeighbors(into).Where(c => FreeCell(fight, c) && !wall.Contains(c) && MapGeometry.Distance(c, me.CellId) > 1)
                                    .DefaultIfEmpty(-1).First();
            if (beside < 0) { sc.Note("no free cell beside the wall to walk a foe from"); return; }
            PutAt(session, fight, foe, beside);
            if (foe.CellId != beside) { sc.Note($"foe {foe.Id} could not be put beside the wall"); return; }
            int life = foe.CurrentHP;
            RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, foe, into));
            sc.Check(rule, foe.CellId == into && (foe.CurrentHP < life || !foe.IsAlive), $"foe {foe.Id} from {beside} to {foe.CellId}: life {life} -> {foe.CurrentHP}");
        }

        /// <summary>
        /// A foe pulled two cells across the wall stops on it, hurt; pushed back out and pulled
        /// again in the same turn he goes through, unhurt. Receptación pulls two cells to the
        /// Tymador, Bombardeo pushes one away from him -- both straight.
        /// </summary>
        private static void PushedIntoTheWall(GameSession session, FightInstance fight, Fighter me, Scenario sc, List<int> wall, int dx, int dy)
        {
            // Across the wall: the Tymador two cells off a wall cell on one side, the foe one cell
            // off it on the other, and the cell between the Tymador and the wall free to land on.
            foreach (int w in wall.Skip(1).Take(Math.Max(1, wall.Count - 2)).OrderBy(c => Math.Abs(wall.IndexOf(c) - wall.Count / 2)))
                foreach (int side in new[] { 1, -1 })
                {
                    int qx = dy * side, qy = dx * side;
                    int s = Along(w, qx, qy, -1), t = Along(w, qx, qy, -2), o = Along(w, qx, qy, 1);
                    if (s < 0 || t < 0 || o < 0 || !FreeCell(fight, s) || !FreeCell(fight, o) || (!FreeCell(fight, t) && t != me.CellId)) continue;
                    if (fight.Todos.Any(f => f.IsAlive && f.CellId == w)) continue;
                    // One the wall has not had this turn: whoever walked it already goes through
                    // («si esta entidad ya ha sufrido los efectos del muro durante su turno»).
                    var foe = FoesOf(fight, me).Where(f => !fight.WallHitThisTurn.Contains(f.Id))
                                               .OrderBy(f => MapGeometry.Distance(f.CellId, o)).FirstOrDefault();
                    if (foe == null) { sc.Note("no foe left that the wall has not had this turn"); return; }
                    if (t != me.CellId) PutAt(session, fight, me, t);
                    PutAt(session, fight, foe, o);
                    if (me.CellId != t || foe.CellId != o) continue;
                    FreshTurn(session, fight, me, keep: true);

                    // The wall's blow is told apart from Receptación's own damage by its line.
                    bool Walled(List<string> lines) => lines.Any(l => l.Contains($"[Fight] {foe.Id} steps on glyph"));
                    var pulled = CastAt(session, Ty.Receptacion, foe.CellId);
                    sc.Check("a foe pulled two cells across the wall stops in it, hurt by it", foe.CellId == w && Walled(pulled),
                             $"foe {foe.Id} from {o}: on {foe.CellId} (wall cell {w}, would have reached {s}), the wall {(Walled(pulled) ? "hit him" : "did not hit him")}");
                    if (!foe.IsAlive || foe.CellId != w) return;
                    CastAt(session, Ty.Bombardeo, foe.CellId);
                    sc.Note($"Bombardeo pushes him back out to {foe.CellId}");
                    if (foe.CellId != o) return;
                    // AP back for the second pull, in the same turn: the once-only limit lasts the turn.
                    FreshTurn(session, fight, me, keep: true);
                    var again = CastAt(session, Ty.Receptacion, foe.CellId);
                    sc.Check("pulled across it again the same turn, he goes through and the wall spares him", foe.CellId == s && !Walled(again),
                             $"foe {foe.Id} from {o}: on {foe.CellId} (past the wall: {s}), the wall {(Walled(again) ? "hit him" : "did not hit him")}");
                    return;
                }
            sc.Note("no cell across the wall to pull a foe from");
        }

        // ─── The combos ───────────────────────────────────────────────────────────

        private static void TymadorCombos(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            if (!ToOpenGround(session, fight, me, 4)) { sc.Check("open ground for the bombs", false, "none on this board"); return; }
            int b1Cell = Along(me.CellId, 1, 0, 3), b2Cell = Along(me.CellId, 0, 1, 3);
            CastAt(session, Ty.Explobomba, b1Cell);
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Ty.Explobomba, b2Cell);
            FreshTurn(session, fight, me, keep: true);
            var b1 = BombsOf(fight, me).FirstOrDefault(b => b.CellId == b1Cell);
            var b2 = BombsOf(fight, me).FirstOrDefault(b => b.CellId == b2Cell);
            if (b1 == null || b2 == null) { sc.Check("two Explobombas go down", false, $"bombs on {Cells(BombsOf(fight, me).Select(b => b.CellId))}"); return; }
            sc.Check("«Cuando se invoca una bomba, esta aparece en Combo I»", Level(b1) == 1 && Level(b2) == 1, $"{Level(b1)} and {Level(b2)}");

            // Kabúm: «A las bombas elementales del lanzador: añade 1 combo.»
            int was = Level(b1);
            CastAt(session, Ty.Kabum, b1.CellId);
            sc.Check("Kabúm adds 1 combo to the bomb in its zone", Level(b1) == was + 1, $"{was} -> {Level(b1)}");

            // Mosquete: «añade 1 combo a las bombas del lanzador en zona. El combo solo se aplica una vez por bomba por turno.»
            FreshTurn(session, fight, me, keep: true);
            was = Level(b1);
            CastAt(session, Ty.Mosquete, b1.CellId);
            int once = Level(b1);
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Ty.Mosquete, b1.CellId);
            sc.Check("Mosquete adds 1 combo, once a turn", once == was + 1 && Level(b1) == once, $"{was} -> {once} -> {Level(b1)} after the second");

            // Imantación: «Atrae las bombas del lanzador hacia el centro y les añade 1 combo en zona. El
            //  combo solo se aplica una vez por bomba por turno y solo si las bombas son desplazadas.»
            FreshTurn(session, fight, me, keep: true);
            was = Level(b1);
            int from = b1.CellId;
            CastAt(session, Ty.Imantacion, me.CellId);
            bool moved = b1.CellId != from;
            once = Level(b1);
            sc.Check("Imantación pulls the bomb in and adds 1 combo", moved && once == was + 1, $"bomb {from} -> {b1.CellId}, combo {was} -> {once}");
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Ty.Imantacion, me.CellId);
            sc.Check("a second Imantación the same turn adds none", Level(b1) == once, $"{once} -> {Level(b1)}");

            // Jugarreta: «Añade 1 combo a una bomba del lanzador, intercambia posición con ella y le aplica el estado Pesante.»
            FreshTurn(session, fight, me, keep: true);
            was = Level(b1);
            int mine = me.CellId, its = b1.CellId;
            CastAt(session, Ty.Jugarreta, b1.CellId);
            sc.Check("Jugarreta swaps with the bomb, adds 1 combo and makes it Pesante",
                     me.CellId == its && b1.CellId == mine && Level(b1) == was + 1 && b1.Buffs.TieneEstado(Ty.Pesante),
                     $"Tymador {mine} -> {me.CellId}, bomb {its} -> {b1.CellId}, combo {was} -> {Level(b1)}, Pesante {b1.Buffs.TieneEstado(Ty.Pesante)}");

            // Polvo: «Vuelve una bomba del lanzador Indesplazable. Añade 2 combos a la bomba: haciendo
            //  que explote si es destruida; eliminando los efectos del hechizo si se lanza a otra bomba.»
            FreshTurn(session, fight, me, keep: true);
            was = Level(b1);
            CastAt(session, Ty.Polvo, b1.CellId);
            sc.Check("Polvo makes the bomb unmovable and adds no combo yet",
                     b1.Buffs.TieneEstado(Ty.Unmovable) && b1.Buffs.TieneEstado(Ty.PolvoState) && Level(b1) == was,
                     $"unmovable {b1.Buffs.TieneEstado(Ty.Unmovable)}, Polvo's mark {b1.Buffs.TieneEstado(Ty.PolvoState)}, combo {was} -> {Level(b1)}");
            FreshTurn(session, fight, me, keep: true);
            int was2 = Level(b2);
            if (ToSee(session, fight, me, b2.CellId, 1, 8)) CastAt(session, Ty.Polvo, b2.CellId);
            else sc.Note($"no cell in reach of the second bomb ({b2.CellId}) sees it");
            sc.Check("Polvo cast on another bomb: the first one gets its 2 combos and loses Polvo",
                     Level(b1) == was + 2 && !b1.Buffs.TieneEstado(Ty.PolvoState) && b2.Buffs.TieneEstado(Ty.PolvoState) && Level(b2) == was2,
                     $"first {was} -> {Level(b1)} (Polvo {b1.Buffs.TieneEstado(Ty.PolvoState)}), second {was2} -> {Level(b2)} (Polvo {b2.Buffs.TieneEstado(Ty.PolvoState)})");

            // Último Aliento: «Reduce la vitalidad máxima del lanzador para añadir temporalmente 3 combos a sus bombas.»
            FreshTurn(session, fight, me, keep: true);
            var levels = BombsOf(fight, me).ToDictionary(b => b.Id, Level);
            int top = me.MaxHP;
            CastAt(session, Ty.UltimoAliento, me.CellId);
            sc.Check("Último Aliento adds 3 combos to every bomb", BombsOf(fight, me).All(b => Level(b) == Math.Min(15, levels[b.Id] + 3)),
                     string.Join(", ", BombsOf(fight, me).Select(b => $"bomb {b.Id}: {levels[b.Id]} -> {Level(b)}")));
            sc.Check("and lowers the Tymador's maximum life", me.MaxHP < top, $"maximum {top} -> {me.MaxHP}");

            // «Al principio de cada turno, el tymador dará 2 combos a todas sus bombas.»
            levels = BombsOf(fight, me).ToDictionary(b => b.Id, Level);
            bool back = PassTurn(session, fight, me);
            sc.Check("at the start of his turn every bomb has two combos more", back && BombsOf(fight, me).All(b => Level(b) == Math.Min(15, levels[b.Id] + 2)),
                     string.Join(", ", BombsOf(fight, me).Select(b => $"bomb {b.Id}: {levels.GetValueOrDefault(b.Id)} -> {Level(b)}")));
            FreshTurn(session, fight, me, keep: true);

            // Detonador: «Añade 1 combo a una bomba del lanzador y activa su explosión. Devuelve 1 PA
            //  al lanzador justo antes de la explosión.» The blow at the bomb's combo, by the sheet's table.
            if (b1.IsAlive)
            {
                NextTo(session, fight, me, b1.CellId);
                was = Level(b1);
                int ap = me.CurrentAP;
                var lines = CastAt(session, Ty.Detonador, b1.CellId);
                sc.Check("Detonador adds a combo and sets the bomb off", !b1.IsAlive && lines.Any(l => l.Contains($"[Combo] {b1.Id} is at level {Math.Min(15, was + 1)},")),
                         $"bomb {b1.Id} {(b1.IsAlive ? "still there" : "gone")}, combo lines: {string.Join(" | ", lines.Where(l => l.Contains("[Combo]")).Take(3))}");
                sc.Check("and gives back the AP it cost", me.CurrentAP == ap, $"AP {ap} -> {me.CurrentAP}");
                CheckComboLines(sc, lines, me, "the explosion hits with the bonus the sheet gives its combo");
            }

            // «Si el objetivo es otra bomba del lanzador, gana 1 combo antes de explotar.»
            FreshTurn(session, fight, me, keep: true);
            if (b2.IsAlive && MapGeometry.Distance(me.CellId, b2.CellId) >= 1)
            {
                NextTo(session, fight, me, b2.CellId);
                was = Level(b2);
                var lines = FightHandler.BoardOf(fight).Sees(me.CellId, b2.CellId) ? CastAt(session, Ty.Explobomba, b2.CellId) : new List<string>();
                sc.Check("an Explobomba cast on his own bomb adds a combo and sets it off", !b2.IsAlive && lines.Any(l => l.Contains($"[Combo] {b2.Id} is at level {Math.Min(15, was + 1)},")),
                         $"bomb {b2.Id} {(b2.IsAlive ? "still there" : "gone")} (combo {was}), lines: {string.Join(" | ", lines.Where(l => l.Contains("[Combo]")).Take(3))}");
                CheckComboLines(sc, lines, me, "the explosion hits with the bonus the sheet gives its combo");
            }

            // Polvo's other half: a bomb under it destroyed by anything but an explosion gets its
            // two combos and goes off.
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            if (ToOpenGround(session, fight, me, 4))
            {
                int cell = Along(me.CellId, 1, 0, 3);
                CastAt(session, Ty.Explobomba, cell);
                FreshTurn(session, fight, me, keep: true);
                var b3 = BombsOf(fight, me).FirstOrDefault(b => b.CellId == cell);
                if (b3 != null)
                {
                    CastAt(session, Ty.Polvo, b3.CellId);
                    NextTo(session, fight, me, b3.CellId);
                    was = Level(b3);
                    var lines = RunInSession(session, stream => FightHandler.TestKillAsync(stream, fight, b3, me));
                    sc.Check("a bomb under Polvo killed outright gets 2 combos and explodes",
                             lines.Any(l => l.Contains($"[Combo] {b3.Id} is at level {Math.Min(15, was + 2)},")),
                             $"combo {was}; lines: {string.Join(" | ", lines.Where(l => l.Contains("[Combo]") || l.Contains("explod")).Take(4)).DefaultIfEmptyText("none")}");
                    CheckComboLines(sc, lines, me, "the explosion hits with the bonus the sheet gives its combo");
                }
            }

            // «Los muros se benefician de la mitad del combo para infligir sus daños» -- the highest
            // of its bombs (the inference BombWalls writes down).
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            if (ToWallRow(session, fight, me, 4) is { } r && TwoBombs(session, fight, me, r.A, r.B) && WallCellsOf(fight).SetEquals(r.Between))
            {
                var ends = BombsOf(fight, me).Where(b => b.CellId == r.A || b.CellId == r.B).ToList();
                foreach (int round in new[] { 0, 1 })
                {
                    if (round == 1)
                    {
                        // Up the ladder: Último Aliento's three on both.
                        FreshTurn(session, fight, me, keep: true);
                        CastAt(session, Ty.UltimoAliento, me.CellId);
                    }
                    int highest = ends.Max(Level);
                    StepAside(session, fight, me, r.Between);
                    var foe = FoesOf(fight, me).OrderBy(f => MapGeometry.Distance(f.CellId, r.Between[1])).FirstOrDefault();
                    int beside = MapGeometry.GetNeighbors(r.Between[1]).Where(c => FreeCell(fight, c) && !r.Between.Contains(c) && MapGeometry.Distance(c, me.CellId) > 1)
                                            .DefaultIfEmpty(-1).First();
                    if (foe == null || beside < 0) { sc.Note("no foe or cell to walk into the wall"); break; }
                    PutAt(session, fight, foe, beside);
                    var lines = RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, foe, r.Between[1]));
                    var wallLines = lines.Where(l => Ty.ComboLine.Match(l) is { Success: true } m && long.Parse(m.Groups[1].Value) == me.Id).ToList();
                    if (highest <= 1)
                        sc.Check("a wall of Combo I bombs hits with no bonus", wallLines.Count == 0, wallLines.FirstOrDefault() ?? "no combo line");
                    else
                    {
                        sc.Check($"a wall of Combo {highest} bombs hits with half its bonus", wallLines.Count > 0, wallLines.FirstOrDefault() ?? "no combo line");
                        CheckComboLines(sc, wallLines, me, $"the wall's bonus is half its highest bomb's (Combo {highest})",
                                        (who, _) => Ty.SheetPercent[Math.Min(15, highest)] / 2);
                    }
                }
            }
            else sc.Note("no wall for the combo's half");

            // «Kabúm [...] los protege de los daños de las explosiones y de los muros. Los malus
            //  aplicados por las explosiones de las bombas se convierten en bonus que duran 3 turnos.»
            // His own bomb's blast reaches him (its rows name the allies without Kabúm).
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            if (ToOpenGround(session, fight, me, 2))
            {
                int cell = Along(me.CellId, 1, 0, 1);
                CastAt(session, Ty.Explobomba, cell);
                FreshTurn(session, fight, me, keep: true);
                var bomb = BombsOf(fight, me).FirstOrDefault(b => b.CellId == cell);
                if (bomb != null)
                {
                    CastAt(session, Ty.Kabum, me.CellId);
                    FreshTurn(session, fight, me, keep: true);
                    bool kabum = me.Buffs.TieneEstado(Managers.BombWalls.KabumState);
                    int life = me.CurrentHP;
                    var rowsBefore = me.Buffs.Puestos.Select(b => b.Numero).ToHashSet();
                    CastAt(session, Ty.Detonador, bomb.CellId);
                    var bonus = me.Buffs.Puestos.Where(b => !rowsBefore.Contains(b.Numero) && b.HechizoOrigen is 20744 or 20747 or 20748).ToList();
                    sc.Check("under Kabúm his own bomb's blast leaves him unhurt and gives him a bonus",
                             kabum && !bomb.IsAlive && me.CurrentHP == life && bonus.Count > 0,
                             $"Kabúm {(kabum ? "on" : "not on")}; bomb {(bomb.IsAlive ? "still there" : "gone")}; life {life} -> {me.CurrentHP}; " +
                             $"new bonus rows: {string.Join(", ", bonus.Select(b => $"{b.EffectId} {b.Cuanto} from {b.HechizoOrigen} until round {b.CaducaEnRonda}")).DefaultIfEmptyText("none")}");
                }
            }
        }

        /// <summary>
        /// The Tymador where he sees a cell from within a spell's reach: where he stands if he
        /// does, else the nearest free cell that does. Whether he does now.
        /// </summary>
        private static bool ToSee(GameSession session, FightInstance fight, Fighter me, int cell, int min, int max)
        {
            var board = FightHandler.BoardOf(fight);
            if (MapGeometry.Distance(me.CellId, cell) is var d && d >= min && d <= max && board.Sees(me.CellId, cell)) return true;
            int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, cell) >= min && MapGeometry.Distance(c, cell) <= max
                                                           && board.Sees(c, cell))
                                 .OrderBy(c => MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();
            if (spot < 0) return false;
            PutAt(session, fight, me, spot);
            return me.CellId == spot && FightHandler.BoardOf(fight).Sees(me.CellId, cell);
        }

        /// <summary>The nearest foe put next to a cell (a bomb about to go off), not next to the Tymador.</summary>
        private static void NextTo(GameSession session, FightInstance fight, Fighter me, int cell)
        {
            var foe = FoesOf(fight, me).OrderBy(f => MapGeometry.Distance(f.CellId, cell)).FirstOrDefault();
            if (foe == null || MapGeometry.Distance(foe.CellId, cell) <= 2) return;
            int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, cell) is >= 1 and <= 2 && c != me.CellId)
                                 .OrderBy(c => MapGeometry.Distance(c, cell)).DefaultIfEmpty(-1).First();
            if (spot >= 0) PutAt(session, fight, foe, spot);
        }
    }
}
