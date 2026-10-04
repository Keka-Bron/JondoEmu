using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los gremios y quién está en cada uno, guardados en world.db.
    ///
    /// Es la base sobre la que se apoya todo lo demás del gremio -la ventana, la tienda, el
    /// cofre y, más adelante, las raids-, así que va aquí y no en el aire: un gremio creado
    /// sobrevive al reinicio del servidor.
    ///
    /// Lo que sabemos de una captura y lo que no: el nombre, el emblema, el nivel, la fecha de
    /// fundación y quién pertenece están medidos en las 12 capturas de Gremio/. La experiencia,
    /// los kamas de gremio, los guildatones y los permisos por rango salen en las tramas pero su
    /// escala completa no se ha reconstruido; se guardan los que se ven y el resto arranca a cero.
    /// </summary>
    public static class GuildStore
    {
        /// <summary>Un gremio: lo que lo identifica y lo que el cliente pinta de él.</summary>
        public sealed class Guild
        {
            public long Id { get; init; }
            public string Name { get; init; } = "";
            public int Level { get; set; } = 1;
            public long Experience { get; set; }

            /// <summary>
            /// El emblema, los cuatro números del <c>jjg</c> de creación: el símbolo, el índice
            /// de color del símbolo, el color de fondo (RGB) y el color del símbolo (RGB).
            /// </summary>
            public int EmblemSymbol { get; init; }
            public int EmblemSymbolColor { get; init; }
            public int EmblemBackground { get; init; }
            public int EmblemSymbolRgb { get; init; }

            /// <summary>La fecha de fundación, en ISO-8601 con Z, tal y como viaja en el <c>jhh</c>.</summary>
            public string FoundedUtc { get; init; } = "";

            /// <summary>Los kamas de gremio que quedan por gastar (contribuciones menos compras).</summary>
            public long GuildKamas { get; set; }
        }

        /// <summary>Un miembro: su personaje, su rango, cuándo entró y la nota que le puso el jefe.</summary>
        public sealed class Member
        {
            public long CharacterId { get; init; }
            public long GuildId { get; init; }
            public int Rank { get; set; } = 1;
            public long JoinedUtcMs { get; init; }
            public long Experience { get; set; }

            /// <summary>La nota de la columna «Nota» y cuándo se escribió. Medido en el jgz de «hola».</summary>
            public string Note { get; set; } = "";
            public long NoteMs { get; set; }
        }

        /// <summary>
        /// Un rango del gremio, tal como viaja en el jco: nombre, permisos, icono y orden.
        /// </summary>
        /// <remarks>
        /// Los permisos son dos cosas que el cliente manda y el servidor devuelve sin cambiarlas:
        /// una lista empaquetada de números (el f3.f3) y una marca (el f3.f1, 1 en todos los
        /// rangos salvo el del jefe y el de los recién llegados). Qué permiso es cada número no se
        /// ha reconstruido y no hace falta para guardarlos: se devuelven como llegaron.
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

        /// <summary>Una línea del diario del gremio (jil).</summary>
        public sealed class LogEntry
        {
            public long GuildId { get; init; }
            public long WhenMs { get; init; }

            /// <summary>0 la fundación, 1 alguien entra, 2 alguien pasa por algo con f4 = 2 (medido, no entendido).</summary>
            public int Kind { get; init; }
            public long CharacterId { get; init; }
            public string Name { get; init; } = "";
        }

        public const int LogFounded = 0;
        public const int LogJoined = 1;

        /// <summary>
        /// La ficha pública del gremio, la del anuario: lo que el jefe escribe con el jcc y lo que
        /// el jci devuelve. Los campos se guardan como llegan; el f1 es cuándo se escribió y el f8
        /// el nombre del jefe, que pone el servidor.
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
        /// El nivel máximo de miembros por nivel de gremio. Medido en dos puntos -un gremio de
        /// nivel 1 dice 50 (jhh f9=50) y uno de nivel 7 dice 410-; entre ellos es una inferencia
        /// y va dicha como tal. El resto de la curva no está medido, así que fuera de esos dos
        /// niveles se devuelve el más cercano conocido.
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

            // Las notas de los miembros, en la tabla que ya existía.
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
                    // Ya estaba.
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

        /// <summary>Crea el gremio con su primer miembro -el fundador- de rango 1, y lo devuelve.</summary>
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

        /// <summary>El gremio de un personaje, o null si no tiene.</summary>
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

        /// <summary>Un gremio por su nombre, sin distinguir mayúsculas. Null si no hay ninguno así.</summary>
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

        /// <summary>El puesto de un personaje en su gremio, o cero si no está en ninguno.</summary>
        public static int RankOf(long characterId)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT Rank FROM GuildMembers WHERE CharacterId = $c;";
            query.Parameters.AddWithValue("$c", characterId);
            var value = query.ExecuteScalar();
            return value == null || value is DBNull ? 0 : Convert.ToInt32(value);
        }

        /// <summary>Los miembros de un gremio, por su personaje y su rango.</summary>
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
        /// Mete a un personaje en un gremio con el rango que se le dé. El rango 4 es el de los
        /// que acaban de entrar: es el que lleva en la lista de miembros el que entró por
        /// candidatura en la captura de crear «Jondo».
        /// </summary>
        public const int RankNewcomer = 4;

        /// <summary>El rango 1 es el del jefe: el que lleva el fundador en el jgw y el jgu de la captura.</summary>
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

        /// <summary>La nota que el jefe le pone a un miembro (jjj). Devuelve el miembro puesto al día.</summary>
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

        /// <summary>Cambia el rango de un miembro. Devuelve el miembro puesto al día, o null si no está.</summary>
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
        /// Las gremichas de un miembro: lo que ha contribuido, en kamas de gremio. Es lo que el
        /// f7.f2 del jgu enseña -10 tras una contribución, 20 tras dos- y la columna «Gremichas».
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
        /// Los cuatro rangos con los que nace un gremio, los del jco de la captura de crear
        /// «Jondo»: nombres por clave de traducción, permisos como llegaron, iconos 116, 115, 114
        /// y 117, y el orden 0 a 3.
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

        /// <summary>Los rangos de un gremio, en el orden en que se enseñan. Los de siempre si no tiene ninguno guardado.</summary>
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

            // Un gremio de antes de que se guardaran: se le ponen los de siempre.
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
        /// Un rango nuevo (jcv): con el nombre y el icono que se le dan, en el orden que se pide,
        /// y los que estaban de ahí para abajo se corren uno. El id es el siguiente libre y la
        /// marca va puesta, que es como nació «Rango personalizado» en la captura: f3 { f1: 1 }.
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

        // ─── El diario ──────────────────────────────────────────────────────────

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

        /// <summary>El diario de un gremio, de lo más viejo a lo más nuevo, como lo manda el jil.</summary>
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

        // ─── La ficha pública ───────────────────────────────────────────────────

        /// <summary>La ficha del anuario de un gremio, o null si nadie la ha escrito.</summary>
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

        /// <summary>Todos los gremios, para el anuario.</summary>
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

        /// <summary>El jefe de un gremio: el de rango 1, o el primero que haya.</summary>
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

        /// <summary>Una candidatura: quién la manda, a qué gremio, con qué texto y cuándo.</summary>
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

        // ─── Kamas de gremio, contribuciones y tienda ───────────────────────────

        /// <summary>
        /// Lo que una contribución mueve: 10.000 kamas del personaje por 10 de gremio, y cinco
        /// como mucho por semana. Medido en «contribuir en el gremio»: el jle dice 10.000 y los
        /// kamas de gremio suben de 10 a 20; el contador de las que quedan bajó de 4 a 3, o sea
        /// que la quinta era la última.
        /// </summary>
        public const int ContributionKamas = 10000;
        public const int ContributionGuildKamas = 10;
        public const int ContributionsPerWeek = 5;

        /// <summary>El martes en que empieza la semana, que es cuando el juego reinicia lo semanal.</summary>
        public static string WeekOf(DateTimeOffset when)
        {
            int back = ((int)when.UtcDateTime.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
            return when.UtcDateTime.Date.AddDays(-back).ToString("yyyy-MM-dd");
        }

        /// <summary>Cuántas contribuciones le quedan esta semana a un personaje.</summary>
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
        /// Apunta una contribución y le suma al gremio sus kamas. Devuelve cuántas le quedan al
        /// personaje esta semana, o menos uno si ya no le quedaba ninguna.
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

        /// <summary>Gasta kamas de gremio. Falso -y no gasta nada- cuando no llegan.</summary>
        public static bool SpendGuildKamas(long guildId, long amount)
        {
            using var conexion = Open();
            var gasta = conexion.CreateCommand();
            gasta.CommandText = "UPDATE Guilds SET GuildKamas = GuildKamas - $k WHERE Id = $g AND GuildKamas >= $k;";
            gasta.Parameters.AddWithValue("$k", amount);
            gasta.Parameters.AddWithValue("$g", guildId);
            return gasta.ExecuteNonQuery() > 0;
        }

        /// <summary>Apunta un oráculo comprado, con el plazo que tienen los miembros para activarlo.</summary>
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

        /// <summary>El plazo de un oráculo comprado, o null si el gremio no lo tiene.</summary>
        public static string OracleDeadline(long guildId, int oracle)
        {
            using var conexion = Open();
            var query = conexion.CreateCommand();
            query.CommandText = "SELECT DeadlineUtc FROM GuildOracles WHERE GuildId = $g AND Oracle = $o;";
            query.Parameters.AddWithValue("$g", guildId);
            query.Parameters.AddWithValue("$o", oracle);
            return query.ExecuteScalar() as string;
        }

        // ─── Las raids compradas ────────────────────────────────────────────────

        /// <summary>Apunta una raid comprada por el gremio, a la espera de lanzarla.</summary>
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

        /// <summary>Las raids que el gremio tiene compradas y sin gastar.</summary>
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

        /// <summary>Se gasta al lanzarla: una raid comprada es un uso, no una llave permanente.</summary>
        public static void DropRaid(long guildId, int raidId)
        {
            using var conexion = Open();
            var borra = conexion.CreateCommand();
            borra.CommandText = "DELETE FROM GuildOwnedRaids WHERE GuildId = $g AND RaidId = $r;";
            borra.Parameters.AddWithValue("$g", guildId);
            borra.Parameters.AddWithValue("$r", raidId);
            borra.ExecuteNonQuery();
        }

        // ─── La clasificación semanal ───────────────────────────────────────────

        /// <summary>Una fila de la clasificación: un gremio, su mejor puntuación de la semana.</summary>
        public sealed class LadderRow
        {
            public long GuildId;
            public string Name = "";
            public long Score;

            /// <summary>Cuántas raids de ésta ha terminado el gremio esta semana.</summary>
            public int Runs;

            /// <summary>El puesto, contando desde uno.</summary>
            public int Place;
        }

        /// <summary>
        /// Apunta lo que ha sacado un gremio en una raid. Se queda con la MEJOR de la semana.
        /// </summary>
        /// <remarks>
        /// La mejor y no la suma, y es una decisión nuestra que conviene decir: la página del juego
        /// habla de una clasificación global en la que «los mejores podrán representar con orgullo a
        /// su gremio», y sumar premiaría al gremio que más veces entra antes que al que mejor lo
        /// hace. Ningún dato del cliente dice cuál de las dos es. Las veces que ha entrado se
        /// apuntan igualmente, que es lo que hace falta para cambiar de idea sin perder nada.
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
        /// La clasificación de una raid en una semana, de más a menos.
        /// </summary>
        /// <remarks>
        /// A igual puntuación manda quien la hizo antes, que es lo que hacen todas las tablas de
        /// este juego y lo único que deja un orden estable: sin eso, dos gremios empatados se
        /// intercambiarían el puesto cada vez que se pinta la lista.
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

        /// <summary>El puesto de un gremio esta semana en una raid, o cero si no está.</summary>
        public static int PlaceOf(long guildId, int raidId, DateTimeOffset when)
        {
            foreach (var row in Ladder(raidId, when, int.MaxValue))
            {
                if (row.GuildId == guildId) return row.Place;
            }

            return 0;
        }

        /// <summary>Saca a un personaje de su gremio. Devuelve el gremio que dejó, o null si no tenía.</summary>
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
