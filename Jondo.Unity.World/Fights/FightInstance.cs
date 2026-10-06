using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.World.Fights
{
    public enum FightState
    {
        Placement,
        Ongoing,
        Ended
    }

    public class FightInstance
    {
        public long FightId { get; set; }

        /// <summary>This fight's rules: what changes compared to fighting monsters.</summary>
        /// <remarks>
        /// They were two flags, <c>IsDuel</c> and <c>IsKoliseo</c>, and the engine looked at them in sixteen
        /// places spread over five methods. See <see cref="FightRules"/> for why this and not
        /// two engines.
        /// </remarks>
        public FightRules Reglas { get; set; } = FightRules.ContraMonstruos;

        /// <summary>Whether there are people opposite and not monsters. The same IsDuel said.</summary>
        public bool EsPvp => !Reglas.EnfrenteHayMonstruos;

        public long MapId { get; set; }
        /// <summary>Who in this fight has already received the preparation.</summary>
        /// <remarks>
        /// This was <c>HasLoadedMap</c>, ONE boolean for the whole fight, and it worked while
        /// there was only one person inside: against monsters, the only player set it and that was it.
        /// In a challenge there are two, and the flow is the same for each one through his own socket:
        ///
        /// <code>
        ///   C-&gt;S  kmv          «I am on the fight map now, give me the actors»
        ///   S-&gt;C  the preparation: jxg of each fighter, kba, jzu, kam, kaa, kae...
        /// </code>
        ///
        /// With the shared flag, the FIRST to send the kmv set it and the second was
        /// answered that there was no preparation pending any more. His client stayed in roleplay mode —with
        /// his spell bar, without fighters and without a ready button— looking at the previous map.
        /// Whether one or the other launched the challenge changed nothing: the second to
        /// load the map always failed, which is a race and not a role.
        ///
        /// Per fighter and not per fight, and with a lock because the two clients arrive through two
        /// connections at once.
        /// </remarks>
        private readonly HashSet<long> _preparados = new HashSet<long>();

        /// <summary>The last turn whose «confirm to me» was already handled, as round and position.</summary>
        private (int Ronda, int Puesto, long Quien) _turnoAtendido = (-1, -1, -1);

        /// <summary>Its own lock: it shares nothing with the preparation's.</summary>
        private readonly object _candadoDelTurno = new object();

        /// <summary>
        /// Lets through ONE single confirmation per turn.
        /// </summary>
        /// <remarks>
        /// The server sends a «confirm to me» (jxh) before each turn and the client answers with its
        /// jwz. With a single person in the fight that is one question and one answer; in a
        /// challenge the question goes to both and both answer, and what hangs from the answer
        /// —undoing expired summons, sweeping fulfilled buffs, giving back points— has to
        /// happen once and not twice. This is where it is decided which of the two answers does the
        /// work; the other is just ignored.
        /// </remarks>
        /// <param name="fighterId">
        /// Whose turn it is. The index alone is not a turn: a death or a summon in the middle of a
        /// round rebuilds the order and moves everybody's index, and the next turn can land on
        /// the index of the last one confirmed. Measured: the Ocra's own turn at index 2 of round
        /// R, a fighter before him gone, his Arakna summoned at index 2 -- and her turn, "round R,
        /// index 2", was taken for the one already opened. No jzc went out, the fight stood still
        /// and the client's clock ran into the negatives.
        /// </param>
        public bool AtenderElTurnoUnaVez(int round, int turnIndex, long fighterId = 0)
        {
            lock (_candadoDelTurno)
            {
                if (_turnoAtendido == (round, turnIndex, fighterId)) return false;
                _turnoAtendido = (round, turnIndex, fighterId);
                return true;
            }
        }

        /// <summary>
        /// Whether the turn at hand has been announced (jzc) or is still waiting for a client to
        /// confirm it (jxh sent, jwz not back). A fight whose only human closed the game parks
        /// here, and somebody reconnecting needs to know which of the two he is walking into.
        /// </summary>
        public bool TurnAwaitingConfirmation
        {
            get { lock (_candadoDelTurno) return _turnoAtendido != (RoundNumber, CurrentTurnIndex, CurrentFighter?.Id ?? 0); }
        }

        /// <summary>
        /// The last turn that went out as a jzc: who, at what index and round, for how long, and
        /// when. It is what a reconnecting client is told first, so that his carousel and his
        /// clock line up with everybody else's. Measured in the reconnection capture: the burst
        /// carries the jzc of the turn IN PROGRESS with f6 = what is left of it, 132 tenths where
        /// 21.8 seconds of a 350 turn had gone by.
        /// </summary>
        public AnnouncedTurn LastAnnouncedTurn { get; set; }

        /// <param name="Carried">The tenths carried from the fighter's last turn, on top of <paramref name="Deciseconds"/>.</param>
        public readonly record struct AnnouncedTurn(long FighterId, int Index, int Round,
                                                    int Deciseconds, DateTime StartedUtc, int Carried = 0)
        {
            public bool Announced => FighterId != 0;

            /// <summary>What is left of it, carried time and all, in tenths of a second; never below zero.</summary>
            public int RemainingDeciseconds(DateTime nowUtc)
            {
                if (!Announced) return 0;
                long gone = (long)(nowUtc - StartedUtc).TotalMilliseconds / 100;
                return (int)Math.Max(0, Deciseconds + Carried - gone);
            }
        }

        /// <summary>Whether this one has already been sent the preparation.</summary>
        public bool HasPrepared(long fighterId)
        {
            lock (_preparados) return _preparados.Contains(fighterId);
        }

        /// <summary>Records him as prepared. Returns false if he already was.</summary>
        /// <remarks>
        /// Recording and checking in the same call is what keeps two frames from the same
        /// client —the kmv and the kkr arrive almost together— from sending the preparation twice.
        /// </remarks>
        public bool MarkPrepared(long fighterId)
        {
            lock (_preparados) return _preparados.Add(fighterId);
        }

        /// <summary>Unrecords him, to send him the preparation again from scratch.</summary>
        public void ForgetPreparation(long fighterId)
        {
            lock (_preparados) _preparados.Remove(fighterId);
        }
        public FightState State { get; private set; } = FightState.Placement;

        // ═══════════════════════════════════════════════════════════════════
        //  The two sides
        // ═══════════════════════════════════════════════════════════════════
        //
        // They were called Team0 and Team1, and next to them it said «// Players» and «// Monsters». A hundred and five
        // references later that had stopped being a comment and was a belief: half the
        // engine took for granted that the one playing is on blue and on red there are creatures.
        //
        // Against monsters it is true. In a challenge it is true for one of the two, and from there came
        // a whole class of bugs -- the red one could not reposition, did not receive his initial
        // waits, could not abandon, and his «ready» did not count -- that carried no «if»
        // because nobody knew they were assumptions.
        //
        // Blue and Red are the colours of the placement cells and promise nothing about who
        // is inside. Below are the three questions the engine asked by hand in sixty places.

        /// <summary>The side that starts: whoever provokes the fight, or whoever challenges.</summary>
        public const int Azules = 0;

        /// <summary>The other: the monsters, or the challenged.</summary>
        public const int Rojos = 1;

        public List<Fighter> Azul { get; } = new List<Fighter>();
        public List<Fighter> Rojo { get; } = new List<Fighter>();

        /// <summary>The Koliseo mode this fight was matched in, for the ladder; -1 for none.</summary>
        public int KoliseoMode { get; set; } = -1;

        public List<int> BluePlacementCells { get; } = new List<int>();
        public List<int> RedPlacementCells { get; } = new List<int>();

        /// <summary>Those of the side given.</summary>
        public List<Fighter> Bando(int equipo) => equipo == Rojos ? Rojo : Azul;

        /// <summary>That side's placement cells.</summary>
        public List<int> CasillasDe(int equipo) => equipo == Rojos ? RedPlacementCells : BluePlacementCells;

        /// <summary>Everyone, from both sides.</summary>
        public IEnumerable<Fighter> Todos => Azul.Concat(Rojo);

        /// <summary>Anyone in the fight, whichever side. Null if not there.</summary>
        public Fighter Buscar(long fighterId)
            => Azul.FirstOrDefault(f => f.Id == fighterId)
               ?? Rojo.FirstOrDefault(f => f.Id == fighterId);

        /// <summary>Which side he is on, or -1 if he is not in the fight.</summary>
        public int EquipoDe(long fighterId)
        {
            if (Azul.Exists(f => f.Id == fighterId)) return Azules;
            if (Rojo.Exists(f => f.Id == fighterId)) return Rojos;
            return -1;
        }

        /// <summary>The side opposite the one given.</summary>
        public static int Contrario(int equipo) => equipo == Rojos ? Azules : Rojos;

        /// <summary>Those on his side, himself included. Empty if he is not in the fight.</summary>
        public List<Fighter> Aliados(long fighterId)
        {
            int suyo = EquipoDe(fighterId);
            return suyo < 0 ? new List<Fighter>() : Bando(suyo);
        }

        /// <summary>Those on the other side. Empty if he is not in the fight.</summary>
        public List<Fighter> Enemigos(long fighterId)
        {
            int suyo = EquipoDe(fighterId);
            return suyo < 0 ? new List<Fighter>() : Bando(Contrario(suyo));
        }

        /// <summary>Whether that side still has someone standing.</summary>
        /// <remarks>
        /// This was written by hand as <c>Team0.Exists(f =&gt; f.IsAlive)</c> and called
        /// «alliesAlive», which is only true if whoever asks is on blue. With the side
        /// up front it can no longer be written the wrong way round without noticing.
        /// </remarks>
        public bool SigueVivo(int equipo) => Bando(equipo).Exists(f => f.IsAlive);

        /// <summary>Whether whoever asks won. Also false for whoever was not there.</summary>
        public bool HaGanado(long fighterId)
        {
            int suyo = EquipoDe(fighterId);
            return suyo >= 0 && SigueVivo(suyo);
        }

        public long ChallengerLeaderId => Azul.FirstOrDefault()?.Id ?? 0;
        public long DefenderLeaderId { get; set; } = -20000;

        /// <summary>
        /// How many times each one has cast each spell in the current turn.
        /// </summary>
        /// <remarks>
        /// THE FIGHT'S, and with the caster in the key. They were in two STATIC dictionaries of
        /// FightHandler indexed only by the spell id, so with two clients fighting at
        /// once one's casts counted against the other's as soon as they shared a spell
        /// —spell ids repeat between players—.
        ///
        /// And worse: they were never emptied. The only thing that cleared them was inside a method with not a
        /// single caller, so on the PROCESS's third cast —adding up all the players and
        /// all the fights— the spell was rejected with «already spent this turn» for every-
        /// body until the server restarted.
        /// </remarks>
        public Dictionary<(long Caster, long Spell), int> CastsThisTurn { get; }
            = new Dictionary<(long, long), int>();

        /// <summary>The same, per target: the cap on casts on the same creature.</summary>
        public Dictionary<(long Caster, long Spell, long Target), int> CastsPerTargetThisTurn { get; }
            = new Dictionary<(long, long, long), int>();

        public List<Fighter> TurnOrder { get; private set; } = new List<Fighter>();
        public int CurrentTurnIndex { get; private set; } = 0;
        public Fighter CurrentFighter => TurnOrder.Count > 0 ? TurnOrder[CurrentTurnIndex] : null;

        public int WinnerTeamId { get; private set; } = -1;

        public long RoleplayMapId { get; set; }
        public long ArenaMapId { get; set; }

        /// <summary>
        /// When the real fighting started, to know how long it lasted: the end-of-fight
        /// screen shows it at the top right and without this it came out 00:00.
        /// </summary>
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The number the next buff gets. It belongs to the FIGHT, not to each fighter: in the
        /// capture the player's and the monster's go in the same series, and it is the number with
        /// which each one is later removed.
        /// </summary>
        private int _ultimoEmbrujo;
        public int SiguienteEmbrujo() => ++_ultimoEmbrujo;

        public CancellationTokenSource PlacementTimerCts { get; set; }
        public CancellationTokenSource TurnTimerCts { get; set; }

        /// <summary>
        /// A sequence the clients are owed but that has not been opened yet: it opens right
        /// before the next frame of the fight goes out, and never opens at all when no frame
        /// follows. The attitude sequences hang on this, because whether an attitude will
        /// announce anything is only known once its effects have run, and the real server
        /// never sends an empty jto/jwi pair -- not one in 264 captures.
        /// </summary>
        public Func<Task> SequenceToOpen { get; set; }

        /// <summary>The end-of-fight numbers of each person, by character id.</summary>
        private readonly Dictionary<long, FightStatistics> _statistics = new Dictionary<long, FightStatistics>();

        /// <summary>This person's numbers so far, started at zero the first time they are asked for.</summary>
        public FightStatistics StatisticsOf(long characterId)
        {
            if (!_statistics.TryGetValue(characterId, out var stats))
            {
                stats = new FightStatistics();
                _statistics[characterId] = stats;
            }
            return stats;
        }

        /// <summary>
        /// Where the blows being dealt right now come from. A glyph going off sets it to
        /// Glyph for as long as it resolves; everything else is Direct.
        /// </summary>
        public DamageSource CurrentDamageSource { get; set; } = DamageSource.Direct;

        /// <summary>
        /// The kind of glyph going off right now (its effect: 400 a trap, 401 a glyph...), zero
        /// the rest of the time: a trap's blow sets off "DT" on whoever it hurts.
        /// </summary>
        public int CurrentGlyphType { get; set; }

        /// <summary>
        /// Who dealt the blow whose triggers are firing right now, for as long as they fire;
        /// null the rest of the time. It is what the target mask letter "O" points at: the
        /// push of Remisión, "repele a sus atacantes", goes to whoever hit the bearer in melee,
        /// who is nowhere near the aimed cell of the spell that pushes.
        /// </summary>
        public Fighter TriggeringAttacker { get; set; }

        public FightInstance(long fightId, long mapId, long arenaMapId = 0)
        {
            FightId = fightId;
            RoleplayMapId = mapId;
            ArenaMapId = arenaMapId != 0 ? arenaMapId : mapId;
            MapId = ArenaMapId;
        }

        public void CancelPlacementTimer()
        {
            try
            {
                PlacementTimerCts?.Cancel();
                PlacementTimerCts?.Dispose();
            }
            catch { }
            finally
            {
                PlacementTimerCts = null;
            }
        }

        public void CancelTurnTimer()
        {
            try
            {
                TurnTimerCts?.Cancel();
                TurnTimerCts?.Dispose();
            }
            catch { }
            finally
            {
                TurnTimerCts = null;
            }
        }

        /// <summary>Eight per side: below this there is no room to place two teams.</summary>
        public const int PlacesForBothTeams = 16;

        /// <summary>
        /// The placement cells as they are, when the map already brings them.
        /// </summary>
        /// <remarks>
        /// The koliseo arenas mark them in the client itself, side by side, so there
        /// there is nothing to split: <see cref="GeneratePlacementCells"/> cuts a list of walkable
        /// cells in half because in an ordinary arena there is nothing else.
        /// </remarks>
        public void SetPlacementCells(IEnumerable<int> blue, IEnumerable<int> red)
        {
            BluePlacementCells.Clear();
            RedPlacementCells.Clear();
            BluePlacementCells.AddRange(blue);
            RedPlacementCells.AddRange(red);
        }

        public void GeneratePlacementCells(List<int> walkableCells)
        {
            BluePlacementCells.Clear();
            RedPlacementCells.Clear();

            // Fewer than sixteen does not give two teams of eight, and cutting them in half would give
            // one or two slots per side: with a single red cell the five monsters are
            // placed on top of each other, and hitting that cell wounds one and not the rest.
            // The bar was "zero", which is exactly the case that did not happen: on arena 188752387
            // TWO arrived, and two is not zero.
            if (walkableCells == null || walkableCells.Count < PlacesForBothTeams)
            {
                BluePlacementCells.AddRange(new[] { 286, 298, 326, 271, 285, 299, 312, 313 });
                RedPlacementCells.AddRange(new[] { 411, 424, 439, 397, 410, 426, 438, 453 });
                return;
            }

            var defaultBlue = new[] { 286, 298, 326, 271, 285, 299, 312, 313 };
            var defaultRed = new[] { 411, 424, 439, 397, 410, 426, 438, 453 };

            if (defaultBlue.All(c => walkableCells.Contains(c)) && defaultRed.All(c => walkableCells.Contains(c)))
            {
                BluePlacementCells.AddRange(defaultBlue);
                RedPlacementCells.AddRange(defaultRed);
                return;
            }

            var sorted = walkableCells.OrderBy(c => c).ToList();
            var team0Candidates = sorted.Take(sorted.Count / 2).ToList();
            var team1Candidates = sorted.Skip(sorted.Count / 2).ToList();

            BluePlacementCells.AddRange(team0Candidates.Take(8));
            RedPlacementCells.AddRange(team1Candidates.Take(8));
        }

        public void AddPlayer(Fighter player)
        {
            player.TeamId = 0;
            if (BluePlacementCells.Count > 0)
                player.CellId = BluePlacementCells[Azul.Count % BluePlacementCells.Count];
            Azul.Add(player);
            UpdateTurnOrder();
        }

        /// <summary>
        /// Puts a PLAYER on the opposing team. It is what makes a fight a duel.
        /// </summary>
        /// <remarks>
        /// <see cref="AddPlayer"/> forces team zero, because until now the only fight that
        /// existed was one against monsters and all the players went on the same side. In a
        /// challenge there is one person on each side, and the cell comes from the red side for the same reason: two
        /// players on the blue cells would start stuck together.
        /// </remarks>
        public void AddOpponent(Fighter player)
        {
            player.TeamId = 1;
            if (RedPlacementCells.Count > 0)
                player.CellId = RedPlacementCells[Rojo.Count % RedPlacementCells.Count];
            Rojo.Add(player);
            UpdateTurnOrder();
        }

        public void AddMonster(Fighter monster)
        {
            monster.TeamId = 1;
            if (RedPlacementCells.Count > 0)
                monster.CellId = RedPlacementCells[Rojo.Count % RedPlacementCells.Count];
            Rojo.Add(monster);
            UpdateTurnOrder();
        }

        /// <summary>
        /// The next free identifier for a summon.
        ///
        /// Fighters that are not players carry a negative number and are handed out one by
        /// one: in the Cra captures the pious are -1 and -2 and the first beacon comes out with -3,
        /// the second with -4, and so on. What is already there is looked at so as not to step on anyone.
        /// </summary>
        public long SiguienteIdDeInvocado()
        {
            long menor = 0;
            foreach (var f in Azul) if (f.Id < menor) menor = f.Id;
            foreach (var f in Rojo) if (f.Id < menor) menor = f.Id;
            return menor - 1;
        }

        /// <summary>
        /// Puts a summon in the fight, on the summoner's side, and rebuilds the turn
        /// order so that it gets to play.
        /// </summary>
        /// <summary>
        /// A monster that comes into the fight once it has started -- a wave of the Fin du rêve.
        /// Not a summon: nobody summoned it, it pays like any monster, and it plays its own turn.
        /// </summary>
        public void Join(Fighter fighter)
        {
            fighter.TeamId = 1;
            Rojo.Add(fighter);
            RebuildTurnOrderKeepingCurrent();
        }

        public void Invocar(Fighter invocado, Fighter dueno)
        {
            invocado.Invocador = dueno.Id;
            invocado.TeamId = dueno.TeamId;
            (dueno.TeamId == 0 ? Azul : Rojo).Add(invocado);

            // The one playing right now keeps playing: the list is rebuilt but whose turn it is
            // is kept, otherwise the turn goes to the next one in the middle of an action.
            var jugando = CurrentFighter;
            TurnOrder = BuildAlternatingTurnOrder();
            if (jugando != null && TurnOrder.Contains(jugando))
            {
                CurrentTurnIndex = TurnOrder.IndexOf(jugando);
            }
        }

        /// <summary>
        /// Takes a fighter off the board for good -- an illusion that is gone. Not a death: no
        /// list keeps him, the carousel never had him, and the turn order is rebuilt around
        /// whoever is playing.
        /// </summary>
        public void Quitar(Fighter fighter)
        {
            if (fighter == null) return;
            fighter.CurrentHP = 0;
            Azul.Remove(fighter);
            Rojo.Remove(fighter);
            RebuildTurnOrderKeepingCurrent();
        }

        public void UpdateTurnOrder()
        {
            TurnOrder = BuildAlternatingTurnOrder();
        }

        /// <summary>
        /// Rebuilds the turn list keeping whose turn it is right now.
        /// </summary>
        /// <remarks>
        /// What is needed when someone LEAVES the list mid-round. Grouping filters by
        /// IsAlive, so rebuilding it removes him and everyone behind shifts one slot; without
        /// re-pointing CurrentTurnIndex, the turn would go to the next one in the middle of an action.
        ///
        /// It is the same <see cref="Invocar"/> already did for the opposite case, when someone
        /// ENTERS. Taken out here so that the two directions cannot drift apart.
        /// </remarks>
        public void RebuildTurnOrderKeepingCurrent()
        {
            var jugando = CurrentFighter;
            TurnOrder = BuildAlternatingTurnOrder();

            if (jugando != null && TurnOrder.Contains(jugando))
            {
                CurrentTurnIndex = TurnOrder.IndexOf(jugando);
            }
            else if (CurrentTurnIndex >= TurnOrder.Count)
            {
                CurrentTurnIndex = TurnOrder.Count > 0 ? TurnOrder.Count - 1 : 0;
            }
        }

        /// <summary>
        /// A side in playing order: the usual ones by initiative, and behind each one those
        /// they have summoned, in the order they brought them out.
        ///
        /// The summons that play no turn are left out of the list, but they are still in
        /// the fight: they can be hit and they count for the board.
        /// </summary>
        private static List<List<Fighter>> Agrupar(List<Fighter> bando)
        {
            var salida = new List<List<Fighter>>();
            foreach (var quien in bando.Where(f => f.IsAlive && !f.EsInvocado)
                                       .OrderByDescending(f => f.Initiative))
            {
                var grupo = new List<Fighter> { quien };
                foreach (var suyo in bando)
                {
                    if (suyo.IsAlive && suyo.EsInvocado && suyo.JuegaTurno && suyo.Invocador == quien.Id)
                    {
                        grupo.Add(suyo);
                    }
                }
                salida.Add(grupo);
            }
            return salida;
        }

        /// <summary>Someone declares himself ready. Returns whether with that everyone is.</summary>
        /// <remarks>
        /// It only looked at blue, in both halves. In a challenge that meant the fight
        /// started as soon as the CHALLENGER pressed ready, without waiting for the other —his side was
        /// all ready because it was just him— and that the challenged's «ready» was recorded
        /// nowhere. It is what was seen as «one is already fighting and the other is still in placement».
        ///
        /// A monster presses nothing, so for counting only people count; if a
        /// side has none —the usual case against monsters— that side is ready.
        /// </remarks>
        /// <summary>
        /// Takes the ready flag back. The real server does it for whoever reconnects during the
        /// placement: the capture shows him pressing ready again before the fight starts.
        /// </summary>
        public void ForgetReady(long fighterId)
        {
            var f = Buscar(fighterId);
            if (f != null) f.IsReady = false;
        }

        public bool SetFighterReady(long fighterId)
        {
            var f = Buscar(fighterId);
            if (f != null) f.IsReady = true;

            if (Todos.All(p => p.IsMonster || p.EsInvocado || p.IsReady))
            {
                CancelPlacementTimer();
                StartFight();
                return true;
            }
            return false;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Somebody joins during the placement
        // ═══════════════════════════════════════════════════════════════════
        //
        // Measured in «Combate/meterse en combate de otra persona haciendo click en la espadita»
        // (a player clicks the swords of a fight on the map), «Combate/entrar a combate con listo
        // automatico y entrada automatica siguiendo a lider de grupo» (a party member pulled in
        // behind his leader) and «Busqueda grupo/busqueda automatica de grupo...» (four players
        // into one dungeon fight, the monster side rebuilt at every arrival). What travels is in
        // Network/FightJoinProtocol.cs; this is only who fits where.

        /// <summary>When the placement opened: what the kaa of a late joiner counts down from.</summary>
        public DateTime PlacementOpenedUtc { get; } = DateTime.UtcNow;

        /// <summary>
        /// What is left of a placement of <paramref name="totalDeciseconds"/>, in tenths, never
        /// below zero. Measured: the follower's kaa said 442 at 0.7 s into a 450 placement, the
        /// fourth player of the dungeon fight 403 at 4.6 s.
        /// </summary>
        public int PlacementDecisecondsLeft(int totalDeciseconds, DateTime nowUtc)
        {
            long gone = (long)(nowUtc - PlacementOpenedUtc).TotalMilliseconds / 100;
            return (int)Math.Max(0, totalDeciseconds - gone);
        }

        /// <summary>Why somebody cannot come into this fight.</summary>
        // ─── Options: who may come in, and who may watch ──────────────────────────────

        /// <summary>No spectators: jzx with no option, kau { f4: 1 } with no f3.</summary>
        public const int OptionSecret = 0;

        /// <summary>Only the side's party: kau { f3: 1 }, on by itself when a party opens it.</summary>
        public const int OptionPartyOnly = 1;

        /// <summary>Nobody else: jzx { f1: 2 }, with lqn 95.</summary>
        public const int OptionClosed = 2;

        /// <summary>Asking for help: jzx { f1: 3 }.</summary>
        public const int OptionHelp = 3;

        private readonly bool[,] _options = new bool[2, 4];

        /// <summary>Whether a side has an option on.</summary>
        public bool OptionOn(int team, int option)
        {
            if (team is < 0 or > 1 || option is < 0 or > 3) return false;
            lock (_options) return _options[team, option];
        }

        /// <summary>Turns a side's option on or off.</summary>
        public void SetOption(int team, int option, bool on)
        {
            if (team is < 0 or > 1 || option is < 0 or > 3) return;
            lock (_options) _options[team, option] = on;
        }

        public enum JoinRefusal
        {
            None,

            /// <summary>The side is closed: nobody else comes in.</summary>
            Closed,

            /// <summary>The placement is over: the swords are gone from the map (hpr).</summary>
            NotInPlacement,

            /// <summary>The team asked for is not one of the two.</summary>
            NoSuchTeam,

            /// <summary>A person does not join the monsters' side.</summary>
            MonsterTeam,

            /// <summary>No room: as many people as a team takes, or no free placement cell.</summary>
            TeamFull,
        }

        /// <summary>The people of a side, summons and monsters left out.</summary>
        public int PeopleIn(int team) => Bando(team).Count(f => !f.IsMonster && !f.EsInvocado);

        /// <summary>
        /// Whether one more person fits in <paramref name="team"/>. The cap is the caller's: it is
        /// not a property of the fight but of the game (eight, see FightJoin).
        /// </summary>
        public JoinRefusal CanJoin(int team, int maxPeoplePerTeam)
        {
            if (State != FightState.Placement) return JoinRefusal.NotInPlacement;
            if (team != Azules && team != Rojos) return JoinRefusal.NoSuchTeam;
            if (Bando(team).Exists(f => f.IsMonster && !f.EsInvocado)) return JoinRefusal.MonsterTeam;
            if (OptionOn(team, OptionClosed)) return JoinRefusal.Closed;
            if (PeopleIn(team) >= maxPeoplePerTeam) return JoinRefusal.TeamFull;
            if (FreePlacementCell(team) < 0) return JoinRefusal.TeamFull;
            return JoinRefusal.None;
        }

        /// <summary>
        /// The first placement cell of that side nobody stands on, or -1.
        /// </summary>
        /// <remarks>
        /// In the order of the kba, which is the order the real server fills: the joiner of the
        /// sword capture landed on 216, the first red cell, next to the leader on 260, the second.
        /// <see cref="AddPlayer"/> picks by index instead, which is right only while nobody has
        /// moved.
        /// </remarks>
        public int FreePlacementCell(int team)
        {
            foreach (int cell in CasillasDe(team))
            {
                if (!Todos.Any(f => f.IsAlive && f.CellId == cell)) return cell;
            }
            return -1;
        }

        /// <summary>
        /// Puts a person who joins into <paramref name="team"/>, on its first free cell. False,
        /// and nothing changed, when <see cref="CanJoin"/> says no.
        /// </summary>
        public bool JoinTeam(Fighter person, int team, int maxPeoplePerTeam)
        {
            if (person == null || CanJoin(team, maxPeoplePerTeam) != JoinRefusal.None) return false;

            person.TeamId = team;
            person.CellId = FreePlacementCell(team);
            Bando(team).Add(person);
            UpdateTurnOrder();
            return true;
        }

        /// <summary>
        /// Takes a person out during the placement: he leaves, the fight goes on without him.
        /// </summary>
        public bool LeavePlacement(long fighterId)
        {
            if (State != FightState.Placement) return false;
            var who = Buscar(fighterId);
            if (who == null || who.IsMonster) return false;

            Azul.Remove(who);
            Rojo.Remove(who);
            ForgetPreparation(fighterId);
            DeDondeVenian.Remove(fighterId);
            UpdateTurnOrder();
            return true;
        }

        /// <summary>
        /// The monster side rebuilt from scratch: every monster that is not a summon goes, and
        /// <paramref name="count"/> new ones come, built by <paramref name="build"/> from their
        /// position in the group, their id and their cell.
        /// </summary>
        /// <remarks>
        /// What the dungeon capture shows at every arrival, even when the number does not change:
        /// with two players the -1..-4 are taken off (jzw) and -5..-8 put on (kae), with three
        /// -5..-8 give way to -9..-12, with four -9..-12 to -13..-16. So the new ids carry on
        /// below the old ones -- which is why they are drawn before anything is removed -- and
        /// the cells are the red ones in order, the same four (487, 444, 485, 486) every time.
        /// </remarks>
        public (List<Fighter> Removed, List<Fighter> Added) ReplaceMonsters(
            int count, Func<int, long, int, Fighter> build)
        {
            var added = new List<Fighter>();
            if (State != FightState.Placement || build == null) return (new List<Fighter>(), added);

            var removed = Rojo.Where(f => f.IsMonster && !f.EsInvocado).ToList();
            long firstId = SiguienteIdDeInvocado();
            foreach (var gone in removed) Rojo.Remove(gone);

            for (int i = 0; i < count; i++)
            {
                int cell = RedPlacementCells.Count > 0
                    ? RedPlacementCells[i % RedPlacementCells.Count]
                    : 0;
                var monster = build(i, firstId - i, cell);
                if (monster == null) continue;
                monster.Id = firstId - i;
                monster.TeamId = Rojos;
                monster.CellId = cell;
                Rojo.Add(monster);
                added.Add(monster);
            }

            UpdateTurnOrder();
            return (removed, added);
        }

        /// <summary>Repositions during the placement phase, each one on his side's cells.</summary>
        public void ChangePlacementCell(long fighterId, int newCellId)
        {
            if (State != FightState.Placement) return;

            int suyo = EquipoDe(fighterId);
            if (suyo < 0) return;

            var f = Buscar(fighterId);
            if (f != null && CasillasDe(suyo).Contains(newCellId))
            {
                // Nobody on top of anybody. With one person per side this could not happen; with
                // a party on one side it can, and two fighters on one cell is one target for two.
                if (Todos.Any(o => o != f && o.IsAlive && o.CellId == newCellId)) return;
                f.CellId = newCellId;
            }
        }

        /// <summary>Whether the client has already been told to stop regenerating life.</summary>
        /// <remarks>
        /// The lqg + lqt pair is sent only once per fight, which is how it comes out in the capture.
        /// It went only in the «everyone ready» branch, so a fight that started because the
        /// placement time ran out —the normal case against monsters if nobody presses— was left without
        /// it, and the client kept refilling the life bar one by one inside the fight.
        /// </remarks>
        public bool RegeneracionApagada { get; set; }

        /// <summary>What is laid on the ground of this arena: glyphs, traps and runes.</summary>
        /// <remarks>
        /// It lives in the fight and not in a global registry on purpose: two fights at once in the
        /// same instance arena would have different glyphs, and a per-map registry would
        /// mix them.
        /// </remarks>
        public List<Glifo> Glifos { get; } = new List<Glifo>();

        /// <summary>
        /// Who a bomb wall has already caught during the turn in progress.
        /// </summary>
        /// <remarks>
        /// The wall only catches a DISPLACED fighter once a turn, and this is the list that
        /// remembers it. From the class sheet: "Si esta entidad ya ha sufrido los efectos del muro
        /// durante su turno y vuelven a mandarla a el, su desplazamiento no se detendra ni sufrira
        /// los danos. No obstante, caminar en el muro no se ve afectado por este limite."
        ///
        /// MEASURED, and it is what tells the two apart. Of the eight displacements in the
        /// captures that land on a wall cell, five are on fighters that are not the Rogue bombs;
        /// four of those five set the wall off, and the one that does not -- frame 8281, -1 pulled
        /// from 231 to 216 -- is the only one whose fighter had ALREADY been caught in that same
        /// turn, at frame 8250. Nothing else separates it from the other four.
        ///
        /// Cleared at every turn start, whoever the turn belongs to.
        /// </remarks>
        public HashSet<long> WallHitThisTurn { get; } = new HashSet<long>();

        /// <summary>
        /// An effect of the cast in progress asked for the caster's turn to end (1031, "Hace
        /// pasar de turno"). Raised by the effect loop, consumed by the cast once its sequence
        /// has closed.
        /// </summary>
        public bool EndTurnRequested { get; set; }

        /// <summary>
        /// How deep the triggers set off by other triggers go right now. A hit fires "D", "D"
        /// casts a spell that hits, and that hit fires "D" again: past a few levels it is a loop
        /// in the data, not a mechanic, and it stops there.
        /// </summary>
        public int TriggerDepth { get; set; }

        /// <summary>
        /// The telefrags of the spell being resolved: who swapped cells with whom through a
        /// teleport, both ways. The client's own sheet on the Xelor says it -- "se generan cuando
        /// dos entidades intercambian posiciones debido a los efectos de teletransportación de un
        /// hechizo" -- and the masks' T names them for the rows that follow in the same spell.
        /// </summary>
        public Dictionary<long, long> Telefrags { get; set; } = new Dictionary<long, long>();

        /// <summary>
        /// The dead, in the order they fell: "Invoca al último aliado muerto" (780, 1034) brings
        /// back the last of the caster's side.
        /// </summary>
        public List<Fighter> Muertos { get; } = new List<Fighter>();

        /// <summary>The "EC" counts that have come true, per fighter, so each goes off once until it is false again.</summary>
        public HashSet<(long, string)> RecuentosCumplidos { get; } = new HashSet<(long, string)>();

        /// <summary>
        /// The damage of the blow that set the triggers off, while they go off: "% de los daños
        /// iniciales sufridos" (1123-1128) and "Cura #1% de los daños sufridos" read it.
        /// </summary>
        public int DanoDelDisparo { get; set; }

        /// <summary>
        /// The element of that same blow: a share of it returned (1223, 1123) goes out in it --
        /// the Xelor's cómplice returns a 92 of air as a 69 of air, "jwe 1225".
        /// </summary>
        public int ElementoDelDisparo { get; set; }

        /// <summary>
        /// Who began the resolution going on right now -- the caster of the cast, or the owner of
        /// the hook that a trigger set off -- whatever sub-casts it runs through. A share of a
        /// blow returned is his: Masacre's 30% goes out in the Yopuka's name although the enemy
        /// who carries it casts the spell that returns it.
        /// </summary>
        public Fighter RootCaster { get; set; }

        /// <summary>
        /// While above zero, a cast's triggered rows are not armed on anybody: an attitude fires
        /// its own rows itself, and a player's passives keep the hooks their captures measured.
        /// </summary>
        public int SinArmar { get; set; }

        /// <summary>
        /// The fighters a teleport of the spell being resolved could not land -- the mirror cell
        /// off the board or not walkable. The masks' W names them: Conde Kontatrás's clock kills a
        /// whole side when his mirror cell does not exist, as the guide says.
        /// </summary>
        public HashSet<long> TeleportsFallidos { get; set; } = new HashSet<long>();

        /// <summary>
        /// Whether the cast being resolved went through a portal: what the masks' R and r ask,
        /// set for as long as a spell cast at a portal is resolved at the other end.
        /// </summary>
        public bool CastThroughPortal { get; set; }

        /// <summary>
        /// The bonus, in percent, of the damage and the healing of the cast being resolved when it
        /// went through portals (PortalNetwork.BonusPercent); zero the rest of the time.
        /// </summary>
        public int PortalBonusPercent { get; set; }

        /// <summary>The portals of this fight (effect 1181): see <see cref="PortalNetwork"/>.</summary>
        public PortalNetwork Portales { get; } = new PortalNetwork();

        /// <summary>
        /// Who stands on a cell -- alive, and not carried on somebody else's -- or zero: what keeps
        /// a portal off.
        /// </summary>
        public long OccupantOf(int cell)
        {
            foreach (var f in Todos)
            {
                if (f != null && f.IsAlive && !f.EstaCargado && f.CellId == cell) return f.Id;
            }
            return 0;
        }

        /// <summary>
        /// Whom the caster of the spell being resolved carried as it began, for the masks' K:
        /// a Pandawa's throw lands the one he carried before its rows hurt or heal him.
        /// </summary>
        public (long Caster, long Carried) CarriedAtCast { get; set; }

        private int _siguienteGlifo;

        /// <summary>Lays something on the ground and gives it its identifier.</summary>
        public Glifo Poner(Glifo glifo)
        {
            glifo.Id = ++_siguienteGlifo;
            Glifos.Add(glifo);
            return glifo;
        }

        /// <summary>
        /// A mark number for a portal. Glyphs and portals go out through the same jwe 401 and 310,
        /// so they are numbered from one count -- INFERRED: no capture lays both in one fight.
        /// </summary>
        public int SiguienteMarca() => ++_siguienteGlifo;

        /// <summary>What fires with someone stepping on that cell.</summary>
        public List<Glifo> LosQuePisa(int casilla)
        {
            var salen = new List<Glifo>();
            foreach (var g in Glifos)
            {
                if (g.SeDisparaAlPisar && g.Cubre(casilla)) salen.Add(g);
            }
            return salen;
        }

        /// <summary>What goes off with somebody ending his turn there.</summary>
        public List<Glifo> LosQueAcaban(int casilla)
        {
            var salen = new List<Glifo>();
            foreach (var g in Glifos)
            {
                if (g.SeDisparaAlAcabarElTurno && g.Cubre(casilla)) salen.Add(g);
            }
            return salen;
        }

        /// <summary>What fires with someone starting his turn there.</summary>
        public List<Glifo> LosQueEmpiezan(int casilla)
        {
            var salen = new List<Glifo>();
            foreach (var g in Glifos)
            {
                if (g.SeDisparaAlEmpezarElTurno && g.Cubre(casilla)) salen.Add(g);
            }
            return salen;
        }

        /// <summary>Removes the ones spent or fulfilled. Returns how many it took away.</summary>
        public List<Glifo> BarrerLosGlifos()
        {
            var caidos = new List<Glifo>();
            // Only the spent ones here. A glyph's time is its caster's: it falls at the start of
            // his turn once its round has come, the way the rows he puts do -- see
            // QuitarLosGlifosCaducados. Falling at whoever's turn came first, the time glyph of a
            // boss who plays last was gone before any player started a turn in it.
            foreach (var g in Glifos)
            {
                if (g.Gastado) caidos.Add(g);
            }

            foreach (var muerto in caidos) Glifos.Remove(muerto);
            return caidos;
        }

        /// <summary>
        /// The glyphs whose time is up at this turn start, taken off: the ones whose round has come
        /// and whose time runs on this fighter's turns (<paramref name="suTiempoCorre"/>), and the
        /// ones whose caster is gone, which do not outlive him.
        /// </summary>
        public List<Glifo> QuitarLosGlifosCaducados(Func<Glifo, bool> suTiempoCorre, Func<Glifo, bool> sinDueno)
        {
            var caidos = new List<Glifo>();
            foreach (var g in Glifos)
            {
                bool cumplido = g.CaducaEnRonda > 0 && RoundNumber >= g.CaducaEnRonda && suTiempoCorre(g);
                if (cumplido || sinDueno(g)) caidos.Add(g);
            }
            foreach (var muerto in caidos) Glifos.Remove(muerto);
            return caidos;
        }

        public void StartFight()
        {
            CancelPlacementTimer();
            State = FightState.Ongoing;
            StartedAt = DateTime.UtcNow;
            if (TurnOrder.Count == 0)
            {
                TurnOrder = BuildAlternatingTurnOrder();
            }
            CurrentTurnIndex = 0;

            if (CurrentFighter != null)
            {
                CurrentFighter.StartTurn(RoundNumber);
            }
        }

        public List<Fighter> BuildAlternatingTurnOrder()
        {
            // Summons are NOT ordered by initiative: they go stuck to whoever placed them, and only those
            // that have something to do at the start of their turn. It is what is seen in the captures, with
            // the Baliza de Supervivencia always playing right behind its Cra.
            // GROUPS are interleaved, not loose fighters: each group is one of the usual ones
            // with their summons behind. Interleaving one by one, the beacon got separated from its
            // Cra and played after the monster, when in the capture it goes immediately behind.
            var team0Sorted = Agrupar(Azul);
            var team1Sorted = Agrupar(Rojo);

            var result = new List<Fighter>();
            int maxCount = Math.Max(team0Sorted.Count, team1Sorted.Count);

            int team0BestInit = team0Sorted.FirstOrDefault()?[0].Initiative ?? 0;
            int team1BestInit = team1Sorted.FirstOrDefault()?[0].Initiative ?? 0;
            bool team0First = team0BestInit >= team1BestInit;

            for (int i = 0; i < maxCount; i++)
            {
                if (team0First)
                {
                    if (i < team0Sorted.Count) result.AddRange(team0Sorted[i]);
                    if (i < team1Sorted.Count) result.AddRange(team1Sorted[i]);
                }
                else
                {
                    if (i < team1Sorted.Count) result.AddRange(team1Sorted[i]);
                    if (i < team0Sorted.Count) result.AddRange(team0Sorted[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// The round THIS fight is on.
        ///
        /// It lived as a static integer of the handler, one for the whole server, so two
        /// players fighting at once shared the counter: when one moved to the next round, the other saw
        /// his buffs expire. Each fight carries its own.
        /// </summary>
        public int RoundNumber { get; private set; } = 1;

        /// <summary>
        /// The action number, which is what the client acknowledges on closing each sequence. It also
        /// was single for the whole server, and one player's client acknowledged numbers that
        /// another's fight had used up.
        /// </summary>
        private int _ultimaAccion;
        public int SiguienteAccion() => System.Threading.Interlocked.Increment(ref _ultimaAccion);

        // ─── The challenges ─────────────────────────────────────────────────────
        //
        // They go here and not in a static field of the handler for the same reason as the round: two
        // players fighting at once would have the same challenges, and whoever chose one would
        // change it for the other.

        /// <summary>How many have to be chosen. One in a normal fight, two in a dungeon.</summary>
        public int ChallengesToPick { get; set; } = 1;

        /// <summary>The two on the table right now. Empty if the list has not been asked for.</summary>
        public List<int> ChallengesOffered { get; } = new List<int>();

        /// <summary>Which of the two the player has marked, even if he has not validated it yet.</summary>
        public int ChallengeMarked { get; set; }

        /// <summary>The ones already fixed, with the percentage they were fixed with.</summary>
        public List<(int Id, int Percent)> ChallengesFixed { get; } = new List<(int, int)>();

        /// <summary>Are there challenges left to choose?</summary>
        public bool ChallengesPending => ChallengesFixed.Count < ChallengesToPick;

        // ─── What is needed to WATCH them ───────────────────────────────────────

        /// <summary>
        /// THIS fight's end is waiting for the client to acknowledge a sequence. Zero
        /// when none is waiting.
        ///
        /// It lived as a static of the handler, one for the whole server, and it was the most
        /// expensive fault there was: with two fights at once, one's acknowledgement closed the other's. And
        /// closing it is not cosmetic — it hands out the experience, the kamas and the loot on the session
        /// of whoever sent the acknowledgement, and writes it to the base. So one player was paid
        /// another's fight, and the owner never got his end screen.
        /// </summary>
        public int FinPendiente { get; set; }

        /// <summary>
        /// What <see cref="FinPendiente"/> holds when the end waits for the client's jwz, not for
        /// the jti of a sequence of its own: a monster's blows are not the client's to acknowledge,
        /// and no jti reaches this number.
        /// </summary>
        public const int WaitsForTheJwz = int.MaxValue;

        /// <summary>The ones already broken. It is reported once and not looked at again.</summary>
        public HashSet<int> ChallengesBroken { get; } = new HashSet<int>();

        /// <summary>Where and with how many MP whoever has the turn now started it.</summary>
        public int TurnStartCell { get; set; }
        public int TurnStartMp { get; set; }

        /// <summary>
        /// The MP the fighter whose turn it is has lost to tackles this turn. They are no MP he
        /// used: the Zombi challenge (1008205) says losing MP to a tackle does not break it.
        /// </summary>
        public int TurnTackledMp { get; set; }

        /// <summary>Whom to finish off before hitting another (challenges 31 and 32).</summary>
        public long ChallengeFocus { get; set; }

        /// <summary>The level of the last enemy that fell, for the order of deaths.</summary>
        public int LastKilledLevel { get; set; } = -1;

        /// <summary>The spells already used in the WHOLE fight, for the Ahorrador.</summary>
        public HashSet<int> SpellsEverUsed { get; } = new HashSet<int>();

        /// <summary>Who has finished someone off, for the Reparto.</summary>
        public HashSet<long> Killers { get; } = new HashSet<long>();

        /// <summary>The element hit with the first time, for the Elemental. Zero, none.</summary>
        public int DamageElement { get; set; }

        /// <summary>Enemies that have been hit and are still alive, for the Blitzkrieg.</summary>
        public HashSet<long> Wounded { get; } = new HashSet<long>();

        /// <summary>
        /// Whom each challenge points at: challenge → fighter. There are challenges that demand killing a specific one
        /// first, or last, or concentrating the attacks on him, and that «specific one» is chosen by the
        /// server and told to the client so it puts the mark on him.
        /// </summary>
        public Dictionary<int, long> ChallengeTargets { get; } = new Dictionary<int, long>();

        /// <summary>Who attacked each enemy first, for the Duelo.</summary>
        public Dictionary<long, long> FirstAttacker { get; } = new Dictionary<long, long>();

        /// <summary>In which round each enemy fell, for the Dum.</summary>
        public Dictionary<long, int> KilledOnRound { get; } = new Dictionary<long, int>();

        /// <summary>Where the one playing has finished someone off, for the Conquistador.</summary>
        public HashSet<int> KillCells { get; } = new HashSet<int>();

        /// <summary>Where each player came from on entering the fight, to return him there.</summary>
        public Dictionary<long, (long Mapa, int Casilla)> DeDondeVenian { get; }
            = new Dictionary<long, (long, int)>();
        public bool StartsNewRound { get; private set; } = false;

        public Fighter NextTurn()
        {
            CancelTurnTimer();
            StartsNewRound = false;

            // This is where the turn really changes, so this is where the counters are emptied. Before,
            // they were emptied in ResetTurnCastCounters, which only HandleTurnReadyAck calls, which nobody
            // calls: they were the only written proof of an intention that was not fulfilled.
            CastsThisTurn.Clear();
            CastsPerTargetThisTurn.Clear();
            CheckFightEnd();
            if (State == FightState.Ended) return null;

            int attempts = 0;
            do
            {
                CurrentTurnIndex++;
                if (CurrentTurnIndex >= TurnOrder.Count)
                {
                    CurrentTurnIndex = 0;
                    RoundNumber++;
                    StartsNewRound = true;

                    // The shields that have run their course drop here, with the change of round. If they do not
                    // expire, a two-round shield stays on until the end of the
                    // fight and it is not noticed: it only shows in the player holding out too long.
                    foreach (var quien in Azul) quien?.CaducarElEscudo(RoundNumber);
                    foreach (var quien in Rojo) quien?.CaducarElEscudo(RoundNumber);
                }
                attempts++;
            } while (!CurrentFighter.IsAlive && attempts < TurnOrder.Count);

            if (!CurrentFighter.IsAlive)
            {
                CheckFightEnd();
                return null;
            }

            CurrentFighter.StartTurn(RoundNumber);
            return CurrentFighter;
        }

        public bool RebuildTurnOrderOnFighterDeath()
        {
            var oldOrder = TurnOrder.ToList();
            var currentFighter = CurrentFighter;
            TurnOrder = BuildAlternatingTurnOrder();
            if (currentFighter != null && TurnOrder.Contains(currentFighter))
            {
                CurrentTurnIndex = TurnOrder.IndexOf(currentFighter);
            }
            else if (TurnOrder.Count > 0)
            {
                CurrentTurnIndex = CurrentTurnIndex % TurnOrder.Count;
            }

            return !oldOrder.SequenceEqual(TurnOrder);
        }

        public void CheckFightEnd()
        {
            // Summons do NOT count for knowing whether a side is still standing: killing the rival's
            // beacon does not win a fight. When whoever placed them dies they all drop on the
            // spot, so in practice this is a belt on top of the braces.
            bool team0Alive = Azul.Any(f => f.IsAlive && !f.EsInvocado);
            bool team1Alive = Rojo.Any(f => f.IsAlive && !f.EsInvocado);

            if (!team1Alive)
            {
                CancelPlacementTimer();
                CancelTurnTimer();
                State = FightState.Ended;
                WinnerTeamId = 0; // Players won!
            }
            else if (!team0Alive)
            {
                CancelPlacementTimer();
                CancelTurnTimer();
                State = FightState.Ended;
                WinnerTeamId = 1; // Monsters won!
            }
        }
    }
}
