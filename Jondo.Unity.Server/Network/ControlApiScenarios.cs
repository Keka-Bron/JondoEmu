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
                // In a team fight the turn may be a JondoBot's: the character's own first.
                if (fight.CurrentFighter != me && !TurnTo(session, fight, me))
                    return Bien(new { name, passed = false, failed = 1, steps = new[] { new { rule = "the turn comes to the character", ok = false, observed = $"turn of {fight.CurrentFighter?.Id}" } } });
                switch (name)
                {
                    case "tymador.bombs": TymadorBombs(session, fight, me, sc); break;
                    case "xelor.telefrag": XelorTelefrag(session, fight, me, sc); break;
                    case "steamer.turrets": SteamerTurrets(session, fight, me, sc); break;
                    case "forgelance.lance": ForgelanceLance(session, fight, me, sc); break;
                    case "sram.invisibility": SramInvisibility(session, fight, me, sc); break;
                    case "pandawa.drunk_and_carry": PandawaDrunkAndCarry(session, fight, me, sc); break;
                    case "uginak.rage": UginakRage(session, fight, me, sc); break;
                    case "ocra.charges": OcraCharges(session, fight, me, sc); break;
                    case "feca.glyphs": FecaGlyphs(session, fight, me, sc); break;
                    case "zobal.masks": ZobalMasks(session, fight, me, sc); break;
                    case "sadida.trees": SadidaTrees(session, fight, me, sc); break;
                    case "eniripsa.words": EniripsaWords(session, fight, me, sc); break;
                    case "huppermage.elements": HuppermageElements(session, fight, me, sc); break;
                    case "selatrop.portals": SelatropPortals(session, fight, me, sc); break;
                    case "zurcarak.poker": ZurcarakPoker(session, fight, me, sc); break;
                    case "yopuka.furor": YopukaFuror(session, fight, me, sc); break;
                    case "sram.trap_chain": SramTrapChain(session, fight, me, sc, pinned: false); break;
                    case "sram.trap_chain_pinned": SramTrapChain(session, fight, me, sc, pinned: true); break;
                    case "cross.unmovable": CrossUnmovable(session, fight, me, sc); break;
                    case "cross.stacking": CrossStacking(session, fight, me, sc); break;
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
            [4] = new[] { "sram.invisibility", "sram.trap_chain", "sram.trap_chain_pinned" },
            [12] = new[] { "pandawa.drunk_and_carry" },
            [18] = new[] { "uginak.rage" },
            [9] = new[] { "ocra.charges" },
            [1] = new[] { "feca.glyphs" },
            [14] = new[] { "zobal.masks" },
            [10] = new[] { "sadida.trees" },
            [7] = new[] { "eniripsa.words" },
            [17] = new[] { "huppermage.elements" },
            [16] = new[] { "selatrop.portals" },
            [6] = new[] { "zurcarak.poker" },
            [8] = new[] { "yopuka.furor" },
        };

        private static Respuesta TestScenarios(string cuerpo)
        {
            int breed = (int)Numero(cuerpo, "breed");
            var own = ScenariosByBreed.TryGetValue(breed, out var names) ? names : Array.Empty<string>();
            return Bien(own.Concat(CrossScenarios).ToArray());
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
            // The turns between, the JondoBots' of a team fight handed on as their player would.
            return TurnTo(session, fight, me) && fight.RoundNumber > round && FightHandler.FightOf(session.State.CharacterId) == fight;
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

        /// <summary>The text, or the given one when it is empty.</summary>
        private static string DefaultIfEmptyText(this string text, string instead) => text.Length == 0 ? instead : text;

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

        /// <summary>
        /// The caster on a cell from which one row of the grid runs <paramref name="length"/>
        /// cells walkable and free (the given fighters may stand on it): that row, or null.
        /// </summary>
        private static List<int>? ToRow(GameSession session, FightInstance fight, Fighter me, int length, params Fighter[] movable)
        {
            var board = FightHandler.BoardOf(fight);
            bool Usable(int c) => c >= 0 && MapGeometry.IsValid(c) && board.Walkable(c)
                                  && (FreeCell(fight, c) || movable.Any(f => f.CellId == c) || c == me.CellId);
            foreach (int start in Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) || c == me.CellId)
                                            .OrderBy(c => MapGeometry.Distance(c, me.CellId)))
                foreach (var (dx, dy) in Rows)
                {
                    var cells = Enumerable.Range(1, length).Select(k => Along(start, dx, dy, k)).ToList();
                    if (!cells.All(Usable)) continue;
                    if (start != me.CellId) PutAt(session, fight, me, start);
                    if (me.CellId != start) continue;
                    // Whoever stood on it out of the way, but the given ones.
                    return cells;
                }
            return null;
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
            // Grown, the syncro is in state 306 for a turn, which its growth asks it not to be in
            // ("e306"): once a turn, whatever the spell. Another spell grows it on the next one.
            bool turnBack = PassTurn(session, fight, me);
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
                sc.Check("next turn, a telefrag of another spell grows the syncro again", Boosts(syncro) == boosts + 1, $"{Fd(syncro)} (turn back {turnBack})");
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

        // ─── The Pandawa (class sheet 969387, «Los estados Sobrio y Borracho») ──────

        private static void PandawaDrunkAndCarry(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Tranka = 12780, Karcham = 12787, Pandikulacion = 12793, Pandatak = 12797, Bambu = 12824;
            const int Sobrio = 3531, Borracho = 498;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 4);
            string Mood() => me.Buffs.TieneEstado(Borracho) ? "Borracho" : me.Buffs.TieneEstado(Sobrio) ? "Sobrio" : "neither";
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            if (foe == null) { sc.Check("a foe to carry", false, "none"); return; }
            int Beside() => MapGeometry.GetNeighbors(me.CellId).Where(c => FreeCell(fight, c) || c == foe.CellId).DefaultIfEmpty(-1).First();

            // The fight starts him Sobrio: his passive (La Jarrita de Pandawa, 12830) casts Sobrio
            // (24037) at the start. Earlier tries may have left him either way: Sobrio again, as
            // the passive puts it.
            bool started = Mood() == "Sobrio";
            if (!started)
            {
                RunInSession(session, stream => FightHandler.AplicarEfectosAsync(stream, fight, me, 24037, 1, me, Managers.EffectEngine.AlLanzar, me.CellId));
                Pause();
            }
            sc.Check("the Pandawa is Sobrio (as his passive starts him)", Mood() == "Sobrio", Mood() + (started ? "" : " (set as the passive does)"));

            // «El estado Borracho reduce la movilidad del pandawa retirándole un PM.»
            int mp = me.MaxMP + me.Buffs.De(23, fight.RoundNumber);
            CastAt(session, Tranka, me.CellId);
            sc.Check("Tranka makes the Pandawa Borracho", Mood() == "Borracho", Mood());
            int drunkMp = me.MaxMP + me.Buffs.De(23, fight.RoundNumber);
            sc.Check("Borracho takes a movement point off him", drunkMp == mp - 1, $"MP {mp} -> {drunkMp}");

            // «Estar en el estado Borracho impide portar a los aliados o los enemigos.»
            FreshTurn(session, fight, me, keep: true);
            PutAll(session, fight, (foe, Beside()));
            CastAt(session, Karcham, foe.CellId);
            sc.Check("Borracho, Karcham does not carry", me.Carrying == 0, me.Carrying == 0 ? "carries nobody" : $"carries {me.Carrying}");

            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Tranka, me.CellId);
            sc.Check("Tranka again makes him Sobrio", Mood() == "Sobrio", Mood());

            // «El objetivo solo puede ser portado en cuerpo a cuerpo.»
            FreshTurn(session, fight, me, keep: true);
            PutAll(session, fight, (foe, Beside()));
            CastAt(session, Karcham, foe.CellId);
            sc.Check("Sobrio, Karcham carries the foe beside him", me.Carrying == foe.Id && foe.CarriedBy == me.Id,
                     $"carrying {me.Carrying}, foe carried by {foe.CarriedBy}");
            // «Cuando portan a una entidad, ganan potencia. Este bonus cambia con el nivel del pandawa.»
            var power = me.Buffs.Puestos.Where(b => b.EffectId == 138).ToList();
            sc.Check("carrying gives him power", me.Carrying == foe.Id && power.Count > 0,
                     power.Count == 0 ? "no power row" : string.Join(", ", power.Select(b => $"+{b.Cuanto} until round {b.CaducaEnRonda}")));
            // The passive's grade by level (12830: grade 6 from level 187) casts 24040 at grade 10: +50.
            if (me.Level >= 187)
                sc.Check("at his level the power is the passive's highest (+50)", power.Any(b => b.Cuanto == 50),
                         string.Join(", ", power.Select(b => $"+{b.Cuanto}")));

            if (me.Carrying == foe.Id)
            {
                // «Cuando un pandawa porta una entidad, ya no puede utilizar sus hechizos, excepto los
                //  que le permiten lanzar a la entidad en cuestión.»
                FreshTurn(session, fight, me, keep: true);
                var other = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && f != foe && !f.EsInvocado)
                                       .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
                if (other != null)
                {
                    int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 3).DefaultIfEmpty(-1).First();
                    PutAll(session, fight, (other, spot));
                    int life = other.CurrentHP, ap = me.CurrentAP;
                    CastAt(session, Pandatak, other.CellId);
                    sc.Check("carrying, he cannot cast Pandatak", other.CurrentHP == life && me.CurrentAP == ap,
                             $"foe life {life} -> {other.CurrentHP}, AP {ap} -> {me.CurrentAP}");
                }
                // «...después puede ser lanzado a mayor o menor distancia.»
                FreshTurn(session, fight, me, keep: true);
                int landing = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 3).DefaultIfEmpty(-1).First();
                CastAt(session, Pandikulacion, landing);
                sc.Check("Pandikulación throws the carried foe onto its cell", foe.CellId == landing && foe.CarriedBy == 0 && me.Carrying == 0,
                         $"foe on {foe.CellId} (aimed {landing}), carried by {foe.CarriedBy}, Pandawa carrying {me.Carrying}");
            }

            // «Si el pandawa porta uno de sus toneles o bambús, recupera inmediatamente 1 PA.»
            FreshTurn(session, fight, me, keep: true, withoutSummons: true);
            int bambooCell = Beside();
            CastAt(session, Bambu, bambooCell);
            var bamboo = fight.Todos.FirstOrDefault(f => f.IsAlive && f.EsInvocado && f.Invocador == me.Id && f.CellId == bambooCell);
            sc.Check("Bambú puts a bamboo beside him", bamboo != null, bamboo == null ? $"nothing on {bambooCell}" : $"bamboo {bamboo.Id} ({bamboo.MonsterId})");
            if (bamboo != null)
            {
                FreshTurn(session, fight, me, keep: true);
                int apBefore = me.CurrentAP, cost = DatabaseManager.GetSpellCombatData(Karcham, FightHandler.CastingGradeOf(me, Karcham))?.APCost ?? 1;
                CastAt(session, Karcham, bamboo.CellId);
                sc.Check("carrying his own bamboo gives the AP back at once", me.Carrying == bamboo.Id && me.CurrentAP == apBefore - cost + 1,
                         $"carrying {me.Carrying}, AP {apBefore} -> {me.CurrentAP}, Karcham costs {cost}");
            }
        }

        // ─── The Uginak (class sheet 1010381, «El estado Presa») ─────────────────────

        private static void UginakRage(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Presa = 13748, Mandibula = 13787, Calcaneo = 13788;
            const int PresaState = 516, Rage1 = 513, Rage2 = 514, Raging = 515, Beast = 517;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 5);
            string Rage() => me.Buffs.TieneEstado(Beast) ? "beast" : me.Buffs.TieneEstado(Rage2) ? "rage 2" : me.Buffs.TieneEstado(Rage1) ? "rage 1" : "none";
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            if (foe == null) { sc.Check("a foe", false, "none"); return; }
            int At(int distance) => Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == distance).DefaultIfEmpty(-1).First();

            // «El estado Presa puede aplicarse mediante los hechizos Presa y Animal de Caza.»
            PutAll(session, fight, (foe, At(3)));
            CastAt(session, Presa, foe.CellId);
            sc.Check("Presa puts the foe in the Presa state", foe.Buffs.TieneEstado(PresaState), foe.Buffs.TieneEstado(PresaState) ? "Presa" : "no Presa");
            sc.Check("the scenario starts with no rage", Rage() == "none", Rage());

            // «La rabia es un indicador que puede ir del 1 al 3 ... Cuando llega al estado de rabia 3,
            //  el uginak se transforma y pasa a la Forma Bestial.»
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Mandibula, me.CellId);
            sc.Check("a rage spell (Mandíbula) puts him at rage 1", Rage() == "rage 1", Rage());
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Mandibula, me.CellId);
            sc.Check("a second one at rage 2", Rage() == "rage 2", Rage());
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Mandibula, me.CellId);
            sc.Check("a third one turns him into the beast", Rage() == "beast", Rage());
            sc.Check("and the beast holds no rage state any more", !me.Buffs.TieneEstado(Rage1) && !me.Buffs.TieneEstado(Rage2) && !me.Buffs.TieneEstado(Raging),
                     $"513 {me.Buffs.TieneEstado(Rage1)}, 514 {me.Buffs.TieneEstado(Rage2)}, 515 {me.Buffs.TieneEstado(Raging)}");
            if (Rage() != "beast") return;

            // «...solo puede atacar hasta 2 AL máximo. Sus hechizos elementales con los que puede
            //  desplazarse o desplazar a un objetivo no pierden alcance con esta transformación.»
            // Despiece (1-4) is pinned to 2 by the beast's rows; Calcáneo pulls, and keeps its 12.
            const int Despiece = 13804;
            FreshTurn(session, fight, me, keep: true);
            PutAll(session, fight, (foe, At(4)));
            int life = foe.CurrentHP, ap = me.CurrentAP;
            CastAt(session, Despiece, foe.CellId);
            sc.Check("the beast cannot hit four cells away with Despiece (range 4 pinned to 2)", foe.CurrentHP == life && me.CurrentAP == ap,
                     $"life {life} -> {foe.CurrentHP}, AP {ap} -> {me.CurrentAP}");
            FreshTurn(session, fight, me, keep: true);
            PutAll(session, fight, (foe, At(2)));
            life = foe.CurrentHP;
            CastAt(session, Despiece, foe.CellId);
            sc.Check("but hits two cells away", foe.CurrentHP < life, $"life {life} -> {foe.CurrentHP}");
            FreshTurn(session, fight, me, keep: true);
            PutAll(session, fight, (foe, At(5)));
            life = foe.CurrentHP;
            CastAt(session, Calcaneo, foe.CellId);
            sc.Check("Calcáneo, which pulls, keeps its range: it hits five cells away", foe.CurrentHP < life, $"life {life} -> {foe.CurrentHP}");

            // «La transformación en bestia dura dos turnos ... Al acabar el segundo turno de
            //  transformación, retoma su forma normal y pierde todos sus estados de rabia.»
            bool back = PassTurn(session, fight, me) && PassTurn(session, fight, me);
            sc.Check("two turns on, the turns came back to him", back, $"round {fight.RoundNumber}");
            if (back)
                sc.Check("and he is himself again, with no rage", Rage() == "none", Rage());
        }

        // ─── The Ocra (class sheet 1010304, «Los hechizos con carga») ──────────────────

        private static void OcraCharges(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int FlechaInmovilizadora = 32436;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 5);
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            if (foe == null) { sc.Check("a foe", false, "none"); return; }
            int At(int distance) => Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == distance).DefaultIfEmpty(-1).First();
            int Charge(int spell) => me.Buffs.DelHechizo(spell, SpellAspect.DanoBase, fight.RoundNumber);

            // «Algunos hechizos se cargan cuando se lanzan a un objetivo ... sucesivamente.»
            PutAll(session, fight, (foe, At(3)));
            int before = Charge(FlechaInmovilizadora);
            CastAt(session, FlechaInmovilizadora, foe.CellId);
            int once = Charge(FlechaInmovilizadora);
            sc.Check("Flecha Inmovilizadora charges itself when cast", once > before, $"base damage bonus {before} -> {once}");
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, FlechaInmovilizadora, foe.CellId);
            int twice = Charge(FlechaInmovilizadora);
            sc.Check("cast again it charges further", twice > once, $"base damage bonus {once} -> {twice}");

            // «...muchos de sus hechizos tienen un alcance mínimo, es decir, solo pueden lanzarse si
            //  el lanzador está lo bastante alejado de sus objetivos.»
            var spells = Managers.SpellTable.PairsOf(me.Breed).SelectMany(p => new[] { p.Base, p.Variant })
                                   .Where(sp => sp != 0 && Managers.SpellTable.GradeFor(sp, me.Level) > 0).ToList();
            int withMinimum = spells.FirstOrDefault(sp => FightHandler.ReachOf(fight, me, sp).Min >= 2
                                                          && Managers.SpellEffects.De(sp, FightHandler.CastingGradeOf(me, sp)).Any(e => e.EffectId >= 91 && e.EffectId <= 100));
            if (withMinimum == 0) { sc.Note("no damage spell with a minimum range of two or more"); return; }
            var reach = FightHandler.ReachOf(fight, me, withMinimum);
            FreshTurn(session, fight, me, keep: true);
            int beside = MapGeometry.GetNeighbors(me.CellId).Where(c => FreeCell(fight, c)).DefaultIfEmpty(-1).First();
            PutAll(session, fight, (foe, beside));
            int life = foe.CurrentHP, ap = me.CurrentAP;
            CastAt(session, withMinimum, foe.CellId);
            sc.Check($"spell {withMinimum} (range {reach.Min}-{reach.Max}) cannot be cast at a foe beside him", foe.CurrentHP == life && me.CurrentAP == ap,
                     $"life {life} -> {foe.CurrentHP}, AP {ap} -> {me.CurrentAP}");
            FreshTurn(session, fight, me, keep: true);
            PutAll(session, fight, (foe, At(Math.Min(reach.Max, Math.Max(reach.Min, 3)))));
            life = foe.CurrentHP;
            CastAt(session, withMinimum, foe.CellId);
            sc.Check("but can within its range", foe.CurrentHP < life, $"foe {MapGeometry.Distance(foe.CellId, me.CellId)} cells away: life {life} -> {foe.CurrentHP}");
        }

        // ─── The Feca (class sheet 1076523, «Las protecciones») ───────────────────────

        private static void FecaGlyphs(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int TierraQuemada = 12985, TierraBatida = 12987, Trashumancia = 12986;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 4);
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            if (foe == null) { sc.Check("a foe", false, "none"); return; }
            int At(int distance, int from) => Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, from) == distance).DefaultIfEmpty(-1).First();

            // «Los glifos elementales: glifos aura ... También infligen daños a los enemigos que
            //  empiecen su turno en su interior.» Tierra Quemada on the foe, the Feca within reach of it.
            PutAll(session, fight, (foe, At(2, me.CellId)));
            var marks = fight.Glifos.Select(g => g.Id).ToHashSet();
            CastAt(session, TierraQuemada, foe.CellId);
            var laid = fight.Glifos.Where(g => !marks.Contains(g.Id)).ToList();
            sc.Check("Tierra Quemada lays its glyphs over the foe", laid.Any(g => g.Cubre(foe.CellId)),
                     laid.Count == 0 ? "no new mark" : string.Join(" | ", laid.Select(g => $"mark {g.Id} on {g.Casillas.Count} cells, covers the foe {g.Cubre(foe.CellId)}, the Feca {g.Cubre(me.CellId)}")));

            // «Cada tipo de glifo puede ponerse una vez por turno como máximo.» No refresh: the same turn.
            marks = fight.Glifos.Select(g => g.Id).ToHashSet();
            int ap = me.CurrentAP;
            int other = At(3, me.CellId);
            CastAt(session, TierraBatida, other);
            sc.Check("a second elemental glyph does not go down the same turn", !fight.Glifos.Any(g => !marks.Contains(g.Id)),
                     $"AP {ap} -> {me.CurrentAP}, new marks {fight.Glifos.Count(g => !marks.Contains(g.Id))}");

            // «Mientras un feca esté en sus glifos elementales, gana resistencias en el elemento del glifo.»
            bool inside = laid.Any(g => g.Cubre(me.CellId));
            if (inside)
            {
                // The aura's armour: Tierra Quemada's Armadura Incandescente, fire resistance.
                var armour = me.Buffs.Puestos.Where(b => b.EffectId is >= 210 and <= 214 && b.Cuanto > 0).ToList();
                sc.Check("standing in his elemental glyph the Feca gains resistance of its element", armour.Count > 0,
                         armour.Count == 0 ? "no resistance row" : string.Join(", ", armour.Select(b => $"{b.EffectId} +{b.Cuanto}% (spell {b.HechizoOrigen})")));
            }
            else sc.Note("the Feca does not stand inside the glyph");

            // «Utilizar los hechizos Trashumancia o Posición Defensiva para activar los glifos
            //  elementales ocasiona daños inmediatos a todos los enemigos en su interior.»
            FreshTurn(session, fight, me, keep: true);
            int life = foe.CurrentHP;
            // Into the glyph: what activates is what the Feca lands in (the capture of the
            // Xelor's Inestabilidad: the glyphs its activator stands in).
            var board = FightHandler.BoardOf(fight);
            int hop = Enumerable.Range(0, 560).Where(c => board.Walkable(c) && !fight.Todos.Any(f => f.IsAlive && f.CellId == c)
                                                           && MapGeometry.Distance(c, me.CellId) is >= 1 and <= 6 && laid.Any(g => g.Cubre(c)))
                                     .OrderBy(c => MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();
            CastAt(session, Trashumancia, hop);
            sc.Check("Trashumancia into the glyph sets it off: the foe inside is hurt at once", foe.CurrentHP < life,
                     $"Feca on {me.CellId} (aimed {hop}); foe life {life} -> {foe.CurrentHP}");

            // And a foe that starts its turn inside it.
            if (foe.IsAlive && laid.Any(g => g.Cubre(foe.CellId)))
            {
                life = foe.CurrentHP;
                bool back = PassTurn(session, fight, me);
                sc.Check("the foe starting its turn inside the glyph is hurt", back && foe.CurrentHP < life || !foe.IsAlive,
                         $"life {life} -> {foe.CurrentHP} (turn back {back})");
            }
        }

        // ─── The Sram's traps, one pushing into the next ─────────────────────────────

        /// <summary>
        /// Two Trampas Repulsivas, the second beside the row the first pushes along: a foe walking
        /// onto the first's arm is pushed two cells away from its centre, lands on the second's
        /// side arm, which goes off in turn and pushes him two cells away from ITS centre --
        /// sideways. The displacements pile up: two along, two across, each trap's blow dealt.
        /// Pinned (Indesplazable, 97), the first only hurts him: he stays on its arm and the
        /// second waits untouched.
        /// </summary>
        private static void SramTrapChain(GameSession session, FightInstance fight, Fighter me, Scenario sc, bool pinned)
        {
            const int TrampaRepulsiva = 12914, Indesplazable = 97;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderByDescending(f => f.CurrentHP).FirstOrDefault();
            if (foe == null) { sc.Check("a foe", false, "none"); return; }
            var board = FightHandler.BoardOf(fight);
            bool Room(int c) => c >= 0 && MapGeometry.IsValid(c) && board.Walkable(c) && (FreeCell(fight, c) || c == foe.CellId);
            // A row of six from the Sram, and a side of it with room: the first trap's centre on
            // 2 (arms 1 and 3), the foe on 4 walking onto 3, pushed to 5; the second's centre
            // beside 5, its arm on 5; the foe pushed from 5 two cells the other way.
            List<int>? row = null;
            (int Dx, int Dy) side = (0, 0);
            for (int tries = 0; tries < 3 && row == null; tries++)
            {
                var candidate = ToRow(session, fight, me, 6, foe);
                if (candidate == null) break;
                var (rx, ry) = MapGeometry.CellToPoint(candidate[0]);
                var (sx, sy) = MapGeometry.CellToPoint(me.CellId);
                var along = (Dx: Math.Sign(rx - sx), Dy: Math.Sign(ry - sy));
                foreach (var s2 in new[] { (along.Dy, along.Dx), (-along.Dy, -along.Dx) })
                {
                    int r5 = candidate[4];
                    if (Room(Along(r5, s2.Item1, s2.Item2, 1)) && Room(Along(r5, -s2.Item1, -s2.Item2, 1)) && Room(Along(r5, -s2.Item1, -s2.Item2, 2)))
                    { row = candidate; side = s2; break; }
                }
                if (row == null) { var away = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) > 4).FirstOrDefault(); PutAt(session, fight, me, away); }
            }
            if (row == null) { sc.Check("a free row of six cells with room on a side", false, "none on this board"); return; }
            int Cell(int k) => row[k - 1];
            int secondCentre = Along(Cell(5), side.Dx, side.Dy, 1);
            int landing = Along(Cell(5), -side.Dx, -side.Dy, 2);
            var reach = FightHandler.ReachOf(fight, me, TrampaRepulsiva);
            int sram = me.CellId;
            foreach (int centre in new[] { Cell(2), secondCentre })
            {
                int d = MapGeometry.Distance(me.CellId, centre);
                if (d < reach.Min || d > reach.Max || !board.Sees(me.CellId, centre))
                {
                    int beside = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && !row.Contains(c) && c != landing && c != secondCentre
                                     && MapGeometry.Distance(c, centre) >= Math.Max(1, reach.Min) && MapGeometry.Distance(c, centre) <= reach.Max
                                     && board.Sees(c, centre))
                                 .OrderBy(c => MapGeometry.Distance(c, centre)).DefaultIfEmpty(-1).First();
                    if (beside >= 0) PutAt(session, fight, me, beside);
                }
                FreshTurn(session, fight, me, keep: true);
                var marks = fight.Glifos.Select(g => g.Id).ToHashSet();
                CastAt(session, TrampaRepulsiva, centre);
                var laid = fight.Glifos.FirstOrDefault(g => !marks.Contains(g.Id));
                if (!sc.Check($"Trampa Repulsiva lays a trap centred on cell {centre}", laid != null,
                              laid == null ? "no new mark" : $"mark {laid.Id} on {Cells(laid.Casillas.OrderBy(c => c))}")) return;
            }
            var first = fight.Glifos.First(g => g.Cubre(Cell(2)));
            var second = fight.Glifos.First(g => g.Cubre(secondCentre));
            sc.Check("the second trap's arm lies where the first one's push ends", second.Cubre(Cell(5)) && !second.Cubre(Cell(4)),
                     $"second trap on {Cells(second.Casillas.OrderBy(c => c))}; the push goes through {Cell(4)} to {Cell(5)}");
            // The Sram off the foe's way, the foe at 4, walking onto the first trap's arm at 3.
            var path = new HashSet<int> { Cell(3), Cell(4), Cell(5), landing, Along(Cell(5), -side.Dx, -side.Dy, 1) };
            if (path.Contains(me.CellId) || fight.Glifos.Any(g => g.Cubre(me.CellId)))
            {
                int aside = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && !path.Contains(c) && !fight.Glifos.Any(g => g.Cubre(c)))
                                      .OrderBy(c => MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();
                if (aside >= 0) PutAt(session, fight, me, aside);
            }
            if (!PutAll(session, fight, (foe, Cell(4)))) { sc.Check("the foe on the row by the first trap", false, $"foe on {foe.CellId}"); return; }
            if (pinned) GiveState(fight, me, foe, Indesplazable, 2);
            int life = foe.CurrentHP;
            RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, foe, Cell(3)));
            Pause();
            bool firstGone = !fight.Glifos.Contains(first), secondGone = !fight.Glifos.Contains(second);
            sc.Check("walking onto the first trap's arm sets it off and hurts him", firstGone && foe.CurrentHP < life,
                     $"first trap {(firstGone ? "gone" : "still there")}, life {life} -> {foe.CurrentHP}");
            if (pinned)
            {
                sc.Check("pinned, he stays on the cell he walked onto", foe.CellId == Cell(3), $"on cell {foe.CellId} (walked onto {Cell(3)})");
                sc.Check("and the second trap waits, untouched", !secondGone, secondGone ? "gone" : "still there");
                return;
            }
            sc.Check("pushed two cells along the row onto the second trap's arm, it goes off too", secondGone,
                     $"second trap {(secondGone ? "gone" : "still there")}");
            sc.Check("and it pushes him two cells away from its own centre: sideways, the two moves piled up", foe.CellId == landing,
                     $"on cell {foe.CellId} (expected {landing}: two along from {Cell(3)} to {Cell(5)}, two across)");
            sc.Note($"life {life} -> {foe.CurrentHP}: two traps' blows");
        }

        // ─── The Sadida (class sheet 1087857, «Los árboles», «Las muñecas», «La infección») ──

        private static void SadidaTrees(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Arbol = 13519, LaLoca = 13564, Zarza = 13516;
            const int Tree = 5894, Doll = 5896, Frondoso = 256, Infectado = 263;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 4);
            List<Fighter> Trees() => fight.Todos.Where(f => f.IsAlive && f.EsInvocado && f.Invocador == me.Id && f.MonsterId == Tree).ToList();
            int Spot() => Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) is >= 2 and <= 4)
                                    .OrderBy(c => MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();

            // «Un árbol se convierte naturalmente en Frondoso un turno después de su invocación.»
            int at = Spot();
            CastAt(session, Arbol, at);
            var tree = Trees().FirstOrDefault(t => t.CellId == at);
            if (!sc.Check("Árbol summons a tree on a free cell", tree != null, tree == null ? $"nothing on {at}" : $"tree {tree.Id} on {at}, {tree.MaxHP} life")) return;
            sc.Check("a new tree is not Frondoso yet", !tree!.Buffs.TieneEstado(Frondoso), tree.Buffs.TieneEstado(Frondoso) ? "Frondoso already" : "not Frondoso");
            int life = tree.MaxHP;
            bool back = PassTurn(session, fight, me);
            sc.Check("a turn later it is Frondoso", back && tree.IsAlive && tree.Buffs.TieneEstado(Frondoso), $"turn back {back}, Frondoso {tree.Buffs.TieneEstado(Frondoso)}");
            // «Cuando un árbol se vuelve frondoso, su vitalidad se duplica, pero ya no puede ser atraído ni empujado.»
            sc.Check("Frondoso, its life doubles", tree.MaxHP == life * 2, $"maximum life {life} -> {tree.MaxHP}");
            sc.Check("and it can no longer be pushed or pulled", Managers.SpellStates.Of(Frondoso)?.CantBePushed == true && tree.Buffs.TieneEstado(Frondoso),
                     $"state {Frondoso} cantBePushed {Managers.SpellStates.Of(Frondoso)?.CantBePushed}");

            // A doll from a tree: «Las muñecas invocadas a partir de un árbol se volverán a convertir
            // en árbol cuando mueran. Si el árbol era frondoso, el resultante ... también lo será.»
            FreshTurn(session, fight, me, keep: true);
            int treeCell = tree.CellId;
            CastAt(session, LaLoca, treeCell);
            var doll = fight.Todos.FirstOrDefault(f => f.IsAlive && f.EsInvocado && f.Invocador == me.Id && f.CellId == treeCell && f.MonsterId != Tree);
            if (sc.Check("La Loca cast on the tree replaces it with the doll", doll != null && !tree.IsAlive,
                         doll == null ? $"no doll on {treeCell}; tree alive {tree.IsAlive}" : $"monster {doll.MonsterId} on {treeCell} (La Loca is {Doll}); tree alive {tree.IsAlive}"))
            {
                RunInSession(session, stream => FightHandler.TestKillAsync(stream, fight, doll!, me));
                Pause();
                var again = Trees().FirstOrDefault(t => t.CellId == treeCell);
                sc.Check("the doll dying turns back into a tree", again != null, again == null ? $"nothing on {treeCell}" : $"tree {again.Id}");
                if (again != null) sc.Check("a Frondoso one, as the tree was", again.Buffs.TieneEstado(Frondoso), again.Buffs.TieneEstado(Frondoso) ? "Frondoso" : "not Frondoso");
            }

            // «...solo pueden tener como máximo 6 árboles en el terreno. Cuando alguno de ellos planta
            // un séptimo árbol, este provoca la muerte del primero.»
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            Fighter? firstTree = null;
            for (int k = 0; k < 7; k++)
            {
                FreshTurn(session, fight, me, keep: true);
                int cell = Spot();
                if (cell < 0) cell = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c)).OrderBy(c => MapGeometry.Distance(c, me.CellId)).First();
                CastAt(session, Arbol, cell);
                firstTree ??= Trees().FirstOrDefault();
            }
            var trees = Trees();
            sc.Check("seven planted, six stand", trees.Count == 6, $"{trees.Count} tree(s)");
            sc.Check("and the first one is the one gone", firstTree != null && !firstTree.IsAlive, firstTree == null ? "no first tree" : $"first tree {firstTree.Id} alive {firstTree.IsAlive}");

            // «Cuando un hechizo monobjetivo del sadida alcanza a un enemigo infectado, los efectos del
            // hechizo afectan también a los demás enemigos infectados.»
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            var foes = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado).OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).Take(2).ToList();
            if (foes.Count < 2) { sc.Note("one foe only: infection's spread not tried"); return; }
            // Zarza infects whom it hits (its 950 263 on A); each foe hit once.
            int a = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 3).DefaultIfEmpty(-1).First();
            PutAll(session, fight, (foes[0], a));
            int b = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 3 && MapGeometry.Distance(c, a) >= 3).DefaultIfEmpty(-1).First();
            PutAll(session, fight, (foes[1], b));
            foreach (var foe in foes)
            {
                FreshTurn(session, fight, me, keep: true);
                CastAt(session, Zarza, foe.CellId);
            }
            sc.Check("Zarza leaves each foe it hits infected", foes.All(f => f.Buffs.TieneEstado(Infectado)),
                     string.Join(", ", foes.Select(f => $"{f.Id} infected {f.Buffs.TieneEstado(Infectado)}")));
            // A single-target spell on one: the other infected one, three cells away, takes it too.
            FreshTurn(session, fight, me, keep: true);
            int other = foes[1].CurrentHP;
            CastAt(session, Zarza, foes[0].CellId);
            sc.Check("a single-target spell on one infected foe reaches the other infected one", foes[1].CurrentHP < other,
                     $"the other's life {other} -> {foes[1].CurrentHP}");
        }


        // ─── The Eniripsa (class sheet 1063431, «Los ánimos y las burlas») ──────────

        /// <summary>
        /// «No se puede acumular más de 1 ánimo por aliado: si se aplica un nuevo ánimo a un objetivo
        /// ya Animado, entonces el nuevo bonus remplaza al antiguo. También sucede igual con las
        /// burlas.» And «cuando se usan estos hechizos en espacio vacío, sirven para invocar hadas
        /// (ánimos) y fuegos fatuos (burlas)».
        /// </summary>
        private static void EniripsaWords(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Estimulante = 25742, Revitalizante = 25744, Declive = 25746, Deprimente = 25748;
            const int Animado = 4157, Burlado = 4163, Fairy = 7372;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 4);
            // A word's rows are its children's -- Palabra Estimulante's bonus is 25761's --: the
            // spell and every spell it casts, down its chain.
            HashSet<int> Family(int spell)
            {
                var family = new HashSet<int> { spell };
                var queue = new Queue<(int Spell, int Grade)>();
                queue.Enqueue((spell, FightHandler.CastingGradeOf(me, spell)));
                while (queue.Count > 0 && family.Count < 40)
                {
                    var (s1, g1) = queue.Dequeue();
                    foreach (var e in Managers.SpellEffects.De(s1, g1))
                        if (Managers.EffectEngine.CastsASpell(e) && e.DiceNum > 0 && family.Add(e.DiceNum))
                            queue.Enqueue((e.DiceNum, e.DiceSide > 0 ? e.DiceSide : 1));
                }
                return family;
            }
            string RowsOf(Fighter who, int spell) { var f = Family(spell); return string.Join(", ", who.Buffs.Puestos.Where(r => f.Contains(r.HechizoOrigen))
                                                                       .Select(r => r.Estado != 0 ? $"state {r.Estado}" : $"{r.EffectId} {r.Cuanto:+0;-0;0}")); }
            bool Has(Fighter who, int spell) { var f = Family(spell); return who.Buffs.Puestos.Any(r => f.Contains(r.HechizoOrigen) && r.Estado == 0); }

            // An ánimo on himself, then another: the first one's bonus goes.
            CastAt(session, Estimulante, me.CellId);
            sc.Check("Palabra Estimulante leaves him Animado with its bonus", me.Buffs.TieneEstado(Animado) && Has(me, Estimulante),
                     $"Animado {me.Buffs.TieneEstado(Animado)}; its rows: {RowsOf(me, Estimulante)}");
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Revitalizante, me.CellId);
            sc.Check("Palabra Revitalizante gives its own bonus", Has(me, Revitalizante), $"its rows: {RowsOf(me, Revitalizante)}");
            sc.Check("and Palabra Estimulante's is gone: one ánimo at a time", !Has(me, Estimulante),
                     Has(me, Estimulante) ? $"still: {RowsOf(me, Estimulante)}" : "gone");
            sc.Check("he stays Animado", me.Buffs.TieneEstado(Animado), me.Buffs.TieneEstado(Animado) ? "Animado" : "not Animado");

            // The same with the burlas, on a foe.
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            if (foe != null)
            {
                FreshTurn(session, fight, me, keep: true);
                int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 2).DefaultIfEmpty(-1).First();
                PutAll(session, fight, (foe, spot));
                CastAt(session, Declive, foe.CellId);
                sc.Check("Palabra de Declive leaves the foe Burlado", foe.Buffs.TieneEstado(Burlado) && Has(foe, Declive),
                         $"Burlado {foe.Buffs.TieneEstado(Burlado)}; its rows: {RowsOf(foe, Declive)}");
                FreshTurn(session, fight, me, keep: true);
                CastAt(session, Deprimente, foe.CellId);
                sc.Check("Palabra Deprimente puts its own", Has(foe, Deprimente), $"its rows: {RowsOf(foe, Deprimente)}");
                sc.Check("and Palabra de Declive's is gone: one burla at a time", !Has(foe, Declive),
                         Has(foe, Declive) ? $"still: {RowsOf(foe, Declive)}" : "gone");
            }

            // On an empty cell, an ánimo summons its fairy.
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            int empty = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 2).DefaultIfEmpty(-1).First();
            CastAt(session, Estimulante, empty);
            var born = fight.Todos.FirstOrDefault(f => f.IsAlive && f.EsInvocado && f.Invocador == me.Id && f.CellId == empty);
            sc.Check("Palabra Estimulante on an empty cell summons its fairy", born != null && born.MonsterId == Fairy,
                     born == null ? $"nothing on {empty}" : $"monster {born.MonsterId} (the fairy is {Fairy})");
        }

        // ─── The Huppermage (class sheet 1057739, «Los estados elementales», «Las runas») ──

        /// <summary>
        /// «Los hechizos elementales ... aplican estados elementales cuando alcanzan a un enemigo»,
        /// with a rune of that element under him; «solo puede haber una runa por casilla»; two
        /// different states make a combination -- earth and fire, «bonus de daños finales recibidos»
        /// on the target for a turn, power for the Huppermage «una vez máximo por combinación».
        /// </summary>
        private static void HuppermageElements(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int OndaSismica = 13668, Lanzallamas = 13666, Erupcion = 13707;
            const int Fire = 290, Water = 291, Earth = 292, Air = 293;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 4);
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            if (foe == null) { sc.Check("a foe", false, "none"); return; }
            int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == 3).DefaultIfEmpty(-1).First();
            PutAll(session, fight, (foe, spot));
            string Elements(Fighter f) => string.Join(" ", new[] { (Fire, "fire"), (Water, "water"), (Earth, "earth"), (Air, "air") }
                                                            .Where(e => f.Buffs.TieneEstado(e.Item1)).Select(e => e.Item2).DefaultIfEmpty("none"));
            List<Glifo> RunesOn(int cell) => fight.Glifos.Where(g => g.Cubre(cell) && g.Dueno == me.Id).ToList();

            CastAt(session, OndaSismica, foe.CellId);
            sc.Check("an earth spell leaves the foe in the earth state", foe.Buffs.TieneEstado(Earth), Elements(foe));
            var first = RunesOn(foe.CellId);
            sc.Check("and an earth rune under him", first.Count == 1, first.Count == 0 ? "no rune" : string.Join(", ", first.Select(g => $"mark {g.Id} casting {g.Hechizo}")));

            // Fire on the same foe: earth + fire, Erupción.
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Lanzallamas, foe.CellId);
            sc.Check("a fire spell on him makes the earth/fire combination: his elemental states go", !foe.Buffs.TieneEstado(Earth) && !foe.Buffs.TieneEstado(Fire),
                     $"states left: {Elements(foe)}");
            sc.Check("the combination leaves him taking more damage", foe.Buffs.Puestos.Any(r => r.HechizoOrigen == Erupcion),
                     string.Join(", ", foe.Buffs.Puestos.Where(r => r.HechizoOrigen == Erupcion).Select(r => $"{r.EffectId} {r.Valor}")).DefaultIfEmptyText("no row of Erupción"));
            int power = me.Buffs.Puestos.Count(r => r.HechizoOrigen == Erupcion && r.EffectId == 138);
            sc.Check("and gives the Huppermage power", power == 1, $"{power} power row(s) of Erupción");
            var runes = RunesOn(foe.CellId);
            sc.Check("one rune per cell: the fire rune replaced the earth one", runes.Count == 1 && first.All(f => !fight.Glifos.Contains(f)),
                     string.Join(", ", runes.Select(g => $"mark {g.Id} casting {g.Hechizo}")).DefaultIfEmptyText("no rune"));

            // The same combination again: its power does not pile up.
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, OndaSismica, foe.CellId);
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Lanzallamas, foe.CellId);
            power = me.Buffs.Puestos.Count(r => r.HechizoOrigen == Erupcion && r.EffectId == 138);
            sc.Check("the same combination again gives no more power", power == 1, $"{power} power row(s) of Erupción");
        }

        // ─── The Selatrop (class sheet 1172961, «Los portales») ──────────────────────

        /// <summary>
        /// «Se colocan en casillas del terreno libres»; «hace falta un mínimo de dos portales
        /// activos para formar una red»; «no se puede colocar un portal en una casilla donde ya
        /// haya otro portal»; walking onto one, «la entidad llega a la casilla del portal de
        /// destino»; «los portales pueden cerrarse con los hechizos Neutral e Interrupción»; «cada
        /// equipo tiene un límite de cuatro portales».
        /// </summary>
        private static void SelatropPortals(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int PortalSpell = 14574, Neutral = 14582;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            // The portals of this fight's earlier spells off the board: a scenario starts from none.
RunInSession(session, _ => FightHandler.TestClearPortalsAsync(fight));
            ToOpenGround(session, fight, me, 4);
            var reach = FightHandler.ReachOf(fight, me, PortalSpell);
            int Free(int distance, params int[] not) => Enumerable.Range(0, 560)
                .Where(c => FreeCell(fight, c) && fight.Portales.At(c) == null && !not.Contains(c) && MapGeometry.Distance(c, me.CellId) == distance)
                .DefaultIfEmpty(-1).First();
            int far = Math.Clamp(3, Math.Max(1, reach.Min), Math.Max(1, reach.Max));

            int a = Free(far);
            CastAt(session, PortalSpell, a);
            var first = fight.Portales.At(a);
            if (!sc.Check("Portal lays a portal on a free cell", first != null, first == null ? $"no portal on {a}" : first.ToString())) return;
            sc.Check("alone, it is no network: off", !first!.Active, first.Active ? "on" : "off");

            FreshTurn(session, fight, me, keep: true);
            int b = Free(far, a);
            CastAt(session, PortalSpell, b);
            var second = fight.Portales.At(b);
            sc.Check("a second one makes the network: both on", second != null && second.Active && first.Active,
                     $"first {(first.Active ? "on" : "off")}, second {(second == null ? "none" : second.Active ? "on" : "off")}");

            // Aimed at a portal, Portal goes through the network -- its level forbids no projection,
            // and the client draws it so -- and lays its own where it comes out: never a second one
            // on the aimed cell.
            FreshTurn(session, fight, me, keep: true);
            int count = fight.Portales.Count;
            CastAt(session, PortalSpell, a);
            var onA = fight.Portales.All.Where(p => p.Cell == a).ToList();
            sc.Check("no portal goes on a cell that has one", onA.Count == 1 && onA[0] == first && fight.Portales.Count <= count + 1,
                     $"{count} -> {fight.Portales.Count} portal(s), on {a}: {string.Join(", ", onA)}");

            // A foe walking onto the first comes out on the second's cell.
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, a)).FirstOrDefault();
            if (foe != null && second != null)
            {
                int beside = MapGeometry.GetNeighbors(a).Where(c => FreeCell(fight, c) && fight.Portales.At(c) == null).DefaultIfEmpty(-1).First();
                if (beside >= 0 && PutAll(session, fight, (foe, beside)))
                {
                    // He comes out of the chain's last portal: the second one, or the one Portal
                    // laid through it just now.
                    var chain = fight.Portales.Chain(first, fight.OccupantOf);
                    int exit = chain.Count > 1 ? chain[chain.Count - 1].Cell : b;
                    RunInSession(session, async stream => await FightHandler.TestWalkAsync(stream, fight, foe, a));
                    Pause();
                    sc.Check("walking onto a portal, he comes out of the chain's last one", foe.CellId == exit,
                             $"on {foe.CellId} (walked onto {a}, the chain ends on {exit})");
                }
                else sc.Note("no free cell beside the first portal for the foe");
            }

            // Neutral closes the one it aims at: out of the network until the Selatrop's next turn.
            FreshTurn(session, fight, me, keep: true);
            var target = second != null && fight.Todos.All(f => !f.IsAlive || f.CellId != b) ? second : first;
            int aimed = target.Cell;
            if (fight.Todos.Any(f => f.IsAlive && f.CellId == aimed)) aimed = target == first ? b : a;
            CastAt(session, Neutral, aimed);
            var closed = fight.Portales.At(aimed);
            sc.Check("Neutral closes the portal it aims at", closed != null && (!closed.Active || closed.NeutralisedBy != 0),
                     closed == null ? $"no portal on {aimed}" : $"{closed}: {(closed.Active ? "on" : "off")}, closed by {closed.NeutralisedBy}");

            // Four a side: the fifth takes the oldest away.
            FreshTurn(session, fight, me, keep: true);
