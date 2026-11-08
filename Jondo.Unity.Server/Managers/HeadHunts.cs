using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The head hunters, the Cazacabezas (<see cref="ServerSettings.HeadHunters"/>): the old game's
    /// «traques» brought back for the cities' war. A hunter of one city takes an order on a
    /// player of the other, chosen from the board; whenever the prey is in the world -- and the
    /// hunter too -- a hunt opens for a while, the hunter hears where the prey was last seen, and
    /// the prey cannot fold his wings; beating him in an aggression then pays chapeuvepés, the
    /// old pévétons, which Ema and Tomás Sacre still trade for scrolls, alichas and soul gems.
    /// The prey who beats his hunter is paid too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the client keeps of the old system: «Has puesto un anuncio de busca y captura» (info
    /// 217) for the order taken, «¡Has sobrevivido al cazarrecompensas {0}!» (180) for the prey
    /// who wins, «Ganas {0} puntos de honor en recompensa de tu gran valor» (80) for the hunter
    /// who does, «No puedes ocultar tu alineamiento en una zona de agresión» (warning 317) for the
    /// prey who tries to switch PvP off, and the Sacres' trade of chapeuvepés. In 1.29 the hunt
    /// needed level 50, both with PvP on, and the hunter was given the prey's position (the
    /// «Ordres d'Exécution»); a fight between them paid a pévéton.
    /// </para>
    /// <para>
    /// This server's rules, «violent but fun»: one order a hunter, three hunters a prey; the prey
    /// a sworn enemy with PvP on, of level 30 or more, from twenty levels below the hunter to the
    /// fifty above that an aggression allows, seen in the last two weeks; a hunt of
    /// <see cref="ServerSettings.HeadHuntWindowMinutes"/> minutes with a sighting every 45
    /// seconds, then <see cref="ServerSettings.HeadHuntRestMinutes"/> of respite; the order lasts
    /// three days. The bounty grows with the prey's grade, his winning streak and his level, and
    /// the hunter who loses waits an hour for the next order.
    /// </para>
    /// <para>
    /// Kept in world.db: <c>HeadHunters (CharacterId, Chapeuvepes, Kills, Survivals, Failures,
    /// Streak, LockedUntilMs)</c> and <c>HeadHuntOrders (Hunter, Target, TakenMs, OpenUntilMs,
    /// RestUntilMs)</c>.
    /// </para>
    /// </remarks>
    public static class HeadHunts
    {
        public const int MinHunterLevel = 50, MinTargetLevel = 30, LevelsBelow = 20, LevelsAbove = 50, HuntersPerTarget = 3;
        public const int KillHonour = 50, SurvivalHonour = 50, SurvivalPay = 1;

        public static readonly TimeSpan OrderLife = TimeSpan.FromDays(3);
        public static readonly TimeSpan FailedLock = TimeSpan.FromHours(1);
        public static readonly TimeSpan AbandonLock = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan Sighting = TimeSpan.FromSeconds(45);
        public static readonly TimeSpan Absence = TimeSpan.FromDays(14);

        // The client's own messages: info 217 and 180 and 80, warning 317.
        public const int NoticePlaced = 217, SurvivedHunter = 180, ValourReward = 80, CannotHide = 317;

        public static bool On => ServerSettings.Current.HeadHunters;
        public static TimeSpan Window => TimeSpan.FromMinutes(Math.Max(1, ServerSettings.Current.HeadHuntWindowMinutes));
        public static TimeSpan Rest => TimeSpan.FromMinutes(Math.Max(0, ServerSettings.Current.HeadHuntRestMinutes));

        /// <summary>A hunter's record: his chapeuvepés, his kills, the hunts he survived and lost, his streak.</summary>
        public sealed record Hunter(long Id, int Chapeuvepes = 0, int Kills = 0, int Survivals = 0, int Failures = 0,
                                    int Streak = 0, long LockedUntilMs = 0);

        /// <summary>An order: who hunts whom, since when, and the hunt's clock.</summary>
        public sealed class Order
        {
            public long Hunter { get; init; }
            public long Target { get; init; }
            public long TakenMs { get; init; }
            public long OpenUntilMs { get; set; }
            public long RestUntilMs { get; set; }
            public long LastSightingMs { get; set; }

            /// <summary>The last place on the world map the prey was seen at, kept while he is somewhere that has none.</summary>
            public (int X, int Y)? LastSeen { get; set; }
            public bool Open(long now) => OpenUntilMs > now;
        }

        /// <summary>A prey on the board.</summary>
        public sealed record Candidate(long Id, string Name, int Level, int Breed, int Grade, bool Online, int Bounty, int Hunters);

        private static readonly ConcurrentDictionary<long, Hunter> _hunters = new();
        private static readonly ConcurrentDictionary<long, Order> _orders = new();
        private static readonly object _gate = new();
        private static bool _loaded;

        internal static Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static long Now => Clock();

        // ─── The record and the chapeuvepés ─────────────────────────────────────────────────

        public static Hunter Of(long characterId)
        {
            Load();
            return _hunters.TryGetValue(characterId, out var hunter) ? hunter : new Hunter(characterId);
        }

        public static Order? OrderOf(long hunter)
        {
            Load();
            return _orders.TryGetValue(hunter, out var order) ? order : null;
        }

        /// <summary>The orders on one prey.</summary>
        public static List<Order> OrdersOn(long target)
        {
            Load();
            return _orders.Values.Where(o => o.Target == target).ToList();
        }

        /// <summary>Whether a hunt on him is open right now: his wings cannot be folded.</summary>
        public static bool HuntedNow(long target) => On && OrdersOn(target).Any(o => o.Open(Now));

        /// <summary>Whether that hunter's hunt on that prey is open right now.</summary>
        public static bool HuntsNow(long hunter, long prey) => On && OrdersOn(prey).Any(o => o.Hunter == hunter && o.Open(Now));

        // ─── What the prey may not do while the hunt is open ───────────────────────────────

        /// <summary>
        /// The ways out of the hunt a prey is denied while it is open: any fight but against his
        /// hunter, and every place the hunter cannot follow him into -- the Koliseo, a Merkasako,
        /// a dungeon, a house, his own or a friend's or the guild's. The server's rule: a hunt that
        /// could be waited out in a house or an arena is no hunt.
        /// </summary>
        public enum Hold { Fight, Koliseo, Merkasako, Dungeon, House }

        /// <summary>
        /// Whether the character in session is held by an open hunt from doing that; he is told so.
        /// Asked where the jail asks (<see cref="Jail.KeepsInAsync"/>), and before every fight.
        /// </summary>
        public static async Task<bool> HoldsAsync(System.Net.Sockets.NetworkStream? stream, Hold what)
        {
            long me = SessionContext.State.CharacterId;
            if (me == 0 || !HuntedNow(me)) return false;
            if (stream != null)
            {
                string language = SessionContext.State.Language;
                string doing = CommandTexts.Get(language, "hunt.held." + what.ToString().ToLowerInvariant());
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice(CommandTexts.Get(language, "hunt.held", (object)doing))));
            }
            Console.WriteLine($"[Head hunts] {me} is hunted and may not go into a {what}.");
            return true;
        }

        /// <summary>Whether these two may fight each other: neither is a prey under an open hunt, or the other is his hunter.</summary>
        public static bool MayFight(long one, long other)
            => (!HuntedNow(one) || HuntsNow(other, one)) && (!HuntedNow(other) || HuntsNow(one, other));

        /// <summary>
        /// A fight between these two refused, and why, to the one in session: he is the prey, or the
        /// other one is somebody else's. True when it was refused.
        /// </summary>
        public static async Task<bool> RefusesFightAsync(System.Net.Sockets.NetworkStream stream, long me, long other)
        {
            if (MayFight(me, other)) return false;
            if (HuntedNow(me) && !HuntsNow(other, me)) return await HoldsAsync(stream, Hold.Fight);

            string language = SessionContext.State.Language;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildNotice(CommandTexts.Get(language, "hunt.taken", (object)NameOf(other)))));
            Console.WriteLine($"[Head hunts] {me} may not fight {other}, whom somebody else is hunting.");
            return true;
        }

        /// <summary>Chapeuvepés given, or taken back; false when he has not that many to give back.</summary>
        public static bool Pay(long characterId, int chapeuvepes)
        {
            lock (_gate)
            {
                var hunter = Of(characterId);
                if (hunter.Chapeuvepes + chapeuvepes < 0) return false;
                Keep(hunter with { Chapeuvepes = hunter.Chapeuvepes + chapeuvepes });
                return true;
            }
        }

        private static void Keep(Hunter hunter)
        {
            _hunters[hunter.Id] = hunter;
            Write("INSERT INTO HeadHunters (CharacterId, Chapeuvepes, Kills, Survivals, Failures, Streak, LockedUntilMs) " +
                  "VALUES ($id, $c, $k, $s, $f, $st, $l) ON CONFLICT(CharacterId) DO UPDATE SET Chapeuvepes = excluded.Chapeuvepes, " +
                  "Kills = excluded.Kills, Survivals = excluded.Survivals, Failures = excluded.Failures, Streak = excluded.Streak, " +
                  "LockedUntilMs = excluded.LockedUntilMs;",
                  ("$id", hunter.Id), ("$c", hunter.Chapeuvepes), ("$k", hunter.Kills), ("$s", hunter.Survivals),
                  ("$f", hunter.Failures), ("$st", hunter.Streak), ("$l", hunter.LockedUntilMs));
        }

        private static void Keep(Order order)
        {
            _orders[order.Hunter] = order;
            Write("INSERT INTO HeadHuntOrders (Hunter, Target, TakenMs, OpenUntilMs, RestUntilMs) VALUES ($h, $t, $taken, $open, $rest) " +
                  "ON CONFLICT(Hunter) DO UPDATE SET Target = excluded.Target, TakenMs = excluded.TakenMs, " +
                  "OpenUntilMs = excluded.OpenUntilMs, RestUntilMs = excluded.RestUntilMs;",
                  ("$h", order.Hunter), ("$t", order.Target), ("$taken", order.TakenMs),
                  ("$open", order.OpenUntilMs), ("$rest", order.RestUntilMs));
        }

        private static void Drop(Order order)
        {
            _orders.TryRemove(order.Hunter, out _);
            Write("DELETE FROM HeadHuntOrders WHERE Hunter = $h;", ("$h", order.Hunter));
        }

        // ─── The board ─────────────────────────────────────────────────────────────────────

        /// <summary>What a prey is worth to this hunter: his grade, his streak, his level.</summary>
        public static int Bounty(long target, int targetLevel, int hunterLevel)
        {
            var alignment = Alignments.Of(target);
            int grade = Math.Max(1, alignment.HonourGrade);
            int streak = Of(target).Streak;
            return 2 + (grade + 1) / 2 + Math.Min(streak / 2, 5) + (targetLevel >= hunterLevel ? 1 : 0);
        }

        /// <summary>Why one may not hunt now, or null when he may.</summary>
        public static string? CannotHunt(long hunterId, int hunterLevel)
        {
            if (!On) return "off";
            var mine = Alignments.Of(hunterId);
            if (!mine.Sworn) return "neutral";
            if (!mine.PvpOn) return "pvp";
            if (hunterLevel < MinHunterLevel) return "level";
            if (mine.Dishonour > 0) return "dishonour";
            if (Of(hunterId).LockedUntilMs > Now) return "locked";
            return null;
        }

        /// <summary>
        /// The prey this hunter may choose: sworn to the other city, with PvP on, of level 30 or
        /// more, from twenty below him to fifty above, seen in the last two weeks; the online first.
        /// </summary>
        public static List<Candidate> CandidatesFor(long hunterId, int hunterLevel)
        {
            var mine = Alignments.Of(hunterId);
            var list = new List<Candidate>();
            if (!mine.Sworn) return list;
            int enemy = mine.Side == Alignments.Bonta ? Alignments.Brakmar : Alignments.Bonta;
            int lowest = Math.Max(MinTargetLevel, Math.Min(hunterLevel, 200) - LevelsBelow);
            int highest = Math.Min(hunterLevel, 200) + LevelsAbove;
            var since = DateTimeOffset.UtcNow - Absence;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT a.CharacterId, c.Name, c.Level, c.Breed, COALESCE(c.LastConnection, '') " +
                                      "FROM CharacterAlignments a JOIN Characters c ON c.Id = a.CharacterId " +
                                      "WHERE a.Side = $enemy AND a.Pvp = 1 AND c.Level >= $low;";
                command.Parameters.AddWithValue("$enemy", enemy);
                command.Parameters.AddWithValue("$low", lowest);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    int level = reader.GetInt32(2);
                    if (id == hunterId || Math.Min(level, 200) > highest) continue;
                    var session = SessionRegistry.FindByCharacter(id);
                    bool online = session != null && session.IsInWorld;
                    if (!online && (!DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture,
                                                             DateTimeStyles.None, out var seen) || seen < since)) continue;
                    var theirs = Alignments.Of(id);
                    if (!theirs.PvpOn || theirs.Side != enemy) continue;
                    list.Add(new Candidate(id, reader.GetString(1), level, reader.GetInt32(3), Math.Max(1, theirs.HonourGrade),
                                           online, Bounty(id, level, hunterLevel), OrdersOn(id).Count));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Head hunts] The board could not be read: {ex.Message}");
            }
            return list.OrderByDescending(c => c.Online).ThenByDescending(c => c.Bounty).ThenBy(c => c.Name).ToList();
        }

        /// <summary>An order taken on a prey of the board: "ok", or why not.</summary>
        public static async Task<string> TakeAsync(long hunterId, int hunterLevel, long targetId)
        {
            lock (_gate)
            {
                string? why = CannotHunt(hunterId, hunterLevel);
                if (why != null) return why;
                if (OrderOf(hunterId) != null) return "busy";
                var prey = CandidatesFor(hunterId, hunterLevel).FirstOrDefault(c => c.Id == targetId);
                if (prey == null) return "target";
                if (prey.Hunters >= HuntersPerTarget) return "full";
                Keep(new Order { Hunter = hunterId, Target = targetId, TakenMs = Now });
            }
            await TellAsync(hunterId, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(NoticePlaced)));
            Console.WriteLine($"[Head hunts] {hunterId} hunts {targetId}.");
            return "ok";
        }

        /// <summary>
        /// He changed city or went back to neutral: the orders he had, and the ones on him, are void.
        /// </summary>
        public static void Forget(long characterId)
        {
            lock (_gate)
            {
                Load();
                foreach (var order in _orders.Values.Where(o => o.Hunter == characterId || o.Target == characterId).ToList())
                    Drop(order);
            }
        }

        /// <summary>A character deleted: his record and every order he was part of go with him.</summary>
        public static void Erase(long characterId)
        {
            lock (_gate)
            {
                Forget(characterId);
                _hunters.TryRemove(characterId, out _);
                Write("DELETE FROM HeadHunters WHERE CharacterId = $id;", ("$id", characterId));
            }
        }

        /// <summary>The order given up; a new one waits ten minutes.</summary>
        public static bool Abandon(long hunterId)
        {
            lock (_gate)
            {
                var order = OrderOf(hunterId);
                if (order == null) return false;
                Drop(order);
                var hunter = Of(hunterId);
                Keep(hunter with { LockedUntilMs = Math.Max(hunter.LockedUntilMs, Now + (long)AbandonLock.TotalMilliseconds) });
                return true;
            }
        }

        // ─── The clock ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The hunts' clock, every few seconds: orders run out, hunts open when both are in the
        /// world and the prey's respite is over, sightings go out while they are open, and they
        /// close when their time is up or the prey has gone.
        /// </summary>
        public static async Task TickAsync()
        {
            if (!On) return;
            Load();
            long now = Now;
            foreach (var order in _orders.Values.ToList())
            {
                if (now - order.TakenMs > (long)OrderLife.TotalMilliseconds)
                {
                    Drop(order);
                    await NoticeAsync(order.Hunter, "hunt.expired", NameOf(order.Target));
                    continue;
                }

                var prey = SessionRegistry.FindByCharacter(order.Target);
                bool preyIn = prey != null && prey.IsInWorld;
                var hunter = SessionRegistry.FindByCharacter(order.Hunter);
                bool hunterIn = hunter != null && hunter.IsInWorld;

                if (order.Open(now))
                {
                    if (!preyIn)
                    {
                        await CloseAsync(order, now, "hunt.escaped");
                        continue;
                    }
                    if (now - order.LastSightingMs >= (long)Sighting.TotalMilliseconds && hunterIn)
                    {
                        order.LastSightingMs = now;
                        await SightedAsync(order, prey!);
                    }
                }
                else if (order.OpenUntilMs != 0)
                {
                    await CloseAsync(order, now, "hunt.closed");
                }
                else if (preyIn && hunterIn && now >= order.RestUntilMs && Alignments.Of(order.Target).Sworn
                         && OutInTheOpen(prey!))
                {
                    await OpenAsync(order, prey!, now);
                }
            }
        }

        /// <summary>
        /// Whether the prey stands where a hunt can open: out of a fight, on the world map. A hunt
        /// opened on him in the Koliseo or in a house had him somewhere the hunter cannot follow,
        /// from where all there was to say was [0,0]. A map the server knows nothing of is taken as
        /// open ground.
        /// </summary>
        private static bool OutInTheOpen(GameSession prey)
        {
            if (prey.State.IsInFight) return false;
            long map = prey.State.MapId;
            return MapManager.GetMapInfo(map) == null || MapManager.OnTheWorldMap(map);
        }

        /// <summary>
        /// A hunt opens: the hunter is told, the prey too -- with his PvP switched on for as long as
        /// it lasts, his wings out --, and the first sighting goes at once.
        /// </summary>
        private static async Task OpenAsync(Order order, GameSession prey, long now)
        {
            using var quiet = ConsoleLogBuffer.QuietWhile(Bots.SimulatedPlayers.OnlyBots(new[] { order.Hunter, order.Target }));
            order.OpenUntilMs = now + (long)Window.TotalMilliseconds;
            order.LastSightingMs = now;
            Keep(order);
            int minutes = (int)Math.Round(Window.TotalMinutes);
            await NoticeAsync(order.Hunter, "hunt.open", NameOf(order.Target), minutes);
            await NoticeAsync(order.Target, "hunt.hunted", minutes);
            if (!Alignments.Of(order.Target).Pvp)
            {
                Alignments.SetPvp(order.Target, true);
                using (SessionContext.Push(prey))
                {
                    await prey.SendAsync(ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
                    await SlotLookHandler.RedrawAsync(prey.Stream);
                }
            }
            // Out of the Koliseo's queue: a match would take him to an arena the hunter cannot reach.
            if (KoliseoQueue.Waits(order.Target) && prey.Stream != null)
                using (SessionContext.Push(prey))
                    await KoliseoHandler.LeaveQueueAsync(prey.Stream);
            await SightedAsync(order, prey);
            Console.WriteLine($"[Head hunts] The hunt of {order.Hunter} on {order.Target} opens for {minutes} minutes.");
        }

        private static async Task CloseAsync(Order order, long now, string why)
        {
            order.OpenUntilMs = 0;
            order.RestUntilMs = now + (long)Rest.TotalMilliseconds;
            Keep(order);
            await NoticeAsync(order.Hunter, why, NameOf(order.Target));
            if (why == "hunt.closed") await NoticeAsync(order.Target, "hunt.respite");
        }

        /// <summary>
        /// Where the prey was seen: his map's coordinates, a link the client sets a flag with -- or,
        /// when his map has none (the Koliseo's arenas, an instance, a house: [0,0] for having no
        /// place), that he is away from the world map and where he was last seen on it. Pointing at
        /// [0,0] sent the hunter to Amakna's village after a prey fighting in the Koliseo.
        /// </summary>
        private static async Task SightedAsync(Order order, GameSession prey)
        {
            string name = NameOf(order.Target);
            var here = PlaceOf(prey);
            if (here == null)
            {
                if (order.LastSeen is { } last) await NoticeAsync(order.Hunter, "hunt.away.last", name, Link(last));
                else await NoticeAsync(order.Hunter, "hunt.away", name);
                return;
            }

            order.LastSeen = here;
            await NoticeAsync(order.Hunter, prey.State.IsInFight ? "hunt.seen.fighting" : "hunt.seen", name, Link(here.Value));
        }

        private static string Link((int X, int Y) at) => $"{{{{map,{at.X},{at.Y},1::[{at.X},{at.Y}]}}}}";

        /// <summary>The prey's place on the world map, or null when where he is has none.</summary>
        public static (int X, int Y)? PlaceOf(GameSession prey)
        {
            long mapId = prey.State.MapId;
            if (!MapManager.OnTheWorldMap(mapId)) return null;
            var map = MapManager.GetMapInfo(mapId);
            return (map.PosX, map.PosY);
        }

        /// <summary>Where the mod's strip says the prey is: where he is on the world map, else where he was last.</summary>
        public static (int X, int Y)? SeenAt(Order order, GameSession? prey)
            => (prey == null ? null : PlaceOf(prey)) ?? order.LastSeen;

        // ─── The end of an aggression ──────────────────────────────────────────────────────

        /// <summary>
        /// An aggression of the cities' war over: the hunts it settles -- a hunter who beat his prey
        /// while the hunt was open, or a prey who beat his hunter -- and everyone's streak.
        /// </summary>
        public static async Task AggressionEndedAsync(FightInstance fight, bool attackersWon)
        {
            if (!On) return;
            var winners = People(attackersWon ? fight.Azul : fight.Rojo);
            var losers = People(attackersWon ? fight.Rojo : fight.Azul);
            long now = Now;

            foreach (long id in winners)
            {
                var record = Of(id);
                Keep(record with { Streak = record.Streak + 1 });
            }
            foreach (long id in losers)
            {
                var record = Of(id);
                if (record.Streak != 0 || _hunters.ContainsKey(id)) Keep(record with { Streak = 0 });
            }

            foreach (var order in _orders.Values.ToList())
            {
                if (!order.Open(now)) continue;
                bool hunterWon = winners.Contains(order.Hunter) && losers.Contains(order.Target);
                bool preyWon = winners.Contains(order.Target) && losers.Contains(order.Hunter);
                if (hunterWon) await KilledAsync(order, fight);
                else if (preyWon) await SurvivedAsync(order);
            }
        }

        private static async Task KilledAsync(Order order, FightInstance fight)
        {
            using var quiet = ConsoleLogBuffer.QuietWhile(Bots.SimulatedPlayers.OnlyBots(new[] { order.Hunter, order.Target }));
            int hunterLevel = fight.Buscar(order.Hunter)?.Level ?? 1;
            int targetLevel = fight.Buscar(order.Target)?.Level ?? 1;
            int bounty = Bounty(order.Target, targetLevel, hunterLevel);
            Drop(order);
            var hunter = Of(order.Hunter);
            Keep(hunter with { Chapeuvepes = hunter.Chapeuvepes + bounty, Kills = hunter.Kills + 1 });
            Alignments.AddHonour(order.Hunter, KillHonour);

            await TellAsync(order.Hunter, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(ValourReward, KillHonour.ToString(CultureInfo.InvariantCulture))));
            await NoticeAsync(order.Hunter, "hunt.killed", NameOf(order.Target), bounty);
            await NoticeAsync(order.Target, "hunt.fallen", NameOf(order.Hunter));
            Console.WriteLine($"[Head hunts] {order.Hunter} got {order.Target}: {bounty} chapeuvepés.");
        }

        private static async Task SurvivedAsync(Order order)
        {
            using var quiet = ConsoleLogBuffer.QuietWhile(Bots.SimulatedPlayers.OnlyBots(new[] { order.Hunter, order.Target }));
            Drop(order);
            var hunter = Of(order.Hunter);
            Keep(hunter with { Failures = hunter.Failures + 1, LockedUntilMs = Now + (long)FailedLock.TotalMilliseconds });
            var prey = Of(order.Target);
            Keep(prey with { Chapeuvepes = prey.Chapeuvepes + SurvivalPay, Survivals = prey.Survivals + 1 });
            Alignments.AddHonour(order.Target, SurvivalHonour);

            await TellAsync(order.Target, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(SurvivedHunter, NameOf(order.Hunter))));
            await NoticeAsync(order.Target, "hunt.survived", SurvivalPay, SurvivalHonour);
            await NoticeAsync(order.Hunter, "hunt.failed", NameOf(order.Target));
            Console.WriteLine($"[Head hunts] {order.Target} survived {order.Hunter}.");
        }

        private static HashSet<long> People(IEnumerable<Fighter> side)
            => side.Where(f => !f.IsMonster && !f.EsInvocado && f.Id > 0).Select(f => f.Id).ToHashSet();

        // ─── Telling ───────────────────────────────────────────────────────────────────────

        private static string NameOf(long characterId)
            => SessionRegistry.FindByCharacter(characterId)?.State.CharacterName is { Length: > 0 } name
                ? name
                : DatabaseManager.GetCharacterById(characterId)?.Name ?? characterId.ToString(CultureInfo.InvariantCulture);

        private static async Task TellAsync(long characterId, byte[] frame)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null || !session.IsInWorld) return;
            try { await session.SendAsync(frame); }
            catch (Exception ex) { Program.LogDebug($"[Head hunts] {characterId} could not be told: {ex.Message}"); }
        }

        /// <summary>One of the hunt's own lines, in the language of whoever reads it.</summary>
        private static Task NoticeAsync(long characterId, string key, params object[] values)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null || !session.IsInWorld) return Task.CompletedTask;
            string text = CommandTexts.Get(session.State.Language, key, values);
            return TellAsync(characterId, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));
        }

        // ─── The base ──────────────────────────────────────────────────────────────────────

        private static void Load()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;
                try
                {
                    using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();
                    Ensure(connection);
                    var hunters = connection.CreateCommand();
                    hunters.CommandText = "SELECT CharacterId, Chapeuvepes, Kills, Survivals, Failures, Streak, LockedUntilMs FROM HeadHunters;";
                    using (var r = hunters.ExecuteReader())
                        while (r.Read())
                            _hunters[r.GetInt64(0)] = new Hunter(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3),
                                                                 r.GetInt32(4), r.GetInt32(5), r.GetInt64(6));
                    var orders = connection.CreateCommand();
                    orders.CommandText = "SELECT Hunter, Target, TakenMs, OpenUntilMs, RestUntilMs FROM HeadHuntOrders;";
                    using (var r = orders.ExecuteReader())
                        while (r.Read())
                            _orders[r.GetInt64(0)] = new Order
                            {
                                Hunter = r.GetInt64(0), Target = r.GetInt64(1), TakenMs = r.GetInt64(2),
                                // A hunt open when the server went down is over: its respite starts again.
                                OpenUntilMs = 0, RestUntilMs = Math.Max(r.GetInt64(4), r.GetInt64(3)),
                            };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Head hunts] Could not read the hunters: {ex.Message}");
                }
                _loaded = true;
            }
        }

        private static void Ensure(SqliteConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS HeadHunters (
                    CharacterId   INTEGER PRIMARY KEY,
                    Chapeuvepes   INTEGER NOT NULL DEFAULT 0,
                    Kills         INTEGER NOT NULL DEFAULT 0,
                    Survivals     INTEGER NOT NULL DEFAULT 0,
                    Failures      INTEGER NOT NULL DEFAULT 0,
                    Streak        INTEGER NOT NULL DEFAULT 0,
                    LockedUntilMs INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS HeadHuntOrders (
                    Hunter      INTEGER PRIMARY KEY,
                    Target      INTEGER NOT NULL,
                    TakenMs     INTEGER NOT NULL,
                    OpenUntilMs INTEGER NOT NULL DEFAULT 0,
                    RestUntilMs INTEGER NOT NULL DEFAULT 0);";
            command.ExecuteNonQuery();
        }

        private static void Write(string sql, params (string Name, object Value)[] parameters)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                Ensure(connection);
                var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Head hunts] Could not keep a hunt: {ex.Message}");
            }
        }

        /// <summary>For the tests: everything forgotten, read again from the base at the next question.</summary>
        internal static void Reset()
        {
            lock (_gate)
            {
                _hunters.Clear();
                _orders.Clear();
                _loaded = false;
            }
        }
    }
}
