using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The test harness's routes, /api/test/...: what tools/JondoPlay asks the server so that a
    /// real client can be played and its spells tried without a person at the keyboard.
    /// </summary>
    /// <remarks>
    /// Asked for on 2026-11-08: a tool that launches the real client, plays every class against
    /// the Puchs and tells which spells work. These routes are there ONLY when the server was
    /// started with JONDO_TEST_KEY in its environment -- the harness starts its own server, on its
    /// own data root, with a key drawn for that run -- and every call must carry that key. A server
    /// started by hand has no test routes at all.
    ///
    /// The harness has its own account, made here with a password nobody knows: it is given the
    /// launcher's token and the client's launch straight away, so no credential travels.
    /// </remarks>
    public static partial class ControlApi
    {
        /// <summary>The key of this run, or empty when the test routes are off.</summary>
        internal static readonly string TestKey = Environment.GetEnvironmentVariable("JONDO_TEST_KEY") ?? "";

        /// <summary>The harness's account; slot n's is jondoplayNN, so that n clients play at once.</summary>
        internal const string TestLogin = "jondoplay";

        /// <summary>The slot this request speaks for, read from its body: 0 is jondoplay itself.</summary>
        [ThreadStatic] private static int _testSlot;

        private static string TestLoginNow => _testSlot <= 0 ? TestLogin : $"{TestLogin}{_testSlot:D2}";

        private const int TestTimeoutMs = 20000;

        private static Respuesta? TestRoute(string ruta, string cuerpo)
        {
            if (!ruta.StartsWith(Prefijo + "test/", StringComparison.Ordinal)) return null;
            if (TestKey.Length == 0) return Mal(404, "ruta");
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(Texto(cuerpo, "key")),
                                                         System.Text.Encoding.UTF8.GetBytes(TestKey)))
                return Mal(401, "key");

            _testSlot = (int)Math.Clamp(Numero(cuerpo, "slot"), 0, 99);
            try
            {
                return TestDispatch(ruta.Substring(Prefijo.Length + "test/".Length), cuerpo);
            }
            finally
            {
                _testSlot = 0;
            }
        }

        private static Respuesta TestDispatch(string route, string cuerpo)
        {
            return route switch
            {
                "launch" => TestLaunch(cuerpo),
                "status" => TestStatus(),
                "classes" => Bien(SpellTable.ClassBreeds),
                "token" => TestToken(),
                "characters" => TestCharacters(),
                "create" => TestCreate(cuerpo),
                "session" => TestSession(),
                "fight" => TestFight(),
                "groups" => TestGroups(),
                "spells" => TestSpells(cuerpo),
                "inject" => TestInject(cuerpo),
                "cast" => TestCast(cuerpo),
                "place" => TestPlace(cuerpo),
                "walk" => TestWalk(cuerpo),
                "stage" => TestStage(cuerpo),
                "spell" => TestSpell(cuerpo),
                "refresh" => TestRefresh(cuerpo),
                "endturn" => TestEndTurn(),
                "scenario" => TestScenario(cuerpo),
                "scenarios" => TestScenarios(cuerpo),
                "team" => TestTeam(cuerpo),
                "state" => TestState(cuerpo),
                "items" => TestItemList(cuerpo),
                "item" => TestItem(cuerpo),
                "equip" => TestEquip(cuerpo),
                "finish" => TestFinish(cuerpo),
                "crit" => TestCrit(cuerpo),
                "kill" => TestKill(cuerpo),
                _ => Mal(404, "ruta"),
            };
        }

        // ─── The account and the client's launch ─────────────────────────────────────────────

        private static long TestAccount()
        {
            string login = TestLoginNow;
            long id = DatabaseManager.AccountIdOf(login);
            if (id > 0) return id;
            // Sixteen bytes: an account's password is 32 characters at the most.
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            string nickname = _testSlot <= 0 ? "JondoPlay" : $"JondoPlay{_testSlot:D2}";
            if (!DatabaseManager.RegisterNewAccount(login, password, nickname, "127.0.0.1", out string error))
                throw new InvalidOperationException(error);
            DatabaseManager.SetAccountRole(login, Roles.Administrador, out _);
            return DatabaseManager.AccountIdOf(login);
        }

        /// <summary>A launcher token and a client launch for the harness's account: what the launcher gets by logging in.</summary>
        private static Respuesta TestLaunch(string cuerpo)
        {
            long account = TestAccount();
            // The harness's client of before was closed from outside, with no word to the launcher:
            // its launch is let go, as the launcher's "fin de lanzamiento" would.
            if (ClientLaunchRegistry.TryGetByAccount(account, out var previous) && previous != null)
                ClientLaunchRegistry.Remove(previous);
            string token = Guid.NewGuid().ToString("N");
            DatabaseManager.SetGameToken(account, token);
            DatabaseManager.SetLauncherToken(account, token);
            ClientLaunchRegistry.RegisterToken(account, token);
            string language = Texto(cuerpo, "language");
            var launch = ClientLaunchRegistry.Register(account, token, Guid.NewGuid().ToString("N"),
                                                       language.Length == 0 ? "es" : language, "127.0.0.1");
            return Bien(new { account, token, instance = launch.InstanceId, hash = launch.Hash, role = DatabaseManager.GetAccountRole(account) });
        }

        /// <summary>A launcher token for the harness's account alone, for the administrator routes, with the client already open.</summary>
        private static Respuesta TestToken()
        {
            long account = TestAccount();
            string token = Guid.NewGuid().ToString("N");
            DatabaseManager.SetLauncherToken(account, token);
            ClientLaunchRegistry.RegisterToken(account, token);
            return Bien(new { account, token });
        }

        private static Respuesta TestCharacters()
        {
            long account = TestAccount();
            return Bien(DatabaseManager.GetCharactersByAccountId(account, DatabaseManager.DefaultServerId)
                .Select(c => new { c.Id, c.Name, c.Breed, c.Level }).ToList());
        }

        /// <summary>A character of a class for the harness, at a level, made as a creation makes one.</summary>
        private static Respuesta TestCreate(string cuerpo)
        {
            int breed = (int)Numero(cuerpo, "breed");
            int level = (int)Math.Clamp(Numero(cuerpo, "level"), 1, 200);
            int sex = (int)Numero(cuerpo, "sex");
            if (!SpellTable.ClassBreeds.Contains(breed)) return Mal(400, "breed");
            long account = TestAccount();
            string name = Texto(cuerpo, "name");
            if (name.Length == 0) name = $"Prueba-{breed:D2}";
            if (DatabaseManager.CharacterNameTaken(name)) return Mal(409, "name");
            long id = DatabaseManager.CreateCharacter(account, DatabaseManager.DefaultServerId, name, breed, sex,
                                                      HeadTable.DefaultHeadId(breed, sex), new long[] { -1, -1, -1, -1, -1 },
                                                      CharacterCreationHandler.StartingMap, level,
                                                      CharacterCreationHandler.StartingKamas, CharacterCreationHandler.ScrolledStat,
                                                      CharacterCreationHandler.StarterItems);
            return id == 0 ? Mal(500, "create") : Bien(new { id, name, breed, level });
        }

        // ─── What the harness's character sees ───────────────────────────────────────────────

        private static GameSession? TestSessionOnline()
        {
            long account = DatabaseManager.AccountIdOf(TestLoginNow);
            return SessionRegistry.All().FirstOrDefault(s => s.AccountId == account && s.IsInWorld);
        }

        /// <summary>How far the slot's client got: connected to the game, and in the world.</summary>
        private static Respuesta TestStatus()
        {
            long account = DatabaseManager.AccountIdOf(TestLoginNow);
            var sessions = SessionRegistry.All().Where(s => s.AccountId == account).ToList();
            return Bien(new { account, connected = sessions.Count > 0, inWorld = sessions.Any(s => s.IsInWorld) });
        }

        private static Respuesta TestSession()
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            var s = session.State;
            return Bien(new
            {
                character = s.CharacterId, name = s.CharacterName, breed = s.Breed, level = s.CharacterLevel,
                map = s.MapId, cell = s.CellId, inFight = s.IsInFight, fightId = s.FightId, kamas = s.Kamas,
            });
        }

        private static object FighterView(FightInstance fight, Fighter f) => new
        {
            f.Id, f.Name, team = f.TeamId, cell = f.CellId, monster = f.IsMonster ? f.MonsterId : 0, f.Breed, f.Level,
            hp = f.CurrentHP, maxHp = f.MaxHP, ap = f.CurrentAP, maxAp = f.MaxAP, mp = f.CurrentMP, maxMp = f.MaxMP,
            // The points as the client shows them: in hand for whoever is playing; for anybody
            // else, what the next turn starts with (the maximum and the rows on top), as the
            // fight sheet tells it (FightHandler.HuecosDeFicha).
            apShown = fight.CurrentFighter == f ? f.CurrentAP : Math.Max(0, f.MaxAP + f.Buffs.De(1, fight.RoundNumber)),
            mpShown = fight.CurrentFighter == f ? f.CurrentMP : Math.Max(0, f.MaxMP + f.Buffs.De(23, fight.RoundNumber)),
            alive = f.IsAlive, summon = f.EsInvocado, bot = f.IsBot, drivenBy = f.DrivenBy, owner = f.Invocador,
            attitudes = f.Buffs.Actitudes.ToList(),
            buffs = f.Buffs.Puestos.Select(b => new { b.Numero, effect = b.EffectId, spell = b.HechizoOrigen, by = b.Quien,
                                                      value = b.Valor, state = b.Estado, untilRound = b.CaducaEnRonda }).ToList(),
        };

        private static object? FightView(GameSession session)
        {
            var fight = FightHandler.FightOf(session.State.CharacterId);
            if (fight == null) return null;
            return new
            {
                id = fight.FightId, state = fight.State.ToString(), round = fight.RoundNumber, map = fight.ArenaMapId,
                turn = fight.CurrentFighter?.Id ?? 0,
                order = fight.TurnOrder.Where(f => f != null && f.IsAlive).Select(f => f.Id).ToList(),
                fighters = fight.Todos.Select(f => FighterView(fight, f)).ToList(),
                glyphs = fight.Glifos.Select(g => new { g.Id, owner = g.Dueno, kind = g.Tipo, spell = g.Hechizo,
                                                        cells = g.Casillas.OrderBy(c => c).ToList() }).ToList(),
            };
        }

        /// <summary>
        /// A state given to a fighter as a spell's 950 would leave it ({ as?, state, rounds }): the
        /// context a spell asks of its caster -- a Zobal's mask, a Pandawa's Borracho -- when
        /// the harness tries that spell alone. Announced to the board as its row.
        /// </summary>
        private static Respuesta TestState(string cuerpo)
        {
            var session = TestSessionOnline();
            var fight = session == null ? null : FightHandler.FightOf(session.State.CharacterId);
            if (session == null || fight == null) return Mal(409, "no-fight");
            var (who, _, error) = ActingAs(session, fight, cuerpo, toItsTurn: false);
            if (who == null || error.Length > 0 && error != "not-played-here") return Mal(404, error);
            int state = (int)Numero(cuerpo, "state");
            int rounds = (int)Math.Max(1, Numero(cuerpo, "rounds"));
            if (state <= 0) return Mal(400, "state");
            if (who.Buffs.TieneEstado(state)) return Bien(new { who = who.Id, state, already = true });
            var lines = RunInSession(session, _ => FightHandler.GiveStateAsync(fight, who, state, rounds));
            return Bien(new { who = who.Id, state, has = who.Buffs.TieneEstado(state), lines });
        }

        /// <summary>The session of another slot's character, in the world.</summary>
        private static GameSession? SessionOfSlot(int slot)
        {
            long account = DatabaseManager.AccountIdOf(slot <= 0 ? TestLogin : $"{TestLogin}{slot:D2}");
            return account <= 0 ? null : SessionRegistry.All().FirstOrDefault(s => s.AccountId == account && s.IsInWorld);
        }

        /// <summary>The session that plays a fighter: the slot's when it is his to play, or its own character's (another slot).</summary>
        private static GameSession? DriverOf(GameSession session, Fighter fighter)
        {
            if (fighter.ControlledBy(session.State.CharacterId)) return session;
            var own = SessionRegistry.FindByCharacter(fighter.EsInvocado ? fighter.Invocador : fighter.Id);
            return own != null && fighter.ControlledBy(own.State.CharacterId) ? own : null;
        }

        /// <summary>
        /// Turns handed on, each by whoever plays it (jxy), until it is <paramref name="fighter"/>'s:
        /// true when it is. A turn nobody here plays -- a monster's -- is waited for.
        /// </summary>
        /// <summary>
        /// Until the turn at hand has begun. A character's turn starts when his client confirms it
        /// (jxh, jwz back), and passed before that it ended without ever starting: his "at the start
        /// of the turn" never went off -- the Cocobur's self-blow, every item's TB.
        /// </summary>
        private static void WaitTillItBegins(FightInstance fight, Fighter? current, int seconds = 10)
        {
            // Every turn starts on a client's confirmation, a JondoBot's and a summon's too: passed
            // before it, a foe's "at the start of its turn" -- a Feca glyph's blow -- never went off.
            if (current == null) return;
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until && fight.State == FightState.Ongoing && fight.CurrentFighter == current
                   && fight.LastTurnBegun != (fight.RoundNumber, current.Id))
                System.Threading.Thread.Sleep(100);
        }

        private static bool TurnTo(GameSession session, FightInstance fight, Fighter fighter, int most = 40)
        {
            for (int turns = 0; turns <= most; turns++)
            {
                if (fight.State != FightState.Ongoing || !fighter.IsAlive) return false;
                var current = fight.CurrentFighter;
                if (current == fighter) { WaitTillItBegins(fight, fighter); return true; }
                var mark = (fight.RoundNumber, fight.CurrentTurnIndex, current);
                var driver = current == null ? null : DriverOf(session, current);
                WaitTillItBegins(fight, current);
                if (driver != null && fight.CurrentFighter == current) RunAsClient(driver, "jxy", Pb.New().Build());
                var until = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < until && fight.State == FightState.Ongoing
                       && (fight.RoundNumber, fight.CurrentTurnIndex, fight.CurrentFighter) == mark)
                    System.Threading.Thread.Sleep(150);
            }
            return fight.CurrentFighter == fighter;
        }

        /// <summary>
        /// The fighter a request acts as ("as", a fighter id; the slot's character when absent),
        /// with its turn come when <paramref name="toItsTurn"/>, and the session that plays it.
        /// </summary>
        private static (Fighter? Who, GameSession? Driver, string Error) ActingAs(GameSession session, FightInstance fight, string cuerpo, bool toItsTurn)
        {
            long id = Numero(cuerpo, "as");
            var who = fight.Buscar(id != 0 ? id : session.State.CharacterId);
            if (who == null) return (null, null, "fighter");
            if (id == 0) return (who, session, "");
            // The character named: his turn brought back when asked, as a JondoBot's would be.
            if (id == session.State.CharacterId)
                return toItsTurn && fight.CurrentFighter != who && !TurnTo(session, fight, who) ? (who, session, "no-turn") : (who, session, "");
            var driver = DriverOf(session, who);
            if (driver == null) return (who, null, "not-played-here");
            if (toItsTurn && !TurnTo(session, fight, who)) return (who, driver, "no-turn");
            return (who, driver, "");
        }

        /// <summary>
        /// A team fight for the harness: { allies: [breed...], foes: [breed...], blue: [slot...],
        /// red: [slot...] }. The slot's character and the blue slots' on one side, the red slots'
        /// in front, and JondoBots of the classes given filling either side -- each played by
        /// this slot's session (Fighter.DrivenBy), never by its own wits. Every real client still
        /// says it is ready (kaq) for the fight to start.
        /// </summary>
        private static Respuesta TestTeam(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null || session.Stream == null) return Mal(404, "offline");
            if (FightHandler.FightOf(session.State.CharacterId) != null) return Mal(409, "in-fight");
            var lists = new Dictionary<string, List<int>>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(cuerpo) ? "{}" : cuerpo);
                foreach (string field in new[] { "allies", "foes", "blue", "red" })
                    lists[field] = doc.RootElement.TryGetProperty(field, out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? a.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.Number).Select(x => x.GetInt32()).ToList()
                        : new List<int>();
            }
            catch (System.Text.Json.JsonException) { return Mal(400, "json"); }

            var blue = new List<GameSession> { session };
            var red = new List<GameSession>();
            foreach (var (field, side) in new[] { ("blue", blue), ("red", red) })
                foreach (int slot in lists[field])
                {
                    var other = SessionOfSlot(slot);
                    if (other == null) return Mal(404, $"slot-{slot}");
                    if (FightHandler.FightOf(other.State.CharacterId) != null) return Mal(409, $"slot-{slot}-in-fight");
                    if (!side.Contains(other)) side.Add(other);
                }
            List<Fighter> Bots(string field) => lists[field].Where(b => SpellTable.ClassBreeds.Contains(b)).Select(b =>
            {
                var spec = KoliseoBots.Create(b);
                var bot = KoliseoBots.BuildFighter(spec);
                KoliseoBots.Forget(spec.Id);
                bot.DrivenBy = session.State.CharacterId;
                return bot;
            }).ToList();
            var allies = Bots("allies");
            var foes = Bots("foes");
            if (red.Count + foes.Count == 0) return Mal(400, "nobody-in-front");

            bool started = false;
            var lines = RunInSession(session, async _ =>
            {
                // On the map given -- a roomy arena for the scenarios that need rows of free cells -- or his own.
                long map = Numero(cuerpo, "map") is var m && m > 0 ? m : session.State.MapId;
                started = await FightHandler.InitiatePvpAsync(blue, red, map, blueBots: allies, redBots: foes);
            });
            var fight = FightHandler.FightOf(session.State.CharacterId);
            return started && fight != null
                ? Bien(new { id = fight.FightId, allies = allies.Select(b => new { b.Id, b.Breed }).ToList(),
                             foes = foes.Select(b => new { b.Id, b.Breed }).ToList(),
                             blue = blue.Select(s => s.State.CharacterId).ToList(), red = red.Select(s => s.State.CharacterId).ToList(), lines })
                : Mal(500, "not-started");
        }

        /// <summary>The monster groups on the character's map: what a click on one attacks (hqa, its id).</summary>
        private static Respuesta TestGroups()
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            return Bien(MobSpawnManager.GetMobsForMap(session.State.MapId).Select(g => new
            {
                id = g.MobId, cell = g.CellId, fighting = FightHandler.IsGroupFighting(g.MobId),
                members = g.Members.Select(m => new { monster = m.Monster?.Id ?? 0, grade = m.GradeIndex }).ToList(),
            }).ToList());
        }

        private static Respuesta TestFight()
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            return Bien((object?)FightView(session) ?? new { none = true });
        }

        /// <summary>A class's spells: each pair, its base and its variant, and the grade each has at a level.</summary>
        private static Respuesta TestSpells(string cuerpo)
        {
            int breed = (int)Numero(cuerpo, "breed");
            int level = (int)Math.Clamp(Numero(cuerpo, "level") is var l && l > 0 ? l : 200, 1, 200);
            return Bien(SpellTable.PairsOf(breed).Select(p => new
            {
                pair = p.Id, @base = p.Base, variant = p.Variant,
                baseGrade = SpellTable.GradeFor(p.Base, level), variantGrade = SpellTable.GradeFor(p.Variant, level),
            }).ToList());
        }

        // ─── Acting as the client ────────────────────────────────────────────────────────────

        /// <summary>The ops a harness may send in the character's name, and the handler each goes to.</summary>
        private static readonly HashSet<string> FightOps = new()
        {
            Op.Jwh, Op.Jwn, "jxy", "jwz", Op.Jrw, Op.Kaq, Op.Jzy,
        };

        /// <summary>A client frame as the client wraps it: { f2 { f1 Any { f1 uri, f2 body }, f2 -1 } }.</summary>
        private static byte[] ClientFrame(string op, byte[] body)
        {
            var any = Pb.New().Str(1, ConnectionProtocol.UriPrefix + op);
            if (body.Length > 0) any.Bytes(2, body);
            return Pb.New().Msg(2, Pb.New().Bytes(1, any.Build()).Var(2, -1)).Build();
        }

        /// <summary>
        /// A frame handed to its handler inside the character's session, in its turn -- after the
        /// packet the client sent before it, before the next --, with every line the server writes
        /// meanwhile captured.
        /// </summary>
        private static List<string> RunAsClient(GameSession session, string op, byte[] body)
        {
            byte[] frame = ClientFrame(op, body);
            string routing = ConnectionProtocol.UriPrefix + op;
            var capture = new List<string>();
            var work = Task.Run(async () =>
            {
                using var turn = await session.EnterTurnAsync();
                using (SessionContext.Push(session))
                {
                    session.Capture = capture;
                    try
                    {
                        var stream = session.Stream!;
                        // kme: giving the fight up, so that a harness closing its client leaves no fight behind.
                        if (FightOps.Contains(op) || op == Op.Kme) await FightHandler.HandleFightMessageAsync(stream, frame, routing);
                        else if (op == Op.Hqa) await FightHandler.AttackAsync(stream, frame);
                        else if (op == Op.Hmt) await SpellHandler.HandleVariantAsync(stream, frame);
                        else throw new InvalidOperationException($"op {op} is not one the harness sends");
                    }
                    finally
                    {
                        session.Capture = null;
                    }
                }
            });
            if (!work.Wait(TestTimeoutMs)) capture.Add("[JondoPlay] The server took too long; what follows may be missing.");
            lock (capture) return capture.ToList();
        }

        /// <summary>Work done inside the character's session, in its turn, its lines captured.</summary>
        private static List<string> RunInSession(GameSession session, Func<System.Net.Sockets.NetworkStream, Task> work)
        {
            var capture = new List<string>();
            var task = Task.Run(async () =>
            {
                using var turn = await session.EnterTurnAsync();
                using (SessionContext.Push(session))
                {
                    session.Capture = capture;
                    try { await work(session.Stream!); }
                    finally { session.Capture = null; }
                }
            });
            if (!task.Wait(TestTimeoutMs)) capture.Add("[JondoPlay] The server took too long.");
            lock (capture) return capture.ToList();
        }

        /// <summary>A fighter put on a cell (or the free one nearest), as a teleport the board sees: { fighter?, cell }.</summary>
        private static Respuesta TestPlace(string cuerpo)
        {
            var session = TestSessionOnline();
            var fight = session == null ? null : FightHandler.FightOf(session.State.CharacterId);
            if (session == null || fight == null) return Mal(409, "no-fight");
            long id = Numero(cuerpo, "fighter");
            var who = fight.Buscar(id == 0 ? session.State.CharacterId : id);
            if (who == null) return Mal(404, "fighter");
            int cell = (int)Numero(cuerpo, "cell");
            var lines = RunInSession(session, _ => FightHandler.InASequenceAsync(fight, who.Id,
                () => FightHandler.TeleportNearAsync(fight, who, cell)));
            return Bien(new { who.Id, cell = who.CellId, lines });
        }

        /// <summary>
        /// The character put where a spell can be cast at a target from: a free cell it can stand
        /// on, in the spell's range, seeing the target -- at three cells when the range allows it.
        /// { spell, target (a fighter's id) or cell }.
        /// </summary>
        private static Respuesta TestStage(string cuerpo)
        {
            var session = TestSessionOnline();
            var fight = session == null ? null : FightHandler.FightOf(session.State.CharacterId);
            if (session == null || fight == null) return Mal(409, "no-fight");
            var (me, _, error) = ActingAs(session, fight, cuerpo, toItsTurn: false);
            if (error.Length > 0 && error != "not-played-here") return Mal(409, error);
            int spell = (int)Numero(cuerpo, "spell");
            long targetId = Numero(cuerpo, "target");
            int target = targetId != 0 ? fight.Buscar(targetId)?.CellId ?? -1 : (int)Numero(cuerpo, "cell");
            if (me == null || target < 0) return Mal(404, "target");

            var board = FightHandler.BoardOf(fight);
            var reach = FightHandler.ReachOf(fight, me, spell);
            // A spell with no reach (0 to 0) is cast on the caster's own cell. Its blow around him --
            // Palabra Entretenida's cross of three -- wants a foe inside: the nearest one is put
            // on the nearest free cell of that zone, or the blow is never seen.
            if (reach.Max <= 0)
            {
                var stageLines = new List<string>();
                var blow = SpellEffects.De(spell, FightHandler.CastingGradeOf(me, spell))
                    .FirstOrDefault(e => Managers.EffectEngine.EsDeDano(e.EffectId) && e.TargetMask.Split(',').Contains("A"));
                var nearestFoe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                            .OrderBy(f => Jondo.Unity.World.Maps.MapGeometry.Distance(f.CellId, me.CellId)).FirstOrDefault();
                if (blow != null && nearestFoe != null)
                {
                    var zone = Managers.EffectEngine.CasillasDelEfecto(blow, me.CellId, me.CellId);
                    if (!zone.Contains(nearestFoe.CellId))
                    {
                        int into = zone.Where(c => c != me.CellId && board.Walkable(c) && !fight.Todos.Any(f => f.IsAlive && f.CellId == c))
                                       .OrderBy(c => Jondo.Unity.World.Maps.MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();
                        if (into >= 0)
                            stageLines = RunInSession(session, _ => FightHandler.InASequenceAsync(fight, me.Id, () => FightHandler.TeleportNearAsync(fight, nearestFoe, into)));
                    }
                }
                return Bien(new { cell = me.CellId, target = me.CellId, range = new[] { 0, 0 }, lines = stageLines });
            }
            // A spell that wants a free cell -- a jump, a summon, a beacon -- aims beside the target.
            // The nearest free cell, beside it when one is: with the Puchs bunched and the earlier
            // summons around them, every neighbour can be taken.
            // A ring that leaves its centre out (O, Q): aimed at a free cell with the target on
            // the ring, or the target is the one fighter it never hits.
            var hollow = SpellEffects.De(spell, FightHandler.CastingGradeOf(me, spell))
                .Where(e => e.Forma is 'O' or 'Q' or '#' && e.Tamano > 0 && e.TargetMask.Contains('A'))
                .Select(e => ((char)e.Forma, e.Tamano)).DefaultIfEmpty((' ', 0)).First();
            int ring = hollow.Item2;
            if (ring != 0 && !reach.FreeCell)
            {
                int aimed = target;
                // On the zone's edge, seen from the aimed cell: a ring (O) by distance, a straight
                // cross (Q) along a row, a diagonal one (#, Flecha Impactante's push) along a diagonal.
                bool OnEdge(int c)
                {
                    var (ax, ay) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(aimed);
                    var (cx, cy) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(c);
                    int dx = Math.Abs(ax - cx), dy = Math.Abs(ay - cy);
                    return hollow.Item1 switch
                    {
                        'O' => Jondo.Unity.World.Maps.MapGeometry.Distance(c, aimed) == ring,
                        'Q' => Math.Max(dx, dy) == ring && (dx == 0 || dy == 0),
                        _ => dx == ring && dy == ring,
                    };
                }
                int onRing = Enumerable.Range(0, 560)
                    .Where(c => c != me.CellId && board.Walkable(c) && !fight.Todos.Any(f => f.IsAlive && f.CellId == c) && OnEdge(c))
                    .OrderBy(c => Jondo.Unity.World.Maps.MapGeometry.Distance(c, me.CellId)).DefaultIfEmpty(-1).First();
                if (onRing >= 0) target = onRing;
            }
            if (reach.FreeCell || cuerpo.Contains("\"free\":true"))
            {
                int aimed = target;
                int free = Enumerable.Range(0, 560)
                    .Where(c => c != aimed && c != me.CellId && board.Walkable(c) && !fight.Todos.Any(f => f.IsAlive && f.CellId == c)
                                && !fight.Glifos.Any(g => g.Casillas.Contains(c)))
                    .OrderBy(c => Jondo.Unity.World.Maps.MapGeometry.Distance(c, aimed))
                    .DefaultIfEmpty(-1).First();
                if (free >= 0) target = free;
            }
            int min = Math.Max(reach.Min, 1), max = Math.Max(reach.Max, min);
            int wanted = Math.Clamp(3, min, max);
            // A push wants room behind the target, away from the caster: without it the target
            // only crashes, and whether it is pushed the cells it should go is never seen.
            int push = SpellEffects.De(spell, FightHandler.CastingGradeOf(me, spell))
                .Where(e => e.EffectId is Jondo.Unity.World.Combat.EffectSupport.Push or Jondo.Unity.World.Combat.EffectSupport.PushWithoutDamage
                            or Jondo.Unity.World.Combat.EffectSupport.StepBack)
                .Select(e => Math.Max(1, e.DiceNum)).DefaultIfEmpty(0).Max();
            int pull = SpellEffects.De(spell, FightHandler.CastingGradeOf(me, spell))
                .Where(e => e.EffectId is Jondo.Unity.World.Combat.EffectSupport.Pull).Select(e => Math.Max(1, e.DiceNum)).DefaultIfEmpty(0).Max();
            bool RoomBetween(int from)
            {
                if (pull == 0) return true;
                var (fx, fy) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(from);
                var (tx, ty) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(target);
                if (tx - fx != 0 && ty - fy != 0 && Math.Abs(tx - fx) != Math.Abs(ty - fy)) return false;
                int dx = Math.Sign(fx - tx), dy = Math.Sign(fy - ty);
                for (int k = 1; k <= Math.Min(pull, 4); k++)
                {
                    int c = Jondo.Unity.World.Maps.MapGeometry.PointToCell(tx + dx * k, ty + dy * k);
                    if (c == from) break;
                    if (c < 0 || !board.Walkable(c) || fight.Todos.Any(f => f.IsAlive && f.CellId == c && f != me)) return false;
                }
                return true;
            }
            bool RoomBehind(int from)
            {
                if (!RoomBetween(from)) return false;
                if (push == 0) return true;
                var (fx, fy) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(from);
                var (tx, ty) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(target);
                if (tx - fx != 0 && ty - fy != 0 && Math.Abs(tx - fx) != Math.Abs(ty - fy)) return false;
                int dx = Math.Sign(tx - fx), dy = Math.Sign(ty - fy);
                for (int k = 1; k <= Math.Min(push, 4); k++)
                {
                    int c = Jondo.Unity.World.Maps.MapGeometry.PointToCell(tx + dx * k, ty + dy * k);
                    if (c < 0 || !Jondo.Unity.World.Maps.MapGeometry.IsValid(c) || !board.Walkable(c)
                        || fight.Todos.Any(f => f.IsAlive && f.CellId == c && f != me) || fight.Glifos.Any(g => g.Cubre(c))) return false;
                }
                return true;
            }
            int best = -1, bestScore = int.MaxValue;
            for (int cell = 0; cell < 560; cell++)
            {
                if (cell != me.CellId && (!board.Walkable(cell) || fight.Todos.Any(f => f.IsAlive && f.CellId == cell))) continue;
                int d = Jondo.Unity.World.Maps.MapGeometry.Distance(cell, target);
                if (d < min || d > max || !board.Sees(cell, target)) continue;
                int score = Math.Abs(d - wanted) * 10 + (cell % 14 == target % 14 || cell / 14 == target / 14 ? 0 : 1)
                          + (RoomBehind(cell) ? 0 : 1000);
                if (score < bestScore) { bestScore = score; best = cell; }
            }
            if (best < 0) return Mal(409, $"no cell within {min}-{max} of {target} that sees it");
            var lines = best == me.CellId ? new List<string>()
                : RunInSession(session, _ => FightHandler.InASequenceAsync(fight, me.Id, () => FightHandler.TeleportNearAsync(fight, me, best)));
            return Bien(new { cell = me.CellId, target, range = new[] { min, max }, lines });
        }

        /// <summary>
        /// A spell as the server holds it at the character's level: its grade, cost, range, uses,
        /// and each effect with what the engine does with it (EffectSupport: Direct, Characteristic,
        /// or PanelOnly -- shown in the buff panel and nothing else).
        /// </summary>
        private static Respuesta TestSpell(string cuerpo)
        {
            int spell = (int)Numero(cuerpo, "spell");
            int level = (int)Numero(cuerpo, "level");
            if (level <= 0) level = TestSessionOnline()?.State.CharacterLevel ?? 200;
            int grade = SpellTable.GradeFor(spell, level);
            if (grade <= 0) grade = 1;
            var limits = FightHandler.LimitesDeGrado(spell, grade);
            var catalogue = new Dictionary<int, (int Characteristic, int Category, string Text)>();
            string name = "";
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Id, Characteristic, Category, Description FROM Effects;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        catalogue[reader.GetInt32(0)] = (reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                                                         reader.IsDBNull(3) ? "" : reader.GetString(3));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT t.Text FROM SpellTemplates s JOIN Translations t ON t.Key = CAST(s.NameId AS TEXT) WHERE s.Id = $id;";
                    command.Parameters.AddWithValue("$id", spell);
                    name = command.ExecuteScalar() as string ?? "";
                }
            }
            catch (Exception ex)
            {
                name = $"(catalogue unreadable: {ex.Message})";
            }
            var effects = SpellEffects.De(spell, grade).Select(e =>
            {
                catalogue.TryGetValue(e.EffectId, out var row);
                return new
                {
                    effect = e.EffectId, text = row.Text, support = Jondo.Unity.World.Combat.EffectSupport.Classify(e.EffectId, row.Characteristic, row.Category).ToString(),
                    dice = new[] { e.DiceNum, e.DiceSide, e.Value }, e.Duration, e.Delay, e.TargetMask, e.Triggers,
                    zone = $"{(char)e.Forma}{e.Tamano}", e.Probabilidad,
                };
            }).ToList();
            return Bien(new
            {
                spell, name, level, grade, cost = limits.Cost, range = new[] { limits.AlcanceMinimo, limits.AlcanceMaximo },
                perTurn = limits.PorTurno, perTarget = limits.PorObjetivo, interval = limits.Intervalo,
                freeCell = limits.NeedFreeCell, takenCell = limits.NeedTakenCell, effects,
                // Every trigger in the spell and the spells it casts at once, three deep: Toxinas's
                // "at the end of the turn" sits in its child, not in its own row.
                triggers = TriggersWithin(spell, grade),
            });
        }

        /// <summary>
        /// A fighter walked to a cell as the fight walks it: { fighter, cell }. What it set off on
        /// the way comes back -- life, points, effects, marks spent -- with the lines logged.
        /// </summary>
        private static Respuesta TestWalk(string cuerpo)
        {
            var session = TestSessionOnline();
            var fight = session == null ? null : FightHandler.FightOf(session.State.CharacterId);
            if (session == null || fight == null) return Mal(409, "no-fight");
            var who = fight.Buscar(Numero(cuerpo, "fighter"));
            if (who == null) return Mal(404, "fighter");
            int to = (int)Numero(cuerpo, "cell");
            var before = fight.Todos.ToDictionary(f => f.Id, f => (f.CurrentHP, f.CurrentAP, f.CurrentMP, f.CellId, f.Buffs.Puestos.Count));
            var glyphsBefore = fight.Glifos.Select(g => g.Id).ToHashSet();
            bool walked = false;
            // Onto a mark: its cells nearest the walker first, until one can be walked to.
            long markId = Numero(cuerpo, "mark");
            var mark = markId == 0 ? null : fight.Glifos.FirstOrDefault(g => g.Id == markId);
            var candidates = mark == null ? new List<int> { to }
                : mark.Casillas.Where(c => !fight.Todos.Any(f => f.IsAlive && f.CellId == c))
                      .OrderBy(c => Jondo.Unity.World.Maps.MapGeometry.Distance(who.CellId, c)).Take(6).ToList();
            var lines = RunInSession(session, async stream =>
            {
                foreach (int cell in candidates)
                {
                    to = cell;
                    if (walked = await FightHandler.TestWalkAsync(stream, fight, who, cell)) break;
                }
            });
            var changes = fight.Todos.Where(f => before.ContainsKey(f.Id)).Select(f => new
            {
                f.Id, hp = f.CurrentHP - before[f.Id].CurrentHP, ap = f.CurrentAP - before[f.Id].CurrentAP, mp = f.CurrentMP - before[f.Id].CurrentMP,
                moved = before[f.Id].CellId != f.CellId ? $"{before[f.Id].CellId}->{f.CellId}" : null,
                buffs = f.Buffs.Puestos.Count - before[f.Id].Count,
            }).Where(c => c.hp != 0 || c.ap != 0 || c.mp != 0 || c.moved != null || c.buffs != 0).ToList();
            var spent = glyphsBefore.Where(id => !fight.Glifos.Any(g => g.Id == id)).ToList();
            return Bien(new { walked, cell = who.CellId, changes, marksGone = spent, lines });
        }

        /// <summary>
        /// The character's turn as it began -- its points, its spells ready -- and its foes' life
        /// whole. With keep, the board stays as the last spells left it (rows, states, marks,
        /// summons): the context a class's spells answer to, as in a real fight.
        /// </summary>
        private static Respuesta TestRefresh(string cuerpo)
        {
            bool keep, noSummons;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(cuerpo) ? "{}" : cuerpo);
                keep = doc.RootElement.TryGetProperty("keep", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.True;
                noSummons = doc.RootElement.TryGetProperty("noSummons", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            catch (System.Text.Json.JsonException) { keep = false; noSummons = false; }
            var session = TestSessionOnline();
            var fight = session == null ? null : FightHandler.FightOf(session.State.CharacterId);
            if (session == null || fight == null) return Mal(409, "no-fight");
            // As another fighter, its turn come first: what the turns between do is done before the clean board.
            var (me, _, error) = ActingAs(session, fight, cuerpo, toItsTurn: true);
            if (me == null || error.Length > 0 && error != "not-played-here") return Mal(404, error);
            var lines = RunInSession(session, stream => FightHandler.FreshTurnAsync(stream, fight, me, keep, noSummons));
            // A heal to try: everybody down to 70% of his life first, or it has nothing to heal.
            if (cuerpo.Contains("\"hurt\":true")) lines.AddRange(RunInSession(session, stream => FightHandler.TestHurtAsync(stream, fight, 70)));
            // Or down to the share asked ("hurtTo"): a blow that kills, to try what answers a death.
            int hurtTo = (int)Numero(cuerpo, "hurtTo");
            if (hurtTo is > 0 and < 100) lines.AddRange(RunInSession(session, stream => FightHandler.TestHurtAsync(stream, fight, hurtTo)));
            return Bien(new { ap = me.CurrentAP, mp = me.CurrentMP, kept = keep, lines });
        }

        /// <summary>
        /// Any client message the harness may send: its op and its varint fields, { "fields": { "1": 300, "4": 161 } }.
        /// </summary>
        private static Respuesta TestInject(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null || session.Stream == null) return Mal(404, "offline");
            string op = Texto(cuerpo, "op");
            var body = Pb.New();
            using (var doc = System.Text.Json.JsonDocument.Parse(cuerpo))
            {
                if (doc.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var field in fields.EnumerateObject().OrderBy(f => int.Parse(f.Name)))
                        body.Var(int.Parse(field.Name), field.Value.GetInt64());
            }
            var lines = RunAsClient(session, op, body.Build());
            return Bien(new { lines, fight = FightView(session) });
        }

        /// <summary>
        /// The character's turn given up (jxy) and waited for until it comes back: the summons and
        /// the monsters play in between, and every spell they cast is judged as the player's are
        /// -- effect by effect against the board -- through FightHandler's cast watch.
        /// </summary>
        private static Respuesta TestEndTurn()
        {
            var session = TestSessionOnline();
            if (session == null || session.Stream == null) return Mal(404, "offline");
            var fight = FightHandler.FightOf(session.State.CharacterId);
            if (fight == null) return Mal(409, "no-fight");
            var me = fight.Buscar(session.State.CharacterId);
            if (me == null) return Mal(404, "fighter");

            var casts = new List<object>();
            var gate = new object();
            (Dictionary<long, (int CurrentHP, int CurrentAP, int CurrentMP, int CellId, int Count, bool IsAlive)> Before,
             HashSet<(long, int)> Rows, Dictionary<long, HashSet<int>> States, HashSet<int> Glyphs, int Round)? snap = null;
            void OnBefore(FightInstance f, Fighter caster, int spell, int grade, int cell)
            {
                if (f != fight) return;
                lock (gate)
                    snap = (f.Todos.ToDictionary(x => x.Id, x => (x.CurrentHP, x.CurrentAP, x.CurrentMP, x.CellId, x.Buffs.Puestos.Count, x.IsAlive)),
                            f.Todos.SelectMany(x => x.Buffs.Puestos.Select(r => (x.Id, r.Numero))).ToHashSet(),
                            f.Todos.ToDictionary(x => x.Id, x => x.Buffs.Puestos.Where(r => r.Estado != 0).Select(r => r.Estado).ToHashSet()),
                            f.Glifos.Select(g => g.Id).ToHashSet(), f.RoundNumber);
            }
            void OnAfter(FightInstance f, Fighter caster, int spell, int grade, int cell)
            {
                if (f != fight) return;
                lock (gate)
                {
                    if (snap is not { } s) return;
                    snap = null;
                    var newRows = f.Todos.SelectMany(x => x.Buffs.Puestos.Where(r => !s.Rows.Contains((x.Id, r.Numero))).Select(r => (Who: x, Row: r))).ToList();
                    var newGlyphs = f.Glifos.Where(g => !s.Glyphs.Contains(g.Id)).ToList();
                    string RoleOf(Fighter x) => x.Id == caster.Id ? "caster" : x.TeamId == caster.TeamId ? "ally" : "foe";
                    var aimed = f.Todos.FirstOrDefault(x => x.IsAlive && x.CellId == cell);
                    List<object> effects;
                    try
                    {
                        effects = VerifyEffects(f, caster, spell, Math.Max(1, grade), s.Round, s.Before, s.States, newRows, newGlyphs, RoleOf,
                                                aimed == null ? null : RoleOf(aimed));
                    }
                    catch (Exception ex) { effects = new List<object> { new { error = ex.Message } }; }
                    var changes = f.Todos.Select(x =>
                    {
                        s.Before.TryGetValue(x.Id, out var b);
                        return new
                        {
                            x.Id, x.Name, hp = x.CurrentHP - b.CurrentHP, moved = b.CellId != x.CellId ? $"{b.CellId}->{x.CellId}" : null,
                            buffs = x.Buffs.Puestos.Count - b.Count, died = b.IsAlive && !x.IsAlive, isNew = !s.Before.ContainsKey(x.Id),
                        };
                    }).Where(c => c.hp != 0 || c.moved != null || c.buffs != 0 || c.died || c.isNew).ToList();
                    casts.Add(new
                    {
                        caster = caster.Id, name = caster.Name, monster = caster.MonsterId, summon = caster.EsInvocado,
                        ours = caster.TeamId == me.TeamId, spell, grade, cell, changes, effects,
                    });
                }
            }

            FightHandler.CastWatchBefore += OnBefore;
            FightHandler.CastWatchAfter += OnAfter;
            List<string> lines;
            bool back = false;
            int roundBefore = fight.RoundNumber;
            // Back when the turn comes to him after somebody else's: from his own turn that is the
            // next round, from a JondoBot's or a summon's of his it is later in this one. Waiting
            // for the next round there let his own turn -- an hour long on a test server -- run
            // out the whole patience.
            var startedOn = fight.CurrentFighter;
            bool leftIt = false;
            try
            {
                WaitTillItBegins(fight, me);
                lines = RunAsClient(session, "jxy", Pb.New().Build());
                var until = DateTime.UtcNow.AddSeconds(150);
                var handedOn = new HashSet<(int, int)>();
                while (DateTime.UtcNow < until)
                {
                    System.Threading.Thread.Sleep(300);
                    if (FightHandler.FightOf(session.State.CharacterId) != fight || !me.IsAlive) break;
                    if (fight.CurrentFighter != startedOn || fight.RoundNumber > roundBefore) leftIt = true;
                    if (leftIt && fight.CurrentFighter?.Id == me.Id) { back = true; WaitTillItBegins(fight, me); break; }
                    // A turn this slot plays and nobody else would -- its summons', a team test's
                    // JondoBots' -- is handed on at once, as their player would, not left to its clock.
                    var current = fight.CurrentFighter;
                    if (current != null && current != me && handedOn.Add((fight.RoundNumber, fight.CurrentTurnIndex))
                        && DriverOf(session, current) is { } driver)
                    {
                        WaitTillItBegins(fight, current);
                        lines.AddRange(RunAsClient(driver, "jxy", Pb.New().Build()));
                    }
                }
            }
            finally
            {
                FightHandler.CastWatchBefore -= OnBefore;
                FightHandler.CastWatchAfter -= OnAfter;
            }
            List<object> found;
            lock (gate) found = casts.ToList();
            return Bien(new { back, round = fight.RoundNumber, alive = me.IsAlive, casts = found, lines });
        }

        /// <summary>
        /// A spell cast as the client casts it (jwh: f1 the cell, f4 the spell), with what the
        /// server wrote while doing it and what changed in the fight: life, points, effects, marks.
        /// </summary>
        private static Respuesta TestCast(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null || session.Stream == null) return Mal(404, "offline");
            var fight = FightHandler.FightOf(session.State.CharacterId);
            if (fight == null) return Mal(409, "no-fight");
            // As another fighter of the fight -- a team test's JondoBot, another slot's
            // character --, at its turn, through the session that plays it.
            var (me, driver, error) = ActingAs(session, fight, cuerpo, toItsTurn: true);
            if (me == null || driver == null || error.Length > 0) return Mal(409, error);
            int spell = (int)Numero(cuerpo, "spell");
            int cell = (int)Numero(cuerpo, "cell");

            var before = fight.Todos.ToDictionary(f => f.Id, f => (f.CurrentHP, f.CurrentAP, f.CurrentMP, f.CellId, f.Buffs.Puestos.Count, f.IsAlive));
            var rowsBefore = fight.Todos.SelectMany(f => f.Buffs.Puestos.Select(r => (f.Id, r.Numero))).ToHashSet();
            var statesBefore = fight.Todos.ToDictionary(f => f.Id, f => f.Buffs.Puestos.Where(r => r.Estado != 0).Select(r => r.Estado).ToHashSet());
            var glyphsBefore = fight.Glifos.Select(g => g.Id).ToHashSet();
            int round = fight.RoundNumber;
            int grade = FightHandler.CastingGradeOf(me, spell);
            var aimedAt = fight.Todos.FirstOrDefault(f => f.IsAlive && f.CellId == cell);
            var lines = RunAsClient(driver, Op.Jwh, Pb.New().Var(1, cell).Var(4, spell).Build());

            var changes = fight.Todos.Select(f =>
            {
                before.TryGetValue(f.Id, out var b);
                return new
                {
                    f.Id, f.Name,
                    hp = f.CurrentHP - b.CurrentHP, ap = f.CurrentAP - b.CurrentAP, mp = f.CurrentMP - b.CurrentMP,
                    moved = b.CellId != f.CellId ? $"{b.CellId}->{f.CellId}" : null,
                    // Rows put by the cast, not the count's difference: a row that replaced its
                    // like (a cap of one) left the count where it was and the cast looked idle.
                    buffs = f.Buffs.Puestos.Count(r => !rowsBefore.Contains((f.Id, r.Numero))),
                    died = b.IsAlive && !f.IsAlive, isNew = !before.ContainsKey(f.Id),
                };
            }).Where(c => c.hp != 0 || c.ap != 0 || c.mp != 0 || c.moved != null || c.buffs != 0 || c.died || c.isNew).ToList();

            bool cast = lines.Any(l => l.Contains("Casts spell"));
            var newRows = fight.Todos.SelectMany(f => f.Buffs.Puestos.Where(r => !rowsBefore.Contains((f.Id, r.Numero))).Select(r => (Who: f, Row: r))).ToList();
            var newGlyphs = fight.Glifos.Where(g => !glyphsBefore.Contains(g.Id)).ToList();
            string RoleOf(Fighter f) => f.Id == me.Id ? "caster" : f.TeamId == me.TeamId ? "ally" : "foe";
            var effects = cast ? VerifyEffects(fight, me, spell, grade, round, before, statesBefore, newRows, newGlyphs, RoleOf,
                                               aimedAt == null ? null : RoleOf(aimedAt), lines, cell) : new List<object>();
            return Bien(new { spell, grade, cell, caster = me.Id, cast, changes, glyphs = newGlyphs.Count, effects, lines });
        }

        // ─── What each effect should have done, against what it did ─────────────────────────

        private static readonly Lazy<Dictionary<int, (int Characteristic, int Category, string Text)>> EffectCatalogue = new(() =>
        {
            var catalogue = new Dictionary<int, (int, int, string)>();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Characteristic, Category, Description FROM Effects;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                catalogue[reader.GetInt32(0)] = (reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                                                 reader.IsDBNull(3) ? "" : reader.GetString(3));
            return catalogue;
        });

        private static readonly HashSet<int> MovementEffects = new()
        {
            Jondo.Unity.World.Combat.EffectSupport.Teleport, Jondo.Unity.World.Combat.EffectSupport.Push, Jondo.Unity.World.Combat.EffectSupport.Pull,
            Jondo.Unity.World.Combat.EffectSupport.SwapPositions, Jondo.Unity.World.Combat.EffectSupport.PushWithoutDamage,
            Jondo.Unity.World.Combat.EffectSupport.PushToTargetCell, Jondo.Unity.World.Combat.EffectSupport.PullToTargetCell,
            Jondo.Unity.World.Combat.EffectSupport.StepBack, Jondo.Unity.World.Combat.EffectSupport.StepForward,
            Jondo.Unity.World.Combat.EffectSupport.Carry, Jondo.Unity.World.Combat.EffectSupport.Throw,
            1100, 1101, 1104, 1105, 1106,
        };

        /// <summary>
        /// Each effect of the spell at the grade cast, judged by what the board shows after the
        /// cast: an effect with a duration by the rows it left (on whom, how much, until when, against
        /// round + delay + duration); a blow by life lost; a heal by life gained; a movement by a cell
        /// changed; a summon by a new fighter; a mark by a new glyph or trap; a state by its row.
        /// What goes off later (another trigger, a delay) or by chance is said so, to be tried apart.
        /// </summary>
        /// <summary>
        /// The effects the engine keeps as rows of other numbers: a steal as the target's loss and
        /// the caster's gain, a shield in a share of the level as the shield's points.
        /// </summary>
        private static readonly Dictionary<int, int[]> KeptAs = new()
        {
            [266] = new[] { 152, 123 }, // chance stolen
            [267] = new[] { 153, 125 }, // vitality
            [268] = new[] { 154, 119 }, // agility
            [269] = new[] { 155, 126 }, // intelligence
            [270] = new[] { 156, 124 }, // wisdom
            [271] = new[] { 157, 118 }, // strength
            [77] = new[] { 169, 128 },  // MP stolen
            [84] = new[] { 168, 111 },  // AP stolen
            [320] = new[] { 116, 117 }, // range stolen
            [1020] = new[] { 1040 },    // shield, % of the level
            [1078] = new[] { 125 },     // vitality in %, as its flat amount (Vitalidad's capture: jxm 125)
            [1033] = new[] { 153 },     // vitality lost in %, as its flat amount (Ultimo Aliento)
        };

        /// <summary>
        /// The effects that have another spell cast (EffectEngine's family of sub-casts): the
        /// spell in the dice number, its grade in the dice side.
        /// </summary>
        private static readonly HashSet<int> SubCasts = new()
        {
            Jondo.Unity.World.Combat.EffectSupport.CastSpell, Jondo.Unity.World.Combat.EffectSupport.TriggerSpell,
            Jondo.Unity.World.Combat.EffectSupport.NearestTargetExecuteSpell,
            793, 1017, 1018, 1019, 2017, 2792, 2793, 2794, 2795, 2960,
        };

        /// <summary>The triggers of a spell's rows and of the spells it casts at once, three deep.</summary>
        private static List<string> TriggersWithin(int spell, int grade)
        {
            var found = new HashSet<string>();
            var seen = new HashSet<(int, int)>();
            var queue = new Queue<(int Spell, int Grade, int Depth)>();
            queue.Enqueue((spell, grade, 0));
            while (queue.Count > 0)
            {
                var (s, g, depth) = queue.Dequeue();
                if (!seen.Add((s, g))) continue;
                foreach (var x in SpellEffects.De(s, g))
                {
                    foreach (var t in x.Triggers.Split('|')) if (t.Length > 0) found.Add(t);
                    if (depth < 3 && SubCasts.Contains(x.EffectId) && x.DiceNum > 0)
                        queue.Enqueue((x.DiceNum, Math.Max(1, x.DiceSide), depth + 1));
                }
            }
            return found.OrderBy(t => t).ToList();
        }

        /// <summary>The states a spell takes off again within its own cast (951), in it or in the spells it casts, three deep.</summary>
        private static HashSet<int> TakenOffWithin(int spell, int grade)
        {
            var found = new HashSet<int>();
            var seen = new HashSet<(int, int)>();
            var queue = new Queue<(int Spell, int Grade, int Depth)>();
            queue.Enqueue((spell, grade, 0));
            while (queue.Count > 0)
            {
                var (s, g, depth) = queue.Dequeue();
                if (!seen.Add((s, g))) continue;
                foreach (var x in SpellEffects.De(s, g))
                {
                    if (x.EffectId == 951 && (x.Triggers.Length == 0 || x.Triggers == "I") && x.Delay == 0) found.Add(x.Value);
                    // A spell's rows taken off by the cast itself (406): kept as a negative number.
                    if (x.EffectId == Jondo.Unity.World.Combat.EffectSupport.RemoveSpellEffects && (x.Triggers.Length == 0 || x.Triggers == "I") && x.Delay == 0)
                        found.Add(-(x.DiceNum != 0 ? x.DiceNum : x.Value));
                    else if (depth < 3 && SubCasts.Contains(x.EffectId) && x.DiceNum > 0 && x.Delay == 0
                             && (x.Triggers.Length == 0 || x.Triggers == "I"))
                        queue.Enqueue((x.DiceNum, Math.Max(1, x.DiceSide), depth + 1));
                }
            }
            return found;
        }

        /// <summary>Whether a mask holds a condition (a token with a number, or T, a telefrag) and not only kinds of fighter.</summary>
        private static bool Gated(string? mask)
            => (mask ?? "").Split(',').Select(k => k.Trim().TrimStart('*')).Any(k => k.Length > 1 && k.Skip(1).Any(char.IsDigit)
                                                                                  // A telefrag; through a portal or not; with a shield or not; the one carried.
                                                                                  || k is "T" or "t" or "R" or "r" or "PB" or "pb" or "K");

        private static List<object> VerifyEffects(FightInstance fight, Fighter me, int spell, int grade, int round,
            Dictionary<long, (int CurrentHP, int CurrentAP, int CurrentMP, int CellId, int Count, bool IsAlive)> before,
            Dictionary<long, HashSet<int>> statesBefore, List<(Fighter Who, Buff Row)> newRows, List<Glifo> newGlyphs,
            Func<Fighter, string> roleOf, string? aimedRole = null, IReadOnlyList<string>? lines = null, int castCell = -1)
        {
            var castLines = lines ?? Array.Empty<string>();
            // A critical cast runs the critical rows (Coraza's 720% where the normal says 600%).
            bool critical = castLines.Any(l => l.Contains($"CRITICAL! with spell {spell} "));
            IReadOnlyList<SpellEffect> RowsOf(int s, int g) => critical && SpellEffects.Criticos(s, g).Count > 0 ? SpellEffects.Criticos(s, g) : SpellEffects.De(s, g);
            var result = new List<object>();
            var catalogue = EffectCatalogue.Value;
            var lostLife = fight.Todos.Where(f => before.TryGetValue(f.Id, out var b) && f.CurrentHP < b.CurrentHP).ToList();
            var gainedLife = fight.Todos.Where(f => before.TryGetValue(f.Id, out var b) && f.CurrentHP > b.CurrentHP).ToList();
            var moved = fight.Todos.Where(f => before.TryGetValue(f.Id, out var b) && f.CellId != b.CellId).ToList();
            var born = fight.Todos.Where(f => !before.ContainsKey(f.Id)).ToList();
            var lostPoints = fight.Todos.Where(f => before.TryGetValue(f.Id, out var b) && f.Id != me.Id
                                                    && (f.CurrentAP < b.CurrentAP || f.CurrentMP < b.CurrentMP)).ToList();
            string Names(IEnumerable<Fighter> fs) => string.Join(", ", fs.Select(f => $"{roleOf(f)} {f.Id}"));

            // The spell's effects, and those of the spells it casts at once (1160, 792, 2160), two deep.
            // The states the spell takes off again itself (951): a marker it sets for its own
            // effects -- Posición Defensiva's 5260 and 5373 -- is gone by the end of the cast.
            var takenOffAgain = TakenOffWithin(spell, grade);
            var pending = new Queue<(SpellEffect E, int Depth, int From, string? Gate)>(RowsOf(spell, grade).Select(x => (x, 0, spell, (string?)null)));
            while (pending.Count > 0)
            {
                var (e, depth, from, gate) = pending.Dequeue();
                catalogue.TryGetValue(e.EffectId, out var row);
                string text = row.Text ?? "";
                string lower = text.ToLowerInvariant();
                // The amount is #1..#2 (the dice) in most texts, #3 (the value) in those that name a spell first.
                bool byValue = text.Contains("#3") && !text.Contains("#2");
                int min = byValue ? e.Value : Math.Min(e.DiceNum, e.DiceSide == 0 ? e.DiceNum : e.DiceSide);
                int max = byValue ? e.Value : Math.Max(e.DiceNum, e.DiceSide);
                // A shield in a share of the caster's level (1020): its points are that share of it.
                if (e.EffectId == 1020) { min = min * me.StatLevel / 100; max = max * me.StatLevel / 100; }
                string verdict, observed;
                int expectedEnd = e.Duration < 0 ? -1 : round + e.Delay + Math.Max(1, e.Duration);
                var rows = newRows.Where(r => r.Row.EffectId == e.EffectId).ToList();
                // A characteristic steal leaves no row of its own: the engine splits it into a
                // loss on the target and a gain on the caster (271 strength -> 157 and 118).
                // A modifier of one spell (293, 280, 285...): the rows on that spell only.
                if (Enum.IsDefined(typeof(SpellAspect), e.EffectId) && e.DiceNum > 0)
                    rows = rows.Where(r => r.Row.HechizoAfectado == e.DiceNum).ToList();
                bool keptAsPoints = false;
                if (rows.Count == 0 && KeptAs.TryGetValue(e.EffectId, out var keptAs))
                {
                    rows = newRows.Where(r => keptAs.Contains(r.Row.EffectId)).ToList();
                    // A percentage kept as the points it came to (vitality in %): no dice to hold them to.
                    keptAsPoints = e.EffectId is 1078 or 1033;
                }

                bool immediate = string.Equals(e.Triggers, "I", StringComparison.OrdinalIgnoreCase) || e.Triggers.Length == 0;
                if (immediate && e.Delay == 0 && e.Duration == 0 && SubCasts.Contains(e.EffectId))
                {
                    int sub = e.DiceNum, subGrade = Math.Max(1, e.DiceSide);
                    verdict = "sub-spell";
                    observed = $"casts spell {sub} grade {subGrade}: its effects follow";
                    // A sub-spell cast only on a condition (a state, a kind of monster, a telefrag:
                    // anything in its mask beyond the plain kinds) hands that condition down.
                    string? passOn = gate ?? (Gated(e.TargetMask) ? e.TargetMask : null);
                    // Cast on one cell for friends only (or foes only), the spell aimed at the other
                    // kind: the sub-spell never went off (Explosión de Grisú's "1160 on g").
                    var castKinds = (e.TargetMask ?? "").Split(',').Select(k => k.Trim()).Where(k => k.Length == 1 && char.IsLetter(k[0])).ToList();
                    if (aimedRole is "foe" or "ally" && castKinds.Count > 0 && (char)e.Forma == 'P' && e.Tamano <= 1
                        && (aimedRole == "foe" ? !castKinds.Any(k => char.IsUpper(k[0])) : !castKinds.Any(k => char.IsLower(k[0]))))
                        passOn = $"not-for: its cast asks {e.TargetMask}, aimed at the {aimedRole}";
                    if (depth < 2 && sub > 0)
                        foreach (var inner in RowsOf(sub, subGrade)) pending.Enqueue((inner, depth + 1, sub, passOn));
                }
                else if (e.EffectId == Jondo.Unity.World.Combat.EffectSupport.RemoveSpellEffects)
                {
                    // The spell is the text's #2: in the value when the dice say nothing.
                    int which = e.DiceNum != 0 ? e.DiceNum : e.Value;
                    // On the spell being cast it takes away what its earlier casts left, and keeps
                    // what this one has just put (Centinela): only the older rows count.
                    var justPut = newRows.Select(r => r.Row).ToHashSet();
                    var left = fight.Todos.Where(f => f.Buffs.Puestos.Any(r => r.HechizoOrigen == which && !(which == spell && justPut.Contains(r)))).ToList();
                    verdict = left.Count == 0 ? "applied" : "missing";
                    observed = left.Count == 0 ? $"nobody keeps older effects of spell {which}" : $"effects of spell {which} still on {Names(left)}";
                }
                else if (!immediate)
                {
                    verdict = rows.Count > 0 ? "armed" : "deferred";
                    observed = rows.Count > 0 ? $"waiting on {Names(rows.Select(r => r.Who))} for trigger {e.Triggers}"
                                              : $"goes off on trigger {e.Triggers}: try it apart";
                }
                else if (e.Delay > 0)
                {
                    verdict = rows.Count > 0 ? "armed" : "missing";
                    observed = rows.Count > 0 ? $"pending on {Names(rows.Select(r => r.Who))}, goes off in round {round + e.Delay}"
                                              : $"a delay of {e.Delay} and no pending row";
                }
                else if (MovementEffects.Contains(e.EffectId))
                {
                    verdict = moved.Count > 0 ? "applied" : "missing";
                    observed = moved.Count > 0 ? "moved: " + string.Join(", ", moved.Select(f => $"{roleOf(f)} {f.Id} {before[f.Id].CellId}->{f.CellId}"))
                                               : "nobody moved";
                    // Pushed into something: no cell changed, the crash's damage instead. Right
                    // when the cell behind him, away from where the push comes from, is a wall,
                    // a hole or taken; wrong when it stood free.
                    // Pulled towards somebody standing right in front of him: nowhere to go.
                    if (moved.Count == 0 && e.EffectId == Jondo.Unity.World.Combat.EffectSupport.Pull && castCell >= 0
                        && fight.Todos.FirstOrDefault(f => f.IsAlive && f.CellId == castCell) is { } pulled)
                    {
                        var (px, py) = MapGeometry.CellToPoint(pulled.CellId);
                        var (cx, cy) = MapGeometry.CellToPoint(me.CellId);
                        int toward = MapGeometry.PointToCell(px + Math.Sign(cx - px), py + Math.Sign(cy - py));
                        var board = FightHandler.BoardOf(fight);
                        bool blocked = toward < 0 || !board.Walkable(toward) || fight.Todos.Any(f => f.IsAlive && f.CellId == toward);
                        if (blocked) { verdict = "blocked"; observed = $"{roleOf(pulled)} {pulled.Id} has cell {toward} taken or no ground in front of him"; }
                    }
                    var crashed = moved.Count > 0 ? new List<Fighter>()
                        : fight.Todos.Where(f => castLines.Any(l => l.Contains($"[Fight] {f.Id} crashes on being pushed"))).ToList();
                    if (crashed.Count > 0)
                    {
                        var board = FightHandler.BoardOf(fight);
                        var judged = crashed.Select(f =>
                        {
                            int from = castCell < 0 || f.CellId == castCell ? before[me.Id].CellId : castCell;
                            var (fx, fy) = MapGeometry.CellToPoint(from);
                            var (tx, ty) = MapGeometry.CellToPoint(f.CellId);
                            int dx = Math.Sign(tx - fx), dy = Math.Sign(ty - fy);
                            bool aligned = tx - fx == 0 || ty - fy == 0 || Math.Abs(tx - fx) == Math.Abs(ty - fy);
                            if (!aligned || dx == 0 && dy == 0) return (f, Free: false, What: "not in line with the push");
                            int behind = MapGeometry.PointToCell(tx + dx, ty + dy);
                            if (behind < 0 || !MapGeometry.IsValid(behind) || !board.Walkable(behind)) return (f, Free: false, What: $"cell {behind} behind is no ground");
                            var there = fight.Todos.FirstOrDefault(x => x.IsAlive && x.CellId == behind);
                            return there != null ? (f, Free: false, What: $"{roleOf(there)} {there.Id} on cell {behind} behind")
                                                 : (f, Free: true, What: $"cell {behind} behind stood free");
                        }).ToList();
                        // One pushed into another fighter: the one hit takes his share of the crash
                        // and the log says he "crashes" too, standing where the other was sent.
                        // Silbo pushes one foe into the next; the second was judged pushed into nothing.
                        var hitCells = judged.Where(j => !j.Free).Select(j => j.What).ToList();
                        judged = judged.Select(j => j.Free && hitCells.Any(w => w.Contains($" {j.f.Id} on cell {j.f.CellId} behind"))
                                                        ? (j.f, Free: false, What: "hit by another pushed into him")
                                                        : j).ToList();
                        verdict = judged.Any(j => j.Free) ? "wrong" : "blocked";
                        observed = "crashed: " + string.Join(", ", judged.Select(j => $"{roleOf(j.f)} {j.f.Id} ({j.What})"));
                    }
                }
                // A summon, not "+N invocaciones" (182), which is a characteristic and lands as a row.
                else if (e.EffectId == Jondo.Unity.World.Combat.EffectSupport.Summon || lower.Contains("invoca") && e.EffectId != 182)
                {
                    verdict = born.Count > 0 ? "applied" : "missing";
                    observed = born.Count > 0 ? "new: " + string.Join(", ", born.Select(f => $"{f.Id} monster {f.MonsterId} on {f.CellId}")) + $" (expected monster {e.DiceNum})"
                                              : "no new fighter";
                    // Aimed at a fighter's cell: nothing is born on a taken cell.
                    if (born.Count == 0 && castCell >= 0 && fight.Todos.Any(f => f.IsAlive && f.CellId == castCell))
                    {
                        verdict = "occupied-cell";
                        observed = $"no new fighter: the aimed cell {castCell} is taken";
                    }
                    if (born.Count > 0 && e.DiceNum > 0 && !born.Any(f => f.MonsterId == e.DiceNum)) verdict = "wrong";
                }
                // "Activa las runas" / "Activa los glifos": the caster's own marks go off -- none
                // on the board, nothing to see.
                else if (e.EffectId is Managers.EffectEngine.ActivaLasRunas or 1026)
                {
                    bool something = lostLife.Count > 0 || moved.Count > 0 || gainedLife.Count > 0 || newRows.Count > 0;
                    verdict = something ? "applied" : "untestable";
                    observed = something ? $"marks went off: {Names(lostLife.Concat(moved).Concat(gainedLife).Distinct())}" : "no mark of the caster's went off (none on the board?)";
                }
                else if (e.EffectId == Managers.EffectEngine.ColocaUnPortal)
                {
                    var portal = castCell >= 0 ? fight.Portales.At(castCell) : null;
                    verdict = portal != null ? "applied" : "missing";
                    observed = portal != null ? $"{portal} ({(portal.Active ? "active" : "not active yet")})" : $"no portal on cell {castCell}";
                }
                else if (e.EffectId == 1183)
                {
                    var off = fight.Portales.All.Where(x => !x.Active || x.NeutralisedBy != 0).ToList();
                    verdict = fight.Portales.Count == 0 ? "untestable" : off.Count > 0 ? "applied" : "missing";
                    observed = fight.Portales.Count == 0 ? "no portal on the board to switch off" : off.Count > 0 ? "off: " + string.Join(", ", off) : "every portal still active";
                }
                else if (e.EffectId is 400 or 401 or 402 or 1091 or 1165 || lower.Contains("glifo") || lower.Contains("trampa") || lower.Contains("runa"))
                {
                    verdict = newGlyphs.Count > 0 ? "applied" : "missing";
                    observed = newGlyphs.Count > 0 ? string.Join(", ", newGlyphs.Select(g => $"mark {g.Id} kind {g.Tipo} on {g.Casillas.Count} cell(s) casting {g.Hechizo}"))
                                                   : "no new mark on the ground";
                }
                else if (e.EffectId == Jondo.Unity.World.Combat.EffectSupport.AddState || e.EffectId == Jondo.Unity.World.Combat.EffectSupport.DisableState)
                {
                    // This effect's state only: a spell sets several, each with its own length.
                    var states = rows.Where(r => r.Row.Estado != 0 && r.Row.Estado == e.Value).ToList();
                    // Rows of this spell's, when there are: the same state from another spell --
                    // a summon born with it from its own spell -- is not this effect's doing.
                    // On whom the mask puts it: the caster (C, c), friends (lower case) or foes (upper
                    // case) -- Duelo Yopukil sets 7 for two turns on the Yopuka and one on the foe.
                    var stateKinds = (e.TargetMask ?? "").Split(',').Select(k => k.Trim()).Where(k => k.Length == 1 && char.IsLetter(k[0])).ToList();
                    if (stateKinds.Count > 0)
                    {
                        bool Fits(Fighter who)
                        {
                            string role = roleOf(who);
                            return role == "caster" ? stateKinds.Any(k => k is "C" or "c" or "a" or "l")
                                 : role == "ally" ? stateKinds.Any(k => char.IsLower(k[0]) && k != "c")
                                 : stateKinds.Any(k => char.IsUpper(k[0]) && k != "C");
                        }
                        var fitting = states.Where(r => Fits(r.Who)).ToList();
                        if (fitting.Count > 0) states = fitting;
                    }
                    var others = states.Where(r => r.Row.HechizoOrigen != from).ToList();
                    states = states.Where(r => r.Row.HechizoOrigen == from).ToList();
                    verdict = states.Count > 0 ? (states.All(r => r.Row.CaducaEnRonda == expectedEnd) ? "applied" : "duration") : "missing";
                    observed = states.Count > 0 ? string.Join(", ", states.Select(r => $"state {r.Row.Estado} on {roleOf(r.Who)} {r.Who.Id} until round {r.Row.CaducaEnRonda}")) + $" (expected until {expectedEnd})"
                                                : $"no state {e.Value} row";
                    // Only another spell's row of that state -- a summon born with it from its
                    // own spell, after this effect found nobody on the cell --: not this effect's.
                    if (states.Count == 0 && others.Count > 0)
                    {
                        verdict = "other-spell";
                        observed = $"state {e.Value} only from other spells: " + string.Join(", ", others.Select(r => $"{roleOf(r.Who)} {r.Who.Id} by spell {r.Row.HechizoOrigen}"));
                    }
                }
                else if (e.EffectId == Jondo.Unity.World.Combat.EffectSupport.RemoveState)
                {
                    var removed = fight.Todos.Where(f => statesBefore.TryGetValue(f.Id, out var had) && had.Contains(e.Value)
                                                         && !f.Buffs.Puestos.Any(r => r.Estado == e.Value)).ToList();
                    verdict = removed.Count > 0 ? "applied" : "nothing-to-remove";
                    observed = removed.Count > 0 ? $"state {e.Value} gone from {Names(removed)}" : $"nobody had state {e.Value}";
                }
                else if (e.EffectId >= Jondo.Unity.World.Combat.EffectSupport.FirstDamage && e.EffectId <= Jondo.Unity.World.Combat.EffectSupport.LastDamage
                         || lower.Contains("daños") && !lower.Contains("%") && e.Duration == 0 || lower.Contains("robo") && e.Duration == 0
                         // "-N% PdV" (1048): life that goes at once, whatever duration the level writes.
                         || e.EffectId == 1048)
                {
                    verdict = lostLife.Count > 0 ? "applied" : "missing";
                    observed = lostLife.Count > 0 ? "life lost: " + string.Join(", ", lostLife.Select(f => $"{roleOf(f)} {f.Id} {f.CurrentHP - before[f.Id].CurrentHP}")) + $" (dice {min}-{max})"
                                                  : "nobody lost life";
                }
                else if ((e.EffectId == Jondo.Unity.World.Combat.EffectSupport.FireHeal || e.EffectId == Jondo.Unity.World.Combat.EffectSupport.HealPercent
                          || lower.Contains("cura") && !lower.Contains("x#1")) && e.Duration == 0)
                {
                    verdict = gainedLife.Count > 0 ? "applied" : "untestable";
                    observed = gainedLife.Count > 0 ? "healed: " + Names(gainedLife) : "nobody gained life (all at full life)";
                }
                else if (rows.Count > 0)
                {
                    bool duration = rows.All(r => r.Row.CaducaEnRonda == expectedEnd || e.Duration == 0);
                    // A loss of AP or MP can be dodged in part: anything from none to the roll.
                    bool dodgeable = text.Contains("PA") || text.Contains("PM");
                    // A shield in a share of the level (1020) cast by a summon -- a 792's "the target
                    // casts" -- is a share of the summon's level, not the character's.
                    bool ShieldOfAnotherLevel(Buff row) => e.EffectId == 1020 && fight.Buscar(row.Quien) is { } by && by != me
                                                           && Math.Abs(row.Cuanto) >= e.DiceNum * by.StatLevel / 100 * 9 / 10
                                                           && Math.Abs(row.Cuanto) <= Math.Max(e.DiceNum, e.DiceSide) * by.StatLevel / 100 * 11 / 10;
                    bool value = keptAsPoints || rows.All(r => min == 0 && max == 0 || Math.Abs(r.Row.Cuanto) >= (dodgeable ? 0 : min) && Math.Abs(r.Row.Cuanto) <= Math.Max(max, min)
                                              || Math.Abs(r.Row.Valor) >= min && Math.Abs(r.Row.Valor) <= Math.Max(max, min) || r.Row.Cuanto == 0
                                              || ShieldOfAnotherLevel(r.Row));
                    verdict = !duration ? "duration" : !value ? "value" : "applied";
                    observed = string.Join(", ", rows.Select(r => $"{roleOf(r.Who)} {r.Who.Id}: {r.Row.Cuanto:+0;-0;0} until round {r.Row.CaducaEnRonda}"))
                               + $" (dice {min}-{max}, expected until {expectedEnd})";
                }
                else if (row.Characteristic > 0 || e.Duration != 0)
                {
                    bool points = lostPoints.Count > 0 && (lower.Contains("pa") || lower.Contains("pm"));
                    // A loss of AP or MP the target dodged whole leaves no row: the engine says so.
                    bool dodged = castLines.Any(l => l.Contains(" dodges ") && l.Contains($"effect {e.EffectId} of spell"));
                    verdict = points ? "applied" : dodged ? "dodged" : "missing";
                    observed = points ? "no row, but points lost: " + Names(lostPoints) : dodged ? "dodged by the target" : "no row on anybody";
                }
                else
                {
                    verdict = "no-rule";
                    observed = "the harness has no check for this effect yet";
                }
                if (e.Probabilidad > 0 && e.Probabilidad < 100 && verdict == "missing") verdict = "chance";
                // An effect whose mask asks for a state (E, *E: the target's or the caster's) that
                // nobody had did right not to go off.
                if (verdict == "missing" && System.Text.RegularExpressions.Regex.IsMatch(e.TargetMask ?? "", @"(^|,)\*?[Ee]\d"))
                {
                    verdict = "conditional";
                    observed += $" (mask {e.TargetMask})";
                }
                // A ring (O) or a hollow cross (Q) misses the cell it is aimed at: the harness aims
                // at a fighter, who is in the zone's empty middle, so a miss there says nothing.
                if (verdict == "missing" && (char)e.Forma is 'O' or 'Q' or '#')
                {
                    verdict = "hollow-zone";
                    observed += $" (zone {(char)e.Forma}{e.Tamano}, aimed at its empty middle)";
                }
                // A mask that asks for one kind of monster (F, f and a number) on a Puch: nothing was due.
                if (verdict == "missing" && System.Text.RegularExpressions.Regex.IsMatch(e.TargetMask ?? "", @"(^|,)\*?[Ff]\d"))
                {
                    verdict = "conditional";
                    observed += $" (mask {e.TargetMask})";
                }
                if (verdict == "missing" && takenOffAgain.Contains(-from) && from != spell)
                {
                    verdict = "transient";
                    observed += $" (the cast takes spell {from}'s rows off again itself)";
                }
                else if (verdict == "missing" && e.EffectId == 950 && takenOffAgain.Contains(e.Value))
                {
                    verdict = "transient";
                    observed += " (the spell takes the state off again itself)";
                }
                // Several variants of one effect, each on its own condition (Marca Mortuoria's 776
                // at 10, 15, 20, 25 or 30 by the caster's state): the rows are the variant that
                // applied, and a conditioned one does not answer for them.
                if (verdict is "value" or "duration" && (Gated(e.TargetMask)
                    && SpellEffects.De(from, from == spell ? grade : 1).Count(x => x.EffectId == e.EffectId) > 1
                    // A branch of a sub-spell cast on a condition: the rows may be another branch's
                    // (Furor's +20 the first time, +40 once it holds 609).
                    || gate != null && !gate.StartsWith("not-for:")))
                {
                    verdict = "variant";
                    observed += $" (another variant of {e.EffectId} applied; this one asks {e.TargetMask})";
                }
                if (verdict is "missing" or "duration" or "value" && gate != null && gate.StartsWith("not-for:"))
                {
                    verdict = "not-for-target";
                    observed += $" ({gate[9..]})";
                }
                else if (verdict == "missing" && gate != null)
                {
                    verdict = "conditional";
                    observed += $" (its sub-spell is cast on a condition: {gate})";
                }
                // A single-cell effect whose mask names only friends (lower case) or only foes
                // (upper case), the cast aimed at the other kind: nothing was due to land.
                var kinds = (e.TargetMask ?? "").Split(',').Select(k => k.Trim()).Where(k => k.Length == 1 && char.IsLetter(k[0])).ToList();
                if (verdict == "missing" && aimedRole != null && kinds.Count > 0 && (char)e.Forma == 'P' && e.Tamano <= 1)
                {
                    bool forFoes = kinds.Any(k => char.IsUpper(k[0])), forFriends = kinds.Any(k => char.IsLower(k[0]));
                    if (aimedRole == "foe" ? !forFoes : !forFriends)
                    {
                        verdict = "not-for-target";
                        observed += $" (mask {e.TargetMask}, aimed at the {aimedRole})";
                    }
                }
                result.Add(new { effect = e.EffectId, via = depth == 0 ? 0 : from, text, dice = new[] { min, max }, e.Duration, e.Delay, e.TargetMask, zone = $"{(char)e.Forma}{e.Tamano}", chance = e.Probabilidad, verdict, observed });
            }
            return result;
        }
    }
}
