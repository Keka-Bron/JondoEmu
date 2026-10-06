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

        /// <summary>How many fights are open right now. The server window draws it.</summary>
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

            // If this player had a fight left hanging from before, his is removed and only his.
            // There was an _activeFights.Clear() here: starting a fight wiped those of ALL the
            // other players on the server. The normal end of a fight already removes it by itself
            // (TryRemove further down, when sending the result), so this is just the safety net in
            // case one was left loose.
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

            // In a kanojedo the fight follows the training rulebook: no challenges, no loot and
            // the puch does not disappear on winning. The map decides -- where the master puch is --
            // and not the monster, because the bag that gets hit and the one the master composes
            // are the same creatures and it makes no difference which way you go in.
            if (Managers.Kanojedo.IsDojo(mapId)) fight.Reglas = FightRules.Entrenamiento;

            // The group's contextual id IS its MobId, the same one that travels in the jss and the
            // jpv and the same one the client sends back when clicking it. The mobContextId parameter
            // is unnecessary since both packets hand out the same number; it stays for outside callers.
            fight.DefenderLeaderId = mobGroup.MobId;

            // THE PLACEMENT CELLS COME FROM GetFightWalkable, not from the seeding filter.
            //
            // GetInnerWalkableCells is a SURFACE filter: it keeps only the cells whose twelve
            // neighbours within radius 2 are all walkable and that are far from the edge, which is
            // what is needed to place a monster group on a roleplay map and not what is needed to
            // place two teams in an arena. In arena 188752387, 2 of its 77 cells pass, and its only
            // safety net -- "if none are left, use them all" -- does not fire because 2 is not zero.
            // That gives 1 blue cell and 1 red, and the five monsters end up stacked on the same one.
            //
            // Incarnam worked by chance: there the filter leaves 0 of 65, the net fires and the 65
            // are used.
            //
            // Measured over the 15,360 maps: with the seeding filter, 7,388 stay below 8 red cells
            // and 987 are left with ONE. With GetFightWalkable, 15,354 have the 8. It is also the set
            // the rest of the fight already relies on -- moving and line of sight ask this same one --.
            //
            // The ToList copy is not cosmetic: GetFightWalkable returns MapManager's live HashSet,
            // and GeneratePlacementCells would get the map's data for good.
            fight.GeneratePlacementCells(PlacementGround(arenaMapId));

            // The four elementals IN FULL: what the player has put in points plus what the
            // equipment gives. They are worked out up here because initiative needs them whole.
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

            // To the fight map, and placement is NOT sent here.
            //
            // In the capture the server first does a whole map change -- kub, jru, lqu, hjk, lva --
            // and waits; the client answers with ijm and kmv, and only then do the jxg, the kba and
            // the rest arrive. Sending it before, the client is still on the surface map, has no
            // fight context and swallows it without a word: in the log placement can be seen going
            // out and on screen nothing happens.
            // Where he is standing on the roleplay map, BEFORE the line below sends him to the
            // arena. It used to be stored afterwards, and by then GameState.CellId was already the
            // arena's cell: at the end of the fight he was sent back to a cell that on the roleplay
            // map often DOES NOT EXIST -- in the Incarnam workshop, 189 on a map whose 42 cells go
            // from 244 to 414 -- and the client drew him nowhere.
            int casillaDeRol = GameState.CellId;

            GameState.MapId = fight.MapId;
            GameState.CellId = fight.Azul.Count > 0 ? fight.Azul[0].CellId : GameState.CellId;
            fight.ForgetPreparation(GameState.CharacterId);

            // Where he left from, to be able to go back. The fight map is an instance and is no
            // good as a place to leave the character.
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

            // Without saving the character: the fight map is an instance map and leaving it written
            // on the sheet would send him back there on logging in again, to a place there is no way
            // out of.
            //
            // That is not enough, mind: anything else that saves the character while the fight lasts --
            // buying, collecting kamas, raising characteristics -- writes it anyway, and since the end of
            // the fight is not done yet, the character stays trapped in the arena. That is why
            // <see cref="LeaveFight"/> sends him back, and the exit to the menu calls it.
            await SendFightEntryAsync(stream, fight);

            // When the placement countdown runs out it starts all the same. The client keeps it by
            // itself -- the real server sends not one timer between the cells and the ready button --
            // so here only the deadline is needed.
            //
            // This had three holes and all three were of the same kind: it wrote to the socket without
            // the lock, so its burst could interleave with that of whoever was serving the client and
            // split a frame in half; it sat for 45 seconds with no way of stopping it, so it survived
            // the disconnection and ended up writing to a closed socket; and it collected nothing, so
            // that failure was lost without a trace.
            //
            // The place to store the timer already existed -- FightInstance.PlacementTimerCts -- and
            // CancelPlacementTimer() is called from four places, but NOBODY ever assigned it: a null
            // was being cancelled. That was the missing half.
            StartPlacementCountdown(stream, fight, fight.Reglas.RelojDeColocacion * 100);

            // And now the rest of the world: his side and theirs before his board, the swords for
            // the map, the ilh for his party and the members who follow him in. See FightJoin.cs.
            await AfterFightOpenedAsync(fight, mobGroup, casillaDeRol);
        }

        /// <summary>Starts a fight's placement deadline.</summary>
        /// <remarks>
        /// It was written out inside <see cref="InitiateFightFromMobCollision"/> with the forty-five
        /// seconds put in the line. The koliseo has its own -- sixty, which is the 592 of the capture's
        /// kaa -- and a challenge has none, so the deadline becomes a parameter and whoever wants no
        /// clock simply does not call it.
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

                // The same lock the turn clock and what comes from the client use. Without it, pressing
                // «ready» right as the deadline ran out started the fight twice.
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


        /// <summary>Where the player came from on entering the fight, to send him back there.</summary>

        /// <summary>
        /// Takes the character out of the fight and sends him back to the surface map.
        ///
        /// It is needed because the end of the fight is not done yet: without this, whoever goes into
        /// a fight stays saved on the arena map, which is an instance, and on logging into the game
        /// again he appears in a place there is no way out of.
        /// </summary>
        public static void LeaveFight()
        {
            // Only HIS. It used to wipe the pending ending and stop the clock of the whole server,
            // so anybody going back to the character screen left somebody else's fight hanging and
            // with no clock.
            var mio = GetCurrentFight();
            if (mio != null)
            {
                mio.FinPendiente = 0;
                mio.CancelTurnTimer();

                // And the placement countdown, otherwise it stays alive for 45 seconds and ends up
                // writing to a socket that is no longer there.
                mio.CancelPlacementTimer();
            }

            var suyo = Network.SessionContext.State;
            if (suyo.RoleplayMapId == 0) return;

            suyo.IsInFight = false;
            suyo.FightId = 0;
            BackToRoleplayMap();

            // ONLY this player's fight. There was an _activeFights.Clear() here, which took down
            // everybody else's fights: when one finished his, the rest had their fight vanish
            // halfway through the screen.
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
            // And always on a cell that EXISTS on the map he goes back to, as the other four
            // teleports of this emulator do -- the zaap, the door, .teleport and the haven bag --,
            // which all go through here. The line above already stores the good cell, so this
            // normally changes nothing; it exists for the sheets already stored with an arena cell
            // from before this fix, which would otherwise enter the world invisible until taking a
            // step.
            if (suyo.RoleplayCellId != 0)
            {
                suyo.CellId = MapManager.GetNearestWalkableCell(suyo.MapId, suyo.RoleplayCellId);
            }

            DatabaseManager.SaveCurrentCharacter();

            suyo.RoleplayMapId = 0;
            suyo.RoleplayCellId = 0;
        }

        /// <summary>
        /// The fight waiting for the client to ask for the map's actors, or null.
        ///
        /// It is there so that a fight's jrh is answered with placement instead of with the map's
        /// normal jss.
        /// </summary>
        public static FightInstance? PendingPreparation()
        {
            var fight = GetCurrentFight();
            if (fight == null) return null;

            // Per fighter: in a challenge there are two clients asking for theirs, and one already
            // having placement says nothing about the other.
            if (fight.HasPrepared(Network.SessionContext.State.CharacterId)) return null;

            return fight.State == Jondo.Unity.World.Fights.FightState.Placement ? fight : null;
        }

        /// <summary>
        /// The fight's placement, as the real server sends it.
        ///
        ///   jxg   one per fighter
        ///   kba   the blue cells and the red ones
        ///   jzu   who is on each team
        ///   jwq   empty
        ///   jrk   the map where the fight takes place
        ///
        /// It is measured from the fifteen fight captures; the shapes and their byte-by-byte check
        /// against the capture live in <see cref="Network.FightProtocol"/>.
        ///
        /// During placement the enemy side travels entirely as -1: the real server does not hand out
        /// identifiers to the monsters until the fight really starts. Here it is done the same even
        /// though they already exist inside, because it is what the client expects to see.
        /// </summary>
        /// <summary>
        /// Sets up the fight of an accepted challenge: one player on each side.
        /// </summary>
        /// <remarks>
        /// The difference from a fight against monsters is not the number of participants, it is that
        /// there are TWO SESSIONS. Each character has his characteristics, his equipment and his socket,
        /// and all that is read from GameState, which belongs to the connection being served. That is
        /// why each half is built inside its own SessionContext.Push: reading the other's from here would
        /// give the same ones twice, which is a fight against a mirror.
        ///
        /// The one on the left goes to the blue team and the challenged one to the red, which is the
        /// only thing that makes this a duel and not two allies with nobody in front.
        ///
        /// The preparation phase is not touched: the engine already starts on «everybody ready» and not
        /// on a timer, so a duel starts when both press the button.
        /// </remarks>
        public static Task<bool> InitiateDuelAsync(GameSession challenger, GameSession target,
                                                   long mapId, int duelId = 0)
            => InitiatePvpAsync(new List<GameSession> { challenger },
                                new List<GameSession> { target }, mapId, duelId);

        /// <summary>
        /// Sets up a fight of PEOPLE against people: one against one or three against three.
        /// </summary>
        /// <remarks>
        /// A challenge is the one-against-one case and the koliseo is the same fight with more people
        /// on each side, so it is a single function. What makes it different from the monster one is
        /// not the number: it is that there are SEVERAL SESSIONS, each with its characteristics, its
        /// equipment and its socket. All that is read from GameState, which belongs to the connection
        /// being served, so each fighter is built inside his own SessionContext.Push. Reading them all
        /// from here would give the same character repeated.
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

            // The fight is identified by the CHALLENGE's id, which is what the capture does: the
            // 494 of the hqc reappears in the kam, in both kae and in the ilh.
            long fightId = pvpId != 0
                ? pvpId
                : System.Threading.Interlocked.Increment(ref _nextFightId);
            // The koliseo has its own arenas and does not fight in the one the roleplay map would
            // get. One is chosen at random among those with room for this team size: the duel ones
            // are small -- 37 of 85 with a single cell per side -- and a three against three does
            // not fit there. Without the arenas file, the usual one.
            var arenaKoliseo = koliseo ? Managers.KoliseoMaps.PickFor(blue.Count + blueBots.Count) : null;
            long arenaMapId = arenaKoliseo?.MapId ?? MapManager.ResolveArenaMapId(mapId);

            var fight = new FightInstance(fightId, mapId, arenaMapId)
            {
                Reglas = koliseo ? FightRules.Koliseo : FightRules.Desafio,
                KoliseoMode = koliseo ? koliseoMode : -1,
            };

            if (arenaKoliseo != null)
            {
                // The koliseo ones bring their real cells, marked in the client itself and
                // checked against the real server's kba. No splitting a list in half.
                fight.SetPlacementCells(arenaKoliseo.Blue, arenaKoliseo.Red);
                Program.LogDebug($"[Koliseo] Arena {arenaKoliseo.MapId} «{arenaKoliseo.Name}», " +
                                 $"{arenaKoliseo.Capacity} por bando.");
            }
            else
            {
                // The same cells as an ordinary fight, and for the same reason: the seeding filter
                // leaves two of an arena's seventy-seven and both would end up on top of each other.
                var enCombate = MapManager.GetFightWalkable(arenaMapId);
                fight.GeneratePlacementCells(enCombate != null && enCombate.Count > 0
                    ? new List<int>(enCombate)
                    : MobSpawnManager.GetInnerWalkableCells(arenaMapId));
            }

            // Who the fight is against, which is what the entry's kmu names. In a challenge it is
            // a person and not a group of creatures.
            fight.DefenderLeaderId = red.Count > 0 ? red[0].State.CharacterId : redBots[0].Id;

            var todos = new List<(GameSession Sesion, bool Azul)>();
            foreach (var sesion in blue) todos.Add((sesion, true));
            foreach (var sesion in red) todos.Add((sesion, false));

            foreach (var (sesion, retador) in todos)
            {
                using (SessionContext.Push(sesion))
                {
                    // If anybody had a fight left hanging, his is removed and only his.
                    long yo = GameState.CharacterId;
                    foreach (var par in _activeFights)
                    {
                        if (par.Value.EquipoDe(yo) >= 0)
                        {
                            _activeFights.TryRemove(par.Key, out _);
                        }
                    }

                    // Where he is standing on the roleplay map, BEFORE sending him to the arena. It
                    // is where he is sent back at the end, and it has to be read now: two lines below
                    // GameState.CellId is already the arena's cell, and sending somebody back to an
                    // arena cell leaves him in a place that does not exist on the roleplay map.
                    //
                    // And THE MAP IS HIS OWN TOO. In a challenge both are on the same one, so reading
                    // it from the fight made no difference; in a koliseo they do not know each other at
                    // all and each comes from his own. With the fight's, the rival went back to the map
                    // of the first one on the blue team: one was in Frigost, the other in Astrub, and
                    // both ended up in Frigost.
                    int casillaDeRol = GameState.CellId;
                    long mapaDeRol = GameState.MapId;

                    var luchador = BuildPlayerFighter(fight);
                    if (retador) fight.AddPlayer(luchador);
                    else fight.AddOpponent(luchador);

                    // The monster path did all this and here it was entirely missing, and without
                    // it the session keeps believing it is still on the roleplay map while the
                    // client is already in the arena: the other handlers answer it with the old map,
                    // and at the end of the fight there is nowhere to send it back to.
                    //
                    // The cell is HIS, not the first blue player's: the monster path could take
                    // Blue[0] because the blue team was a single person.
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

            // And the entry, to each through his socket and from his context: the first frame
            // names whoever leaves the map, so sending both from here would take the same one out twice.
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

            // And the clock, only in the koliseo: there the capture sends the kaa's f5 and here the
            // same has to run out as the client shows. Each his own, because each has his socket
            // and his fight in progress.
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
        /// How a PERSON is announced in placement, whichever side he is on.
        /// </summary>
        /// <remarks>
        /// The team-zero loop read the sheet of the player watching, which works when the only player
        /// in the fight is him. With two people the sheet of EACH ONE has to be read: the look and the
        /// name come from his row, not from the watcher's.
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

        /// <summary>A fighter's full sheet for the jxb, person or creature.</summary>
        /// <remarks>
        /// <see cref="BuildPlayerAppearance"/>'s twin for the start of the fight: that one makes the
        /// placement jxg and this one the jxb block, which is the same content with the sheet filled
        /// in. Both read the FIGHTER's sheet and not the watcher's, which was the bug.
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
        /// The fighter of WHOEVER is talking right now, read from his own session.
        /// </summary>
        /// <remarks>
        /// It was inside InitiateFightFromMobCollision because there was only one player per fight. In
        /// a challenge there are two, each with his characteristics and his equipment, and the only way
        /// to read the other's is to build his INSIDE his session context: all this comes from
        /// GameState, which belongs to the connection being served.
        ///
        /// The cell is not set here. Whoever puts him in a team decides it, since that is what knows
        /// whether he goes to the blue side or the red.
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
                // And for the same reason, the points: base plus equipment, from the same place the
                // sheet the player sees comes from. They were written here as 6 and 3, so a character
                // with +4 AP and +2 MP from equipment saw 10 and 5 on screen and then fought with 6 and 3.
                MaxAP = StatsHandler.GetPlayerMaxAp(),
                MaxMP = StatsHandler.GetPlayerMaxMp(),
                // The same initiative the sheet shows, and from the same place.
                //
                // It added only the INVESTED points and characteristic 44's bonus, so the elementals
                // the equipment gives did not count. And they are nearly all of it: on the test
                // character the equipment gives +730 strength and +760 agility, and neither counted.
                // Opposite, a monster DOES add its five base characteristics, so Pioch el Arenil got
                // more initiative than a level 200 and played first. Measured in the log: fight #1002,
                // nine fighters, «first -8», which is exactly the last one in the carousel.
                //
                // The comment that was here said exactly this -- that ignoring the items made the piwi
                // play first -- and fixed only half: it put in the initiative bonus and left out the
                // four elementals.
                Initiative = StatsHandler.GetPlayerInitiative(),
                Strength = fuerza,
                Intelligence = inteligencia,
                Chance = suerte,
                Agility = agilidad,
                // Power from the gear (characteristic 25). It feeds straight into damage.
                Power = StatsHandler.GetEquipBonus(25),
                // Critical hit from the gear (characteristic 18): the Turquoise Dofus gives +10.
                CriticalBonus = StatsHandler.GetEquipBonus(18),
                // The damage added at the end, without multiplying: general, critical and each
                // element's.
                FlatDamage = StatsHandler.GetEquipBonus(16),
                CriticalDamage = StatsHandler.GetEquipBonus(86),
                EarthDamage = StatsHandler.GetEquipBonus(88),
                FireDamage = StatsHandler.GetEquipBonus(89),
                WaterDamage = StatsHandler.GetEquipBonus(90),
                AirDamage = StatsHandler.GetEquipBonus(91),
                NeutralDamage = StatsHandler.GetEquipBonus(92),
                // The percentage resistances per element (characteristics 33 to 37). They were missing:
                // these five Fighter fields were only touched by the monster code and the summon code, so
                // for the player they stayed at zero and the fight panel showed 0% everywhere. And it was
                // not only cosmetic: the same zero reached the damage calculation, so the character took
                // the hits with no resistance at all.
                EarthResPct = StatsHandler.GetEquipBonus(33),
                FireResPct = StatsHandler.GetEquipBonus(34),
                WaterResPct = StatsHandler.GetEquipBonus(35),
                AirResPct = StatsHandler.GetEquipBonus(36),
                NeutralResPct = StatsHandler.GetEquipBonus(37),
                // Push (84 on equipment, which the client draws as 85) and range (19).
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

            // The attitudes his items give him: the six dofus and the trophies each give a
            // "spell" through their effect 1175, and those are the ones that do things at the start
            // of the turn or on taking a hit. That is where, without writing anything of its own, the
            // Ochre Dofus's action point comes from.
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
        /// ONE player's entry into the fight: he leaves the roleplay map and loads the arena's.
        /// </summary>
        /// <remarks>
        /// It was inside InitiateFightFromMobCollision, written for the only one fighting. In a
        /// challenge two fight, each on his socket and with his state, so it has to be possible to send
        /// it twice -- and to send it from each one's context, because the first frame names WHO leaves
        /// the map.
        ///
        /// The order is not for show. The kmp with f1 at one goes BEFORE telling it to load the map:
        /// it is what makes the client ask for the fight with an ijm instead of asking for an ordinary
        /// map with a jrh. Without it, it loads the board and stays in normal map mode.
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

            // Before anything else, where each one is. In the capture eight kmk come one after another
            // -- one per fighter -- right after loading the map and BEFORE the fight is announced. They
            // are what put people on the board; without them the client has a fight with fighters who
            // are on no cell.
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

            // How many challenges are chosen in this fight. It goes here, after the kmk and before the
            // first jxg, which is where the real server puts it; and it goes TWICE, with the same
            // number, because the original repeats it after the cells. The empty kwk only goes with
            // the first one.
            // Challenges are about fighting monsters: they give a bonus on their loot. In a challenge
            // between players there is no loot to multiply, and the capture does not carry a single one.
            if (fight.Reglas.HayRetos) await ChallengeHandler.SendCountAsync(stream, fight, primeraVez: true);

            // First of all, telling it THERE IS A FIGHT HERE. Without the kam the client has no fight
            // to hang what comes after on, and it can be seen blowing up in its own log when the jwq
            // reaches it, walking a list of fighters that does not exist. With the tactical map already
            // loaded and nothing drawn on it, which is exactly what used to happen.
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ijq,
                Network.FightProtocol.BuildMapReady()));

            // A challenge is announced differently, and this is measured in «enviar desafio y el otro
            // acepta»: its kam arrives «f3=challenged f5=id f6=challenger», WITHOUT a type and without
            // a list of monsters, because there are none in front.
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

            // And its kaa is six bytes with no countdown. In a challenge there is no placement clock:
            // it is not hidden, the real server simply sends none -- the fight starts when both press
            // ready and not when a time runs out.
            // The koliseo does have a clock: its kaa in the capture carries f5=592. The challenge does not.
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

            // Each with HIS look, read from his own sheet.
            //
            // There was another bug here of the same kind as the shared placement one, and it only
            // shows with two people on the same side or looking from the other: the look was built with
            // «character», which is the sheet of WHOEVER RECEIVES the frames, and it was given to
            // everybody on the blue team. Against monsters it made no difference -- only the watcher was
            // on blue -- but in a challenge, from the challenged player's client, the challenger was drawn
            // with the challenged player's breed, sex and head. In a three against three koliseo it would
            // be the three teammates cloned.
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
                // There can be a person in front. Announcing him as a creature is what made the client
                // draw the rival with a «???» and no bar of anything: it got a monster identity with id
                // zero, which does not exist in its table.
                if (!fighter.IsMonster)
                {
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxg,
                        BuildPlayerAppearance(fight, fighter)));
                    continue;
                }

                // Each with its own negative identifier, not all with the same: against four poutchs
                // the real server hands out -1, -2, -3 and -4.
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

            // And again how many challenges, with the same number. It is not a slip of the capture:
            // the real server sends it twice, here and before the kaa, in the seven captures that have
            // challenges.
            if (fight.Reglas.HayRetos) await ChallengeHandler.SendCountAsync(stream, fight);

            // After the cells, which is where the capture puts them: who is in the fight and the four
            // options.
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

            // The list of challenges does NOT go here. The client sends its panel settings (kwo) right
            // after receiving the jrk, and in the twelve real appearances the list ALWAYS arrives after
            // that kwo -- including the two the server sends without anybody asking --. It is fired from
            // ChallengeHandler.SettingsAsync.

            Program.LogDebug($"[Combate] Preparación del combate #{fight.FightId}: " +
                             $"{fight.Azul.Count} contra {fight.Rojo.Count}, " +
                             $"{fight.BluePlacementCells.Count} casillas azules y " +
                             $"{fight.RedPlacementCells.Count} rojas.");
        }

        /// <summary>
        /// Which way one faces: towards the enemy in front of him.
        ///
        /// In the capture the player comes out with orientation 5 and the poutch with 1, which is
        /// exactly facing each other on the cells they occupied; they are not constants. It is worked
        /// out from the centre of the opposing side, so it changes with the quadrant one gets placed in.
        ///
        /// The grid is diagonal -- each row goes down half a cell and odd rows are shifted -- so first the
        /// cell is turned into diamond coordinates and then the sign of the difference is looked at.
        ///
        /// The table that was here was TURNED TWO STEPS and that is why the character stood looking to
        /// one side instead of at the creature. The good one comes from two places that agree:
        ///
        ///   - From the geometry. On the diamond, x = (row - row%2)/2 + column and
        ///     y = (row + row%2)/2 - column. Lowering y with the row fixed is raising the column, that
        ///     is going to the RIGHT of the screen; raising x with the column fixed is going down a row,
        ///     that is going DOWN. And WorldMoveHandler.FacingFor says right is 0, down 2, left 4 and up
        ///     6. So -y = 0, +x = 2, +y = 4, -x = 6.
        ///
        ///   - From the captures. The player comes out facing 5 from 285 and from 271 with the poutch
        ///     on 270, and 3 from 284 with the four poutchs down to the right. All three come out with
        ///     this table and none with the old one.
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

            // The eight directions, by the sign of each axis.
            if (dx > 0 && dy == 0) return 2;   // abajo
            if (dx > 0 && dy > 0) return 3;    // down and to the left
            if (dx == 0 && dy > 0) return 4;   // izquierda
            if (dx < 0 && dy > 0) return 5;    // up and to the left
            if (dx < 0 && dy == 0) return 6;   // arriba
            if (dx < 0 && dy < 0) return 7;    // up and to the right
            if (dx == 0 && dy < 0) return 0;   // derecha
            if (dx > 0 && dy < 0) return 1;    // down and to the right
            return MonsterFacing;
        }

        /// <summary>The cell in diamond coordinates, which is how the board is laid out.</summary>
        private static (int X, int Y) Diamond(int cell)
        {
            int row = cell / 14;
            int col = cell % 14;
            int x = (row - (row % 2)) / 2 + col;
            int y = (row + (row % 2)) / 2 - col;
            return (x, y);
        }

        /// <summary>Facing the opposing side, which is what the real server does.</summary>
        private static int FacingOf(FightInstance fight, Fighter fighter)
            => FacingFrom(fighter.CellId, fighter.TeamId == 0 ? fight.Rojo : fight.Azul);

        /// <summary>When there is nobody to face, the one the monsters carry in the capture.</summary>
        private const int MonsterFacing = 1;

        /// <summary>
        /// The sheet that travels during placement.
        ///
        /// In the capture nearly every characteristic goes with its slot set and no number inside: the
        /// real values do not arrive until the fight starts. Here the same is sent, with the action and
        /// movement points, which are the only ones the client draws in this phase.
        /// </summary>
        private static List<(int Characteristic, long Base, long Gear)> PlacementSheetOf(Fighter fighter)
            => FullSheetOf(fighter);

        /// <summary>
        /// A fighter's sheet, so that the regression guard can compare it with the real server's. The
        /// game does not use it.
        /// </summary>
        public static List<(int Characteristic, long Base, long Gear)> FichaParaLaGuardia(Fighter fighter)
            => FullSheetOf(fighter);

        /// <summary>"Daños sufridos x#1%" (damage taken x#1%), the multiplier Represalias sets.</summary>
        private const int DanoSufridoPorCiento = 1163;

        /// <summary>Multiplier on the final damage dealt. Its base is 100.</summary>
        private const int DanoFinalInfligidoCaracteristica = 107;

        // The characteristics that go into the damage formula, with the catalogue's numbers.
        private const int PotenciaCaracteristica = 25;
        private const int CriticoCaracteristica = 18;
        private const int DanoFijoCaracteristica = 16;
        private const int DanoCriticoCaracteristica = 86;

        /// <summary>
        /// The TOTAL of a characteristic: what the fighter already carries -- base, scrolls and
        /// equipment, worked out when he was set up -- plus whatever the spells have put on him while
        /// the fight lasts.
        ///
        /// Buffs drop by themselves when their round comes, so the bonus is withdrawn without doing
        /// anything else, and since they live in the fight's fighter and not in the character, nothing
        /// stays stuck on going back to roleplay.
        /// </summary>
        /// <summary>
        /// What a characteristic is worth RIGHT NOW, buffs included, to send it on its own.
        ///
        /// The points go apart -- they are what he has left to play this turn -- and the rest comes from
        /// what the sheet has plus whatever has been put on top, which is exactly the same count the
        /// full sheet at the start of the fight does.
        /// </summary>
        /// <summary>A characteristic ready to go into a jxw, with its three slots.</summary>
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
            // The points are what he has left to play this turn, and they go in their own mould. And
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

            // THIS IS WHERE THE PREVIEW HOLE WAS.
            //
            // This was a list written BY HAND -- first two cases, then thirteen -- in parallel with the full
            // sheet sent when the fight starts. And the problem was never which ones were missing, but that
            // it was a separate list: the full sheet has fifty-three entries and the copy always fell
            // short. Whatever was not in it fell into ficha.Otra(), which returns zero, and since the jxw
            // sends the ABSOLUTE value, the zero overwrote on the client the good number that had already
            // reached it.
            //
            // What it cost, measured on our own traffic log: damage multiplier 107 comes out at 100 in the
            // initial sheet, and fifty-six milliseconds later a jxw overwrote it with 10 -- effect 1171 adds
            // ten to it and here the base came out zero --. The client estimates the hit by MULTIPLYING by
            // it, so the preview came out divided by ten. And the same with the other ten multipliers and
            // with element damage 88 to 92, which the last batch did not cover either.
            //
            // Now the value is looked up in THE SAME sheet that was sent, so the two cannot drift apart
            // again: whatever is added there is covered here without touching anything.
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

        /// <summary>The characteristic that feeds each element in the damage formula.</summary>
        private static int CaracteristicaDelElemento(Jondo.Unity.World.Fights.ElementType elemento)
            => elemento switch
            {
                Jondo.Unity.World.Fights.ElementType.Earth => 10,       // fuerza
                Jondo.Unity.World.Fights.ElementType.Fire => 15,        // inteligencia
                Jondo.Unity.World.Fights.ElementType.Water => 13,       // suerte
                Jondo.Unity.World.Fights.ElementType.Air => 14,         // agilidad
                _ => 10,                                                // neutral goes with strength
            };

        /// <summary>
        /// The critical roll: a number from zero to ninety-nine against the percentage.
        /// </summary>
        /// <summary>
        /// A number from 0 to 100 with decimals, for loot chances.
        ///
        /// It comes from the same Random as everything else and with the same lock. Drop percentages
        /// carry real decimals -- the red piwi boss's bag of lemons drops at 3% -- so rounding to an
        /// integer would change what drops.
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
                Look = look,
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

        /// <summary>The same numbers datos/characteristics.json uses.</summary>
        private const int ActionPointsCharacteristic = 1;
        private const int VitalityCharacteristicId = 11;
        private const int MovementPointsCharacteristic = 23;

        /// <summary>Plain range, the one that adds to ALL spells.</summary>
        private const int AlcanceCaracteristica = 19;
        private const int LifeCharacteristic = 0;

        /// <summary>
        /// The sheet with the real values, which is the one that goes in the jxb.
        ///
        /// The placement one carries thirty-six characteristics and this one fifty-three: the
        /// elementals, damages, critical, range, power and resistances are added, and initiative is
        /// taken out, since it only travels during placement.
        ///
        /// Here go the ones the client draws in the panel and the carousel. The ones not known are sent
        /// as zero, which is how those of a monster that really has them at zero travel.
        /// </summary>
        /// <summary>
        /// The rest of the character's sheet: what plays no part in any of the server's calculations but
        /// the client draws, and which went ALL at zero.
        ///
        /// Two of these are noticed playing: dodge and lock. With 170 agility one must have 17 dodge,
        /// which is plenty for a piwi not to lock you; sending zero, the client believed you were locked
        /// and warned that you would lose points by moving.
        ///
        /// The bases are the usual ones -- dodge and lock come from agility, the dodges from wisdom, at a
        /// tenth -- and the equipment part is set by GetEquipBonus, which since it adds per
        /// characteristic and with sign already knows all of these: 752 adds dodge and 754 subtracts it,
        /// 160 adds AP dodge and 162 subtracts it, and so on.
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
            Poner(27, sabiduria / PorCadaDiez);   // action point dodge
            Poner(28, sabiduria / PorCadaDiez);   // movement point dodge
            Poner(82, sabiduria / PorCadaDiez);   // retira PA: a tenth of wisdom, plus the gear's 410/411
            Poner(83, sabiduria / PorCadaDiez);   // retira PM: idem, 412/413
            Poner(12, GameState.TotalWisdom);     // wisdom
            Poner(49, 0);                         // flat heals
            Poner(26, 0);                         // invocaciones
            Poner(50, 0);                         // reflect
            // TEN, NOT ZERO. The sheet sent to the client already said (75, base 10) -- see
            // FullSheetOf -- while the server kept its own copy at zero: the client showed 10%
            // erosion and the server never eroded anybody, and every damage block on a player
            // went out without its f5. The one byte that told our hits apart from the real ones.
            Poner(75, Fighter.ErosionBase);       // erosion
            Poner(101, 0);                        // % damage resistance
            Poner(102, 0);
            Poner(95, 0); Poner(96, 0); Poner(97, 0);
            // Percentage resistances, one per element.
            foreach (int cual in new[] { 54, 55, 56, 57, 58 }) Poner(cual, 0);
            // Push: 84 is the DAMAGE done by pushing and 85 the RESISTANCE to being pushed. They
            // were swapped: push damage was sent in the resistance slot, and that is why the sheet
            // showed 130 in the resistance column.
            Poner(84, 0);
            Poner(85, 0);
        }

        /// <summary>
        /// The eleven damage multipliers, which the real server ALWAYS sends at a hundred.
        ///
        /// They are the reason the damage preview did not show. The client estimates the hit by
        /// multiplying by them, and whatever does not arrive is worth zero: any calculation multiplied
        /// by zero over a hundred gives zero, and a zero is not drawn. The player's real sheet carries
        /// all eleven, all with base one hundred, and so does the monster's.
        /// </summary>
        private static readonly int[] Multiplicadores =
            { 107, 150, 120, 121, 122, 123, 124, 125, 141, 142, 143 };

        /// <summary>
        /// The fighter's sheet, in the real jxb's order and with its fifty-three characteristics.
        ///
        /// It used to send twenty-one. The missing ones were not decoration: besides the eleven
        /// multipliers, 85 was missing -- fixed push, which is exactly what feeds the DISPLACEMENT preview
        /// of spells that push -- and range. And one was wrong: critical damage went in 86, which does
        /// not appear in any of the capture's fifty-three entries; the right one is 87.
        ///
        /// The same one is sent in placement and in the fight. The real server does too: in the
        /// placement jxg the player carries his values and only the monster goes with the slots empty.
        /// </summary>
        /// <param name="conTraza">
        /// The [FICHA] line of the log. It is off when the one asking is ValorDeFicha, which calls this
        /// once for each characteristic a buff moves: with the trace on it filled the log with copies of
        /// the same sheet several times per turn.
        /// </param>
        private static List<(int Characteristic, long Base, long Gear)> FullSheetOf(Fighter fighter,
                                                                                   bool conTraza = true)
        {
            var summonCharacteristic = SummonCharacteristicFor(fighter);
            var ficha = new List<(int, long, long)>
            {
                (ActionPointsCharacteristic, fighter.MaxAP, 0),
                (MovementPointsCharacteristic, fighter.MaxMP, 0),
                // The five percentage resistances, IN THE RIGHT-HAND SLOT.
                //
                // They went in the left one -- the invested points one -- and the real server sends them
                // in the equipment one: measured on the jxb of «combate contra 4 poutchs», where all
                // five come out as f7 and none as f2. Put in the wrong slot, the client reads zero
                // where there is something.
                (37, 0, fighter.NeutralResPct),
                (33, 0, fighter.EarthResPct),
                (35, 0, fighter.WaterResPct),
                (36, 0, fighter.AirResPct),
                (34, 0, fighter.FireResPct),
                (58, 0, fighter.Otra(58)), (54, 0, fighter.Otra(54)), (56, 0, fighter.Otra(56)),
                (57, 0, fighter.Otra(57)), (55, 0, fighter.Otra(55)),
                // Push and critical damage, which are among the ones the client needs to estimate.
                // 84 is push damage and 85 the resistance to it; and criticals go in 87, not in 86,
                // which does not appear in any entry of the capture.
                (85, 0, fighter.Otra(85)),
                (87, 0, fighter.CriticalDamage),
                (101, 0, fighter.Otra(101)),
                (27, 0, fighter.Otra(27)), (28, 0, fighter.Otra(28)), (93, 3, 0),
                (79, 0, fighter.Otra(79)), (78, 0, fighter.Otra(78)),

                // The WHOLE life, the player's as well.
                //
                // For a while only the life the level gives was sent, because with the sheet half
                // done -- twenty-one characteristics -- the client mixed ours with what it knew of the
                // items and the bar came out doubled. With the full sheet it no longer mixes: it keeps
                // ours, and sending only the level's left the character with 1,050 life in the middle
                // of the fight.
                (LifeCharacteristic, fighter.MaxHP, 0),

                (10, 0, fighter.Strength),
                // Vitality stays at zero ON PURPOSE, even though the real server sends it.
                // Measured twice: this client ADDS it to maximum life on top of what goes in
                // characteristic 0, and the character went into a fight with 7,856 life where he
                // has 4,453 -- exactly his 3,403 vitality too many --. Putting it back requires
                // first finding out exactly what it expects in 0.
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
                // 84, push damage, went here. The real server DOES NOT SEND IT: its sheet has 53
                // entries and 84 is in none of them, while 85 -- push resistance -- is. And we put it
                // right at position 33, which is where the elemental damages start (88 to 92), so
                // it was an entry the client does not expect, placed right in front of the five
                // that feed the damage preview.
                (88, 0, fighter.EarthDamage),
                (89, 0, fighter.FireDamage),
                (90, 0, fighter.WaterDamage),
                (91, 0, fighter.AirDamage),
                (92, 0, fighter.NeutralDamage),
                (95, 0, fighter.Otra(95)), (96, 0, fighter.Otra(96)),

                // 97 is the life he is missing, and it is the ONLY way the client has of knowing the life
                // of the character it controls: it works out the others' by itself from the hits.
                // It was nailed at zero, so the player's bar stayed full the whole fight.
                // It also goes here so that a reconnection to a fight in progress draws the right life.
                (Network.FightProtocol.TemporaryLifeMalus,
                 fighter.CurrentHP - fighter.MaxHP, -fighter.VidaErosionada),
                (102, 0, fighter.Otra(102)),
            };

            foreach (int cual in Multiplicadores) ficha.Add((cual, 100, 0));

            // TRACE of the sheet: it is WHAT THE CLIENT SEES to work out its damage preview. If
            // power, damages and elementals come out there with their right numbers and the preview
            // still shows only the base damage, then the one not doing the calculation is the
            // client, and what else it expects has to be looked at.
            //
            // Only the one of the character the player controls: the monsters' would fill the log
            // and is not the one being measured.
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

        /// <summary>A monster's look: the same block it carries on the map.</summary>
        /// <summary>
        /// A monster's look in a fight: the whole of it when its look string is known -- colours,
        /// bones, scale, skins, as on the map (ConnectionProtocol.MonsterLook) and as the real
        /// server sends it in jxg and jxb, 213 times with the scale in the fight captures --, the
        /// bones alone otherwise.
        /// </summary>
        private static byte[] MonsterLook(Fighter fighter)
            => string.IsNullOrEmpty(fighter.Look)
                ? Network.Pb.New().Var(2, 3).VarIfNotZero(3, fighter.LookBoneId).Build()
                : ConnectionProtocol.MonsterLook(fighter.Look).Build();

        /// <summary>The normal look the fighter had on entering this fight.</summary>
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
        /// Announces a transformation through fight action 149. Appearance zero brings back the normal
        /// look; the others keep colours, skins, scale and pets and replace only the root's bones.
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

            // Note and check in one go: if it was already there, THIS client has already been sent it
            // -- through here or through SendPreparationAsync -- and what is due is the short resend.
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
            // The same lock the turn clock uses: while what the client sends is being served, the
            // clock cannot push its burst in between, and the other way round. It belongs to this
            // session, so a stuck client only gets itself stuck.
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

            // Only these three are sent by the 3.6.10.10 client during placement, and they are
            // measured. The old code listened for jyz, jza, jub, jwe and jxw: the first with its letters
            // transposed and the others either nonexistent in this version or messages the SERVER
            // sends, not the client. The rest of the fight will come in here as it is deciphered.
            // Safety net for the pending end: if anything else arrives before the acknowledgement,
            // it is shown right away. That way a client that does not acknowledge does not leave the
            // fight hanging forever, and no timer writing to the socket on its own is needed, which
            // would interleave with what this same thread writes.
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
                // "Got the jxh": until this arrives, the turn does not start.
                await ConfirmAsync(stream);
            }
            else if (payloadStr.Contains("type.ankama.com/jxy"))
            {
                // Pass turn. It comes empty.
                await PassTurnAsync(stream);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Jrw)))
            {
                // Walking. It is the same message as outside a fight; here it spends MP.
                await WalkAsync(stream, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Jwh)) || payloadStr.Contains(Op.Uri(Op.Jwn)))
            {
                // Casting a spell, or hitting with the weapon if it carries no spell. Both messages
                // come in here: the jwh aims by cell and the jwn from the carousel, by fighter
                // identifier, and CastAsync knows how to read both.
                //
                // The jwn was at the outer door -- GameNodeProxy -- but NOT here, so it reached the
                // fight and no branch picked it up: it fell off the end of the if/else with no trace
                // at all. There are TWO routing chains, not one.
                await CastAsync(stream, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Jti)))
            {
                // The acknowledgement of each closed sequence. It carries no answer, but it is what
                // unlocks the end-of-fight screen when the last hit left it waiting.
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
            // The challenges. Four of the five carry no answer: the real server stays silent at
            // the kwv and the kwi, and only answers the kwr with the list and the kwj with the
            // locked-in challenge. See Handlers.ChallengeHandler.
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
                // Marking a candidate. CAREFUL: the first one to arrive is not a click of the player,
                // but the preselection the client makes by itself two milliseconds after receiving the
                // list. That is why marking locks in NOTHING: the kwj is needed.
                if (fight != null) ChallengeHandler.Mark(fight, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kwo)))
            {
                await ChallengeHandler.SettingsAsync(stream, fight, payload);
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kwi)) || payloadStr.Contains(Op.Uri(Op.Kxb)))
            {
                // Hovering over a challenge and the other panel setting. They carry no answer in any
                // of the 305 captures; they are picked up so that they do not show in the log as
                // unhandled packets.
            }
        }

        /// <summary>
        /// The client attacks a group of monsters (hqa).
        ///
        ///   f1: the group's contextual id, the same negative it travels with in the jss
        ///
        /// The server answers with an empty jsq and starts placement.
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
                // The group the player clicked, and only that one.
                //
                // Before, if the lookup by id failed, it fell into a mobs.FirstOrDefault() and the
                // player ended up fighting any group on the map: the first one on the list. And the
                // one that disappeared from the map on winning was that first one, not the one he had
                // clicked. With the ids now matching between the jss and the jpv the lookup should
                // never fail; if it fails, what has to be done is say so, not attack another one.
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
        /// The fight THE PLAYER OF THIS CONNECTION is in.
        ///
        /// It used to return <c>_activeFights.Values.FirstOrDefault()</c>, that is the first open fight
        /// on the whole server. With a single player it made no difference; with two, both drove the
        /// same fight: the second to come in moved the first one's pieces.
        ///
        /// Now it is looked up by the session's character. If the player is in none, there is no
        /// fight, which is correct and did not happen before either.
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

            // The client sends jzy, not jyz: the three letters were transposed in the old code, so
            // the branch was never entered.
            var (_, newCell) = Network.FightProtocol.ReadPlacementMove(payload);
            if (newCell == 0) return;

            // On whichever team. It was only looked for on blue, so in a challenge the challenged
            // player could not reposition: his request fell here without a word.
            var player = fight.Buscar(GameState.CharacterId);
            if (player == null) return;

            // And each on the cells of HIS side. It was always checked against the blue ones, which
            // against monsters is the same since there is only one person on blue.
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

            // The kmk says WHERE EACH ONE IS; it is not "this cell is freed". Everybody's position is
            // sent, which is what the real server does.
            //
            // I got this badly wrong the first time. In the capture, when the player repositioned a
            // kmk came out with two entries and one carried -1, so I read it as "the cell left goes
            // with nobody". But -1 is NOT nobody: it is the identifier of the FIRST MONSTER. That fight
            // had a single monster and it was exactly on that cell, so both readings fitted the same
            // bytes. Sent as "nobody", the client understood that monster -1 moved to the cell the
            // player had just left, and a piwi could be seen chasing him around the board.
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

            // Acknowledged, which is the only thing the real server answers to ready.
            //
            // To EVERYBODY, not just whoever pressed it. The kah is what draws the two crossed swords
            // over the portrait, and since it was sent only through the socket of whoever declared
            // himself ready, the other never found out: on his screen the rival was still unmarked.
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
        /// Starts the fight for real, with the burst the real server sends after ready:
        ///
        ///   kai   placement is over
        ///   jyy   the spell bar
        ///   jxz   which round it is
        ///   jxc   the cooldowns
        ///   jto   opens
        ///   jxb   ALL the fighters with the full sheet
        ///   jwi   closes
        ///   jxh   "confirm to me", and the client answers jwz
        ///
        /// The kai is the cut between the two phases: placement goes before it and the fight after.
        /// </summary>
        private static async Task StartFightAsync(FightInstance fight)
        {
            // The fight part, only once.
            fight.StartFight();
            fight.CancelPlacementTimer();

            // The placement is over: the swords go from the map (hpr). Nobody joins any more.
            await SwordsGoneAsync(fight);

            // And the whole burst to each one from his own context. It is sent whole and separately,
            // instead of splitting «this to everybody and this to whoever», because the order the
            // capture carries -- kai, jyy, jxz, jxc, jto, jxb, jwi, jxh -- mixes fight frames with
            // frames of whoever is watching, and splitting it would leave a client receiving the jxb
            // out of its own sequence.
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

        /// <summary>The starting burst, as ONE of the people in the fight sees it.</summary>
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

            // The challenges left unvalidated are closed by the server here, BEFORE the kai: if the
            // player declared himself ready with one marked and not validated, that one counts, and
            // if not even that, the server fills in. The ones the place imposes go after. Measured in
            // the anomaly.
            // Challenges are about fighting monsters: they give a bonus on their loot. In a
            // challenge between players there is no loot to multiply, and none was offered in the
            // placement phase -- filling them in here is where the challenge that appeared on its own
            // at the start of the fight came from.
            if (fight.Reglas.HayRetos) await ChallengeHandler.FillAsync(stream, fight);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kai,
                Network.FightProtocol.BuildFightBegins()));

            // And the final list, which goes between the kai and the jyy.
            if (fight.Reglas.HayRetos) await ChallengeHandler.SendFinalListAsync(stream, fight);

            // The spells fought with are the SAME the character has outside the fight, and with the
            // same guts: the usual hms carries f1 { f1: grade, f3: spell, f4: 1 } and the jyy repeats
            // it in its f6. It used to be read from the shortcut bar, which can be half filled, and
            // that is why the client showed every spell during placement -- its own, from before --
            // and went blank at the start of the turn, when our short list finally reached it.
            var spellLayout = Managers.FightSpellLayout.Current(character?.Breed ?? 0,
                                                                 GameState.CharacterLevel,
                                                                 SessionContext.Current.AccountId);
            var spells = spellLayout.Spells;
            var bar = spellLayout.Bar;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jyy,
                Network.FightProtocol.BuildSpellBar(me, spells, bar)));

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxz,
                Network.FightProtocol.BuildRound(FirstRound)));

            // The challenges that point out an enemy do it here, after the jyy, which is where the
            // three kwm of the captures come out.
            if (fight.Reglas.HayRetos) await ChallengeWatcher.FightStartedAsync(stream, fight);

            // The spells that are BORN with a cooldown: their grade's InitialCooldown. In the Paso de
            // Cacería capture the fight's first jxc carries {370:1, 373:1, 32469:1}, and in the
            // database those three are exactly the ones with InitialCooldown at one.
            // On whichever team, for the same reason: in a challenge the watcher can be on red and
            // was left without his initial cooldowns.
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

            // Everybody's full sheets. This is where the life, the level and the resistances that
            // travelled empty during placement arrive.
            // Each with HIS face and HIS identity, and a creature only whoever really is one.
            //
            // Here were the two halves of what was seen when a challenge started. The blue team was
            // built with «character» -- the sheet of whoever receives -- for ALL its members, so the
            // teammate was drawn with one's own face. And the red team was announced entirely as a
            // monster, with a monster identity and id zero: that is what turned the person in front
            // into a question mark as soon as the fight started, after having looked fine during
            // placement.
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
        /// "Confirm to me" (jxh). The server sends it before each turn and waits for the client's
        /// jwz; until it arrives, the turn does not start.
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

            // The question goes to both, so that both clients know a turn is starting. Of the two
            // answers, only the first does the work: see ConfirmAsync.
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxh,
                Network.FightProtocol.BuildConfirmTurn(deQuien != 0 ? deQuien : next.Id)));
        }

        /// <summary>
        /// The client has confirmed (jwz). Now the turn is given to whoever is next.
        /// </summary>
        /// <summary>
        /// The player's turn clock, and the lock that keeps it from writing over anybody.
        ///
        /// The turn NEVER ended: the client draws the countdown the jzc tells it, reaches zero and keeps
        /// counting into the negatives, because on the server's side there was nobody to take the turn
        /// away. There was a timer -- StartTurnTimer --, but it hung from a handler nobody calls, from
        /// 3.6.4.3's fight code, and on top of that it sent opcodes that do not exist in this version.
        ///
        /// The deadline is the SAME one that travels in the jzc, not a separate constant: there was one
        /// of 300 tenths living alongside the 400 actually sent, and it would have cut the turn ten
        /// seconds before what the client shows.
        ///
        /// The delicate part is that this writes to the socket from another thread. NetworkMessage
        /// splits each frame into TWO writes -- first the length, then the body -- with no lock, so two
        /// writers do not interleave message with message, but one's length with another's body, and
        /// the client loses the stream's synchronisation forever. That is why the clock and everything
        /// that comes from the client go through the same lock: while one writes its burst, the other
        /// waits.
        /// </summary>
        /// <summary>
        /// The turn of the session being served right now.
        ///
        /// It is ASKED for once and kept in a variable in each place that uses it, never called twice.
        /// If it were called to take it and again to release it, the second could resolve to another
        /// session -- the context is an AsyncLocal -- and a lock not held would be released while one's
        /// own stays closed forever.
        /// </summary>
        private static System.Threading.SemaphoreSlim MiTurno()
            => Network.SessionContext.Current.UnoCadaVez;

        /// <summary>
        /// Stopping ONE fight's turn clock.
        ///
        /// It was a static CancellationTokenSource, one for the whole server, so the second player to
        /// start a turn cancelled the first one's clock: only the last had a turn cut-off, and for the
        /// others, if they walked away from the keyboard, the fight never moved on. The right piece
        /// already existed unused -- FightInstance.TurnTimerCts -- and only dead code from the previous
        /// version touched it.
        /// </summary>
        private static void PararElReloj(FightInstance? fight) => fight?.CancelTurnTimer();

        private static void ArrancarElReloj(NetworkStream stream, FightInstance fight,
                                            Fighter quien, int decimas)
        {
            PararElReloj(fight);

            // A monster gets no clock: it plays by itself and hands over the turn itself. A summon a
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
                    // Everything may have changed while it waited: the fight may have ended, the turn may
                    // already be someone else's, or we may be in another round.
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

            // In a challenge both clients answer and the work of opening the turn is a single one.
            // The second jwz is let go without doing anything: otherwise expired summons are undone
            // twice and the points are given back twice.
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

            // To both. It is what switches on the turn clock, and since it went out only through the
            // socket of whoever confirmed, the other was left with the fight started and no countdown.
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

            // The expired buffs drop before the points are given back, so that what is given back is
            // what really belongs to this round. And each one has to be announced with its jya, which
            // is how the client removes them from the panel: by number, one by one.
            // ALL the buffs, not only those of whoever plays: the ones the player put on a piwi drop
            // all the same, and until now only whoever was starting his turn was swept.
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
                // And wrapped in their sequence. The notices went loose, bare, and the 3.6.10.10
                // client only applies what reaches it inside an open jto -- it is what it later
                // acknowledges with its jti --, so it was swallowing them: the buff dropped on the
                // server and stayed drawn on the panel forever. Measured over the captures: 5,091 of
                // the 5,098 real jya go inside a sequence; of ours, none.
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                    Network.FightProtocol.BuildSequenceStart(fighter.Id,
                                                             Network.FightProtocol.ActionSequence)));

                foreach (var (quien, caido) in caducados)
                {
                    // If the buff touched a spell's RANGE, its modifier is withdrawn first. The hnk is
                    // exactly that: the withdrawal. It is not the declaration that goes with the hnd,
                    // which is how it was first implemented and why giving range did nothing -- it was put
                    // on and taken off in the same burst --. Measured with a clock on «ocra-disparos
                    // lejanos»: on casting 68 hnd go out and ZERO hnk; on expiring 68 hnk and ZERO hnd. And
                    // in «ocra-tiro de repliegue» the 60 hnk live alone, right in front of the 61 jya.
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

                    // And HERE WAS THE HOLE: the panel row was deleted and the characteristic was not
                    // given back.
                    //
                    // The engine expires the buff fine -- Buffs.Barrer takes it out and ValorDeFicha
                    // already returns the right number -- but that number did not go out on the wire, so
                    // the client kept the last one it received: +250 power and -3 range nailed in, with
                    // the buff panel empty. And it survived fights, because nobody ever corrected it.
                    //
                    // The real server sends the sheet after EVERY jya. Measured in
                    // «ocra-tiros potentes», frames #205 to #222, with this very spell:
                    //     jya 289 -> jxw characteristic 19 (range) back to zero
                    //     jya 290 -> jxw characteristic 25 (power)
                    //     jya 291 -> jxw characteristic 84 (push damage)
                    //     jya 292 -> jxw characteristic 18 (critical %)
                    // The pattern repeats in 787 restored sheets in the Ocra and Combate folders.
                    //
                    // AP and MP do not go this way: those are given back as points, which is what
                    // GivePointsBackAsync does right below.
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

            // Whatever was put on the ground under his feet. It goes before giving him back his
            // points because a glyph that removes AP or MP has to bite into those of the turn that
            // starts, not those of the previous one.
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

            // Where he starts from and with how many MP: it is what is needed to judge, at the end,
            // the position challenges and the spend-exactly-one-MP one. It goes AFTER giving back
            // the points.
            ChallengeWatcher.TurnStarted(fight, fighter);
            await ChallengeWatcher.EnemyTurnStartedAsync(stream, fight, fighter);
            await ChallengeWatcher.AllyTurnStartedAsync(stream, fight, fighter);

            // His copies, if any, go before anything else of his turn: jto 6, the switch back
            // to visible, one 1029 each, jwi -- right behind the jzc in the capture.
            await DesvanecerLasIlusionesAsync(stream, fight, fighter);

            // And now the "start of turn" attitudes: this is where the Ochre Dofus checks whether he
            // has been hit since his previous turn.
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

            // The "you can play now" only goes if whoever plays is one this client controls. In a
            // monster's turn that step does not exist.
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

            // And the clock, with the SAME duration the client has just been told.
            ArrancarElReloj(stream, fight, fighter, duration + carried);

            // The monster plays by itself: there is nobody to press for it. And a summon with nothing
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
                // And a beacon hands over the turn AT ONCE: its spell has already done its part in the
                // start-of-turn attitudes and it has nothing else to play.
                //
                // This is where the fight hung. This called EndTurnAsync, which is the OLD generation of
                // packets -- jwk, jwu, juu -- and the 3.6.10.10 client does not understand it: the
                // beacon's turn never ended, the player could not pass it either and there was nothing
                // left but to abandon the fight. The right turn pass is PassTurnAsync, the same one the
                // monsters use.
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
        /// The points given back at the start of the turn.
        ///
        /// Without this, whoever spent his points stayed at zero forever: the server did give them back
        /// internally (Fighter.StartTurn) but told nobody, and the client kept drawing the last thing
        /// it got.
        ///
        /// It is wrapped as in the capture: a jto of 7 with the two sheets inside, first the movement
        /// points and then the action points, each in its jto/jwi of 3.
        ///
        /// What is NOT copied from the capture is the content. There both sheets go with an empty slot,
        /// because that server sends in that block the turn's MODIFIER and emptying it amounts to
        /// "nothing is missing any more". This emulator encodes something else: it puts in the ABSOLUTE
        /// value (see BuildFighterSheet, which with zero writes an empty f2 and with a number puts it in
        /// f5). Copying the empty slot as it was, the client understood zero and the turn started with
        /// 0 AP and 0 MP. So here go the maximums, which is what this encoding means.
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

            // In a fight it sends the ARENA's list of cells, not the walking one: the outer ring of
            // a fight map is not walkable even if it is on the normal map.
            var pisables = MapManager.GetFightWalkable(fight.MapId);
            if (pisables != null && !pisables.Contains(destination)) return;
            if (pisables == null && !MapManager.IsCellWalkable(fight.MapId, destination)) return;

            // Who is in the way, so as not to walk through them or end up on top of them.
            var ocupadas = new HashSet<int>();
            foreach (var otro in fight.Azul) if (otro.IsAlive && otro != walker) ocupadas.Add(otro.CellId);
            foreach (var otro in fight.Rojo) if (otro.IsAlive && otro != walker) ocupadas.Add(otro.CellId);
            if (ocupadas.Contains(destination)) return;

            // The WHOLE path, cell by cell.
            //
            // This is where the infinite movement points were. The client does not send the path:
            // it sends only the VERTICES, one for each change of direction. Walking ten cells in a
            // straight line is two vertices, and here «vertices minus one» was charged, that is ONE
            // point for ten cells. Crossing the map cost whatever turning cost.
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

            // And whatever reacts to walking, ONCE PER CELL. The Sentinel takes one range and two
            // percent of ranged damage on every step, not on every move: walking three cells at
            // once costs three, not one.
            for (int paso = 0; paso < steps; paso++)
            {
                await EngancheAsync(stream, fight, walker, Managers.EffectEngine.AlAndar);
                await EngancheAsync(stream, fight, walker, Managers.EffectEngine.AlUsarUnPM);
            }

            // And whatever was put on the ground where he ended up. It goes AFTER walking and the
            // triggers: first he arrives, and once on his new cell whatever was there goes off.
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
        /// Fires whatever is put on the ground under a fighter.
        /// </summary>
        /// <remarks>
        /// A single path for the four families -- aura glyph, turn-start glyph, trap and rune --,
        /// because the only thing that tells them apart is the moment, and the moment is this
        /// parameter. What they do is always the same: cast the spell they carry inside, with its
        /// grade, on behalf of whoever put it there.
        ///
        /// The trap is spent when it fires; the glyph stays until it expires. And sweeping happens at
        /// the end, not during the walk, because firing a glyph can move whoever stepped on it and leave
        /// him on top of another.
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
        /// Bomb walls: what lies between two lined-up bombs of the same Rogue.
        /// </summary>
        /// <remarks>
        /// It goes the same way as glyphs and at the same two moments -- on stepping in and on starting
        /// the turn inside -- because it is what the class sheet says it is: «Se trata de un glifo en el
        /// suelo que no bloquea los desplazamientos ni las líneas de visión. Una entidad que se
        /// desplace en el muro o entre en él sufrirá daños, incluso si empieza su turno en el
        /// interior» (a glyph on the ground that blocks neither movement nor line of sight; an entity
        /// moving in the wall or entering it takes damage, even if it starts its turn inside).
        ///
        /// But it is NOT a stored <c>Glifo</c>: a wall is a function of where the bombs are, so it is
        /// worked out on the fly. A bomb that dies takes its wall with it without anybody having to
        /// remember to delete it, and one pushed onto the line raises it on the spot.
        ///
        /// It is cast by the bomb with the MOST combo among those holding up the wall. That is
        /// inference: the sheet says the wall benefits from half the combo, but not from which one when
        /// the bombs are at different levels.
        /// </remarks>
        private static async Task ReconciliarLosMurosAsync(NetworkStream stream, FightInstance fight,
                                                           bool fireOnBirth = true)
        {
            var justBorn = new List<Jondo.Unity.World.Fights.Glifo>();
            // What OUGHT to be on the ground right now, cell by cell.
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

            // The walls that are laid down. They are recognised by their spell: no glyph of anything
            // else casts one of the four.
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
        /// The grade a wall hits with. The four wall spells have three, and no capture says which one
        /// the real server uses, so the highest goes and it is said so.
        /// </summary>
        private const int MuroGrado = 3;

        /// <summary>
        /// Casting a spell (jwh).
        ///
        /// The order is the one of the level 50 poutch capture, and it is not the one there was:
        ///
        ///   server   jto   opens
        ///            jwe   f14 300, what is cast and where
        ///            jto   opens a sequence of 3 just for the sheet
        ///            jxw   the action points the caster has left
        ///            jwi   closes it
        ///            jwe   f14 102, the action points spent
        ///            jwe   f14 89..100 for each one taking damage
        ///            jwe   f14 103 for each one left without life, AT THE END
        ///            jwi   closes
        ///
        /// What does NOT go: a sheet (jxw) with the life of whoever takes the hit. The real server does
        /// not send it -- the client subtracts the life from the hit itself -- and sending it applied it
        /// on the spot: the creature fell dead before either the spell or the damage was seen.
        ///
        /// If the jwh carries no spell it is a weapon hit, and then the type of the first jwe is 303.
        /// </summary>
        public static async Task CastAsync(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null || fight.State != Jondo.Unity.World.Fights.FightState.Ongoing) return;

            var caster = fight.CurrentFighter;
            if (caster == null || !caster.ControlledBy(GameState.CharacterId)) return;

            var (cell, spell) = Network.FightProtocol.ReadCast(payload);

            // If it does not come by cell, it comes THROUGH THE CAROUSEL: the client lets you aim by
            // pressing a fighter's portrait instead of his cell on the board, and then it sends another
            // message with his identifier. It is the only comfortable way of casting a buff on oneself,
            // and the emulator did not even listen to it: it fell into the drawer of unhandled packets.
            // It is resolved to a cell and goes on the same way as the other.
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

            // RANGE, which was not checked anywhere along the live path: anything could be cast at
            // any distance. That is why the buffs that give range seemed to do nothing -- it is not
            // that they did not add up, there was no limit to extend.
            //
            // Two things add up: characteristic 19, which is plain range, and the adjustments
            // pointing at THIS spell in particular, which is what Disparos Lejanos does.
            //
            // If the spell carries no maximum range in the database, nothing is checked: a missing
            // piece of data must not prevent casting.
            if (limites.AlcanceMaximo > 0)
            {
                int lejos = Jondo.Unity.World.Maps.MapGeometry.Distance(caster.CellId, cell);

                int minimo = limites.AlcanceMinimo
                           + caster.Buffs.DelHechizo(spell, Jondo.Unity.World.Fights.SpellAspect.AlcanceMinimo,
                                                     fight.RoundNumber);
                // The EQUIPMENT's range was missing. Only what the buffs give was added here
                // -- Buffs.De(19) -- so a character with range on his items did not see it anywhere:
                // characteristic 19 is sent to the client in the sheet, and the server ignored it
                // when checking whether the spell reaches.
                //
                // Minimum range is NOT touched: 19 extends how far you reach, not from where.
                int maximo = limites.AlcanceMaximo
                           + caster.Range
                           + caster.Buffs.De(AlcanceCaracteristica, fight.RoundNumber)
                           + caster.Buffs.DelHechizo(spell, Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo,
                                                     fight.RoundNumber);

                // Range TRACE: where each addend comes from. With this it can be seen at a glance
                // whether what fails is the equipment, the generic buff or the one aimed at this spell.
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

            // What prevents casting it again. None of this existed: any spell could be repeated
            // while action points were left, and 35 of the Cra's 44 have a per-turn cap, 13 a
            // per-target cap and 9 rounds of cooldown.
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

            // The per-target cap is counted against whoever is on the aimed cell. AREA spells,
            // which touch several, count short here: the engine's list of those affected would be
            // needed, and that is not hooked up yet.
            caster.LanzadosPorObjetivo.TryGetValue((spell, aQuien), out int sobreEse);
            int porObjetivo = Managers.SpellModifiers.CastsPerTarget(caster, spell, limites.PorObjetivo, fight.RoundNumber);
            int topePorObjetivo = porObjetivo > 0 ? porObjetivo + caster.ExtraCastsPerTarget : 0;
            if (aQuien != 0 && topePorObjetivo > 0 && sobreEse >= topePorObjetivo)
            {
                Program.LogDebug($"[Combate] El hechizo {spell} ya se ha lanzado {sobreEse} " +
                                 $"vez/veces sobre {aQuien}, y el tope es {topePorObjetivo}.");
                return;
            }

            // Is it a critical? It is rolled once per cast, against the sum of what the spell
            // brings and what the character carries. In the database, Flecha Helada has ten critical
            // of its own, and the Cra carries 47; the client draws 57% in its tooltip, which is
            // exactly the sum. Before, this was nailed to "false" and not a single critical came out,
            // not even by chance.
            // The EQUIPMENT's critical was lost entirely: a zero was passed as the base, so only the
            // spell's and the buffs' counted. The log said it out loud: «20 % = 20 of the spell + 0
            // of the character», with a character wearing 16. There is no risk of counting it twice:
            // CriticalBonus is only the equipment and Buffs.De(18) only the buffs.
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

            // Versatile (do not repeat an action) and the two about finishing off before changing target.
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
                        // Only when the hit is the weapon's. A spell carries f10 at zero, the same as the
                        // punch: what the client looks at to put the name is this.
                        arma: spell == 0 ? ArmaEquipada(caster) : 0,
                        noTarget: victim == null,
                        portals: proyeccion?.Chain.Select(p => p.Id).ToList()),
                    Network.FightProtocol.CastDetail)));

            // The sheet goes in its own sequence, as in the capture, not loose in the middle.
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

                // And what the spell leaves behind, which is not only damage: the AP Flecha Helada steals,
                // its three turns of basic damage, Disparos Lejanos's range...
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
        /// Brings a summon onto the board: a Cra beacon, a glyph, a trap.
        ///
        /// It is not a buff, it is a FIGHTER. It is handed a negative identifier, its sheet is built
        /// from the creature's template, it is put on the summoner's side and it enters the turn order.
        /// And its behaviour is not written here: it comes from its grade's <c>startingSpellId</c>,
        /// which is a spell full of 792 triggers -- "at the start of my turn cast my grade 2" --, the
        /// same machinery as the dofus attitudes. That is why the Survival Beacon heals itself and the
        /// Tactical one pushes by itself without a line written about either.
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
                // Only to the summoner, and only if he is the player. This is also reached from the
                // monster's turn and from a summon casting its own spell, and through those roads the
                // notice would go out through the player's socket anyway: it would tell him he has
                // reached a cap that is not his.
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

            // Does it get a turn? Only if its spell has something to do at its start. The Survival
            // Beacon has -- it heals itself -- and the Tactical one does not, it only reacts to what
            // happens around it; that is why in the captures the first plays and the second does
            // not appear even once in the carousel.
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

            // And after it, the list of fighters again: it is what registers the summon in the
            // client and puts it in the carousel.
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

            // Its spell becomes its attitude, and it is cast on the spot so that its triggers and
            // its countdown are in place.
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

            // And if it is a bomb, it is born at Combo I. One, not two: measured in «tymador-explobomba
            // resiliente», where the three bombs get ONE combo the round they come out and TWO
            // every round after. And that one comes out of its own spell: Encendimiento's
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

            // And if a wall went up with it, let it be seen.
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
        /// What raises each bomb's combo at the start of its Rogue's turn. Measured --
        /// "tymador-explobomba resiliente", three bombs over eight rounds, two each every round
        /// after the one they are born in -- and dealt by his class passive, whose 20577 carries
        /// exactly two ladder casts.
        /// </summary>
        internal const int CombosPorTurno = 2;

        /// <summary>
        /// One's cooldowns, for the jxc. ALL the ones ever set are named, even those already at zero:
        /// it is what the real server does, whose jxc keeps listing the same spells round after round
        /// with a zero next to them.
        /// </summary>
        private static IEnumerable<(int Spell, int Rounds)> RecargasDe(Fighter quien)
        {
            if (quien == null) yield break;
            foreach (var par in quien.Recarga) yield return (par.Key, par.Value);
        }

        /// <summary>
        /// Leaves the spell noted on whoever carries it, if it still has something to do.
        ///
        /// "Something to do" is having effects with a trigger other than "on cast". It is noted until
        /// the round in which the longest-lasting buff it put expires, which is what decides how long
        /// the spell stays alive.
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
        /// Fires whatever a fighter has pending for this moment: the spells he carries that react to
        /// what has just happened.
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

        /// <summary>Everybody in the fight, on both sides.</summary>
        /// <summary>Everybody in the fight. It lives in the fight itself since the sides have names.</summary>
        private static IEnumerable<Fighter> TodosLosCombatientes(FightInstance fight) => fight.Todos;

        // ═══════════════════════════════════════════════════════════════════════
        //  Who receives what happens in the fight
        // ═══════════════════════════════════════════════════════════════════════
        //
        // The whole engine was written for ONE player and one socket: each method receives the stream
        // of whoever has just sent something and answers him. Against monsters that is right -- the
        // only human in the fight is the one talking --, but in a challenge there are two and
        // everything that happens has to be seen on both screens.
        //
        // These two helpers are the missing piece. They do not convert the whole engine: what uses
        // the stream directly today still reaches only one, and that is said where it matters.

        /// <summary>The sessions of the people who are in this fight and still connected.</summary>
        /// <remarks>
        /// People only: a monster or a summon has no screen. They are looked up by their character id,
        /// which for a player is the same as the fighter's.
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

        /// <summary>The same frame to everybody in the fight.</summary>
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
                    // One having lost his socket cannot leave the other without his frame.
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
        /// A sheet (jxw) to everybody, with the «this one is yours» mark set for each.
        /// </summary>
        /// <remarks>
        /// The jxw carries a field that says whether the fighter it talks about is the one controlled by
        /// whoever receives it, and the client uses it to know which bar to apply the number to. So the
        /// frame is NOT the same for both: one has to be built per person.
        ///
        /// A single one was sent with that mark worked out against the session being served -- that of
        /// whoever had sent the frame that set all this off --, so the other received his own points
        /// marked as somebody else's, or the rival's marked as his. That is where «a Flecha Helada
        /// leaves Dragon-Lord with 2 AP» came from: the points drawn were not the watcher's.
        /// </remarks>
        private static Task FichaATodosAsync(FightInstance fight, long fighterId,
            params (int Characteristic, long Base, long Gear, long Buff)[] cambios)
            => ACadaUnoAsync(fight, sesion => WriteFrameAsync(sesion.Stream,
                   ConnectionProtocol.Push(Op.Jxw,
                       Network.FightProtocol.BuildFighterSheet(
                           fighterId, cambios, fighterId == sesion.State.CharacterId))));

        /// <summary>Each his own, built from his own session context.</summary>
        /// <remarks>
        /// It is needed for everything that comes from <see cref="GameState"/> -- the spell bar, the
        /// cooldowns, the characteristics -- because that belongs to the watcher and not to the fight.
        /// Pushing his session is the only way to read it: <c>GameState</c> reads the connection being
        /// served.
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
        /// Sends the list of fighters (jzu) again.
        ///
        /// The client keeps there ITS record of who is in the fight, and the turn carousel is indexed
        /// against that list -- the jzc's f7 is the position in it --. The emulator sent it ONCE, in
        /// placement, and never again; that is why a beacon summoned mid-fight did not appear anywhere
        /// even though its summoning packet was right, and a dead fighter never left the carousel.
        ///
        /// The real server sends it again whole after every summoning and every death: measured over
        /// the 37 Cra files, 18 jwe f14=181 plus 46 jwe f14=103 make 64 events, and in all 64 the next
        /// packet is a jzu. No exception.
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
                    // Without filtering by who it is: this is only reached from CastAsync, which is the
                    // player's cast. The filter is needed in InvocarAsync, which is reached from the
                    // monster's turn.
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
        /// Whether a spell has something to do at the start of the turn: an effect with a "TB" trigger,
        /// or a 792 hook whose chained grade has one.
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
        /// The cell a summon fits in: the one asked for, and if it is taken, the nearest free
        /// neighbour.
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

        /// <summary>Who takes the hit: the living enemy on that cell, if there is one.</summary>
        /// <summary>
        /// Telling the client that somebody has lost AP or MP, so that the little floating number
        /// appears over him as with life.
        ///
        /// Only when they are TAKEN: if the effect gives points, the measured message is a different one
        /// and it is not tied to a specific case, so nothing is sent rather than sending the wrong one.
        /// </summary>
        /// <summary>
        /// Refreshing the life the player is missing.
        ///
        /// Only to him: the client keeps everybody else's bar by itself, subtracting the hits it sees
        /// go by, and takes its own from the cap plus this characteristic. In the 305 captures there is
        /// not a single send of 97 for a monster or for the rival player.
        ///
        /// It is wrapped in its jto/jwi, like any loose sheet.
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
        /// Whoever is on the aimed cell, on whichever side.
        ///
        /// It used to look only at the OPPOSING side, so aiming at an ally -- or at oneself, which is how
        /// buffs are cast -- returned nobody: the per-target cap did not count and the target block did
        /// not travel. Who the effect reaches is decided afterwards by the spell's own mask, which is
        /// what it is there for.
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
        /// What a spell leaves behind, and telling the client.
        ///
        /// The engine decides what happens by reading the spell's EffectsJson and the effect catalogue;
        /// here it is only sent over the wire: a jxm for each buff, so that it appears on the panel, and
        /// a sheet for each characteristic that changed, so that the number is seen.
        ///
        /// Action and movement points are also touched ON THE SPOT, because a "+1 AP" or a "-2 AP" is
        /// not panel decoration: it changes what you have left to play that turn.
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

            // If the spell has something pending for later -- effects with a trigger that is not
            // "on cast" -- it is left noted on whoever carries it, to be able to fire it when the
            // time comes. It is what the Sentinel needs: its range drops only happen when walking,
            // and without remembering that the spell is still on there is no way.
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

            // Whoever this cast has moved, so that his ground is stepped on once they are all
            // in place. Not during the walk: a glyph can move whoever steps on it again,
            // and then the order stops making sense.
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

                // 141: it kills, and through the same road as a hit, so that it is announced the same,
                // the summons drop the same and the fight ends the same.
                if (c.Fulmina)
                {
                    if (!c.Sobre.IsAlive) continue;

                    Program.LogDebug($"[Combate] {(c.Caster ?? quienLanza).Id} fulmina a " +
                                     $"{c.Sobre.Id} con el hechizo {c.HechizoOrigen} " +
                                     $"({c.Sobre.CurrentHP} de vida).");

                    int dondeEstaba = c.Sobre.CellId;
                    await UnGolpeAsync(stream, fight, c.Caster ?? quienLanza, c.HechizoOrigen,
                                       c.Efecto, 0, c.Sobre, 0, 0, false, 0, fulmina: true);

                    // 405, «kill and replace»: the Reaping brings the creature out ON THE CELL of the one
                    // who has just fallen, not next to the caster. If the dead one has not left his place
                    // free, the summon looks for room like any other.
                    if (c.Invoca != 0)
                    {
                        await InvocarAsync(stream, fight, quienLanza, c.Invoca, grado,
                                           c.EnLaCasillaDelMuerto ? dondeEstaba : celdaApuntada,
                                           c.Efecto.EffectId);
                    }
                    continue;
                }

                // Life that goes without being a hit -- the «-N% HP» -- and life that is transferred.
                // They do not go through the damage engine on purpose: there is no element, nor
                // resistances, nor criticals to apply, and putting them through there would invent
                // all three for them.
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
                        // To EVERYBODY, not to the socket being served. Both sides have to see the
                        // bounce animation: against monsters it made no difference because there is only
                        // one person watching, but in a challenge or a koliseo the rival did not see
                        // from where to where the arrow jumps.
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
                    // TRACE to measure point removal: if the number seen on screen does not match
                    // this one, the mismatch is the client's and not ours.
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
                    // And ANY OTHER characteristic the buff moved: power, range, damages… Nothing was
                    // touched here, so a "+250 power" was noted in the engine -- and the damage really
                    // went up -- but the client's panel kept showing the old number, and it looked as if
                    // the buff did nothing.
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

                // Heals.
                if (c.Cura > 0)
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                        Network.FightProtocol.BuildHeal(quienLanza.Id, c.Cura, c.Sobre.Id)));
                    AnotarLaCura(fight, quienLanza, c.Sobre, c.Cura);
                    Program.LogDebug($"[Combate] {quienLanza.Id} cura {c.Cura} a {c.Sobre.Id}; " +
                                     $"queda en {c.Sobre.CurrentHP}/{c.Sobre.MaxHP}.");

                    // Heartless: healing oneself is fine, being healed by somebody else is not.
                    await ChallengeWatcher.HealedAsync(stream, fight, quienLanza, c.Sobre);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerCurado, quienLanza);
                    Disparo(quienLanza, Managers.EffectEngine.AlCurar, c.Sobre);

                    // The absolute sheet goes out once, after every consequence.
                    vidasCambiadas[c.Sobre.Id] = c.Sobre;
                    continue;
                }

                // The ones that bring a creature onto the board.
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

                // The ones that move somebody: where he ended up is announced.
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
                    // On the wire, a displacement ALWAYS travels as 5, whatever moved
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

                    // WHOEVER IS MOVED STEPS AS WELL. The ground only went off when walking, so
                    // putting somebody into a wall with a push or a pull did nothing to him
                    // -- neither the wall, nor a trap, nor a Feca glyph.
                    movidos.Add(c.Sobre);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerEmpujado, quienLanza);
                    Disparo(c.Sobre, Managers.EffectEngine.AlSerMovido, quienLanza);
                    // Drawn in (MA): Imantación's combo "solo si las bombas son desplazadas" by its
                    // pull. And the caster moved somebody else (PO).
                    if (c.Efecto.EffectId is Jondo.Unity.World.Combat.EffectSupport.Pull
                                         or Jondo.Unity.World.Combat.EffectSupport.PullToTargetCell or 1022)
                        Disparo(c.Sobre, Managers.EffectEngine.AlSerAtraido, quienLanza);
                    if (c.Sobre != quienLanza) Disparo(quienLanza, Managers.EffectEngine.AlDesplazarAOtro, c.Sobre);

                    // And the second, if the effect moved two. It is the swap of positions:
                    // without this announcement the client leaves the caster drawn where he was,
                    // and from then on its board and ours no longer agree on anything.
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

                // And the one who has NOT moved a single cell but has crashed all the same. The
                // engine returns a consequence without displacement, and the real server does the
                // same: in that case it does not send the movement message, only the hit's.
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

                // An effect with several triggers is announced once for each, which is what the real
                // server does.
                var (categoria, boost) = DatabaseManager.EffectFamily(c.Efecto.EffectId);
                int familia = Network.FightProtocol.FamiliaDelEmbrujo(c.Efecto.EffectId, categoria, boost);

                // The round IN WHICH IT DROPS, not what it has left: that is how the real server sends it.
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

            // And now the ground of whoever ended up on another cell. WHOEVER IS PUSHED STEPS AS
            // WELL: the ground only went off when walking, so putting somebody into a wall with a
            // push or a pull did nothing to him -- neither the wall, nor a trap, nor a Feca glyph.
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
        /// The range the buffs have changed, spell by spell (hnd and hnk).
        ///
        /// This was ENTIRELY MISSING, and it is the reason giving range did nothing. The jxm we already
        /// sent is byte for byte the real server's -- same f1, f4, f6, f8, f10, f14 -- but that one only
        /// feeds the effects panel: the client showed «Disparos Lejanos: +6 maximum range» and kept
        /// lighting up the same cells.
        ///
        /// The cells are worked out by the client with the hnd, one per spell touched and modifier.
        ///
        /// ONLY THE HND GOES HERE. The first version sent the hnk burst after it, believing it was the
        /// declaration that went with it, and that is why giving range did absolutely nothing: the
        /// modifier was set and in the same burst the client was told to delete it. The hnk is the
        /// WITHDRAWAL, and it goes when the buff expires -- it is in ConfirmAsync's expired loop --.
        ///
        /// Measured with a clock on «ocra-disparos lejanos»: on casting 68 hnd go out and ZERO hnk; on
        /// expiring, 68 hnk and ZERO hnd, and the cycle repeats the same four times. In
        /// «ocra-tiro de repliegue» the 60 hnk appear alone, right in front of the 61 jya, without an
        /// hnd nearby: the hnk lives on its own.
        ///
        /// The TOTAL the spell has now is sent, not what this buff has just added: that way two buffs
        /// on the same spell do not overwrite each other, and removing one leaves the right number.
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
        /// The passives and the attitudes, cast before the first turn.
        ///
        /// It is what the real server does and the emulator did not: between "ready" and the first turn
        /// it puts ten sequences with sixteen or nineteen casts and between fifty-five and seventy-six
        /// jxm. They are the character's passives -- Negro Ébano, La Sangre de Sacrogrito,
        /// Transposición... -- cast on the fighters. The client reaches turn one with each one's buff
        /// stack already built; here it arrived empty, and the effects panel with it.
        ///
        /// What is cast is the ATTITUDES the items give. There is no need to guess which: each equipped
        /// item's effect 1175 says so. And they fit the capture, because a passive and an attitude are
        /// the same kind of thing: in SpellTemplates, the spells cast by hand carry typeId 9 and none of
        /// these does -- the dofus go with 732 --.
        ///
        /// Each one goes in its own sequence, with the capture's shape: jto, the cast's jwe and whatever
        /// jxm come out, and jwi.
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
        /// The attitudes the items give, fired at their moment.
        ///
        /// Each dofus and each trophy gives a "spell" through its effect 1175, and that spell says when
        /// it does its thing. The Ochre Dofus, for instance, has an effect with the "TB" trigger --
        /// start of turn -- that casts its own grade 3, and that one is what gives the action point if
        /// one has not been hit.
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
                // An attitude's hook is ALWAYS on its grade one, not on the highest one the character
                // has open. The three grades of Amarillo Ocre are of minimum level 1, so asking for the
                // character's grade returned 3 -- the one that gives the action point -- and there is
                // no start-of-turn trigger there, so the attitude did nothing. The inner grades are
                // named by the hook itself. The one exception is an initial spell of the character's
                // own choices, held at his grade of the choice (see Buffs.GradoDeActitud).
                int grado = quien.Buffs.GradoDeActitud(actitud);
                await AplicarEfectosAsync(stream, fight, quien, actitud, grado, quien, disparador, quien.CellId, armar: false);

                // And the grades the attitude chains, on their own. It is needed because a chained
                // grade can bring effects with ITS own trigger: Amarillo Ocre's grade 3 is cast at the
                // start of the turn and gives the action point on the spot, but it also carries inside
                // a "remove the state" with an END of turn trigger, and that one has to be fetched
                // when the turn ends.
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
        /// A cast's damage, with the usual formula.
        ///
        ///   base × (100 + the element's characteristic + power) / 100
        ///   minus the fixed resistance, times (1 − resistance%)
        ///
        /// The calculation is done by <see cref="Jondo.Unity.World.Fights.DamageCalculator"/>, which was
        /// already written and is Dofus's; here only who gets it is chosen and the result is sent.
        /// </summary>
        /// <param name="tirada">The cast's draw of the random rows; drawn here when null.</param>
        internal static async Task HurtAsync(NetworkStream stream, FightInstance fight,
                                            Fighter caster, int spell, int grade, Fighter target,
                                            int celdaApuntada = -1, bool critico = false,
                                            IReadOnlyList<Managers.SpellEffect> tirada = null,
                                            string disparador = Managers.EffectEngine.AlLanzar,
                                            bool soloAlObjetivo = false)
        {
            // An area spell hits even if there is nobody EXACTLY on the aimed cell, so it is no
            // longer possible to leave here for lack of a direct target.
            if (target == null && celdaApuntada < 0) return;

            // The damage comes from the spell's EFFECTS, not from a flattened summary.
            //
            // It used to ask for a SpellCombatData that kept a single pair of dice, and only looked
            // at effects 96 to 100. That broke three things at once: Flecha Voraz hits with 94 --
            // fire steal -- and did not fit, Ojo de Topo with 91, and Tiro de Repliegue, which has
            // NOT a single damage effect, ended up taking life all the same.
            //
            // Now the engine is asked which blows this spell deals on this target. If it deals
            // none, nobody is touched here.
            var golpes = spell != 0
                ? Managers.EffectEngine.Golpes(fight, caster, spell, grade, target, celdaApuntada, critico, tirada,
                                               disparador, soloAlObjetivo)
                : (target != null ? GolpeDelArma(caster, target)
                                  : new List<(Managers.SpellEffect, int, Fighter, int)>());
            if (golpes.Count == 0) return;

            // The die is rolled ONCE per effect, not once per target: if a "25 to 30" spell rolls a
            // 26, 26 goes in at the centre of the area and the same 26, reduced by distance, goes
            // in for those around. Rolling per head, two creatures next to the centre would get
            // different numbers and the player would see an area that does not add up.
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
        /// The base damage coming from the effect's die: from <c>diceNum</c> to <c>diceSide</c>, both
        /// included. If there is no side, it is a fixed number.
        ///
        /// The AVERAGE used to be taken, so a 25 to 30 spell always hit 27 and in the game a hit was
        /// never seen to vary.
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
        /// The template of the weapon worn, or zero if bare-handed.
        ///
        /// It is what the client reads to say what you hit with. Without this every hit came out as
        /// «Puñetazo» (Punch) even though the damage and the element were the sword's.
        /// </summary>
        private static int ArmaEquipada(Fighter caster)
        {
            if (caster.Id != GameState.CharacterId) return 0;

            // The same slot GetEquippedWeaponAsSpell looks at: 1 is the hand.
            const int CasillaDelArma = 1;
            foreach (var pieza in GameState.GetInventoryCopy())
                if (pieza.Position == CasillaDelArma) return pieza.ItemId;
            return 0;
        }

        /// <summary>The equipped weapon's hit, which still comes from the usual summary.</summary>
        private static List<(Managers.SpellEffect Efecto, int Elemento, Fighter Sobre, int Lejos)>
            GolpeDelArma(Fighter caster, Fighter target)
        {
            var fuera = new List<(Managers.SpellEffect, int, Fighter, int)>();
            var arma = DatabaseManager.GetEquippedWeaponAsSpell(GameState.CharacterId);
            if (arma == null || (arma.BaseDamageMin <= 0 && arma.BaseDamageMax <= 0)) return fuera;

            // ONE HIT PER LINE. Before, a single one came out, with the line doing the most damage
            // and the effect number at zero, so a three-line weapon showed one figure in the chat
            // and the other two did not exist. The effect number matters: it is what makes the
            // client write «water damage» or «life steal», and the real server uses zero nowhere.
            //
            // The weapon hits a single target point-blank, so there is no distance to the centre.
            // The effect uid has to be different on each line or the die would be rolled once for
            // all three: whoever walks them groups them by that uid.
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

            // And if for whatever reason there are no lines, it hits with what there was: better a
            // hit than none.
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

            // The element is given by the catalogue: 0 neutral, 1 earth, 2 fire, 3 water, 4 air.
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

            // What came out of the die, rolled once for the whole cast -- turned to its
            // bottom or its top by a 781 on the caster or a 782 on the target, and scaled by the
            // MP he has left for the "% PM restantes" blows (EffectEngine).
            sacadoDelDado = Managers.EffectEngine.ConLosAzares(efecto, caster, target, fight.RoundNumber, sacadoDelDado);
            sacadoDelDado = Managers.EffectEngine.ConLosPMRestantes(efecto, sacadoDelDado, caster, fight.RoundNumber);
            int baseDamage = sacadoDelDado;

            // Except the ones that hit ACCORDING TO how much the target has eroded: there the die
            // is not the damage, it is the PERCENTAGE. Represalias carries effect 1092, "neutral
            // damage: 20% of the target's eroded HP", and against somebody untouched it does
            // nothing; against one who has had 300 of his cap eaten, it does 60.
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

            // And whatever has been added to THAT spell by a buff: effect 293, "+#3 basic damage".
            // Flecha Helada puts it on itself, so the second time it is cast it hits harder than
            // the first.
            int deEmbrujo = capturedSpellBonus ?? caster.Buffs.DelHechizo(
                spell, Jondo.Unity.World.Fights.SpellAspect.DanoBase, fight.RoundNumber);
            if (deEmbrujo != 0)
            {
                baseDamage += deEmbrujo;
                Program.LogDebug($"[Combate] El hechizo {spell} lleva {deEmbrujo:+#;-#;0} de daños " +
                                 $"básicos por embrujo: base {baseDamage}.");
            }

            // The fixed damages go at the END, without multiplying by the characteristic or by
            // power: the general ones of characteristic 16 plus those of the element being hit
            // with (88 to 92), and if the hit is critical, the critical damage (86) as well.
            int flat = ConBonos(fuente, DanoFijoCaracteristica, fuente.FlatDamage, fight.RoundNumber)
                     + (critical ? ConBonos(fuente, DanoCriticoCaracteristica, fuente.CriticalDamage, fight.RoundNumber) : 0);

            // And the area falloff: whoever is in the centre takes the whole hit and for each cell
            // of distance the percentage the spell says is taken off. It is applied BEFORE
            // characteristics and resistances, on the base damage, which is what the effect
            // describes.
            int enElBorde = Managers.EffectEngine.ConLaCaidaDeLaZona(baseDamage, efecto, lejosDelCentro);
            if (enElBorde != baseDamage)
            {
                Program.LogDebug($"[Combate] {target.Id} está a {lejosDelCentro} casilla(s) del centro: " +
                                 $"los daños base bajan de {baseDamage} a {enElBorde} " +
                                 $"({efecto.PasoDeCaida}% por casilla, tope {efecto.TopeDeCaida}).");
                baseDamage = enElBorde;
            }

            // THE CHARACTERISTICS WITH THEIR FIGHT BONUSES. This was the missing half of Tiros
            // Potentes: its +250 power was stored as a buff and announced to the panel, but the
            // damage formula kept reading the usual number, so the spell did not make hits harder.
            // A characteristic's total is the base, plus scrolls and equipment -- which already
            // came in the Fighter --, plus whatever the spells add while the fight lasts.
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

            // The bestial form puts +20 on characteristic 107, whose base is 100.
            // It is a final multiplier: it applies after characteristics/resistances and
            // before the target's damage-taken multipliers.
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

            // THE COMBO. It goes with the hitter's multipliers and not the receiver's, because it
            // is his: each combo makes the bomb blow up harder, from 0% at Combo I to 360% at Combo
            // XV. It is read from the state it wears and not from the buffs, which pile up one per
            // rung and would give 120% where 60% is due.
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

            // The MULTIPLIERS of whoever receives it: "damage taken x110%" is effect 1163, the one
            // Represalias sets. They go at the end, on the damage already worked out. Salto's is a
            // row under "D", read by any blow of the round.
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

            // KILL: the catalogue's effect 141, «Mata al objetivo» (kills the target). It is not a
            // very big hit, it is something else, and that is why it goes in HERE and not above:
            // neither the die, nor resistances, nor percentages can leave anybody at exactly zero.
            // The life he has is taken away and it goes on by the same road as any hit -- the
            // announcement, the death, the loot, the end of the fight --, which is the only thing
            // to share.
            if (fulmina) damage = target.CurrentHP;

            // THE SHIELD eats the hit before the life, and does not stop it all: what is left over
            // goes on its way. It goes before the clip to the remaining life, because a hit of two
            // hundred against a shield of one hundred and fifty is fifty of life, not two hundred.
            if (!fulmina && target.PuntosDeEscudo > 0)
            {
                int antesDelEscudo = damage;
                damage = target.PasarPorElEscudo(damage);
                Program.LogDebug($"[Combate] El escudo de {target.Id} se come " +
                                 $"{antesDelEscudo - damage} de {antesDelEscudo}; le quedan " +
                                 $"{target.PuntosDeEscudo} de escudo.");
            }

            // What is ANNOUNCED can never go beyond the life he has left. If a piwi with seventy
            // takes two hundred, the hit the player sees is seventy: above that there is no life to
            // take, and the number left over only confuses.
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

            // This is where Untouchable breaks -- if the one losing life is an ally -- and Elemental.
            await ChallengeWatcher.DamagedAsync(stream, fight, target, aplicado, caster, elemento);

            // And EROSION: besides the life of now, each hit takes a pinch off the cap.
            //
            // How much is given by the receiver's characteristic 75, called "Erosión" in the
            // client's catalogue and travelling in the fight sheet; in the Cra capture it is 10.
            // With a thousand life and a hit of a hundred, the creature is left at 900/990.
            //
            // Erosion works on the CALCULATED damage, not on the clipped one: hitting two hundred
            // on somebody with seventy life erodes by two hundred.
            int porcientoDeErosion = target.Otra(Fighter.CaracteristicaDeErosion)
                                   + target.Buffs.De(Fighter.CaracteristicaDeErosion, fight.RoundNumber);
            int erosionado = target.Erosionar(damage, porcientoDeErosion);
            if (erosionado > 0)
            {
                Program.LogDebug($"[Combate] {target.Id} se erosiona {erosionado} de vida máxima " +
                                 $"({porcientoDeErosion}% de {damage}); se queda en " +
                                 $"{target.CurrentHP}/{target.MaxHP}, {target.VidaErosionada} erosionados.");
            }

            // He has been hit, and the attitudes look at that: it is half of the Ochre Dofus rule.
            if (aplicado > 0 && caster.TeamId != target.TeamId) target.LeHanPegado = true;

            // f14 is THE EFFECT NUMBER, not an element code: 91 is water steal, 96 water damage, 99
            // fire damage... It was nailed at 91, so every hit was announced as water steal
            // whatever its element.
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

            // LIFE STEAL. Effects 91 to 95 are not plain damage: they are «water steal», «earth
            // steal», «air steal», «fire steal» and «neutral steal», and 82 is the fixed neutral
            // steal. All six hit like normal damage and ALSO heal the caster for half of what they
            // took.
            //
            // Here they were treated as damage and nothing else, so the Cra hit with Flecha Voraz
            // and did not heal. And it explains the weapon thing too: the character's sword
            // carries «[91, 0, 27, 33]», which is not water damage but water STEAL, and its main
            // hit was left half done.
            //
            // Half, rounding down, and never above the cap: whoever is at full life gains nothing.
            // It heals on what was APPLIED, not on what was calculated: if the target had twenty
            // left and the hit was three hundred, ten are stolen.
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

            // HurtAsync will send the life sheet only once, after all the spell's lines.
            // Here only this individual component is applied and announced.

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

            // The death, LAST. And without sending a sheet with life at zero first: the real server
            // does not send it, the client subtracts the life from the hit above, and sending it
            // made the creature fall dead before the animation was seen.
            if (!target.IsAlive)
            {
                // What goes off on his death has already gone off, above, with him standing.
                CarriedFollows(fight, target);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildDeath(caster.Id, target.Id)));
                Program.LogDebug($"[Combate] {target.Id} se queda sin vida.");

                // Level order, finishing with a weapon and falling next to an obstacle: all three are
                // judged here. The weapon is spell zero, which is how melee travels.
                await ChallengeWatcher.DiedAsync(stream, fight, target,
                                                 spell == Network.FightProtocol.HechizoCuerpoACuerpo,
                                                 caster);
                await ChallengeWatcher.AllyDiedAsync(stream, fight, target);

                await CaenSusInvocadosAsync(stream, fight, target);
                await ReenviarLaListaAsync(stream, fight);

                // And if the one who fell was a bomb, the wall it held up falls with it. Without
                // this the red cells stayed drawn until the next turn, which is what was seen after
                // a Detonador: dead bombs and the wall whole.
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
        /// The damage of having crashed when pushed, and what whoever served as the wall takes.
        ///
        /// The engine has already done the calculation -- see EffectEngine's push branch -- and here it
        /// is only charged: clipped by the remaining life, eroded, announced, and whether somebody died
        /// is checked. It is the same door a normal hit goes through, on purpose: killing by collision
        /// has to be announced the same as killing with an arrow.
        ///
        /// BOTH go in the same sequence and in this order -- first the pushed one with the whole hit,
        /// then the wall with half --, which is how the 9 measured pairs come out.
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

        /// <summary>A single collision hit, against a single target.</summary>
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

            // What is announced can never go beyond the life he has left, the same as in a normal
            // hit: above that there is no life to take.
            int aplicado = Math.Min(dano, quien.CurrentHP);
            if (aplicado >= quien.CurrentHP && !quien.Muriendo)
            {
                if (!await AlMorirAsync(stream, fight, quien, quienEmpuja)) return;
                aplicado = Math.Min(dano, quien.CurrentHP);
            }
            quien.TakeDamage(aplicado);
            AnotarElGolpe(fight, quienEmpuja, quien, aplicado, push: true);

            // Erosion is worked out on the WHOLE damage, not on the clipped one. Measured: in the
            // koliseo there is a hit of 663 announced as 417 -- clipped by the life -- and with
            // erosion at 66, which is a tenth of 663 and not of 417.
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
        /// Whoever dies loses ALL his summons, on the spot.
        ///
        /// It is not that they stop counting for the end of the fight: they disappear. A beacon does not
        /// outlive its Cra nor gets to play the turn it had pending.
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

            // And out of the carousel, so that their turn never comes.
            fight.RebuildTurnOrderOnFighterDeath();

            Program.LogDebug($"[Combate] Con {muerto.Id} se caen sus {suyos.Count} invocación(es): " +
                             $"{string.Join(", ", suyos.ConvertAll(f => f.Id.ToString()))}.");
        }

        /// <summary>
        /// A monster's turn: it gets closer and hits until it runs out of points.
        ///
        /// It is not much of an intelligence -- it goes for the nearest living enemy, gets next to him
        /// spending MP and casts whatever it can pay for with the AP it has -- but it does what it has
        /// to and respects the points, which is what the client checks.
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

            // And whatever was on the ground where it ended up. This was NOT there: the
            // monster changed cell and was announced, and that was the end of it. Neither
            // the bomb walls nor the traps nor the Feca glyphs ever went off on anybody
            // who was not a player.
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

            // And its identifier, so that its cast also says WHAT is cast.
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

                // And its effects, the same as when the player casts. This was missing: the monster's
                // turn only worked out damage, so the penalties its spells leave -- the range Picoteo
                // takes, for instance -- were neither applied nor announced, and nothing put by a
                // creature ever appeared on the player's panel.
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
        /// The cap on casts in a monster's turn.
        ///
        /// It is not a game rule: it is a safety screw. Since MaxCastPerTurn is respected, a spell with
        /// no limit is cast while there are action points, and although the cost is always greater than
        /// zero -- it is checked before -- it is better that an odd piece of data cannot leave the
        /// server spinning inside a piwi's turn.
        /// </summary>
        private const int TopeDeLanzamientosPorTurno = 20;

        /// <summary>
        /// How many cells there are from one to the other.
        ///
        /// This used <see cref="Diamond"/>, which is MIRRORED with respect to the client's grid: for even
        /// rows it flips the y axis, and for odd ones it also shifts the x. With those coordinates, the
        /// "four cells next to it" that came out were not the ones next to it, and that is why a piwi
        /// could be seen walking over another: it is not that it did not check whether the cell was
        /// taken -- it does --, it checked the wrong cell.
        ///
        /// The right grid is <see cref="MapGeometry"/>'s, which is what the fight uses for range, line of
        /// sight and pushes.
        /// </summary>
        private static int CellDistance(int from, int to) => MapGeometry.Distance(from, to);

        /// <summary>
        /// Whether a cell can be stepped on IN A FIGHT. The arena's list rules, not the walking one: the
        /// outer ring of a fight map is not walkable even if it is outside a fight.
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
        /// Is anybody left standing on both sides? If not, it is over.
        ///
        ///   kuf   it is over
        ///   jyg   how each one ended up
        ///   and back to the surface map
        /// </summary>
        /// <summary>
        /// The ending waiting for the client to acknowledge the last sequence, and the action number it
        /// expects. While it is set, the end-of-fight screen is up in the air.
        /// </summary>

        /// <summary>
        /// The client has acknowledged a sequence (jti). If the fight was waiting for exactly this one,
        /// now the ending can be shown.
        ///
        /// This is what was missing for the last hit to be seen. The fight ended inside the same hit that
        /// finished it, so the kuf and the jyg went out stuck to the spell's and the client showed the
        /// results screen before animating anything: neither the spell, nor the damage, nor the death.
        /// Waiting for the acknowledgement, the client has already swallowed the whole sequence.
        /// </summary>
        private static async Task AcuseAsync(NetworkStream stream, byte[] payload)
        {
            // THE FIGHT OF WHOEVER SENDS THE ACKNOWLEDGEMENT, not the last one left waiting on the
            // whole server: with two fights at once, one's acknowledgement closed the other and paid
            // the wrong person.
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

            // WHAT THIS DOES NOT DO, and it has to be written before somebody takes it for done:
            // it tells NOBODY ELSE. All the frames down here go out through the socket of whoever
            // gives up and through no other.
            //
            // Today it makes no difference, and that is the only reason it stays this way: in this
            // emulator there ARE NO two-player fights. AddPlayer is called from a single place --
            // the creation of the fight, with the session's character -- and there is no road that
            // puts a second player into somebody else's fight, not even by inviting him. Blue always
            // has exactly one.
            //
            // The day there is, this is what is missing and in this order: the death's jwe, the
            // resent list and the jto/jwi that wrap them have to go to the other participants as
            // well -- through their fight list, NOT through the map: two fights share an arena, see
            // SessionRegistry.Hears -- and the fight has to go on for them instead of ending.
            //
            // What ALREADY works for that day, and they are two different lists on purpose: the
            // ROUND is rebuilt by Agrupar, which filters by IsAlive, so whoever abandons never gets
            // the turn again; and the CAROUSEL is drawn by the client with the team list, which keeps
            // its dead, so he stays on screen in grey and the others' slots are not renumbered.
            var quitter = AbandoningFighter(fight, GameState.CharacterId);
            if (quitter == null)
            {
                Program.LogDebug($"[Fight] Ignored kme because character {GameState.CharacterId} " +
                                 $"is not an alive fighter in fight #{fight.FightId}.");
                return;
            }

            if (fight.CurrentFighter == quitter) PararElReloj(fight);

            // THE AUTHOR OF THE SEQUENCE IS THE FIGHTER WHOSE TURN IT IS, not the one giving up, and
            // the two captures seemed to contradict each other until looking at who dies inside:
            //
            //   poutch level 75  jto 08a28280c8e708 1005   author = the player, who is the only one
            //                    jwe dies   a28280c8e708   and is also the one whose turn it is
            //
            //   accept challenge jto 08a282f0a6c408 1005   author = the OTHER player
            //                    jwe dies   a28280c8e708   but the one who dies is ours
            //
            // So the one giving up is the one in the inner jwe, and the one wrapping is the turn's.
            // With quitter.Id in both, the second capture is contradicted.
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

            // And the jxh after it, which the three captures send there and the client answers with
            // jwz. In «aceptar desafio» that jwz is the ONLY acknowledgement that arrives: there is no
            // jti anywhere, so waiting only for the jti would leave the result screen never showing.
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxh,
                Network.FightProtocol.BuildConfirmTurn(author)));

            Program.LogDebug($"[Fight] Character {quitter.Id} abandoned fight #{fight.FightId}; " +
                             $"waiting for jti action {closure} before the result screen.");
            await CheckFightOverAsync(stream, fight, closure, alreadyAsked: true);
        }

        internal static Fighter? AbandoningFighter(FightInstance? fight, long characterId)
        {
            if (fight == null || fight.State != FightState.Ongoing) return null;
            // On both teams: in a challenge the challenged player is on red, and looking for him only
            // on blue his «abandon» found nobody and did nothing.
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

        /// <summary>What is sent when the fight really ends.</summary>
        private static async Task EndFightAsync(FightInstance fight)
        {
            // Who wins is a fact of the fight; «I won» depends on which side you were on. Against
            // monsters they are the same and that is why this was written with a single boolean,
            // but in a challenge the loser also has to get his screen and his return to the map:
            // without this he was left standing in the arena forever.
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

        /// <summary>A line of the results list.</summary>
        /// <remarks>
        /// The sheet -- level, experience and loot -- is only filled in for whoever receives the list: it
        /// is his and comes from his <c>GameState</c>. Of the others only who they are and whether they
        /// won is said, which is what the client needs to draw both sides.
        /// </remarks>
        private static Network.FightProtocol.FightResult FinDe(
            Fighter fighter, bool gano, bool esQuienMira, long xpGained,
            Network.FightProtocol.Spoils spoils, bool gane, Reward? suyo = null)
        {
            // A monster goes without a sheet: only who it is and whether it won.
            if (fighter.IsMonster)
            {
                return new Network.FightProtocol.FightResult { Fighter = fighter.Id, Winner = gano };
            }

            // A PERSON ALWAYS carries his level, whoever he is. In the real koliseo's jyg all four
            // entries -- the two who win and the two who lose -- carry their experience block with
            // their level inside: 227, 354, 447...
            //
            // Here only the watcher's was filled in, and the client understands that an entry
            // without a level is a monster. Since it had no monster to draw for the rival, on the
            // end-of-fight screen a question mark came out where his portrait should have gone.
            if (!esQuienMira)
            {
                // We do not have the rival's exact experience here -- the database sheet does not keep
                // it -- so the floor of his level goes, which is the only figure that does not lie:
                // the minimum one has to have to be at that level. His bar comes out empty, and that
                // is cosmetic; what was needed was the LEVEL, which is what tells a person from a
                // monster.
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

        /// <summary>The end of the fight as ONE of the people who were in it lives it.</summary>
        /// <param name="gane">Whether whoever receives this won.</param>
        /// <param name="azulGana">Whether the blue team won, which is what goes in the results list.</param>
        private static async Task TerminarParaUnoAsync(NetworkStream stream, FightInstance fight,
                                                       bool gane, bool azulGana)
        {
            bool alliesAlive = gane;

            // What is won. The experience is what each monster declares in its sheet (gradeXp),
            // which is the same the client shows when hovering over the group; there is no made-up
            // formula. Kamas and items, whatever each one drops.
            bool won = alliesAlive;

            // The challenges, before everything about the end: the real server sends its kwl a few
            // frames ahead of the jyg, and in a defeat it sends them all one after another right there.
            // What it returns is the bonus of the met ones, added up, as a percentage.
            int extraDeRetos = await ChallengeWatcher.FightEndedAsync(stream, fight, won);

            // And the quests that asked for defeating something, for the same reason: this is where
            // it is known that it really fell, and the client is not trusted on that.
            await QuestWatcher.FightEndedAsync(stream, fight, won);

            // And what the achievements count, set aside until the character is back on the map.
            AchievementWatcher.FightEnded(fight, won);

            // And this is where it is applied. On the wire it does NOT travel broken down: the
            // percentage only exists inside the placement's ldd, and the final figure arrives with
            // the bonus already added. The 68 jyg of the captures were reviewed and there is no slot
            // where a breakdown fits, so it is the server that has to apply it before sending the number.
            // Without the summons. The sum went over the WHOLE opposing side, and a summon goes into
            // it with its level set: a monster that summons was paying kamas for creatures it made
            // itself during the fight. Experience did not notice because a summon carries no
            // XpReward, but kamas did.
            // In a challenge nothing is won: no experience, no kamas, no items. Without this, the
            // winner collected kamas for the rival's level as if he were a monster.
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

            // The koliseo pays ITS OWN. It does not come in through the above because there are no
            // monsters in front to get experience, kamas or a loot table from: the koliseo pays for
            // winning, and it is kolichas and vitorichas. The loser gets nothing, not even experience
            // -- in the capture's jyg his block goes WITHOUT the won field, not with a zero --.
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
                    // Five characteristic points per level, as in TotalCapitalForLevel, but only up to 200.
                    // From there up the table keeps counting -- 201 is Omega 1, and the capture's 354 is a
                    // 200 with Omega 154 -- and what each Omega gives is not measured, so the level goes up
                    // and nothing is handed out. Rather than make it up, nothing.
                    int upToTwoHundred = Math.Max(0, Math.Min(newLevel, MaxLevelWithPoints)
                                                     - Math.Min(GameState.CharacterLevel, MaxLevelWithPoints));
                    if (upToTwoHundred > 0) GameState.CharacterRemainingPoints += upToTwoHundred * 5;
                    Program.LogDebug($"[Combate] ¡Sube de nivel! {GameState.CharacterLevel} -> {newLevel} " +
                                     $"(+{upToTwoHundred * 5} puntos).");
                    GameState.CharacterLevel = newLevel;

                    // And the window, which is what the player expects to see on levelling up.
                    await WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kua, ConnectionProtocol.BuildLevelUp(newLevel)));
                }
            }
            if (kamas > 0) GameState.Kamas += kamas;
            if (xpGained > 0 || kamas > 0 || loot.Count > 0) DatabaseManager.SaveCurrentCharacter();

            var spoils = new Network.FightProtocol.Spoils { Kamas = kamas };
            foreach (var kv in loot) spoils.Items.Add((kv.Value, kv.Key));

            // Who won goes in absolute terms -- blue or red -- and not «me or the other»: the list is
            // the same for both clients and each looks for himself inside. It went with «won», which
            // is the watcher's, so in a challenge the loser received the list with the winners
            // swapped around.
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

            // AND NOW THE CLIENT IS TOLD IT HAS THEM.
            //
            // This was entirely missing, and it is the reason the loot was stored fine and seen
            // nowhere: the end-of-fight screen drew it -- the jyg -- but nobody told the client that
            // those items had gone into the inventory, so they did not appear until the next login.
            // With the Jondo Coin it was plain as day: 73 units in the database and none in the bag.
            //
            // The real server sends one iua per item. Measured in the jalató dungeon: four 17-byte
            // iua with f3{f1:63, f5{gid, quantity, uid}}. 63 is the bag.
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

            // The character's sheet again, otherwise the client keeps the FIGHT one on returning
            // to the map: it came out with the action points left at the end -- four -- and with
            // the fighter's life.
            //
            // And it goes through opcode kub, which is this version's sheet. It used to be sent
            // through "kri", and that does not exist: zero appearances in the 295 captures of all
            // the folders, against 672 of kub. So the packet went out from here and the client
            // never picked it up, and that is why the previous fix changed nothing.
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

            // The group just killed disappears from the map, and another comes out in its place.
            //
            // This was written in SendFightEnd, which nobody calls: its three callers hang from
            // methods that in turn nobody calls. The ending that really runs is this one, and it
            // did not delete the group. In the log it is clear: twelve fights won and ZERO
            // «removed from map» lines, and the same group starting three fights in a row within
            // the same run. So on returning to the map the dead group was still drawn, with its
            // same id, and could be attacked again: infinite experience, kamas and loot from the
            // same group.
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

                // In a dream room it is NOT replaced: the room is cleared and stays cleared, which
                // is what makes advancing mean something. Replacing it would leave the player fighting
                // the same room forever.
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

            // And back to the map he left from, since the arena one is an instance.
            long back = Network.SessionContext.State.RoleplayMapId != 0
                ? Network.SessionContext.State.RoleplayMapId
                : fight.RoleplayMapId;
            LeaveFight();

            // A loser does not go back where he fought: he goes to his save point, beside its
            // zaap. Set after LeaveFight, which puts him back on the map he left. A prisoner
            // does not: a lost fight is no way out of jail, and he goes back to his cell.
            if (defeat != null && MapManager.GetMapInfo(defeat.SavePointMap) != null
                && !Managers.Jail.IsJailed(Network.SessionContext.State.CharacterId))
            {
                back = defeat.SavePointMap;
                Network.SessionContext.State.MapId = defeat.SavePointMap;
                Network.SessionContext.State.CellId = defeat.SavePointCell;
                DatabaseManager.SaveCurrentCharacter();
            }

            // Was the fight inside a dungeon? Then winning moves: to the next room, or out if it
            // was the last. It is decided HERE and not after this is over, because the jru below
            // already names a map and the client answers that one: a later teleport would be eaten
            // by the kkr that comes back.
            //
            // Both things have to be touched, `back` and the state, because `back` was read before
            // LeaveFight() wiped the roleplay map. Changing only one leaves the client loading a
            // map and the server believing it is on another.
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

            // If this was a dream room, the state has changed -- the room is done and the points
            // went up -- and it has to be said before reloading the map, or the window will keep
            // showing the old ones until the room changes. Only then: any other
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
        /// The life points the level gives, without vitality: fifty to start with and five per level.
        /// It is the same calculation StatsHandler.GetPlayerMaxHp does before adding anything.
        /// </summary>
        private static int LifeFromLevel(int level) => 50 + (Math.Max(1, level) * 5);

        /// <summary>What casting something costs when it is not known: a spell's ordinary cost.</summary>
        private const int DefaultCastCost = 3;

        /// <summary>
        /// The last level that hands out characteristic points. The client's table goes up to 1889,
        /// but from 201 on that is Omega: the capture's 354 is a level 200 with Omega 154.
        /// </summary>
        private const int MaxLevelWithPoints = 200;

        /// <summary>
        /// The grade of a spell the character has open: what it costs and its identifier.
        ///
        /// It comes from SpellLevels, which is where the client itself takes it from to draw the number on
        /// the icon; if the spell is not there, the ordinary cost is charged and there is no identifier.
        ///
        /// BOTH are needed. The cost, to subtract the action points; and the SpellLevels.Id, because the
        /// cast's jwe carries it next to the spell's and without it the client does not know what it is
        /// drawing. They go together in the same query so that they cannot come from different rows.
        /// </summary>
        private static (int Cost, int LevelId, int Grade) GradeOf(int spellId, int level)
        {
            var todo = LimitesDe(spellId, level);
            return (todo.Cost, todo.LevelId, todo.Grade);
        }

        /// <summary>What a spell costs and what limits it, all from the same row.</summary>
        public readonly record struct LimitesDelHechizo(
            int Cost, int LevelId, int Grade,
            int PorTurno, int PorObjetivo, int Intervalo, int EsperaInicial,
            int CriticoPropio, int AlcanceMinimo = 0, int AlcanceMaximo = 0,
            bool NeedFreeCell = false, bool NeedTakenCell = false);

        /// <summary>
        /// The casting limits, which come from the same SpellLevels columns the cost comes from:
        ///
        ///   MaxCastPerTurn     how many times per turn          MaxCastPerTarget  and per target
        ///   MinCastInterval    rounds until it can be repeated  InitialCooldown   the starting wait
        ///
        /// The GRADE matters and that is why it goes into the key: Paso de Cacería goes from three
        /// rounds of interval at its grade one to two at grades two and three, and with the cache keyed
        /// only by spell the first character to cast set the number for all the others.
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
                // There was a silent catch here. The interval columns are not in the emulator's CREATE
                // TABLE, so a regenerated database would lose them and every spell would start costing
                // three action points without anybody noticing.
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
        /// Each spell's limits per grade, already read.
        ///
        /// It was a normal Dictionary, and several sessions write to it at once: two fights casting
        /// different spells at the same instant can catch the dictionary halfway through resizing, and
        /// that does not give an exception -- it gives an infinite loop inside the Dictionary itself, with
        /// the thread eating a whole core forever --. It is the nastiest concurrency failure there is in
        /// .NET because it leaves no trace: no exception, no log, just a server getting worse and worse.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Hechizo, int Nivel), LimitesDelHechizo> _grades
            = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), LimitesDelHechizo>();

        /// <summary>
        /// The player passes the turn (jxy, empty), or his time runs out.
        ///
        ///   jyt   the turn is over
        ///   jto / jxc / jwi   the closing block
        ///   jxh   and on to the next one
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

            // Stopping THIS fight's clock: if the turn is passed by hand it must not fire afterwards.
            PararElReloj(fight);

            var ending = fight.CurrentFighter;
            if (ending == null) return;

            // The end of the turn belongs to the FIGHT, not to whoever presses it: the four frames
            // that follow talk about the fighter who finishes and both have to see them.
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

            // Whatever the attitudes have to do at the end of the turn. This is where Amarillo Ocre
            // takes off the "I have been hit" state, so that the next turn checks it clean again.
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

            // Cooldowns go down one round WHEN their owner's turn ENDS, and the closing jxc already
            // carries them lowered: measured in the Agudeza Absoluta capture, cast in round 8 with
            // interval 4 -- that turn's jxc says 3, round 9's says 2, round 10's one, round 11's zero
            // and it is cast again in round 12 --. Eight plus four, twelve.
            foreach (var hechizo in new List<int>(ending.Recarga.Keys))
            {
                if (ending.Recarga[hechizo] > 0) ending.Recarga[hechizo]--;
            }
            // The position challenges are judged HERE, with the one finishing still where he finished
            // and his MP not restored. It goes before clearing the turn counters, which Versatile uses.
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

            // If this one closes the lap, a new round starts and it has to be said: the jxz is what
            // makes the little number of the carousel go up, and without it it stays stuck at 1 forever.
            bool wasLast = fight.CurrentTurnIndex >= fight.TurnOrder.Count - 1;
            fight.NextTurn();

            if (wasLast)
            {
                // The round is raised by fight.NextTurn() on going round the order; here it is only
                // announced. A separate static counter used to be raised as well, and they were two
                // different numbers following each other.
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxz,
                    Network.FightProtocol.BuildRound(fight.RoundNumber)));
                Program.LogDebug($"[Combate] Empieza la ronda {fight.RoundNumber}.");

                // Unpredictable points out another enemy on every global turn.
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

            // WARNING: this is NOT how one walks in a fight, even if the name suggests so.
            //
            // Walking in a fight is the jrw, and WalkAsync handles it. Measured on the capture
            // «combate contra 4 poutchs nivel 25»: the client sends jrw fourteen times and jzy ONCE,
            // the one for placing itself before starting. This function is only reached with a jzy
            // when the fight is already under way, and the client does not send that.
            //
            // It looked for «jyz» -- the z and the y swapped, the same slip that had already appeared
            // in HandlePlacementCellChangeRequest -- and since ExtractMessagePayload compares the whole
            // url exactly, it always returned null. The letters are corrected and it is left: it costs
            // nothing and fixed it does not fool the next one to read it. What must NOT be done is to
            // take it for the movement handler, which is exactly what misled the audit.
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

            // 21. 44, initiative. It was at base 5 and bonus 12, which are the numbers of the capture's
            // character. The fighter's goes, split the same as in the roleplay sheet: what was invested
            // in the base and the equipment's in the bonus. The monster sends it empty, which is what
            // the capture does.
            //
            // It comes from THIS session's GameState, like everything else in this method: with several
            // players in a fight it will have to come from the fighter himself.
            if (!fighter.IsMonster) AddBaseBonusVal(44, StatsHandler.IniciativaInvertida(),
                                                        StatsHandler.IniciativaDelEquipo());
            else AddStatEntry(44, new ProtoMessage());

            // 22. STATID 0 = LIFE POINTS / MAX HP! (statId = null -> omitted f5)
            //
            // Careful with what goes here in the PLAYER's case: the life points that do NOT come from
            // vitality, that is the fifty to start with plus five per level. Vitality is added by the
            // client by itself, since it already knows its items, and whatever we send here is ADDED to
            // it.
            //
            // It was seen in two places at once. Sending GetPlayerMaxHp() -- which now does include the
            // equipment, since LoadInventory reads the effects properly -- the character went into a fight
            // with 8556 life instead of 4803: vitality counted twice, the client's and ours. And before
            // that, when the equipment was worth zero, the BASE vitality was still left over: outside a
            // fight it said 4803 and inside 4806, three too many, which are exactly the three Vitality
            // points of the sheet. With the level's bare life both cases add up.
            //
            // Monsters go the other way round: there their whole life does go, because the client knows
            // nothing about them.
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

        // There was a second Random here -- _lootRandom -- with no lock, while the other one (_dado)
        // had one. Random is NOT thread-safe: two fights rolling at once do not get the same
        // number, they leave the internal state a mess and from then on it returns ZEROS forever.
        // With the loot that is a server where nothing drops and nobody understands why. It was
        // removed and now both rolls come from the same place, with its lock.

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
        /// <summary>Raises a quantity by the percentage the challenges have given.</summary>
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

            // SUMMONS do not pay. They go into their summoner's side with IsMonster set, so a
            // monster that summons would put its creature into this loop: it would take its own
            // loot table and, with the coin, it would be a money factory that opens by itself.
            // They are told apart by the Invocador, which only they have.
            foreach (var monster in fight.Rojo.Where(m => m.IsMonster && !m.EsInvocado))
            {
                // The server's coin. It ALWAYS drops, without rolling the die: it is not an item of
                // the monster's table, it is what the fight pays. The quantity comes from the level, in
                // steps of 25 (see Managers.JondoCoin).
                int monedas = Managers.JondoCoin.RewardFor(monster.Level);
                loot.TryGetValue(Managers.JondoCoin.TemplateId, out int llevadas);
                loot[Managers.JondoCoin.TemplateId] = llevadas + monedas;

                var table = DatabaseManager.GetMonsterDrops(monster.MonsterId, monster.GradeIndex);
                foreach (var drop in table)
                {
                    // In the loot the bonus raises the PROBABILITY of a drop, not the quantity: it is
                    // one roll per item and per monster, and what the challenge improves is the luck.
                    double probabilidad = extra > 0
                        ? Math.Min(100.0, drop.PercentDrop * (100.0 + extra) / 100.0)
                        : drop.PercentDrop;
                    if (TirarPorcentaje() >= probabilidad) continue;
                    loot.TryGetValue(drop.ObjectId, out int q);
                    loot[drop.ObjectId] = q + 1;
                }

                // And the GLOBAL table, which is the other one it has and that was not being read. That
                // is where the raids' loot lives: the nine monsters of the Sima carry not a single row
                // in their own table and carry fifty-five in this one -- the salt of the depths and the
                // seven gems --, so without this a raid is a place where nothing drops.
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
        /// Whether this player gets a row of the global table, according to the criterion it carries.
        /// </summary>
        /// <remarks>
        /// What is not known does NOT drop, and that is half of why this works. Nearly every monster of
        /// the game carries the anomaly fragments row with the criterion
        /// <c>(HA=50|HS=3383)&amp;Az=1&amp;Pm!28049666</c>, of which we cannot answer a single letter: it
        /// comes out Unknown, it does not drop, and the whole world stays as it was. The raid ones carry
        /// no criterion at all, so they drop.
        /// </remarks>
        private static bool SeLoLleva(string criterion)
        {
            if (string.IsNullOrWhiteSpace(criterion)) return true;
            return Jondo.Unity.World.Content.Criterion.Met(criterion,
                Managers.GuildRaidManager.ResolverFor(GameState.CharacterId));
        }

        /// <summary>
        /// Puts the loot in the inventory and keeps BOTH views up to date.
        /// </summary>
        /// <remarks>
        /// It was inside <see cref="RollFightLoot"/> and comes out of there because the koliseo pays its
        /// own without going through the monsters' tables and needs exactly this. Having two roads that
        /// hand out items and only one refreshing the views is the known way for the loot to be stored
        /// fine in the database and the player not to see it.
        ///
        /// There are two views of the inventory: <c>GameState</c> is the session state's, and the one
        /// BuildInventory reads to build the ivx is <c>Managers.Equipment</c>. Refreshing only the first
        /// left the second with the old one until the next login -- 73 Jondo Coin in CharacterItems and
        /// not one on screen.
        ///
        /// All at once and not one by one: each AddItemToInventory loaded the whole inventory to see
        /// whether the item was already there, so five different items were five reads.
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
