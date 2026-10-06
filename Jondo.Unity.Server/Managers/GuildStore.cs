using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guilds and who is in each one, stored in world.db.
    ///
    /// It is the base everything else about the guild rests on -- the window, the shop, the
    /// chest and, later on, the raids --, so it goes here and not in the air: a created guild
    /// survives a server restart.
    ///
    /// What we know from a capture and what we do not: the name, the emblem, the level, the
    /// founding date and who belongs are measured on the 12 captures of Gremio/. The experience,
    /// the guild kamas, the guildathons and the per-rank permissions appear in the frames but
    /// their full scale has not been reconstructed; the ones seen are stored and the rest start at zero.
    /// </summary>
    public static class GuildStore
    {
        /// <summary>A guild: what identifies it and what the client draws of it.</summary>
        public sealed class Guild
        {
            public long Id { get; init; }
            public string Name { get; init; } = "";
            public int Level { get; set; } = 1;
            public long Experience { get; set; }

            /// <summary>
            /// The emblem, the four numbers of the creation <c>jjg</c>: the symbol, the symbol's
            /// colour index, the background colour (RGB) and the symbol's colour (RGB).
            /// </summary>
            public int EmblemSymbol { get; init; }
            public int EmblemSymbolColor { get; init; }
            public int EmblemBackground { get; init; }
            public int EmblemSymbolRgb { get; init; }

            /// <summary>The founding date, in ISO-8601 with Z, just as it travels in the <c>jhh</c>.</summary>
            public string FoundedUtc { get; init; } = "";

            /// <summary>The guild kamas left to spend (contributions minus purchases).</summary>
            public long GuildKamas { get; set; }
        }

        /// <summary>A member: his character, his rank, when he joined and the note the leader gave him.</summary>
        public sealed class Member
        {
            public long CharacterId { get; init; }
            public long GuildId { get; init; }
            public int Rank { get; set; } = 1;
            public long JoinedUtcMs { get; init; }
            public long Experience { get; set; }

            /// <summary>The note in the «Nota» column and when it was written. Measured on the jgz of «hola».</summary>
            public string Note { get; set; } = "";
            public long NoteMs { get; set; }
        }

        /// <summary>
        /// A guild rank, just as it travels in the jco: name, permissions, icon and order.
        /// </summary>
        /// <remarks>
        /// The permissions are two things the client sends and the server returns unchanged:
        /// a packed list of numbers (the f3.f3) and a flag (the f3.f1, 1 in every rank except
        /// the leader's and the newcomers'). Which permission each number is has not been
        /// reconstructed, and it is not needed to store them: they are returned as they came.
        /// </remarks>
        public sealed class Rank
        {
            public long GuildId { get; init; }
            public int Id { get; init; }
            public string Name { get; set; } = "";
            public byte[] Rights { get; set; } = Array.Empty<byte>();
            public bool Flag { get; set; }
            public int Icon { get; set; }
            public int Order { get; set; }
        }

        /// <summary>A line of the guild's journal (jil).</summary>
        public sealed class LogEntry
        {
            public long GuildId { get; init; }
            public long WhenMs { get; init; }

            /// <summary>0 the founding, 1 someone joins, 2 someone goes through something with f4 = 2 (measured, not understood).</summary>
            public int Kind { get; init; }
            public long CharacterId { get; init; }
            public string Name { get; init; } = "";
        }

        public const int LogFounded = 0;
        public const int LogJoined = 1;

        /// <summary>
        /// The guild's public sheet, the directory's: what the leader writes with the jcc and what
        /// the jci returns. The fields are stored as they come; f1 is when it was written and f8
        /// the leader's name, which the server puts in.
        /// </summary>
        public sealed class Profile
        {
            public long GuildId { get; init; }
            public long WhenMs { get; set; }
            public string Description { get; set; } = "";
            public int MinLevel { get; set; }
            public int MaxLevel { get; set; }
            public byte[] Tags { get; set; } = Array.Empty<byte>();
            public int F5 { get; set; }
            public byte[] F6 { get; set; } = Array.Empty<byte>();
            public string Title { get; set; } = "";
        }

        /// <summary>
        /// The maximum number of members per guild level. Measured at two points -- a level 1
        /// guild says 50 (jhh f9=50) and a level 7 one says 410 --; between them it is an inference
        /// and is stated as such. The rest of the curve is not measured, so outside those two
        /// levels the closest known one is returned.
        /// </summary>
        public static int MaxMembers(int level) => level <= 1 ? 50 : level >= 7 ? 410 : 50 + (level - 1) * 60;

        public static void EnsureTables(SqliteConnection world)
        {
            var create = world.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS Guilds (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Level INTEGER NOT NULL DEFAULT 1,
                    Experience INTEGER NOT NULL DEFAULT 0,
                    EmblemSymbol INTEGER NOT NULL DEFAULT 0,
                    EmblemSymbolColor INTEGER NOT NULL DEFAULT 0,
                    EmblemBackground INTEGER NOT NULL DEFAULT 0,
                    EmblemSymbolRgb INTEGER NOT NULL DEFAULT 0,
                    FoundedUtc TEXT NOT NULL DEFAULT '',
                    GuildKamas INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS GuildMembers (
                    CharacterId INTEGER PRIMARY KEY,
                    GuildId INTEGER NOT NULL,
                    Rank INTEGER NOT NULL DEFAULT 1,
                    JoinedUtcMs INTEGER NOT NULL DEFAULT 0,
                    Experience INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS IdxGuildMembersGuild ON GuildMembers (GuildId);
                CREATE TABLE IF NOT EXISTS GuildApplications (
                    GuildId INTEGER NOT NULL,
                    CharacterId INTEGER NOT NULL,
                    Message TEXT NOT NULL DEFAULT '',
                    WhenMs INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (GuildId, CharacterId)
                );
                CREATE TABLE IF NOT EXISTS GuildOracles (
                    GuildId INTEGER NOT NULL,
                    Oracle INTEGER NOT NULL,
                    DeadlineUtc TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (GuildId, Oracle)
                );
                CREATE TABLE IF NOT EXISTS GuildOwnedRaids (
                    GuildId INTEGER NOT NULL,
                    RaidId INTEGER NOT NULL,
                    BoughtUtcMs INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (GuildId, RaidId)
                );
                CREATE TABLE IF NOT EXISTS GuildContributions (
                    CharacterId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    Done INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (CharacterId, Week)
                );
                CREATE TABLE IF NOT EXISTS GuildRaidScores (
                    GuildId INTEGER NOT NULL,
                    RaidId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    Score INTEGER NOT NULL DEFAULT 0,
                    Runs INTEGER NOT NULL DEFAULT 0,
                    BestUtcMs INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (GuildId, RaidId, Week)
                );
                CREATE TABLE IF NOT EXISTS GuildRanks (
                    GuildId INTEGER NOT NULL,
                    RankId INTEGER NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    Rights BLOB,
                    Flag INTEGER NOT NULL DEFAULT 0,
                    Icon INTEGER NOT NULL DEFAULT 0,
                    Ord INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (GuildId, RankId)
                );
                CREATE TABLE IF NOT EXISTS GuildLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GuildId INTEGER NOT NULL,
                    WhenMs INTEGER NOT NULL,
                    Kind INTEGER NOT NULL,
                    CharacterId INTEGER NOT NULL DEFAULT 0,
                    Name TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS GuildProfiles (
                    GuildId INTEGER PRIMARY KEY,
                    WhenMs INTEGER NOT NULL DEFAULT 0,
                    Description TEXT NOT NULL DEFAULT '',
                    MinLevel INTEGER NOT NULL DEFAULT 0,
                    MaxLevel INTEGER NOT NULL DEFAULT 0,
                    Tags BLOB,
                    F5 INTEGER NOT NULL DEFAULT 0,
                    F6 BLOB,
                    Title TEXT NOT NULL DEFAULT ''
                );";
            create.ExecuteNonQuery();

            // The members' notes, in the table that already existed.
            foreach (string column in new[] { "Note TEXT NOT NULL DEFAULT ''", "NoteMs INTEGER NOT NULL DEFAULT 0" })
            {
                try
                {
                    var add = world.CreateCommand();
                    add.CommandText = $"ALTER TABLE GuildMembers ADD COLUMN {column};";
                    add.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // It was already there.
                }
            }
        }

        /// <summary>A connection string a test can point at a temp database; null uses world.db.</summary>
        internal static string ConnectionStringOverride { get; set; }

        private static SqliteConnection Open()
        {
            var conexion = new SqliteConnection(ConnectionStringOverride ?? DatabaseManager.WorldConnectionString);
            conexion.Open();
            EnsureTables(conexion);
            return conexion;
        }

        /// <summary>Creates the guild with its first member -- the founder -- at rank 1, and returns it.</summary>
        public static Guild Create(long founderCharacterId, string name, int symbol, int symbolColor,
                                   int background, int symbolRgb)
        {
            using var conexion = Open();
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string founded = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

            var insertGuild = conexion.CreateCommand();
            insertGuild.CommandText = @"
                INSERT INTO Guilds (Name, Level, EmblemSymbol, EmblemSymbolColor, EmblemBackground, EmblemSymbolRgb, FoundedUtc)
                VALUES ($name, 1, $sym, $symc, $bg, $symrgb, $founded);
                SELECT last_insert_rowid();";
            insertGuild.Parameters.AddWithValue("$name", name);
            insertGuild.Parameters.AddWithValue("$sym", symbol);
            insertGuild.Parameters.AddWithValue("$symc", symbolColor);
            insertGuild.Parameters.AddWithValue("$bg", background);
            insertGuild.Parameters.AddWithValue("$symrgb", symbolRgb);
            insertGuild.Parameters.AddWithValue("$founded", founded);
            long id = (long)insertGuild.ExecuteScalar();

            var insertMember = conexion.CreateCommand();
            insertMember.CommandText = @"
                INSERT OR REPLACE INTO GuildMembers (CharacterId, GuildId, Rank, JoinedUtcMs)
                VALUES ($c, $g, 1, $ms);";
            insertMember.Parameters.AddWithValue("$c", founderCharacterId);
            insertMember.Parameters.AddWithValue("$g", id);
            insertMember.Parameters.AddWithValue("$ms", ms);
            insertMember.ExecuteNonQuery();

            foreach (var rank in DefaultRanks(id)) SaveRank(conexion, rank);
            WriteLog(conexion, id, ms, LogFounded, 0, "");

            return new Guild
            {
                Id = id, Name = name, Level = 1,
                EmblemSymbol = symbol, EmblemSymbolColor = symbolColor,
                EmblemBackground = background, EmblemSymbolRgb = symbolRgb,
                FoundedUtc = founded,
            };
        }

        /// <summary>A character's guild, or null if he has none.</summary>
        public static Guild GuildOf(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT g.Id, g.Name, g.Level, g.Experience, g.EmblemSymbol, g.EmblemSymbolColor,
                       g.EmblemBackground, g.EmblemSymbolRgb, g.FoundedUtc, g.GuildKamas
                FROM GuildMembers m JOIN Guilds g ON g.Id = m.GuildId
                WHERE m.CharacterId = $c;";
            query.Parameters.AddWithValue("$c", characterId);
            using var lector = query.ExecuteReader();
            if (!lector.Read()) return null;
            return new Guild
            {
                Id = lector.GetInt64(0), Name = lector.GetString(1), Level = lector.GetInt32(2),
                Experience = lector.GetInt64(3), EmblemSymbol = lector.GetInt32(4),
                EmblemSymbolColor = lector.GetInt32(5), EmblemBackground = lector.GetInt32(6),
                EmblemSymbolRgb = lector.GetInt32(7), FoundedUtc = lector.GetString(8),
                GuildKamas = lector.GetInt64(9),
            };
        }

        /// <summary>A guild by its name, case-insensitive. Null if there is none like that.</summary>
        public static Guild ByName(string name)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT Id, Name, Level, Experience, EmblemSymbol, EmblemSymbolColor,
                       EmblemBackground, EmblemSymbolRgb, FoundedUtc, GuildKamas
                FROM Guilds WHERE Name = $n COLLATE NOCASE LIMIT 1;";
            query.Parameters.AddWithValue("$n", name ?? "");
            using var lector = query.ExecuteReader();
            if (!lector.Read()) return null;
            return new Guild
            {
                Id = lector.GetInt64(0), Name = lector.GetString(1), Level = lector.GetInt32(2),
                Experience = lector.GetInt64(3), EmblemSymbol = lector.GetInt32(4),
                EmblemSymbolColor = lector.GetInt32(5), EmblemBackground = lector.GetInt32(6),
                EmblemSymbolRgb = lector.GetInt32(7), FoundedUtc = lector.GetString(8),
                GuildKamas = lector.GetInt64(9),
            };
        }

        /// <summary>A character's position in his guild, or zero if he is in none.</summary>
        public static int RankOf(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT Rank FROM GuildMembers WHERE CharacterId = $c;";
            query.Parameters.AddWithValue("$c", characterId);
            var value = query.ExecuteScalar();
            return value == null || value is DBNull ? 0 : Convert.ToInt32(value);
        }

        /// <summary>A guild's members, by their character and their rank.</summary>
        public static List<Member> Members(long guildId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT CharacterId, GuildId, Rank, JoinedUtcMs, Experience, Note, NoteMs
                FROM GuildMembers WHERE GuildId = $g ORDER BY Rank, CharacterId;";
            query.Parameters.AddWithValue("$g", guildId);
            using var lector = query.ExecuteReader();
            var fuera = new List<Member>();
            while (lector.Read())
            {
                fuera.Add(new Member
                {
                    CharacterId = lector.GetInt64(0), GuildId = lector.GetInt64(1),
                    Rank = lector.GetInt32(2), JoinedUtcMs = lector.GetInt64(3),
                    Experience = lector.GetInt64(4),
                    Note = lector.IsDBNull(5) ? "" : lector.GetString(5),
                    NoteMs = lector.IsDBNull(6) ? 0 : lector.GetInt64(6),
                });
            }
            return fuera;
        }

        /// <summary>
        /// Puts a character into a guild with the rank given. Rank 4 is the one of those who
        /// have just joined: it is the one carried on the member list by whoever joined by
        /// application in the capture of creating «Jondo».
        /// </summary>
        public const int RankNewcomer = 4;

        /// <summary>Rank 1 is the leader's: the one the founder carries in the capture's jgw and jgu.</summary>
        public const int RankLeader = 1;

        public static Member Join(long characterId, long guildId, int rank = RankNewcomer)
        {
            using var conexion = Open();
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var insert = conexion.CreateCommand();
            insert.CommandText = @"
                INSERT OR REPLACE INTO GuildMembers (CharacterId, GuildId, Rank, JoinedUtcMs)
                VALUES ($c, $g, $r, $ms);
                DELETE FROM GuildApplications WHERE CharacterId = $c;";
            insert.Parameters.AddWithValue("$c", characterId);
            insert.Parameters.AddWithValue("$g", guildId);
            insert.Parameters.AddWithValue("$r", rank);
            insert.Parameters.AddWithValue("$ms", ms);
            insert.ExecuteNonQuery();

            var character = DatabaseManager.GetCharacterById(characterId);
            WriteLog(conexion, guildId, ms, LogJoined, characterId, character?.Name ?? "");
            return new Member { CharacterId = characterId, GuildId = guildId, Rank = rank, JoinedUtcMs = ms };
        }

        /// <summary>Un miembro suelto, o null.</summary>
        public static Member MemberOf(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT CharacterId, GuildId, Rank, JoinedUtcMs, Experience, Note, NoteMs
                FROM GuildMembers WHERE CharacterId = $c;";
            query.Parameters.AddWithValue("$c", characterId);
            using var lector = query.ExecuteReader();
            if (!lector.Read()) return null;
            return new Member
            {
                CharacterId = lector.GetInt64(0), GuildId = lector.GetInt64(1),
                Rank = lector.GetInt32(2), JoinedUtcMs = lector.GetInt64(3),
                Experience = lector.GetInt64(4),
                Note = lector.IsDBNull(5) ? "" : lector.GetString(5),
                NoteMs = lector.IsDBNull(6) ? 0 : lector.GetInt64(6),
            };
        }

        /// <summary>The note the leader puts on a member (jjj). Returns the member brought up to date.</summary>
        public static Member SetNote(long characterId, string note, long whenMs)
        {
            using var conexion = Open();
            var update = conexion.CreateCommand();
            update.CommandText = "UPDATE GuildMembers SET Note = $n, NoteMs = $ms WHERE CharacterId = $c;";
            update.Parameters.AddWithValue("$n", note ?? "");
            update.Parameters.AddWithValue("$ms", whenMs);
            update.Parameters.AddWithValue("$c", characterId);
            update.ExecuteNonQuery();
            return MemberOf(characterId);
        }

        /// <summary>Changes a member's rank. Returns the member brought up to date, or null if he is not there.</summary>
        public static Member SetRank(long characterId, int rank)
        {
            using var conexion = Open();
            var update = conexion.CreateCommand();
            update.CommandText = "UPDATE GuildMembers SET Rank = $r WHERE CharacterId = $c;";
            update.Parameters.AddWithValue("$r", rank);
            update.Parameters.AddWithValue("$c", characterId);
            update.ExecuteNonQuery();
            return MemberOf(characterId);
        }

        /// <summary>
        /// A member's guild coins: what he has contributed, in guild kamas. It is what the jgu's
        /// f7.f2 shows -- 10 after one contribution, 20 after two -- and the «Gremichas» column.
        /// </summary>
        public static int ContributedBy(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT COALESCE(SUM(Done), 0) FROM GuildContributions WHERE CharacterId = $c;";
            query.Parameters.AddWithValue("$c", characterId);
            return Convert.ToInt32(query.ExecuteScalar()) * ContributionGuildKamas;
        }

        // ─── Rangos ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The four ranks a guild is born with, the ones in the jco of the capture of creating
        /// «Jondo»: names by translation key, permissions as they came, icons 116, 115, 114
        /// and 117, and order 0 to 3.
        /// </summary>
        public static List<Rank> DefaultRanks(long guildId)
        {
            byte[] rights1 = { 0x01, 0x02, 0x05, 0x06, 0x07, 0x08, 0x0d, 0x0e, 0x0f, 0x17, 0x18, 0x19,
                               0x1a, 0x1d, 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
                               0x28, 0x29, 0x2a, 0x2b };
            byte[] rights2 = { 0x01, 0x02, 0x05, 0x06, 0x26, 0x07, 0x27, 0x08, 0x28, 0x29, 0x0d, 0x0e,
                               0x0f, 0x17, 0x18, 0x19, 0x1a };
            return new List<Rank>
            {
                new Rank { GuildId = guildId, Id = 1, Name = "guild.rank.1.name", Rights = rights1, Flag = false, Icon = 116, Order = 0 },
                new Rank { GuildId = guildId, Id = 2, Name = "guild.rank.2.name", Rights = rights2, Flag = true, Icon = 115, Order = 1 },
                new Rank { GuildId = guildId, Id = 3, Name = "guild.rank.3.name", Rights = Array.Empty<byte>(), Flag = true, Icon = 114, Order = 2 },
                new Rank { GuildId = guildId, Id = 4, Name = "guild.rank.4.name", Rights = Array.Empty<byte>(), Flag = false, Icon = 117, Order = 3 },
            };
        }

        /// <summary>A guild's ranks, in the order they are shown. The usual ones if it has none stored.</summary>
        public static List<Rank> Ranks(long guildId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT RankId, Name, Rights, Flag, Icon, Ord FROM GuildRanks WHERE GuildId = $g ORDER BY Ord, RankId;";
            query.Parameters.AddWithValue("$g", guildId);
            using var lector = query.ExecuteReader();
            var fuera = new List<Rank>();
            while (lector.Read())
            {
                fuera.Add(new Rank
                {
                    GuildId = guildId, Id = lector.GetInt32(0), Name = lector.GetString(1),
                    Rights = lector.IsDBNull(2) ? Array.Empty<byte>() : (byte[])lector[2],
                    Flag = lector.GetInt32(3) != 0, Icon = lector.GetInt32(4), Order = lector.GetInt32(5),
                });
            }

            if (fuera.Count > 0) return fuera;

            // A guild from before they were stored: it is given the usual ones.
            var defaults = DefaultRanks(guildId);
            foreach (var rank in defaults) SaveRank(conexion, rank);
            return defaults;
        }

        public static void SaveRank(Rank rank)
        {
            using var conexion = Open();
            SaveRank(conexion, rank);
        }

        private static void SaveRank(SqliteConnection conexion, Rank rank)
        {
            var upsert = conexion.CreateCommand();
            upsert.CommandText = @"
                INSERT INTO GuildRanks (GuildId, RankId, Name, Rights, Flag, Icon, Ord)
                VALUES ($g, $r, $n, $rights, $f, $i, $o)
                ON CONFLICT(GuildId, RankId) DO UPDATE SET
                    Name = $n, Rights = $rights, Flag = $f, Icon = $i, Ord = $o;";
            upsert.Parameters.AddWithValue("$g", rank.GuildId);
            upsert.Parameters.AddWithValue("$r", rank.Id);
            upsert.Parameters.AddWithValue("$n", rank.Name ?? "");
            upsert.Parameters.AddWithValue("$rights", rank.Rights ?? Array.Empty<byte>());
            upsert.Parameters.AddWithValue("$f", rank.Flag ? 1 : 0);
            upsert.Parameters.AddWithValue("$i", rank.Icon);
            upsert.Parameters.AddWithValue("$o", rank.Order);
            upsert.ExecuteNonQuery();
        }

        /// <summary>
        /// A new rank (jcv): with the name and icon given, in the order asked for, and the ones
        /// from there down shift by one. The id is the next free one and the flag is set, which
        /// is how «Rango personalizado» was born in the capture: f3 { f1: 1 }.
        /// </summary>
        public static Rank CreateRank(long guildId, string name, int icon, int order)
        {
            var ranks = Ranks(guildId);
            int id = 1;
            foreach (var rank in ranks) if (rank.Id >= id) id = rank.Id + 1;

            using var conexion = Open();
            foreach (var rank in ranks)
            {
                if (rank.Order < order) continue;
                rank.Order++;
                SaveRank(conexion, rank);
            }

            var created = new Rank { GuildId = guildId, Id = id, Name = name ?? "", Icon = icon, Order = order, Flag = true };
            SaveRank(conexion, created);
            return created;
        }

        // ─── The journal ────────────────────────────────────────────────────────

        private static void WriteLog(SqliteConnection conexion, long guildId, long whenMs, int kind, long characterId, string name)
        {
            var insert = conexion.CreateCommand();
            insert.CommandText = "INSERT INTO GuildLog (GuildId, WhenMs, Kind, CharacterId, Name) VALUES ($g, $ms, $k, $c, $n);";
            insert.Parameters.AddWithValue("$g", guildId);
            insert.Parameters.AddWithValue("$ms", whenMs);
            insert.Parameters.AddWithValue("$k", kind);
            insert.Parameters.AddWithValue("$c", characterId);
            insert.Parameters.AddWithValue("$n", name ?? "");
            insert.ExecuteNonQuery();
        }

        /// <summary>A guild's journal, from oldest to newest, as the jil sends it.</summary>
        public static List<LogEntry> LogOf(long guildId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT WhenMs, Kind, CharacterId, Name FROM GuildLog WHERE GuildId = $g ORDER BY WhenMs, Id;";
            query.Parameters.AddWithValue("$g", guildId);
            using var lector = query.ExecuteReader();
            var fuera = new List<LogEntry>();
            while (lector.Read())
            {
                fuera.Add(new LogEntry
                {
                    GuildId = guildId, WhenMs = lector.GetInt64(0), Kind = lector.GetInt32(1),
                    CharacterId = lector.GetInt64(2), Name = lector.GetString(3),
                });
            }
            return fuera;
        }

        // ─── The public sheet ───────────────────────────────────────────────────

        /// <summary>A guild's directory sheet, or null if nobody has written it.</summary>
        public static Profile ProfileOf(long guildId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT WhenMs, Description, MinLevel, MaxLevel, Tags, F5, F6, Title FROM GuildProfiles WHERE GuildId = $g;";
            query.Parameters.AddWithValue("$g", guildId);
            using var lector = query.ExecuteReader();
            if (!lector.Read()) return null;
            return new Profile
            {
                GuildId = guildId, WhenMs = lector.GetInt64(0), Description = lector.GetString(1),
                MinLevel = lector.GetInt32(2), MaxLevel = lector.GetInt32(3),
                Tags = lector.IsDBNull(4) ? Array.Empty<byte>() : (byte[])lector[4],
                F5 = lector.GetInt32(5),
                F6 = lector.IsDBNull(6) ? Array.Empty<byte>() : (byte[])lector[6],
                Title = lector.GetString(7),
            };
        }

        public static void SaveProfile(Profile profile)
        {
            using var conexion = Open();
            var upsert = conexion.CreateCommand();
            upsert.CommandText = @"
                INSERT INTO GuildProfiles (GuildId, WhenMs, Description, MinLevel, MaxLevel, Tags, F5, F6, Title)
                VALUES ($g, $ms, $d, $min, $max, $tags, $f5, $f6, $t)
                ON CONFLICT(GuildId) DO UPDATE SET
                    WhenMs = $ms, Description = $d, MinLevel = $min, MaxLevel = $max,
                    Tags = $tags, F5 = $f5, F6 = $f6, Title = $t;";
            upsert.Parameters.AddWithValue("$g", profile.GuildId);
            upsert.Parameters.AddWithValue("$ms", profile.WhenMs);
            upsert.Parameters.AddWithValue("$d", profile.Description ?? "");
            upsert.Parameters.AddWithValue("$min", profile.MinLevel);
            upsert.Parameters.AddWithValue("$max", profile.MaxLevel);
            upsert.Parameters.AddWithValue("$tags", profile.Tags ?? Array.Empty<byte>());
            upsert.Parameters.AddWithValue("$f5", profile.F5);
            upsert.Parameters.AddWithValue("$f6", profile.F6 ?? Array.Empty<byte>());
            upsert.Parameters.AddWithValue("$t", profile.Title ?? "");
            upsert.ExecuteNonQuery();
        }

        /// <summary>All the guilds, for the directory.</summary>
        public static List<Guild> AllGuilds()
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT Id, Name, Level, Experience, EmblemSymbol, EmblemSymbolColor,
                       EmblemBackground, EmblemSymbolRgb, FoundedUtc, GuildKamas
                FROM Guilds ORDER BY Id;";
            using var lector = query.ExecuteReader();
            var fuera = new List<Guild>();
            while (lector.Read())
            {
                fuera.Add(new Guild
                {
                    Id = lector.GetInt64(0), Name = lector.GetString(1), Level = lector.GetInt32(2),
                    Experience = lector.GetInt64(3), EmblemSymbol = lector.GetInt32(4),
                    EmblemSymbolColor = lector.GetInt32(5), EmblemBackground = lector.GetInt32(6),
                    EmblemSymbolRgb = lector.GetInt32(7), FoundedUtc = lector.GetString(8),
                    GuildKamas = lector.GetInt64(9),
                });
            }
            return fuera;
        }

        /// <summary>A guild's leader: the one at rank 1, or the first there is.</summary>
        public static Member LeaderOf(long guildId)
        {
            Member first = null;
            foreach (var member in Members(guildId))
            {
                if (member.Rank == RankLeader) return member;
                first ??= member;
            }
            return first;
        }

        // ─── Candidaturas ───────────────────────────────────────────────────────

        /// <summary>An application: who sends it, to which guild, with what text and when.</summary>
        public sealed class Application
        {
            public long GuildId { get; init; }
            public long CharacterId { get; init; }
            public string Message { get; init; } = "";
            public long WhenMs { get; init; }
        }

        public static Application Apply(long characterId, long guildId, string message)
        {
            using var conexion = Open();
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var insert = conexion.CreateCommand();
            insert.CommandText = @"
                INSERT OR REPLACE INTO GuildApplications (GuildId, CharacterId, Message, WhenMs)
                VALUES ($g, $c, $m, $ms);";
            insert.Parameters.AddWithValue("$g", guildId);
            insert.Parameters.AddWithValue("$c", characterId);
            insert.Parameters.AddWithValue("$m", message ?? "");
            insert.Parameters.AddWithValue("$ms", ms);
            insert.ExecuteNonQuery();
            return new Application { GuildId = guildId, CharacterId = characterId, Message = message ?? "", WhenMs = ms };
        }

        public static List<Application> Applications(long guildId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT GuildId, CharacterId, Message, WhenMs FROM GuildApplications
                WHERE GuildId = $g ORDER BY WhenMs;";
            query.Parameters.AddWithValue("$g", guildId);
            using var lector = query.ExecuteReader();
            var fuera = new List<Application>();
            while (lector.Read())
            {
                fuera.Add(new Application
                {
                    GuildId = lector.GetInt64(0), CharacterId = lector.GetInt64(1),
                    Message = lector.GetString(2), WhenMs = lector.GetInt64(3),
                });
            }
            return fuera;
        }

        public static Application ApplicationOf(long guildId, long characterId)
        {
            foreach (var one in Applications(guildId))
            {
                if (one.CharacterId == characterId) return one;
            }
            return null;
        }

        public static void DropApplication(long guildId, long characterId)
        {
            using var conexion = Open();
            var borra = conexion.CreateCommand();
            borra.CommandText = "DELETE FROM GuildApplications WHERE GuildId = $g AND CharacterId = $c;";
            borra.Parameters.AddWithValue("$g", guildId);
            borra.Parameters.AddWithValue("$c", characterId);
            borra.ExecuteNonQuery();
        }

        // ─── Guild kamas, contributions and shop ────────────────────────────────

        /// <summary>
        /// What a contribution moves: 10,000 of the character's kamas for 10 guild ones, and five
        /// at most per week. Measured on «contribuir en el gremio»: the jle says 10,000 and the
        /// guild kamas go up from 10 to 20; the counter of the ones left went down from 4 to 3, so
        /// the fifth was the last.
        /// </summary>
        public const int ContributionKamas = 10000;
        public const int ContributionGuildKamas = 10;
        public const int ContributionsPerWeek = 5;

        /// <summary>The Tuesday the week starts on, which is when the game resets the weekly things.</summary>
        public static string WeekOf(DateTimeOffset when)
        {
            int back = ((int)when.UtcDateTime.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
            return when.UtcDateTime.Date.AddDays(-back).ToString("yyyy-MM-dd");
        }

        /// <summary>How many contributions a character has left this week.</summary>
        public static int ContributionsLeft(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT Done FROM GuildContributions WHERE CharacterId = $c AND Week = $w;";
            query.Parameters.AddWithValue("$c", characterId);
            query.Parameters.AddWithValue("$w", WeekOf(DateTimeOffset.UtcNow));
            var value = query.ExecuteScalar();
            int done = value == null || value is DBNull ? 0 : Convert.ToInt32(value);
            return Math.Max(0, ContributionsPerWeek - done);
        }

        /// <summary>
        /// Records a contribution and adds its kamas to the guild. Returns how many the character
        /// has left this week, or minus one if he had none left.
        /// </summary>
        public static int Contribute(long characterId, long guildId)
        {
            if (ContributionsLeft(characterId) <= 0) return -1;
            using var conexion = Open();
            var apunta = conexion.CreateCommand();
            apunta.CommandText = @"
                INSERT INTO GuildContributions (CharacterId, Week, Done) VALUES ($c, $w, 1)
                ON CONFLICT (CharacterId, Week) DO UPDATE SET Done = Done + 1;
                UPDATE Guilds SET GuildKamas = GuildKamas + $k WHERE Id = $g;";
            apunta.Parameters.AddWithValue("$c", characterId);
            apunta.Parameters.AddWithValue("$w", WeekOf(DateTimeOffset.UtcNow));
            apunta.Parameters.AddWithValue("$k", ContributionGuildKamas);
            apunta.Parameters.AddWithValue("$g", guildId);
            apunta.ExecuteNonQuery();
            return ContributionsLeft(characterId);
        }

        /// <summary>Spends guild kamas. False -- and spends nothing -- when they do not reach.</summary>
        public static bool SpendGuildKamas(long guildId, long amount)
        {
            using var conexion = Open();
            var gasta = conexion.CreateCommand();
            gasta.CommandText = "UPDATE Guilds SET GuildKamas = GuildKamas - $k WHERE Id = $g AND GuildKamas >= $k;";
            gasta.Parameters.AddWithValue("$k", amount);
            gasta.Parameters.AddWithValue("$g", guildId);
            return gasta.ExecuteNonQuery() > 0;
        }

        /// <summary>Records a bought oracle, with the deadline the members have to activate it.</summary>
        public static void BuyOracle(long guildId, int oracle, DateTimeOffset deadline)
        {
            using var conexion = Open();
            var insert = conexion.CreateCommand();
            insert.CommandText = @"
                INSERT OR REPLACE INTO GuildOracles (GuildId, Oracle, DeadlineUtc)
                VALUES ($g, $o, $d);";
            insert.Parameters.AddWithValue("$g", guildId);
            insert.Parameters.AddWithValue("$o", oracle);
            insert.Parameters.AddWithValue("$d", deadline.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
            insert.ExecuteNonQuery();
        }

        /// <summary>A bought oracle's deadline, or null if the guild does not have it.</summary>
        public static string OracleDeadline(long guildId, int oracle)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT DeadlineUtc FROM GuildOracles WHERE GuildId = $g AND Oracle = $o;";
            query.Parameters.AddWithValue("$g", guildId);
            query.Parameters.AddWithValue("$o", oracle);
            return query.ExecuteScalar() as string;
        }

        // ─── Bought raids ───────────────────────────────────────────────────────

        /// <summary>Records a raid bought by the guild, waiting to be launched.</summary>
        public static void BuyRaid(long guildId, int raidId)
        {
            using var conexion = Open();
            var insert = conexion.CreateCommand();
            insert.CommandText = @"
                INSERT OR REPLACE INTO GuildOwnedRaids (GuildId, RaidId, BoughtUtcMs)
                VALUES ($g, $r, $ms);";
            insert.Parameters.AddWithValue("$g", guildId);
            insert.Parameters.AddWithValue("$r", raidId);
            insert.Parameters.AddWithValue("$ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            insert.ExecuteNonQuery();
        }

        public static bool OwnsRaid(long guildId, int raidId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT 1 FROM GuildOwnedRaids WHERE GuildId = $g AND RaidId = $r;";
            query.Parameters.AddWithValue("$g", guildId);
            query.Parameters.AddWithValue("$r", raidId);
            return query.ExecuteScalar() != null;
        }

        /// <summary>The raids the guild has bought and not spent.</summary>
        public static List<int> OwnedRaids(long guildId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT RaidId FROM GuildOwnedRaids WHERE GuildId = $g ORDER BY RaidId;";
            query.Parameters.AddWithValue("$g", guildId);
            using var lector = query.ExecuteReader();
            var fuera = new List<int>();
            while (lector.Read()) fuera.Add(lector.GetInt32(0));
            return fuera;
        }

        /// <summary>It is spent on launching: a bought raid is one use, not a permanent key.</summary>
        public static void DropRaid(long guildId, int raidId)
        {
            using var conexion = Open();
            var borra = conexion.CreateCommand();
            borra.CommandText = "DELETE FROM GuildOwnedRaids WHERE GuildId = $g AND RaidId = $r;";
            borra.Parameters.AddWithValue("$g", guildId);
            borra.Parameters.AddWithValue("$r", raidId);
            borra.ExecuteNonQuery();
        }

        // ─── The weekly ranking ─────────────────────────────────────────────────

        /// <summary>A row of the ranking: a guild, its best score of the week.</summary>
        public sealed class LadderRow
        {
            public long GuildId;
            public string Name = "";
            public long Score;

            /// <summary>How many raids of this one the guild has finished this week.</summary>
            public int Runs;

            /// <summary>The position, counting from one.</summary>
            public int Place;
        }

        /// <summary>
        /// Records what a guild scored in a raid. It keeps the BEST of the week.
        /// </summary>
        /// <remarks>
        /// The best and not the sum, and it is a decision of ours worth stating: the game's page
        /// speaks of a global ranking in which «los mejores podrán representar con orgullo a
        /// su gremio», and adding up would reward the guild that goes in most often over the one
        /// that does it best. No client data says which of the two it is. The number of times it
        /// went in is recorded anyway, which is what is needed to change our mind without losing anything.
        /// </remarks>
        public static long RecordRaidScore(long guildId, int raidId, long score, DateTimeOffset when)
        {
            string week = WeekOf(when);
            using var conexion = Open();

            var query = conexion.CreateCommand();
            query.CommandText = "SELECT Score FROM GuildRaidScores " +
                                "WHERE GuildId = $g AND RaidId = $r AND Week = $w;";
            query.Parameters.AddWithValue("$g", guildId);
            query.Parameters.AddWithValue("$r", raidId);
            query.Parameters.AddWithValue("$w", week);
            var had = query.ExecuteScalar();
            long best = had == null || had is DBNull ? 0 : Convert.ToInt64(had);

            bool better = score > best;
            var apunta = conexion.CreateCommand();
            apunta.CommandText = @"
                INSERT INTO GuildRaidScores (GuildId, RaidId, Week, Score, Runs, BestUtcMs)
                VALUES ($g, $r, $w, $s, 1, $ms)
                ON CONFLICT(GuildId, RaidId, Week) DO UPDATE SET
                    Runs = Runs + 1,
                    Score = CASE WHEN $s > Score THEN $s ELSE Score END,
                    BestUtcMs = CASE WHEN $s > Score THEN $ms ELSE BestUtcMs END;";
            apunta.Parameters.AddWithValue("$g", guildId);
            apunta.Parameters.AddWithValue("$r", raidId);
            apunta.Parameters.AddWithValue("$w", week);
            apunta.Parameters.AddWithValue("$s", score);
            apunta.Parameters.AddWithValue("$ms", when.ToUnixTimeMilliseconds());
            apunta.ExecuteNonQuery();

            return better ? score : best;
        }

        /// <summary>
        /// A raid's ranking in a week, from most to least.
        /// </summary>
        /// <remarks>
        /// On equal score, whoever did it first wins, which is what every table of this game does
        /// and the only thing that leaves a stable order: without it, two tied guilds would swap
        /// places every time the list is drawn.
        /// </remarks>
        public static List<LadderRow> Ladder(int raidId, DateTimeOffset when, int most = 20)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT s.GuildId, g.Name, s.Score, s.Runs
                FROM GuildRaidScores s LEFT JOIN Guilds g ON g.Id = s.GuildId
                WHERE s.RaidId = $r AND s.Week = $w AND s.Score > 0
                ORDER BY s.Score DESC, s.BestUtcMs ASC
                LIMIT $n;";
            query.Parameters.AddWithValue("$r", raidId);
            query.Parameters.AddWithValue("$w", WeekOf(when));
            query.Parameters.AddWithValue("$n", Math.Max(1, most));

            var rows = new List<LadderRow>();
            using var lector = query.ExecuteReader();
            while (lector.Read())
            {
                rows.Add(new LadderRow
                {
                    GuildId = lector.GetInt64(0),
                    Name = lector.IsDBNull(1) ? "" : lector.GetString(1),
                    Score = lector.GetInt64(2),
                    Runs = lector.GetInt32(3),
                    Place = rows.Count + 1,
                });
            }

            return rows;
        }

        /// <summary>A guild's position this week in a raid, or zero if it is not there.</summary>
        public static int PlaceOf(long guildId, int raidId, DateTimeOffset when)
        {
            foreach (var row in Ladder(raidId, when, int.MaxValue))
            {
                if (row.GuildId == guildId) return row.Place;
            }

            return 0;
        }

        /// <summary>Takes a character out of his guild. Returns the guild he left, or null if he had none.</summary>
        public static Guild Leave(long characterId)
        {
            var guild = GuildOf(characterId);
            if (guild == null) return null;
            using var conexion = Open();
            var borra = conexion.CreateCommand();
            borra.CommandText = "DELETE FROM GuildMembers WHERE CharacterId = $c;";
            borra.Parameters.AddWithValue("$c", characterId);
            borra.ExecuteNonQuery();
            return guild;
        }
    }
}
