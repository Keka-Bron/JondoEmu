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

        /// <summary>Las reglas de este combate: qué cambia respecto a pelear contra monstruos.</summary>
        /// <remarks>
        /// Eran dos banderas, <c>IsDuel</c> e <c>IsKoliseo</c>, y el motor las miraba en dieciséis
        /// sitios repartidos por cinco métodos. Ver <see cref="FightRules"/> para por qué esto y no
        /// dos motores.
        /// </remarks>
        public FightRules Reglas { get; set; } = FightRules.ContraMonstruos;

        /// <summary>Si enfrente hay personas y no monstruos. Lo mismo que decía IsDuel.</summary>
        public bool EsPvp => !Reglas.EnfrenteHayMonstruos;

        public long MapId { get; set; }
        /// <summary>Quiénes de este combate ya han recibido la preparación.</summary>
        /// <remarks>
        /// Esto era <c>HasLoadedMap</c>, UN booleano para el combate entero, y funcionaba mientras
        /// sólo hubiera una persona dentro: contra monstruos, el único jugador lo ponía y ya está.
        /// En un desafío hay dos, y el flujo es el mismo para cada uno por su propio socket:
        ///
        /// <code>
        ///   C-&gt;S  kmv          «ya estoy en el mapa de combate, dame los actores»
        ///   S-&gt;C  la preparación: jxg de cada combatiente, kba, jzu, kam, kaa, kae...
        /// </code>
        ///
        /// Con la bandera compartida, el PRIMERO en mandar el kmv la ponía y al segundo le
        /// contestaba que ya no había preparación pendiente. Su cliente se quedaba en modo rol —con
        /// su barra de hechizos, sin combatientes y sin botón de listo— mirando el mapa de antes.
        /// Que el desafío lo lanzara uno u otro no cambiaba nada: fallaba siempre el segundo en
        /// cargar el mapa, que es una carrera y no un papel.
        ///
        /// Por combatiente y no por combate, y con candado porque los dos clientes llegan por dos
        /// conexiones a la vez.
        /// </remarks>
        private readonly HashSet<long> _preparados = new HashSet<long>();

        /// <summary>El último turno cuyo «confírmame» ya se atendió, como ronda y posición.</summary>
        private (int Ronda, int Puesto, long Quien) _turnoAtendido = (-1, -1, -1);

        /// <summary>Su propio candado: no comparte nada con el de la preparacion.</summary>
        private readonly object _candadoDelTurno = new object();

        /// <summary>
        /// Deja pasar UNA sola confirmación por turno.
        /// </summary>
        /// <remarks>
        /// El servidor manda un «confírmame» (jxh) antes de cada turno y el cliente contesta con su
        /// jwz. Con una sola persona en el combate eso es una pregunta y una respuesta; en un
        /// desafío la pregunta va a los dos y contestan los dos, y lo que cuelga de la respuesta
        /// —deshacer invocados vencidos, barrer embrujos cumplidos, devolver puntos— tiene que
        /// pasar una vez y no dos. Aquí es donde se decide cuál de las dos respuestas hace el
        /// trabajo; a la otra sólo se le ignora.
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

        /// <summary>Si a este ya se le mandó la preparación.</summary>
        public bool HasPrepared(long fighterId)
        {
            lock (_preparados) return _preparados.Contains(fighterId);
        }

        /// <summary>Lo apunta como preparado. Devuelve false si ya lo estaba.</summary>
        /// <remarks>
        /// Apuntar y comprobar en la misma llamada es lo que impide que dos tramas del mismo
        /// cliente —el kmv y el kkr llegan casi juntos— manden la preparación dos veces.
        /// </remarks>
        public bool MarkPrepared(long fighterId)
        {
            lock (_preparados) return _preparados.Add(fighterId);
        }

        /// <summary>Lo desapunta, para volver a mandarle la preparación desde cero.</summary>
        public void ForgetPreparation(long fighterId)
        {
            lock (_preparados) _preparados.Remove(fighterId);
        }
        public FightState State { get; private set; } = FightState.Placement;

        // ═══════════════════════════════════════════════════════════════════
        //  Los dos bandos
        // ═══════════════════════════════════════════════════════════════════
        //
        // Se llamaban Team0 y Team1, y al lado ponía «// Players» y «// Monsters». Ciento cinco
        // referencias más adelante eso había dejado de ser un comentario y era una creencia: medio
        // motor daba por hecho que en el azul está quien juega y en el rojo hay bichos.
        //
        // Contra monstruos es verdad. En un desafío es verdad para uno de los dos, y de ahí salió
        // una clase entera de fallos -- el del rojo no podía recolocarse, no recibía sus esperas
        // iniciales, no podía abandonar, y su «listo» no contaba -- que no llevaban ningún «if»
        // porque nadie sabía que eran supuestos.
        //
        // Azul y Rojo son los colores de las casillas de colocación y no prometen nada sobre quién
        // hay dentro. Debajo están las tres preguntas que el motor hacía a mano en sesenta sitios.

        /// <summary>El bando que empieza: quien provoca el combate, o quien reta.</summary>
        public const int Azules = 0;

        /// <summary>El otro: los monstruos, o el retado.</summary>
        public const int Rojos = 1;

        public List<Fighter> Azul { get; } = new List<Fighter>();
        public List<Fighter> Rojo { get; } = new List<Fighter>();

        /// <summary>The Koliseo mode this fight was matched in, for the ladder; -1 for none.</summary>
        public int KoliseoMode { get; set; } = -1;

        public List<int> BluePlacementCells { get; } = new List<int>();
        public List<int> RedPlacementCells { get; } = new List<int>();

        /// <summary>Los del bando que se diga.</summary>
        public List<Fighter> Bando(int equipo) => equipo == Rojos ? Rojo : Azul;

        /// <summary>Las casillas de colocación de ese bando.</summary>
        public List<int> CasillasDe(int equipo) => equipo == Rojos ? RedPlacementCells : BluePlacementCells;

        /// <summary>Todos, de los dos bandos.</summary>
        public IEnumerable<Fighter> Todos => Azul.Concat(Rojo);

        /// <summary>Cualquiera del combate, del bando que sea. Null si no está.</summary>
        public Fighter Buscar(long fighterId)
            => Azul.FirstOrDefault(f => f.Id == fighterId)
               ?? Rojo.FirstOrDefault(f => f.Id == fighterId);

        /// <summary>En qué bando está, o -1 si no está en el combate.</summary>
        public int EquipoDe(long fighterId)
        {
            if (Azul.Exists(f => f.Id == fighterId)) return Azules;
            if (Rojo.Exists(f => f.Id == fighterId)) return Rojos;
            return -1;
        }

        /// <summary>El bando contrario al que se diga.</summary>
        public static int Contrario(int equipo) => equipo == Rojos ? Azules : Rojos;

        /// <summary>Los de su lado, él incluido. Vacío si no está en el combate.</summary>
        public List<Fighter> Aliados(long fighterId)
        {
            int suyo = EquipoDe(fighterId);
            return suyo < 0 ? new List<Fighter>() : Bando(suyo);
        }

        /// <summary>Los del otro lado. Vacío si no está en el combate.</summary>
        public List<Fighter> Enemigos(long fighterId)
        {
            int suyo = EquipoDe(fighterId);
            return suyo < 0 ? new List<Fighter>() : Bando(Contrario(suyo));
        }

        /// <summary>Si a ese bando le queda alguien en pie.</summary>
        /// <remarks>
        /// Esto se escribía a mano como <c>Team0.Exists(f =&gt; f.IsAlive)</c> y se llamaba
        /// «alliesAlive», que sólo es verdad si quien pregunta está en el azul. Con el bando por
        /// delante ya no se puede escribir al revés sin darse cuenta.
        /// </remarks>
        public bool SigueVivo(int equipo) => Bando(equipo).Exists(f => f.IsAlive);

        /// <summary>Si ganó quien pregunta. Falso también para quien no estaba.</summary>
        public bool HaGanado(long fighterId)
        {
            int suyo = EquipoDe(fighterId);
            return suyo >= 0 && SigueVivo(suyo);
        }

        public long ChallengerLeaderId => Azul.FirstOrDefault()?.Id ?? 0;
        public long DefenderLeaderId { get; set; } = -20000;

        /// <summary>
        /// Cuántas veces ha lanzado cada uno cada hechizo en el turno que corre.
        /// </summary>
        /// <remarks>
        /// DEL COMBATE, y con el lanzador en la clave. Estaban en dos diccionarios ESTÁTICOS de
        /// FightHandler indexados sólo por el id del hechizo, así que con dos clientes peleando a la
        /// vez los lanzamientos de uno contaban contra los del otro en cuanto compartían hechizo
        /// —los ids de hechizo se repiten entre jugadores—.
        ///
        /// Y peor: no se vaciaban nunca. Lo único que los limpiaba estaba dentro de un método sin un
        /// solo llamante, así que al tercer lanzamiento del PROCESO —sumando todos los jugadores y
        /// todos los combates— el hechizo quedaba rechazado con «ya gastado este turno» para todo el
        /// mundo hasta reiniciar el servidor.
        /// </remarks>
        public Dictionary<(long Caster, long Spell), int> CastsThisTurn { get; }
            = new Dictionary<(long, long), int>();

        /// <summary>Lo mismo, por objetivo: el tope de lanzamientos sobre la misma criatura.</summary>
        public Dictionary<(long Caster, long Spell, long Target), int> CastsPerTargetThisTurn { get; }
            = new Dictionary<(long, long, long), int>();

        public List<Fighter> TurnOrder { get; private set; } = new List<Fighter>();
        public int CurrentTurnIndex { get; private set; } = 0;
        public Fighter CurrentFighter => TurnOrder.Count > 0 ? TurnOrder[CurrentTurnIndex] : null;

        public int WinnerTeamId { get; private set; } = -1;

        public long RoleplayMapId { get; set; }
        public long ArenaMapId { get; set; }

        /// <summary>
        /// Cuándo empezó a pelearse de verdad, para saber lo que ha durado: la pantalla de fin de
        /// combate lo enseña arriba a la derecha y sin esto salía 00:00.
        /// </summary>
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// El número que le toca al siguiente embrujo. Es del COMBATE, no de cada luchador: en la
        /// captura los del jugador y los del monstruo van en la misma serie, y es el número con el
        /// que luego se quita cada uno.
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

        /// <summary>Ocho por bando: por debajo de esto no hay sitio para colocar dos equipos.</summary>
        public const int PlacesForBothTeams = 16;

        /// <summary>
        /// Las casillas de colocación tal cual, cuando el mapa ya las trae.
        /// </summary>
        /// <remarks>
        /// Las arenas de koliseo las marcan en el propio cliente, bando por bando, así que ahí no
        /// hay nada que repartir: <see cref="GeneratePlacementCells"/> parte una lista de casillas
        /// pisables por la mitad porque en un arena corriente no hay otra cosa.
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

            // Menos de dieciseis no da para dos equipos de ocho, y partirlas por la mitad daria
            // uno o dos huecos por bando: con una sola casilla roja los cinco monstruos se
            // colocan encima unos de otros, y golpear esa casilla hiere a uno y a los demas no.
            // El listón era "cero", que es justo el caso que no pasaba: en el arena 188752387
            // llegaban DOS, y dos no es cero.
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
        /// Mete a un JUGADOR en el equipo contrario. Es lo que hace de un combate un duelo.
        /// </summary>
        /// <remarks>
        /// <see cref="AddPlayer"/> fuerza el equipo cero, porque hasta ahora el unico combate que
        /// existia era uno contra monstruos y todos los jugadores iban del mismo lado. En un
        /// desafio hay una persona a cada lado, y la casilla sale del lado rojo por lo mismo: dos
        /// jugadores en las casillas azules empezarian pegados.
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
        /// El siguiente identificador libre para un invocado.
        ///
        /// Los combatientes que no son jugadores llevan número negativo y se reparten de uno en
        /// uno: en las capturas del Ocra los pious son -1 y -2 y la primera baliza sale con el -3,
        /// la segunda con el -4, y así. Se mira lo que ya hay para no pisar a nadie.
        /// </summary>
        public long SiguienteIdDeInvocado()
        {
            long menor = 0;
            foreach (var f in Azul) if (f.Id < menor) menor = f.Id;
            foreach (var f in Rojo) if (f.Id < menor) menor = f.Id;
            return menor - 1;
        }

        /// <summary>
        /// Mete un invocado en el combate, en el bando del que lo invoca, y rehace el orden de
        /// turnos para que le toque jugar.
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

            // El que está jugando ahora mismo sigue jugando: se rehace la lista pero se conserva
            // a quién le toca, que si no el turno se le va al de al lado en mitad de una acción.
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
        /// Rehace la lista de turnos conservando a quién le toca ahora mismo.
        /// </summary>
        /// <remarks>
        /// Lo que hace falta cuando alguien SALE de la lista a media ronda. Agrupar filtra por
        /// IsAlive, así que rehacerla lo quita y todos los de detrás se corren un hueco; sin
        /// repuntar CurrentTurnIndex, el turno se le iría al de al lado en mitad de una acción.
        ///
        /// Es lo mismo que ya hacía <see cref="Invocar"/> para el caso contrario, cuando alguien
        /// ENTRA. Sacado aquí para que las dos direcciones no puedan separarse.
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
        /// Un bando en orden de juego: los de siempre por iniciativa, y detrás de cada uno los que
        /// haya invocado, en el orden en que los sacó.
        ///
        /// Los invocados que no juegan turno se quedan fuera de la lista, pero siguen estando en
        /// el combate: se les puede pegar y cuentan para el tablero.
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

        /// <summary>Alguien se declara listo. Devuelve si con eso ya lo están todos.</summary>
        /// <remarks>
        /// Miraba sólo el azul, en las dos mitades. En un desafío eso significaba que el combate
        /// arrancaba en cuanto pulsaba listo el RETADOR, sin esperar al otro —su bando estaba
        /// entero listo porque era él solo— y que el «listo» del retado no se apuntaba en ninguna
        /// parte. Es lo que se veía como «uno ya está peleando y el otro sigue en colocación».
        ///
        /// Un monstruo no pulsa nada, así que para contar sólo cuentan las personas; si en un
        /// bando no hay ninguna —el caso de siempre contra monstruos— ese bando está listo.
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

        /// <summary>Se recoloca durante la fase de colocación, cada uno en las casillas de su lado.</summary>
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

        /// <summary>Si ya se le ha dicho al cliente que deje de regenerar vida.</summary>
        /// <remarks>
        /// La pareja lqg + lqt se manda una sola vez por combate, que es como sale en la captura.
        /// Iba sólo en la rama de «todos listos», así que un combate que arrancara porque se acabó
        /// el tiempo de colocación —el caso normal contra monstruos si nadie pulsa— se quedaba sin
        /// ella, y el cliente seguía rellenando la barra de vida de uno en uno dentro de la pelea.
        /// </remarks>
        public bool RegeneracionApagada { get; set; }

        /// <summary>Lo que hay puesto en el suelo de esta arena: glifos, trampas y runas.</summary>
        /// <remarks>
        /// Vive en el combate y no en un registro global a propósito: dos combates a la vez en la
        /// misma arena de instancia tendrían glifos distintos, y un registro por mapa los
        /// mezclaría.
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

        /// <summary>Pone algo en el suelo y le da su identificador.</summary>
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

        /// <summary>Lo que se dispara con alguien pisando esa casilla.</summary>
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

        /// <summary>Lo que se dispara con alguien empezando su turno ahí.</summary>
        public List<Glifo> LosQueEmpiezan(int casilla)
        {
            var salen = new List<Glifo>();
            foreach (var g in Glifos)
            {
                if (g.SeDisparaAlEmpezarElTurno && g.Cubre(casilla)) salen.Add(g);
            }
            return salen;
        }

        /// <summary>Quita los que se han gastado o cumplido. Devuelve cuántos se ha llevado.</summary>
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
            // Los invocados NO se ordenan por iniciativa: van pegados a quien los puso, y sólo los
            // que tengan algo que hacer al empezar su turno. Es lo que se ve en las capturas, con
            // la Baliza de Supervivencia jugando siempre justo detrás de su Ocra.
            // Se intercalan GRUPOS, no combatientes sueltos: cada grupo es uno de los de siempre
            // con sus invocados detrás. Intercalando de uno en uno, la baliza se separaba de su
            // Ocra y jugaba después del monstruo, cuando en la captura va inmediatamente detrás.
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
        /// La ronda en la que va ESTE combate.
        ///
        /// Vivía como un entero estático del manejador, uno para todo el servidor, así que dos
        /// jugadores peleando a la vez compartían el contador: al pasar de ronda uno, el otro veía
        /// caducar sus embrujos. Cada combate lleva el suyo.
        /// </summary>
        public int RoundNumber { get; private set; } = 1;

        /// <summary>
        /// El número de acción, que es lo que el cliente acusa al cerrar cada secuencia. También
        /// era único para todo el servidor, y el cliente de un jugador acusaba números que había
        /// gastado el combate de otro.
        /// </summary>
        private int _ultimaAccion;
        public int SiguienteAccion() => System.Threading.Interlocked.Increment(ref _ultimaAccion);

        // ─── Los retos ──────────────────────────────────────────────────────────
        //
        // Van aquí y no en un campo estático del manejador por lo mismo que la ronda: dos
        // jugadores peleando a la vez tendrían los mismos retos, y el que eligiera uno se lo
        // cambiaría al otro.

        /// <summary>Cuántos hay que elegir. Uno en un combate normal, dos en mazmorra.</summary>
        public int ChallengesToPick { get; set; } = 1;

        /// <summary>Los dos que están sobre la mesa ahora mismo. Vacío si no se ha pedido la lista.</summary>
        public List<int> ChallengesOffered { get; } = new List<int>();

        /// <summary>Cuál de los dos tiene marcado el jugador, aunque todavía no lo haya validado.</summary>
        public int ChallengeMarked { get; set; }

        /// <summary>Los que ya están fijados, con el porcentaje con el que se fijaron.</summary>
        public List<(int Id, int Percent)> ChallengesFixed { get; } = new List<(int, int)>();

        /// <summary>¿Quedan retos por elegir?</summary>
        public bool ChallengesPending => ChallengesFixed.Count < ChallengesToPick;

        // ─── Lo que hace falta para VIGILARLOS ──────────────────────────────────

        /// <summary>
        /// El final de ESTE combate está esperando a que el cliente acuse una secuencia. Cero
        /// cuando no hay ninguno esperando.
        ///
        /// Vivía como un estático del manejador, uno para todo el servidor, y era la avería más
        /// cara que había: con dos combates a la vez, el acuse de uno cerraba el del otro. Y
        /// cerrarlo no es cosmético — reparte la experiencia, los kamas y el botín sobre la sesión
        /// de quien mandó el acuse, y lo escribe en la base. O sea que un jugador cobraba el
        /// combate de otro, y al dueño no le llegaba nunca su pantalla de fin.
        /// </summary>
        public int FinPendiente { get; set; }

        /// <summary>
        /// What <see cref="FinPendiente"/> holds when the end waits for the client's jwz, not for
        /// the jti of a sequence of its own: a monster's blows are not the client's to acknowledge,
        /// and no jti reaches this number.
        /// </summary>
        public const int WaitsForTheJwz = int.MaxValue;

        /// <summary>Los que ya se han roto. Se avisa una vez y no se vuelve a mirar.</summary>
        public HashSet<int> ChallengesBroken { get; } = new HashSet<int>();

        /// <summary>Dónde y con cuántos PM empezó su turno el que lo tiene ahora.</summary>
        public int TurnStartCell { get; set; }
        public int TurnStartMp { get; set; }

        /// <summary>
        /// The MP the fighter whose turn it is has lost to tackles this turn. They are no MP he
        /// used: the Zombi challenge (1008205) says losing MP to a tackle does not break it.
        /// </summary>
        public int TurnTackledMp { get; set; }

        /// <summary>A quién hay que rematar antes de pegarle a otro (retos 31 y 32).</summary>
        public long ChallengeFocus { get; set; }

        /// <summary>El nivel del último enemigo que cayó, para el orden de muertes.</summary>
        public int LastKilledLevel { get; set; } = -1;

        /// <summary>Los hechizos ya usados en TODO el combate, para el Ahorrador.</summary>
        public HashSet<int> SpellsEverUsed { get; } = new HashSet<int>();

        /// <summary>Quiénes han rematado a alguien, para el Reparto.</summary>
        public HashSet<long> Killers { get; } = new HashSet<long>();

        /// <summary>El elemento con el que se pegó la primera vez, para el Elemental. Cero, ninguno.</summary>
        public int DamageElement { get; set; }

        /// <summary>Enemigos a los que se ha pegado y siguen vivos, para el Blitzkrieg.</summary>
        public HashSet<long> Wounded { get; } = new HashSet<long>();

        /// <summary>
        /// A quién señala cada reto: reto → luchador. Hay retos que exigen matar a uno concreto
        /// el primero, o el último, o concentrarle los ataques, y ese «uno concreto» lo elige el
        /// servidor y se lo dice al cliente para que le ponga la marca encima.
        /// </summary>
        public Dictionary<int, long> ChallengeTargets { get; } = new Dictionary<int, long>();

        /// <summary>Quién atacó primero a cada enemigo, para el Duelo.</summary>
        public Dictionary<long, long> FirstAttacker { get; } = new Dictionary<long, long>();

        /// <summary>En qué ronda cayó cada enemigo, para el Dum.</summary>
        public Dictionary<long, int> KilledOnRound { get; } = new Dictionary<long, int>();

        /// <summary>Dónde ha rematado a alguien el que juega, para el Conquistador.</summary>
        public HashSet<int> KillCells { get; } = new HashSet<int>();

        /// <summary>De dónde salió cada jugador al entrar en combate, para devolverlo ahí.</summary>
        public Dictionary<long, (long Mapa, int Casilla)> DeDondeVenian { get; }
            = new Dictionary<long, (long, int)>();
        public bool StartsNewRound { get; private set; } = false;

        public Fighter NextTurn()
        {
            CancelTurnTimer();
            StartsNewRound = false;

            // Aquí es donde cambia el turno de verdad, así que aquí se vacían los contadores. Antes
            // se vaciaban en ResetTurnCastCounters, que sólo llama HandleTurnReadyAck, que no llama
            // nadie: eran la única prueba escrita de una intención que no se cumplía.
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

                    // Los escudos que ya cumplieron se caen aquí, con el cambio de ronda. Si no
                    // se caducan, un escudo de dos rondas se queda puesto hasta el final del
                    // combate y no se nota: sólo se ve en que el jugador aguanta de más.
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
            // Los invocados NO cuentan para saber si un bando sigue en pie: matar la baliza del
            // rival no gana un combate. Cuando el que las puso se muere se le caen todas en el
            // acto, así que en la práctica esto es un cinturón además de los tirantes.
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
