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
        private (int Ronda, int Puesto) _turnoAtendido = (-1, -1);

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
        public bool AtenderElTurnoUnaVez(int round, int turnIndex)
        {
            lock (_candadoDelTurno)
            {
                if (_turnoAtendido == (round, turnIndex)) return false;
                _turnoAtendido = (round, turnIndex);
                return true;
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
        /// Los invocados a los que se les ha acabado el tiempo en esta ronda. El efecto 141 les
        /// cuelga la cuenta atrás al nacer y aquí se cobra.
        /// </summary>
        public List<Fighter> InvocadosQueSeDeshacen(int ronda)
        {
            var fuera = new List<Fighter>();
            foreach (var f in Azul) if (SeDeshace(f, ronda)) fuera.Add(f);
            foreach (var f in Rojo) if (SeDeshace(f, ronda)) fuera.Add(f);
            return fuera;
        }

        private static bool SeDeshace(Fighter f, int ronda)
            => f.EsInvocado && f.IsAlive && f.MuereEnRonda >= 0 && ronda >= f.MuereEnRonda;

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

        /// <summary>Se recoloca durante la fase de colocación, cada uno en las casillas de su lado.</summary>
        public void ChangePlacementCell(long fighterId, int newCellId)
        {
            if (State != FightState.Placement) return;

            int suyo = EquipoDe(fighterId);
            if (suyo < 0) return;

            var f = Buscar(fighterId);
            if (f != null && CasillasDe(suyo).Contains(newCellId))
            {
                f.CellId = newCellId;
            }
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
                CurrentFighter.StartTurn();
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

        /// <summary>Los que ya se han roto. Se avisa una vez y no se vuelve a mirar.</summary>
        public HashSet<int> ChallengesBroken { get; } = new HashSet<int>();

        /// <summary>Dónde y con cuántos PM empezó su turno el que lo tiene ahora.</summary>
        public int TurnStartCell { get; set; }
        public int TurnStartMp { get; set; }

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
                }
                attempts++;
            } while (!CurrentFighter.IsAlive && attempts < TurnOrder.Count);

            if (!CurrentFighter.IsAlive)
            {
                CheckFightEnd();
                return null;
            }

            CurrentFighter.StartTurn();
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
