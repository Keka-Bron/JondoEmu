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
    public static class FightHandler
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
            // Si a este jugador le quedaba un combate colgado de antes, se le quita el suyo y sólo
            // el suyo. Aquí había un _activeFights.Clear(): empezar una pelea borraba las de TODOS
            // los demás jugadores del servidor. El final normal de un combate ya lo quita solo
            // (TryRemove más abajo, al mandar el resultado), así que esto es sólo la red por si
            // alguno se quedó suelto.
            long yo = GameState.CharacterId;
            foreach (var par in _activeFights)
            {
                bool esSuyo = par.Value.Team0.Exists(f => f.Id == yo)
                           || par.Value.Team1.Exists(f => f.Id == yo);
                if (esSuyo) _activeFights.TryRemove(par.Key, out _);
            }

            GameState.IsInFight = true;
            GameState.CurrentFightMobId = mobGroup.MobId;

            long fightId = System.Threading.Interlocked.Increment(ref _nextFightId);
            long arenaMapId = MapManager.ResolveArenaMapId(mapId);
            var fight = new FightInstance(fightId, mapId, arenaMapId);

            // El id contextual del grupo ES su MobId, el mismo que viaja en el jss y en el jpv y el
            // mismo que el cliente devuelve al clicarlo. El parámetro mobContextId sobra desde que
            // los dos paquetes reparten el mismo número; se queda por las llamadas de fuera.
            fight.DefenderLeaderId = mobGroup.MobId;

            // Generate placement cells from arena map walkable cells
            var walkableCells = MobSpawnManager.GetInnerWalkableCells(arenaMapId);
            fight.GeneratePlacementCells(walkableCells);

            // Las cuatro elementales COMPLETAS: lo que el jugador se ha puesto de puntos más lo que
            // le dé el equipo. Se calculan aquí arriba porque la iniciativa las necesita enteras.
            int fuerza = GameState.StatStrength + StatsHandler.GetEquipBonus(10);
            int inteligencia = GameState.StatIntelligence + StatsHandler.GetEquipBonus(15);
            int suerte = GameState.StatChance + StatsHandler.GetEquipBonus(13);
            int agilidad = GameState.StatAgility + StatsHandler.GetEquipBonus(14);

            // Build Player Fighter from GameState (Fighter ID = player CharacterId)
            var playerFighter = new Fighter
            {
                Id = GameState.CharacterId,
                Name = GameState.CharacterName,
                TeamId = 0,
                CellId = fight.BluePlacementCells.FirstOrDefault(),
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
                Vitality = GameState.StatVitality + StatsHandler.GetEquipBonus(11),
                Range = StatsHandler.GetEquipBonus(19),
                LookBoneId = 744,
                IsMonster = false
            };
            playerFighter.CurrentHP = playerFighter.MaxHP;
            playerFighter.CurrentAP = playerFighter.MaxAP;
            playerFighter.CurrentMP = playerFighter.MaxMP;
            RellenarLaFicha(playerFighter);

            // Las actitudes que le dan sus objetos: los seis dofus y los trofeos regalan cada uno
            // un "hechizo" por su efecto 1175, y ésos son los que hacen cosas al empezar el turno o
            // al recibir un golpe. De ahí sale, sin escribir nada suyo, el punto de acción del
            // Dofus Ocre.
            playerFighter.Buffs.Vaciar();
            playerFighter.Buffs.Actitudes.AddRange(
                Managers.SpellEffects.ActitudesDelEquipo(GameState.CharacterId));
            if (playerFighter.Buffs.Actitudes.Count > 0)
            {
                Program.LogDebug($"[Combate] Actitudes del equipo: " +
                                 string.Join(", ", playerFighter.Buffs.Actitudes));
            }

            fight.AddPlayer(playerFighter);

            // Build Monster Fighters from real MobGroup data.
            // Fighter IDs for monsters MUST be sequential negative numbers per fight (-1, -2, -3...)
            long monsterSeqId = -1;
            int redIdx = 0;
            foreach (var member in mobGroup.Members)
            {
                long monFighterId = monsterSeqId--;
                int monCellId = (fight.RedPlacementCells.Count > redIdx)
                    ? fight.RedPlacementCells[redIdx++]
                    : fight.RedPlacementCells.FirstOrDefault();

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
                monsterFighter.CurrentAP = monsterFighter.MaxAP;
                monsterFighter.CurrentMP = monsterFighter.MaxMP;

                fight.AddMonster(monsterFighter);
            }

            _activeFights[fightId] = fight;
            Program.LogDebug($"[FightHandler] Fight #{fightId} created on map {mapId}:");
            Program.LogDebug($"  Team 0 (Players): {fight.Team0.Count} fighters (Leader ID: {fight.ChallengerLeaderId})");
            Program.LogDebug($"  Team 1 (Monsters): {fight.Team1.Count} fighters (Context ID: {fight.DefenderLeaderId})");
            foreach (var m in fight.Team1)
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
            GameState.MapId = fight.MapId;
            GameState.CellId = fight.Team0.Count > 0 ? fight.Team0[0].CellId : GameState.CellId;
            fight.HasLoadedMap = false;

            // De dónde se salió, para poder volver. El mapa de combate es de instancia y no vale
            // como sitio donde dejar al personaje.
            var suyo = Network.SessionContext.State;
            suyo.RoleplayMapId = fight.RoleplayMapId;
            suyo.RoleplayCellId = fight.Team0.Count > 0 ? fight.Team0[0].CellId : 0;

            // Sin guardar el personaje: el mapa de combate es un mapa de instancia y dejarlo escrito
            // en la ficha lo devolvería ahí al volver a entrar, a un sitio del que no se sale.
            //
            // Con eso no basta, ojo: cualquier otra cosa que guarde el personaje mientras dura el
            // combate —comprar, cobrar kamas, subir características— lo escribe igual, y como el
            // final de combate todavía no está hecho, el personaje se queda atrapado en la arena.
            // Por eso <see cref="LeaveFight"/> lo devuelve, y lo llama la salida al menú.
            await WriteFrameAsync(stream, ConnectionProtocol.BuildActorLeft(GameState.CharacterId));

            // Y aquí va la marca, ANTES de mandarle cargar el mapa: kmp con f1 a uno quiere decir
            // "lo que viene es un mapa de combate". De eso depende todo lo demás, porque es lo que
            // hace que el cliente pida el combate con un ijm en vez de pedir un mapa corriente con
            // un jrh. Sin ella carga el tablero y se queda en modo mapa normal.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmu,
                Network.FightProtocol.BuildFightAgainst(fight.DefenderLeaderId)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kml));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmp,
                Network.FightProtocol.BuildFightMapComing()));

            await WriteFrameAsync(stream, ConnectionProtocol.BuildLoadMap(fight.MapId));
            await WriteFrameAsync(stream, ConnectionProtocol.BuildMapClock());
            await WriteFrameAsync(stream, ConnectionProtocol.BuildMapDiscovered(fight.MapId));

            Program.LogDebug($"[Combate] Combate #{fight.FightId} en el mapa {fight.MapId}. " +
                             "Esperando a que el cliente pida los actores.");

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
            long currentFightId = fight.FightId;
            var cuentaAtras = new System.Threading.CancellationTokenSource();
            fight.CancelPlacementTimer();
            fight.PlacementTimerCts = cuentaAtras;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(PlacementTimeoutMs, cuentaAtras.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                // El mismo candado que usan el reloj de turno y lo que llega del cliente. Sin él,
                // pulsar «listo» justo al vencer el plazo arrancaba el combate dos veces.
                var turno = MiTurno();
                await turno.WaitAsync();
                try
                {
                    var f = GetCurrentFight();
                    if (f == null || f.FightId != currentFightId) return;
                    if (f.State != Jondo.Unity.World.Fights.FightState.Placement) return;

                    Program.LogDebug($"[Combate] Se acabó el tiempo de colocación del combate #{currentFightId}.");
                    await HandleTurnReady(stream, Array.Empty<byte>());
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

        /// <summary>Lo que dura la colocación. El cliente enseña la misma cuenta atrás.</summary>
        private const int PlacementTimeoutMs = 45000;

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
            suyo.MapId = suyo.RoleplayMapId;
            if (suyo.RoleplayCellId != 0) suyo.CellId = suyo.RoleplayCellId;
            DatabaseManager.SaveCurrentCharacter();

            suyo.RoleplayMapId = 0;
            suyo.RoleplayCellId = 0;

            // SÓLO el combate de este jugador. Aquí había un _activeFights.Clear(), que se llevaba
            // por delante los combates de todos los demás: al acabar uno el suyo, al resto se le
            // evaporaba la pelea a media pantalla.
            long quien = suyo.CharacterId;
            foreach (var par in _activeFights)
            {
                bool esSuyo = par.Value.Team0.Exists(f => f.Id == quien)
                           || par.Value.Team1.Exists(f => f.Id == quien);
                if (esSuyo) _activeFights.TryRemove(par.Key, out _);
            }

            Program.LogDebug("[Combate] Fuera del combate; el personaje vuelve al mapa de superficie.");
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
            if (fight == null || fight.HasLoadedMap) return null;
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
        public static async Task SendPreparationAsync(NetworkStream stream, FightInstance fight)
        {
            fight.HasLoadedMap = true;

            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);

            // Antes que nada, dónde está cada uno. En la captura salen ocho kmk seguidos —uno por
            // combatiente— nada más cargar el mapa y ANTES de que se anuncie el combate. Son los
            // que ponen a la gente sobre el tablero; sin ellos el cliente tiene un combate con
            // combatientes que no están en ninguna casilla.
            foreach (var fighter in fight.Team0)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmk,
                    Network.FightProtocol.BuildFighterPlaced(fighter.CellId, FacingOf(fight, fighter), fighter.Id)));
            }
            foreach (var fighter in fight.Team1)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmk,
                    Network.FightProtocol.BuildFighterPlaced(fighter.CellId, FacingOf(fight, fighter), fighter.Id)));
            }

            // Cuántos retos se eligen en este combate. Va aquí, detrás de los kmk y delante del
            // primer jxg, que es donde lo pone el servidor real; y va DOS VECES, con el mismo
            // número, porque el original lo repite detrás de las casillas. El kwk vacío sólo
            // acompaña al primero.
            await ChallengeHandler.SendCountAsync(stream, fight, primeraVez: true);

            // Lo primero, decirle que AQUÍ HAY UN COMBATE. Sin el kam el cliente no tiene ningún
            // combate al que agarrar lo que viene detrás, y se le ve reventar en su propio registro
            // al llegarle el jwq, recorriendo una lista de combatientes que no existe. Con el mapa
            // táctico ya cargado y nada dibujado encima, que es justo lo que pasaba.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ijq,
                Network.FightProtocol.BuildMapReady()));

            var monsters = fight.Team1.ConvertAll(f => (long)f.MonsterId);
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kam,
                Network.FightProtocol.BuildFightAnnounced(
                    Network.FightProtocol.AgainstMonsters, fight.DefenderLeaderId, monsters,
                    fight.FightId, GameState.CharacterId)));

            // Con la cuenta atrás de la colocación, para que el reloj que el cliente enseña sea el
            // mismo que el que corre aquí.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kaa,
                Network.FightProtocol.BuildFightSummary(Network.FightProtocol.AgainstMonsters,
                                                        PlacementTimeoutMs / 100)));

            foreach (var fighter in fight.Team0)
            {
                byte[] look = character != null
                    ? Managers.BreedLookTable.BuildLook(character.Breed, character.Sex,
                                                        character.HeadId, null, character.Id)
                    : Array.Empty<byte>();

                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxg,
                    Network.FightProtocol.BuildFighter(
                        fighter.CellId, FacingOf(fight, fighter), fighter.Id, PlacementSheetOf(fighter), look,
                        Network.FightProtocol.PlayerIdentity(character?.Breed ?? 0, fighter.Name,
                                                            character?.Sex ?? 0, fighter.Level),
                        isMonster: false)));
            }

            foreach (var fighter in fight.Team1)
            {
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
            await ChallengeHandler.SendCountAsync(stream, fight);

            // Detrás de las casillas, que es donde los pone la captura: quién está metido en el
            // combate y las cuatro opciones.
            foreach (var fighter in fight.Team0)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kae,
                    Network.FightProtocol.BuildFighterInFight(fighter.Id, fight.FightId)));
            }

            foreach (int option in Network.FightProtocol.FightOptions)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kau,
                    Network.FightProtocol.BuildFightOption(option, fight.FightId)));
            }

            var everyone = new List<long>();
            foreach (var fighter in fight.Team0) everyone.Add(fighter.Id);
            foreach (var fighter in fight.Team1) everyone.Add(fighter.Id);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzu,
                Network.FightProtocol.BuildTeams(everyone)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwq,
                Network.FightProtocol.BuildPlacementDone()));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jrk,
                Network.FightProtocol.BuildFightMap(fight.MapId)));

            // La lista de retos NO va aquí. El cliente manda sus ajustes del panel (kwo) nada más
            // recibir el jrk, y en las doce apariciones reales la lista llega SIEMPRE detrás de
            // ese kwo —incluidas las dos que el servidor manda sin que nadie las pida—. Se dispara
            // desde ChallengeHandler.SettingsAsync.

            Program.LogDebug($"[Combate] Preparación del combate #{fight.FightId}: " +
                             $"{fight.Team0.Count} contra {fight.Team1.Count}, " +
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
            => FacingFrom(fighter.CellId, fighter.TeamId == 0 ? fight.Team1 : fight.Team0);

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
            Fighter ficha, int caracteristica, int ronda)
        {
            var (suBase, suEquipo, suEmbrujo) = HuecosDeFicha(ficha, caracteristica, ronda);
            return (caracteristica, suBase, suEquipo, suEmbrujo);
        }

        private static (long Base, long Equipo, long Embrujo) HuecosDeFicha(Fighter ficha,
                                                                            int caracteristica,
                                                                            int ronda)
        {
            // Los puntos son lo que le queda de jugar este turno, y van en su molde propio.
            if (caracteristica == ActionPointsCharacteristic) return (ficha.CurrentAP, 0, 0);
            if (caracteristica == MovementPointsCharacteristic) return (ficha.CurrentMP, 0, 0);

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

        /// <summary>Los mismos números que usa datos/characteristics.json.</summary>
        private const int ActionPointsCharacteristic = 1;
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
            int sabiduria = GameState.StatWisdom + StatsHandler.GetEquipBonus(12);

            Poner(78, agilidad / PorCadaDiez);    // huida
            Poner(79, agilidad / PorCadaDiez);    // placaje
            Poner(27, sabiduria / PorCadaDiez);   // esquiva de puntos de acción
            Poner(28, sabiduria / PorCadaDiez);   // esquiva de puntos de movimiento
            Poner(12, GameState.StatWisdom);      // sabiduría
            Poner(26, 0);                         // invocaciones
            Poner(50, 0);                         // reenvío
            Poner(75, 0);                         // erosión
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
                (26, 0, fighter.Otra(26)), (50, 0, fighter.Otra(50)), (75, 10, fighter.Otra(75)),
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

            if (fight.HasLoadedMap)
            {
                // Re-send burst 3 on subsequent map load requests (jqf/kkr)
                await ResendFightMapBurst3(stream, fight);
                return;
            }
            fight.HasLoadedMap = true;
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
            foreach (var f in fight.Team0.Concat(fight.Team1))
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
            foreach (var f in fight.Team0.Concat(fight.Team1))
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
                : fight.Team0.Concat(fight.Team1).OrderByDescending(f => f.Initiative).ToList();

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

        public static async Task SendTurnStart(NetworkStream stream, Fighter fighter)
        {
            var fight = GetCurrentFight();

            // Send jwo header first (GameFightTurnStartPlayingMessage - empty 3-letter opcode)
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwo", Array.Empty<byte>()));

            // Send jox (GameFightTurnStartMessage) for turn 1
            var joxSub = new ProtoMessage();
            joxSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighter.Id });
            joxSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 0 });

            var joxMsg = new ProtoMessage();
            joxMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 450 }); // Turn time limit (45s)
            joxMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = joxSub.ToByteArray() });
            if (fight != null)
            {
                joxMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fight.MapId });
            }

            byte[] joxPacket = BuildGameNodePacket("type.ankama.com/jox", joxMsg.ToByteArray());
            await WriteFrameAsync(stream, joxPacket);
            Program.LogDebug($"[FightHandler] Sent jwo & jox (GameFightTurnStartMessage) for Fighter ID {fighter.Id} on map {fight?.MapId}.");
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
            var fight = GetCurrentFight();

            if (fight != null && fight.FinPendiente != 0 && !payloadStr.Contains(Op.Uri(Op.Jti)))
            {
                fight.FinPendiente = 0;
                Program.LogDebug("[Combate] El final estaba esperando el acuse y ha llegado otra cosa; se enseña.");
                await EndFightAsync(stream, fight);
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
                foreach (var f in fight.Team0.Concat(fight.Team1))
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
        {
            long quien = Network.SessionContext.State.CharacterId;
            if (quien == 0) return null;

            foreach (var combate in _activeFights.Values)
            {
                foreach (var f in combate.Team0) if (f.Id == quien) return combate;
                foreach (var f in combate.Team1) if (f.Id == quien) return combate;
            }
            return null;
        }

        private static async Task HandlePlacementCellChangeRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            // El cliente manda jzy, no jyz: las tres letras estaban transpuestas en el código de
            // antes, así que la rama nunca llegaba a entrar.
            var (_, newCell) = Network.FightProtocol.ReadPlacementMove(payload);
            if (newCell == 0) return;

            var player = fight.Team0.Find(f => f.Id == GameState.CharacterId);
            if (player == null) return;

            if (!fight.BluePlacementCells.Contains(newCell))
            {
                Program.LogDebug($"[Combate] La casilla {newCell} no es de las azules; no se coloca ahí.");
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
            foreach (var other in fight.Team0) spots.Add((other.CellId, FacingOf(fight, other), other.Id));
            foreach (var other in fight.Team1) spots.Add((other.CellId, FacingOf(fight, other), other.Id));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmk,
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

            Program.LogDebug("[Combate] El jugador se declara listo (kaq).");
            bool allReady = fight.SetFighterReady(GameState.CharacterId);

            // La pareja lqg + lqt, los dos vacíos, justo cuando están todos listos.
            //
            // Esto es lo que le dice al cliente que deje de regenerar vida. La regeneración la
            // lleva ÉL: el servidor no suma vida por su cuenta en ningún sitio. Al entrar al
            // mundo se le enciende —el lqg va en la ráfaga de entrada, que replicamos— y como
            // nadie se la apagaba, seguía tictaqueando dentro del combate: el jugador recibía un
            // golpe y veía cómo la barra se le rellenaba sola de uno en uno.
            //
            // Van aquí y no dentro de StartFightAsync porque en la captura del combate real caen
            // ANTES del kah del listo. Se mandan una sola vez por combate, que es como salen allí:
            // una vez en 2.937 mensajes.
            if (allReady)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqg));
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqt));
            }

            // Enterado, que es lo único que contesta el servidor real al listo.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kah,
                Network.FightProtocol.BuildReadyAck(GameState.CharacterId)));

            if (allReady) await StartFightAsync(stream, fight);
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
        private static async Task StartFightAsync(NetworkStream stream, FightInstance fight)
        {
            fight.StartFight();
            fight.CancelPlacementTimer();

            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            long me = GameState.CharacterId;

            // Los retos que quedaran sin validar los cierra el servidor aquí, ANTES del kai: si el
            // jugador se declaró listo con uno marcado y sin validar, ése cuenta, y si ni eso, el
            // servidor rellena. Detrás van los que impone el sitio. Medido en la anomalía.
            await ChallengeHandler.FillAsync(stream, fight);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kai,
                Network.FightProtocol.BuildFightBegins()));

            // Y la lista definitiva, que va entre el kai y el jyy.
            await ChallengeHandler.SendFinalListAsync(stream, fight);

            // Los hechizos con los que se pelea son los MISMOS que el personaje tiene fuera del
            // combate, y con la misma tripa: el hms de siempre lleva f1 { f1: grado, f3: hechizo,
            // f4: 1 } y el jyy lo repite en su f6. Antes se leía de la barra de accesos directos,
            // que puede estar a medio llenar, y por eso el cliente enseñaba todos los hechizos
            // durante la colocación —los suyos, de antes— y se quedaba en blanco al empezar el
            // turno, cuando por fin le llegaba nuestra lista corta.
            var spells = new List<(int Spell, int Grade)>();
            foreach (var spell in Managers.SpellTable.KnownFor(character?.Breed ?? 0,
                                                               GameState.CharacterLevel,
                                                               Managers.SpellChoices.Chosen))
            {
                spells.Add((spell.SpellId, spell.Grade));
            }
            // Dónde va cada uno en el panel: lo que el jugador tenga puesto, y si no tiene nada
            // puesto, los suyos en orden, que es mejor que dejarle el panel en blanco.
            var bar = new List<(int Slot, int Spell)>();
            foreach (var slot in Managers.SpellChoices.Bar)
            {
                if (slot.Value != 0) bar.Add((slot.Key, slot.Value));
            }
            if (bar.Count == 0)
            {
                for (int i = 0; i < spells.Count; i++) bar.Add((i + 1, spells[i].Spell));
            }

            // Y el cuerpo a cuerpo en el primer hueco que quede libre. Es una entrada de la barra
            // como cualquier otra, sólo que con el hechizo cero, y va SIEMPRE: no depende de que
            // se lleve arma equipada.
            bar.Add((PrimerHuecoLibre(bar), Network.FightProtocol.HechizoCuerpoACuerpo));
            bar.Sort((a, b) => a.Slot.CompareTo(b.Slot));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyy,
                Network.FightProtocol.BuildSpellBar(me, spells, bar)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxz,
                Network.FightProtocol.BuildRound(FirstRound)));

            // Los retos que senalan a un enemigo lo hacen aqui, detras del jyy, que es donde salen
            // los tres kwm de las capturas.
            await ChallengeWatcher.FightStartedAsync(stream, fight);

            // Los hechizos que NACEN con espera: el InitialCooldown de su grado. En la captura de
            // Paso de Cacería el primer jxc del combate lleva {370:1, 373:1, 32469:1}, y en la
            // base esos tres son exactamente los que tienen InitialCooldown a uno.
            var yoMismo = fight.Team0.Find(f => f.Id == me);
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
            var everyone = new List<Network.Pb>();
            foreach (var fighter in fight.Team0)
            {
                byte[] look = character != null
                    ? Managers.BreedLookTable.BuildLook(character.Breed, character.Sex,
                                                        character.HeadId, null, character.Id)
                    : Array.Empty<byte>();
                everyone.Add(Network.FightProtocol.FighterBlock(
                    fighter.CellId, FacingOf(fight, fighter), fighter.Id, FullSheetOf(fighter), look,
                    Network.FightProtocol.PlayerIdentity(character?.Breed ?? 0, fighter.Name,
                                                        character?.Sex ?? 0, fighter.Level),
                    isMonster: false));
            }
            foreach (var fighter in fight.Team1)
            {
                everyone.Add(Network.FightProtocol.FighterBlock(
                    fighter.CellId, FacingOf(fight, fighter), fighter.Id, FullSheetOf(fighter),
                    MonsterLook(fighter),
                    Network.FightProtocol.MonsterIdentity(fighter.GradeIndex + 1, fighter.MonsterId,
                                                          fighter.Level),
                    isMonster: true));
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
        private static async Task AskToConfirmAsync(NetworkStream stream, FightInstance fight)
        {
            var next = fight.CurrentFighter;
            if (next == null) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxh,
                Network.FightProtocol.BuildConfirmTurn(next.Id)));
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

            // Al monstruo no se le pone reloj: juega solo y cede el turno él mismo.
            if (quien.IsMonster || decimas <= 0) return;

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

            int duration = fighter.EsInvocado
                ? Network.FightProtocol.SummonTurnDeciseconds
                : fighter.IsMonster
                    ? Network.FightProtocol.MonsterTurnDeciseconds
                    : Network.FightProtocol.PlayerTurnDeciseconds;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzc,
                Network.FightProtocol.BuildTurnStart(fighter.Id, duration,
                                                     fight.CurrentTurnIndex, fight.RoundNumber)));

            // Los invocados a los que se les ha acabado el tiempo se deshacen aquí, al principio
            // del turno, que es cuando lo hace el servidor real: en la captura la baliza sale en
            // la ronda 28 y el "muere" llega al empezar el turno del jugador en la 30.
            foreach (var gastado in fight.InvocadosQueSeDeshacen(fight.RoundNumber))
            {
                gastado.CurrentHP = 0;
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildDeath(gastado.Id, gastado.Id)));
                Program.LogDebug($"[Combate] Se deshace el invocado {gastado.Id} " +
                                 $"(le tocaba en la ronda {gastado.MuereEnRonda}).");
                await ReenviarLaListaAsync(stream, fight);
            }

            // Se caen los embrujos cumplidos antes de devolver los puntos, para que lo que se
            // devuelva sea lo que de verdad toca esta ronda. Y de cada uno hay que avisar con su
            // jya, que es como el cliente los quita del panel: por su número, uno a uno.
            // TODOS los embrujos, no sólo los del que juega: los que el jugador le puso a un pío
            // se caen igual, y hasta ahora sólo se barría al que le empezaba el turno.
            var caducados = new List<(Fighter Quien, Jondo.Unity.World.Fights.Buff Caido)>();
            foreach (var quien in TodosLosCombatientes(fight))
            {
                foreach (var caido in quien.Buffs.Barrer(fight.RoundNumber))
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
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
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
                    if (caido.HechizoAfectado != 0 &&
                        (caido.Sobre == Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo ||
                         caido.Sobre == Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo))
                    {
                        int modificador = caido.Sobre == Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo
                            ? Network.FightProtocol.SpellMinRange
                            : Network.FightProtocol.SpellMaxRange;
                        await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Hnk,
                            Network.FightProtocol.BuildSpellModifierDeclared(
                                quien.Id, modificador, caido.HechizoAfectado)));
                    }

                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jya,
                        Network.FightProtocol.BuildBuffGone(quien.Id, caido.Numero)));

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
                        await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxw,
                            Network.FightProtocol.BuildFighterSheet(
                                quien.Id,
                                new[] { Refresco(quien, caido.Caracteristica, fight.RoundNumber) },
                                quien.Id == GameState.CharacterId)));
                    }
                }

                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), fighter.Id,
                                                           Network.FightProtocol.ActionSequence)));

                Program.LogDebug($"[Combate] Se caen {caducados.Count} embrujo(s): " +
                                 string.Join(", ", caducados.ConvertAll(c => $"{c.Caido.Numero} de {c.Quien.Id}")));
            }

            fighter.StartTurn();
            await GivePointsBackAsync(stream, fight, fighter);

            // De donde sale y con cuantos PM: es lo que hace falta para juzgar al acabar los retos
            // de posicion y el de gastar exactamente un PM. Va DESPUES de devolver los puntos.
            ChallengeWatcher.TurnStarted(fight, fighter);
            await ChallengeWatcher.EnemyTurnStartedAsync(stream, fight, fighter);
            await ChallengeWatcher.AllyTurnStartedAsync(stream, fight, fighter);

            // Y ahora las actitudes de "principio de turno": aquí es donde el Dofus Ocre mira si le
            // han pegado desde su turno anterior.
            await ActitudesAsync(stream, fight, fighter, Managers.EffectEngine.AlEmpezarElTurno);
            fighter.LeHanPegado = false;

            // El "ya puedes jugar" sólo va si el que juega es de los que maneja este cliente. En el
            // turno de un monstruo ese paso no existe.
            if (!fighter.IsMonster)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyj,
                    Network.FightProtocol.BuildYourTurn()));
            }

            Program.LogDebug($"[Combate] Turno de {fighter.Id} ({(fighter.IsMonster ? "monstruo" : "jugador")}), " +
                             $"{duration} décimas, puesto {fight.CurrentTurnIndex}.");

            // Y el reloj, con la MISMA duración que se le acaba de decir al cliente.
            ArrancarElReloj(stream, fight, fighter, duration);

            // El monstruo juega solo: no hay nadie que pulse por él. Un invocado NO pasa por la
            // inteligencia de los monstruos —no persigue ni ataca— porque lo suyo ya lo ha hecho
            // su propio hechizo en las actitudes de principio de turno: la baliza se cura o
            // empuja ahí y no tiene que hacer nada más.
            if (fighter.IsMonster && !fighter.EsInvocado)
            {
                await MonsterTurnAsync(stream, fight, fighter);
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
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(fighter.Id,
                                                         Network.FightProtocol.TurnSequence)));

            foreach (var (characteristic, value) in new[]
                     {
                         (MovementPointsCharacteristic, (long)fighter.CurrentMP),
                         (ActionPointsCharacteristic, (long)fighter.CurrentAP),
                     })
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(fighter.Id,
                                                             Network.FightProtocol.SheetSequence)));
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxw,
                    Network.FightProtocol.BuildFighterSheet(fighter.Id, new[]
                    {
                        (characteristic, value, 0L, 0L),
                    }, fighter.Id == GameState.CharacterId)));
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), fighter.Id,
                                                           Network.FightProtocol.SheetSequence)));
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), fighter.Id,
                                                       Network.FightProtocol.TurnSequence)));
        }

        /// <summary>
        /// Andar durante el combate (jrw).
        ///
        ///   servidor jto   abre la secuencia de andar
        ///            jsj   el camino entero, la orientación final y quién se mueve
        ///            jwe   f14 129, con los pasos gastados en negativo
        ///            jxw   la ficha con los puntos de movimiento que quedan
        ///            jwi   cierra, y el cliente lo acusa con un jti
        ///
        /// El cliente manda sólo las esquinas del camino; el jsj devuelve la ristra completa de
        /// casillas, que es lo que el cliente anima.
        /// </summary>
        public static async Task WalkAsync(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null || fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;

            var walker = fight.CurrentFighter;
            if (walker == null || walker.IsMonster || walker.Id != GameState.CharacterId) return;

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
            foreach (var otro in fight.Team0) if (otro.IsAlive && otro != walker) ocupadas.Add(otro.CellId);
            foreach (var otro in fight.Team1) if (otro.IsAlive && otro != walker) ocupadas.Add(otro.CellId);
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

            var path = new List<long>();
            foreach (int celda in camino) path.Add(celda);

            walker.CurrentMP -= steps;
            walker.CellId = camino[camino.Count - 1];
            destination = walker.CellId;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(walker.Id,
                                                         Network.FightProtocol.WalkSequence)));

            await WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorMoved(walker.Id, path, facing));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(walker.Id, Network.FightProtocol.Walked,
                                                  Network.FightProtocol.Spent(walker.Id, steps),
                                                  Network.FightProtocol.PointsDetail)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxw,
                Network.FightProtocol.BuildFighterSheet(walker.Id, new[]
                {
                    (MovementPointsCharacteristic, (long)walker.CurrentMP, 0L, 0L),
                }, walker.Id == GameState.CharacterId)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), walker.Id,
                                                       Network.FightProtocol.WalkSequence)));

            Program.LogDebug($"[Combate] Anda hasta la casilla {destination}: {steps} pasos, " +
                             $"le quedan {walker.CurrentMP} PM.");

            // Y lo que reaccione a andar, UNA VEZ POR CASILLA. El Centinela se come uno de alcance
            // y un dos por ciento de daños a distancia en cada paso, no en cada movimiento: andar
            // tres casillas de golpe cuesta tres, no uno.
            for (int paso = 0; paso < steps; paso++)
            {
                await EngancheAsync(stream, fight, walker, Managers.EffectEngine.AlAndar);
            }
        }

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
            if (caster == null || caster.IsMonster || caster.Id != GameState.CharacterId) return;

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

            var limites = LimitesDe(spell, caster.Level);
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

                if (lejos < minimo || lejos > maximo)
                {
                    Program.LogDebug($"[Combate] El hechizo {spell} no llega: {lejos} casillas, " +
                                     $"y su alcance es de {minimo} a {maximo}.");
                    return;
                }
            }
            if (cost <= 0) cost = DefaultCastCost;
            if (cost > caster.CurrentAP) return;

            var victim = VictimAt(fight, caster, cell);
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
            if (limites.PorTurno > 0 && esteTurno >= limites.PorTurno)
            {
                Program.LogDebug($"[Combate] El hechizo {spell} ya se ha lanzado {esteTurno} " +
                                 $"vez/veces este turno, y el tope es {limites.PorTurno}.");
                return;
            }

            // El tope por objetivo se cuenta contra el que está en la casilla apuntada. Los
            // hechizos de ZONA, que tocan a varios, cuentan aquí de menos: haría falta la lista de
            // afectados del motor, y eso todavía no está enganchado.
            caster.LanzadosPorObjetivo.TryGetValue((spell, aQuien), out int sobreEse);
            if (aQuien != 0 && limites.PorObjetivo > 0 && sobreEse >= limites.PorObjetivo)
            {
                Program.LogDebug($"[Combate] El hechizo {spell} ya se ha lanzado {sobreEse} " +
                                 $"vez/veces sobre {aQuien}, y el tope es {limites.PorObjetivo}.");
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
                ConBonos(caster, CriticoCaracteristica, caster.CriticalBonus, fight.RoundNumber);
            bool critico = TirarCritico(probabilidadCritico);
            if (critico)
            {
                Program.LogDebug($"[Combate] ¡CRÍTICO! con el hechizo {spell} " +
                                 $"({probabilidadCritico}% = {limites.CriticoPropio} del hechizo + " +
                                 $"{ConBonos(caster, CriticoCaracteristica, 0, fight.RoundNumber)} del personaje).");
            }

            caster.CurrentAP -= cost;

            caster.LanzadosEsteTurno[spell] = esteTurno + 1;
            if (aQuien != 0) caster.LanzadosPorObjetivo[(spell, aQuien)] = sobreEse + 1;

            // El Versatil (no repetir accion) y los dos de rematar antes de cambiar de objetivo.
            await ChallengeWatcher.CastAsync(stream, fight, caster, spell, victim,
                                             esteTurno + 1);
            if (limites.Intervalo > 0) caster.Recarga[spell] = limites.Intervalo;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(caster.Id,
                                                         Network.FightProtocol.ActionSequence)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(
                    caster.Id,
                    spell == 0 ? Network.FightProtocol.WeaponCast : Network.FightProtocol.Cast,
                    Network.FightProtocol.CastAt(
                        caster.Id, aQuien, cell, spell, spellLevel, critico,
                        sobreEseObjetivo: limites.PorObjetivo > 0 ? sobreEse + 1 : 0,
                        esteTurno: limites.PorTurno > 0 ? esteTurno + 1 : 0,
                        intervalo: limites.Intervalo,
                        // Sólo cuando el golpe es del arma. Un hechizo lleva el f10 a cero, igual
                        // que el puñetazo: lo que el cliente mira para poner el nombre es esto.
                        arma: spell == 0 ? ArmaEquipada(caster) : 0),
                    Network.FightProtocol.CastDetail)));

            // La ficha va en su propia secuencia, como en la captura, no suelta en medio.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(caster.Id,
                                                         Network.FightProtocol.SheetSequence)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxw,
                Network.FightProtocol.BuildFighterSheet(caster.Id, new[]
                {
                    (ActionPointsCharacteristic, (long)caster.CurrentAP, 0L, 0L),
                }, caster.Id == GameState.CharacterId)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), caster.Id,
                                                       Network.FightProtocol.SheetSequence)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildAction(caster.Id,
                                                  Network.FightProtocol.SpentActionPoints,
                                                  Network.FightProtocol.Spent(caster.Id, cost),
                                                  Network.FightProtocol.PointsDetail)));

            await HurtAsync(stream, fight, caster, spell, grade, victim, cell, critico);

            // Y lo que el hechizo deja puesto, que no es sólo daño: los PA que roba Flecha Helada,
            // sus tres turnos de daños básicos, el alcance de Disparos Lejanos...
            await AplicarEfectosAsync(stream, fight, caster, spell, grade, victim,
                                      Managers.EffectEngine.AlLanzar, cell, critico);

            int cierre = fight.SiguienteAccion();
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(cierre, caster.Id,
                                                       Network.FightProtocol.ActionSequence)));

            Program.LogDebug($"[Combate] Lanza el hechizo {spell} (grado {spellLevel}) a la casilla " +
                             $"{cell} por {cost} PA; le quedan {caster.CurrentAP}.");

            await CheckFightOverAsync(stream, fight, cierre);
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
        private static async Task InvocarAsync(NetworkStream stream, FightInstance fight,
                                               Fighter quienInvoca, int plantilla, int grado,
                                               int celdaApuntada)
        {
            var receta = Managers.Summons.De(plantilla, grado);
            if (receta == null)
            {
                Program.LogDebug($"[Combate] No hay plantilla {plantilla} grado {grado}; no se invoca.");
                return;
            }

            // Cuántas puede llevar a la vez: la característica 26, que el cliente pinta como
            // "Invocación" en el panel. Sale de la base más el equipo, como todo lo demás.
            int tope = TopeDeInvocaciones(quienInvoca, fight.RoundNumber);
            int puestas = CuantasLleva(fight, quienInvoca);
            if (tope > 0 && puestas >= tope)
            {
                Program.LogDebug($"[Combate] {quienInvoca.Id} ya lleva {puestas} invocación(es) y su " +
                                 $"tope es {tope}; no se invoca la plantilla {plantilla}.");
                return;
            }

            int celda = CasillaLibreCerca(fight, celdaApuntada >= 0 ? celdaApuntada : quienInvoca.CellId);
            if (celda < 0)
            {
                Program.LogDebug($"[Combate] No hay sitio libre para invocar la {plantilla}.");
                return;
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
                Look = receta.Look,
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
            invocado.MaxHP = Managers.Summons.VidaDelInvocado(receta.Vida, quienInvoca.Level,
                                                              receta.VidaFija);
            invocado.CurrentHP = invocado.MaxHP;

            int vive = Managers.Summons.RondasQueVive(plantilla);
            invocado.MuereEnRonda = vive > 0 ? fight.RoundNumber + vive : -1;

            // ¿Le toca turno? Sólo si su hechizo tiene algo que hacer al empezarlo. La Baliza de
            // Supervivencia lo tiene —se cura sola— y la Táctica no, que sólo reacciona a lo que
            // le pase alrededor; por eso en las capturas la primera juega y la segunda no aparece
            // ni una vez en el carrusel.
            invocado.JuegaTurno = TieneAlgoQueHacerAlEmpezar(receta.HechizoPropio,
                                                             receta.GradoDelHechizoPropio);

            fight.Invocar(invocado, quienInvoca);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildSummon(
                    quienInvoca.Id, invocado.Id, celda, FacingOf(fight, invocado),
                    receta.PlantillaDelAspecto, plantilla, grado, FullSheetOf(invocado))));

            // Y detrás, la lista de combatientes otra vez: es lo que da de alta al invocado en el
            // cliente y lo mete en el carrusel.
            await ReenviarLaListaAsync(stream, fight);

            Program.LogDebug($"[Combate] {quienInvoca.Id} invoca la plantilla {plantilla} grado " +
                             $"{grado} como {invocado.Id} en la casilla {celda} con " +
                             $"{invocado.MaxHP} de vida y el hechizo {receta.HechizoPropio}.");

            // Su hechizo pasa a ser su actitud, y se lanza en el acto para que queden puestos sus
            // enganches y su cuenta atrás.
            if (receta.HechizoPropio != 0)
            {
                invocado.Buffs.Actitudes.Add(receta.HechizoPropio);
                await AplicarEfectosAsync(stream, fight, invocado, receta.HechizoPropio,
                                          receta.GradoDelHechizoPropio,
                                          invocado, Managers.EffectEngine.AlLanzar, celda);
            }
        }

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
        private static void EngancharLoPendiente(List<Managers.Outcome> consecuencias,
                                                 int hechizo, int grado)
        {
            bool haySuspendidos = false;
            foreach (var efecto in Managers.SpellEffects.De(hechizo, grado))
            {
                foreach (var d in efecto.Disparadores())
                {
                    if (!string.Equals(d, Managers.EffectEngine.AlLanzar, StringComparison.OrdinalIgnoreCase))
                    {
                        haySuspendidos = true;
                        break;
                    }
                }
                if (haySuspendidos) break;
            }
            if (!haySuspendidos) return;

            var hasta = new Dictionary<Fighter, int>();
            foreach (var c in consecuencias)
            {
                if (c.Buff == null || c.Sobre == null) continue;
                int cuando = c.Buff.CaducaEnRonda;
                if (!hasta.TryGetValue(c.Sobre, out int ya) || cuando < 0 || (ya >= 0 && cuando > ya))
                {
                    hasta[c.Sobre] = cuando;
                }
            }

            foreach (var (quien, cuando) in hasta)
            {
                quien.Buffs.Enganchar(hechizo, grado, cuando);
            }
        }

        /// <summary>
        /// Dispara lo que un luchador tenga pendiente para este momento: los hechizos que lleva
        /// puestos y que reaccionan a lo que acaba de pasar.
        /// </summary>
        private static async Task EngancheAsync(NetworkStream stream, FightInstance fight,
                                                Fighter quien, string disparador)
        {
            if (quien == null) return;
            quien.Buffs.BarrerEnganches(fight.RoundNumber);

            foreach (var enganche in new List<Jondo.Unity.World.Fights.Buffs.ActiveSpell>(quien.Buffs.ActiveSpells))
            {
                await AplicarEfectosAsync(stream, fight, quien, enganche.Hechizo, enganche.Grado,
                                          quien, disparador, quien.CellId);
            }
        }

        /// <summary>Todos los que están en el combate, de los dos bandos.</summary>
        private static IEnumerable<Fighter> TodosLosCombatientes(FightInstance fight)
        {
            foreach (var f in fight.Team0) yield return f;
            foreach (var f in fight.Team1) yield return f;
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
            var todos = new List<long>();
            foreach (var f in fight.Team0) if (f.IsAlive) todos.Add(f.Id);
            foreach (var f in fight.Team1) if (f.IsAlive) todos.Add(f.Id);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzu,
                Network.FightProtocol.BuildTeams(todos)));
        }

        /// <summary>
        /// El primer hueco de la barra que no tenga nada, para meter ahí el cuerpo a cuerpo. En
        /// las capturas casi siempre es el cero, pero no siempre: hay jugadores que lo tienen en
        /// el 12, el 21, el 24, el 31 o el 49 porque llenaron los de delante a mano.
        /// </summary>
        private static int PrimerHuecoLibre(List<(int Slot, int Spell)> bar)
        {
            var ocupados = new HashSet<int>();
            foreach (var (slot, _) in bar) ocupados.Add(slot);
            int hueco = 0;
            while (ocupados.Contains(hueco)) hueco++;
            return hueco;
        }

        /// <summary>La característica 26, que el cliente pinta como "Invocación" en el panel.</summary>
        private const int CaracteristicaDeInvocaciones = 26;

        /// <summary>
        /// Cuántas invocaciones puede llevar uno a la vez: lo suyo de siempre más lo que le den el
        /// equipo y los embrujos. Para el jugador sale de la ficha; para un monstruo, de lo que
        /// traiga su plantilla.
        /// </summary>
        private static int TopeDeInvocaciones(Fighter quien, int ronda)
        {
            int suyo = quien.Otra(CaracteristicaDeInvocaciones);
            if (!quien.IsMonster)
            {
                suyo += StatsHandler.GetEquipBonus(CaracteristicaDeInvocaciones);
            }
            return Math.Max(0, suyo + quien.Buffs.De(CaracteristicaDeInvocaciones, ronda));
        }

        /// <summary>Las que tiene ahora mismo en el tablero.</summary>
        private static int CuantasLleva(FightInstance fight, Fighter quien)
        {
            int cuantas = 0;
            foreach (var f in TodosLosCombatientes(fight))
            {
                if (f.EsInvocado && f.IsAlive && f.Invocador == quien.Id) cuantas++;
            }
            return cuantas;
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
                                                       Fighter quien)
        {
            if (quien.Id != GameState.CharacterId) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(quien.Id,
                                                         Network.FightProtocol.SheetSequence)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxw,
                Network.FightProtocol.BuildLifeSheet(quien.Id,
                                                     quien.CurrentHP - quien.MaxHP,
                                                     quien.VidaErosionada)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                       Network.FightProtocol.SheetSequence)));
        }

        private static async Task AnunciarPuntosAsync(NetworkStream stream, Fighter quienLanza,
                                                      Fighter sobre, int efecto, int cuanto)
        {
            if (cuanto >= 0) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildPointsLost(quienLanza.Id, efecto, sobre.Id, cuanto)));
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
                if (uno.CellId == cell && uno.IsAlive) return uno;
            }
            return null;
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
        private static async Task AplicarEfectosAsync(NetworkStream stream, FightInstance fight,
                                                      Fighter quienLanza, int hechizo, int grado,
                                                      Fighter objetivo, string disparador,
                                                      int celdaApuntada = -1, bool critico = false)
        {
            if (hechizo == 0) return;

            List<Managers.Outcome> consecuencias;
            try
            {
                consecuencias = Managers.EffectEngine.Resolver(fight, quienLanza, hechizo, grado,
                                                                 objetivo, disparador, fight.RoundNumber,
                                                                 hondo: 0,
                                                                 celdaApuntada: celdaApuntada,
                                                                 critico: critico);
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
            if (string.Equals(disparador, Managers.EffectEngine.AlLanzar, StringComparison.OrdinalIgnoreCase))
            {
                EngancharLoPendiente(consecuencias, hechizo, grado);
            }

            var fichas = new HashSet<(long Quien, int Caracteristica)>();

            foreach (var c in consecuencias)
            {
                if (c.Caracteristica == ActionPointsCharacteristic)
                {
                    // TRAZA para medir la retirada de PA. Se apunta de qué a qué, con qué efecto
                    // y cuántos embrujos de PA lleva encima el afectado: si el número que se ve en
                    // pantalla no cuadra con éste, el desajuste lo pone el cliente y no nosotros.
                    int antes = c.Sobre.CurrentAP;
                    c.Sobre.CurrentAP = Math.Max(0, c.Sobre.CurrentAP + c.Cuanto);
                    Program.LogDebug($"[PUNTOS] PA de {c.Sobre.Id}: {antes} -> {c.Sobre.CurrentAP} " +
                                     $"({c.Cuanto:+#;-#;0}) por el efecto {c.Efecto.EffectId} del " +
                                     $"hechizo {hechizo}; tope {c.Sobre.MaxAP}, embrujos de PA " +
                                     $"encima: {c.Sobre.Buffs.De(ActionPointsCharacteristic, fight.RoundNumber)}");

                    fichas.Add((c.Sobre.Id, ActionPointsCharacteristic));
                    await AnunciarPuntosAsync(stream, quienLanza, c.Sobre,
                                              Network.FightProtocol.ActionPointsLost, c.Cuanto);
                }
                else if (c.Caracteristica == MovementPointsCharacteristic)
                {
                    int antes = c.Sobre.CurrentMP;
                    c.Sobre.CurrentMP = Math.Max(0, c.Sobre.CurrentMP + c.Cuanto);
                    Program.LogDebug($"[PUNTOS] PM de {c.Sobre.Id}: {antes} -> {c.Sobre.CurrentMP} " +
                                     $"({c.Cuanto:+#;-#;0}) por el efecto {c.Efecto.EffectId} del " +
                                     $"hechizo {hechizo}; tope {c.Sobre.MaxMP}, embrujos de PM " +
                                     $"encima: {c.Sobre.Buffs.De(MovementPointsCharacteristic, fight.RoundNumber)}");

                    fichas.Add((c.Sobre.Id, MovementPointsCharacteristic));
                    await AnunciarPuntosAsync(stream, quienLanza, c.Sobre,
                                              Network.FightProtocol.MovementPointsLost, c.Cuanto);
                }
                else if (c.Caracteristica != 0)
                {
                    // Y CUALQUIER OTRA caracteristica que el embrujo haya movido: la potencia, el
                    // alcance, los daños… Aqui no se tocaba nada, asi que un "+250 de potencia" se
                    // apuntaba en el motor -y el daño subia de verdad- pero el panel del cliente
                    // seguia enseñando el numero de antes, y parecia que el embrujo no hacia nada.
                    fichas.Add((c.Sobre.Id, c.Caracteristica));
                }

                // Y si era un ROBO, lo que se le ha quitado a uno se le da al otro.
                if (c.LeDaAlLanzador > 0 && quienLanza != c.Sobre)
                {
                    if (c.Caracteristica == ActionPointsCharacteristic)
                    {
                        quienLanza.CurrentAP += c.LeDaAlLanzador;
                        fichas.Add((quienLanza.Id, ActionPointsCharacteristic));
                    }
                    else if (c.Caracteristica == MovementPointsCharacteristic)
                    {
                        quienLanza.CurrentMP += c.LeDaAlLanzador;
                        fichas.Add((quienLanza.Id, MovementPointsCharacteristic));
                    }
                }

                // Las curaciones.
                if (c.Cura > 0)
                {
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildHeal(quienLanza.Id, c.Cura, c.Sobre.Id)));
                    Program.LogDebug($"[Combate] {quienLanza.Id} cura {c.Cura} a {c.Sobre.Id}; " +
                                     $"queda en {c.Sobre.CurrentHP}/{c.Sobre.MaxHP}.");

                    // El Sin Corazon: curarse uno mismo vale, que le curen a uno no.
                    await ChallengeWatcher.HealedAsync(stream, fight, quienLanza, c.Sobre);

                    // La cura tambien mueve la vida que le falta al jugador.
                    await RefrescarLaVidaAsync(stream, fight, c.Sobre);
                    continue;
                }

                // Los que sacan un bicho al tablero.
                if (c.Invoca != 0)
                {
                    await InvocarAsync(stream, fight, quienLanza, c.Invoca, grado, celdaApuntada);
                    continue;
                }

                // Los que mueven a alguien de sitio: se anuncia adónde ha ido a parar.
                if (c.Mueve)
                {
                    // Por el cable, un desplazamiento viaja SIEMPRE como el 5 —alejarse— o el 6
                    // —acercarse—, aunque el efecto que lo provoque sea otro. Medido en Tiro de
                    // Repliegue, cuyo efecto es el 1041 ("Retrocede") y cuyo paquete lleva f14 = 5.
                    int comoViaja = Jondo.Unity.World.Maps.Zone.SeAleja(
                        c.CasillaDesde, c.CasillaHasta, celdaApuntada, quienLanza.CellId)
                        ? Network.FightProtocol.Alejarse
                        : Network.FightProtocol.Acercarse;

                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildDisplacement(
                            quienLanza.Id, comoViaja, c.Sobre.Id,
                            c.CasillaDesde, c.CasillaHasta)));
                    Program.LogDebug($"[Combate] El hechizo {hechizo} mueve a {c.Sobre.Id} " +
                                     $"de la casilla {c.CasillaDesde} a la {c.CasillaHasta}.");

                    await DanoDeColisionAsync(stream, fight, quienLanza, c);
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

                if (c.Buff == null) continue;

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

                foreach (var d in c.Efecto.Disparadores())
                {
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxm,
                        Network.FightProtocol.BuildBuff(
                            c.Sobre.Id, quienLanza.Id, c.Buff.Numero, c.Efecto.EffectId,
                            c.Efecto.EffectUid, c.Efecto.Value, c.Efecto.DiceNum, c.Efecto.DiceSide,
                            c.HechizoOrigen, d, rondas, c.Efecto.Dispellable, familia,
                            c.NivelOrigen, critico)));
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

            foreach (var (quien, caracteristica) in fichas)
            {
                var ficha = fight.Team0.Find(f => f.Id == quien) ?? fight.Team1.Find(f => f.Id == quien);
                if (ficha == null) continue;
                var refresco = Refresco(ficha, caracteristica, fight.RoundNumber);

                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(quien, Network.FightProtocol.SheetSequence)));
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxw,
                    Network.FightProtocol.BuildFighterSheet(quien, new[] { refresco },
                                                            quien == GameState.CharacterId)));
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                    Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien,
                                                           Network.FightProtocol.SheetSequence)));
            }

            await AnunciarElAlcanceAsync(stream, fight, consecuencias);
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
            // Quién y qué hechizo han quedado tocados. Se junta primero para no mandar dos veces
            // lo mismo cuando un hechizo lleva el mínimo y el máximo a la vez.
            var tocados = new List<(Fighter Quien, int Hechizo)>();
            foreach (var c in consecuencias)
            {
                var buff = c.Buff;
                if (buff == null || buff.HechizoAfectado == 0) continue;
                if (buff.Sobre != Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo &&
                    buff.Sobre != Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo) continue;
                if (tocados.Exists(t => t.Quien.Id == c.Sobre.Id && t.Hechizo == buff.HechizoAfectado)) continue;
                tocados.Add((c.Sobre, buff.HechizoAfectado));
            }
            if (tocados.Count == 0) return;

            // Primero todos los valores y después todas las declaraciones, que es el orden de la
            // captura: la ráfaga de hnd va junta y la de hnk detrás.
            foreach (var (quien, hechizo) in tocados)
            {
                int minimo = quien.Buffs.DelHechizo(hechizo, Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo,
                                                    fight.RoundNumber);
                int maximo = quien.Buffs.DelHechizo(hechizo, Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo,
                                                    fight.RoundNumber);

                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Hnd,
                    Network.FightProtocol.BuildSpellModifier(
                        quien.Id, Network.FightProtocol.SpellMinRange, hechizo, minimo)));
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Hnd,
                    Network.FightProtocol.BuildSpellModifier(
                        quien.Id, Network.FightProtocol.SpellMaxRange, hechizo, maximo)));
            }

            Program.LogDebug($"[ALCANCE] Anunciado el alcance de {tocados.Count} hechizo(s) " +
                             "con hnd y hnk.");
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
            foreach (var quien in fight.Team0)
            {
                foreach (int actitud in quien.Buffs.Actitudes)
                {
                    const int grado = Managers.EffectEngine.GradoDelEnganche;
                    var (_, nivelId, _) = Managers.SpellEffects.GradoDe(actitud, quien.Level);

                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                        Network.FightProtocol.BuildSequenceStart(quien.Id,
                                                                 Network.FightProtocol.ActionSequence)));

                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildAction(
                            quien.Id, Network.FightProtocol.Cast,
                            Network.FightProtocol.CastAt(quien.Id, quien.Id, quien.CellId, actitud,
                                                         nivelId, critical: false),
                            Network.FightProtocol.CastDetail)));

                    await AplicarEfectosAsync(stream, fight, quien, actitud, grado, quien,
                                              Managers.EffectEngine.AlLanzar);

                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                        Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), quien.Id,
                                                               Network.FightProtocol.ActionSequence)));
                }

                if (quien.Buffs.Actitudes.Count > 0)
                {
                    Program.LogDebug($"[Combate] Cascada de {quien.Buffs.Actitudes.Count} " +
                                     $"actitud(es) de {quien.Id} antes del primer turno.");
                }
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
            foreach (int actitud in quien.Buffs.Actitudes)
            {
                // El enganche de una actitud está SIEMPRE en su grado uno, no en el más alto que el
                // personaje tenga abierto. Los tres grados del Amarillo Ocre son de nivel mínimo 1,
                // así que preguntar por el grado del personaje devolvía el 3 —el que da el punto de
                // acción— y allí no hay ningún disparador de principio de turno, con lo que la
                // actitud no hacía nada. Los grados de dentro los dice el propio enganche.
                const int grado = Managers.EffectEngine.GradoDelEnganche;
                await AplicarEfectosAsync(stream, fight, quien, actitud, grado, quien, disparador);

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
                                              quien, disparador);
                }
            }
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
        private static async Task HurtAsync(NetworkStream stream, FightInstance fight,
                                            Fighter caster, int spell, int grade, Fighter target,
                                            int celdaApuntada = -1, bool critico = false)
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
                ? Managers.EffectEngine.Golpes(fight, caster, spell, grade, target, celdaApuntada, critico)
                : (target != null ? GolpeDelArma(caster, target)
                                  : new List<(Managers.SpellEffect, int, Fighter, int)>());
            if (golpes.Count == 0) return;

            // El dado se tira UNA VEZ por efecto, no una por afectado: si un hechizo de "25 a 30"
            // saca un 26, en el centro de la zona entran 26 y a los de alrededor les entra ese
            // mismo 26 ya rebajado por la distancia. Tirando por cabeza, dos bichos pegados al
            // centro recibirían números distintos y el jugador vería una zona que no cuadra.
            var tirada = new Dictionary<int, int>();

            foreach (var (efecto, elementoDelGolpe, aQuien, lejos) in golpes)
            {
                if (!tirada.TryGetValue(efecto.EffectUid, out int sacado))
                {
                    sacado = TirarElDado(efecto);
                    tirada[efecto.EffectUid] = sacado;
                }
                await UnGolpeAsync(stream, fight, caster, spell, efecto, elementoDelGolpe, aQuien,
                                   sacado, lejos, critico);
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

        private static async Task UnGolpeAsync(NetworkStream stream, FightInstance fight,
                                               Fighter caster, int spell,
                                               Managers.SpellEffect efecto, int elemento,
                                               Fighter target, int sacadoDelDado, int lejosDelCentro,
                                               bool critical)
        {
            // El elemento lo dice el catálogo: 0 neutral, 1 tierra, 2 fuego, 3 agua, 4 aire.
            var element = elemento switch
            {
                1 => Jondo.Unity.World.Fights.ElementType.Earth,
                2 => Jondo.Unity.World.Fights.ElementType.Fire,
                3 => Jondo.Unity.World.Fights.ElementType.Water,
                4 => Jondo.Unity.World.Fights.ElementType.Air,
                _ => Jondo.Unity.World.Fights.ElementType.Neutral,
            };

            // Lo que ha salido del dado, tirado una vez para todo el lanzamiento.
            int baseDamage = sacadoDelDado;

            // Salvo los que pegan EN FUNCIÓN de lo que el objetivo lleve erosionado: ahí el dado
            // no es el daño, es el TANTO POR CIENTO. Represalias lleva el efecto 1092, "daños
            // neutrales: 20% de los PdV erosionados del objetivo", y contra alguien intacto no
            // hace nada; contra uno al que se le han comido 300 de tope, hace 60.
            if (Managers.EffectEngine.PegaSegunLoErosionado(efecto.EffectId))
            {
                baseDamage = target.VidaErosionada * sacadoDelDado / 100;
                Program.LogDebug($"[Combate] El efecto {efecto.EffectId} pega el {sacadoDelDado}% de " +
                                 $"los {target.VidaErosionada} erosionados de {target.Id}: {baseDamage}.");
            }

            // Y lo que le hayan sumado a ESE hechizo por embrujo: el efecto 293, "+#3 de daños
            // básicos". Flecha Helada se lo pone a sí misma, así que la segunda vez que se lanza
            // pega más que la primera.
            int deEmbrujo = caster.Buffs.DelHechizo(spell, Jondo.Unity.World.Fights.SpellAspect.DanoBase, fight.RoundNumber);
            if (deEmbrujo != 0)
            {
                baseDamage += deEmbrujo;
                Program.LogDebug($"[Combate] El hechizo {spell} lleva {deEmbrujo:+#;-#;0} de daños " +
                                 $"básicos por embrujo: base {baseDamage}.");
            }

            // Los daños fijos van al FINAL, sin multiplicar por la característica ni por la
            // potencia: los generales de la característica 16 más los del elemento con el que se
            // pega (88 a 92), y si el golpe sale crítico, además los daños críticos (86).
            int flat = ConBonos(caster, DanoFijoCaracteristica, caster.FlatDamage, fight.RoundNumber)
                     + (critical ? ConBonos(caster, DanoCriticoCaracteristica, caster.CriticalDamage, fight.RoundNumber) : 0);

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
            int elementoDelPersonaje = ConBonos(caster, CaracteristicaDelElemento(element),
                                                caster.GetStatForElement(element), fight.RoundNumber);
            int potencia = ConBonos(caster, PotenciaCaracteristica, caster.Power, fight.RoundNumber);

            int damage = Jondo.Unity.World.Fights.DamageCalculator.CalculateDamage(
                baseDamage: baseDamage,
                element: element,
                statValue: elementoDelPersonaje,
                power: potencia,
                flatElementDamage: caster.GetFlatDamageForElement(element),
                flatDamage: flat,
                targetResPct: target.GetResPctForElement(element),
                targetFlatRes: 0);

            // Los MULTIPLICADORES de quien lo recibe: "daños sufridos x110%" es el efecto 1163, el
            // que pone Represalias. Van al final, sobre el daño ya calculado.
            int multiplica = target.Buffs.Multiplicador(DanoSufridoPorCiento, fight.RoundNumber);
            if (multiplica != 100)
            {
                int antes = damage;
                damage = (int)Math.Round(damage * multiplica / 100.0);
                Program.LogDebug($"[Combate] {target.Id} sufre los daños al {multiplica}%: " +
                                 $"{antes} pasa a {damage}.");
            }

            // Lo que se ANUNCIA nunca puede pasar de la vida que le queda. Si a un pío de setenta
            // le entran doscientos, el golpe que ve el jugador es de setenta: por encima de eso no
            // hay vida que quitar, y el número que sobra sólo confunde.
            int aplicado = Math.Min(damage, target.CurrentHP);
            target.TakeDamage(aplicado);

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
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildDamage(caster.Id, efecto.EffectId,
                                                  target.Id, aplicado, elemento, erosionado)));

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
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildHeal(caster.Id, curado, caster.Id)));
                    await RefrescarLaVidaAsync(stream, fight, caster);
                    Program.LogDebug($"[Combate] Robo de vida del efecto {efecto.EffectId}: " +
                                     $"{caster.Id} se cura {curado} de los {aplicado} quitados; " +
                                     $"se queda en {caster.CurrentHP}/{caster.MaxHP}.");
                }
            }

            // Y al jugador, la vida que le queda. Sin esto le pegaban toda la pelea y su barra
            // seguia llena: el cliente no descuenta la suya de los golpes, la lee de la ficha.
            await RefrescarLaVidaAsync(stream, fight, target);

            if (target.LeHanPegado)
            {
                await ActitudesAsync(stream, fight, target, Managers.EffectEngine.CuandoMePegan);
            }

            Program.LogDebug($"[Combate] {aplicado} de daño a {target.Id} (calculado {damage}); " +
                             $"le quedan {target.CurrentHP}.");

            // La muerte, LA ÚLTIMA. Y sin mandar antes una ficha con la vida a cero: el servidor
            // real no la manda, la vida la descuenta el cliente del golpe de arriba, y mandarla
            // hacía que el bicho se cayera muerto antes de que se viera la animación.
            if (!target.IsAlive)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
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
            }
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
                                       c.CollisionDamageToBlocker);
            }
        }

        /// <summary>Un solo golpe de colisión, contra uno solo.</summary>
        private static async Task UnEstampadoAsync(NetworkStream stream, FightInstance fight,
                                                   Fighter quienEmpuja, Fighter quien, int dano)
        {
            if (quien == null || !quien.IsAlive || dano <= 0) return;

            // Lo que se anuncia nunca puede pasar de la vida que le queda, igual que en un golpe
            // normal: por encima de eso no hay vida que quitar.
            int aplicado = Math.Min(dano, quien.CurrentHP);
            quien.TakeDamage(aplicado);

            // La erosión se calcula sobre el daño ENTERO, no sobre el recortado. Medido: en el
            // koliseo hay un golpe de 663 anunciado como 417 —recortado por la vida— y con la
            // erosión en 66, que es la décima parte de 663 y no de 417.
            int porciento = quien.Otra(Fighter.CaracteristicaDeErosion) +
                            quien.Buffs.De(Fighter.CaracteristicaDeErosion, fight.RoundNumber);
            int erosionado = quien.Erosionar(dano, porciento);

            await ChallengeWatcher.DamagedAsync(stream, fight, quien, aplicado, quienEmpuja, -1);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildPushDamage(quienEmpuja.Id, quien.Id, aplicado, erosionado)));

            if (aplicado > 0 && quienEmpuja.TeamId != quien.TeamId) quien.LeHanPegado = true;

            await RefrescarLaVidaAsync(stream, fight, quien);

            Program.LogDebug($"[Combate] {quien.Id} se estampa al ser empujado: {aplicado} de daño " +
                             $"(calculado {dano}, erosión {erosionado}); le quedan {quien.CurrentHP}.");

            if (quien.LeHanPegado)
            {
                await ActitudesAsync(stream, fight, quien, Managers.EffectEngine.CuandoMePegan);
            }

            if (quien.IsAlive) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildDeath(quienEmpuja.Id, quien.Id)));
            Program.LogDebug($"[Combate] {quien.Id} se queda sin vida por el golpe del empujón.");

            await ChallengeWatcher.DiedAsync(stream, fight, quien, false, quienEmpuja);
            await ChallengeWatcher.AllyDiedAsync(stream, fight, quien);
            await CaenSusInvocadosAsync(stream, fight, quien);
            await ReenviarLaListaAsync(stream, fight);
        }

        /// <summary>
        /// Al que se muere se le caen TODAS sus invocaciones, en el acto.
        ///
        /// No es que dejen de contar para el final del combate: es que desaparecen. Una baliza no
        /// sobrevive a su Ocra ni llega a jugar el turno que tuviera pendiente.
        /// </summary>
        private static async Task CaenSusInvocadosAsync(NetworkStream stream, FightInstance fight,
                                                        Fighter muerto)
        {
            if (muerto == null || muerto.EsInvocado) return;

            var suyos = new List<Fighter>();
            foreach (var f in fight.Team0) if (f.EsInvocado && f.IsAlive && f.Invocador == muerto.Id) suyos.Add(f);
            foreach (var f in fight.Team1) if (f.EsInvocado && f.IsAlive && f.Invocador == muerto.Id) suyos.Add(f);
            if (suyos.Count == 0) return;

            foreach (var invocado in suyos)
            {
                invocado.CurrentHP = 0;
                invocado.MuereEnRonda = -1;
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
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
        private static async Task MonsterTurnAsync(NetworkStream stream, FightInstance fight,
                                                   Fighter monster)
        {
            var enemies = monster.TeamId == 0 ? fight.Team1 : fight.Team0;
            Fighter? prey = null;
            int best = int.MaxValue;
            foreach (var enemy in enemies)
            {
                if (!enemy.IsAlive) continue;
                int distance = CellDistance(monster.CellId, enemy.CellId);
                if (distance < best) { best = distance; prey = enemy; }
            }

            // A QUÉ DISTANCIA LE CONVIENE PONERSE, que no es «al lado» siempre.
            //
            // Antes se pegaba: allowed = Min(CurrentMP, best - 1). Y pegado no se pueden lanzar los
            // 857 hechizos de monstruo (11,3 % del arsenal) que tienen alcance mínimo mayor que
            // uno. El bicho gastaba los puntos de movimiento en meterse justo donde no podía hacer
            // nada, y encima ya no le quedaban para volver a separarse.
            int deseada = prey != null ? DistanciaQueLeConviene(monster, best) : 1;

            if (prey != null && deseada != best && monster.CurrentMP > 0)
            {
                var walked = PathToward(fight, monster.CellId, prey.CellId, monster.CurrentMP, deseada);
                if (walked.Count > 1)
                {
                    var path = walked.ConvertAll(c => (long)c);
                    int steps = walked.Count - 1;
                    int destination = walked[walked.Count - 1];
                    monster.CurrentMP -= steps;
                    monster.CellId = destination;

                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                        Network.FightProtocol.BuildSequenceStart(monster.Id,
                                                                 Network.FightProtocol.WalkSequence)));
                    await WriteFrameAsync(stream,
                        ConnectionProtocol.BuildActorMoved(monster.Id, path, FacingOf(fight, monster)));
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildAction(monster.Id, Network.FightProtocol.Walked,
                                                          Network.FightProtocol.Spent(monster.Id, steps),
                                                          Network.FightProtocol.PointsDetail)));
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                        Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), monster.Id,
                                                               Network.FightProtocol.WalkSequence)));
                    best = CellDistance(monster.CellId, prey.CellId);
                }
            }

            // Y a pegar con lo que tenga, mientras le lleguen los puntos de acción.
            int lanzados = 0;
            foreach (int spell in monster.SpellIds)
            {
                if (prey == null || !prey.IsAlive) break;

                // El grado que el monstruo tiene de ese hechizo, que viene en su ficha.
                int monsterGrade = monster.SpellGrades.TryGetValue(spell, out int g) ? g : 1;

                var data = DatabaseManager.GetSpellCombatData(spell, monsterGrade);
                if (data == null) continue;
                if (data.APCost <= 0) continue;

                // CUÁNTAS VECES SE PUEDE LANZAR. El cero quiere decir «sin límite», y entonces
                // manda lo que dé el bolsillo. Lanzando uno solo por hechizo, que es lo que se
                // hacía, los monstruos dejaban sin gastar 19.469 de sus 47.148 puntos de acción
                // (41,3 %): el 72,3 % de su arsenal admite repetición.
                int tope = data.MaxCastPerTurn > 0 ? data.MaxCastPerTurn : int.MaxValue;

                for (int vez = 0; vez < tope; vez++)
                {
                    if (data.APCost > monster.CurrentAP) break;
                    if (lanzados >= TopeDeLanzamientosPorTurno) break;
                    if (!prey.IsAlive) break;

                    // A QUIÉN APUNTA ESTE HECHIZO. No tiene por qué ser la presa, y apuntarlo todo
                    // a ella es lo que hacía que un jalamutín te lanzara su curación a la cara.
                    var objetivo = ObjetivoDe(fight, monster, spell, monsterGrade, prey, data);
                    if (objetivo == null || !objetivo.IsAlive) break;

                    var rechazo = SpellCastRules.Check(
                        monster, spell, objetivo.Id, monster.CellId, objetivo.CellId,
                        data.MaxCastPerTurn, data.MaxCastPerTarget, data.MinCastInterval,
                        data.CastInLine);
                    if (rechazo != SpellCastRules.Rejection.None)
                    {
                        Program.LogDebug($"[Combate] El monstruo {monster.Id} no lanza el hechizo " +
                                         $"{spell} sobre {objetivo.Id}: {rechazo}.");
                        break;
                    }

                    // Y el alcance se mide contra el objetivo ELEGIDO, no contra la presa. Ésa era
                    // la línea que dejaba muertos los hechizos de alcance cero.
                    int lejos = CellDistance(monster.CellId, objetivo.CellId);
                    if (lejos < data.MinRange || lejos > data.MaxRange) break;

                    // Y LA LINEA DE VISION, que el turno del monstruo no miraba.
                    //
                    // El lanzamiento del jugador si la comprueba desde siempre; aqui no, y hasta
                    // ahora casi no se notaba porque el bicho se pegaba al lado y de al lado se ve
                    // todo. Desde que se queda a la distancia que le conviene y repite hechizo, se
                    // notaria: la piden 3.643 de los 8.093 pares hechizo-grado de monstruo (45,0 %),
                    // y sin ella dispararian a traves de los muros.
                    if (data.NeedsLineOfSight &&
                        !MapGeometry.HasLineOfSight(monster.CellId, objetivo.CellId,
                                                    MapManager.GetLosBlockers(fight.ArenaMapId)))
                    {
                        break;
                    }

                    monster.CurrentAP -= data.APCost;
                    lanzados++;
                    SpellCastRules.Register(monster, spell, objetivo.Id, data.MinCastInterval);

                    // Y su identificador, para que su lanzamiento también diga QUÉ se lanza.
                    int spellLevel = data.SpellLevelId;

                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                        Network.FightProtocol.BuildSequenceStart(monster.Id,
                                                                 Network.FightProtocol.ActionSequence)));
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildAction(
                            monster.Id, Network.FightProtocol.Cast,
                            Network.FightProtocol.CastAt(monster.Id, objetivo.Id, objetivo.CellId,
                                                         spell, spellLevel, critical: false),
                            Network.FightProtocol.CastDetail)));
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildAction(monster.Id,
                                                          Network.FightProtocol.SpentActionPoints,
                                                          Network.FightProtocol.Spent(monster.Id, data.APCost),
                                                          Network.FightProtocol.PointsDetail)));

                    await HurtAsync(stream, fight, monster, spell, monsterGrade, objetivo,
                                    objetivo.CellId);

                    // Y sus efectos, igual que cuando lanza el jugador. Esto faltaba: el turno del
                    // monstruo sólo calculaba daño, así que los malus que dejan sus hechizos —el
                    // alcance que quita el Picoteo, por ejemplo— no se aplicaban ni se anunciaban, y
                    // en el panel del jugador no aparecía nunca nada puesto por un bicho.
                    await AplicarEfectosAsync(stream, fight, monster, spell, monsterGrade, objetivo,
                                              Managers.EffectEngine.AlLanzar, objetivo.CellId);

                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
                        Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), monster.Id,
                                                               Network.FightProtocol.ActionSequence)));
                }
            }

            if (await CheckFightOverAsync(stream, fight)) return;

            // Y cede el turno solo, que el monstruo no tiene quien pulse por él.
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
        /// A qué distancia de la presa le conviene ponerse al monstruo: la banda del hechizo
        /// ofensivo más lejano que pueda pagar.
        ///
        /// Si ya está dentro de una banda no se mueve, que acercarse cuesta puntos. Y si está
        /// DEMASIADO CERCA para lo que quiere lanzar, la distancia que sale es mayor que la de
        /// ahora y el camino lo aleja: es lo que hace falta para los hechizos de alcance mínimo
        /// grande, que pegado no se pueden lanzar.
        /// </summary>
        private static int DistanciaQueLeConviene(Fighter monster, int ahora)
        {
            int deseada = -1;
            foreach (int spell in monster.SpellIds)
            {
                int grado = monster.SpellGrades.TryGetValue(spell, out int g) ? g : 1;
                var data = DatabaseManager.GetSpellCombatData(spell, grado);
                if (data == null || data.APCost <= 0 || data.APCost > monster.CurrentAP) continue;

                // Los de alcance cero se lanzan encima de uno mismo: no piden acercarse a nada.
                if (data.MaxRange <= 0) continue;
                if (!EsOfensivo(spell, grado)) continue;

                int cabe = Math.Min(data.MaxRange, ahora);
                if (cabe < data.MinRange) cabe = data.MinRange;
                if (cabe > deseada) deseada = cabe;
            }

            // Sin nada ofensivo que pagar, lo de siempre: ponerse al lado.
            return deseada > 0 ? deseada : 1;
        }

        /// <summary>¿Este hechizo le hace algo a los de enfrente? Lo dice la máscara de sus efectos.</summary>
        private static bool EsOfensivo(int hechizo, int grado)
        {
            foreach (var efecto in Managers.SpellEffects.De(hechizo, grado))
            {
                foreach (var trozo in (efecto.TargetMask ?? "").Split(','))
                {
                    if (trozo.Trim().TrimStart('*') == "A") return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A quién apunta un hechizo de monstruo.
        ///
        /// Se recorría la lista de hechizos apuntándolo TODO a la presa, y de ahí salían dos cosas
        /// que se ven jugando: que un jalamutín te lance su curación a la cara —el hechizo 2226
        /// «Esmuto», cuyo único efecto lleva máscara «g»— y que un bicho gaste el turno en bufos
        /// en vez de en atacar.
        ///
        /// La máscara la sabe leer EffectEngine.AQuien de sobra; aquí sólo hace falta saber HACIA
        /// DÓNDE se lanza, que es cosa distinta y anterior: el anuncio que va por el cable lleva la
        /// casilla apuntada, y el cliente pinta la animación hacia ella aunque luego el efecto no
        /// le toque a nadie. Por eso la curación «se veía» aunque no curara.
        /// </summary>
        private static Fighter ObjetivoDe(FightInstance fight, Fighter monster, int hechizo,
                                          int grado, Fighter presa,
                                          SpellCombatData data)
        {
            // ALCANCE CERO: se lanza sobre uno mismo. Son 1.555 hechizos de monstruo, el 20,4 % del
            // arsenal, y hasta ahora NO SE LANZABA NI UNO, porque el alcance se comprobaba contra la
            // presa y la distancia a la presa nunca vale cero: el bicho no se le sube encima.
            //
            // Y no son autobufos: 443 de esos 1.555 llevan daño, o sea que son hechizos de ZONA
            // centrados en quien los lanza. Ésta es la causa principal de que 1.280 monstruos de
            // 5.134 —el 24,9 %— no le hicieran absolutamente nada al jugador teniéndolo al lado.
            if (data.MaxRange <= 0) return monster;

            bool aEnemigos = false, aLosSuyos = false, alLanzador = false;
            foreach (var efecto in Managers.SpellEffects.De(hechizo, grado))
            {
                foreach (var trozo in (efecto.TargetMask ?? "").Split(','))
                {
                    string t = trozo.Trim().TrimStart('*');
                    if (t == "A") aEnemigos = true;
                    else if (t == "a" || t == "g") aLosSuyos = true;
                    else if (t == "C") alLanzador = true;
                }
            }

            // Si toca a los de enfrente va a la presa, aunque además toque a los suyos: el Ojo de
            // Topo se lleva por delante a los aliados que pille dentro y aun así se lanza al enemigo.
            if (aEnemigos) return presa;
            if (aLosSuyos) return MasHeridoDeLosSuyos(fight, monster) ?? monster;
            if (alLanzador) return monster;
            return presa;
        }

        /// <summary>El de su bando al que más vida le falta, que es a quien se cura.</summary>
        private static Fighter MasHeridoDeLosSuyos(FightInstance fight, Fighter monster)
        {
            var suyos = monster.TeamId == 0 ? fight.Team0 : fight.Team1;
            Fighter peor = null;
            int falta = 0;
            foreach (var f in suyos)
            {
                if (f == null || !f.IsAlive) continue;
                int suya = f.MaxHP - f.CurrentHP;
                if (suya > falta) { falta = suya; peor = f; }
            }
            return peor;
        }

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
        /// El camino hacia la presa, casilla a casilla y SIN DIAGONALES.
        ///
        /// En Dofus no se anda en diagonal: desde una casilla sólo se pasa a las cuatro que tiene
        /// pegadas, que en coordenadas de rombo son las que están a distancia uno. Antes se elegía
        /// directamente la mejor casilla dentro del alcance y el monstruo aparecía cruzando el
        /// tablero de esquina a esquina, que es un movimiento que el juego no permite.
        ///
        /// Devuelve la ristra de casillas, empezando por la de salida.
        /// </summary>
        /// <param name="deseada">
        /// A qué distancia del destino hay que quedarse. Uno es «pegado», que es lo de siempre,
        /// pero un hechizo de alcance mínimo tres quiere quedarse a tres, y entonces esto también
        /// sirve para ALEJARSE si el bicho está demasiado cerca.
        /// </param>
        private static List<int> PathToward(FightInstance fight, int from, int to, int steps,
                                            int deseada = 1)
        {
            var camino = new List<int> { from };
            if (steps <= 0) return camino;

            // BÚSQUEDA EN ANCHURA, y no el descenso avaro de antes.
            //
            // Lo de antes daba un paso a la vecina que más acortara, y si NINGUNA acortaba se
            // paraba. Con eso, un bicho cuya única vecina buena no fuera pisable se quedaba
            // clavado: medido en un combate real, el Jalamut Real pasó siete turnos entero en la
            // casilla 326 sin gastar uno solo de sus 105 puntos de acción, porque la única vecina
            // que acortaba —la 313— no es pisable en combate. La anchura rodea el obstáculo.
            //
            // Cuesta cuatro vecinas por casilla y tantos niveles como puntos de movimiento, o sea
            // nada: un monstruo se mueve seis casillas en el mejor de los casos.
            var deDonde = new Dictionary<int, int> { [from] = from };
            var pasos = new Dictionary<int, int> { [from] = 0 };
            var cola = new Queue<int>();
            cola.Enqueue(from);

            int mejor = from;
            int mejorFallo = Math.Abs(CellDistance(from, to) - deseada);

            while (cola.Count > 0)
            {
                int aqui = cola.Dequeue();
                int paso = pasos[aqui];
                if (paso >= steps) continue;

                foreach (int vecina in Neighbours(aqui))
                {
                    if (pasos.ContainsKey(vecina)) continue;
                    if (vecina == to) continue;                        // no se le pisa encima
                    if (!PisableEnCombate(fight, vecina)) continue;
                    if (Occupied(fight, vecina)) continue;

                    pasos[vecina] = paso + 1;
                    deDonde[vecina] = aqui;
                    cola.Enqueue(vecina);

                    // La mejor casilla es la que deja al bicho más cerca de la distancia que
                    // quiere, y como la anchura las visita en orden de pasos, la primera que
                    // empata es también la que menos puntos de movimiento cuesta.
                    int fallo = Math.Abs(CellDistance(vecina, to) - deseada);
                    if (fallo < mejorFallo) { mejorFallo = fallo; mejor = vecina; }
                }
            }

            if (mejor == from) return camino;

            var alReves = new List<int>();
            for (int c = mejor; c != from; c = deDonde[c]) alReves.Add(c);
            alReves.Reverse();
            camino.AddRange(alReves);
            return camino;
        }

        /// <summary>Las cuatro casillas pegadas a una, que son las que están a distancia uno.</summary>
        private static IEnumerable<int> Neighbours(int cell) => MapGeometry.GetNeighbors(cell);

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
            foreach (var f in fight.Team0) if (f.IsAlive && f.CellId == cell) return true;
            foreach (var f in fight.Team1) if (f.IsAlive && f.CellId == cell) return true;
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
            await EndFightAsync(stream, fight);
        }

        private static async Task<bool> CheckFightOverAsync(NetworkStream stream, FightInstance fight,
                                                            int esperarAcuse = 0)
        {
            bool alliesAlive = fight.Team0.Exists(f => f.IsAlive);
            bool enemiesAlive = fight.Team1.Exists(f => f.IsAlive);
            if (alliesAlive && enemiesAlive) return false;

            // Si el golpe que lo ha terminado acaba de salir, no se le enseña el final hasta que el
            // cliente diga que ha tragado la secuencia; si no, se come las animaciones.
            if (esperarAcuse != 0)
            {
                fight.FinPendiente = esperarAcuse;
                Program.LogDebug($"[Combate] Se acabó, pero se espera a que el cliente acuse la " +
                                 $"acción {esperarAcuse} antes de enseñar el final.");
                return true;
            }

            await EndFightAsync(stream, fight);
            return true;
        }

        /// <summary>Lo que se manda cuando el combate se acaba de verdad.</summary>
        private static async Task EndFightAsync(NetworkStream stream, FightInstance fight)
        {
            bool alliesAlive = fight.Team0.Exists(f => f.IsAlive);

            // Lo que se gana. La experiencia es la que declara cada monstruo en su ficha (gradeXp),
            // que es la misma que enseña el cliente al pasar el ratón por el grupo; no hay fórmula
            // inventada. Los kamas y los objetos, lo que suelte cada uno.
            bool won = alliesAlive;

            // Los retos, antes de todo lo del final: el servidor real manda sus kwl unas pocas
            // tramas por delante del jyg, y en una derrota los manda todos seguidos ahi mismo.
            // Lo que devuelve es el extra de los cumplidos, sumado, en tanto por ciento.
            int extraDeRetos = await ChallengeWatcher.FightEndedAsync(stream, fight, won);

            // Y aqui se aplica. En el cable NO viaja desglosado: el porcentaje solo existe dentro
            // del ldd de la preparacion, y la cifra del final llega ya con el extra sumado. Se
            // revisaron los 68 jyg de las capturas y no hay ningun hueco donde quepa un desglose,
            // asi que es el servidor quien tiene que aplicarlo antes de mandar el numero.
            // Sin los invocados. La suma iba sobre TODO el bando contrario, y una invocación
            // entra en él con su nivel puesto: un monstruo que invoque estaba pagando kamas por
            // criaturas que él mismo se fabricaba durante el combate. La experiencia no lo
            // notaba porque un invocado no lleva XpReward, pero los kamas sí.
            var quePagan = fight.Team1.Where(m => !m.EsInvocado).ToList();
            long xpGained = won ? ConElExtra(quePagan.Sum(m => (long)m.XpReward), extraDeRetos) : 0;
            long kamas = won ? ConElExtra(quePagan.Sum(m => 10L + (m.Level * 5L)), extraDeRetos) : 0;
            var caidos = new List<PlayerItem>();
            var loot = won ? RollFightLoot(fight, extraDeRetos, out caidos)
                           : new Dictionary<int, int>();

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

            var results = new List<Network.FightProtocol.FightResult>();
            foreach (var f in fight.Team0)
            {
                results.Add(new Network.FightProtocol.FightResult
                {
                    Fighter = f.Id,
                    Winner = won,
                    Level = GameState.CharacterLevel,
                    Xp = GameState.Experience,
                    XpGained = xpGained,
                    Spoils = won ? spoils : null,
                });
            }
            foreach (var f in fight.Team1)
            {
                // Los monstruos van sin ficha de personaje: sólo quién son y si ganaron.
                results.Add(new Network.FightProtocol.FightResult { Fighter = f.Id, Winner = !won });
            }

            int duration = (int)Math.Max(0, (DateTime.UtcNow - fight.StartedAt).TotalMilliseconds);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kuf,
                Network.FightProtocol.BuildFightOver()));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyg,
                Network.FightProtocol.BuildFightResults(results, duration)));

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

            // El grupo que se acaba de matar desaparece del mapa, y en su sitio sale otro.
            //
            // Esto estaba escrito en SendFightEnd, que no lo llama nadie: sus tres llamadores
            // cuelgan de métodos que a su vez no llama nadie. El final que corre de verdad es éste,
            // y no borraba el grupo. En el registro se ve limpio: doce combates ganados y CERO
            // líneas de «removed from map», y el mismo grupo empezando tres combates seguidos
            // dentro de la misma ejecución. O sea que al volver al mapa el grupo muerto seguía
            // dibujado, con su mismo id, y se le podía volver a atacar: experiencia, kamas y botín
            // infinitos sobre el mismo grupo.
            if (won)
            {
                long muerto = GameState.CurrentFightMobId;
                MobSpawnManager.RemoveMobGroup(fight.RoleplayMapId, muerto);
                Program.LogDebug($"[Combate] El grupo #{muerto} desaparece del mapa {fight.RoleplayMapId}.");

                var repuesto = MobSpawnManager.RespawnOneGroup(fight.RoleplayMapId);
                if (repuesto != null)
                {
                    Program.LogDebug($"[Combate] Repuesto el grupo #{repuesto.MobId} en la casilla " +
                                     $"{repuesto.CellId} con {repuesto.Members.Count} miembro(s).");
                }
            }
            GameState.CurrentFightMobId = 0;

            // Y de vuelta al mapa de donde se salió, que el de arena es de instancia.
            long back = Network.SessionContext.State.RoleplayMapId != 0
                ? Network.SessionContext.State.RoleplayMapId
                : fight.RoleplayMapId;
            LeaveFight();

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kml));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmp));
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
            int CriticoPropio, int AlcanceMinimo = 0, int AlcanceMaximo = 0);

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
                    "MinRange, MaxRange FROM SpellLevels " +
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
                        reader.IsDBNull(9) ? 0 : (int)reader.GetInt64(9));
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
        public static async Task PassTurnAsync(NetworkStream stream)
        {
            var fight = GetCurrentFight();
            if (fight == null || fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;

            // Parar el reloj DE ESTE combate: si el turno se pasa a mano no debe saltar después.
            PararElReloj(fight);

            var ending = fight.CurrentFighter;
            if (ending == null) return;

            // Lo que las actitudes tengan que hacer al acabar el turno. Aquí es donde el Amarillo
            // Ocre se quita el estado de "me han pegado", para que el turno siguiente vuelva a
            // mirarlo limpio.
            await ActitudesAsync(stream, fight, ending, Managers.EffectEngine.AlAcabarElTurno);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyt,
                Network.FightProtocol.BuildTurnEnd(ending.Id)));

            // Las esperas bajan una ronda AL ACABAR el turno de su dueño, y el jxc de cierre ya
            // las lleva bajadas: medido en la captura de Agudeza Absoluta, lanzada en la ronda 8
            // con intervalo 4 —el jxc de ese turno dice 3, el de la 9 dice 2, el de la 10 uno, el
            // de la 11 cero y se relanza en la 12—. Ocho más cuatro, doce.
            SpellCastRules.AdvanceCooldowns(ending);
            // Los retos de posicion se juzgan AQUI, con el que acaba todavia donde acabo y con sus
            // PM sin reponer. Va antes de limpiar los contadores del turno, que el Versatil los usa.
            await ChallengeWatcher.TurnEndedAsync(stream, fight, ending);

            SpellCastRules.ClearTurnCounters(ending);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(ending.Id,
                                                         Network.FightProtocol.TurnEndSequence)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxc,
                Network.FightProtocol.BuildCooldowns(ending.Id, RecargasDe(ending))));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwi,
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
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxz,
                    Network.FightProtocol.BuildRound(fight.RoundNumber)));
                Program.LogDebug($"[Combate] Empieza la ronda {fight.RoundNumber}.");

                // El Imprevisible senala otro enemigo en cada turno global.
                await ChallengeWatcher.RoundStartedAsync(stream, fight);
            }

            await AskToConfirmAsync(stream, fight);
        }

        public static async Task SendSpellList(NetworkStream stream)
        {
            var jvnMsg = new ProtoMessage();

            // Same list as the roleplay shortcut bar.
            // TODO: once "which variant the player picked" is persisted, read it in
            // GetPlayerAvailableSpells instead of always assuming the base one.
            var spellList = DatabaseManager.GetPlayerAvailableSpells(GameState.Breed, GameState.CharacterLevel);

            Program.LogDebug($"[FightHandler] jvn: {spellList.Count} spells available at level " +
                             $"{GameState.CharacterLevel} for breed {GameState.Breed}: " +
                             string.Join(", ", spellList));

            // First entry: the WEAPON. It carries no spell id and uses f3 = 2 (spells use f3 = 1).
            // It was missing, and that is why the sword icon vanished from the bar on entering a
            // fight: jvn rebuilds the bar and was leaving it without the weapon slot.
            var weaponSub = new ProtoMessage();
            weaponSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 2 });
            weaponSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = weaponSub.ToByteArray() });
            foreach (var spellId in spellList)
            {
                var sSub = new ProtoMessage();
                sSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spellId });
                sSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
                sSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
                jvnMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = sSub.ToByteArray() });
            }

            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = GameState.CharacterId });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = GameState.CharacterId });

            // Slot 0 left empty, just like the official capture and the itp: that is the weapon slot.
            var weaponSlot = new ProtoMessage();
            weaponSlot.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = Array.Empty<byte>() });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = weaponSlot.ToByteArray() });

            int slot = 1;
            foreach (var spellId in spellList)
            {
                var spSub = new ProtoMessage();
                spSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spellId });

                var slotSub = new ProtoMessage();
                slotSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = slot++ });
                slotSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = spSub.ToByteArray() });

                jvnMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = slotSub.ToByteArray() });
            }

            byte[] env = BuildGameNodePacket("type.ankama.com/jvn", jvnMsg.ToByteArray());
            await WriteFrameAsync(stream, env);
            Program.LogDebug($"[FightHandler] Sent jvn (SpellListMessage) with {spellList.Count} spells for Breed {GameState.Breed}.");
        }

        public static async Task SendFighterResync(NetworkStream stream, FightInstance fight)
        {
            var jwmMsg = new ProtoMessage();
            foreach (var f in fight.Team0.Concat(fight.Team1))
            {
                byte[] fShowBytes = BuildFighterShowBytes(f);
                byte[]? payload = NetworkEnvelope.ExtractGameNodePayload(fShowBytes);
                if (payload != null)
                {
                    var innerMsg = ProtoMessage.Parse(payload);
                    if (innerMsg.Fields.Count > 1)
                    {
                        jwmMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = innerMsg.Fields[1].BytesValue });
                    }
                }
            }
            byte[] env = BuildGameNodePacket("type.ankama.com/jwm", jwmMsg.ToByteArray());
            await WriteFrameAsync(stream, env);
            Program.LogDebug("[FightHandler] Sent jwm (FighterResyncMessage).");
        }

        public static async Task HandleTurnReadyAck(NetworkStream stream)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            var current = fight.CurrentFighter;
            if (current == null) return;

            Program.LogDebug($"[FightHandler] Turn Ready Ack (jwe) received for Fighter #{current.Id} ({current.Name}).");

            // Send jut & jwl
            var jutMsg = new ProtoMessage();
            jutMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = TurnDurationDeciseconds });
            jutMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = fight.RoundNumber });
            jutMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = current.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jut", jutMsg.ToByteArray()));

            ResetTurnCastCounters();

            // Point refresh at the start of the turn. Fighter.StartTurn() already restores AP/MP on
            // the server side; this tells the client about it. With a delta of 0 the value block
            // collapses down to just the maximum, which is how the official capture expresses
            // "points back to full".
            //
            // It goes wrapped in jud/juc: the client's sequence engine discards characteristic
            // changes that arrive loose, outside an open sequence.
            await WriteFrameAsync(stream, BuildJud(4, current.Id));
            await WriteFrameAsync(stream, BuildJud(3, current.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(current.Id, 1, 0, current.MaxAP));
            await WriteFrameAsync(stream, BuildJuc(3, current.Id));
            await WriteFrameAsync(stream, BuildJud(3, current.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(current.Id, 23, 0, current.MaxMP));
            await WriteFrameAsync(stream, BuildJuc(3, current.Id));
            await WriteFrameAsync(stream, BuildJuc(4, current.Id));

            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwl", Array.Empty<byte>()));
            Program.LogDebug($"[FightHandler] Sent jut & jwl (Turn Started & Playable) for Fighter #{current.Id} " +
                              $"(AP {current.CurrentAP}/{current.MaxAP}, MP {current.CurrentMP}/{current.MaxMP}).");

            if (current.IsMonster)
            {
                await RunMonsterTurnAsync(stream, current);
            }
            else
            {
                StartTurnTimer(stream, fight, current);
            }
        }

        /// <summary>
        /// Starts the turn timer. jut.f1 = 300 only tells the client how many tenths of a second
        /// the turn lasts; enforcing the deadline is the server's job. Without this, once the time
        /// ran out the client's counter just kept ticking down into negatives and the turn never
        /// passed.
        /// </summary>
        private static void StartTurnTimer(NetworkStream stream, FightInstance fight, Fighter fighter)
        {
            fight.CancelTurnTimer();

            var cts = new CancellationTokenSource();
            fight.TurnTimerCts = cts;

            long fightId = fight.FightId;
            long fighterId = fighter.Id;
            int round = fight.RoundNumber;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TurnDurationMs, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // the player passed the turn in time
                }

                // Only force the end if we are still on exactly the same turn.
                var f = GetCurrentFight();
                if (f == null || f.FightId != fightId) return;
                if (f.State != FightState.Ongoing) return;
                if (f.CurrentFighter == null || f.CurrentFighter.Id != fighterId) return;
                if (f.RoundNumber != round) return;
                Program.LogDebug($"[FightHandler] ⏰ Fighter #{fighterId} ran out of time. Passing the turn automatically.");

                try
                {
                    await EndTurnAsync(stream, f.CurrentFighter);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[FightHandler] Error while forcing the end of the turn: {ex.Message}");
                }
            });
        }

        public static async Task EndTurnAsync(NetworkStream stream, Fighter endingFighter)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            // The turn is over: cancel the timer so it cannot force a second end of turn.
            fight.CancelTurnTimer();
            SpellCastRules.EndTurn(endingFighter);

            var jwkMsg = new ProtoMessage();
            jwkMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = endingFighter.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwk", jwkMsg.ToByteArray()));

            var nextFighter = fight.NextTurn();
            if (nextFighter == null || fight.State == FightState.Ended)
            {
                await SendFightEnd(stream, fight);
                return;
            }

            if (fight.StartsNewRound)
            {
                var jwbMsg = new ProtoMessage();
                jwbMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fight.RoundNumber });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwb", jwbMsg.ToByteArray()));
            }

            var jwuMsg = new ProtoMessage();
            jwuMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = nextFighter.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwu", jwuMsg.ToByteArray()));

            var juuMsg = new ProtoMessage();
            juuMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = nextFighter.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/juu", juuMsg.ToByteArray()));
            Program.LogDebug($"[FightHandler] Sent juu (Wait Turn Ack) for Fighter #{nextFighter.Id} ({nextFighter.Name}).");
        }

        private static async Task HandlePassTurnRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            var current = fight.CurrentFighter;
            if (current != null)
            {
                Program.LogDebug($"[FightHandler] Player requested Pass Turn (jxw) for Fighter #{current.Id}.");
                await EndTurnAsync(stream, current);
            }
        }

        public static List<int> GenerateSimplePath(int startCell, int targetCell)
        {
            var path = new List<int> { startCell };
            int startX = startCell % 14;
            int startY = startCell / 14;
            int targetX = targetCell % 14;
            int targetY = targetCell / 14;

            int currX = startX;
            int currY = startY;
            while (currX != targetX || currY != targetY)
            {
                if (currX < targetX) currX++;
                else if (currX > targetX) currX--;

                if (currY < targetY) currY++;
                else if (currY > targetY) currY--;

                int cell = currY * 14 + currX;
                path.Add(cell);
                if (path.Count > 20) break;
            }
            return path;
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

        /// <summary>
        /// "Cast spell" action (f13 = 300). f5 identifies the spell with TWO ids: f1 is the
        /// SpellLevels row (the specific level) and f4 the spell id. f1 used to be hardcoded to
        /// 41870, which is level 1 of Magic Arrow: every other spell reached the client carrying a
        /// level that did not belong to it.
        /// </summary>
        public static byte[] BuildJtxSpellCastPacket(long casterId, int targetCell, long spellId, int spellLevelId, long targetId, int launchIndex)
        {
            var f5Sub = new ProtoMessage();
            f5Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spellLevelId > 0 ? spellLevelId : spellId });
            f5Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = spellId });

            // f7 only carries the caster and the cast index. There used to be 13 bytes of zero
            // padding here, in a field 1 that does not exist in the real message; that alone was
            // enough for the client to drop the whole action and show no animation at all.
            var f7Sub = new ProtoMessage();
            f7Sub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = casterId });
            f7Sub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = launchIndex });

            var f34Msg = new ProtoMessage();
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = targetCell });
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = f5Sub.ToByteArray() });
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 2, BytesValue = f7Sub.ToByteArray() });
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = targetId });

            var jtxMsg = new ProtoMessage();
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 300 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = casterId });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 34, WireType = 2, BytesValue = f34Msg.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray());
        }

        /// <summary>
        /// "Life point loss" action (f13 = 99). The damage travels in f25, NOT in f6.
        ///
        /// Inside f25 the damage is field 5 and the element is field 1 — not the other way round.
        /// They were swapped, so the client always drew and applied the fixed value that field 5
        /// happened to carry (a 7) while the server subtracted the real health: the monster's bar
        /// went down 7 at a time and the fight ended all of a sudden with the creature still at
        /// full health on screen. The official capture confirms it twice: spell 13425 deals 7-9
        /// fire damage and sends f1=2 (fire) with f5=7 (the roll).
        /// </summary>
        public static byte[] BuildJtxDamagePacket(long casterId, long targetId, int damageDealt, int elementId)
        {
            var f25Sub = new ProtoMessage();
            f25Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = elementId });
            f25Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = targetId });
            f25Sub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = damageDealt });

            var jtxMsg = new ProtoMessage();
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 99 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 25, WireType = 2, BytesValue = f25Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = casterId });

            return BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray());
        }

        /// <summary>
        /// "Fighter killed" action (f13 = 103). Without it the client never considers anyone dead:
        /// the monster stayed on its feet and the end-of-fight screen counted zero enemies
        /// defeated. In the official capture it comes right after the killing blow (frame 313).
        /// </summary>
        public static byte[] BuildJtxDeathPacket(long killerId, long deadFighterId)
        {
            var f2Sub = new ProtoMessage();
            f2Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = deadFighterId });

            var jtxMsg = new ProtoMessage();
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = f2Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 103 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = killerId });

            return BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray());
        }

        /// <summary>
        /// "Action point loss" action (f13 = 102) used after casting a spell. This is the one that
        /// draws the floating "-N AP" over the caster; jvm only updates the counter.
        /// </summary>
        /// <summary>
        /// Point loss action: 102 for AP and 129 for MP. This is the one that draws the floating
        /// "-N" over the fighter and writes the line into the combat log; jvm only moves the
        /// counter, without announcing anything.
        ///
        /// <paramref name="victimId"/> and <paramref name="casterId"/> match when the cost is
        /// self-inflicted (casting a spell) and differ when someone else strips the points off you.
        /// </summary>
        public static byte[] BuildJtxPointLossPacket(long victimId, long casterId, int amount, bool isMp = false)
        {
            var f6Sub = new ProtoMessage();
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = victimId });
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -amount });

            var jtxMsg = new ProtoMessage();
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = f6Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = isMp ? 129 : 102 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = casterId });

            return BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray());
        }

        public static byte[] BuildJtxApLossPacket(long casterId, int apLost)
            => BuildJtxPointLossPacket(casterId, casterId, apLost);

        // There used to be a "life variation" jvm here on characteristic 19. It did nothing: the
        // health bar is moved by the damage jtx itself, and 19 is not health. It is removed rather
        // than leaving a made-up message in circulation.

        private static byte[] BuildJud(int kind, long fighterId)
        {
            var m = new ProtoMessage();
            m.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = kind });
            m.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighterId });
            return BuildGameNodePacket("type.ankama.com/jud", m.ToByteArray());
        }

        private static byte[] BuildJuc(int kind, long fighterId)
        {
            var m = new ProtoMessage();
            m.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = kind });
            m.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighterId });
            m.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            return BuildGameNodePacket("type.ankama.com/juc", m.ToByteArray());
        }

        /// <summary>
        /// Casts of each spell during the current turn, per spell and per target. The client reads
        /// that number straight from the cast packet (f7.f5) and compares it against the spell's
        /// limit to grey the icon out.
        ///
        /// There used to be a single global counter here that was never reset: by the third or
        /// fourth cast of the fight the client already believed Frozen Arrow's 3 casts per turn
        /// were spent and disabled it, even when it was the first cast of that turn.
        /// </summary>
        private static readonly Dictionary<long, int> _castsThisTurn = new Dictionary<long, int>();
        private static readonly Dictionary<(long Spell, long Target), int> _castsPerTargetThisTurn
            = new Dictionary<(long, long), int>();

        public static void ResetTurnCastCounters()
        {
            _castsThisTurn.Clear();
            _castsPerTargetThisTurn.Clear();
        }

        private static async Task HandleSpellCastRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            var current = fight.CurrentFighter;
            if (current == null || current.IsMonster) return;

            long spellId = 0;
            int targetCell = -1;
            try
            {
                var inner = ExtractMessagePayload(payload, "type.ankama.com/jub");
                if (inner != null)
                {
                    // By field NUMBER, not by position: a weapon hit arrives as { f2 = cell }
                    // with no field 1, and reading by position took the cell as the spell id and
                    // rejected the whole request.
                    var jubMsg = ProtoMessage.Parse(inner);
                    foreach (var f in jubMsg.Fields)
                    {
                        if (f.WireType != 0) continue;
                        if (f.FieldNumber == 1) spellId = f.VarIntValue;
                        else if (f.FieldNumber == 2) targetCell = (int)f.VarIntValue;
                    }
                }
            }
            catch { }

            if (targetCell < 0)
            {
                Program.LogDebug("[FightHandler] Cast request with no target cell; discarding it.");
                return;
            }

            // No spell id = a hit with the equipped WEAPON. That is exactly how the client sends
            // it, with the cell alone, and until now it was rejected outright.
            bool isWeapon = spellId <= 0;
            var spellData = isWeapon
                ? DatabaseManager.GetEquippedWeaponAsSpell(GameState.CharacterId)
                : DatabaseManager.GetSpellCombatData((int)spellId, current.Level);

            if (spellData == null)
            {
                Program.LogDebug(isWeapon
                    ? "[FightHandler] Weapon hit rejected: no equipped weapon deals damage."
                    : $"[FightHandler] Rejected spell cast: spell {spellId} data not found in DB.");
                return;
            }

            if (current.CurrentAP < spellData.APCost)
            {
                Program.LogDebug($"[FightHandler] Player has insufficient AP ({current.CurrentAP}/{spellData.APCost}) for spell {spellId}.");
                return;
            }

            int distToTarget = MapGeometry.Distance(current.CellId, targetCell);
            if (distToTarget < spellData.MinRange || distToTarget > spellData.MaxRange)
            {
                Program.LogDebug($"[FightHandler] Spell {spellId} out of range ({distToTarget} cells, range {spellData.MinRange}-{spellData.MaxRange}).");
                return;
            }

            if (spellData.NeedsLineOfSight &&
                !MapGeometry.HasLineOfSight(current.CellId, targetCell, MapManager.GetLosBlockers(fight.ArenaMapId)))
            {
                Program.LogDebug($"[FightHandler] Spell {spellId} has no line of sight from {current.CellId} to {targetCell}.");
                return;
            }

            var target = fight.Team1.FirstOrDefault(m => m.IsAlive && (m.CellId == targetCell || MapGeometry.Distance(m.CellId, targetCell) <= 1));
            long targetId = target != null ? target.Id : -1;

            // Cast limits, exactly as the spell declares them in the database.
            _castsThisTurn.TryGetValue(spellId, out int castsDone);
            if (spellData.MaxCastPerTurn > 0 && castsDone >= spellData.MaxCastPerTurn)
            {
                Program.LogDebug($"[FightHandler] Spell {spellId} already spent this turn ({castsDone}/{spellData.MaxCastPerTurn}).");
                return;
            }

            var perTargetKey = (spellId, targetId);
            _castsPerTargetThisTurn.TryGetValue(perTargetKey, out int castsOnTarget);
            if (targetId != -1 && spellData.MaxCastPerTarget > 0 && castsOnTarget >= spellData.MaxCastPerTarget)
            {
                Program.LogDebug($"[FightHandler] Spell {spellId} already spent on that target ({castsOnTarget}/{spellData.MaxCastPerTarget}).");
                return;
            }

            current.AccumulatedApLoss += spellData.APCost;
            current.CurrentAP -= spellData.APCost;

            castsDone++;
            _castsThisTurn[spellId] = castsDone;
            if (targetId != -1) _castsPerTargetThisTurn[perTargetKey] = castsOnTarget + 1;

            Program.LogDebug($"[FightHandler] {(isWeapon ? "Weapon hit" : $"Player cast spell {spellId}")} " +
                             $"on cell {targetCell} (costs {spellData.APCost} AP, {current.CurrentAP} left, " +
                             $"cast {castsDone}" +
                             $"{(spellData.MaxCastPerTurn > 0 ? "/" + spellData.MaxCastPerTurn : "")} of the turn).");

            // Sequence traced from the official capture (frame 254):
            //   jud(4) -> jtx(300 cast) -> jud(3) -> jvm(AP) -> juc(3) -> jtx(102 AP loss)
            //   -> jtx(99 damage) -> juc(4)
            //
            // The cast jtx is not sent for a weapon hit: there is no capture of a weapon attack
            // and it is unknown how the client encodes that action. Only the AP cost and the
            // damage go out, which are both verified. The sword swing animation will be missing.
            await WriteFrameAsync(stream, BuildJud(4, current.Id));
            if (!isWeapon)
            {
                await WriteFrameAsync(stream, BuildJtxSpellCastPacket(current.Id, targetCell, spellId, spellData.SpellLevelId, targetId, castsDone));
            }

            await WriteFrameAsync(stream, BuildJud(3, current.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(current.Id, 1, -current.AccumulatedApLoss, current.MaxAP));
            await WriteFrameAsync(stream, BuildJuc(3, current.Id));
            await WriteFrameAsync(stream, BuildJtxApLossPacket(current.Id, spellData.APCost));

            if (target != null)
            {
                await ApplySpellEffectsAsync(stream, fight, current, spellData, target);
            }

            await WriteFrameAsync(stream, BuildJuc(4, current.Id));

            fight.CheckFightEnd();
            if (fight.State == FightState.Ended)
            {
                await SendFightEnd(stream, fight);
            }
        }

        /// <summary>
        /// Applies EVERY effect of a spell to a target and reports them to the client. Player
        /// casts and monster casts both go through it, so that a piwi stripping range does exactly
        /// what the character would do with the same spell.
        ///
        /// Covers damage (per element), displacement (push/pull) and any effect that modifies a
        /// characteristic. That last group comes from the effect catalogue imported from the
        /// client: there is no hand-written list of effects anywhere.
        ///
        /// What it still does NOT do: the dodge roll. In Dofus, stripping AP or MP is resolved by
        /// pitting the caster's "withdrawal" against the target's "dodge", and the character's
        /// withdrawal is not being computed from the gear, so for now the effect is applied in
        /// full. The structure is already in place to add the roll once that value is read from
        /// the gear.
        /// </summary>
        private static async Task<int> ApplySpellEffectsAsync(
            NetworkStream stream, FightInstance fight, Fighter caster, SpellCombatData spell, Fighter target)
        {
            int damageDealt = 0;

            if (spell.BaseDamageMin > 0 || spell.BaseDamageMax > 0)
            {
                var element = (ElementType)spell.Element;

                // Critical hit: the spell's own probability plus whatever critical the gear adds.
                // On a critical the damage is NOT multiplied; the critical range carried by the
                // spell itself is used instead (Frozen Arrow goes from 12-14 to 15-17).
                int criticalChance = spell.CriticalHitProbability + caster.CriticalBonus;
                bool isCritical = spell.HasCriticalDamage && criticalChance > 0 && TirarCritico(criticalChance);

                int minBase = isCritical ? spell.CriticalDamageMin : spell.BaseDamageMin;
                int maxBase = isCritical ? spell.CriticalDamageMax : spell.BaseDamageMax;

                // Base damage bonus the spell left on itself during an earlier cast (effect 293).
                // It adds to the BASE damage, before multiplying by the characteristic: Frozen
                // Arrow goes from 12-14 to 16-18 on the second cast.
                int baseBonus = caster.GetSpellDamageBonus((int)spell.SpellId, fight.RoundNumber);
                int baseDamageRoll = ((minBase + maxBase) / 2) + baseBonus;

                damageDealt = DamageCalculator.CalculateDamage(
                    baseDamage: baseDamageRoll,
                    element: element,
                    statValue: caster.GetStatForElement(element),
                    power: caster.Power,
                    flatElementDamage: 0,
                    flatDamage: 0,
                    targetResPct: target.GetResPctForElement(element),
                    targetFlatRes: 0);

                target.TakeDamage(damageDealt);
                Program.LogDebug($"[FightHandler] {caster.Name} deals {damageDealt} damage to {target.Name} " +
                                 $"(element {spell.Element}, base {baseDamageRoll}" +
                                 $"{(baseBonus != 0 ? $" including +{baseBonus} from the effect" : "")}" +
                                 $"{(isCritical ? $", CRITICAL at {criticalChance} %" : "")}). " +
                                 $"HP: {target.CurrentHP}/{target.MaxHP}");

                await WriteFrameAsync(stream, BuildJtxDamagePacket(caster.Id, target.Id, damageDealt, spell.Element));

                if (!target.IsAlive)
                {
                    await WriteFrameAsync(stream, BuildJtxDeathPacket(caster.Id, target.Id));
                    Program.LogDebug($"[FightHandler] {target.Name} has fallen.");
                }
            }

            // Bonuses the spell leaves on the CASTER for later uses. Casting it again refreshes
            // the duration instead of stacking a second time: Frozen Arrow's maximum stack is 1.
            foreach (var buff in spell.DamageBuffs)
            {
                caster.ApplySpellDamageBuff(buff.SpellId, buff.Bonus, buff.Duration, fight.RoundNumber);
                Program.LogDebug($"[FightHandler]   {caster.Name} gains +{buff.Bonus} base damage on spell " +
                                 $"{buff.SpellId} for {buff.Duration} turn(s).");
            }

            // Characteristic effects: AP removal (effect 1079 of Frozen Arrow), range removal
            // (effect 116, the one the piwi carries), and so on.
            foreach (var se in spell.StatEffects)
            {
                if (se.Characteristic == 1)
                {
                    int lost = Math.Min(Math.Abs(se.Value), target.CurrentAP);
                    if (lost <= 0) continue;
                    target.CurrentAP -= lost;
                    target.AccumulatedApLoss += lost;
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, 1, -target.AccumulatedApLoss, target.MaxAP));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    await WriteFrameAsync(stream, BuildJtxPointLossPacket(target.Id, caster.Id, lost));
                    Program.LogDebug($"[FightHandler]   effect {se.EffectId}: -{lost} AP on {target.Name}.");
                }
                else if (se.Characteristic == 23)
                {
                    int lost = Math.Min(Math.Abs(se.Value), target.CurrentMP);
                    if (lost <= 0) continue;
                    target.CurrentMP -= lost;
                    target.AccumulatedMpLoss += lost;
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, 23, -target.AccumulatedMpLoss, target.MaxMP));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    await WriteFrameAsync(stream, BuildJtxPointLossPacket(target.Id, caster.Id, lost, isMp: true));
                    Program.LogDebug($"[FightHandler]   effect {se.EffectId}: -{lost} MP on {target.Name}.");
                }
                else
                {
                    // Every other characteristic (range, power, resistances...): the client only
                    // needs the variation; the server does not use them in its own maths yet.
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, se.Characteristic, se.Value, 0));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    Program.LogDebug($"[FightHandler]   effect {se.EffectId}: {se.Value} on characteristic {se.Characteristic} of {target.Name}.");
                }
            }

            // Displacement. With no capture of a real push, the same joo that walks a fighter
            // along a path is reused: the animation will not be a shove, but the monster ends up
            // on the right cell instead of staying nailed to the spot.
            if (spell.PushDistance != 0 && target.IsAlive)
            {
                var walkable = MapManager.GetFightWalkable(fight.ArenaMapId);
                var occupied = new HashSet<int>(fight.Team0.Concat(fight.Team1)
                    .Where(f => f.IsAlive && f.Id != target.Id).Select(f => f.CellId));

                var pushPath = MapGeometry.ComputePush(caster.CellId, target.CellId, spell.PushDistance, walkable, occupied);
                if (pushPath.Count > 1)
                {
                    target.CellId = pushPath[pushPath.Count - 1];
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJooMovementPacket(target.Id, pushPath));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    Program.LogDebug($"[FightHandler]   displacement: {target.Name} moves to cell {target.CellId} " +
                                     $"({pushPath.Count - 1} of {Math.Abs(spell.PushDistance)} cells).");
                }
            }

            return damageDealt;
        }

        private static async Task HandleCombatMoveRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;
            var current = fight.CurrentFighter;
            if (current == null || current.Id != GameState.CharacterId) return;

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
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jud", jud4Start.ToByteArray()));

            await WriteFrameAsync(stream, BuildJooMovementPacket(current.Id, actualPath));

            var jud3 = new ProtoMessage();
            jud3.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 3 });
            jud3.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jud", jud3.ToByteArray()));

            await WriteFrameAsync(stream, BuildJvmPacket(current.Id, 23, -current.AccumulatedMpLoss, current.MaxMP));

            var juc3 = new ProtoMessage();
            juc3.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 3 });
            juc3.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            juc3.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/juc", juc3.ToByteArray()));

            var jtxMsg = new ProtoMessage();
            var f6Sub = new ProtoMessage();
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = current.Id });
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -steps });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = f6Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 129 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = current.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray()));

            var juc4End = new ProtoMessage();
            juc4End.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 4 });
            juc4End.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            juc4End.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/juc", juc4End.ToByteArray()));
        }

        private static async Task RunMonsterTurnAsync(NetworkStream stream, Fighter monster)
        {
            var fight = GetCurrentFight();
            if (fight == null || !monster.IsMonster || !monster.IsAlive) return;
            Program.LogDebug($"[FightHandler] Running AI turn for Monster #{monster.Id} ({monster.Name})...");

            var arenaWalkable = MapManager.GetFightWalkable(fight.ArenaMapId);
            var losBlockers = MapManager.GetLosBlockers(fight.ArenaMapId);

            var turnResult = MonsterAI.ExecuteTurn(
                monster,
                fight.Team0.Concat(fight.Team1).ToList(),
                arenaWalkable,
                (spellId) =>
                {
                    // The spell grade comes from the monster's own sheet (spellGrades), not from its level.
                    int grade = monster.SpellGrades.TryGetValue(spellId, out var g) ? g : 1;
                    var sData = DatabaseManager.GetSpellCombatData(spellId, grade);
                    if (sData == null) return null;
                    return new MonsterAI.AISpellData
                    {
                        SpellId = (int)spellId,
                        APCost = sData.APCost,
                        MinRange = sData.MinRange,
                        MaxRange = sData.MaxRange,
                        BaseDamageMin = sData.BaseDamageMin,
                        BaseDamageMax = sData.BaseDamageMax,
                        Element = sData.Element,
                        NeedsLineOfSight = sData.NeedsLineOfSight,
                        CastInLine = sData.CastInLine,
                        MaxCastPerTurn = sData.MaxCastPerTurn,
                        MaxCastPerTarget = sData.MaxCastPerTarget,
                        MinCastInterval = sData.MinCastInterval
                    };
                },
                losBlockers
            );

            // Order matters: if the monster attacked and THEN fled, that is the order it has to be
            // sent in. The other way round, the client draws the shot from the escape cell and it
            // looks as if the monster attacked from much farther away than its spell allows.
            if (turnResult.CastBeforeMove)
            {
                if (await SendMonsterCastAsync(stream, fight, monster, turnResult)) return;
                await SendMonsterMoveAsync(stream, monster, turnResult);
            }
            else
            {
                await SendMonsterMoveAsync(stream, monster, turnResult);
                if (await SendMonsterCastAsync(stream, fight, monster, turnResult)) return;
            }

            await EndTurnAsync(stream, monster);
        }

        private static async Task SendMonsterMoveAsync(NetworkStream stream, Fighter monster, MonsterTurnResult turnResult)
        {
            if (turnResult.PathCells.Count <= 1) return;

            Program.LogDebug($"[FightHandler] Monster #{monster.Id} walks {turnResult.PathCells.Count - 1} cell(s) to cell {monster.CellId}.");

            await WriteFrameAsync(stream, BuildJud(4, monster.Id));
            await WriteFrameAsync(stream, BuildJooMovementPacket(monster.Id, turnResult.PathCells));
            await WriteFrameAsync(stream, BuildJud(3, monster.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(monster.Id, 23, -monster.AccumulatedMpLoss, monster.MaxMP));
            await WriteFrameAsync(stream, BuildJuc(3, monster.Id));
            await WriteFrameAsync(stream, BuildJuc(4, monster.Id));
        }

        /// <summary>Returns true if the fight is over and has already been reported.</summary>
        private static async Task<bool> SendMonsterCastAsync(
            NetworkStream stream, FightInstance fight, Fighter monster, MonsterTurnResult turnResult)
        {
            if (turnResult.SpellId == 0) return false;

            var target = fight.Team0.Concat(fight.Team1).FirstOrDefault(p => p.Id == turnResult.TargetFighterId);
            int grade = monster.SpellGrades.TryGetValue(turnResult.SpellId, out var mg) ? mg : 1;
            var monSpell = DatabaseManager.GetSpellCombatData(turnResult.SpellId, grade);
            if (target == null || monSpell == null) return false;

            // The cell the spell was cast FROM, not the current one: if the monster attacked and
            // then fled, its CellId is already the destination.
            int fromCell = turnResult.CastFromCell >= 0 ? turnResult.CastFromCell : monster.CellId;
            int d = MapGeometry.Distance(fromCell, target.CellId);
            int castCount = Math.Max(1, turnResult.CastCount);
            Program.LogDebug($"[FightHandler] Monster #{monster.Id} casts spell {turnResult.SpellId} " +
                             $"{castCount} time(s) on {target.Name} from cell {fromCell} " +
                             $"(distance {d}, range {monSpell.MinRange}-{monSpell.MaxRange}).");

            // Exactly the same sequence as a player cast, effect application included: that way a
            // monster that pushes or strips AP does the same thing the character would do with
            // that spell.
            for (int i = 1; i <= castCount; i++)
            {
                await WriteFrameAsync(stream, BuildJud(4, monster.Id));
                await WriteFrameAsync(stream, BuildJtxSpellCastPacket(monster.Id, turnResult.TargetCellId, turnResult.SpellId, monSpell.SpellLevelId, target.Id, i));

                await WriteFrameAsync(stream, BuildJud(3, monster.Id));
                await WriteFrameAsync(stream, BuildJvmPacket(monster.Id, 1, -monster.AccumulatedApLoss, monster.MaxAP));
                await WriteFrameAsync(stream, BuildJuc(3, monster.Id));
                if (monSpell.APCost > 0)
                {
                    await WriteFrameAsync(stream, BuildJtxApLossPacket(monster.Id, monSpell.APCost));
                }

                await ApplySpellEffectsAsync(stream, fight, monster, monSpell, target);

                await WriteFrameAsync(stream, BuildJuc(4, monster.Id));

                fight.CheckFightEnd();
                if (fight.State == FightState.Ended)
                {
                    await SendFightEnd(stream, fight);
                    return true;
                }
                if (!target.IsAlive) break;
            }
            return false;
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

        public static byte[] BuildKkzPacket(int cellId, long fighterId, int direction)
        {
            var kkzSub = new ProtoMessage();
            kkzSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = cellId });
            kkzSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fighterId });
            kkzSub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = direction });

            var kkzMsg = new ProtoMessage();
            kkzMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = kkzSub.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/kkz", kkzMsg.ToByteArray());
        }

        public static byte[] BuildKkzAllPacket(FightInstance fight)
        {
            var kkzMsg = new ProtoMessage();

            foreach (var f in fight.Team0.Concat(fight.Team1))
            {
                int dir = f.TeamId == 0 ? 3 : 7;
                var kkzSub = new ProtoMessage();
                kkzSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = f.CellId });
                kkzSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = f.Id });
                kkzSub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = dir });

                kkzMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = kkzSub.ToByteArray() });
            }

            return BuildGameNodePacket("type.ankama.com/kkz", kkzMsg.ToByteArray());
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
            if (!fighter.IsMonster) AddBaseBonusVal(11, 0, GameState.StatVitality + StatsHandler.GetEquipBonus(11));
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

        private static async Task SendPointsVariation(NetworkStream stream, long fighterId, int current, int max, bool isMP)
        {
            // kkz for MP, jys for AP
            string typeUrl = isMP ? "type.ankama.com/kkz" : "type.ankama.com/jys";
            var msg = new ProtoMessage();
            msg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighterId });
            msg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current });
            msg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = max });
            byte[] env = BuildGameNodePacket(typeUrl, msg.ToByteArray());
            await WriteFrameAsync(stream, env);
        }

        private static async Task SendLifePointsVariation(NetworkStream stream, long fighterId, int currentHP, int maxHP, int damage)
        {
            var msg = new ProtoMessage();
            msg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighterId });
            msg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = damage });
            msg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = currentHP });
            msg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = maxHP });
            byte[] env = BuildGameNodePacket("type.ankama.com/jwu", msg.ToByteArray());
            await WriteFrameAsync(stream, env);
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
            caidos = new List<PlayerItem>();
            var loot = new Dictionary<int, int>();

            // Los INVOCADOS no pagan. Entran en el bando del que los invoca con IsMonster
            // puesto, así que un monstruo que invoque metería a su criatura en este bucle: se
            // llevaría su propia tabla de botín y, con la moneda, sería una fábrica de dinero
            // que se abre sola. Se distinguen por el Invocador, que sólo tienen ellos.
            foreach (var monster in fight.Team1.Where(m => m.IsMonster && !m.EsInvocado))
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
            }

            // De una vez: cada AddItemToInventory cargaba el inventario entero para ver si el
            // objeto ya estaba, así que cinco objetos distintos eran cinco lecturas completas.
            caidos = DatabaseManager.AddItemsToInventory(GameState.CharacterId, loot);
            foreach (var kv in loot)
                Program.LogDebug($"[FightHandler] Loot: item {kv.Key} x{kv.Value} added to the inventory.");

            if (loot.Count > 0)
            {
                GameState.SetInventory(DatabaseManager.LoadInventory(GameState.CharacterId));

                // Y LA OTRA LISTA, que es la que de verdad se le manda al cliente.
                //
                // Hay dos vistas del inventario y esto sólo refrescaba una. GameState es la del
                // estado de sesión; la que lee BuildInventory para armar el ivx es
                // Managers.Equipment, y se quedaba con lo de antes hasta el siguiente login. Por
                // eso el botín se guardaba bien en la base y el jugador no lo veía por ningún
                // lado: 73 Jondo Coin en CharacterItems y ni una en la pantalla.
                foreach (var pieza in caidos)
                {
                    Managers.Equipment.Remove(pieza.Uid, int.MaxValue);
                    Managers.Equipment.Add(pieza.Uid, pieza.ItemId, pieza.Quantity,
                                           Managers.Equipment.Bag, pieza.RawEffects ?? "[]");
                }
            }

            return loot;
        }

        private static async Task SendFightEnd(NetworkStream stream, FightInstance fight)
        {
            // The REAL experience of each defeated monster (gradeXp from its sheet), not a made-up
            // formula. It is the same figure the client shows when hovering over the group.
            //
            // What is NOT applied: the adjustment for the level gap between the group and the
            // character, nor the split across several team members. With a single player and no
            // verified formula, the base experience is handed out as is.
            long totalXP = (fight.WinnerTeamId == 0) ? fight.Team1.Sum(m => (long)m.XpReward) : 0;
            int totalKamas = fight.Team1.Sum(m => 10 + (m.Level * 5));

            int previousLevel = GameState.CharacterLevel;
            if (totalXP > 0)
            {
                GameState.Experience += totalXP;
                int newLevel = ExperienceTable.LevelForXp(GameState.Experience);
                if (newLevel > GameState.CharacterLevel)
                {
                    // 5 characteristic points per level, same as in TotalCapitalForLevel.
                    int levelsGained = newLevel - GameState.CharacterLevel;
                    GameState.CharacterRemainingPoints += levelsGained * 5;
                    GameState.CharacterLevel = newLevel;
                    Program.LogDebug($"[FightHandler] Level up! {previousLevel} -> {newLevel} " +
                                     $"(+{levelsGained * 5} characteristic points).");

                    await WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kua, ConnectionProtocol.BuildLevelUp(newLevel)));
                }
                DatabaseManager.SaveCurrentCharacter();
                Program.LogDebug($"[FightHandler] +{totalXP} experience (total {GameState.Experience}, " +
                                 $"level {GameState.CharacterLevel}: from {ExperienceTable.LevelFloor(GameState.CharacterLevel)} " +
                                 $"to {ExperienceTable.NextLevelFloor(GameState.CharacterLevel)}).");
            }

            // jwf = the end-of-fight screen. Field 1 is REPEATED and of message type: one entry
            // per fighter. It used to be sent as a plain varint holding the winning team, so the
            // client could not even decode the message and no screen showed up at all.
            //
            // Structure taken from the official capture (frame 334):
            //   f1 { f1 { f1: 1, f2 { f1{f3=itemId, f4=quantity}..., f3 = kamas } }   <- loot
            //        f3 { f4: 1, f5 = fighterId }
            //        f4: 2 }                                                          <- winner
            //   f1 { f1: {}, f3 { f4: 1, f5 = fighterId } }                           <- loser
            //   f2: -1
            //
            // The f3.f9 experience progress block is left out: the capture gives a single sample
            // and does not let us tell what each number means, so it is omitted instead of being
            // filled in by eye. It is optional; the screen still shows up, minus the XP bar.
            var loot = (fight.WinnerTeamId == 0) ? RollFightLoot(fight, 0, out _)
                                                 : new Dictionary<int, int>();

            var lootMsg = new ProtoMessage();
            foreach (var kv in loot)
            {
                var itemEntry = new ProtoMessage();
                itemEntry.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = kv.Key });
                itemEntry.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = kv.Value });
                lootMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = itemEntry.ToByteArray() });
            }

            // The kamas go in field 1 of the loot wrapper, not in field 3 of the inner block. The
            // proof is direct: a fixed 1 copied from the capture used to sit there and the
            // end-of-fight screen showed "kamas 1" after a fight that paid 65. Field 3 (3273 in
            // the official capture) is the estimated value of the loot, which the client works
            // out on its own anyway.
            var lootWrap = new ProtoMessage();
            lootWrap.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = totalKamas });
            lootWrap.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = lootMsg.ToByteArray() });

            var jwfMsg = new ProtoMessage();
            foreach (var f in fight.Team0.Concat(fight.Team1))
            {
                bool isWinner = f.TeamId == fight.WinnerTeamId;

                var fighterResult = new ProtoMessage();
                fighterResult.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
                fighterResult.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = f.Id });

                // Experience progress block, for the player's character only.
                //
                // Structure deduced from three captures with the character at levels 1, 2 and 3,
                // and cross-checked against the client's experience table:
                //   f4 = experience the current level starts at (omitted when 0)
                //   f6 = experience at which the next level is reached
                //   f7 = experience accumulated right now (omitted when 0)
                //   f9 = experience gained in this fight (omitted when 0)
                //   f1, f2, f3, f5, f8 = 1 in all three captures
                // and, one level up, f2 = the character's level.
                // Check: at level 3 the capture sends f4=650 and f6=1500, which are exactly the
                // thresholds of levels 3 and 4 in the client's table.
                if (!f.IsMonster)
                {
                    long levelFloor = ExperienceTable.LevelFloor(GameState.CharacterLevel);
                    long nextLevelFloor = ExperienceTable.NextLevelFloor(GameState.CharacterLevel);

                    var xpDetail = new ProtoMessage();
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
                    if (levelFloor > 0)
                        xpDetail.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = levelFloor });
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 1 });
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = nextLevelFloor });
                    if (GameState.Experience > 0)
                        xpDetail.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = GameState.Experience });
                    if (totalXP > 0)
                    {
                        xpDetail.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = 1 });
                        xpDetail.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 0, VarIntValue = totalXP });
                    }

                    var xpWrap = new ProtoMessage();
                    xpWrap.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = xpDetail.ToByteArray() });

                    var xpBlock = new ProtoMessage();
                    xpBlock.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = xpWrap.ToByteArray() });
                    xpBlock.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterLevel });

                    fighterResult.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 2, BytesValue = xpBlock.ToByteArray() });
                }

                var entry = new ProtoMessage();
                entry.Fields.Add(new ProtoField
                {
                    FieldNumber = 1,
                    WireType = 2,
                    BytesValue = (isWinner && !f.IsMonster) ? lootWrap.ToByteArray() : Array.Empty<byte>()
                });
                entry.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = fighterResult.ToByteArray() });
                if (isWinner)
                {
                    entry.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 2 });
                }

                jwfMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = entry.ToByteArray() });
            }
            jwfMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -1 });

            // krh = experience gained. In both captures it comes right before the jwf.
            var krhMsg = new ProtoMessage();
            if (totalXP > 0)
            {
                krhMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = totalXP });
            }
            await WriteFrameAsync(stream, BuildGameNodePacket(Op.Uri(Op.Krh), krhMsg.ToByteArray()));

            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwf", jwfMsg.ToByteArray()));

            // The juo that used to be sent here ({f1 = xp, f2 = kamas}) did not look like the real
            // one either, whose field 1 is a submessage. It is dropped instead of replaced by
            // another invention: a malformed message is worse than no message at all.

            if (fight.WinnerTeamId == 0)
            {
                MobSpawnManager.RemoveMobGroup(fight.RoleplayMapId, GameState.CurrentFightMobId);
                Program.LogDebug($"[FightHandler] Mob group #{GameState.CurrentFightMobId} removed from map {fight.RoleplayMapId}.");

                // The defeated group is replaced with a freshly randomized one, leaving the groups
                // still on the map alone. It runs before the synthesized kkr further down, so that
                // the jpv sent to the client already includes it.
                var respawned = MobSpawnManager.RespawnOneGroup(fight.RoleplayMapId);
                if (respawned != null)
                {
                    Program.LogDebug($"[FightHandler] Respawned group #{respawned.MobId} on cell " +
                                     $"{respawned.CellId} with {respawned.Members.Count} monster(s).");
                }
            }

            GameState.IsInFight = false;
            GameState.CurrentFightMobId = 0;
            _activeFights.TryRemove(fight.FightId, out _);

            // Back to roleplay, traced from the official capture (frames 336-339):
            //   lxs -> kkp -> kkm -> krb -> joh -> lor
            //
            // What used to be here was jpf + kkq(0) + joh, and neither of the first two does that
            // job: kkq identifies the mob group and jpf opens the fight context. The messages that
            // really pull the client out of the fight are kkp (destroy context) and kkm (create
            // the new one; empty = roleplay). Without them the client stayed inside the fight
            // context, which is why the turn counter and the timer were still on screen after
            // closing the victory panel.
            await WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLxsMessage());
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/kkp", Array.Empty<byte>()));
            await WriteFrameAsync(stream, BuildGameNodePacket(Op.Uri(Op.Kkm), Array.Empty<byte>()));
            await WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKrbMessage());

            var johRp = new ProtoMessage();
            johRp.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.RoleplayMapId });
            await WriteFrameAsync(stream, BuildGameNodePacket(Op.Uri(Op.Joh), johRp.ToByteArray()));

            var lorRp = new ProtoMessage();
            lorRp.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 120 });
            lorRp.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/lor", lorRp.ToByteArray()));

            // Repopulate the roleplay map. With joh alone the client was left on an empty map, with
            // no player, no NPCs and no mob groups: the kkr -> jpv cycle is missing. We trigger it
            // ourselves by synthesizing the kkr instead of waiting for the client to ask for it.
            GameState.MapId = fight.RoleplayMapId;
            var kkrSynth = new ProtoMessage();
            kkrSynth.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fight.RoleplayMapId });
            byte[] kkrPacket = BuildGameNodePacket(Op.Uri(Op.Kkr), kkrSynth.ToByteArray());
            await MapLoadHandler.HandleMapLoadRequest(stream, kkrPacket);

            // The kamas won. They are saved on the character and a bvr (KamasUpdateMessage) is sent
            // to the client; without it the purse kept showing the pre-fight amount.
            if (fight.WinnerTeamId == 0 && totalKamas > 0)
            {
                GameState.Kamas += totalKamas;
                DatabaseManager.SaveCurrentCharacter();

                var bvrMsg = new ProtoMessage();
                bvrMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = GameState.Kamas });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/bvr", bvrMsg.ToByteArray()));
                Program.LogDebug($"[FightHandler] +{totalKamas} kamas (total {GameState.Kamas}).");
            }

            // With new loot the whole inventory has to be resent: otherwise the items are in the
            // database but the client keeps showing the bag it had before the fight.
            if (loot.Count > 0)
            {
                await WriteFrameAsync(stream, BuildGameNodePacket(
                    Op.Uri(Op.Irm), CharacterSelectionHandler.BuildDynamicIrmPayload()));
                Program.LogDebug($"[FightHandler] Inventory resent with {loot.Count} looted item(s).");
            }

            // La ficha del personaje. El cliente sale del combate con los contadores que tuviera el
            // combatiente al morir el último enemigo, y por eso aparecía en el mapa con la vida y
            // los puntos del final de la pelea.
            //
            // Por kub, que es el opcode de la ficha en esta versión; el "kri" que había aquí no
            // sale ni una vez en las 295 capturas.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kub,
                ConnectionProtocol.BuildCharacteristics()));
            Program.LogDebug("[FightHandler] Ficha del personaje reenviada al volver al mapa.");

            // On level up the spell bar is rebuilt: there may be a new spell that now meets the
            // minimum level. The client pops the level-up screen by itself as soon as it sees a
            // higher level in the kri than the one it had.
            if (GameState.CharacterLevel > previousLevel)
            {
                await WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHmdMessage());
                foreach (var itp in TransitionPacketsBuilder.BuildItpList())
                {
                    await WriteFrameAsync(stream, itp);
                }
                Program.LogDebug($"[FightHandler] Spell book and spell bar rebuilt after reaching level {GameState.CharacterLevel}.");
            }

            Program.LogDebug($"[FightHandler] Fight #{fight.FightId} ended! Restored Roleplay Map {fight.RoleplayMapId}. Winner: Team {fight.WinnerTeamId}. Rewards: {totalXP} XP, {totalKamas} Kamas, {loot.Count} item(s).");
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
