using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The test harness's scenarios: a class's mechanic played step by step and checked against
    /// the rules the client's own class sheet states (Translations, the sixteen «&lt;title
    /// iconSpellId» texts) -- not against what the engine happens to do. One spell tried alone
    /// never meets a bomb wall, a combo that grows or a telefrag; these do.
    ///
    /// Every cast goes the way the client's does (jwh through the session), every turn passes the
    /// way a player passes it (jxy). Only on a server started for the harness (JONDO_TEST_KEY).
    /// </summary>
    public static partial class ControlApi
    {
        private sealed class Scenario
        {
            public readonly List<object> Steps = new();
            public int Failed { get; private set; }

            /// <summary>One rule of the sheet, whether the board kept it, and what the board showed.</summary>
            public bool Check(string rule, bool ok, string observed)
            {
                Steps.Add(new { rule, ok, observed });
                if (!ok) Failed++;
                return ok;
            }

            public void Note(string what) => Steps.Add(new { note = what });
        }

        private static Respuesta TestScenario(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null || session.Stream == null) return Mal(404, "offline");
            var fight = FightHandler.FightOf(session.State.CharacterId);
            if (fight == null) return Mal(409, "no-fight");
            var me = fight.Buscar(session.State.CharacterId);
            if (me == null) return Mal(404, "fighter");
            string name = Texto(cuerpo, "name");
            long pace = Numero(cuerpo, "pace");
            _pace = pace > 0 ? (int)Math.Min(pace, 10000) : cuerpo.Contains("\"pace\":0") ? 0 : 1500;
            var sc = new Scenario();
            try
            {
                switch (name)
                {
                    case "tymador.bombs": TymadorBombs(session, fight, me, sc); break;
                    case "xelor.telefrag": XelorTelefrag(session, fight, me, sc); break;
                    case "steamer.turrets": SteamerTurrets(session, fight, me, sc); break;
                    case "forgelance.lance": ForgelanceLance(session, fight, me, sc); break;
                    case "sram.invisibility": SramInvisibility(session, fight, me, sc); break;
                    default: return Mal(404, "scenario");
                }
            }
            catch (Exception ex) { sc.Check("the scenario runs to its end", false, ex.GetType().Name + ": " + ex.Message); }
            return Bien(new { name, passed = sc.Failed == 0, failed = sc.Failed, steps = sc.Steps });
        }

        /// <summary>The scenarios there are, by class.</summary>
        private static readonly Dictionary<int, string[]> ScenariosByBreed = new()
        {
            [13] = new[] { "tymador.bombs" },
            [5] = new[] { "xelor.telefrag" },
            [15] = new[] { "steamer.turrets" },
            [20] = new[] { "forgelance.lance" },
            [4] = new[] { "sram.invisibility" },
        };

        private static Respuesta TestScenarios(string cuerpo)
        {
            int breed = (int)Numero(cuerpo, "breed");
            return Bien(ScenariosByBreed.TryGetValue(breed, out var names) ? names : Array.Empty<string>());
        }

        // ─── What a scenario plays with ───────────────────────────────────────────────

        /// <summary>
        /// How long a scenario waits after each step: long enough for the client to play the
        /// animation and for a person watching the window to follow what happens. Each scenario
        /// call sets it (the request's "pace", in milliseconds; 1500 when it says nothing).
        /// </summary>
        [ThreadStatic] private static int _pace;

        private static void Pause() { if (_pace > 0) System.Threading.Thread.Sleep(_pace); }

        private static List<string> CastAt(GameSession session, int spell, int cell)
        {
            var lines = RunAsClient(session, Op.Jwh, Pb.New().Var(1, cell).Var(4, spell).Build());
            Pause();
            return lines;
        }

        private static void FreshTurn(GameSession session, FightInstance fight, Fighter me, bool keep, bool withoutSummons = false)
        {
            RunInSession(session, stream => FightHandler.FreshTurnAsync(stream, fight, me, keep, withoutSummons));
            Pause();
        }

        private static void PutAt(GameSession session, FightInstance fight, Fighter who, int cell)
        {
            RunInSession(session, _ => FightHandler.InASequenceAsync(fight, who.Id, () => FightHandler.TeleportNearAsync(fight, who, cell)));
            if (_pace > 0) System.Threading.Thread.Sleep(Math.Min(_pace, 600));
        }

        /// <summary>
        /// Several fighters put on their cells at once, each first out of the way -- one may stand
        /// on the other's cell -- and true only when every one of them got there.
        /// </summary>
        private static bool PutAll(GameSession session, FightInstance fight, params (Fighter Who, int Cell)[] places)
        {
            var wanted = places.Select(p => p.Cell).ToHashSet();
            foreach (var (who, _) in places)
            {
                if (!wanted.Contains(who.CellId)) continue;
                int aside = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && !wanted.Contains(c))
                                      .OrderBy(c => MapGeometry.Distance(c, who.CellId)).DefaultIfEmpty(-1).First();
                if (aside >= 0) PutAt(session, fight, who, aside);
            }
            foreach (var (who, cell) in places) if (who.CellId != cell) PutAt(session, fight, who, cell);
            return places.All(p => p.Who.CellId == p.Cell);
        }

        /// <summary>The turn given up, and back: true when it came back to the character.</summary>
        private static bool PassTurn(GameSession session, FightInstance fight, Fighter me)
        {
            int round = fight.RoundNumber;
            RunAsClient(session, "jxy", Pb.New().Build());
            var until = DateTime.UtcNow.AddSeconds(150);
            while (DateTime.UtcNow < until)
            {
                System.Threading.Thread.Sleep(300);
                if (FightHandler.FightOf(session.State.CharacterId) != fight || !me.IsAlive) return false;
                if (fight.RoundNumber > round && fight.CurrentFighter?.Id == me.Id) return true;
            }
            return false;
        }

        private static bool FreeCell(FightInstance fight, int cell)
        {
            var board = FightHandler.BoardOf(fight);
            return MapGeometry.IsValid(cell) && board.Walkable(cell)
                   && !fight.Todos.Any(f => f.IsAlive && f.CellId == cell) && !fight.Glifos.Any(g => g.Cubre(cell));
        }

        /// <summary>The cell <paramref name="steps"/> along a row or a column of the grid, or -1.</summary>
        private static int Along(int cell, int dx, int dy, int steps)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return x == int.MinValue ? -1 : MapGeometry.PointToCell(x + dx * steps, y + dy * steps);
        }

        private static readonly (int Dx, int Dy)[] Rows = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        private static string Cells(IEnumerable<int> cells) => string.Join(", ", cells);

        /// <summary>A state put on a fighter for a scenario's setup, as a spell's 950 would leave it.</summary>
        private static void GiveState(FightInstance fight, Fighter caster, Fighter who, int state, int rounds)
        {
            who.Buffs.Poner(new Buff
            {
                EffectId = Jondo.Unity.World.Combat.EffectSupport.AddState, Estado = state, Quien = caster.Id,
                Disparador = Managers.EffectEngine.AlLanzar, CaducaEnRonda = fight.RoundNumber + rounds, EmpiezaEnRonda = fight.RoundNumber,
            }, fight.SiguienteEmbrujo);
            // The row alone is not the state: the engine also tells the bearer he holds it.
            who.Buffs.PonerEstado(state);
        }

        /// <summary>A row from a cell: the cells one to <paramref name="far"/> along it, walkable, or null.</summary>
        private static List<int>? RowFrom(FightInstance fight, int from, int far, Func<List<int>, bool> wanted)
        {
            var board = FightHandler.BoardOf(fight);
            foreach (var (dx, dy) in Rows)
            {
                var cells = Enumerable.Range(1, far).Select(k => Along(from, dx, dy, k)).ToList();
                if (cells.Any(c => c < 0 || !board.Walkable(c))) continue;
                if (wanted(cells)) return cells;
            }
            return null;
        }

        /// <summary>
        /// The caster put on open ground: a free cell with the four rows of the grid walkable and
        /// free for <paramref name="far"/> cells, so that a step needing cells on both sides of
        /// him finds them. False when the board has none.
        /// </summary>
        private static bool ToOpenGround(GameSession session, FightInstance fight, Fighter me, int far)
        {
            var board = FightHandler.BoardOf(fight);
            int open = Enumerable.Range(0, 560)
                .Where(c => FreeCell(fight, c) || c == me.CellId)
                .Where(c => Rows.All(d => Enumerable.Range(1, far).All(k => Along(c, d.Dx, d.Dy, k) is int x && x >= 0 && board.Walkable(x))))
                .OrderBy(c => MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();
            if (open < 0) return false;
            if (open != me.CellId) PutAt(session, fight, me, open);
            return me.CellId == open;
        }

        // ─── The Xelor's teleports (class sheet 1010380, «Las teletransportaciones») ──

        private static void XelorTelefrag(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Teletransportacion = 13249, Perturbacion = 13288, Syncro = 13247, SyncroTemplate = 3958;
            const int Telefrag = 3622, Indesplazable = 97, FinalDamage = 107;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            var foes = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado).ToList();
            if (foes.Count < 2) { sc.Check("two foes to swap and to aim at", false, $"{foes.Count} foe(s)"); return; }
            bool Usable(int c, params Fighter[] allowed) => FreeCell(fight, c) || fight.Todos.Any(f => f.IsAlive && f.CellId == c && allowed.Contains(f));
            int Cost(int spell) => DatabaseManager.GetSpellCombatData(spell, FightHandler.CastingGradeOf(me, spell))?.APCost ?? 0;
            List<Fighter> Syncros() => fight.Todos.Where(f => f.IsAlive && f.EsInvocado && f.MonsterId == SyncroTemplate && f.TeamId == me.TeamId).ToList();
            int Boosts(Fighter syncro) => syncro.Buffs.Puestos.Count(b => b.EffectId == 1171);
            string Fd(Fighter f) => $"final damage {f.Buffs.De(FinalDamage, fight.RoundNumber):+0;-0;0}% ({Boosts(f)} x +200)";

            // A telefrag made with Teletransportación: the Xelor to the cell symmetric about the
            // target, onto whoever stands there. Returns whether the row was found and played.
            bool TelefragWithTeleport(Fighter aimed, Fighter onto)
            {
                var row = RowFrom(fight, me.CellId, 4, cells => Usable(cells[1], aimed, onto) && Usable(cells[3], aimed, onto)
                                                                  && FreeCell(fight, cells[0]) && FreeCell(fight, cells[2]));
                if (row == null || !PutAll(session, fight, (aimed, row[1]), (onto, row[3])))
                {
                    sc.Note(row == null ? "no row for the telefrag" : $"could not stand {aimed.Id} on {row[1]} and {onto.Id} on {row[3]}");
                    return false;
                }
                CastAt(session, Teletransportacion, row[1]);
                return true;
            }

            // «Se generan cuando dos entidades intercambian posiciones debido a los efectos de
            //  teletransportación de un hechizo xelor. El estado telefrag se aplica durante dos
            //  turnos a los objetivos transposicionados así.» The Xelor too: the real server puts
            //  3622 on both of them («xelor-teletransportacion», after every swap).
            int start = me.CellId, ap = me.CurrentAP, round = fight.RoundNumber, cost = Cost(Teletransportacion);
            if (!TelefragWithTeleport(foes[0], foes[1])) { sc.Check("a row of four cells from the Xelor to play the telefrag on", false, "none"); return; }
            sc.Check("the Xelor and whoever stood on the symmetric cell swap (a telefrag)", foes[1].CellId == start, $"Xelor on {me.CellId}, foe {foes[1].Id} on {foes[1].CellId} (Xelor was on {start})");
            var mine = me.Buffs.Puestos.FirstOrDefault(b => b.Estado == Telefrag);
            var theirs = foes[1].Buffs.Puestos.FirstOrDefault(b => b.Estado == Telefrag);
            sc.Check("both swapped are in Telefrag for two turns", mine?.CaducaEnRonda == round + 2 && theirs?.CaducaEnRonda == round + 2,
                     $"Xelor until {mine?.CaducaEnRonda.ToString() ?? "none"}, foe until {theirs?.CaducaEnRonda.ToString() ?? "none"} (round {round})");
            // «Cuando se genera un telefrag, el lanzador gana 2 PA, 1 vez por hechizo y por turno.»
            sc.Check("the telefrag gives the Xelor 2 AP", me.CurrentAP == ap - cost + 2, $"AP {ap} -> {me.CurrentAP}, the spell costs {cost}");

            // «Solo puede haber una syncro por equipo. Si la syncro está aún presente y vuelve a ser
            //  invocada, la anterior se destruye y se sustituye por la nueva.»
            FreshTurn(session, fight, me, keep: false);
            int first = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 2).DefaultIfEmpty(-1).First();
            if (first >= 0) CastAt(session, Syncro, first);
            sc.Check("Syncro puts a syncro (3958) on its cell", Syncros().Any(f => f.CellId == first), $"syncros on {Cells(Syncros().Select(f => f.CellId))}");
            FreshTurn(session, fight, me, keep: true);
            int second = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 3).DefaultIfEmpty(-1).First();
            if (second >= 0) CastAt(session, Syncro, second);
            sc.Check("a second Syncro replaces the first: one syncro at a time", Syncros().Count == 1 && Syncros()[0].CellId == second,
                     $"syncros on {Cells(Syncros().Select(f => f.CellId))}");
            var syncro = Syncros().FirstOrDefault();
            if (syncro == null) return;
            // Fin de los Tiempos, in the game's tooltip: «-200% de daños finales» from the start.
            sc.Check("a syncro starts at -200% final damage", syncro.Buffs.De(FinalDamage, fight.RoundNumber) == -200, Fd(syncro));

            // «Aumenta los daños finales y cura del todo a la syncro cuando un xelor genera un
            //  telefrag. Los efectos solo se aplican una vez por turno y por hechizo de generación
            //  de telefrag.»
            FreshTurn(session, fight, me, keep: true);
            int boosts = Boosts(syncro);
            if (TelefragWithTeleport(foes[0], foes[1]))
                sc.Check("a telefrag made by the Xelor gives the syncro +200% final damage", Boosts(syncro) == boosts + 1, Fd(syncro));
            FreshTurn(session, fight, me, keep: true);
            boosts = Boosts(syncro);
            if (TelefragWithTeleport(foes[0], foes[1]))
                sc.Check("a second telefrag of the same spell in the same turn gives it nothing more", Boosts(syncro) == boosts, Fd(syncro));
            FreshTurn(session, fight, me, keep: true);
            boosts = Boosts(syncro);
            // Perturbación: the target to the cell symmetric about the Xelor, onto the other foe.
            ToOpenGround(session, fight, me, 2);
            var row2 = RowFrom(fight, me.CellId, 2, cells => Usable(cells[1], foes[0], foes[1]) && FreeCell(fight, cells[0]));
            var back2 = row2 == null ? null : RowFrom(fight, me.CellId, 2, cells => cells[1] == MapGeometry.Reflejar(row2[1], me.CellId)
                                                                                     && Usable(cells[1], foes[0], foes[1]));
            if (row2 != null && back2 != null)
            {
                PutAll(session, fight, (foes[0], row2[1]), (foes[1], back2[1]));
                CastAt(session, Perturbacion, row2[1]);
                sc.Check("Perturbación swaps its target with whoever stands opposite the Xelor", foes[0].CellId == back2[1] && foes[1].CellId == row2[1],
                         $"foe {foes[0].Id} on {foes[0].CellId}, foe {foes[1].Id} on {foes[1].CellId}");
                sc.Check("a telefrag of another spell grows the syncro again", Boosts(syncro) == boosts + 1, Fd(syncro));
            }
            else sc.Note("no row for Perturbación's telefrag");

            // «Se destruye para ocasionar daños de aire a los enemigos en zona a su alrededor cuando
            //  entra en el estado Telefrag. Los daños de zona no son regresivos.» (23 air, circle 3)
            FreshTurn(session, fight, me, keep: true);
            int landing = me.CellId;
            var near = foes.Where(f => f.IsAlive).ToList();
            int beside = MapGeometry.GetNeighbors(landing).Where(c => FreeCell(fight, c)).DefaultIfEmpty(-1).First();
            if (beside >= 0 && near.Count > 1) PutAt(session, fight, near[1], beside);
            var lives = foes.ToDictionary(f => f.Id, f => f.CurrentHP);
            int damage = syncro.Buffs.De(FinalDamage, fight.RoundNumber);
            if (TelefragWithTeleport(near[0], syncro))
            {
                sc.Check("the syncro swapped into Telefrag is destroyed", !syncro.IsAlive, $"syncro {syncro.Id} {(syncro.IsAlive ? "still on " + syncro.CellId : "gone")}");
                var inZone = foes.Where(f => MapGeometry.Distance(f.CellId, landing) <= 3).ToList();
                sc.Check($"its blast hurts the foes within three cells (final damage {damage:+0;-0;0}%)",
                         inZone.Count > 0 && inZone.All(f => f.CurrentHP < lives[f.Id] || !f.IsAlive),
                         string.Join(", ", foes.Select(f => $"{f.Id} at {MapGeometry.Distance(f.CellId, landing)} cells: {lives[f.Id]} -> {f.CurrentHP}")));
            }
            else sc.Note("no row to swap the Xelor with the syncro");

            // Somebody «Indesplazable» (97) is not moved by anything: no swap, no telefrag.
            FreshTurn(session, fight, me, keep: true);
            ToOpenGround(session, fight, me, 4);
            var row3 = RowFrom(fight, me.CellId, 4, cells => Usable(cells[1], foes[0], foes[1]) && Usable(cells[3], foes[0], foes[1]) && FreeCell(fight, cells[0]));
            if (row3 != null && foes.All(f => f.IsAlive))
            {
                PutAll(session, fight, (foes[0], row3[1]), (foes[1], row3[3]));
                GiveState(fight, me, foes[1], Indesplazable, 2);
                var telefragsBefore = foes[1].Buffs.Puestos.Where(x => x.Estado == Telefrag).Select(x => x.Numero).ToHashSet();
                CastAt(session, Teletransportacion, row3[1]);
                sc.Check("an Indesplazable foe on the symmetric cell is not swapped", foes[1].CellId == row3[3], $"foe {foes[1].Id} on {foes[1].CellId}");
                var newTelefrag = foes[1].Buffs.Puestos.Where(x => x.Estado == Telefrag && !telefragsBefore.Contains(x.Numero)).ToList();
                sc.Check("and is not put in Telefrag", newTelefrag.Count == 0, newTelefrag.Count == 0 ? "no new Telefrag" : $"{newTelefrag.Count} new Telefrag row(s)");
            }
            else sc.Note("no row for the Indesplazable check");
        }

        // ─── The Steamer's turrets (class sheet 1086774, «Las torretas») ───────────────

        private static void SteamerTurrets(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Arponeras = 13829, Guardianas = 13830, Evolucion = 13832;
            const int Arponera = 5836, Guardiana = 5835, EvolutionII = 134, EvolutionIII = 135;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 3);
            List<Fighter> Turrets(int template) => fight.Todos.Where(f => f.IsAlive && f.EsInvocado && f.Invocador == me.Id && f.MonsterId == template).ToList();
            string Evolution(Fighter t) => t.Buffs.TieneEstado(EvolutionIII) ? "III" : t.Buffs.TieneEstado(EvolutionII) ? "II" : "I";
            int Spot(int distance, params int[] not) => Enumerable.Range(0, 560)
                .Where(c => FreeCell(fight, c) && !not.Contains(c) && MapGeometry.Distance(c, me.CellId) == distance).DefaultIfEmpty(-1).First();

            // «Las torretas son independientes del número de invocaciones máximo del lanzador.»
            int a = Spot(2);
            CastAt(session, Arponeras, a);
            var first = Turrets(Arponera).FirstOrDefault();
            sc.Check("Arponeras puts an arponera (5836) on its cell", first?.CellId == a, $"arponeras on {Cells(Turrets(Arponera).Select(t => t.CellId))}");
            if (first == null) return;
            sc.Check("a turret takes no place among the summons", first.SummonCost == 0, $"summon cost {first.SummonCost}");
            // «Las torretas se encuentran por defecto en Evolución I.»
            sc.Check("a turret comes out at Evolución I", Evolution(first) == "I", $"Evolución {Evolution(first)}");

            // «Solo puede haber una misma torreta por equipo. Si esta torreta está aún presente y
            //  vuelve a ponerse otra igual, la anterior se destruye y se sustituye por la nueva.»
            FreshTurn(session, fight, me, keep: true);
            int b = Spot(3, a);
            CastAt(session, Arponeras, b);
            var arponeras = Turrets(Arponera);
            sc.Check("a second arponera replaces the first", arponeras.Count == 1 && arponeras[0].CellId == b && !first.IsAlive,
                     $"arponeras on {Cells(arponeras.Select(t => t.CellId))}, the first {(first.IsAlive ? "still there" : "gone")}");
            var arponera = arponeras.FirstOrDefault();
            FreshTurn(session, fight, me, keep: true);
            int g = Spot(2, a, b);
            CastAt(session, Guardianas, g);
            var guardiana = Turrets(Guardiana).FirstOrDefault();
            sc.Check("a turret of another kind stands beside it", guardiana != null && arponera?.IsAlive == true,
                     $"guardiana {(guardiana == null ? "missing" : "on " + guardiana.CellId)}, arponera {(arponera?.IsAlive == true ? "there" : "gone")}");
            if (arponera == null || guardiana == null) return;

            // «...el steamer puede usar algunos de sus hechizos para hacer que una torreta evolucione.
            //  Las torretas se encuentran por defecto en Evolución I, y pueden pasar a Evolución II y III.»
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Evolucion, arponera.CellId);
            sc.Check("Evolución takes a turret to Evolución II", Evolution(arponera) == "II", $"arponera at Evolución {Evolution(arponera)}");
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Evolucion, arponera.CellId);
            sc.Check("a turret evolves once a turn: a second Evolución the same turn does nothing", Evolution(arponera) == "II", $"arponera at Evolución {Evolution(arponera)}");

            bool back = PassTurn(session, fight, me);
            sc.Check("the turn goes round and comes back to the Steamer", back, $"round {fight.RoundNumber}");
            if (!back || !arponera.IsAlive) return;
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Evolucion, arponera.CellId);
            sc.Check("the next turn Evolución takes it to Evolución III", Evolution(arponera) == "III", $"arponera at Evolución {Evolution(arponera)}");

            // «Solo puede haber una torreta en Evolución III a la vez. Cuando haya una en Evolución III
            //  y se mejore otra que esté en Evolución II, esta pasará al nivel III, pero la primera
            //  retrocederá al nivel II.»
            if (!guardiana.IsAlive) { sc.Note("the guardiana fell before it could evolve"); return; }
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Evolucion, guardiana.CellId);
            sc.Check("another turret goes to Evolución II", Evolution(guardiana) == "II", $"guardiana at Evolución {Evolution(guardiana)}");
            back = PassTurn(session, fight, me);
            if (!back || !guardiana.IsAlive || !arponera.IsAlive) { sc.Note("a turret fell, or the turn did not come back"); return; }
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Evolucion, guardiana.CellId);
            sc.Check("it goes to Evolución III", Evolution(guardiana) == "III", $"guardiana at Evolución {Evolution(guardiana)}");
            sc.Check("and the turret that was at III goes back to II: one at III at a time", Evolution(arponera) == "II", $"arponera at Evolución {Evolution(arponera)}");
        }

        // ─── The Forgelance's lance (class sheet 998985, «La Lanza Inmortal») ───────────

        private static void ForgelanceLance(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int JabalinaRayo = 23392, Lance = 7139, Desarmado = 3361;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 3);
            Fighter? TheLance() => fight.Todos.FirstOrDefault(f => f.IsAlive && f.EsInvocado && f.Invocador == me.Id && f.MonsterId == Lance);
            // Armado is the absence of Desarmado: the client draws the lance icon from 3361 and 3589
            // (FightForgelance), and the server never sets 3360.
            bool Armed() => !me.Buffs.TieneEstado(Desarmado);
            string States() => $"Desarmado {me.Buffs.TieneEstado(Desarmado)}, beside the lance {me.Buffs.TieneEstado(3589)}";

            // «Cuando la lanza no está en el terreno, el forjalanza tiene el estado Armado.»
            sc.Check("with no lance on the ground the Forgelance is Armado", Armed(), States());

            // «En cuanto se invoca a la lanza, este pierde el estado Armado y entra en el estado Desarmado.»
            var reach = FightHandler.ReachOf(fight, me, JabalinaRayo);
            int far = Math.Clamp(3, Math.Max(1, reach.Min), Math.Max(1, reach.Max));
            int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == far).DefaultIfEmpty(-1).First();
            CastAt(session, JabalinaRayo, spot);
            var lance = TheLance();
            sc.Check("Jabalina Rayo throws the lance onto its cell", lance?.CellId == spot, lance == null ? $"no lance (aimed at {spot})" : $"lance {lance.Id} on {lance.CellId}");
            sc.Check("with the lance thrown the Forgelance is Desarmado", !Armed(), States());
            if (lance == null) return;

            // «Si el forjalanza está a 1 casilla de su lanza en línea o en diagonal, se encontrará en el estado Armado.»
            FreshTurn(session, fight, me, keep: true);
            // The lance's aura (a glyph on its square of one) covers every cell beside it: only
            // nobody may stand there.
            var board = FightHandler.BoardOf(fight);
            int beside = MapGeometry.GetNeighbors(lance.CellId)
                .Where(c => c == me.CellId || board.Walkable(c) && !fight.Todos.Any(f => f.IsAlive && f.CellId == c))
                .OrderBy(c => MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();
            if (beside >= 0)
            {
                RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, me, beside));
                Pause();
                sc.Check("walked to a cell beside its lance, the Forgelance is in reach of it (3589)", me.Buffs.TieneEstado(3589),
                         $"on {me.CellId} beside the lance on {lance.CellId}: {States()}");
            }
            else sc.Note("no free cell beside the lance");

            // «Si la lanza muere, el forjalanza recupera el estado Armado, y la lanza puede invocarse de nuevo.»
            FreshTurn(session, fight, me, keep: true, withoutSummons: true);
            sc.Check("with the lance dead the Forgelance is Armado again", Armed(), States());
            ToOpenGround(session, fight, me, 3);
            spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == far).DefaultIfEmpty(-1).First();
            CastAt(session, JabalinaRayo, spot);
            sc.Check("and the lance can be thrown again", TheLance()?.CellId == spot, TheLance() == null ? "no lance" : $"lance on {TheLance()!.CellId}");
        }

        // ─── The Sram (class sheet 1084674, «La invisibilidad») ──────────────────────

        private static void SramInvisibility(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Invisibilidad = 12913, Hurto = 12937, TrampaMiserable = 12931, Doble = 12915;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 3);
            bool Invisible(Fighter f) => Managers.MonsterTactics.IsInvisible(f);
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            if (foe == null) { sc.Check("a foe to hit", false, "none"); return; }
            int Spot(int distance, params int[] not) => Enumerable.Range(0, 560)
                .Where(c => FreeCell(fight, c) && !not.Contains(c) && MapGeometry.Distance(c, me.CellId) == distance).DefaultIfEmpty(-1).First();

            // «La invisibilidad hace que los srams se puedan hacer invisibles a los ojos de sus adversarios.»
            CastAt(session, Invisibilidad, me.CellId);
            sc.Check("Invisibilidad on his own cell makes the Sram invisible", Invisible(me), Invisible(me) ? "invisible" : "visible");

            // «Infligir daños directos disipará inmediatamente la invisibilidad.» And «cuando el sram
            //  pierde su invisibilidad ... gana un bonus de golpe crítico durante 1 turno».
            FreshTurn(session, fight, me, keep: true);
            int spot = Spot(2);
            PutAll(session, fight, (foe, spot));
            int life = foe.CurrentHP, critical = me.Buffs.De(18, fight.RoundNumber);
            CastAt(session, Hurto, foe.CellId);
            sc.Check("Hurto hurts the foe", foe.CurrentHP < life, $"life {life} -> {foe.CurrentHP}");
            sc.Check("dealing direct damage takes the invisibility off", !Invisible(me), Invisible(me) ? "still invisible" : "visible again");
            sc.Check("and losing it gives a critical bonus", me.Buffs.De(18, fight.RoundNumber) > critical,
                     $"critical bonus {critical} -> {me.Buffs.De(18, fight.RoundNumber)}");

            // «Por otra parte, los daños de trampas ... permiten infligir daños sin perder la invisibilidad.»
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Invisibilidad, me.CellId);
            FreshTurn(session, fight, me, keep: true);
            int trapCell = Spot(3, foe.CellId);
            var marks = fight.Glifos.Select(g => g.Id).ToHashSet();
            CastAt(session, TrampaMiserable, trapCell);
            var trap = fight.Glifos.FirstOrDefault(g => !marks.Contains(g.Id) && g.Cubre(trapCell));
            sc.Check("Trampa Miserable lays a trap on a free cell", trap != null, trap == null ? $"no new mark on {trapCell}" : $"mark {trap.Id} on {Cells(trap.Casillas)}");
            if (trap != null && Invisible(me))
            {
                int beside = MapGeometry.GetNeighbors(trapCell).Where(c => FreeCell(fight, c)).DefaultIfEmpty(-1).First();
                if (beside >= 0)
                {
                    PutAll(session, fight, (foe, beside));
                    life = foe.CurrentHP;
                    RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, foe, trapCell));
                    Pause();
                    // «Las trampas se activan cuando una entidad cae ... en la casilla en cuestión.»
                    sc.Check("a foe walking onto the trap sets it off and is hurt", foe.CurrentHP < life, $"life {life} -> {foe.CurrentHP}");
                    sc.Check("the trap's damage leaves the Sram invisible", Invisible(me), Invisible(me) ? "still invisible" : "visible");
                }
            }
            else if (!Invisible(me)) sc.Note("the second Invisibilidad did not take hold");

            // «Los srams tienen la habilidad de poner trampas en el terreno, pero solo en las casillas libres.»
            FreshTurn(session, fight, me, keep: true);
            marks = fight.Glifos.Select(g => g.Id).ToHashSet();
            PutAll(session, fight, (foe, Spot(2)));
            CastAt(session, TrampaMiserable, foe.CellId);
            sc.Check("no trap goes down on a cell somebody stands on", !fight.Glifos.Any(g => !marks.Contains(g.Id)), $"aimed at {foe.CellId}");

            // «...invocar un doble del lanzador, que posee exactamente las mismas características ...
            //  Ni el doble ni el conspirador pueden lanzar hechizos, solamente desplazarse.»
            FreshTurn(session, fight, me, keep: true);
            int doubleCell = Spot(2);
            CastAt(session, Doble, doubleCell);
            var doble = fight.Todos.FirstOrDefault(f => f.IsAlive && f.DoubleOf == me.Id);
            sc.Check("Doble puts a double of the Sram on its cell", doble?.CellId == doubleCell, doble == null ? "no double" : $"double {doble.Id} on {doble.CellId}");
            if (doble != null)
            {
                sc.Check("the double has the Sram's life, AP and MP", doble.MaxHP == me.MaxHP && doble.MaxAP == me.MaxAP && doble.MaxMP == me.MaxMP,
                         $"life {doble.MaxHP}/{me.MaxHP}, AP {doble.MaxAP}/{me.MaxAP}, MP {doble.MaxMP}/{me.MaxMP}");
                sc.Check("and no spell to cast", doble.HechizosDeInvocado == null || doble.HechizosDeInvocado.Count == 0,
                         $"{doble.HechizosDeInvocado?.Count ?? 0} spell(s)");
            }
        }

        // ─── The Tymador's bombs (class sheet 1010378, «Las bombas») ──────────────────

        private static void TymadorBombs(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Explobomba = 13444, Tornabombas = 13435, Detonador = 13432;
            const int ExplobombaTemplate = 3112, TornabombaTemplate = 3113;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            List<Fighter> Bombs() => fight.Todos.Where(f => f.IsAlive && f.EsInvocado && f.Invocador == me.Id && FightHandler.EsBomba(f.MonsterId)).ToList();
            sc.Check("the scenario starts with no bomb of the Tymador's", Bombs().Count == 0, $"{Bombs().Count} bomb(s)");

            // Two free cells on one row of the grid, three cells of ground between them, both in
            // the bombs' reach (1 to 6) from the Tymador.
            (int A, int B, int Dx, int Dy)? line = null;
            foreach (int a in Enumerable.Range(0, 560).Where(c => FreeCell(fight, c))
                                        .OrderBy(c => Math.Abs(MapGeometry.Distance(c, me.CellId) - 2)))
            {
                int da = MapGeometry.Distance(a, me.CellId);
                if (da < 1 || da > 4) continue;
                foreach (var (dx, dy) in Rows)
                {
                    int b = Along(a, dx, dy, 4);
                    if (b < 0 || !FreeCell(fight, b)) continue;
                    int db = MapGeometry.Distance(b, me.CellId);
                    if (db < 1 || db > 6) continue;
                    if (!Enumerable.Range(1, 3).All(k => Along(a, dx, dy, k) is int c && c >= 0 && FightHandler.BoardOf(fight).Walkable(c))) continue;
                    line = (a, b, dx, dy);
                    break;
                }
                if (line != null) break;
            }
            if (line is not { } l)
            {
                sc.Check("two free cells in a row, three apart, in the bombs' reach", false, "none found on this board");
                return;
            }
            var between = Enumerable.Range(1, 3).Select(k => Along(l.A, l.Dx, l.Dy, k)).ToList();
            sc.Note($"Tymador on {me.CellId}; bombs to go on {l.A} and {l.B}, ground between: {Cells(between)}");

            // «Cuando se invoca una bomba, esta aparece en Combo I.»
            int apBefore = me.CurrentAP;
            CastAt(session, Explobomba, l.A);
            int firstCost = apBefore - me.CurrentAP;
            var bombA = Bombs().FirstOrDefault(b => b.CellId == l.A);
            sc.Check("Explobomba puts a fire bomb (3112) on its cell", bombA?.MonsterId == ExplobombaTemplate,
                     bombA == null ? "no bomb on " + l.A : $"bomb {bombA.Id}, template {bombA.MonsterId}");
            if (bombA != null)
                sc.Check("a bomb comes out at Combo I", Managers.Combo.LevelOf(bombA) == 1, $"combo level {Managers.Combo.LevelOf(bombA)}");

            // «Cuando se invoca una bomba, aumenta en 1 el coste en PA del hechizo que la ha invocado mientras esté con vida.»
            FreshTurn(session, fight, me, keep: true);
            apBefore = me.CurrentAP;
            CastAt(session, Explobomba, l.B);
            int secondCost = apBefore - me.CurrentAP;
            var bombB = Bombs().FirstOrDefault(b => b.CellId == l.B);
            sc.Check("a second Explobomba goes down on the row", bombB != null, bombB == null ? "no bomb on " + l.B : $"bomb {bombB.Id}");
            sc.Check("with one bomb alive, Explobomba costs one AP more", secondCost == firstCost + 1, $"first cast {firstCost} AP, second {secondCost} AP");

            // «Cuando hay al menos 2 bombas alineadas y espaciadas de 2 a 6 casillas máximo, forman automáticamente un muro de bombas.»
            // The wall goes down as one glyph a cell: what counts is that the ground between is covered.
            var wallCells = fight.Glifos.Where(Managers.BombWalls.IsWall).SelectMany(g => g.Casillas).ToHashSet();
            bool wall = between.All(wallCells.Contains);
            sc.Check("two bombs in a row three cells apart raise a wall over the ground between", wall,
                     $"wall cells: {Cells(wallCells.OrderBy(c => c))}");

            // «Una entidad que se desplace en el muro o entre en él sufrirá daños.»
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, between[1])).FirstOrDefault();
            if (wall && foe != null)
            {
                // The foe beside the wall, then walked onto its middle.
                int beside = between.SelectMany(MapGeometry.GetNeighbors).Where(c => FreeCell(fight, c) && !between.Contains(c))
                                    .OrderBy(c => MapGeometry.Distance(c, between[1])).DefaultIfEmpty(-1).First();
                if (beside >= 0)
                {
                    PutAt(session, fight, foe, beside);
                    int life = foe.CurrentHP;
                    RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, foe, between[1]));
                    sc.Check("a foe walking into the wall is hurt", foe.CurrentHP < life,
                             $"foe {foe.Id} from {beside} to {foe.CellId}: life {life} -> {foe.CurrentHP}");
                }
                else sc.Note("no free cell beside the wall to walk a foe from");
            }

            // «Un tymador puede colocar hasta un máximo de tres bombas en el terreno simultáneamente, ya sean del mismo elemento o no.»
            FreshTurn(session, fight, me, keep: true);
            int third = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && !between.Contains(c))
                                  .Where(c => MapGeometry.Distance(c, me.CellId) is >= 1 and <= 6)
                                  .Where(c => MapGeometry.Distance(c, l.A) > 1 && MapGeometry.Distance(c, l.B) > 1)
                                  .DefaultIfEmpty(-1).First();
            if (third >= 0) CastAt(session, Tornabombas, third);
            var bombC = Bombs().FirstOrDefault(b => b.CellId == third);
            sc.Check("a Tornabombas puts an air bomb (3113) as the third", bombC?.MonsterId == TornabombaTemplate,
                     bombC == null ? $"no bomb on {third}" : $"bomb {bombC.Id}, template {bombC.MonsterId}");
            FreshTurn(session, fight, me, keep: true);
            int fourth = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && !between.Contains(c))
                                   .Where(c => MapGeometry.Distance(c, me.CellId) is >= 1 and <= 6)
                                   .DefaultIfEmpty(-1).First();
            int countBefore = Bombs().Count;
            if (fourth >= 0) CastAt(session, Explobomba, fourth);
            sc.Check("a fourth bomb does not go down: three at most", Bombs().Count <= 3,
                     $"{countBefore} bomb(s) before, {Bombs().Count} after casting on {fourth}");

            // «Al principio de cada turno, el tymador dará 2 combos a todas sus bombas presentes en el terreno.»
            var levels = Bombs().ToDictionary(b => b.Id, b => Managers.Combo.LevelOf(b));
            bool back = PassTurn(session, fight, me);
            sc.Check("the turn goes round and comes back to the Tymador", back, $"round {fight.RoundNumber}");
            if (back)
            {
                var after = Bombs().Where(b => levels.ContainsKey(b.Id)).ToList();
                sc.Check("at the start of his turn every bomb has two combos more",
                         after.Count > 0 && after.All(b => Managers.Combo.LevelOf(b) == Math.Min(15, levels[b.Id] + 2)),
                         string.Join(", ", after.Select(b => $"bomb {b.Id}: {levels[b.Id]} -> {Managers.Combo.LevelOf(b)}")));
            }

            // «Si una bomba está unida a otras por un muro y explota, hará explotar también a las otras bombas del muro.»
            FreshTurn(session, fight, me, keep: true);
            bombA = Bombs().FirstOrDefault(b => b.CellId == l.A);
            bombB = Bombs().FirstOrDefault(b => b.CellId == l.B);
            if (bombA != null && bombB != null)
            {
                var near = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado
                                                  && (MapGeometry.Distance(f.CellId, l.A) <= 2 || MapGeometry.Distance(f.CellId, l.B) <= 2))
                                      .ToDictionary(f => f.Id, f => f.CurrentHP);
                CastAt(session, Detonador, l.A);
                sc.Check("Detonador on a bomb makes it explode", !bombA.IsAlive, $"bomb {bombA.Id} {(bombA.IsAlive ? "still there" : "gone")}");
                sc.Check("the bomb joined to it by the wall explodes too", !bombB.IsAlive, $"bomb {bombB.Id} {(bombB.IsAlive ? "still there" : "gone")}");
                if (near.Count > 0)
                    sc.Check("foes within two cells of the exploding bombs are hurt",
                             near.All(n => fight.Buscar(n.Key) is { } f && (f.CurrentHP < n.Value || !f.IsAlive)),
                             string.Join(", ", near.Select(n => $"{n.Key}: {n.Value} -> {fight.Buscar(n.Key)?.CurrentHP}")));
                else sc.Note("no foe stood within two cells of the bombs");
            }
            else sc.Note("the wall's bombs were not both there for the explosion");

            // «La bomba también puede lanzarse sobre una casilla ocupada, en cuyo caso explota automáticamente
            //  e inflige daños de su elemento al objetivo y a las entidades en un radio de dos casillas.»
            FreshTurn(session, fight, me, keep: true);
            var target = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                    .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            if (target != null && Bombs().Count < 3)
            {
                if (MapGeometry.Distance(target.CellId, me.CellId) > 6 || MapGeometry.Distance(target.CellId, me.CellId) < 1)
                {
                    int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 3)
                                         .DefaultIfEmpty(-1).First();
                    if (spot >= 0) PutAt(session, fight, target, spot);
                }
                int life = target.CurrentHP, count = Bombs().Count;
                CastAt(session, Explobomba, target.CellId);
                sc.Check("an Explobomba cast on a foe explodes at once and hurts it", target.CurrentHP < life || !target.IsAlive,
                         $"foe {target.Id} on {target.CellId}: life {life} -> {target.CurrentHP}");
                sc.Check("and leaves no bomb behind", Bombs().Count == count, $"{count} bomb(s) before, {Bombs().Count} after");
            }
            else sc.Note(target == null ? "no foe left" : "three bombs already: no room for the occupied-cell bomb");
        }
    }
}
