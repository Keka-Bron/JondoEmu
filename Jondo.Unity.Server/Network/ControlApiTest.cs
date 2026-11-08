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
            alive = f.IsAlive, summon = f.EsInvocado,
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
            var me = fight.Buscar(session.State.CharacterId);
            int spell = (int)Numero(cuerpo, "spell");
            long targetId = Numero(cuerpo, "target");
            int target = targetId != 0 ? fight.Buscar(targetId)?.CellId ?? -1 : (int)Numero(cuerpo, "cell");
            if (me == null || target < 0) return Mal(404, "target");

            var board = FightHandler.BoardOf(fight);
            var reach = FightHandler.ReachOf(fight, me, spell);
            // A spell with no reach (0 to 0) is cast on the caster's own cell: nothing to stage.
            if (reach.Max <= 0)
                return Bien(new { cell = me.CellId, target = me.CellId, range = new[] { 0, 0 }, lines = new List<string>() });
            // A spell that wants a free cell -- a jump, a summon, a beacon -- aims beside the target.
            // The nearest free cell, beside it when one is: with the Puchs bunched and the earlier
            // summons around them, every neighbour can be taken.
            if (reach.FreeCell)
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
            int best = -1, bestScore = int.MaxValue;
            for (int cell = 0; cell < 560; cell++)
            {
                if (cell != me.CellId && (!board.Walkable(cell) || fight.Todos.Any(f => f.IsAlive && f.CellId == cell))) continue;
                int d = Jondo.Unity.World.Maps.MapGeometry.Distance(cell, target);
                if (d < min || d > max || !board.Sees(cell, target)) continue;
                int score = Math.Abs(d - wanted) * 10 + (cell % 14 == target % 14 || cell / 14 == target / 14 ? 0 : 1);
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
            var me = fight.Buscar(session.State.CharacterId);
            if (me == null) return Mal(404, "fighter");
            var lines = RunInSession(session, stream => FightHandler.FreshTurnAsync(stream, fight, me, keep, noSummons));
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
            try
            {
                lines = RunAsClient(session, "jxy", Pb.New().Build());
                var until = DateTime.UtcNow.AddSeconds(150);
                while (DateTime.UtcNow < until)
                {
                    System.Threading.Thread.Sleep(300);
                    if (FightHandler.FightOf(session.State.CharacterId) != fight || !me.IsAlive) break;
                    if (fight.RoundNumber > roundBefore && fight.CurrentFighter?.Id == me.Id) { back = true; break; }
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
            var me = fight.Buscar(session.State.CharacterId);
            if (me == null) return Mal(404, "fighter");
            int spell = (int)Numero(cuerpo, "spell");
            int cell = (int)Numero(cuerpo, "cell");

            var before = fight.Todos.ToDictionary(f => f.Id, f => (f.CurrentHP, f.CurrentAP, f.CurrentMP, f.CellId, f.Buffs.Puestos.Count, f.IsAlive));
            var rowsBefore = fight.Todos.SelectMany(f => f.Buffs.Puestos.Select(r => (f.Id, r.Numero))).ToHashSet();
            var statesBefore = fight.Todos.ToDictionary(f => f.Id, f => f.Buffs.Puestos.Where(r => r.Estado != 0).Select(r => r.Estado).ToHashSet());
            var glyphsBefore = fight.Glifos.Select(g => g.Id).ToHashSet();
            int round = fight.RoundNumber;
            int grade = FightHandler.CastingGradeOf(me, spell);
            var aimedAt = fight.Todos.FirstOrDefault(f => f.IsAlive && f.CellId == cell);
            var lines = RunAsClient(session, Op.Jwh, Pb.New().Var(1, cell).Var(4, spell).Build());

            var changes = fight.Todos.Select(f =>
            {
                before.TryGetValue(f.Id, out var b);
                return new
                {
                    f.Id, f.Name,
                    hp = f.CurrentHP - b.CurrentHP, ap = f.CurrentAP - b.CurrentAP, mp = f.CurrentMP - b.CurrentMP,
                    moved = b.CellId != f.CellId ? $"{b.CellId}->{f.CellId}" : null,
                    buffs = f.Buffs.Puestos.Count - b.Count, died = b.IsAlive && !f.IsAlive, isNew = !before.ContainsKey(f.Id),
                };
            }).Where(c => c.hp != 0 || c.ap != 0 || c.mp != 0 || c.moved != null || c.buffs != 0 || c.died || c.isNew).ToList();

            bool cast = lines.Any(l => l.Contains("Casts spell"));
            var newRows = fight.Todos.SelectMany(f => f.Buffs.Puestos.Where(r => !rowsBefore.Contains((f.Id, r.Numero))).Select(r => (Who: f, Row: r))).ToList();
            var newGlyphs = fight.Glifos.Where(g => !glyphsBefore.Contains(g.Id)).ToList();
            string RoleOf(Fighter f) => f.Id == me.Id ? "caster" : f.TeamId == me.TeamId ? "ally" : "foe";
            var effects = cast ? VerifyEffects(fight, me, spell, grade, round, before, statesBefore, newRows, newGlyphs, RoleOf,
                                               aimedAt == null ? null : RoleOf(aimedAt), lines) : new List<object>();
            return Bien(new { spell, grade, cell, cast, changes, glyphs = newGlyphs.Count, effects, lines });
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
        };

        /// <summary>Whether a mask holds a condition (a token with a number, or T, a telefrag) and not only kinds of fighter.</summary>
        private static bool Gated(string? mask)
            => (mask ?? "").Split(',').Select(k => k.Trim().TrimStart('*')).Any(k => k.Length > 1 && k.Skip(1).Any(char.IsDigit) || k is "T" or "t");

        private static List<object> VerifyEffects(FightInstance fight, Fighter me, int spell, int grade, int round,
            Dictionary<long, (int CurrentHP, int CurrentAP, int CurrentMP, int CellId, int Count, bool IsAlive)> before,
            Dictionary<long, HashSet<int>> statesBefore, List<(Fighter Who, Buff Row)> newRows, List<Glifo> newGlyphs,
            Func<Fighter, string> roleOf, string? aimedRole = null, IReadOnlyList<string>? lines = null)
        {
            var castLines = lines ?? Array.Empty<string>();
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
            var takenOffAgain = SpellEffects.De(spell, grade).Where(x => x.EffectId == 951).Select(x => x.Value).ToHashSet();
            var pending = new Queue<(SpellEffect E, int Depth, int From, string? Gate)>(SpellEffects.De(spell, grade).Select(x => (x, 0, spell, (string?)null)));
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
                string verdict, observed;
                int expectedEnd = e.Duration < 0 ? -1 : round + e.Delay + Math.Max(1, e.Duration);
                var rows = newRows.Where(r => r.Row.EffectId == e.EffectId).ToList();
                // A characteristic steal leaves no row of its own: the engine splits it into a
                // loss on the target and a gain on the caster (271 strength -> 157 and 118).
                // A modifier of one spell (293, 280, 285...): the rows on that spell only.
                if (Enum.IsDefined(typeof(SpellAspect), e.EffectId) && e.DiceNum > 0)
                    rows = rows.Where(r => r.Row.HechizoAfectado == e.DiceNum).ToList();
                if (rows.Count == 0 && KeptAs.TryGetValue(e.EffectId, out var keptAs))
                    rows = newRows.Where(r => keptAs.Contains(r.Row.EffectId)).ToList();

                bool immediate = string.Equals(e.Triggers, "I", StringComparison.OrdinalIgnoreCase) || e.Triggers.Length == 0;
                if (immediate && e.Delay == 0 && e.Duration == 0
                    && e.EffectId is Jondo.Unity.World.Combat.EffectSupport.TriggerSpell or Jondo.Unity.World.Combat.EffectSupport.CastSpell
                                  or Jondo.Unity.World.Combat.EffectSupport.NearestTargetExecuteSpell)
                {
                    int sub = e.DiceNum, subGrade = Math.Max(1, e.DiceSide);
                    verdict = "sub-spell";
                    observed = $"casts spell {sub} grade {subGrade}: its effects follow";
                    // A sub-spell cast only on a condition (a state, a kind of monster, a telefrag:
                    // anything in its mask beyond the plain kinds) hands that condition down.
                    string? passOn = gate ?? (Gated(e.TargetMask) ? e.TargetMask : null);
                    if (depth < 2 && sub > 0)
                        foreach (var inner in SpellEffects.De(sub, subGrade)) pending.Enqueue((inner, depth + 1, sub, passOn));
                }
                else if (e.EffectId == Jondo.Unity.World.Combat.EffectSupport.RemoveSpellEffects)
                {
                    var left = fight.Todos.Where(f => f.Buffs.Puestos.Any(r => r.HechizoOrigen == e.DiceNum)).ToList();
                    verdict = left.Count == 0 ? "applied" : "missing";
                    observed = left.Count == 0 ? $"nobody keeps effects of spell {e.DiceNum}" : $"effects of spell {e.DiceNum} still on {Names(left)}";
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
                }
                // A summon, not "+N invocaciones" (182), which is a characteristic and lands as a row.
                else if (e.EffectId == Jondo.Unity.World.Combat.EffectSupport.Summon || lower.Contains("invoca") && e.EffectId != 182)
                {
                    verdict = born.Count > 0 ? "applied" : "missing";
                    observed = born.Count > 0 ? "new: " + string.Join(", ", born.Select(f => $"{f.Id} monster {f.MonsterId} on {f.CellId}")) + $" (expected monster {e.DiceNum})"
                                              : "no new fighter";
                    if (born.Count > 0 && e.DiceNum > 0 && !born.Any(f => f.MonsterId == e.DiceNum)) verdict = "wrong";
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
                    verdict = states.Count > 0 ? (states.All(r => r.Row.CaducaEnRonda == expectedEnd) ? "applied" : "duration") : "missing";
                    observed = states.Count > 0 ? string.Join(", ", states.Select(r => $"state {r.Row.Estado} on {roleOf(r.Who)} {r.Who.Id} until round {r.Row.CaducaEnRonda}")) + $" (expected until {expectedEnd})"
                                                : $"no state {e.Value} row";
                }
                else if (e.EffectId == Jondo.Unity.World.Combat.EffectSupport.RemoveState)
                {
                    var removed = fight.Todos.Where(f => statesBefore.TryGetValue(f.Id, out var had) && had.Contains(e.Value)
                                                         && !f.Buffs.Puestos.Any(r => r.Estado == e.Value)).ToList();
                    verdict = removed.Count > 0 ? "applied" : "nothing-to-remove";
                    observed = removed.Count > 0 ? $"state {e.Value} gone from {Names(removed)}" : $"nobody had state {e.Value}";
                }
                else if (e.EffectId >= Jondo.Unity.World.Combat.EffectSupport.FirstDamage && e.EffectId <= Jondo.Unity.World.Combat.EffectSupport.LastDamage
                         || lower.Contains("daños") && !lower.Contains("%") && e.Duration == 0 || lower.Contains("robo") && e.Duration == 0)
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
                    bool value = rows.All(r => min == 0 && max == 0 || Math.Abs(r.Row.Cuanto) >= (dodgeable ? 0 : min) && Math.Abs(r.Row.Cuanto) <= Math.Max(max, min)
                                              || Math.Abs(r.Row.Valor) >= min && Math.Abs(r.Row.Valor) <= Math.Max(max, min) || r.Row.Cuanto == 0);
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
                if (verdict == "missing" && (char)e.Forma is 'O' or 'Q')
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
                if (verdict == "missing" && e.EffectId == 950 && takenOffAgain.Contains(e.Value))
                {
                    verdict = "transient";
                    observed += " (the spell takes the state off again itself)";
                }
                // Several variants of one effect, each on its own condition (Marca Mortuoria's 776
                // at 10, 15, 20, 25 or 30 by the caster's state): the rows are the variant that
                // applied, and a conditioned one does not answer for them.
                if (verdict is "value" or "duration" && Gated(e.TargetMask)
                    && SpellEffects.De(from, from == spell ? grade : 1).Count(x => x.EffectId == e.EffectId) > 1)
                {
                    verdict = "variant";
                    observed += $" (another variant of {e.EffectId} applied; this one asks {e.TargetMask})";
                }
                if (verdict == "missing" && gate != null)
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
