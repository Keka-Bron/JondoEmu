using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Achievements;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The achievements: earning them, showing them in the window, and paying for them.
    /// </summary>
    /// <remarks>
    /// Same two halves as the quests. Ankama's catalogue is the same for everybody and is read once
    /// at startup; what one character has earned lives on <see cref="SessionState"/> and is reached
    /// through <c>SessionContext.State</c>. A static dictionary here would hand somebody else's
    /// badge to whoever logged in second.
    ///
    /// <b>Earning and being paid are two different things</b>, and the capture is what says so.
    /// <c>Logros\aceptar recompensas de un logro</c> is a player pressing the claim button: the
    /// client sends <c>mga {1: 8990}</c> and only then does the reward arrive. So the server marks
    /// an achievement earned when it is earned — <c>mfu</c>, and nothing else — and hands over the
    /// rewards when it is asked, closing with <c>mfs</c>.
    ///
    /// <b>What is looked at, and when.</b> The catalogue files every achievement under what its
    /// objectives mention, and each thing that happens asks only for its own: a finished quest,
    /// a new zone, a fight won, an item crafted, a job or a character level. Entering the world
    /// looks at everything once, so what a character did before this engine could judge it is
    /// recognised the next time they play.
    /// </remarks>
    public static class Achievements
    {
        /// <summary>
        /// The game type this server runs as, for <c>SC</c>: 0, "Clásico", in the client's
        /// server_game_types table. The achievements written for Temporis (5) do not apply here.
        /// </summary>
        public const int ServerGameType = 0;

        /// <summary>
        /// Volatile, and assigned only once the catalogue is fully built: readers take no lock,
        /// so this assignment is what publishes it to them.
        /// </summary>
        private static volatile AchievementCatalogue? _book;
        private static readonly object _loadLock = new object();

        /// <summary>This character's badges. Null before entering the world.</summary>
        public static AchievementLog? Log => SessionContext.State.Achievements;

        /// <summary>Reads the catalogue. Once, at startup.</summary>
        public static void Load()
        {
            if (_book != null) return;
            lock (_loadLock)
            {
                if (_book != null) return;

                var book = new AchievementCatalogue(null, Console.WriteLine);
                _book = book;

                if (!book.Ready)
                {
                    Console.WriteLine("[Achievements] There is no catalogue. None will be earned.");
                    return;
                }

                Console.WriteLine($"[Achievements] {book.Count:N0} achievements, {book.ObjectiveCount:N0} objectives, " +
                                  $"{book.RewardCount:N0} rewards, {book.FromQuestsCount:N0} " +
                                  $"earned by finishing quests, {book.LinkedCount} server objectives " +
                                  "tied to areas, levels and jobs.");
            }
        }

        /// <summary>
        /// Any character's achievement points, connected or not: what the guild members list shows in its
        /// «Logros» (Achievements) column.
        /// </summary>
        public static int PointsOf(long characterId)
        {
            if (_book == null || !_book.Ready) return 0;

            int total = 0;
            foreach (var (achievement, _) in DatabaseManager.LoadAchievements(characterId))
            {
                total += _book.Of(achievement)?.Points ?? 0;
            }

            return total;
        }

        /// <summary>Every character's achievement points, read at once: the ladder window's achievements tab.</summary>
        public static Dictionary<long, int> AllPoints()
        {
            var points = new Dictionary<long, int>();
            if (_book == null || !_book.Ready) return points;

            foreach (var (characterId, achievement) in DatabaseManager.LoadAllAchievements())
            {
                points[characterId] = points.GetValueOrDefault(characterId) + (_book.Of(achievement)?.Points ?? 0);
            }

            return points;
        }

        /// <summary>
        /// Puts this character's badges on, from the database, with the tallies they count.
        /// </summary>
        /// <remarks>
        /// Nothing is taken away on login, deliberately. An achievement earned under an older
        /// version of the rules stays earned: taking somebody's badge away because this emulator
        /// got better at judging is worse than leaving one that should not have been given.
        /// </remarks>
        public static void LoadFrom(long characterId)
        {
            var state = SessionContext.State;
            state.AchievementTallies = DatabaseManager.LoadAchievementCounters(characterId);
            state.ChallengesDone = DatabaseManager.LoadChallengesDone(characterId);
            state.AchievementLevelChecked = 0;
            state.AchievementsPending.Clear();

            if (_book == null || !_book.Ready)
            {
                state.Achievements = null;
                return;
            }

            var log = new AchievementLog(_book, new CharacterFacts());

            // The account's, not the character's: what one character of the account achieved is
            // achieved for all of them and paid once (the captured lists name up to four different
            // characters). Read per character, another character of the same account earned it all
            // again and was paid for it again. The character's own row is preferred, then a paid one.
            long account = DatabaseManager.GetCharacterById(characterId)?.AccountId ?? 0;
            var rowsOf = account != 0
                ? DatabaseManager.LoadAccountAchievements(account)
                : DatabaseManager.LoadAchievements(characterId)
                    .Select(r => (CharacterId: characterId, r.Achievement, r.Claimed, Level: 0)).ToList();

            int rows = 0;
            foreach (var group in rowsOf.GroupBy(r => r.Achievement))
            {
                // One every character earns for himself (the guided tutorial's «Primer tiempo»):
                // another character's leaves it to be earned and paid again.
                if (_book.Of(group.Key)?.EachCharacter == true && group.All(r => r.CharacterId != characterId)) continue;

                bool claimed = group.Any(r => r.Claimed);
                long achiever = group.Any(r => r.CharacterId == characterId) ? characterId
                                : group.Where(r => r.Claimed).Select(r => r.CharacterId).FirstOrDefault();
                if (achiever == 0) achiever = group.First().CharacterId;
                log.Restore(group.Key, claimed, achiever);
                log.NoteLevel(group.Key, group.Where(r => r.CharacterId == achiever).Select(r => r.Level).FirstOrDefault());
                rows++;
            }

            state.Achievements = log;
            if (rows > 0)
            {
                Console.WriteLine($"[Achievements] {rows} in the showcase of character {characterId}, " +
                                  $"{log.Points} points.");
            }
        }

        // ─── Entering the world ───────────────────────────────────────────────────

        /// <summary>
        /// The character's achievements (mft), in place of the recorded account's 954, and then
        /// whatever they had already done without it being recognised.
        /// </summary>
        public static async Task SendListAsync(NetworkStream stream)
        {
            var log = Log;
            if (log == null) return;

            var state = SessionContext.State;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Mft, AchievementProtocol.BuildList(ListOf(log, state.CharacterId, state.CharacterLevel))));

            var earned = log.CheckEverything();
            state.AchievementLevelChecked = state.CharacterLevel;
            await AnnounceAsync(stream, earned);
        }

        /// <summary>
        /// The list mft carries: every achievement of the account, sorted, each with the character
        /// that achieved it, the unpaid ones with the level.
        /// </summary>
        public static List<(int Achievement, long AchievedBy, int UnclaimedLevel)> ListOf(AchievementLog log, long characterId, int level)
        {
            var list = new List<(int, long, int)>();
            foreach (int id in log.Earned.OrderBy(i => i))
            {
                long by = log.AchieverOf(id);
                // The level it was earned at, which the window reckons the reward at; the
                // character's own for one earned before that was kept.
                int earnedAt = log.LevelOf(id);
                list.Add((id, by != 0 ? by : characterId, log.WasClaimed(id) ? 0 : LevelOnTheWire(earnedAt > 0 ? earnedAt : level)));
            }

            return list;
        }

        /// <summary>
        /// The level an achievement message carries: the character's, stopping at 200. Measured:
        /// the two characters of the route captures hold the experience of omega 53 and omega 154,
        /// and all fifteen of their mfu say 200.
        /// </summary>
        public static int LevelOnTheWire(int level) => Math.Clamp(level, 1, RewardFormula.LevelCap);

        // ─── What moves them ─────────────────────────────────────────────────────

        /// <summary>A quest has just been finished. Grant whatever that earned.</summary>
        public static Task AfterQuestAsync(NetworkStream stream, int questId)
        {
            var book = _book;
            if (book == null) return Task.CompletedTask;

            // An alignment quest or an apprenticeship moves what «Pa>19» and «Pr=2|...» ask.
            var (level, rank) = Alignments.Moves(questId);
            return CheckAsync(stream, book.WaitingOn("Qf", questId)
                .Concat(book.WaitingOn("Qc", questId))
                .Concat(book.WaitingOn("QF", questId))
                .Concat(book.WaitingOn("QQ"))
                .Concat(level ? book.WaitingOn("Pa") : Enumerable.Empty<int>())
                .Concat(rank ? book.WaitingOn("Pr") : Enumerable.Empty<int>()));
        }

        /// <summary>
        /// A quest objective has just been validated: the achievements waiting on it (Qo). The
        /// tutorial's «Primer tiempo» is earned this way, on 2502's objective 19173.
        /// </summary>
        public static Task AfterObjectiveAsync(NetworkStream stream, int objectiveId)
        {
            var book = _book;
            if (book == null) return Task.CompletedTask;
            var waiting = book.WaitingOn("Qo", objectiveId);
            return waiting.Count == 0 ? Task.CompletedTask : CheckAsync(stream, waiting);
        }

        /// <summary>
        /// The character has arrived on a map: the zone it belongs to is explored, and what the
        /// level, the map and the bag now hold is looked at.
        /// </summary>
        /// <remarks>
        /// Exploration is the measured one: the long-route captures earn "Landas de Cania",
        /// "Bosque de Litneg" and fifteen more on arriving at a map of the subarea of that name.
        /// The level is looked at here too, and only when it changed, because this is where a
        /// character comes back to after the fight that gave it.
        /// </remarks>
        public static async Task OnMapEnteredAsync(NetworkStream stream, long mapId, int subAreaId)
        {
            var book = _book;
            var state = SessionContext.State;
            if (book == null || Log == null) return;

            var candidates = new List<int>();
            if (subAreaId > 0 && Tally(AchievementCatalogue.ExploreKey, subAreaId) == 0)
            {
                SetTally(AchievementCatalogue.ExploreKey, subAreaId, 1);
                candidates.AddRange(book.WaitingOn(AchievementCatalogue.ExploreKey, subAreaId));
                candidates.AddRange(book.WaitingOn("ES", subAreaId));
            }

            // Where he stands and what he holds: his guild's hall (HP) and his best Koliseo league
            // (lB, which a Koliseo fight moves before he is back on a map).
            candidates.AddRange(book.WaitingOn("HP"));
            candidates.AddRange(book.WaitingOn("lB"));

            if (state.AchievementLevelChecked != state.CharacterLevel)
            {
                state.AchievementLevelChecked = state.CharacterLevel;
                candidates.AddRange(book.WaitingOn("PL"));
            }

            candidates.AddRange(book.WaitingOn("Pm", mapId));
            candidates.AddRange(book.WaitingOn("PO"));

            // A map some achievement asks to have been on (BI): Incarnam's «A lo largo y a lo
            // ancho», 421 objectives. Only those maps are written down, once.
            var onTheMap = book.WaitingOn(VisitedKind, mapId);
            if (onTheMap.Count > 0 && Tally(VisitedKind, mapId) == 0)
            {
                SetTally(VisitedKind, mapId, 1);
                candidates.AddRange(onTheMap);
            }

            // And whatever the last fight set aside.
            candidates.AddRange(state.AchievementsPending);
            state.AchievementsPending.Clear();

            await CheckAsync(stream, candidates);
        }

        /// <summary>The character's level went up. The level achievements, looked at now.</summary>
        public static Task AfterLevelAsync(NetworkStream stream)
        {
            var book = _book;
            if (book == null) return Task.CompletedTask;
            SessionContext.State.AchievementLevelChecked = SessionContext.State.CharacterLevel;
            return CheckAsync(stream, book.WaitingOn("PL"));
        }

        /// <summary>Items crafted: the tally, and the achievements that count it or a job level.</summary>
        /// <remarks>
        /// Measured: the tutorial earns 120 "El prototipo", "Fabricar 1 objeto", right after its
        /// ring is crafted — after the job's level-up and the character sheet, which is where this
        /// is called from.
        /// </remarks>
        public static Task AfterCraftAsync(NetworkStream stream, int count)
        {
            var book = _book;
            if (book == null || count <= 0) return Task.CompletedTask;

            SetTally(AchievementCatalogue.CraftKey, 0, Tally(AchievementCatalogue.CraftKey, 0) + count);
            return CheckAsync(stream, book.WaitingOn(AchievementCatalogue.CraftKey)
                .Concat(book.WaitingOn(AchievementCatalogue.JobKey))
                .Concat(book.WaitingOn("EC")));
        }

        /// <summary>A job's level went up: the achievements that want jobs at a level.</summary>
        public static Task AfterJobLevelAsync(NetworkStream stream)
        {
            var book = _book;
            if (book == null) return Task.CompletedTask;
            return CheckAsync(stream, book.WaitingOn(AchievementCatalogue.JobKey));
        }

        /// <summary>
        /// A fight is over. The monsters beaten are counted — all of them for <c>EM</c>, and again
        /// for <c>Ef</c> when a challenge was won — and the achievements that count them are set
        /// aside, to be looked at when the character is back on the map.
        /// </summary>
        /// <remarks>
        /// Set aside rather than announced here, because here is the middle of the fight's end:
        /// the challenge verdicts and the results screen are still to go, and an mfu in between
        /// would be the one message of that sequence no capture has in it. The map the character
        /// returns to is where exploration is judged anyway (<see cref="OnMapEnteredAsync"/>).
        /// </remarks>
        /// <param name="monsters">Monster template to how many of it were beaten. Summons left out.</param>
        /// <param name="inDungeon">Whether the fight was in a dungeon room, for <c>EM&gt;id,0,d</c>.</param>
        /// <param name="challengesWon">The challenges validated in this fight.</param>
        public static void AfterFight(IReadOnlyDictionary<int, int> monsters, bool inDungeon,
                                      IReadOnlyCollection<int> challengesWon)
        {
            var book = _book;
            var state = SessionContext.State;
            if (book == null || Log == null) return;

            foreach (var (monster, count) in monsters)
            {
                if (monster <= 0 || count <= 0) continue;

                SetTally("EM", monster, Tally("EM", monster) + count);
                if (inDungeon) SetTally(DungeonKind, monster, Tally(DungeonKind, monster) + count);
                state.AchievementsPending.UnionWith(book.WaitingOn("EM", monster));

                if (challengesWon.Count > 0)
                {
                    SetTally("Ef", monster, Tally("Ef", monster) + count);
                    state.AchievementsPending.UnionWith(book.WaitingOn("Ef", monster));
                }
            }

            foreach (int challenge in challengesWon)
            {
                state.ChallengesDone.Add(challenge);
                state.AchievementsPending.UnionWith(book.WaitingOn("EH", challenge));
            }

            // And the challenges won, all told (EA, «Retador milenario») and in dungeons (Ea).
            if (challengesWon.Count > 0)
            {
                SetTally("EA", 0, Tally("EA", 0) + challengesWon.Count);
                state.AchievementsPending.UnionWith(book.WaitingOn("EA"));
                if (inDungeon)
                {
                    SetTally("Ea", 0, Tally("Ea", 0) + challengesWon.Count);
                    state.AchievementsPending.UnionWith(book.WaitingOn("Ea"));
                }
            }
        }

        /// <summary>
        /// The Infinite Dreams' tallies, «en intensidad X o superior»: kept per difficulty, a term's
        /// key the lowest one it takes (<c>ET&gt;4,999</c>, a Paradoja I or harder).
        /// </summary>
        private static readonly HashSet<string> FromIntensity = new() { "ET", "EY", "EW", "EZ", "Et", "Ez" };

        /// <summary>
        /// A dream's fight over, at its difficulty: the monsters and dungeon guardians it beat, and
        /// the dream fragments it paid -- counted too as «sin utilizar ninguna arena de Draconiros»
        /// and «sin comprar nada en la fuente» when the dream was so played. Looked at back on the map.
        /// </summary>
        public static void AfterDream(int difficulty, int monsters, int guardians, int fragments,
                                      bool spentSands, bool bought)
        {
            var book = _book;
            var state = SessionContext.State;
            if (book == null || Log == null || difficulty <= 0) return;

            void Add(string kind, int by)
            {
                if (by <= 0) return;
                SetTally(kind, difficulty, Tally(kind, difficulty) + by);
                for (int key = 1; key <= difficulty; key++) state.AchievementsPending.UnionWith(book.WaitingOn(kind, key));
            }

            Add("Et", monsters);
            Add("Ez", guardians);
            Add("ET", fragments);
            if (!spentSands) Add("EY", fragments);
            if (!bought) Add("EW", fragments);
            if (!spentSands && !bought) Add("EZ", fragments);
        }

        /// <summary>A dream tally at this intensity or a harder one.</summary>
        private static long AtIntensityOrHarder(string kind, long lowest)
        {
            long total = 0;
            for (int difficulty = (int)Math.Max(1, lowest); difficulty <= Dreams.MaximaDificultad; difficulty++)
                total += Tally(kind, difficulty);
            return total;
        }

        /// <summary>
        /// Something the achievements count happened -- the kind and key their criteria name:
        /// <c>EB</c> 0 a mount born, <c>HD</c> a ride caught (its item), <c>NC</c> a guild mission
        /// done (its super category), <c>NT</c> guild tokens obtained, <c>EI</c> an item used,
        /// <c>Eu</c> items broken into runes, <c>EJ</c> a dream trial won (its achievement) -- and
        /// the achievements waiting on it are looked at
        /// now; with no stream, back on the map, as a fight's are.
        /// </summary>
        public static async Task CountAsync(NetworkStream? stream, string kind, long key, long by = 1)
        {
            var book = _book;
            if (book == null || Log == null || by <= 0 || SessionContext.State.CharacterId == 0) return;

            SetTally(kind, key, Tally(kind, key) + by);
            var waiting = book.WaitingOn(kind, key).Concat(Totals.Contains(kind) ? book.WaitingOn(kind) : Array.Empty<int>()).ToList();
            if (stream == null)
            {
                SessionContext.State.AchievementsPending.UnionWith(waiting);
                return;
            }
            await CheckAsync(stream, waiting);
        }

        /// <summary>The kinds judged as one number (NT&gt;99), whatever key their term carries.</summary>
        private static readonly HashSet<string> Totals = new() { "EA", "Ea", "NT" };

        /// <summary>The best Koliseo league the character has reached this season, in any mode: lB.</summary>
        private static long BestLeague()
        {
            var state = SessionContext.State;
            int best = KoliseoLeagues.None;
            foreach (var standing in KoliseoLadder.AllOf(state.CharacterId, state.CharacterLevel))
                if (standing.BestLeague != KoliseoLeagues.None)
                    best = best == KoliseoLeagues.None ? standing.BestLeague : KoliseoLeagues.Higher(best, standing.BestLeague);
            return best;
        }

        /// <summary>Whether the character stands in his own guild's hall: HP=1,1, «Estás en tu casa».</summary>
        private static bool InOwnGuildHall()
        {
            var state = SessionContext.State;
            var guild = GuildStore.GuildOf(state.CharacterId);
            var hall = GuildHalls.OfRoom(state.MapId);
            return guild != null && hall != null && GuildHalls.ChoiceOf(guild.Id)?.HallId == hall.Id;
        }

        /// <summary>A map been on, by its id: <c>BI=85983744</c>.</summary>
        private const string VisitedKind = "BI";

        /// <summary>A year in minutes: how long every account has been subscribed, for OV.</summary>
        private const long SubscribedMinutes = 365L * 24 * 60;

        /// <summary>The tally kind for monsters beaten in their dungeon: EM with the "d" flag.</summary>
        private const string DungeonKind = "EMd";

        /// <summary>Looks at these achievements and announces the ones earned.</summary>
        private static async Task CheckAsync(NetworkStream stream, IEnumerable<int> candidates)
        {
            var log = Log;
            if (log == null) return;

            var earned = log.CheckAll(candidates.Distinct());
            await AnnounceAsync(stream, earned);
        }

        /// <summary>
        /// Tells the client achievements are earned (mfu, one each) and writes them down.
        /// </summary>
        /// <remarks>
        /// Only mfu. The captures never follow an mfu with an mfs: that comes after the claim, with
        /// the character sheet the reward changed. The level in mfu is the character's, 1 and 2 in
        /// the tutorial and 200 in the long-route capture.
        /// </remarks>
        private static async Task AnnounceAsync(NetworkStream stream, IReadOnlyList<int> earned)
        {
            var state = SessionContext.State;
            foreach (int achievementId in earned)
            {
                Log?.Credit(achievementId, state.CharacterId);
                Log?.NoteLevel(achievementId, LevelOnTheWire(state.CharacterLevel));
                DatabaseManager.SaveAchievement(state.CharacterId, achievementId, claimed: false,
                                                level: LevelOnTheWire(state.CharacterLevel));

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Mfu, AchievementProtocol.BuildEarned(
                        LevelOnTheWire(state.CharacterLevel), state.CharacterId, achievementId)));

                // And whoever stands by reads it in his chat. INFERRED: no capture has an mgc, and
                // the map is who the client's own line («ha desbloqueado el logro») suits.
                if (SessionContext.Current is { } me && !state.IsInFight)
                    await SessionRegistry.BroadcastToMapAsync(state.MapId, ConnectionProtocol.Push(Op.Mgc,
                        AchievementProtocol.BuildUnlockedBy(state.CharacterId, state.CharacterName,
                                                            LevelOnTheWire(state.CharacterLevel), achievementId)), me.Id);

                Console.WriteLine($"[Achievements] {state.CharacterName} earns {achievementId}.");
            }
        }

        // ─── The window ───────────────────────────────────────────────────────────

        /// <summary>
        /// The window has opened (mfe): the achievements closest to being earned (mgb).
        /// </summary>
        /// <remarks>
        /// The one capture of the window sends mfe, mfp and mff and gets mgb, mfx and mfo back in
        /// that order; each request is answered here with the answer in the same position. Which
        /// request the real server answers with which message is not in the capture beyond that.
        /// </remarks>
        public static Task OpenedAsync(NetworkStream stream)
        {
            var log = Log;
            if (log == null) return Task.CompletedTask;

            var list = log.AlmostFinished().Select(id => (id, (IReadOnlyList<ObjectiveProgress>)log.Progress(id)));
            return Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Mgb, AchievementProtocol.BuildAlmostFinished(list)));
        }

        /// <summary>
        /// The window's second request (mfp): answered with an empty mfx on root 3, which is
        /// exactly what the capture's real server sent. What would go in it is not known.
        /// </summary>
        public static Task SecondRequestAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Mfx, null, ConnectionProtocol.RequestId(frame)));

        /// <summary>One category of the window (mff): every achievement in it, with its progress (mfo).</summary>
        public static Task CategoryAsync(NetworkStream stream, int categoryId)
        {
            var log = Log;
            if (log == null || _book == null) return Task.CompletedTask;

            return Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Mfo, AchievementProtocol.BuildDetailedList(CategoryList(log, categoryId))));
        }

        /// <summary>
        /// What mfo lists for a category: all of its achievements, the ones still to earn first,
        /// each in the category's own order — the order the capture's answer for category 40 has.
        /// </summary>
        public static List<(int Achievement, IReadOnlyList<ObjectiveProgress> Objectives)> CategoryList(
            AchievementLog log, int categoryId)
        {
            var open = new List<(int, IReadOnlyList<ObjectiveProgress>)>();
            var done = new List<(int, IReadOnlyList<ObjectiveProgress>)>();
            foreach (int category in WithDescendants(log.Book, categoryId))
            {
                foreach (var achievement in log.Book.InCategory(category))
                {
                    var progress = log.Progress(achievement.Id);
                    if (progress.Count == 0) continue;
                    (log.Has(achievement.Id) ? done : open).Add((achievement.Id, progress));
                }
            }

            open.AddRange(done);
            return open;
        }

        /// <summary>
        /// A category and every category under it. The capture asks for a category with no
        /// children; a parent asked for is answered with what hangs from it rather than with
        /// nothing — INFERRED, the capture does not show one.
        /// </summary>
        private static List<int> WithDescendants(AchievementCatalogue book, int categoryId)
        {
            var all = new List<int> { categoryId };
            for (int i = 0; i < all.Count; i++)
            {
                foreach (var category in book.Categories)
                {
                    if (category.ParentId == all[i] && category.Id != all[i] && !all.Contains(category.Id))
                    {
                        all.Add(category.Id);
                    }
                }
            }

            return all;
        }

        /// <summary>One achievement (mfm), answered with its objectives (mfg). INFERRED.</summary>
        public static Task DetailsAsync(NetworkStream stream, int achievementId)
        {
            var log = Log;
            if (log == null || achievementId <= 0 || _book?.Of(achievementId) == null) return Task.CompletedTask;

            return Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Mfg, AchievementProtocol.BuildDetails(achievementId, log.Progress(achievementId))));
        }

        // ─── Paying ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The client is asking for the reward of an achievement it has earned (mga), or of all of
        /// them with -1.
        /// </summary>
        /// <remarks>
        /// Per achievement, in the order the captures show it: the kamas (ivf and the "gained"
        /// line), the level-up if there is one, the character sheet (kub) and the experience
        /// gained (kuf), the items, the emotes (khi), and last the mfs that says it is paid. The
        /// numbers come from <see cref="AchievementLog.Payout"/>, which is the client's own formula.
        ///
        /// Titles and ornaments are recorded in the log and nothing is sent for them: every
        /// character is offered all 539 titles and 167 ornaments already (see Titles), so there is
        /// nothing to add to what they have.
        /// </remarks>
        public static async Task ClaimAsync(NetworkStream stream, int achievementId)
        {
            var log = Log;
            if (log == null || _book == null) return;

            // Only what this character achieved: the client's «Recompensas» window lists the unpaid
            // achievements whose achiever (mft) is the character playing, and «Obtenerlo todo» (-1)
            // means those. Paying every unpaid one of the account handed a new character of an
            // account with others the rewards of a hundred achievements it was never shown.
            var state = SessionContext.State;
            bool Mine(int id) => log.AchieverOf(id) is var by && (by == 0 || by == state.CharacterId);

            var owed = new List<int>();
            if (achievementId > 0)
            {
                if (log.Has(achievementId) && !log.WasClaimed(achievementId) && Mine(achievementId)) owed.Add(achievementId);
            }
            else
            {
                owed.AddRange(log.Unclaimed().Where(Mine).OrderBy(i => i));
            }

            bool levelled = false;
            foreach (int id in owed)
            {
                // Judged before it is marked paid: "Ob!<itself>" is the commonest reward condition.
                // With the character's experience bonus, which the client adds to what its window
                // shows: «Primer tiempo» is 115 there and in the capture, 110 without it.
                // At the level it was earned at, as the window shows it: «Segundo tiempo», earned at
                // level 1 by leaving the tutorial, shows 115 there and not the 241 of level 2.
                int earnedAt = log.LevelOf(id);
                var payout = log.Payout(id, earnedAt > 0 ? earnedAt : state.CharacterLevel,
                                        CombatExperience.CharacterBonusPercent);
                if (!log.MarkClaimed(id)) continue;
                // Paid on the row of whoever achieved it: the account's achievement, paid once.
                long achiever = log.AchieverOf(id);
                DatabaseManager.SaveAchievement(achiever != 0 ? achiever : state.CharacterId, id, claimed: true);

                await CharacterRewards.GiveKamasAsync(stream, payout.Kamas);
                levelled |= await CharacterRewards.GiveExperienceAsync(stream, payout.Experience);

                foreach (var (item, count) in payout.Items)
                {
                    if (!await Equipment.GiveAsync(stream, item, Math.Max(1, count)))
                    {
                        Console.WriteLine($"[Achievements] Item {item} of achievement {id} could not be given.");
                    }
                }

                foreach (int emote in payout.Emotes) await Emotes.LearnAsync(stream, emote);

                if (payout.Experience > 0 || payout.Kamas > 0) CharacterRewards.Save();

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Mfs, AchievementProtocol.BuildRewarded(id)));

                Console.WriteLine($"[Achievements] {state.CharacterName} claims {id}: {payout.Experience} " +
                                  $"experience, {payout.Kamas} kamas, {payout.Items.Count} item(s), " +
                                  $"{payout.Emotes.Count} attitude(s)" +
                                  (payout.Titles.Count + payout.Ornaments.Count > 0
                                      ? $", {payout.Titles.Count} title(s) and {payout.Ornaments.Count} ornament(s) that are already offered to everybody."
                                      : "."));
            }

            if (levelled) await AfterLevelAsync(stream);
        }

        // ─── Tallies ──────────────────────────────────────────────────────────────

        /// <summary>One of this character's tallies.</summary>
        public static long Tally(string kind, long key)
            => SessionContext.State.AchievementTallies.TryGetValue((kind, key), out long n) ? n : 0;

        /// <summary>Sets one of this character's tallies and writes it down.</summary>
        public static void SetTally(string kind, long key, long value)
        {
            var state = SessionContext.State;
            state.AchievementTallies[(kind, key)] = value;
            if (state.CharacterId != 0) DatabaseManager.SaveAchievementCounter(state.CharacterId, kind, key, value);
        }

        /// <summary>
        /// The character, as the achievement engine asks about it: the quest log for quests, and
        /// the session for everything the achievements count.
        /// </summary>
        /// <remarks>
        /// Read through the session on every question rather than captured when built, like the
        /// quest log's level and map: the same connection can come back with another character.
        /// </remarks>
        internal sealed class CharacterFacts : IQuestFacts
        {
            private static SessionState S => SessionContext.State;

            public int Level => S.CharacterLevel;
            public long MapId => S.MapId;
            public bool Finished(int questId) => S.Quests?.Finished(questId) ?? false;
            public bool Active(int questId) => S.Quests?.Active(questId) ?? false;
            public bool ObjectiveDone(int objectiveId) => S.Quests?.ObjectiveDone(objectiveId) ?? false;

            public long? Scalar(string op) => op switch
            {
                "SC" => ServerGameType,

                // His class: «The Tymador Job» asks PG=13, the class quests and items theirs.
                "PG" => S.Breed,

                // His alignment (Alignments): the side, 0 neutral, 1 Bonta, 2 Brakmar; the level
                // its quests have raised; the rank its orders' apprenticeships have given.
                "Ps" => Alignments.CriterionSide(S.CharacterId),
                "Pa" => Alignments.Level(),
                "Pr" => Alignments.Rank(),

                // Every account is subscribed here: its end date is a year away (Subscription).
                "PZ" => 1,

                // The client's AlwaysTrueCriterion (mw.ood): «BT=1» on 45 old quests.
                "BT" => 1,

                // The client's SexCriterion, KamaCriterion and SubareaCriterion (mw.ood): «PS=0»
                // a male, «PK>999» the kamas carried, «PB=4|PB=22» the subarea stood in.
                "PS" => S.Sex,
                "PK" => S.Kamas,
                "PB" => DatabaseManager.SubAreaOfMap(S.MapId),

                // SubscriptionDurationCriterion, in minutes («OV>2879», two days): every account
                // is subscribed here for a year (Subscription).
                "OV" => SubscribedMinutes,
                "QQ" => S.Quests?.Runs.Values.Count(r => r.Finished) ?? 0,
                "EA" or "Ea" or "NT" => Tally(op, 0),

                // The hour on the server's clock, Paris's: «SH>19|SH<8», the night.
                "SH" => OsavoraSeasons.InParis(DateTimeOffset.UtcNow).Hour,
                "lB" => BestLeague(),
                AchievementCatalogue.CraftKey => Tally(AchievementCatalogue.CraftKey, 0),
                Almanax.DayOperator => Almanax.Scalar(op),
                _ => null,
            };

            public long? Count(string op, long key, string flag) => op switch
            {
                "PO" => Equipment.HowMany((int)key),

                // A world event going on (WorldEvents): «WE=230», Osavora's Repos.
                "WE" => WorldEvents.IsActive((int)key, DateTimeOffset.UtcNow) ? 1 : 0,

                // A content switch, on, or a yearly event while it runs (SeasonalEvents): «Sc=967», Halouine.
                "Sc" => SeasonalEvents.IsOn((int)key, Almanax.Clock()) ? 1 : 0,

                // The days since an item held was received (its 805): «DD>7,31582».
                "DD" => Equipment.DaysSinceReceived((int)key, DateTimeOffset.UtcNow),
                "DH" => Equipment.HoursSinceReceived((int)key, DateTimeOffset.UtcNow),
                "DM" => Equipment.MinutesSinceReceived((int)key, DateTimeOffset.UtcNow),

                // An alteration carried, the client's «HA=88».
                "HA" => Alterations.Has(S.CharacterId, (int)key) ? 1 : 0,
                "EM" => Tally(flag == "d" ? DungeonKind : "EM", key),
                "Ef" => Tally("Ef", key),
                "EH" => S.ChallengesDone.Contains((int)key) ? 1 : 0,
                "EB" or "HD" or "NC" or "EI" or "Eu" or "EJ" or VisitedKind => Tally(op, key),
                _ when FromIntensity.Contains(op) => AtIntensityOrHarder(op, key),
                "EC" => Tally(AchievementCatalogue.CraftKey, 0),
                "ES" => Tally(AchievementCatalogue.ExploreKey, key),
                "HP" => key == 1 && InOwnGuildHall() ? 1 : 0,

                // Finished at least once is all the quest log keeps: "QF>1300,4", five times,
                // is then never met, which is the safe way to be wrong.
                "QF" => Finished((int)key) ? 1 : 0,

                // A job's level: «PJ>26,79», the alchemist past 79; «Pj» the same in the
                // Vulkania quests' «Pj>26,99».
                "PJ" or "Pj" => S.JobLevel((int)key),

                // An emote known, the client's EmoteCriterion: «PE!8», the scroll of an emote not
                // learnt yet.
                "PE" => S.Emotes.Contains((int)key) ? 1 : 0,

                AchievementCatalogue.ExploreKey => Tally(AchievementCatalogue.ExploreKey, key),
                AchievementCatalogue.JobKey => S.Jobs.Values.Count(j => j.Level >= key),
                _ => null,
            };
        }
    }
}
