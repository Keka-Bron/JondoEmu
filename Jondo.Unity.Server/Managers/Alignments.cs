using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A character's alignment: neutral, Bontarian or Brakmarian, his grade and when it last changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the wire it is the client's alignment block (llp, read by giq.bkjt into its eih): f2 the
    /// side, f4 the grade, f5 the level, and in the sheet f3 { f3: 500 } besides. The sides are 2
    /// neutral, 3 and 4 the two cities: a brand new character's kub carries 2, and the client draws
    /// wings only for a side above 2 with a grade, picking them as «grade + (side - 3) · 10». Over
    /// the captures' players, 107 are side 3, 51 side 2 and 7 side 4, none with a grade: 3 is Bonta,
    /// the city most players swear to, and the order is the old game's (angel, then evil), each one
    /// up by two. The criterion «Ps» counts them the old way: 0 neutral, 1 Bonta, 2 Brakmar.
    /// </para>
    /// <para>
    /// Swearing is Chantal Inea's, in Incarnam: «No puedes dirigirte al Mundo de los Doce sin jurar
    /// lealtad a una ciudad o a otra.». Becoming neutral again is Morry Gann's, in Amakna's militia:
    /// «entre los 500 y los 256 000 kamas, según tu implicación con la milicia» -- ten steps, each
    /// double the last -- and never twice within «dos semanas» (AlignmentHandler). Swearing, or
    /// leaving, starts the honour again from nothing. The grade is earned in aggressions
    /// (<see cref="Managers.Honour"/>, AlignmentWar).
    /// </para>
    /// <para>
    /// The alignment's level and rank are the quests' (<see cref="Progression"/>).
    /// </para>
    /// <para>
    /// PvP is the sworn's own switch, the sheet's alignment tab's (hnu, <see cref="SetPvp"/>). The
    /// client reads it from the block's f3, the honour's: { f1 honour, f2 the aggression status,
    /// f3 the next grade's honour, f4 this grade's }, the status its lbc -- the old game's
    /// AggressableStatus, 1 «PvP enabled, aggressable» --, and draws the switch on for 1 or 2
    /// (AlignmentUi.DisplayPvpInformations). Every player of the captures has it off, f2 absent,
    /// and none has a grade: a grade only shows with PvP on, where honour 0 is the first grade.
    /// </para>
    /// <para>
    /// Kept in world.db, in a table of its own created at first use:
    /// <c>CharacterAlignments (CharacterId, Side, Grade, ChangedMs, Pvp, Honour, Dishonour)</c>.
    /// The grade is the honour's (<see cref="Managers.Honour"/>); its column keeps the last one.
    /// </para>
    /// </remarks>
    public static class Alignments
    {
        public const int Neutral = 2, Bonta = 3, Brakmar = 4;

        /// <summary>How long after a change no other is taken: «hace menos de dos semanas».</summary>
        public static readonly TimeSpan ChangeLock = TimeSpan.FromDays(14);

        /// <summary>What becoming neutral costs at the first of its ten steps; each one doubles it.</summary>
        public const long FirstLeaveCost = 500;
        public const int LeaveSteps = 10;

        public sealed record Alignment(int Side, int Grade, long ChangedMs, bool Pvp = false, int Honour = 0, int Dishonour = 0)
        {
            public bool Sworn => Side == Bonta || Side == Brakmar;

            /// <summary>Whether his PvP is on: the sworn's only.</summary>
            public bool PvpOn => Sworn && Pvp;

            /// <summary>His honour's grade, 1 to 10, the sworn's only.</summary>
            public int HonourGrade => Sworn ? Managers.Honour.GradeOf(Honour) : 0;

            /// <summary>The grade the wings show: none with PvP off, his honour's with it on.</summary>
            public int ShownGrade => PvpOn ? HonourGrade : 0;

            /// <summary>The honour block's aggression status: 1 with PvP on, else nothing.</summary>
            public int AggressionStatus => PvpOn ? PvpAggressable : 0;
        }

        /// <summary>The client's lbc 1, the old game's PvP_ENABLED_AGGRESSABLE.</summary>
        public const int PvpAggressable = 1;

        private static readonly Alignment Unsworn = new(Neutral, 0, 0);
        private static readonly ConcurrentDictionary<long, Alignment> _known = new();
        private static readonly object _tableLock = new();
        private static bool _tableReady;

        public static Alignment Of(long characterId)
            => characterId == 0 ? Unsworn : _known.GetOrAdd(characterId, Read);

        /// <summary>The side as the criterion «Ps» counts it: 0 neutral, 1 Bonta, 2 Brakmar.</summary>
        public static int CriterionSide(long characterId) => Of(characterId).Side - Neutral;

        /// <summary>
        /// Which of the ten steps becoming neutral costs him, from his alignment level: «Les frais de
        /// dossier varient en fonction de ta réputation et de tes exploits», from «Puisque tu es très
        /// jeune» at 500 kamas to «Tout Amakna a entendu parler de toi» at 256 000. INFERRED: ten
        /// levels a step, the hundred levels of the alignment quests over the ten fees.
        /// </summary>
        public static int LeaveStep(int level) => Math.Clamp(level / 10, 0, LeaveSteps - 1);

        public static long LeaveCost(int step) => FirstLeaveCost << Math.Clamp(step, 0, LeaveSteps - 1);

        /// <summary>Whether his last change is old enough for another.</summary>
        public static bool MayChange(Alignment alignment, long nowMs)
            => alignment.ChangedMs == 0 || nowMs - alignment.ChangedMs >= (long)ChangeLock.TotalMilliseconds;

        /// <summary>Sworn to a city, or back to neutral: kept, with when. PvP starts off.</summary>
        public static Alignment Set(long characterId, int side, long nowMs)
        {
            var alignment = new Alignment(side, 0, nowMs);
            _known[characterId] = alignment;
            Store(characterId, alignment);
            return alignment;
        }

        /// <summary>
        /// Whether one may attack another for their cities: the client's own rule for offering
        /// «Agredir» (PlayerMenuMaker.AllowAggression) -- the attacker with PvP on, the target sworn
        /// to the other city and showing a grade, which only PvP on gives, and no more than fifty
        /// levels above him, both counted up to 200.
        /// </summary>
        public static bool MayAggress(long attacker, int attackerLevel, long target, int targetLevel)
        {
            var mine = Of(attacker);
            var theirs = Of(target);
            return mine.PvpOn && theirs.Sworn && theirs.ShownGrade > 0 && mine.Side != theirs.Side
                   && Math.Min(attackerLevel, 200) + 50 >= Math.Min(targetLevel, 200);
        }

        /// <summary>
        /// Honour and dishonour won or lost, neither below nothing nor honour over its most; a
        /// neutral one has none. The grade is the new honour's.
        /// </summary>
        public static Alignment AddHonour(long characterId, int honour, int dishonour = 0)
        {
            var alignment = Of(characterId);
            if (!alignment.Sworn || (honour == 0 && dishonour == 0)) return alignment;
            int kept = Math.Clamp(alignment.Honour + honour, 0, Managers.Honour.Most);
            alignment = alignment with
            {
                Honour = kept,
                Dishonour = Math.Max(0, alignment.Dishonour + dishonour),
                Grade = Managers.Honour.GradeOf(kept),
            };
            _known[characterId] = alignment;
            Store(characterId, alignment);
            return alignment;
        }

        /// <summary>The map flag that allows aggressions there (the client's CapabilityAllowAggression).</summary>
        public const long AllowAggressionFlag = 2;

        /// <summary>
        /// Whether a map allows an aggression: its flag, which 8,296 of the 15,360 maps carry --
        /// none of Astrub, its fields or the cities' streets.
        /// </summary>
        public static bool MapAllowsAggression(long mapId)
            => ((MapManager.GetMapInfo(mapId)?.Flags ?? 0) & AllowAggressionFlag) != 0;

        /// <summary>PvP switched on or off; a neutral one has none to switch.</summary>
        public static Alignment SetPvp(long characterId, bool on)
        {
            var alignment = Of(characterId);
            if (!alignment.Sworn || alignment.Pvp == on) return alignment;
            alignment = alignment with { Pvp = on };
            _known[characterId] = alignment;
            Store(characterId, alignment);
            return alignment;
        }

        // ─── Level and rank ───────────────────────────────────────────────────

        /// <summary>The character in session's alignment level, «Pa».</summary>
        public static int Level() => Index().Level(CriterionSide(SessionContext.State.CharacterId), FinishedQuests());

        /// <summary>The character in session's rank, «Pr»: one of the client's AlignmentRanks.</summary>
        public static int Rank() => Index().Rank(CriterionSide(SessionContext.State.CharacterId), FinishedQuests());

        /// <summary>The alignment and order quests of a side he has finished: what leaving it forgets.</summary>
        public static List<int> QuestsOfSide(int criterionSide)
            => FinishedQuests().Where(q => Index().SideOf(q) == criterionSide).ToList();

        /// <summary>Whether finishing a quest moves the alignment level, the rank, or neither.</summary>
        public static (bool Level, bool Rank) Moves(int questId)
            => (Index().Levels.ContainsKey(questId), Index().Ranks.ContainsKey(questId));

        private static IEnumerable<int> FinishedQuests()
            => SessionContext.State.Quests?.Runs.Values.Where(r => r.Finished).Select(r => r.QuestId)
               ?? Enumerable.Empty<int>();

        private static volatile Progression? _progression;

        /// <summary>Read off the quest catalogue once it is there; empty before.</summary>
        internal static Progression Index()
        {
            if (_progression != null) return _progression;
            var book = Quests.Book;
            if (book == null || !book.Ready) return Progression.Empty;
            return _progression = Progression.From(book.All());
        }

        /// <summary>
        /// The alignment's progression, read off the quest catalogue's own conditions: which quest
        /// leaves the alignment level where, and which order quest gives which rank.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The level («Pa») is a chain of a hundred quests on each side, each asking for the level it
        /// raises: «Entraînement avec Tarche» asks Ps=1&amp;Pa=0, «La serveuse Dame Cloude»
        /// Ps=1&amp;Pa=1&amp;Qf=55, and so on to «L'exorciste» at Pa=99 -- «Les 100 quêtes d'alignement
        /// de Bonta» (Dofus pour les noobs, «Alignement Bontarien»). Finishing the one that asks Pa=N
        /// leaves the level at N + 1, which is what the next one asks. The sheet of a level 200
        /// Bontarian measures how far it goes: f1 = 100 in its kub's alignment block.
        /// </para>
        /// <para>
        /// The rank («Pr», the client's AlignmentRanks) is the orders' «Apprentissage» quests: each
        /// asks for the rank it leaves behind, and the next one in its order for the rank it gives.
        /// 433 «Disciple de Ménalt» asks Ps=1&amp;Pr=1 and 109 «Écuyer» Pr=2&amp;Qf=433, so 433 gives
        /// rank 2, «Disciple de Menalt». The last of each order has none after it and gives the rank
        /// after the one it asks, as every order's ranks follow one another: 1916 «Héros Légendaire»
        /// asks Pr=5 and gives 6, the client's «Héros Légendaire». A sworn character with no order is
        /// the Néophyte his side's first apprenticeships ask for: 1 for Bonta, 18 for Brâkmar. The
        /// client is told the rank in hod f1, which the capture sends on entering the world: 17,
        /// «Maître des Illusions», for its level 200 Bontarian, and nothing for a new character
        /// (fhc sets it into the alignment service, which the sheet's alignment tab reads).
        /// </para>
        /// <para>
        /// The side is the criterion's: 1 Bonta, 2 Brâkmar. An apprenticeship that does not say it is
        /// on the side of the one it follows.
        /// </para>
        /// </remarks>
        internal sealed class Progression
        {
            public static readonly Progression Empty = new();

            /// <summary>Quest → its side and the level it leaves.</summary>
            public Dictionary<int, (int Side, int Level)> Levels { get; } = new();

            /// <summary>Quest → its side and the rank it gives.</summary>
            public Dictionary<int, (int Side, int Rank)> Ranks { get; } = new();

            /// <summary>Side → the rank of the sworn who has no order.</summary>
            public Dictionary<int, int> Novices { get; } = new();

            public static Progression From(IEnumerable<Quest> quests)
            {
                var progression = new Progression();
                var apprenticeships = new Dictionary<int, (int Asks, int Side, List<int> After)>();
                foreach (var quest in quests)
                {
                    var terms = QuestCriterion.Terms(quest.Criterion);
                    int? side = Single(terms, "Ps"), level = Single(terms, "Pa"), rank = Single(terms, "Pr");
                    if (side is int s && level is int l) progression.Levels[quest.Id] = (s, l + 1);
                    if (rank is not int r) continue;

                    // «Qf=433» or «QF>120,0»: the apprenticeship this one follows.
                    var after = terms.Where(t => t.Op is "Qf" or "QF" && t.Comparison is '=' or '>' && t.Values.Count > 0)
                                     .Select(t => int.TryParse(t.Values[0], out int q) ? q : 0)
                                     .Where(q => q > 0).ToList();
                    apprenticeships[quest.Id] = (r, side ?? 0, after);
                    if (side is int sworn) progression.Novices[sworn] = r;
                }

                foreach (var (quest, apprenticeship) in apprenticeships)
                {
                    int gives = apprenticeships.Values.Where(a => a.After.Contains(quest))
                                               .Select(a => a.Asks).DefaultIfEmpty(apprenticeship.Asks + 1).Min();
                    int side = SideOf(quest, apprenticeships);
                    if (side != 0) progression.Ranks[quest] = (side, gives);
                }
                return progression;
            }

            private static int SideOf(int quest, Dictionary<int, (int Asks, int Side, List<int> After)> apprenticeships)
            {
                for (int depth = 0; depth < 16 && apprenticeships.TryGetValue(quest, out var a); depth++)
                {
                    if (a.Side != 0) return a.Side;
                    quest = a.After.FirstOrDefault(apprenticeships.ContainsKey);
                }
                return 0;
            }

            /// <summary>The value of an operator written once and with «=»; null otherwise.</summary>
            private static int? Single(List<CriterionTerm> terms, string op)
            {
                var found = terms.Where(t => t.Op == op).ToList();
                return found.Count == 1 && found[0].Comparison == '=' && found[0].Values.Count > 0
                       && int.TryParse(found[0].Values[0], out int value) ? value : null;
            }

            /// <summary>The level his side's finished quests leave him at: 0 before the first.</summary>
            public int Level(int side, IEnumerable<int> finished)
            {
                int level = 0;
                foreach (int quest in finished)
                    if (Levels.TryGetValue(quest, out var l) && l.Side == side && l.Level > level) level = l.Level;
                return level;
            }

            /// <summary>His rank: his order's last apprenticeship's, else his side's Néophyte; 0 neutral.</summary>
            public int Rank(int side, IEnumerable<int> finished)
            {
                if (!Novices.TryGetValue(side, out int rank)) return 0;
                foreach (int quest in finished)
                    if (Ranks.TryGetValue(quest, out var r) && r.Side == side && r.Rank > rank) rank = r.Rank;
                return rank;
            }

            /// <summary>The side an alignment or order quest belongs to; 0 for any other quest.</summary>
            public int SideOf(int quest)
                => Levels.TryGetValue(quest, out var l) ? l.Side : Ranks.TryGetValue(quest, out var r) ? r.Side : 0;
        }

        /// <summary>For the tests: forget what was read.</summary>
        internal static void Forget(long characterId) => _known.TryRemove(characterId, out _);

        private static void EnsureTable(SqliteConnection connection)
        {
            if (_tableReady) return;
            lock (_tableLock)
            {
                if (_tableReady) return;
                var command = connection.CreateCommand();
                command.CommandText = @"CREATE TABLE IF NOT EXISTS CharacterAlignments (
                        CharacterId INTEGER PRIMARY KEY,
                        Side        INTEGER NOT NULL,
                        Grade       INTEGER NOT NULL DEFAULT 0,
                        ChangedMs   INTEGER NOT NULL DEFAULT 0,
                        Pvp         INTEGER NOT NULL DEFAULT 0);";
                command.ExecuteNonQuery();

                // A table made before PvP, or before honour, gets its columns.
                foreach (string column in new[] { "Pvp", "Honour", "Dishonour" })
                {
                    command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('CharacterAlignments') WHERE name = '{column}';";
                    if (Convert.ToInt64(command.ExecuteScalar()) != 0) continue;
                    command.CommandText = $"ALTER TABLE CharacterAlignments ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0;";
                    command.ExecuteNonQuery();
                }
                _tableReady = true;
            }
        }

        private static Alignment Read(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                EnsureTable(connection);
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Side, Grade, ChangedMs, Pvp, Honour, Dishonour FROM CharacterAlignments WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new Alignment(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3) != 0,
                                    reader.GetInt32(4), reader.GetInt32(5))
                    : Unsworn;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Alignment] Could not read {characterId}'s alignment: {ex.Message}");
                return Unsworn;
            }
        }

        private static void Store(long characterId, Alignment alignment)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                EnsureTable(connection);
                var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO CharacterAlignments (CharacterId, Side, Grade, ChangedMs, Pvp, Honour, Dishonour) " +
                                      "VALUES ($id, $side, $grade, $when, $pvp, $honour, $dishonour) ON CONFLICT(CharacterId) DO UPDATE SET " +
                                      "Side = excluded.Side, Grade = excluded.Grade, ChangedMs = excluded.ChangedMs, Pvp = excluded.Pvp, " +
                                      "Honour = excluded.Honour, Dishonour = excluded.Dishonour;";
                command.Parameters.AddWithValue("$honour", alignment.Honour);
                command.Parameters.AddWithValue("$dishonour", alignment.Dishonour);
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$side", alignment.Side);
                command.Parameters.AddWithValue("$grade", alignment.Grade);
                command.Parameters.AddWithValue("$when", alignment.ChangedMs);
                command.Parameters.AddWithValue("$pvp", alignment.Pvp ? 1 : 0);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Alignment] Could not keep {characterId}'s alignment: {ex.Message}");
            }
        }
    }
}
