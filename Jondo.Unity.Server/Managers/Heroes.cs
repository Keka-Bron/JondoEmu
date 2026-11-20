using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The hero mode: the other characters of one's account connected beside one's own -- the
    /// master -- fighting at his side and played by hand from his client in their turns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ankama's Dofus 3 has none (« pas de mode Héros avant 2027 »); Wakfu's and Dofus Touch's
    /// are the model. A hero has no client of his own: his character is loaded into a session
    /// with no socket (<see cref="GameSession.SinSocket"/>), the way a player's is on selecting
    /// him, so that everything written against a session's state -- his sheet, his gear, his
    /// spells, his experience and his bag -- reads and writes his. That session is NOT in the
    /// registry: nobody sends to it, no map draws it, and a fight's people (Publico) leave it out.
    /// </para>
    /// <para>
    /// In a fight the master's client plays him as it plays a summon of its own -- his bar and
    /// his turns go to it (FightHandler.PlayAsHero, FightHeroes.cs) --, which the 3.6.10.11
    /// client does with no patch: measured on «.heros», 2026-11-15.
    /// </para>
    /// </remarks>
    public static class Heroes
    {
        /// <summary>The most heroes the settings may allow; the window's grid has eight places with the master.</summary>
        public const int Most = 7;

        /// <summary>One hero connected: his character and the session that holds it.</summary>
        public sealed class Hero
        {
            public long CharacterId { get; init; }
            public long MasterId { get; internal set; }
            public GameSession Session { get; init; } = null!;
            public DateTime Since { get; init; } = DateTime.UtcNow;

            /// <summary>His turns passed by the server as they come: the group panel's «automatic pass».</summary>
            public bool AutoPass { get; set; }
        }

        /// <summary>
        /// Who of each master's group takes the items all of them win -- himself or one of his
        /// heroes --; absent, each takes his own: the group panel's «loot recipient».
        /// </summary>
        private static readonly ConcurrentDictionary<long, long> _lootTo = new();

        /// <summary>Whom the master's group's items go to, or zero when each keeps his own.</summary>
        public static long LootTo(long masterId) => _lootTo.TryGetValue(masterId, out long to) ? to : 0;

        /// <summary>
        /// The group's items to one of it -- the master or a hero of his --, or back to each his
        /// own with zero or with the one who has them already. False for anybody else.
        /// </summary>
        public static bool SetLootTo(long masterId, long characterId)
        {
            if (characterId == 0 || LootTo(masterId) == characterId)
            {
                _lootTo.TryRemove(masterId, out _);
                return true;
            }
            if (characterId != masterId && AsHero(characterId)?.MasterId != masterId) return false;
            _lootTo[masterId] = characterId;
            return true;
        }

        /// <summary>A hero of his passes his turns on his own, or not. False when he is not one of his.</summary>
        public static bool SetAutoPass(long masterId, long heroId, bool on)
        {
            var hero = AsHero(heroId);
            if (hero == null || hero.MasterId != masterId) return false;
            hero.AutoPass = on;
            return true;
        }

        /// <summary>Whether this fighter is a hero whose turns pass on their own.</summary>
        public static bool PassesOnHisOwn(long characterId) => AsHero(characterId)?.AutoPass == true;

        // Sharing quests is on unless the player turned it off for a hero: forgetting to turn it on
        // is how a hero fell behind without anybody noticing (Keka-Bron/JondoEmu#206). Kept by
        // character, so the choice stays when he switches between master and hero; a restart of
        // the server puts it back on, the safe side.
        private static readonly ConcurrentDictionary<long, byte> _questSyncOff = new();

        internal static bool SharesQuests(long characterId) => !_questSyncOff.ContainsKey(characterId);

        internal static bool SetQuestSync(long masterId, long heroId, bool on)
        {
            lock (_lock)
            {
                if (AsHero(heroId)?.MasterId != masterId) return false;
                if (on) _questSyncOff.TryRemove(heroId, out _);
                else
                {
                    _questSyncOff[heroId] = 0;
                    // Turned off on purpose: what he had not followed is no longer news.
                    _questLags.TryRemove(heroId, out _);
                }
                return true;
            }
        }

        // ─── A hero who did not follow ───────────────────────────────────────────────────

        /// <summary>Why a hero did not follow his master's step of a quest.</summary>
        public enum QuestLagReason
        {
            /// <summary>Not on his master's map.</summary>
            Elsewhere = 1,
            /// <summary>In a fight, or his master was.</summary>
            InFight = 2,
            /// <summary>Not among the winners of the fight that ticked it.</summary>
            NotInTheFight = 3,
            /// <summary>Without the items the objective takes.</summary>
            MissingItems = 4,
            /// <summary>The reply that did it is not shared: it takes items or kamas, or moves the master.</summary>
            NotShared = 5,
            /// <summary>He does not have the quest.</summary>
            NotStarted = 6,
            /// <summary>He was not on the same step.</summary>
            Behind = 7,
            /// <summary>He does not meet the quest's conditions: his level, a quest before it.</summary>
            Requirements = 8,
        }

        /// <summary>What stopped a hero, with the item and how many of it he lacks when that is it.</summary>
        public sealed record LagCause(QuestLagReason Reason, int ItemId = 0, int ItemCount = 0);

        /// <summary>A quest a hero stayed behind in, and why.</summary>
        public sealed record QuestLag(int QuestId, int StepId, QuestLagReason Reason, int ItemId, int ItemCount);

        /// <summary>Each hero's quests he is behind his master in, by quest.</summary>
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<int, QuestLag>> _questLags = new();

        /// <summary>
        /// The quests a hero is still behind his master in. Those he has caught up with since --
        /// by doing the step himself, or his master having gone back -- are let go as they are read.
        /// </summary>
        public static IReadOnlyList<QuestLag> LagsOf(GameSession master, long heroId)
        {
            if (!_questLags.TryGetValue(heroId, out var lags) || lags.IsEmpty) return Array.Empty<QuestLag>();
            var hero = AsHero(heroId);
            if (hero == null || hero.MasterId != master.CharacterId) return Array.Empty<QuestLag>();
            foreach (int quest in lags.Keys)
            {
                if (Quests.ProgressOf(hero.Session, quest) >= Quests.ProgressOf(master, quest))
                    lags.TryRemove(quest, out _);
            }
            return lags.Values.OrderBy(l => l.QuestId).ToList();
        }

        /// <summary>
        /// Does to each hero of the current character who shares quests what the master just did,
        /// then, for the quests named, tells the master which heroes stayed behind and why.
        /// </summary>
        /// <param name="apply">The master's action, done again in each hero's session.</param>
        /// <param name="participates">Who takes part; by default the heroes on the master's map, out of a fight.</param>
        /// <param name="quests">The quests the action moved for the master: a hero left behind in one is told.</param>
        /// <param name="explain">Why a hero who took part still did not follow, when the action knows better than «not on the same step».</param>
        /// <summary>A hero who could not start the quest because of its conditions, for <see cref="ShareQuestActionAsync"/>'s explain.</summary>
        internal static LagCause? CouldNotStart(GameSession hero, int questId)
            => hero.State.Quests is { } log && !log.Active(questId) && !log.Finished(questId) && !log.CanStart(questId, out _)
                ? new LagCause(QuestLagReason.Requirements)
                : null;

        internal static async Task ShareQuestActionAsync(Func<GameSession, Task> apply,
                                                         Func<Hero, bool>? participates = null,
                                                         IReadOnlyCollection<int>? quests = null,
                                                         Func<GameSession, LagCause?>? explain = null)
        {
            var master = SessionContext.Current;
            long masterId = master.CharacterId;
            if (!On || masterId == 0 || AsHero(masterId) != null) return;

            foreach (var hero in Of(masterId))
            {
                if (!SharesQuests(hero.CharacterId) || AsHero(hero.CharacterId) != hero || hero.MasterId != masterId)
                    continue;
                bool eligible = participates?.Invoke(hero)
                    ?? (hero.Session.MapId == master.MapId && !hero.Session.State.IsInFight && !master.State.IsInFight);

                LagCause? cause = null;
                if (!eligible)
                {
                    cause = new LagCause(participates != null ? QuestLagReason.NotInTheFight
                        : hero.Session.MapId != master.MapId ? QuestLagReason.Elsewhere
                        : QuestLagReason.InFight);
                }
                else
                {
                    try
                    {
                        using (SessionContext.Push(hero.Session))
                            await apply(hero.Session);
                        if (hero.Session.State.AchievementsPending.Count > 0)
                            await Achievements.ForHeroAsync(master, hero.Session);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Heroes] Quest sharing failed for {hero.CharacterId}: {ex.Message}");
                    }
                }

                if (quests == null || quests.Count == 0) continue;
                try { await NoteLagsAsync(master, hero, quests, cause, explain); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Heroes] Could not tell whether {hero.CharacterId} followed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Each quest the hero is now behind his master in, kept with its reason; the master is told
        /// once a quest, when the hero falls behind in it, not again at each step after.
        /// </summary>
        private static async Task NoteLagsAsync(GameSession master, Hero hero, IReadOnlyCollection<int> quests,
                                                LagCause? cause, Func<GameSession, LagCause?>? explain)
        {
            var lags = _questLags.GetOrAdd(hero.CharacterId, _ => new ConcurrentDictionary<int, QuestLag>());
            foreach (int quest in quests.Distinct())
            {
                int heroAt = Quests.ProgressOf(hero.Session, quest);
                int masterAt = Quests.ProgressOf(master, quest);
                if (heroAt >= masterAt)
                {
                    lags.TryRemove(quest, out _);
                    continue;
                }
                if (lags.ContainsKey(quest)) continue;

                LagCause? explained = null;
                if (cause == null && explain != null)
                {
                    using (SessionContext.Push(hero.Session)) explained = explain(hero.Session);
                }
                LagCause why = cause ?? explained
                    ?? new LagCause(heroAt < 0 ? QuestLagReason.NotStarted : QuestLagReason.Behind);
                int step = master.State.Quests?.Run(quest)?.StepId ?? 0;
                lags[quest] = new QuestLag(quest, step, why.Reason, why.ItemId, why.ItemCount);
                await TellMasterAsync(master, hero, why);
                Console.WriteLine($"[Heroes] {hero.CharacterId} stayed behind {master.CharacterId} in quest {quest}: {why.Reason}" +
                                  (why.ItemId != 0 ? $" ({why.ItemCount}x{why.ItemId})" : ""));
            }

        }

        /// <summary>The master's warning, in his language: who stayed behind and why.</summary>
        private static async Task TellMasterAsync(GameSession master, Hero hero, LagCause why)
        {
            if (!master.IsInWorld) return;
            string language = master.State.Language;
            string name = hero.Session.State.CharacterName is { Length: > 0 } n ? n : hero.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string reason = why.Reason == QuestLagReason.MissingItems
                ? Handlers.CommandTexts.Get(language, "hero.quest.why.missingitems", why.ItemCount)
                : Handlers.CommandTexts.Get(language, "hero.quest.why." + why.Reason.ToString().ToLowerInvariant());
            string text = Handlers.CommandTexts.Get(language, "hero.quest.behind", name, reason);
            try
            {
                // Free text goes as information (type 0), red and bold as a dungeon's refusal: as a
                // warning (type 1), id 0 is an error number to the client, and it wrote «L'erreur
                // n°0 est survenue.» instead of the sentence.
                await master.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice($"<color=#FF0000><b>{text}</b></color>")));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Heroes] {master.CharacterId} could not be told about {hero.CharacterId}: {ex.Message}");
            }
        }

        /// <summary>Each master's heroes, in the order they were connected.</summary>
        private static readonly ConcurrentDictionary<long, List<Hero>> _byMaster = new();

        /// <summary>Whose hero each connected hero is.</summary>
        private static readonly ConcurrentDictionary<long, Hero> _byHero = new();

        private static readonly object _lock = new();

        public static bool On => ServerSettings.Current.HeroMode;

        public static int Max => Math.Clamp(ServerSettings.Current.HeroesMax, 1, Most);

        /// <summary>A master's heroes, in their order.</summary>
        public static IReadOnlyList<Hero> Of(long masterId)
        {
            lock (_lock) return _byMaster.TryGetValue(masterId, out var list) ? list.ToList() : new List<Hero>();
        }

        /// <summary>The hero a character is connected as, or null.</summary>
        public static Hero? AsHero(long characterId) => _byHero.TryGetValue(characterId, out var hero) ? hero : null;

        /// <summary>The connected hero with that name, case aside, or null: his session is in no registry.</summary>
        public static Hero? Named(string name)
            => string.IsNullOrWhiteSpace(name) ? null
               : _byHero.Values.FirstOrDefault(h => string.Equals(h.Session.State.CharacterName, name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The session of a hero, or null for anybody who is not one.</summary>
        public static GameSession? SessionOf(long characterId) => AsHero(characterId)?.Session;

        /// <summary>
        /// Connects one of the master's account's characters as his hero. "ok", or why not:
        /// off, self, account, online, already, full, fight, missing.
        /// </summary>
        public static string Connect(GameSession master, long heroId)
        {
            if (!On) return "off";
            long masterId = master.State.CharacterId;
            if (heroId == masterId) return "self";
            if (master.State.IsInFight) return "fight";
            if (!DatabaseManager.CharacterBelongsToAccount(heroId, master.AccountId)) return "account";
            // Somebody playing him from a client of his own: he is not free to be anybody's hero.
            if (SessionRegistry.FindByCharacter(heroId) != null) return "online";

            lock (_lock)
            {
                if (_byHero.ContainsKey(heroId)) return "already";
                var list = _byMaster.GetOrAdd(masterId, _ => new List<Hero>());
                if (list.Count >= Max) return "full";

                var session = GameSession.SinSocket();
                session.BindAccount(master.AccountId, master.ServerId, master.State.Language);
                using (SessionContext.Push(session))
                {
                    if (!Handlers.CharacterSelectionHandler.LoadForHero(heroId)) return "missing";
                }
                var hero = new Hero { CharacterId = heroId, MasterId = masterId, Session = session };
                list.Add(hero);
                _byHero[heroId] = hero;
            }
            Console.WriteLine($"[Heroes] {master.State.CharacterName} connects {SessionOf(heroId)?.State.CharacterName} ({heroId}) as a hero.");
            Appear(master, _byHero[heroId]);
            InParty(() => Handlers.PartyHandler.HeroJoinedAsync(master, heroId), heroId);
            Announce(heroId);
            return "ok";
        }

        /// <summary>The characters whose party is left as it is while a switch lets them go and takes them back.</summary>
        private static readonly ConcurrentDictionary<long, byte> _partyHeld = new();

        /// <summary>A switch begins (Network.HeroSwitch): these characters' party is not told of their going and coming.</summary>
        public static void HoldParty(IEnumerable<long> characterIds)
        {
            foreach (long id in characterIds) _partyHeld[id] = 0;
        }

        /// <summary>The switch is over: their party is told again.</summary>
        public static void ReleaseParty(IEnumerable<long> characterIds)
        {
            foreach (long id in characterIds) _partyHeld.TryRemove(id, out _);
        }

        /// <summary>A hero into his master's party, or out of the one he was in: the game's own window (Handlers.PartyHandler).</summary>
        private static void InParty(Func<Task> change, long heroId)
        {
            if (_partyHeld.ContainsKey(heroId)) return;
            try { change().GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine($"[Heroes] {heroId}'s party not told: {ex.Message}"); }
        }

        /// <summary>A hero at his master's side on the map, drawn for everybody there (Network.HeroPresence).</summary>
        private static void Appear(GameSession master, Hero hero)
        {
            if (master.Stream == null) return;
            try { Network.HeroPresence.ArriveAsync(master, hero).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine($"[Heroes] {hero.CharacterId} could not appear: {ex.Message}"); }
        }

        /// <summary>A hero let go, off the map he stood on.</summary>
        private static void Vanish(Hero hero)
        {
            try { Network.HeroPresence.LeaveAsync(hero).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine($"[Heroes] {hero.CharacterId} could not vanish: {ex.Message}"); }
        }

        /// <summary>His guild and his alliance see him as he stands now, in the world or not.</summary>
        private static void Announce(long characterId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Handlers.GuildHandler.TellConnectionAsync(characterId, SessionRegistry.IsOnline(characterId));
                    await Handlers.AllianceHandler.HeroConnectionAsync(characterId);
                }
                catch (Exception ex) { Console.WriteLine($"[Heroes] {characterId}'s guild and alliance not told: {ex.Message}"); }
            });
        }

        /// <summary>Disconnects a hero of his: saved as he stands. False when he is not one of his.</summary>
        public static bool Disconnect(long masterId, long heroId)
        {
            Hero? hero;
            lock (_lock)
            {
                if (!_byHero.TryGetValue(heroId, out hero) || hero.MasterId != masterId) return false;
                if (hero.Session.State.IsInFight) return false;
                Forget(hero);
            }
            Save(hero);
            return true;
        }

        /// <summary>The hero each account asked to take in hand, and when: the next way in selects him.</summary>
        private static readonly ConcurrentDictionary<long, (long Hero, DateTime At)> _switchTo = new();

        /// <summary>How long a switch waits for the client to come back to its character list.</summary>
        public static readonly TimeSpan SwitchWaits = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Taking a hero in hand at the character list: the hero is remembered for the account,
        /// and when its client comes back to the character list the server picks him for it -- the
        /// way a character still in a fight is picked (kvd, then kwb). False when he is not a hero of his.
        /// </summary>
        public static bool AskSwitch(GameSession master, long heroId)
        {
            if (AsHero(heroId)?.MasterId != master.State.CharacterId || master.State.IsInFight) return false;
            _switchTo[master.AccountId] = (heroId, DateTime.UtcNow);
            Console.WriteLine($"[Heroes] {master.State.CharacterName} asks to take {heroId} in hand.");
            return true;
        }

        /// <summary>Whether the account's client is to be handed a hero at its character list.</summary>
        public static bool SwitchPending(long accountId)
            => _switchTo.TryGetValue(accountId, out var ask) && DateTime.UtcNow - ask.At <= SwitchWaits;

        /// <summary>The hero the account's client is to be handed, taken: zero when none waits.</summary>
        public static long TakeSwitch(long accountId)
            => _switchTo.TryRemove(accountId, out var ask) && DateTime.UtcNow - ask.At <= SwitchWaits ? ask.Hero : 0;

        /// <summary>A group whose master left the world, kept for a while for whoever of it comes back.</summary>
        private sealed record Group(long MasterId, List<(long Id, bool AutoPass)> Heroes, long LootTo, DateTime LeftAt);

        /// <summary>Each account's group left behind, until one of it comes back or it is too old.</summary>
        private static readonly ConcurrentDictionary<long, Group> _leftBehind = new();

        /// <summary>How long a group waits for one of its characters to come back into the world.</summary>
        public static readonly TimeSpan GroupWaits = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Every hero of a master let go as he leaves the world, the group kept aside for his
        /// account (<see cref="Regroup"/>): changing character -- the client leaves and comes back
        /// -- is how one takes another of the group in hand.
        /// </summary>
        public static void DisconnectAll(GameSession master)
        {
            long masterId = master.State.CharacterId;
            var heroes = Of(masterId);
            if (heroes.Count > 0 && master.AccountId > 0)
                _leftBehind[master.AccountId] = new Group(masterId, heroes.Select(h => (h.CharacterId, h.AutoPass)).ToList(),
                                                          LootTo(masterId), DateTime.UtcNow);
            DisconnectAll(masterId);
        }

        /// <summary>
        /// The account's group back around the character that has just come into the world: the one
        /// who led it before is a hero now if it is another, and the heroes are his again with their
        /// switches. Nothing when the group is too old or he comes back into a fight.
        /// </summary>
        public static void Regroup(GameSession me)
        {
            if (!On || !_leftBehind.TryRemove(me.AccountId, out var group)) return;
            if (DateTime.UtcNow - group.LeftAt > GroupWaits || me.State.IsInFight) return;
            long id = me.State.CharacterId;
            var members = new List<(long Id, bool AutoPass)> { (group.MasterId, false) };
            members.AddRange(group.Heroes);
            foreach (var (member, autoPass) in members)
            {
                if (member == id) continue;
                if (Connect(me, member) == "ok" && autoPass) SetAutoPass(id, member, true);
            }
            if (group.LootTo != 0) SetLootTo(id, group.LootTo);
            Console.WriteLine($"[Heroes] {me.State.CharacterName} takes the group back: {Of(id).Count} hero(es).");
        }

        /// <summary>
        /// Every hero of a master let go: he is leaving the world. A hero in a fight goes with
        /// it as his master does; he is saved off the arena all the same.
        /// </summary>
        public static void DisconnectAll(long masterId)
        {
            List<Hero> gone;
            lock (_lock)
            {
                _lootTo.TryRemove(masterId, out _);
                if (!_byMaster.TryGetValue(masterId, out var list)) return;
                gone = list.ToList();
                foreach (var hero in gone) Forget(hero);
            }
            foreach (var hero in gone) Save(hero);
        }

        /// <summary>
        /// A master taking one of his heroes in hand (Network.HeroSwitch): that hero is let go and
        /// saved -- the master's client is about to be him --, and the others stay as they stand,
        /// on the map and in their fights' order, his heroes from now on. They are left in the
        /// group's round from the one after him; where in it the master he was comes back as a
        /// hero is returned (<see cref="PlaceAt"/>), or -1 when the hero is not his.
        /// </summary>
        public static int HandOver(long masterId, long heroId)
        {
            Hero? taken;
            int place;
            lock (_lock)
            {
                if (!_byHero.TryGetValue(heroId, out taken) || taken.MasterId != masterId) return -1;
                if (!_byMaster.TryGetValue(masterId, out var list)) return -1;
                int at = list.FindIndex(h => h.CharacterId == heroId);
                // The round: the master, then his heroes; from the one after the hero taken.
                var others = list.Skip(at + 1).Concat(list.Take(at)).ToList();
                place = list.Count - 1 - at;
                _lootTo.TryRemove(masterId, out _);
                _byHero.TryRemove(heroId, out _);
                _byMaster.TryRemove(masterId, out _);
                foreach (var other in others) other.MasterId = heroId;
                if (others.Count > 0) _byMaster[heroId] = others;
            }
            Save(taken);
            return place;
        }

        /// <summary>A hero just connected moved to his place in his master's heroes' order.</summary>
        public static void PlaceAt(long masterId, long heroId, int place)
        {
            lock (_lock)
            {
                if (!_byMaster.TryGetValue(masterId, out var list)) return;
                int at = list.FindIndex(h => h.CharacterId == heroId);
                if (at < 0) return;
                var hero = list[at];
                list.RemoveAt(at);
                list.Insert(Math.Clamp(place, 0, list.Count), hero);
            }
        }

        /// <summary>
        /// A character chosen to play from a client of his own stops being anybody's hero: he
        /// cannot be in the world twice.
        /// </summary>
        public static void Release(long characterId)
        {
            var hero = AsHero(characterId);
            if (hero == null) return;
            lock (_lock) Forget(hero);
            Save(hero);
        }

        private static void Forget(Hero hero)
        {
            _byHero.TryRemove(hero.CharacterId, out _);
            if (LootTo(hero.MasterId) == hero.CharacterId) _lootTo.TryRemove(hero.MasterId, out _);
            if (_byMaster.TryGetValue(hero.MasterId, out var list))
            {
                list.RemoveAll(h => h.CharacterId == hero.CharacterId);
                if (list.Count == 0) _byMaster.TryRemove(hero.MasterId, out _);
            }
        }

        /// <summary>A hero let go, already forgotten: off his map, saved, and his guild and alliance told.</summary>
        private static void Save(Hero hero)
        {
            Vanish(hero);
            InParty(() => Handlers.PartyHandler.HeroLeftAsync(hero.CharacterId), hero.CharacterId);
            try
            {
                using (SessionContext.Push(hero.Session))
                {
                    hero.Session.State.IsInFight = false;
                    DatabaseManager.SaveCurrentCharacter();
                }
                Console.WriteLine($"[Heroes] {hero.Session.State.CharacterName} ({hero.CharacterId}) disconnected and saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Heroes] Could not save hero {hero.CharacterId}: {ex.Message}");
            }
            Announce(hero.CharacterId);
        }
    }
}
