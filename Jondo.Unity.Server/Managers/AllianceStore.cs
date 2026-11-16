using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The alliances and who is in each one, stored in world.db.
    /// </summary>
    /// <remarks>
    /// Since the alliance rework an alliance is made of CHARACTERS, not of guilds: in the capture
    /// "entrar en alianza y salir de alianza" a character of the guild «Juicio Final» joins «A B S
    /// I N T O» by himself (mac with the alliance's uuid) and leaves it by expelling himself (mcc
    /// with his own id), while his guild stays as it was. Each one has a rank of the alliance's
    /// own, four of them at founding (<see cref="AllianceData.Ranks"/>).
    ///
    /// An alliance is known to the client by its uuid, not by a number: the uuid goes in every
    /// alliance block and is what the join request names.
    /// </remarks>
    public static class AllianceStore
    {
        /// <summary>An alliance: what identifies it and what the client draws of it.</summary>
        public sealed class Alliance
        {
            public long Id { get; init; }
            public string Uuid { get; init; } = "";
            public string Name { get; init; } = "";
            public string Tag { get; init; } = "";

            /// <summary>
            /// The emblem, the five numbers of the creation's lld: the symbol, the symbol's colour
            /// index, the background colour (RGB), the background shape and the symbol's colour (RGB).
            /// «E X A M P L E» is {408, 27, 3116544, 0, 15859463}.
            /// </summary>
            public int EmblemSymbol { get; init; }
            public int EmblemSymbolColor { get; init; }
            public int EmblemBackground { get; init; }
            public int EmblemBackgroundShape { get; init; }
            public int EmblemSymbolRgb { get; init; }

            /// <summary>When it was founded, in Unix seconds, as the card's f2 carries it.</summary>
            public long CreatedUtcS { get; init; }
        }

        /// <summary>A member: his character, his rank and when he joined.</summary>
        public sealed class Member
        {
            public long CharacterId { get; init; }
            public long AllianceId { get; init; }
            public int Rank { get; set; } = RankNewcomer;
            public long JoinedUtcMs { get; init; }
        }

        /// <summary>An alliance rank, as it travels in the mbm: name, rights, icon and order.</summary>
        public sealed class Rank
        {
            public long AllianceId { get; init; }
            public int Id { get; init; }
            public string Name { get; set; } = "";
            public List<int> Rights { get; set; } = new();
            public bool Flag { get; set; }
            public int Icon { get; set; }
            public int Order { get; set; }
        }

        /// <summary>
        /// The alliance's recruitment card (lmn), the one the directory shows: its description, its
        /// title, the minimum level, whether anyone may join (1) or must apply, its tags and
        /// languages. Measured on the 40 alliances of the capture's directory.
        /// </summary>
        public sealed class Profile
        {
            public long AllianceId { get; init; }
            public long WhenMs { get; set; }
            public string Description { get; set; } = "";
            public string Title { get; set; } = "";
            public int MinLevel { get; set; } = MinimumLevel;
            public int Recruitment { get; set; } = RecruitmentOpen;
            public List<int> Tags { get; set; } = new();
            public List<int> Languages { get; set; } = new();
        }

        /// <summary>The leader's rank, «General de alianza».</summary>
        public const int RankLeader = 1;

        /// <summary>The newcomers' rank, «Bisoño»: what «E X A M P L E» gave the character who joined.</summary>
        public const int RankNewcomer = 4;

        /// <summary>
        /// The level an alliance asks for: «Solo los personajes de nivel 50 o superior podrán unirse a
        /// una alianza» (info 1/698), and the temple's door says the same of founding one (1/393).
        /// </summary>
        public const int MinimumLevel = 50;

        /// <summary>Recruitment through the directory without applying: «E X A M P L E»'s, joined by a mac.</summary>
        public const int RecruitmentOpen = 1;

        public static void EnsureTables(SqliteConnection world)
        {
            var create = world.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS Alliances (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Uuid TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Tag TEXT NOT NULL,
                    EmblemSymbol INTEGER NOT NULL DEFAULT 0,
                    EmblemSymbolColor INTEGER NOT NULL DEFAULT 0,
                    EmblemBackground INTEGER NOT NULL DEFAULT 0,
                    EmblemBackgroundShape INTEGER NOT NULL DEFAULT 0,
                    EmblemSymbolRgb INTEGER NOT NULL DEFAULT 0,
                    CreatedUtcS INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IdxAlliancesUuid ON Alliances (Uuid);
                CREATE TABLE IF NOT EXISTS AllianceMembers (
                    CharacterId INTEGER PRIMARY KEY,
                    AllianceId INTEGER NOT NULL,
                    Rank INTEGER NOT NULL DEFAULT 4,
                    JoinedUtcMs INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS IdxAllianceMembersAlliance ON AllianceMembers (AllianceId);
                CREATE TABLE IF NOT EXISTS AllianceRanks (
                    AllianceId INTEGER NOT NULL,
                    RankId INTEGER NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    Rights TEXT NOT NULL DEFAULT '',
                    Flag INTEGER NOT NULL DEFAULT 0,
                    Icon INTEGER NOT NULL DEFAULT 0,
                    Ord INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (AllianceId, RankId)
                );
                CREATE TABLE IF NOT EXISTS AllianceProfiles (
                    AllianceId INTEGER PRIMARY KEY,
                    WhenMs INTEGER NOT NULL DEFAULT 0,
                    Description TEXT NOT NULL DEFAULT '',
                    Title TEXT NOT NULL DEFAULT '',
                    MinLevel INTEGER NOT NULL DEFAULT 50,
                    Recruitment INTEGER NOT NULL DEFAULT 1,
                    Tags TEXT NOT NULL DEFAULT '',
                    Languages TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS AllianceNotices (
                    AllianceId INTEGER NOT NULL,
                    Kind INTEGER NOT NULL,
                    Text TEXT NOT NULL DEFAULT '',
                    WhenS INTEGER NOT NULL DEFAULT 0,
                    AuthorName TEXT NOT NULL DEFAULT '',
                    AuthorId INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (AllianceId, Kind)
                );
                CREATE TABLE IF NOT EXISTS AllianceApplications (
                    CharacterId INTEGER PRIMARY KEY,
                    AllianceId INTEGER NOT NULL,
                    Message TEXT NOT NULL DEFAULT '',
                    WhenMs INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS IdxAllianceApplicationsAlliance ON AllianceApplications (AllianceId);
                CREATE TABLE IF NOT EXISTS AllianceConnectionWarnings (
                    CharacterId INTEGER PRIMARY KEY
                );
                CREATE TABLE IF NOT EXISTS AllianceAvaFighters (
                    CharacterId INTEGER PRIMARY KEY
                );";
            create.ExecuteNonQuery();
        }

        /// <summary>A connection string a test can point at a temp database; null uses world.db.</summary>
        internal static string ConnectionStringOverride { get; set; }

        internal static SqliteConnection Open()
        {
            var conexion = new SqliteConnection(ConnectionStringOverride ?? DatabaseManager.WorldConnectionString);
            conexion.Open();
            EnsureTables(conexion);
            return conexion;
        }

        /// <summary>
        /// Founds the alliance with its first member -- the founder -- as its leader, its four ranks
        /// and an open recruitment card, and returns it.
        /// </summary>
        public static Alliance Create(long founderCharacterId, string name, string tag, int symbol, int symbolColor,
                                      int background, int backgroundShape, int symbolRgb)
        {
            using var conexion = Open();
            var now = DateTimeOffset.UtcNow;
            var alliance = new Alliance
            {
                Uuid = Guid.NewGuid().ToString(), Name = name, Tag = tag,
                EmblemSymbol = symbol, EmblemSymbolColor = symbolColor, EmblemBackground = background,
                EmblemBackgroundShape = backgroundShape, EmblemSymbolRgb = symbolRgb,
                CreatedUtcS = now.ToUnixTimeSeconds(),
            };

            var insert = conexion.CreateCommand();
            insert.CommandText = @"
                INSERT INTO Alliances (Uuid, Name, Tag, EmblemSymbol, EmblemSymbolColor, EmblemBackground,
                                       EmblemBackgroundShape, EmblemSymbolRgb, CreatedUtcS)
                VALUES ($uuid, $name, $tag, $sym, $symc, $bg, $shape, $symrgb, $created);
                SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$uuid", alliance.Uuid);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$tag", tag);
            insert.Parameters.AddWithValue("$sym", symbol);
            insert.Parameters.AddWithValue("$symc", symbolColor);
            insert.Parameters.AddWithValue("$bg", background);
            insert.Parameters.AddWithValue("$shape", backgroundShape);
            insert.Parameters.AddWithValue("$symrgb", symbolRgb);
            insert.Parameters.AddWithValue("$created", alliance.CreatedUtcS);
            long id = (long)insert.ExecuteScalar();

            foreach (var rank in DefaultRanks(id)) SaveRank(conexion, rank);
            SaveProfile(conexion, new Profile { AllianceId = id, WhenMs = now.ToUnixTimeMilliseconds() });
            AddMember(conexion, founderCharacterId, id, RankLeader, now.ToUnixTimeMilliseconds());

            return new Alliance
            {
                Id = id, Uuid = alliance.Uuid, Name = name, Tag = tag,
                EmblemSymbol = symbol, EmblemSymbolColor = symbolColor, EmblemBackground = background,
                EmblemBackgroundShape = backgroundShape, EmblemSymbolRgb = symbolRgb,
                CreatedUtcS = alliance.CreatedUtcS,
            };
        }

        private const string Columns = @"a.Id, a.Uuid, a.Name, a.Tag, a.EmblemSymbol, a.EmblemSymbolColor,
                                         a.EmblemBackground, a.EmblemBackgroundShape, a.EmblemSymbolRgb, a.CreatedUtcS";

        private static Alliance Read(SqliteDataReader lector) => new Alliance
        {
            Id = lector.GetInt64(0),
            Uuid = lector.GetString(1),
            Name = lector.GetString(2),
            Tag = lector.GetString(3),
            EmblemSymbol = lector.GetInt32(4),
            EmblemSymbolColor = lector.GetInt32(5),
            EmblemBackground = lector.GetInt32(6),
            EmblemBackgroundShape = lector.GetInt32(7),
            EmblemSymbolRgb = lector.GetInt32(8),
            CreatedUtcS = lector.GetInt64(9),
        };

        private static Alliance One(string where, string parameter, object value)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = $"SELECT {Columns} FROM Alliances a {where} LIMIT 1;";
            query.Parameters.AddWithValue(parameter, value);
            using var lector = query.ExecuteReader();
            return lector.Read() ? Read(lector) : null;
        }

        /// <summary>A character's alliance, or null if he has none.</summary>
        public static Alliance AllianceOf(long characterId)
            => One("JOIN AllianceMembers m ON m.AllianceId = a.Id WHERE m.CharacterId = $c", "$c", characterId);

        public static Alliance ById(long allianceId) => One("WHERE a.Id = $id", "$id", allianceId);

        public static Alliance ByUuid(string uuid) => One("WHERE a.Uuid = $u", "$u", uuid ?? "");

        /// <summary>An alliance by its name, whatever the case: two names that differ only in it clash.</summary>
        public static Alliance ByName(string name) => One("WHERE a.Name = $n COLLATE NOCASE", "$n", name ?? "");

        /// <summary>An alliance by its tag, whatever the case.</summary>
        public static Alliance ByTag(string tag) => One("WHERE a.Tag = $t COLLATE NOCASE", "$t", tag ?? "");

        /// <summary>Every alliance, oldest first.</summary>
        public static List<Alliance> All()
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = $"SELECT {Columns} FROM Alliances a ORDER BY a.Id;";
            using var lector = query.ExecuteReader();
            var list = new List<Alliance>();
            while (lector.Read()) list.Add(Read(lector));
            return list;
        }

        /// <summary>The alliance's members, the leader first and then by rank and seniority.</summary>
        public static List<Member> Members(long allianceId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT CharacterId, AllianceId, Rank, JoinedUtcMs FROM AllianceMembers
                WHERE AllianceId = $a ORDER BY Rank, JoinedUtcMs, CharacterId;";
            query.Parameters.AddWithValue("$a", allianceId);
            using var lector = query.ExecuteReader();
            var list = new List<Member>();
            while (lector.Read())
            {
                list.Add(new Member
                {
                    CharacterId = lector.GetInt64(0), AllianceId = lector.GetInt64(1),
                    Rank = lector.GetInt32(2), JoinedUtcMs = lector.GetInt64(3),
                });
            }
            return list;
        }

        public static Member MemberOf(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT CharacterId, AllianceId, Rank, JoinedUtcMs FROM AllianceMembers WHERE CharacterId = $c;";
            query.Parameters.AddWithValue("$c", characterId);
            using var lector = query.ExecuteReader();
            if (!lector.Read()) return null;
            return new Member
            {
                CharacterId = lector.GetInt64(0), AllianceId = lector.GetInt64(1),
                Rank = lector.GetInt32(2), JoinedUtcMs = lector.GetInt64(3),
            };
        }

        /// <summary>A member's rank, or 0 for one without an alliance.</summary>
        public static int RankOf(long characterId) => MemberOf(characterId)?.Rank ?? 0;

        /// <summary>Whether the member's rank holds a right. The leader holds every one.</summary>
        public static bool HasRight(long characterId, int right)
        {
            var member = MemberOf(characterId);
            if (member == null) return false;
            if (member.Rank == RankLeader) return true;
            var rank = Ranks(member.AllianceId).FirstOrDefault(r => r.Id == member.Rank);
            return rank != null && rank.Rights.Contains(right);
        }

        /// <summary>
        /// The most members an alliance holds: the directory shows each one's count out of it
        /// («3/250»), and the client has «Tu alianza ya ha alcanzado el número máximo de miembros y
        /// no permite nuevas incorporaciones» (information 670) for whoever would go past it.
        /// </summary>
        public const int MaxMembers = 250;

        /// <summary>Whether the alliance has as many members as it may hold.</summary>
        public static bool IsFull(long allianceId) => Members(allianceId).Count >= MaxMembers;

        /// <summary>Joins a character to an alliance at a rank, the newcomers' unless told otherwise.</summary>
        public static Member Join(long characterId, long allianceId, int rank = RankNewcomer)
        {
            using var conexion = Open();
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            AddMember(conexion, characterId, allianceId, rank, ms);
            return new Member { CharacterId = characterId, AllianceId = allianceId, Rank = rank, JoinedUtcMs = ms };
        }

        private static void AddMember(SqliteConnection conexion, long characterId, long allianceId, int rank, long ms)
        {
            var insert = conexion.CreateCommand();
            insert.CommandText = @"
                INSERT OR REPLACE INTO AllianceMembers (CharacterId, AllianceId, Rank, JoinedUtcMs)
                VALUES ($c, $a, $r, $ms);";
            insert.Parameters.AddWithValue("$c", characterId);
            insert.Parameters.AddWithValue("$a", allianceId);
            insert.Parameters.AddWithValue("$r", rank);
            insert.Parameters.AddWithValue("$ms", ms);
            insert.ExecuteNonQuery();
        }

        public static void SetRank(long characterId, int rank)
        {
            using var conexion = Open();
            var update = conexion.CreateCommand();
            update.CommandText = "UPDATE AllianceMembers SET Rank = $r WHERE CharacterId = $c;";
            update.Parameters.AddWithValue("$r", rank);
            update.Parameters.AddWithValue("$c", characterId);
            update.ExecuteNonQuery();
        }

        /// <summary>The outcome of somebody leaving, for whoever has to tell the others.</summary>
        public sealed record Departure(Alliance Alliance, long NewLeader, bool Dissolved);

        /// <summary>
        /// Takes a character out of his alliance. A leader who leaves hands the alliance to the
        /// member next in line -- the best rank, then the longest there --, and the last one to
        /// leave takes the alliance with him. Returns null if he had none.
        /// </summary>
        public static Departure Leave(long characterId)
        {
            var alliance = AllianceOf(characterId);
            if (alliance == null) return null;
            int rank = RankOf(characterId);

            using var conexion = Open();
            var delete = conexion.CreateCommand();
            delete.CommandText = "DELETE FROM AllianceMembers WHERE CharacterId = $c;";
            delete.Parameters.AddWithValue("$c", characterId);
            delete.ExecuteNonQuery();

            var left = Members(alliance.Id);
            if (left.Count == 0)
            {
                Dissolve(conexion, alliance.Id);
                return new Departure(alliance, 0, true);
            }

            long newLeader = 0;
            if (rank == RankLeader && left.All(m => m.Rank != RankLeader))
            {
                newLeader = NextInLine(alliance.Id, left);
                SetRank(newLeader, RankLeader);
            }
            return new Departure(alliance, newLeader, false);
        }

        /// <summary>
        /// Who takes over from a leader: the member of the best rank -- the first in the ranks'
        /// order, not the lowest rank id: a rank created later has a higher id wherever it is put
        /// --, then the longest there.
        /// </summary>
        internal static long NextInLine(long allianceId, IReadOnlyList<Member> members)
        {
            var order = Ranks(allianceId).ToDictionary(r => r.Id, r => r.Order);
            return members.OrderBy(m => order.TryGetValue(m.Rank, out int o) ? o : int.MaxValue)
                          .ThenBy(m => m.JoinedUtcMs)
                          .ThenBy(m => m.CharacterId)
                          .First().CharacterId;
        }

        private static void Dissolve(SqliteConnection conexion, long allianceId)
        {
            var delete = conexion.CreateCommand();
            delete.CommandText = @"
                DELETE FROM AllianceRanks WHERE AllianceId = $a;
                DELETE FROM AllianceProfiles WHERE AllianceId = $a;
                DELETE FROM AllianceNotices WHERE AllianceId = $a;
                DELETE FROM AllianceApplications WHERE AllianceId = $a;
                DELETE FROM Alliances WHERE Id = $a;";
            delete.Parameters.AddWithValue("$a", allianceId);
            delete.ExecuteNonQuery();
        }

        /// <summary>
        /// The four ranks an alliance is born with, from the client's data: «General de alianza»
        /// with every right, then «Teniente», «Aspirante» and «Bisoño» with none, icons 116, 115,
        /// 114 and 117.
        /// </summary>
        /// <remarks>
        /// The names go as keys, «alliance.rank.N.name», as a guild's do: the client's rank handler
        /// (fgv's mbm) replaces any name containing «alliance.rank.» -- one of its encrypted
        /// strings, read in Ghidra -- with the data's name for that rank id, in the player's own
        /// language. The rights of the three lower ranks are not measured -- the only capture
        /// shows an alliance that had already set its own -- so they start empty, and the leader
        /// gives them out.
        /// </remarks>
        public static List<Rank> DefaultRanks(long allianceId)
        {
            var everyRight = AllianceData.Rights.Select(r => r.Id).OrderBy(id => id).ToList();
            return AllianceData.Ranks.Select(template => new Rank
            {
                AllianceId = allianceId,
                Id = template.Id,
                Name = $"{RankKeyPrefix}{template.Id}.name",
                Rights = template.Id == RankLeader ? new List<int>(everyRight) : new List<int>(),
                Flag = template.Modifiable,
                Icon = template.Icon,
                Order = template.Order,
            }).ToList();
        }

        /// <summary>An alliance's ranks, in the order they are shown. The usual ones if it has none stored.</summary>
        public static List<Rank> Ranks(long allianceId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT RankId, Name, Rights, Flag, Icon, Ord FROM AllianceRanks WHERE AllianceId = $a ORDER BY Ord, RankId;";
            query.Parameters.AddWithValue("$a", allianceId);
            using var lector = query.ExecuteReader();
            var list = new List<Rank>();
            while (lector.Read())
            {
                list.Add(new Rank
                {
                    AllianceId = allianceId,
                    Id = lector.GetInt32(0),
                    Name = lector.GetString(1),
                    Rights = ParseIds(lector.GetString(2)),
                    Flag = lector.GetInt64(3) != 0,
                    Icon = lector.GetInt32(4),
                    Order = lector.GetInt32(5),
                });
            }
            return list.Count > 0 ? list : DefaultRanks(allianceId);
        }

        private static void SaveRank(SqliteConnection conexion, Rank rank)
        {
            var save = conexion.CreateCommand();
            save.CommandText = @"
                INSERT OR REPLACE INTO AllianceRanks (AllianceId, RankId, Name, Rights, Flag, Icon, Ord)
                VALUES ($a, $r, $n, $rights, $f, $i, $o);";
            save.Parameters.AddWithValue("$a", rank.AllianceId);
            save.Parameters.AddWithValue("$r", rank.Id);
            save.Parameters.AddWithValue("$n", rank.Name ?? "");
            save.Parameters.AddWithValue("$rights", JoinIds(rank.Rights));
            save.Parameters.AddWithValue("$f", rank.Flag ? 1 : 0);
            save.Parameters.AddWithValue("$i", rank.Icon);
            save.Parameters.AddWithValue("$o", rank.Order);
            save.ExecuteNonQuery();
        }

        /// <summary>The alliance's recruitment card, or a fresh open one if it has none stored.</summary>
        public static Profile ProfileOf(long allianceId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT WhenMs, Description, Title, MinLevel, Recruitment, Tags, Languages
                FROM AllianceProfiles WHERE AllianceId = $a;";
            query.Parameters.AddWithValue("$a", allianceId);
            using var lector = query.ExecuteReader();
            if (!lector.Read()) return new Profile { AllianceId = allianceId };
            return new Profile
            {
                AllianceId = allianceId,
                WhenMs = lector.GetInt64(0),
                Description = lector.GetString(1),
                Title = lector.GetString(2),
                MinLevel = lector.GetInt32(3),
                Recruitment = lector.GetInt32(4),
                Tags = ParseIds(lector.GetString(5)),
                Languages = ParseIds(lector.GetString(6)),
            };
        }

        public static void SaveProfile(Profile profile)
        {
            using var conexion = Open();
            SaveProfile(conexion, profile);
        }

        private static void SaveProfile(SqliteConnection conexion, Profile profile)
        {
            var save = conexion.CreateCommand();
            save.CommandText = @"
                INSERT OR REPLACE INTO AllianceProfiles (AllianceId, WhenMs, Description, Title, MinLevel,
                                                         Recruitment, Tags, Languages)
                VALUES ($a, $w, $d, $t, $min, $r, $tags, $lang);";
            save.Parameters.AddWithValue("$a", profile.AllianceId);
            save.Parameters.AddWithValue("$w", profile.WhenMs);
            save.Parameters.AddWithValue("$d", profile.Description ?? "");
            save.Parameters.AddWithValue("$t", profile.Title ?? "");
            save.Parameters.AddWithValue("$min", profile.MinLevel);
            save.Parameters.AddWithValue("$r", profile.Recruitment);
            save.Parameters.AddWithValue("$tags", JoinIds(profile.Tags));
            save.Parameters.AddWithValue("$lang", JoinIds(profile.Languages));
            save.ExecuteNonQuery();
        }

        /// <summary>The alliance's leader, or null.</summary>
        public static Member LeaderOf(long allianceId)
            => Members(allianceId).FirstOrDefault(m => m.Rank == RankLeader);

        // ─── Name and emblem changed with a potion ───────────────────────────────

        /// <summary>A new name and tag for the alliance; returns it as it is now.</summary>
        public static Alliance Rename(long allianceId, string name, string tag)
        {
            using (var conexion = Open())
            {
                var update = conexion.CreateCommand();
                update.CommandText = "UPDATE Alliances SET Name = $n, Tag = $t WHERE Id = $a;";
                update.Parameters.AddWithValue("$n", name);
                update.Parameters.AddWithValue("$t", tag);
                update.Parameters.AddWithValue("$a", allianceId);
                update.ExecuteNonQuery();
            }
            return ById(allianceId);
        }

        /// <summary>A new emblem for the alliance; returns it as it is now.</summary>
        public static Alliance SetEmblem(long allianceId, int symbol, int symbolColor, int background, int shape, int symbolRgb)
        {
            using (var conexion = Open())
            {
                var update = conexion.CreateCommand();
                update.CommandText = @"
                    UPDATE Alliances SET EmblemSymbol = $s, EmblemSymbolColor = $sc, EmblemBackground = $b,
                                         EmblemBackgroundShape = $sh, EmblemSymbolRgb = $rgb
                    WHERE Id = $a;";
                update.Parameters.AddWithValue("$s", symbol);
                update.Parameters.AddWithValue("$sc", symbolColor);
                update.Parameters.AddWithValue("$b", background);
                update.Parameters.AddWithValue("$sh", shape);
                update.Parameters.AddWithValue("$rgb", symbolRgb);
                update.Parameters.AddWithValue("$a", allianceId);
                update.ExecuteNonQuery();
            }
            return ById(allianceId);
        }

        // ─── Ranks edited from the window ───────────────────────────────────────

        /// <summary>What a rank's name carries for the client to name it in the player's language.</summary>
        public const string RankKeyPrefix = "alliance.rank.";

        /// <summary>The longest rank name the editor lets through.</summary>
        public const int MaxRankNameLength = 30;

        /// <summary>
        /// A new rank, named and drawn as the editor left it, right after the rank it was created
        /// from; the ranks below it go one further down. It starts without rights.
        /// </summary>
        public static Rank CreateRank(long allianceId, string name, int icon, int afterRankId)
        {
            var ranks = Ranks(allianceId);
            var after = ranks.FirstOrDefault(r => r.Id == afterRankId) ?? ranks.OrderBy(r => r.Order).First();
            int order = after.Order + 1;

            using var conexion = Open();
            foreach (var below in ranks.Where(r => r.Order >= order))
            {
                below.Order++;
                SaveRank(conexion, below);
            }
            var rank = new Rank
            {
                AllianceId = allianceId, Id = ranks.Max(r => r.Id) + 1, Name = name, Flag = true,
                Icon = icon, Order = order,
            };
            SaveRank(conexion, rank);
            return rank;
        }

        /// <summary>A rank stored as it is now: its name, rights, icon and order.</summary>
        public static void SaveRank(Rank rank)
        {
            using var conexion = Open();
            SaveRank(conexion, rank);
        }

        /// <summary>
        /// Removes a rank, its members going to another; the ranks below it close the gap. Returns
        /// false for a rank that cannot go -- the leader's, the newcomers', or one that is not there
        /// --, and for the leader's as where its members go: that made as many leaders as it had.
        /// </summary>
        public static bool RemoveRank(long allianceId, int rankId, int replacementId)
        {
            var ranks = Ranks(allianceId);
            var removed = ranks.FirstOrDefault(r => r.Id == rankId);
            var replacement = ranks.FirstOrDefault(r => r.Id == replacementId);
            if (removed == null || replacement == null || removed == replacement) return false;
            if (removed.Id == RankLeader || removed.Id == RankNewcomer || !removed.Flag) return false;
            if (replacement.Id == RankLeader) return false;

            using var conexion = Open();
            var move = conexion.CreateCommand();
            move.CommandText = @"
                UPDATE AllianceMembers SET Rank = $to
                WHERE Rank = $from AND AllianceId = $a;
                DELETE FROM AllianceRanks WHERE AllianceId = $a AND RankId = $from;";
            move.Parameters.AddWithValue("$to", replacementId);
            move.Parameters.AddWithValue("$from", rankId);
            move.Parameters.AddWithValue("$a", allianceId);
            move.ExecuteNonQuery();

            int order = 0;
            foreach (var rank in ranks.Where(r => r.Id != rankId).OrderBy(r => r.Order))
            {
                rank.Order = order++;
                SaveRank(conexion, rank);
            }
            return true;
        }

        // ─── The alliance message and the bulletin ──────────────────────────────

        /// <summary>The alliance message (mcm) and the bulletin (mcr).</summary>
        public const int NoticeMessage = 0;
        public const int NoticeBulletin = 1;

        /// <summary>A notice as the leg carries it: the text, when, and who wrote it.</summary>
        public sealed record Notice(string Text, long WhenS, string AuthorName, long AuthorId);

        /// <summary>
        /// An alliance's notice of a kind. One never written has no text and the alliance's founding
        /// date, which is what joining «E X A M P L E» brought.
        /// </summary>
        public static Notice NoticeOf(Alliance alliance, int kind)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT Text, WhenS, AuthorName, AuthorId FROM AllianceNotices WHERE AllianceId = $a AND Kind = $k;";
            query.Parameters.AddWithValue("$a", alliance.Id);
            query.Parameters.AddWithValue("$k", kind);
            using var lector = query.ExecuteReader();
            return lector.Read()
                ? new Notice(lector.GetString(0), lector.GetInt64(1), lector.GetString(2), lector.GetInt64(3))
                : new Notice("", alliance.CreatedUtcS, "", 0);
        }

        public static void SetNotice(long allianceId, int kind, Notice notice)
        {
            using var conexion = Open();
            var save = conexion.CreateCommand();
            save.CommandText = @"
                INSERT OR REPLACE INTO AllianceNotices (AllianceId, Kind, Text, WhenS, AuthorName, AuthorId)
                VALUES ($a, $k, $t, $w, $n, $id);";
            save.Parameters.AddWithValue("$a", allianceId);
            save.Parameters.AddWithValue("$k", kind);
            save.Parameters.AddWithValue("$t", notice.Text ?? "");
            save.Parameters.AddWithValue("$w", notice.WhenS);
            save.Parameters.AddWithValue("$n", notice.AuthorName ?? "");
            save.Parameters.AddWithValue("$id", notice.AuthorId);
            save.ExecuteNonQuery();
        }

        // ─── Applications ───────────────────────────────────────────────────────

        /// <summary>A character's application to an alliance: one at a time, as the directory shows it.</summary>
        public sealed record Application(long CharacterId, long AllianceId, string Message, long WhenMs);

        /// <summary>Applies to an alliance, replacing whatever application the character had.</summary>
        public static Application Apply(long characterId, long allianceId, string message)
        {
            var application = new Application(characterId, allianceId, message ?? "",
                                              DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var conexion = Open();
            var save = conexion.CreateCommand();
            save.CommandText = @"
                INSERT OR REPLACE INTO AllianceApplications (CharacterId, AllianceId, Message, WhenMs)
                VALUES ($c, $a, $m, $w);";
            save.Parameters.AddWithValue("$c", characterId);
            save.Parameters.AddWithValue("$a", allianceId);
            save.Parameters.AddWithValue("$m", application.Message);
            save.Parameters.AddWithValue("$w", application.WhenMs);
            save.ExecuteNonQuery();
            return application;
        }

        /// <summary>A character's pending application, or null.</summary>
        public static Application ApplicationOf(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT CharacterId, AllianceId, Message, WhenMs FROM AllianceApplications WHERE CharacterId = $c;";
            query.Parameters.AddWithValue("$c", characterId);
            using var lector = query.ExecuteReader();
            return lector.Read()
                ? new Application(lector.GetInt64(0), lector.GetInt64(1), lector.GetString(2), lector.GetInt64(3))
                : null;
        }

        /// <summary>An alliance's pending applications, oldest first.</summary>
        public static List<Application> Applications(long allianceId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT CharacterId, AllianceId, Message, WhenMs FROM AllianceApplications
                WHERE AllianceId = $a ORDER BY WhenMs, CharacterId;";
            query.Parameters.AddWithValue("$a", allianceId);
            using var lector = query.ExecuteReader();
            var list = new List<Application>();
            while (lector.Read())
                list.Add(new Application(lector.GetInt64(0), lector.GetInt64(1), lector.GetString(2), lector.GetInt64(3)));
            return list;
        }

        public static void DropApplication(long characterId)
        {
            using var conexion = Open();
            var delete = conexion.CreateCommand();
            delete.CommandText = "DELETE FROM AllianceApplications WHERE CharacterId = $c;";
            delete.Parameters.AddWithValue("$c", characterId);
            delete.ExecuteNonQuery();
        }

        // ─── Warning when a member connects ─────────────────────────────────────

        /// <summary>Whether the character wants to hear when a member of his alliance connects.</summary>
        public static bool WarnsOnConnection(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT 1 FROM AllianceConnectionWarnings WHERE CharacterId = $c;";
            query.Parameters.AddWithValue("$c", characterId);
            return query.ExecuteScalar() != null;
        }

        public static void SetWarnsOnConnection(long characterId, bool warns)
        {
            using var conexion = Open();
            var save = conexion.CreateCommand();
            save.CommandText = warns
                ? "INSERT OR IGNORE INTO AllianceConnectionWarnings (CharacterId) VALUES ($c);"
                : "DELETE FROM AllianceConnectionWarnings WHERE CharacterId = $c;";
            save.Parameters.AddWithValue("$c", characterId);
            save.ExecuteNonQuery();
        }

        /// <summary>The «Alliance fight» switch per character, read once: every map's actors ask.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> _avaFighters = new();

        /// <summary>Whether this character has the alliance window's «Alliance fight» switch on.</summary>
        public static bool TakesPartInAva(long characterId)
            => _avaFighters.GetOrAdd(characterId, id =>
            {
                using var conexion = Open();
                var query = conexion.CreateCommand();
                query.CommandText = "SELECT 1 FROM AllianceAvaFighters WHERE CharacterId = $c;";
                query.Parameters.AddWithValue("$c", id);
                return query.ExecuteScalar() != null;
            });

        public static void SetTakesPartInAva(long characterId, bool takesPart)
        {
            using var conexion = Open();
            var save = conexion.CreateCommand();
            save.CommandText = takesPart
                ? "INSERT OR IGNORE INTO AllianceAvaFighters (CharacterId) VALUES ($c);"
                : "DELETE FROM AllianceAvaFighters WHERE CharacterId = $c;";
            save.Parameters.AddWithValue("$c", characterId);
            save.ExecuteNonQuery();
            _avaFighters[characterId] = takesPart;
        }

        private static List<int> ParseIds(string text)
            => string.IsNullOrEmpty(text)
                ? new List<int>()
                : text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

        private static string JoinIds(IEnumerable<int> ids) => string.Join(",", ids ?? Array.Empty<int>());
    }
}
