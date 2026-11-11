using System;
using System.Collections.Generic;
using System.Linq;
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
        /// Rights retain their historical packed-int32 storage representation (f3.f3).
        /// GuildRankRights decodes them for delivery and permission checks. Flag is the
        /// client's Modifiable value (f3.f1), true by default except for leader/newcomer.
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

        /// <summary>The guild's two notices: the message of the day (jhk) and the logbook's bulletin (jjf).</summary>
        public enum NoticeKind { MessageOfTheDay = 1, Bulletin = 2 }

        /// <summary>A notice as it was written, and by whom.</summary>
        public sealed record Notice(string Text, long WhenMs, long AuthorId, string AuthorName);

        /// <summary>The journal's lists, jil's fields: the logbook's tabs.</summary>
        public enum LogTab { General = 1, Missions = 2, Raids = 3, Chest = 5 }

        /// <summary>A line of the guild's journal (jil): its list, its case and the case's fields, as they travel.</summary>
        public sealed class LogEntry
        {
            public long GuildId { get; init; }
            public long WhenMs { get; init; }
            public LogTab Tab { get; init; } = LogTab.General;

            /// <summary>The line's case in the client's oneof (see <see cref="Network.GuildLogProtocol"/>).</summary>
            public int Field { get; init; }

            /// <summary>The case's own message, already written.</summary>
            public byte[] Details { get; init; } = Array.Empty<byte>();

            /// <summary>Who did it, when somebody did: for the store's own readers.</summary>
            public long CharacterId { get; init; }
            public string Name { get; init; } = "";
        }

        /// <summary>
        /// The journal's two kinds before it had its cases: kept in the old rows' Kind, read back as
        /// a chest tab unlocked (the founding's line) and as a member's coming or going.
        /// </summary>
        private const int OldLogFounded = 0;
        private const int OldLogJoined = 1;

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

            /// <summary>The sheet's f9, which the client's card (jm.npn) reads as the minimum achievement points.</summary>
            public int MaxLevel { get; set; }
            public byte[] Tags { get; set; } = Array.Empty<byte>();

            /// <summary>
            /// The recruitment, the sheet's f5 (jm.npn → GuildCardUI): 1 lets anybody join at once
            /// («Unirse»), 0 and 2 take applications -- 2 the unwritten sheet's, as measured.
            /// </summary>
            public int F5 { get; set; }
            public byte[] F6 { get; set; } = Array.Empty<byte>();
            public string Title { get; set; } = "";
        }


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
                CREATE TABLE IF NOT EXISTS GuildActivityWeeks (
                    GuildId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    ActivityId INTEGER NOT NULL DEFAULT 0,
                    NextActivityId INTEGER NOT NULL DEFAULT 0,
                    Points INTEGER NOT NULL DEFAULT 0,
                    Preferences BLOB,
                    NextPreferences BLOB,
                    PRIMARY KEY (GuildId, Week)
                );
                CREATE TABLE IF NOT EXISTS GuildMissionDraws (
                    GuildId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    Slot INTEGER NOT NULL,
                    MissionId INTEGER NOT NULL,
                    Uuid TEXT NOT NULL,
                    ObjectiveId INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (GuildId, Week, Slot)
                );
                CREATE TABLE IF NOT EXISTS GuildMissionInterest (
                    GuildId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    Slot INTEGER NOT NULL,
                    CharacterId INTEGER NOT NULL,
                    PRIMARY KEY (GuildId, Week, Slot, CharacterId)
                );
                CREATE TABLE IF NOT EXISTS GuildMissionRerolls (
                    GuildId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    MissionId INTEGER NOT NULL,
                    PRIMARY KEY (GuildId, Week, MissionId)
                );
                CREATE TABLE IF NOT EXISTS GuildMissionProgress (
                    GuildId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    Slot INTEGER NOT NULL,
                    CharacterId INTEGER NOT NULL,
                    Count INTEGER NOT NULL DEFAULT 0,
                    Done INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (GuildId, Week, Slot, CharacterId)
                );
                CREATE TABLE IF NOT EXISTS GuildTokenWeeks (
                    CharacterId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    Tokens INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (CharacterId, Week)
                );
                CREATE TABLE IF NOT EXISTS GuildAcknowledgments (
                    CharacterId INTEGER PRIMARY KEY,
                    Pending INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS GuildHallChoices (
                    GuildId INTEGER PRIMARY KEY,
                    HallId INTEGER NOT NULL,
                    Theme INTEGER NOT NULL,
                    ChangedMs INTEGER NOT NULL DEFAULT 0
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

            // How long each guild's best raid of the week took, for the ladder's duration column.
            try
            {
                var add = world.CreateCommand();
                add.CommandText = "ALTER TABLE GuildRaidScores ADD COLUMN BestDurationMs INTEGER NOT NULL DEFAULT 0;";
                add.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // It was already there.
            }

            // The message of the day and the logbook's bulletin.
            var notices = world.CreateCommand();
            notices.CommandText = @"
                CREATE TABLE IF NOT EXISTS GuildNotices (
                    GuildId    INTEGER NOT NULL,
                    Kind       INTEGER NOT NULL,
                    Text       TEXT NOT NULL DEFAULT '',
                    WhenMs     INTEGER NOT NULL DEFAULT 0,
                    AuthorId   INTEGER NOT NULL DEFAULT 0,
                    AuthorName TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (GuildId, Kind));";
            notices.ExecuteNonQuery();

            // The journal's list, case and case message, for every kind of line the logbook has.
            foreach (string column in new[] { "Tab INTEGER NOT NULL DEFAULT 1", "Field INTEGER NOT NULL DEFAULT 0", "Details BLOB" })
            {
                try
                {
                    var add = world.CreateCommand();
                    add.CommandText = $"ALTER TABLE GuildLog ADD COLUMN {column};";
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

        internal static SqliteConnection Open()
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
            // The founding's line in the capture: the guild's first chest tab.
            WriteLog(conexion, id, ms, Network.GuildLogProtocol.ChestTabUnlocked, Array.Empty<byte>(), 0, "");

            return new Guild
            {
                Id = id, Name = name, Level = 1,
                EmblemSymbol = symbol, EmblemSymbolColor = symbolColor,
                EmblemBackground = background, EmblemSymbolRgb = symbolRgb,
                FoundedUtc = founded,
            };
        }

        /// <summary>A guild's new name, with a potion of the guild's. Returns it as it stands now.</summary>
        public static Guild Rename(long guildId, string name)
        {
            using var conexion = Open();
            var update = conexion.CreateCommand();
            update.CommandText = "UPDATE Guilds SET Name = $n WHERE Id = $g;";
            update.Parameters.AddWithValue("$n", name);
            update.Parameters.AddWithValue("$g", guildId);
            update.ExecuteNonQuery();
            return ById(guildId);
        }

        /// <summary>A guild's new emblem, the four numbers of the jjg's. Returns it as it stands now.</summary>
        public static Guild SetEmblem(long guildId, int symbol, int symbolColor, int background, int symbolRgb)
        {
            using var conexion = Open();
            var update = conexion.CreateCommand();
            update.CommandText = @"UPDATE Guilds SET EmblemSymbol = $sym, EmblemSymbolColor = $symc,
                                   EmblemBackground = $bg, EmblemSymbolRgb = $symrgb WHERE Id = $g;";
            update.Parameters.AddWithValue("$sym", symbol);
            update.Parameters.AddWithValue("$symc", symbolColor);
            update.Parameters.AddWithValue("$bg", background);
            update.Parameters.AddWithValue("$symrgb", symbolRgb);
            update.Parameters.AddWithValue("$g", guildId);
            update.ExecuteNonQuery();
            return ById(guildId);
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

        /// <summary>A guild by its id, or null.</summary>
        public static Guild ById(long guildId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT Id, Name, Level, Experience, EmblemSymbol, EmblemSymbolColor,
                       EmblemBackground, EmblemSymbolRgb, FoundedUtc, GuildKamas
                FROM Guilds WHERE Id = $g;";
            query.Parameters.AddWithValue("$g", guildId);
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

        /// <summary>Guild right 43 of the client's data: "Administrar las raids".</summary>
        public const int ManageRaidsRight = 43;

        /// <summary>Guild right 6 of the client's data: "Gestionar las candidaturas e invitaciones".</summary>
        public const int ManageApplicationsRight = 6;

        /// <summary>Guild right 13 of the client's data: "Excluir a un miembro".</summary>
        public const int ExpelRight = 13;

        /// <summary>Guild right 26 of the client's data: "Atribuir rangos inferiores al mío".</summary>
        public const int AssignRanksRight = 26;

        /// <summary>
        /// How long a member has to have been in the guild before his rank can change: the "24" the
        /// real server's lqn 740 carries.
        /// </summary>
        public const int RankChangeMinimumHours = 24;

        /// <summary>
        /// Whether a member's rank carries a right of the client's guild rights table. Rank 1, the
        /// guild master, has them all; the rest have what their rank's list says, which is the list
        /// of right ids as the client sends it, decoded as packed int32s rather than bytes.
        /// </summary>
        public static bool HasRight(long characterId, int right)
        {
            var guild = GuildOf(characterId);
            if (guild == null) return false;
            int rank = RankOf(characterId);
            if (rank == 1) return true;
            var rights = Ranks(guild.Id).Find(r => r.Id == rank)?.Rights;
            return GuildRankRights.Decode(rights).Contains(right);
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
            string name = character?.Name ?? "";
            WriteLog(conexion, guildId, ms, Network.GuildLogProtocol.PlayerFlow,
                     Network.GuildLogProtocol.PlayerFlowLine(characterId, name, Network.GuildLogProtocol.Flow.Joined), characterId, name);
            return new Member { CharacterId = characterId, GuildId = guildId, Rank = rank, JoinedUtcMs = ms };
        }

        /// <summary>A single member, or null.</summary>
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
            return checked(Convert.ToInt32(query.ExecuteScalar()) * ContributionGuildKamas);
        }

        // ─── Ranks ──────────────────────────────────────────────────────────────

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
            // Validate the whole list before persisting it; a malformed suffix must
            // never turn into a partial permission set on a later read.
            GuildRankRights.Decode(rank.Rights);
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

        /// <summary>
        /// A rank deleted: its members go to <paramref name="replacementId"/> and the ranks below it
        /// go up one. Never the leader's, nor one without the flag the client draws as removable
        /// (the usual first and fourth). The members moved, or null when it may not be deleted.
        /// </summary>
        public static List<long> DeleteRank(long guildId, int rankId, int replacementId)
        {
            var ranks = Ranks(guildId);
            var gone = ranks.Find(r => r.Id == rankId);
            if (gone == null || rankId == RankLeader || !gone.Flag || rankId == replacementId
                || replacementId == RankLeader || ranks.Find(r => r.Id == replacementId) == null)
                return null;

            using var conexion = Open();
            var moved = new List<long>();
            using (var read = conexion.CreateCommand())
            {
                read.CommandText = "SELECT CharacterId FROM GuildMembers WHERE GuildId = $g AND Rank = $r;";
                read.Parameters.AddWithValue("$g", guildId);
                read.Parameters.AddWithValue("$r", rankId);
                using var lector = read.ExecuteReader();
                while (lector.Read()) moved.Add(lector.GetInt64(0));
            }
            using (var write = conexion.CreateCommand())
            {
                write.CommandText = "UPDATE GuildMembers SET Rank = $to WHERE GuildId = $g AND Rank = $r; " +
                                    "DELETE FROM GuildRanks WHERE GuildId = $g AND RankId = $r;";
                write.Parameters.AddWithValue("$to", replacementId);
                write.Parameters.AddWithValue("$g", guildId);
                write.Parameters.AddWithValue("$r", rankId);
                write.ExecuteNonQuery();
            }
            foreach (var rank in ranks)
            {
                if (rank.Id == rankId || rank.Order <= gone.Order) continue;
                rank.Order--;
                SaveRank(conexion, rank);
            }
            return moved;
        }

        // ─── The notices ────────────────────────────────────────────────────────

        /// <summary>A guild's notice, or null when nobody has written it.</summary>
        public static Notice NoticeOf(long guildId, NoticeKind kind)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT Text, WhenMs, AuthorId, AuthorName FROM GuildNotices WHERE GuildId = $g AND Kind = $k;";
            query.Parameters.AddWithValue("$g", guildId);
            query.Parameters.AddWithValue("$k", (int)kind);
            using var lector = query.ExecuteReader();
            return lector.Read() ? new Notice(lector.GetString(0), lector.GetInt64(1), lector.GetInt64(2), lector.GetString(3)) : null;
        }

        /// <summary>Writes a guild's notice. The notice as written.</summary>
        public static Notice SetNotice(long guildId, NoticeKind kind, string text, long authorId, string authorName)
        {
            var notice = new Notice(text ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), authorId, authorName ?? "");
            using var conexion = Open();
            var upsert = conexion.CreateCommand();
            upsert.CommandText = @"
                INSERT INTO GuildNotices (GuildId, Kind, Text, WhenMs, AuthorId, AuthorName) VALUES ($g, $k, $t, $ms, $a, $n)
                ON CONFLICT(GuildId, Kind) DO UPDATE SET Text = $t, WhenMs = $ms, AuthorId = $a, AuthorName = $n;";
            upsert.Parameters.AddWithValue("$g", guildId);
            upsert.Parameters.AddWithValue("$k", (int)kind);
            upsert.Parameters.AddWithValue("$t", notice.Text);
            upsert.Parameters.AddWithValue("$ms", notice.WhenMs);
            upsert.Parameters.AddWithValue("$a", authorId);
            upsert.Parameters.AddWithValue("$n", notice.AuthorName);
            upsert.ExecuteNonQuery();
            return notice;
        }

        // ─── The journal ────────────────────────────────────────────────────────

        private static void WriteLog(SqliteConnection conexion, long guildId, long whenMs, int field, byte[] details,
                                     long characterId, string name)
        {
            var insert = conexion.CreateCommand();
            insert.CommandText = "INSERT INTO GuildLog (GuildId, WhenMs, Kind, CharacterId, Name, Tab, Field, Details) " +
                                 "VALUES ($g, $ms, $f, $c, $n, $t, $f, $d);";
            insert.Parameters.AddWithValue("$g", guildId);
            insert.Parameters.AddWithValue("$ms", whenMs);
            insert.Parameters.AddWithValue("$f", field);
            insert.Parameters.AddWithValue("$c", characterId);
            insert.Parameters.AddWithValue("$n", name ?? "");
            insert.Parameters.AddWithValue("$t", (int)Network.GuildLogProtocol.TabOf(field));
            insert.Parameters.AddWithValue("$d", details ?? Array.Empty<byte>());
            insert.ExecuteNonQuery();
        }

        /// <summary>
        /// A line of the journal: its case and the case's message (see <see cref="Network.GuildLogProtocol"/>),
        /// and who did it, when somebody did. Its list follows from its case.
        /// </summary>
        public static void Log(long guildId, int field, byte[] details, long characterId = 0, string name = "")
        {
            if (guildId <= 0) return;
            try
            {
                using var conexion = Open();
                WriteLog(conexion, guildId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), field, details, characterId, name);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Guild] A journal line of guild {guildId} could not be written: {ex.Message}");
            }
        }

        /// <summary>A guild's journal, from oldest to newest, as the jil sends it.</summary>
        public static List<LogEntry> LogOf(long guildId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT WhenMs, Kind, CharacterId, Name, Tab, Field, Details FROM GuildLog " +
                                "WHERE GuildId = $g ORDER BY WhenMs, Id;";
            query.Parameters.AddWithValue("$g", guildId);
            using var lector = query.ExecuteReader();
            var fuera = new List<LogEntry>();
            while (lector.Read())
            {
                long character = lector.GetInt64(2);
                string name = lector.GetString(3);
                int field = lector.GetInt32(5);
                byte[] details = lector.IsDBNull(6) ? Array.Empty<byte>() : (byte[])lector[6];
                var tab = (LogTab)lector.GetInt32(4);
                if (field == 0)
                {
                    // A line written before the journal had its cases.
                    int kind = lector.GetInt32(1);
                    field = kind == OldLogFounded ? Network.GuildLogProtocol.ChestTabUnlocked : Network.GuildLogProtocol.PlayerFlow;
                    details = kind == OldLogFounded ? Array.Empty<byte>()
                        : Network.GuildLogProtocol.PlayerFlowLine(character, name,
                            kind == OldLogJoined ? Network.GuildLogProtocol.Flow.Joined : (Network.GuildLogProtocol.Flow)kind);
                    tab = LogTab.General;
                }
                fuera.Add(new LogEntry
                {
                    GuildId = guildId, WhenMs = lector.GetInt64(0), Tab = tab, Field = field, Details = details,
                    CharacterId = character, Name = name,
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

        // ─── Applications ───────────────────────────────────────────────────────

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

        /// <summary>The applications a character has sent, to whichever guilds.</summary>
        public static List<Application> ApplicationsBy(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT GuildId, CharacterId, Message, WhenMs FROM GuildApplications
                WHERE CharacterId = $c ORDER BY WhenMs;";
            query.Parameters.AddWithValue("$c", characterId);
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
            => Network.GuildProtocol.NextWeeklyReset(when.UtcDateTime).AddDays(-7).ToString("yyyy-MM-dd");

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

            /// <summary>How long the best raid took, in milliseconds.</summary>
            public long DurationMs;
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
        public static long RecordRaidScore(long guildId, int raidId, long score, DateTimeOffset when, long durationMs = 0)
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
                INSERT INTO GuildRaidScores (GuildId, RaidId, Week, Score, Runs, BestUtcMs, BestDurationMs)
                VALUES ($g, $r, $w, $s, 1, $ms, $d)
                ON CONFLICT(GuildId, RaidId, Week) DO UPDATE SET
                    Runs = Runs + 1,
                    BestUtcMs = CASE WHEN $s > Score THEN $ms ELSE BestUtcMs END,
                    BestDurationMs = CASE WHEN $s > Score THEN $d ELSE BestDurationMs END,
                    Score = CASE WHEN $s > Score THEN $s ELSE Score END;";
            apunta.Parameters.AddWithValue("$g", guildId);
            apunta.Parameters.AddWithValue("$r", raidId);
            apunta.Parameters.AddWithValue("$w", week);
            apunta.Parameters.AddWithValue("$s", score);
            apunta.Parameters.AddWithValue("$ms", when.ToUnixTimeMilliseconds());
            apunta.Parameters.AddWithValue("$d", durationMs);
            apunta.ExecuteNonQuery();

            return better ? score : best;
        }

        /// <summary>The guild's best score in a raid this week, or 0.</summary>
        public static long BestRaidScore(long guildId, int raidId, DateTimeOffset when)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT Score FROM GuildRaidScores WHERE GuildId = $g AND RaidId = $r AND Week = $w;";
            query.Parameters.AddWithValue("$g", guildId);
            query.Parameters.AddWithValue("$r", raidId);
            query.Parameters.AddWithValue("$w", WeekOf(when));
            var had = query.ExecuteScalar();
            return had == null || had is DBNull ? 0 : Convert.ToInt64(had);
        }

        /// <summary>
        /// A raid's ranking in a week, from most to least.
        /// </summary>
        /// <remarks>
        /// On equal score the faster run wins -- the Sanctuary's 50,000 is a ceiling many guilds reach,
        /// and the guides' readers were told ties are broken by time --, and on equal time whoever
        /// did it first. A run whose duration was not recorded counts as the slowest.
        /// </remarks>
        public static List<LadderRow> Ladder(int raidId, DateTimeOffset when, int most = 20)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = @"
                SELECT s.GuildId, g.Name, s.Score, s.Runs, s.BestDurationMs
                FROM GuildRaidScores s LEFT JOIN Guilds g ON g.Id = s.GuildId
                WHERE s.RaidId = $r AND s.Week = $w AND s.Score > 0
                ORDER BY s.Score DESC,
                         CASE WHEN s.BestDurationMs > 0 THEN s.BestDurationMs ELSE 9223372036854775807 END ASC,
                         s.BestUtcMs ASC
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
                    DurationMs = lector.GetInt64(4),
                });
            }

            return rows;
        }

        /// <summary>A guild's position this week in a raid, or zero if it is not there.</summary>
        public static int PlaceOf(long guildId, int raidId, DateTimeOffset when)
            => LadderRowOf(guildId, raidId, when)?.Place ?? 0;

        /// <summary>A guild's line in a raid's ranking of a week, or null when it did not score.</summary>
        public static LadderRow LadderRowOf(long guildId, int raidId, DateTimeOffset when)
            => Ladder(raidId, when, int.MaxValue).FirstOrDefault(row => row.GuildId == guildId);

        /// <summary>How many guilds scored in a raid in a week: what the ladder's percentages are of.</summary>
        public static int LadderCount(int raidId, DateTimeOffset when)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM GuildRaidScores WHERE RaidId = $r AND Week = $w AND Score > 0;";
            query.Parameters.AddWithValue("$r", raidId);
            query.Parameters.AddWithValue("$w", WeekOf(when));
            return Convert.ToInt32(query.ExecuteScalar());
        }

        /// <summary>
        /// Adds experience to a guild, and the level follows it (<see cref="GuildLevels"/>).
        /// Returns the guild's level after it.
        /// </summary>
        public static int AddGuildExperience(long guildId, long amount)
        {
            using var conexion = Open();
            int before;
            using (var was = conexion.CreateCommand())
            {
                was.CommandText = "SELECT Level FROM Guilds WHERE Id = $g;";
                was.Parameters.AddWithValue("$g", guildId);
                before = was.ExecuteScalar() is long l ? (int)l : 1;
            }
            if (amount > 0)
            {
                var add = conexion.CreateCommand();
                add.CommandText = "UPDATE Guilds SET Experience = Experience + $x WHERE Id = $g;";
                add.Parameters.AddWithValue("$x", amount);
                add.Parameters.AddWithValue("$g", guildId);
                add.ExecuteNonQuery();
            }
            var read = conexion.CreateCommand();
            read.CommandText = "SELECT Experience FROM Guilds WHERE Id = $g;";
            read.Parameters.AddWithValue("$g", guildId);
            var experience = read.ExecuteScalar();
            if (experience == null || experience is DBNull) return 0;
            int level = GuildLevels.LevelFor(Convert.ToInt64(experience));
            var set = conexion.CreateCommand();
            set.CommandText = "UPDATE Guilds SET Level = $l WHERE Id = $g AND Level < $l;";
            set.Parameters.AddWithValue("$l", level);
            set.Parameters.AddWithValue("$g", guildId);
            set.ExecuteNonQuery();

            // The journal: each level reached, and the chest tab one of them unlocks.
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int reached = before + 1; reached <= level; reached++)
            {
                WriteLog(conexion, guildId, ms, Network.GuildLogProtocol.LevelUp, Network.GuildLogProtocol.LevelUpLine(reached), 0, "");
                if (GuildChests.TabsAtLevel(reached) > GuildChests.TabsAtLevel(reached - 1))
                    WriteLog(conexion, guildId, ms, Network.GuildLogProtocol.ChestTabUnlocked, Array.Empty<byte>(), 0, "");
            }
            return level;
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

            string name = DatabaseManager.GetCharacterById(characterId)?.Name ?? "";
            WriteLog(conexion, guild.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Network.GuildLogProtocol.PlayerFlow,
                     Network.GuildLogProtocol.PlayerFlowLine(characterId, name, Network.GuildLogProtocol.Flow.Left), characterId, name);

            // His account's houses are not the guild's any more: 1/277 tells the guild so.
            long account = DatabaseManager.GetCharacterById(characterId)?.AccountId ?? 0;
            if (account > 0) HouseStore.StopSharing(guild.Id, account);
            return guild;
        }
    }
}
