using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using static Jondo.Unity.Server.Network.NetworkEnvelope;
using static Jondo.Protocol.NetworkMessage;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    public static partial class FightHandler
    {
        private static ConcurrentDictionary<long, FightInstance> _activeFights = new ConcurrentDictionary<long, FightInstance>();
        private static long _nextFightId = 1000;

        /// <summary>Cuantos combates hay abiertos ahora mismo. Lo pinta la ventana del servidor.</summary>
        public static int CombatesEnCurso => _activeFights.Count;

        /// <summary>Turn duration in tenths of a second, exactly as it travels in jut.f1 and jyf.f2.</summary>
        public const int TurnDurationDeciseconds = 300;
        /// <summary>The same duration in milliseconds, for the server-side timer.</summary>
        private const int TurnDurationMs = TurnDurationDeciseconds * 100;

        public static void RegisterHandlers()
        {
            Program.LogDebug("[FightHandler] Combat handlers registered for jxx, jyk, jyz, jza, jwb, hoy.");
        }

        /// <summary>
        /// Called by MapChangeHandler when the player's movement path terminates on a mob's cell.
        /// Builds the FightInstance from real mob data and sends placement bursts 1 and 2.
        /// </summary>
        public static async Task InitiateFightFromMobCollision(NetworkStream stream, MobSpawnManager.MobGroup mobGroup, long mapId, long mobContextId = 0)
        {
            // A group already being fought went off the map with a kmu: it is nobody's to attack
            // a second time. Its fight is joined by its swords (kay), not by clicking the group.
            if (IsGroupFighting(mobGroup.MobId))
            {
                Program.LogDebug($"[Fight] Group {mobGroup.MobId} is already being fought; no second fight.");
                return;
            }

            // Si a este jugador le quedaba un combate colgado de antes, se le quita el suyo y sólo
            // el suyo. Aquí había un _activeFights.Clear(): empezar una pelea borraba las de TODOS
            // los demás jugadores del servidor. El final normal de un combate ya lo quita solo
            // (TryRemove más abajo, al mandar el resultado), así que esto es sólo la red por si
            // alguno se quedó suelto.
            long yo = GameState.CharacterId;
            foreach (var par in _activeFights)
            {
                bool esSuyo = par.Value.EquipoDe(yo) >= 0;
                if (esSuyo) _activeFights.TryRemove(par.Key, out _);
            }

            GameState.IsInFight = true;
            GameState.CurrentFightMobId = mobGroup.MobId;

            long fightId = System.Threading.Interlocked.Increment(ref _nextFightId);
            long arenaMapId = MapManager.ResolveArenaMapId(mapId);
            var fight = new FightInstance(fightId, mapId, arenaMapId);

            // En un kanojedo se pelea con el libro del entrenamiento: sin retos, sin botín y sin
            // que el puch desaparezca al ganar. Lo decide el mapa -donde está el puch maestro- y
            // no el monstruo, porque el saco al que se le pega y el que el maestro compone son
            // los mismos bichos y da igual por dónde se entre.
            if (Managers.Kanojedo.IsDojo(mapId)) fight.Reglas = FightRules.Entrenamiento;

            // El id contextual del grupo ES su MobId, el mismo que viaja en el jss y en el jpv y el
            // mismo que el cliente devuelve al clicarlo. El parámetro mobContextId sobra desde que
            // los dos paquetes reparten el mismo número; se queda por las llamadas de fuera.
            fight.DefenderLeaderId = mobGroup.MobId;

            // LAS CASILLAS DE COLOCACION SALEN DE GetFightWalkable, no del filtro de siembra.
            //
            // GetInnerWalkableCells es un filtro de SUPERFICIE: se queda solo con las casillas
            // cuyas doce vecinas en radio 2 son todas andables y que estan lejos del borde, que es
            // lo que hace falta para plantar un grupo de monstruos en un mapa de rol y no lo que
            // hace falta para colocar dos equipos en un arena. En el arena 188752387 pasan 2 de
            // sus 77 casillas, y su unica red de seguridad -"si no queda ninguna, usalas todas"-
            // no salta porque 2 no es cero. De ahi salen 1 casilla azul y 1 roja, y los cinco
            // monstruos acaban apilados en la misma.
            //
            // Incarnam funcionaba por casualidad: alli el filtro deja 0 de 65, la red salta y se
            // usan las 65.
            //
            // Medido sobre los 15.360 mapas: con el filtro de siembra, 7.388 se quedan por debajo
            // de 8 casillas rojas y 987 se quedan en UNA. Con GetFightWalkable, 15.354 tienen las
            // 8. Es ademas el conjunto en el que ya confia el resto del combate -moverse y la
            // linea de vision preguntan a este mismo-.
            //
            // La copia con ToList no es cosmetica: GetFightWalkable devuelve el HashSet vivo de
            // MapManager, y GeneratePlacementCells recibiria los datos del mapa para siempre.
            fight.GeneratePlacementCells(PlacementGround(arenaMapId));

            // Las cuatro elementales COMPLETAS: lo que el jugador se ha puesto de puntos más lo que
            // le dé el equipo. Se calculan aquí arriba porque la iniciativa las necesita enteras.
            var playerFighter = BuildPlayerFighter(fight);
            playerFighter.CellId = fight.BluePlacementCells.FirstOrDefault();
            fight.AddPlayer(playerFighter);

            // Build Monster Fighters from real MobGroup data.
            // Fighter IDs for monsters MUST be sequential negative numbers per fight (-1, -2, -3...)
            long monsterSeqId = -1;
            int redIdx = 0;
            // All of an ordinary group; of a dungeon room's, the first clamp(players, 4, 8).
            int players = fight.Azul.Count(f => !f.IsMonster);
            foreach (var member in MobSpawnManager.MembersFor(mobGroup, players))
            {
                long monFighterId = monsterSeqId--;
                int monCellId = (fight.RedPlacementCells.Count > redIdx)
                    ? fight.RedPlacementCells[redIdx++]
                    : fight.RedPlacementCells.FirstOrDefault();

                fight.AddMonster(BuildMonsterFighter(member, monFighterId, monCellId));
            }

            // On a dream's last room this is the Fin du rêve: its first wave at its level.
            DreamHandler.OnFightCreated(fight);

            _activeFights[fightId] = fight;
            Program.LogDebug($"[FightHandler] Fight #{fightId} created on map {mapId}:");
            Program.LogDebug($"  Team 0 (Players): {fight.Azul.Count} fighters (Leader ID: {fight.ChallengerLeaderId})");
            Program.LogDebug($"  Team 1 (Monsters): {fight.Rojo.Count} fighters (Context ID: {fight.DefenderLeaderId})");
            foreach (var m in fight.Rojo)
            {
                Program.LogDebug($"    - Monster ID {m.MonsterId} (Fighter ID {m.Id}, Level {m.Level}, HP {m.MaxHP}, BoneId {m.LookBoneId})");
            }

            // Al mapa de combate, y la preparación NO se manda aquí.
            //
            // En la captura el servidor hace primero un cambio de mapa entero —kub, jru, lqu, hjk,
            // lva— y se queda esperando; el cliente contesta con ijm y kmv, y sólo entonces llegan
            // las jxg, el kba y los demás. Mandándolo antes, el cliente todavía está en el mapa de
            // superficie, no tiene contexto de combate y se lo come sin decir nada: en el registro
            // se ve la preparación saliendo y en pantalla no pasa nada.
            // Donde esta de pie en el mapa de rol, ANTES de que la linea de abajo lo mande al
            // arena. Se guardaba despues, y para entonces GameState.CellId ya era la casilla del
            // arena: al acabar el combate se le devolvia a una casilla que en el mapa de rol
            // muchas veces NO EXISTE -en el taller de Incarnam, la 189 sobre un mapa cuyas 42
            // casillas van de la 244 a la 414- y el cliente no lo dibujaba en ningun sitio.
            int casillaDeRol = GameState.CellId;

            GameState.MapId = fight.MapId;
            GameState.CellId = fight.Azul.Count > 0 ? fight.Azul[0].CellId : GameState.CellId;
            fight.ForgetPreparation(GameState.CharacterId);

            // De dónde se salió, para poder volver. El mapa de combate es de instancia y no vale
            // como sitio donde dejar al personaje.
            // NOT announced to the roleplay map with a jsd, and that is measured now.
            //
            // What a bystander receives when somebody beside him starts a fight is in «entrar a
            // combate con listo automatico y entrada automatica siguiendo a lider de grupo»,
            // frames 131-141: no jsd, but a kmu for the group and one for the attacker, then the
            // swords (hpy), the count of fights on the map (jqz) and the teams (kae). Sent by
            // AfterFightOpenedAsync below; see FightJoin.cs.
            var suyo = Network.SessionContext.State;
            suyo.FightId = fight.FightId;
            suyo.RoleplayMapId = fight.RoleplayMapId;
            suyo.RoleplayCellId = casillaDeRol;
            fight.DeDondeVenian[suyo.CharacterId] = (fight.RoleplayMapId, casillaDeRol);

            // Sin guardar el personaje: el mapa de combate es un mapa de instancia y dejarlo escrito
            // en la ficha lo devolvería ahí al volver a entrar, a un sitio del que no se sale.
            //
            // Con eso no basta, ojo: cualquier otra cosa que guarde el personaje mientras dura el
            // combate —comprar, cobrar kamas, subir características— lo escribe igual, y como el
            // final de combate todavía no está hecho, el personaje se queda atrapado en la arena.
            // Por eso <see cref="LeaveFight"/> lo devuelve, y lo llama la salida al menú.
            await SendFightEntryAsync(stream, fight);

            // Cuando se acaba la cuenta atrás de la colocación se empieza igual. El cliente la lleva
            // por su cuenta —el servidor real no manda ni un temporizador entre las casillas y el
            // botón de listo— así que aquí sólo hace falta el plazo.
            //
            // Esto tenía tres agujeros y los tres eran del mismo tipo: escribía en el socket sin
            // el candado, así que su ráfaga podía entrelazarse con la de quien estuviera
            // atendiendo al cliente y partir una trama por la mitad; se plantaba 45 segundos sin
            // manera de pararlo, de modo que sobrevivía a la desconexión y acababa escribiendo en
            // un socket cerrado; y no recogía nada, así que ese fallo se perdía sin rastro.
            //
            // El sitio donde apuntar el temporizador ya estaba —FightInstance.PlacementTimerCts—
            // y CancelPlacementTimer() se llama desde cuatro sitios, pero NADIE lo asignaba nunca:
            // se cancelaba un null. Ésa era la mitad que faltaba.
            StartPlacementCountdown(stream, fight, fight.Reglas.RelojDeColocacion * 100);

            // And now the rest of the world: his side and theirs before his board, the swords for
            // the map, the ilh for his party and the members who follow him in. See FightJoin.cs.
            await AfterFightOpenedAsync(fight, mobGroup, casillaDeRol);
        }

        /// <summary>Arranca el plazo de colocación de un combate.</summary>
        /// <remarks>
        /// Estaba escrita a pelo dentro de <see cref="InitiateFightFromMobCollision"/> con los
        /// cuarenta y cinco segundos metidos en la línea. El koliseo tiene los suyos —sesenta, que
        /// es el 592 del kaa de la captura— y un desafío no tiene ninguno, así que el plazo pasa a
        /// ser un parámetro y el que no quiera reloj sencillamente no llama.
        /// </remarks>
        private static void StartPlacementCountdown(NetworkStream stream, FightInstance fight,
                                                    int timeoutMs)
        {
            long currentFightId = fight.FightId;
            var cuentaAtras = new System.Threading.CancellationTokenSource();
            fight.CancelPlacementTimer();
            fight.PlacementTimerCts = cuentaAtras;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(timeoutMs, cuentaAtras.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                // El mismo candado que usan el reloj de turno y lo que llega del cliente. Sin él,
                // pulsar «listo» justo al vencer el plazo arrancaba el combate dos veces.
                var turno = MiTurno();
                await turno.WaitAsync();
                try
                {
                    // By its id: the one who opened it may have left the placement since, and his
                    // session no longer has a fight to find.
                    var f = FightById(currentFightId);
                    if (f == null || f.FightId != currentFightId) return;
                    if (f.State != Jondo.Unity.World.Fights.FightState.Placement) return;

                    Program.LogDebug($"[Combate] Se acabó el tiempo de colocación del combate #{currentFightId}.");
                    if (GetCurrentFight() == f) await HandleTurnReady(stream, Array.Empty<byte>());

                    // Whoever came in later and has not pressed ready is not waited for either.
                    await StartWhoeverIsLeftAsync(f);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Combate] La cuenta atrás de la colocación se atragantó: {ex.Message}");
                }
                finally
                {
                    turno.Release();
                }
            });
        }


        /// <summary>De dónde salió el jugador al entrar en combate, para devolverlo ahí.</summary>

        /// <summary>
        /// Saca al personaje del combate y lo devuelve al mapa de superficie.
        ///
        /// Hace falta porque el final de combate todavía no está hecho: sin esto, quien entra en
        /// una pelea se queda guardado en el mapa de arena, que es de instancia, y al volver a
        /// entrar al juego aparece en un sitio del que no se sale.
        /// </summary>
        public static void LeaveFight()
        {
            // Solo lo SUYO. Antes borraba el final pendiente y paraba el reloj de todo el
            // servidor, asi que cualquiera que volviera a la pantalla de personajes le dejaba a
            // otro el combate colgado y sin reloj.
            var mio = GetCurrentFight();
            if (mio != null)
            {
                mio.FinPendiente = 0;
                mio.CancelTurnTimer();

                // Y la cuenta atrás de la colocación, que si no sigue viva 45 segundos y acaba
                // escribiendo en un socket que ya no está.
                mio.CancelPlacementTimer();
            }

            var suyo = Network.SessionContext.State;
            if (suyo.RoleplayMapId == 0) return;

            suyo.IsInFight = false;
            suyo.FightId = 0;
            BackToRoleplayMap();

            // SÓLO el combate de este jugador. Aquí había un _activeFights.Clear(), que se llevaba
            // por delante los combates de todos los demás: al acabar uno el suyo, al resto se le
            // evaporaba la pelea a media pantalla.
            long quien = suyo.CharacterId;
            foreach (var par in _activeFights)
            {
                bool esSuyo = par.Value.EquipoDe(quien) >= 0;
                if (esSuyo) _activeFights.TryRemove(par.Key, out _);
            }

            Program.LogDebug("[Combate] Fuera del combate; el personaje vuelve al mapa de superficie.");
        }

        /// <summary>
        /// Puts the character back on the map he left to fight, and saves him there. Nothing
        /// happens when he did not leave one.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="LeaveFight"/> because the socket teardown needs this half and
        /// not the other: a client closed in the middle of a fight -- killed, crashed, the cable
        /// -- was saved ON THE ARENA, since only the kqq of "back to the character list" went
        /// through LeaveFight. The next login then loaded the tactical map with the fight's music
        /// and the roleplay monsters spawned on it, and a zaap was the only way out. The fight
        /// itself is left alone here: in a challenge the other player is still in it.
        /// </remarks>
        public static void BackToRoleplayMap()
        {
            var suyo = Network.SessionContext.State;
            if (suyo.RoleplayMapId == 0) return;

            suyo.MapId = suyo.RoleplayMapId;
            // Y siempre sobre una casilla que EXISTA en el mapa al que vuelve, igual que hacen
            // los otros cuatro teletransportes de este emulador -el zaap, la puerta, el .teleport
            // y el merkasako-, que todos pasan por aqui. La linea de arriba ya guarda la casilla
            // buena, asi que esto normalmente no cambia nada; existe por las fichas que ya estan
            // guardadas con una casilla de arena de antes de este arreglo, que si no entrarian al
            // mundo invisibles hasta dar un paso.
            if (suyo.RoleplayCellId != 0)
            {
                suyo.CellId = MapManager.GetNearestWalkableCell(suyo.MapId, suyo.RoleplayCellId);
            }

            DatabaseManager.SaveCurrentCharacter();

            suyo.RoleplayMapId = 0;
            suyo.RoleplayCellId = 0;
        }

        /// <summary>
        /// El combate que está esperando a que el cliente pida los actores del mapa, o null.
        ///
        /// Sirve para que el jrh de un combate conteste con la preparación en vez de con el jss
        /// normal del mapa.
        /// </summary>
        public static FightInstance? PendingPreparation()
        {
            var fight = GetCurrentFight();
            if (fight == null) return null;

            // Por combatiente: en un desafio hay dos clientes pidiendo lo suyo y que uno ya tenga
            // la preparacion no dice nada del otro.
            if (fight.HasPrepared(Network.SessionContext.State.CharacterId)) return null;

            return fight.State == Jondo.Unity.World.Fights.FightState.Placement ? fight : null;
        }

        /// <summary>
        /// La preparación del combate, tal y como la manda el servidor real.
        ///
        ///   jxg   una por combatiente
        ///   kba   las casillas azules y las rojas
        ///   jzu   quién va en cada equipo
        ///   jwq   vacío
        ///   jrk   el mapa donde se pelea
        ///
        /// Está medido de las quince capturas de combate; las formas y su comprobación byte a byte
        /// contra la captura viven en <see cref="Network.FightProtocol"/>.
        ///
        /// Durante la colocación el bando enemigo viaja entero como -1: el servidor real no reparte
        /// identificadores a los monstruos hasta que el combate empieza de verdad. Aquí se hace
        /// igual aunque por dentro ya existan, porque es lo que el cliente espera ver.
        /// </summary>
        /// <summary>
        /// Monta el combate de un desafio aceptado: un jugador a cada lado.
        /// </summary>
        /// <remarks>
        /// La diferencia con un combate contra monstruos no es el numero de participantes, es que
        /// hay DOS SESIONES. Cada personaje tiene sus caracteristicas, su equipo y su socket, y
        /// todo eso se lee de GameState, que es de la conexion que se esta atendiendo. Por eso
        /// cada mitad se construye dentro de SessionContext.Push del suyo: leer las del otro desde
        /// aqui daria dos veces las mismas, que es un combate contra un espejo.
        ///
        /// El de la izquierda va al equipo azul y el retado al rojo, que es lo unico que hace de
        /// esto un duelo y no dos aliados sin nadie enfrente.
        ///
        /// La fase de preparacion no se toca: el motor ya arranca por «todos listos» y no por
        /// temporizador, asi que un duelo empieza cuando los dos pulsan el boton.
        /// </remarks>
        public static Task<bool> InitiateDuelAsync(GameSession challenger, GameSession target,
                                                   long mapId, int duelId = 0)
            => InitiatePvpAsync(new List<GameSession> { challenger },
                                new List<GameSession> { target }, mapId, duelId);

        /// <summary>
        /// Monta un combate de PERSONAS contra personas: uno contra uno o tres contra tres.
        /// </summary>
        /// <remarks>
        /// Un desafio es el caso de uno contra uno y el koliseo es el mismo combate con mas gente
        /// a cada lado, asi que es una sola funcion. Lo que la hace distinta de la de monstruos no
        /// es el numero: es que hay VARIAS SESIONES, cada una con sus caracteristicas, su equipo y
        /// su socket. Todo eso se lee de GameState, que es de la conexion que se esta atendiendo,
        /// asi que cada luchador se construye dentro de SessionContext.Push del suyo. Leerlos
        /// todos desde aqui daria el mismo personaje repetido.
        /// </remarks>
        public static async Task<bool> InitiatePvpAsync(IReadOnlyList<GameSession> blue,
                                                        IReadOnlyList<GameSession> red,
                                                        long mapId, int pvpId = 0,
                                                        bool koliseo = false, int koliseoMode = -1,
                                                        IReadOnlyList<Fighter>? blueBots = null,
                                                        IReadOnlyList<Fighter>? redBots = null)
        {
            // Koliseo JondoBots fill sides with no session behind them; somebody real still fights.
            blueBots ??= Array.Empty<Fighter>();
            redBots ??= Array.Empty<Fighter>();
            if (blue.Count + blueBots.Count == 0 || red.Count + redBots.Count == 0) return false;
            if (blue.Count + red.Count == 0) return false;
            foreach (var sesion in blue) if (sesion.State.CharacterId == 0) return false;
            foreach (var sesion in red) if (sesion.State.CharacterId == 0) return false;

            // El combate se identifica con el id del DESAFIO, que es lo que hace la captura: el
            // 494 del hqc reaparece en el kam, en los dos kae y en el ilh.
            long fightId = pvpId != 0
                ? pvpId
                : System.Threading.Interlocked.Increment(ref _nextFightId);
            // El koliseo tiene sus propias arenas y no se pelea en la que le tocaria al mapa de
            // rol. Se elige una al azar de las que tengan sitio para este tamano de equipo: las de
            // duelo son pequenas -37 de 85 con una sola casilla por bando- y meter ahi un tres
            // contra tres no cabe. Sin el fichero de arenas, al de siempre.
            var arenaKoliseo = koliseo ? Managers.KoliseoMaps.PickFor(blue.Count + blueBots.Count) : null;
            long arenaMapId = arenaKoliseo?.MapId ?? MapManager.ResolveArenaMapId(mapId);

            var fight = new FightInstance(fightId, mapId, arenaMapId)
            {
                Reglas = koliseo ? FightRules.Koliseo : FightRules.Desafio,
                KoliseoMode = koliseo ? koliseoMode : -1,
            };

            if (arenaKoliseo != null)
            {
                // Las de koliseo traen las suyas de verdad, marcadas en el propio cliente y
                // comprobadas contra el kba del servidor real. Nada de partir una lista por la
                // mitad.
                fight.SetPlacementCells(arenaKoliseo.Blue, arenaKoliseo.Red);
                Program.LogDebug($"[Koliseo] Arena {arenaKoliseo.MapId} «{arenaKoliseo.Name}», " +
                                 $"{arenaKoliseo.Capacity} por bando.");
            }
            else
            {
                // Las mismas casillas que un combate corriente, y por la misma razon: el filtro de
                // siembra deja dos de las setenta y siete de un arena y los dos acabarian encima.
                var enCombate = MapManager.GetFightWalkable(arenaMapId);
                fight.GeneratePlacementCells(enCombate != null && enCombate.Count > 0
                    ? new List<int>(enCombate)
                    : MobSpawnManager.GetInnerWalkableCells(arenaMapId));
            }

            // Contra quien se pelea, que es lo que nombra el kmu de la entrada. En un desafio es
            // una persona y no un grupo de bichos.
            fight.DefenderLeaderId = red.Count > 0 ? red[0].State.CharacterId : redBots[0].Id;

            var todos = new List<(GameSession Sesion, bool Azul)>();
            foreach (var sesion in blue) todos.Add((sesion, true));
            foreach (var sesion in red) todos.Add((sesion, false));

            foreach (var (sesion, retador) in todos)
            {
                using (SessionContext.Push(sesion))
                {
                    // Si a alguno le quedaba un combate colgado, se le quita el suyo y solo el suyo.
                    long yo = GameState.CharacterId;
                    foreach (var par in _activeFights)
                    {
                        if (par.Value.EquipoDe(yo) >= 0)
                        {
                            _activeFights.TryRemove(par.Key, out _);
                        }
                    }

                    // Dónde está de pie en el mapa de rol, ANTES de mandarlo al arena. Es a donde
                    // se le devuelve al acabar, y tiene que leerse ahora: dos líneas más abajo
                    // GameState.CellId ya es la casilla del arena, y devolver a alguien a una
                    // casilla del arena lo deja en un sitio que en el mapa de rol no existe.
                    //
                    // Y EL MAPA TAMBIÉN ES EL SUYO. En un desafío los dos están en el mismo, así
                    // que daba igual leerlo del combate; en un koliseo no se conocen de nada y cada
                    // uno viene del suyo. Con el del combate, el rival volvía al mapa del primero
                    // del equipo azul: uno estaba en Frigost, el otro en Astrub, y los dos
                    // acababan en Frigost.
                    int casillaDeRol = GameState.CellId;
                    long mapaDeRol = GameState.MapId;

                    var luchador = BuildPlayerFighter(fight);
                    if (retador) fight.AddPlayer(luchador);
                    else fight.AddOpponent(luchador);

                    // Todo esto lo hacía el camino de los monstruos y aquí faltaba entero, y sin
                    // ello la sesión se queda creyendo que sigue en el mapa de rol mientras el
                    // cliente ya está en el arena: los demás manejadores le contestan con el mapa
                    // de antes, y al acabar el combate no hay a dónde devolverlo.
                    //
                    // La casilla es LA SUYA, no la del primero del equipo azul: el camino de los
                    // monstruos podía coger Azul[0] porque el equipo azul era una sola persona.
                    GameState.MapId = fight.MapId;
                    GameState.CellId = luchador.CellId;

                    var suyo = SessionContext.State;
                    suyo.FightId = fight.FightId;
                    suyo.RoleplayMapId = mapaDeRol;
                    suyo.RoleplayCellId = casillaDeRol;
                    fight.DeDondeVenian[suyo.CharacterId] = (mapaDeRol, casillaDeRol);

                    GameState.IsInFight = true;
                    GameState.CurrentFightMobId = 0;
                }
            }

            // The JondoBots, after the people: placed like them, ready from the start.
            foreach (var bot in blueBots) fight.AddPlayer(bot);
            foreach (var bot in redBots) fight.AddOpponent(bot);

            _activeFights[fightId] = fight;

            // Y la entrada, a cada uno por su socket y desde su contexto: el primer frame nombra a
            // quien se va del mapa, asi que mandarlos los dos desde aqui sacaria dos veces al mismo.
            foreach (var (sesion, _) in todos)
            {
                using (SessionContext.Push(sesion))
                {
                    await SendFightEntryAsync(sesion.Stream, fight);
                }
            }

            // Both sides with their person before the board, as both views of the challenge
            // show (frames 30-31 of «aceptar desafio», 31-32 of «enviar desafio»), and the swords
            // for the map. The koliseo stands on no map and gets only the first.
            {
                var primero = todos[0].Sesion;
                int suCasilla = fight.DeDondeVenian.TryGetValue(primero.State.CharacterId, out var origen)
                    ? origen.Casilla
                    : 0;
                using (SessionContext.Push(primero))
                {
                    await AfterFightOpenedAsync(fight, null, suCasilla);
                }
            }

            // Y el reloj, sólo en el koliseo: allí la captura manda el f5 del kaa y aquí tiene que
            // vencer lo mismo que el cliente enseña. Cada uno el suyo, porque cada uno tiene su
            // socket y su combate en curso.
            if (koliseo)
            {
                foreach (var (sesion, _) in todos)
                {
                    using (SessionContext.Push(sesion))
                    {
                        StartPlacementCountdown(sesion.Stream, fight,
                            fight.Reglas.RelojDeColocacion * 100);
                    }
                }
            }

            Console.WriteLine($"[{(koliseo ? "Koliseo" : "PvP")}] Combate #{fightId} en el mapa " +
                              $"{arenaMapId}: {blue.Count + blueBots.Count} contra {red.Count + redBots.Count}" +
                              (blueBots.Count + redBots.Count > 0 ? $", {blueBots.Count + redBots.Count} JondoBot(s)." : "."));
            return true;
        }

        /// <summary>
        /// Como se anuncia a una PERSONA en la preparacion, sea de que lado sea.
        /// </summary>
        /// <remarks>
        /// El bucle del equipo cero leia la ficha del jugador que esta mirando, lo cual funciona
        /// cuando el unico jugador del combate es el. Con dos personas hay que leer la ficha de
        /// CADA UNA: el aspecto y el nombre salen de su fila, no de la de quien mira.
        /// </remarks>
        private static byte[] BuildPlayerAppearance(FightInstance fight, Fighter fighter)
        {
            var (look, breed, sex) = CharacterLookOf(fighter);
            return Network.FightProtocol.BuildFighter(
                fighter.CellId, FacingOf(fight, fighter), fighter.Id, PlacementSheetOf(fighter), look,
                Network.FightProtocol.PlayerIdentity(breed, fighter.Name, sex, fighter.Level),
                isMonster: false);
        }

        /// <summary>
        /// A character fighter's look, class and sex: from his row, or -- a Koliseo JondoBot has
        /// none -- from the fighter itself.
        /// </summary>
        private static (byte[] Look, int Breed, int Sex) CharacterLookOf(Fighter fighter)
        {
            if (fighter.IsBot) return (fighter.BotLook ?? Array.Empty<byte>(), fighter.Breed, fighter.Sex);
            var ficha = DatabaseManager.GetCharacterById(fighter.Id);
            byte[] look = ficha != null
                ? Managers.BreedLookTable.BuildLook(ficha.Breed, ficha.Sex, ficha.HeadId, null, ficha.Id)
                : Array.Empty<byte>();
            return (look, ficha?.Breed ?? 0, ficha?.Sex ?? 0);
        }

        /// <summary>La ficha completa de un combatiente para el jxb, sea persona o bicho.</summary>
        /// <remarks>
        /// El gemelo de <see cref="BuildPlayerAppearance"/> para el arranque del combate: aquel
        /// hace el jxg de la colocación y éste el bloque del jxb, que es el mismo contenido con la
        /// ficha llena. Los dos leen la ficha DEL COMBATIENTE y no la de quien mira, que era el
        /// fallo.
        /// </remarks>
        private static Network.Pb BloqueDe(FightInstance fight, Fighter fighter)
        {
            if (fighter.IsMonster)
            {
                return Network.FightProtocol.FighterBlock(
                    fighter.CellId, FacingOf(fight, fighter), fighter.Id, FullSheetOf(fighter),
                    MonsterLook(fighter),
                    Network.FightProtocol.MonsterIdentity(fighter.GradeIndex + 1, fighter.MonsterId,
                                                          fighter.Level),
                    isMonster: true);
            }

            var (look, breed, sex) = CharacterLookOf(fighter);
            return Network.FightProtocol.FighterBlock(
                fighter.CellId, FacingOf(fight, fighter), fighter.Id, FullSheetOf(fighter), look,
                Network.FightProtocol.PlayerIdentity(breed, fighter.Name, sex, fighter.Level),
                isMonster: false);
        }

        /// <summary>
        /// El luchador de QUIEN esta hablando ahora mismo, leido de su propia sesion.
        /// </summary>
        /// <remarks>
        /// Estaba dentro de InitiateFightFromMobCollision porque solo habia un jugador por combate.
        /// En un desafio hay dos, cada uno con sus caracteristicas y su equipo, y la unica manera
        /// de leer las del otro es construir el suyo DENTRO de su contexto de sesion: todo esto
        /// sale de GameState, que es de la conexion que esta atendiendose.
        ///
        /// La casilla no se pone aqui. La decide quien lo mete en un equipo, que es lo que sabe si
        /// va al lado azul o al rojo.
        /// </remarks>
        public static Fighter BuildPlayerFighter(FightInstance fight)
        {
            int fuerza = GameState.TotalStrength + StatsHandler.GetEquipBonus(10);
            int inteligencia = GameState.TotalIntelligence + StatsHandler.GetEquipBonus(15);
            int suerte = GameState.TotalChance + StatsHandler.GetEquipBonus(13);
            int agilidad = GameState.TotalAgility + StatsHandler.GetEquipBonus(14);

            // Build Player Fighter from GameState (Fighter ID = player CharacterId)
            var playerFighter = new Fighter
            {
                Id = GameState.CharacterId,
                Name = GameState.CharacterName,
                // The class the masks' B and b ask about.
                Breed = GameState.Breed,
                TeamId = 0,
                                Level = GameState.CharacterLevel > 0 ? GameState.CharacterLevel : 40,
                // Same source as the jxx we send to the client. There used to be a custom formula
                // here that only looked at BASE vitality: the server believed the character had
                // 305 HP while the client displayed 514, because equipped items (the Emerald
                // Dofus gives +200) were only added on one side. The result: the character died
                // "in the background" after 8 turns with a full health bar on screen.
                MaxHP = StatsHandler.GetPlayerMaxHp(),
                // Y por lo mismo, los puntos: base más equipo, del mismo sitio del que sale la
                // ficha que el jugador ve. Estaban a 6 y a 3 escritos aquí, así que un personaje
                // con +4 PA y +2 PM de equipo veía 10 y 5 en pantalla y luego peleaba con 6 y 3.
                MaxAP = StatsHandler.GetPlayerMaxAp(),
                MaxMP = StatsHandler.GetPlayerMaxMp(),
                // La misma iniciativa que enseña la ficha, y del mismo sitio.
                //
                // Sumaba sólo los puntos INVERTIDOS y el bonus de la característica 44, así que las
                // elementales que da el equipo no entraban. Y son casi todas: en el personaje de
                // pruebas el equipo pone +730 de fuerza y +760 de agilidad, y ninguno de los dos
                // contaba. Enfrente, un monstruo SÍ suma sus cinco características de la base, así
                // que Pioch el Arenil sacaba más iniciativa que un nivel 200 y jugaba primero.
                // Medido en el registro: combate #1002, nueve combatientes, «primero -8», que es
                // justo el último del carrusel.
                //
                // El comentario que había aquí decía exactamente esto —que ignorar los objetos
                // hacía que el pío jugara primero— y arreglaba sólo la mitad: metió el bonus de
                // iniciativa y se dejó las cuatro elementales.
                Initiative = StatsHandler.GetPlayerInitiative(),
                Strength = fuerza,
                Intelligence = inteligencia,
                Chance = suerte,
                Agility = agilidad,
                // Power from the gear (characteristic 25). It feeds straight into damage.
                Power = StatsHandler.GetEquipBonus(25),
                // Critical hit from the gear (characteristic 18): the Turquoise Dofus gives +10.
                CriticalBonus = StatsHandler.GetEquipBonus(18),
                // Los daños que se suman al final, sin multiplicar: los generales, los críticos y
                // los de cada elemento.
                FlatDamage = StatsHandler.GetEquipBonus(16),
                CriticalDamage = StatsHandler.GetEquipBonus(86),
                EarthDamage = StatsHandler.GetEquipBonus(88),
                FireDamage = StatsHandler.GetEquipBonus(89),
                WaterDamage = StatsHandler.GetEquipBonus(90),
                AirDamage = StatsHandler.GetEquipBonus(91),
                NeutralDamage = StatsHandler.GetEquipBonus(92),
                // Las resistencias porcentuales por elemento (características 33 a 37). Faltaban:
                // estos cinco campos del Fighter sólo los tocaba el código de los monstruos y el
                // de los invocados, así que en el jugador se quedaban a cero y el panel de combate
                // enseñaba 0% en todo. Y no era sólo cosmético: el mismo cero llegaba al cálculo
                // de daño, de modo que el personaje encajaba los golpes sin resistencia ninguna.
                EarthResPct = StatsHandler.GetEquipBonus(33),
                FireResPct = StatsHandler.GetEquipBonus(34),
                WaterResPct = StatsHandler.GetEquipBonus(35),
                AirResPct = StatsHandler.GetEquipBonus(36),
                NeutralResPct = StatsHandler.GetEquipBonus(37),
                // Empuje (84 en el equipo, que el cliente pinta en la 85) y alcance (19).
                PushDamage = StatsHandler.GetEquipBonus(84),
                Vitality = GameState.TotalVitality + StatsHandler.GetEquipBonus(11),
                Range = StatsHandler.GetEquipBonus(19),
                LookBoneId = 744,
                IsMonster = false
            };
            // The life he has, not the whole of it: what a defeat left missing comes back with
            // the regeneration, and the fight takes what is there (FightDefeat.cs).
            playerFighter.CurrentHP = Managers.RestingLife.LifeAt(GameState.CharacterId, playerFighter.MaxHP,
                                                                   DateTime.UtcNow);
            playerFighter.CurrentAP = playerFighter.MaxAP;
            playerFighter.CurrentMP = playerFighter.MaxMP;
            RellenarLaFicha(playerFighter);

            // In a dream's room, the dream's bonuses: they are the dream's and they apply to its
            // fights, not to the character outside them.
            var dream = Managers.Dreams.De(GameState.CharacterId);
            if (dream != null && dream.SalaActual?.MapaDeLaSala == fight.RoleplayMapId)
            {
                var applied = Managers.Dreams.ApplyTo(playerFighter, dream);
                if (applied.Count > 0)
                    Program.LogDebug($"[Sueños] Bonuses in the fight: {string.Join(", ", applied)}.");
            }

            // Las actitudes que le dan sus objetos: los seis dofus y los trofeos regalan cada uno
            // un "hechizo" por su efecto 1175, y ésos son los que hacen cosas al empezar el turno o
            // al recibir un golpe. De ahí sale, sin escribir nada suyo, el punto de acción del
            // Dofus Ocre.
            playerFighter.Buffs.Vaciar();

            // And the class's own, in the order the real server casts them before the first
            // turn: the initial spells of his choices, the items' attitudes, the class passive
            // last. See ClassPassives for what is measured and what is read off the names.
            var propios = Managers.ClassPassives.ForFight(
                GameState.Breed,
                Managers.FightSpellLayout.Current(GameState.Breed, GameState.CharacterLevel,
                                                  SessionContext.Current.AccountId).Spells);
            int pasivo = Managers.ClassPassives.PassiveOf(GameState.Breed);
            foreach (var (hechizo, grado) in propios)
            {
                if (hechizo != pasivo) playerFighter.Buffs.PonerActitud(hechizo, grado);
            }
            foreach (int hechizo in Managers.SpellEffects.ActitudesDelEquipo(GameState.CharacterId))
            {
                playerFighter.Buffs.PonerActitud(hechizo);
            }
            if (pasivo != 0) playerFighter.Buffs.PonerActitud(pasivo);

            if (playerFighter.Buffs.Actitudes.Count > 0)
            {
                Program.LogDebug($"[Combate] Actitudes del equipo: " +
                                 string.Join(", ", playerFighter.Buffs.Actitudes));
            }

            return playerFighter;
        }

        /// <summary>
        /// La entrada al combate de UN jugador: sale del mapa de rol y carga el de la arena.
        /// </summary>
        /// <remarks>
        /// Estaba dentro de InitiateFightFromMobCollision, escrita para el unico que peleaba. En un
        /// desafio pelean dos, cada uno en su socket y con su estado, asi que hay que poder
        /// mandarla dos veces -- y mandarla desde el contexto de cada uno, porque el primer frame
        /// nombra a QUIEN se va del mapa.
        ///
        /// El orden no es de adorno. El kmp con f1 a uno va ANTES de mandar cargar el mapa: es lo
        /// que hace que el cliente pida el combate con un ijm en vez de pedir un mapa corriente
        /// con un jrh. Sin el carga el tablero y se queda en modo mapa normal.
        /// </remarks>
        /// <param name="joining">
        /// For somebody coming into a fight that is already there: no jsd and no kmu in front.
        /// Measured in the two joins that were recorded from the joiner's side -- «meterse en
        /// combate de otra persona» frames 1-7 and the follow capture 143-151 -- whose entry
        /// starts straight at the kml.
        /// </param>
        public static async Task SendFightEntryAsync(NetworkStream stream, FightInstance fight,
                                                     bool joining = false)
        {
            if (!joining)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.BuildActorLeft(GameState.CharacterId));

                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmu,
                    Network.FightProtocol.BuildFightAgainst(fight.DefenderLeaderId)));
            }
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kml));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmp,
                Network.FightProtocol.BuildFightMapComing()));

            await WriteFrameAsync(stream, ConnectionProtocol.BuildLoadMap(fight.MapId));
            await WriteFrameAsync(stream, ConnectionProtocol.BuildMapClock());

            // Regeneration ends. The real server puts the kuq right here, between the lqu and
            // the lva of the tactical map, 83 times out of 97, and it is what stops the counter
            // the world entry started: without it this client kept adding one point every half
            // second to its own bar all through the fight, which no 97 could hold down for
            // long. Life is the fighter's, or the sheet's when the fight has not built him yet.
            await WriteFrameAsync(stream, ConnectionProtocol.BuildRegenerationEnded(
                LifeAtFightEntry(fight),
                ConnectionProtocol.RegenerationTicksSince(Network.SessionContext.State.RegenerationStartedUtc,
                                                          DateTime.UtcNow),
                MaxLifeAtFightEntry(fight)));

            await WriteFrameAsync(stream, ConnectionProtocol.BuildMapDiscovered(fight.MapId));

            Program.LogDebug($"[Combate] Combate #{fight.FightId} en el mapa {fight.MapId}. " +
                             "Esperando a que el cliente pida los actores.");
        }

        /// <summary>The life the kuq reports for whoever is entering: his fighter's, else full.</summary>
        private static int LifeAtFightEntry(FightInstance fight)
            => fight.Buscar(GameState.CharacterId)?.CurrentHP ?? StatsHandler.GetPlayerMaxHp();

        private static int MaxLifeAtFightEntry(FightInstance fight)
            => fight.Buscar(GameState.CharacterId)?.MaxHP ?? StatsHandler.GetPlayerMaxHp();

        public static async Task SendPreparationAsync(NetworkStream stream, FightInstance fight)
        {
            fight.MarkPrepared(GameState.CharacterId);

            // Antes que nada, dónde está cada uno. En la captura salen ocho kmk seguidos —uno por
            // combatiente— nada más cargar el mapa y ANTES de que se anuncie el combate. Son los
            // que ponen a la gente sobre el tablero; sin ellos el cliente tiene un combate con
            // combatientes que no están en ninguna casilla.
            foreach (var fighter in fight.Azul)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmk,
                    Network.FightProtocol.BuildFighterPlaced(fighter.CellId, FacingOf(fight, fighter), fighter.Id)));
            }
            foreach (var fighter in fight.Rojo)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmk,
                    Network.FightProtocol.BuildFighterPlaced(fighter.CellId, FacingOf(fight, fighter), fighter.Id)));
            }

            // Cuántos retos se eligen en este combate. Va aquí, detrás de los kmk y delante del
            // primer jxg, que es donde lo pone el servidor real; y va DOS VECES, con el mismo
            // número, porque el original lo repite detrás de las casillas. El kwk vacío sólo
            // acompaña al primero.
            // Los retos son cosa de pelear contra monstruos: dan un extra sobre su botín. En un
            // desafío entre jugadores no hay botín que multiplicar, y la captura no trae ni uno.
            if (fight.Reglas.HayRetos) await ChallengeHandler.SendCountAsync(stream, fight, primeraVez: true);

            // Lo primero, decirle que AQUÍ HAY UN COMBATE. Sin el kam el cliente no tiene ningún
            // combate al que agarrar lo que viene detrás, y se le ve reventar en su propio registro
            // al llegarle el jwq, recorriendo una lista de combatientes que no existe. Con el mapa
            // táctico ya cargado y nada dibujado encima, que es justo lo que pasaba.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ijq,
                Network.FightProtocol.BuildMapReady()));

            // Un desafío se anuncia distinto, y esto está medido en «enviar desafio y el otro
            // acepta»: su kam llega «f3=retado f5=id f6=retador», SIN tipo y sin lista de
            // monstruos, porque enfrente no hay ninguno.
            var monsters = fight.Reglas.EnfrenteHayMonstruos
                ? fight.Rojo.ConvertAll(f => (long)f.MonsterId)
                : new List<long>();
            // The f6 is who OPENED the fight, whoever receives it: the follower of the follow
            // capture gets Harmoo there (frame 162), the joiner of the sword capture Uvok (17),
            // and in the challenge both views name the challenger. It was the receiver, which was
            // right only for the one who attacked.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kam,
                Network.FightProtocol.BuildFightAnnounced(
                    fight.Reglas.TipoDelKam,
                    fight.DefenderLeaderId, monsters,
                    fight.FightId, fight.ChallengerLeaderId)));

            // Y su kaa son seis bytes sin cuenta atrás. En un desafío no hay reloj de colocación:
            // no es que se esconda, es que el servidor real no manda ninguno -- el combate empieza
            // cuando los dos pulsan listo y no cuando se acaba un tiempo.
            // El koliseo sí tiene reloj: su kaa de la captura trae el f5=592. El desafío no.
            //
            // And the countdown is what is LEFT of it: 442 for the follower 0.7 s in, 403 for the
            // fourth player of the dungeon capture 4.6 s in. Whoever comes in late must not be
            // shown a full placement the server will not wait for.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kaa,
                fight.Reglas.KaaConCuentaAtras
                    ? Network.FightProtocol.BuildFightSummary(fight.Reglas.TipoDelKam,
                          Math.Max(1, fight.PlacementDecisecondsLeft(fight.Reglas.RelojDeColocacion,
                                                                     DateTime.UtcNow)))
                    : Network.FightProtocol.BuildDuelSummary()));

            // Cada uno con SU aspecto, leído de su propia ficha.
            //
            // Aquí había otro fallo del mismo tipo que el de la preparación compartida, y sólo se
            // ve con dos personas del mismo lado o mirando desde el otro: el aspecto se construía
            // con «character», que es la ficha de QUIEN RECIBE las tramas, y se le ponía a todos
            // los del equipo azul. Contra monstruos daba igual —en el azul sólo estaba quien
            // miraba— pero en un desafío, desde el cliente del retado, al retador se le pintaba con
            // la raza, el sexo y la cabeza del retado. En un koliseo de tres contra tres serían los
            // tres compañeros clonados.
            foreach (var fighter in fight.Azul)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxg,
                    fighter.IsMonster
                        ? Network.FightProtocol.BuildFighter(
                              fighter.CellId, FacingOf(fight, fighter), fighter.Id,
                              PlacementSheetOf(fighter), MonsterLook(fighter),
                              Network.FightProtocol.MonsterIdentity(fighter.GradeIndex + 1,
                                                                    fighter.MonsterId, fighter.Level),
                              isMonster: true)
                        : BuildPlayerAppearance(fight, fighter)));
            }

            foreach (var fighter in fight.Rojo)
            {
                // Enfrente puede haber una persona. Anunciarla como bicho es lo que hacia que el
                // cliente pintase al rival con un «???» y sin barra de nada: le llegaba una
                // identidad de monstruo con el id cero, que no existe en su tabla.
                if (!fighter.IsMonster)
                {
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxg,
                        BuildPlayerAppearance(fight, fighter)));
                    continue;
                }

                // Con su propio identificador negativo, no todos con el mismo: contra cuatro
                // poutchs el servidor real reparte -1, -2, -3 y -4.
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxg,
                    Network.FightProtocol.BuildFighter(
                        fighter.CellId, FacingOf(fight, fighter), fighter.Id, PlacementSheetOf(fighter),
                        MonsterLook(fighter),
                        Network.FightProtocol.MonsterIdentity(fighter.GradeIndex + 1,
                                                              fighter.MonsterId, fighter.Level),
                        isMonster: true)));
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kba,
                Network.FightProtocol.BuildPlacementCells(
                    fight.BluePlacementCells.ConvertAll(c => (long)c),
                    fight.RedPlacementCells.ConvertAll(c => (long)c))));

            // Y otra vez cuántos retos, con el mismo número. No es un descuido de la captura: el
            // servidor real lo manda dos veces, aquí y antes del kaa, en las siete capturas donde
            // hay retos.
            if (fight.Reglas.HayRetos) await ChallengeHandler.SendCountAsync(stream, fight);

            // Detrás de las casillas, que es donde los pone la captura: quién está metido en el
            // combate y las cuatro opciones.
            //
            // ONE kae, for the receiver's own side, with its leader and no members: every board of
            // the captures has exactly that -- the follow capture's 168, the sword capture's 23
            // (the red side, f6 = 1), the dungeon one's 184, both views of the challenge and the
            // koliseo. The member lists travel in the kae BEFORE the board (AfterFightOpenedAsync
            // and the join), which is what the two per-person kae here were standing in for: with
            // a party on one side they named a second leader for it.
            int miBando = fight.EquipoDe(GameState.CharacterId);
            if (miBando >= 0)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kae,
                    FightJoinProtocol.BuildTeamUpdate(TeamOnMap(fight, miBando, withMembers: false),
                                                      fight.FightId)));
            }

            // Each side's options with their state, the attackers' and, when the other side is
            // people, theirs too: both challenge captures show the kau with f1 = 1 (frames 29, 43).
            foreach (int side in fight.Reglas.EnfrenteHayMonstruos
                         ? new[] { FightInstance.Azules } : new[] { FightInstance.Azules, FightInstance.Rojos })
            {
                foreach (int option in Network.FightProtocol.FightOptions)
                {
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kau,
                        Network.FightProtocol.BuildFightOption(side, option, fight.OptionOn(side, option), fight.FightId)));
                }
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzu,
                Network.FightProtocol.BuildTeams(CarouselOrder(fight))));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwq,
                Network.FightProtocol.BuildPlacementDone()));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jrk,
                Network.FightProtocol.BuildFightMap(fight.MapId)));

            // La lista de retos NO va aquí. El cliente manda sus ajustes del panel (kwo) nada más
            // recibir el jrk, y en las doce apariciones reales la lista llega SIEMPRE detrás de
            // ese kwo —incluidas las dos que el servidor manda sin que nadie las pida—. Se dispara
            // desde ChallengeHandler.SettingsAsync.

            Program.LogDebug($"[Combate] Preparación del combate #{fight.FightId}: " +
                             $"{fight.Azul.Count} contra {fight.Rojo.Count}, " +
                             $"{fight.BluePlacementCells.Count} casillas azules y " +
                             $"{fight.RedPlacementCells.Count} rojas.");
        }

        /// <summary>
        /// Hacia dónde mira uno: al enemigo que tenga enfrente.
        ///
        /// En la captura el jugador sale con orientación 5 y el poutch con 1, que es justo mirarse
        /// el uno al otro en las casillas que ocupaban; no son constantes. Se calcula del centro del
        /// bando contrario, así que cambia según el cuadrante en el que a uno le toque colocarse.
        ///
        /// La retícula está en diagonal —cada fila baja media casilla y las filas impares van
        /// desplazadas— así que primero se pasa la casilla a coordenadas de rombo y luego se mira
        /// el signo de la diferencia.
        ///
        /// La tabla que había estaba GIRADA DOS PASOS y por eso el personaje se quedaba mirando
        /// a un lado en vez de al bicho. La buena sale de dos sitios que coinciden:
        ///
        ///   - De la geometría. En el rombo, x = (fila - fila%2)/2 + columna e
        ///     y = (fila + fila%2)/2 - columna. Bajar y con la fila quieta es subir la columna, o
        ///     sea ir a la DERECHA de la pantalla; subir x con la columna quieta es bajar de fila,
        ///     o sea ir hacia ABAJO. Y WorldMoveHandler.FacingFor dice que derecha es 0, abajo 2,
        ///     izquierda 4 y arriba 6. Luego -y = 0, +x = 2, +y = 4, -x = 6.
        ///
        ///   - De las capturas. El jugador sale mirando 5 desde la 285 y desde la 271 con el
        ///     poutch en la 270, y 3 desde la 284 con los cuatro poutchs abajo a la derecha. Los
        ///     tres salen con esta tabla y ninguno con la de antes.
        /// </summary>
        private static int FacingFrom(int cell, IEnumerable<Fighter> enemies)
        {
            int count = 0, sumX = 0, sumY = 0;
            foreach (var enemy in enemies)
            {
                var (ex, ey) = Diamond(enemy.CellId);
                sumX += ex; sumY += ey; count++;
            }
            if (count == 0) return MonsterFacing;

            var (x, y) = Diamond(cell);
            int dx = (sumX / count) - x;
            int dy = (sumY / count) - y;

            // Las ocho direcciones, por el signo de cada eje.
            if (dx > 0 && dy == 0) return 2;   // abajo
            if (dx > 0 && dy > 0) return 3;    // abajo y a la izquierda
            if (dx == 0 && dy > 0) return 4;   // izquierda
            if (dx < 0 && dy > 0) return 5;    // arriba y a la izquierda
            if (dx < 0 && dy == 0) return 6;   // arriba
            if (dx < 0 && dy < 0) return 7;    // arriba y a la derecha
            if (dx == 0 && dy < 0) return 0;   // derecha
            if (dx > 0 && dy < 0) return 1;    // abajo y a la derecha
            return MonsterFacing;
        }

        /// <summary>La casilla en coordenadas de rombo, que es como está puesto el tablero.</summary>
        private static (int X, int Y) Diamond(int cell)
        {
            int row = cell / 14;
            int col = cell % 14;
            int x = (row - (row % 2)) / 2 + col;
            int y = (row + (row % 2)) / 2 - col;
            return (x, y);
        }

        /// <summary>Mirando al bando contrario, que es lo que hace el servidor real.</summary>
        private static int FacingOf(FightInstance fight, Fighter fighter)
            => FacingFrom(fighter.CellId, fighter.TeamId == 0 ? fight.Rojo : fight.Azul);

        /// <summary>Cuando no hay a quién mirar, la que llevan los monstruos en la captura.</summary>
        private const int MonsterFacing = 1;

        /// <summary>
        /// La ficha que viaja durante la colocación.
        ///
        /// En la captura casi todas las características van con el hueco puesto y sin número
        /// dentro: los valores de verdad no llegan hasta que el combate empieza. Aquí se manda lo
        /// mismo, con los puntos de acción y de movimiento, que son los únicos que el cliente pinta
        /// en esta fase.
        /// </summary>
        private static List<(int Characteristic, long Base, long Gear)> PlacementSheetOf(Fighter fighter)
            => FullSheetOf(fighter);

        /// <summary>
        /// La ficha de un combatiente, para que la guardia de regresión pueda compararla con la
        /// del servidor real. No la usa el juego.
        /// </summary>
        public static List<(int Characteristic, long Base, long Gear)> FichaParaLaGuardia(Fighter fighter)
            => FullSheetOf(fighter);

        /// <summary>"Daños sufridos x#1%", el multiplicador que pone Represalias.</summary>
        private const int DanoSufridoPorCiento = 1163;

        /// <summary>Multiplier on the final damage dealt. Its base is 100.</summary>
        private const int DanoFinalInfligidoCaracteristica = 107;

        // Las características que entran en la fórmula de daño, con los números del catálogo.
        private const int PotenciaCaracteristica = 25;
        private const int CriticoCaracteristica = 18;
        private const int DanoFijoCaracteristica = 16;
        private const int DanoCriticoCaracteristica = 86;

        /// <summary>
        /// El TOTAL de una característica: lo que ya trae el combatiente —base, pergaminos y
        /// equipo, que se calcularon al montarlo— más lo que le hayan puesto los hechizos mientras
        /// dura el combate.
        ///
        /// Los embrujos se caen solos al llegar su ronda, así que el bono se retira sin hacer nada
        /// más, y como viven en el combatiente del combate y no en el personaje, al salir a
        /// roleplay no queda nada pegado.
        /// </summary>
        /// <summary>
        /// Lo que vale una caracteristica AHORA MISMO, embrujos incluidos, para mandarla suelta.
        ///
        /// Los puntos van por su cuenta -son los que le quedan de jugar este turno- y el resto
        /// sale de lo que tiene la ficha mas lo que le hayan puesto encima, que es exactamente la
        /// misma cuenta que hace la ficha completa del principio del combate.
        /// </summary>
        /// <summary>Una característica lista para meterla en un jxw, con sus tres huecos.</summary>
        private static (int Characteristic, long Base, long Gear, long Buff) Refresco(
            Fighter ficha, int caracteristica, int ronda, bool enSuTurno = true)
        {
            var (suBase, suEquipo, suEmbrujo) = HuecosDeFicha(ficha, caracteristica, ronda, enSuTurno);
            return (caracteristica, suBase, suEquipo, suEmbrujo);
        }

        private static (long Base, long Equipo, long Embrujo) HuecosDeFicha(Fighter ficha,
                                                                            int caracteristica,
                                                                            int ronda,
                                                                            bool enSuTurno = true)
        {
            // Los puntos son lo que le queda de jugar este turno, y van en su molde propio. And
            // outside his turn, what his next one starts with: after a removal on a monster
            // that is not playing, the real server's sheet reads its maximum less the removal
            // ("f5{f1=23 f2{f2=2}}" for three MP and one lost), not what it had left.
            if (caracteristica == ActionPointsCharacteristic)
                return (enSuTurno ? ficha.CurrentAP : Math.Max(0, ficha.MaxAP + ficha.Buffs.De(ActionPointsCharacteristic, ronda)), 0, 0);
            if (caracteristica == MovementPointsCharacteristic)
                return (enSuTurno ? ficha.CurrentMP : Math.Max(0, ficha.MaxMP + ficha.Buffs.De(MovementPointsCharacteristic, ronda)), 0, 0);

            // The shield is the points the fighter holds, in the base hole: "f5{f1=96 f2{f2=350}}"
            // in the Patada capture, and 700 after the second Patada.
            if (caracteristica == Managers.EffectEngine.ShieldCharacteristic) return (ficha.PuntosDeEscudo, 0, 0);

            // AQUÍ ESTABA EL AGUJERO DE LA PREVISUALIZACIÓN.
            //
            // Esto era una lista escrita A MANO —primero dos casos, luego trece— en paralelo a la
            // ficha completa que se manda al empezar el combate. Y el problema nunca fue cuáles
            // faltaban, sino que fuese una lista aparte: la ficha llena tiene cincuenta y tres
            // entradas y la copia siempre se quedaba corta. Lo que no estuviera en ella caía en
            // ficha.Otra(), que devuelve cero, y como el jxw manda el valor ABSOLUTO, el cero
            // pisaba en el cliente el número bueno que ya le había llegado.
            //
            // Lo que costaba, medido sobre nuestro propio registro de tráfico: el multiplicador de
            // daño 107 sale a 100 en la ficha inicial, y cincuenta y seis milisegundos después un
            // jxw lo pisaba con 10 —el efecto 1171 le suma diez y aquí la base salía cero—. El
            // cliente estima el golpe MULTIPLICANDO por él, así que la previsualización salía
            // dividida por diez. Y lo mismo con los otros diez multiplicadores y con los daños por
            // elemento 88 a 92, que la última tanda tampoco cubría.
            //
            // Ahora el valor se busca en LA MISMA ficha que se mandó, así que las dos no pueden
            // volver a separarse: lo que se añada allí queda cubierto aquí sin tocar nada.
            long baseDelPersonaje = 0, delEquipo = 0;
            foreach (var (cual, suBase, suEquipo) in FullSheetOf(ficha, conTraza: false))
            {
                if (cual != caracteristica) continue;
                baseDelPersonaje = suBase;
                delEquipo = suEquipo;
                break;
            }
            return (baseDelPersonaje, delEquipo, ficha.Buffs.De(caracteristica, ronda));
        }

        private static int ConBonos(Fighter quien, int caracteristica, int loQueYaTiene, int ronda)
        {
            if (caracteristica <= 0) return loQueYaTiene;
            return loQueYaTiene + quien.Buffs.De(caracteristica, ronda);
        }

        /// <summary>La característica que alimenta cada elemento en la fórmula de daño.</summary>
        private static int CaracteristicaDelElemento(Jondo.Unity.World.Fights.ElementType elemento)
            => elemento switch
            {
                Jondo.Unity.World.Fights.ElementType.Earth => 10,       // fuerza
                Jondo.Unity.World.Fights.ElementType.Fire => 15,        // inteligencia
                Jondo.Unity.World.Fights.ElementType.Water => 13,       // suerte
                Jondo.Unity.World.Fights.ElementType.Air => 14,         // agilidad
                _ => 10,                                                // el neutral va con fuerza
            };

        /// <summary>
        /// La tirada del crítico: un número del cero al noventa y nueve contra el porcentaje.
        /// </summary>
        /// <summary>
        /// Un numero de 0 a 100 con decimales, para las probabilidades de botin.
        ///
        /// Sale del mismo Random que todo lo demas y con el mismo candado. Los porcentajes de
        /// caida llevan decimales de verdad —la bolsa de limones del jefe piwi rojo cae al 3 %—
        /// asi que redondear a entero cambiaria lo que cae.
        /// </summary>
        private static double TirarPorcentaje()
        {
            lock (_dado) return _dado.NextDouble() * 100.0;
        }

        private static bool TirarCritico(int porciento)
        {
            if (porciento <= 0) return false;
            if (porciento >= 100) return true;
            lock (_dado) return _dado.Next(100) < porciento;
        }

        /// <summary>
        /// A monster as it fights: its grade's life, points, characteristics, resistances and
        /// dodges, standing on a cell. What a fight puts on the board -- and what a dream's
        /// bestiary shows before the fight, from this same method so the two never disagree.
        /// </summary>
        internal static Fighter BuildMonsterFighter(MobSpawnManager.MobMember member, long monFighterId, int monCellId)
        {
            int boneId = 1;
            string look = member.Monster?.Look ?? "";
            if (!string.IsNullOrEmpty(look))
            {
                string stripped = look.Trim('{', '}');
                string[] parts = stripped.Split('|');
                if (parts.Length > 0 && int.TryParse(parts[0], out int parsedBone))
                {
                    boneId = parsedBone;
                }
            }

            int monLevel = member.Level > 0 ? member.Level : 1;
            int monsterId = member.Monster?.Id ?? 0;
            int gradeIdx = member.GradeIndex;

            var dbStats = DatabaseManager.GetMonsterGradeStats(monsterId, gradeIdx);

            var monsterFighter = new Fighter
            {
                Id = monFighterId,
                Name = $"Monster_{monsterId}",
                TeamId = 1,
                CellId = monCellId,
                IsMonster = true,
                MonsterId = monsterId,
                GradeIndex = gradeIdx,
                Level = dbStats?.Level ?? monLevel,
                MaxHP = dbStats?.LifePoints ?? (40 + (monLevel * 8)),
                MaxAP = dbStats?.ActionPoints ?? 6,
                MaxMP = dbStats?.MovementPoints ?? 3,
                Initiative = dbStats != null ? (dbStats.Agility + dbStats.Strength + dbStats.Intelligence + dbStats.Chance + dbStats.Wisdom) : (50 + monLevel),
                Strength = dbStats?.Strength ?? (5 + monLevel),
                Intelligence = dbStats?.Intelligence ?? (5 + monLevel / 2),
                Chance = dbStats?.Chance ?? (5 + monLevel / 2),
                Agility = dbStats?.Agility ?? (5 + monLevel / 2),
                NeutralResPct = dbStats?.NeutralResistance ?? Math.Min(50, monLevel / 3),
                EarthResPct = dbStats?.EarthResistance ?? Math.Min(50, monLevel / 4),
                FireResPct = dbStats?.FireResistance ?? Math.Min(50, monLevel / 4),
                WaterResPct = dbStats?.WaterResistance ?? Math.Min(50, monLevel / 4),
                AirResPct = dbStats?.AirResistance ?? Math.Min(50, monLevel / 4),
                LookBoneId = boneId,
                SpellIds = dbStats?.SpellIds ?? new List<int>(),
                SpellGrades = dbStats?.SpellGrades ?? new Dictionary<int, int>(),
                // gradeXp from the monster template: the experience it awards on death, the
                // same figure the client shows when hovering over the group.
                XpReward = dbStats?.GradeXp ?? 0
            };
            monsterFighter.CurrentHP = monsterFighter.MaxHP;
            monsterFighter.Otras[Fighter.CaracteristicaDeErosion] = Fighter.ErosionBase;
            monsterFighter.CurrentAP = monsterFighter.MaxAP;
            monsterFighter.CurrentMP = monsterFighter.MaxMP;

            // What it dodges and what it removes: the grade's own dodge plus a tenth of its
            // wisdom for the one, a tenth of its wisdom for the other.
            int sabiduriaDelBicho = dbStats?.Wisdom ?? 0;
            monsterFighter.Otras[Managers.EffectEngine.EsquivaPA] = (dbStats?.PaDodge ?? 0) + sabiduriaDelBicho / 10;
            monsterFighter.Otras[Managers.EffectEngine.EsquivaPM] = (dbStats?.PmDodge ?? 0) + sabiduriaDelBicho / 10;
            monsterFighter.Otras[Managers.EffectEngine.RetiraPA] = sabiduriaDelBicho / 10;
            monsterFighter.Otras[Managers.EffectEngine.RetiraPM] = sabiduriaDelBicho / 10;

            // Escape and tackle: a tenth of its agility, and the grade's own bonus on top. That is
            // the sheet of every plain monster the captures fight -- 492 at agility 44, 48, 52 and
            // 56 carries 4, 4, 5 and 5 of each, the Dopeul's 3286 at 800 carries 80 -- and the
            // same tenth the client gives a player (Translations 1113698). Whether it tackles at
            // all is its template's.
            SetTackleSheet(monsterFighter, dbStats?.Agility ?? 0,
                           dbStats?.TackleEvadeBonus ?? 0, dbStats?.TackleBlockBonus ?? 0);
            monsterFighter.TemplateAllowsTackle = Managers.MonsterFlags.AllowsTackle(monsterId);

            // The spells that are born waiting: their grade's InitialCooldown, as a player's are.
            // 75 bosses' spells could be cast on the first turn that the game holds back.
            foreach (int hechizo in monsterFighter.SpellIds)
            {
                int suGrado = monsterFighter.SpellGrades.TryGetValue(hechizo, out int gg) ? gg : 1;
                int espera = LimitesDeGrado(hechizo, suGrado).EsperaInicial;
                if (espera > 0) monsterFighter.Recarga[hechizo] = espera;
            }

            // Its behaviour: the grade's startingSpellId, a SpellLevels.Id, held as an attitude the
            // way a summon holds its own spell -- cast when the fight starts, fired at every turn
            // start and turn end, every hit. It is where a boss keeps what makes him one: Conde
            // Kontatrás's Carrillón makes him invulnerable, drops the glyph that kills whoever
            // stands in line and alternates his odd and even turns. 157 of the 209 bosses have
            // one; none of them was ever cast.
            monsterFighter.Conducta = Managers.SpellCriteria.SpellOfLevel(dbStats?.StartingSpellLevelId ?? 0);

            return monsterFighter;
        }

        /// <summary>
        /// The cells a fight on this arena is placed from: the fight-walkable ones, or the inner
        /// walkable ones of the map when it has none.
        /// </summary>
        private static List<int> PlacementGround(long arenaMapId)
        {
            var enCombate = MapManager.GetFightWalkable(arenaMapId);
            return enCombate != null && enCombate.Count > 0
                ? new List<int>(enCombate)
                : MobSpawnManager.GetInnerWalkableCells(arenaMapId);
        }

        /// <summary>
        /// Where the monsters of a fight on this map will stand, in the order of the group: the
        /// red placement cells, computed the way a fight computes them.
        /// </summary>
        public static IReadOnlyList<int> DefenderPlacement(long mapId) => Placement(mapId).Defenders;

        /// <summary>Both teams' placement cells for a fight on this map, the way a fight computes them.</summary>
        public static (List<int> Attackers, List<int> Defenders) Placement(long mapId)
        {
            long arenaMapId = MapManager.ResolveArenaMapId(mapId);
            var fight = new FightInstance(0, mapId, arenaMapId);
            fight.GeneratePlacementCells(PlacementGround(arenaMapId));
            return (fight.BluePlacementCells.ToList(), fight.RedPlacementCells.ToList());
        }

        /// <summary>Los mismos números que usa datos/characteristics.json.</summary>
        private const int ActionPointsCharacteristic = 1;
        private const int VitalityCharacteristicId = 11;
        private const int MovementPointsCharacteristic = 23;

        /// <summary>El alcance a secas, la que suma a TODOS los hechizos.</summary>
        private const int AlcanceCaracteristica = 19;
        private const int LifeCharacteristic = 0;

        /// <summary>
        /// La ficha con los valores de verdad, que es la que va en el jxb.
        ///
        /// La de la colocación lleva treinta y seis características y ésta cincuenta y tres: se le
        /// añaden las elementales, los daños, el crítico, el alcance, la potencia y las
        /// resistencias, y se le quita la iniciativa, que sólo viaja durante la colocación.
        ///
        /// Aquí van las que el cliente pinta en el panel y en el carrusel. Las que no se sepan se
        /// mandan a cero, que es como viajan las de un monstruo que de verdad las tiene a cero.
        /// </summary>
        /// <summary>
        /// El resto de la ficha del personaje: lo que no interviene en ninguna cuenta del servidor
        /// pero el cliente pinta, y que iba TODO a cero.
        ///
        /// Dos de éstas se notan jugando: la huida y el placaje. Con 170 de agilidad hay que tener
        /// 17 de huida, que es de sobra para que un pío no te placa; mandando cero, el cliente
        /// creía que estabas placado y avisaba de que perderías puntos al moverte.
        ///
        /// Las bases son las de siempre —huida y placaje salen de la agilidad, las esquivas de la
        /// sabiduría, a razón de una décima parte— y lo del equipo lo pone GetEquipBonus, que desde
        /// que suma por característica y con signo ya sabe de todas éstas: la 752 suma huida y la
        /// 754 la resta, la 160 suma esquiva de PA y la 162 la resta, y así.
        /// </summary>
        private static void RellenarLaFicha(Fighter quien)
        {
            void Poner(int caracteristica, int baseDelPersonaje)
                => quien.Otras[caracteristica] = baseDelPersonaje + StatsHandler.GetEquipBonus(caracteristica);

            const int PorCadaDiez = 10;
            int agilidad = quien.Agility;
            int sabiduria = GameState.TotalWisdom + StatsHandler.GetEquipBonus(12);

            Poner(78, agilidad / PorCadaDiez);    // huida
            Poner(79, agilidad / PorCadaDiez);    // placaje
            Poner(27, sabiduria / PorCadaDiez);   // esquiva de puntos de acción
            Poner(28, sabiduria / PorCadaDiez);   // esquiva de puntos de movimiento
            Poner(82, sabiduria / PorCadaDiez);   // retira PA: a tenth of wisdom, plus the gear's 410/411
            Poner(83, sabiduria / PorCadaDiez);   // retira PM: idem, 412/413
            Poner(12, GameState.TotalWisdom);     // sabiduría
            Poner(49, 0);                         // flat heals
            Poner(26, 0);                         // invocaciones
            Poner(50, 0);                         // reenvío
            // TEN, NOT ZERO. The sheet sent to the client already said (75, base 10) -- see
            // FullSheetOf -- while the server kept its own copy at zero: the client showed 10%
            // erosion and the server never eroded anybody, and every damage block on a player
            // went out without its f5. The one byte that told our hits apart from the real ones.
            Poner(75, Fighter.ErosionBase);       // erosión
            Poner(101, 0);                        // % de resistencia a los daños
            Poner(102, 0);
            Poner(95, 0); Poner(96, 0); Poner(97, 0);
            // Resistencias porcentuales, una por elemento.
            foreach (int cual in new[] { 54, 55, 56, 57, 58 }) Poner(cual, 0);
            // Empuje: la 84 es el DAÑO que se hace empujando y la 85 la RESISTENCIA a que te
            // empujen. Estaban cambiadas: el daño de empuje se mandaba en el hueco de la
            // resistencia, y por eso la ficha enseñaba 130 en la columna de resistencia.
            Poner(84, 0);
            Poner(85, 0);
        }

        /// <summary>
        /// Los once multiplicadores de daño, que el servidor real manda SIEMPRE a cien.
        ///
        /// Son la razón de que no se viera la previsualización de daños. El cliente estima el golpe
        /// multiplicando por ellos, y lo que no llega vale cero: cualquier cuenta multiplicada por
        /// cero partido cien da cero, y un cero no se pinta. En la ficha real del jugador van las
        /// once, todas con base cien, y en la del monstruo también.
        /// </summary>
        private static readonly int[] Multiplicadores =
            { 107, 150, 120, 121, 122, 123, 124, 125, 141, 142, 143 };

        /// <summary>
        /// La ficha del combatiente, en el orden del jxb real y con sus cincuenta y tres
        /// características.
        ///
        /// Mandaba veintiuna. Las que faltaban no eran adorno: además de los once multiplicadores,
        /// faltaban la 85 —el empuje fijo, que es justo lo que alimenta la previsualización de
        /// DESPLAZAMIENTO de los hechizos que empujan— y el alcance. Y una estaba mal: los daños
        /// críticos iban en la 86, que no aparece en ninguna de las cincuenta y tres entradas de la
        /// captura; la buena es la 87.
        ///
        /// Se manda la misma en la colocación y en el combate. El servidor real también: en el jxg
        /// de la colocación el jugador lleva sus valores puestos y sólo el monstruo va con los
        /// huecos vacíos.
        /// </summary>
        /// <param name="conTraza">
        /// El renglón [FICHA] del registro. Va apagado cuando quien pregunta es ValorDeFicha, que
        /// llama a esto una vez por cada característica que mueve un embrujo: con la traza puesta
        /// llenaba el registro de copias de la misma ficha varias veces por turno.
        /// </param>
        private static List<(int Characteristic, long Base, long Gear)> FullSheetOf(Fighter fighter,
                                                                                   bool conTraza = true)
        {
            var summonCharacteristic = SummonCharacteristicFor(fighter);
            var ficha = new List<(int, long, long)>
            {
                (ActionPointsCharacteristic, fighter.MaxAP, 0),
                (MovementPointsCharacteristic, fighter.MaxMP, 0),
                // Las cinco resistencias en tanto por ciento, EN EL HUECO DE LA DERECHA.
                //
                // Iban en el de la izquierda —el de los puntos invertidos— y el servidor real las
                // manda en el del equipo: medido sobre el jxb de «combate contra 4 poutchs», donde
                // las cinco salen como f7 y ninguna como f2. Puestas en el hueco que no es, el
                // cliente lee cero donde hay algo.
                (37, 0, fighter.NeutralResPct),
                (33, 0, fighter.EarthResPct),
                (35, 0, fighter.WaterResPct),
                (36, 0, fighter.AirResPct),
                (34, 0, fighter.FireResPct),
                (58, 0, fighter.Otra(58)), (54, 0, fighter.Otra(54)), (56, 0, fighter.Otra(56)),
                (57, 0, fighter.Otra(57)), (55, 0, fighter.Otra(55)),
                // El empuje y los daños críticos, que son de las que el cliente necesita para
                // estimar. La 84 es el daño de empuje y la 85 la resistencia a él; y los críticos
                // van en la 87, no en la 86, que no aparece en ninguna entrada de la captura.
                (85, 0, fighter.Otra(85)),
                (87, 0, fighter.CriticalDamage),
                (101, 0, fighter.Otra(101)),
                (27, 0, fighter.Otra(27)), (28, 0, fighter.Otra(28)), (93, 3, 0),
                (79, 0, fighter.Otra(79)), (78, 0, fighter.Otra(78)),

                // La vida ENTERA, también la del jugador.
                //
                // Estuvo un rato mandándose sólo la que da el nivel, porque con la ficha a medias
                // —veintiuna características— el cliente mezclaba lo nuestro con lo que él sabía de
                // los objetos y la barra salía al doble. Con la ficha completa ya no mezcla: se
                // queda con la nuestra, y mandarle sólo la del nivel dejaba al personaje con 1.050
                // de vida en mitad del combate.
                (LifeCharacteristic, fighter.MaxHP, 0),

                (10, 0, fighter.Strength),
                // La vitalidad se queda a cero A PROPÓSITO, aunque el servidor real la mande.
                // Medido dos veces: este cliente la SUMA a la vida máxima además de la que va en
                // la característica 0, y el personaje entraba en combate con 7.856 de vida donde
                // tiene 4.453 —justo sus 3.403 de vitalidad de más—. Volver a ponerla exige
                // averiguar antes qué espera exactamente en la 0.
                (11, 0, 0),
                (13, 0, fighter.Chance),
                (14, 0, fighter.Agility),
                (15, 0, fighter.Intelligence),
                (16, 0, fighter.FlatDamage),
                (18, 0, fighter.CriticalBonus),
                (19, 0, fighter.Range),
                (25, 0, fighter.Power),
                (CaracteristicaDeInvocaciones, summonCharacteristic.Base,
                 summonCharacteristic.Gear),
                (50, 0, fighter.Otra(50)), (75, Fighter.ErosionBase, fighter.Otra(75) - Fighter.ErosionBase),
                // Aquí iba la 84, el daño de empuje. El servidor real NO LA MANDA: su ficha
                // tiene 53 entradas y la 84 no está en ninguna, mientras que la 85 —la
                // resistencia al empuje— sí. Y la metíamos justo en la posición 33, que es donde
                // empiezan los daños elementales (88 a 92), así que era una entrada que el
                // cliente no espera, colocada justo delante de las cinco que alimentan la
                // previsualización de daño.
                (88, 0, fighter.EarthDamage),
                (89, 0, fighter.FireDamage),
                (90, 0, fighter.WaterDamage),
                (91, 0, fighter.AirDamage),
                (92, 0, fighter.NeutralDamage),
                (95, 0, fighter.Otra(95)), (96, 0, fighter.Otra(96)),

                // La 97 es la vida que le falta, y es la UNICA forma que tiene el cliente de saber
                // la del personaje que maneja: la de los demas la descuenta el solo de los golpes.
                // Iba clavada a cero, asi que la barra del jugador se quedaba llena toda la pelea.
                // Aqui va tambien para que un reenganche a un combate en marcha pinte la vida buena.
                (Network.FightProtocol.TemporaryLifeMalus,
                 fighter.CurrentHP - fighter.MaxHP, -fighter.VidaErosionada),
                (102, 0, fighter.Otra(102)),
            };

            foreach (int cual in Multiplicadores) ficha.Add((cual, 100, 0));

            // TRAZA de la ficha: es LO QUE VE EL CLIENTE para calcular su previsualización de
            // daño. Si ahí salen la potencia, los daños y los elementales con sus números buenos
            // y la previsualización sigue enseñando sólo el daño base, entonces el que no está
            // haciendo la cuenta es el cliente, y hay que mirar qué más espera.
            //
            // Sólo la del personaje que maneja el jugador: la de los monstruos llenaría el
            // registro y no es la que se está midiendo.
            if (conTraza && !fighter.IsMonster)
            {
                var interesan = new[] { 10, 11, 13, 14, 15, 16, 18, 19, 25, 84, 88, 89, 90, 91, 92 };
                var pintado = new List<string>();
                foreach (var (cual, baseV, extra) in ficha)
                {
                    if (Array.IndexOf(interesan, cual) < 0) continue;
                    pintado.Add($"{cual}={baseV}+{extra}");
                }
                Program.LogDebug($"[FICHA] {fighter.Id}: " + string.Join(" ", pintado));
            }

            return ficha;
        }

        /// <summary>El aspecto de un monstruo: el mismo bloque que lleva en el mapa.</summary>
        private static byte[] MonsterLook(Fighter fighter)
            => Network.Pb.New()
                .Var(2, 3)
                .VarIfNotZero(3, fighter.LookBoneId)
                .Build();

        /// <summary>El aspecto normal que el combatiente llevaba al entrar en esta pelea.</summary>
        private static byte[] NormalFightLook(Fighter fighter)
        {
            // A double (180) wears the look of the character it copies.
            if (fighter.IsMonster && fighter.DoubleOf == 0) return MonsterLook(fighter);
            if (fighter.IsBot) return fighter.BotLook ?? Array.Empty<byte>();

            var character = DatabaseManager.GetCharacterById(fighter.DoubleOf != 0 ? fighter.DoubleOf : fighter.Id);
            return character != null
                ? Managers.BreedLookTable.BuildLook(character.Breed, character.Sex,
                                                     character.HeadId, null, character.Id)
                : Array.Empty<byte>();
        }

        /// <summary>
        /// Anuncia una transformación por la acción de combate 149. La apariencia cero devuelve
        /// el aspecto normal; las demás conservan colores, pieles, escala y mascotas y sustituyen
        /// sólo los huesos de la raíz.
        /// </summary>
        private static async Task AnnounceAppearanceAsync(NetworkStream stream, Fighter fighter,
                                                          int appearance)
        {
            byte[] look = NormalFightLook(fighter);
            if (look.Length == 0) return;

            if (appearance != 0)
            {
                int bones = Managers.Cosmetics.AppearanceBones(appearance);
                if (bones <= 0)
                {
                    Program.LogDebug($"[Combate] La apariencia {appearance} no tiene huesos " +
                                     "compatibles; no se cambia el aspecto.");
                    return;
                }
                look = Network.FightProtocol.WithRootBones(look, bones);
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildLookChanged(fighter.Id, look)));
            Program.LogDebug($"[Combate] Aspecto de {fighter.Id}: " +
                             (appearance == 0 ? "normal" : $"apariencia {appearance}"));
        }

        public static byte[] BuildIgsPacket(FightInstance fight)
        {
            return BuildGameNodePacket("type.ankama.com/igs", Array.Empty<byte>());
        }

        /// <summary>
        /// Responds to map load request (kkr / jqf) from the client during fight setup.
        /// Sends BURST 3 containing igs, jya, jyj, jxx, jyi, jyf, jyk, jxe, jwo, jox.
        /// </summary>
        public static async Task HandleFightMapLoad(NetworkStream stream)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            // Apuntar y comprobar de una vez: si ya estaba, es que a ESTE cliente ya se le mandó
            // -- por aquí o por SendPreparationAsync -- y lo que toca es el reenvío corto.
            if (!fight.MarkPrepared(GameState.CharacterId))
            {
                // Re-send burst 3 on subsequent map load requests (jqf/kkr)
                await ResendFightMapBurst3(stream, fight);
                return;
            }
            Program.LogDebug("[FightHandler] Responding to fight map request (kkr) with BURST 3...");

            // =========================================================================
            // BURST 3 (Fired by the client's kkr)
            // Sequence: igs, jya, jyj, jxx (all), jyi, jyf, jykjxe, jwo, jox
            // =========================================================================
            // 1. igs (GameFightComplementaryInformationsDataMessage with subarea & placement positions)
            await WriteFrameAsync(stream, BuildIgsPacket(fight));

            // 1b. jyg (GameFightJoinMessage - switches soundtrack to combat music and hides roleplay entities)
            var jygMsg = new ProtoMessage();
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 0 });
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 0 });
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 450 });
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 4 });
            await WriteFrameAsync(stream, BuildGameNodePacket(Op.Uri(Op.Jyg), jygMsg.ToByteArray()));

            // 2. jya (FightStarting)
            await SendFightStarting(stream, fight);

            // 3. jyj (GameFightOptionStateUpdateMessage)
            var jyjMsg = new ProtoMessage();
            jyjMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });
            jyjMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 4 });
            jyjMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 443 });
            jyjMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 1 });
            await WriteFrameAsync(stream, BuildGameNodePacket(Op.Uri(Op.Jyj), jyjMsg.ToByteArray()));

            // 4. jxx (GameFightShowFighterMessage) for each fighter
            foreach (var f in fight.Todos)
            {
                await SendFighterShow(stream, f);
            }

            // 5. jyi (GameFightPlacementPossiblePositionsMessage)
            await SendPlacementPositionsList(stream, fight);

            // 6. jyf (placement options update - single packet)
            var jyfPackets = BuildPlacementPossiblePositionsPackets(fight);
            if (jyfPackets.Count > 0)
            {
                await WriteFrameAsync(stream, jyfPackets[0]);
            }

            // 7. jyk options (0, 1, 2, 3)
            int[] optionTypes = new int[] { 2, 1, 3, 0 };
            foreach (int opt in optionTypes)
            {
                var jykMsg = new ProtoMessage();
                jykMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = opt });
                jykMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 300 });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jyk", jykMsg.ToByteArray()));
            }

            // 8. jxe (GameFightTurnListMessage)
            await SendTurnList(stream, fight);

            // 9. jwo (GameFightTurnStartPlayingMessage header - empty 3-letter opcode)
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwo", Array.Empty<byte>()));

            // 10. jox (GameFightTurnStartMessage for placement phase - f1=450, f2.f1=-3, f2.f2=-2)
            await SendPlacementTurnStart(stream, fight);
            Program.LogDebug("[FightHandler] BURST 3 sent successfully. Client in placement phase (45s).");
        }

        private static async Task ResendFightMapBurst3(NetworkStream stream, FightInstance fight)
        {
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/igs", Array.Empty<byte>()));
            foreach (var f in fight.Todos)
            {
                await SendFighterShow(stream, f);
            }
            await SendPlacementPositionsList(stream, fight);
        }

        public static byte[] BuildTurnListBytes(FightInstance fight)
        {
            var jxeMsg = new ProtoMessage();
            var fighters = (fight.TurnOrder.Count > 0) 
                ? fight.TurnOrder 
                : fight.Todos.OrderByDescending(f => f.Initiative).ToList();

            foreach (var fighter in fighters)
            {
                var fSubInner = new ProtoMessage();
                fSubInner.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighter.Id });

                var fSubOuter = new ProtoMessage();
                fSubOuter.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = fSubInner.ToByteArray() });

                jxeMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = fSubOuter.ToByteArray() });
            }

            return BuildGameNodePacket("type.ankama.com/jxe", jxeMsg.ToByteArray());
        }

        public static async Task SendTurnList(NetworkStream stream, FightInstance fight)
        {
            byte[] jxePacket = BuildTurnListBytes(fight);
            await WriteFrameAsync(stream, jxePacket);
            Program.LogDebug($"[FightHandler] Sent jxe (GameFightTurnListMessage) with {fight.TurnOrder.Count} fighters in turn order.");
        }

        public static async Task SendPlacementTurnStart(NetworkStream stream, FightInstance fight)
        {
            var joxSub = new ProtoMessage();
            joxSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = -3 });
            joxSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -2 });

            var joxMsg = new ProtoMessage();
            joxMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 450 }); // 45s Placement Phase Timer
            joxMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = joxSub.ToByteArray() });
            if (fight != null)
            {
                joxMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fight.MapId });
            }

            byte[] joxPacket = BuildGameNodePacket("type.ankama.com/jox", joxMsg.ToByteArray());
            await WriteFrameAsync(stream, joxPacket);
            Program.LogDebug($"[FightHandler] Sent jox Placement Phase Countdown (45s) for map {fight?.MapId}.");
        }

        public static byte[] BuildPlacementPositionsListBytes(FightInstance fight)
        {
            var jyiMsg = new ProtoMessage();

            using var msRed = new MemoryStream();
            var codedRed = new CodedOutputStream(msRed);
            foreach (var c in fight.RedPlacementCells)
            {
                codedRed.WriteUInt32((uint)c);
            }
            codedRed.Flush();

            using var msBlue = new MemoryStream();
            var codedBlue = new CodedOutputStream(msBlue);
            foreach (var c in fight.BluePlacementCells)
            {
                codedBlue.WriteUInt32((uint)c);
            }
            codedBlue.Flush();

            var innerSub = new ProtoMessage();
            innerSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = msRed.ToArray() });
            innerSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = msBlue.ToArray() });

            jyiMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = innerSub.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jyi", jyiMsg.ToByteArray());
        }

        public static async Task SendPlacementPositionsList(NetworkStream stream, FightInstance fight)
        {
            byte[] jyiPacket = BuildPlacementPositionsListBytes(fight);
            await WriteFrameAsync(stream, jyiPacket);
            Program.LogDebug("[FightHandler] Sent dynamic jyi (GameFightPlacementPossiblePositionsMessage).");
        }

        public static async Task HandleFightMessageAsync(NetworkStream stream, byte[] payload, string payloadStr)
        {
            // El mismo candado que usa el reloj del turno: mientras se atiende lo que manda el
            // cliente, el reloj no puede meter su ráfaga por el medio, y al revés. Es de esta
            // sesión, así que un cliente atascado sólo se atasca a sí mismo.
            var turno = MiTurno();
            await turno.WaitAsync();
            try
            {
                await AtenderAlClienteAsync(stream, payload, payloadStr);
            }
            finally
            {
                turno.Release();
            }
        }

        private static async Task AtenderAlClienteAsync(NetworkStream stream, byte[] payload, string payloadStr)
        {
            Program.LogDebug($"\n[FIGHT PACKET RECEIVED] Length: {payload.Length} bytes");
            try
            {
                var parsed = ProtoMessage.Parse(payload);
                Program.LogDebug(parsed.DumpFieldsToString("  "));
            }
            catch
            {
                string hex = BitConverter.ToString(payload).Replace("-", " ");
                if (hex.Length > 80) hex = hex.Substring(0, 80) + "...";
                Program.LogDebug($"  Hex: {hex}");
            }

            // Sólo estos tres los manda el cliente de la 3.6.10.10 durante la preparación, y están
            // medidos. Lo de antes escuchaba jyz, jza, jub, jwe y jxw: el primero con las letras
            // transpuestas y los otros o inexistentes en esta versión o mensajes que manda el
            // SERVIDOR, no el cliente. El resto del combate irá entrando aquí conforme se descifre.
            // Red de seguridad del final en espera: si llega cualquier otra cosa antes que el acuse,
            // se enseña ya. Así un cliente que no acuse no deja el combate colgado para siempre, y
            // no hace falta un temporizador escribiendo en el socket por su cuenta, que se
            // entrelazaría con lo que escribe este mismo hilo.
            // And the jwz that answers the jxh of the end is what normally ends it: it comes once
            // the client has played the blows that ended the fight (see CheckFightOverAsync).
            var fight = GetCurrentFight();

            if (fight != null && fight.FinPendiente != 0 && !payloadStr.Contains(Op.Uri(Op.Jti)))
            {
                fight.FinPendiente = 0;
                Program.LogDebug("[Combate] El final estaba esperando el acuse y ha llegado otra cosa; se enseña.");
                await EndFightAsync(fight);
            }

            if (payloadStr.Contains(Op.Uri(Op.Jzy)))
            {
                if (fight != null && fight.State == Jondo.Unity.World.Fights.FightState.Ongoing)
                {
                    await HandleCombatMoveRequest(stream, payload);
                }
                else
                {
                    await HandlePlacementCellChangeRequest(stream, payload);
                }
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kaq)))
            {
                if (Network.FightProtocol.ReadReady(payload)) await HandleTurnReady(stream, payload);
            }
            else if (payloadStr.Contains("type.ankama.com/jwz"))
            {
                // "Enterado del jxh": hasta que no llega esto, el turno no empieza.
                await ConfirmAsync(stream);
            }
            else if (payloadStr.Contains("type.ankama.com/jxy"))
            {
                // Pasar turno. Va vacío.
                await PassTurnAsync(stream);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Jrw)))
            {
                // Andar. Es el mismo mensaje que fuera del combate; aquí gasta PM.
                await WalkAsync(stream, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Jwh)) || payloadStr.Contains(Op.Uri(Op.Jwn)))
            {
                // Lanzar un hechizo, o pegar con el arma si no trae hechizo. Los dos mensajes
                // entran por aquí: el jwh apunta por casilla y el jwn desde el carrusel, por
                // identificador de combatiente, y CastAsync sabe leer los dos.
                //
                // El jwn estaba en la puerta de fuera -GameNodeProxy- pero NO aqui, asi que
                // llegaba al combate y no lo recogia ninguna rama: se caia por el final del
                // if/else sin traza ninguna. Son DOS cadenas de enrutado, no una.
                await CastAsync(stream, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Jti)))
            {
                // El acuse de cada secuencia cerrada. No lleva respuesta, pero es lo que destraba
                // la pantalla de fin de combate cuando el último golpe la dejó esperando.
                await AcuseAsync(stream, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kme)))
            {
                await AbandonAsync(stream);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Hoy)))
            {
                await HandleFightOptionToggleRequest(stream, payload);
            }
            // Los retos. Cuatro de los cinco no llevan respuesta: el servidor real se queda
            // callado ante el kwv y el kwi, y sólo contesta al kwr con la lista y al kwj con el
            // reto fijado. Ver Handlers.ChallengeHandler.
            else if (payloadStr.Contains(Op.Uri(Op.Kwr)))
            {
                if (fight != null) await ChallengeHandler.OpenAsync(stream, fight);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kwj)))
            {
                if (fight != null) await ChallengeHandler.ValidateAsync(stream, fight, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kwv)))
            {
                // Marcar un candidato. OJO: el primero que llega no es un clic del jugador, sino
                // la preselección que hace el cliente él solo dos milisegundos después de recibir
                // la lista. Por eso marcar NO fija nada: hace falta el kwj.
                if (fight != null) ChallengeHandler.Mark(fight, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kwo)))
            {
                await ChallengeHandler.SettingsAsync(stream, fight, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kwi)) || payloadStr.Contains(Op.Uri(Op.Kxb)))
            {
                // Pasar el ratón por un reto y el otro ajuste del panel. No llevan respuesta en
                // ninguna de las 305 capturas; se recogen para que no salgan por el registro como
                // paquetes sin atender.
            }
        }

        /// <summary>
        /// El cliente ataca a un grupo de monstruos (hqa).
        ///
        ///   f1: el id contextual del grupo, el mismo negativo con el que viaja en el jss
        ///
        /// El servidor contesta un jsq vacío y arranca la preparación.
        /// </summary>
        public static async Task AttackAsync(NetworkStream stream, byte[] payload)
        {
            long groupId = Network.FightProtocol.ReadFightRequest(payload);
            if (groupId == 0) return;

            long here = GameState.MapId;
            var group = MobSpawnManager.GetMobsForMap(here).Find(g => g.MobId == groupId);
            if (group == null)
            {
                Program.LogDebug($"[Combate] El cliente ataca al {groupId} del mapa {here}, " +
                                 "que aquí no es ningún grupo.");
                return;
            }

            if (IsGroupFighting(groupId))
            {
                Program.LogDebug($"[Fight] Group {groupId} of map {here} is already being fought.");
                return;
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jsq,
                Network.FightProtocol.BuildFightAccepted()));

            await InitiateFightFromMobCollision(stream, group, here, groupId);
        }

        private static async Task HandleFightOptionToggleRequest(NetworkStream stream, byte[] payload)
        {
            long mobContextId = 0;
            try
            {
                var msg = ProtoMessage.Parse(payload);
                if (msg.Fields.Count > 0 && msg.Fields[0].WireType == 2)
                {
                    var inner = ProtoMessage.Parse(msg.Fields[0].BytesValue);
                    if (inner.Fields.Count > 1 && inner.Fields[1].WireType == 2)
                    {
                        var inner2 = ProtoMessage.Parse(inner.Fields[1].BytesValue);
                        if (inner2.Fields.Count > 1 && inner2.Fields[1].WireType == 2)
                        {
                            var inner3 = ProtoMessage.Parse(inner2.Fields[1].BytesValue);
                            if (inner3.Fields.Count > 0 && inner3.Fields[0].WireType == 0)
                            {
                                mobContextId = inner3.Fields[0].VarIntValue;
                            }
                        }
                    }
                }
            }
            catch { }
            Program.LogDebug($"[FightHandler] Client requested Fight Interaction (hoy) for Mob Context ID {mobContextId}.");

            var fight = GetCurrentFight();
            if (fight == null)
            {
                // El grupo que ha clicado el jugador, y sólo ése.
                //
                // Antes, si la búsqueda por id fallaba, se caía en un mobs.FirstOrDefault() y el
                // jugador acababa peleando contra un grupo cualquiera del mapa: el primero de la
                // lista. Y el que desaparecía del mapa al ganar era ese primero, no el que él había
                // clicado. Con los ids ya cuadrados entre el jss y el jpv la búsqueda no debería
                // fallar nunca; si falla, lo que hay que hacer es decirlo, no atacar a otro.
                var mobs = MobSpawnManager.GetMobsForMap(GameState.MapId);
                var mobGroup = mobContextId != 0
                    ? (mobs.FirstOrDefault(m => m.MobId == mobContextId)
                       ?? MobSpawnManager.GetMobGroupById(mobContextId))
                    : null;

                if (mobGroup != null)
                {
                    await InitiateFightFromMobCollision(stream, mobGroup, GameState.MapId, mobContextId);
                    return;
                }

                Program.LogDebug($"[Combate] El cliente pide pelear con el {mobContextId} del mapa " +
                                 $"{GameState.MapId}, que aquí no es ningún grupo. No se hace nada.");
            }
            else
            {
                // Re-sync combat context packets if requested
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/joq", Array.Empty<byte>()));
                await WriteFrameAsync(stream, BuildJpfPacket(fight.DefenderLeaderId));
                
                var johMsg = new ProtoMessage();
                johMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.MapId });
                await WriteFrameAsync(stream, BuildGameNodePacket(Op.Uri(Op.Joh), johMsg.ToByteArray()));

                foreach (var p in BuildPlacementPossiblePositionsPackets(fight))
                {
                    await WriteFrameAsync(stream, p);
                }
                foreach (var f in fight.Todos)
                {
                    await SendFighterShow(stream, f);
                }
                await SendFightStarting(stream, fight);
            }
        }

        /// <summary>
        /// El combate en el que está EL JUGADOR DE ESTA CONEXIÓN.
        ///
        /// Devolvía <c>_activeFights.Values.FirstOrDefault()</c>, o sea el primer combate abierto
        /// en todo el servidor. Con un solo jugador daba igual; con dos, los dos manejaban el
        /// mismo combate: el segundo en entrar movía las fichas del primero.
        ///
        /// Ahora se busca por el personaje de la sesión. Si el jugador no está en ninguno, no hay
        /// combate, que es lo correcto y antes tampoco pasaba.
        /// </summary>
        private static FightInstance? GetCurrentFight()
            => FightOf(Network.SessionContext.State.CharacterId);

        /// <summary>
        /// The session of the player who plays this fighter, when it is a summon of his and he
        /// is connected; null for a player himself, for a monster, and for a summon whose owner
        /// is a monster or is away. The away case is what makes a summon fall back to passing
        /// its turn on its own.
        /// </summary>
        private static GameSession? Dueno(FightInstance fight, Fighter fighter)
        {
            if (fighter == null || !fighter.EsInvocado) return null;
            var owner = fight.Buscar(fighter.Invocador);
            if (owner == null || owner.IsMonster) return null;
            return SessionRegistry.FindByCharacter(owner.Id);
        }

        /// <summary>
        /// Whose end-of-fight numbers a fighter's deeds go to: his own when he is a person,
        /// his summoner's when he is that person's summon, nobody's for a monster.
        /// </summary>
        private static Jondo.Unity.World.Fights.FightStatistics StatisticsBehind(FightInstance fight,
                                                                                Fighter fighter)
        {
            if (fighter == null) return null;
            if (!fighter.IsMonster && !fighter.EsInvocado) return fight.StatisticsOf(fighter.Id);
            if (!fighter.EsInvocado) return null;
            var owner = fight.Buscar(fighter.Invocador);
            return owner != null && !owner.IsMonster ? fight.StatisticsOf(owner.Id) : null;
        }

        /// <summary>
        /// What goes off when a fighter dies, fired while he is still standing: his attitudes
        /// under X and the spells hooked on him with an X trigger. Called by whoever is about
        /// to take his last point of life, BEFORE taking it -- the Tymobot's passive casts
        /// 20683 under X and the capture has that cast before the 103, and Polvo's "explode if
        /// destroyed" needs a bomb that can still cast its explosion. The explosion's own 141
        /// comes back through here for the same bomb, which is what the flag is for: the
        /// second time round nothing fires, the blow is simply taken.
        /// </summary>
        /// <returns>Whether he is still alive once everything of his has gone off.</returns>
        private static async Task<bool> AlMorirAsync(NetworkStream stream, FightInstance fight, Fighter quien,
                                                     Fighter asesino = null)
        {
            if (quien == null || !quien.IsAlive) return false;
            if (quien.Muriendo) return true;
            quien.Muriendo = true;
            var antes = fight.TriggeringAttacker;
            fight.TriggeringAttacker = asesino ?? antes;
            try
            {
                await ActitudesAsync(stream, fight, quien, Managers.EffectEngine.AlMorir);
                await EngancheAsync(stream, fight, quien, Managers.EffectEngine.AlMorir);
            }
            finally
            {
                fight.TriggeringAttacker = antes;
                if (quien.IsAlive) quien.Muriendo = false;
            }
            return quien.IsAlive;
        }

        /// <summary>A blow landed: written down for whoever dealt it and for whoever took it.</summary>
        private static void AnotarElGolpe(FightInstance fight, Fighter author, Fighter victim, int amount,
                                          bool fromTurnTrigger = false, bool push = false)
        {
            if (amount <= 0) return;

            if (victim != null && !victim.IsMonster && !victim.EsInvocado)
                fight.StatisticsOf(victim.Id).DamageTaken += amount;

            var dealt = StatisticsBehind(fight, author);
            if (dealt == null) return;
            // Hurting your own side counts for nobody.
            if (victim != null && victim.TeamId == author.TeamId) return;

            if (author.EsInvocado) dealt.SummonDamage += amount;
            else if (push) dealt.PushDamage += amount;
            else if (fromTurnTrigger) dealt.TriggerDamage += amount;
            else if (fight.CurrentDamageSource == Jondo.Unity.World.Fights.DamageSource.Glyph) dealt.GlyphDamage += amount;
            else dealt.DirectDamage += amount;
        }

        /// <summary>A heal landed: given by one, received by the other.</summary>
        private static void AnotarLaCura(FightInstance fight, Fighter author, Fighter target, int amount)
        {
            if (amount <= 0) return;
            var given = StatisticsBehind(fight, author);
            if (given != null) given.HealsGiven += amount;
            if (target != null && !target.IsMonster && !target.EsInvocado)
                fight.StatisticsOf(target.Id).HealsReceived += amount;
        }

        /// <summary>The enemies of this person's side that have fallen, summons not counted.</summary>
        private static int EnemigosCaidos(FightInstance fight, long characterId)
        {
            var yo = fight.Buscar(characterId);
            if (yo == null) return 0;
            return fight.Bando(yo.TeamId == FightInstance.Azules ? FightInstance.Rojos : FightInstance.Azules)
                        .Count(f => f != null && !f.IsAlive && !f.EsInvocado && !f.EsIlusion);
        }

        /// <summary>The cast limits of whoever is casting: a summon's own grade, a player's by level.</summary>
        private static LimitesDelHechizo LimitesDelQueLanza(Fighter caster, int spell)
        {
            if (caster.EsInvocado)
            {
                foreach (var (suyo, grado) in caster.HechizosDeInvocado)
                {
                    if (suyo == spell) return LimitesDeGrado(spell, grado);
                }
            }
            return LimitesDe(spell, caster.Level);
        }

        /// <summary>The fight this character is in, or null. Anybody may ask, not only his session.</summary>
        public static FightInstance? FightOf(long characterId)
        {
            if (characterId == 0) return null;

            foreach (var combate in _activeFights.Values)
            {
                foreach (var f in combate.Azul) if (f.Id == characterId) return combate;
                foreach (var f in combate.Rojo) if (f.Id == characterId) return combate;
            }
            return null;
        }

        // ─── Coming back into a fight ───────────────────────────────────────────
        //
        // A client closed in the middle of a fight -- the game killed, the cable, a crash -- leaves
        // its fighter in the fight and the fight running (NetworkMessage drops what is written to
        // the dead socket). The real server offers the way back at the next login, and these are
        // the pieces, measured in the two reconnection captures:
        //
        //   kvi  kvd   the character list and an EMPTY kvd behind it: "do not stop here"
        //   kwb        the client's answer, also empty: "go on then"
        //   kva ...    the world entry as always, blocks 1 and 2 untouched
        //   kml kmp(1) jru(arena) lqu lqn(184 name) lva hms itg lru      block 3, fight flavour
        //   ijm kmv    the client asks for the board, as at any fight entry
        //   ...        during the placement: the preparation again, from scratch
        //              during the fight: ResumeForOneAsync below
        //
        // What is NOT measured is a way of saying no: the "decir que no" of the second capture
        // leaves no trace on the wire and the fight simply continues, so there is none here.

        /// <summary>
        /// The fight a character of this account would be put straight back into: running, not
        /// over, and the character still alive in it. Null when the login is an ordinary one.
        /// </summary>
        public static FightInstance? FightToRejoin(long characterId)
        {
            var fight = FightOf(characterId);
            if (fight == null || fight.State == Jondo.Unity.World.Fights.FightState.Ended) return null;
            var mine = fight.Buscar(characterId);
            return mine != null && mine.IsAlive ? fight : null;
        }

        /// <summary>
        /// Puts the freshly selected session into its fight: the map is the arena, and the place
        /// he left is what the database just loaded, since the teardown saved him there.
        /// </summary>
        public static void RejoinState(FightInstance fight)
        {
            var suyo = Network.SessionContext.State;

            // Where he came from is the FIGHT's memory, not the database's: whatever saved the
            // character while he was fighting saved him on the arena, and the first rejoin sent
            // him back to the arena at the end of the fight, monsters spawning around him.
            if (fight.DeDondeVenian.TryGetValue(suyo.CharacterId, out var origen))
            {
                suyo.RoleplayMapId = origen.Mapa;
                suyo.RoleplayCellId = origen.Casilla;
            }
            else
            {
                suyo.RoleplayMapId = fight.RoleplayMapId;
                suyo.RoleplayCellId = suyo.MapId == fight.RoleplayMapId ? suyo.CellId : 0;
            }
            suyo.MapId = fight.MapId;
            suyo.IsInFight = true;
            suyo.FightId = fight.FightId;
            suyo.CurrentFightMobId = fight.DefenderLeaderId;
            suyo.FightRejoinPending = true;

            // During the placement the preparation goes out again from scratch, and so does the
            // ready button: the capture shows him pressing it a second time.
            if (fight.State == Jondo.Unity.World.Fights.FightState.Placement)
            {
                fight.ForgetPreparation(suyo.CharacterId);
                fight.ForgetReady(suyo.CharacterId);
            }

            Program.LogDebug($"[Combate] {suyo.CharacterName} vuelve al combate #{fight.FightId} " +
                             $"({fight.State}), mapa {fight.MapId}.");
        }

        /// <summary>
        /// A player turns a fight down from the character screen: his fighter falls, whoever is
        /// still in the fight sees him fall the way a surrender is seen, and his session is put
        /// back where he left the map. A fight with nobody left to watch it is dropped.
        /// </summary>
        /// <remarks>
        /// Not measured on the wire -- the second reconnection capture's "decir que no" leaves
        /// no trace and the fight simply goes on -- so this is the surrender sequence of
        /// AbandonAsync sent to the OTHERS, from outside the fight. Nothing is written to the
        /// one leaving: he is at the character screen.
        /// </remarks>
        public static async Task AbandonFromOutsideAsync(FightInstance fight, long characterId)
        {
            var suyo = Network.SessionContext.State;
            if (fight.DeDondeVenian.TryGetValue(characterId, out var origen))
            {
                suyo.MapId = origen.Mapa;
                suyo.CellId = origen.Casilla;
            }
            else if (suyo.MapId == fight.MapId && fight.RoleplayMapId != fight.MapId)
            {
                suyo.MapId = fight.RoleplayMapId;
                suyo.CellId = MapManager.GetNearestWalkableCell(fight.RoleplayMapId, TeleportHandler.MapCentre);
            }
            suyo.IsInFight = false;
            suyo.FightId = 0;
            suyo.RoleplayMapId = 0;
            suyo.RoleplayCellId = 0;
            DatabaseManager.SaveCurrentCharacter();

            var quitter = fight.Buscar(characterId);
            if (quitter == null) return;

            var others = Publico(fight).Where(sesion => sesion.State.CharacterId != characterId).ToList();
            if (fight.State == Jondo.Unity.World.Fights.FightState.Ongoing && quitter.IsAlive)
            {
                if (fight.CurrentFighter == quitter) PararElReloj(fight);
                var author = (fight.CurrentFighter ?? quitter).Id;
                quitter.CurrentHP = 0;
                foreach (var otro in others)
                {
                    await otro.SendAsync(ConnectionProtocol.Push(Op.Jto,
                        Network.FightProtocol.BuildSequenceStart(author, Network.FightProtocol.SurrenderSequence)));
                    await otro.SendAsync(ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildDeath(quitter.Id, quitter.Id)));
                    await otro.SendAsync(ConnectionProtocol.Push(Op.Jzu,
                        Network.FightProtocol.BuildTeams(CarouselOrder(fight))));
                    await otro.SendAsync(ConnectionProtocol.Push(Op.Jwi,
                        Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), author,
                                                               Network.FightProtocol.SurrenderSequence)));
                }
            }
            else
            {
                quitter.CurrentHP = 0;
            }

            Program.LogDebug($"[Combate] {characterId} renuncia al combate #{fight.FightId} desde " +
                             $"la pantalla de personajes; quedan {others.Count} persona(s) dentro.");

            if (others.Count == 0)
            {
                fight.CancelTurnTimer();
                fight.CancelPlacementTimer();
                _activeFights.TryRemove(fight.FightId, out _);
                await FightOffTheMapAsync(fight);
                return;
            }

            // The others' fight goes on, or ends if he was the last of his side.
            var primero = others[0];
            using (SessionContext.Push(primero))
            {
                await CheckFightOverAsync(primero.Stream, fight);
            }
        }

        /// <summary>
        /// The running fight this session still owes the board of, once its client has asked for
        /// it with ijm/kmv. Null for a placement (that goes through PendingPreparation) and for
        /// everybody else.
        /// </summary>
        public static FightInstance? PendingResume()
        {
            var suyo = Network.SessionContext.State;
            if (!suyo.FightRejoinPending) return null;
            var fight = GetCurrentFight();
            if (fight == null || fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return null;
            return fight;
        }

        /// <summary>
        /// The board of a fight already running, for somebody who just came back into it.
        /// </summary>
        /// <remarks>
        /// The order is the one of the capture at 30.8 s, frame for frame:
        ///
        ///   ijq kam kaa jxg(each) jyy kmk(everybody) [jxu] jxb jzc [kwu] jxz kau jzu jwq jrk
        ///
        /// with the jzc carrying what is left of the turn in progress (f6), the jwq carrying
        /// every live buff, and the kmk everybody where they stand NOW. The jxu -- the receiver's
        /// own trigger counts and buffs, resent -- is not built: its buffs travel in the jwq
        /// anyway, and the trigger counts (jtn) are not kept per fighter here.
        ///
        /// Then one of two things, which is where the two reconnections of the second capture
        /// differ:
        ///
        ///   the turn is in progress (30.8 s)    nothing more; the client gets no jxh and sends no
        ///                                       jwz, its clock just runs down
        ///   a turn is waiting to be confirmed   jxh, so that the client answers jwz and the turn
        ///   (61.1 s)                            opens through ConfirmAsync as any other
        ///
        /// A fight whose only player was away parks in the second state, because nobody was
        /// there to confirm; a challenge is in the first, because the other player kept it going.
        /// </remarks>
        public static async Task ResumeForOneAsync(NetworkStream stream, FightInstance fight)
        {
            long me = GameState.CharacterId;
            Network.SessionContext.State.FightRejoinPending = false;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ijq,
                Network.FightProtocol.BuildMapReady()));

            var monsters = fight.Reglas.EnfrenteHayMonstruos
                ? fight.Rojo.ConvertAll(f => (long)f.MonsterId)
                : new List<long>();
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kam,
                Network.FightProtocol.BuildFightAnnounced(
                    fight.Reglas.TipoDelKam, fight.DefenderLeaderId, monsters, fight.FightId, me)));
            // The kaa of a fight in progress: f1 = 1 and no countdown. Without the flag the
            // client came back into the placement phase -- the READY button where the pass
            // button goes -- and pressing it started the fight over. Measured: "0801180120013004"
            // in both resumes of the capture against "1801200128bc033004" in its placement.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kaa,
                Network.FightProtocol.BuildFightInProgressSummary(
                    fight.Reglas.KaaConCuentaAtras ? fight.Reglas.TipoDelKam : 0)));

            // Everybody, where he stands now. The dead are listed too: the capture's jxg of a
            // fight in progress carry every fighter, and the jxb behind them says who is alive.
            foreach (var fighter in TodosLosCombatientes(fight))
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxg,
                    fighter.IsMonster
                        ? Network.FightProtocol.BuildFighter(
                              fighter.CellId, FacingOf(fight, fighter), fighter.Id,
                              PlacementSheetOf(fighter), MonsterLook(fighter),
                              Network.FightProtocol.MonsterIdentity(fighter.GradeIndex + 1,
                                                                    fighter.MonsterId, fighter.Level),
                              isMonster: true)
                        : BuildPlayerAppearance(fight, fighter)));
            }

            var character = DatabaseManager.GetCharacterById(me);
            var spellLayout = Managers.FightSpellLayout.Current(character?.Breed ?? 0,
                                                                 GameState.CharacterLevel,
                                                                 SessionContext.Current.AccountId);
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyy,
                Network.FightProtocol.BuildSpellBar(me, spellLayout.Spells, spellLayout.Bar)));

            var spots = new List<(int Cell, int Orientation, long Fighter)>();
            foreach (var fighter in TodosLosCombatientes(fight))
            {
                if (fighter.IsAlive) spots.Add((fighter.CellId, FacingOf(fight, fighter), fighter.Id));
            }
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmk,
                Network.FightProtocol.BuildFightersPlaced(spots)));

            var everyone = new List<Network.Pb>();
            var listados = new HashSet<long>();
            foreach (var fighter in fight.TurnOrder)
            {
                if (fighter == null || !listados.Add(fighter.Id)) continue;
                everyone.Add(BloqueDe(fight, fighter));
            }
            foreach (var fighter in TodosLosCombatientes(fight))
            {
                if (fighter == null || !listados.Add(fighter.Id)) continue;
                everyone.Add(BloqueDe(fight, fighter));
            }
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxb,
                Network.FightProtocol.BuildAllFighters(everyone)));

            // The turn the fight is on. The last one announced, with what is left of it; and if
            // none was announced yet -- the fight had just started when he left -- the one that
            // is about to be.
            var announced = fight.LastAnnouncedTurn;
            if (announced.Announced)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzc,
                    Network.FightProtocol.BuildTurnResumed(announced.FighterId, announced.Deciseconds,
                                                           announced.RemainingDeciseconds(DateTime.UtcNow),
                                                           announced.Round, announced.Carried)));
            }

            if (fight.Reglas.HayRetos) await ChallengeHandler.SendFinalListAsync(stream, fight);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxz,
                Network.FightProtocol.BuildRound(fight.RoundNumber)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kau,
                Network.FightProtocol.BuildFightOption(FightInstance.Azules, FightInstance.OptionSecret,
                    fight.OptionOn(FightInstance.Azules, FightInstance.OptionSecret), fight.FightId)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzu,
                Network.FightProtocol.BuildTeams(CarouselOrder(fight))));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwq,
                Network.FightProtocol.BuildBuffSync(LiveBuffFrames(fight))));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jrk,
                Network.FightProtocol.BuildFightMap(fight.MapId)));

            // His own cooldowns, which the fight-start burst would have given him.
            var yoMismo = fight.Buscar(me);
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxc,
                Network.FightProtocol.BuildCooldowns(me, RecargasDe(yoMismo))));

            if (fight.TurnAwaitingConfirmation)
            {
                var next = fight.CurrentFighter;
                if (next != null)
                {
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxh,
                        Network.FightProtocol.BuildConfirmTurn(next.Id)));
                }
            }

            Program.LogDebug($"[Combate] {GameState.CharacterName} tiene otra vez el tablero del " +
                             $"combate #{fight.FightId}: ronda {fight.RoundNumber}, " +
                             $"turno de {fight.CurrentFighter?.Id}" +
                             (fight.TurnAwaitingConfirmation ? ", esperando su jwz." : "."));
        }

        /// <summary>
        /// Every live buff in the fight as the payload its jxm carried, rebuilt from what the
        /// buff kept. Dice and dispellability are not kept, so those two fields stay out; the
        /// client draws the panel from the rest.
        /// </summary>
        private static IEnumerable<byte[]> LiveBuffFrames(FightInstance fight)
        {
            foreach (var quien in TodosLosCombatientes(fight))
            {
                if (quien == null || !quien.IsAlive) continue;
                foreach (var buff in quien.Buffs.Puestos)
                {
                    if (!buff.Vivo(fight.RoundNumber)) continue;
                    var (categoria, boost) = DatabaseManager.EffectFamily(buff.EffectId);
                    yield return Network.FightProtocol.BuildBuff(
                        quien.Id, buff.Quien, buff.Numero, buff.EffectId, buff.EffectUid,
                        buff.Cuanto, 0, 0, buff.HechizoOrigen, buff.Disparador, buff.CaducaEnRonda,
                        0, Network.FightProtocol.FamiliaDelEmbrujo(buff.EffectId, categoria, boost),
                        buff.NivelOrigen, buff.Critico);
                }
            }
        }

        private static async Task HandlePlacementCellChangeRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            // El cliente manda jzy, no jyz: las tres letras estaban transpuestas en el código de
            // antes, así que la rama nunca llegaba a entrar.
            var (_, newCell) = Network.FightProtocol.ReadPlacementMove(payload);
            if (newCell == 0) return;

            // En el equipo que sea. Se buscaba solo en el azul, asi que en un desafio el retado no
            // podia recolocarse: su peticion se caia aqui sin decir nada.
            var player = fight.Buscar(GameState.CharacterId);
            if (player == null) return;

            // Y cada uno en las casillas de SU lado. Se comprobaba siempre contra las azules, que
            // contra monstruos es lo mismo porque en el azul solo hay una persona.
            bool esAzul = fight.Azul.Contains(player);
            var suyas = esAzul ? fight.BluePlacementCells : fight.RedPlacementCells;
            if (!suyas.Contains(newCell))
            {
                Program.LogDebug($"[Combate] La casilla {newCell} no es de las " +
                                 $"{(esAzul ? "azules" : "rojas")}; no se coloca ahí.");
                return;
            }

            int oldCell = player.CellId;
            if (oldCell == newCell) return;

            fight.ChangePlacementCell(GameState.CharacterId, newCell);

            // El kmk dice DÓNDE ESTÁ CADA UNO; no es "esta casilla se libera". Se manda la posición
            // de todos, que es lo que hace el servidor real.
            //
            // Aquí me equivoqué feo la primera vez. En la captura, al recolocarse el jugador salía
            // un kmk con dos entradas y una llevaba -1, así que lo leí como "la casilla que se deja
            // va con nadie". Pero -1 NO es nadie: es el identificador del PRIMER MONSTRUO. Aquel
            // combate tenía un solo monstruo y estaba justo en esa casilla, así que las dos lecturas
            // encajaban con los mismos bytes. Mandado como "nadie", el cliente entendía que el
            // monstruo -1 se mudaba a la casilla que el jugador acababa de dejar, y se veía un pío
            // persiguiéndole por el tablero.
            var spots = new List<(int, int, long)>();
            foreach (var other in fight.Azul) spots.Add((other.CellId, FacingOf(fight, other), other.Id));
            foreach (var other in fight.Rojo) spots.Add((other.CellId, FacingOf(fight, other), other.Id));

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kmk,
                Network.FightProtocol.BuildFightersPlaced(spots)));

            Program.LogDebug($"[Combate] El jugador se coloca en la casilla {newCell} (venía de la {oldCell}).");
        }

        // NOTE: HandleCombatMovementRequest used to live here, an old version of combat movement
        // that echoed the client's compressed path back without expanding it and tacked on a kkz
        // that forced the position. That is what caused the teleporting. It was removed so it can
        // no longer compete with HandleCombatMoveRequest (the two names differed by a single
        // letter and the routing kept picking the wrong one).

        private static async Task HandleTurnReady(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;
            if (fight.State != Jondo.Unity.World.Fights.FightState.Placement)
            {
                Program.LogDebug("[Combate] Un listo (kaq) con el combate ya en marcha; se ignora.");
                return;
            }

            Program.LogDebug("[Combate] El jugador se declara listo (kaq).");

            // The members of his party with the automatic ready on are ready with him, BEFORE
            // the count of who is ready is made: fight 488 of the follow capture starts on the
            // leader's click with both kah side by side. See ReadyAlongWith in FightJoin.cs.
            var alongWith = ReadyAlongWith(fight, GameState.CharacterId);
            bool allReady = fight.SetFighterReady(GameState.CharacterId);

            // No lqg + lqt here any more. See ApagarLaRegeneracionAsync for what that pair
            // turned out to be and why sending it at fight start was the regeneration itself.

            // Enterado, que es lo único que contesta el servidor real al listo.
            //
            // A TODOS, no sólo a quien lo pulsó. El kah es lo que pinta las dos espadas cruzadas
            // sobre el retrato, y como se mandaba únicamente por el socket de quien se declaraba
            // listo, el otro no se enteraba nunca: en su pantalla el rival seguía sin marcar.
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kah,
                Network.FightProtocol.BuildReadyAck(GameState.CharacterId)));
            foreach (long otro in alongWith)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kah,
                    Network.FightProtocol.BuildReadyAck(otro)));
            }

            if (allReady) await StartFightAsync(fight);
        }

        /// <summary>
        /// Arranca el combate de verdad, con la tanda que manda el servidor real detrás del listo:
        ///
        ///   kai   se acabó la colocación
        ///   jyy   la barra de hechizos
        ///   jxz   en qué ronda vamos
        ///   jxc   los tiempos de relanzamiento
        ///   jto   abre
        ///   jxb   TODOS los combatientes con la ficha llena
        ///   jwi   cierra
        ///   jxh   "confírmame", y el cliente contesta jwz
        ///
        /// El kai es el corte entre las dos fases: delante va la colocación y detrás el combate.
        /// </summary>
        private static async Task StartFightAsync(FightInstance fight)
        {
            // Lo del combate, una sola vez.
            fight.StartFight();
            fight.CancelPlacementTimer();

            // The placement is over: the swords go from the map (hpr). Nobody joins any more.
            await SwordsGoneAsync(fight);

            // Y la racha entera a cada uno desde su propio contexto. Se manda completa y por
            // separado, en vez de repartir «esto a todos y esto al que sea», porque el orden que
            // trae la captura -- kai, jyy, jxz, jxc, jto, jxb, jwi, jxh -- mezcla tramas del
            // combate con tramas de quien mira, y partirlo dejaria a un cliente recibiendo el jxb
            // fuera de su propia secuencia.
            await ACadaUnoAsync(fight, sesion => ArrancarParaUnoAsync(sesion.Stream, fight));
        }

        /// <summary>
        /// The lqg + lqt pair. NOT SENT ANY MORE, and kept only so that nobody puts it back.
        /// </summary>
        /// <remarks>
        /// This used to go out at every fight start as "the switch that stops the client from
        /// regenerating life". Measured across the 400 captures it is the opposite of a fight
        /// switch: 203 lqg, every one followed by its lqt, and only THREE anywhere near the
        /// messages that open a fight. Where they actually cluster is behind look changes -- lxc,
        /// lwz, the emotes -- and behind world events; once, mid-fight, behind the turn start of
        /// a summon. That is the footprint of a regeneration being RE-EVALUATED: an empty "regen
        /// ends" followed by an empty "regen begins at the default rate", which is what a server
        /// does when you sit down or stand up.
        ///
        /// So sending the pair at fight start was starting the roleplay regeneration inside the
        /// fight. And that was the whole self-life mystery: the damage DID land on the client's
        /// own bar -- the erosion off the maximum proved it was reading the hit -- and then the
        /// bar climbed back one point at a time towards the WORLD maximum, which is why it read
        /// "2400/2386", above a maximum the erosion had already lowered. The real server sends
        /// nothing at fight start; its client stops regenerating on its own.
        ///
        /// What the pair means is still an inference; that it does not belong at fight start is
        /// measured.
        /// </remarks>
        private static Task ApagarLaRegeneracionAsync(FightInstance fight) => Task.CompletedTask;

        /// <summary>La racha de arranque, tal y como la ve UNA de las personas del combate.</summary>
        private static async Task ArrancarParaUnoAsync(NetworkStream stream, FightInstance fight)
        {
            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            long me = GameState.CharacterId;
            ActivityJournal.Current.Write("fight.started", SessionContext.Current.AccountId, me,
                new
                {
                    fightId = fight.FightId,
                    mapId = fight.MapId,
                    roleplayMapId = fight.RoleplayMapId,
                    monsters = fight.Rojo.Count,
                });

            // Los retos que quedaran sin validar los cierra el servidor aquí, ANTES del kai: si el
            // jugador se declaró listo con uno marcado y sin validar, ése cuenta, y si ni eso, el
            // servidor rellena. Detrás van los que impone el sitio. Medido en la anomalía.
            // Los retos son cosa de pelear contra monstruos: dan un extra sobre su botin. En un
            // desafio entre jugadores no hay botin que multiplicar, y en la fase de colocacion no
            // se ofrecio ninguno -- rellenarlos aqui es de donde salia el reto que aparecia solo
            // al empezar el combate.
            if (fight.Reglas.HayRetos) await ChallengeHandler.FillAsync(stream, fight);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kai,
                Network.FightProtocol.BuildFightBegins()));

            // Y la lista definitiva, que va entre el kai y el jyy.
            if (fight.Reglas.HayRetos) await ChallengeHandler.SendFinalListAsync(stream, fight);

            // Los hechizos con los que se pelea son los MISMOS que el personaje tiene fuera del
            // combate, y con la misma tripa: el hms de siempre lleva f1 { f1: grado, f3: hechizo,
            // f4: 1 } y el jyy lo repite en su f6. Antes se leía de la barra de accesos directos,
            // que puede estar a medio llenar, y por eso el cliente enseñaba todos los hechizos
            // durante la colocación —los suyos, de antes— y se quedaba en blanco al empezar el
            // turno, cuando por fin le llegaba nuestra lista corta.
            var spellLayout = Managers.FightSpellLayout.Current(character?.Breed ?? 0,
                                                                 GameState.CharacterLevel,
                                                                 SessionContext.Current.AccountId);
            var spells = spellLayout.Spells;
            var bar = spellLayout.Bar;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyy,
                Network.FightProtocol.BuildSpellBar(me, spells, bar)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxz,
                Network.FightProtocol.BuildRound(FirstRound)));

            // Los retos que senalan a un enemigo lo hacen aqui, detras del jyy, que es donde salen
            // los tres kwm de las capturas.
            if (fight.Reglas.HayRetos) await ChallengeWatcher.FightStartedAsync(stream, fight);

            // Los hechizos que NACEN con espera: el InitialCooldown de su grado. En la captura de
            // Paso de Cacería el primer jxc del combate lleva {370:1, 373:1, 32469:1}, y en la
            // base esos tres son exactamente los que tienen InitialCooldown a uno.
            // En el equipo que sea, por lo mismo: en un desafio quien mira puede estar en el rojo y
            // se quedaba sin sus esperas iniciales.
            var yoMismo = fight.Buscar(me);
            if (yoMismo != null)
            {
                foreach (var (hechizo, _) in spells)
                {
                    int espera = LimitesDe(hechizo, GameState.CharacterLevel).EsperaInicial;
                    if (espera > 0) yoMismo.Recarga[hechizo] = espera;
                }
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxc,
                Network.FightProtocol.BuildCooldowns(me, RecargasDe(yoMismo))));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(me, Network.FightProtocol.OpeningSequence)));

            // Las fichas completas de todos. Aquí es donde llegan la vida, el nivel y las
            // resistencias que en la colocación viajaban vacías.
            // Cada uno con SU cara y SU identidad, y bicho sólo el que de verdad lo sea.
            //
            // Aquí estaban las dos mitades de lo que se veía al empezar un desafío. El equipo azul
            // se construía con «character» -- la ficha de quien recibe -- para TODOS sus miembros,
            // así que al compañero se le pintaba con la cara de uno mismo. Y el equipo rojo se
            // anunciaba entero como monstruo, con identidad de monstruo e id cero: eso es lo que
            // convertía a la persona de enfrente en una interrogación en cuanto arrancaba el
            // combate, después de haberse visto bien durante la colocación.
            // In PLAY ORDER, the same order as the jzu: the real fight-start jxb lists the first
            // player first, and the client indexes its carousel against that.
            var everyone = new List<Network.Pb>();
            var listados = new HashSet<long>();
            foreach (var fighter in fight.TurnOrder)
            {
                if (fighter == null || !listados.Add(fighter.Id)) continue;
                everyone.Add(BloqueDe(fight, fighter));
            }
            foreach (var fighter in TodosLosCombatientes(fight))
            {
                if (fighter == null || !listados.Add(fighter.Id)) continue;
                everyone.Add(BloqueDe(fight, fighter));
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxb,
                Network.FightProtocol.BuildAllFighters(everyone)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), me,
                                                       Network.FightProtocol.OpeningSequence)));

            Program.LogDebug($"[Combate] Empieza el combate #{fight.FightId}: " +
                             $"{everyone.Count} combatientes, primero {fight.CurrentFighter?.Id}.");

            await CascadaDePasivosAsync(stream, fight);

            await AskToConfirmAsync(stream, fight);
        }

        private const int FirstRound = 1;


        /// <summary>
        /// "Confírmame" (jxh). El servidor lo manda antes de cada turno y se queda esperando el jwz
        /// del cliente; hasta que no llega, el turno no empieza.
        /// </summary>
        /// <param name="deQuien">
        /// Whose id it carries: the fighter whose turn has just ENDED, not the one about to
        /// play. Measured over 77 captures: 1,409 of the 1,471 jxh name the fighter of the jyt
        /// before them, and the rest are the first of a fight -- where there is nobody ending
        /// and it names the first to play -- or the last, with no turn behind. The client
        /// answers with the jti of the closing sequences and then the jwz.
        /// </param>
        private static async Task AskToConfirmAsync(NetworkStream stream, FightInstance fight,
                                                    long deQuien = 0)
        {
            var next = fight.CurrentFighter;
            if (next == null) return;

            // La pregunta va a los dos, para que los dos clientes sepan que empieza un turno. De
            // las dos respuestas, sólo la primera hace el trabajo: ver ConfirmAsync.
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxh,
                Network.FightProtocol.BuildConfirmTurn(deQuien != 0 ? deQuien : next.Id)));
        }

        /// <summary>
        /// El cliente ha confirmado (jwz). Ahora sí se le da el turno al que toca.
        /// </summary>
        /// <summary>
        /// El reloj del turno del jugador, y el candado que evita que escriba encima de nadie.
        ///
        /// El turno no se acababa NUNCA: el cliente pinta la cuenta atrás que le dice el jzc, llega
        /// a cero y sigue restando en negativo, porque del lado del servidor no había quien le
        /// quitara el turno. Temporizador sí había —StartTurnTimer—, pero colgaba de un manejador
        /// que no llama nadie, del combate de la 3.6.4.3, y encima mandaba opcodes que en esta
        /// versión no existen.
        ///
        /// El plazo es el MISMO que viaja en el jzc, no una constante aparte: había una de 300
        /// décimas conviviendo con las 400 que se mandan de verdad, y habría cortado el turno diez
        /// segundos antes de lo que el cliente enseña.
        ///
        /// Lo delicado es que esto escribe en el socket desde otro hilo. NetworkMessage parte cada
        /// trama en DOS escrituras —primero la longitud, luego el cuerpo— y sin candado, así que dos
        /// escritores no se entrelazan mensaje con mensaje, sino la longitud de uno con el cuerpo de
        /// otro, y el cliente pierde la sincronía del flujo para siempre. Por eso el reloj y todo lo
        /// que llega del cliente pasan por el mismo candado: mientras uno escribe su ráfaga, el
        /// otro espera.
        /// </summary>
        /// <summary>
        /// El turno de la sesión que esté atendiendo ahora mismo.
        ///
        /// Se PIDE una vez y se guarda en una variable en cada sitio que lo usa, nunca se llama
        /// dos veces. Si se llamara para pedirlo y otra vez para soltarlo, el segundo podría
        /// resolverse a otra sesión —el contexto es un AsyncLocal— y se soltaría un candado que
        /// no se tiene mientras el propio se queda cerrado para siempre.
        /// </summary>
        private static System.Threading.SemaphoreSlim MiTurno()
            => Network.SessionContext.Current.UnoCadaVez;

        /// <summary>
        /// Parar el reloj de turno de UN combate.
        ///
        /// Era un CancellationTokenSource estático, uno para todo el servidor, así que el segundo
        /// jugador que empezara turno le cancelaba el reloj al primero: sólo el último tenía corte
        /// de turno y a los demás, si se iban del teclado, el combate no les avanzaba nunca. La
        /// pieza correcta ya existía sin usar —FightInstance.TurnTimerCts— y sólo la tocaba código
        /// muerto de la versión anterior.
        /// </summary>
        private static void PararElReloj(FightInstance? fight) => fight?.CancelTurnTimer();

        private static void ArrancarElReloj(NetworkStream stream, FightInstance fight,
                                            Fighter quien, int decimas)
        {
            PararElReloj(fight);

            // Al monstruo no se le pone reloj: juega solo y cede el turno él mismo. A summon a
            // player is playing gets one, like the player: 150 tenths in the captures.
            if ((quien.IsMonster && Dueno(fight, quien) == null) || decimas <= 0) return;

            var reloj = new System.Threading.CancellationTokenSource();
            fight.TurnTimerCts = reloj;

            long deQuien = quien.Id;
            long deQueCombate = fight.FightId;
            int ronda = fight.RoundNumber;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(decimas * 100, reloj.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                var turno = MiTurno();
                await turno.WaitAsync();
                try
                {
                    // Puede haber cambiado todo mientras esperaba: que el combate se acabara, que
                    // el turno ya sea de otro, o que estemos en otra ronda.
                    var ahora = GetCurrentFight();
                    if (ahora == null || ahora.FightId != deQueCombate) return;
                    if (ahora.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;
                    if (ahora.CurrentFighter?.Id != deQuien || fight.RoundNumber != ronda) return;

                    Program.LogDebug($"[Combate] Se le acabó el tiempo a {deQuien}; se le pasa el turno.");
                    await PassTurnAsync(stream);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Combate] El reloj del turno se atragantó: {ex.Message}");
                }
                finally
                {
                    turno.Release();
                }
            });
        }

        public static async Task ConfirmAsync(NetworkStream stream)
        {
            var fight = GetCurrentFight();
            if (fight == null || fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;

            var fighter = fight.CurrentFighter;
            if (fighter == null) return;

            // En un desafio contestan los dos clientes y el trabajo de abrir el turno es uno solo.
            // Al segundo jwz se le deja marchar sin hacer nada: si no, los invocados vencidos se
            // deshacen dos veces y los puntos se devuelven dos veces.
            if (!fight.AtenderElTurnoUnaVez(fight.RoundNumber, fight.CurrentTurnIndex, fighter.Id)) return;

            int duration = fighter.EsInvocado
                ? Network.FightProtocol.SummonTurnDeciseconds
                : fighter.IsMonster
                    ? Network.FightProtocol.MonsterTurnDeciseconds
                    : Network.FightProtocol.PlayerTurnDeciseconds;

            // A turn of his, or of a summon he drives, for the per-turn averages of the end.
            if (!fighter.IsMonster || Dueno(fight, fighter) != null)
            {
                var suyas = StatisticsBehind(fight, fighter);
                if (suyas != null) suyas.TurnsPlayed++;
            }

            // A los dos. Es lo que enciende el reloj del turno, y como salia solo por el socket de
            // quien confirmaba, el otro se quedaba con el combate empezado y sin cuenta atras.
            // And what he kept of the turn he passed (FightProtocol.SavedAfter): in the f4, and on
            // the clock.
            int carried = KeepsTurnTime(fighter) ? fighter.SavedTurnTime : 0;
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jzc,
                Network.FightProtocol.BuildTurnStart(fighter.Id, duration,
                                                     fight.CurrentTurnIndex, fight.RoundNumber, carried)));
            fight.LastAnnouncedTurn = new FightInstance.AnnouncedTurn(
                fighter.Id, fight.CurrentTurnIndex, fight.RoundNumber, duration, DateTime.UtcNow, carried);

            // The portals this turn brings back, right behind the jzc (FightPortals.cs).
            await PortalsAtTurnStartAsync(fight, fighter);

            // A summon whose time is up dies here too, at the first turn of its round -- in
            // the capture the beacon comes out in round 28 and her death arrives at the start
            // of the player's turn in the 30 -- through the waiting row of the 141 her own
            // spell hung on her, in ApplyDuePendingAsync below. It used to be a hand-measured
            // table and a bare jwe 103 outside any sequence, which the 3.6.10.10 client does
            // not apply: the beacon died on the server and stayed on the screen, and her cell
            // could not be aimed at.

            // Delayed one-shot heals, before the ordinary expiry sweep can remove their temporary
            // panel entry.
            //
            // "At the start of their round" would be the tidy sentence and it is not what happens:
            // this runs from ConfirmAsync, which fires at the start of EVERY fighter's turn, and
            // the heal activates on the first of those where round >= EmpiezaEnRonda. So it lands
            // on whichever turn happens to open the round -- which can be an enemy's. It matches
            // how the rest of the delayed effects here already behave, and there is no capture of
            // a delayed heal to say whether Ankama does the same.
            await ApplyDelayedHealingAsync(stream, fight);

            // And the rest of what was waiting for this round: kills, markers, points.
            await ApplyDuePendingAsync(stream, fight);
            if (fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;

            // Se caen los embrujos cumplidos antes de devolver los puntos, para que lo que se
            // devuelva sea lo que de verdad toca esta ronda. Y de cada uno hay que avisar con su
            // jya, que es como el cliente los quita del panel: por su número, uno a uno.
            // TODOS los embrujos, no sólo los del que juega: los que el jugador le puso a un pío
            // se caen igual, y hasta ahora sólo se barría al que le empezaba el turno.
            //
            // But only the rows whose CASTER is the one starting his turn: that is when the real
            // server drops them, not at the first turn of the round. It matters whenever the
            // caster is not first in the order: a "-2 PA" put on a monster that plays before
            // its caster fell at the monster's turn start, before it could cost him a thing.
            // The rows of a summon fall at the summon's turn; those of one that never plays
            // -- a bomb -- at its owner's; and those of a caster no longer in the fight, at
            // anybody's, as before. Measured on the rounds a monster opens: thirteen rows of
            // the player fall at his own turn against one at the monster's, and fifty-seven
            // rows of summons at the summon's.
            var caducados = new List<(Fighter Quien, Jondo.Unity.World.Fights.Buff Caido)>();
            foreach (var quien in TodosLosCombatientes(fight))
            {
                foreach (var caido in quien.Buffs.Barrer(fight.RoundNumber, embrujo => LeTocaCaer(fight, fighter, embrujo)))
                {
                    caducados.Add((quien, caido));
                }
            }

            if (caducados.Count > 0)
            {
                // Y envueltos en su secuencia. Los avisos iban sueltos, a pelo, y el cliente de
                // la 3.6.10.10 sólo aplica lo que le llega dentro de un jto abierto —es lo que
                // acusa luego con su jti—, así que se los estaba comiendo: el embrujo se caía en
                // el servidor y se quedaba pintado para siempre en el panel. Medido sobre las
                // capturas: 5.091 de los 5.098 jya reales van dentro de una secuencia; de los
                // nuestros, ninguno.
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(fighter.Id,
                                                             Network.FightProtocol.ActionSequence)));

                foreach (var (quien, caido) in caducados)
                {
                    // Si el embrujo tocaba el ALCANCE de un hechizo, primero se le retira el
                    // modificador. El hnk es eso: la retirada. No es la declaración que acompaña
                    // al hnd, que es como se implementó primero y por lo que dar alcance no servía
                    // de nada —se ponía y se quitaba en la misma ráfaga—. Medido con reloj sobre
                    // «ocra-disparos lejanos»: al lanzar van 68 hnd y CERO hnk; al caducar van 68
                    // hnk y CERO hnd. Y en «ocra-tiro de repliegue» los 60 hnk viven solos, justo
                    // delante de los 61 jya.
                    // Every modifier of a spell the same way (SpellModifiers): while other rows still
                    // hold it, the new total goes out as an hnd -- Dépouille's base damage steps down
                    // 60, 40, 20 in its capture -- and the last one takes it away with the hnk.
                    if (caido.HechizoAfectado != 0 && Managers.SpellModifiers.OnTheWire(caido.Sobre) is { } alCable)
                    {
                        if (Managers.SpellModifiers.Holds(quien, caido.HechizoAfectado, caido.Sobre, fight.RoundNumber))
                        {
                            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Hnd,
                                Network.FightProtocol.BuildSpellModifier(
                                    quien.Id, alCable.Kind, caido.HechizoAfectado,
                                    Managers.SpellModifiers.Total(quien, caido.HechizoAfectado, caido.Sobre, fight.RoundNumber),
                                    alCable.Action)));
                        }
                        else
                        {
                            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Hnk,
                                Network.FightProtocol.BuildSpellModifierDeclared(
                                    quien.Id, alCable.Kind, caido.HechizoAfectado, alCable.Action)));
                        }
                    }

                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                        Network.FightProtocol.BuildBuffGone(quien.Id, caido.Numero)));

                    if (caido.EffectId == Jondo.Unity.World.Combat.EffectSupport.Visibility)
                        await VisibleOtraVezAsync(fight, quien, quien);

                    if (caido.Apariencia != 0)
                    {
                        await AnnounceAppearanceAsync(stream, quien,
                            quien.Buffs.AparienciaEn(fight.RoundNumber));
                    }

                    // A vitality percentage moved the maximum when it went on; it moves back.
                    if (caido.EffectId is Jondo.Unity.World.Combat.EffectSupport.VitalityFlatMalus
                                         or Jondo.Unity.World.Combat.EffectSupport.VitalityFlatBonus
                        && caido.Caracteristica == VitalityCharacteristicId)
                    {
                        quien.MaxHP = Math.Max(1, quien.MaxHP - caido.Cuanto);
                        if (quien.CurrentHP > quien.MaxHP) quien.CurrentHP = quien.MaxHP;
                    }

                    // A shield row gone means the points are gone: the fighter drops them by the
                    // round, and the sheet says so.
                    if (caido.EffectId == Managers.EffectEngine.ShieldPanelEffect)
                    {
                        quien.CaducarElEscudo(fight.RoundNumber);
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                            Network.FightProtocol.BuildSequenceStart(quien.Id, Network.FightProtocol.SheetSequence)));
                        await FichaATodosAsync(fight, quien.Id,
                            Refresco(quien, Managers.EffectEngine.ShieldCharacteristic, fight.RoundNumber));
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                            Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                                   Network.FightProtocol.SheetSequence)));
                    }

                    // Y AQUÍ ESTABA EL AGUJERO: se borraba la fila del panel y no se devolvía la
                    // característica.
                    //
                    // El motor caduca el embrujo bien —Buffs.Barrer lo saca y ValorDeFicha ya
                    // devuelve el número bueno— pero ese número no salía por el cable, así que el
                    // cliente se quedaba con el último que recibió: +250 de potencia y -3 de
                    // alcance clavados, con el panel de embrujos vacío. Y sobrevivía a los
                    // combates, porque nadie se lo corregía nunca.
                    //
                    // El servidor real manda la ficha detrás de CADA jya. Medido en
                    // «ocra-tiros potentes», tramas #205 a #222, con este mismo hechizo:
                    //     jya 289 -> jxw característica 19 (alcance) de vuelta a cero
                    //     jya 290 -> jxw característica 25 (potencia)
                    //     jya 291 -> jxw característica 84 (daño de empuje)
                    //     jya 292 -> jxw característica 18 (% de crítico)
                    // El patrón se repite en 787 fichas restauradas de las carpetas Ocra y Combate.
                    //
                    // Los PA y los PM no van por aquí: ésos se devuelven como puntos, que es lo
                    // que hace GivePointsBackAsync justo debajo.
                    if (caido.Caracteristica != 0 &&
                        caido.Caracteristica != ActionPointsCharacteristic &&
                        caido.Caracteristica != MovementPointsCharacteristic)
                    {
                        await FichaATodosAsync(fight, quien.Id,
                            Refresco(quien, caido.Caracteristica, fight.RoundNumber));
                    }
                }

                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), fighter.Id,
                                                           Network.FightProtocol.ActionSequence)));

                Program.LogDebug($"[Combate] Se caen {caducados.Count} embrujo(s): " +
                                 string.Join(", ", caducados.ConvertAll(c => $"{c.Caido.Numero} de {c.Quien.Id}")));

                // A state that fell with its row sets off what waits on it going (EOFF<n>), as
                // when a spell takes it away: Protozorror, Tal Kasha and their kind turn on it.
                var yaSalto = new HashSet<(long, int)>();
                foreach (var (quien, caido) in caducados)
                {
                    if (caido.Estado == 0 || caido.EffectId != Jondo.Unity.World.Combat.EffectSupport.AddState) continue;
                    if (quien.Buffs.TieneEstado(caido.Estado) || !yaSalto.Add((quien.Id, caido.Estado))) continue;
                    await DispararAsync(stream, fight, quien, Managers.EffectEngine.AlQuitarseElEstado(caido.Estado));
                }
            }

            fighter.StartTurn(fight.RoundNumber);

            // Where this turn starts, for effect 1099; and no "end the turn" left over from
            // somebody else's -- a monster's 1031 used to end the next player's turn at his
            // first cast.
            fighter.CasillaAlEmpezarTurno = fighter.CellId;
            if (fighter.CasillaAlEmpezarCombate < 0) fighter.CasillaAlEmpezarCombate = fighter.CellId;
            fight.EndTurnRequested = false;

            // And the wall can stop anybody again: the once-only limit is PER TURN, and in the
            // capture it is seen resetting at every jzc.
            fight.WallHitThisTurn.Clear();

            // Lo que hubiera puesto en el suelo bajo sus pies. Va antes de devolverle los puntos
            // porque un glifo que quita PA o PM tiene que morder sobre los del turno que empieza,
            // no sobre los del anterior.
            // Without firing the newborn ones: whoever is starting the turn eats them anyway on
            // the line below, and with the 307 that belongs to them instead of the 306.
            await ReconciliarLosMurosAsync(stream, fight, fireOnBirth: false);
            await QuitarLosGlifosCaducadosAsync(fight, fighter);
            await DispararLosGlifosAsync(stream, fight, fighter, alPisar: false);

            // AND IF IT KILLED HIM, THE TURN STILL HAS TO MOVE ON. This returned bare, and a
            // bare return from here leaves the fight dead in the water: nobody starts the clock,
            // nobody sends "your turn", and for a monster MonsterTurnAsync never runs either, so
            // there is nothing left that could ever end the turn. Measured in the log --
            // "-2 empieza el turno en el glifo 12 [...] 78 de dano [...] -2 se queda sin vida" and
            // then not one more line for a minute, until the player gave up and quit.
            //
            // Same trap as the beacon two hundred lines below: the way out of a turn is
            // PassTurnAsync, and it has to be taken explicitly.
            if (!fighter.IsAlive)
            {
                Program.LogDebug($"[Combate] {fighter.Id} se muere al empezar su turno; " +
                                 $"se pasa el turno.");
                if (!await CheckFightOverAsync(stream, fight)) await PassTurnAsync(stream);
                return;
            }

            await GivePointsBackAsync(stream, fight, fighter);

            // The sheets for expired buffs, and then the ones that give AP/MP back, can leave the
            // client holding an old characteristic 97 -- and the server has given no life back at
            // all. Closing the handover with the authoritative value keeps the bar pinned to
            // CurrentHP.
            await RefrescarLaVidaAsync(stream, fight, fighter, fighter);

            // De donde sale y con cuantos PM: es lo que hace falta para juzgar al acabar los retos
            // de posicion y el de gastar exactamente un PM. Va DESPUES de devolver los puntos.
            ChallengeWatcher.TurnStarted(fight, fighter);
            await ChallengeWatcher.EnemyTurnStartedAsync(stream, fight, fighter);
            await ChallengeWatcher.AllyTurnStartedAsync(stream, fight, fighter);

            // His copies, if any, go before anything else of his turn: jto 6, the switch back
            // to visible, one 1029 each, jwi -- right behind the jzc in the capture.
            await DesvanecerLasIlusionesAsync(stream, fight, fighter);

            // Y ahora las actitudes de "principio de turno": aquí es donde el Dofus Ocre mira si le
            // han pegado desde su turno anterior.
            await ActitudesAsync(stream, fight, fighter, Managers.EffectEngine.AlEmpezarElTurno);
            await EngancheAsync(stream, fight, fighter, Managers.EffectEngine.AlEmpezarElTurno);
            fighter.LeHanPegado = false;

            // A poison can kill here, at the start of its victim's turn -- a JondoBot Sram's
            // Arsénico does. Nothing looked: the dead player was handed his turn and the fight
            // stood until his clock ran out.
            if (!fighter.IsAlive || !fight.SigueVivo(FightInstance.Azules) || !fight.SigueVivo(FightInstance.Rojos))
            {
                Program.LogDebug($"[Combate] {fighter.Id} empieza el turno y alguien cae por lo que salta " +
                                 $"al empezarlo.");
                if (await CheckFightOverAsync(stream, fight)) return;
                // A dream's next wave came in instead: whoever is standing plays on.
                if (!fighter.IsAlive)
                {
                    await PassTurnAsync(stream);
                    return;
                }
            }

            // The two combos the Tymador hands his bombs each turn are no longer dealt here:
            // they are what his class passive does at turn start -- 20488, "La Astucia del
            // Tymador": 20683 for the state that doubles his walls, then 20577 whose two 792
            // climb the ladder on every bomb of his -- and the passive is an attitude of his
            // like any other since ClassPassives. Dealt here on top, every bomb climbed four.

            // El "ya puedes jugar" sólo va si el que juega es de los que maneja este cliente. En el
            // turno de un monstruo ese paso no existe.
            //
            // A summon's turn is its owner's to play: the jyj goes to his socket and to nobody
            // else. Measured on the Osamodas capture, where every jzc of an animal is followed
            // by a jyj and then by the owner's jrw and jwh.
            // A summon with nothing to play -- a beacon -- has no owner to hand the turn to:
            // no jyj, and the turn goes on below.
            var owner = fighter.PlaysOnItsOwn ? null : Dueno(fight, fighter);
            if (!fighter.IsMonster)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jyj,
                    Network.FightProtocol.BuildYourTurn()));
            }
            else if (owner != null)
            {
                await owner.SendAsync(ConnectionProtocol.Push(Op.Jyj,
                    Network.FightProtocol.BuildYourTurn()));
            }

            Program.LogDebug($"[Combate] Turno de {fighter.Id} ({(fighter.IsMonster ? "monstruo" : "jugador")}), " +
                             $"{duration} décimas, puesto {fight.CurrentTurnIndex}.");

            // Y el reloj, con la MISMA duración que se le acaba de decir al cliente.
            ArrancarElReloj(stream, fight, fighter, duration + carried);

            // El monstruo juega solo: no hay nadie que pulse por él. And a summon with nothing
            // to play hands the turn on at once: what its own spell does at turn start has gone
            // out above, and the monsters' wits find no step and no spell -- the beacon's
            // capture, jzc, its cast, jyt, jxh, a quarter of a second. A summon that CAN act is
            // its owner's to play by hand, always; an AI for it would be an option the player
            // switches on, and nothing of that is measured.
            // "Turno cancelado" (140): a live row of it and this turn is lost, handed on at once.
            if (fighter.Buffs.Puestos.Any(b => b.EffectId == Managers.EffectEngine.TurnoCancelado
                                               && b.Vivo(fight.RoundNumber) && !b.Pendiente))
            {
                Program.LogDebug($"[Combate] {fighter.Id} pierde el turno: turno cancelado.");
                await PassTurnAsync(stream);
                return;
            }

            // A monster's summon plays itself, as its summoner does: nobody's client plays it.
            // Handed on at once before, whatever it could do.
            var invocador = fighter.EsInvocado ? fight.Buscar(fighter.Invocador) : null;
            bool deUnMonstruo = invocador is { IsMonster: true } || invocador is { IsBot: true };
            if (fighter.IsBot || (fighter.IsMonster && (!fighter.EsInvocado || fighter.PlaysOnItsOwn || deUnMonstruo)))
            {
                // A Koliseo JondoBot plays like a monster: nobody's client plays it, and neither
                // its summons.
                await MonsterTurnAsync(stream, fight, fighter);
            }
            else if (fighter.EsInvocado && owner != null)
            {
                // Played by its owner, from his client: the clock is running and his jrw, jwh
                // and jxy come in as for himself. Nothing to do here until they do.
            }
            else if (fighter.EsInvocado)
            {
                // Y una baliza cede el turno EN EL ACTO: lo suyo ya lo ha hecho su hechizo en las
                // actitudes de principio de turno y no tiene nada más que jugar.
                //
                // Aquí se colgaba el combate. Esto llamaba a EndTurnAsync, que es la generación
                // VIEJA de paquetes —jwk, jwu, juu— y el cliente de la 3.6.10.10 no la entiende:
                // el turno de la baliza no acababa nunca, el jugador tampoco podía pasarlo y no
                // quedaba más que abandonar la pelea. El paso de turno bueno es PassTurnAsync, el
                // mismo que usan los monstruos.
                await PassTurnAsync(stream);
            }
        }

        /// <summary>
        /// Announces delayed heals activated by the effect engine and removes their temporary buff
        /// entry from the client. The engine has already capped and applied HP before this method
        /// writes any packet.
        /// </summary>
        private static async Task ApplyDelayedHealingAsync(NetworkStream stream, FightInstance fight)
        {
            var due = Managers.EffectEngine.ActivateDelayedHealing(fight, fight.RoundNumber);
            if (due.Count == 0) return;

            long sequenceOwner = fight.CurrentFighter?.Id ?? 0;
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(
                    sequenceOwner, Network.FightProtocol.ActionSequence)));

            foreach (var result in due)
            {
                var caster = fight.Buscar(result.CasterId);
                long sourceId = caster?.Id ?? result.CasterId;

                if (result.Healed > 0)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildHeal(sourceId, result.Healed, result.Target.Id)));
                    AnotarLaCura(fight, caster, result.Target, result.Healed);
                    if (caster != null)
                    {
                        await ChallengeWatcher.HealedAsync(stream, fight, caster, result.Target);
                    }
                    await RefrescarLaVidaAsync(stream, fight, result.Target, caster);
                }

                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                    Network.FightProtocol.BuildBuffGone(result.Target.Id, result.Buff.Numero)));
                Program.LogDebug($"[Fight] Delayed heal from {sourceId} to {result.Target.Id}: " +
                                 $"{result.Healed} HP at round {fight.RoundNumber}.");
            }

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(
                    fight.SiguienteAccion(), sequenceOwner, Network.FightProtocol.ActionSequence)));
        }

        /// <summary>
        /// The waiting rows whose round has come, at the first turn of that round: the kill of a
        /// beacon goes out as a death, the script marker as its jwe, a +1 MP as a live row that
        /// names the waiting one, and then every waiting row falls with its jya. The order and
        /// the frames are those of the Baliza de Supervivencia and Paso de Cacería captures.
        /// </summary>
        private static async Task ApplyDuePendingAsync(NetworkStream stream, FightInstance fight)
        {
            var due = Managers.EffectEngine.ActivateDuePending(fight, fight.RoundNumber);
            if (due.Count == 0) return;

            long sequenceOwner = fight.CurrentFighter?.Id ?? 0;
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(sequenceOwner, Network.FightProtocol.ActionSequence)));

            foreach (var result in due)
            {
                var caster = fight.Buscar(result.CasterId) ?? result.Target;
                var waiting = result.Waiting;

                if (result.Kills)
                {
                    if (!result.Target.IsAlive) continue;
                    Program.LogDebug($"[Combate] Se cumple el plazo: {caster.Id} fulmina a {result.Target.Id} " +
                                     $"con el hechizo {waiting.HechizoOrigen} en la ronda {fight.RoundNumber}.");
                    await UnGolpeAsync(stream, fight, caster, waiting.HechizoOrigen,
                                       new Managers.SpellEffect { EffectId = waiting.EffectId, EffectUid = waiting.EffectUid },
                                       0, result.Target, 0, 0, false, 0, fulmina: true);
                }
                else if (result.Quitados != null)
                {
                    foreach (var quitado in result.Quitados)
                    {
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                            Network.FightProtocol.BuildBuffGone(result.Target.Id, quitado.Numero)));
                    }
                }
                else if (result.Casts)
                {
                    if (!result.Target.IsAlive) continue;
                    Program.LogDebug($"[Combate] Se cumple el plazo: {caster.Id} lanza {waiting.Dado} " +
                                     $"(grado {Math.Max(1, waiting.Cara)}) sobre {result.Target.Id}, del hechizo " +
                                     $"{waiting.HechizoOrigen}, en la ronda {fight.RoundNumber}.");
                    if (Managers.PlayerSpells.Contains(waiting.Dado))
                    {
                        await AplicarEfectosAsync(stream, fight, caster, waiting.Dado, Math.Max(1, waiting.Cara),
                                                  result.Target, Managers.EffectEngine.AlLanzar, result.Target.CellId);
                    }
                    else
                    {
                        await LanzarPorOrdenAsync(stream, fight, caster, waiting.Dado, Math.Max(1, waiting.Cara),
                                                  result.Target, result.Target.CellId, Managers.EffectEngine.AlLanzar,
                                                  Managers.EffectEngine.EfectosSorteados(waiting.Dado, Math.Max(1, waiting.Cara), false));
                    }
                }
                else if (result.Marks)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildScriptMarker(caster.Id, waiting.NivelOrigen, result.Target.CellId,
                                                                waiting.HechizoOrigen, waiting.Valor)));
                }
                else if (result.Live != null)
                {
                    var (categoria, boost) = DatabaseManager.EffectFamily(waiting.EffectId);
                    int familia = Network.FightProtocol.FamiliaDelEmbrujo(waiting.EffectId, categoria, boost);
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxm,
                        Network.FightProtocol.BuildBuff(
                            result.Target.Id, caster.Id, result.Live.Numero, waiting.EffectId, waiting.EffectUid,
                            waiting.Valor, waiting.Dado, waiting.Cara, waiting.HechizoOrigen,
                            Managers.EffectEngine.Esperando, result.Live.CaducaEnRonda, waiting.Dispellable,
                            familia, waiting.NivelOrigen, critico: waiting.Critico, padre: waiting.Numero)));
                    Program.LogDebug($"[Combate] Se cumple el plazo del embrujo {waiting.Numero} sobre {result.Target.Id}: " +
                                     $"efecto {waiting.EffectId}" +
                                     (waiting.Caracteristica != 0 ? $", característica {waiting.Caracteristica} {waiting.Cuanto:+#;-#;0}" : "") +
                                     $" como el {result.Live.Numero}, hasta la ronda {result.Live.CaducaEnRonda}.");
                }
            }

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), sequenceOwner,
                                                       Network.FightProtocol.ActionSequence)));

            // And the waiting rows fall, in their own sequence, whether what they waited for
            // happened or not: the beacon's 141 falls after her death in the capture.
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(sequenceOwner, Network.FightProtocol.TurnStartSequence)));
            foreach (var result in due)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                    Network.FightProtocol.BuildBuffGone(result.Target.Id, result.Waiting.Numero)));
            }
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), sequenceOwner,
                                                       Network.FightProtocol.TurnStartSequence)));
        }

        /// <summary>
        /// Whether an expired row falls at THIS turn start: at its caster's, at its owner's when
        /// the caster is a summon that never plays, and at anybody's when the caster is gone
        /// or nobody in particular. See the sweep in ConfirmAsync for the measurement.
        /// </summary>
        private static bool LeTocaCaer(FightInstance fight, Fighter quienEmpieza, Jondo.Unity.World.Fights.Buff embrujo)
        {
            if (embrujo.Quien == 0 || embrujo.Quien == quienEmpieza.Id) return true;
            var quienLoPuso = fight.Buscar(embrujo.Quien);
            if (quienLoPuso == null || !quienLoPuso.IsAlive) return true;
            if (quienLoPuso.EsInvocado && !quienLoPuso.JuegaTurno) return quienLoPuso.Invocador == quienEmpieza.Id;
            return false;
        }

        /// <summary>
        /// Los puntos de vuelta al empezar el turno.
        ///
        /// Sin esto, el que gastaba sus puntos se quedaba a cero para siempre: el servidor sí se
        /// los devolvía por dentro (Fighter.StartTurn) pero no se lo decía a nadie, y el cliente
        /// seguía pintando lo último que le llegó.
        ///
        /// Va envuelto como en la captura: un jto del 7 con las dos fichas dentro, primero los
        /// puntos de movimiento y luego los de acción, cada una en su jto/jwi del 3.
        ///
        /// Lo que NO se copia de la captura es el contenido. Allí las dos fichas van con el hueco
        /// vacío, porque ese servidor manda en ese bloque el MODIFICADOR del turno y vaciarlo
        /// equivale a "ya no le falta nada". Este emulador codifica otra cosa: mete el valor
        /// ABSOLUTO (ver BuildFighterSheet, que con cero escribe un f2 vacío y con número lo mete
        /// en f5). Copiando el hueco vacío tal cual, el cliente entendía cero y el turno empezaba
        /// con 0 PA y 0 PM. Así que aquí van los máximos, que es lo que esta codificación quiere
        /// decir.
        /// </summary>
        private static async Task GivePointsBackAsync(NetworkStream stream, FightInstance fight, Fighter fighter)
        {
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(fighter.Id,
                                                         Network.FightProtocol.TurnSequence)));

            foreach (var (characteristic, value) in new[]
                     {
                         (MovementPointsCharacteristic, (long)fighter.CurrentMP),
                         (ActionPointsCharacteristic, (long)fighter.CurrentAP),
                     })
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(fighter.Id,
                                                             Network.FightProtocol.SheetSequence)));
                await FichaATodosAsync(fight, fighter.Id, (characteristic, value, 0L, 0L));
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), fighter.Id,
                                                           Network.FightProtocol.SheetSequence)));
            }

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), fighter.Id,
                                                       Network.FightProtocol.TurnSequence)));
        }

        /// <summary>
        /// Walking in a fight (jrw).
        ///
        ///   server  jto   opens the walk sequence
        ///           jsj   the whole path, the way its last step faces, and who moves
        ///           jxw   the MP left, inside its own jto 3 / jwi 3
        ///           jwe   f14 129, the steps spent, negative
        ///           jwi   closes it, and the client acknowledges with a jti
        ///
        /// with a jwe 104 and its losses in front of any stretch that leaves an enemy's contact:
        /// see <see cref="WalkPathAsync"/>. The client sends only the corners of the path; the
        /// jsj gives back every cell, which is what the client animates.
        /// </summary>
        public static async Task WalkAsync(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null || fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;

            var walker = fight.CurrentFighter;
            if (walker == null || !walker.ControlledBy(GameState.CharacterId)) return;

            var (_, corners, facing) = Network.FightProtocol.ReadMove(payload);
            if (corners.Count < 2) return;

            int destination = corners[corners.Count - 1];

            // En combate manda la lista de casillas de la ARENA, no la de paseo: el anillo de
            // fuera de un mapa de combate no se pisa aunque en el mapa normal sí se pise.
            var pisables = MapManager.GetFightWalkable(fight.MapId);
            if (pisables != null && !pisables.Contains(destination)) return;
            if (pisables == null && !MapManager.IsCellWalkable(fight.MapId, destination)) return;

            // Quién está por medio, para no atravesarlo ni acabar encima.
            var ocupadas = new HashSet<int>();
            foreach (var otro in fight.Azul) if (otro.IsAlive && otro != walker) ocupadas.Add(otro.CellId);
            foreach (var otro in fight.Rojo) if (otro.IsAlive && otro != walker) ocupadas.Add(otro.CellId);
            if (ocupadas.Contains(destination)) return;

            // El camino ENTERO, casilla a casilla.
            //
            // Aquí estaba lo de los puntos de movimiento infinitos. El cliente no manda el camino:
            // manda sólo los VÉRTICES, uno por cada cambio de dirección. Andar diez casillas en
            // línea recta son dos vértices, y aquí se cobraba «vértices menos uno», o sea UN punto
            // por diez casillas. Cruzarse el mapa costaba lo que costara girar.
            var enteras = new List<int>();
            for (int i = 0; i < corners.Count; i++) enteras.Add((int)corners[i]);
            var camino = Jondo.Unity.World.Maps.MapGeometry.ExpandPath(enteras, pisables, ocupadas);
            if (camino.Count < 2) return;

            int steps = camino.Count - 1;
            if (steps > walker.CurrentMP) return;

            // The walk itself, tackles paid on the way: see FightTackle.cs. What comes back is
            // what was really walked, shorter than asked when a tackle leaves too few MP.
            var walked = await WalkPathAsync(fight, walker, camino, facing, stream);
            steps = walked.Count - 1;
            camino = walked;
            destination = walker.CellId;

            Program.LogDebug($"[Fight] Walks to cell {destination}: {steps} step(s), " +
                             $"{walker.CurrentMP} MP and {walker.CurrentAP} AP left.");

            // Y lo que reaccione a andar, UNA VEZ POR CASILLA. El Centinela se come uno de alcance
            // y un dos por ciento de daños a distancia en cada paso, no en cada movimiento: andar
            // tres casillas de golpe cuesta tres, no uno.
            for (int paso = 0; paso < steps; paso++)
            {
                await EngancheAsync(stream, fight, walker, Managers.EffectEngine.AlAndar);
                await EngancheAsync(stream, fight, walker, Managers.EffectEngine.AlUsarUnPM);
            }

            // Y lo que hubiera puesto en el suelo donde ha ido a parar. Va DESPUÉS de andar y de
            // los enganches: primero llega, y ya en su casilla nueva le salta lo que hubiera.
            //
            // The wall goes on its own because it charges PER CELL, not per move: the whole path
            // is walked again and every cell of it that belongs to a wall is charged.
            await WalkThroughTheWallsAsync(stream, fight, walker, camino);
            if (!walker.IsAlive)
            {
                // Walking into your own wall can now kill you, so this is a real way for a fight
                // to end, and it was ending nowhere: the check only ran after a cast, after a
                // monster turn and on quitting.
                await CheckFightOverAsync(stream, fight);
                return;
            }

            await ReconciliarLosMurosAsync(stream, fight);
            await DispararLosGlifosAsync(stream, fight, walker, alPisar: true, skipWalls: true);
            if (!walker.IsAlive) await CheckFightOverAsync(stream, fight);
        }

        /// <summary>
        /// Dispara lo que hay puesto en el suelo bajo un combatiente.
        /// </summary>
        /// <remarks>
        /// Un solo camino para las cuatro familias —glifo de aura, glifo de inicio de turno,
        /// trampa y runa—, porque lo único que las distingue es el momento, y el momento es este
        /// parámetro. Lo que hacen es siempre lo mismo: lanzar el hechizo que llevan dentro, con
        /// su grado, a nombre de quien lo puso.
        ///
        /// La trampa se gasta al dispararse; el glifo se queda hasta que caduque. Y se barre al
        /// final, no dentro del recorrido, porque disparar un glifo puede mover al que lo pisó y
        /// dejarlo encima de otro.
        /// </remarks>
        /// <summary>
        /// The glyphs whose time is up at this turn start go, each with its jwe 310: the ones of
        /// the fighter starting it once their round has come -- or of the summon of his that never
        /// plays -- and those of a caster who is gone. Not the bomb walls, which are the bombs'
        /// business (ReconciliarLosMurosAsync).
        /// </summary>
        private static async Task QuitarLosGlifosCaducadosAsync(FightInstance fight, Fighter quienEmpieza)
        {
            bool EsSuTiempo(Jondo.Unity.World.Fights.Glifo g)
            {
                if (g.Dueno == quienEmpieza.Id) return true;
                var dueno = fight.Buscar(g.Dueno);
                return dueno != null && dueno.EsInvocado && !dueno.JuegaTurno && dueno.Invocador == quienEmpieza.Id;
            }
            bool SinDueno(Jondo.Unity.World.Fights.Glifo g)
                => !Managers.BombWalls.IsWall(g) && fight.Buscar(g.Dueno) is not { IsAlive: true };

            var caidos = fight.QuitarLosGlifosCaducados(g => !Managers.BombWalls.IsWall(g) && EsSuTiempo(g), SinDueno);
            foreach (var caido in caidos)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildGlyphGone(caido.Dueno, caido.Id)));
                Program.LogDebug($"[Combate] Se cae el glifo {caido.Id} de {caido.Dueno} al empezar el turno de {quienEmpieza.Id}.");
            }
        }

        /// <summary>The aura glyph effect: given while one stands in it.</summary>
        private const int GlifoDeAura = 1091;

        /// <summary>Whether a glyph is a monster's aura, whose gifts go with the one who leaves it.</summary>
        private static bool EsAuraDeMonstruo(Jondo.Unity.World.Fights.Glifo glifo)
            => glifo.Tipo == GlifoDeAura && !Managers.PlayerSpells.Contains(glifo.HechizoQueLoPuso);

        /// <summary>
        /// What a monster's aura gave a fighter goes when he is no longer on it: the rows of the
        /// aura's spell, and the states they held -- with their EOFF.
        /// </summary>
        private static async Task SalirDeLasAurasAsync(NetworkStream stream, FightInstance fight, Fighter quien)
        {
            foreach (var aura in fight.Glifos.Where(g => EsAuraDeMonstruo(g) && g.Dentro.Contains(quien.Id)
                                                         && !g.Cubre(quien.CellId)).ToList())
            {
                aura.Dentro.Remove(quien.Id);
                var quitados = quien.Buffs.QuitarDelHechizo(aura.Hechizo);
                foreach (var quitado in quitados)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                        Network.FightProtocol.BuildBuffGone(quien.Id, quitado.Numero)));
                }
                foreach (int estado in quitados.Where(q => q.Estado != 0 && q.EffectId == Jondo.Unity.World.Combat.EffectSupport.AddState)
                                               .Select(q => q.Estado).Distinct())
                {
                    if (!quien.Buffs.TieneEstado(estado))
                        await DispararAsync(stream, fight, quien, Managers.EffectEngine.AlQuitarseElEstado(estado));
                }
                Program.LogDebug($"[Combate] {quien.Id} sale del aura {aura.Id}: se le quitan {quitados.Count} embrujo(s).");
            }
        }

        private static async Task DispararLosGlifosAsync(NetworkStream stream, FightInstance fight,
                                                         Fighter quien, bool alPisar,
                                                         bool byDisplacement = false,
                                                         bool skipWalls = false)
        {
            if (quien == null || !quien.IsAlive || fight.Glifos.Count == 0) return;

            // A monster's aura gives while one stands in it: whoever has walked, been pushed or
            // been thrown out of it loses what it gave -- the Globiluz's light on Sombra's
            // Silueta, Tanukui's geoglyph on himself.
            if (alPisar) await SalirDeLasAurasAsync(stream, fight, quien);

            var saltan = alPisar ? fight.LosQuePisa(quien.CellId) : fight.LosQueEmpiezan(quien.CellId);
            if (saltan.Count == 0) return;

            foreach (var glifo in saltan)
            {
                // The walls have already charged cell by cell along the path, which is how they
                // charge. Firing them again here would charge the last cell twice.
                if (skipWalls && Managers.BombWalls.IsWall(glifo)) continue;
                if (!GlyphCatches(fight, glifo, quien, byDisplacement)) continue;

                await FireOneGlyphAsync(stream, fight, glifo, quien, alPisar);
                if (!quien.IsAlive) break;
            }

            var caidos = fight.BarrerLosGlifos();
            foreach (var caido in caidos)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildGlyphGone(caido.Dueno, caido.Id)));
            }
            if (caidos.Count > 0)
            {
                Program.LogDebug($"[Combate] Se llevan por delante {caidos.Count} glifo(s).");
            }
        }

        /// <summary>
        /// Whether this glyph goes off under this fighter.
        /// </summary>
        /// <remarks>
        /// The general rule is that your own does not catch you: it is what keeps a Feca from
        /// burning himself on his own glyph walking over it, and what makes dropping one under an
        /// enemy worth doing instead of being suicide.
        ///
        /// A BOMB WALL DOES NOT WORK LIKE THAT. Whose it is means nothing there -- the class sheet
        /// calls the victim "una entidad", and Kabum exists precisely to shield "al lanzador y a
        /// sus aliados" from it -- so the wall keeps its own three rules, which live next to the
        /// walls themselves in <see cref="Managers.BombWalls.Catches"/>.
        /// </remarks>
        public static bool GlyphCatches(FightInstance fight, Jondo.Unity.World.Fights.Glifo glifo,
                                        Fighter quien, bool byDisplacement)
        {
            if (Managers.BombWalls.IsWall(glifo))
                return Managers.BombWalls.Catches(fight, glifo, quien, byDisplacement);

            // Its owner too, when its mask reaches his side or him -- "a", "c", "C": Cil's glyphs are
            // written for him to stand on. The bomb walls, with no mask, never catch the Rogue.
            if (glifo.Dueno != quien.Id) return true;
            foreach (var trozo in (glifo.Mascara ?? "").Split(','))
            {
                string t = trozo.Trim();
                if (t == "a" || t == "c" || t == "C") return true;
            }
            return false;
        }

        /// <summary>
        /// Fires ONE glyph on ONE fighter: tells the client, hits, and applies the rest.
        /// </summary>
        /// <remarks>
        /// Down the same road as any other cast, AND THERE ARE TWO OF THEM. Only
        /// AplicarEfectosAsync used to be called here, and that half does not hit: the damage of a
        /// root cast is applied by HurtAsync, and the effect engine does not even produce an
        /// outcome for it -- measured on the Muro de Fuego, six damage-99 effects and ZERO
        /// outcomes. So a glyph that should hurt did nothing at all, neither the bomb wall nor a
        /// trap nor the Feca glyph.
        ///
        /// AND LET IT SHOW, which is not announcing the cast: the real server does not announce
        /// it. The four wall spells appear 143 times in the Rogue captures and all 143 sit inside
        /// a jwe f14 = 401; not one inside an f14 = 300. What it sends is "this fighter was caught
        /// by that glyph" -- 306 on entering, 307 on starting the turn on it -- and the blow right
        /// behind, both inside a sequence opened IN THE NAME OF WHOEVER STEPPED ON IT. Without
        /// that notice the client got the damage on its own and drew none of it.
        /// </remarks>
        private static async Task FireOneGlyphAsync(NetworkStream stream, FightInstance fight,
                                                    Jondo.Unity.World.Fights.Glifo glifo,
                                                    Fighter quien, bool alPisar, int celda = -1)
        {
            var dueno = fight.Buscar(glifo.Dueno) ?? quien;

            // A trap goes off at ITS CENTRE, wherever in it it was stepped on: the 306 of the
            // Sram captures names the cell the trap was aimed at -- Deriva laid on 217 over five
            // cells, "306 f1 217" -- and its spell is thrown from there, which is what gives its
            // push a direction. Aimed at the cell of whoever stepped in, a push had none and
            // moved nobody, and the zone missed the enemies around the centre.
            if (celda < 0)
                celda = glifo.Tipo == Managers.EffectEngine.ColocaUnaTrampa && glifo.Centro >= 0 ? glifo.Centro : quien.CellId;

            // And it is spent the moment it goes off, before anything it does: its own push can
            // put the victim back on one of its cells, and it went off twice. In the captures
            // the 310 that takes it away comes right behind the 306, ahead of the blow.
            if (glifo.SeGastaAlDispararse) glifo.Gastado = true;

            // What the sheet calls "haber sufrido los efectos del muro durante su turno": it is
            // written down here, and only a displacement ever reads it.
            if (Managers.BombWalls.IsWall(glifo)) fight.WallHitThisTurn.Add(quien.Id);

            Program.LogDebug($"[Combate] {quien.Id} {(alPisar ? "pisa" : "empieza el turno en")} " +
                             $"el glifo {glifo.Id} de {glifo.Dueno}: lanza el hechizo " +
                             $"{glifo.Hechizo} grado {glifo.Grado}.");

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(quien.Id,
                                                         Network.FightProtocol.GlyphSequence)));

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildGlyphTriggered(dueno.Id, glifo.Id, celda,
                                                          quien.Id, walkedIn: alPisar)));

            // Whatever it does counts as glyph damage on its owner's end screen, and a trap's
            // blows are a trap's (DT).
            var antes = fight.CurrentDamageSource;
            var tipoAntes = fight.CurrentGlyphType;
            fight.CurrentDamageSource = Jondo.Unity.World.Fights.DamageSource.Glyph;
            fight.CurrentGlyphType = glifo.Tipo;
            try
            {
                var tirada = Managers.EffectEngine.EfectosSorteados(glifo.Hechizo, glifo.Grado, false);
                await HurtAsync(stream, fight, dueno, glifo.Hechizo, glifo.Grado, quien,
                                celdaApuntada: celda, tirada: tirada);

                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                           Network.FightProtocol.GlyphSequence)));
                if (!quien.IsAlive) return;

                await AplicarEfectosAsync(stream, fight, dueno, glifo.Hechizo, glifo.Grado,
                                          quien, Managers.EffectEngine.AlLanzar,
                                          celdaApuntada: celda, tirada: tirada);
            }
            finally
            {
                fight.CurrentDamageSource = antes;
                fight.CurrentGlyphType = tipoAntes;
            }

            if (glifo.SeGastaAlDispararse) glifo.Gastado = true;
            if (EsAuraDeMonstruo(glifo) && quien.IsAlive) glifo.Dentro.Add(quien.Id);
        }

        /// <summary>
        /// One hit per MP spent inside a wall, cell by cell along the path just walked.
        /// </summary>
        /// <remarks>
        /// The class sheet is explicit, and it is the one limit walking does not share with being
        /// pushed: "No obstante, caminar en el muro no se ve afectado por este limite de una vez
        /// por turno, por lo que esto inflige danos POR CADA PM que esta entidad consuma en el
        /// muro." Walking three cells of a wall is three hits, not one.
        ///
        /// The rule is Ankama own text; THE SHAPE IS INFERENCE and worth saying so. In the class
        /// captures nobody ever walks through a bomb wall -- every one of the 246 wall triggers is
        /// either a 307 at turn start or a 306 from the wall being raised or from a displacement --
        /// so there is no measurement of what a multi-cell walk looks like on the wire. What goes
        /// out here is one 306 plus its blow per crossed cell, each naming the cell it crossed,
        /// which is the same shape as the single one that IS measured.
        /// </remarks>
        private static async Task WalkThroughTheWallsAsync(NetworkStream stream, FightInstance fight,
                                                           Fighter walker, IReadOnlyList<int> camino)
        {
            if (walker == null || camino == null || camino.Count < 2) return;

            for (int paso = 1; paso < camino.Count; paso++)
            {
                if (!walker.IsAlive) return;

                foreach (var muro in fight.Glifos
                             .Where(g => Managers.BombWalls.IsWall(g) && g.Cubre(camino[paso]))
                             .ToList())
                {
                    if (!GlyphCatches(fight, muro, walker, byDisplacement: false)) continue;

                    await FireOneGlyphAsync(stream, fight, muro, walker, alPisar: true,
                                            celda: camino[paso]);
                    if (!walker.IsAlive) return;
                }
            }
        }

        /// <summary>
        /// Los muros de bombas: lo que hay entre dos bombas alineadas del mismo tymador.
        /// </summary>
        /// <remarks>
        /// Va por el mismo camino que los glifos y en los mismos dos momentos —al pisar y al
        /// empezar el turno encima— porque es lo que la ficha de clase dice que es: «Se trata de
        /// un glifo en el suelo que no bloquea los desplazamientos ni las líneas de visión. Una
        /// entidad que se desplace en el muro o entre en él sufrirá daños, incluso si empieza su
        /// turno en el interior».
        ///
        /// Pero NO es un <c>Glifo</c> guardado: un muro es una función de dónde están las bombas,
        /// así que se calcula al vuelo. Una bomba que muere se lleva su muro sin que nadie tenga
        /// que acordarse de borrarlo, y una que empujen hasta la línea lo levanta en el acto.
        ///
        /// Lo lanza la bomba con MÁS combo de las que sostienen el muro. Eso es inferencia: la
        /// ficha dice que el muro se beneficia de la mitad del combo, pero no dice de cuál cuando
        /// las bombas van a distinto nivel.
        /// </remarks>
        private static async Task ReconciliarLosMurosAsync(NetworkStream stream, FightInstance fight,
                                                           bool fireOnBirth = true)
        {
            var justBorn = new List<Jondo.Unity.World.Fights.Glifo>();
            // Lo que TENDRÍA que haber en el suelo ahora mismo, casilla a casilla.
            var toca = new Dictionary<int, (Fighter Dueno, int Hechizo)>();
            foreach (var dueno in TodosLosCombatientes(fight).ToList())
            {
                if (dueno == null || !dueno.IsAlive || dueno.IsMonster || dueno.EsInvocado) continue;

                foreach (var muro in Managers.BombWalls.Of(TodosLosCombatientes(fight), dueno))
                {
                    if (!Managers.BombWalls.WallSpell.TryGetValue(muro.Template, out int hechizo))
                        continue;
                    foreach (int casilla in muro.Cells) toca[casilla] = (dueno, hechizo);
                }
            }

            // Lo que hay puesto de muros. Se reconocen por su hechizo: ningún glifo de otra cosa
            // lanza uno de los cuatro.
            var puestos = fight.Glifos
                .Where(g => Managers.BombWalls.WallSpell.Values.Contains(g.Hechizo))
                .ToList();

            foreach (var sobra in puestos.Where(g => !g.Casillas.Any(toca.ContainsKey)).ToList())
            {
                fight.Glifos.Remove(sobra);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildGlyphGone(sobra.Dueno, sobra.Id)));
                Program.LogDebug($"[Muro] Se cae el glifo {sobra.Id} de {sobra.Dueno}.");
            }

            var yaCubiertas = puestos.Where(g => fight.Glifos.Contains(g))
                                     .SelectMany(g => g.Casillas)
                                     .ToHashSet();

            foreach (var (casilla, quien) in toca)
            {
                if (yaCubiertas.Contains(casilla)) continue;

                var glifo = fight.Poner(new Jondo.Unity.World.Fights.Glifo(
                    quien.Dueno.Id, new[] { casilla }, quien.Hechizo, MuroGrado,
                    Network.FightProtocol.GlyphRed, caducaEnRonda: 0, mascara: "",
                    cuando: Jondo.Unity.World.Fights.Disparo.AlPisarYAlEmpezar));

                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildGlyph(quien.Dueno.Id, glifo.Id, casilla,
                                                     quien.Hechizo, MuroGrado, size: 2,
                                                     colour: Network.FightProtocol.GlyphRed)));

                Program.LogDebug($"[Muro] Glifo {glifo.Id} de {quien.Dueno.Id} en la casilla " +
                                 $"{casilla} con el hechizo {quien.Hechizo}.");
                justBorn.Add(glifo);
            }

            // AND WHOEVER WAS ALREADY STANDING THERE GETS HIT ON THE SPOT. "Una entidad que se
            // desplace en el muro O ENTRE EN EL sufrira danos", says the sheet, and having a wall
            // raised under your feet is entering it without moving. Measured in "glifo de bombas
            // sismobomba": frames 105 to 109 lay the five cells of the wall down and frame 110 is
            // a jwe f14 = 306 on -4, who was standing on one; 112 is its death. Same at 438-445.
            //
            // Nothing happened here: the wall got painted and sat waiting for somebody to walk.
            // That was the only one of the four cases that worked.
            if (!fireOnBirth) return;

            foreach (var born in justBorn)
            {
                if (!fight.Glifos.Contains(born)) continue;

                foreach (var standing in TodosLosCombatientes(fight).ToList())
                {
                    if (standing == null || !standing.IsAlive) continue;
                    if (!born.Cubre(standing.CellId)) continue;
                    if (!GlyphCatches(fight, born, standing, byDisplacement: false)) continue;

                    await FireOneGlyphAsync(stream, fight, born, standing, alPisar: true);
                }
            }
        }

        /// <summary>
        /// El grado con el que pega un muro. Los cuatro hechizos de muro tienen tres, y no hay
        /// captura que diga cuál usa el servidor real, así que va el más alto y queda dicho.
        /// </summary>
        private const int MuroGrado = 3;

        /// <summary>
        /// Lanzar un hechizo (jwh).
        ///
        /// El orden es el de la captura del poutch de nivel 50, y no es el que había:
        ///
        ///   servidor jto   abre
        ///            jwe   f14 300, qué se lanza y a dónde
        ///            jto   abre una secuencia del 3 sólo para la ficha
        ///            jxw   los puntos de acción que le quedan al que lanza
        ///            jwi   la cierra
        ///            jwe   f14 102, los puntos de acción gastados
        ///            jwe   f14 89..100 por cada uno que recibe daño
        ///            jwe   f14 103 por cada uno que se queda sin vida, AL FINAL
        ///            jwi   cierra
        ///
        /// Lo que NO va: una ficha (jxw) con la vida del que recibe el golpe. El servidor real no
        /// la manda —la vida la descuenta el cliente del propio golpe— y mandarla la aplicaba en
        /// el acto: el bicho caía muerto antes de que se viera ni el hechizo ni el daño.
        ///
        /// Si el jwh no trae hechizo es un golpe de arma, y entonces el tipo del primer jwe es 303.
        /// </summary>
        public static async Task CastAsync(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null || fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;

            var caster = fight.CurrentFighter;
            if (caster == null || !caster.ControlledBy(GameState.CharacterId)) return;

            var (cell, spell) = Network.FightProtocol.ReadCast(payload);

            // Si no viene por casilla, viene POR EL CARRUSEL: el cliente deja apuntar pulsando la
            // ficha de un combatiente en vez de su casilla del tablero, y entonces manda otro
            // mensaje con su identificador. Es la unica forma comoda de echarse un embrujo a uno
            // mismo, y el emulador ni siquiera lo escuchaba: se caia en el cajon de paquetes sin
            // atender. Se resuelve a casilla y sigue por el mismo camino que el otro.
            if (cell == 0)
            {
                var (senalado, hechizo) = Network.FightProtocol.ReadCastAtFighter(payload);
                if (senalado == 0) return;

                Fighter apuntado = null;
                foreach (var uno in TodosLosCombatientes(fight))
                {
                    if (uno.Id == senalado && uno.IsAlive) { apuntado = uno; break; }
                }
                if (apuntado == null)
                {
                    Program.LogDebug($"[Combate] El carrusel apunta a {senalado}, que no esta en el combate.");
                    return;
                }
                cell = apuntado.CellId;
                spell = hechizo;
            }
            if (cell == 0) return;

            // A summon casts at the grade its template opens, which the level lookup cannot
            // give: monster spells have no player level and would all resolve to the top grade.
            // A summon casts its own spells, never its owner's. With the client and the fight out
            // of step -- the owner's client still on his turn, the fight on his summon's -- his
            // Invocación de Chaferloko came out of his Arakna at grade 0 and summoned for her.
            if (caster.EsInvocado && spell != 0 && (caster.HechizosDeInvocado == null || !caster.HechizosDeInvocado.Any(h => h.Spell == spell)))
            {
                Program.LogDebug($"[Combate] {caster.Id} no tiene el hechizo {spell}; no se lanza.");
                return;
            }

            var limites = LimitesDelQueLanza(caster, spell);
            int cost = limites.Cost, spellLevel = limites.LevelId, grade = limites.Grade;

            // EL ALCANCE, que no se comprobaba en ninguna parte del camino vivo: se podía lanzar
            // cualquier cosa a cualquier distancia. Por eso los embrujos que dan alcance parecían
            // no hacer nada — no es que no se sumaran, es que no había límite que ampliar.
            //
            // Suman dos cosas: la característica 19, que es el alcance a secas, y los ajustes que
            // apuntan a ESTE hechizo en concreto, que es lo que hacen Disparos Lejanos.
            //
            // Si el hechizo no trae alcance máximo en la base, no se comprueba nada: un dato que
            // falta no debe impedir lanzar.
            if (limites.AlcanceMaximo > 0)
            {
                int lejos = Jondo.Unity.World.Maps.MapGeometry.Distance(caster.CellId, cell);

                int minimo = limites.AlcanceMinimo
                           + caster.Buffs.DelHechizo(spell, Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo,
                                                     fight.RoundNumber);
                // El alcance del EQUIPO faltaba. Aquí sólo se sumaba el que dan los embrujos
                // —Buffs.De(19)— así que un personaje con alcance en los objetos no lo veía por
                // ningún lado: la característica 19 se le manda al cliente en la ficha, y el
                // servidor la ignoraba al comprobar si el hechizo llega.
                //
                // El alcance mínimo NO lo toca: la 19 amplía hasta dónde llegas, no desde dónde.
                int maximo = limites.AlcanceMaximo
                           + caster.Range
                           + caster.Buffs.De(AlcanceCaracteristica, fight.RoundNumber)
                           + caster.Buffs.DelHechizo(spell, Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo,
                                                     fight.RoundNumber);

                // TRAZA del alcance: de dónde sale cada sumando. Con esto se ve de un vistazo si
                // lo que falla es el equipo, el embrujo genérico o el que apunta a este hechizo.
                Program.LogDebug($"[ALCANCE] hechizo {spell} a {lejos} casillas. " +
                                 $"minimo {minimo} = base {limites.AlcanceMinimo} + embrujo " +
                                 $"{caster.Buffs.DelHechizo(spell, Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo, fight.RoundNumber)}. " +
                                 $"maximo {maximo} = base {limites.AlcanceMaximo} + equipo {caster.Range} + " +
                                 $"caracteristica {caster.Buffs.De(AlcanceCaracteristica, fight.RoundNumber)} + embrujo " +
                                 $"{caster.Buffs.DelHechizo(spell, Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo, fight.RoundNumber)}");

                // And the rest of the spell's modifiers: 294/295 take range away, 2905/2906 pin it.
                (minimo, maximo) = Managers.SpellModifiers.Range(caster, spell, minimo, maximo, fight.RoundNumber);

                if (lejos < minimo || lejos > maximo)
                {
                    Program.LogDebug($"[Combate] El hechizo {spell} no llega: {lejos} casillas, " +
                                     $"y su alcance es de {minimo} a {maximo}.");
                    return;
                }
            }
            if (cost <= 0) cost = DefaultCastCost;
            // The spell's own modifiers of its AP cost: 285 takes off, 296 adds.
            if (spell != 0) cost = Managers.SpellModifiers.ApCost(caster, spell, cost, fight.RoundNumber);
            if (cost > caster.CurrentAP) return;

            // Aimed at a portal that is on, the spell comes out of the network's last portal and
            // lands where the caster's aim leads from there (FightPortals.cs): whoever stands THERE
            // is its target, and what the cell has to be is judged there too.
            var proyeccion = spell != 0
                ? Projection(fight, caster, cell, Managers.SpellEffects.De(spell, grade))
                : null;
            if (proyeccion is { } alOtroLado)
            {
                Program.LogDebug($"[Portal] {caster.Id} casts {spell} at the portal on {cell}: through " +
                                 $"{string.Join(", ", alOtroLado.Chain.Select(p => p.Id))}, it lands on {alOtroLado.Cell}.");
                cell = alOtroLado.Cell;
            }

            var victim = VictimAt(fight, caster, cell);

            // What the cell has to be. Imantación wants somebody on it ("ocupada"), Tymadura
            // wants it empty ("libre"); cast on the wrong kind of cell, the client would not
            // even have offered it, and the server must not do the work either. A modifier
            // switches either (314/297 occupied, 299 free).
            var (needFree, needTaken) = Managers.SpellModifiers.Cells(caster, spell, limites.NeedFreeCell,
                                                                      limites.NeedTakenCell, fight.RoundNumber);
            if (needTaken && victim == null)
            {
                Program.LogDebug($"[Combate] El hechizo {spell} quiere una casilla ocupada y la {cell} está vacía.");
                return;
            }
            if (needFree && victim != null)
            {
                Program.LogDebug($"[Combate] El hechizo {spell} quiere una casilla libre y en la {cell} está {victim.Id}.");
                return;
            }
            long aQuien = victim?.Id ?? 0;

            // Lo que impide relanzarlo. Nada de esto existía: se podía repetir cualquier hechizo
            // mientras quedaran puntos de acción, y hay 35 de los 44 del Ocra con tope por turno,
            // 13 con tope por objetivo y 9 con rondas de espera.
            if (caster.Recarga.TryGetValue(spell, out int leFalta) && leFalta > 0)
            {
                Program.LogDebug($"[Combate] El hechizo {spell} todavía tiene {leFalta} ronda(s) " +
                                 $"de espera; no se lanza.");
                return;
            }

            caster.LanzadosEsteTurno.TryGetValue(spell, out int esteTurno);
            int porTurno = Managers.SpellModifiers.CastsPerTurn(caster, spell, limites.PorTurno, fight.RoundNumber);
            if (porTurno > 0 && esteTurno >= porTurno)
            {
                Program.LogDebug($"[Combate] El hechizo {spell} ya se ha lanzado {esteTurno} " +
                                 $"vez/veces este turno, y el tope es {porTurno}.");
                return;
            }

            // El tope por objetivo se cuenta contra el que está en la casilla apuntada. Los
            // hechizos de ZONA, que tocan a varios, cuentan aquí de menos: haría falta la lista de
            // afectados del motor, y eso todavía no está enganchado.
            caster.LanzadosPorObjetivo.TryGetValue((spell, aQuien), out int sobreEse);
            int porObjetivo = Managers.SpellModifiers.CastsPerTarget(caster, spell, limites.PorObjetivo, fight.RoundNumber);
            int topePorObjetivo = porObjetivo > 0 ? porObjetivo + caster.ExtraCastsPerTarget : 0;
            if (aQuien != 0 && topePorObjetivo > 0 && sobreEse >= topePorObjetivo)
            {
                Program.LogDebug($"[Combate] El hechizo {spell} ya se ha lanzado {sobreEse} " +
                                 $"vez/veces sobre {aQuien}, y el tope es {topePorObjetivo}.");
                return;
            }

            // ¿Sale crítico? Se tira una vez por lanzamiento, contra la suma de lo que aporta el
            // hechizo y lo que lleva el personaje. En la base, Flecha Helada tiene un diez de
            // crítico propio, y el Ocra lleva 47; el cliente pinta 57% en su tooltip, que es
            // exactamente la suma. Antes esto estaba clavado a "false" y no salía un crítico ni
            // por casualidad.
            // El critico del EQUIPO se perdia entero: se pasaba un cero como base, asi que solo
            // contaba el del hechizo y el de los embrujos. El registro lo decia en voz alta:
            // «20 % = 20 del hechizo + 0 del personaje», con un personaje que lleva 16 puesto. No
            // hay riesgo de contarlo dos veces: CriticalBonus es solo el equipo y Buffs.De(18)
            // solo los embrujos.
            int probabilidadCritico = limites.CriticoPropio +
                ConBonos(caster, CriticoCaracteristica, caster.CriticalBonus, fight.RoundNumber) +
                Managers.SpellModifiers.Critical(caster, spell, fight.RoundNumber);
            bool critico = TirarCritico(probabilidadCritico);
            if (critico)
            {
                Program.LogDebug($"[Combate] ¡CRÍTICO! con el hechizo {spell} " +
                                 $"({probabilidadCritico}% = {limites.CriticoPropio} del hechizo + " +
                                 $"{ConBonos(caster, CriticoCaracteristica, 0, fight.RoundNumber)} del personaje).");
            }

            // The capacity refusal and AP mutation stay in one tested operation. Previously the
            // capacity guard ran much later, when the effect was applied and AP was already gone.
            IEnumerable<SpellEffect> castEffects = spell == 0
                ? Array.Empty<SpellEffect>()
                : Managers.EffectEngine.EfectosDeLaTirada(spell, grade, critico);
            if (!await TryPayCastCostAsync(
                    fight, caster, castEffects, cost,
                    packet => WriteFrameAsync(stream, packet)))
            {
                return;
            }

            caster.LanzadosEsteTurno[spell] = esteTurno + 1;
            if (aQuien != 0) caster.LanzadosPorObjetivo[(spell, aQuien)] = sobreEse + 1;
            // Casting gives an invisible one away: his enemies see where the spell came from.
            if (Managers.MonsterTactics.IsInvisible(caster)) caster.LastSeenCell = caster.CellId;

            // El Versatil (no repetir accion) y los dos de rematar antes de cambiar de objetivo.
            await ChallengeWatcher.CastAsync(stream, fight, caster, spell, victim,
                                             esteTurno + 1);
            int intervalo = Math.Max(0, Managers.SpellModifiers.CastInterval(caster, spell, limites.Intervalo, fight.RoundNumber)
                                        - caster.CooldownReduction);
            if (intervalo > 0) caster.Recarga[spell] = intervalo;

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(caster.Id,
                                                         Network.FightProtocol.ActionSequence)));

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(
                    caster.Id,
                    spell == 0 ? Network.FightProtocol.WeaponCast : Network.FightProtocol.Cast,
                    Network.FightProtocol.CastAt(
                        caster.Id, aQuien, cell, spell, spellLevel, critico,
                        sobreEseObjetivo: porObjetivo > 0 ? sobreEse + 1 : 0,
                        esteTurno: porTurno > 0 ? esteTurno + 1 : 0,
                        intervalo: intervalo,
                        // Sólo cuando el golpe es del arma. Un hechizo lleva el f10 a cero, igual
                        // que el puñetazo: lo que el cliente mira para poner el nombre es esto.
                        arma: spell == 0 ? ArmaEquipada(caster) : 0,
                        noTarget: victim == null,
                        portals: proyeccion?.Chain.Select(p => p.Id).ToList()),
                    Network.FightProtocol.CastDetail)));

            // La ficha va en su propia secuencia, como en la captura, no suelta en medio.
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(caster.Id,
                                                         Network.FightProtocol.SheetSequence)));
            await FichaATodosAsync(fight, caster.Id,
                (ActionPointsCharacteristic, (long)caster.CurrentAP, 0L, 0L));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), caster.Id,
                                                       Network.FightProtocol.SheetSequence)));

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(caster.Id,
                                                  Network.FightProtocol.SpentActionPoints,
                                                  Network.FightProtocol.Spent(caster.Id, cost),
                                                  Network.FightProtocol.PointsDetail)));
            var cuenta = StatisticsBehind(fight, caster);
            int daboAntes = cuenta?.OwnDamage ?? 0;
            if (cuenta != null) cuenta.ActionPointsSpent += cost;

            // ONE DRAW FOR THE WHOLE CAST: Bumerán Pérfido steals in one element and boosts
            // that element's characteristic, out of the same throw of the dice.
            var tirada = spell != 0 ? Managers.EffectEngine.EfectosSorteados(spell, grade, critico) : null;

            // Through a portal the cast says so to its masks' R and r, and its damage and healing
            // grow with the network it crossed.
            fight.CastThroughPortal = proyeccion != null;
            fight.PortalBonusPercent = proyeccion is { } porElPortal ? Jondo.Unity.World.Fights.PortalNetwork.BonusPercent(porElPortal.Chain) : 0;
            try
            {
                await HurtAsync(stream, fight, caster, spell, grade, victim, cell, critico, tirada);

                // Y lo que el hechizo deja puesto, que no es sólo daño: los PA que roba Flecha Helada,
                // sus tres turnos de daños básicos, el alcance de Disparos Lejanos...
                await AplicarEfectosAsync(stream, fight, caster, spell, grade, victim,
                                          Managers.EffectEngine.AlLanzar, cell, critico, tirada);
            }
            finally
            {
                fight.CastThroughPortal = false;
                fight.PortalBonusPercent = 0;
            }

            // Cast through a portal (PST): the Selatrop's passive gives him its +2% after every one,
            // behind what the spell did -- Extinción's capture, frame 13.
            if (proyeccion != null && caster.IsAlive)
                await DispararAsync(stream, fight, caster, Managers.EffectEngine.AlProyectarPorUnPortal);

            // A critical hit (CC) is what the Zurcarák's Buena Estrella and Destino wait on --
            // "si el objetivo realiza un golpe crítico" -- on whoever landed it.
            if (critico && caster.IsAlive)
                await DispararAsync(stream, fight, caster, Managers.EffectEngine.AlGolpeCritico);

            // The AP that bought damage, for the "per AP" of the end screen.
            if (cuenta != null && cuenta.OwnDamage > daboAntes) cuenta.ActionPointsOnDamage += cost;

            int cierre = fight.SiguienteAccion();
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(cierre, caster.Id,
                                                       Network.FightProtocol.ActionSequence)));

            Program.LogDebug($"[Combate] Lanza el hechizo {spell} (grado {spellLevel}) a la casilla " +
                             $"{cell} por {cost} PA; le quedan {caster.CurrentAP}.");

            if (await CheckFightOverAsync(stream, fight, cierre)) return;

            // "Hace pasar de turno" (1031): the turn ends right behind the cast, as the jyt of
            // the Tymadura capture does, with the illusions already placed.
            if (fight.EndTurnRequested)
            {
                fight.EndTurnRequested = false;
                await PassTurnAsync(stream);
            }
        }

        /// <summary>
        /// Saca un invocado al tablero: una baliza del Ocra, un glifo, una trampa.
        ///
        /// No es un embrujo, es un COMBATIENTE. Se le reparte identificador negativo, se le monta
        /// la ficha desde la plantilla del bicho, se mete en el bando del que invoca y entra en el
        /// orden de turnos. Y su comportamiento no se escribe aquí: sale del
        /// <c>startingSpellId</c> de su grado, que es un hechizo lleno de enganches 792 —"al
        /// empezar mi turno lanza mi grado 2"—, la misma maquinaria que las actitudes de los
        /// dofus. Por eso la Baliza de Supervivencia se cura sola y la Táctica empuja sola sin
        /// que haya una línea escrita sobre ninguna de las dos.
        /// </summary>
        private static async Task<Fighter> InvocarAsync(NetworkStream stream, FightInstance fight,
                                               Fighter quienInvoca, int plantilla, int grado,
                                               int celdaApuntada,
                                               int efectoQueInvoca = Network.FightProtocol.Invoca)
        {
            var receta = Managers.Summons.De(plantilla, grado);
            if (receta == null)
            {
                Program.LogDebug($"[Combate] No hay plantilla {plantilla} grado {grado}; no se invoca.");
                return null;
            }

            // Bombs answer to their own cap and to nothing else: they cost no capacity, so the
            // check below would never stop them however many were already out.
            if (EsBomba(plantilla) &&
                ActiveBombCount(fight, quienInvoca) >= MaxBombsOnBoard)
            {
                Program.LogDebug($"[Fight] Fighter {quienInvoca.Id} already has {MaxBombsOnBoard} " +
                                 $"bomb(s) on the board; template {plantilla} was not summoned.");
                return null;
            }

            // Keep a defensive check for delayed or chained summon effects. Immediate casts have
            // already crossed the preflight in CastAsync, before paying AP.
            int limit = SummonLimitFor(quienInvoca, fight.RoundNumber);
            int active = UsedSummonCapacity(fight, quienInvoca);
            if (limit > 0 && receta.SummonCost > 0 && active + receta.SummonCost > limit)
            {
                // Solo a quien invoca, y solo si es el jugador. Aqui se llega tambien desde el
                // turno del monstruo y desde una invocacion lanzando su propio hechizo, y por esos
                // caminos el aviso saldria igualmente por el socket del jugador: le diria que ha
                // llegado a un tope que no es el suyo.
                if (quienInvoca.Id == GameState.CharacterId)
                {
                    await SendSummonLimitWarningAsync(
                        packet => WriteFrameAsync(stream, packet), limit);
                }

                Program.LogDebug($"[Fight] Fighter {quienInvoca.Id} already controls {active} " +
                                 $"summon(s), at capacity {limit}; template {plantilla} was not summoned.");
                return null;
            }

            int celda = CasillaLibreCerca(fight, celdaApuntada >= 0 ? celdaApuntada : quienInvoca.CellId);
            if (celda < 0)
            {
                Program.LogDebug($"[Combate] No hay sitio libre para invocar la {plantilla}.");
                return null;
            }

            var invocado = new Fighter
            {
                Id = fight.SiguienteIdDeInvocado(),
                Name = $"invocado {plantilla}",
                CellId = celda,
                IsMonster = true,
                MonsterId = plantilla,
                GradeIndex = grado,
                Level = receta.Nivel,
                SummonCost = receta.SummonCost,
                Look = receta.Look,

                // AND THE BONE, which nothing was filling in. A summon carried its look STRING
                // and no bone number, so every packet built out of MonsterLook came out as
                // f3 { f2 = 3 } with nothing to draw -- which is exactly what a bomb growing
                // looked like on the wire: "1a06 1003 2a02be01", the scale on its own and no
                // f3 in sight. The real one is "1a08 1003 189a0c 2a0169": bone 1562, scale 105.
                //
                // The number is the one already resolved for the summon packet: what the look
                // string names between its braces, not what that number points at.
                LookBoneId = receta.PlantillaDelAspecto,
                HechizoPropio = receta.HechizoPropio,
                MaxAP = receta.PuntosDeAccion,
                CurrentAP = receta.PuntosDeAccion,
                MaxMP = receta.PuntosDeMovimiento,
                CurrentMP = receta.PuntosDeMovimiento,
                NeutralResPct = receta.ResistenciaNeutral,
                EarthResPct = receta.ResistenciaTierra,
                FireResPct = receta.ResistenciaFuego,
                WaterResPct = receta.ResistenciaAgua,
                AirResPct = receta.ResistenciaAire,
            };
            invocado.Otras[Fighter.CaracteristicaDeErosion] = Fighter.ErosionBase;
            invocado.MaxHP = Managers.Summons.VidaDelInvocado(receta.Vida, quienInvoca.Level,
                                                              receta.VidaFija);

            // And its characteristics, which it went without: every summon hit with its spells'
            // bare dice. See Summons.CaracteristicaDelInvocado.
            invocado.Strength = Managers.Summons.CaracteristicaDelInvocado(receta.Fuerza, receta.BonusFuerza, quienInvoca.Level);
            invocado.Intelligence = Managers.Summons.CaracteristicaDelInvocado(receta.Inteligencia, receta.BonusInteligencia, quienInvoca.Level);
            invocado.Chance = Managers.Summons.CaracteristicaDelInvocado(receta.Suerte, receta.BonusSuerte, quienInvoca.Level);
            invocado.Agility = Managers.Summons.CaracteristicaDelInvocado(receta.Agilidad, receta.BonusAgilidad, quienInvoca.Level);
            invocado.Power = Managers.Summons.PotenciaDelInvocado(receta.BonusDeDanos);
            invocado.CurrentHP = invocado.MaxHP;

            // ¿Le toca turno? Sólo si su hechizo tiene algo que hacer al empezarlo. La Baliza de
            // Supervivencia lo tiene —se cura sola— y la Táctica no, que sólo reacciona a lo que
            // le pase alrededor; por eso en las capturas la primera juega y la segunda no aparece
            // ni una vez en el carrusel.
            // Whether it plays is the template's flag, not a guess from its spell: see
            // Summon.Juega. The old reading -- "only if its spell has something to do at turn
            // start" -- happened to fit the two beacons and nothing else: a Tymobot has nothing
            // to do at turn start and plays, controlled by its owner.
            invocado.JuegaTurno = receta.Juega;
            invocado.HechizosDeInvocado = receta.Hechizos;
            invocado.TemplateAllowsTackle = receta.AllowsTackle;

            fight.Invocar(invocado, quienInvoca);

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildSummon(
                    quienInvoca.Id, invocado.Id, celda, FacingOf(fight, invocado),
                    receta.PlantillaDelAspecto, plantilla, grado, FullSheetOf(invocado),
                    efectoQueInvoca)));

            // Y detrás, la lista de combatientes otra vez: es lo que da de alta al invocado en el
            // cliente y lo mete en el carrusel.
            await ReenviarLaListaAsync(stream, fight);

            // And its spells to whoever plays it: an empty jxc and a jyy of its own, in the
            // owner's socket only. Measured on the Tymobot and on the Osamodas' animals: the
            // jyy carries the summon in f3 and the owner in f4, the spells at their grades,
            // and no melee entry. Without it the owner's client has nothing to cast with when
            // the summon's turn comes.
            if (invocado.JuegaTurno && receta.Hechizos.Count > 0)
            {
                await ACadaUnoAsync(fight, async sesion =>
                {
                    if (sesion.State.CharacterId != quienInvoca.Id) return;
                    await WriteFrameAsync(sesion.Stream, ConnectionProtocol.Push(Op.Jxc,
                        Network.FightProtocol.BuildCooldowns(invocado.Id, RecargasDe(invocado))));
                    await WriteFrameAsync(sesion.Stream, ConnectionProtocol.Push(Op.Jyy,
                        Network.FightProtocol.BuildSummonSpellBar(invocado.Id, quienInvoca.Id,
                                                                  receta.Hechizos)));
                });
            }

            Program.LogDebug($"[Combate] {quienInvoca.Id} invoca la plantilla {plantilla} grado " +
                             $"{grado} como {invocado.Id} en la casilla {celda} con " +
                             $"{invocado.MaxHP} de vida y el hechizo {receta.HechizoPropio}.");

            // Su hechizo pasa a ser su actitud, y se lanza en el acto para que queden puestos sus
            // enganches y su cuenta atrás.
            if (receta.HechizoPropio != 0 && EsDelBandoDeLosMonstruos(fight, quienInvoca))
            {
                // A monster's summon brings its spell as a monster does: cast, its rows armed.
                invocado.Conducta = (receta.HechizoPropio, receta.GradoDelHechizoPropio);
                await LanzarLaConductaAsync(stream, fight, invocado);
            }
            else if (receta.HechizoPropio != 0)
            {
                invocado.Buffs.Actitudes.Add(receta.HechizoPropio);
                await AplicarEfectosAsync(stream, fight, invocado, receta.HechizoPropio,
                                          receta.GradoDelHechizoPropio,
                                          invocado, Managers.EffectEngine.AlLanzar, celda, armar: false);
            }

            // Y si es una bomba, nace en Combo I. Uno, no dos: medido en «tymador-explobomba
            // resiliente», donde las tres bombas reciben UN combo la ronda en que salen y DOS
            // cada ronda posterior. And that one comes out of its own spell: Encendimiento's
            // 1017 hands La Astucia del Tymador back to its summoner, whose 792 casts the
            // ladder on the bomb -- "20577 by the Rogue, 20497 by the bomb, state 2484" at the
            // birth of every bomb in the sismobomba capture, nothing more. Giving it another
            // one here on top, as was done before the chain resolved, had every bomb born at
            // II. The rung is only given by hand when the chain left the bomb without one.
            if (EsBomba(plantilla))
            {
                if (Managers.Combo.LevelOf(invocado) == 0)
                {
                    await UnComboAsync(stream, fight, quienInvoca, invocado);
                }
                Program.LogDebug($"[Combo] La bomba {invocado.Id} nace en el nivel " +
                                 $"{Managers.Combo.LevelOf(invocado)}.");
            }

            // Y si con ella se ha levantado un muro, que se vea.
            await ReconciliarLosMurosAsync(stream, fight);
            return invocado;
        }

        /// <summary>
        /// A double (180) put on the board by the engine, told as "sram-doble" has it: the jwe 180
        /// with the owner's look, name and level (frame 16), the list again (jzu, 17), and to
        /// the owner alone an empty jxc (18) and a jyy with no spell in it (31) -- it walks and
        /// does not attack, and it is his to play.
        /// </summary>
        private static async Task AnunciarElDobleAsync(NetworkStream stream, FightInstance fight,
                                                       Fighter dueno, Fighter doble)
        {
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildDouble(dueno.Id, doble.Id, doble.CellId, FacingOf(fight, doble),
                                                  NormalFightLook(doble), doble.Name, doble.Level,
                                                  FullSheetOf(doble, conTraza: false))));
            await ReenviarLaListaAsync(stream, fight);
            await ACadaUnoAsync(fight, async sesion =>
            {
                if (sesion.State.CharacterId != dueno.Id) return;
                await WriteFrameAsync(sesion.Stream, ConnectionProtocol.Push(Op.Jxc,
                    Network.FightProtocol.BuildCooldowns(doble.Id, RecargasDe(doble))));
                await WriteFrameAsync(sesion.Stream, ConnectionProtocol.Push(Op.Jyy,
                    Network.FightProtocol.BuildSummonSpellBar(doble.Id, dueno.Id, doble.HechizosDeInvocado)));
            });
            Program.LogDebug($"[Combat] {dueno.Id} summons its double {doble.Id} on cell {doble.CellId} " +
                             $"with {doble.MaxHP} life, {doble.MaxAP} AP and {doble.MaxMP} MP.");
        }

        /// <summary>
        /// The jwe 300 of a spell set off by another. A summon's carries two f4 -- itself and
        /// its summoner -- and no f8, the shape of the bomb's ladder casts in "tymador-explobomba
        /// resiliente" (frames 262 and 264); a person's carries the ordinary cast block.
        /// </summary>
        /// <param name="celda">
        /// The cell the chained cast was aimed at when it was resolved, or minus one for the
        /// target's cell now.
        /// </param>
        private static async Task AnunciarElEncadenadoAsync(FightInstance fight, Fighter quien,
                                                            Fighter sobre, int hechizo, int grado, int celda = -1)
        {
            var limites = LimitesDeGrado(hechizo, Math.Max(1, grado));
            if (limites.LevelId <= 0) return;

            // A chained cast carries no f8: 18,526 of the casts in the class captures that no
            // jwh asked for have none, against 3,171 of 3,171 asked for that have it -- the 737
            // left are the casts a fight or a monster makes of its own, not chained ones.
            byte[] trama = quien.EsInvocado
                ? Network.FightProtocol.BuildComboCast(quien.Id, quien.Invocador, quien.CellId,
                                                       hechizo, limites.LevelId)
                : Network.FightProtocol.BuildAction(
                    quien.Id, Network.FightProtocol.Cast,
                    Network.FightProtocol.CastAt(quien.Id, sobre.Id, celda >= 0 ? celda : sobre.CellId, hechizo,
                                                 limites.LevelId, critical: false, chained: true),
                    Network.FightProtocol.CastDetail);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe, trama));
        }

        /// <summary>
        /// One combo on one bomb: the cast, the rung, and the bomb growing.
        /// </summary>
        /// <remarks>
        /// The ladder alone was never enough. The server climbed it right -- the log says so, and
        /// our jxm for the rung is byte for byte the real one -- but on screen the bomb stayed on
        /// Combo I and stayed the same size, because the two things the client actually redraws
        /// off were missing:
        ///
        ///   1. THE BOMB CASTING ON ITSELF. Measured in "tymador-explobomba resiliente": one
        ///      jwe f14 = 300 naming 20497 per combo granted (frames 108, 247, 262, 423, 441...),
        ///      and, when the rung moves, a second one naming the grade of 20500 that pays for it
        ///      (250, 264, 425, 443...).
        ///   2. THE LOOK. A jwe f14 = 149 with a bigger scale, at the tail of the step (259, 283,
        ///      439, 462...). See <see cref="Managers.Combo.SizeOf"/> for where the number comes
        ///      from -- it is derived from the 1060 buffs, not a table.
        /// </remarks>
        private static async Task UnComboAsync(NetworkStream stream, FightInstance fight,
                                               Fighter dueno, Fighter bomba)
        {
            int antes = Managers.Combo.LevelOf(bomba);
            int tamanoAntes = Managers.Combo.SizeOf(bomba, fight.RoundNumber);

            var escalera = LimitesDeGrado(Managers.Combo.LadderSpell, 1);
            if (escalera.LevelId > 0)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildComboCast(bomba.Id, dueno.Id, bomba.CellId,
                                                         Managers.Combo.LadderSpell,
                                                         escalera.LevelId)));
            }

            await AplicarEfectosAsync(stream, fight, bomba, Managers.Combo.LadderSpell, 1,
                                      bomba, Managers.EffectEngine.AlLanzar, bomba.CellId);

            // The 20500 behind the rung, and the look, go out from AplicarEfectosAsync itself
            // now: every chained cast is announced there, and every cast that moves a bomb's
            // size redraws it.
            _ = antes; _ = tamanoAntes;
        }

        /// <summary>
        /// Lo que sube el combo de cada bomba al empezar el turno de su tymador. Measured --
        /// "tymador-explobomba resiliente", three bombs over eight rounds, two each every round
        /// after the one they are born in -- and dealt by his class passive, whose 20577 carries
        /// exactly two ladder casts.
        /// </summary>
        internal const int CombosPorTurno = 2;

        /// <summary>
        /// Las esperas de uno, para el jxc. Se nombran TODAS las que alguna vez han estado
        /// puestas, incluso las que ya están a cero: es lo que hace el servidor real, cuyo jxc
        /// sigue listando los mismos hechizos ronda tras ronda con un cero al lado.
        /// </summary>
        private static IEnumerable<(int Spell, int Rounds)> RecargasDe(Fighter quien)
        {
            if (quien == null) yield break;
            foreach (var par in quien.Recarga) yield return (par.Key, par.Value);
        }

        /// <summary>
        /// Deja apuntado el hechizo sobre quien lo lleva, si le queda algo por hacer.
        ///
        /// "Algo por hacer" es tener efectos con un disparador distinto de "al lanzar". Se apunta
        /// hasta la ronda en la que caduca el embrujo que más dure de los que ha puesto, que es lo
        /// que decide hasta cuándo sigue vivo el hechizo.
        /// </summary>
        /// <remarks>
        /// The chained spells hook too, each at its own grade: Furor's cast leaves nothing of
        /// 13156 to fire later, it is 28604 at grade 3 -- reached through two 1160s -- that
        /// carries the "1160 under TE" of the decay, and the capture registers that row on the
        /// Yopuka (jxm 38, trigger TE, activation the round after). Read off the root spell
        /// alone, the decay never existed. A hook is put on whoever the spell left a row on, for
        /// as long as its longest row, from the round of the cast.
        /// </remarks>
        /// <param name="incluirElPropio">Whether the spell fired itself is hooked, or only what it chained.</param>
        /// <param name="critico">Whether the cast was critical: the hook fires with the critical lists.</param>
        internal static void EngancharLoPendiente(List<Managers.Outcome> consecuencias,
                                                  int hechizo, int grado, long lanzador, int ronda,
                                                  bool incluirElPropio = true, bool critico = false,
                                                  bool conducta = false)
        {
            // A monster spell's rows armed by the engine, one hook per bearer, caster and spell,
            // holding just those rows. The behaviour spell's own rows are the monster's for good.
            foreach (var grupo in consecuencias.Where(c => c.FilaArmada && c.Sobre != null)
                                               .GroupBy(c => (c.Sobre, c.HechizoOrigen, c.NivelOrigen, Quien: c.Caster?.Id ?? lanzador)))
            {
                bool deConducta = conducta && grupo.Key.HechizoOrigen == hechizo;
                int hasta = ronda + 1;
                foreach (var c in grupo)
                {
                    int suya = Managers.EffectEngine.CaducidadDeLaFila(c.Efecto, ronda, deConducta);
                    if (suya < 0) { hasta = -1; break; }
                    hasta = Math.Max(hasta, suya);
                }
                grupo.Key.Sobre.Buffs.ArmarFilas(grupo.Key.HechizoOrigen, grupo.Key.NivelOrigen, hasta, grupo.Key.Quien,
                                                 ronda, grupo.Select(c => ClaveDeFila(c.Efecto)), critico);
            }

            // Every spell this cast ran that still has rows under a trigger, AT ITS GRADE: one
            // spell can run at two grades in one cast, and only the grade that has the rows is
            // hooked, and only on who that grade left something on. Doble's 12966 runs at grade
            // 1 on the double -- "cast 12964 at the end of the turn", which swaps it with the
            // Sram and kills its caster -- and at grade 2 on the Sram, a state and nothing else.
            // Keyed by the spell alone, the Sram was hooked at grade 1 too: at the end of his
            // turn he cast 12964 at himself and died of it (sram-doble capture: 12964 is cast by
            // the double, in its own second turn).
            var conAlgoPendiente = new HashSet<(int Spell, int Grade)>();
            void Anotar(int cual, int enGrado)
            {
                if (cual == 0 || conAlgoPendiente.Contains((cual, enGrado))) return;
                // A monster spell's rows are armed one by one, above.
                if (!Managers.PlayerSpells.Contains(cual)) return;
                foreach (var efecto in Managers.SpellEffects.De(cual, enGrado))
                {
                    foreach (var d in efecto.Disparadores())
                    {
                        if (!string.Equals(d, Managers.EffectEngine.AlLanzar, StringComparison.OrdinalIgnoreCase))
                        {
                            conAlgoPendiente.Add((cual, enGrado));
                            return;
                        }
                    }
                }
            }
            if (incluirElPropio) Anotar(hechizo, grado);
            foreach (var c in consecuencias)
            {
                if (!incluirElPropio && (c.HechizoOrigen, c.NivelOrigen) == (hechizo, grado)) continue;
                Anotar(c.HechizoOrigen, c.NivelOrigen);
            }
            if (conAlgoPendiente.Count == 0) return;

            // A child that left nothing on anybody is hooked on its target, in its caster's name,
            // for as long as its waiting rows say.
            foreach (var marca in consecuencias)
            {
                if (!marca.EnganchePendiente || marca.Sobre == null) continue;
                if (!conAlgoPendiente.Contains((marca.HechizoOrigen, marca.NivelOrigen))) continue;
                bool yaTieneFilas = consecuencias.Any(c => c.Buff != null && c.Sobre == marca.Sobre
                                                        && c.HechizoOrigen == marca.HechizoOrigen
                                                        && c.NivelOrigen == marca.NivelOrigen);
                if (yaTieneFilas) continue;
                marca.Sobre.Buffs.Enganchar(marca.HechizoOrigen, marca.NivelOrigen,
                    Managers.EffectEngine.CaducidadDelEnganche(marca.HechizoOrigen, marca.NivelOrigen, ronda),
                    marca.Caster?.Id ?? lanzador, ronda, critico);
            }

            foreach (var (cual, enGrado) in conAlgoPendiente)
            {
                var hasta = new Dictionary<Fighter, int>();
                foreach (var c in consecuencias)
                {
                    if (c.Buff == null || c.Sobre == null || c.HechizoOrigen != cual || c.NivelOrigen != enGrado) continue;
                    int cuando = c.Buff.CaducaEnRonda;
                    if (!hasta.TryGetValue(c.Sobre, out int ya) || cuando < 0 || (ya >= 0 && cuando > ya))
                    {
                        hasta[c.Sobre] = cuando;
                    }
                }

                foreach (var (quien, cuando) in hasta)
                {
                    // Its caster is the one who cast it on THIS bearer, as the rows say.
                    long quienLoLanzo = lanzador;
                    foreach (var c in consecuencias)
                    {
                        if (c.HechizoOrigen == cual && c.NivelOrigen == enGrado && c.Sobre == quien && c.Caster != null) { quienLoLanzo = c.Caster.Id; break; }
                    }
                    quien.Buffs.Enganchar(cual, enGrado, cuando, quienLoLanzo, ronda, critico);
                }
            }
        }

        /// <summary>The key an armed row is held by: its effect uid, or its place when it has none.</summary>
        private static int ClaveDeFila(Managers.SpellEffect fila)
            => fila.EffectUid != 0 ? fila.EffectUid : -1 - (fila.EffectId * 31 + fila.DiceNum);

        /// <summary>The rows of an armed hook that wait on this trigger.</summary>
        private static List<Managers.SpellEffect> FilasArmadas(Jondo.Unity.World.Fights.Buffs.ActiveSpell enganche,
                                                              string disparador)
        {
            var lista = enganche.Critico
                ? Managers.EffectEngine.EfectosDeLaTirada(enganche.Hechizo, enganche.Grado, true)
                : Managers.SpellEffects.De(enganche.Hechizo, enganche.Grado);
            return lista.Where(f => enganche.Filas.Contains(ClaveDeFila(f))
                                 && f.Disparadores().Any(d => string.Equals(d, disparador, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
        }

        /// <summary>
        /// A monster spell's rows done in the order they are written: the blows through HurtAsync and
        /// the rest through the engine, a run of each at a time. Arm spells like Kabaal's are
        /// "952 on his invulnerability, then the blow, then 406": dealt before the 952, the blow
        /// landed on an invulnerable boss and nothing could ever hurt him.
        /// </summary>
        private static async Task LanzarPorOrdenAsync(NetworkStream stream, FightInstance fight, Fighter caster,
                                                      int hechizo, int grado, Fighter objetivo, int celda,
                                                      string disparador, IReadOnlyList<Managers.SpellEffect> filas,
                                                      bool critico = false, int rondaDelEnganche = -1,
                                                      bool soloAlObjetivo = false, bool conducta = false)
        {
            bool EsGolpe(Managers.SpellEffect f)
                => Managers.EffectEngine.EsDeDano(f.EffectId)
                   && f.Disparadores().Any(d => string.Equals(d, disparador, StringComparison.OrdinalIgnoreCase));

            int i = 0;
            while (i < filas.Count)
            {
                bool golpes = EsGolpe(filas[i]);
                var tanda = new List<Managers.SpellEffect>();
                while (i < filas.Count && EsGolpe(filas[i]) == golpes) tanda.Add(filas[i++]);

                if (golpes)
                {
                    await HurtAsync(stream, fight, caster, hechizo, grado, objetivo, celda, critico, tirada: tanda,
                                    disparador: disparador, soloAlObjetivo: soloAlObjetivo);
                }
                else
                {
                    await AplicarEfectosAsync(stream, fight, caster, hechizo, grado, objetivo, disparador, celda,
                                              critico, tirada: tanda, rondaDelEnganche: rondaDelEnganche,
                                              soloAlObjetivo: soloAlObjetivo, conducta: conducta);
                }
                if (!caster.IsAlive && !conducta) break;
            }
        }

        /// <summary>
        /// A monster's behaviour spell, cast when the fight begins or when it comes in: a cast
        /// like any other on the wire, and its triggered rows armed for good on whoever they name.
        /// </summary>
        private static async Task LanzarLaConductaAsync(NetworkStream stream, FightInstance fight, Fighter quien)
        {
            var (hechizo, grado) = quien.Conducta;
            if (hechizo == 0 || !quien.IsAlive) return;
            int nivelId = LimitesDeGrado(hechizo, grado).LevelId;

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(quien.Id, Network.FightProtocol.ActionSequence)));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(
                    quien.Id, Network.FightProtocol.Cast,
                    Network.FightProtocol.CastAt(quien.Id, quien.Id, quien.CellId, hechizo, nivelId, critical: false),
                    Network.FightProtocol.CastDetail)));

            var filas = Managers.EffectEngine.EfectosSorteados(hechizo, grado, false);
            await LanzarPorOrdenAsync(stream, fight, quien, hechizo, grado, quien, quien.CellId,
                                      Managers.EffectEngine.AlLanzar, filas, conducta: true);

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                       Network.FightProtocol.ActionSequence)));
            Program.LogDebug($"[Combate] {quien.Id} lanza su hechizo de comportamiento {hechizo} (grado {grado}).");
        }

        /// <summary>
        /// The rows of a summoning spell written for the one it brought out -- "U" in their mask --
        /// done on it now that it is on the board: Tal Kasha's revived get their mark, Kumijo's
        /// Kitsunebis their states.
        /// </summary>
        private static async Task AplicarLoDelInvocadoAsync(NetworkStream stream, FightInstance fight, Fighter caster,
                                                            int hechizo, int grado, Fighter invocado)
        {
            if (invocado == null || hechizo == 0 || !invocado.IsAlive) return;
            var filas = Managers.SpellEffects.De(hechizo, grado)
                .Where(f => (f.TargetMask ?? "").Split(',').Any(t => t.Trim() == "U")
                            && f.Disparadores().Any(d => string.Equals(d, Managers.EffectEngine.AlLanzar, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (filas.Count == 0) return;
            await LanzarPorOrdenAsync(stream, fight, caster, hechizo, grado, invocado, invocado.CellId,
                                      Managers.EffectEngine.AlLanzar, filas, soloAlObjetivo: true);
        }

        /// <summary>The Zombi state a fighter brought back carries: 74, which the dungeons' spells read.</summary>
        private const int EstadoZombi = 74;

        /// <summary>
        /// "Invoca al último aliado muerto" (780, 1034): the last of the caster's side to fall comes
        /// back as his summon, with the life the die says, on the aimed cell or the nearest free
        /// one, in the Zombi state -- which is what Tal Kasha's glyphs, Vórtex's corruption and
        /// Sylargh's zombie wait on (EON74). Then what the spell writes for him ("U").
        /// </summary>
        private static async Task ResucitarAsync(NetworkStream stream, FightInstance fight, Fighter quien,
                                                 int hechizo, int grado, int efecto, int porcentaje, int celda)
        {
            var muerto = fight.Muertos.LastOrDefault(m => !m.IsAlive && m.TeamId == quien.TeamId
                                                          && m.IsMonster && m != quien);
            if (muerto == null)
            {
                Program.LogDebug($"[Combate] {quien.Id} quiere resucitar a alguien y no ha caído nadie de los suyos.");
                return;
            }
            int donde = celda >= 0 && !Occupied(fight, celda) && PisableEnCombate(fight, celda)
                ? celda : CasillaLibreCerca(fight, celda >= 0 ? celda : quien.CellId);
            if (donde < 0) return;

            fight.Muertos.Remove(muerto);
            muerto.Buffs.Vaciar();
            muerto.Muriendo = false;
            muerto.Invocador = quien.Id;
            muerto.CellId = donde;
            muerto.CurrentHP = Math.Max(1, muerto.MaxHP * porcentaje / 100);
            muerto.CurrentAP = muerto.MaxAP;
            muerto.CurrentMP = muerto.MaxMP;
            fight.RebuildTurnOrderKeepingCurrent();

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildSummon(
                    quien.Id, muerto.Id, donde, FacingOf(fight, muerto),
                    muerto.MonsterId, muerto.MonsterId, muerto.GradeIndex + 1, FullSheetOf(muerto), efecto)));
            await ReenviarLaListaAsync(stream, fight);

            var zombi = muerto.Buffs.Poner(new Jondo.Unity.World.Fights.Buff
            {
                EffectId = Jondo.Unity.World.Combat.EffectSupport.AddState, Estado = EstadoZombi,
                HechizoOrigen = hechizo, NivelOrigen = grado, Quien = quien.Id, Disparador = Managers.EffectEngine.AlLanzar,
                CaducaEnRonda = -1, EmpiezaEnRonda = fight.RoundNumber,
            }, fight.SiguienteEmbrujo);
            muerto.Buffs.PonerEstado(EstadoZombi);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxm,
                Network.FightProtocol.BuildBuff(muerto.Id, quien.Id, zombi.Numero,
                    Jondo.Unity.World.Combat.EffectSupport.AddState, 0, EstadoZombi, 0, 0, hechizo,
                    Managers.EffectEngine.AlLanzar, -1, 0, 2, grado)));

            Program.LogDebug($"[Combate] {quien.Id} resucita a {muerto.Id} (plantilla {muerto.MonsterId}) en la casilla " +
                             $"{donde} con {muerto.CurrentHP}/{muerto.MaxHP}.");

            await DispararAsync(stream, fight, muerto, Managers.EffectEngine.AlPonerseElEstado(EstadoZombi));
            await AplicarLoDelInvocadoAsync(stream, fight, quien, hechizo, grado, muerto);
            await RevisarLosRecuentosAsync(stream, fight);
        }

        /// <summary>
        /// The triggers that carry a mask, on everybody who waits on one: "EK:&lt;mask&gt;" when the
        /// one who just died is what the mask names -- Tejossus's ally Xa, Cil's summon -- seen
        /// from the one waiting.
        /// </summary>
        private static async Task DispararLosDeUnaMuerteAsync(NetworkStream stream, FightInstance fight, Fighter muerto)
        {
            foreach (var quien in TodosLosCombatientes(fight).ToList())
            {
                if (quien == null || !quien.IsAlive) continue;
                foreach (var disparador in DisparadoresQueEspera(quien).Where(d => d.StartsWith("EK:")).ToList())
                {
                    if (!Managers.EffectEngine.CumpleLaMascara(quien, muerto, disparador.Substring(3))) continue;
                    var antes = fight.TriggeringAttacker;
                    fight.TriggeringAttacker = muerto;
                    await DispararAsync(stream, fight, quien, disparador);
                    fight.TriggeringAttacker = antes;
                }
            }
            await RevisarLosRecuentosAsync(stream, fight);
        }

        /// <summary>
        /// "EC:&lt;op&gt;&lt;n&gt;:&lt;mask&gt;": the count of fighters a mask names, compared -- "EC:=0:g" is
        /// "no allies left", Tejossus alone. Goes off when it comes true, once, and again only
        /// after it has been false.
        /// </summary>
        private static async Task RevisarLosRecuentosAsync(NetworkStream stream, FightInstance fight)
        {
            var todos = TodosLosCombatientes(fight).Where(f => f != null && f.IsAlive).ToList();
            foreach (var quien in todos)
            {
                foreach (var disparador in DisparadoresQueEspera(quien).Where(d => d.StartsWith("EC:")).ToList())
                {
                    var partes = disparador.Split(':', 3);
                    if (partes.Length < 3 || partes[1].Length < 2) continue;
                    char op = partes[1][0];
                    if (!int.TryParse(partes[1].Substring(1), out int n)) continue;
                    int cuantos = todos.Count(f => Managers.EffectEngine.CumpleLaMascara(quien, f, partes[2]));
                    bool cumple = op switch { '=' => cuantos == n, '>' => cuantos > n, '<' => cuantos < n, _ => false };
                    var clave = (quien.Id, disparador);
                    if (!cumple) { fight.RecuentosCumplidos.Remove(clave); continue; }
                    if (!fight.RecuentosCumplidos.Add(clave)) continue;
                    await DispararAsync(stream, fight, quien, disparador);
                }
            }
        }

        /// <summary>Every trigger a fighter's armed rows, hooks and attitudes wait on.</summary>
        private static IEnumerable<string> DisparadoresQueEspera(Fighter quien)
        {
            var vistos = new HashSet<string>();
            IEnumerable<Managers.SpellEffect> Filas(int hechizo, int grado) => Managers.SpellEffects.De(hechizo, grado);
            foreach (var enganche in quien.Buffs.ActiveSpells.ToList())
            {
                foreach (var fila in Filas(enganche.Hechizo, enganche.Grado))
                {
                    if (enganche.Filas != null && !enganche.Filas.Contains(ClaveDeFila(fila))) continue;
                    foreach (var d in fila.Disparadores()) if (vistos.Add(d)) yield return d;
                }
            }
            foreach (int actitud in quien.Buffs.Actitudes.ToList())
                foreach (var fila in Filas(actitud, quien.Buffs.GradoDeActitud(actitud)))
                    foreach (var d in fila.Disparadores()) if (vistos.Add(d)) yield return d;
        }

        /// <summary>Whether a fighter is on the monsters' side: a monster, or the summon of one.</summary>
        private static bool EsDelBandoDeLosMonstruos(FightInstance fight, Fighter quien)
        {
            var raiz = quien;
            for (int i = 0; i < 8 && raiz != null && raiz.EsInvocado; i++) raiz = fight.Buscar(raiz.Invocador);
            return raiz != null && raiz.IsMonster;
        }

        /// <summary>
        /// Dispara lo que un luchador tenga pendiente para este momento: los hechizos que lleva
        /// puestos y que reaccionan a lo que acaba de pasar.
        /// </summary>
        /// <remarks>
        /// Fired for every trigger the engine names -- turn start, turn end, when hit, on
        /// death, per step walked -- and not only for the steps, which is all it was wired to
        /// for a long while: a spell hooked with an X effect, Polvo's "explode if destroyed",
        /// never went off. The effects come from whoever cast the spell, with the bearer as
        /// the target, which is who the mask letters were written for.
        /// </remarks>
        private static async Task EngancheAsync(NetworkStream stream, FightInstance fight,
                                                Fighter quien, string disparador)
        {
            if (quien == null) return;
            quien.Buffs.BarrerEnganches(fight.RoundNumber);

            // Inside ONE sequence of the bearer's, opened when the first frame goes out, the
            // way the attitudes do: the client applies nothing that arrives outside an open
            // jto. Furor's decay went out bare after the jyt -- jya, jwe 406, jxm -- and the
            // client kept Furor II on the panel for good. The real server wraps a turn
            // trigger's casts so: "jyt -6, jto{-6,3}, jwe 300 13155, ..., jya, jwi" in the
            // Sentencia capture.
            await DentroDeUnaSecuenciaAsync(fight, quien, async () =>
            {
                foreach (var enganche in new List<Jondo.Unity.World.Fights.Buffs.ActiveSpell>(quien.Buffs.ActiveSpells))
                {
                    // A spell he holds as an attitude fires as one, in ActitudesAsync; hooked as well,
                    // its rows went off twice -- a boss's behaviour spell, cast at the start, left a
                    // row on him and was hooked on top.
                    if (quien.Buffs.Actitudes.Contains(enganche.Hechizo)) continue;

                    if (enganche.Filas != null)
                    {
                        if (!enganche.Vivo(fight.RoundNumber)) continue;
                        var armadas = FilasArmadas(enganche, disparador);
                        if (armadas.Count == 0) continue;
                        var suLanzador = (enganche.Lanzador != 0 ? fight.Buscar(enganche.Lanzador) : null) ?? quien;
                        await LanzarPorOrdenAsync(stream, fight, suLanzador, enganche.Hechizo, enganche.Grado, quien,
                                                  quien.CellId, disparador, armadas, enganche.Critico,
                                                  rondaDelEnganche: enganche.PuestoEnRonda, soloAlObjetivo: true);
                        continue;
                    }
                    var lanzador = (enganche.Lanzador != 0 ? fight.Buscar(enganche.Lanzador) : null) ?? quien;

                    // Its rows under this trigger, in their order: the blows through HurtAsync, the
                    // rest through the engine. Arsénico's "3793, then 98 under TB" is its marker and
                    // then its 27 air damage at the start of the target's turn, as in its capture;
                    // handed whole to the engine, the damage row was dropped as a root blow -- the
                    // cast had dealt it already, at the wrong time.
                    var todas = enganche.Critico
                        ? Managers.EffectEngine.EfectosDeLaTirada(enganche.Hechizo, enganche.Grado, true)
                        : Managers.SpellEffects.De(enganche.Hechizo, enganche.Grado);
                    var suyas = todas.Where(f => f.Disparadores().Any(d => string.Equals(d, disparador, StringComparison.OrdinalIgnoreCase)))
                                     .ToList();
                    if (suyas.Count == 0) continue;
                    if (suyas.Any(f => Managers.EffectEngine.EsDeDano(f.EffectId)))
                    {
                        await LanzarPorOrdenAsync(stream, fight, lanzador, enganche.Hechizo, enganche.Grado, quien,
                                                  quien.CellId, disparador, suyas, enganche.Critico,
                                                  rondaDelEnganche: enganche.PuestoEnRonda);
                        continue;
                    }
                    await AplicarEfectosAsync(stream, fight, lanzador, enganche.Hechizo, enganche.Grado,
                                              quien, disparador, quien.CellId, critico: enganche.Critico,
                                              rondaDelEnganche: enganche.PuestoEnRonda);
                }
            });
        }

        /// <summary>
        /// Runs <paramref name="cuerpo"/> with a sequence of <paramref name="quien"/>'s owed to
        /// the clients: the jto goes out right before the first frame the body sends and the
        /// jwi after the body, and nothing at all when the body sends nothing. Nested: a
        /// sequence already owed opens first and closes last.
        /// </summary>
        private static async Task DentroDeUnaSecuenciaAsync(FightInstance fight, Fighter quien, Func<Task> cuerpo)
        {
            bool abierta = false;
            var debidaAntes = fight.SequenceToOpen;
            Func<Task> abrir = null;
            abrir = async () =>
            {
                if (abierta) return;
                abierta = true;
                if (debidaAntes != null) await debidaAntes();
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(quien.Id, Network.FightProtocol.ActionSequence)));
            };
            fight.SequenceToOpen = abrir;

            await cuerpo();

            if (abierta)
            {
                if (fight.SequenceToOpen == abrir) fight.SequenceToOpen = null;
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                           Network.FightProtocol.ActionSequence)));
            }
            else if (fight.SequenceToOpen == abrir)
            {
                fight.SequenceToOpen = debidaAntes;
            }
        }

        /// <summary>Todos los que están en el combate, de los dos bandos.</summary>
        /// <summary>Todos los del combate. Vive en el propio combate desde que los bandos tienen nombre.</summary>
        private static IEnumerable<Fighter> TodosLosCombatientes(FightInstance fight) => fight.Todos;

        // ═══════════════════════════════════════════════════════════════════════
        //  A quién le llega lo del combate
        // ═══════════════════════════════════════════════════════════════════════
        //
        // El motor entero se escribió para UN jugador y un socket: cada método recibe el stream de
        // quien acaba de mandar algo y le contesta a él. Contra monstruos eso es correcto -- el
        // único humano del combate es quien está hablando --, pero en un desafío hay dos y todo lo
        // que pasa tiene que verse en las dos pantallas.
        //
        // Estos dos ayudantes son la pieza que faltaba. No convierten el motor entero: lo que hoy
        // usa el stream directamente sigue llegando a uno solo, y eso está dicho donde toca.

        /// <summary>Las sesiones de las personas que están en este combate y siguen conectadas.</summary>
        /// <remarks>
        /// Sólo personas: un monstruo o un invocado no tienen pantalla. Se buscan por su id de
        /// personaje, que para un jugador es el mismo que el del combatiente.
        /// </remarks>
        private static List<GameSession> Publico(FightInstance fight)
        {
            var quienes = new List<GameSession>();
            var vistos = new HashSet<long>();

            foreach (var luchador in TodosLosCombatientes(fight))
            {
                if (luchador.IsMonster || luchador.EsInvocado) continue;
                if (!vistos.Add(luchador.Id)) continue;

                var sesion = SessionRegistry.FindByCharacter(luchador.Id);
                if (sesion != null) quienes.Add(sesion);
            }

            return quienes;
        }

        /// <summary>La misma trama a todos los del combate.</summary>
        private static async Task ATodosAsync(FightInstance fight, byte[] frame)
        {
            await AbrirLoDebidoAsync(fight);
            foreach (var sesion in Publico(fight))
            {
                try
                {
                    await sesion.SendAsync(frame);
                }
                catch (Exception ex)
                {
                    // Que a uno se le haya caído el socket no puede dejar al otro sin su trama.
                    Program.LogDebug($"[Combate] No se ha podido escribir a {sesion.Id}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// A frame for the people of ONE side: whoever is looking from the other side does
        /// not get it. What the Tymadura's visibility switch needs -- see the cast below.
        /// </summary>
        private static async Task ASuBandoAsync(FightInstance fight, int teamId, byte[] frame)
        {
            await AbrirLoDebidoAsync(fight);
            foreach (var sesion in Publico(fight))
            {
                var suyo = fight.Buscar(sesion.State.CharacterId);
                if (suyo == null || suyo.TeamId != teamId) continue;
                try
                {
                    await sesion.SendAsync(frame);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Combate] No se ha podido escribir a {sesion.Id}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The sequence owed to the clients, if any, opened now that a frame is about to go out.
        /// Cleared BEFORE it runs: the jto it sends comes back through here.
        /// </summary>
        private static async Task AbrirLoDebidoAsync(FightInstance fight)
        {
            var abrir = fight?.SequenceToOpen;
            if (abrir == null) return;
            fight.SequenceToOpen = null;
            await abrir();
        }

        /// <summary>
        /// Una ficha (jxw) a todos, con la marca de «esto es tuyo» puesta para cada uno.
        /// </summary>
        /// <remarks>
        /// El jxw lleva un campo que dice si el combatiente del que habla es el que maneja quien lo
        /// recibe, y el cliente lo usa para saber a qué barra aplicar el número. O sea que la trama
        /// NO es la misma para los dos: hay que construir una por persona.
        ///
        /// Se mandaba una sola con esa marca calculada contra la sesión que estuviera atendiéndose
        /// —la de quien hubiera mandado la trama que disparó todo esto—, así que el otro recibía
        /// sus propios puntos marcados como ajenos, o los del rival marcados como suyos. Es de
        /// donde salía el «una flecha helada deja a Dragon-Lord con 2 PA»: los puntos que se
        /// pintaban no eran los de quien miraba.
        /// </remarks>
        private static Task FichaATodosAsync(FightInstance fight, long fighterId,
            params (int Characteristic, long Base, long Gear, long Buff)[] cambios)
            => ACadaUnoAsync(fight, sesion => WriteFrameAsync(sesion.Stream,
                   ConnectionProtocol.Push(Op.Jxw,
                       Network.FightProtocol.BuildFighterSheet(
                           fighterId, cambios, fighterId == sesion.State.CharacterId))));

        /// <summary>A cada uno lo suyo, construido desde su propio contexto de sesión.</summary>
        /// <remarks>
        /// Hace falta para todo lo que sale de <see cref="GameState"/> —la barra de hechizos, las
        /// recargas, las características— porque eso es de quien mira y no del combate. Empujar su
        /// sesión es la única forma de leerlo: <c>GameState</c> lee la conexión que se esté
        /// atendiendo.
        /// </remarks>
        private static async Task ACadaUnoAsync(FightInstance fight, Func<GameSession, Task> loSuyo)
        {
            await AbrirLoDebidoAsync(fight);
            foreach (var sesion in Publico(fight))
            {
                try
                {
                    using (SessionContext.Push(sesion))
                    {
                        await loSuyo(sesion);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Combate] Falló lo de {sesion.Id}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Vuelve a mandar la lista de combatientes (jzu).
        ///
        /// El cliente lleva ahí SU registro de quién está en el combate, y el carrusel de turnos
        /// se indexa contra esa lista —el f7 del jzc es la posición dentro de ella—. El emulador
        /// la mandaba UNA vez, en la colocación, y nunca más; por eso una baliza invocada a mitad
        /// de combate no aparecía por ninguna parte aunque su paquete de invocación fuera
        /// correcto, y un muerto no salía nunca del carrusel.
        ///
        /// El servidor real la reenvía entera detrás de cada invocación y de cada muerte: medido
        /// sobre los 37 ficheros del Ocra, 18 jwe f14=181 más 46 jwe f14=103 son 64 sucesos, y en
        /// los 64 el paquete siguiente es un jzu. Ninguna excepción.
        /// </summary>
        private static async Task ReenviarLaListaAsync(NetworkStream stream, FightInstance fight)
        {
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jzu,
                Network.FightProtocol.BuildTeams(CarouselOrder(fight))));
        }

        /// <summary>
        /// The jzu list IN PLAY ORDER, which is what the carousel is.
        /// </summary>
        /// <remarks>
        /// It went out as blue team then red team, and the turn order is by initiative, so in
        /// a duel where the red player was faster the list said "the Rogue, then the Ocra" while
        /// the first jzc named the Ocra with no f7 -- slot zero. The carousel is indexed against
        /// this list, so it lit the Rogue up while the Ocra was playing.
        ///
        /// Measured in both challenge captures: the jzu of the placement phase already lists the
        /// first player first (293213045026 then 302677754146, and the first jzc is for
        /// 293213045026), and the fight-start jxb keeps the same order.
        /// </remarks>
        private static List<long> CarouselOrder(FightInstance fight)
        {
            var ids = new List<long>();
            foreach (var f in fight.TurnOrder)
            {
                if (EntraEnElCarrusel(f) && !ids.Contains(f.Id)) ids.Add(f.Id);
            }
            // Anybody alive that the turn order has not caught up with yet goes at the end, so
            // that a list is never shorter than the fight.
            foreach (var f in fight.Azul) if (EntraEnElCarrusel(f) && !ids.Contains(f.Id)) ids.Add(f.Id);
            foreach (var f in fight.Rojo) if (EntraEnElCarrusel(f) && !ids.Contains(f.Id)) ids.Add(f.Id);
            return ids;
        }

        /// <summary>
        /// Whether this fighter belongs in the jzu list, which is the carousel.
        ///
        /// A SUMMON THAT NEVER PLAYS IS NOT IN IT. The carousel is indexed against jzu -- the f7
        /// of jzc is a position inside that list -- so listing something that never gets a turn
        /// leaves a portrait in the strip that nothing ever highlights. That is what put a bomb
        /// in the Rogue's carousel.
        ///
        /// Measured over the class captures: 52 summoned templates, 219 summons. Whether a summon
        /// appears in any jzu matches whether it ever receives a jzc, with no counterexample in
        /// either direction -- seven templates are listed without having played, all of them in
        /// fights that ended first, and NOT ONE plays without being listed. The three Rogue bombs
        /// are 66 summons, zero jzu, zero turns; the Ocra's Tactical Beacon 6 and 0; his Survival
        /// Beacon, which does heal itself every turn, 3 and 3.
        /// </summary>
        internal static bool EntraEnElCarrusel(Fighter f) => f.IsAlive && (!f.EsInvocado || f.JuegaTurno);

        /// <summary>Characteristic 26, displayed as summon capacity by the client.</summary>
        private const int CaracteristicaDeInvocaciones = 26;

        /// <summary>Every player can control one summon before equipment and buffs.</summary>
        internal const int BasePlayerSummonLimit = 1;

        /// <summary>
        /// Splits the innate player point from equipment, in the same base/gear columns the rest
        /// of the combat sheet uses.
        /// </summary>
        /// <remarks>
        /// A monster reads ZERO here, and that is worth saying because the obvious reading is that
        /// it takes the value from its template. It does not: <c>Fighter.Otras</c> is written in
        /// exactly one place, <c>RellenarLaFicha</c>, and that runs for the player fighter and
        /// nobody else. So <see cref="SummonLimitFor"/> returns 0 for every monster and every
        /// summon, and the <c>limit &gt; 0</c> guard reads 0 as "no cap" -- which is the behaviour
        /// this server already had and which this change does not alter either way.
        ///
        /// Left as it is on purpose rather than quietly given a number: what a monster's real
        /// summon cap should be is not in any data this repository holds, and inventing one would
        /// change every fight against a summoner.
        /// </remarks>
        internal static (long Base, long Gear) SummonCharacteristicFor(Fighter fighter)
            => fighter.IsMonster
                ? (0, fighter.Otra(CaracteristicaDeInvocaciones))
                : (BasePlayerSummonLimit, fighter.Otra(CaracteristicaDeInvocaciones));

        /// <summary>
        /// Returns the simultaneous summon capacity. Player equipment is already stored in
        /// Otras[26] by RellenarLaFicha, so reading StatsHandler again would count it twice.
        /// </summary>
        /// <summary>
        /// Whether one more of this template fits the caster's summon limit: the check the summon
        /// itself goes through, asked beforehand by the tactics.
        /// </summary>
        internal static bool FitsTheSummonLimit(FightInstance fight, Fighter caster, int template, int grade)
        {
            var recipe = Managers.Summons.De(template, grade);
            if (recipe == null) return false;
            int limit = SummonLimitFor(caster, fight.RoundNumber);
            return !(limit > 0 && recipe.SummonCost > 0 && UsedSummonCapacity(fight, caster) + recipe.SummonCost > limit);
        }

        internal static int SummonLimitFor(Fighter fighter, int round)
        {
            int innate = fighter.IsMonster ? 0 : BasePlayerSummonLimit;
            int equipmentOrTemplate = fighter.Otra(CaracteristicaDeInvocaciones);
            int buffs = fighter.Buffs.De(CaracteristicaDeInvocaciones, round);
            return Math.Max(0, innate + equipmentOrTemplate + buffs);
        }

        /// <summary>
        /// How much of this fighter's summon capacity is currently taken up.
        ///
        /// It ADDS UP the cost of each living summon instead of counting bodies, because a summon
        /// does not always cost one. The number is <c>MonsterTemplates.summonCost</c> and in
        /// world.db it is 1 for 4,640 templates, <b>0 for 485</b>, 2 for four and 3 for five.
        ///
        /// Counting bodies is what let a Rogue place a single bomb and no more: his three bombs
        /// cost zero each, so all three fit alongside a real summon, and the client says so —
        /// measured over the 22 Rogue captures, 62 bombs summoned and the board holds three at
        /// once in seven of them, never four.
        /// </summary>
        internal static int UsedSummonCapacity(FightInstance fight, Fighter owner)
        {
            int used = 0;
            foreach (var f in TodosLosCombatientes(fight))
            {
                if (f.EsInvocado && f.IsAlive && f.Invocador == owner.Id) used += f.SummonCost;
            }
            return used;
        }

        /// <summary>The bomb templates, as the Rogue's own spells name them.</summary>
        /// <remarks>
        /// Not a list somebody wrote: it is the target mask of every bomb spell in world.db.
        /// Explobomba, Bombas de agua, Sismobomba and Detonador all carry
        /// <c>a,P,F3112,F3113,F3114,F5161</c>, which is the game saying "these four are bombs".
        /// Tymobot (3120) shares their race 220 and is NOT here, and that is the point of taking
        /// the list from the masks rather than from the race: it plays turns like any summon.
        /// </remarks>
        internal static bool EsBomba(int plantilla) => Managers.Bombs.Is(plantilla);

        /// <summary>How many bombs this fighter already has on the board.</summary>
        internal static int ActiveBombCount(FightInstance fight, Fighter owner)
        {
            int bombs = 0;
            foreach (var f in TodosLosCombatientes(fight))
            {
                if (f.EsInvocado && f.IsAlive && f.Invocador == owner.Id &&
                    EsBomba(f.MonsterId)) bombs++;
            }
            return bombs;
        }

        /// <summary>
        /// The Rogue's bombs cap at three on the board at once.
        /// </summary>
        /// <remarks>
        /// MEASURED, not assumed: across the 22 Tymador captures the Rogue summons 62 bombs and
        /// the peak on the board is three, reached in seven separate fights and never exceeded.
        /// The number is not in world.db anywhere -- there is no bomb characteristic and the
        /// spells' MaxStack is zero -- so it lives here with its evidence rather than being
        /// derived from a field that does not exist.
        ///
        /// What the real server does with the FOURTH cast is NOT this: instead of refusing it, it
        /// detonates on the spot for the spell's area damage, the same as casting a bomb onto an
        /// occupied cell. That needs effect 1009 "Activa una bomba", which this engine does not
        /// implement yet, so for now the cast is refused before it costs anything.
        /// </remarks>
        internal const int MaxBombsOnBoard = 3;

        /// <summary>
        /// Pays a cast only after an immediate summon has passed its capacity check. Keeping the
        /// check and the AP mutation in this method makes their ordering structural and testable.
        /// </summary>
        /// <remarks>
        /// THE WHOLE CAST IS REFUSED, not just its summon half, and that is the part to argue with
        /// if this ever looks wrong. Measured in bases/world.db: of the spell levels carrying
        /// effect 181, 1,129 carry nothing else and <b>750 carry it alongside other effects</b> --
        /// among them the Ocra's Baliza Tactica, spell 32467, whose effects are
        /// [181, 6, 1103, 141, 138, 1160]. So at capacity this also refuses those five.
        ///
        /// That matches how the client behaves -- a summon spell greys out when you are at the
        /// cap, which is the whole spell and not part of it -- but no capture in this repository
        /// shows a cast attempted at capacity, so it is an inference and is named as one. What
        /// would settle it: a capture of a player at their summon cap casting a spell that both
        /// summons and does something else.
        /// </remarks>
        internal static async Task<bool> TryPayCastCostAsync(
            FightInstance fight,
            Fighter caster,
            IEnumerable<SpellEffect> effects,
            int cost,
            Func<byte[], Task> sendAsync)
        {
            // What this cast is about to put on the board, and what it will cost in capacity.
            // A summon that costs nothing -- every bomb, every beacon -- is not what this check
            // is for and must not be refused by it.
            int incoming = 0;
            bool bombIncoming = false;
            foreach (var effect in effects)
            {
                if (!Managers.EffectEngine.EsInvocacion(effect.EffectId)) continue;
                if (effect.DiceNum <= 0) continue;
                if (!effect.Disparadores().Any(trigger =>
                        string.Equals(trigger, Managers.EffectEngine.AlLanzar,
                                      StringComparison.OrdinalIgnoreCase))) continue;

                if (EsBomba(effect.DiceNum)) bombIncoming = true;
                var receta = Managers.Summons.De(effect.DiceNum, 1);
                incoming += receta?.SummonCost ?? 1;
            }

            // A fourth bomb is refused HERE and not later, so it does not cost the AP of a cast
            // that puts nothing on the board. This is not what the real server does -- there the
            // fourth detonates on the spot -- and it stays a refusal only until effect 1009
            // "Activa una bomba" exists. Silence that also eats 2 AP would be worse than either.
            if (bombIncoming && ActiveBombCount(fight, caster) >= MaxBombsOnBoard)
            {
                Program.LogDebug($"[Fight] Fighter {caster.Id} is at {MaxBombsOnBoard} bombs; " +
                                 "the cast is refused before paying, pending effect 1009.");
                return false;
            }

            if (incoming > 0)
            {
                int limit = SummonLimitFor(caster, fight.RoundNumber);
                int active = UsedSummonCapacity(fight, caster);
                if (limit > 0 && active + incoming > limit)
                {
                    // Sin filtrar por quien es: aqui solo se llega desde CastAsync, que es el
                    // lanzamiento del jugador. El filtro hace falta en InvocarAsync, que si se
                    // alcanza desde el turno del monstruo.
                    await SendSummonLimitWarningAsync(sendAsync, limit);

                    Program.LogDebug($"[Fight] Fighter {caster.Id} cannot cast a summon: " +
                                     $"{active} active summon(s), capacity {limit}.");
                    return false;
                }
            }

            caster.CurrentAP -= cost;
            return true;
        }

        private static Task SendSummonLimitWarningAsync(Func<byte[], Task> sendAsync, int limit)
        {
            byte[] packet = ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Warning,
                    InfoMessages.SummonLimitReached,
                    limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return sendAsync(packet);
        }

        /// <summary>
        /// Si un hechizo tiene algo que hacer al empezar el turno: un efecto con disparador "TB",
        /// o un enganche 792 cuyo grado encadenado lo tenga.
        /// </summary>
        private static bool TieneAlgoQueHacerAlEmpezar(int hechizo, int grado)
        {
            if (hechizo == 0) return false;
            foreach (var efecto in Managers.SpellEffects.De(hechizo, Math.Max(1, grado)))
            {
                foreach (var d in efecto.Disparadores())
                {
                    if (string.Equals(d, Managers.EffectEngine.AlEmpezarElTurno,
                                      StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// La casilla en la que cabe un invocado: la que se pide, y si está pillada, la vecina
        /// libre más cercana.
        /// </summary>
        private static int CasillaLibreCerca(FightInstance fight, int deseada)
        {
            if (!Occupied(fight, deseada) && MapGeometry.IsValid(deseada)) return deseada;
            foreach (int vecina in MapGeometry.GetNeighbors(deseada))
            {
                if (!Occupied(fight, vecina)) return vecina;
            }
            foreach (int vecina in MapGeometry.GetNeighbors(deseada))
            {
                foreach (int lejos in MapGeometry.GetNeighbors(vecina))
                {
                    if (!Occupied(fight, lejos)) return lejos;
                }
            }
            return -1;
        }

        /// <summary>A quién le toca el golpe: el enemigo vivo que esté en esa casilla, si lo hay.</summary>
        /// <summary>
        /// Decirle al cliente que a alguien le han quitado PA o PM, para que salga el numerito
        /// flotando encima igual que con la vida.
        ///
        /// Sólo cuando se QUITAN: si el efecto da puntos, el mensaje medido es otro y no está
        /// atado a un caso concreto, así que no se manda nada antes que mandar el que no es.
        /// </summary>
        /// <summary>
        /// Refrescarle al jugador la vida que le falta.
        ///
        /// Sólo a él: el cliente lleva la barra de todos los demás por su cuenta, descontando los
        /// golpes que ve pasar, y la suya la saca del tope más esta característica. En las 305
        /// capturas no hay ni un solo envío de la 97 para un monstruo ni para el jugador rival.
        ///
        /// Va envuelta en su jto/jwi, como cualquier ficha suelta.
        /// </summary>
        private static async Task RefrescarLaVidaAsync(NetworkStream stream, FightInstance fight,
                                                       Fighter quien, Fighter porQuien)
        {
            // Only a player carries the sheet; monsters and summons never get a 97.
            if (quien == null || quien.IsMonster || quien.EsInvocado) return;

            // AFTER EVERY CHANGE OF LIFE, WHOEVER CAUSED IT. This was gated to "only when he did
            // it to himself" for one night, on the strength of the real captures -- sixteen hits
            // and two 97 in a whole challenge -- and the night proved that THIS client does not
            // move its own bar without it: no regeneration, no 97, and a Rogue at 2400 of 2390
            // after 110 of damage, his own tooltip reading 100%. How the real client keeps its
            // own life with two 97 a fight is not measured; what moves this one is. The 24-08
            // note had it right: "sin esto le pegaban toda la pelea y su barra seguia llena".
            //
            // TO HIS OWN CLIENT, whoever is acting. The old guard compared against the character
            // of the CONNECTION being served, so in a duel the one being hit never got his sheet.
            // The measured rule stays: each client receives its own 97 and nobody else's.
            //
            // The parameter is kept for the log and for the day the real rule is understood.
            _ = porQuien;

            await ACadaUnoAsync(fight, async sesion =>
            {
                if (sesion.State.CharacterId != quien.Id) return;

                await WriteFrameAsync(sesion.Stream, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(quien.Id,
                                                             Network.FightProtocol.SheetSequence)));
                await WriteFrameAsync(sesion.Stream, ConnectionProtocol.Push(Op.Jxw,
                    Network.FightProtocol.BuildLifeSheet(quien.Id,
                                                         quien.CurrentHP - (quien.MaxHP + quien.VidaErosionada),
                                                         0)));
                await WriteFrameAsync(sesion.Stream, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                           Network.FightProtocol.SheetSequence)));
            });
        }

        /// <summary>
        /// Quien esta en la casilla apuntada, del bando que sea.
        ///
        /// Antes solo miraba al bando CONTRARIO, asi que apuntar a un aliado -o a uno mismo, que
        /// es como se echan los embrujos- no devolvia a nadie: el tope por objetivo no contaba y
        /// el bloque del objetivo no viajaba. A quien le toca el efecto lo decide luego la mascara
        /// del propio hechizo, que para eso esta.
        /// </summary>
        private static Fighter VictimAt(FightInstance fight, Fighter caster, int cell)
        {
            foreach (var uno in TodosLosCombatientes(fight))
            {
                if (uno.CellId == cell && uno.IsAlive && !uno.EstaCargado) return uno;
            }
            return null;
        }

        /// <summary>The moves that travel as a jwe 4: the plain teleport and the symmetric ones.</summary>
        private static bool EsTeletransporte(int efecto)
            => efecto is Jondo.Unity.World.Combat.EffectSupport.Teleport or 1099 or 1100 or 1104 or 1105 or 1106;

        /// <summary>
        /// The copies of a fighter go: the switch back to visible and one 1029 per copy, in a
        /// sequence of their own (jto 6 in the capture), and off the board. At his turn start
        /// all of them, when he is hit all of them, when one is hit that one (see
        /// IllusionHitAsync).
        /// </summary>
        private static async Task DesvanecerLasIlusionesAsync(NetworkStream stream, FightInstance fight,
                                                               Fighter dueno)
        {
            if (dueno == null) return;
            if (dueno.Ilusiones.Count == 0)
            {
                // The copies went one by one, but he is still drawn as hidden on his own
                // side: the switch back is owed all the same, or he stays translucent for
                // the rest of the fight.
                if (dueno.HiddenAmongCopies) await VolverAVerseAsync(fight, dueno);
                return;
            }

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(dueno.Id, Network.FightProtocol.TurnStartSequence)));
            await VolverAVerseAsync(fight, dueno);
            foreach (long id in dueno.Ilusiones.ToList())
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildIllusionGone(dueno.Id, id)));
                fight.Quitar(fight.Buscar(id));
            }
            dueno.Ilusiones.Clear();
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), dueno.Id,
                                                       Network.FightProtocol.TurnStartSequence)));
            Program.LogDebug($"[Combate] Se desvanecen las ilusiones de {dueno.Id}.");
        }

        /// <summary>
        /// The switch back to visible, to his own side, the only side that ever saw him
        /// hidden. Idempotent: nothing goes out when he is not hidden.
        /// </summary>
        private static async Task VolverAVerseAsync(FightInstance fight, Fighter dueno)
        {
            if (!dueno.HiddenAmongCopies) return;
            dueno.HiddenAmongCopies = false;
            await ASuBandoAsync(fight, dueno.TeamId, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildVisibility(dueno.Id, dueno.Id, Network.FightProtocol.Visible)));
        }

        /// <summary>
        /// The switch back to visible once the last invisibility row (150) of a fighter is gone:
        /// "jya 275, jwe 150 f34{f1=2 f4=Sram}" at frames 316-317 of "sram-invisibilidad", when
        /// the row ran out. Nothing goes out while another row still hides him.
        /// </summary>
        private static async Task VisibleOtraVezAsync(FightInstance fight, Fighter author, Fighter quien)
        {
            if (quien == null) return;
            if (quien.Buffs.Puestos.Any(b => b.EffectId == Jondo.Unity.World.Combat.EffectSupport.Visibility && !b.Pendiente)) return;
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildVisibility((author ?? quien).Id, quien.Id, Network.FightProtocol.Visible)));
        }

        /// <summary>
        /// A copy takes a hit: it goes, and nothing else happens to it. The class sheet: "al
        /// primer golpe de daño"; a poison or anything that is not damage leaves it be, which
        /// is what <paramref name="fromTurnTrigger"/> tells apart. When it was the last one
        /// the original is shown again: with no copy left there is nobody to hide among.
        /// </summary>
        private static async Task IllusionHitAsync(NetworkStream stream, FightInstance fight, Fighter copia)
        {
            var dueno = fight.Buscar(copia.Invocador);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildIllusionGone(copia.Invocador, copia.Id)));
            fight.Quitar(copia);
            dueno?.Ilusiones.Remove(copia.Id);
            Program.LogDebug($"[Combate] La ilusión {copia.Id} se desvanece al recibir un golpe.");

            if (dueno != null && dueno.Ilusiones.Count == 0) await VolverAVerseAsync(fight, dueno);
        }

        /// <summary>
        /// Whoever this fighter carries goes where he goes; whoever he carried is set down where
        /// he fell. Called after every step and every death.
        /// </summary>
        private static void CarriedFollows(FightInstance fight, Fighter carrier)
        {
            if (carrier == null || carrier.Carrying == 0) return;
            var carried = fight.Buscar(carrier.Carrying);
            if (carried == null) { carrier.Carrying = 0; return; }
            carried.CellId = carrier.CellId;
            if (!carrier.IsAlive)
            {
                carried.CarriedBy = 0;
                carrier.Carrying = 0;
                carrier.Buffs.QuitarEstado(Jondo.Unity.World.Combat.EffectSupport.CarryingState);
                carried.Buffs.QuitarEstado(Jondo.Unity.World.Combat.EffectSupport.CarriedState);
            }
        }

        /// <summary>
        /// Lo que un hechizo deja puesto, y contárselo al cliente.
        ///
        /// El motor decide qué pasa leyendo el EffectsJson del hechizo y el catálogo de efectos;
        /// aquí sólo se manda por el cable: un jxm por cada embrujo, para que salga en el panel, y
        /// una ficha por cada característica que haya cambiado, para que se vea el número.
        ///
        /// Los puntos de acción y de movimiento se tocan además EN EL ACTO, porque un "+1 PA" o un
        /// "-2 PA" no es un adorno del panel: cambia lo que te queda para jugar ese turno.
        /// </summary>
        /// <param name="tirada">
        /// The cast's draw of the random rows, the same one the blows were dealt from. Null
        /// for anything that is not a cast with blows of its own.
        /// </param>
        /// <param name="rondaDelEnganche">
        /// For a trigger fired off a hooked spell, the round the hook was put in; negative at
        /// a cast.
        /// </param>
        internal static async Task AplicarEfectosAsync(NetworkStream stream, FightInstance fight,
                                                      Fighter quienLanza, int hechizo, int grado,
                                                      Fighter objetivo, string disparador,
                                                      int celdaApuntada = -1, bool critico = false,
                                                      IReadOnlyList<Managers.SpellEffect> tirada = null,
                                                      int rondaDelEnganche = -1,
                                                      bool armar = true, bool soloAlObjetivo = false,
                                                      bool conducta = false)
        {
            if (hechizo == 0) return;

            // The size of every bomb before anything happens, so that a combo granted from
            // inside a spell -- Mosquete, Kabúm, Último Aliento, a Detonador -- redraws the bomb
            // the way the turn-start combo does. The look is the one thing the client does not
            // work out from the 1060 buff on its own.
            var tamanosAntes = new Dictionary<long, int>();
            foreach (var bomba in TodosLosCombatientes(fight))
            {
                if (bomba != null && bomba.IsAlive && EsBomba(bomba.MonsterId))
                    tamanosAntes[bomba.Id] = Managers.Combo.SizeOf(bomba, fight.RoundNumber);
            }

            List<Managers.Outcome> consecuencias;
            try
            {
                consecuencias = Managers.EffectEngine.Resolver(fight, quienLanza, hechizo, grado,
                                                                 objetivo, disparador, fight.RoundNumber,
                                                                 hondo: 0,
                                                                 celdaApuntada: celdaApuntada,
                                                                 critico: critico,
                                                                 efectosSorteados: tirada,
                                                                 rondaDelEnganche: rondaDelEnganche,
                                                                 armar: armar, soloAlObjetivo: soloAlObjetivo);
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Combate] El motor de efectos se ha atragantado con el hechizo " +
                                 $"{hechizo}: {ex.Message}");
                return;
            }
            if (consecuencias.Count == 0) return;

            // Si el hechizo tiene algo pendiente para más adelante —efectos con un disparador que
            // no es "al lanzar"— se deja apuntado sobre quien lo lleva, para poder dispararlo
            // cuando toque. Es lo que hace falta para el Centinela: sus bajadas de alcance sólo
            // ocurren al andar, y sin acordarse de que el hechizo sigue puesto no hay manera.
            // At a cast, the spell and everything it chained; off a trigger, only what it
            // chained -- the fired spell keeps the hook it has. Furor's decay casts 28604 at
            // grade 3, whose own "1160 under TE" is what takes Furor I away a turn later.
            bool alLanzar = string.Equals(disparador, Managers.EffectEngine.AlLanzar, StringComparison.OrdinalIgnoreCase);
            EngancharLoPendiente(consecuencias, hechizo, grado, quienLanza.Id, fight.RoundNumber,
                                 incluirElPropio: alLanzar, critico: critico, conducta: conducta);

            var fichas = new HashSet<(long Quien, int Caracteristica)>();
            var vidasCambiadas = new Dictionary<long, Fighter>();

            // What this cast sets off on the ones it touches -- a state put or taken, a push, a
            // teleport, points lost, a heal -- fired once everything has been told, in order.
            var disparos = new List<(Fighter Quien, string Disparador, Fighter Fuente)>();
            var yaDisparados = new HashSet<(long, string)>();
            // Once per bearer and trigger; per bearer, trigger and source for the ones that count
            // each fighter they touched.
            void Disparo(Fighter quienSalta, string queSalta, Fighter fuente, bool unaVezPorFuente = false)
            {
                string clave = unaVezPorFuente && fuente != null ? queSalta + "@" + fuente.Id : queSalta;
                if (quienSalta != null && yaDisparados.Add((quienSalta.Id, clave))) disparos.Add((quienSalta, queSalta, fuente));
            }

            // EVERY CHAINED CAST IS ANNOUNCED, once, before the first thing it does: the real
            // server sends a jwe 300 for each spell a 792 or a 1160 sets off -- "20577 by the
            // Tymador, 20497 by the bomb, 20500 by the bomb" at every combo -- and the client
            // draws the combo off that cast, not off the buff. Damage chains announce their own
            // per target (the rebound's animation, below) and are left alone here.
            // Per spell AND grade: Furor's cast announces 28604 twice, grade 1 (level 76314)
            // and then grade 3 (76316), and its decay announces the grade it falls to.
            var conDano = new HashSet<(int, int, long)>();
            foreach (var c in consecuencias)
            {
                if (c.NestedDamage && (c.HechizoOrigen, c.NivelOrigen) != (hechizo, grado))
                    conDano.Add((c.HechizoOrigen, c.NivelOrigen, (c.Caster ?? quienLanza).Id));
            }
            var anunciados = new HashSet<(int, int, long)>();

            // lo pisa, y entonces el orden deja de tener sentido.
            // ya en su sitio. No dentro del recorrido: un glifo puede volver a mover a quien
            // A quien ha movido este lanzamiento, para pisarle el suelo cuando esten todos
            var movidos = new List<Fighter>();

            foreach (var c in consecuencias)
            {
                if (c.HechizoOrigen != 0 && (c.HechizoOrigen, c.NivelOrigen) != (hechizo, grado))
                {
                    var quienEncadena = c.Caster ?? quienLanza;
                    var clave = (c.HechizoOrigen, c.NivelOrigen, quienEncadena.Id);
                    if (!conDano.Contains(clave) && anunciados.Add(clave))
                    {
                        await AnunciarElEncadenadoAsync(fight, quienEncadena, c.Sobre ?? quienEncadena,
                                                        c.HechizoOrigen, c.NivelOrigen, c.CeldaDelLanzamiento);
                    }
                }

                if (c.EnganchePendiente) continue;

                if (c.CambiaLaRecarga)
                {
                    if (!c.Sobre.IsMonster)
                    {
                        var suya = SessionRegistry.FindByCharacter(c.Sobre.Id);
                        if (suya != null)
                            await suya.SendAsync(ConnectionProtocol.Push(Op.Jxc,
                                Network.FightProtocol.BuildCooldowns(c.Sobre.Id, RecargasDe(c.Sobre))));
                    }
                    continue;
                }

                if (c.ActivaSuelo != 0)
                {
                    var suyos = fight.Glifos.Where(g => g.Dueno == (c.Caster ?? quienLanza).Id
                        && (c.ActivaSuelo == Managers.EffectEngine.ActivaLasRunas ? g.Tipo == 2022 : g.Tipo != 2022 && g.Tipo != 400))
                        .ToList();
                    foreach (var glifo in suyos)
                    {
                        foreach (var quien in TodosLosCombatientes(fight).Where(f => f != null && f.IsAlive && glifo.Cubre(f.CellId)).ToList())
                        {
                            if (!GlyphCatches(fight, glifo, quien, false)) continue;
                            await FireOneGlyphAsync(stream, fight, glifo, quien, alPisar: false);
                        }
                    }
                    continue;
                }

                if (c.Revive > 0)
                {
                    await ResucitarAsync(stream, fight, c.Caster ?? quienLanza, c.HechizoOrigen, c.NivelOrigen,
                                         c.Efecto.EffectId, c.Revive,
                                         c.CasillaDeLaInvocacion >= 0 ? c.CasillaDeLaInvocacion : celdaApuntada);
                    continue;
                }

                // A glyph, a trap, an aura laid by the spell: shown, with its cells and the colour
                // its row carries. It fired unseen before -- the player died on a glyph he had no
                // way of seeing.
                if (c.Glifo != null)
                {
                    var dueno = c.Caster ?? quienLanza;
                    int apuntada = c.Glifo.Centro >= 0 ? c.Glifo.Centro : c.Glifo.Casillas.FirstOrDefault();
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildSpellGlyph(dueno.Id, c.Glifo.Id, c.Glifo.Casillas, apuntada,
                                                              c.Glifo.Hechizo, c.HechizoOrigen, c.NivelOrigen,
                                                              c.Glifo.Color)));
                    Program.LogDebug($"[Combate] Glifo {c.Glifo.Id} de {dueno.Id}: {c.Glifo.Casillas.Count} casilla(s), " +
                                     $"lanza {c.Glifo.Hechizo}, color {c.Glifo.Color:X6}, cae en la ronda {c.Glifo.CaducaEnRonda}.");
                    continue;
                }

                // The portals (FightPortals.cs): laid, switched off, gone through.
                if (c.PortalAt >= 0)
                {
                    await LayPortalAsync(fight, c.Caster ?? quienLanza, c);
                    continue;
                }
                if (c.PortalsOffAt != null)
                {
                    await SwitchPortalsOffAsync(fight, c.Caster ?? quienLanza, c.PortalsOffAt);
                    continue;
                }
                if (c.Teleportal)
                {
                    if (await CrossPortalAsync(stream, fight, c.Sobre, walkedIn: false)) movidos.Add(c.Sobre);
                    continue;
                }

                // Lazo Espiritual's bond (2184): the bearer walks up to the caster.
                if (c.Follows > 0)
                {
                    await FollowAsync(fight, c.Sobre, c.Caster ?? quienLanza, c.Follows);
                    continue;
                }

                // What the target dodged of a removal goes out first, and a removal dodged
                // whole is nothing more than that.
                if (c.PuntosEsquivados > 0)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildPointsDodged(quienLanza.Id,
                            c.Caracteristica == ActionPointsCharacteristic || c.Efecto.EffectId is 1079 or 101 or 84 or 440
                                ? ActionPointsCharacteristic : MovementPointsCharacteristic,
                            c.Sobre.Id, c.PuntosEsquivados)));
                    Program.LogDebug($"[PUNTOS] {c.Sobre.Id} esquiva {c.PuntosEsquivados} punto(s) del efecto " +
                                     $"{c.Efecto.EffectId} del hechizo {hechizo}.");
                    if (c.Buff == null && c.Caracteristica == 0) continue;
                }

                // The 3793 going off now: an action on the target's cell, and nothing kept.
                if (c.Marcador)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildScriptMarker(quienLanza.Id, c.NivelOrigen, c.Sobre.CellId,
                                                                c.HechizoOrigen, c.Efecto.Value)));
                    continue;
                }

                // El 141: mata, y por el mismo camino que un golpe, para que se anuncie igual,
                // se le caigan las invocaciones igual y el combate termine igual.
                if (c.Fulmina)
                {
                    if (!c.Sobre.IsAlive) continue;

                    Program.LogDebug($"[Combate] {(c.Caster ?? quienLanza).Id} fulmina a " +
                                     $"{c.Sobre.Id} con el hechizo {c.HechizoOrigen} " +
                                     $"({c.Sobre.CurrentHP} de vida).");

                    int dondeEstaba = c.Sobre.CellId;
                    await UnGolpeAsync(stream, fight, c.Caster ?? quienLanza, c.HechizoOrigen,
                                       c.Efecto, 0, c.Sobre, 0, 0, false, 0, fulmina: true);

                    // El 405, «mata y reemplaza»: la Siega saca el bicho EN LA CASILLA del que
                    // acaba de caer, no al lado del que lanza. Si el muerto no ha dejado su sitio
                    // libre, la invocación busca hueco como cualquier otra.
                    if (c.Invoca != 0)
                    {
                        await InvocarAsync(stream, fight, quienLanza, c.Invoca, grado,
                                           c.EnLaCasillaDelMuerto ? dondeEstaba : celdaApuntada,
                                           c.Efecto.EffectId);
                    }
                    continue;
                }

                // La vida que se va sin ser un golpe —el «-N% PdV»— y la que se transfiere.
                // No pasan por el motor de daño a propósito: no hay elemento, ni resistencias,
                // ni críticos que aplicar, y meterlas por ahí les inventaría los tres.
                if (c.VidaQueSeVa > 0)
                {
                    int leQuedaba = c.Sobre.CurrentHP;
                    c.Sobre.TakeDamage(c.Sobre.PasarPorElEscudo(c.VidaQueSeVa));
                    Program.LogDebug($"[Combate] A {c.Sobre.Id} se le van {c.VidaQueSeVa} de vida " +
                                     $"({leQuedaba} -> {c.Sobre.CurrentHP}) por el efecto " +
                                     $"{c.Efecto.EffectId}.");
                    await RefrescarLaVidaAsync(stream, fight, c.Sobre, quienLanza);
                    continue;
                }

                if (c.VidaTransferida > 0)
                {
                    var daLaVida = c.Caster ?? quienLanza;
                    daLaVida.TakeDamage(c.VidaTransferida);
                    c.Sobre.CurrentHP = Math.Min(c.Sobre.MaxHP,
                                                 c.Sobre.CurrentHP + c.VidaTransferida);

                    Program.LogDebug($"[Combate] {daLaVida.Id} le pasa {c.VidaTransferida} de vida " +
                                     $"a {c.Sobre.Id}.");

                    await RefrescarLaVidaAsync(stream, fight, daLaVida, quienLanza);
                    await RefrescarLaVidaAsync(stream, fight, c.Sobre, quienLanza);
                    continue;
                }

                if (c.NestedDamage)
                {
                    Fighter animationCaster = c.AnimationCaster ?? c.Caster ?? quienLanza;
                    var animationGrade = LimitesDeGrado(c.HechizoOrigen, c.NivelOrigen);
                    if (animationGrade.LevelId > 0)
                    {
                        // A TODOS, no al socket que atiende. La animacion del rebote la tienen
                        // que ver los dos bandos: contra monstruos daba igual porque solo hay una
                        // persona mirando, pero en un desafio o en un koliseo el rival se quedaba
                        // sin ver de donde a donde salta la flecha.
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                            Network.FightProtocol.BuildAction(
                                animationCaster.Id,
                                Network.FightProtocol.Cast,
                                Network.FightProtocol.CastAt(
                                    animationCaster.Id, c.Sobre.Id, c.Sobre.CellId,
                                    c.HechizoOrigen, animationGrade.LevelId,
                                    c.CriticalDamage),
                                Network.FightProtocol.CastDetail)));
                        Program.LogDebug($"[Fight] Nested spell {c.HechizoOrigen} animation " +
                                         $"{animationCaster.Id} -> {c.Sobre.Id} on cell " +
                                         $"{c.Sobre.CellId} (level {animationGrade.LevelId}).");
                    }

                    int lifeBefore = c.Sobre.CurrentHP;
                    await UnGolpeAsync(
                        stream, fight, c.Caster ?? quienLanza, c.HechizoOrigen, c.Efecto,
                        c.DamageElement, c.Sobre, TirarElDado(c.Efecto), c.DamageDistance,
                        c.CriticalDamage, c.DamageSpellBonus,
                        fromTurnTrigger: disparador != Managers.EffectEngine.AlLanzar);
                    if (c.Sobre.IsAlive && c.Sobre.CurrentHP != lifeBefore)
                        await RefrescarLaVidaAsync(stream, fight, c.Sobre, quienLanza);
                    continue;
                }

                if (c.Caracteristica == ActionPointsCharacteristic || c.Caracteristica == MovementPointsCharacteristic)
                {
                    // The points in his hand change only while he is the one playing: outside
                    // his turn, what he had left when his last turn ended is nobody's business,
                    // and the row itself is what his next turn starts with. No jwe 101/127 for
                    // it: in the 74 removals of the class captures none travels, the sheet and
                    // the row do the telling.
                    //
                    // TRAZA para medir la retirada de puntos: si el número que se ve en pantalla
                    // no cuadra con éste, el desajuste lo pone el cliente y no nosotros.
                    bool deAccion = c.Caracteristica == ActionPointsCharacteristic;
                    bool enSuTurno = fight.CurrentFighter == c.Sobre;
                    if (c.Cuanto < 0 && quienLanza.TeamId != c.Sobre.TeamId)
                        Disparo(c.Sobre, deAccion ? Managers.EffectEngine.AlPerderPA : Managers.EffectEngine.AlPerderPM, quienLanza);
                    // And the one who took them (CAPAS/CMPAS), once for each fighter who lost
                    // some: Zarzas Agresivas gives "1 PM al lanzador por enemigo alcanzado".
                    var quienQuita = c.Caster ?? quienLanza;
                    if (c.Cuanto < 0 && quienQuita != null && quienQuita != c.Sobre)
                        Disparo(quienQuita, deAccion ? Managers.EffectEngine.AlQuitarPA : Managers.EffectEngine.AlQuitarPM,
                                c.Sobre, unaVezPorFuente: true);
                    int antes = deAccion ? c.Sobre.CurrentAP : c.Sobre.CurrentMP;
                    if (enSuTurno)
                    {
                        if (deAccion) c.Sobre.CurrentAP = Math.Max(0, c.Sobre.CurrentAP + c.Cuanto);
                        else c.Sobre.CurrentMP = Math.Max(0, c.Sobre.CurrentMP + c.Cuanto);
                    }
                    Program.LogDebug($"[PUNTOS] {(deAccion ? "PA" : "PM")} de {c.Sobre.Id}: {antes} -> " +
                                     $"{(deAccion ? c.Sobre.CurrentAP : c.Sobre.CurrentMP)} ({c.Cuanto:+#;-#;0}) por el " +
                                     $"efecto {c.Efecto.EffectId} del hechizo {hechizo}" +
                                     (enSuTurno ? "" : " (fuera de su turno: cuenta para el siguiente)") +
                                     $"; tope {(deAccion ? c.Sobre.MaxAP : c.Sobre.MaxMP)}, embrujos encima: " +
                                     $"{c.Sobre.Buffs.De(c.Caracteristica, fight.RoundNumber)}");

                    // AP given back (120) go out at once: the sheet in its own short sequence,
                    // then "jwe 120 f20{f1=N f2=who}" -- Neutral's frames 8-11, and 116 more.
                    if (c.Efecto.EffectId == Managers.EffectEngine.DevuelvePA && c.Cuanto > 0)
                    {
                        await AnnouncePointsAsync(fight, c.Sobre, ActionPointsCharacteristic, c.Sobre.CurrentAP);
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                            Network.FightProtocol.BuildPointsGiven((c.Caster ?? quienLanza).Id, Managers.EffectEngine.DevuelvePA,
                                                                   c.Sobre.Id, c.Cuanto)));
                        continue;
                    }

                    fichas.Add((c.Sobre.Id, c.Caracteristica));
                }
                else if (c.Caracteristica != 0)
                {
                    // Y CUALQUIER OTRA caracteristica que el embrujo haya movido: la potencia, el
                    // alcance, los daños… Aqui no se tocaba nada, asi que un "+250 de potencia" se
                    // apuntaba en el motor -y el daño subia de verdad- pero el panel del cliente
                    // seguia enseñando el numero de antes, y parecia que el embrujo no hacia nada.
                    fichas.Add((c.Sobre.Id, c.Caracteristica));

                    // Range taken away by somebody else (R): Desprendimiento "ocasiona daños de
                    // tierra si el objetivo sufre una retirada de alcance".
                    if (c.Caracteristica == AlcanceCaracteristica && c.Cuanto < 0 && c.Sobre != quienLanza)
                        Disparo(c.Sobre, Managers.EffectEngine.AlPerderAlcance, quienLanza);
                }

                // The shield's sheet entry, and the caster's turn end when the effect asks for
                // it (Tymadura's 1031), once everything of this cast has gone out.
                if (c.Escudo > 0)
                {
                    fichas.Add((c.Sobre.Id, Managers.EffectEngine.ShieldCharacteristic));
                    var escudos = StatisticsBehind(fight, quienLanza);
                    if (escudos != null) escudos.ShieldsGiven += c.Escudo;
                    // And whoever put it has put a shield (CS), once for the cast.
                    Disparo(c.Caster ?? quienLanza, Managers.EffectEngine.AlPonerEscudo, c.Sobre);
                }
                if (c.AcabaElTurno) fight.EndTurnRequested = true;

                // A steal hands its points over as a row of its own on the caster (Steals), which
                // the points branch above has already put in his hand.

                // Las curaciones.
                if (c.Cura > 0)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildHeal(quienLanza.Id, c.Cura, c.Sobre.Id)));
                    AnotarLaCura(fight, quienLanza, c.Sobre, c.Cura);
                    Program.LogDebug($"[Combate] {quienLanza.Id} cura {c.Cura} a {c.Sobre.Id}; " +
                                     $"queda en {c.Sobre.CurrentHP}/{c.Sobre.MaxHP}.");

                    // El Sin Corazon: curarse uno mismo vale, que le curen a uno no.
                    await ChallengeWatcher.HealedAsync(stream, fight, quienLanza, c.Sobre);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerCurado, quienLanza);
                    Disparo(quienLanza, Managers.EffectEngine.AlCurar, c.Sobre);

                    // The absolute sheet goes out once, after every consequence.
                    vidasCambiadas[c.Sobre.Id] = c.Sobre;
                    continue;
                }

                // Los que sacan un bicho al tablero.
                if (c.Invoca != 0)
                {
                    // Owned by whoever cast the spell that summons -- a sub-cast's own caster -- at
                    // that spell's grade; and what the spell writes for it ("U") done on it.
                    var invoca = c.Caster ?? quienLanza;
                    int suGrado = (c.HechizoOrigen, c.NivelOrigen) == (hechizo, grado) ? grado : c.NivelOrigen;
                    var nuevo = await InvocarAsync(stream, fight, invoca, c.Invoca, suGrado,
                                                   c.CasillaDeLaInvocacion >= 0 ? c.CasillaDeLaInvocacion : celdaApuntada,
                                                   c.Efecto.EffectId);
                    await AplicarLoDelInvocadoAsync(stream, fight, invoca, c.HechizoOrigen, c.NivelOrigen, nuevo);
                    if (nuevo != null) await RevisarLosRecuentosAsync(stream, fight);
                    // The summoner has summoned (CI), with the new one as what set it off -- the "u"
                    // of the rows it fires: Pacto Bestial sacrifices "sus invocaciones ... futuras".
                    if (nuevo != null) Disparo(invoca, Managers.EffectEngine.AlInvocar, nuevo);
                    continue;
                }

                // A double of the caster (180), already on the board: told, and then what the
                // spell writes for it -- Doble's "1160 12966 on a,U", which gives it its control
                // row, its states and its end two turns later.
                if (c.Doble != null)
                {
                    var invoca = c.Caster ?? quienLanza;
                    await AnunciarElDobleAsync(stream, fight, invoca, c.Doble);
                    await AplicarLoDelInvocadoAsync(stream, fight, invoca, c.HechizoOrigen, c.NivelOrigen, c.Doble);
                    await RevisarLosRecuentosAsync(stream, fight);
                    Disparo(invoca, Managers.EffectEngine.AlInvocar, c.Doble);
                    continue;
                }

                // Glyphs taken off the board by a 2018: one jwe 310 each, as when they fall.
                if (c.GlifosQuitados != null)
                {
                    foreach (var g in c.GlifosQuitados)
                    {
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                            Network.FightProtocol.BuildGlyphGone(g.Dueno, g.Id)));
                    }
                    Program.LogDebug($"[Combate] {quienLanza.Id} disipa {c.GlifosQuitados.Count} glifo(s) de {c.Sobre.Id}.");
                    continue;
                }

                // Los que mueven a alguien de sitio: se anuncia adónde ha ido a parar.
                if (c.Ilusiones != null)
                {
                    // In the capture's order: the switch to hidden, the teleport, one block per
                    // copy. The copy's f7 points at the original on the cell he LEFT.
                    //
                    // THE SWITCH GOES TO HIS OWN SIDE ONLY. The capture is the Tymador's own
                    // client, and there the real server marks the original with visibility
                    // state 1, which the client draws as the translucent one among opaque
                    // copies: that is how he tells himself apart. Sent to everybody, the enemy
                    // got the same hint and the copies were pointless. What the enemy's client
                    // receives is not measured -- there is no capture from the other side --
                    // so it gets nothing, which leaves the original and the copies alike.
                    await ASuBandoAsync(fight, quienLanza.TeamId, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildVisibility(quienLanza.Id, quienLanza.Id,
                                                              Network.FightProtocol.Hidden)));
                    quienLanza.HiddenAmongCopies = true;
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildTeleport(quienLanza.Id, quienLanza.Id, c.CasillaHasta)));
                    byte[] look = NormalFightLook(quienLanza);

                    // Two blocks per copy. His own side gets the captured one -- no identity,
                    // a monster's mould of a sheet -- and the other side gets the copy dressed
                    // as him, or hovering it would show no name where hovering him shows his.
                    var ficha = DatabaseManager.GetCharacterById(quienLanza.Id);
                    var comoEl = Network.FightProtocol.PlayerIdentity(ficha?.Breed ?? 0, quienLanza.Name,
                                                                      ficha?.Sex ?? 0, quienLanza.Level);
                    var suFicha = FullSheetOf(quienLanza, conTraza: false);
                    int elOtroBando = quienLanza.TeamId == FightInstance.Azules ? FightInstance.Rojos : FightInstance.Azules;
                    foreach (var copia in c.Ilusiones)
                    {
                        await ASuBandoAsync(fight, quienLanza.TeamId, ConnectionProtocol.Push(Op.Jwe,
                            Network.FightProtocol.BuildIllusion(
                                quienLanza.Id, copia.Id, copia.CellId, FacingOf(fight, copia),
                                c.CasillaDesde, FacingOf(fight, quienLanza),
                                Network.FightProtocol.IllusionSheet(quienLanza.Level), look)));
                        await ASuBandoAsync(fight, elOtroBando, ConnectionProtocol.Push(Op.Jwe,
                            Network.FightProtocol.BuildIllusion(
                                quienLanza.Id, copia.Id, copia.CellId, FacingOf(fight, copia),
                                c.CasillaDesde, FacingOf(fight, quienLanza),
                                suFicha, look, identity: comoEl)));
                    }
                    Program.LogDebug($"[Combate] {quienLanza.Id} salta de {c.CasillaDesde} a " +
                                     $"{c.CasillaHasta} y deja {c.Ilusiones.Count} ilusiones.");
                    movidos.Add(quienLanza);
                    continue;
                }

                if (c.Carga)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildCarry(quienLanza.Id, c.CasillaDesde, c.Sobre.Id)));
                    Program.LogDebug($"[Combate] {quienLanza.Id} carga con {c.Sobre.Id} desde la casilla {c.CasillaDesde}.");
                    continue;
                }
                if (c.Lanza)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildThrow(quienLanza.Id, c.Sobre.Id, c.CasillaHasta)));
                    Program.LogDebug($"[Combate] {quienLanza.Id} lanza a {c.Sobre.Id} a la casilla {c.CasillaHasta}.");
                    movidos.Add(c.Sobre);
                    await RefreshPortalsAsync(fight);
                    continue;
                }

                if (c.Mueve && c.MueveTambien && c.Efecto.EffectId == Jondo.Unity.World.Combat.EffectSupport.SwapPositions)
                {
                    // A swap is ONE frame for the two: jwe 8 with the caster's old cell, the
                    // other and his old cell. Measured on Jugarreta and Impostura; two slides
                    // was what we sent.
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildSwap(quienLanza.Id, c.CasillaDesdeDelOtro,
                                                        c.Sobre.Id, c.CasillaDesde)));
                    Program.LogDebug($"[Combate] {quienLanza.Id} y {c.Sobre.Id} intercambian " +
                                     $"{c.CasillaDesdeDelOtro} y {c.CasillaDesde}.");
                    movidos.Add(c.Sobre);
                    movidos.Add(c.Tambien);
                    // Moved by a swap (MS), both of them; and the caster moved somebody else (PO).
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerIntercambiado, quienLanza);
                    Disparo(c.Tambien, Managers.EffectEngine.AlSerIntercambiado, quienLanza);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerMovido, quienLanza);
                    Disparo(quienLanza, Managers.EffectEngine.AlDesplazarAOtro, c.Sobre);
                    await RefreshPortalsAsync(fight);
                    continue;
                }

                if (c.Mueve && EsTeletransporte(c.Efecto.EffectId))
                {
                    // A teleport is a jwe 4 with where and who, in the 581 of the captures; a
                    // slide with from and to is what we sent for it. When it is somebody else
                    // who moves -- the bomb Colado mirrors -- the frame goes out in HIS name:
                    // "jwe f3=-17 f14=4 f35{f1=328 f2=-17}", three casts out of three.
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildTeleport(c.Sobre.Id, c.Sobre.Id, c.CasillaHasta)));
                    Program.LogDebug($"[Combate] El hechizo {hechizo} teletransporta a {c.Sobre.Id} " +
                                     $"de la casilla {c.CasillaDesde} a la {c.CasillaHasta}.");
                    movidos.Add(c.Sobre);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerTeletransportado, quienLanza);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerMovido, quienLanza);

                    // A telefrag: the one who stood there goes to the cell the other left.
                    if (c.Tambien != null)
                    {
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                            Network.FightProtocol.BuildTeleport(c.Tambien.Id, c.Tambien.Id, c.CasillaHastaDelOtro)));
                        Program.LogDebug($"[Combate] Telefrag: {c.Tambien.Id} pasa de {c.CasillaDesdeDelOtro} " +
                                         $"a {c.CasillaHastaDelOtro}.");
                        movidos.Add(c.Tambien);
                        Disparo(c.Tambien, Managers.EffectEngine.AlSerTeletransportado, quienLanza);
                        Disparo(c.Tambien, Managers.EffectEngine.AlSerMovido, quienLanza);
                    }
                    // A portal he has left comes back on: Estela's, frame 15.
                    await RefreshPortalsAsync(fight);
                    continue;
                }

                if (c.Mueve)
                {
                    // Por el cable, un desplazamiento viaja SIEMPRE como el 5, whatever moved
                    // it and whichever way it went. It used to pick 6 for a move towards the
                    // caster; measured over the 400 captures, 553 displacements travel as 5 and
                    // not one as 6 -- Imantación's pull and the Tymobot's Aspirador included.
                    int comoViaja = Network.FightProtocol.Alejarse;

                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildDisplacement(
                            quienLanza.Id, comoViaja, c.Sobre.Id,
                            c.CasillaDesde, c.CasillaHasta)));
                    Program.LogDebug($"[Combate] El hechizo {hechizo} mueve a {c.Sobre.Id} " +
                                     $"de la casilla {c.CasillaDesde} a la {c.CasillaHasta}.");

                    // A QUIEN LO MUEVEN TAMBIÉN PISA. El suelo sólo saltaba andando, así que
                    // meter a alguien en un muro de un empujón o de un tirón no le hacía nada
                    // -- ni el muro, ni una trampa, ni un glifo del feca.
                    movidos.Add(c.Sobre);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerEmpujado, quienLanza);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerMovido, quienLanza);
                    // Drawn in (MA): Imantación's combo "solo si las bombas son desplazadas" by its
                    // pull. And the caster moved somebody else (PO).
                    if (c.Efecto.EffectId is Jondo.Unity.World.Combat.EffectSupport.Pull
                                         or Jondo.Unity.World.Combat.EffectSupport.PullToTargetCell or 1022)
                        Disparo(c.Sobre, Managers.EffectEngine.AlSerAtraido, quienLanza);
                    if (c.Sobre != quienLanza) Disparo(quienLanza, Managers.EffectEngine.AlDesplazarAOtro, c.Sobre);

                    // Y el segundo, si el efecto movía a dos. Es el intercambio de posiciones:
                    // sin este anuncio el cliente deja al lanzador pintado donde estaba, y a
                    // partir de ahí su tablero y el nuestro ya no coinciden en nada.
                    if (c.MueveTambien)
                    {
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                            Network.FightProtocol.BuildDisplacement(
                                quienLanza.Id, comoViaja, c.Tambien.Id,
                                c.CasillaDesdeDelOtro, c.CasillaHastaDelOtro)));
                        Program.LogDebug($"[Combate] Y mueve a {c.Tambien.Id} de la casilla " +
                                         $"{c.CasillaDesdeDelOtro} a la {c.CasillaHastaDelOtro}.");
                    }

                    await DanoDeColisionAsync(stream, fight, quienLanza, c);

                    // Moved onto a portal that is on, he goes through it, right behind the move:
                    // Odisea's step back onto the portal on 215, frames 92-97.
                    if (c.Sobre.IsAlive && DisplacementCrossesPortals(c.Efecto.EffectId)
                        && PortalCatches(fight, c.Sobre, c.Sobre.CellId))
                        await CrossPortalAsync(stream, fight, c.Sobre, walkedIn: true);
                    else
                        await RefreshPortalsAsync(fight);
                    continue;
                }

                // Y el que NO se ha movido ni una casilla pero se ha estampado igual. El motor
                // devuelve una consecuencia sin desplazamiento, y el servidor real hace lo mismo:
                // en ese caso no manda el mensaje de movimiento, sólo el del golpe.
                if (c.CollisionDamage > 0)
                {
                    await DanoDeColisionAsync(stream, fight, quienLanza, c);
                    continue;
                }

                // A state being removed, or effect 406 ("removes the spell's effects"). This is what
                // brings Rage back down cleanly, and what takes the beast form off at once with
                // Apaisement/Affection.
                bool quitaApariencia = false;
                bool quitaInvisibilidad = false;
                foreach (var quitado in c.BuffsQuitados)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                        Network.FightProtocol.BuildBuffGone(c.Sobre.Id, quitado.Numero)));
                    if (quitado.EffectId == Jondo.Unity.World.Combat.EffectSupport.Visibility) quitaInvisibilidad = true;
                    if (quitado.Estado != 0 && quitado.EffectId == Jondo.Unity.World.Combat.EffectSupport.AddState
                        && !c.Sobre.Buffs.TieneEstado(quitado.Estado))
                        Disparo(c.Sobre, Managers.EffectEngine.AlQuitarseElEstado(quitado.Estado), quienLanza);
                    if (quitado.Apariencia != 0) quitaApariencia = true;
                    if (quitado.Caracteristica != 0 &&
                        quitado.Caracteristica != ActionPointsCharacteristic &&
                        quitado.Caracteristica != MovementPointsCharacteristic)
                    {
                        fichas.Add((c.Sobre.Id, quitado.Caracteristica));
                    }
                }
                if (quitaApariencia)
                {
                    await AnnounceAppearanceAsync(stream, c.Sobre,
                        c.Sobre.Buffs.AparienciaEn(fight.RoundNumber));
                }
                // Invisible no more -- a 202 revealed him, a 406 or a dispel took the row -- and
                // everybody is told so behind the jya, as when the row runs out. The author is
                // the one whose effect did it: an inference, no capture has a reveal.
                if (quitaInvisibilidad)
                    await VisibleOtraVezAsync(fight, quienLanza, c.Sobre);

                // Dispelled (DIS): the Feca's Barricada "aumenta sus PM si es atacado a distancia
                // o desembrujado"; the Aniripsa's Crioterapia goes off "si el objetivo pierde el
                // estado".
                if (c.Efecto.EffectId == Managers.EffectEngine.Desembrujo && c.BuffsQuitados.Count > 0)
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerDesembrujado, quienLanza);

                // A 406 says so after the rows it took: "jwe 406 f33{f2: the spell, f4: on
                // whom}" behind the three jya of Furor's recast in its capture, and behind
                // Tempestad de Potencia's in hers.
                if (c.Efecto.EffectId == Jondo.Unity.World.Combat.EffectSupport.RemoveSpellEffects
                    && c.BuffsQuitados.Count > 0)
                {
                    int hechizoQuitado = c.Efecto.Value != 0 ? c.Efecto.Value : c.Efecto.DiceNum;
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildSpellEffectsRemoved(quienLanza.Id, hechizoQuitado, c.Sobre.Id,
                            shown: (c.Efecto.Flags & Network.FightProtocol.ShownRowFlag) != 0)));
                }
                // And a 1406 with the grade it took: "jwe 1406 f33{f2=30842 f3=6 f4=-3}" in Aguja's capture.
                if (c.Efecto.EffectId == Managers.EffectEngine.QuitaUnGradoDeUnHechizo && c.BuffsQuitados.Count > 0)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildSpellEffectsRemoved(quienLanza.Id, c.Efecto.Value, c.Sobre.Id,
                            grade: c.Efecto.DiceSide != 0 ? c.Efecto.DiceSide : c.Efecto.DiceNum,
                            effect: Managers.EffectEngine.QuitaUnGradoDeUnHechizo)));
                }

                // The rows the new one replaced go first, gone and expired: Espada del Juicio
                // cast again is "jya 9, jwe 514 {9}, jxm 13" in its capture, never a jxm that
                // reuses the number.
                foreach (var relevado in c.Relevados)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                        Network.FightProtocol.BuildBuffGone(c.Sobre.Id, relevado.Numero)));
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildBuffExpired(c.Sobre.Id, relevado.Numero)));
                    if (relevado.Caracteristica != 0 &&
                        relevado.Caracteristica != ActionPointsCharacteristic &&
                        relevado.Caracteristica != MovementPointsCharacteristic)
                    {
                        fichas.Add((c.Sobre.Id, relevado.Caracteristica));
                    }
                }

                if (c.Buff == null) continue;

                // A row that waits: one jxm with trigger "Y", the activation round as its
                // expiry and in its f12, hidden from the panel. What it holds goes out when
                // its round comes, from ApplyDuePendingAsync.
                if (c.Buff.Pendiente)
                {
                    c.Buff.Critico = c.Critico;
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxm,
                        Network.FightProtocol.BuildBuff(
                            c.Sobre.Id, quienLanza.Id, c.Buff.Numero, c.Efecto.EffectId,
                            c.Efecto.EffectUid, c.Efecto.Value, c.Efecto.DiceNum, c.Efecto.DiceSide,
                            c.HechizoOrigen, Managers.EffectEngine.Esperando, c.Buff.EmpiezaEnRonda,
                            c.Efecto.Dispellable, Network.FightProtocol.HiddenFamily, c.NivelOrigen,
                            critico: c.Critico, activacion: c.Buff.EmpiezaEnRonda)));
                    Program.LogDebug($"[Combate] Embrujo {c.Buff.Numero} sobre {c.Sobre.Id} a la espera: efecto " +
                                     $"{c.Efecto.EffectId} del hechizo {c.HechizoOrigen}, salta en la ronda " +
                                     $"{c.Buff.EmpiezaEnRonda}.");
                    continue;
                }

                if (c.SoloParaElPanel)
                {
                    Program.LogDebug($"[Combate] El efecto {c.Efecto.EffectId} del hechizo {hechizo} " +
                                     $"todavía no se sabe aplicar; se manda al panel tal cual " +
                                     $"(valor {c.Efecto.Value}, dado {c.Efecto.DiceNum}/{c.Efecto.DiceSide}).");
                }

                // Un efecto con varios disparadores se anuncia una vez por cada uno, que es lo que
                // hace el servidor real.
                var (categoria, boost) = DatabaseManager.EffectFamily(c.Efecto.EffectId);
                int familia = Network.FightProtocol.FamiliaDelEmbrujo(c.Efecto.EffectId, categoria, boost);

                // La ronda EN LA QUE SE CAE, no lo que le queda: así lo manda el servidor real.
                int rondas = c.Buff.CaducaEnRonda;

                // A landed removal is announced as the loss it turned out to be: "-N PA" with
                // the N that landed, the family of that effect, and no dice of its own.
                int efectoAnunciado = c.EfectoEnElCable != 0 ? c.EfectoEnElCable : c.Efecto.EffectId;
                int dadoAnunciado = c.EfectoEnElCable != 0 ? -c.Cuanto
                                  : c.FilaEnganchada && c.Efecto.DiceNum == 0 ? c.Efecto.Value
                                  : c.Efecto.DiceNum;
                int caraAnunciada = c.EfectoEnElCable != 0 ? 0 : c.Efecto.DiceSide;
                if (c.EfectoEnElCable != 0)
                {
                    var (categoriaCable, boostCable) = DatabaseManager.EffectFamily(efectoAnunciado);
                    familia = Network.FightProtocol.FamiliaDelEmbrujo(efectoAnunciado, categoriaCable, boostCable);
                }

                // The critical flag is the ROW's: a row out of a spell's critical list, at
                // whatever depth of the chain. Virtud's critical shield is "1040 dice 1100 f9=1
                // uid 383796", 29723's own critical row; Tumulto's critical cast puts 13154's
                // ordinary "+20", which has no critical list, without the flag.
                c.Buff.Critico = c.Critico;
                foreach (var d in c.Efecto.Disparadores())
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxm,
                        Network.FightProtocol.BuildBuff(
                            c.Sobre.Id, quienLanza.Id, c.Buff.Numero, efectoAnunciado,
                            c.Efecto.EffectUid, c.Efecto.Value, dadoAnunciado, caraAnunciada,
                            c.HechizoOrigen, d, rondas, c.Efecto.Dispellable, familia,
                            c.NivelOrigen, c.Critico)));
                }
                // An invisibility row goes with the switch, to everybody, right behind it: "jxm
                // 150, jwe 150 f34{f1=1 f4=Sram}" at frames 10-11 of "sram-invisibilidad", and a
                // monster going invisible reaches the Osamodas' client, his enemy's, the same way.
                if (c.Efecto.EffectId == Jondo.Unity.World.Combat.EffectSupport.Visibility)
                {
                    c.Sobre.LastSeenCell = c.Sobre.CellId;
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildVisibility(quienLanza.Id, c.Sobre.Id, Network.FightProtocol.Hidden)));
                }
                if (c.Buff.Estado != 0 && c.Efecto.EffectId == Jondo.Unity.World.Combat.EffectSupport.AddState
                    && !(c.Relevados?.Any(r => r.Estado == c.Buff.Estado) ?? false)
                    && c.Sobre.Buffs.Puestos.Count(b => b.Estado == c.Buff.Estado
                                                       && b.EffectId == Jondo.Unity.World.Combat.EffectSupport.AddState) == 1)
                    Disparo(c.Sobre, Managers.EffectEngine.AlPonerseElEstado(c.Buff.Estado), quienLanza);

                if (c.Apariencia != 0)
                {
                    await AnnounceAppearanceAsync(stream, c.Sobre, c.Apariencia);
                }

                Program.LogDebug($"[Combate] Buff {c.Buff.Numero} sobre {c.Sobre.Id}: efecto " +
                                 $"{c.Efecto.EffectId}" +
                                 (c.Caracteristica != 0 ? $", característica {c.Caracteristica} {c.Cuanto:+#;-#;0}" : "") +
                                 (c.Buff.Estado != 0 ? $", estado {c.Buff.Estado}" : "") +
                                 (c.Buff.HechizoAfectado != 0
                                     ? $", {c.Buff.Sobre} {c.Buff.Cuanto:+#;-#;0} del hechizo {c.Buff.HechizoAfectado}"
                                     : "") +
                                 $", hasta la ronda {c.Buff.CaducaEnRonda}.");
            }

            // Y ahora si, el suelo de quien haya acabado en otra casilla. A QUIEN LO EMPUJAN
            // TAMBIEN PISA: el suelo solo saltaba andando, asi que meter a alguien en un muro de
            // un empujon o de un tiron no le hacia nada -- ni el muro, ni una trampa, ni un glifo
            // del feca.
            foreach (var movido in movidos) CarriedFollows(fight, movido);

            if (movidos.Count > 0)
            {
                await ReconciliarLosMurosAsync(stream, fight);
                foreach (var movido in movidos)
                {
                    if (movido == null || !movido.IsAlive) continue;
                    await DispararLosGlifosAsync(stream, fight, movido, alPisar: true,
                                                 byDisplacement: true);
                }
            }

            foreach (var cambiado in vidasCambiadas.Values)
                await RefrescarLaVidaAsync(stream, fight, cambiado, quienLanza);

            foreach (var (quien, caracteristica) in fichas)
            {
                var ficha = fight.Buscar(quien);
                if (ficha == null) continue;
                var refresco = Refresco(ficha, caracteristica, fight.RoundNumber, fight.CurrentFighter == ficha);

                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(quien, Network.FightProtocol.SheetSequence)));
                await FichaATodosAsync(fight, quien, refresco);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien,
                                                           Network.FightProtocol.SheetSequence)));
            }

            await AnunciarElAlcanceAsync(stream, fight, consecuencias);

            await RedibujarLasBombasAsync(fight, tamanosAntes);

            foreach (var (quienSalta, queSalta, fuente) in disparos)
            {
                var antes = fight.TriggeringAttacker;
                fight.TriggeringAttacker = fuente;
                await DispararAsync(stream, fight, quienSalta, queSalta);
                fight.TriggeringAttacker = antes;
            }

            // The hooks this cast laid for itself alone, gone now that what they waited on has
            // gone off: see EffectEngine.CierraUnEngancheDelLanzamiento.
            foreach (var c in consecuencias)
            {
                if (c.Sobre == null || c.EnganchePendiente || c.Efecto == null) continue;
                if (!Managers.EffectEngine.CierraUnEngancheDelLanzamiento(c.HechizoOrigen, c.NivelOrigen, c.Efecto, critico)) continue;
                int cual = c.Efecto.Value != 0 ? c.Efecto.Value : c.Efecto.DiceNum;
                if (c.Sobre.Buffs.Desenganchar(cual) > 0)
                    Program.LogDebug($"[Combat] {c.Sobre.Id} loses the hook of {cual}: the same cast of {c.HechizoOrigen} laid it and takes it off.");
            }
        }

        /// <summary>
        /// The jwe 149 with the new scale for every bomb whose combo size moved during a cast.
        /// Measured in "tymador-explobomba resiliente": one at the tail of every step that
        /// changes the rung. See <see cref="Managers.Combo.SizeOf"/> for the number.
        /// </summary>
        private static async Task RedibujarLasBombasAsync(FightInstance fight, Dictionary<long, int> tamanosAntes)
        {
            foreach (var bomba in TodosLosCombatientes(fight))
            {
                if (bomba == null || !bomba.IsAlive || !EsBomba(bomba.MonsterId)) continue;
                int tamano = Managers.Combo.SizeOf(bomba, fight.RoundNumber);
                if (tamanosAntes.TryGetValue(bomba.Id, out int antes) && antes == tamano) continue;
                if (!tamanosAntes.ContainsKey(bomba.Id) && tamano == Managers.Combo.BaseSize) continue;

                byte[] aspecto = Network.FightProtocol.WithScale(NormalFightLook(bomba), tamano);
                if (aspecto.Length == 0) continue;
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildLookChanged(bomba.Id, aspecto)));
                Program.LogDebug($"[Combo] La bomba {bomba.Id} crece al {tamano}% " +
                                 $"en el nivel {Managers.Combo.LevelOf(bomba)}.");
            }
        }

        /// <summary>
        /// El alcance que han cambiado los embrujos, hechizo por hechizo (hnd y hnk).
        ///
        /// Esto FALTABA ENTERO, y es la razón de que dar alcance no sirviera de nada. El jxm que
        /// ya mandábamos es byte a byte el del servidor real —mismo f1, f4, f6, f8, f10, f14—
        /// pero ése sólo alimenta el panel de efectos: el cliente enseñaba «Disparos Lejanos: +6
        /// de alcance máximo» y seguía iluminando las mismas casillas.
        ///
        /// Las casillas las calcula el cliente con el hnd, uno por hechizo tocado y modificador.
        ///
        /// AQUÍ SÓLO VA EL HND. La primera versión mandaba detrás la ráfaga de hnk, creyendo que
        /// era la declaración que lo acompañaba, y por eso dar alcance no servía absolutamente de
        /// nada: se ponía el modificador y en la misma ráfaga se le decía al cliente que lo
        /// borrara. El hnk es la RETIRADA, y va cuando el embrujo caduca —está puesto en el bucle
        /// de caducados de ConfirmAsync—.
        ///
        /// Medido con reloj sobre «ocra-disparos lejanos»: al lanzar van 68 hnd y CERO hnk; al
        /// caducar, 68 hnk y CERO hnd, y el ciclo se repite igual cuatro veces. En
        /// «ocra-tiro de repliegue» los 60 hnk aparecen solos, justo delante de los 61 jya, sin un
        /// hnd cerca: el hnk vive por su cuenta.
        ///
        /// Se manda el TOTAL que tiene el hechizo ahora, no lo que acaba de sumar este embrujo:
        /// así dos embrujos sobre el mismo hechizo no se pisan, y quitar uno deja el número bueno.
        /// </summary>
        private static async Task AnunciarElAlcanceAsync(
            NetworkStream stream, FightInstance fight,
            List<Managers.Outcome> consecuencias)
        {
            // Quién y qué hechizo han quedado tocados, and by which modifier. A range row announces
            // both ranges of its spell, as the Disparos Lejanos capture has it: 68 hnd for 34 spells.
            var tocados = new List<(Fighter Quien, int Hechizo, Jondo.Unity.World.Fights.SpellAspect Que)>();
            void Tocado(Fighter quien, int hechizo, Jondo.Unity.World.Fights.SpellAspect que)
            {
                if (tocados.Exists(t => t.Quien.Id == quien.Id && t.Hechizo == hechizo && t.Que == que)) return;
                tocados.Add((quien, hechizo, que));
            }
            foreach (var c in consecuencias)
            {
                var buff = c.Buff;
                if (buff == null || buff.HechizoAfectado == 0 || c.Sobre == null) continue;
                if (Managers.SpellModifiers.OnTheWire(buff.Sobre) == null) continue;
                if (buff.Sobre is Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo
                                or Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo)
                {
                    Tocado(c.Sobre, buff.HechizoAfectado, Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo);
                    Tocado(c.Sobre, buff.HechizoAfectado, Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo);
                    continue;
                }
                Tocado(c.Sobre, buff.HechizoAfectado, buff.Sobre);
            }

            // And the modifiers a 406, a replacement or a dispel took off in this cast: the new
            // total while other rows still hold, the hnk once none does.
            var quitados = new List<(Fighter Quien, int Hechizo, Jondo.Unity.World.Fights.SpellAspect Que)>();
            foreach (var c in consecuencias)
            {
                if (c.Sobre == null) continue;
                foreach (var ido in c.BuffsQuitados.Concat(c.Relevados ?? Array.Empty<Jondo.Unity.World.Fights.Buff>()))
                {
                    if (ido.HechizoAfectado == 0 || Managers.SpellModifiers.OnTheWire(ido.Sobre) == null) continue;
                    if (tocados.Exists(t => t.Quien.Id == c.Sobre.Id && t.Hechizo == ido.HechizoAfectado && t.Que == ido.Sobre)) continue;
                    if (quitados.Exists(t => t.Quien.Id == c.Sobre.Id && t.Hechizo == ido.HechizoAfectado && t.Que == ido.Sobre)) continue;
                    quitados.Add((c.Sobre, ido.HechizoAfectado, ido.Sobre));
                }
            }
            foreach (var (quien, hechizo, que) in quitados)
            {
                var (modificador, accion) = Managers.SpellModifiers.OnTheWire(que).Value;
                if (Managers.SpellModifiers.Holds(quien, hechizo, que, fight.RoundNumber))
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Hnd,
                        Network.FightProtocol.BuildSpellModifier(quien.Id, modificador, hechizo,
                            Managers.SpellModifiers.Total(quien, hechizo, que, fight.RoundNumber), accion)));
                }
                else
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Hnk,
                        Network.FightProtocol.BuildSpellModifierDeclared(quien.Id, modificador, hechizo, accion)));
                }
            }

            if (tocados.Count == 0) return;

            // The total each modifier has for its spell now, with its action: the client computes
            // with this, not with the jxm.
            foreach (var (quien, hechizo, que) in tocados)
            {
                var (modificador, accion) = Managers.SpellModifiers.OnTheWire(que).Value;
                long cuanto = Managers.SpellModifiers.Total(quien, hechizo, que, fight.RoundNumber);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Hnd,
                    Network.FightProtocol.BuildSpellModifier(quien.Id, modificador, hechizo, cuanto, accion)));
            }

            Program.LogDebug($"[ALCANCE] Anunciados {tocados.Count} modificador(es) de hechizo con hnd.");
        }

        /// <summary>
        /// Los pasivos y las actitudes, lanzados antes del primer turno.
        ///
        /// Es lo que hace el servidor real y el emulador no hacía: entre el "listo" y el primer
        /// turno mete diez secuencias con dieciséis o diecinueve lanzamientos y entre cincuenta y
        /// cinco y setenta y seis jxm. Son los pasivos del personaje —Negro Ébano, La Sangre de
        /// Sacrogrito, Transposición...— lanzados sobre los combatientes. El cliente llega al turno
        /// uno con la pila de embrujos de cada uno ya montada; aquí llegaba vacía, y el panel de
        /// efectos con ella.
        ///
        /// Lo que se lanza son las ACTITUDES que dan los objetos. No hay que adivinar cuáles son:
        /// las dice el efecto 1175 de cada objeto equipado. Y encajan con lo de la captura, porque
        /// un pasivo y una actitud son la misma clase de cosa: en SpellTemplates, los hechizos que
        /// se lanzan a mano llevan typeId 9 y ninguno de éstos lo lleva —los dofus van con el 732—.
        ///
        /// Cada uno va en su secuencia, con la forma de la captura: jto, el jwe del lanzamiento y
        /// los jxm que salgan, y jwi.
        /// </summary>
        private static async Task CascadaDePasivosAsync(NetworkStream stream, FightInstance fight)
        {
            // Where each one stood when the fight began, for effect 784.
            foreach (var luchador in TodosLosCombatientes(fight))
            {
                if (luchador != null && luchador.CasillaAlEmpezarCombate < 0) luchador.CasillaAlEmpezarCombate = luchador.CellId;
            }

            // Both sides: the red one too, whose monsters bring their behaviour spells and whose
            // player, in a duel, his passives. Only the blue side's ever went off.
            foreach (var quien in fight.Azul.Concat(fight.Rojo).ToList())
            {
                // Over a COPY: see ActitudesAsync.
                foreach (int actitud in quien.Buffs.Actitudes.ToList())
                {
                    int grado = quien.Buffs.GradoDeActitud(actitud);
                    int nivelId = LimitesDeGrado(actitud, grado).LevelId;
                    if (nivelId <= 0) (_, nivelId, _) = Managers.SpellEffects.GradoDe(actitud, quien.Level);

                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                        Network.FightProtocol.BuildSequenceStart(quien.Id,
                                                                 Network.FightProtocol.ActionSequence)));

                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildAction(
                            quien.Id, Network.FightProtocol.Cast,
                            Network.FightProtocol.CastAt(quien.Id, quien.Id, quien.CellId, actitud,
                                                         nivelId, critical: false),
                            Network.FightProtocol.CastDetail)));

                    await AplicarEfectosAsync(stream, fight, quien, actitud, grado, quien,
                                              Managers.EffectEngine.AlLanzar, quien.CellId, armar: false);

                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                        Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                               Network.FightProtocol.ActionSequence)));
                }

                if (quien.Buffs.Actitudes.Count > 0)
                {
                    Program.LogDebug($"[Combate] Cascada de {quien.Buffs.Actitudes.Count} " +
                                     $"actitud(es) de {quien.Id} antes del primer turno.");
                }

                // And a monster's behaviour spell.
                if (quien.Conducta.Spell != 0) await LanzarLaConductaAsync(stream, fight, quien);
            }
        }

        /// <summary>
        /// Las actitudes que dan los objetos, disparadas en su momento.
        ///
        /// Cada dofus y cada trofeo regala un "hechizo" por su efecto 1175, y ese hechizo dice
        /// cuándo hace lo suyo. El Dofus Ocre, por ejemplo, tiene un efecto con el disparador "TB"
        /// —principio del turno— que lanza su propio grado 3, y ése es el que da el punto de acción
        /// si no le han pegado a uno.
        /// </summary>
        private static async Task ActitudesAsync(NetworkStream stream, FightInstance fight,
                                                 Fighter quien, string disparador)
        {
            if (quien == null) return;

            // Whatever the attitudes announce goes inside ONE sequence of the bearer's, the
            // ordinary action one, opened right before the first frame goes out and closed at
            // the end; nothing is written when nothing comes out of them. The client only
            // applies what arrives inside an open jto, and the real server wraps a turn
            // trigger's casts exactly so: "jto{-12,3} jwe 300 ... jwe 103 ... jwi" at the
            // Tymobot's turn end -- which is where its death went out bare, and stayed on the
            // client's board -- and "jto{-12,3} jwe 300 jxm jwi" at its turn start.
            //
            // The opening is left with the fight (SequenceToOpen) and happens inside the
            // senders, not here: opening as soon as the engine returned SOMETHING sent an
            // empty "jto jwi" when that something had nothing to announce -- the Tymobot's
            // death trigger re-casting the state removal its turn end had already done -- and
            // an empty pair is a thing the real server never sends. Behind one the client
            // stopped acknowledging sequences and the fight stood still with the clock in
            // the negative.
            //
            // Nested: a death inside these attitudes fires the dead one's own, so a sequence
            // may already be owed. The inner opening runs the outer one first, so the frames
            // land in order -- outer jto, inner jto -- and the outer still closes last.
            bool abierta = false;
            var debidaAntes = fight.SequenceToOpen;
            Func<Task> abrir = null;
            abrir = async () =>
            {
                if (abierta) return;
                abierta = true;
                if (debidaAntes != null) await debidaAntes();
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(quien.Id, Network.FightProtocol.ActionSequence)));
            };
            fight.SequenceToOpen = abrir;

            // Over a COPY: an attitude can disarm itself while it resolves -- the Silver Dofus
            // does, through its own 406 -- and removing from the list being walked threw
            // "Collection was modified" straight out of the connection handler, which is
            // what dropped the Ocra client mid-fight the first time the Dofus fired right.
            foreach (int actitud in quien.Buffs.Actitudes.ToList())
            {
                // El enganche de una actitud está SIEMPRE en su grado uno, no en el más alto que el
                // personaje tenga abierto. Los tres grados del Amarillo Ocre son de nivel mínimo 1,
                // así que preguntar por el grado del personaje devolvía el 3 —el que da el punto de
                // acción— y allí no hay ningún disparador de principio de turno, con lo que la
                // actitud no hacía nada. Los grados de dentro los dice el propio enganche. The
                // one exception is an initial spell of the character's own choices, held at his
                // grade of the choice (see Buffs.GradoDeActitud).
                int grado = quien.Buffs.GradoDeActitud(actitud);
                await AplicarEfectosAsync(stream, fight, quien, actitud, grado, quien, disparador, quien.CellId, armar: false);

                // Y los grados que la actitud encadena, por su cuenta. Hace falta porque un grado
                // encadenado puede traer efectos con SU propio disparador: el grado 3 del Amarillo
                // Ocre se lanza al empezar el turno y da el punto de acción en el acto, pero
                // además lleva dentro un "quita el estado" con disparador de FIN de turno, y a ése
                // hay que ir a buscarlo cuando el turno acaba.
                foreach (var efecto in Managers.SpellEffects.De(actitud, grado))
                {
                    if (efecto.EffectId != Managers.EffectEngine.EfectoQueLanzaHechizo) continue;
                    if (efecto.DiceNum <= 0) continue;
                    await AplicarEfectosAsync(stream, fight, quien, efecto.DiceNum,
                                              efecto.DiceSide > 0 ? efecto.DiceSide : 1,
                                              quien, disparador, quien.CellId, armar: false);
                }
            }

            if (abierta)
            {
                // Nothing of ours is owed any more; the outer one, if any, opened with us.
                if (fight.SequenceToOpen == abrir) fight.SequenceToOpen = null;
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                           Network.FightProtocol.ActionSequence)));
            }
            else if (fight.SequenceToOpen == abrir)
            {
                // Nothing came out: the sequence never opened, and whatever was owed before
                // us is owed again.
                fight.SequenceToOpen = debidaAntes;
            }
        }

        /// <summary>How deep triggers may set off triggers before it is a loop in the data.</summary>
        private const int TopeDeDisparosEncadenados = 6;

        /// <summary>
        /// Fires one trigger on a fighter: what his attitudes and his hooked spells hold for it.
        /// The triggers a trigger sets off go the same way, and past a few levels deep they stop.
        /// </summary>
        internal static async Task DispararAsync(NetworkStream stream, FightInstance fight,
                                                Fighter quien, string disparador)
        {
            if (quien == null || !quien.IsAlive || string.IsNullOrEmpty(disparador)) return;
            if (fight.TriggerDepth >= TopeDeDisparosEncadenados)
            {
                Program.LogDebug($"[Combate] Disparo {disparador} sobre {quien.Id} descartado: " +
                                 $"{fight.TriggerDepth} disparos encadenados.");
                return;
            }
            fight.TriggerDepth++;
            try
            {
                await ActitudesAsync(stream, fight, quien, disparador);
                await EngancheAsync(stream, fight, quien, disparador);
            }
            finally
            {
                fight.TriggerDepth--;
            }
        }

        /// <summary>
        /// The triggers of a blow, on both ends of it. On the one hit: damage at all (D), of the
        /// blow's element (DN DE DF DW DA), from his own side (DBA), in melee (DCAC). On the one
        /// who hit: the damage he dealt, by element (CDN CDE CDF CDW CDA) and in melee (CDM) --
        /// Hell Mina counts her players' blows of each element so. The one at the other end is
        /// the fight's TriggeringAttacker meanwhile, for the "O" of the masks and the source of
        /// a 1018.
        /// </summary>
        private static async Task DispararLosDelGolpeAsync(NetworkStream stream, FightInstance fight,
                                                           Fighter caster, Fighter target, int elemento, bool deCerca,
                                                           int dano = 0, bool deHechizo = false)
        {
            var antes = fight.TriggeringAttacker;
            var danoAntes = fight.DanoDelDisparo;
            var elementoAntes = fight.ElementoDelDisparo;
            fight.TriggeringAttacker = caster;
            fight.DanoDelDisparo = dano;
            fight.ElementoDelDisparo = elemento;
            await DispararAsync(stream, fight, target, Managers.EffectEngine.AlRecibirDano);
            // A spell's blow, not a weapon's (DS): the Xelor's cómplice returns "una parte de los
            // daños de hechizo que sufre" through its 792 under DS, and in its capture it goes
            // off after the Xelor's 13254 lands on it.
            if (deHechizo) await DispararAsync(stream, fight, target, Managers.EffectEngine.AlRecibirDanoDeHechizo);
            // A trap's blow (DT): Toxinas "vuelve a aplicarse mientras ... el objetivo sufra daños de
            // trampas", Concentración de Chakra "roba vida ... si este sufre daños de trampas".
            if (fight.CurrentGlyphType == Managers.EffectEngine.ColocaUnaTrampa)
                await DispararAsync(stream, fight, target, Managers.EffectEngine.AlSufrirDanoDeTrampa);
            await DispararAsync(stream, fight, target, Managers.EffectEngine.DanoDeElemento(elemento));
            if (caster != target && caster.TeamId == target.TeamId)
                await DispararAsync(stream, fight, target, Managers.EffectEngine.DanoDeAliado);
            if (deCerca) await DispararAsync(stream, fight, target, Managers.EffectEngine.DanoCuerpoACuerpo);
            if (caster.EsInvocado) await DispararAsync(stream, fight, target, Managers.EffectEngine.DanoDeInvocacion);

            if (caster != target && caster.IsAlive)
            {
                fight.TriggeringAttacker = target;
                await DispararAsync(stream, fight, caster, Managers.EffectEngine.CausaDanoDeElemento(elemento));
                if (deCerca) await DispararAsync(stream, fight, caster, Managers.EffectEngine.CausaDanoDeCerca);
            }
            fight.TriggeringAttacker = antes;
            fight.DanoDelDisparo = danoAntes;
            fight.ElementoDelDisparo = elementoAntes;
        }

        /// <summary>
        /// El daño de un lanzamiento, con la fórmula de siempre.
        ///
        ///   base × (100 + característica del elemento + potencia) / 100
        ///   menos la resistencia fija, por (1 − resistencia%)
        ///
        /// La cuenta la hace <see cref="Jondo.Unity.World.Fights.DamageCalculator"/>, que ya estaba
        /// escrita y es la de Dofus; aquí sólo se elige a quién le toca y se manda el resultado.
        /// </summary>
        /// <param name="tirada">The cast's draw of the random rows; drawn here when null.</param>
        internal static async Task HurtAsync(NetworkStream stream, FightInstance fight,
                                            Fighter caster, int spell, int grade, Fighter target,
                                            int celdaApuntada = -1, bool critico = false,
                                            IReadOnlyList<Managers.SpellEffect> tirada = null,
                                            string disparador = Managers.EffectEngine.AlLanzar,
                                            bool soloAlObjetivo = false)
        {
            // Un hechizo de zona pega aunque no haya nadie EXACTAMENTE en la casilla apuntada, así
            // que ya no se puede salir de aquí por no tener objetivo directo.
            if (target == null && celdaApuntada < 0) return;

            // El daño sale de los EFECTOS del hechizo, no de un resumen aplanado.
            //
            // Antes se pedía un SpellCombatData que se quedaba con un único par de dados, y sólo
            // miraba los efectos del 96 al 100. Eso rompía tres cosas a la vez: Flecha Voraz pega
            // con el 94 —robo de fuego— y no encajaba, el Ojo de Topo con el 91, y Tiro de
            // Repliegue, que NO tiene ni un efecto de daño, acababa quitando vida igual.
            //
            // Ahora se pregunta al motor qué golpes da este hechizo sobre este objetivo. Si no da
            // ninguno, aquí no se toca a nadie.
            var golpes = spell != 0
                ? Managers.EffectEngine.Golpes(fight, caster, spell, grade, target, celdaApuntada, critico, tirada,
                                               disparador, soloAlObjetivo)
                : (target != null ? GolpeDelArma(caster, target)
                                  : new List<(Managers.SpellEffect, int, Fighter, int)>());
            if (golpes.Count == 0) return;

            // El dado se tira UNA VEZ por efecto, no una por afectado: si un hechizo de "25 a 30"
            // saca un 26, en el centro de la zona entran 26 y a los de alrededor les entra ese
            // mismo 26 ya rebajado por la distancia. Tirando por cabeza, dos bichos pegados al
            // centro recibirían números distintos y el jugador vería una zona que no cuadra.
            var dados = new Dictionary<int, int>();

            // Life reaches the client as an ABSOLUTE sheet. On a spell with several lines, sending
            // one between each damage figure puts the sheet and the animations in a race and can
            // make the bar visibly climb back up. So the life before the action is remembered and
            // a single final sheet goes out once every line has been applied.
            var vidasAntes = new Dictionary<long, (Fighter Fighter, int Vida)>();
            vidasAntes[caster.Id] = (caster, caster.CurrentHP);
            foreach (var (_, _, aQuien, _) in golpes)
            {
                if (!vidasAntes.ContainsKey(aQuien.Id))
                    vidasAntes[aQuien.Id] = (aQuien, aQuien.CurrentHP);
            }

            foreach (var (efecto, elementoDelGolpe, aQuien, lejos) in golpes)
            {
                if (!dados.TryGetValue(efecto.EffectUid, out int sacado))
                {
                    sacado = TirarElDado(efecto);
                    dados[efecto.EffectUid] = sacado;
                }
                await UnGolpeAsync(stream, fight, caster, spell, efecto, elementoDelGolpe, aQuien,
                                   sacado, lejos, critico,
                                   fromTurnTrigger: !string.Equals(disparador, Managers.EffectEngine.AlLanzar,
                                                                   StringComparison.OrdinalIgnoreCase));
            }

            foreach (var estado in vidasAntes.Values)
            {
                if (estado.Fighter.CurrentHP != estado.Vida)
                    await RefrescarLaVidaAsync(stream, fight, estado.Fighter, caster);
            }
        }

        /// <summary>
        /// Los daños base que salen del dado del efecto: de <c>diceNum</c> a <c>diceSide</c>, los
        /// dos incluidos. Si no hay cara, es un número fijo.
        ///
        /// Antes se cogía el PROMEDIO, así que un hechizo de 25 a 30 pegaba siempre 27 y en el
        /// juego nunca se veía variar un golpe.
        /// </summary>
        private static int TirarElDado(Managers.SpellEffect efecto)
        {
            int minimo = efecto.DiceNum;
            int maximo = Math.Max(efecto.DiceNum, efecto.DiceSide);
            if (maximo <= minimo) return minimo;
            lock (_dado) return _dado.Next(minimo, maximo + 1);
        }

        private static readonly Random _dado = new Random();

        /// <summary>
        /// La plantilla del arma que lleva puesta, o cero si va a mano limpia.
        ///
        /// Es lo que el cliente lee para decir con qué has pegado. Sin esto todo golpe salía como
        /// «Puñetazo» aunque el daño y el elemento fueran los de la espada.
        /// </summary>
        private static int ArmaEquipada(Fighter caster)
        {
            if (caster.Id != GameState.CharacterId) return 0;

            // La misma casilla que mira GetEquippedWeaponAsSpell: la 1 es la mano.
            const int CasillaDelArma = 1;
            foreach (var pieza in GameState.GetInventoryCopy())
                if (pieza.Position == CasillaDelArma) return pieza.ItemId;
            return 0;
        }

        /// <summary>El golpe del arma equipada, que sigue viniendo del resumen de siempre.</summary>
        private static List<(Managers.SpellEffect Efecto, int Elemento, Fighter Sobre, int Lejos)>
            GolpeDelArma(Fighter caster, Fighter target)
        {
            var fuera = new List<(Managers.SpellEffect, int, Fighter, int)>();
            var arma = DatabaseManager.GetEquippedWeaponAsSpell(GameState.CharacterId);
            if (arma == null || (arma.BaseDamageMin <= 0 && arma.BaseDamageMax <= 0)) return fuera;

            // UN GOLPE POR LÍNEA. Antes salía uno solo, con la línea de más daño y el número de
            // efecto a cero, así que un arma de tres líneas enseñaba una cifra en el chat y las
            // otras dos no existían. El número de efecto importa: es lo que hace que el cliente
            // escriba «de daños de agua» o «de robo de vida», y el cero no lo usa el servidor real
            // en ningún sitio.
            //
            // El arma pega a uno solo y a bocajarro, así que no hay distancia al centro que valga.
            // El uid de efecto tiene que ser distinto en cada línea o el dado se tiraría una vez
            // para las tres: quien las recorre las agrupa por ese uid.
            int cual = 0;
            foreach (var (efecto, elemento, minimo, maximo) in arma.WeaponLines)
            {
                fuera.Add((new Managers.SpellEffect
                {
                    EffectId = efecto,
                    EffectUid = -(++cual),
                    DiceNum = minimo,
                    DiceSide = maximo,
                }, elemento, target, 0));
            }

            // Y si por lo que sea no hay líneas, se pega con lo que había: mejor un golpe que
            // ninguno.
            if (fuera.Count == 0)
            {
                fuera.Add((new Managers.SpellEffect
                {
                    EffectId = 0,
                    DiceNum = arma.BaseDamageMin,
                    DiceSide = arma.BaseDamageMax,
                }, arma.Element, target, 0));
            }
            return fuera;
        }

        /// <summary>
        /// Whose characteristics a hit scales with: the summoner for a bomb, the caster for
        /// everybody else.
        /// </summary>
        /// <remarks>
        /// Identity stays with the caster -- the bomb is still who is announced as hitting, whose
        /// combo is read, whose spell buffs apply. Only the NUMBERS come from the Rogue: element,
        /// power, flat and critical damage, and the final-damage modifier. That is the standard
        /// rule for bombs and it is what the captures show, see <see cref="UnGolpeAsync"/>.
        /// </remarks>
        private static Fighter StatSourceOf(FightInstance fight, Fighter caster)
        {
            if (caster != null && caster.EsInvocado && EsBomba(caster.MonsterId))
                return fight.Buscar(caster.Invocador) ?? caster;
            return caster;
        }

        private static async Task UnGolpeAsync(NetworkStream stream, FightInstance fight,
                                               Fighter caster, int spell,
                                               Managers.SpellEffect efecto, int elemento,
                                               Fighter target, int sacadoDelDado, int lejosDelCentro,
                                               bool critical, int? capturedSpellBonus = null,
                                               bool fulmina = false, bool fromTurnTrigger = false)
        {
            // A copy goes at the first point of damage and takes none; a poison, which arrives
            // on a turn trigger, does not count. And the original's copies all go when HE is
            // hit -- and he takes the damage as anybody.
            if (target.EsIlusion)
            {
                if (!fromTurnTrigger) await IllusionHitAsync(stream, fight, target);
                return;
            }
            if (target.Ilusiones.Count > 0 && !fromTurnTrigger)
            {
                await DesvanecerLasIlusionesAsync(stream, fight, target);
            }

            // «Intercepta los daños» (765): the one who put the row takes the blow in the place of
            // the one hit, once -- an interceptor's own rows do not pass it on. INFERRED, see
            // EffectEngine.InterceptaLosDanos.
            if (!fulmina)
            {
                var intercepta = Interceptor(fight, caster, target, fromTurnTrigger);
                if (intercepta != null)
                {
                    Program.LogDebug($"[Combat] {intercepta.Id} intercepts the blow of {caster.Id} on {target.Id}.");
                    target = intercepta;
                }
            }

            // El elemento lo dice el catálogo: 0 neutral, 1 tierra, 2 fuego, 3 agua, 4 aire.
            var element = elemento switch
            {
                1 => Jondo.Unity.World.Fights.ElementType.Earth,
                2 => Jondo.Unity.World.Fights.ElementType.Fire,
                3 => Jondo.Unity.World.Fights.ElementType.Water,
                4 => Jondo.Unity.World.Fights.ElementType.Air,
                _ => Jondo.Unity.World.Fights.ElementType.Neutral,
            };

            // WHOSE NUMBERS THE HIT SCALES WITH. For anybody but a bomb, its own. A bomb has no
            // characteristics of its own -- no intelligence, no power, no flat damage -- so its
            // explosion came out as the bare die: "14 pasa a 45" in the log for an Explobomba at
            // Combo XI, while a wall of the very same dice, cast in the Rogue name, hit for 171.
            //
            // Measured in "tymador-bomba de agua y sismobomba resiliente": an explosion at Combo V
            // hits -1, -2 and -4 for 102, 91 and 91, on the same scale as the walls of that fight
            // (28 to 86). Explosions and walls scale alike, and walls scale with the Rogue.
            var fuente = StatSourceOf(fight, caster);

            // INVULNERABLE: a state the client's catalogue flags -- Influencia's 269 is
            // "Invulnerable" and nothing else -- takes the whole blow away, and the blow still
            // goes out: in the Influencia capture the Presión that follows lands as "jwe 97
            // f40{f2: the victim, f4: the element}", no amount, no erosion, and the target
            // keeps every point. Nothing of the blow happens: no erosion, no life steal, no
            // trigger of "when hit".
            int lejos = Jondo.Unity.World.Maps.MapGeometry.Distance(caster.CellId, target.CellId);

            // A Pacifista deals no damage at all: Klim's Carcassetagne leaves the players unable to hurt.
            if (!fulmina && Managers.SpellStates.KeepsFromDealingDamage(caster))
            {
                Program.LogDebug($"[Combate] {caster.Id} no puede hacer daño: el efecto {efecto.EffectId} no le quita nada a {target.Id}.");
                return;
            }

            if (!fulmina && Managers.SpellStates.ShieldsFromBlow(target, lejos))
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildDamage(caster.Id, efecto.EffectId, target.Id, 0, elemento)));
                Program.LogDebug($"[Combate] {target.Id} es invulnerable: el efecto {efecto.EffectId} " +
                                 $"del hechizo {spell} no le quita nada.");

                // But it is still a blow, and what waits on being hit goes off: Conde Kontatrás
                // is invulnerable for the whole fight, and being HIT is what throws him on an even
                // turn and lifts it -- the guide's "every hit on him". Not the "hit by an enemy"
                // of the attitudes (DBE), which the Influencia capture shows untouched.
                {
                    var antes = fight.TriggeringAttacker;
                    fight.TriggeringAttacker = caster;
                    string alcance = lejos <= 1 ? Managers.EffectEngine.CuandoMePeganDeCerca
                                                : Managers.EffectEngine.CuandoMePeganDeLejos;
                    await DispararAsync(stream, fight, target, alcance);
                    fight.TriggeringAttacker = antes;
                }
                await DispararLosDelGolpeAsync(stream, fight, caster, target, elemento, lejos <= 1, sacadoDelDado,
                                               deHechizo: spell != 0);
                return;
            }

            // Lo que ha salido del dado, tirado una vez para todo el lanzamiento -- turned to its
            // bottom or its top by a 781 on the caster or a 782 on the target, and scaled by the
            // MP he has left for the "% PM restantes" blows (EffectEngine).
            sacadoDelDado = Managers.EffectEngine.ConLosAzares(efecto, caster, target, fight.RoundNumber, sacadoDelDado);
            sacadoDelDado = Managers.EffectEngine.ConLosPMRestantes(efecto, sacadoDelDado, caster, fight.RoundNumber);
            int baseDamage = sacadoDelDado;

            // Salvo los que pegan EN FUNCIÓN de lo que el objetivo lleve erosionado: ahí el dado
            // no es el daño, es el TANTO POR CIENTO. Represalias lleva el efecto 1092, "daños
            // neutrales: 20% de los PdV erosionados del objetivo", y contra alguien intacto no
            // hace nada; contra uno al que se le han comido 300 de tope, hace 60.
            // Blows whose number is not the die grown by the caster: a share of a life, a fixed
            // amount, a share of the blow that set them off, so much per point spent.
            bool deSuVida = Managers.EffectEngine.ModoDe(efecto.EffectId) != Managers.EffectEngine.ModoDeDano.Normal;
            if (deSuVida)
            {
                baseDamage = Managers.EffectEngine.BaseDelModo(efecto, sacadoDelDado, caster, target, fight);
                Program.LogDebug($"[Combate] El efecto {efecto.EffectId} ({Managers.EffectEngine.ModoDe(efecto.EffectId)}) " +
                                 $"pega {baseDamage} con el dado en {sacadoDelDado}.");
            }

            if (Managers.EffectEngine.PegaSegunLoErosionado(efecto.EffectId))
            {
                // 1092-1096 read the target's eroded life, 1118-1122 the caster's: "PdV erosionados
                // del lanzador", in their own description.
                var deQuien = efecto.EffectId >= 1118 && efecto.EffectId <= 1122 ? caster : target;
                baseDamage = deQuien.VidaErosionada * sacadoDelDado / 100;
                Program.LogDebug($"[Combate] El efecto {efecto.EffectId} pega el {sacadoDelDado}% de " +
                                 $"los {deQuien.VidaErosionada} erosionados de {deQuien.Id}: {baseDamage}.");
            }

            // Y lo que le hayan sumado a ESE hechizo por embrujo: el efecto 293, "+#3 de daños
            // básicos". Flecha Helada se lo pone a sí misma, así que la segunda vez que se lanza
            // pega más que la primera.
            int deEmbrujo = capturedSpellBonus ?? caster.Buffs.DelHechizo(
                spell, Jondo.Unity.World.Fights.SpellAspect.DanoBase, fight.RoundNumber);
            if (deEmbrujo != 0)
            {
                baseDamage += deEmbrujo;
                Program.LogDebug($"[Combate] El hechizo {spell} lleva {deEmbrujo:+#;-#;0} de daños " +
                                 $"básicos por embrujo: base {baseDamage}.");
            }

            // Los daños fijos van al FINAL, sin multiplicar por la característica ni por la
            // potencia: los generales de la característica 16 más los del elemento con el que se
            // pega (88 a 92), y si el golpe sale crítico, además los daños críticos (86).
            int flat = ConBonos(fuente, DanoFijoCaracteristica, fuente.FlatDamage, fight.RoundNumber)
                     + (critical ? ConBonos(fuente, DanoCriticoCaracteristica, fuente.CriticalDamage, fight.RoundNumber) : 0);

            // Y la caída de la zona: el que está en el centro se lleva el golpe entero y a cada
            // casilla de distancia se le quita el tanto por ciento que diga el hechizo. Se aplica
            // ANTES de las características y las resistencias, sobre los daños base, que es lo que
            // el efecto describe.
            int enElBorde = Managers.EffectEngine.ConLaCaidaDeLaZona(baseDamage, efecto, lejosDelCentro);
            if (enElBorde != baseDamage)
            {
                Program.LogDebug($"[Combate] {target.Id} está a {lejosDelCentro} casilla(s) del centro: " +
                                 $"los daños base bajan de {baseDamage} a {enElBorde} " +
                                 $"({efecto.PasoDeCaida}% por casilla, tope {efecto.TopeDeCaida}).");
                baseDamage = enElBorde;
            }

            // LAS CARACTERÍSTICAS CON SUS BONOS DE COMBATE. Ésta era la mitad que faltaba de
            // Tiros Potentes: sus +250 de potencia se guardaban como embrujo y se anunciaban al
            // panel, pero la fórmula de daño seguía leyendo el número de siempre, así que el
            // hechizo no hacía pegar más. El total de una característica es lo de base, más los
            // pergaminos y el equipo —que ya venían en el Fighter—, más lo que pongan los hechizos
            // mientras dure el combate.
            int elementoDelPersonaje = ConBonos(fuente, CaracteristicaDelElemento(element),
                                                fuente.GetStatForElement(element), fight.RoundNumber);
            int potencia = ConBonos(fuente, PotenciaCaracteristica, fuente.Power, fight.RoundNumber);

            int damage = Jondo.Unity.World.Fights.DamageCalculator.CalculateDamage(
                baseDamage: baseDamage,
                element: element,
                statValue: deSuVida ? 0 : elementoDelPersonaje,
                power: deSuVida ? 0 : potencia,
                flatElementDamage: deSuVida ? 0 : fuente.GetFlatDamageForElement(element),
                flatDamage: deSuVida ? 0 : flat,
                targetResPct: target.GetResPctForElement(element),
                targetFlatRes: 0);

            // La forme bestiale pose +20 sur la caractéristique 107, dont la base vaut 100.
            // C'est un multiplicateur final : il s'applique après caractéristiques/résistances et
            // avant les multiplicateurs de dégâts subis de la cible.
            int finalInfligido = 100 + fuente.Buffs.De(DanoFinalInfligidoCaracteristica,
                                                       fight.RoundNumber);
            if (finalInfligido != 100)
            {
                int antes = damage;
                damage = Math.Max(0, (int)Math.Round(damage * finalInfligido / 100.0));
                Program.LogDebug($"[Combate] {caster.Id} pega con el daño final al " +
                                 $"{finalInfligido}%: {antes} se queda en {damage}.");
            }

            // Through portals, the blow grows with the network it crossed: "+#3% daños, +#1% de
            // daños por casilla que separe entre 2 portales" (PortalNetwork.BonusPercent).
            // INFERRED as one more final multiplier: no capture holds the same blow both ways.
            if (fight.PortalBonusPercent != 0 && damage > 0)
            {
                int antesDelPortal = damage;
                damage = Math.Max(0, (int)Math.Round(damage * (100 + fight.PortalBonusPercent) / 100.0));
                Program.LogDebug($"[Portal] +{fight.PortalBonusPercent}% through the portals: {antesDelPortal} to {damage}.");
            }

            // EL COMBO. Va con los multiplicadores del que pega y no con los del que recibe,
            // porque es suyo: cada combo hace que la bomba estalle más fuerte, del 0% en Combo I
            // al 360% en Combo XV. Se lee del estado que lleva puesto y no de los embrujos, que
            // se acumulan uno por peldaño y darían 120% donde toca 60%.
            int combo = Managers.Combo.PercentOf(caster);

            // A WALL IS CAST IN THE ROGUE NAME, AND THE ROGUE CARRIES NO COMBO. So this read zero
            // for every wall, and a Combo X wall hit for the same 160-170 as a Combo I one. The
            // combo of a wall is the combo of the bombs holding it up -- "los muros se benefician
            // de la mitad del combo", says the sheet -- and which bomb when they differ it does
            // not say: the highest is the inference already written down where the walls are
            // raised, and it stays an inference here.
            if (Managers.BombWalls.WallSpell.Values.Contains(spell))
            {
                combo = 0;
                var muro = Managers.BombWalls.Covering(TodosLosCombatientes(fight), caster,
                                                       target.CellId);
                if (muro != null)
                {
                    foreach (var bomba in muro.Bombs)
                        combo = Math.Max(combo, Managers.Combo.PercentOf(bomba));
                }
                combo /= 2;
            }

            if (combo != 0)
            {
                int antesDelCombo = damage;
                damage = Math.Max(0, (int)Math.Round(damage * (100 + combo) / 100.0));
                Program.LogDebug($"[Combo] {caster.Id} está en el nivel " +
                                 $"{Managers.Combo.LevelOf(caster)}, +{combo}%: " +
                                 $"{antesDelCombo} pasa a {damage}.");
            }

            // What the caster deals, in percent, multiplying everything above: a dream's "%
            // damage". Its guide: every bonus adds up first, and the % of damage multiplies last.
            if (caster.DamageDealtPercent != 100 && damage > 0)
            {
                int antesDelPorcentaje = damage;
                damage = Math.Max(0, (int)Math.Round(damage * caster.DamageDealtPercent / 100.0));
                Program.LogDebug($"[Combate] {caster.Id} hace el {caster.DamageDealtPercent}% de daño: " +
                                 $"{antesDelPorcentaje} pasa a {damage}.");
            }

            // THE KINDS OF THIS BLOW, for the rows that name one: "D" any, "DM"/"DCAC" from
            // next door, "DR" from further away, "DTB"/"DTE" a turn's poison.
            bool deCerca = Jondo.Unity.World.Maps.MapGeometry.Distance(caster.CellId, target.CellId) <= 1;
            var clases = new List<string> { "D", deCerca ? Managers.EffectEngine.CuandoMePeganDeCerca
                                                         : Managers.EffectEngine.CuandoMePeganDeLejos };
            if (deCerca) clases.Add("DCAC");
            if (fromTurnTrigger) { clases.Add("DTB"); clases.Add("DTE"); }

            // Los MULTIPLICADORES de quien lo recibe: "daños sufridos x110%" es el efecto 1163, el
            // que pone Represalias. Van al final, sobre el daño ya calculado. Salto's is a row
            // under "D", read by any blow of the round.
            int multiplica = target.Buffs.Multiplicador(DanoSufridoPorCiento, fight.RoundNumber, clases);
            if (multiplica != 100)
            {
                int antes = damage;
                damage = (int)Math.Round(damage * multiplica / 100.0);
                Program.LogDebug($"[Combate] {target.Id} sufre los daños al {multiplica}%: " +
                                 $"{antes} pasa a {damage}.");
            }

            // "-N de daños recibidos" (105, 265): a flat cut at the end of the sum, from the
            // rows the target holds for blows of this KIND -- Remisión's on a bomb is ranged
            // blows only. A kill is not a blow.
            if (!fulmina && damage > 0)
            {
                int reduccion = target.Buffs.ReduccionDeDanoRecibido(fight.RoundNumber, clases);
                if (reduccion > 0)
                {
                    int antes = damage;
                    damage = Math.Max(0, damage - reduccion);
                    Program.LogDebug($"[Combate] {target.Id} recibe {reduccion} menos de daño " +
                                     $"({(deCerca ? "de cerca" : "de lejos")}): {antes} se queda en {damage}.");
                }
            }

            // «Comparte los daños» (1061): the blow cut in equal shares among the fighters one
            // cast linked, each his own, what does not divide staying on the one hit. INFERRED,
            // see EffectEngine.ComparteLosDanos.
            if (!fulmina && damage > 0)
            {
                var enlazados = Enlazados(fight, target, clases);
                if (enlazados.Count > 0)
                {
                    int parte = damage / (enlazados.Count + 1);
                    Program.LogDebug($"[Combat] {target.Id} shares {damage} with {string.Join(", ", enlazados.Select(e => e.Id))}: {parte} each.");
                    foreach (var otro in enlazados)
                        await UnaParteDelGolpeAsync(stream, fight, caster, efecto.EffectId, elemento, otro, parte);
                    damage -= parte * enlazados.Count;
                }
            }

            // FULMINAR: el efecto 141 del catálogo, «Mata al objetivo». No es un golpe muy grande,
            // es otra cosa, y por eso entra AQUÍ y no arriba: ni el dado, ni las resistencias, ni
            // los porcentajes pueden dejar a nadie exactamente en cero. Se le quita la vida que
            // tenga y se sigue por el mismo camino que cualquier golpe —el anuncio, la muerte, el
            // botín, el fin del combate—, que es lo único que hay que compartir.
            if (fulmina) damage = target.CurrentHP;

            // EL ESCUDO se come el golpe antes que la vida, y no lo para todo: lo que sobra sigue
            // su camino. Va antes del recorte a la vida que queda, porque un golpe de doscientos
            // contra un escudo de ciento cincuenta son cincuenta de vida, no doscientos.
            if (!fulmina && target.PuntosDeEscudo > 0)
            {
                int antesDelEscudo = damage;
                damage = target.PasarPorElEscudo(damage);
                Program.LogDebug($"[Combate] El escudo de {target.Id} se come " +
                                 $"{antesDelEscudo - damage} de {antesDelEscudo}; le quedan " +
                                 $"{target.PuntosDeEscudo} de escudo.");
            }

            // Lo que se ANUNCIA nunca puede pasar de la vida que le queda. Si a un pío de setenta
            // le entran doscientos, el golpe que ve el jugador es de setenta: por encima de eso no
            // hay vida que quitar, y el número que sobra sólo confunde.
            int aplicado = Math.Min(damage, target.CurrentHP);

            // A threshold (2872) holds the life where it stands: the blow that reaches it goes no
            // further, and the threshold goes -- setting off "TR" and the spell that put it.
            Jondo.Unity.World.Fights.Buff umbralCruzado = null;
            if (!fulmina && aplicado > 0)
            {
                foreach (var umbral in target.Buffs.Puestos
                             .Where(b => b.EffectId == Managers.EffectEngine.Umbral && !b.Pendiente && b.Vivo(fight.RoundNumber))
                             .OrderByDescending(b => b.Cuanto).ToList())
                {
                    int suelo = Math.Max(1, (int)Math.Ceiling(target.MaxHP * umbral.Cuanto / 100.0));
                    if (target.CurrentHP <= suelo || target.CurrentHP - aplicado > suelo) continue;
                    aplicado = target.CurrentHP - suelo;
                    umbralCruzado = umbral;
                    Program.LogDebug($"[Combate] {target.Id} se queda en su umbral de {umbral.Cuanto}% ({suelo} de vida).");
                    break;
                }
            }

            // The blow that finishes him: what fires on his death goes first, with him still
            // standing. If that finished him on its own -- a Polvo bomb blowing itself up --
            // the death has been announced in there and this blow has nothing left to take.
            if (aplicado >= target.CurrentHP && !target.Muriendo)
            {
                if (!await AlMorirAsync(stream, fight, target, caster)) return;
                aplicado = Math.Min(damage, target.CurrentHP);
            }

            target.TakeDamage(aplicado);
            AnotarElGolpe(fight, caster, target, aplicado, fromTurnTrigger);

            // Aqui se rompen el Intocable -si el que pierde vida es aliado- y el Elemental.
            await ChallengeWatcher.DamagedAsync(stream, fight, target, aplicado, caster, elemento);

            // Y la EROSIÓN: además de la vida de ahora, cada golpe se lleva un pellizco del tope.
            //
            // Cuánto lo dice la característica 75 del que recibe, que se llama "Erosión" en el
            // catálogo del cliente y viaja en la ficha de combate; en la captura del Ocra vale 10.
            // Con mil de vida y un golpe de cien, el bicho se queda en 900/990.
            //
            // Se erosiona sobre el daño CALCULADO, no sobre el recortado: pegarle doscientos a uno
            // que tiene setenta de vida erosiona por doscientos.
            int porcientoDeErosion = target.Otra(Fighter.CaracteristicaDeErosion)
                                   + target.Buffs.De(Fighter.CaracteristicaDeErosion, fight.RoundNumber);
            int erosionado = target.Erosionar(damage, porcientoDeErosion);
            if (erosionado > 0)
            {
                Program.LogDebug($"[Combate] {target.Id} se erosiona {erosionado} de vida máxima " +
                                 $"({porcientoDeErosion}% de {damage}); se queda en " +
                                 $"{target.CurrentHP}/{target.MaxHP}, {target.VidaErosionada} erosionados.");
            }

            // Le han pegado, y eso lo miran las actitudes: es la mitad de la regla del Dofus Ocre.
            if (aplicado > 0 && caster.TeamId != target.TeamId) target.LeHanPegado = true;

            // El f14 es EL NÚMERO DE EFECTO, no un código de elemento: el 91 es robo de agua, el
            // 96 daños de agua, el 99 daños de fuego... Iba clavado al 91, así que todo golpe se
            // anunciaba como robo de agua fuera del elemento que fuera.
            //
            // A KILL IS NOT A HIT ON THE WIRE. The 141 takes the life here, but the real server
            // announces nothing for it beyond the death itself: of the 431 deaths in the class
            // and combat captures, not one is preceded by a "jwe 141", and the Tymobot's own
            // turn-end death is "jwe 300 jya jwe 300 jwe 103", no damage frame anywhere.
            if (!fulmina)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildDamage(caster.Id, efecto.EffectId,
                                                      target.Id, aplicado, elemento, erosionado)));
            }

            // EL ROBO DE VIDA. Los efectos 91 a 95 no son daño a secas: son «robo de agua»,
            // «robo de tierra», «robo de aire», «robo de fuego» y «robo neutral», y el 82 es el
            // robo neutral fijo. Los seis pegan igual que un daño normal y ADEMÁS curan a quien
            // lanza por la mitad de lo que han quitado.
            //
            // Aquí se trataban como daño y nada más, así que el Ocra pegaba con Flecha Voraz y no
            // se curaba. Y explica de paso lo del arma: la espada del personaje lleva
            // «[91, 0, 27, 33]», que no es daño de agua sino ROBO de agua, y su golpe principal
            // se quedaba a medias.
            //
            // La mitad, redondeando hacia abajo, y nunca por encima del tope: quien está a tope
            // de vida no gana nada. Se cura sobre lo APLICADO, no sobre lo calculado: si al
            // objetivo le quedaban veinte y el golpe era de trescientos, se roban diez.
            if (aplicado > 0 && Managers.EffectEngine.EsRoboDeVida(efecto.EffectId))
            {
                int curado = Math.Min(aplicado / 2, Math.Max(0, caster.MaxHP - caster.CurrentHP));
                if (curado > 0)
                {
                    caster.CurrentHP += curado;
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildHeal(caster.Id, curado, caster.Id)));
                    AnotarLaCura(fight, caster, caster, curado);
                    Program.LogDebug($"[Combate] Robo de vida del efecto {efecto.EffectId}: " +
                                     $"{caster.Id} se cura {curado} de los {aplicado} quitados; " +
                                     $"se queda en {caster.CurrentHP}/{caster.MaxHP}.");
                }
            }

            // HurtAsync enverra la fiche de vie une seule fois, après toutes les lignes du sort.
            // Ici, on ne fait qu'appliquer et annoncer ce composant individuel.

            // What the blow sets off, with the one who dealt it at hand for the "O" of the
            // masks: when hit by an enemy (DBE), and when hurt from next door (DM) or from
            // further away (DR), by anybody -- Remisión on a bomb throws its own Tymador back
            // when he hits it in melee. Melee is the attacker one cell away, "cuerpo a cuerpo"
            // in the sheets; the rest is ranged. A kill is not a blow.
            fight.TriggeringAttacker = caster;
            if (target.LeHanPegado)
            {
                await ActitudesAsync(stream, fight, target, Managers.EffectEngine.CuandoMePegan);
                await EngancheAsync(stream, fight, target, Managers.EffectEngine.CuandoMePegan);
            }
            if (aplicado > 0 && !fulmina && target.IsAlive)
            {
                string alcance = deCerca
                    ? Managers.EffectEngine.CuandoMePeganDeCerca
                    : Managers.EffectEngine.CuandoMePeganDeLejos;
                await ActitudesAsync(stream, fight, target, alcance);
                await EngancheAsync(stream, fight, target, alcance);
                await DispararLosDelGolpeAsync(stream, fight, caster, target, elemento, deCerca, damage,
                                               deHechizo: spell != 0);
            }
            if (umbralCruzado != null && target.IsAlive && target.Buffs.QuitarFila(umbralCruzado))
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                    Network.FightProtocol.BuildBuffGone(target.Id, umbralCruzado.Numero)));
                await DispararAsync(stream, fight, target, Managers.EffectEngine.AlCruzarElUmbral(umbralCruzado.HechizoOrigen));
            }
            fight.TriggeringAttacker = null;

            Program.LogDebug($"[Combate] {aplicado} de daño a {target.Id} (calculado {damage}); " +
                             $"le quedan {target.CurrentHP}.");

            // La muerte, LA ÚLTIMA. Y sin mandar antes una ficha con la vida a cero: el servidor
            // real no la manda, la vida la descuenta el cliente del golpe de arriba, y mandarla
            // hacía que el bicho se cayera muerto antes de que se viera la animación.
            if (!target.IsAlive)
            {
                // What goes off on his death has already gone off, above, with him standing.
                CarriedFollows(fight, target);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildDeath(caster.Id, target.Id)));
                Program.LogDebug($"[Combate] {target.Id} se queda sin vida.");

                // Orden de niveles, remate con arma y caida junto a un obstaculo: los tres se
                // juzgan aqui. El arma es el hechizo cero, que es como viaja el cuerpo a cuerpo.
                await ChallengeWatcher.DiedAsync(stream, fight, target,
                                                 spell == Network.FightProtocol.HechizoCuerpoACuerpo,
                                                 caster);
                await ChallengeWatcher.AllyDiedAsync(stream, fight, target);

                await CaenSusInvocadosAsync(stream, fight, target);
                await ReenviarLaListaAsync(stream, fight);

                // Y si el que ha caído era una bomba, el muro que sostenía se cae con ella. Sin
                // esto las casillas rojas se quedaban pintadas hasta el siguiente turno, que es
                // lo que se veía después de un Detonador: bombas muertas y muro entero.
                await ReconciliarLosMurosAsync(stream, fight);

                await AlMatarAsync(stream, fight, caster, target);
            }
        }

        /// <summary>
        /// The one whose blow took the last life has killed (K): what waits on him killing goes
        /// off, with the dead one as the one who set it off. And a portal the dead one stood on
        /// comes back on.
        /// </summary>
        private static async Task AlMatarAsync(NetworkStream stream, FightInstance fight, Fighter killer, Fighter dead)
        {
            await RefreshPortalsAsync(fight);
            if (killer == null || !killer.IsAlive || killer == dead) return;
            var antes = fight.TriggeringAttacker;
            fight.TriggeringAttacker = dead;
            await DispararAsync(stream, fight, killer, Managers.EffectEngine.AlMatar);
            fight.TriggeringAttacker = antes;
        }

        /// <summary>The kinds of a blow, as the rows under a damage trigger name them.</summary>
        private static List<string> ClasesDelGolpe(Fighter caster, Fighter target, bool fromTurnTrigger)
        {
            bool deCerca = Jondo.Unity.World.Maps.MapGeometry.Distance(caster.CellId, target.CellId) <= 1;
            var clases = new List<string> { "D", deCerca ? Managers.EffectEngine.CuandoMePeganDeCerca
                                                         : Managers.EffectEngine.CuandoMePeganDeLejos };
            if (deCerca) clases.Add("DCAC");
            if (fromTurnTrigger) { clases.Add("DTB"); clases.Add("DTE"); }
            return clases;
        }

        /// <summary>
        /// Who intercepts a blow on <paramref name="target"/>: the caster of a live 765 row of
        /// his under a kind this blow is of, alive, and neither the one hit nor the one hitting.
        /// </summary>
        internal static Fighter Interceptor(FightInstance fight, Fighter caster, Fighter target, bool fromTurnTrigger)
        {
            if (fight == null || caster == null || target == null) return null;
            var clases = ClasesDelGolpe(caster, target, fromTurnTrigger);
            foreach (var row in target.Buffs.LeidasPorElGolpe(Managers.EffectEngine.InterceptaLosDanos, fight.RoundNumber, clases))
            {
                var quien = fight.Buscar(row.Quien);
                if (quien == null || !quien.IsAlive || quien == target || quien == caster) continue;
                return quien;
            }
            return null;
        }

        /// <summary>
        /// The fighters a blow on <paramref name="target"/> is shared with: every other living
        /// bearer of a 1061 row laid by the same caster with the same spell as the one of his
        /// that this blow reads.
        /// </summary>
        internal static List<Fighter> Enlazados(FightInstance fight, Fighter target, IReadOnlyCollection<string> clases)
        {
            var fuera = new List<Fighter>();
            if (fight == null || target == null) return fuera;
            foreach (var row in target.Buffs.LeidasPorElGolpe(Managers.EffectEngine.ComparteLosDanos, fight.RoundNumber, clases))
            {
                foreach (var otro in fight.Todos)
                {
                    if (otro == null || otro == target || !otro.IsAlive || fuera.Contains(otro)) continue;
                    if (otro.Buffs.Puestos.Any(b => b.EffectId == Managers.EffectEngine.ComparteLosDanos
                                                    && b.Quien == row.Quien && b.HechizoOrigen == row.HechizoOrigen
                                                    && !b.Pendiente && b.Vivo(fight.RoundNumber)))
                        fuera.Add(otro);
                }
                if (fuera.Count > 0) break;
            }
            return fuera;
        }

        /// <summary>
        /// A share of a blow already worked out, on a fighter it was shared with: his shield, his
        /// life, his erosion, the blow on the wire as the blow it is part of, and his death. It
        /// sets off nothing of its own -- INFERRED, as the whole share is.
        /// </summary>
        private static async Task UnaParteDelGolpeAsync(NetworkStream stream, FightInstance fight, Fighter caster,
                                                        int efecto, int elemento, Fighter quien, int dano)
        {
            if (quien == null || !quien.IsAlive || dano <= 0) return;
            if (quien.PuntosDeEscudo > 0) dano = quien.PasarPorElEscudo(dano);

            int aplicado = Math.Min(dano, quien.CurrentHP);
            if (aplicado >= quien.CurrentHP && !quien.Muriendo)
            {
                if (!await AlMorirAsync(stream, fight, quien, caster)) return;
                aplicado = Math.Min(dano, quien.CurrentHP);
            }
            quien.TakeDamage(aplicado);
            AnotarElGolpe(fight, caster, quien, aplicado, false);
            int porciento = quien.Otra(Fighter.CaracteristicaDeErosion)
                          + quien.Buffs.De(Fighter.CaracteristicaDeErosion, fight.RoundNumber);
            int erosionado = quien.Erosionar(dano, porciento);
            await ChallengeWatcher.DamagedAsync(stream, fight, quien, aplicado, caster, elemento);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildDamage(caster.Id, efecto, quien.Id, aplicado, elemento, erosionado)));
            await RefrescarLaVidaAsync(stream, fight, quien, caster);
            if (quien.IsAlive) return;

            CarriedFollows(fight, quien);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildDeath(caster.Id, quien.Id)));
            await ChallengeWatcher.DiedAsync(stream, fight, quien, false, caster);
            await ChallengeWatcher.AllyDiedAsync(stream, fight, quien);
            await CaenSusInvocadosAsync(stream, fight, quien);
            await ReenviarLaListaAsync(stream, fight);
            await AlMatarAsync(stream, fight, caster, quien);
        }

        /// <summary>
        /// El daño de haberse estampado al recibir un empujón, y el que se lleva quien hizo de
        /// pared.
        ///
        /// El motor ya ha hecho la cuenta —ver la rama de empuje de EffectEngine— y aquí sólo se
        /// cobra: se recorta por la vida que queda, se erosiona, se anuncia y se mira si alguien se
        /// ha muerto. Es la misma puerta por la que pasa un golpe normal, a propósito: matar por
        /// colisión tiene que anunciarse igual que matar de un flechazo.
        ///
        /// Van los DOS en la misma secuencia y en este orden —primero el empujado con el golpe
        /// entero, detrás la pared con la mitad—, que es como salen las 9 parejas medidas.
        /// </summary>
        private static async Task DanoDeColisionAsync(NetworkStream stream, FightInstance fight,
                                                      Fighter quienEmpuja, Managers.Outcome c)
        {
            if (c.CollisionDamage <= 0) return;

            await UnEstampadoAsync(stream, fight, quienEmpuja, c.Sobre, c.CollisionDamage);

            if (c.Blocker != null && c.CollisionDamageToBlocker > 0)
            {
                await UnEstampadoAsync(stream, fight, quienEmpuja, c.Blocker,
                                       c.CollisionDamageToBlocker, indirecto: true);
            }
        }

        /// <summary>Un solo golpe de colisión, contra uno solo.</summary>
        private static async Task UnEstampadoAsync(NetworkStream stream, FightInstance fight,
                                                   Fighter quienEmpuja, Fighter quien, int dano,
                                                   bool indirecto = false)
        {
            if (quien == null || !quien.IsAlive || dano <= 0) return;

            // The collision is a trigger whatever it costs: PD on the one pushed, PPD and PMD on
            // the one he was pushed into. Klim and Obsidiantre are made vulnerable exactly so --
            // somebody pushed into them -- while they are invulnerable, so it goes off first.
            {
                var antes = fight.TriggeringAttacker;
                fight.TriggeringAttacker = quienEmpuja;
                if (indirecto)
                {
                    await DispararAsync(stream, fight, quien, Managers.EffectEngine.AlChocarleUnEmpujado);
                    await DispararAsync(stream, fight, quien, Managers.EffectEngine.AlChocarleUnEmpujadoM);
                }
                else
                {
                    await DispararAsync(stream, fight, quien, Managers.EffectEngine.AlChocarEmpujado);
                    // And on the one who pushed him: he dealt push damage (CPD).
                    if (quienEmpuja != null && quienEmpuja != quien && quienEmpuja.IsAlive)
                    {
                        fight.TriggeringAttacker = quien;
                        await DispararAsync(stream, fight, quienEmpuja, Managers.EffectEngine.CausaDanoDeEmpuje);
                    }
                }
                fight.TriggeringAttacker = antes;
                if (!quien.IsAlive) return;
            }

            // And an invulnerable one loses nothing to it, the same as to a spell's blow.
            if (Managers.SpellStates.ShieldsFromBlow(quien, 1))
            {
                Program.LogDebug($"[Combate] {quien.Id} es invulnerable: el choque no le quita nada.");
                return;
            }

            // Lo que se anuncia nunca puede pasar de la vida que le queda, igual que en un golpe
            // normal: por encima de eso no hay vida que quitar.
            int aplicado = Math.Min(dano, quien.CurrentHP);
            if (aplicado >= quien.CurrentHP && !quien.Muriendo)
            {
                if (!await AlMorirAsync(stream, fight, quien, quienEmpuja)) return;
                aplicado = Math.Min(dano, quien.CurrentHP);
            }
            quien.TakeDamage(aplicado);
            AnotarElGolpe(fight, quienEmpuja, quien, aplicado, push: true);

            // La erosión se calcula sobre el daño ENTERO, no sobre el recortado. Medido: en el
            // koliseo hay un golpe de 663 anunciado como 417 —recortado por la vida— y con la
            // erosión en 66, que es la décima parte de 663 y no de 417.
            int porciento = quien.Otra(Fighter.CaracteristicaDeErosion) +
                            quien.Buffs.De(Fighter.CaracteristicaDeErosion, fight.RoundNumber);
            int erosionado = quien.Erosionar(dano, porciento);

            await ChallengeWatcher.DamagedAsync(stream, fight, quien, aplicado, quienEmpuja, -1);

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildPushDamage(quienEmpuja.Id, quien.Id, aplicado, erosionado)));

            if (aplicado > 0 && quienEmpuja.TeamId != quien.TeamId) quien.LeHanPegado = true;

            await RefrescarLaVidaAsync(stream, fight, quien, quienEmpuja);

            Program.LogDebug($"[Combate] {quien.Id} se estampa al ser empujado: {aplicado} de daño " +
                             $"(calculado {dano}, erosión {erosionado}); le quedan {quien.CurrentHP}.");

            if (quien.LeHanPegado)
            {
                await ActitudesAsync(stream, fight, quien, Managers.EffectEngine.CuandoMePegan);
                await EngancheAsync(stream, fight, quien, Managers.EffectEngine.CuandoMePegan);
            }

            if (quien.IsAlive) return;

            // What goes off on his death has already gone off, above, with him standing.
            CarriedFollows(fight, quien);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildDeath(quienEmpuja.Id, quien.Id)));
            Program.LogDebug($"[Combate] {quien.Id} se queda sin vida por el golpe del empujón.");

            await ChallengeWatcher.DiedAsync(stream, fight, quien, false, quienEmpuja);
            await ChallengeWatcher.AllyDiedAsync(stream, fight, quien);
            await CaenSusInvocadosAsync(stream, fight, quien);
            await ReenviarLaListaAsync(stream, fight);
            await AlMatarAsync(stream, fight, quienEmpuja, quien);
        }

        /// <summary>
        /// Al que se muere se le caen TODAS sus invocaciones, en el acto.
        ///
        /// No es que dejen de contar para el final del combate: es que desaparecen. Una baliza no
        /// sobrevive a su Ocra ni llega a jugar el turno que tuviera pendiente.
        /// </summary>
        internal static async Task CaenSusInvocadosAsync(NetworkStream stream, FightInstance fight,
                                                        Fighter muerto)
        {
            if (muerto != null && !fight.Muertos.Contains(muerto)) fight.Muertos.Add(muerto);

            // A monster's doing goes with it: its rows on everybody, the states only they held and
            // the rows it armed. A Pépite's mark on Crunchidor, an Éclat's invulnerability on its
            // escort, a Malamibe's lock on the next one stayed after they died.
            // And a summon's, whoever's it is. A Tymador's bomb puts "+1 AP to Explobomba" on its
            // owner as it comes out (Encendimiento, duration -1), and the real server takes it off
            // when the bomb dies: "explobomba-...-explotandolas", frames 3799-3819, each bomb's
            // death (jwe 103) and then a jya of its row on the Tymador, 15, 18 and 12. Kept, every
            // bomb of the fight went on raising the cost, and by the third turn a second bomb could
            // not be paid for.
            if (muerto != null && (EsDelBandoDeLosMonstruos(fight, muerto) || muerto.EsInvocado))
            {
                foreach (var otro in TodosLosCombatientes(fight).ToList())
                {
                    if (otro == null || otro == muerto || !otro.IsAlive) continue;
                    var quitados = otro.Buffs.QuitarLoDe(muerto.Id);
                    foreach (var quitado in quitados)
                    {
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                            Network.FightProtocol.BuildBuffGone(otro.Id, quitado.Numero)));
                    }
                    foreach (var estado in quitados.Where(q => q.Estado != 0 && q.EffectId == Jondo.Unity.World.Combat.EffectSupport.AddState)
                                                   .Select(q => q.Estado).Distinct())
                    {
                        if (!otro.Buffs.TieneEstado(estado))
                            await DispararAsync(stream, fight, otro, Managers.EffectEngine.AlQuitarseElEstado(estado));
                    }
                }
            }

            if (muerto != null) await DispararLosDeUnaMuerteAsync(stream, fight, muerto);

            if (muerto == null || muerto.EsInvocado) return;

            var suyos = new List<Fighter>();
            foreach (var f in fight.Azul) if (f.EsInvocado && f.IsAlive && f.Invocador == muerto.Id) suyos.Add(f);
            foreach (var f in fight.Rojo) if (f.EsInvocado && f.IsAlive && f.Invocador == muerto.Id) suyos.Add(f);
            if (suyos.Count == 0) return;

            foreach (var invocado in suyos)
            {
                invocado.CurrentHP = 0;
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildDeath(muerto.Id, invocado.Id)));
            }

            // Y fuera del carrusel, para que no les llegue a tocar.
            fight.RebuildTurnOrderOnFighterDeath();

            Program.LogDebug($"[Combate] Con {muerto.Id} se caen sus {suyos.Count} invocación(es): " +
                             $"{string.Join(", ", suyos.ConvertAll(f => f.Id.ToString()))}.");
        }

        /// <summary>
        /// El turno de un monstruo: se acerca y pega hasta que se le acaben los puntos.
        ///
        /// No es una inteligencia gran cosa —va a por el enemigo vivo más cercano, se le pone al
        /// lado gastando PM y le lanza lo que pueda pagar con los PA que tenga— pero hace lo que
        /// tiene que hacer y respeta los puntos, que es lo que el cliente comprueba.
        /// </summary>
        /// <summary>
        /// A monster's turn: <see cref="Managers.MonsterTactics"/> decides, this sends. While there
        /// is something worth doing it walks where it has to and casts; then it places itself.
        /// </summary>
        private static async Task MonsterTurnAsync(NetworkStream stream, FightInstance fight,
                                                   Fighter monster)
        {
            var spells = TacticsOf(monster);
            var board = BoardOf(fight);

            for (int step = 0; step < TopeDeLanzamientosPorTurno; step++)
            {
                // Only the spells whose level allows it as he stands now: Kontatrás's Jaquemart
                // asks for Invulnerable (HS=56) and Multicuenta for its absence (HS!56).
                var usable = spells.FindAll(s => Managers.SpellCriteria.Allows(monster, s.Id, s.Grade));
                var action = Managers.MonsterTactics.Next(board, monster, usable);
                if (action == null) break;

                if (action.Path.Count > 1 && !await MonsterWalkAsync(stream, fight, monster, new List<int>(action.Path)))
                    return;

                // The plan counted on the tackles of its path, but the walk is what happened: a
                // monster that did not get where it meant to, or that was left without the AP,
                // thinks again from where it stands instead of casting from the wrong cell.
                if (monster.CellId != action.From || monster.CurrentAP < action.Spell.Cost)
                {
                    Program.LogDebug($"[AI] {monster.Id} stopped at {monster.CellId} with {monster.CurrentAP} AP " +
                                     $"instead of casting {action.Spell.Id} from {action.From}; thinking again.");
                    continue;
                }

                Program.LogDebug($"[IA] {monster.Id} lanza {action.Spell.Id} a {action.Target.Id} " +
                                 $"(casilla {action.TargetCell}) desde {monster.CellId}, valor {action.Score:0.0}.");
                await MonsterCastAsync(stream, fight, monster, action.Spell, action.Target, action.TargetCell);
                // Dead of its own blow, or the blow that emptied a side: the turn ends there, as
                // in the Dopeul's defeat, with no step taken after it.
                if (!monster.IsAlive || !fight.SigueVivo(FightInstance.Azules) || !fight.SigueVivo(FightInstance.Rojos))
                {
                    await EndMonsterTurnAsync(stream, fight);
                    return;
                }

                // "Hace pasar de turno" (1031) in its own spell: the turn is over.
                if (fight.EndTurnRequested)
                {
                    fight.EndTurnRequested = false;
                    Program.LogDebug($"[IA] {monster.Id} pasa el turno por su hechizo {action.Spell.Id}.");
                    await EndMonsterTurnAsync(stream, fight);
                    return;
                }
            }

            var place = Managers.MonsterTactics.Reposition(board, monster, spells);
            if (place.Count > 1 && !await MonsterWalkAsync(stream, fight, monster, place)) return;

            await EndMonsterTurnAsync(stream, fight);
        }

        /// <summary>The board as the tactics see it: the fight's floor, its line of sight, its fighters.</summary>
        internal static Managers.MonsterTactics.Board BoardOf(FightInstance fight)
        {
            var blockers = MapManager.GetLosBlockers(fight.ArenaMapId);
            return new Managers.MonsterTactics.Board
            {
                Walkable = cell => PisableEnCombate(fight, cell),
                Sees = (from, to) => MapGeometry.HasLineOfSight(from, to,
                    cell => (blockers != null && blockers.Contains(cell)) || BlocksSight(fight, cell)),
                Fighters = TodosLosCombatientes(fight).ToList(),
                // The walk's own rule, so that what it plans is what it will pay.
                TackleAt = (mover, cell, ap, mp) => TackleAt(fight, mover, cell, ap, mp).Loss,
                // And the engine's own reading of whom a row reaches, for the class spells.
                Reach = (row, caster, from, aim) => Managers.EffectEngine.ReachOf(fight, caster, row, from, aim),
                // And its summon limit, as it will be checked when the summon comes out.
                CanSummon = (caster, template, grade) => FitsTheSummonLimit(fight, caster, template, grade),
                // An invisible enemy where he was last seen, not where he is.
                Hidden = Managers.MonsterTactics.IsInvisible,
                LastSeen = fighter => fighter.LastSeenCell,
                // The traps of the side whose turn it is.
                Trapped = cell => OwnTraps(fight).Any(g => g.Cubre(cell)),
                TrapsOut = () => OwnTraps(fight).Count(),
            };
        }

        /// <summary>The traps laid by the side whose turn it is, not yet sprung.</summary>
        private static IEnumerable<Jondo.Unity.World.Fights.Glifo> OwnTraps(FightInstance fight)
        {
            int side = fight.CurrentFighter?.TeamId ?? -1;
            return fight.Glifos.Where(g => !g.Gastado && g.Tipo == Managers.EffectEngine.ColocaUnaTrampa
                                           && fight.Buscar(g.Dueno)?.TeamId == side);
        }

        /// <summary>
        /// Whether a fighter stands in the way of sight on that cell: anybody alive but the one
        /// whose turn it is, who is planning where it will be and has left where it stood. In the
        /// game a fighter blocks sight like a pillar does; without this a JondoBot shot through
        /// a summon or an ally as if the cell were empty.
        /// </summary>
        private static bool BlocksSight(FightInstance fight, int cell)
        {
            long planning = fight.CurrentFighter?.Id ?? 0;
            foreach (var fighter in TodosLosCombatientes(fight))
                if (fighter.IsAlive && fighter.Id != planning && fighter.CellId == cell) return true;
            return false;
        }

        /// <summary>
        /// A monster's spells as the tactics weigh them, from the same data the fight casts them
        /// with: the cost, the range and the limits of its grade, the blow and the element of its
        /// damage line, and what its effects do to whom by their target masks.
        /// </summary>
        internal static List<Managers.MonsterTactics.Spell> TacticsOf(Fighter monster)
        {
            var spells = new List<Managers.MonsterTactics.Spell>();
            // A monster's attacks, or a summon's own spells when it is one.
            var suyos = monster.SpellIds.Count > 0 || monster.HechizosDeInvocado == null
                ? monster.SpellIds.Select(s => (Spell: s, Grade: monster.SpellGrades.TryGetValue(s, out int g) ? g : 1)).ToList()
                : monster.HechizosDeInvocado.Select(h => (h.Spell, h.Grade)).ToList();
            foreach (var (spell, grade) in suyos)
            {
                var data = DatabaseManager.GetSpellCombatData(spell, grade);
                if (data == null || data.APCost < 0) continue;
                var limits = LimitesDeGrado(spell, grade);

                bool damages = false, hurtsAllies = false, summons = false, mechanics = false, mechanicsOnEnemies = false;
                int zone = 0, removal = 0, buff = 0;
                double heal = 0;
                var masks = new Dictionary<int, string>();
                foreach (var effect in Managers.SpellEffects.De(spell, grade))
                {
                    string mask = effect.TargetMask ?? "";
                    masks.TryAdd(effect.EffectId, mask);
                    bool enemies = HasMask(mask, "A"), own = HasMask(mask, "a") || HasMask(mask, "g");
                    double average = effect.DiceSide > effect.DiceNum ? (effect.DiceNum + effect.DiceSide) / 2.0 : effect.DiceNum;

                    if (effect.EffectId >= Jondo.Unity.World.Combat.EffectSupport.FirstDamage &&
                        effect.EffectId <= Jondo.Unity.World.Combat.EffectSupport.LastDamage)
                    {
                        if (enemies || (!own && !HasMask(mask, "C"))) damages = true;
                        if (own) hurtsAllies = true;
                        if (effect.Forma != 'P') zone = Math.Max(zone, effect.Tamano);
                    }
                    else if (effect.EffectId == Jondo.Unity.World.Combat.EffectSupport.FireHeal)
                        heal = Math.Max(heal, average);
                    else if (effect.EffectId == Jondo.Unity.World.Combat.EffectSupport.HealPercent)
                        heal = Math.Max(heal, monster.MaxHP * average / 100.0);
                    else if (Managers.EffectEngine.EsInvocacion(effect.EffectId))
                        summons = true;
                    else if (!Managers.EffectEngine.EsMarcadorDeGuion(effect.EffectId)
                             && string.Equals(effect.Triggers ?? "I", Managers.EffectEngine.AlLanzar, StringComparison.OrdinalIgnoreCase))
                    {
                        // Anything else it does when cast: a state, a glyph, a teleport, a sub-cast.
                        mechanics = true;
                        if (enemies) mechanicsOnEnemies = true;
                    }
                }

                foreach (var stat in data.StatEffects)
                {
                    string mask = masks.TryGetValue(stat.EffectId, out var m) ? m : "";
                    bool onEnemies = HasMask(mask, "A");
                    if (stat.Value < 0 && onEnemies &&
                        (stat.Characteristic == ActionPointsCharacteristic || stat.Characteristic == MovementPointsCharacteristic))
                        removal += -stat.Value;
                    else if (stat.Value > 0 && !onEnemies)
                        buff += stat.Value;
                }

                spells.Add(new Managers.MonsterTactics.Spell
                {
                    Id = spell,
                    Grade = grade,
                    Cost = data.APCost,
                    MinRange = data.MinRange,
                    MaxRange = data.MaxRange,
                    NeedsLineOfSight = data.NeedsLineOfSight,
                    InLine = data.CastInLine,
                    PerTurn = limits.PorTurno > 0 ? limits.PorTurno : data.MaxCastPerTurn,
                    PerTarget = limits.PorObjetivo > 0 ? limits.PorObjetivo : data.MaxCastPerTarget,
                    Damage = damages ? (data.BaseDamageMin + data.BaseDamageMax) / 2.0 : 0,
                    Element = (Jondo.Unity.World.Fights.ElementType)Math.Clamp(data.Element, 0, 4),
                    Zone = zone,
                    HurtsAllies = hurtsAllies,
                    Heal = heal,
                    Removal = removal,
                    Buff = buff,
                    Summons = summons,
                    NeedsFreeCell = limits.NeedFreeCell,
                    Utility = !damages && heal == 0 && removal == 0 && buff == 0 && !summons && mechanics
                        ? 25 + monster.Level / 10.0 : 0,
                    UtilityOnEnemies = mechanicsOnEnemies,
                    // A class spell -- a JondoBot's -- is weighed row by row, as the engine reads it.
                    Rows = Managers.PlayerSpells.Contains(spell)
                        ? Managers.SpellEffects.De(spell, grade)
                        : Array.Empty<Managers.SpellEffect>(),
                });
            }
            return spells;
        }

        private static bool HasMask(string mask, string who)
        {
            foreach (var part in mask.Split(','))
                if (part.Trim().TrimStart('*') == who) return true;
            return false;
        }

        /// <summary>
        /// A monster walks a path: the move announced, and what lies on the ground where it ends.
        /// False when the ground killed it -- the turn is over then, and has been ended.
        /// </summary>
        private static async Task<bool> MonsterWalkAsync(NetworkStream stream, FightInstance fight, Fighter monster,
                                                         List<int> planned)
        {
            // The same walk as a player's, tackles and all: the real server holds its monsters
            // as it holds people (the collector and the Dopeul are tackled in their captures).
            var walked = await WalkPathAsync(fight, monster, planned, stream: stream);

            // Y lo que hubiera en el suelo donde ha ido a parar. Esto NO estaba: el
            // monstruo cambiaba de casilla y se anunciaba, y ahí se acababa. Ni los muros
            // de bombas ni las trampas ni los glifos del feca le saltaban nunca a nadie
            // que no fuera un jugador.
            // AND IF THE GROUND KILLED IT, THE TURN STILL HAS TO END. These two were
            // bare returns, and a bare return out of a monster turn hangs the fight the
            // same way the one in ConfirmAsync did -- and worse, because when the monster
            // was the LAST one alive nothing got round to checking that the fight was
            // over either. Measured in the log: "-2 pisa el glifo 3 [...] 170 de dano
            // [...] -2 se queda sin vida" at 00:12:30.960, and not one packet after it.
            await WalkThroughTheWallsAsync(stream, fight, monster, walked);
            if (!monster.IsAlive)
            {
                await EndMonsterTurnAsync(stream, fight);
                return false;
            }

            await ReconciliarLosMurosAsync(stream, fight);
            await DispararLosGlifosAsync(stream, fight, monster, alPisar: true,
                                         skipWalls: true);
            if (!monster.IsAlive)
            {
                await EndMonsterTurnAsync(stream, fight);
                return false;
            }
            return true;
        }

        /// <summary>
        /// A monster casts a spell at a cell: the cast announced, its damage and its effects, and
        /// the fight's limits counted -- per turn, per target, and the cooldown, which monsters
        /// never had, so a spell meant for every third turn came out every turn.
        /// </summary>
        private static async Task MonsterCastAsync(NetworkStream stream, FightInstance fight, Fighter monster,
                                                   Managers.MonsterTactics.Spell chosen, Fighter objetivo, int aim)
        {
            int spell = chosen.Id;
            int monsterGrade = chosen.Grade;
            var data = DatabaseManager.GetSpellCombatData(spell, monsterGrade);
            if (data == null) return;

            // A class spell -- a JondoBot's -- lands on whoever stands on the aimed cell, as a
            // player's does (CastAsync's VictimAt), and on nobody when it is aimed at an empty
            // one: a lance thrown next to the enemy. Handed the caster as its target there, a
            // row falling back on "the target" would have gone to the JondoBot itself.
            // And its critical hit, rolled as a player's is: the spell's own chance, the
            // character's with its buffs, and what modifies the spell. It was never rolled, and a
            // JondoBot with 30 % of critical hits never landed one.
            bool critico = false;
            if (Managers.PlayerSpells.Contains(spell) && chosen.Rows.Count > 0)
            {
                objetivo = VictimAt(fight, monster, aim);
                int probabilidad = LimitesDeGrado(spell, monsterGrade).CriticoPropio
                    + ConBonos(monster, CriticoCaracteristica, monster.CriticalBonus, fight.RoundNumber)
                    + Managers.SpellModifiers.Critical(monster, spell, fight.RoundNumber);
                critico = TirarCritico(probabilidad);
            }

            monster.CurrentAP -= data.APCost;
            if (Managers.MonsterTactics.IsInvisible(monster)) monster.LastSeenCell = monster.CellId;

            // Y su identificador, para que su lanzamiento también diga QUÉ se lanza.
            int spellLevel = data.SpellLevelId;

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(monster.Id,
                                                         Network.FightProtocol.ActionSequence)));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(
                    monster.Id, Network.FightProtocol.Cast,
                    Network.FightProtocol.CastAt(monster.Id, objetivo?.Id ?? 0, aim,
                                                 spell, spellLevel, critical: critico),
                    Network.FightProtocol.CastDetail)));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(monster.Id,
                                                  Network.FightProtocol.SpentActionPoints,
                                                  Network.FightProtocol.Spent(monster.Id, data.APCost),
                                                  Network.FightProtocol.PointsDetail)));

            var tirada = Managers.EffectEngine.EfectosSorteados(spell, monsterGrade, critico);
            if (!Managers.PlayerSpells.Contains(spell))
            {
                // A monster's own spell: its rows in the order they are written.
                await LanzarPorOrdenAsync(stream, fight, monster, spell, monsterGrade, objetivo, aim,
                                          Managers.EffectEngine.AlLanzar, tirada);
            }
            else
            {
                await HurtAsync(stream, fight, monster, spell, monsterGrade, objetivo,
                                aim, critico, tirada: tirada);

                // Y sus efectos, igual que cuando lanza el jugador. Esto faltaba: el turno del
                // monstruo sólo calculaba daño, así que los malus que dejan sus hechizos —el
                // alcance que quita el Picoteo, por ejemplo— no se aplicaban ni se anunciaban, y
                // en el panel del jugador no aparecía nunca nada puesto por un bicho.
                await AplicarEfectosAsync(stream, fight, monster, spell, monsterGrade, objetivo,
                                          Managers.EffectEngine.AlLanzar, aim, critico,
                                          tirada: tirada);
            }

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), monster.Id,
                                                       Network.FightProtocol.ActionSequence)));

            monster.LanzadosEsteTurno.TryGetValue(spell, out int esteTurno);
            monster.LanzadosEsteTurno[spell] = esteTurno + 1;
            if (objetivo != null && objetivo.Id != monster.Id)
            {
                monster.LanzadosPorObjetivo.TryGetValue((spell, objetivo.Id), out int sobreEse);
                monster.LanzadosPorObjetivo[(spell, objetivo.Id)] = sobreEse + 1;
            }
            int intervalo = LimitesDeGrado(spell, monsterGrade).Intervalo;
            if (intervalo > 0) monster.Recarga[spell] = intervalo;
        }

        /// <summary>
        /// The next wave of a Fin du rêve, on the board: each of its monsters built as any fight
        /// builds them, brought to the wave's level, placed on a free defender cell, and announced
        /// the way a summon is -- the one way the client knows to take a fighter in mid-fight --
        /// in the name of the last of the wave that fell. Then the list of fighters again.
        /// False when there is no next wave, and the fight is over.
        /// </summary>
        private static async Task<bool> NextDreamWaveAsync(NetworkStream stream, FightInstance fight)
        {
            var next = DreamHandler.NextWave(fight);
            if (next == null) return false;
            var (members, level, wave) = next.Value;

            var group = MobSpawnManager.ComposeOffMap(members);
            if (group == null || group.Members.Count == 0) return false;

            var fallen = fight.Rojo.LastOrDefault();
            int joined = 0;
            foreach (var member in group.Members)
            {
                int cell = fight.RedPlacementCells.Where(c => !Occupied(fight, c)).DefaultIfEmpty(-1).First();
                if (cell < 0) cell = CasillaLibreCerca(fight, fallen?.CellId ?? fight.RedPlacementCells.FirstOrDefault());
                if (cell < 0) break;

                var monster = BuildMonsterFighter(member, fight.SiguienteIdDeInvocado(), cell);
                Managers.Dreams.ScaleTo(monster, level);
                fight.Join(monster);
                joined++;

                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildSummon(
                        fallen?.Id ?? monster.Id, monster.Id, cell, FacingOf(fight, monster),
                        monster.MonsterId, monster.MonsterId, monster.GradeIndex + 1, FullSheetOf(monster),
                        Network.FightProtocol.Invoca)));
            }
            if (joined == 0) return false;

            await ReenviarLaListaAsync(stream, fight);

            // And their behaviour spells, now that the client knows them, as at a fight's start.
            foreach (var recien in fight.Rojo.Skip(fight.Rojo.Count - joined).ToList())
            {
                recien.CasillaAlEmpezarCombate = recien.CellId;
                await LanzarLaConductaAsync(stream, fight, recien);
            }
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildNotice(CommandTexts.Get("dream.wave", wave, level))));
            Program.LogDebug($"[Sueños] Wave {wave}: {joined} monster(s) at level {level}.");
            return true;
        }

        /// <summary>
        /// The one way out of a monster turn: see whether the fight ended, and if it did not,
        /// hand the turn on. A monster has nobody to press the button for it.
        /// </summary>
        /// <remarks>
        /// Worth a name of its own because it has now been forgotten twice in the same file, and
        /// forgetting it does not throw or log: the fight simply stops, with the clock not
        /// running and no way out but quitting.
        /// </remarks>
        private static async Task EndMonsterTurnAsync(NetworkStream stream, FightInstance fight)
        {
            if (await CheckFightOverAsync(stream, fight)) return;
            await PassTurnAsync(stream);
        }

        /// <summary>
        /// El tope de lanzamientos de un turno de monstruo.
        ///
        /// No es una regla del juego: es un tornillo de seguridad. Desde que se respeta el
        /// MaxCastPerTurn, un hechizo sin límite se lanza mientras haya puntos de acción, y aunque
        /// el coste siempre es mayor que cero —está comprobado antes— más vale que un dato raro no
        /// pueda dejar al servidor dando vueltas dentro del turno de un pío.
        /// </summary>
        private const int TopeDeLanzamientosPorTurno = 20;

        /// <summary>
        /// Cuántas casillas hay de una a otra.
        ///
        /// Esto lo hacía con <see cref="Diamond"/>, que está ESPEJADO respecto a la retícula del
        /// cliente: para las filas pares le da la vuelta al eje y, y para las impares desplaza
        /// además el x. Con esas coordenadas, las "cuatro casillas de al lado" que salían no eran
        /// las de al lado, y por eso se veía a un pío andar por encima de otro: no es que no
        /// mirara si estaba ocupada —sí lo mira—, es que comprobaba la casilla equivocada.
        ///
        /// La retícula buena es la de <see cref="MapGeometry"/>, que es la que usa el combate para
        /// el alcance, la línea de visión y los empujes.
        /// </summary>
        private static int CellDistance(int from, int to) => MapGeometry.Distance(from, to);

        /// <summary>
        /// Si se puede pisar una casilla EN COMBATE. Manda la lista de la arena, no la de paseo:
        /// el anillo exterior de un mapa de combate no se pisa aunque fuera del combate sí.
        /// </summary>
        private static bool PisableEnCombate(FightInstance fight, int cell)
        {
            var pisables = MapManager.GetFightWalkable(fight.MapId);
            if (pisables != null) return pisables.Contains(cell);
            return MapManager.IsCellWalkable(fight.MapId, cell);
        }

        private static bool Occupied(FightInstance fight, int cell)
        {
            foreach (var f in fight.Azul) if (f.IsAlive && f.CellId == cell) return true;
            foreach (var f in fight.Rojo) if (f.IsAlive && f.CellId == cell) return true;
            return false;
        }

        /// <summary>
        /// ¿Queda alguien de pie en los dos bandos? Si no, se acaba.
        ///
        ///   kuf   se acabó
        ///   jyg   cómo ha quedado cada uno
        ///   y de vuelta al mapa de superficie
        /// </summary>
        /// <summary>
        /// El final que está esperando a que el cliente acuse la última secuencia, y el número de
        /// acción que espera. Mientras esté puesto, la pantalla de fin de combate está en el aire.
        /// </summary>

        /// <summary>
        /// El cliente ha acusado una secuencia (jti). Si el combate estaba esperando justo a ésta,
        /// ahora sí se le puede enseñar el final.
        ///
        /// Esto es lo que faltaba para que el último golpe se viera. El combate se acababa dentro
        /// del mismo golpe que lo terminaba, así que el kuf y el jyg salían pegados al del hechizo
        /// y el cliente enseñaba la pantalla de resultados antes de animar nada: ni el hechizo, ni
        /// el daño, ni la muerte. Esperando al acuse, el cliente ya ha tragado la secuencia entera.
        /// </summary>
        private static async Task AcuseAsync(NetworkStream stream, byte[] payload)
        {
            // EL COMBATE DE QUIEN MANDA EL ACUSE, no el ultimo que se quedo esperando en todo el
            // servidor: con dos peleas a la vez, el acuse de una cerraba la otra y le pagaba a
            // quien no era.
            var fight = GetCurrentFight();
            if (fight == null || fight.FinPendiente == 0) return;

            int acusada = Network.FightProtocol.ReadSequenceAck(payload);
            if (acusada != 0 && acusada < fight.FinPendiente) return;

            fight.FinPendiente = 0;
            await EndFightAsync(fight);
        }

        /// <summary>
        /// Abandonment follows the death handshake measured in the three captures that carry a
        /// kme: «Combate/combate contra poutch nivel 75 sin dialogar con poutch maestro-marcadores
        /// permanentes-punetazo-hechizos sacro-rendirse.pcapng», «Combate/aceptar desafio-combate
        /// completo-abandonar al final.pcapng» and «Combate/entrar a combate-cerrar juego para
        /// emular desconexion-reconectar-aceptar reanudar combate.pcapng». The fighter dies inside
        /// a sequence of kind 5, a jxh follows, and the result screen waits for the acknowledgement
        /// rather than cutting through an unfinished cast.
        /// </summary>
        public static async Task AbandonAsync(NetworkStream stream)
        {
            var fight = GetCurrentFight();
            if (fight == null)
            {
                Program.LogDebug("[Fight] Ignored kme because the session has no active fight.");
                return;
            }
            // Leaving during the placement is captured now: «meterse en combate de otra persona
            // haciendo click en la espadita y luego abandonar para salirse», frames 50-55.
            if (fight.State == FightState.Placement)
            {
                await LeavePlacementAsync(stream, fight);
                return;
            }
            if (fight.State != FightState.Ongoing)
            {
                Program.LogDebug($"[Fight] Ignored kme for fight #{fight.FightId} in state {fight.State}.");
                return;
            }

            // LO QUE ESTO NO HACE, y hace falta escribirlo antes de que alguien lo de por hecho:
            // no avisa a NADIE MAS. Todas las tramas de aqui abajo salen por el socket del que se
            // rinde y por ninguno mas.
            //
            // Hoy da igual, y esa es la unica razon por la que se queda asi: en este emulador NO
            // HAY combates de dos jugadores. AddPlayer se llama desde un unico sitio -la creacion
            // del combate, con el personaje de la sesion- y no existe ningun camino que meta a un
            // segundo jugador en un combate ajeno, ni siquiera invitandolo. El Azul tiene siempre
            // exactamente uno.
            //
            // El dia que lo haya, esto es lo que falta y en este orden: el jwe de la muerte, la
            // lista reenviada y el jto/jwi que los envuelven tienen que ir tambien a los demas
            // participantes -por su lista de combate, NO por el mapa: dos combates comparten
            // arena, ver SessionRegistry.Hears- y el combate tiene que seguir para ellos en vez de
            // acabarse.
            //
            // Lo que YA funciona para ese dia, y son dos listas distintas a proposito: la RONDA la
            // rehace Agrupar, que filtra por IsAlive, asi que al que abandona no le vuelve a tocar
            // el turno nunca; y el CARRUSEL lo dibuja el cliente con la lista de equipos, que
            // conserva a sus muertos, asi que se queda en pantalla en gris y no se renumeran los
            // huecos de los demas.
            var quitter = AbandoningFighter(fight, GameState.CharacterId);
            if (quitter == null)
            {
                Program.LogDebug($"[Fight] Ignored kme because character {GameState.CharacterId} " +
                                 $"is not an alive fighter in fight #{fight.FightId}.");
                return;
            }

            if (fight.CurrentFighter == quitter) PararElReloj(fight);

            // EL AUTOR DE LA SECUENCIA ES EL LUCHADOR DEL TURNO, no el que se rinde, y las dos
            // capturas parecian contradecirse hasta mirar quien muere dentro:
            //
            //   poutch nivel 75  jto 08a28280c8e708 1005   autor = el jugador, que es el unico
            //                    jwe muere  a28280c8e708   y ademas es de quien es el turno
            //
            //   aceptar desafio  jto 08a282f0a6c408 1005   autor = el OTRO jugador
            //                    jwe muere  a28280c8e708   pero el que muere es el nuestro
            //
            // O sea que el que se rinde es el del jwe de dentro, y el que envuelve es el del turno.
            // Con quitter.Id en las dos, la segunda captura queda desmentida.
            var author = (fight.CurrentFighter ?? quitter).Id;

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(author,
                                                         Network.FightProtocol.SurrenderSequence)));

            quitter.CurrentHP = 0;
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildDeath(quitter.Id, quitter.Id)));
            await CaenSusInvocadosAsync(stream, fight, quitter);
            await ReenviarLaListaAsync(stream, fight);

            int closure = fight.SiguienteAccion();
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(closure, author,
                                                       Network.FightProtocol.SurrenderSequence)));

            // Y el jxh detras, que las tres capturas mandan ahi y el cliente contesta con jwz. En
            // «aceptar desafio» ese jwz es el UNICO acuse que llega: no hay jti por ninguna parte,
            // asi que esperar solo al jti dejaria la pantalla de resultado sin salir.
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxh,
                Network.FightProtocol.BuildConfirmTurn(author)));

            Program.LogDebug($"[Fight] Character {quitter.Id} abandoned fight #{fight.FightId}; " +
                             $"waiting for jti action {closure} before the result screen.");
            await CheckFightOverAsync(stream, fight, closure, alreadyAsked: true);
        }

        internal static Fighter? AbandoningFighter(FightInstance? fight, long characterId)
        {
            if (fight == null || fight.State != FightState.Ongoing) return null;
            // En los dos equipos: en un desafio el retado esta en el rojo, y buscandolo solo en el
            // azul su «abandonar» no encontraba a nadie y no hacia nada.
            var suyo = fight.Buscar(characterId);
            return suyo != null && suyo.IsAlive ? suyo : null;
        }

        /// <summary>
        /// Whether a side has nobody left, and then the end: asked for with a jxh and shown once
        /// the client has played what ended it.
        /// </summary>
        /// <remarks>
        /// Every fight end of the captures closes the same way, the 60-odd won and lost: the last
        /// sequence, a jxh naming whose turn it was, the client's jwz once it has played it all
        /// -- behind the jti of its own sequence when the last blow was its -- and only then the
        /// kuf and the jyg. In the Dopeul's defeat the jwz comes 6.5 s behind the jxh, the time
        /// the monster's blows take to show. The end went out at once when a monster's turn, a
        /// turn's start or end or a trap ended the fight, and the end screen cut the blows
        /// short: a JondoBot's turn that killed was seen as a fight lost out of nowhere.
        /// </remarks>
        /// <param name="esperarAcuse">
        /// The closing action of the client's own sequence that ended it: its jti may end the wait
        /// before the jwz does.
        /// </param>
        /// <param name="alreadyAsked">The jxh has gone out already (abandoning sends its own).</param>
        internal static async Task<bool> CheckFightOverAsync(NetworkStream stream, FightInstance fight,
                                                             int esperarAcuse = 0, bool alreadyAsked = false)
        {
            bool alliesAlive = fight.SigueVivo(FightInstance.Azules);
            bool enemiesAlive = fight.SigueVivo(FightInstance.Rojos);

            // The Fin du rêve does not end with a wave: the next one comes in.
            if (alliesAlive && !enemiesAlive && await NextDreamWaveAsync(stream, fight)) return false;

            if (alliesAlive && enemiesAlive) return false;

            // Nothing on the board to be played yet -- a fight left during its placement -- ends
            // at once.
            var whose = fight.CurrentFighter;
            if (fight.State != Jondo.Unity.World.Fights.FightState.Ongoing || whose == null)
            {
                await EndFightAsync(fight);
                return true;
            }

            if (!alreadyAsked)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxh,
                    Network.FightProtocol.BuildConfirmTurn(whose.Id)));
            }
            fight.FinPendiente = esperarAcuse != 0 ? esperarAcuse : FightInstance.WaitsForTheJwz;
            Program.LogDebug($"[Combate] Se acabó en el turno de {whose.Id}; el final espera a que el cliente " +
                             (esperarAcuse != 0 ? $"acuse la acción {esperarAcuse} o conteste el jxh."
                                                : "conteste el jxh (jwz)."));
            return true;
        }

        /// <summary>Lo que se manda cuando el combate se acaba de verdad.</summary>
        private static async Task EndFightAsync(FightInstance fight)
        {
            // Quien gana es un hecho del combate; «he ganado yo» depende de en qué lado estabas.
            // Contra monstruos son lo mismo y por eso esto se escribio con un solo booleano, pero
            // en un desafio el perdedor tambien tiene que recibir su pantalla y su vuelta al mapa:
            // sin esto se quedaba plantado en la arena para siempre.
            bool azulGana = fight.SigueVivo(FightInstance.Azules);

            var gente = Publico(fight);
            PlanRewards(fight);
            RecordLadder(fight);
            await ACadaUnoAsync(fight, sesion =>
            {
                return TerminarParaUnoAsync(sesion.Stream, fight,
                                            fight.HaGanado(sesion.State.CharacterId), azulGana);
            });
            ForgetRewards(fight);
            ChallengeWatcher.Forget(fight);

            // And the map: the people drawn again where they came back -- they went off it with
            // a kmu -- the count of fights one less, the group back or its replacement.
            foreach (var sesion in gente) await BackOnTheMapAsync(sesion);
            await FightOffTheMapAsync(fight);
        }

        /// <summary>Una línea de la lista de resultados.</summary>
        /// <remarks>
        /// La ficha —nivel, experiencia y botín— sólo se rellena para quien recibe la lista: es lo
        /// suyo y sale de su <c>GameState</c>. De los demás sólo se dice quién son y si ganaron,
        /// que es lo que el cliente necesita para pintar los dos bandos.
        /// </remarks>
        private static Network.FightProtocol.FightResult FinDe(
            Fighter fighter, bool gano, bool esQuienMira, long xpGained,
            Network.FightProtocol.Spoils spoils, bool gane, Reward? suyo = null)
        {
            // Un monstruo va sin ficha: solo quien es y si gano.
            if (fighter.IsMonster)
            {
                return new Network.FightProtocol.FightResult { Fighter = fighter.Id, Winner = gano };
            }

            // Una PERSONA lleva SIEMPRE su nivel, sea quien sea. En el jyg del koliseo real las
            // cuatro entradas -- las dos que ganan y las dos que pierden -- traen su bloque de
            // experiencia con su nivel dentro: 227, 354, 447...
            //
            // Aqui solo se rellenaba el de quien miraba, y el cliente entiende que una entrada sin
            // nivel es un monstruo. Como del rival no tenia monstruo que dibujar, en la pantalla de
            // fin de combate salia una interrogacion donde tenia que ir su retrato.
            if (!esQuienMira)
            {
                // La experiencia exacta del rival no la tenemos aqui -- la ficha de la base no la
                // guarda -- asi que va el suelo de su nivel, que es la unica cifra que no miente:
                // el minimo que hay que tener para estar en ese nivel. Su barra sale vacia, y eso
                // es cosmetico; lo que hacia falta era el NIVEL, que es lo que distingue a una
                // persona de un monstruo.
                // What he won, though, is known when the fight was shared out: each end screen of the
                // follow capture lists both players' experience, kamas and items.
                int nivel = Math.Max(1, fighter.Level);
                return new Network.FightProtocol.FightResult
                {
                    Fighter = fighter.Id,
                    Winner = gano,
                    Level = nivel,
                    Xp = ExperienceTable.LevelFloor(nivel) + (suyo?.Xp ?? 0),
                    XpGained = suyo?.Xp ?? 0,
                    Spoils = gano && suyo != null ? SpoilsOf(suyo) : null,
                };
            }

            return new Network.FightProtocol.FightResult
            {
                Fighter = fighter.Id,
                Winner = gano,
                Level = GameState.CharacterLevel,
                Xp = GameState.Experience,
                XpGained = xpGained,
                Spoils = gane ? spoils : null,
            };
        }

        /// <summary>El final del combate tal y como lo vive UNA de las personas que estaba dentro.</summary>
        /// <param name="gane">Si ganó quien recibe esto.</param>
        /// <param name="azulGana">Si ganó el equipo azul, que es lo que va en la lista de resultados.</param>
        private static async Task TerminarParaUnoAsync(NetworkStream stream, FightInstance fight,
                                                       bool gane, bool azulGana)
        {
            bool alliesAlive = gane;

            // Lo que se gana. La experiencia es la que declara cada monstruo en su ficha (gradeXp),
            // que es la misma que enseña el cliente al pasar el ratón por el grupo; no hay fórmula
            // inventada. Los kamas y los objetos, lo que suelte cada uno.
            bool won = alliesAlive;

            // Los retos, antes de todo lo del final: el servidor real manda sus kwl unas pocas
            // tramas por delante del jyg, y en una derrota los manda todos seguidos ahi mismo.
            // Lo que devuelve es el extra de los cumplidos, sumado, en tanto por ciento.
            int extraDeRetos = await ChallengeWatcher.FightEndedAsync(stream, fight, won);

            // Y las misiones que pedian vencer a algo, por lo mismo: aqui es donde se sabe
            // que ha caido de verdad, y de eso no se fia uno del cliente.
            await QuestWatcher.FightEndedAsync(stream, fight, won);

            // And what the achievements count, set aside until the character is back on the map.
            AchievementWatcher.FightEnded(fight, won);

            // Y aqui se aplica. En el cable NO viaja desglosado: el porcentaje solo existe dentro
            // del ldd de la preparacion, y la cifra del final llega ya con el extra sumado. Se
            // revisaron los 68 jyg de las capturas y no hay ningun hueco donde quepa un desglose,
            // asi que es el servidor quien tiene que aplicarlo antes de mandar el numero.
            // Sin los invocados. La suma iba sobre TODO el bando contrario, y una invocación
            // entra en él con su nivel puesto: un monstruo que invoque estaba pagando kamas por
            // criaturas que él mismo se fabricaba durante el combate. La experiencia no lo
            // notaba porque un invocado no lleva XpReward, pero los kamas sí.
            // En un desafio no se gana nada: ni experiencia, ni kamas, ni objetos. Sin esto, el
            // ganador cobraba kamas por el nivel del rival como si fuera un monstruo.
            var quePagan = fight.Reglas.ReparteBotin
                ? fight.Rojo.Where(m => !m.EsInvocado).ToList()
                : new List<Fighter>();
            long xpGained = won ? ConElExtra(quePagan.Sum(m => (long)m.XpReward), extraDeRetos) : 0;
            long kamas = won ? ConElExtra(quePagan.Sum(m => 10L + (m.Level * 5L)), extraDeRetos) : 0;
            var caidos = new List<PlayerItem>();
            Dictionary<int, int> loot;

            // His share, when the fight was planned for all its winners (FightRewards): the same
            // numbers his partners see in their end screen. Only winners are planned, and the one
            // loser who is: the dreamer who falls at the Fin du rêve after its minimum of waves.
            var suyo = RewardOf(fight, GameState.CharacterId);
            if (suyo != null)
            {
                xpGained = suyo.Xp;
                kamas = suyo.Kamas;
                loot = suyo.Loot;
                EntregarBotin(loot, out caidos);
            }
            else
            {
                loot = won ? RollFightLoot(fight, extraDeRetos, out caidos) : new Dictionary<int, int>();
            }

            // El koliseo paga LO SUYO. No entra por lo de arriba porque enfrente no hay monstruos
            // de los que sacar experiencia, kamas ni tabla de botín: lo paga el koliseo por ganar,
            // y son kolichas y vitorichas. El que pierde no cobra nada, ni siquiera experiencia
            // — en el jyg de la captura su bloque va SIN el campo de lo ganado, no con un cero —.
            if (won && fight.Reglas.PagaElKoliseo)
            {
                xpGained = Managers.KoliseoRewards.Experiencia(GameState.CharacterLevel);
                kamas = Managers.KoliseoRewards.KamasPorVictoria;
                loot = Managers.KoliseoRewards.Botin();
                EntregarBotin(loot, out caidos);

                Program.LogDebug($"[Koliseo] Victoria: {kamas} kamas, " +
                                 $"{Managers.KoliseoRewards.KolichasPorVictoria} kolicha(s), " +
                                 $"{Managers.KoliseoRewards.VitorichasPorVictoria} vitoricha(s) y " +
                                 $"{xpGained} de experiencia.");
            }

            if (extraDeRetos > 0)
            {
                Program.LogDebug($"[Retos] Los retos cumplidos suman un {extraDeRetos} % de mas: " +
                                 $"{xpGained} de experiencia y {kamas} kamas.");
            }

            if (xpGained > 0)
            {
                GameState.Experience += xpGained;
                int newLevel = ExperienceTable.LevelForXp(GameState.Experience);
                if (newLevel > GameState.CharacterLevel)
                {
                    // Cinco puntos de característica por nivel, como en TotalCapitalForLevel, pero
                    // sólo hasta el 200. De ahí para arriba la tabla sigue contando —el 201 es el
                    // Omega 1, y el 354 de la captura es un 200 con Omega 154— y lo que da cada
                    // Omega no está medido, así que se sube el nivel y no se reparte nada. Antes de
                    // inventarlo, nada.
                    int upToTwoHundred = Math.Max(0, Math.Min(newLevel, MaxLevelWithPoints)
                                                     - Math.Min(GameState.CharacterLevel, MaxLevelWithPoints));
                    if (upToTwoHundred > 0) GameState.CharacterRemainingPoints += upToTwoHundred * 5;
                    Program.LogDebug($"[Combate] ¡Sube de nivel! {GameState.CharacterLevel} -> {newLevel} " +
                                     $"(+{upToTwoHundred * 5} puntos).");
                    GameState.CharacterLevel = newLevel;

                    // Y la ventana, que es lo que el jugador espera ver al subir.
                    await WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kua, ConnectionProtocol.BuildLevelUp(newLevel)));
                }
            }
            if (kamas > 0) GameState.Kamas += kamas;
            if (xpGained > 0 || kamas > 0 || loot.Count > 0) DatabaseManager.SaveCurrentCharacter();

            var spoils = new Network.FightProtocol.Spoils { Kamas = kamas };
            foreach (var kv in loot) spoils.Items.Add((kv.Value, kv.Key));

            // Quien gano va en absoluto -- azul o rojo -- y no «yo o el otro»: la lista es la misma
            // para los dos clientes y cada uno se busca a si mismo dentro. Iba con «won», que es
            // del que mira, asi que en un desafio el perdedor recibia la lista con los ganadores
            // cambiados de sitio.
            // People and monsters, not summons: the real jyg of the Tymobot fight lists the
            // Rogue and the four monsters, and none of the eight bombs and bots he put out.
            var results = new List<Network.FightProtocol.FightResult>();
            long yo = GameState.CharacterId;
            foreach (var f in fight.Azul)
            {
                if (f.EsInvocado || f.EsIlusion) continue;
                results.Add(FinDe(f, azulGana, f.Id == yo, xpGained, spoils, gane, RewardOf(fight, f.Id)));
            }
            foreach (var f in fight.Rojo)
            {
                if (f.EsInvocado || f.EsIlusion) continue;
                results.Add(FinDe(f, !azulGana, f.Id == yo, xpGained, spoils, gane, RewardOf(fight, f.Id)));
            }

            int duration = (int)Math.Max(0, (DateTime.UtcNow - fight.StartedAt).TotalMilliseconds);
            ActivityJournal.Current.Write("fight.ended", SessionContext.Current.AccountId,
                GameState.CharacterId,
                new
                {
                    fightId = fight.FightId,
                    won,
                    durationMs = duration,
                    xp = xpGained,
                    kamas,
                    itemKinds = loot.Count,
                    itemQuantity = loot.Sum(item => item.Value),
                });

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kuf,
                Network.FightProtocol.BuildFightOver()));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyg,
                Network.FightProtocol.BuildFightResults(results, duration)));

            // His own numbers, and nobody else's: "kuf jyg jxo" in every capture.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxo,
                Network.FightProtocol.BuildFightStatistics(yo, fight.StatisticsOf(yo),
                                                           EnemigosCaidos(fight, yo))));

            // Y AHORA SE LE DICE AL CLIENTE QUE LOS TIENE.
            //
            // Esto faltaba entero, y es la razón de que el botín se guardara bien y no se viera
            // por ningún lado: la pantalla de fin de combate lo pintaba —el jyg— pero nadie le
            // decía al cliente que esos objetos habían entrado en el inventario, así que no
            // aparecían hasta el siguiente login. Con la Jondo Coin se vio clarísimo: 73 unidades
            // en la base y ninguna en la mochila.
            //
            // El servidor real manda un iua por objeto. Medido en la mazmorra de los jalates:
            // cuatro iua de 17 bytes con f3{f1:63, f5{gid, cantidad, uid}}. El 63 es la mochila.
            foreach (var pieza in caidos)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Iua,
                    ConnectionProtocol.BuildItemArrived(3, new Managers.HavenBagStore.StoredItem
                    {
                        Uid = pieza.Uid,
                        Gid = pieza.ItemId,
                        Quantity = pieza.Quantity,
                        Effects = pieza.RawEffects ?? "[]",
                    })));
            }

            Program.LogDebug($"[Combate] Reparto: {xpGained} de experiencia (total {GameState.Experience}, " +
                             $"nivel {GameState.CharacterLevel}), {kamas} kamas y {loot.Count} clase(s) de objeto.");

            // What losing costs, against monsters (FightDefeat.cs): the energy, half the life, and
            // the way back to the save point. Applied before the kub, which is what shows the
            // first two. Any other end gives the life back whole, as it always has.
            var defeat = !gane && DefeatCostsIn(fight) ? ApplyDefeat() : null;
            if (defeat == null) Managers.RestingLife.Clear(GameState.CharacterId);

            // La ficha del personaje otra vez, que si no el cliente se queda con la del COMBATE
            // puesta al volver al mapa: se salía con los puntos de acción que quedaran al terminar
            // —cuatro— y con la vida del combatiente.
            //
            // Y va por el opcode kub, que es la ficha de esta versión. Antes se mandaba por "kri",
            // y ése no existe: cero apariciones en las 295 capturas de todas las carpetas, contra
            // 672 de kub. O sea que el paquete salía de aquí y el cliente no lo recogía nunca, y
            // por eso el arreglo anterior no cambió nada.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kub,
                ConnectionProtocol.BuildCharacteristics()));

            // "Has perdido 2000 puntos de energía.", right behind that kub in the Pandala and
            // Dopeul defeats. With nothing lost -- the gauge already at its last point -- there is
            // no capture, and nothing is said.
            if (defeat != null && defeat.EnergyLost > 0)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(Managers.InfoMessages.EnergyLost,
                                                          defeat.EnergyLost.ToString())));
            }

            // El grupo que se acaba de matar desaparece del mapa, y en su sitio sale otro.
            //
            // Esto estaba escrito en SendFightEnd, que no lo llama nadie: sus tres llamadores
            // cuelgan de métodos que a su vez no llama nadie. El final que corre de verdad es éste,
            // y no borraba el grupo. En el registro se ve limpio: doce combates ganados y CERO
            // líneas de «removed from map», y el mismo grupo empezando tres combates seguidos
            // dentro de la misma ejecución. O sea que al volver al mapa el grupo muerto seguía
            // dibujado, con su mismo id, y se le podía volver a atacar: experiencia, kamas y botín
            // infinitos sobre el mismo grupo.
            // ONCE per fight, by the first of its people: each of them runs this end in his own
            // context, and with a party on the side every one of them removed the group and put a
            // new one in its place.
            if (won && fight.Reglas.BorraElGrupoAlGanar && SettlesTheGroup(fight))
            {
                long muerto = GameState.CurrentFightMobId != 0
                    ? GameState.CurrentFightMobId
                    : fight.DefenderLeaderId;
                MobSpawnManager.RemoveMobGroup(fight.RoleplayMapId, muerto);
                Program.LogDebug($"[Combate] El grupo #{muerto} desaparece del mapa {fight.RoleplayMapId}.");

                // En una sala de sueño NO se repone: la sala se limpia y se queda limpia, que es
                // lo que hace que avanzar signifique algo. Reponerla dejaría al jugador peleando
                // la misma sala para siempre.
                if (!DreamHandler.SalaLimpiada(muerto, fight.RoleplayMapId))
                {
                    // A dungeon room comes back as itself -- its eight, its boss -- and not as a
                    // random group of the subarea, which could have the boss in it.
                    var repuesto = DungeonManager.IsRoom(fight.RoleplayMapId)
                        ? MobSpawnManager.RecomposeDungeonRoom(fight.RoleplayMapId)
                        : MobSpawnManager.RespawnOneGroup(fight.RoleplayMapId);
                    if (repuesto != null)
                    {
                        Replaced(fight, repuesto);
                        Program.LogDebug($"[Combate] Repuesto el grupo #{repuesto.MobId} en la casilla " +
                                         $"{repuesto.CellId} con {repuesto.Members.Count} miembro(s).");
                    }
                }
            }
            GameState.CurrentFightMobId = 0;

            // Y de vuelta al mapa de donde se salió, que el de arena es de instancia.
            long back = Network.SessionContext.State.RoleplayMapId != 0
                ? Network.SessionContext.State.RoleplayMapId
                : fight.RoleplayMapId;
            LeaveFight();

            // A loser does not go back where he fought: he goes to his save point, beside its
            // zaap. Set after LeaveFight, which puts him back on the map he left.
            if (defeat != null && MapManager.GetMapInfo(defeat.SavePointMap) != null)
            {
                back = defeat.SavePointMap;
                Network.SessionContext.State.MapId = defeat.SavePointMap;
                Network.SessionContext.State.CellId = defeat.SavePointCell;
                DatabaseManager.SaveCurrentCharacter();
            }

            // ¿Se peleaba dentro de una mazmorra? Entonces ganar mueve: a la sala siguiente, o
            // fuera si era la última. Se decide AQUÍ y no después de que esto termine, porque el
            // jru de abajo ya nombra un mapa y el cliente contesta a ése: un teletransporte
            // posterior se lo comería el kkr que llega de vuelta.
            //
            // Hay que tocar las dos cosas, `back` y el estado, porque `back` se leyó antes de que
            // LeaveFight() borrase el mapa de rol. Cambiar sólo una deja al cliente cargando un
            // mapa y al servidor creyendo que está en otro.
            if (alliesAlive && fight.Reglas.AvanzaDeSala)
            {
                long enLaMazmorra = DungeonHandler.AfterAWinIn(back);
                if (enLaMazmorra != 0 && enLaMazmorra != back &&
                    MapManager.GetMapInfo(enLaMazmorra) != null)
                {
                    back = enLaMazmorra;
                    Network.SessionContext.State.MapId = enLaMazmorra;
                    Network.SessionContext.State.CellId =
                        MapManager.GetNearestWalkableCell(enLaMazmorra, TeleportHandler.MapCentre);
                    DatabaseManager.SaveCurrentCharacter();
                }
            }

            // A fight in a dream's room decides the dream: won at its end, lost without an arena.
            // Decided here, before the jru below names the map, for the same reason as a dungeon.
            var (fueraDelSueno, casillaFuera, avisoDelSueno, suenoAcabado) = DreamHandler.AfterTheFight(fight, alliesAlive);
            if (fueraDelSueno != 0 && fueraDelSueno != back && MapManager.GetMapInfo(fueraDelSueno) != null)
            {
                back = fueraDelSueno;
                Network.SessionContext.State.MapId = fueraDelSueno;
                Network.SessionContext.State.CellId = casillaFuera > 0
                    ? casillaFuera
                    : MapManager.GetNearestWalkableCell(fueraDelSueno, TeleportHandler.MapCentre);
                DatabaseManager.SaveCurrentCharacter();
            }

            // Si esto era una sala de sueño, el estado ha cambiado —la sala está hecha y los
            // puntos han subido— y hay que decírselo antes de recargar el mapa, o la ventana
            // seguirá enseñando lo de antes hasta que se cambie de sala. Only then: any other
            // fight, with a dream left to be continued, put the dream's interface on the world.
            if (Managers.Dreams.IsDreamMap(fight.RoleplayMapId) && !suenoAcabado)
                await DreamHandler.RefrescarEstadoAsync(stream);
            if (suenoAcabado) { await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ixg)); DreamHandler.MarkLeft(); }
            if (avisoDelSueno != null)
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(avisoDelSueno)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kml));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmp));

            // Regeneration begins again, right behind the roleplay context: "kml kmp ktz" in all
            // 143 captures of it. The world entry replays the captured one; here it is built.
            await WriteFrameAsync(stream, ConnectionProtocol.BuildRegenerationStarted(
                ConnectionProtocol.RegenerationRate));
            Network.SessionContext.State.RegenerationStartedUtc = DateTime.UtcNow;

            await WriteFrameAsync(stream, ConnectionProtocol.BuildLoadMap(back));
            await WriteFrameAsync(stream, ConnectionProtocol.BuildMapClock());

            Program.LogDebug($"[Combate] Se acabó el combate #{fight.FightId}: " +
                             $"{(alliesAlive ? "victoria" : "derrota")}. De vuelta al mapa {back}.");
        }

        /// <summary>
        /// Los puntos de vida que da el nivel, sin la vitalidad: cincuenta de salida y cinco por
        /// nivel. Es la misma cuenta que hace StatsHandler.GetPlayerMaxHp antes de sumarle nada.
        /// </summary>
        private static int LifeFromLevel(int level) => 50 + (Math.Max(1, level) * 5);

        /// <summary>Lo que cuesta lanzar algo cuando no se sabe: el coste corriente de un hechizo.</summary>
        private const int DefaultCastCost = 3;

        /// <summary>
        /// El último nivel que reparte puntos de característica. La tabla del cliente llega al
        /// 1889, pero del 201 en adelante eso ya es el Omega: el 354 de la captura es un nivel 200
        /// con Omega 154.
        /// </summary>
        private const int MaxLevelWithPoints = 200;

        /// <summary>
        /// El grado que el personaje tiene abierto de un hechizo: lo que cuesta y su identificador.
        ///
        /// Sale de SpellLevels, que es de donde lo saca el propio cliente para pintar el número en
        /// el icono; si el hechizo no está, se cobra el corriente y no hay identificador.
        ///
        /// Hacen falta LOS DOS. El coste, para descontar los puntos de acción; y el
        /// SpellLevels.Id, porque el jwe del lanzamiento lo lleva junto al del hechizo y sin él el
        /// cliente no sabe qué está pintando. Van juntos en la misma consulta para que no puedan
        /// salir de filas distintas.
        /// </summary>
        private static (int Cost, int LevelId, int Grade) GradeOf(int spellId, int level)
        {
            var todo = LimitesDe(spellId, level);
            return (todo.Cost, todo.LevelId, todo.Grade);
        }

        /// <summary>Lo que un hechizo cuesta y lo que le limita, todo de la misma fila.</summary>
        public readonly record struct LimitesDelHechizo(
            int Cost, int LevelId, int Grade,
            int PorTurno, int PorObjetivo, int Intervalo, int EsperaInicial,
            int CriticoPropio, int AlcanceMinimo = 0, int AlcanceMaximo = 0,
            bool NeedFreeCell = false, bool NeedTakenCell = false);

        /// <summary>
        /// Los límites de lanzamiento, que salen de las mismas columnas de SpellLevels de las que
        /// sale el coste:
        ///
        ///   MaxCastPerTurn     cuántas veces por turno       MaxCastPerTarget  y por objetivo
        ///   MinCastInterval    rondas hasta poder repetirlo  InitialCooldown   la espera de salida
        ///
        /// El GRADO importa y por eso entra en la clave: Paso de Cacería pasa de tres rondas de
        /// intervalo en su grado uno a dos en los grados dos y tres, y con la caché guardada sólo
        /// por hechizo el primer personaje que lanzara fijaba el número para todos los demás.
        /// </summary>
        private static LimitesDelHechizo LimitesDe(int spellId, int level)
        {
            if (spellId == 0) return new LimitesDelHechizo(DefaultCastCost, 0, 1, 0, 0, 0, 0, 0);

            int nivel = Math.Max(1, level);
            if (_grades.TryGetValue((spellId, nivel), out var conocido)) return conocido;

            var salida = new LimitesDelHechizo(0, 0, 1, 0, 0, 0, 0, 0);
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT APCost, Id, Grade, MaxCastPerTurn, MaxCastPerTarget, " +
                    "MinCastInterval, InitialCooldown, CriticalHitProbability, " +
                    "MinRange, MaxRange, NeedFreeCell, NeedTakenCell FROM SpellLevels " +
                    "WHERE SpellId = $id AND MinPlayerLevel <= $lvl ORDER BY Grade DESC LIMIT 1;";
                command.Parameters.AddWithValue("$id", spellId);
                command.Parameters.AddWithValue("$lvl", nivel);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    salida = new LimitesDelHechizo(
                        (int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2),
                        reader.IsDBNull(3) ? 0 : (int)reader.GetInt64(3),
                        reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4),
                        reader.IsDBNull(5) ? 0 : (int)reader.GetInt64(5),
                        reader.IsDBNull(6) ? 0 : (int)reader.GetInt64(6),
                        reader.IsDBNull(7) ? 0 : (int)reader.GetInt64(7),
                        reader.IsDBNull(8) ? 0 : (int)reader.GetInt64(8),
                        reader.IsDBNull(9) ? 0 : (int)reader.GetInt64(9),
                        !reader.IsDBNull(10) && reader.GetInt64(10) != 0,
                        !reader.IsDBNull(11) && reader.GetInt64(11) != 0);
                }
            }
            catch (Exception ex)
            {
                // Aquí había un catch mudo. Las columnas de intervalo no están en el CREATE TABLE
                // del emulador, así que una base regenerada las perdería y todos los hechizos
                // pasarían a costar tres puntos de acción sin que se notara.
                Program.LogDebug($"[Combate] No se pudieron leer los límites del hechizo {spellId} " +
                                 $"para el nivel {nivel}: {ex.Message}");
            }

            _grades[(spellId, nivel)] = salida;
            return salida;
        }

        /// <summary>Exact hidden-spell grade used by chained cast animations.</summary>
        internal static LimitesDelHechizo LimitesDeGrado(int spellId, int grade)
        {
            int exactGrade = Math.Max(1, grade);
            var cacheKey = (spellId, -exactGrade);
            if (_grades.TryGetValue(cacheKey, out var known)) return known;

            var result = new LimitesDelHechizo(0, 0, exactGrade, 0, 0, 0, 0, 0);
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT APCost, Id, Grade, MaxCastPerTurn, MaxCastPerTarget, " +
                    "MinCastInterval, InitialCooldown, CriticalHitProbability, " +
                    "MinRange, MaxRange, NeedFreeCell, NeedTakenCell FROM SpellLevels " +
                    "WHERE SpellId = $id AND Grade = $grade LIMIT 1;";
                command.Parameters.AddWithValue("$id", spellId);
                command.Parameters.AddWithValue("$grade", exactGrade);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    result = new LimitesDelHechizo(
                        (int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2),
                        reader.IsDBNull(3) ? 0 : (int)reader.GetInt64(3),
                        reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4),
                        reader.IsDBNull(5) ? 0 : (int)reader.GetInt64(5),
                        reader.IsDBNull(6) ? 0 : (int)reader.GetInt64(6),
                        reader.IsDBNull(7) ? 0 : (int)reader.GetInt64(7),
                        reader.IsDBNull(8) ? 0 : (int)reader.GetInt64(8),
                        reader.IsDBNull(9) ? 0 : (int)reader.GetInt64(9),
                        !reader.IsDBNull(10) && reader.GetInt64(10) != 0,
                        !reader.IsDBNull(11) && reader.GetInt64(11) != 0);
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Fight] Could not read exact grade {exactGrade} of spell " +
                                 $"{spellId}: {ex.Message}");
            }

            _grades[cacheKey] = result;
            return result;
        }

        /// <summary>
        /// Los límites de cada hechizo por grado, ya leídos.
        ///
        /// Era un Dictionary normal, y lo escriben varias sesiones a la vez: dos combates
        /// lanzando hechizos distintos en el mismo instante pueden pillar el diccionario a medio
        /// redimensionar, y eso no da una excepción —da un bucle infinito dentro del propio
        /// Dictionary, con el hilo comiéndose un núcleo entero para siempre—. Es el fallo de
        /// concurrencia más desagradable que hay en .NET porque no deja rastro: no hay excepción,
        /// no hay registro, sólo un servidor que va cada vez peor.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Hechizo, int Nivel), LimitesDelHechizo> _grades
            = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), LimitesDelHechizo>();

        /// <summary>
        /// El jugador pasa turno (jxy, vacío), o se le acaba el tiempo.
        ///
        ///   jyt   se acabó el turno
        ///   jto / jxc / jwi   el bloque de cierre
        ///   jxh   y a por el siguiente
        /// </summary>
        /// <summary>
        /// Whether a fighter keeps what he leaves of his turns: a character does. In the captures
        /// only a character's jyt carries an f1; monsters, summons and a JondoBot play theirs at once.
        /// </summary>
        internal static bool KeepsTurnTime(Fighter fighter)
            => fighter != null && !fighter.IsMonster && !fighter.EsInvocado && !fighter.IsBot;

        public static async Task PassTurnAsync(NetworkStream stream)
        {
            var fight = GetCurrentFight();
            if (fight == null || fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;

            // Parar el reloj DE ESTE combate: si el turno se pasa a mano no debe saltar después.
            PararElReloj(fight);

            var ending = fight.CurrentFighter;
            if (ending == null) return;

            // El final de turno es del COMBATE, no de quien lo pulsa: las cuatro tramas que vienen
            // hablan del combatiente que acaba y las tienen que ver los dos.
            //
            // FIRST the jyt, THEN what the attitudes do at turn end. It was the other way round,
            // and the Tymobot's own death -- its passive's 141 on the TE trigger -- went out
            // before the turn had ended and outside any sequence, and the client left it standing
            // on the board. Measured: "jyt -12" first, then "jto{-12,3} jwe 300 … jwe 103 … jwi".
            // What he keeps of it for his next turn: half of what is left, in the jyt's f1.
            var announced = fight.LastAnnouncedTurn;
            int saved = KeepsTurnTime(ending) && announced.FighterId == ending.Id && announced.Round == fight.RoundNumber
                ? Network.FightProtocol.SavedAfter(announced.RemainingDeciseconds(DateTime.UtcNow), announced.Deciseconds)
                : 0;
            ending.SavedTurnTime = saved;
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jyt,
                Network.FightProtocol.BuildTurnEnd(ending.Id, saved)));

            // Lo que las actitudes tengan que hacer al acabar el turno. Aquí es donde el Amarillo
            // Ocre se quita el estado de "me han pegado", para que el turno siguiente vuelva a
            // mirarlo limpio.
            await ActitudesAsync(stream, fight, ending, Managers.EffectEngine.AlAcabarElTurno);
            await EngancheAsync(stream, fight, ending, Managers.EffectEngine.AlAcabarElTurno);

            // And the turn-end glyphs under him (402).
            if (ending.IsAlive)
            {
                foreach (var glifo in fight.LosQueAcaban(ending.CellId))
                {
                    if (!GlyphCatches(fight, glifo, ending, false)) continue;
                    await FireOneGlyphAsync(stream, fight, glifo, ending, alPisar: false);
                    if (!ending.IsAlive) break;
                }
            }

            // A turn-end effect can be the one that empties a side -- a poison, a Tymobot that
            // was the last of its team -- and then there is no next turn to hand out.
            if (await CheckFightOverAsync(stream, fight)) return;

            // Las esperas bajan una ronda AL ACABAR el turno de su dueño, y el jxc de cierre ya
            // las lleva bajadas: medido en la captura de Agudeza Absoluta, lanzada en la ronda 8
            // con intervalo 4 —el jxc de ese turno dice 3, el de la 9 dice 2, el de la 10 uno, el
            // de la 11 cero y se relanza en la 12—. Ocho más cuatro, doce.
            foreach (var hechizo in new List<int>(ending.Recarga.Keys))
            {
                if (ending.Recarga[hechizo] > 0) ending.Recarga[hechizo]--;
            }
            // Los retos de posicion se juzgan AQUI, con el que acaba todavia donde acabo y con sus
            // PM sin reponer. Va antes de limpiar los contadores del turno, que el Versatil los usa.
            if (fight.Reglas.HayRetos) await ChallengeWatcher.TurnEndedAsync(stream, fight, ending);

            ending.LanzadosEsteTurno.Clear();
            ending.LanzadosPorObjetivo.Clear();

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(ending.Id,
                                                         Network.FightProtocol.TurnEndSequence)));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxc,
                Network.FightProtocol.BuildCooldowns(ending.Id, RecargasDe(ending))));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), ending.Id,
                                                       Network.FightProtocol.TurnEndSequence)));

            // Si con éste se cierra la vuelta, empieza ronda nueva y hay que decirlo: el jxz es lo
            // que hace subir el numerito del carrusel, y sin él se queda clavado en 1 para siempre.
            bool wasLast = fight.CurrentTurnIndex >= fight.TurnOrder.Count - 1;
            fight.NextTurn();

            if (wasLast)
            {
                // La ronda la sube fight.NextTurn() al dar la vuelta al orden; aqui solo se
                // anuncia. Antes se subia ademas un contador estatico aparte, y eran dos numeros
                // distintos siguiendose el uno al otro.
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxz,
                    Network.FightProtocol.BuildRound(fight.RoundNumber)));
                Program.LogDebug($"[Combate] Empieza la ronda {fight.RoundNumber}.");

                // El Imprevisible senala otro enemigo en cada turno global.
                await ChallengeWatcher.RoundStartedAsync(stream, fight);
            }

            await AskToConfirmAsync(stream, fight, deQuien: ending.Id);
        }

        public static async Task RefreshPlayerSpellBarAsync(NetworkStream stream)
        {
            if (!GameState.IsInFight || !Managers.SpellTable.IsLoaded || GetCurrentFight() == null)
                return;

            var layout = Managers.FightSpellLayout.Current(GameState.Breed,
                                                           GameState.CharacterLevel,
                                                           SessionContext.Current.AccountId);
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyy,
                Network.FightProtocol.BuildSpellBar(GameState.CharacterId,
                                                    layout.Spells, layout.Bar)));
            Program.LogDebug($"[FightHandler] Refreshed the in-fight spell bar with " +
                             $"{layout.Spells.Count} spells at level {GameState.CharacterLevel}.");
        }

        /// <summary>
        /// Builds the joo (movement broadcast) exactly as the official server emits it:
        ///   joo { f1 = fighterId, f2 = &lt;PACKED path&gt;, f5 = final orientation }
        /// Field 2 is a packed repeated int32: the cell varints are concatenated WITHOUT tags.
        /// Writing them as tagged fields (08 xx 08 xx ...) corrupts the path, because the client
        /// reads the 0x08 as just another cell number.
        /// Verified against the capture: f2 = ac03 ab03 b803 c603 ... for [428,427,440,454,...].
        /// </summary>
        public static byte[] BuildJooMovementPacket(long fighterId, List<int> pathCells, int orientation = 3)
        {
            using var packed = new MemoryStream();
            foreach (var c in pathCells)
            {
                ProtoMessage.WriteVarInt(packed, (ulong)c);
            }

            var jooMsg = new ProtoMessage();
            jooMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighterId });
            jooMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = packed.ToArray() });
            jooMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = orientation });

            return BuildGameNodePacket("type.ankama.com/joo", jooMsg.ToByteArray());
        }

        /// <summary>
        /// Variation of a combat characteristic (AP = 1, MP = 23, health = 19).
        ///
        /// The three fields of the value block are OPTIONAL, and the client tells "present with a
        /// value of zero" apart from "absent". The official capture makes it plain: during the turn
        /// it sends {f2 = -accumulated loss, f4 = maximum, f8 = loss}, but when the points are
        /// restored it sends ONLY {f4 = maximum}. Writing "f2 = 0" is not the same as leaving it
        /// out: the client reads it as "apply a variation of zero" and leaves the counter where it
        /// was. That is why AP/MP stayed at zero when your turn came round again.
        /// </summary>
        public static byte[] BuildJvmPacket(long fighterId, int statId, int accumulatedDelta, int maxStatValue)
        {
            var f8Sub = new ProtoMessage();
            if (accumulatedDelta != 0)
            {
                f8Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = accumulatedDelta });
            }
            f8Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = maxStatValue });
            if (accumulatedDelta != 0)
            {
                f8Sub.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = Math.Abs(accumulatedDelta) });
            }

            var f4Inner = new ProtoMessage();
            f4Inner.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = f8Sub.ToByteArray() });
            f4Inner.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = statId });

            var f3Sub = new ProtoMessage();
            f3Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 2 });
            f3Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = f4Inner.ToByteArray() });

            var jvmMsg = new ProtoMessage();
            jvmMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighterId });
            jvmMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = f3Sub.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jvm", jvmMsg.ToByteArray());
        }

        // There used to be a "life variation" jvm here on characteristic 19. It did nothing: the
        // health bar is moved by the damage jtx itself, and 19 is not health. It is removed rather
        // than leaving a made-up message in circulation.

        // The per-turn cast counters used to be two static dictionaries here, and the doc comment
        // that stood in this spot described them as if they were still fields. They now live on the
        // FightInstance -- keyed by caster as well as spell, and emptied in NextTurn -- because as
        // process-wide state keyed by spell alone, two players in two different fights spent each
        // other's casts, and nothing ever cleared them, so after three casts of a spell (summed
        // over every player and every fight since boot) it was refused for everybody until a
        // restart. See FightInstance.CastsThisTurn and CastCounterIsolationTests.
        //
        // The number matters to the client rather than only to us: it reads it out of the cast
        // packet (f7.f5) and compares it against the spell's limit to grey the icon out.

        private static async Task HandleCombatMoveRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;
            var current = fight.CurrentFighter;
            if (current == null || !current.ControlledBy(GameState.CharacterId)) return;

            // AVISO: esto NO es por donde se anda en combate, aunque lo parezca por el nombre.
            //
            // Andar en combate es el jrw, y lo resuelve WalkAsync. Medido sobre la captura
            // «combate contra 4 poutchs nivel 25»: el cliente manda jrw catorce veces y jzy UNA,
            // la de colocarse antes de empezar. Esta función sólo se alcanza con un jzy cuando el
            // combate ya está en marcha, y eso el cliente no lo manda.
            //
            // Buscaba «jyz» —la z y la y cambiadas de sitio, el mismo desliz que ya apareció en
            // HandlePlacementCellChangeRequest— y como ExtractMessagePayload compara la url entera
            // y exacta, devolvía null siempre. Se corrigen las letras y se deja: cuesta cero y
            // arreglado no engaña al siguiente que lo lea. Lo que NO se puede hacer es tomarlo por
            // el manejador del movimiento, que es justo lo que despistó a la auditoría.
            var inner = ExtractMessagePayload(payload, Op.Uri(Op.Jzy));
            if (inner == null) inner = ExtractMessagePayload(payload, Op.Uri(Op.Joi));

            var vertices = new List<int>();
            if (inner != null)
            {
                try
                {
                    var msg = ProtoMessage.Parse(inner);
                    foreach (var f in msg.Fields)
                    {
                        if (f.FieldNumber == 3)
                        {
                            if (f.WireType == 0)
                            {
                                int val = (int)f.VarIntValue;
                                vertices.Add(val % 4096);
                            }
                            else if (f.WireType == 2)
                            {
                                int pos = 0;
                                while (pos < f.BytesValue.Length)
                                {
                                    int val = (int)ReadVarInt(f.BytesValue, ref pos);
                                    vertices.Add(val % 4096);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            if (vertices.Count == 0) return;

            // COMBAT walkability, not the one in map_walkable_cells.json: that one trims the map
            // borders (it was generated to place mobs in roleplay) and left out the arena's outer
            // ring, which you can perfectly well walk on during a fight.
            var arenaWalkable = MapManager.GetFightWalkable(fight.ArenaMapId);
            var expandedPath = MapGeometry.ExpandPath(vertices, arenaWalkable);

            if (expandedPath.Count <= 1) return;

            int steps = Math.Min(expandedPath.Count - 1, current.CurrentMP);
            var actualPath = expandedPath.Take(steps + 1).ToList();

            current.AccumulatedMpLoss += steps;
            current.CurrentMP -= steps;
            current.CellId = actualPath.Last();

            Program.LogDebug($"[FightHandler] Combat move for Player #{current.Id}: {actualPath.Count} cells to cell {current.CellId} (used {steps} MP, {current.CurrentMP} MP left).");

            var jud4Start = new ProtoMessage();
            jud4Start.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 4 });
            jud4Start.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            await ATodosAsync(fight, BuildGameNodePacket("type.ankama.com/jud", jud4Start.ToByteArray()));

            await ATodosAsync(fight, BuildJooMovementPacket(current.Id, actualPath));

            var jud3 = new ProtoMessage();
            jud3.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 3 });
            jud3.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            await ATodosAsync(fight, BuildGameNodePacket("type.ankama.com/jud", jud3.ToByteArray()));

            await ATodosAsync(fight, BuildJvmPacket(current.Id, 23, -current.AccumulatedMpLoss, current.MaxMP));

            var juc3 = new ProtoMessage();
            juc3.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 3 });
            juc3.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            juc3.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            await ATodosAsync(fight, BuildGameNodePacket("type.ankama.com/juc", juc3.ToByteArray()));

            var jtxMsg = new ProtoMessage();
            var f6Sub = new ProtoMessage();
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = current.Id });
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -steps });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = f6Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 129 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = current.Id });
            await ATodosAsync(fight, BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray()));

            var juc4End = new ProtoMessage();
            juc4End.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 4 });
            juc4End.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            juc4End.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            await ATodosAsync(fight, BuildGameNodePacket("type.ankama.com/juc", juc4End.ToByteArray()));
        }

        // =========================================================================
        // PACKET BUILDERS AND SENDERS (100% Organic Protobuf Construction)
        // =========================================================================

        public static byte[] BuildJpfPacket(long mobContextId)
        {
            int subAreaId = 450;
            var fight = GetCurrentFight();
            long mId = fight != null ? fight.RoleplayMapId : GameState.MapId;
            if (MapManager.Maps.TryGetValue(mId, out var mInfo) && mInfo.SubAreaId != 0)
            {
                subAreaId = mInfo.SubAreaId;
            }

            var jpfSub = new ProtoMessage();

            var f1Sub = new ProtoMessage();
            f1Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = subAreaId });
            f1Sub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 5 });
            jpfSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = f1Sub.ToByteArray() });

            var boneSub = new ProtoMessage();
            boneSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3273 });
            boneSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 3 });
            boneSub.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 3 });

            var f3Sub = new ProtoMessage();
            f3Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = boneSub.ToByteArray() });

            var actorSub = new ProtoMessage();
            actorSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -1 });
            actorSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = f3Sub.ToByteArray() });
            actorSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });

            var lookSub = new ProtoMessage();
            lookSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 3256 });
            lookSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });

            var f2Sub = new ProtoMessage();
            f2Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = lookSub.ToByteArray() });
            f2Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = actorSub.ToByteArray() });

            jpfSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = f2Sub.ToByteArray() });
            jpfSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = mobContextId });

            var jpfMsg = new ProtoMessage();
            jpfMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = jpfSub.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jpf", jpfMsg.ToByteArray());
        }

        public static List<byte[]> BuildPlacementPossiblePositionsPackets(FightInstance fight)
        {
            var list = new List<byte[]>();

            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(GameState.CharacterName);

            var lookBreedSub = new ProtoMessage();
            lookBreedSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = nameBytes });
            lookBreedSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = GameState.Breed });

            var memberSub = new ProtoMessage();
            memberSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.ChallengerLeaderId });
            memberSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = lookBreedSub.ToByteArray() });

            var memberOuter = new ProtoMessage();
            memberOuter.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = memberSub.ToByteArray() });

            // Send jyf #1 (Team 0: Player Team)
            var msg1 = new ProtoMessage();
            var team0Wrapper = new ProtoMessage();
            team0Wrapper.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.ChallengerLeaderId });
            team0Wrapper.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = 1 });
            team0Wrapper.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 2, BytesValue = memberOuter.ToByteArray() });

            msg1.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = team0Wrapper.ToByteArray() });
            msg1.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 300 });
            list.Add(BuildGameNodePacket("type.ankama.com/jyf", msg1.ToByteArray()));

            // Send jyf #2 (Team 1: Monster Team)
            var msg2 = new ProtoMessage();
            var team1Wrapper = new ProtoMessage();
            team1Wrapper.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.DefenderLeaderId });
            team1Wrapper.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
            team1Wrapper.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 1 });
            team1Wrapper.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = 1 });

            msg2.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = team1Wrapper.ToByteArray() });
            msg2.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 300 });
            list.Add(BuildGameNodePacket("type.ankama.com/jyf", msg2.ToByteArray()));

            return list;
        }

        private static async Task SendFightStarting(NetworkStream stream, FightInstance fight)
        {
            var msg = new ProtoMessage();
            msg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 300 });
            msg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.ChallengerLeaderId });
            msg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 4 });
            msg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = fight.DefenderLeaderId });

            byte[] env = BuildGameNodePacket(Op.Uri(Op.Jya), msg.ToByteArray());
            await WriteFrameAsync(stream, env);
            Program.LogDebug($"[FightHandler] Sent jya (FightStarting) for Challenger={fight.ChallengerLeaderId}, Defender={fight.DefenderLeaderId}.");
        }

        public static byte[] BuildFighterShowBytes(Fighter fighter)
        {
            int cellId = fighter.CellId;
            int dir = fighter.TeamId == 0 ? 3 : 7;
            long fighterId = fighter.Id; // -1, -2 for monsters, CharacterId for player

            // 1. Position submessage: f1=0, f2=cellId, f5=dir
            var posMsg = new ProtoMessage();
            posMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 0 });
            posMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = cellId });
            posMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = dir });
            byte[] posBytes = posMsg.ToByteArray();

            // 2. Fighter inner location: f4 = { f1 = posBytes, f3 = fighterId }
            var fighterInnerLoc = new ProtoMessage();
            fighterInnerLoc.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = posBytes });
            fighterInnerLoc.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fighterId });

            // 3. Team submessage: f2 = teamId, f3 = 1, f4 = fighterInnerLoc
            var teamMsg = new ProtoMessage();
            teamMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighter.TeamId });
            teamMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            teamMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = fighterInnerLoc.ToByteArray() });

            // 4. Stats submessage (lgk): 36 canonical entries matching official PCAP
            var statsMsg = new ProtoMessage();
            statsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 2 });

            void AddStatEntry(int? statId, ProtoMessage valMsg)
            {
                var entry = new ProtoMessage();
                entry.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = valMsg.ToByteArray() });
                if (statId.HasValue)
                {
                    entry.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = statId.Value });
                }
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = entry.ToByteArray() });
            }

            void AddSimpleVal(int? statId, int val)
            {
                var vSub = new ProtoMessage();
                vSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = val });
                AddStatEntry(statId, vSub);
            }

            void AddBaseBonusVal(int? statId, int baseVal, int bonusVal)
            {
                var vSub = new ProtoMessage();
                if (baseVal != 0) vSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = baseVal });
                if (bonusVal != 0) vSub.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = bonusVal });
                AddStatEntry(statId, vSub);
            }

            // 1. AP (statId 1)
            if (!fighter.IsMonster) AddBaseBonusVal(1, fighter.MaxAP, 0);
            else AddSimpleVal(1, fighter.MaxAP);

            // 2. MP (statId 23)
            if (!fighter.IsMonster) AddBaseBonusVal(23, fighter.MaxMP, 0);
            else AddSimpleVal(23, fighter.MaxMP);

            // 3-6. 37, 33, 35, 36 (empty)
            AddStatEntry(37, new ProtoMessage());
            AddStatEntry(33, new ProtoMessage());
            AddStatEntry(35, new ProtoMessage());
            AddStatEntry(36, new ProtoMessage());

            // 7. 34 (Total HP - 12 for monster, empty for player)
            if (fighter.IsMonster) AddSimpleVal(34, 12);
            else AddStatEntry(34, new ProtoMessage());

            // 8-15. 58, 54, 56, 57, 55, 85, 87, 101 (empty)
            AddStatEntry(58, new ProtoMessage());
            AddStatEntry(54, new ProtoMessage());
            AddStatEntry(56, new ProtoMessage());
            AddStatEntry(57, new ProtoMessage());
            AddStatEntry(55, new ProtoMessage());
            AddStatEntry(85, new ProtoMessage());
            AddStatEntry(87, new ProtoMessage());
            AddStatEntry(101, new ProtoMessage());

            // 16-17. 27, 28 (1 for monster, empty for player)
            if (fighter.IsMonster) { AddSimpleVal(27, 1); AddSimpleVal(28, 1); }
            else { AddStatEntry(27, new ProtoMessage()); AddStatEntry(28, new ProtoMessage()); }

            // 18. 93 (val 3)
            AddSimpleVal(93, 3);

            // 19-20. 79, 78 (empty)
            AddStatEntry(79, new ProtoMessage());
            AddStatEntry(78, new ProtoMessage());

            // 21. 44, la iniciativa. Estaba a base 5 y bonus 12, que son los números del personaje
            // de la captura. Va la del combatiente, partida igual que en la ficha de roleplay: lo
            // invertido en la base y lo del equipo en el bonus. El monstruo lo manda vacío, que es
            // lo que hace la captura.
            //
            // Sale del GameState de ESTA sesión, como todo lo demás de este método: con varios
            // jugadores en un combate habrá que sacarlo del propio combatiente.
            if (!fighter.IsMonster) AddBaseBonusVal(44, StatsHandler.IniciativaInvertida(),
                                                        StatsHandler.IniciativaDelEquipo());
            else AddStatEntry(44, new ProtoMessage());

            // 22. STATID 0 = LIFE POINTS / MAX HP! (statId = null -> omitted f5)
            //
            // Ojo con lo que va aquí en el caso del JUGADOR: los puntos de vida que NO salen de la
            // vitalidad, o sea los cincuenta de salida más cinco por nivel. La vitalidad la pone el
            // cliente por su cuenta, que para eso ya conoce sus objetos, y lo que mandemos aquí se
            // le SUMA.
            //
            // Se veía en dos sitios a la vez. Mandando GetPlayerMaxHp() —que ahora sí incluye el
            // equipo, desde que LoadInventory lee bien los efectos— el personaje entraba en combate
            // con 8556 de vida en vez de 4803: la vitalidad contada dos veces, la del cliente y la
            // nuestra. Y antes de eso, cuando el equipo valía cero, seguía sobrando la vitalidad
            // BASE: fuera de combate ponía 4803 y dentro 4806, tres de más, que son justo los tres
            // puntos de Vitality de la ficha. Con la vida pelada del nivel cuadran los dos casos.
            //
            // Los monstruos van al revés: ahí sí va su vida entera, porque el cliente no sabe nada
            // de ellos.
            if (!fighter.IsMonster) AddBaseBonusVal(null, LifeFromLevel(fighter.Level), 0);
            else AddSimpleVal(null, fighter.MaxHP);

            // 23. 11 (Vitality: player bonus; monster empty)
            if (!fighter.IsMonster) AddBaseBonusVal(11, 0, GameState.TotalVitality + StatsHandler.GetEquipBonus(11));
            else AddStatEntry(11, new ProtoMessage());

            // 25. 97 (empty)
            AddStatEntry(97, new ProtoMessage());

            // 26-36. 107, 150, 120..125, 141..143 = 100
            AddSimpleVal(107, 100);
            AddSimpleVal(150, 100);
            for (int s = 120; s <= 125; s++) AddSimpleVal(s, 100);
            for (int s = 141; s <= 143; s++) AddSimpleVal(s, 100);

            // 5. Fighter sub-field 3: f1 = teamMsg, f2 = (player ? playerId : 0), f4 = statsMsg, f7 = (monster ? f7Sub : null)
            var fighterSub3 = new ProtoMessage();
            fighterSub3.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = teamMsg.ToByteArray() });
            fighterSub3.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighter.IsMonster ? 0 : fighterId });
            fighterSub3.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = statsMsg.ToByteArray() });

            if (fighter.IsMonster)
            {
                int mId = fighter.MonsterId > 0 ? fighter.MonsterId : 3273;
                int gr = fighter.GradeIndex + 1;
                var f7Inner = new ProtoMessage();
                f7Inner.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = mId });
                f7Inner.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = gr });
                f7Inner.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 3 });

                var f7Outer = new ProtoMessage();
                f7Outer.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = f7Inner.ToByteArray() });
                fighterSub3.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 2, BytesValue = f7Outer.ToByteArray() });
            }
            else
            {
                // Block f9: the PLAYER's sheet (name and level). It is the counterpart of the f7
                // monsters use, and without it the client shows "???" and "Lv. 0" on mouse over.
                // Structure decoded from the capture (a level 2 character):
                //   f9 { f3 { f2 = 1 },
                //        f4 { f2 = <breed>, f3 = 3, f4 = 1, f5 { f2 = <level>, f4 = 3 } },
                //        f6 = -1,
                //        f7 = "<name>" }          <- the character name as raw UTF-8 bytes
                var f9Level = new ProtoMessage();
                f9Level.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighter.Level });
                f9Level.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 3 });

                var f9Breed = new ProtoMessage();
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.Breed });
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = f9Level.ToByteArray() });

                var f9Flag = new ProtoMessage();
                f9Flag.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });

                var f9 = new ProtoMessage();
                f9.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = f9Flag.ToByteArray() });
                f9.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = f9Breed.ToByteArray() });
                f9.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = -1 });
                f9.Fields.Add(new ProtoField
                {
                    FieldNumber = 7,
                    WireType = 2,
                    BytesValue = System.Text.Encoding.UTF8.GetBytes(fighter.Name ?? GameState.CharacterName ?? "")
                });

                fighterSub3.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 2, BytesValue = f9.ToByteArray() });
            }

            // 6. Entity details field 2:
            var entityDetails = new ProtoMessage();

            if (!fighter.IsMonster)
            {
                byte[] playerLookBytes = (GameState.LookBytes != null && GameState.LookBytes.Length > 0)
                    ? GameState.LookBytes
                    : NetworkEnvelope.ConvertHexStringToByteArray("08-01-18-03-22-18-A2-8B-9B-0F-CB-E5-F6-15-A4-E1-B9-19-92-A6-C8-20-88-8C-A0-28-F5-B7-CB-34-2A-03-5B-E4-10-42-01-34-32-02-20-01-38-09");
                entityDetails.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = playerLookBytes });
            }
            else
            {
                int boneId = fighter.LookBoneId > 0 ? fighter.LookBoneId : 3256;
                var monsterLookMsg = new ProtoMessage();
                monsterLookMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = boneId });
                monsterLookMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });
                entityDetails.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = monsterLookMsg.ToByteArray() });

                var boneSub = new ProtoMessage();
                boneSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = boneId });
                boneSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 3 });
                boneSub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 3 });

                var boneWrapper = new ProtoMessage();
                boneWrapper.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = boneSub.ToByteArray() });
                entityDetails.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 2, BytesValue = boneWrapper.ToByteArray() });
            }

            entityDetails.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = fighterSub3.ToByteArray() });

            // 7. Outer jxx payload: f2 = { f1 = posBytes, f2 = entityDetails, f3 = fighterId }
            var jxxInnerPayload = new ProtoMessage();
            jxxInnerPayload.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = posBytes });
            jxxInnerPayload.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = entityDetails.ToByteArray() });
            jxxInnerPayload.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fighterId });

            var jxxOuterPayload = new ProtoMessage();
            jxxOuterPayload.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = jxxInnerPayload.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jxx", jxxOuterPayload.ToByteArray());
        }

        private static async Task SendFighterShow(NetworkStream stream, Fighter fighter)
        {
            byte[] packet = BuildFighterShowBytes(fighter);
            await WriteFrameAsync(stream, packet);
            Program.LogDebug($"[FightHandler] Sent organic jxx for {(fighter.IsMonster ? $"Monster ID {fighter.MonsterId} (Fighter ID {fighter.Id}, BoneId {fighter.LookBoneId})" : $"Player ID {fighter.Id}")} at Cell {fighter.CellId}.");
        }

        // Aqui habia un segundo Random —_lootRandom— sin candado, mientras el otro (_dado) si lo
        // llevaba. Random NO es seguro entre hilos: dos combates tirando a la vez no es que saquen
        // el mismo numero, es que dejan el estado interno hecho un lio y a partir de ahi devuelve
        // CEROS para siempre. Con el botin eso es un servidor donde no cae nada y nadie entiende
        // por que. Se quito y ahora las dos tiradas salen del mismo sitio, con su candado.

        /// <summary>
        /// Rolls the loot of every defeated monster and puts it into the inventory.
        ///
        /// Each monster has its own table in MonsterTemplates.drops, with one probability per
        /// grade. The red piwi chief, for instance, drops a red piwi feather at 100 %, sesame
        /// seeds at 18 % and a pouch of lemons at 3 %.
        ///
        /// What is NOT applied yet: prospecting. In the real game the probability is multiplied by
        /// the character's prospecting divided by 100, but prospecting from the gear is not being
        /// computed, so the base percentage is used (equivalent to 100 prospecting).
        /// </summary>
        /// <summary>Sube una cantidad en el tanto por ciento que hayan dado los retos.</summary>
        private static long ConElExtra(long cuanto, int extra)
            => extra <= 0 ? cuanto : cuanto + cuanto * extra / 100;

        private static Dictionary<int, int> RollFightLoot(FightInstance fight, int extra,
                                                          out List<PlayerItem> caidos)
        {
            var loot = RollLoot(fight, extra);
            EntregarBotin(loot, out caidos);
            return loot;
        }

        /// <summary>
        /// The roll alone, for the player of this session, delivering nothing: what
        /// <see cref="PlanRewards"/> rolls for each winner before anybody is shown the end.
        /// </summary>
        private static Dictionary<int, int> RollLoot(FightInstance fight, int extra)
        {
            var loot = new Dictionary<int, int>();

            // Los INVOCADOS no pagan. Entran en el bando del que los invoca con IsMonster
            // puesto, así que un monstruo que invoque metería a su criatura en este bucle: se
            // llevaría su propia tabla de botín y, con la moneda, sería una fábrica de dinero
            // que se abre sola. Se distinguen por el Invocador, que sólo tienen ellos.
            foreach (var monster in fight.Rojo.Where(m => m.IsMonster && !m.EsInvocado))
            {
                // La moneda del servidor. Cae SIEMPRE, sin tirar el dado: no es un objeto de la
                // tabla del monstruo, es lo que paga el combate. La cantidad sale del nivel, de
                // 25 en 25 (ver Managers.JondoCoin).
                int monedas = Managers.JondoCoin.RewardFor(monster.Level);
                loot.TryGetValue(Managers.JondoCoin.TemplateId, out int llevadas);
                loot[Managers.JondoCoin.TemplateId] = llevadas + monedas;

                var table = DatabaseManager.GetMonsterDrops(monster.MonsterId, monster.GradeIndex);
                foreach (var drop in table)
                {
                    // En el botin el extra sube la PROBABILIDAD de que caiga, no la cantidad: es
                    // una tirada por objeto y por monstruo, y lo que el reto mejora es la suerte.
                    double probabilidad = extra > 0
                        ? Math.Min(100.0, drop.PercentDrop * (100.0 + extra) / 100.0)
                        : drop.PercentDrop;
                    if (TirarPorcentaje() >= probabilidad) continue;
                    loot.TryGetValue(drop.ObjectId, out int q);
                    loot[drop.ObjectId] = q + 1;
                }

                // Y la tabla GLOBAL, que es la otra que tiene y que no se leía. Ahí es donde vive
                // el botín de las raids: los nueve monstruos de la Sima no llevan ni una fila en
                // su tabla propia y llevan cincuenta y cinco en ésta —la sal de las profundidades
                // y las siete gemas—, así que sin esto una raid es un sitio donde no cae nada.
                foreach (var drop in DatabaseManager.GetMonsterGlobalDrops(monster.MonsterId))
                {
                    if (!SeLoLleva(drop.ReceiverCriterion)) continue;

                    double probabilidad = extra > 0
                        ? Math.Min(100.0, drop.PercentDrop * (100.0 + extra) / 100.0)
                        : drop.PercentDrop;
                    if (TirarPorcentaje() >= probabilidad) continue;
                    loot.TryGetValue(drop.ObjectId, out int q);
                    loot[drop.ObjectId] = q + 1;
                }
            }

            return loot;
        }

        /// <summary>
        /// Si a este jugador le toca una fila de la tabla global, según el criterio que ella trae.
        /// </summary>
        /// <remarks>
        /// Lo que no se sabe NO cae, y eso es la mitad de por qué esto funciona. La fila de los
        /// fragmentos de anomalía la llevan casi todos los monstruos del juego con el criterio
        /// <c>(HA=50|HS=3383)&amp;Az=1&amp;Pm!28049666</c>, del que no sabemos contestar ni una
        /// letra: sale Desconocido, no cae, y el mundo entero sigue como estaba. Las de la raid no
        /// traen criterio ninguno, así que caen.
        /// </remarks>
        private static bool SeLoLleva(string criterion)
        {
            if (string.IsNullOrWhiteSpace(criterion)) return true;
            return Jondo.Unity.World.Content.Criterion.Met(criterion,
                Managers.GuildRaidManager.ResolverFor(GameState.CharacterId));
        }

        /// <summary>
        /// Mete el botín en el inventario y deja las DOS vistas al día.
        /// </summary>
        /// <remarks>
        /// Estaba dentro de <see cref="RollFightLoot"/> y sale de ahí porque el koliseo paga lo
        /// suyo sin pasar por las tablas de los monstruos y necesita exactamente esto mismo. Que
        /// haya dos caminos que entregan objetos y sólo uno refresque las vistas es la forma
        /// conocida de que el botín se guarde bien en la base y el jugador no lo vea.
        ///
        /// Hay dos vistas del inventario: <c>GameState</c> es la del estado de sesión, y la que lee
        /// BuildInventory para armar el ivx es <c>Managers.Equipment</c>. Refrescar sólo la primera
        /// dejaba a la segunda con lo de antes hasta el siguiente login — 73 Jondo Coin en
        /// CharacterItems y ni una en la pantalla.
        ///
        /// De una vez y no de uno en uno: cada AddItemToInventory cargaba el inventario entero para
        /// ver si el objeto ya estaba, así que cinco objetos distintos eran cinco lecturas.
        /// </remarks>
        private static void EntregarBotin(Dictionary<int, int> loot, out List<PlayerItem> caidos)
        {
            caidos = DatabaseManager.AddItemsToInventory(GameState.CharacterId, loot);
            foreach (var kv in loot)
                Program.LogDebug($"[FightHandler] Loot: item {kv.Key} x{kv.Value} added to the inventory.");

            if (loot.Count == 0) return;

            GameState.SetInventory(DatabaseManager.LoadInventory(GameState.CharacterId));

            foreach (var pieza in caidos)
            {
                Managers.Equipment.Remove(pieza.Uid, int.MaxValue);
                Managers.Equipment.Add(pieza.Uid, pieza.ItemId, pieza.Quantity,
                                       Managers.Equipment.Bag, pieza.RawEffects ?? "[]");
            }
        }

        // =========================================================================
        // HELPERS
        // =========================================================================

        private static uint ReadVarInt(byte[] data, ref int pos)
        {
            uint value = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return value;
        }
    }
}
