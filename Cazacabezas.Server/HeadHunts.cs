using System.Text.Json;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Jondo.Unity.ServerMods;

namespace Cazacabezas.Server
{
    /// <summary>
    /// The head hunters, the Cazacabezas (<c>HeadHunters</c>): the old game's
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
    /// <c>HeadHuntWindowMinutes</c> minutes with a sighting every 45
    /// seconds, then <c>HeadHuntRestMinutes</c> of respite; the order lasts
    /// three days. The bounty grows with the prey's grade, his winning streak and his level, and
    /// the hunter who loses waits an hour for the next order.
    /// </para>
    /// <para>
    /// Kept in world.db: <c>HeadHunters (CharacterId, Chapeuvepes, Kills, Survivals, Failures,
    /// Streak, LockedUntilMs)</c> and <c>HeadHuntOrders (Hunter, Target, TakenMs, OpenUntilMs,
    /// RestUntilMs)</c>.
    /// </para>
    /// </remarks>
    internal sealed class HeadHunts(IAlignmentWorld world, IWorldStorage storage, ISimulatedPlayerWorld bots,
        HuntSettings settings, Action<string> log)
        : IPlayerActivityPolicy, IPvpEvents, IDialogueCurrency, ISimulatedPlayerEncounters
    {
        public const int MinHunterLevel = 50, MinTargetLevel = 30, LevelsBelow = 20, LevelsAbove = 50, HuntersPerTarget = 3;
        public const int KillHonour = 50, SurvivalHonour = 50, SurvivalPay = 1;

        public static readonly TimeSpan OrderLife = TimeSpan.FromDays(3);
        public static readonly TimeSpan FailedLock = TimeSpan.FromHours(1);
        public static readonly TimeSpan AbandonLock = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan Sighting = TimeSpan.FromSeconds(45);
        public static readonly TimeSpan Absence = TimeSpan.FromDays(14);

        // The client's own messages: info 217 and 180 and 80, warning 317.
        public const int NoticePlaced = 217, SurvivedHunter = 180, ValourReward = 80;

        public bool On => settings.Enabled;
        public TimeSpan Window => TimeSpan.FromMinutes(Math.Max(1, settings.HuntWindowMinutes));
        public TimeSpan Rest => TimeSpan.FromMinutes(Math.Max(0, settings.RestMinutes));

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

        private readonly ConcurrentDictionary<long, Hunter> _hunters = new();
        private readonly ConcurrentDictionary<long, Order> _orders = new();
        private readonly object _gate = new();
        private volatile bool _loaded;

        internal Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private long Now => Clock();

        // ─── The record and the chapeuvepés ─────────────────────────────────────────────────

        public Hunter Of(long characterId)
        {
            Load();
            return _hunters.TryGetValue(characterId, out var hunter) ? hunter : new Hunter(characterId);
        }

        public Order? OrderOf(long hunter)
        {
            Load();
            return _orders.TryGetValue(hunter, out var order) ? order : null;
        }

        /// <summary>The orders on one prey.</summary>
        public List<Order> OrdersOn(long target)
        {
            Load();
            return _orders.Values.Where(o => o.Target == target).ToList();
        }

        /// <summary>Whether a hunt on him is open right now: his wings cannot be folded.</summary>
        public bool HuntedNow(long target) => On && OrdersOn(target).Any(o => o.Open(Now));

        /// <summary>Whether that hunter's hunt on that prey is open right now.</summary>
        public bool HuntsNow(long hunter, long prey) => On && OrdersOn(prey).Any(o => o.Hunter == hunter && o.Open(Now));

        // ─── What the prey may not do while the hunt is open ───────────────────────────────

        public async Task<bool> BlocksActivityAsync(long me, PlayerActivity what, string language, Func<string, Task>? notify)
        {
            if (me == 0 || !HuntedNow(me)) return false;
            if (notify != null)
            {
                string doing = HuntTexts.Get(language, "hunt.held." + what.ToString().ToLowerInvariant());
                await notify(HuntTexts.Get(language, "hunt.held", doing));
            }
            log($"[Head hunts] {me} is hunted and may not go into a {what}.");
            return true;
        }

        /// <summary>Whether these two may fight each other: neither is a prey under an open hunt, or the other is his hunter.</summary>
        public bool MayFight(long one, long other)
            => (!HuntedNow(one) || HuntsNow(other, one)) && (!HuntedNow(other) || HuntsNow(one, other));

        /// <summary>
        /// A fight between these two refused, and why, to the one in session: he is the prey, or the
        /// other one is somebody else's. True when it was refused.
        /// </summary>
        public async Task<bool> BlocksDuelAsync(long me, long other, string language, Func<string, Task> notify)
        {
            if (MayFight(me, other)) return false;
            if (HuntedNow(me) && !HuntsNow(other, me)) return await BlocksActivityAsync(me, PlayerActivity.Fight, language, notify);

            await notify(HuntTexts.Get(language, "hunt.taken", NameOf(other)));
            log($"[Head hunts] {me} may not fight {other}, whom somebody else is hunting.");
            return true;
        }

        public int Cost(IReadOnlyDictionary<string, JsonElement> fields)
            => fields.TryGetValue("chapeuvepes", out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int cost) ? Math.Max(0, cost) : 0;

        public bool AdjustBalance(long characterId, int amount) => Pay(characterId, amount);

        public bool BlocksAutomaticEncounter(long characterId, AutomaticEncounter encounter)
            => encounter is AutomaticEncounter.MonsterAggression or AutomaticEncounter.TurnOffPvp
                && HuntedNow(characterId);

        /// <summary>Adjusts a character's chapeuvepé balance if it covers the amount.</summary>
        public bool Pay(long characterId, int chapeuvepes)
        {
            lock (_gate)
            {
                var hunter = Of(characterId);
                if (hunter.Chapeuvepes + chapeuvepes < 0) return false;
                Keep(hunter with { Chapeuvepes = hunter.Chapeuvepes + chapeuvepes });
                return true;
            }
        }

        private void Keep(Hunter hunter)
        {
            _hunters[hunter.Id] = hunter;
            Write("INSERT INTO HeadHunters (CharacterId, Chapeuvepes, Kills, Survivals, Failures, Streak, LockedUntilMs) " +
                  "VALUES ($id, $c, $k, $s, $f, $st, $l) ON CONFLICT(CharacterId) DO UPDATE SET Chapeuvepes = excluded.Chapeuvepes, " +
                  "Kills = excluded.Kills, Survivals = excluded.Survivals, Failures = excluded.Failures, Streak = excluded.Streak, " +
                  "LockedUntilMs = excluded.LockedUntilMs;",
                  ("$id", hunter.Id), ("$c", hunter.Chapeuvepes), ("$k", hunter.Kills), ("$s", hunter.Survivals),
                  ("$f", hunter.Failures), ("$st", hunter.Streak), ("$l", hunter.LockedUntilMs));
        }

        private void Keep(Order order)
        {
            _orders[order.Hunter] = order;
            Write("INSERT INTO HeadHuntOrders (Hunter, Target, TakenMs, OpenUntilMs, RestUntilMs) VALUES ($h, $t, $taken, $open, $rest) " +
                  "ON CONFLICT(Hunter) DO UPDATE SET Target = excluded.Target, TakenMs = excluded.TakenMs, " +
                  "OpenUntilMs = excluded.OpenUntilMs, RestUntilMs = excluded.RestUntilMs;",
                  ("$h", order.Hunter), ("$t", order.Target), ("$taken", order.TakenMs),
                  ("$open", order.OpenUntilMs), ("$rest", order.RestUntilMs));
        }

        private void Drop(Order order)
        {
            _orders.TryRemove(order.Hunter, out _);
            Write("DELETE FROM HeadHuntOrders WHERE Hunter = $h;", ("$h", order.Hunter));
        }

        // ─── The board ─────────────────────────────────────────────────────────────────────

        /// <summary>What a prey is worth to this hunter: his grade, his streak, his level.</summary>
        public int Bounty(long target, int targetLevel, int hunterLevel)
        {
            var alignment = world.AlignmentOf(target);
            int grade = Math.Max(1, alignment.HonourGrade);
            int streak = Of(target).Streak;
            return 2 + (grade + 1) / 2 + Math.Min(streak / 2, 5) + (targetLevel >= hunterLevel ? 1 : 0);
        }

        /// <summary>Why one may not hunt now, or null when he may.</summary>
        public string? CannotHunt(long hunterId, int hunterLevel)
        {
            if (!On) return "off";
            var mine = world.AlignmentOf(hunterId);
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
        public List<Candidate> CandidatesFor(long hunterId, int hunterLevel)
        {
            var mine = world.AlignmentOf(hunterId);
            var list = new List<Candidate>();
            if (mine.EnemySide is not int enemy) return list;
            int lowest = Math.Max(MinTargetLevel, Math.Min(hunterLevel, 200) - LevelsBelow);
            int highest = Math.Min(hunterLevel, 200) + LevelsAbove;
            var since = DateTimeOffset.UtcNow - Absence;
            try
            {
                foreach (var (character, lastConnection) in world.FindPvpCharacters(enemy, lowest))
                {
                    long id = character.Id;
                    int level = character.Level;
                    if (id == hunterId || Math.Min(level, 200) > highest) continue;
                    var session = world.FindPresence(id);
                    bool online = session != null && session.IsInWorld;
                    if (!online && (!DateTimeOffset.TryParse(lastConnection, CultureInfo.InvariantCulture,
                                                             DateTimeStyles.None, out var seen) || seen < since)) continue;
                    var theirs = world.AlignmentOf(id);
                    if (!theirs.PvpOn || theirs.Side != enemy) continue;
                    list.Add(new Candidate(id, character.Name, level, character.Breed, Math.Max(1, theirs.HonourGrade),
                                           online, Bounty(id, level, hunterLevel), OrdersOn(id).Count));
                }
            }
            catch (Exception ex)
            {
                log($"[Head hunts] The board could not be read: {ex.Message}");
            }
            return list.OrderByDescending(c => c.Online).ThenByDescending(c => c.Bounty).ThenBy(c => c.Name).ToList();
        }

        /// <summary>An order taken on a prey of the board: "ok", or why not.</summary>
        public async Task<string> TakeAsync(long hunterId, int hunterLevel, long targetId)
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
            await world.SendSystemMessageAsync(hunterId, NoticePlaced);
            log($"[Head hunts] {hunterId} hunts {targetId}.");
            return "ok";
        }

        /// <summary>
        /// He changed city or went back to neutral: the orders he had, and the ones on him, are void.
        /// </summary>
        public void AlignmentChanged(long characterId)
        {
            lock (_gate)
            {
                Load();
                foreach (var order in _orders.Values.Where(o => o.Hunter == characterId || o.Target == characterId).ToList())
                    Drop(order);
            }
        }

        /// <summary>A character deleted: his record and every order he was part of go with him.</summary>
        public void CharacterDeleted(long characterId)
        {
            lock (_gate)
            {
                AlignmentChanged(characterId);
                _hunters.TryRemove(characterId, out _);
                Write("DELETE FROM HeadHunters WHERE CharacterId = $id;", ("$id", characterId));
            }
        }

        /// <summary>The order given up; a new one waits ten minutes.</summary>
        public bool Abandon(long hunterId)
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
        public async Task TickAsync()
        {
            if (!On) return;
            Load();
            long now = Now;
            foreach (var order in _orders.Values.ToList())
            {
                if (now - order.TakenMs > (long)OrderLife.TotalMilliseconds)
                {
                    lock (_gate)
                    {
                        if (!IsCurrent(order)) continue;
                        Drop(order);
                    }
                    await NoticeAsync(order.Hunter, "hunt.expired", NameOf(order.Target));
                    continue;
                }

                var prey = world.FindPresence(order.Target);
                bool preyIn = prey != null && prey.IsInWorld;
                var hunter = world.FindPresence(order.Hunter);
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
                        lock (_gate)
                        {
                            if (!IsCurrent(order)) continue;
                            order.LastSightingMs = now;
                        }
                        await SightedAsync(order);
                    }
                }
                else if (order.OpenUntilMs != 0)
                {
                    await CloseAsync(order, now, "hunt.closed");
                }
                else if (preyIn && hunterIn && now >= order.RestUntilMs && world.AlignmentOf(order.Target).Sworn
                         && OutInTheOpen(prey!))
                {
                    await OpenAsync(order, now);
                }
            }
        }

        private bool OutInTheOpen(CharacterPresence prey)
        {
            if (prey.IsInFight) return false;
            var location = world.Locate(prey.MapId);
            return !location.KnownMap || location.WorldPosition.HasValue;
        }

        /// <summary>
        /// A hunt opens: the hunter is told, the prey too -- with his PvP switched on for as long as
        /// it lasts, his wings out --, and the first sighting goes at once.
        /// </summary>
        private async Task OpenAsync(Order order, long now)
        {
            using var quiet = bots.QuietForBots(order.Hunter, order.Target);
            var window = Window;
            lock (_gate)
            {
                if (!IsCurrent(order)) return;
                order.OpenUntilMs = now + (long)window.TotalMilliseconds;
                order.LastSightingMs = now;
                Keep(order);
            }
            int minutes = (int)Math.Round(window.TotalMinutes);
            await NoticeAsync(order.Hunter, "hunt.open", NameOf(order.Target), minutes);
            await NoticeAsync(order.Target, "hunt.hunted", minutes);
            if (!world.AlignmentOf(order.Target).Pvp)
                await world.RaisePvpWingsAsync(order.Target);
            await world.LeaveArenaQueueAsync(order.Target);
            await SightedAsync(order);
            log($"[Head hunts] The hunt of {order.Hunter} on {order.Target} opens for {minutes} minutes.");
        }

        private async Task CloseAsync(Order order, long now, string why)
        {
            lock (_gate)
            {
                if (!IsCurrent(order)) return;
                order.OpenUntilMs = 0;
                order.RestUntilMs = now + (long)Rest.TotalMilliseconds;
                Keep(order);
            }
            await NoticeAsync(order.Hunter, why, NameOf(order.Target));
            if (why == "hunt.closed") await NoticeAsync(order.Target, "hunt.respite");
        }

        private bool IsCurrent(Order order)
            => _orders.TryGetValue(order.Hunter, out var current) && ReferenceEquals(current, order);

        /// <summary>
        /// Where the prey was seen: his map's coordinates, a link the client sets a flag with -- or,
        /// when his map has none (the Koliseo's arenas, an instance, a house: [0,0] for having no
        /// place), that he is away from the world map and where he was last seen on it. Pointing at
        /// [0,0] sent the hunter to Amakna's village after a prey fighting in the Koliseo.
        /// </summary>
        private async Task SightedAsync(Order order)
        {
            string name = NameOf(order.Target);
            var prey = world.FindPresence(order.Target);
            var here = PlaceOf(prey);
            if (here == null)
            {
                if (order.LastSeen is { } last) await NoticeAsync(order.Hunter, "hunt.away.last", name, Link(last));
                else await NoticeAsync(order.Hunter, "hunt.away", name);
                return;
            }

            order.LastSeen = here;
            await NoticeAsync(order.Hunter, prey!.IsInFight ? "hunt.seen.fighting" : "hunt.seen", name, Link(here.Value));
        }

        private string Link((int X, int Y) at) => $"{{{{map,{at.X},{at.Y},1::[{at.X},{at.Y}]}}}}";

        public (int X, int Y)? SeenAt(Order order, CharacterPresence? prey)
            => PlaceOf(prey) ?? order.LastSeen;

        private (int X, int Y)? PlaceOf(CharacterPresence? prey)
            => prey == null ? null : world.Locate(prey.MapId).WorldPosition;

        // ─── The end of an aggression ──────────────────────────────────────────────────────

        /// <summary>
        /// An aggression of the cities' war over: the hunts it settles -- a hunter who beat his prey
        /// while the hunt was open, or a prey who beat his hunter -- and everyone's streak.
        /// </summary>
        public async Task AggressionEndedAsync(IReadOnlyList<PvpCombatant> winners, IReadOnlyList<PvpCombatant> losers)
        {
            if (!On) return;
            var won = winners.DistinctBy(f => f.Id).ToDictionary(f => f.Id, f => f.Level);
            var lost = losers.DistinctBy(f => f.Id).ToDictionary(f => f.Id, f => f.Level);
            long now = Now;

            lock (_gate)
            {
                foreach (long id in won.Keys)
                {
                    var record = Of(id);
                    Keep(record with { Streak = record.Streak + 1 });
                }
                foreach (long id in lost.Keys)
                {
                    var record = Of(id);
                    if (record.Streak != 0 || _hunters.ContainsKey(id)) Keep(record with { Streak = 0 });
                }
            }

            foreach (var order in _orders.Values.ToList())
            {
                if (!order.Open(now)) continue;
                bool hunterWon = won.TryGetValue(order.Hunter, out int hunterLevel) && lost.ContainsKey(order.Target);
                bool preyWon = won.ContainsKey(order.Target) && lost.ContainsKey(order.Hunter);
                if (hunterWon) await KilledAsync(order, now, hunterLevel, lost[order.Target]);
                else if (preyWon) await SurvivedAsync(order, now);
            }
        }

        private async Task KilledAsync(Order order, long now, int hunterLevel, int targetLevel)
        {
            using var quiet = bots.QuietForBots(order.Hunter, order.Target);
            int bounty;
            lock (_gate)
            {
                if (!IsCurrent(order) || !order.Open(now)) return;
                bounty = Bounty(order.Target, targetLevel, hunterLevel);
                Drop(order);
                var hunter = Of(order.Hunter);
                Keep(hunter with { Chapeuvepes = hunter.Chapeuvepes + bounty, Kills = hunter.Kills + 1 });
                world.AddHonour(order.Hunter, KillHonour);
            }

            await world.SendSystemMessageAsync(order.Hunter, ValourReward, KillHonour.ToString(CultureInfo.InvariantCulture));
            await NoticeAsync(order.Hunter, "hunt.killed", NameOf(order.Target), bounty);
            await NoticeAsync(order.Target, "hunt.fallen", NameOf(order.Hunter));
            log($"[Head hunts] {order.Hunter} got {order.Target}: {bounty} chapeuvepés.");
        }

        private async Task SurvivedAsync(Order order, long now)
        {
            using var quiet = bots.QuietForBots(order.Hunter, order.Target);
            lock (_gate)
            {
                if (!IsCurrent(order) || !order.Open(now)) return;
                Drop(order);
                var hunter = Of(order.Hunter);
                Keep(hunter with { Failures = hunter.Failures + 1, LockedUntilMs = Now + (long)FailedLock.TotalMilliseconds });
                var prey = Of(order.Target);
                Keep(prey with { Chapeuvepes = prey.Chapeuvepes + SurvivalPay, Survivals = prey.Survivals + 1 });
                world.AddHonour(order.Target, SurvivalHonour);
            }

            await world.SendSystemMessageAsync(order.Target, SurvivedHunter, NameOf(order.Hunter));
            await NoticeAsync(order.Target, "hunt.survived", SurvivalPay, SurvivalHonour);
            await NoticeAsync(order.Hunter, "hunt.failed", NameOf(order.Target));
            log($"[Head hunts] {order.Target} survived {order.Hunter}.");
        }

        private string NameOf(long characterId)
            => world.FindPresence(characterId)?.Name is { Length: > 0 } name
                ? name
                : world.FindCharacter(characterId)?.Name ?? characterId.ToString(CultureInfo.InvariantCulture);

        private Task NoticeAsync(long characterId, string key, params object[] values)
        {
            var presence = world.FindPresence(characterId);
            return presence?.IsInWorld == true
                ? world.SendNoticeAsync(characterId, HuntTexts.Get(presence.Language, key, values))
                : Task.CompletedTask;
        }

        // ─── The base ──────────────────────────────────────────────────────────────────────

        private void Load()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;
                try
                {
                    using var connection = storage.OpenWorldDatabase();
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
                    log($"[Head hunts] Could not read the hunters: {ex.Message}");
                }
                _loaded = true;
            }
        }

        private void Ensure(DbConnection connection)
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

        private void Write(string sql, params (string Name, object Value)[] parameters)
        {
            try
            {
                using var connection = storage.OpenWorldDatabase();
                Ensure(connection);
                var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) AddParameter(command, name, value);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                log($"[Head hunts] Could not keep a hunt: {ex.Message}");
            }
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        public async Task<bool> TryFindEncounterAsync(long characterId, int level, bool inWorld, Func<int, int> choose)
        {
            if (!On || OrderOf(characterId) != null || CannotHunt(characterId, level) != null || !inWorld)
                return false;
            var online = CandidatesFor(characterId, level).Where(p => p.Online && p.Hunters < HuntersPerTarget).ToList();
            return online.Count != 0 && await TakeAsync(characterId, level, online[choose(online.Count)].Id) == "ok";
        }

        public long? EncounterTarget(long characterId)
            => OrderOf(characterId) is { } order && order.Open(Now) ? order.Target : null;
    }
}