RunInSession(session, _ => FightHandler.TestClearPortalsAsync(fight));
            var laid = new List<int>();
            for (int k = 0; k < 5; k++)
            {
                FreshTurn(session, fight, me, keep: true);
                int cell = Enumerable.Range(Math.Max(1, reach.Min), Math.Max(1, reach.Max)).Select(d => Free(d, laid.ToArray())).FirstOrDefault(c => c >= 0, -1);
                if (cell < 0) break;
                CastAt(session, PortalSpell, cell);
                if (fight.Portales.At(cell) != null) laid.Add(cell);
            }
            var mine = fight.Portales.All.Where(p => p.Owner == me.Id).ToList();
            sc.Check("five laid, four stand", laid.Count == 5 && mine.Count == 4, $"{laid.Count} laid, {mine.Count} on the board");
            if (laid.Count == 5) sc.Check("and the first one is the one gone", fight.Portales.At(laid[0]) == null, $"on {laid[0]}: {fight.Portales.At(laid[0])?.ToString() ?? "none"}");
        }

        // ─── The Zurcarák (class sheet 1086903, «El póquer de Zurcarák») ─────────────

        /// <summary>
        /// «Todos los hechizos elementales del zurcarák ... le permiten robar cartas. Solo puede
        /// haber un ejemplar de cada carta en la mano, y una mano puede componerse de 4 cartas como
        /// máximo. Una vez llena, la mano ya no se puede modificar.» A card is a state on him (Kraps
        /// and Baraka, 3 AP of water, both the queen of clubs, 5446); the hand counts itself in
        /// 5461..5464.
        /// </summary>
        private static void ZurcarakPoker(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            // Spells that only draw: Baraka, Jass, Belote and Blakjak draw their queen or, holding
            // it, take it away again (their 29927, 29926, 29929, 29928 under "E" of the queen).
            const int Kraps = 12883, Reflejos = 12857, Topkaj = 12846, Almohadillas = 12875, CaraOCruz = 12841, Baraka = 12848;
            int[] hand = { 5461, 5462, 5463, 5464 };
            var cardOf = new Dictionary<int, int> { [Kraps] = 5446, [Reflejos] = 5440, [Topkaj] = 5448, [Almohadillas] = 5443, [CaraOCruz] = 5452 };
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderByDescending(f => f.CurrentHP).FirstOrDefault();
            if (foe == null) { sc.Check("a foe", false, "none"); return; }
            int Size() => hand.Select((s, i) => me.Buffs.TieneEstado(s) ? i + 1 : 0).DefaultIfEmpty(0).Max();
            string Cards() => string.Join(", ", cardOf.Values.Distinct().Where(me.Buffs.TieneEstado)).DefaultIfEmptyText("none");
            bool Play(int spell)
            {
                FreshTurn(session, fight, me, keep: true);
                if (!StageForMove(session, fight, me, foe, spell, Displacement.Push)) return false;
                return CastAt(session, spell, foe.CellId).Any(l => l.Contains("Casts spell"));
            }
            sc.Check("the hand starts empty", Size() == 0 && Cards() == "none", $"hand {Size()}, cards {Cards()}");

            Play(Kraps);
            sc.Check("Kraps (3 AP, water) draws the queen of clubs", me.Buffs.TieneEstado(cardOf[Kraps]) && Size() == 1, $"hand {Size()}, cards {Cards()}");
            Play(Kraps);
            sc.Check("the same card again draws nothing more: one of each card", Size() == 1, $"hand {Size()}, cards {Cards()}");

            foreach (int spell in new[] { Reflejos, Topkaj, Almohadillas }) Play(spell);
            sc.Check("three other cards fill the hand: four", Size() == 4 && new[] { Kraps, Reflejos, Topkaj, Almohadillas }.All(s => me.Buffs.TieneEstado(cardOf[s])),
                     $"hand {Size()}, cards {Cards()}");
            Play(CaraOCruz);
            sc.Check("a full hand takes no fifth card", !me.Buffs.TieneEstado(cardOf[CaraOCruz]) && Size() == 4, $"hand {Size()}, cards {Cards()}");
            Play(Baraka);
            sc.Check("Baraka, holding the queen of clubs, takes it away", !me.Buffs.TieneEstado(cardOf[Kraps]) && Size() == 3, $"hand {Size()}, cards {Cards()}");
        }

        // ─── The Yopuka's Furor (its description: «los daños del hechizo aumentan después de cada
        //     lanzamiento ... los bonus disminuyen si el hechizo no se vuelve a lanzar») ─────

        private static void YopukaFuror(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Furor = 13156;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderByDescending(f => f.CurrentHP).FirstOrDefault();
            if (foe == null) { sc.Check("a foe", false, "none"); return; }
            int Bonus() => me.Buffs.DelHechizo(Furor, SpellAspect.DanoBase, fight.RoundNumber);
            bool Strike()
            {
                var reach = FightHandler.ReachOf(fight, me, Furor);
                int at = Math.Clamp(1, Math.Max(1, reach.Min), Math.Max(1, reach.Max));
                int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == at).DefaultIfEmpty(-1).First();
                if (spot >= 0) PutAll(session, fight, (foe, spot));
                return CastAt(session, Furor, foe.CellId).Any(l => l.Contains("Casts spell"));
            }
            int start = Bonus();
            sc.Check("the scenario starts with no Furor bonus", start == 0, $"+{start}");
            Strike();
            int first = Bonus();
            sc.Check("the first cast leaves a bonus on Furor's damage", first > 0, $"+{first}");
            bool back = PassTurn(session, fight, me);
            FreshTurn(session, fight, me, keep: true);
            Strike();
            int second = Bonus();
            sc.Check("cast again the next turn, the bonus grows", back && second > first, $"+{first} -> +{second} (turn back {back})");
            back = PassTurn(session, fight, me);
            FreshTurn(session, fight, me, keep: true);
            Strike();
            int third = Bonus();
            sc.Check("a third time it stays at its cap", back && third == second, $"+{second} -> +{third}");
            // A turn without casting it: the bonus goes down.
            back = PassTurn(session, fight, me) && PassTurn(session, fight, me);
            int after = Bonus();
            sc.Check("not cast again, the bonus goes down", back && after < third, $"+{third} -> +{after} (turns back {back})");
        }

        // ─── The Zobal (class sheet 888304, «Las máscaras») ─────────────────────────

        private static void ZobalMasks(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Intrepido = 13386, Saikopat = 13388, Coraza = 13397;
            const int Tank = 98, Damage = 99, Mobility = 100;
            FreshTurn(session, fight, me, keep: false, withoutSummons: true);
            ToOpenGround(session, fight, me, 3);
            string Mask() => me.Buffs.TieneEstado(Tank) ? "tank (98)" : me.Buffs.TieneEstado(Damage) ? "damage (99)" : me.Buffs.TieneEstado(Mobility) ? "mobility (100)" : "none";
            var reach = FightHandler.ReachOf(fight, me, Coraza);
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderBy(f => MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
            int CorazaCell()
            {
                if (reach.Min == 0) return me.CellId;
                int spot = Enumerable.Range(0, 560).Where(c => FreeCell(fight, c) && MapGeometry.Distance(c, me.CellId) == Math.Max(1, reach.Min)).DefaultIfEmpty(-1).First();
                if (foe != null) PutAll(session, fight, (foe, spot));
                return foe?.CellId ?? spot;
            }
            // From the damage mask, whatever the earlier spells left on: a mask stays until another
            // takes its place.
            CastAt(session, Saikopat, me.CellId);
            sc.Check("Máscara Sáikopat puts the damage mask on", Mask() == "damage (99)", Mask());

            // «Cuando una máscara está activa, bloquea la utilización de ciertos hechizos y desbloquea otros.»
            int ap = me.CurrentAP;
            CastAt(session, Coraza, CorazaCell());
            sc.Check("without the tank mask Coraza (which asks for it) cannot be cast", me.CurrentAP == ap, $"AP {ap} -> {me.CurrentAP}");
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Intrepido, me.CellId);
            sc.Check("Máscara de Intrépido puts the tank mask on", Mask() == "tank (98)", Mask());
            sc.Check("and Sáikopat's effects go with the damage mask", !me.Buffs.Puestos.Any(b => b.HechizoOrigen == Saikopat),
                     string.Join(", ", me.Buffs.Puestos.Where(b => b.HechizoOrigen == Saikopat).Select(b => $"{b.EffectId}")).DefaultIfEmptyText("none left"));
            var tankRows = me.Buffs.Puestos.Where(b => b.HechizoOrigen == Intrepido).Select(b => b.Numero).ToList();
            ap = me.CurrentAP;
            CastAt(session, Coraza, CorazaCell());
            sc.Check("with it Coraza is cast", me.CurrentAP < ap, $"AP {ap} -> {me.CurrentAP}");

            // «Los zobals solo pueden usar una máscara cada vez ... Utilizar una máscara nueva retira
            //  los efectos de la máscara utilizada anteriormente y fija su intervalo de lanzamiento en 1 turno.»
            // ...which is why Sáikopat, the mask Intrépido took off, waits for the next turn.
            ap = me.CurrentAP;
            CastAt(session, Saikopat, me.CellId);
            sc.Check("the mask just taken off waits a turn: Sáikopat cannot come back yet", Mask() == "tank (98)" && me.CurrentAP == ap,
                     $"{Mask()}, AP {ap} -> {me.CurrentAP}");
            bool back = PassTurn(session, fight, me);
            FreshTurn(session, fight, me, keep: true);
            CastAt(session, Saikopat, me.CellId);
            sc.Check("Máscara Sáikopat swaps it for the damage mask", Mask() == "damage (99)", $"{Mask()} (turn back {back})");
            var left = me.Buffs.Puestos.Where(b => tankRows.Contains(b.Numero)).ToList();
            sc.Check("and the tank mask's effects go with it", left.Count == 0, left.Count == 0 ? "none left" : $"{left.Count} row(s) of Intrépido left");
            ap = me.CurrentAP;
            CastAt(session, Coraza, CorazaCell());
            sc.Check("Coraza is locked again", me.CurrentAP == ap, $"AP {ap} -> {me.CurrentAP}");
            ap = me.CurrentAP;
            CastAt(session, Intrepido, me.CellId);
            sc.Check("the mask just taken off cannot be put back the same turn", Mask() == "damage (99)" && me.CurrentAP == ap, $"{Mask()}, AP {ap} -> {me.CurrentAP}");
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
