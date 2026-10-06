using Jondo.Unity.Launcher;
using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Google.Protobuf;
using Jondo.Unity.Protocol.Messages;

namespace Jondo.Unity.Server
{
    public static class DatabaseManager
    {
        private static readonly string AuthConnectionString = Paths.AuthConnectionString;
        public static readonly string WorldConnectionString = Paths.WorldConnectionString;

        public static void Initialize()
        {
            Console.WriteLine("[SQLite] Initializing databases...");

            // 1. Repair world.db before anything else touches it.
            //
            // This block was AFTER the backup, and that order broke the self-repair that
            // already worked: DatabaseBackups.CreateBeforeMigration() opens both databases to
            // verify the copy, so with a corrupt, truncated or half-extracted world.db it blew
            // up and aborted the whole Initialize() -- and the server, which until then healed
            // itself by taking it out of datos/world.zip again, stopped starting --.
            //
            // First repair, then copy, and then migrate. That way the copy is made of a healthy
            // database and still comes before every migration, which is what the PR wanted.
            string dbPath = Paths.WorldDb;
            bool needsExtraction = !File.Exists(dbPath);
            if (!needsExtraction)
            {
                try
                {
                    using (var checkConn = new SqliteConnection(WorldConnectionString))
                    {
                        checkConn.Open();
                        using var checkCmd = checkConn.CreateCommand();
                        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ItemTemplates';";
                        long hasItemTemplates = Convert.ToInt64(checkCmd.ExecuteScalar() ?? 0L);
                        if (hasItemTemplates == 0) needsExtraction = true;
                        checkConn.Close();
                    }
                    SqliteConnection.ClearAllPools();
                }
                catch { needsExtraction = true; }
            }

            if (needsExtraction)
            {
                string zipPath = Paths.WorldZip;
                if (File.Exists(zipPath))
                {
                    try
                    {
                        SqliteConnection.ClearAllPools();
                        Console.WriteLine("[SQLite] Auto-extracting full world.db from world.zip...");
                        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, Path.GetDirectoryName(Paths.WorldDb)!, true);
                        Console.WriteLine("[SQLite] Successfully extracted world.db from world.zip.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SQLite] Zip extraction skipped (file in use or active): {ex.Message}");
                    }
                }
            }

            // 2. And only now the backup, with both databases sound and before a single migration.
            DatabaseBackups.CreateBeforeMigration();

            // 3. Initialize auth.db
            using (var authConnection = new SqliteConnection(AuthConnectionString))
            {
                authConnection.Open();

                using (var pragmaCmd = authConnection.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                    pragmaCmd.ExecuteNonQuery();
                }

                var createAccounts = authConnection.CreateCommand();
                createAccounts.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Accounts (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Login TEXT NOT NULL UNIQUE,
                        Password TEXT NOT NULL,
                        Nickname TEXT NOT NULL,
                        GameToken TEXT,
                        Role INTEGER NOT NULL DEFAULT 1
                    );
                ";
                createAccounts.ExecuteNonQuery();

                // The Role column, for databases that already existed before there were roles.
                //
                // SQLite has no ADD COLUMN IF NOT EXISTS, so the table is looked at first. Without
                // this, whoever already had his auth.db could not sign in: every account query
                // already asks for the column.
                bool tieneRol = false;
                var mirar = authConnection.CreateCommand();
                mirar.CommandText = "PRAGMA table_info(Accounts);";
                using (var lector = mirar.ExecuteReader())
                {
                    while (lector.Read())
                    {
                        if (string.Equals(lector.GetString(1), "Role", StringComparison.OrdinalIgnoreCase))
                        {
                            tieneRol = true;
                            break;
                        }
                    }
                }
                if (!tieneRol)
                {
                    var anadir = authConnection.CreateCommand();
                    anadir.CommandText = "ALTER TABLE Accounts ADD COLUMN Role INTEGER NOT NULL DEFAULT 1;";
                    anadir.ExecuteNonQuery();
                    Console.WriteLine("[DatabaseManager] Columna Role añadida a Accounts; todos empiezan como jugador.");
                }

                // The role scale stopped at 4, which meant administrator. Giny and Dofus's rights
                // criteria use the whole scale from 1 to 5: 3 is padawan, 4 game master and 5
                // administrator.
                //
                // Renumbering changes what TWO values mean, so both have to be moved. 4 becomes 5 --
                // those who were administrators still are -- and 3 becomes 4, because whoever was game
                // master with 3 would otherwise be left as padawan. The order matters: first 4 and then
                // 3, or those going up from 3 would go up again.
                //
                // And it is done ONCE, noted in JondoMigrations. A loose UPDATE on every start would
                // silently promote every future game master to administrator.
                var createMigrations = authConnection.CreateCommand();
                createMigrations.CommandText = @"
                    CREATE TABLE IF NOT EXISTS JondoMigrations (
                        Name TEXT PRIMARY KEY,
                        AppliedAt TEXT NOT NULL
                    );
                ";
                createMigrations.ExecuteNonQuery();

                const string roleScaleMigration = "roles-giny-1-to-5";
                var migrationDone = authConnection.CreateCommand();
                migrationDone.CommandText = "SELECT 1 FROM JondoMigrations WHERE Name = $name LIMIT 1;";
                migrationDone.Parameters.AddWithValue("$name", roleScaleMigration);
                if (migrationDone.ExecuteScalar() == null)
                {
                    using var transaction = authConnection.BeginTransaction();

                    var migrarAdministradores = authConnection.CreateCommand();
                    migrarAdministradores.Transaction = transaction;
                    migrarAdministradores.CommandText =
                        "UPDATE Accounts SET Role = $admin WHERE Role = 4;";
                    migrarAdministradores.Parameters.AddWithValue("$admin", Roles.Administrador);
                    int administradores = migrarAdministradores.ExecuteNonQuery();

                    var migrarGameMasters = authConnection.CreateCommand();
                    migrarGameMasters.Transaction = transaction;
                    migrarGameMasters.CommandText =
                        "UPDATE Accounts SET Role = $gm WHERE Role = 3;";
                    migrarGameMasters.Parameters.AddWithValue("$gm", Roles.GameMaster);
                    int gameMasters = migrarGameMasters.ExecuteNonQuery();

                    var rememberMigration = authConnection.CreateCommand();
                    rememberMigration.Transaction = transaction;
                    rememberMigration.CommandText =
                        "INSERT INTO JondoMigrations (Name, AppliedAt) VALUES ($name, $when);";
                    rememberMigration.Parameters.AddWithValue("$name", roleScaleMigration);
                    rememberMigration.Parameters.AddWithValue("$when", DateTimeOffset.UtcNow.ToString("O"));
                    rememberMigration.ExecuteNonQuery();

                    transaction.Commit();
                    Console.WriteLine($"[DatabaseManager] Escala de roles 1..5 aplicada: " +
                                      $"{administradores} administrador(es) de 4 a 5 y " +
                                      $"{gameMasters} game master(s) de 3 a 4.");
                }

                // The LAUNCHER's session, which until now was the same token as the game's.
                //
                // And that broke by itself: on starting a client, the Zaap and the HAAPI give the
                // account a new GameToken, so the one the launcher had stored stopped being valid.
                // The next time the launcher opened with the account set, the server no longer
                // recognised its token, and on pressing play «the server does not respond» came up --
                // which was a lie too, the server answered perfectly well that the session was not
                // valid --. With a column of its own, rotating the game's no longer touches the launcher's.
                bool tieneSesion = false;
                var mirarSesion = authConnection.CreateCommand();
                mirarSesion.CommandText = "PRAGMA table_info(Accounts);";
                using (var lector = mirarSesion.ExecuteReader())
                {
                    while (lector.Read())
                    {
                        if (string.Equals(lector.GetString(1), "LauncherToken", StringComparison.OrdinalIgnoreCase))
                        {
                            tieneSesion = true;
                            break;
                        }
                    }
                }
                if (!tieneSesion)
                {
                    var anadir = authConnection.CreateCommand();
                    anadir.CommandText = "ALTER TABLE Accounts ADD COLUMN LauncherToken TEXT;";
                    anadir.ExecuteNonQuery();
                    Console.WriteLine("[DatabaseManager] Columna LauncherToken añadida a Accounts.");
                }

                // Until when each account is subscribed. It did not exist before: the date was worked
                // out on the fly, the same for all, and that was the same as having the data written in
                // the code. With a column of its own, one account can expire and another not, which is
                // what the client expects to be able to tell apart.
                //
                // It is stored in the format that travels -- ISO 8601 with a numeric offset, never with
                // Z -- so that what is in the database is exactly what is sent and there is no
                // conversion in between to lose it in.
                bool tieneAbono = false;
                var mirarAbono = authConnection.CreateCommand();
                mirarAbono.CommandText = "PRAGMA table_info(Accounts);";
                using (var lector = mirarAbono.ExecuteReader())
                {
                    while (lector.Read())
                    {
                        if (string.Equals(lector.GetString(1), "SubscriptionEnd", StringComparison.OrdinalIgnoreCase))
                        {
                            tieneAbono = true;
                            break;
                        }
                    }
                }
                if (!tieneAbono)
                {
                    var anadir = authConnection.CreateCommand();
                    anadir.CommandText = "ALTER TABLE Accounts ADD COLUMN SubscriptionEnd TEXT;";
                    anadir.ExecuteNonQuery();
                    Console.WriteLine("[DatabaseManager] Columna SubscriptionEnd añadida a Accounts.");
                }

                // And those without a date -- the ones from before the column, and those created before
                // this start -- begin with a year. Only the empty ones: a date already written, even if
                // expired, is data and is not overwritten.
                var rellenar = authConnection.CreateCommand();
                rellenar.CommandText =
                    "UPDATE Accounts SET SubscriptionEnd = $hasta " +
                    "WHERE SubscriptionEnd IS NULL OR SubscriptionEnd = '';";
                rellenar.Parameters.AddWithValue("$hasta", Network.Subscription.DefaultEndDate());
                int puestas = rellenar.ExecuteNonQuery();
                if (puestas > 0)
                    Console.WriteLine($"[DatabaseManager] {puestas} cuenta(s) sin fecha de abono; " +
                                      $"se les pone un año.");

                // The test account, keka / test, administrator, on every installation: whoever
                // downloads the emulator signs in with it, finds keka's characters -- the ones
                // datos/world.zip carries, at their level and with their items -- and has the
                // administration commands to try it out. It is created through the same path as
                // any account -- the password hashed, the subscription written --, under the id
                // those characters belong to, and only when there is no keka yet, so an existing
                // one keeps its own password.
                //
                // DECIDED ON PURPOSE, 04/10/2026, and written down here because a review flags it
                // every time. It was taken out on 28/08/2026 because the password is published in
                // this repository: on a server reachable from outside, anybody can sign in as
                // administrator with it. Santiago brought it back: it is a test account, and
                // without it nobody who downloads the emulator can try the administration
                // commands. Whoever opens a server to the outside (JONDO_PUBLIC_BIND=1) has to
                // change that password or delete the account first; the README says so.
                if (!AccountExists(TestAccountLogin))
                {
                    if (RegisterNewAccount(TestAccountLogin, TestAccountPassword, "Keka", "", out string porQue,
                                           id: TestAccountId))
                        Console.WriteLine($"[DatabaseManager] Cuenta de prueba creada: {TestAccountLogin} / {TestAccountPassword}.");
                    else
                        Console.WriteLine($"[DatabaseManager] No se ha podido crear la cuenta de prueba: {porQue}");
                }

                // And the two who run the server are administrators: keka, and dragonlord once he
                // has signed up. In ANY installation, whoever holds one of those two logins is an
                // administrator at the next start -- the same caveat as the test account's.
                var duenos = authConnection.CreateCommand();
                duenos.CommandText = "UPDATE Accounts SET Role = $admin " +
                                     "WHERE Login IN ('keka', 'dragonlord') AND Role < $admin;";
                duenos.Parameters.AddWithValue("$admin", Roles.Administrador);
                int promovidos = duenos.ExecuteNonQuery();
                if (promovidos > 0) Console.WriteLine($"[DatabaseManager] {promovidos} cuenta(s) puestas como administrador.");
            }

            using (var worldConnection = new SqliteConnection(WorldConnectionString))
            {
                worldConnection.Open();

                using (var pragmaCmd = worldConnection.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                    pragmaCmd.ExecuteNonQuery();
                }

                var createCharacters = worldConnection.CreateCommand();
                createCharacters.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Characters (
                        Id INTEGER PRIMARY KEY,
                        AccountId INTEGER NOT NULL,
                        Name TEXT NOT NULL,
                        Breed INTEGER NOT NULL,
                        Sex INTEGER NOT NULL,
                        Level INTEGER NOT NULL DEFAULT 1,
                        MapId INTEGER NOT NULL DEFAULT 154010884,
                        CellId INTEGER NOT NULL DEFAULT 315,
                        RemainingPoints INTEGER NOT NULL DEFAULT 0,
                        Vitality INTEGER NOT NULL DEFAULT 0,
                        Wisdom INTEGER NOT NULL DEFAULT 0,
                        Strength INTEGER NOT NULL DEFAULT 0,
                        Intelligence INTEGER NOT NULL DEFAULT 0,
                        Chance INTEGER NOT NULL DEFAULT 0,
                        Agility INTEGER NOT NULL DEFAULT 0,
                        Look TEXT NOT NULL,
                        Orientation INTEGER NOT NULL DEFAULT 1,
                        Kamas INTEGER NOT NULL DEFAULT 0
                    );
                ";
                createCharacters.ExecuteNonQuery();

                // Migration: Ensure Orientation column exists
                try
                {
                    var addColCmd = worldConnection.CreateCommand();
                    addColCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Orientation INTEGER NOT NULL DEFAULT 1;";
                    addColCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Added Orientation column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Column already exists, ignore
                }

                // Migration: the character's accumulated experience column.
                try
                {
                    var addXpCmd = worldConnection.CreateCommand();
                    addXpCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Experience INTEGER NOT NULL DEFAULT 0;";
                    addXpCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Experience column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                // Migration: Ensure Kamas column exists
                try
                {
                    var addKamasCmd = worldConnection.CreateCommand();
                    addKamasCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Kamas INTEGER NOT NULL DEFAULT 0;";
                    addKamasCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Kamas column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Column already exists, ignore
                }

                // Servers table. Until now the list came from a fixed binary template, so there
                // was no way to tell which character lived on which server.
                //
                //   Type     what the protocol sends in the server list. It groups servers by
                //            category; the values come from the real capture.
                //   Status   what is advertised over HTTP, and what decides the server's colour
                //            on the selection screen.
                //   Joinable whether the server accepts connections. Checked here too, not only
                //            on the client.
                var createServers = worldConnection.CreateCommand();
                createServers.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Servers (
                        Id INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Type INTEGER NOT NULL DEFAULT 1,
                        Status INTEGER NOT NULL DEFAULT 3,
                        Joinable INTEGER NOT NULL DEFAULT 0,
                        IsDefault INTEGER NOT NULL DEFAULT 0
                    );
                ";
                createServers.ExecuteNonQuery();

                foreach (string column in new[]
                         {
                             "Type INTEGER NOT NULL DEFAULT 1",
                             "Status INTEGER NOT NULL DEFAULT 3",
                             "Joinable INTEGER NOT NULL DEFAULT 0"
                         })
                {
                    try
                    {
                        var addCmd = worldConnection.CreateCommand();
                        addCmd.CommandText = $"ALTER TABLE Servers ADD COLUMN {column};";
                        addCmd.ExecuteNonQuery();
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException)
                    {
                        // Already exists.
                    }
                }

                SeedServers(worldConnection);

                // Migration: which server each character belongs to.
                try
                {
                    var addServerCmd = worldConnection.CreateCommand();
                    addServerCmd.CommandText =
                        $"ALTER TABLE Characters ADD COLUMN ServerId INTEGER NOT NULL DEFAULT {DefaultServerId};";
                    addServerCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added ServerId column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                // Migration: last connection, shown per server on the character list.
                try
                {
                    var addLastConnCmd = worldConnection.CreateCommand();
                    addLastConnCmd.CommandText = "ALTER TABLE Characters ADD COLUMN LastConnection TEXT;";
                    addLastConnCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added LastConnection column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                // Migration: head chosen at creation. Its skin travels in the look, and a
                // character without it is drawn with no face.
                try
                {
                    var addHeadCmd = worldConnection.CreateCommand();
                    addHeadCmd.CommandText = "ALTER TABLE Characters ADD COLUMN HeadId INTEGER;";
                    addHeadCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added HeadId column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                // Migration: how big the character is, as a percentage of how big his breed is.
                // A hundred is the usual size, so the ones that already existed do not change look
                // when the column appears.
                try
                {
                    var addSizeCmd = worldConnection.CreateCommand();
                    addSizeCmd.CommandText = "ALTER TABLE Characters ADD COLUMN Size INTEGER NOT NULL DEFAULT 100;";
                    addSizeCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Size column to Characters table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Already exists.
                }

                // Migration: what the scrolls gave, apart from the points spent.
                //
                // It starts at 100 for everybody who already existed, which is this server's policy --
                // every character is created with the scrolls done -- and what the captures show of real
                // characters: each characteristic's f3 is 100 in 156 captures and 4,815 times, and 101
                // does not come up once. And then the 101 the creation put there are taken out of the
                // database, which is what left a freshly made level 200 with 183 points instead of
                // 995: it counted the scrolls as points spent.
                MoveScrollsOutOfTheBase(worldConnection);

                FillMissingHeads(worldConnection);

                // A character with no date leaves the server-selection screen empty, so no row is
                // allowed to stay without one. This covers the characters that already existed
                // when the column was added.
                try
                {
                    var fillLastConn = worldConnection.CreateCommand();
                    fillLastConn.CommandText =
                        "UPDATE Characters SET LastConnection = $now " +
                        "WHERE LastConnection IS NULL OR LastConnection = '';";
                    fillLastConn.Parameters.AddWithValue("$now",
                        DateTimeOffset.Now.ToString(Network.ConnectionProtocol.ConnectionDateFormat));
                    int filled = fillLastConn.ExecuteNonQuery();
                    if (filled > 0)
                    {
                        Console.WriteLine($"[SQLite] Migration: filled in the last connection of {filled} character(s).");
                    }
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    Console.WriteLine($"[SQLite] Could not fill in the last connection: {ex.Message}");
                }

                var createItems = worldConnection.CreateCommand();
                createItems.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterItems (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        CharacterId INTEGER NOT NULL,
                        Uid INTEGER NOT NULL,
                        Gid INTEGER NOT NULL,
                        Quantity INTEGER NOT NULL DEFAULT 1,
                        Position INTEGER NOT NULL DEFAULT 63,
                        Effects TEXT
                    );

                    -- El uid es unico en TODO el servidor, no por personaje: los uid nuevos se
                    -- reparten con un MAX(Uid) global (NpcHandler, Lottery) y varios INSERT usan
                    -- ON CONFLICT(Uid), que exige un indice unico para siquiera compilar.
                    --
                    -- Estaba creado dentro de SeedInventory, o sea que el invariante dependia de
                    -- que a alguien le tocase sembrar el inventario de la captura. En una base
                    -- que naciera sin pasar por ahi, los ON CONFLICT fallaban y dos personajes
                    -- podian acabar con el mismo uid. Va aqui, con la tabla, que es donde deja
                    -- de ser una casualidad.
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_items_uid ON CharacterItems(Uid);

                    -- Y por personaje, que es como se pregunta al dibujar a alguien: el aspecto
                    -- de cada actor de un mapa necesita saber que lleva puesto su dueno, y sin
                    -- indice eso es un recorrido de la tabla entera por actor.
                    CREATE INDEX IF NOT EXISTS idx_items_character ON CharacterItems(CharacterId);
                ";
                createItems.ExecuteNonQuery();

                // The client stores the inventory uid in 32 bits. Characters whose id is large were
                // seeded with `characterId * 1000`: for example 13825561032 reached the client as
                // 940659144. On sending that number back to equip, the server did not find the
                // original item and left the sheet without its effects. Reassigns once any uid that
                // cannot make the round trip without being truncated.
                RepairClientItemUids(worldConnection);

                // Migration: Ensure Effects column exists in CharacterItems
                try
                {
                    var addColCmd = worldConnection.CreateCommand();
                    addColCmd.CommandText = "ALTER TABLE CharacterItems ADD COLUMN Effects TEXT;";
                    addColCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Effects column to CharacterItems table.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Column already exists, ignore
                }

                // Which of each spell pair the character carries. It is the only thing about spells
                // that does not come from the client's data: the pairs and the levels are its own, but
                // the choice is the player's and has to survive from one session to the next.
                var createSpellChoices = worldConnection.CreateCommand();
                createSpellChoices.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterSpellChoices (
                        CharacterId INTEGER NOT NULL,
                        PairId INTEGER NOT NULL,
                        SpellId INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, PairId)
                    );
                ";
                createSpellChoices.ExecuteNonQuery();

                // Each profession's experience. It is what the catalogue's comment had been promising
                // for a while: the character's profession levels go apart from the catalogue, which is
                // the client's and does not change.
                var createJobs = worldConnection.CreateCommand();
                createJobs.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterJobs (
                        CharacterId INTEGER NOT NULL,
                        JobId INTEGER NOT NULL,
                        Experience INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, JobId)
                    );
                ";
                createJobs.ExecuteNonQuery();

                // What each character says of itself as an artisan, job by job: the minimum
                // level of a customer, crafting for free, and being in the public directory.
                var createCrafter = worldConnection.CreateCommand();
                createCrafter.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterCrafterSettings (
                        CharacterId INTEGER NOT NULL,
                        JobId INTEGER NOT NULL,
                        MinLevel INTEGER NOT NULL DEFAULT 1,
                        Free INTEGER NOT NULL DEFAULT 1,
                        Listed INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, JobId)
                    );
                ";
                createCrafter.ExecuteNonQuery();

                // The Infinite Dream each character has going, whole, as JSON: it outlives a
                // disconnection and a restart, and it is what the well offers to continue.
                var createDream = worldConnection.CreateCommand();
                createDream.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterDreams (
                        CharacterId INTEGER PRIMARY KEY,
                        Json TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                ";
                createDream.ExecuteNonQuery();

                // The dungeon challenges already achieved. They carry an achievement, and an achievement
                // is done ONCE: once the challenge is met, it is never offered to that character again.
                // Normal challenges do not go through here, those always come up.
                var createChallenges = worldConnection.CreateCommand();
                createChallenges.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterChallenges (
                        CharacterId INTEGER NOT NULL,
                        ChallengeId INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, ChallengeId)
                    );
                ";
                createChallenges.ExecuteNonQuery();

                // Where each character has got to on each quest. StepId zero with Completed 1 is a
                // quest that is over; Objectives is the comma-separated list of the ones already
                // ticked off ON THE STEP IN HAND, which is why it is emptied whenever the step
                // changes: the client is only ever told about the current step's objectives.
                var createQuests = worldConnection.CreateCommand();
                createQuests.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterQuests (
                        CharacterId INTEGER NOT NULL,
                        QuestId INTEGER NOT NULL,
                        StepId INTEGER NOT NULL DEFAULT 0,
                        Objectives TEXT NOT NULL DEFAULT '',
                        Completed INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, QuestId)
                    );
                ";
                createQuests.ExecuteNonQuery();

                // The achievements earned, and whether what they give has already been collected. They
                // are two facts and not one: the client asks for the reward with a separate packet -- the
                // Logros capture is exactly that -- and whoever merged them would pay twice or never.
                var createAchievements = worldConnection.CreateCommand();
                createAchievements.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterAchievements (
                        CharacterId INTEGER NOT NULL,
                        AchievementId INTEGER NOT NULL,
                        Claimed INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, AchievementId)
                    );
                ";
                createAchievements.ExecuteNonQuery();

                // And the tallies and emotes that go with them, created the same way.
                EnsureProgressionTables();

                // The keyring's free entry, spent or not, per character and per dungeon. One row for
                // both because the keyring gives "a free entry in EACH dungeon, once a week" --
                // translation 1189621, the client's own help page --, so using it at one door does not
                // close the others.
                //
                // Week is the Tuesday the week starts on, not the day it was used: the keyring "resets
                // every Tuesday", which is not the same as seven days later. Whoever goes in on a Monday
                // has it again the next day.
                var createKeyring = worldConnection.CreateCommand();
                createKeyring.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterKeyring (
                        CharacterId INTEGER NOT NULL,
                        DungeonId INTEGER NOT NULL,
                        Week TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY (CharacterId, DungeonId)
                    );
                ";
                createKeyring.ExecuteNonQuery();

                // The interactives this character has ever used.
                //
                // It is needed for the quests that start by reading something. The Incarnam tavern's
                // job offer is not an objective -- the quest has a single step and the notice does not
                // appear in it --, it is the CONDITION for the tavern keeper to offer «He visto el
                // anuncio que has puesto»: without having read it that answer should not exist. Without
                // remembering it there is no way of knowing, because a click leaves no trace.
                //
                // All of them are stored, not just the notice: it costs one row and avoids having to
                // decide beforehand which ones matter, which is a decision that is always made too late.
                var createElements = worldConnection.CreateCommand();
                createElements.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterElements (
                        CharacterId INTEGER NOT NULL,
                        ElementId INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, ElementId)
                    );
                ";
                createElements.ExecuteNonQuery();

                // The guilds and their members. The base of everything about the guild; see GuildStore.
                Managers.GuildStore.EnsureTables(worldConnection);

                // The keyring for everybody who already had a character. New ones receive it with the
                // adventurer's set -- see CharacterCreationHandler --, but those already there would be
                // left without it, and without the keyring one gets into none of the 107 dungeons that
                // accept it except by crafting their key.
                //
                // It is checked before giving, so starting twice does not hand out two.
                DarElManojoALosQueYaEstaban(worldConnection);

                // And which bar slot each spell was put in, for the same reason.
                var createSpellBar = worldConnection.CreateCommand();
                createSpellBar.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterSpellBar (
                        CharacterId INTEGER NOT NULL,
                        Slot INTEGER NOT NULL,
                        SpellId INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, Slot)
                    );
                ";
                createSpellBar.ExecuteNonQuery();

                // Seed default character if empty
                var checkChar = worldConnection.CreateCommand();
                checkChar.CommandText = "SELECT COUNT(*) FROM Characters WHERE Id = 13825558;";
                long count = (long)checkChar.ExecuteScalar();
                if (count == 0)
                {
                    var seedChar = worldConnection.CreateCommand();
                    seedChar.CommandText = @"
                        INSERT INTO Characters (
                            Id, AccountId, Name, Breed, Sex, Level, MapId, CellId, 
                            RemainingPoints, Vitality, Wisdom, Strength, Intelligence, Chance, Agility, Look, Kamas
                        ) VALUES (
                            13825558, 188940901, $name, 9, 1, 40, 154010884, 280, 
                            195, 0, 0, 0, 0, 0, 0, $look, 50000
                        );
                    ";
                    seedChar.Parameters.AddWithValue("$name", "[#KEKA-BRON#]");
                    seedChar.Parameters.AddWithValue("$look", "080118032218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB342A035BE410420134320220013809");
                    seedChar.ExecuteNonQuery();
                }

                // 3. Initialize NpcSpawns table
                var createNpcSpawns = worldConnection.CreateCommand();
                createNpcSpawns.CommandText = @"
                    CREATE TABLE IF NOT EXISTS NpcSpawns (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        MapId INTEGER NOT NULL,
                        NpcId INTEGER NOT NULL,
                        CellId INTEGER NOT NULL,
                        Orientation INTEGER NOT NULL,
                        BoneId INTEGER NOT NULL,
                        Look TEXT
                    );
                    CREATE TABLE IF NOT EXISTS MapPositions (
                        MapId INTEGER PRIMARY KEY,
                        PosX INTEGER NOT NULL DEFAULT 0,
                        PosY INTEGER NOT NULL DEFAULT 0,
                        SubAreaId INTEGER NOT NULL DEFAULT 1,
                        Outdoor INTEGER NOT NULL DEFAULT 1,
                        Name TEXT
                    );
                    CREATE TABLE IF NOT EXISTS MapScrolls (
                        MapId INTEGER PRIMARY KEY,
                        RightMapId INTEGER NOT NULL DEFAULT 0,
                        BottomMapId INTEGER NOT NULL DEFAULT 0,
                        LeftMapId INTEGER NOT NULL DEFAULT 0,
                        TopMapId INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS Dungeons (
                        Id INTEGER PRIMARY KEY,
                        Name TEXT,
                        MinLevel INTEGER NOT NULL DEFAULT 1,
                        OptimalLevel INTEGER NOT NULL DEFAULT 1,
                        Difficulty INTEGER NOT NULL DEFAULT 0,
                        EntranceMapId INTEGER NOT NULL DEFAULT 0,
                        ExitMapId INTEGER NOT NULL DEFAULT 0,
                        Bosses TEXT
                    );
                    CREATE TABLE IF NOT EXISTS DungeonRooms (
                        DungeonId INTEGER NOT NULL,
                        Position INTEGER NOT NULL,
                        MapId INTEGER NOT NULL,
                        PRIMARY KEY (DungeonId, Position)
                    );
                    CREATE INDEX IF NOT EXISTS idx_dungeon_rooms_map ON DungeonRooms(MapId);

                    /* Los índices de los hechizos, que NO son un adorno: son la diferencia entre
                       un combate fluido y uno a trompicones.

                       SpellLevels tiene 34.823 filas y sus dos columnas de efectos suman 67 MB de
                       texto, casi dos kilobytes por fila. La clave primaria es Id, pero TODAS las
                       consultas del combate buscan por SpellId —los efectos de un hechizo, su
                       grado, su coste, sus recargas—, así que cada una recorría la tabla entera:
                       medido, 37 milisegundos por consulta y cuatro consultas por lanzamiento.
                       Eso es el parón de entre 47 y 138 milisegundos que se notaba al lanzar.

                       Con el índice, la misma consulta pasa de SCAN a SEARCH y baja a cuatro
                       milésimas de milisegundo. Crearlos cuesta 92 milisegundos una sola vez. */
                    CREATE INDEX IF NOT EXISTS idx_spelllevels_hechizo ON SpellLevels(SpellId, Grade);
                    CREATE INDEX IF NOT EXISTS idx_spelllevels_nivel ON SpellLevels(SpellId, MinPlayerLevel);
                ";
                createNpcSpawns.ExecuteNonQuery();

                // Seed Noken Okuto spawn if empty
                var checkNpc = worldConnection.CreateCommand();
                checkNpc.CommandText = "SELECT COUNT(*) FROM NpcSpawns WHERE NpcId = 2892 AND MapId = 154010883;";
                long npcCount = (long)checkNpc.ExecuteScalar();
                if (npcCount == 0)
                {
                    var seedNpc = worldConnection.CreateCommand();
                    seedNpc.CommandText = @"
                        INSERT INTO NpcSpawns (MapId, NpcId, CellId, Orientation, BoneId, Look)
                        VALUES (154010883, 2892, 329, 3, 231, '{231|||95}');
                    ";
                    seedNpc.ExecuteNonQuery();
                }

                // 4. Initialize Monsters and MapMobs tables
                var createMonsters = worldConnection.CreateCommand();
                createMonsters.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Monsters (
                        Id INTEGER PRIMARY KEY,
                        NameId INTEGER,
                        Look TEXT,
                        Grades TEXT,
                        Spells TEXT DEFAULT '[]'
                    );
                    CREATE TABLE IF NOT EXISTS Subareas (
                        Id INTEGER PRIMARY KEY,
                        Monsters TEXT
                    );
                    CREATE TABLE IF NOT EXISTS MapSubareas (
                        MapId INTEGER PRIMARY KEY,
                        SubAreaId INTEGER
                    );
                    CREATE TABLE IF NOT EXISTS MapMobs (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        MapId INTEGER NOT NULL,
                        MobId INTEGER NOT NULL,
                        CellId INTEGER NOT NULL,
                        MembersJson TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS Spells (
                        Id INTEGER PRIMARY KEY,
                        NameId INTEGER,
                        DescriptionId INTEGER,
                        IconId INTEGER,
                        TypeId INTEGER
                    );
                    CREATE TABLE IF NOT EXISTS SpellLevels (
                        Id INTEGER PRIMARY KEY,
                        SpellId INTEGER,
                        Grade INTEGER,
                        MinPlayerLevel INTEGER,
                        APCost INTEGER,
                        MinRange INTEGER,
                        MaxRange INTEGER,
                        CastInLine INTEGER,
                        MaxCastPerTurn INTEGER,
                        MaxCastPerTarget INTEGER,
                        EffectsJson TEXT
                    );
                    CREATE TABLE IF NOT EXISTS SpellVariants (
                        BreedId INTEGER PRIMARY KEY,
                        SpellIdsJson TEXT
                    );
                ";
                createMonsters.ExecuteNonQuery();

                EnsureProfessionCatalogSchema(worldConnection);
                EnsureInteractiveTeleportSchema(worldConnection);

                // 5. Ensure Monsters, Mobs, Spells, and SpellLevels are seeded
                EnsureMobsSeeded(worldConnection);
                EnsureSpellsSeeded(worldConnection);

                if (count == 0)
                {
                    Console.WriteLine("[SQLite] Seeded the default character.");
                }
                else
                {
                    // Migration: strip the leftover bracket/hash markers from the seeded name
                    using (var updateCmd = worldConnection.CreateCommand())
                    {
                        updateCmd.CommandText = "UPDATE Characters SET Name = 'CADERNIS' WHERE Name = '[#CADERNIS#]' OR Name = '#CADERNIS#';";
                        int affected = updateCmd.ExecuteNonQuery();
                        if (affected > 0)
                        {
                            Console.WriteLine("[SQLite] Migration: Normalized the seeded character name.");
                        }
                    }

                    // Migration: Unstick character from CellId 116
                    using (var updateCellCmd = worldConnection.CreateCommand())
                    {
                        updateCellCmd.CommandText = "UPDATE Characters SET CellId = 320 WHERE CellId = 116 OR CellId <= 0;";
                        int cellAffected = updateCellCmd.ExecuteNonQuery();
                        if (cellAffected > 0)
                        {
                            Console.WriteLine("[SQLite] Migration: Unstuck character, moved to Cell 320.");
                        }
                    }

                    // Migration: Ensure character Breed is 9 (Cra)
                    using (var updateBreedCmd = worldConnection.CreateCommand())
                    {
                        updateBreedCmd.CommandText = "UPDATE Characters SET Breed = 9 WHERE Id = 13825558 AND Breed <> 9;";
                        int breedAffected = updateBreedCmd.ExecuteNonQuery();
                        if (breedAffected > 0)
                        {
                            Console.WriteLine("[SQLite] Migration: Updated character Breed to 9 (Cra).");
                        }
                    }
                }
            }
            // Migration: Add Spells column to Monsters table if missing
            {
                using var wConn = new SqliteConnection(WorldConnectionString);
                wConn.Open();
                var pragmaCmd = wConn.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA table_info(Monsters);";
                bool hasSpells = false;
                using (var pragmaReader = pragmaCmd.ExecuteReader())
                {
                    while (pragmaReader.Read())
                    {
                        if (pragmaReader.GetString(1) == "Spells") { hasSpells = true; break; }
                    }
                }

                if (!hasSpells)
                {
                    var alterCmd = wConn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE Monsters ADD COLUMN Spells TEXT DEFAULT '[]';";
                    alterCmd.ExecuteNonQuery();
                    Console.WriteLine("[SQLite] Migration: Added Spells column to Monsters table.");

                    // Re-seed Monsters to populate Spells column
                    var deleteCmd = wConn.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM Monsters;";
                    deleteCmd.ExecuteNonQuery();
                    PopulateMonstersFromJSON(wConn);
                    Console.WriteLine("[SQLite] Migration: Re-populated Monsters with spell data.");
                }
            }

            Console.WriteLine("[SQLite] Databases initialized successfully.");
        }

        /// <summary>
        /// Static profession catalogue from the 3.6.10.10 client data. Variable-length arrays
        /// are normalized so recipes and skill capabilities can be queried directly by handlers.
        /// This is catalogue data only; character job levels will belong to a separate table.
        /// </summary>
        private static void EnsureProfessionCatalogSchema(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Jobs (
                    Id INTEGER PRIMARY KEY,
                    NameId INTEGER NOT NULL,
                    IconId INTEGER NOT NULL,
                    HasLegendaryCraft INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS Skills (
                    Id INTEGER PRIMARY KEY,
                    NameId INTEGER NOT NULL,
                    ParentJobId INTEGER NOT NULL,
                    ElementActionId INTEGER NOT NULL,
                    LevelMin INTEGER NOT NULL,
                    GatheredResourceItem INTEGER NOT NULL,
                    Cursor INTEGER NOT NULL,
                    Range INTEGER NOT NULL,
                    UseAnimation TEXT NOT NULL DEFAULT '',
                    UseRangeInClient INTEGER NOT NULL DEFAULT 0,
                    ClientDisplay INTEGER NOT NULL DEFAULT 0,
                    AvailableInHouse INTEGER NOT NULL DEFAULT 0,
                    AllowMarking INTEGER NOT NULL DEFAULT 0,
                    IsForgemagus INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (ParentJobId) REFERENCES Jobs(Id)
                );

                CREATE TABLE IF NOT EXISTS SkillCraftableItems (
                    SkillId INTEGER NOT NULL,
                    ItemId INTEGER NOT NULL,
                    Position INTEGER NOT NULL,
                    PRIMARY KEY (SkillId, Position),
                    FOREIGN KEY (SkillId) REFERENCES Skills(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS SkillModifiableItemTypes (
                    SkillId INTEGER NOT NULL,
                    ItemTypeId INTEGER NOT NULL,
                    Position INTEGER NOT NULL,
                    PRIMARY KEY (SkillId, Position),
                    FOREIGN KEY (SkillId) REFERENCES Skills(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS Recipes (
                    ResultId INTEGER PRIMARY KEY,
                    ResultNameId INTEGER NOT NULL,
                    ResultTypeId INTEGER NOT NULL,
                    ResultLevel INTEGER NOT NULL,
                    JobId INTEGER NOT NULL,
                    SkillId INTEGER NOT NULL,
                    FOREIGN KEY (JobId) REFERENCES Jobs(Id),
                    FOREIGN KEY (SkillId) REFERENCES Skills(Id)
                );

                CREATE TABLE IF NOT EXISTS RecipeIngredients (
                    ResultId INTEGER NOT NULL,
                    Position INTEGER NOT NULL,
                    IngredientId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL,
                    PRIMARY KEY (ResultId, Position),
                    FOREIGN KEY (ResultId) REFERENCES Recipes(ResultId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_Skills_ParentJobId ON Skills(ParentJobId);
                CREATE INDEX IF NOT EXISTS IX_Skills_ElementActionId ON Skills(ElementActionId);
                CREATE INDEX IF NOT EXISTS IX_Recipes_JobId ON Recipes(JobId);
                CREATE INDEX IF NOT EXISTS IX_Recipes_SkillId ON Recipes(SkillId);
                CREATE INDEX IF NOT EXISTS IX_RecipeIngredients_IngredientId
                    ON RecipeIngredients(IngredientId);
            ";
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// The passages between maps imported from the normalised catalogue.
        ///
        /// The key carries the destination inside so that the ambiguous candidates can be stored too -- the
        /// ones that give two destinations for the same element -- which necessarily stay switched off. The
        /// game only loads the rows with Enabled=1.
        /// </summary>
        private static void EnsureInteractiveTeleportSchema(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS InteractiveTeleports (
                    SourceMapId INTEGER NOT NULL,
                    ElementId INTEGER NOT NULL,
                    SourceCellId INTEGER NOT NULL,
                    GfxId INTEGER NOT NULL,
                    InteractiveType INTEGER NOT NULL,
                    SkillId INTEGER NOT NULL,
                    DestinationMapId INTEGER NOT NULL,
                    DestinationCellId INTEGER NOT NULL,
                    SourceVersion TEXT NOT NULL,
                    Confidence TEXT NOT NULL,
                    ValidationStatus TEXT NOT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (SourceMapId, ElementId, DestinationMapId, DestinationCellId)
                );

                CREATE INDEX IF NOT EXISTS IX_InteractiveTeleports_Source
                    ON InteractiveTeleports(SourceMapId, ElementId);
                CREATE INDEX IF NOT EXISTS IX_InteractiveTeleports_EnabledMap
                    ON InteractiveTeleports(Enabled, SourceMapId);
            ";
            command.ExecuteNonQuery();
        }

        public static void EnsureMobsSeeded(SqliteConnection connection)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM MapMobs;";
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return;

            Console.WriteLine("[DatabaseManager] Auto-seeding Monsters, Subareas, MapSubareas, and MapMobs from JSON...");

            string basePath = Paths.DataDir;
            if (!Directory.Exists(basePath))
            {
                basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dofus3_data");
            }

            if (!Directory.Exists(basePath))
            {
                Console.WriteLine($"[DatabaseManager] Warning: JSON directory not found at {basePath}. Skipping auto-seeding.");
                return;
            }

            var checkMonsters = connection.CreateCommand();
            checkMonsters.CommandText = "SELECT COUNT(*) FROM Monsters;";
            long mCount = (long)checkMonsters.ExecuteScalar();
            if (mCount == 0)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    string monstersPath = Path.Combine(basePath, "monsters.json");
                    if (File.Exists(monstersPath))
                    {
                        using var fs = new FileStream(monstersPath, FileMode.Open, FileAccess.Read);
                        using var doc = System.Text.Json.JsonDocument.Parse(fs);
                        var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO Monsters (Id, NameId, Look, Grades) VALUES ($id, $nameId, $look, $grades);";
                        insertCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                        insertCmd.Parameters.Add("$nameId", Microsoft.Data.Sqlite.SqliteType.Integer);
                        insertCmd.Parameters.Add("$look", Microsoft.Data.Sqlite.SqliteType.Text);
                        insertCmd.Parameters.Add("$grades", Microsoft.Data.Sqlite.SqliteType.Text);
                        int countM = 0;
                        for (int i = 0; i < refsArr.GetArrayLength(); i++)
                        {
                            if (!refsArr[i].TryGetProperty("data", out var data)) continue;
                            int monsterId = data.TryGetProperty("id", out var mid) ? mid.GetInt32() : 0;
                            if (monsterId == 0) continue;
                            int nameId = data.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                            string look = data.TryGetProperty("look", out var lk) ? lk.GetString() ?? "" : "";
                            string grades = data.TryGetProperty("grades", out var gr) ? gr.GetRawText() : "[]";
                            insertCmd.Parameters["$id"].Value = monsterId;
                            insertCmd.Parameters["$nameId"].Value = nameId;
                            insertCmd.Parameters["$look"].Value = look;
                            insertCmd.Parameters["$grades"].Value = grades;
                            insertCmd.ExecuteNonQuery();
                            countM++;
                        }
                        Console.WriteLine($"[DatabaseManager] Inserted {countM} monsters into DB.");
                    }

                    string subareasPath = Path.Combine(basePath, "subareas.json");
                    if (File.Exists(subareasPath))
                    {
                        using var fs = new FileStream(subareasPath, FileMode.Open, FileAccess.Read);
                        using var doc = System.Text.Json.JsonDocument.Parse(fs);
                        var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO Subareas (Id, Monsters) VALUES ($id, $monsters);";
                        insertCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                        insertCmd.Parameters.Add("$monsters", Microsoft.Data.Sqlite.SqliteType.Text);
                        for (int i = 0; i < refsArr.GetArrayLength(); i++)
                        {
                            if (!refsArr[i].TryGetProperty("data", out var data)) continue;
                            int subAreaId = data.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0;
                            if (subAreaId == 0) continue;
                            string monsters = data.TryGetProperty("monsters", out var mst) ? mst.GetRawText() : "[]";
                            insertCmd.Parameters["$id"].Value = subAreaId;
                            insertCmd.Parameters["$monsters"].Value = monsters;
                            insertCmd.ExecuteNonQuery();
                        }
                    }

                    string mapsPath = Path.Combine(basePath, "maps_information.json");
                    if (File.Exists(mapsPath))
                    {
                        using var fs = new FileStream(mapsPath, FileMode.Open, FileAccess.Read);
                        using var doc = System.Text.Json.JsonDocument.Parse(fs);
                        var refsArr = doc.RootElement.GetProperty("references").GetProperty("RefIds");
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO MapSubareas (MapId, SubAreaId) VALUES ($id, $subid);";
                        insertCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                        insertCmd.Parameters.Add("$subid", Microsoft.Data.Sqlite.SqliteType.Integer);
                        for (int i = 0; i < refsArr.GetArrayLength(); i++)
                        {
                            if (!refsArr[i].TryGetProperty("data", out var data)) continue;
                            long mapId = data.TryGetProperty("id", out var mid) ? mid.GetInt64() : 0;
                            if (mapId == 0) continue;
                            int subAreaId = data.TryGetProperty("subAreaId", out var sid) ? sid.GetInt32() : 0;
                            insertCmd.Parameters["$id"].Value = mapId;
                            insertCmd.Parameters["$subid"].Value = subAreaId;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    Console.WriteLine("[DatabaseManager] Error seeding JSON data: " + ex.Message);
                }
            }

            PopulateMapMobs(connection);
        }

        private static void PopulateMapMobs(SqliteConnection connection)
        {
            var monsters = new Dictionary<int, Managers.MobSpawnManager.MonsterData>();
            var subareas = new Dictionary<int, List<int>>();
            var mapSubareas = new Dictionary<long, int>();

            var cmdMonsters = connection.CreateCommand();
            cmdMonsters.CommandText = "SELECT Id, NameId, Look, Grades FROM Monsters;";
            using (var reader = cmdMonsters.ExecuteReader())
            {
                while (reader.Read())
                {
                    var data = new Managers.MobSpawnManager.MonsterData
                    {
                        Id = reader.GetInt32(0),
                        NameId = reader.GetInt32(1),
                        Look = reader.GetString(2)
                    };
                    string gradesJson = reader.GetString(3);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(gradesJson);
                        var root = doc.RootElement;
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Array", out var arrProp))
                        {
                            root = arrProp;
                        }
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var g in root.EnumerateArray())
                            {
                                int lvl = g.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
                                data.Grades.Add(new Managers.MobSpawnManager.MonsterGrade { Level = lvl });
                            }
                        }
                    }
                    catch { }
                    monsters[data.Id] = data;
                }
            }

            var cmdSubareas = connection.CreateCommand();
            cmdSubareas.CommandText = "SELECT Id, Monsters FROM Subareas;";
            using (var reader = cmdSubareas.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var list = new List<int>();
                    string monstersJson = reader.GetString(1);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(monstersJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var m in doc.RootElement.EnumerateArray()) list.Add(m.GetInt32());
                        }
                        else if (doc.RootElement.TryGetProperty("Array", out var arrProp))
                        {
                            foreach (var m in arrProp.EnumerateArray()) list.Add(m.GetInt32());
                        }
                    }
                    catch { }
                    subareas[id] = list;
                }
            }

            var cmdMapSubareas = connection.CreateCommand();
            cmdMapSubareas.CommandText = "SELECT MapId, SubAreaId FROM MapSubareas;";
            using (var reader = cmdMapSubareas.ExecuteReader())
            {
                while (reader.Read()) mapSubareas[reader.GetInt64(0)] = reader.GetInt32(1);
            }

            long currentMobId = -1000000;
            Random rand = new Random();

            using var transaction = connection.BeginTransaction();
            var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO MapMobs (MapId, MobId, CellId, MembersJson) VALUES ($mapId, $mobId, $cellId, $json);";
            insertCmd.Parameters.Add("$mapId", Microsoft.Data.Sqlite.SqliteType.Integer);
            insertCmd.Parameters.Add("$mobId", Microsoft.Data.Sqlite.SqliteType.Integer);
            insertCmd.Parameters.Add("$cellId", Microsoft.Data.Sqlite.SqliteType.Integer);
            insertCmd.Parameters.Add("$json", Microsoft.Data.Sqlite.SqliteType.Text);

            int totalSpawns = 0;
            foreach (var kvp in mapSubareas)
            {
                long mapId = kvp.Key;
                int subAreaId = kvp.Value;

                if (!subareas.TryGetValue(subAreaId, out var allowedMonsters) || allowedMonsters.Count == 0) continue;
                var validMonsters = allowedMonsters.Where(id => monsters.ContainsKey(id) && id != 494).ToList();
                if (validMonsters.Count == 0) validMonsters = allowedMonsters.Where(id => monsters.ContainsKey(id)).ToList();
                if (validMonsters.Count == 0) continue;

                int numMobs = rand.Next(2, 5); // 2 to 4 groups
                var validCells = Managers.MobSpawnManager.GetInnerWalkableCells(mapId);
                var usedCells = new HashSet<int>();

                for (int i = 0; i < numMobs; i++)
                {
                    int groupSize = rand.Next(1, 9); // 1 to 8 members (official Dofus range)
                    int cellId = validCells[rand.Next(validCells.Count)];
                    while (usedCells.Contains(cellId) && usedCells.Count < validCells.Count)
                    {
                        cellId = validCells[rand.Next(validCells.Count)];
                    }
                    usedCells.Add(cellId);

                    long mobId = currentMobId--;

                    var members = new List<object>();
                    for (int m = 0; m < groupSize; m++)
                    {
                        int monsterId = validMonsters[rand.Next(validMonsters.Count)];
                        var mData = monsters[monsterId];
                        int gradeIdx = 0;
                        int lvl = 1;
                        if (mData.Grades.Count > 0)
                        {
                            gradeIdx = rand.Next(mData.Grades.Count);
                            lvl = mData.Grades[gradeIdx].Level;
                        }
                        members.Add(new { id = monsterId, grade = gradeIdx, level = lvl });
                    }

                    insertCmd.Parameters["$mapId"].Value = mapId;
                    insertCmd.Parameters["$mobId"].Value = mobId;
                    insertCmd.Parameters["$cellId"].Value = cellId;
                    insertCmd.Parameters["$json"].Value = System.Text.Json.JsonSerializer.Serialize(members);
                    insertCmd.ExecuteNonQuery();
                    totalSpawns++;
                }
            }
            transaction.Commit();
            Console.WriteLine($"[DatabaseManager] Successfully auto-seeded {totalSpawns} mobs into MapMobs table.");
        }

        // --- Auth Operations & Security (Anti-SQL Injection & Anti-DDoS) ---

        public class DbAccount
        {
            public long Id { get; set; }
            public string Login { get; set; } = "";
            public string Nickname { get; set; } = "";

            /// <summary>What this account can do. See <see cref="Roles"/>.</summary>
            public int Role { get; set; } = Roles.PorDefecto;
        }

        public static bool ValidateAccountCredentials(string login, string password, string clientIp, out DbAccount? account, out string errorMessage)
        {
            account = null;
            errorMessage = "";

            // Shape of the name first, so the throttle counts "keka" and "Keka" as one account and
            // so a request with nothing in it never books an attempt against anybody.
            login = (login ?? "").Trim().ToLowerInvariant();
            password = (password ?? "").Trim();

            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
            {
                errorMessage = "Please enter a username and a password.";
                return false;
            }

            // Booked BEFORE the query and before Claves.Comprueba, which is a PBKDF2 on purpose.
            // The old check read the counter here and incremented at the bottom of the method, so
            // parallel attempts all walked past a limit that had not been written yet and each one
            // bought its own hash. See LoginThrottle for the other three holes.
            if (!Managers.LoginThrottle.TryBegin(clientIp, login, DateTime.UtcNow, out errorMessage))
            {
                return false;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(login, @"^[a-zA-Z0-9_@.-]{3,32}$"))
            {
                errorMessage = "The username contains invalid characters or has the wrong length (3-32 characters).";
                return false;
            }

            // 3. Parametrized Query against auth.db
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                // The password is no longer compared in the SQL. Before this was «AND Password = $pass»,
                // which forces having the password stored in clear to be able to match it; now what is
                // stored is fetched and Claves checks it, which knows both the hashed ones and the old
                // ones.
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Login, Nickname, Role, Password FROM Accounts " +
                                      "WHERE LOWER(Login) = $login;";
                command.Parameters.AddWithValue("$login", login);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    string guardada = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    if (Managers.Claves.Comprueba(password, guardada, out bool reescribir))
                    {
                        account = new DbAccount
                        {
                            Id = reader.GetInt64(0),
                            Login = reader.GetString(1),
                            Nickname = reader.GetString(2),
                            Role = reader.IsDBNull(3) ? Roles.PorDefecto : reader.GetInt32(3),
                        };

                        // It is the only moment the password is in hand and it is known to be the right one,
                        // so this is where the one stored in clear is converted. The old database hashes
                        // itself as each one signs in.
                        if (reescribir) ReescribirClave(account.Id, password);

                        Managers.LoginThrottle.Succeeded(clientIp, login);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager Error] Authentication error: {ex.Message}");
            }

            // No counting here: the attempt was already booked at the top, which is the whole
            // point. Reaching this line just means it stays booked.
            errorMessage = "Wrong username or password.";
            return false;
        }

        /// <summary>
        /// What that account can do.
        ///
        /// The database is asked every time, it is not kept in the session: that way taking a role away from
        /// somebody takes effect at once and not whenever he feels like reconnecting. They are accounts, not
        /// millions of rows, and the query goes by primary key.
        /// </summary>
        public static int GetAccountRole(long accountId)
        {
            if (accountId <= 0) return Roles.PorDefecto;
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Role FROM Accounts WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", accountId);
                object? valor = command.ExecuteScalar();
                return valor == null || valor == DBNull.Value
                    ? Roles.PorDefecto
                    : Convert.ToInt32(valor);
            }
            catch (Exception ex)
            {
                // If the database does not answer, the one who can do least. Never the other way round.
                Console.WriteLine($"[DatabaseManager] No se ha podido leer el rol de {accountId}: {ex.Message}");
                return Roles.PorDefecto;
            }
        }

        /// <summary>Changes an account's role by its name. Returns false if it does not exist.</summary>
        public static bool SetAccountRole(string login, int role, out int cuantas)
        {
            cuantas = 0;
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE Accounts SET Role = $role WHERE LOWER(Login) = $login;";
                command.Parameters.AddWithValue("$role", Math.Clamp(role, Roles.Jugador, Roles.Administrador));
                command.Parameters.AddWithValue("$login", (login ?? "").Trim().ToLowerInvariant());
                cuantas = command.ExecuteNonQuery();
                return cuantas > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se ha podido cambiar el rol de {login}: {ex.Message}");
                return false;
            }
        }

        /// <summary>The test account every installation comes with: see where Initialize creates it.</summary>
        public const string TestAccountLogin = "keka", TestAccountPassword = "test";

        /// <summary>
        /// Its id: the one the characters of keka that datos/world.zip carries belong to, and the
        /// one the account always had (the first of the two the old seed wrote, 188940901).
        /// </summary>
        public const long TestAccountId = 188940901;

        /// <summary>Whether an account with that login exists, whatever its case.</summary>
        public static bool AccountExists(string login)
        {
            using var connection = new SqliteConnection(AuthConnectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Accounts WHERE LOWER(Login) = $login;";
            command.Parameters.AddWithValue("$login", (login ?? "").Trim().ToLowerInvariant());
            return (long)(command.ExecuteScalar() ?? 0L) > 0;
        }

        /// <param name="id">
        /// The account's id, for the test account only: its characters in the shipped world.db
        /// belong to it. Zero, as for anybody who signs up, lets the table pick; one already taken
        /// does too.
        /// </param>
        public static bool RegisterNewAccount(string login, string password, string nickname, string clientIp, out string errorMessage,
                                              long id = 0)
        {
            errorMessage = "";

            login = (login ?? "").Trim().ToLowerInvariant();
            password = (password ?? "").Trim();
            nickname = (nickname ?? "").Trim();

            if (!System.Text.RegularExpressions.Regex.IsMatch(login, @"^[a-zA-Z0-9_@.-]{3,32}$"))
            {
                errorMessage = "The username may only contain letters, digits and the characters _ @ . - (3-32 characters).";
                return false;
            }

            if (password.Length < 3 || password.Length > 32)
            {
                errorMessage = "The password must be between 3 and 32 characters long.";
                return false;
            }

            if (string.IsNullOrEmpty(nickname)) nickname = login;

            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM Accounts WHERE LOWER(Login) = $login;";
                checkCmd.Parameters.AddWithValue("$login", login);
                if ((long)(checkCmd.ExecuteScalar() ?? 0L) > 0)
                {
                    errorMessage = "That username is already registered.";
                    return false;
                }

                // The subscription is written here, not left for the next start's filling. An account
                // created with the server already running was born with the column empty and used the
                // default value until somebody restarted: it worked, but then the date the player sees
                // is nowhere and cannot be changed for him.
                if (id > 0)
                {
                    var taken = connection.CreateCommand();
                    taken.CommandText = "SELECT COUNT(*) FROM Accounts WHERE Id = $id;";
                    taken.Parameters.AddWithValue("$id", id);
                    if ((long)(taken.ExecuteScalar() ?? 0L) > 0) id = 0;
                }

                var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = id > 0
                    ? "INSERT INTO Accounts (Id, Login, Password, Nickname, SubscriptionEnd) " +
                      "VALUES ($id, $login, $pass, $nick, $hasta);"
                    : "INSERT INTO Accounts (Login, Password, Nickname, SubscriptionEnd) " +
                      "VALUES ($login, $pass, $nick, $hasta);";
                if (id > 0) insertCmd.Parameters.AddWithValue("$id", id);
                insertCmd.Parameters.AddWithValue("$login", login);
                insertCmd.Parameters.AddWithValue("$pass", Managers.Claves.Cifrar(password));
                insertCmd.Parameters.AddWithValue("$nick", nickname);
                insertCmd.Parameters.AddWithValue("$hasta", Network.Subscription.DefaultEndDate());
                insertCmd.ExecuteNonQuery();

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Error registering the account: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Writes an account's password again, now hashed.
        ///
        /// It is only called from inside a sign-in that went well, that is with the password verified. If it
        /// fails nothing is said to whoever signs in -- he is already in -- but it is noted: that it cannot
        /// be converted is the database's business, not his.
        /// </summary>
        private static void ReescribirClave(long cuenta, string clave)
        {
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "UPDATE Accounts SET Password = $pass WHERE Id = $id;";
                command.Parameters.AddWithValue("$pass", Managers.Claves.Cifrar(clave));
                command.Parameters.AddWithValue("$id", cuenta);
                command.ExecuteNonQuery();
                Console.WriteLine($"[Auth] La contraseña de la cuenta {cuenta} ya está cifrada.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Auth] No se pudo cifrar la contraseña de {cuenta}: {ex.Message}");
            }
        }

        /// <summary>
        /// Stores the LAUNCHER's session. It is a token separate from the game's on purpose: the game's is
        /// rotated by the client every time it starts, and if they were the same, opening a client would
        /// leave the launcher without a session for the next time.
        /// </summary>
        /// <summary>
        /// Until when an account is subscribed, as it travels. Empty string if there is nothing.
        /// </summary>
        /// <remarks>
        /// With try/catch for the same reason as its neighbours: this is asked on the authentication path and
        /// on every client launch, and a half-made database -- a first start, the continuous integration,
        /// which only unzips the world -- answers «no such table: Accounts» by throwing. That exception would
        /// rise up to the connection handler, which is code that runs for EVERYBODY who shows up. It already
        /// happened once with GetAccountIdByToken and took down the whole continuous integration.
        /// </remarks>
        public static string GetSubscriptionEnd(long accountId)
        {
            if (accountId <= 0) return "";
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT SubscriptionEnd FROM Accounts WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", accountId);
                return command.ExecuteScalar() as string ?? "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se pudo leer el abono de {accountId}: {ex.Message}");
                return "";
            }
        }

        /// <summary>Changes an account's subscription date. Returns whether it wrote anything.</summary>
        public static bool SetSubscriptionEnd(long accountId, string endDate)
        {
            if (accountId <= 0) return false;
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE Accounts SET SubscriptionEnd = $hasta WHERE Id = $id;";
                command.Parameters.AddWithValue("$hasta", endDate);
                command.Parameters.AddWithValue("$id", accountId);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se pudo escribir el abono de {accountId}: {ex.Message}");
                return false;
            }
        }

        public static void SetLauncherToken(long accountId, string token)
        {
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Accounts SET LauncherToken = $token WHERE Id = $id;";
                command.Parameters.AddWithValue("$token", token);
                command.Parameters.AddWithValue("$id", accountId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se ha podido guardar la sesión del lanzador: {ex.Message}");
            }
        }

        /// <summary>Whose launcher session this is, or zero if nobody recognises it.</summary>
        public static long GetAccountIdByLauncherToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return 0;
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id FROM Accounts WHERE LauncherToken = $token;";
                command.Parameters.AddWithValue("$token", token);
                var result = command.ExecuteScalar();
                return result != null ? (long)result : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se ha podido leer la sesión del lanzador: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Stores an account's game token, overwriting the previous one.</summary>
        /// <remarks>
        /// With its net for the same reason as <see cref="GetAccountIdByToken"/>: four places call it --
        /// HAAPI twice, the Zaap and the control channel --, all of them while serving a request, and without
        /// an authentication database SQLite throws instead of answering. Not being able to store the token
        /// is bad; taking the whole request down with it is worse.
        /// </remarks>
        public static void SetGameToken(long accountId, string token)
        {
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE Accounts
                    SET GameToken = $token
                    WHERE Id = $id;
                ";
                command.Parameters.AddWithValue("$token", token);
                command.Parameters.AddWithValue("$id", accountId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se ha podido guardar el token de la cuenta " +
                                  $"{accountId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether that token belongs to some account. NOBODY CALLS IT.
        /// </summary>
        /// <remarks>
        /// It is written down because a public method that validates credentials and is not used reads as
        /// if it were the door one comes in through, and it is not: whoever resolves a token is
        /// ClientLaunchRegistry.ResolveToken, which looks in three places and not this one. If it stays
        /// unused, delete it.
        /// </remarks>
        public static bool ValidateGameToken(string token)
        {
            using var connection = new SqliteConnection(AuthConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*)
                FROM Accounts
                WHERE GameToken = $token;
            ";
            command.Parameters.AddWithValue("$token", token);
            return (long)command.ExecuteScalar() > 0;
        }

        public static DbAccount? GetAccountById(long accountId)
        {
            if (accountId <= 0) return null;
            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Login, Nickname FROM Accounts WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", accountId);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return new DbAccount
                    {
                        Id = reader.GetInt64(0),
                        Login = reader.GetString(1),
                        Nickname = reader.GetString(2)
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error reading account {accountId}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// A game token's account. Zero if it belongs to nobody.
        /// </summary>
        /// <remarks>
        /// With its net, the same as <see cref="GetAccountIdByLauncherToken"/>, which is right next to it
        /// and did have one. This one did not, and it was no detail: if the authentication database does not
        /// exist yet -- first start, or the file deleted -- SQLite throws «no such table: Accounts» and the
        /// exception rises through ResolveToken up to the connection handler, which is code that runs for
        /// EVERY client that shows up.
        ///
        /// Zero means «I do not know it», which is the right answer when there is nowhere to look. The
        /// continuous integration uncovered it, since there is no auth.db there.
        /// </remarks>
        public static long GetAccountIdByToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return 0;

            try
            {
                using var connection = new SqliteConnection(AuthConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id FROM Accounts WHERE GameToken = $token;";
                command.Parameters.AddWithValue("$token", token);
                var result = command.ExecuteScalar();
                return result != null ? (long)result : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se ha podido buscar el token de juego: {ex.Message}");
                return 0;
            }
        }

        // --- Servers ---

        /// <summary>
        /// The server the emulated world lives on. The client resolves each server's name and
        /// artwork by id against its own data, so we use one it already knows about.
        /// </summary>
        public const int DefaultServerId = 290;

        /// <summary>Where a character starts, and where one goes back to if it ends up nowhere.
        /// The same values the Characters table gives a new row.</summary>
        public const long StartingMap = 154010884L;
        public const int StartingCell = 315;

        /// <summary>Status advertised over HTTP for a server that can be joined.</summary>
        public const int ServerStatusOnline = 3;

        /// <summary>
        /// Status of a server that shows up in the list but does not accept connections. This is
        /// the most likely value according to the classic Dofus enum, where 3 means online; it
        /// could not be verified against a capture because that traffic is encrypted. If the
        /// client does not render it as expected, change it with an UPDATE on the Servers table.
        /// </summary>
        public const int ServerStatusNoJoin = 4;

        public class DbServer
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            /// <summary>Server category, exactly as it travels in the protocol's server list.</summary>
            public int Type { get; set; } = 1;
            /// <summary>Status advertised over HTTP; decides the colour on the selection screen.</summary>
            public int Status { get; set; } = ServerStatusOnline;
            /// <summary>Whether it accepts connections. Checked here, not only on the client.</summary>
            public bool Joinable { get; set; }
            public bool IsDefault { get; set; }
        }

        /// <summary>
        /// The servers on offer. Only one of them is open: the rest show up in the list but
        /// cannot be joined, so the screen looks populated without promising worlds that do not
        /// exist. The ids and their category come from the real capture; the client resolves
        /// each server's name against its own data.
        /// </summary>
        private static void SeedServers(SqliteConnection connection)
        {
            // (id, category)
            var closed = new (int Id, int Type)[]
            {
                (291, 1), (292, 1), (293, 1), (294, 1), (295, 0),
                (350, 3), (351, 3), (352, 3),
                (353, 2), (354, 2), (355, 2),
                (99, 4), (50, 5)
            };

            var open = connection.CreateCommand();
            open.CommandText =
                "INSERT OR IGNORE INTO Servers (Id, Name, Type, Status, Joinable, IsDefault) " +
                "VALUES ($id, $name, 1, $status, 1, 1);";
            open.Parameters.AddWithValue("$id", DefaultServerId);
            open.Parameters.AddWithValue("$name", DefaultServerName);
            open.Parameters.AddWithValue("$status", ServerStatusOnline);
            open.ExecuteNonQuery();

            foreach (var (id, type) in closed)
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "INSERT OR IGNORE INTO Servers (Id, Name, Type, Status, Joinable, IsDefault) " +
                    "VALUES ($id, $name, $type, $status, 0, 0);";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$name", "Server " + id);
                cmd.Parameters.AddWithValue("$type", type);
                cmd.Parameters.AddWithValue("$status", ServerStatusNoJoin);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Name of the open server. Only used by the emulator's own logs.</summary>
        public const string DefaultServerName = "Tal Kasha";

        public static List<DbServer> GetServers()
        {
            var list = new List<DbServer>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT Id, Name, Type, Status, Joinable, IsDefault FROM Servers ORDER BY IsDefault DESC, Id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new DbServer
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Type = reader.GetInt32(2),
                        Status = reader.GetInt32(3),
                        Joinable = reader.GetInt32(4) != 0,
                        IsDefault = reader.GetInt32(5) != 0
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error reading the server list: {ex.Message}");
            }

            // With no table (an old database) we at least offer the open server, which is where
            // the migration left every character.
            if (list.Count == 0)
            {
                list.Add(new DbServer
                {
                    Id = DefaultServerId,
                    Name = DefaultServerName,
                    Type = 1,
                    Status = ServerStatusOnline,
                    Joinable = true,
                    IsDefault = true
                });
            }
            return list;
        }

        /// <summary>Checks that a server exists and accepts connections.</summary>
        public static bool IsServerJoinable(int serverId)
        {
            foreach (var server in GetServers())
            {
                if (server.Id == serverId) return server.Joinable;
            }
            return false;
        }

        // --- Character Operations ---

        public class DbCharacter
        {
            public long Id { get; set; }
            public long AccountId { get; set; }
            public int ServerId { get; set; } = DefaultServerId;
            public string Name { get; set; }
            public int Breed { get; set; }
            public int Sex { get; set; }
            public int Level { get; set; }
            public string LookHex { get; set; }
            /// <summary>ISO date of the last connection, empty if the character never logged in.</summary>
            public string LastConnection { get; set; } = "";
            /// <summary>Head chosen at creation. Its skin is part of the look.</summary>
            public int HeadId { get; set; }
        }

        /// <summary>
        /// The characters on an account. If a server is given, only the ones on that server.
        ///
        /// There is no fallback of any kind: if the account has no characters, the list comes
        /// back empty. The fallback that used to be here returned another account's characters
        /// whenever the query found nothing, which with several accounts meant showing one
        /// player the characters of another.
        /// </summary>
        public static List<DbCharacter> GetCharactersByAccountId(long accountId, int serverId = 0)
        {
            var list = new List<DbCharacter>();
            if (accountId <= 0) return list;

            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, Name, Breed, Sex, Level, Look, " +
                "COALESCE(ServerId, $defaultServer), COALESCE(LastConnection, ''), " +
                "COALESCE(HeadId, 0) " +
                "FROM Characters WHERE AccountId = $accId" +
                (serverId > 0 ? " AND COALESCE(ServerId, $defaultServer) = $serverId" : "") +
                " ORDER BY Id;";
            command.Parameters.AddWithValue("$accId", accountId);
            command.Parameters.AddWithValue("$defaultServer", DefaultServerId);
            if (serverId > 0) command.Parameters.AddWithValue("$serverId", serverId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DbCharacter
                {
                    Id = reader.GetInt64(0),
                    AccountId = accountId,
                    Name = reader.GetString(1),
                    Breed = reader.GetInt32(2),
                    Sex = reader.GetInt32(3),
                    Level = reader.GetInt32(4),
                    LookHex = reader.GetString(5),
                    ServerId = reader.GetInt32(6),
                    LastConnection = reader.GetString(7),
                    HeadId = reader.GetInt32(8)
                });
            }

            return list;
        }

        /// <summary>One character by id, or null when there is no such character.</summary>
        public static DbCharacter? GetCharacterById(long characterId)
        {
            if (characterId <= 0) return null;

            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, AccountId, Name, Breed, Sex, Level, Look, " +
                "COALESCE(ServerId, $defaultServer), COALESCE(LastConnection, ''), " +
                "COALESCE(HeadId, 0) " +
                "FROM Characters WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$defaultServer", DefaultServerId);

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            return new DbCharacter
            {
                Id = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                Name = reader.GetString(2),
                Breed = reader.GetInt32(3),
                Sex = reader.GetInt32(4),
                Level = reader.GetInt32(5),
                LookHex = reader.GetString(6),
                ServerId = reader.GetInt32(7),
                LastConnection = reader.GetString(8),
                HeadId = reader.GetInt32(9)
            };
        }

        /// <summary>
        /// The six scroll columns, and the one-off that takes the scrolls back out of the base.
        /// </summary>
        /// <remarks>
        /// The columns default to 100 -- every character on this server is born scrolled, and 100
        /// is what the captures show on every real one -- so a row that existed before them is
        /// scrolled the moment they appear. What has to be undone by hand is what creation did
        /// before: it wrote 101 into all six base characteristics to stand in for the scrolls,
        /// and the base is where spent points live. A row with exactly 101 in all six is one of
        /// those and nothing else -- no spread of points a player chose lands on 101 six times --
        /// so it goes back to zero, and its points to spend go back to being its whole capital.
        /// The columns already being there means the second half has already run: it is keyed
        /// on the ALTER succeeding, so it runs once.
        /// </remarks>
        internal static void MoveScrollsOutOfTheBase(SqliteConnection connection)
        {
            string[] columns =
            {
                "ScrolledVitality", "ScrolledWisdom", "ScrolledStrength",
                "ScrolledIntelligence", "ScrolledChance", "ScrolledAgility",
            };

            bool added = false;
            foreach (string column in columns)
            {
                try
                {
                    var add = connection.CreateCommand();
                    add.CommandText = $"ALTER TABLE Characters ADD COLUMN {column} INTEGER NOT NULL DEFAULT " +
                                      $"{Handlers.CharacterCreationHandler.ScrolledStat};";
                    add.ExecuteNonQuery();
                    added = true;
                }
                catch (SqliteException)
                {
                    // Already there.
                }
            }

            if (!added) return;
            Console.WriteLine("[SQLite] Migration: Added the six Scrolled columns to Characters.");

            try
            {
                var fix = connection.CreateCommand();
                fix.CommandText = @"
                    UPDATE Characters
                    SET Vitality = 0, Wisdom = 0, Strength = 0, Intelligence = 0, Chance = 0, Agility = 0,
                        RemainingPoints = 5 * (MIN(Level, 200) - 1)
                    WHERE Vitality = 101 AND Wisdom = 101 AND Strength = 101
                      AND Intelligence = 101 AND Chance = 101 AND Agility = 101;";
                int moved = fix.ExecuteNonQuery();
                if (moved > 0)
                {
                    Console.WriteLine($"[SQLite] Migration: {moved} character(s) had the scrolls inside " +
                                      "the base; moved out, with their capital back to spend.");
                }
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[SQLite] Could not move the scrolls out of the base: {ex.Message}");
            }
        }

        /// <summary>
        /// Gives a head to the characters that have none. They predate the column, so the one
        /// their player picked is not recorded anywhere: each gets the first head the creation
        /// screen offers for its breed and sex, which is what the real client defaults to.
        /// </summary>
        private static void FillMissingHeads(SqliteConnection connection)
        {
            try
            {
                var pending = new List<(long Id, int Breed, int Sex)>();

                var query = connection.CreateCommand();
                query.CommandText =
                    "SELECT Id, Breed, Sex FROM Characters WHERE HeadId IS NULL OR HeadId = 0;";
                using (var reader = query.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        pending.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
                    }
                }

                int assigned = 0;
                foreach (var (id, breed, sex) in pending)
                {
                    int headId = Managers.HeadTable.DefaultHeadId(breed, sex);
                    if (headId <= 0) continue;

                    var update = connection.CreateCommand();
                    update.CommandText = "UPDATE Characters SET HeadId = $head WHERE Id = $id;";
                    update.Parameters.AddWithValue("$head", headId);
                    update.Parameters.AddWithValue("$id", id);
                    update.ExecuteNonQuery();
                    assigned++;
                }

                if (assigned > 0)
                {
                    Console.WriteLine($"[SQLite] Migration: assigned a head to {assigned} character(s).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Could not assign the missing heads: {ex.Message}");
            }
        }

        /// <summary>Records when a character last entered the world.</summary>
        /// <summary>When and from where he connected the PREVIOUS time. Null the first time.</summary>
        public sealed class LastVisit
        {
            public DateTimeOffset When { get; init; }
            public string Ip { get; init; } = "";
        }

        /// <summary>
        /// Last time's, READ BEFORE overwriting it.
        ///
        /// The order matters: if it is updated first, what the player is shown is the connection of right
        /// now, which tells him nothing. That is why this goes before the Touch.
        /// </summary>
        public static LastVisit? ReadLastVisit(long characterId)
        {
            try
            {
                EnsureLastIpColumn();
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT LastConnection, LastIp FROM Characters WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;

                string cuando = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string ip = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(cuando)) return null;
                if (!DateTimeOffset.TryParse(cuando, out var fecha)) return null;

                return new LastVisit { When = fecha, Ip = ip };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se ha podido leer la ultima conexion: {ex.Message}");
                return null;
            }
        }

        /// <summary>The column is new, so it adds itself in databases that come from before.</summary>
        private static void EnsureLastIpColumn()
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var check = connection.CreateCommand();
                check.CommandText = "PRAGMA table_info(Characters);";
                using var reader = check.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "LastIp", StringComparison.OrdinalIgnoreCase))
                        return;
                }
                reader.Close();

                using var add = connection.CreateCommand();
                add.CommandText = "ALTER TABLE Characters ADD COLUMN LastIp TEXT;";
                add.ExecuteNonQuery();
                Console.WriteLine("[SQLite] Anadida la columna LastIp a Characters.");
            }
            catch (Exception) { }
        }

        public static void TouchLastConnection(long characterId, string ip = "")
        {
            try
            {
                EnsureLastIpColumn();
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE Characters SET LastConnection = $now, LastIp = $ip WHERE Id = $id;";
                command.Parameters.AddWithValue("$now",
                    DateTimeOffset.Now.ToString(Network.ConnectionProtocol.ConnectionDateFormat));
                command.Parameters.AddWithValue("$ip", ip ?? "");
                command.Parameters.AddWithValue("$id", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Could not update the last connection: {ex.Message}");
            }
        }

        /// <summary>Checks that a character really does belong to the account asking for it.</summary>
        /// <summary>Notes that this character has used that interactive. Repeating does no harm.</summary>
        public static void RememberElement(long characterId, int elementId)
        {
            if (characterId <= 0 || elementId == 0) return;
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT OR IGNORE INTO CharacterElements (CharacterId, ElementId) " +
                    "VALUES ($c, $e);";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$e", elementId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se pudo apuntar el elemento {elementId}: {ex.Message}");
            }
        }

        /// <summary>The interactives this character has already used.</summary>
        public static HashSet<int> LoadElementsUsed(long characterId)
        {
            var usados = new HashSet<int>();
            if (characterId <= 0) return usados;
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT ElementId FROM CharacterElements WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) usados.Add(reader.GetInt32(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se pudieron leer los elementos usados: {ex.Message}");
            }
            return usados;
        }

        /// <summary>
        /// Every table a character owns a row in. Deleting one has to empty all of them.
        /// </summary>
        /// <remarks>
        /// Read off the schema rather than remembered, and kept as a list so that adding a table
        /// without adding it here is a visible omission instead of a silent orphan. Rows left
        /// behind would be handed to whoever inherits the id.
        /// </remarks>
        private static readonly string[] CharacterOwnedTables =
        {
            "CharacterItems", "CharacterSpellChoices", "CharacterSpellBar",
            "HavenBag", "HavenBagFurniture", "HavenBagChest",
            "CharacterWardrobe", "CharacterAppearance", "CharacterJobs", "CharacterCrafterSettings",
            "CharacterDreams",
            "CharacterChallenges", "CharacterQuests", "CharacterAchievements",
            "CharacterKeyring", "CharacterElements",
            "CharacterEnergy",
        };

        /// <summary>
        /// Deletes a character and everything hanging off it. Returns its name, or "" if nothing
        /// was deleted.
        /// </summary>
        /// <remarks>
        /// The account is a parameter and not a courtesy: the id arrives over the wire and the only
        /// thing standing between it and someone else's character is this check. It runs inside the
        /// transaction, against the same connection, so it cannot be answered by a row that is
        /// about to change.
        ///
        /// All or nothing. A half-deleted character -- gone from Characters, still owning items --
        /// is worse than one that is still there, and a transaction is what keeps the thirteen
        /// tables and the character itself on the same side of the outcome.
        /// </remarks>
        public static string DeleteCharacter(long characterId, long accountId)
        {
            if (characterId <= 0 || accountId <= 0) return "";

            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                var owner = connection.CreateCommand();
                owner.Transaction = transaction;
                owner.CommandText = "SELECT Name FROM Characters WHERE Id = $id AND AccountId = $accId;";
                owner.Parameters.AddWithValue("$id", characterId);
                owner.Parameters.AddWithValue("$accId", accountId);
                string name = owner.ExecuteScalar() as string ?? "";

                if (name.Length == 0)
                {
                    Console.WriteLine($"[DatabaseManager] Character {characterId} is not on account " +
                                      $"{accountId}; nothing deleted.");
                    return "";
                }

                foreach (string table in CharacterOwnedTables)
                {
                    var cleanup = connection.CreateCommand();
                    cleanup.Transaction = transaction;
                    // The table names come from the constant list above, never from the wire.
                    cleanup.CommandText = $"DELETE FROM {table} WHERE CharacterId = $id;";
                    cleanup.Parameters.AddWithValue("$id", characterId);
                    try
                    {
                        cleanup.ExecuteNonQuery();
                    }
                    catch (SqliteException ex)
                    {
                        // A table an older database does not have yet. Nothing to empty in it, and
                        // it must not take the deletion down with it.
                        Console.WriteLine($"[DatabaseManager] Skipping {table}: {ex.Message}");
                    }
                }

                var remove = connection.CreateCommand();
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM Characters WHERE Id = $id AND AccountId = $accId;";
                remove.Parameters.AddWithValue("$id", characterId);
                remove.Parameters.AddWithValue("$accId", accountId);
                int gone = remove.ExecuteNonQuery();

                if (gone == 0)
                {
                    transaction.Rollback();
                    return "";
                }

                transaction.Commit();
                Console.WriteLine($"[DatabaseManager] Deleted character {name} ({characterId}) " +
                                  $"and its rows in {CharacterOwnedTables.Length} tables.");
                return name;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error deleting character {characterId}: {ex.Message}");
                return "";
            }
        }

        public static bool CharacterBelongsToAccount(long characterId, long accountId)
        {
            if (characterId <= 0 || accountId <= 0) return false;
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Characters WHERE Id = $id AND AccountId = $accId;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$accId", accountId);
                return Convert.ToInt64(command.ExecuteScalar() ?? 0L) > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error checking character ownership: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The account a character belongs to (Characters.AccountId), or zero when there is no such
        /// character. What a marketplace pays a seller's bank through, connected or not.
        /// </summary>
        public static long AccountIdOfCharacter(long characterId)
        {
            if (characterId <= 0) return 0;
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT AccountId FROM Characters WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                return command.ExecuteScalar() is long accountId ? accountId : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Could not read the account of {characterId}: {ex.Message}");
                return 0;
            }
        }

        public static bool LoadCharacter(long characterId)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Name, Level, MapId, CellId, RemainingPoints, Vitality, Wisdom, Strength, Intelligence, Chance, Agility, Look, Breed, Sex, Orientation, Kamas, Experience,
                       ScrolledVitality, ScrolledWisdom, ScrolledStrength, ScrolledIntelligence, ScrolledChance, ScrolledAgility
                FROM Characters
                WHERE Id = $charId;
            ";
            command.Parameters.AddWithValue("$charId", characterId);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                Jondo.Unity.Server.Network.SessionContext.State.CharacterId = characterId;
                Jondo.Unity.Server.Network.SessionContext.State.CharacterName = reader.GetString(0);
                Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel = reader.GetInt32(1);
                // Load actual position from the database
                Jondo.Unity.Server.Network.SessionContext.State.MapId = reader.GetInt64(2);
                Jondo.Unity.Server.Network.SessionContext.State.CellId = reader.GetInt32(3);

                // A map the world data does not know is a map the client cannot load either: it
                // gets the jru, finds nothing, and the character never appears anywhere. It should
                // not be possible to be standing on one, but a character that got there once stays
                // there for good, so it is worth catching on the way in.
                if (MapManager.GetMapInfo(Jondo.Unity.Server.Network.SessionContext.State.MapId) == null && MapManager.Maps.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[SQLite] {Jondo.Unity.Server.Network.SessionContext.State.CharacterName} is on map {Jondo.Unity.Server.Network.SessionContext.State.MapId}, " +
                                      "which is not in the world data. Sending it back to the start.");
                    Console.ResetColor();
                    Jondo.Unity.Server.Network.SessionContext.State.MapId = StartingMap;
                    Jondo.Unity.Server.Network.SessionContext.State.CellId = StartingCell;
                }

                // And the cell, for the same reason and for a specific one: until it was fixed, the end
                // of a fight stored the ARENA's cell as if it were the roleplay map's. In the Incarnam
                // workshop that was 189 on a map whose 34 cells go from 244 to 414, that is a cell that
                // does not exist there, and the client drew the character nowhere. The fix keeps it
                // from happening again; this cleans the sheets that already have it stored, which would
                // otherwise enter invisible until taking a step.
                var suyo = Jondo.Unity.Server.Network.SessionContext.State;
                if (suyo.CellId > 0 && MapManager.WalkableCells.Count > 0
                    && !MapManager.IsCellWalkable(suyo.MapId, suyo.CellId))
                {
                    int buena = MapManager.GetNearestWalkableCell(suyo.MapId, suyo.CellId);
                    Console.WriteLine($"[SQLite] {suyo.CharacterName} estaba guardado en la casilla " +
                                      $"{suyo.CellId} del mapa {suyo.MapId}, que no se puede pisar. " +
                                      $"Se le pone en la {buena}.");
                    suyo.CellId = buena;
                }
                Jondo.Unity.Server.Network.SessionContext.State.Orientation = reader.IsDBNull(14) ? 1 : reader.GetInt32(14);
                Jondo.Unity.Server.Network.SessionContext.State.Kamas = reader.IsDBNull(15) ? 0 : reader.GetInt64(15);

                // If the character predates the column, give it the minimum experience that
                // matches its level so the bar does not show up empty.
                long storedXp = reader.IsDBNull(16) ? 0 : reader.GetInt64(16);
                long levelFloor = ExperienceTable.LevelFloor(Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel);
                Jondo.Unity.Server.Network.SessionContext.State.Experience = Math.Max(storedXp, levelFloor);
                Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints = reader.GetInt32(4);
                Jondo.Unity.Server.Network.SessionContext.State.StatVitality = reader.GetInt32(5);
                Jondo.Unity.Server.Network.SessionContext.State.StatWisdom = reader.GetInt32(6);
                Jondo.Unity.Server.Network.SessionContext.State.StatStrength = reader.GetInt32(7);
                Jondo.Unity.Server.Network.SessionContext.State.StatIntelligence = reader.GetInt32(8);
                Jondo.Unity.Server.Network.SessionContext.State.StatChance = reader.GetInt32(9);
                Jondo.Unity.Server.Network.SessionContext.State.StatAgility = reader.GetInt32(10);
                Jondo.Unity.Server.Network.SessionContext.State.ScrolledVitality = reader.GetInt32(17);
                Jondo.Unity.Server.Network.SessionContext.State.ScrolledWisdom = reader.GetInt32(18);
                Jondo.Unity.Server.Network.SessionContext.State.ScrolledStrength = reader.GetInt32(19);
                Jondo.Unity.Server.Network.SessionContext.State.ScrolledIntelligence = reader.GetInt32(20);
                Jondo.Unity.Server.Network.SessionContext.State.ScrolledChance = reader.GetInt32(21);
                Jondo.Unity.Server.Network.SessionContext.State.ScrolledAgility = reader.GetInt32(22);
                Jondo.Unity.Server.Network.SessionContext.State.Breed = reader.GetInt32(12);
                Jondo.Unity.Server.Network.SessionContext.State.Sex = reader.GetInt32(13);

                string lookHex = reader.GetString(11);
                byte[] lookBytes = ConvertHexStringToByteArray(lookHex);
                Jondo.Unity.Server.Network.SessionContext.State.LookBytes = lookBytes;

                // Reconstruct PlayerActorDetails (detailsMsg with look and humanoid name)
                // detailsMsg has: Field 1 (Look), Field 2 (HumanoidMsg)
                // HumanoidMsg has: Field 2 (HumanInformationsMsg)
                // HumanInformationsMsg has: Field 3 (Name)
                Jondo.Unity.Server.Network.SessionContext.State.PlayerActorDetails = ReconstructActorDetails(lookBytes, Jondo.Unity.Server.Network.SessionContext.State.CharacterName);

                // The professions, which live in their own table because they belong to the character
                // and not to the client's catalogue.
                var estado = Jondo.Unity.Server.Network.SessionContext.State;
                estado.Jobs.Clear();
                foreach (var par in LoadJobExperience(estado.CharacterId))
                {
                    estado.Jobs[par.Key] = new Managers.JobExperience.Progress
                    {
                        JobId = par.Key,
                        Experience = par.Value,
                    };
                }

                estado.CrafterSettings.Clear();
                foreach (var setting in LoadCrafterSettings(estado.CharacterId))
                    estado.CrafterSettings[setting.Key] = setting.Value;

                Console.WriteLine($"[SQLite] Successfully loaded character: {Jondo.Unity.Server.Network.SessionContext.State.CharacterName} (Level {Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel}), {estado.Jobs.Count} oficios.");
                return true;
            }
            return false;
        }

        public static void SaveCurrentCharacter()
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Characters
                SET MapId = $mapId, CellId = $cellId, Orientation = $orientation,
                    RemainingPoints = $pts, Vitality = $vit, Wisdom = $wis,
                    Strength = $str, Intelligence = $int, Chance = $cha, Agility = $agi,
                    ScrolledVitality = $svit, ScrolledWisdom = $swis, ScrolledStrength = $sstr,
                    ScrolledIntelligence = $sint, ScrolledChance = $scha, ScrolledAgility = $sagi,
                    Level = $lvl, Kamas = $kamas, Experience = $xp
                WHERE Id = $charId;
            ";
            command.Parameters.AddWithValue("$charId", Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            command.Parameters.AddWithValue("$mapId", Jondo.Unity.Server.Network.SessionContext.State.MapId);
            command.Parameters.AddWithValue("$cellId", Jondo.Unity.Server.Network.SessionContext.State.CellId);
            command.Parameters.AddWithValue("$orientation", Jondo.Unity.Server.Network.SessionContext.State.Orientation);
            command.Parameters.AddWithValue("$pts", Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints);
            command.Parameters.AddWithValue("$vit", Jondo.Unity.Server.Network.SessionContext.State.StatVitality);
            command.Parameters.AddWithValue("$wis", Jondo.Unity.Server.Network.SessionContext.State.StatWisdom);
            command.Parameters.AddWithValue("$str", Jondo.Unity.Server.Network.SessionContext.State.StatStrength);
            command.Parameters.AddWithValue("$int", Jondo.Unity.Server.Network.SessionContext.State.StatIntelligence);
            command.Parameters.AddWithValue("$cha", Jondo.Unity.Server.Network.SessionContext.State.StatChance);
            command.Parameters.AddWithValue("$agi", Jondo.Unity.Server.Network.SessionContext.State.StatAgility);
            command.Parameters.AddWithValue("$svit", Jondo.Unity.Server.Network.SessionContext.State.ScrolledVitality);
            command.Parameters.AddWithValue("$swis", Jondo.Unity.Server.Network.SessionContext.State.ScrolledWisdom);
            command.Parameters.AddWithValue("$sstr", Jondo.Unity.Server.Network.SessionContext.State.ScrolledStrength);
            command.Parameters.AddWithValue("$sint", Jondo.Unity.Server.Network.SessionContext.State.ScrolledIntelligence);
            command.Parameters.AddWithValue("$scha", Jondo.Unity.Server.Network.SessionContext.State.ScrolledChance);
            command.Parameters.AddWithValue("$sagi", Jondo.Unity.Server.Network.SessionContext.State.ScrolledAgility);
            command.Parameters.AddWithValue("$lvl", Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel);
            command.Parameters.AddWithValue("$kamas", Jondo.Unity.Server.Network.SessionContext.State.Kamas);
            command.Parameters.AddWithValue("$xp", Jondo.Unity.Server.Network.SessionContext.State.Experience);
            command.ExecuteNonQuery();
        }

        public static void SaveCharacterLook(long characterId, byte[] lookBytes)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Characters SET Look = $look WHERE Id = $id;";
            command.Parameters.AddWithValue("$look", BitConverter.ToString(lookBytes).Replace("-", ""));
            command.Parameters.AddWithValue("$id", characterId);
            command.ExecuteNonQuery();
        }

        // --- Inventory Operations ---

        public static List<PlayerItem> LoadInventory(long characterId)
        {
            var list = new List<PlayerItem>();
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Uid, Gid, Quantity, Position, Effects FROM CharacterItems WHERE CharacterId = $charId;";
            command.Parameters.AddWithValue("$charId", characterId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = new PlayerItem
                {
                    Uid = reader.GetInt64(0),
                    ItemId = reader.GetInt32(1),
                    Quantity = reader.GetInt32(2),
                    Position = reader.GetInt32(3)
                };

                // Effects are stored as the client sends them, a list of lists:
                // [[effect, value, die, side], ...]. Here they were read as if they were a dictionary
                // {"138": 80}, which is SOMETHING ELSE: System.Text.Json choked, the catch swallowed
                // the exception and ALL items were left without effects. With that, the equipment sum
                // was zero for everything -- power, damage, strength, critical, AP and MP --, which is
                // why the character fought with 6 AP and 3 MP and hit as if naked, while the client's
                // panel did show the items fine, because it reads them elsewhere
                // (Managers.Equipment.ParseEffects).
                //
                // It is read with that same parser, which is the one that already knew the right shape.
                // The old dictionary shape is still accepted in case something was left stored that way.
                string jsonEffects = reader.IsDBNull(4) ? "" : reader.GetString(4);
                item.RawEffects = jsonEffects;
                if (!string.IsNullOrEmpty(jsonEffects))
                {
                    var parsed = Managers.Equipment.ParseEffects(jsonEffects);
                    if (parsed.Count > 0)
                    {
                        foreach (var effect in parsed)
                        {
                            // An item can repeat an effect; they are added, not overwritten.
                            item.Effects.TryGetValue(effect.Effect, out int already);
                            item.Effects[effect.Effect] = already + (int)effect.Value;
                        }
                    }
                    else
                    {
                        try
                        {
                            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(jsonEffects);
                            if (dict != null)
                            {
                                foreach (var kvp in dict) item.Effects[kvp.Key] = kvp.Value;
                            }
                        }
                        catch (Exception) { }
                    }
                }

                list.Add(item);
            }
            return list;
        }

        /// <summary>
        /// An item's effects ready to store, in the shape everybody expects:
        /// [[effect, value, die, side], ...].
        ///
        /// If the item came from the database they are returned as they arrived, without rebuilding them, so
        /// as not to lose the dice along the way. The list is only built when the item is new.
        /// </summary>
        private static string EffectsForStorage(PlayerItem item)
        {
            if (!string.IsNullOrEmpty(item.RawEffects)) return item.RawEffects;

            var lista = new List<int[]>();
            foreach (var kvp in item.Effects) lista.Add(new[] { kvp.Key, kvp.Value, 0, 0 });
            return System.Text.Json.JsonSerializer.Serialize(lista);
        }

        public static void SaveInventoryItem(long characterId, PlayerItem item)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            string jsonEffects = EffectsForStorage(item);

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO CharacterItems (CharacterId, Uid, Gid, Quantity, Position, Effects)
                VALUES ($charId, $uid, $gid, $qty, $pos, $effects)
                ON CONFLICT(Uid) DO UPDATE SET
                    Gid = $gid,
                    Quantity = $qty,
                    Position = $pos,
                    Effects = $effects
                WHERE CharacterItems.CharacterId = $charId;
            ";
            // The WHERE at the end is what keeps this from taking down ANOTHER character's row.
            // With the uid allocator fixed it should never collide, but if it ever collides again,
            // nothing happening is much better than turning somebody's Dofus into a piwi feather
            // without a peep.
            command.Parameters.AddWithValue("$charId", characterId);
            command.Parameters.AddWithValue("$uid", item.Uid);
            command.Parameters.AddWithValue("$gid", item.ItemId);
            command.Parameters.AddWithValue("$qty", item.Quantity);
            command.Parameters.AddWithValue("$pos", item.Position);
            command.Parameters.AddWithValue("$effects", jsonEffects);
            command.ExecuteNonQuery();
        }

        /// <summary>Is there already somebody with that name? Names are unique across the whole server.</summary>
        public static bool CharacterNameTaken(string name)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Characters WHERE Name = $n COLLATE NOCASE;";
                command.Parameters.AddWithValue("$n", name);
                return command.ExecuteScalar() is long n && n > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Creates a character with what comes factory fitted: level 1, the adventurer's set equipped, a
        /// million kamas and the characteristics the scrolls give.
        ///
        /// The identifier is the largest there is plus one. The uids of his items come from a band of their
        /// own per character, so that they do not collide with anybody's.
        /// </summary>
        public static long CreateCharacter(long accountId, int serverId, string name, int breed,
                                           int sex, int headId, IReadOnlyList<long> colors,
                                           long mapId, int level, long kamas, int stat,
                                           IReadOnlyList<(int Gid, int Slot)> starterSet)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var siguiente = connection.CreateCommand();
                siguiente.CommandText = "SELECT IFNULL(MAX(Id), 1000000) + 1 FROM Characters;";
                long id = siguiente.ExecuteScalar() is long max ? max : 1000001;

                // The cell: next to the zaap, not on it.
                var zaap = Managers.Interactives.ZaapOf(mapId);
                int cell = MapManager.GetNearestWalkableCell(mapId, zaap.Cell);

                // The colours the client sends as -1 are "the breed's"; they are stored empty and
                // BreedLookTable puts its own.
                var propios = new List<long>();
                foreach (long c in colors) if (c >= 0) propios.Add(c);
                byte[] look = Managers.BreedLookTable.BuildLook(breed, sex, headId,
                                                               propios.Count > 0 ? propios : null);

                // They are reserved before opening the transaction: NextItemUid queries the same
                // database with another connection the first time and SQLite must not find it locked here.
                var starterUids = new List<long>();
                foreach (var _ in starterSet) starterUids.Add(NextItemUid());

                using var transaction = connection.BeginTransaction();

                // The scrolls go in THEIR columns and the base starts at zero: the base is the points
                // the player spends, and a freshly made character has spent none.
                var insertar = connection.CreateCommand();
                insertar.CommandText = @"
                    INSERT INTO Characters
                        (Id, AccountId, Name, Breed, Sex, Level, MapId, CellId, RemainingPoints,
                         Vitality, Wisdom, Strength, Intelligence, Chance, Agility,
                         ScrolledVitality, ScrolledWisdom, ScrolledStrength,
                         ScrolledIntelligence, ScrolledChance, ScrolledAgility,
                         Look, Orientation, Kamas)
                    VALUES ($id, $acc, $name, $breed, $sex, $level, $map, $cell, 0,
                            0, 0, 0, 0, 0, 0,
                            $stat, $stat, $stat, $stat, $stat, $stat,
                            $look, 1, $kamas);";
                insertar.Parameters.AddWithValue("$id", id);
                insertar.Parameters.AddWithValue("$acc", accountId);
                insertar.Parameters.AddWithValue("$name", name);
                insertar.Parameters.AddWithValue("$breed", breed);
                insertar.Parameters.AddWithValue("$sex", sex);
                insertar.Parameters.AddWithValue("$level", level);
                insertar.Parameters.AddWithValue("$map", mapId);
                insertar.Parameters.AddWithValue("$cell", cell);
                insertar.Parameters.AddWithValue("$stat", stat);
                // In hexadecimal, which is how the ones already there store it. In base64 the loader
                // chokes: "Additional non-parsable characters are at the end of the string".
                insertar.Parameters.AddWithValue("$look", Convert.ToHexString(look));
                insertar.Parameters.AddWithValue("$kamas", kamas);
                insertar.ExecuteNonQuery();

                SetServerAndHead(connection, id, serverId, headId);

                int uidIndex = 0;
                foreach (var (gid, slot) in starterSet)
                {
                    var objeto = connection.CreateCommand();
                    objeto.CommandText = "INSERT INTO CharacterItems " +
                                         "(CharacterId, Uid, Gid, Quantity, Position, Effects) " +
                                         "VALUES ($id, $uid, $gid, 1, $pos, $e);";
                    objeto.Parameters.AddWithValue("$id", id);
                    objeto.Parameters.AddWithValue("$uid", starterUids[uidIndex++]);
                    objeto.Parameters.AddWithValue("$gid", gid);
                    objeto.Parameters.AddWithValue("$pos", slot);
                    objeto.Parameters.AddWithValue("$e", EffectsOfTemplate(connection, gid));
                    objeto.ExecuteNonQuery();
                }

                transaction.Commit();
                return id;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo crear el personaje: {ex.Message}");
                return 0;
            }
        }

        /// <summary>The server and the face, which do have a column of their own.</summary>
        private static void SetServerAndHead(SqliteConnection connection, long characterId,
                                             int serverId, int headId)
        {
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Characters SET ServerId = $s, HeadId = $h WHERE Id = $id;";
            command.Parameters.AddWithValue("$s", serverId);
            command.Parameters.AddWithValue("$h", headId);
            command.Parameters.AddWithValue("$id", characterId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// A template's factory effects, in the shape CharacterItems stores.
        ///
        /// They are not loose: the template carries in its Data a list of `rid` and each one points at an
        /// ItemEffects row with the effect, the value and the pair of dice. Without this, the adventurer's
        /// set would come out without a single characteristic.
        ///
        /// From the adventurer's hat come 118 (strength), 126 (intelligence), 119 (agility) and 123
        /// (chance), all four with die 5, which is "from 1 to 5". It is given the maximum, which is what a
        /// brand-new item should carry.
        /// </summary>
        private static string EffectsOfTemplate(SqliteConnection connection, int gid)
        {
            try
            {
                var leer = connection.CreateCommand();
                leer.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $gid;";
                leer.Parameters.AddWithValue("$gid", gid);
                if (leer.ExecuteScalar() is not string data) return "[]";

                using var doc = System.Text.Json.JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("possibleEffects", out var posibles)) return "[]";
                if (!posibles.TryGetProperty("Array", out var lista)) return "[]";

                var salida = new List<string>();
                foreach (var entrada in lista.EnumerateArray())
                {
                    if (!entrada.TryGetProperty("rid", out var rid)) continue;

                    var efecto = connection.CreateCommand();
                    efecto.CommandText = "SELECT EffectId, DiceNum, DiceSide, Value FROM ItemEffects " +
                                         "WHERE Rid = $rid;";
                    efecto.Parameters.AddWithValue("$rid", rid.GetInt64());

                    using var reader = efecto.ExecuteReader();
                    if (!reader.Read()) continue;

                    int id = reader.GetInt32(0);
                    int diceNum = reader.GetInt32(1);
                    int diceSide = reader.GetInt32(2);
                    int value = reader.GetInt32(3);
                    if (id == 0) continue;

                    // The brand-new value: the die's top if there is one, and if not, the fixed one.
                    int fijo = value != 0 ? value : (diceSide != 0 ? diceSide : diceNum);
                    salida.Add($"[{id},{fijo},0,0]");
                }
                return salida.Count > 0 ? "[" + string.Join(",", salida) + "]" : "[]";
            }
            catch { }
            return "[]";
        }

        /// <summary>
        /// Looks up an item template and rolls its factory effects at their maximum value.
        /// Returning false distinguishes a real effect-less item from an unknown template id.
        /// </summary>
        /// <summary>
        /// The level needed to wear an item. Zero if it is not known.
        /// </summary>
        /// <remarks>
        /// It comes from the template's <c>level</c> field, which ALL 21,748 carry: it does not have to be
        /// guessed for any. Eleven thousand two hundred and seventy-five ask for level 1 and the cap is 200.
        ///
        /// It returns zero when the template is not there or cannot be read, and whoever calls should treat it
        /// as "no requirement": refusing to equip because the lookup could not be done would leave somebody
        /// without his equipment over a database failure.
        /// </remarks>
        public static int ItemLevelRequirement(int gid)
        {
            if (gid <= 0) return 0;

            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $gid;";
                command.Parameters.AddWithValue("$gid", gid);

                if (command.ExecuteScalar() is not string data || data.Length == 0) return 0;

                using var doc = System.Text.Json.JsonDocument.Parse(data);
                return doc.RootElement.TryGetProperty("level", out var level)
                       && level.TryGetInt32(out int cuanto)
                    ? Math.Max(0, cuanto)
                    : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se ha podido leer el nivel del objeto {gid}: {ex.Message}");
                return 0;
            }
        }

        public static bool TryGetItemTemplateEffects(int gid, out string effects)
        {
            effects = "[]";
            if (gid <= 0) return false;

            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var exists = connection.CreateCommand();
                exists.CommandText = "SELECT 1 FROM ItemTemplates WHERE Id = $gid LIMIT 1;";
                exists.Parameters.AddWithValue("$gid", gid);
                if (exists.ExecuteScalar() == null) return false;

                effects = EffectsOfTemplate(connection, gid);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo leer la plantilla {gid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Puts a new item into somebody's inventory. The haven bag lottery uses it, which is the only thing
        /// that makes items out of nothing.
        /// </summary>
        public static bool InsertCharacterItem(long uid, long characterId, int gid, int quantity,
                                               int position, string? effects)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO CharacterItems " +
                                      "(CharacterId, Uid, Gid, Quantity, Position, Effects) " +
                                      "VALUES ($id, $uid, $gid, $n, $pos, $e);";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$gid", gid);
                command.Parameters.AddWithValue("$n", Math.Max(1, quantity));
                command.Parameters.AddWithValue("$pos", position);
                command.Parameters.AddWithValue("$e", (object?)effects ?? DBNull.Value);
                command.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo crear el objeto {uid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Rewrites how many there are of an item and what it carries: a crafted stack that grows,
        /// a rune that changes an item, a signature. Only on the owner's row, like every write here.
        /// </summary>
        public static bool UpdateCharacterItem(long characterId, long uid, int quantity, string effects)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "UPDATE CharacterItems SET Quantity = $n, Effects = $e " +
                                      "WHERE Uid = $uid AND CharacterId = $id;";
                command.Parameters.AddWithValue("$n", Math.Max(1, quantity));
                command.Parameters.AddWithValue("$e", effects);
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$id", characterId);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Could not rewrite item {uid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Destroys an inventory item, whole or by units. Returns whether anything was done.
        /// </summary>
        public static bool DestroyCharacterItem(long characterId, long uid, int quantity)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var leer = connection.CreateCommand();
                leer.CommandText = "SELECT Quantity FROM CharacterItems WHERE Uid = $uid AND CharacterId = $id;";
                leer.Parameters.AddWithValue("$uid", uid);
                leer.Parameters.AddWithValue("$id", characterId);
                if (leer.ExecuteScalar() is not long tiene) return false;

                var command = connection.CreateCommand();
                if (quantity <= 0 || quantity >= tiene)
                {
                    command.CommandText = "DELETE FROM CharacterItems WHERE Uid = $uid AND CharacterId = $id;";
                }
                else
                {
                    command.CommandText = "UPDATE CharacterItems SET Quantity = Quantity - $n " +
                                          "WHERE Uid = $uid AND CharacterId = $id;";
                    command.Parameters.AddWithValue("$n", quantity);
                }
                command.Parameters.AddWithValue("$uid", uid);
                command.Parameters.AddWithValue("$id", characterId);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo destruir el objeto {uid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Moves an item to another slot.
        ///
        /// Before this was «UPDATE CharacterItems SET Position = $pos WHERE Uid = $uid», without looking whose
        /// it is. The uid is unique across the whole server -- there is a unique index and they are handed out
        /// with a global MAX -- so it did not touch half the world's item, but it did let ANOTHER's be moved:
        /// the uid is chosen by the client and nothing was checked here, so an iuk with the number of somebody
        /// else's item moved it to another slot all the same. With the owner in front, a uid that is not yours
        /// finds no row and nothing happens.
        ///
        /// The owner is required on purpose -- it has no default value -- so that a new call that forgets to
        /// pass it does not even compile.
        /// </summary>
        public static bool SaveItemPosition(long uid, int position, long characterId)
        {
            if (characterId <= 0) return false;

            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE CharacterItems SET Position = $pos " +
                                  "WHERE Uid = $uid AND CharacterId = $id;";
            command.Parameters.AddWithValue("$pos", position);
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$id", characterId);
            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>Reads an item template's realWeight (pods) from the ItemTemplates Data JSON.</summary>
        /// <summary>
        /// What an item weighs, in pods. It is asked once per template and that is it.
        ///
        /// A template's weight never changes -- it comes from the client dump -- and this worked it out every
        /// time: it opened a SQLite connection, ran the query and parsed the template's whole JSON to get ONE
        /// number. Since the caller walks the inventory, dragging a single item opened 1,737 connections and
        /// parsed 1,737 JSONs, and the player felt the jolt.
        ///
        /// The dictionary is concurrent because several sessions touch it at once. Two may ask for the same
        /// template at the same instant and both go to the database: no harm, it is the same answer and the
        /// same is written. What cannot happen is the dictionary breaking inside, and ConcurrentDictionary
        /// takes care of that.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _pesoPorPlantilla
            = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();

        public static int GetItemRealWeight(int gid)
        {
            if (_pesoPorPlantilla.TryGetValue(gid, out int guardado)) return guardado;

            int peso = LeerPesoDeLaBase(gid);
            _pesoPorPlantilla[gid] = peso;
            return peso;
        }

        private static int LeerPesoDeLaBase(int gid)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $gid;";
            command.Parameters.AddWithValue("$gid", gid);
            if (command.ExecuteScalar() is not string data || string.IsNullOrEmpty(data))
                return 0;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("realWeight", out var weight))
                    return weight.TryGetInt32(out int w) ? w : (int)weight.GetDouble();
            }
            catch (Exception) { }
            return 0;
        }

        public static void ClearInventory(long characterId)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $charId;";
            command.Parameters.AddWithValue("$charId", characterId);
            command.ExecuteNonQuery();
        }

        public static void SeedInventory(long characterId, List<PlayerItem> items)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();
            try
            {
                // Create UNIQUE index on Uid to support INSERT ... ON CONFLICT
                var createIndex = connection.CreateCommand();
                createIndex.Transaction = transaction;
                createIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_items_uid ON CharacterItems(Uid);";
                createIndex.ExecuteNonQuery();

                foreach (var item in items)
                {
                    string jsonEffects = EffectsForStorage(item);
                    var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        INSERT OR REPLACE INTO CharacterItems (CharacterId, Uid, Gid, Quantity, Position, Effects)
                        VALUES ($charId, $uid, $gid, $qty, $pos, $effects);
                    ";
                    command.Parameters.AddWithValue("$charId", characterId);
                    command.Parameters.AddWithValue("$uid", item.Uid);
                    command.Parameters.AddWithValue("$gid", item.ItemId);
                    command.Parameters.AddWithValue("$qty", item.Quantity);
                    command.Parameters.AddWithValue("$pos", item.Position);
                    command.Parameters.AddWithValue("$effects", jsonEffects);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                Console.WriteLine($"[SQLite] Successfully seeded {items.Count} items into database for Character {characterId}.");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Console.WriteLine($"[-] Error seeding inventory: {ex.Message}");
            }
        }

        // --- Helpers ---

        private static byte[] ConvertHexStringToByteArray(string hex)
        {
            hex = hex.Replace("-", "");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        public static void GiveAllLevel200Items(long characterId)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            // First check if already given
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM CharacterItems WHERE CharacterId = $id AND ItemGid = 21081;"; // Examples
            checkCmd.Parameters.AddWithValue("$id", characterId);
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return;

            Console.WriteLine($"[SQLite] Giving all level 200 items to Character {characterId}...");

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, PossibleEffects FROM ItemTemplates WHERE Level = 200;";
            var itemsToAdd = new List<(int id, string effects)>();

            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string effects = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                    itemsToAdd.Add((id, effects));
                }
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO CharacterItems (CharacterId, ItemGid, Position, Quantity, Effects)
                    VALUES ($charId, $gid, 63, 1, $effects);
                ";
                insertCmd.Parameters.Add("$charId", SqliteType.Integer);
                insertCmd.Parameters.Add("$gid", SqliteType.Integer);
                insertCmd.Parameters.Add("$effects", SqliteType.Text);

                foreach (var item in itemsToAdd)
                {
                    insertCmd.Parameters["$charId"].Value = characterId;
                    insertCmd.Parameters["$gid"].Value = item.id;
                    insertCmd.Parameters["$effects"].Value = item.effects;
                    insertCmd.ExecuteNonQuery();
                }

                transaction.Commit();
                Console.WriteLine($"[SQLite] Successfully added {itemsToAdd.Count} level 200 items.");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Console.WriteLine($"[SQLite] Error adding level 200 items: {ex.Message}");
            }
        }

        public static byte[] ReconstructActorDetails(byte[] lookBytes, string name)
        {
            try
            {
                var statsMsg = new Network.ProtoMessage();

                // Field 1: breed & sex wrapper
                var breedSexMsg = new Network.ProtoMessage();
                breedSexMsg.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Server.Network.SessionContext.State.Breed > 0 ? Jondo.Unity.Server.Network.SessionContext.State.Breed : 8 });
                breedSexMsg.Fields.Add(new Network.ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = Jondo.Unity.Server.Network.SessionContext.State.Sex });
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 1, WireType = 2, BytesValue = breedSexMsg.ToByteArray() });

                // Field 2: Level
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel > 0 ? Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel : 2 });

                // Field 4: AccountId (using default 188940901L)
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 188940901L });

                // Field 5: alignment
                var alignMsg = new Network.ProtoMessage();
                alignMsg.Fields.Add(new Network.ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 1 });
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 5, WireType = 2, BytesValue = alignMsg.ToByteArray() });

                // Field 7: constant 1
                statsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = 1 });

                // Build lgk (Player Desc)
                var lgkMsg = new Network.ProtoMessage();
                lgkMsg.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 2, BytesValue = statsMsg.ToByteArray() });
                lgkMsg.Fields.Add(new Network.ProtoField { FieldNumber = 3, WireType = 2, BytesValue = System.Text.Encoding.UTF8.GetBytes(name) });

                // Build humanoidInfo wrapper (HumanInformations)
                var humanoidInfo = new Network.ProtoMessage();
                humanoidInfo.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 2, BytesValue = lgkMsg.ToByteArray() });

                // Build detailsMsg (lgx)
                var detailsMsg = new Network.ProtoMessage();
                if (lookBytes != null && lookBytes.Length > 0)
                {
                    detailsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 1, WireType = 2, BytesValue = lookBytes });
                }
                detailsMsg.Fields.Add(new Network.ProtoField { FieldNumber = 2, WireType = 2, BytesValue = humanoidInfo.ToByteArray() });

                return detailsMsg.ToByteArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error in ReconstructActorDetails: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public class NpcSpawn
        {
            public int Id { get; set; }
            public long MapId { get; set; }
            public int NpcId { get; set; }
            public int CellId { get; set; }
            public int Orientation { get; set; }
            public int BoneId { get; set; }
            public string Look { get; set; } = "";
        }

        public static List<NpcSpawn> GetNpcSpawnsForMap(long mapId)
        {
            var spawns = new List<NpcSpawn>();
            try
            {
                using (var connection = new SqliteConnection(WorldConnectionString))
                {
                    connection.Open();
                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT Id, MapId, NpcId, CellId, Orientation, BoneId, Look FROM NpcSpawns WHERE MapId = $mapId;";
                    command.Parameters.AddWithValue("$mapId", mapId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            spawns.Add(new NpcSpawn
                            {
                                Id = reader.GetInt32(0),
                                MapId = reader.GetInt64(1),
                                NpcId = reader.GetInt32(2),
                                CellId = reader.GetInt32(3),
                                Orientation = reader.GetInt32(4),
                                BoneId = reader.GetInt32(5),
                                Look = reader.IsDBNull(6) ? "" : reader.GetString(6)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error fetching NPC spawns for map {mapId}: {ex.Message}");
            }
            return spawns;
        }

        public static string GetNpcTemplateLook(int npcId)
        {
            try
            {
                using (var connection = new SqliteConnection(WorldConnectionString))
                {
                    connection.Open();
                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT Look FROM NpcTemplates WHERE Id = $npcId;";
                    command.Parameters.AddWithValue("$npcId", npcId);
                    var val = command.ExecuteScalar();
                    if (val != null && val != DBNull.Value)
                    {
                        return val.ToString() ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error fetching NPC template look for NPC {npcId}: {ex.Message}");
            }
            return "";
        }

        public static List<long> GetItemTemplatePossibleEffects(int itemId)
        {
            var rids = new List<long>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $itemId;";
                command.Parameters.AddWithValue("$itemId", itemId);
                var data = command.ExecuteScalar();
                if (data != null && data != DBNull.Value)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(data.ToString()!);
                    if (doc.RootElement.TryGetProperty("possibleEffects", out var possibleEffects))
                    {
                        if (possibleEffects.TryGetProperty("Array", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var element in arr.EnumerateArray())
                            {
                                if (element.TryGetProperty("rid", out var ridProp))
                                {
                                    rids.Add(ridProp.GetInt64());
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error parsing ItemTemplate {itemId} for possibleEffects: {ex.Message}");
            }
            return rids;
        }

        public class ItemEffectData
        {
            public int EffectId { get; set; }
            public int DiceNum { get; set; }
            public int DiceSide { get; set; }
            public int Value { get; set; }
        }

        public static List<ItemEffectData> GetItemEffectsData(List<long> rids)
        {
            var results = new List<ItemEffectData>();
            if (rids == null || rids.Count == 0) return results;

            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var parameters = string.Join(",", rids.Select((_, i) => $"$p{i}"));
                var command = connection.CreateCommand();
                command.CommandText = $"SELECT EffectId, DiceNum, DiceSide, Value FROM ItemEffects WHERE Rid IN ({parameters})";

                for (int i = 0; i < rids.Count; i++)
                {
                    command.Parameters.AddWithValue($"$p{i}", rids[i]);
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new ItemEffectData
                    {
                        EffectId = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                        DiceNum = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                        DiceSide = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        Value = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error fetching ItemEffectsData: {ex.Message}");
            }
            return results;
        }
        private static string? FindDataFile(string filename)
        {
            string[] candidates = new string[]
            {
                Path.Combine(Paths.DataDir, filename),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "dofus3_data", filename),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dofus3_data", filename),
                Path.Combine(@"..\dofus3_data", filename),
                filename
            };
            foreach (var path in candidates)
            {
                try { if (File.Exists(path)) return path; } catch { }
            }
            return null;
        }

        public static void PopulateMonstersFromJSON(SqliteConnection connection)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM Monsters;";
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return; // Already populated

            Console.WriteLine("[SQLite] Populating Monsters, Subareas, and MapSubareas from JSON. This may take a moment...");

            using var transaction = connection.BeginTransaction();
            try
            {
                // Monsters
                string? monstersPath = FindDataFile("monsters.json");
                if (!string.IsNullOrEmpty(monstersPath) && File.Exists(monstersPath))
                {
                    using var fs = new FileStream(monstersPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);

                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT OR REPLACE INTO Monsters (Id, NameId, Look, Grades, Spells) VALUES ($id, $nameId, $look, $grades, $spells);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$nameId", SqliteType.Integer);
                    insertCmd.Parameters.Add("$look", SqliteType.Text);
                    insertCmd.Parameters.Add("$grades", SqliteType.Text);
                    insertCmd.Parameters.Add("$spells", SqliteType.Text);

                    if (doc.RootElement.TryGetProperty("references", out var refsObj) && refsObj.TryGetProperty("RefIds", out var refIdsArr))
                    {
                        foreach (var item in refIdsArr.EnumerateArray())
                        {
                            if (!item.TryGetProperty("data", out var data)) continue;
                            int monsterId = data.TryGetProperty("id", out var mid) ? mid.GetInt32() : 0;
                            if (monsterId <= 0) continue;

                            int nameId = data.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                            string look = data.TryGetProperty("look", out var lk) ? lk.GetString() : "";
                            string grades = data.TryGetProperty("grades", out var gr) ? gr.GetRawText() : "[]";

                            string spellsJson = "[]";
                            if (data.TryGetProperty("spells", out var spellsProp))
                            {
                                if (spellsProp.ValueKind == System.Text.Json.JsonValueKind.Object && spellsProp.TryGetProperty("Array", out var spellArr))
                                    spellsJson = spellArr.GetRawText();
                                else if (spellsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    spellsJson = spellsProp.GetRawText();
                            }

                            insertCmd.Parameters["$id"].Value = monsterId;
                            insertCmd.Parameters["$nameId"].Value = nameId;
                            insertCmd.Parameters["$look"].Value = look ?? "";
                            insertCmd.Parameters["$grades"].Value = grades;
                            insertCmd.Parameters["$spells"].Value = spellsJson;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    else if (doc.RootElement.TryGetProperty("objectsById", out var objById))
                    {
                        var mValuesArr = objById.GetProperty("m_values").GetProperty("Array");
                        var mKeysArr = objById.GetProperty("m_keys").GetProperty("Array");

                        for (int i = 0; i < mKeysArr.GetArrayLength(); i++)
                        {
                            var monsterId = mKeysArr[i].GetInt32();
                            var data = mValuesArr[i].TryGetProperty("data", out var d) ? d : mValuesArr[i];
                            int nameId = data.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                            string look = data.TryGetProperty("look", out var lk) ? lk.GetString() : "";
                            string grades = data.TryGetProperty("grades", out var gr) ? gr.GetRawText() : "[]";

                            string spellsJson = "[]";
                            if (data.TryGetProperty("spells", out var spellsProp))
                            {
                                if (spellsProp.ValueKind == System.Text.Json.JsonValueKind.Object && spellsProp.TryGetProperty("Array", out var spellArr))
                                    spellsJson = spellArr.GetRawText();
                                else if (spellsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    spellsJson = spellsProp.GetRawText();
                            }

                            insertCmd.Parameters["$id"].Value = monsterId;
                            insertCmd.Parameters["$nameId"].Value = nameId;
                            insertCmd.Parameters["$look"].Value = look ?? "";
                            insertCmd.Parameters["$grades"].Value = grades;
                            insertCmd.Parameters["$spells"].Value = spellsJson;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                }

                // Subareas
                string? subareasPath = FindDataFile("subareas.json");
                if (!string.IsNullOrEmpty(subareasPath) && File.Exists(subareasPath))
                {
                    using var fs = new FileStream(subareasPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);
                    var mValuesArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_values").GetProperty("Array");
                    var mKeysArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_keys").GetProperty("Array");

                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT OR REPLACE INTO Subareas (Id, Monsters) VALUES ($id, $monsters);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$monsters", SqliteType.Text);

                    for (int i = 0; i < mKeysArr.GetArrayLength(); i++)
                    {
                        var subAreaId = mKeysArr[i].GetInt32();
                        var data = mValuesArr[i].GetProperty("data");
                        string monsters = data.TryGetProperty("monsters", out var mst) ? mst.GetRawText() : "[]";

                        insertCmd.Parameters["$id"].Value = subAreaId;
                        insertCmd.Parameters["$monsters"].Value = monsters;
                        insertCmd.ExecuteNonQuery();
                    }
                }

                // MapSubareas
                string? mapsPath = FindDataFile("maps_information.json");
                if (!string.IsNullOrEmpty(mapsPath) && File.Exists(mapsPath))
                {
                    using var fs = new FileStream(mapsPath, FileMode.Open, FileAccess.Read);
                    var doc = System.Text.Json.JsonDocument.Parse(fs);
                    var mValuesArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_values").GetProperty("Array");
                    var mKeysArr = doc.RootElement.GetProperty("objectsById").GetProperty("m_keys").GetProperty("Array");

                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT OR REPLACE INTO MapSubareas (MapId, SubAreaId) VALUES ($id, $subid);";
                    insertCmd.Parameters.Add("$id", SqliteType.Integer);
                    insertCmd.Parameters.Add("$subid", SqliteType.Integer);

                    for (int i = 0; i < mKeysArr.GetArrayLength(); i++)
                    {
                        long mapId = mKeysArr[i].GetInt64();
                        var data = mValuesArr[i].GetProperty("data");
                        int subAreaId = data.TryGetProperty("subAreaId", out var sid) ? sid.GetInt32() : 0;

                        insertCmd.Parameters["$id"].Value = mapId;
                        insertCmd.Parameters["$subid"].Value = subAreaId;
                        insertCmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                Console.WriteLine("[SQLite] Successfully populated JSON data.");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Console.WriteLine($"[-] Error populating from JSON: {ex.Message}");
            }
        }

        public static void EnsureSpellsSeeded(SqliteConnection connection)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM Spells;";
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return;

            Console.WriteLine("[DatabaseManager] Auto-seeding Spells, SpellLevels, and SpellVariants from JSON...");

            string basePath = Paths.DataDir;
            if (!Directory.Exists(basePath))
            {
                basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dofus3_data");
            }

            if (!Directory.Exists(basePath)) return;

            // Seed Spells
            string spellsPath = Path.Combine(basePath, "spells.json");
            if (File.Exists(spellsPath))
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(spellsPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("references", out var refs) && refs.TryGetProperty("RefIds", out var refIds))
                    {
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO Spells (Id, NameId, DescriptionId, IconId, TypeId) VALUES ($id, $nid, $did, $iid, $tid);";
                        insertCmd.Parameters.Add("$id", SqliteType.Integer);
                        insertCmd.Parameters.Add("$nid", SqliteType.Integer);
                        insertCmd.Parameters.Add("$did", SqliteType.Integer);
                        insertCmd.Parameters.Add("$iid", SqliteType.Integer);
                        insertCmd.Parameters.Add("$tid", SqliteType.Integer);

                        foreach (var item in refIds.EnumerateArray())
                        {
                            if (!item.TryGetProperty("data", out var d)) continue;
                            int id = d.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0;
                            if (id <= 0) continue;
                            insertCmd.Parameters["$id"].Value = id;
                            insertCmd.Parameters["$nid"].Value = d.TryGetProperty("nameId", out var nid) ? nid.GetInt32() : 0;
                            insertCmd.Parameters["$did"].Value = d.TryGetProperty("descriptionId", out var did) ? did.GetInt32() : 0;
                            insertCmd.Parameters["$iid"].Value = d.TryGetProperty("iconId", out var iid) ? iid.GetInt32() : 0;
                            insertCmd.Parameters["$tid"].Value = d.TryGetProperty("typeId", out var tid) ? tid.GetInt32() : 0;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch { transaction.Rollback(); }
            }

            // Seed SpellLevels
            string slPath = Path.Combine(basePath, "spell_levels.json");
            if (File.Exists(slPath))
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(slPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("references", out var refs) && refs.TryGetProperty("RefIds", out var refIds))
                    {
                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO SpellLevels (Id, SpellId, Grade, MinPlayerLevel, APCost, MinRange, MaxRange, CastInLine, MaxCastPerTurn, MaxCastPerTarget, EffectsJson) VALUES ($id, $sid, $grade, $mpl, $ap, $minr, $maxr, $cil, $mcpt, $mcptg, $fx);";
                        insertCmd.Parameters.Add("$id", SqliteType.Integer);
                        insertCmd.Parameters.Add("$sid", SqliteType.Integer);
                        insertCmd.Parameters.Add("$grade", SqliteType.Integer);
                        insertCmd.Parameters.Add("$mpl", SqliteType.Integer);
                        insertCmd.Parameters.Add("$ap", SqliteType.Integer);
                        insertCmd.Parameters.Add("$minr", SqliteType.Integer);
                        insertCmd.Parameters.Add("$maxr", SqliteType.Integer);
                        insertCmd.Parameters.Add("$cil", SqliteType.Integer);
                        insertCmd.Parameters.Add("$mcpt", SqliteType.Integer);
                        insertCmd.Parameters.Add("$mcptg", SqliteType.Integer);
                        insertCmd.Parameters.Add("$fx", SqliteType.Text);

                        foreach (var item in refIds.EnumerateArray())
                        {
                            if (!item.TryGetProperty("data", out var d)) continue;
                            int id = d.TryGetProperty("id", out var slid) ? slid.GetInt32() : 0;
                            if (id <= 0) continue;

                            string fxJson = "[]";
                            if (d.TryGetProperty("effects", out var fxObj) && fxObj.TryGetProperty("Array", out var fxArr))
                            {
                                fxJson = fxArr.GetRawText();
                            }

                            insertCmd.Parameters["$id"].Value = id;
                            insertCmd.Parameters["$sid"].Value = d.TryGetProperty("spellId", out var sid) ? sid.GetInt32() : 0;
                            insertCmd.Parameters["$grade"].Value = d.TryGetProperty("grade", out var g) ? g.GetInt32() : 1;
                            insertCmd.Parameters["$mpl"].Value = d.TryGetProperty("minPlayerLevel", out var mpl) ? mpl.GetInt32() : 0;
                            insertCmd.Parameters["$ap"].Value = d.TryGetProperty("apCost", out var ap) ? ap.GetInt32() : 3;
                            insertCmd.Parameters["$minr"].Value = d.TryGetProperty("minRange", out var minr) ? minr.GetInt32() : 0;
                            insertCmd.Parameters["$maxr"].Value = d.TryGetProperty("range", out var maxr) ? maxr.GetInt32() : 1;
                            insertCmd.Parameters["$cil"].Value = d.TryGetProperty("castInLine", out var cil) && cil.GetBoolean() ? 1 : 0;
                            insertCmd.Parameters["$mcpt"].Value = d.TryGetProperty("maxCastPerTurn", out var mcpt) ? mcpt.GetInt32() : 0;
                            insertCmd.Parameters["$mcptg"].Value = d.TryGetProperty("maxCastPerTarget", out var mcptg) ? mcptg.GetInt32() : 0;
                            insertCmd.Parameters["$fx"].Value = fxJson;
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch { transaction.Rollback(); }
            }

            // Seed SpellVariants
            string svPath = Path.Combine(basePath, "spell_variants.json");
            if (File.Exists(svPath))
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(svPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("references", out var refs) && refs.TryGetProperty("RefIds", out var refIds))
                    {
                        var breedMap = new Dictionary<int, List<int>>();
                        foreach (var item in refIds.EnumerateArray())
                        {
                            if (!item.TryGetProperty("data", out var d)) continue;
                            int bid = d.TryGetProperty("breedId", out var b) ? b.GetInt32() : 0;
                            if (bid <= 0) continue;
                            if (!breedMap.ContainsKey(bid)) breedMap[bid] = new List<int>();

                            if (d.TryGetProperty("spellIds", out var sObj) && sObj.TryGetProperty("Array", out var sArr))
                            {
                                foreach (var sid in sArr.EnumerateArray()) breedMap[bid].Add(sid.GetInt32());
                            }
                        }

                        var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT OR REPLACE INTO SpellVariants (BreedId, SpellIdsJson) VALUES ($bid, $json);";
                        insertCmd.Parameters.Add("$bid", SqliteType.Integer);
                        insertCmd.Parameters.Add("$json", SqliteType.Text);

                        foreach (var kvp in breedMap)
                        {
                            insertCmd.Parameters["$bid"].Value = kvp.Key;
                            insertCmd.Parameters["$json"].Value = System.Text.Json.JsonSerializer.Serialize(kvp.Value.Distinct().ToList());
                            insertCmd.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch { transaction.Rollback(); }
            }
        }

        // =========================================================================
        // COMBAT DATA QUERIES
        // =========================================================================

        /// <summary>
        /// Retrieves the real stats for a monster at a specific grade index from the Monsters table.
        /// Returns null if the monster or grade is not found.
        /// </summary>
        public static MonsterGradeStats? GetMonsterGradeStats(int monsterId, int gradeIndex)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Grades, Spells FROM Monsters WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", monsterId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            string gradesJson = reader.GetString(0);
            string spellsRaw = reader.IsDBNull(1) ? "[]" : reader.GetString(1);

            var stats = new MonsterGradeStats();

            // Parse Grades JSON
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(gradesJson);
                var root = doc.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Array", out var arrProp))
                    root = arrProp;

                if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var grades = root.EnumerateArray().ToList();
                    int idx = Math.Clamp(gradeIndex, 0, grades.Count - 1);
                    var g = grades[idx];

                    stats.Level = g.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
                    stats.LifePoints = g.TryGetProperty("lifePoints", out var hp) ? hp.GetInt32() : 50;
                    stats.ActionPoints = g.TryGetProperty("actionPoints", out var ap) ? ap.GetInt32() : 6;
                    stats.MovementPoints = g.TryGetProperty("movementPoints", out var mp) ? mp.GetInt32() : 3;
                    stats.Strength = g.TryGetProperty("strength", out var str) ? str.GetInt32() : 0;
                    stats.Intelligence = g.TryGetProperty("intelligence", out var intl) ? intl.GetInt32() : 0;
                    stats.Chance = g.TryGetProperty("chance", out var cha) ? cha.GetInt32() : 0;
                    stats.Agility = g.TryGetProperty("agility", out var agi) ? agi.GetInt32() : 0;
                    stats.Wisdom = g.TryGetProperty("wisdom", out var wis) ? wis.GetInt32() : 0;
                    stats.PaDodge = g.TryGetProperty("paDodge", out var pad) ? pad.GetInt32() : 0;
                    stats.PmDodge = g.TryGetProperty("pmDodge", out var pmd) ? pmd.GetInt32() : 0;
                    if (g.TryGetProperty("bonusCharacteristics", out var bonus))
                    {
                        stats.TackleEvadeBonus = bonus.TryGetProperty("tackleEvade", out var te) ? te.GetInt32() : 0;
                        stats.TackleBlockBonus = bonus.TryGetProperty("tackleBlock", out var tb) ? tb.GetInt32() : 0;
                    }
                    stats.NeutralResistance = g.TryGetProperty("neutralResistance", out var nr) ? nr.GetInt32() : 0;
                    stats.EarthResistance = g.TryGetProperty("earthResistance", out var er) ? er.GetInt32() : 0;
                    stats.FireResistance = g.TryGetProperty("fireResistance", out var fr) ? fr.GetInt32() : 0;
                    stats.WaterResistance = g.TryGetProperty("waterResistance", out var wr) ? wr.GetInt32() : 0;
                    stats.AirResistance = g.TryGetProperty("airResistance", out var ar) ? ar.GetInt32() : 0;
                    stats.GradeXp = g.TryGetProperty("gradeXp", out var xp) ? xp.GetInt32() : 100;
                    // The startingSpellId does NOT go here, and putting it in cost 2,051 monsters their
                    // spells.
                    //
                    // It is a SpellLevels.Id -- a spell LEVEL id -- and not a Spells.Id. The repository
                    // itself has it written right in Summons.cs:209, which translates it with
                    // «SELECT SpellId, Grade FROM SpellLevels WHERE Id = $id».
                    //
                    // Put in raw, two things happened, and the second is the poisonous one: GetSpellCombatData
                    // did not find that number and returned null, and above all the list stopped being
                    // empty, so the fallback further down -- the one that reads the REAL spells from
                    // MonsterTemplates -- no longer ran. A useless id cancelled the ones that were good.
                    //
                    // Measured: 2,133 of 5,134 monsters carry startingSpellId, and 1,975 of those match no
                    // spell. In total 2,051 of 5,134 -- 40% -- were left unable to cast anything. The Royal
                    // Jalamut was one.
                    //
                    // And even translated properly, it is not its attack list either: it is the behaviour
                    // spell it starts with. The attacks are MonsterTemplates.spells. It is kept apart in
                    // case it is ever needed, and not mixed in.
                    if (g.TryGetProperty("startingSpellId", out var ssp) && ssp.GetInt32() > 0)
                    {
                        stats.StartingSpellLevelId = ssp.GetInt32();
                    }
                }
            }
            catch { }

            // Parse Spells (can be in the Monsters row or from the grades)
            try
            {
                // Spells are stored as a JSON string, could be "[626, 4195]" or similar
                if (!string.IsNullOrEmpty(spellsRaw))
                {
                    using var spellDoc = System.Text.Json.JsonDocument.Parse(spellsRaw);
                    var spellRoot = spellDoc.RootElement;
                    if (spellRoot.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var s in spellRoot.EnumerateArray())
                        {
                            if (s.ValueKind == System.Text.Json.JsonValueKind.Number)
                                stats.SpellIds.Add(s.GetInt32());
                        }
                    }
                }
            }
            catch { }

            // The Monsters.Spells column is empty for all 5134 monsters: the importer never filled
            // it in. The spells are in MonsterTemplates.Data though, along with the grade that
            // applies to each one. Without this the AI ended up with no spells and fell back to a
            // made-up one with range 6, so no monster ever had a reason to move.
            //
            //   spells      : {"Array":[626, 4195]}
            //   spellGrades : {"Array":["3,11;3,12;...", "1,11;1,12;..."]}
            //
            // spellGrades[i] describes the spell spells[i]: one "grade,level" entry per monster
            // grade, separated by ';'.
            //
            // It is ALWAYS read. It used to sit behind an «if the list is empty», and that guard
            // protected nothing: Monsters.Spells is '[]' in all 5,134 monsters, so the list could
            // only be filled with the startingSpellId, which is not a spell. All the guard achieved
            // was leaving without spells the monster whose grade carried that number. Repeating
            // does have to be avoided, and the Contains below takes care of that.
            {
                try
                {
                    var tplCmd = connection.CreateCommand();
                    tplCmd.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
                    tplCmd.Parameters.AddWithValue("$id", monsterId);
                    string? tplJson = tplCmd.ExecuteScalar() as string;

                    if (!string.IsNullOrEmpty(tplJson))
                    {
                        using var tplDoc = System.Text.Json.JsonDocument.Parse(tplJson);
                        var tplRoot = tplDoc.RootElement;

                        static List<System.Text.Json.JsonElement> UnwrapArray(System.Text.Json.JsonElement parent, string name)
                        {
                            if (!parent.TryGetProperty(name, out var prop)) return new List<System.Text.Json.JsonElement>();
                            if (prop.ValueKind == System.Text.Json.JsonValueKind.Object && prop.TryGetProperty("Array", out var inner))
                                prop = inner;
                            if (prop.ValueKind != System.Text.Json.JsonValueKind.Array) return new List<System.Text.Json.JsonElement>();
                            return prop.EnumerateArray().ToList();
                        }

                        var spellElems = UnwrapArray(tplRoot, "spells");
                        var gradeElems = UnwrapArray(tplRoot, "spellGrades");

                        for (int i = 0; i < spellElems.Count; i++)
                        {
                            if (spellElems[i].ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                            int sid = spellElems[i].GetInt32();
                            if (sid <= 0) continue;

                            int spellGrade = 1;
                            if (i < gradeElems.Count && gradeElems[i].ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                string[] perGrade = (gradeElems[i].GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
                                if (perGrade.Length > 0)
                                {
                                    string entry = perGrade[Math.Clamp(gradeIndex, 0, perGrade.Length - 1)];
                                    string[] parts = entry.Split(',');
                                    if (parts.Length > 0 && int.TryParse(parts[0], out int g) && g > 0) spellGrade = g;
                                }
                            }

                            if (!stats.SpellIds.Contains(sid)) stats.SpellIds.Add(sid);
                            stats.SpellGrades[sid] = spellGrade;
                        }
                    }
                }
                catch { }
            }

            if (stats.SpellIds.Count == 0)
            {
                Program.LogDebug($"[DatabaseManager] WARN: monster {monsterId} has no spells in Monsters nor in MonsterTemplates.");
            }

            return stats;
        }

        // Effect catalogue (Effects table, imported from data_assets_effectsdataroot). It says
        // which characteristic each effectId touches; without it nothing but damage can be applied.
        private static Dictionary<int, int>? _effectCharacteristics;

        /// <summary>
        /// For each effect, which characteristic it touches and with what sign, according to the client's
        /// catalogue.
        ///
        /// The sign comes from the DESCRIPTION, not from the BonusType. It looks cruder and it is the other
        /// way round: the BonusType cannot be trusted. 1079, which is the one that steals AP -- "-#1 to -#2
        /// AP" --, has it at ZERO, the same as 101; with that rule Flecha Helada stole nothing. The
        /// description, on the other hand, is the template the client itself writes the effect on screen
        /// with, and the ones that subtract all start with a hyphen: 1079, range's 116 and MP's 169.
        ///
        /// And category 2 ones are left out, which are the WEAPON's: 101 points at action points, but it is
        /// what hitting with it costs, not points that are earned.
        /// </summary>
        public static (int Characteristic, int Sign) EffectMeta(int effectId)
        {
            LoadEffectCatalogue();
            return _effectMeta!.TryGetValue(effectId, out var meta) ? meta : (0, 0);
        }

        private static Dictionary<int, (int Characteristic, int Sign)>? _effectMeta;

        /// <summary>
        /// An effect's FAMILY: its category and whether it is a bonus, exactly as the client's catalogue
        /// declares them.
        ///
        /// With these two numbers the client decides whether a buff is drawn in the effects panel or whether
        /// it is internal machinery that is not shown. They go in their own dictionary, without filtering by
        /// characteristic, because the ones needed here -- 950 that sets a state, 792 that chains spells, 293
        /// of basic damage -- have no characteristic of their own.
        /// </summary>
        public static (int Category, int Boost) EffectFamily(int effectId)
        {
            if (_effectFamily == null)
            {
                var mapa = new Dictionary<int, (int, int)>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id, Category, Boost FROM Effects;";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                    {
                        mapa[rd.GetInt32(0)] = (rd.IsDBNull(1) ? 0 : rd.GetInt32(1),
                                                rd.IsDBNull(2) ? 0 : rd.GetInt32(2));
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudo leer la familia de los efectos: {ex.Message}");
                }
                _effectFamily = mapa;
            }
            return _effectFamily.TryGetValue(effectId, out var familia) ? familia : (0, 0);
        }

        private static Dictionary<int, (int Category, int Boost)>? _effectFamily;

        /// <summary>
        /// Which element an effect hits with, according to the catalogue: 0 neutral, 1 earth, 2 fire, 3
        /// water and 4 air. Minus one when the effect hits with no element.
        /// </summary>
        public static int EffectElement(int effectId)
        {
            if (_effectElement == null)
            {
                var mapa = new Dictionary<int, int>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id, ElementId FROM Effects;";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) mapa[rd.GetInt32(0)] = rd.IsDBNull(1) ? -1 : rd.GetInt32(1);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudo leer el elemento de los efectos: {ex.Message}");
                }
                _effectElement = mapa;
            }
            return _effectElement.TryGetValue(effectId, out int elemento) ? elemento : -1;
        }

        private static Dictionary<int, int>? _effectElement;

        /// <summary>The category of the effects that describe the weapon, not the character.</summary>
        private const int WeaponEffectCategory = Jondo.Unity.World.Combat.EffectSupport.WeaponCategory;

        // ─── The ones that STEAL points ────────────────────────────────────────

        private static Dictionary<int, int>? _roboDePuntos;

        /// <summary>
        /// Which characteristic a steal effect steals, or zero if it steals nothing.
        ///
        /// They are a family apart and that is why room is made for them: the four -- 77 and 441 for
        /// movement points, 84 and 440 for action points -- carry <c>Characteristic = 0</c> and
        /// <c>Category = 2</c> in the table, so both filters of the general catalogue ate them and they never
        /// reached the engine. The result on screen was that Flecha Inmovilizadora, instead of taking a
        /// movement point off the piwi, hung on it a buff literally called "Roba 1 PM" that did nothing.
        ///
        /// Which one they steal is said by their own description, which is where the catalogue already takes
        /// the others' sign from: "Roba #1 a #2 PM" against "Roba #1 a #2 PA". There is no hand-written list.
        /// </summary>
        public static int RoboDePuntos(int effectId)
        {
            if (_roboDePuntos == null)
            {
                var mapa = new Dictionary<int, int>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id, Description FROM Effects " +
                                      "WHERE Description LIKE 'Roba %';";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                    {
                        string texto = rd.IsDBNull(1) ? "" : rd.GetString(1).TrimEnd();
                        if (texto.EndsWith("PM", StringComparison.Ordinal))
                            mapa[rd.GetInt32(0)] = MovementPointsCharacteristic;
                        else if (texto.EndsWith("PA", StringComparison.Ordinal))
                            mapa[rd.GetInt32(0)] = ActionPointsCharacteristic;
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudieron leer los robos de puntos: {ex.Message}");
                }
                _roboDePuntos = mapa;
            }
            return _roboDePuntos.TryGetValue(effectId, out int cual) ? cual : 0;
        }

        private const int ActionPointsCharacteristic = 1;
        private const int MovementPointsCharacteristic = 23;

        // ─── The ones that MULTIPLY ────────────────────────────────────────────

        private static HashSet<int>? _multiplicadores;

        /// <summary>
        /// Whether an effect multiplies instead of adding.
        /// </summary>
        /// <remarks>
        /// They are recognised by their description, which has the shape "… x#1%": 1163 is "Daños sufridos
        /// x#1%" (damage taken) and 1159 "Curas recibidas x#1%" (heals received). Neither has a
        /// characteristic in the catalogue, because the client resolves them by their number, and that is why
        /// they do not fit the engine's ordinary path.
        /// </remarks>
        public static bool EsMultiplicador(int effectId)
        {
            if (_multiplicadores == null)
            {
                var lista = new HashSet<int>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id FROM Effects WHERE Description LIKE '% x#1%';";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) lista.Add(rd.GetInt32(0));
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] No se pudieron leer los multiplicadores: {ex.Message}");
                }
                _multiplicadores = lista;
            }
            return _multiplicadores.Contains(effectId);
        }

        private static void LoadEffectCatalogue()
        {
            if (_effectMeta != null) return;
            var meta = new Dictionary<int, (int, int)>();
            try
            {
                using var conn = new SqliteConnection(WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Id, Characteristic, Category, Description FROM Effects " +
                                  "WHERE Characteristic > 0;";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    int category = rd.IsDBNull(2) ? 0 : rd.GetInt32(2);
                    if (category == WeaponEffectCategory) continue;

                    string description = rd.IsDBNull(3) ? "" : rd.GetString(3);
                    int sign = description.TrimStart().StartsWith("-") ? -1 : 1;
                    meta[rd.GetInt32(0)] = (rd.GetInt32(1), sign);
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DatabaseManager] No se pudo leer el catálogo de efectos: {ex.Message}");
            }
            _effectMeta = meta;
        }

        private static int GetEffectCharacteristic(int effectId)
        {
            if (_effectCharacteristics == null)
            {
                var map = new Dictionary<int, int>();
                try
                {
                    using var conn = new SqliteConnection(WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    // No filtering by UseInFight: that column is 0 for exactly the effects we care
                    // about (1079, which removes AP, and 116, which removes range) and only 31
                    // rows in the whole table have it set to 1, so it does not mean "used in
                    // combat". With the filter in place, Frozen Arrow never took the 2 AP away.
                    cmd.CommandText = "SELECT Id, Characteristic FROM Effects WHERE Characteristic > 0;";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) map[rd.GetInt32(0)] = rd.IsDBNull(1) ? 0 : rd.GetInt32(1);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[DatabaseManager] Could not load the Effects table: {ex.Message}");
                }
                _effectCharacteristics = map;
            }
            return _effectCharacteristics.TryGetValue(effectId, out int c) ? c : 0;
        }

        /// <summary>
        /// The equipped weapon, expressed as if it were a spell, so that a weapon hit goes down
        /// the same path as a regular cast.
        ///
        /// AP cost and range come from the item template; damage comes from the effects rolled on
        /// that particular instance. Effects 91-95 (steal) and 96-100 (damage) carry their element
        /// in the client's own effect table, so no hand-written mapping is needed.
        /// </summary>
        public static SpellCombatData? GetEquippedWeaponAsSpell(long characterId)
        {
            const int WeaponSlot = 1;
            var weapon = LoadInventory(characterId).FirstOrDefault(i => i.Position == WeaponSlot);
            if (weapon == null) return null;

            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", weapon.ItemId);
            string? json = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(json)) return null;

            var data = new SpellCombatData
            {
                SpellId = 0,
                SpellLevelId = 0,
                APCost = 3,
                MinRange = 1,
                MaxRange = 1,
                BaseDamageMin = 0,
                BaseDamageMax = 0,
                NeedsLineOfSight = true
            };

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("apCost", out var ap)) data.APCost = ap.GetInt32();
                if (root.TryGetProperty("minRange", out var mr)) data.MinRange = mr.GetInt32();
                if (root.TryGetProperty("range", out var r)) data.MaxRange = r.GetInt32();
                if (root.TryGetProperty("criticalHitProbability", out var cp)) data.CriticalHitProbability = cp.GetInt32();
            }
            catch { }

            // The damage of the weapon he wears.
            //
            // CAREFUL WITH WHERE IT COMES FROM. In the stored instance, a damage effect is
            // «[96, 0, 9, 13]»: 96 is the effect, 0 is the VALUE -- which for damage goes empty --
            // and 9 and 13 are the DICE, which is where the real damage is.
            //
            // This read weapon.Effects, which is an effect→value dictionary and so has already lost
            // the dice along the way. With the value at zero, the condition «kv.Value <= 0» discarded
            // ALL the damage effects, the weapon was left with zero, and GolpeDelArma returned an
            // empty list: that is why attacking with the sword came out as a punch.
            //
            // It is read from RawEffects, which is the json as it came, with the same parser that
            // already knows its shape.
            //
            // And ALL the lines are kept, not only the one doing most damage. There used to be an
            // «if (maximo <= data.BaseDamageMax) continue;» that kept the largest and threw away the
            // others, so a three-line weapon hit only once. Cangrancio's Lanzapinza has
            // [[96,0,9,13],[96,0,9,13],[91,0,27,33]] and only the last survived: ONE figure came
            // out in the chat where three should have.
            //
            // The real server sends one hit per line, each with ITS effect number: the critical
            // Cocobur sends three (2822, 91, 93), the Lavacha two (97, 92) and the Claws two (96, 94).
            //
            // The loose fields -- Element, BaseDamageMin and BaseDamageMax -- are still filled with
            // the biggest line, because they are the ones the AI looks at to estimate damage and the
            // largest is fine for that.
            foreach (var efecto in Managers.Equipment.ParseEffects(weapon.RawEffects))
            {
                if (efecto.Effect < 91 || efecto.Effect > 100) continue;

                int minimo = (int)(efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value);
                int maximo = (int)(efecto.DiceSide != 0 ? efecto.DiceSide : minimo);
                if (minimo <= 0 && maximo <= 0) continue;
                if (maximo < minimo) maximo = minimo;

                var elemCmd = connection.CreateCommand();
                elemCmd.CommandText = "SELECT ElementId FROM Effects WHERE Id = $id;";
                elemCmd.Parameters.AddWithValue("$id", efecto.Effect);
                object? elem = elemCmd.ExecuteScalar();
                if (elem == null || elem == DBNull.Value) continue;

                int elemento = Convert.ToInt32(elem);
                data.WeaponLines.Add((efecto.Effect, elemento, minimo, maximo));

                if (maximo <= data.BaseDamageMax) continue;
                data.Element = elemento;
                data.BaseDamageMin = minimo;
                data.BaseDamageMax = maximo;
            }

            return data.BaseDamageMin > 0 ? data : null;
        }

        /// <summary>
        /// The spells the character has available at its level.
        ///
        /// SpellVariants.SpellIdsJson comes INTERLEAVED: base spell, its variant, base spell, its
        /// variant... (checked against the localized spell names, which alternate between a base
        /// arrow spell and its variant). We keep the even indices and filter by each spell's
        /// minimum level.
        ///
        /// Both the combat jvn and the roleplay shortcut bar use this, so the player sees the
        /// same list in either place.
        /// </summary>
        public static List<int> GetPlayerAvailableSpells(int breedId, int level)
        {
            return GetBreedSpellIds(breedId)
                .Where((_, index) => index % 2 == 0)
                .Where(id => GetSpellMinPlayerLevel(id) <= level)
                .ToList();
        }

        /// <summary>
        /// A monster's loot table, already resolved for the given grade.
        ///
        /// It comes from MonsterTemplates.Data → drops[], where every entry carries the item and
        /// its per-grade probability (percentDropForGrade1..5). Taking the Red Piwi as an example:
        /// red piwi feather at 100 %, sesame seeds at 18 %, pouch of lemons at 3 %.
        ///
        /// Entries with criteria (`hasCriterions`) are discarded, because they are conditional
        /// — quest or achievement drops — and their criteria language ("Qo=13820&amp;PO!19649&amp;…") is
        /// not implemented. Never dropping them is preferable to always dropping them.
        /// </summary>
        public static List<MonsterDrop> GetMonsterDrops(int monsterId, int gradeIndex)
        {
            var drops = new List<MonsterDrop>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", monsterId);
                string? json = cmd.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(json)) return drops;

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("drops", out var dropsProp)) return drops;
                if (dropsProp.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    dropsProp.TryGetProperty("Array", out var inner)) dropsProp = inner;
                if (dropsProp.ValueKind != System.Text.Json.JsonValueKind.Array) return drops;

                // percentDropForGrade1..5; grades above the fifth reuse the last one.
                string percentKey = "percentDropForGrade" + Math.Clamp(gradeIndex + 1, 1, 5);

                foreach (var e in dropsProp.EnumerateArray())
                {
                    if (e.TryGetProperty("hasCriterions", out var hc) && hc.GetInt32() != 0) continue;
                    if (!e.TryGetProperty("objectId", out var oid)) continue;

                    double pct = 0;
                    if (e.TryGetProperty(percentKey, out var p)) pct = p.GetDouble();
                    else if (e.TryGetProperty("percentDropForGrade1", out var p1)) pct = p1.GetDouble();
                    if (pct <= 0) continue;

                    drops.Add(new MonsterDrop { ObjectId = oid.GetInt32(), PercentDrop = pct });
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DatabaseManager] Error reading the loot table of monster {monsterId}: {ex.Message}");
            }
            return drops;
        }

        /// <summary>
        /// The other loot table a monster has: the global one, the same for every grade.
        /// </summary>
        /// <remarks>
        /// A monster carries two lists and they are not the same thing. <c>drops</c> is its own,
        /// with a percentage per grade; <c>globalDrops</c> is what it hands out on top of that, one
        /// percentage for everyone, and it is where the seasonal and the RAID loot lives -- every
        /// one of the Abyss monsters carries its salt and its gems here and nothing at all in the
        /// other list, so a raid where nothing drops is a raid that never read this.
        ///
        /// Each row brings a criterion saying who may receive it, and it is left for the caller to
        /// answer rather than filtered here: the criterion asks where the player is standing and
        /// what raid he is in, and that is not something a database reader knows.
        ///
        /// MEASURED: 55 raid-resource rows across nine monsters, all of them with min and max
        /// alike and none of them with a criterion, so the two ends are the same number and the
        /// minimum is what goes out. Where the two ends differ -- the anomaly fragment, at 1 to
        /// 20 -- the row always carries a criterion we cannot satisfy, so nothing is decided here
        /// on a guess.
        /// </remarks>
        public static List<MonsterDrop> GetMonsterGlobalDrops(int monsterId)
        {
            var drops = new List<MonsterDrop>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", monsterId);
                string? json = cmd.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(json)) return drops;

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("globalDrops", out var list)) return drops;
                if (list.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    list.TryGetProperty("Array", out var inner)) list = inner;
                if (list.ValueKind != System.Text.Json.JsonValueKind.Array) return drops;

                foreach (var e in list.EnumerateArray())
                {
                    if (!e.TryGetProperty("objectId", out var oid)) continue;
                    int objectId = oid.GetInt32();

                    // Rows with item -1: they do not hand out an item, they hand out an alteration, and
                    // none of that is done. They are passed over instead of putting a nonexistent item in
                    // the bag.
                    if (objectId <= 0) continue;

                    double pct = 0;
                    if (e.TryGetProperty("minPercentDrop", out var min)) pct = min.GetDouble();
                    if (pct <= 0) continue;

                    string criterion = e.TryGetProperty("receiverCriterion", out var c)
                        ? (c.GetString() ?? "") : "";

                    drops.Add(new MonsterDrop
                    {
                        ObjectId = objectId,
                        PercentDrop = pct,
                        ReceiverCriterion = criterion,
                    });
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DatabaseManager] Error reading the global loot table of monster " +
                                 $"{monsterId}: {ex.Message}");
            }
            return drops;
        }

        /// <summary>
        /// Puts an item into the inventory. If one of the same kind is already loose in the bag,
        /// it adds to that stack instead of creating another entry. Returns the resulting item.
        /// </summary>
        /// <summary>Stores a profession's experience. It is called on every gathering.</summary>
        public static void SaveJobExperience(long characterId, int jobId, long experience)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO CharacterJobs (CharacterId, JobId, Experience)
                    VALUES ($c, $j, $e)
                    ON CONFLICT(CharacterId, JobId) DO UPDATE SET Experience = $e;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$j", jobId);
                command.Parameters.AddWithValue("$e", experience);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se ha podido guardar el oficio {jobId}: {ex.Message}");
            }
        }

        /// <summary>A character's dream, as JSON, over the one it had.</summary>
        public static void SaveDream(long characterId, string json)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO CharacterDreams (CharacterId, Json, UpdatedAt) VALUES ($c, $j, $t)
                    ON CONFLICT(CharacterId) DO UPDATE SET Json = $j, UpdatedAt = $t;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$j", json);
                command.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Could not save the dream of {characterId}: {ex.Message}");
            }
        }

        /// <summary>A character's dream, over: won, or lost with no arena left.</summary>
        public static void DeleteDream(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM CharacterDreams WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Could not delete the dream of {characterId}: {ex.Message}");
            }
        }

        /// <summary>A character's dream as JSON, or null when it has none.</summary>
        public static string? LoadDream(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Json FROM CharacterDreams WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                return command.ExecuteScalar() as string;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Could not read the dream of {characterId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>One job's artisan settings, written as they change.</summary>
        public static void SaveCrafterSetting(long characterId, int jobId, Handlers.ArtisanHandler.Setting setting)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO CharacterCrafterSettings (CharacterId, JobId, MinLevel, Free, Listed)
                    VALUES ($c, $j, $m, $f, $l)
                    ON CONFLICT(CharacterId, JobId) DO UPDATE SET MinLevel = $m, Free = $f, Listed = $l;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$j", jobId);
                command.Parameters.AddWithValue("$m", setting.MinLevel);
                command.Parameters.AddWithValue("$f", setting.Free ? 1 : 0);
                command.Parameters.AddWithValue("$l", setting.Listed ? 1 : 0);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Could not save the artisan settings of job {jobId}: {ex.Message}");
            }
        }

        /// <summary>A character's artisan settings, job by job.</summary>
        public static Dictionary<int, Handlers.ArtisanHandler.Setting> LoadCrafterSettings(long characterId)
        {
            var settings = new Dictionary<int, Handlers.ArtisanHandler.Setting>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT JobId, MinLevel, Free, Listed FROM CharacterCrafterSettings WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    settings[reader.GetInt32(0)] = new Handlers.ArtisanHandler.Setting(
                        reader.GetInt32(1), reader.GetInt32(2) != 0, reader.GetInt32(3) != 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Could not read the artisan settings: {ex.Message}");
            }
            return settings;
        }

        /// <summary>One quest's progress as the database holds it.</summary>
        public readonly struct QuestRow
        {
            public QuestRow(int questId, int stepId, string objectives, bool completed)
            {
                QuestId = questId;
                StepId = stepId;
                Objectives = objectives;
                Completed = completed;
            }

            public int QuestId { get; }
            public int StepId { get; }

            /// <summary>The ticked objectives of the step in hand, comma separated.</summary>
            public string Objectives { get; }

            public bool Completed { get; }
        }

        /// <summary>
        /// Writes where a character has got to on one quest.
        /// </summary>
        /// <remarks>
        /// Write-through, called in the same breath as the in-memory change, the way
        /// <see cref="SaveJobExperience"/> is. There is no periodic autosave anywhere in this
        /// server and <c>SaveCurrentCharacter</c> only writes the Characters row, so anything that
        /// waits for logout is lost on a crash — and losing a quest people have spent an evening on
        /// is worse than losing a few kamas.
        /// </remarks>
        public static void SaveQuestProgress(long characterId, QuestRow quest)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO CharacterQuests (CharacterId, QuestId, StepId, Objectives, Completed)
                    VALUES ($c, $q, $s, $o, $d)
                    ON CONFLICT(CharacterId, QuestId) DO UPDATE
                        SET StepId = $s, Objectives = $o, Completed = $d;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$q", quest.QuestId);
                command.Parameters.AddWithValue("$s", quest.StepId);
                command.Parameters.AddWithValue("$o", quest.Objectives ?? "");
                command.Parameters.AddWithValue("$d", quest.Completed ? 1 : 0);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] La misión {quest.QuestId} no se ha podido guardar: {ex.Message}");
            }
        }

        /// <summary>A character's quest log, for putting back on when they come in.</summary>
        public static List<QuestRow> LoadQuestProgress(long characterId)
        {
            var salida = new List<QuestRow>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT QuestId, StepId, Objectives, Completed FROM CharacterQuests " +
                    "WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    salida.Add(new QuestRow(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.GetInt32(3) != 0));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se han podido leer las misiones: {ex.Message}");
            }
            return salida;
        }

        private static volatile bool _progressionTables;

        /// <summary>
        /// The tables achievements and emotes keep, created if they are not there.
        /// </summary>
        /// <remarks>
        /// Its own method, and called by the readers and writers below as well as by
        /// <see cref="Initialize"/>, because a base fresh out of <c>datos/world.zip</c> has none of
        /// them: that is the schema the continuous integration tests against.
        ///
        /// CharacterAchievementCounters holds the tallies the achievement data counts and nothing
        /// else keeps: monsters beaten (EM), monsters beaten with a challenge won (Ef), subareas
        /// entered (Xs), items crafted (Xc) and the day of the last Almanax offering (Ax). Kind is
        /// the operator the achievement data uses, or a two-letter key of ours where it names none.
        ///
        /// CharacterEmotes holds the emotes a character has learned beyond the four every
        /// character starts with.
        /// </remarks>
        public static void EnsureProgressionTables()
        {
            if (_progressionTables) return;
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterAchievements (
                        CharacterId INTEGER NOT NULL,
                        AchievementId INTEGER NOT NULL,
                        Claimed INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, AchievementId)
                    );
                    CREATE TABLE IF NOT EXISTS CharacterAchievementCounters (
                        CharacterId INTEGER NOT NULL,
                        Kind TEXT NOT NULL,
                        Key INTEGER NOT NULL,
                        Count INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (CharacterId, Kind, Key)
                    );
                    CREATE TABLE IF NOT EXISTS CharacterEmotes (
                        CharacterId INTEGER NOT NULL,
                        EmoteId INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, EmoteId)
                    );";
                command.ExecuteNonQuery();
                _progressionTables = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Las tablas de logros y actitudes no se han podido crear: {ex.Message}");
            }
        }

        /// <summary>A character's achievement tallies, by kind and key.</summary>
        public static Dictionary<(string Kind, long Key), long> LoadAchievementCounters(long characterId)
        {
            EnsureProgressionTables();
            var tallies = new Dictionary<(string, long), long>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT Kind, Key, Count FROM CharacterAchievementCounters WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) tallies[(reader.GetString(0), reader.GetInt64(1))] = reader.GetInt64(2);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se han podido leer los contadores de logros: {ex.Message}");
            }
            return tallies;
        }

        /// <summary>Writes one tally as it now stands.</summary>
        public static void SaveAchievementCounter(long characterId, string kind, long key, long count)
        {
            EnsureProgressionTables();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO CharacterAchievementCounters (CharacterId, Kind, Key, Count)
                    VALUES ($c, $k, $key, $n)
                    ON CONFLICT(CharacterId, Kind, Key) DO UPDATE SET Count = $n;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$k", kind);
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$n", count);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] El contador {kind}/{key} no se ha podido guardar: {ex.Message}");
            }
        }

        /// <summary>The emotes a character has learned, beyond the starting ones.</summary>
        public static HashSet<int> LoadEmotes(long characterId)
        {
            EnsureProgressionTables();
            var emotes = new HashSet<int>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT EmoteId FROM CharacterEmotes WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) emotes.Add(reader.GetInt32(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se han podido leer las actitudes: {ex.Message}");
            }
            return emotes;
        }

        /// <summary>Writes down that a character has learned an emote.</summary>
        public static void SaveEmote(long characterId, int emoteId)
        {
            EnsureProgressionTables();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO CharacterEmotes (CharacterId, EmoteId) VALUES ($c, $e)
                    ON CONFLICT(CharacterId, EmoteId) DO NOTHING;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$e", emoteId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] La actitud {emoteId} no se ha podido guardar: {ex.Message}");
            }
        }

        /// <summary>Writes down that a character has an achievement, and whether it was paid.</summary>
        public static void SaveAchievement(long characterId, int achievementId, bool claimed)
        {
            EnsureProgressionTables();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO CharacterAchievements (CharacterId, AchievementId, Claimed)
                    VALUES ($c, $a, $d)
                    ON CONFLICT(CharacterId, AchievementId) DO UPDATE SET Claimed = $d;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$a", achievementId);
                command.Parameters.AddWithValue("$d", claimed ? 1 : 0);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] El logro {achievementId} no se ha podido guardar: {ex.Message}");
            }
        }

        /// <summary>A character's achievements: the id and whether the reward was taken.</summary>
        public static List<(int Achievement, bool Claimed)> LoadAchievements(long characterId)
        {
            EnsureProgressionTables();
            var salida = new List<(int, bool)>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT AchievementId, Claimed FROM CharacterAchievements WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) salida.Add((reader.GetInt32(0), reader.GetInt32(1) != 0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se han podido leer los logros: {ex.Message}");
            }
            return salida;
        }

        /// <summary>A character's professions, to leave them in place on entering.</summary>
        public static Dictionary<int, long> LoadJobExperience(long characterId)
        {
            var salida = new Dictionary<int, long>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT JobId, Experience FROM CharacterJobs WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) salida[reader.GetInt32(0)] = reader.GetInt64(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se han podido leer los oficios: {ex.Message}");
            }
            return salida;
        }

        /// <summary>
        /// The achievement challenges this character has already met, so as not to offer them again.
        ///
        /// Today it always returns empty, and that is correct: there is still nothing checking during the
        /// fight whether a challenge is met, so there is nobody to note anything for yet. The table already
        /// exists so that the day that check is in place only <see cref="MarkChallengeDone"/> has to be called.
        /// </summary>
        public static HashSet<int> LoadChallengesDone(long characterId)
        {
            var salida = new HashSet<int>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT ChallengeId FROM CharacterChallenges WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) salida.Add(reader.GetInt32(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se han podido leer los retos cumplidos: {ex.Message}");
            }
            return salida;
        }

        /// <summary>Notes an achievement challenge as met. It will not be offered to him again.</summary>
        public static void MarkChallengeDone(long characterId, int challengeId)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO CharacterChallenges (CharacterId, ChallengeId)
                    VALUES ($c, $r)
                    ON CONFLICT(CharacterId, ChallengeId) DO NOTHING;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$r", challengeId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se ha podido apuntar el reto {challengeId}: {ex.Message}");
            }
        }

        /// <summary>The map the database says for a character. Only for diagnostics.</summary>
        public static long MapOf(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT MapId FROM Characters WHERE Id = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                object? valor = command.ExecuteScalar();
                return valor == null ? 0 : Convert.ToInt64(valor);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// The next free uid. One for the WHOLE server, and atomic.
        ///
        /// This fixed a bug that ate items. The uid is unique across the whole table -- there is a unique index
        /// and SaveInventoryItem does ON CONFLICT(Uid) DO UPDATE -- but it was handed out looking at ONE
        /// character's inventory: «the largest uid I have, plus one». With two new characters that gives 1 to
        /// both, and the second to loot does not add his item: he OVERWRITES the first one's. Checked on a copy
        /// of the database: Ana loots a feather and gets uid 1; Beto loots a seed, uid 1 comes out again, and
        /// the row becomes «Ana's character, Beto's item». Ana loses the feather and Beto gets nothing, and not
        /// a single error fires.
        ///
        /// The other two allocators there were -- NpcHandler and Lottery -- read MAX(Uid) from the database each
        /// time, which was correct but had its own race: two purchases at the same instant read the same
        /// maximum and return the same number. Now all three go through here, the database is asked ONCE and
        /// from then on it is a counter.
        ///
        /// The floor is in case the table is empty; if there is something, it carries on above whatever
        /// exists, whatever band it is in.
        /// </summary>
        private const long PrimerUidRepartido = 1_000_000_000L;

        /// <summary>
        /// The 3.6 client reduces the inventory uid to 32 bits. We also stay in the positive half so that no
        /// layer treating it as a signed int can change it.
        /// </summary>
        public const long MaxClientItemUid = int.MaxValue;

        private static long _ultimoUidRepartido;
        private static readonly object _candadoDelUid = new object();

        /// <summary>
        /// Puts the keyring in the bag of every character who does not have it.
        /// </summary>
        /// <remarks>
        /// New characters receive it with the adventurer's set. This is for those who already existed: without
        /// the keyring one gets into none of the 107 dungeons that accept it except by crafting its loose key,
        /// and that part of the game stayed closed to them.
        ///
        /// Idempotent on purpose: it looks at who does NOT have it before giving anything, so starting the
        /// server twice does not hand out two keyrings. And if somebody throws it away, the next start gives
        /// it back, which for a quest item that is not spent is the right thing.
        /// </remarks>
        private static void DarElManojoALosQueYaEstaban(SqliteConnection connection)
        {
            const int Manojo = Handlers.DungeonHandler.Keyring;

            try
            {
                var faltan = new List<long>();

                using (var buscar = connection.CreateCommand())
                {
                    buscar.CommandText =
                        "SELECT c.Id FROM Characters c WHERE NOT EXISTS " +
                        "(SELECT 1 FROM CharacterItems i WHERE i.CharacterId = c.Id AND i.Gid = $gid);";
                    buscar.Parameters.AddWithValue("$gid", Manojo);

                    using var reader = buscar.ExecuteReader();
                    while (reader.Read()) faltan.Add(reader.GetInt64(0));
                }

                if (faltan.Count == 0) return;

                string efectos = EffectsOfTemplate(connection, Manojo);

                foreach (long personaje in faltan)
                {
                    using var dar = connection.CreateCommand();
                    dar.CommandText = "INSERT INTO CharacterItems " +
                                      "(CharacterId, Uid, Gid, Quantity, Position, Effects) " +
                                      "VALUES ($id, $uid, $gid, 1, $pos, $e);";
                    dar.Parameters.AddWithValue("$id", personaje);
                    dar.Parameters.AddWithValue("$uid", NextItemUid());
                    dar.Parameters.AddWithValue("$gid", Manojo);
                    dar.Parameters.AddWithValue("$pos", Managers.Equipment.Bag);
                    dar.Parameters.AddWithValue("$e", efectos);
                    dar.ExecuteNonQuery();
                }

                Console.WriteLine($"[SQLite] Manojo de llaves repartido a {faltan.Count} personaje(s) " +
                                  "que no lo tenian.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se ha podido repartir el manojo de llaves: {ex.Message}");
            }
        }

        public static long NextItemUid()
        {
            if (System.Threading.Interlocked.Read(ref _ultimoUidRepartido) == 0)
            {
                lock (_candadoDelUid)
                {
                    if (_ultimoUidRepartido == 0)
                        _ultimoUidRepartido = Math.Max(MayorUidEnUso(), PrimerUidRepartido);
                }
            }

            long next = System.Threading.Interlocked.Increment(ref _ultimoUidRepartido);
            if (next > MaxClientItemUid)
                throw new InvalidOperationException("No quedan uid de objeto compatibles con el cliente.");
            return next;
        }

        /// <summary>The largest uid written in the database. The regression guard uses it.</summary>
        public static long MayorUidGuardado() => MayorUidEnUso();

        /// <summary>
        /// Makes sure no uid handed out from now on is at or below <paramref name="highest"/>.
        /// </summary>
        /// <remarks>
        /// For the tables that keep items outside CharacterItems, the bank's first: the dispenser
        /// starts above the highest uid in CharacterItems and nothing else, so an item that went
        /// into the bank with the highest uid of all would have its number handed out again on the
        /// next start. Whoever owns such a table tells the dispenser its highest uid once.
        /// </remarks>
        public static void KeepUidsAbove(long highest)
        {
            if (highest <= 0) return;

            if (System.Threading.Interlocked.Read(ref _ultimoUidRepartido) == 0)
            {
                lock (_candadoDelUid)
                {
                    if (_ultimoUidRepartido == 0)
                        _ultimoUidRepartido = Math.Max(MayorUidEnUso(), PrimerUidRepartido);
                }
            }

            long seen;
            do
            {
                seen = System.Threading.Interlocked.Read(ref _ultimoUidRepartido);
                if (seen >= highest) return;
            }
            while (System.Threading.Interlocked.CompareExchange(ref _ultimoUidRepartido, highest, seen) != seen);
        }

        private static long MayorUidEnUso()
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT MAX(Uid) FROM CharacterItems " +
                                      "WHERE Uid > 0 AND Uid <= $max;";
                command.Parameters.AddWithValue("$max", MaxClientItemUid);
                return command.ExecuteScalar() is long max ? max : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo leer el mayor uid en uso: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Fixes the uids that came from `characterId * 1000` and do not fit in the 32 bits the client keeps.
        ///
        /// Measured on our own database: 10 of 1,777 items were like that, all from the three characters whose
        /// id goes over two million. Item 13,825,560,000 reached the client as 940,658,112 -- which is the same
        /// number cut to 32 bits -- and on sending it back to equip it the server did not recognise it. The
        /// sheet was left without that piece's effects and the log said «not one of ours».
        ///
        /// And it was not only the past: since NextItemUid started at the largest uid of the table, with a
        /// 13,825,560,013 written the next item the server made -- a loot, a purchase, the haven bag -- was born
        /// already broken. That is why the largest uid query is now bounded to the client's range.
        ///
        /// The row changes neither owner, nor template, nor place, nor effects: the only thing changed is the
        /// number it travels with.
        /// </summary>
        private static void RepairClientItemUids(SqliteConnection connection)
        {
            var invalidRows = new List<(long Id, long CharacterId)>();
            var find = connection.CreateCommand();
            find.CommandText = "SELECT Id, CharacterId FROM CharacterItems " +
                               "WHERE Uid <= 0 OR Uid > $max ORDER BY Id;";
            find.Parameters.AddWithValue("$max", MaxClientItemUid);
            using (var reader = find.ExecuteReader())
            {
                while (reader.Read()) invalidRows.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }

            if (invalidRows.Count == 0) return;

            var highest = connection.CreateCommand();
            highest.CommandText = "SELECT MAX(Uid) FROM CharacterItems " +
                                  "WHERE Uid > 0 AND Uid <= $max;";
            highest.Parameters.AddWithValue("$max", MaxClientItemUid);
            long next = highest.ExecuteScalar() is long used
                ? Math.Max(used, PrimerUidRepartido)
                : PrimerUidRepartido;

            if (next + invalidRows.Count > MaxClientItemUid)
                throw new InvalidOperationException(
                    "No quedan uid de 32 bits libres para arreglar CharacterItems.");

            using var transaction = connection.BeginTransaction();
            foreach (var row in invalidRows)
            {
                var update = connection.CreateCommand();
                update.Transaction = transaction;
                // The CharacterId stays in the filter even though the Id is already unique: that way
                // every inventory write keeps the ownership condition the guard checks at start-up, and
                // nobody who copies this query later can forget it.
                update.CommandText = "UPDATE CharacterItems SET Uid = $uid " +
                                     "WHERE Id = $id AND CharacterId = $character;";
                update.Parameters.AddWithValue("$uid", ++next);
                update.Parameters.AddWithValue("$id", row.Id);
                update.Parameters.AddWithValue("$character", row.CharacterId);
                update.ExecuteNonQuery();
            }
            transaction.Commit();

            // If the allocator had already been queried, it has to carry on behind the numbers this
            // repair has just used.
            System.Threading.Interlocked.Exchange(ref _ultimoUidRepartido, next);
            Console.WriteLine($"[SQLite] {invalidRows.Count} uid de objeto que no cabían en 32 bits, arreglados.");
        }

        /// <summary>
        /// How many items are written with a uid the client cannot give back whole.
        ///
        /// The regression guard uses it. It always has to be zero: the ones there were got fixed by
        /// <see cref="RepairClientItemUids"/> at start-up, and new ones come from <see cref="NextItemUid"/>,
        /// which does not hand out above the cap. If this grows, somebody is writing uids on his own.
        /// </summary>
        public static int ObjetosConUidFueraDelCliente()
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM CharacterItems " +
                                      "WHERE Uid <= 0 OR Uid > $max;";
                command.Parameters.AddWithValue("$max", MaxClientItemUid);
                return Convert.ToInt32(command.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] No se pudo contar los uid fuera de rango: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Several items at once, reading the inventory ONCE.
        ///
        /// AddItemToInventory loads the whole inventory to know whether you already have one of those and stack
        /// it. That is fine for a loose item, but a fight's loot calls once per different item, so a fight that
        /// drops five things loaded the complete inventory five times -- 1,737 rows on the capture's account --
        /// right at the moment the player is waiting for the reward screen.
        /// </summary>
        public static List<PlayerItem> AddItemsToInventory(long characterId,
                                                           IReadOnlyDictionary<int, int> items)
        {
            var tocados = new List<PlayerItem>();
            if (items == null || items.Count == 0) return tocados;

            var inventory = LoadInventory(characterId);

            foreach (var kv in items)
            {
                var existing = inventory.FirstOrDefault(i => i.ItemId == kv.Key && i.Position == 63);
                if (existing != null)
                {
                    existing.Quantity += kv.Value;
                    SaveInventoryItem(characterId, existing);
                    tocados.Add(existing);
                    continue;
                }

                var nuevo = new PlayerItem
                {
                    Uid = NextItemUid(),
                    ItemId = kv.Key,
                    Quantity = kv.Value,
                    Position = 63
                };
                SaveInventoryItem(characterId, nuevo);

                // To the list in memory as well: if the same loot brings the same item twice -- it does
                // not happen today, but the dictionary does not prevent it -- the second has to stack on
                // the first and not create another row.
                inventory.Add(nuevo);
                tocados.Add(nuevo);
            }

            // They are returned with the uid and the quantity THAT IS LEFT, which is what is needed
            // to tell the client they have dropped: without the uid the notice cannot be built, and
            // without it the item stays invisible until the next login.
            return tocados;
        }

        public static PlayerItem AddItemToInventory(long characterId, int itemGid, int quantity)
        {
            var inventory = LoadInventory(characterId);
            var existing = inventory.FirstOrDefault(i => i.ItemId == itemGid && i.Position == 63);

            if (existing != null)
            {
                existing.Quantity += quantity;
                SaveInventoryItem(characterId, existing);
                return existing;
            }

            var item = new PlayerItem
            {
                Uid = NextItemUid(),
                ItemId = itemGid,
                Quantity = quantity,
                Position = 63
            };
            SaveInventoryItem(characterId, item);
            return item;
        }

        /// <summary>
        /// What a spell does at a grade: cost, range, damage, effects.
        ///
        /// It is kept per (spell, grade), as SpellEffects already did. The SpellLevels table does not change
        /// while the server is up, and this is asked MANY times: every time a monster decides what to cast it
        /// walks all its spells, and each pass opened a SQLite connection and parsed two JSONs -- the normal one
        /// and the critical one -- to get exactly the same again.
        ///
        /// The null is kept too: a spell that is not in the table will not appear later either, and without
        /// that the bad case -- the one asked most often -- kept going to the database on every turn.
        ///
        /// CAREFUL: what comes out of here is shared by everybody, so it is NOT TOUCHED. The four places that
        /// use it only read, and there is a guard (SecurityGuardTests) that fires if somebody starts writing to
        /// the returned object: changing one's cost would change it for everybody, in every fight at once, and
        /// that would give no error anywhere.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), SpellCombatData?> _hechizoPorGrado
            = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), SpellCombatData?>();

        public static SpellCombatData? GetSpellCombatData(int spellId, int grade = 1)
        {
            if (_hechizoPorGrado.TryGetValue((spellId, grade), out var guardado)) return guardado;

            var leido = LeerHechizoDeLaBase(spellId, grade);
            _hechizoPorGrado[(spellId, grade)] = leido;
            return leido;
        }

        private static SpellCombatData? LeerHechizoDeLaBase(int spellId, int grade)
        {
            using var connection = new SqliteConnection(WorldConnectionString);
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, APCost, MinRange, MaxRange, EffectsJson, CastTestLos, CastInLine, MaxCastPerTurn, MaxCastPerTarget, CriticalHitProbability, CriticalEffectsJson FROM SpellLevels WHERE SpellId = $sid AND Grade = $g LIMIT 1;";
            cmd.Parameters.AddWithValue("$sid", spellId);
            cmd.Parameters.AddWithValue("$g", grade);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                // Fallback: try grade 1
                cmd.Parameters["$g"].Value = 1;
                reader.Close();
                using var reader2 = cmd.ExecuteReader();
                if (!reader2.Read()) return null;
                return ParseSpellCombatData(reader2);
            }
            return ParseSpellCombatData(reader);
        }

        private static SpellCombatData ParseSpellCombatData(SqliteDataReader reader)
        {
            var data = new SpellCombatData
            {
                SpellLevelId = reader.GetInt32(0),
                APCost = reader.GetInt32(1),
                MinRange = reader.GetInt32(2),
                MaxRange = reader.GetInt32(3),
                NeedsLineOfSight = reader.IsDBNull(5) || reader.GetInt32(5) == 1,
                CastInLine = !reader.IsDBNull(6) && reader.GetInt32(6) == 1,
                MaxCastPerTurn = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                MaxCastPerTarget = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                CriticalHitProbability = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
            };

            // Critical hit damage. It comes in its own effect list: Frozen Arrow does 12-14 water
            // normally and 15-17 on a critical, with the same -2 AP.
            try
            {
                string critJson = reader.IsDBNull(10) ? "" : reader.GetString(10);
                if (!string.IsNullOrEmpty(critJson))
                {
                    using var critDoc = System.Text.Json.JsonDocument.Parse(critJson);
                    if (critDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var e in critDoc.RootElement.EnumerateArray())
                        {
                            int eid = e.TryGetProperty("effectId", out var ce) ? ce.GetInt32() : 0;
                            if (eid < 96 || eid > 100) continue;
                            data.CriticalDamageMin = e.TryGetProperty("diceNum", out var cdn) ? cdn.GetInt32() : 0;
                            data.CriticalDamageMax = e.TryGetProperty("diceSide", out var cds) ? cds.GetInt32() : 0;
                            break;
                        }
                    }
                }
            }
            catch { }

            string effectsJson = reader.IsDBNull(4) ? "[]" : reader.GetString(4);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(effectsJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        int effectId = e.TryGetProperty("effectId", out var eid) ? eid.GetInt32() : 0;
                        // effectIds 96-100 are damage effects (96=Water, 97=Earth, 98=Air, 99=Fire, 100=Neutral)
                        if (effectId >= 96 && effectId <= 100)
                        {
                            data.BaseDamageMin = e.TryGetProperty("diceNum", out var dn) ? dn.GetInt32() : 5;
                            data.BaseDamageMax = e.TryGetProperty("diceSide", out var ds) ? ds.GetInt32() : 10;

                            // The element is given by the effect itself (effectElement). It is the
                            // same number the client expects in the damage packet: spell 13425
                            // carries effectElement=2 and the official capture sends f25.f1=2.
                            // The switch on effectId is only used when the field is missing.
                            data.Element = e.TryGetProperty("effectElement", out var ee)
                                ? ee.GetInt32()
                                : effectId switch
                                {
                                    96 => 3,  // Water
                                    97 => 1,  // Earth
                                    98 => 4,  // Air
                                    99 => 2,  // Fire
                                    100 => 0, // Neutral
                                    _ => 0
                                };
                            continue; // keep going: a spell can deal damage and push as well
                        }

                        int dice = e.TryGetProperty("diceNum", out var dnum) ? dnum.GetInt32() : 0;
                        int dur = e.TryGetProperty("duration", out var dur0) ? dur0.GetInt32() : 0;

                        // 5 = push, 6 = pull; the number of cells travels in diceNum.
                        // Repelling Arrow (32426) is 98 (air 15-17) + 5 (push of 2).
                        if (effectId == 5 || effectId == 6)
                        {
                            if (dice > 0) data.PushDistance = effectId == 5 ? dice : -dice;
                            continue;
                        }

                        // Effect 293: raises the base damage of one specific spell for a few turns
                        // ("Frozen Arrow: +4 base damage - 3 turns"). The affected spell travels
                        // in diceNum, the bonus in value and the turns in duration.
                        if (effectId == 293)
                        {
                            int affectedSpellId = dice;
                            int bonus = e.TryGetProperty("value", out var val293) ? val293.GetInt32() : 0;
                            if (affectedSpellId > 0 && bonus != 0)
                            {
                                data.DamageBuffs.Add(new SpellDamageBuff
                                {
                                    SpellId = affectedSpellId,
                                    Bonus = bonus,
                                    Duration = dur > 0 ? dur : 1
                                });
                            }
                            continue;
                        }

                        // Any other effect that touches a characteristic of the target. The
                        // characteristic comes from the client's effect catalogue, not from a
                        // hand-written list: Frozen Arrow's 1079 removes AP (characteristic 1)
                        // and the piwi's 116 removes range (characteristic 19).
                        int characteristic = GetEffectCharacteristic(effectId);
                        if (characteristic > 0 && dice != 0)
                        {
                            // The sign is the catalogue's own, read off the description: a bonus adds, a "-" row
                            // takes away. Everything was a removal before, and a monster's own
                            // buffs -- +MP, +damage, power -- never counted as buffs to it.
                            data.StatEffects.Add(new SpellStatEffect
                            {
                                EffectId = effectId,
                                Characteristic = characteristic,
                                Value = EffectMeta(effectId).Sign < 0 ? -dice : dice,
                                Duration = dur
                            });
                        }
                    }
                }
            }
            catch { }

            return data;
        }

        public static List<int> GetBreedSpellIds(int breedId)
        {
            var spells = new List<int>();
            try
            {
                using var conn = new SqliteConnection(WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT SpellIdsJson FROM SpellVariants WHERE BreedId = @breedId;";
                cmd.Parameters.AddWithValue("@breedId", breedId);
                var result = cmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(result))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        spells.Add(elem.GetInt32());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error fetching breed spells for breed {breedId}: {ex.Message}");
            }

            if (spells.Count == 0)
            {
                // No made-up fallback: if the breed has no spells in the database that is a data
                // problem and it has to be visible, not papered over with another class's ids.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[DatabaseManager][WARN] Breed {breedId} has no spells in SpellVariants. " +
                                  "The character will end up with no spells in combat.");
                Console.ResetColor();
            }
            return spells;
        }

        /// <summary>
        /// Minimum character level at which a spell unlocks, per SpellLevels.
        /// Returns 1 when there is no record, so that missing data does not hide spells.
        /// </summary>
        public static int GetSpellMinPlayerLevel(int spellId)
        {
            try
            {
                using var conn = new SqliteConnection(WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT MIN(MinPlayerLevel) FROM SpellLevels WHERE SpellId = @sid;";
                cmd.Parameters.AddWithValue("@sid", spellId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value && int.TryParse(result.ToString(), out int lvl) && lvl > 0)
                {
                    return lvl;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] Error reading MinPlayerLevel of spell {spellId}: {ex.Message}");
            }
            return 1;
        }

        /// <summary>
        /// The map where the vendors are: the one with the most NPCs placed.
        ///
        /// It is looked up instead of written in. Today the Amakna Village wins (88212759) with 52 rows in
        /// NpcSpawns against a single one for the next, so there is no tie to break; but if another map is
        /// populated tomorrow, the one coming out of here will be the right one without having to touch the
        /// command. It returns (0, 0) when there is no NPC placed.
        /// </summary>
        public static (long MapId, int Npcs) GetMapWithMostNpcSpawns()
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT MapId, COUNT(*) AS c FROM NpcSpawns " +
                    "GROUP BY MapId ORDER BY c DESC, MapId ASC LIMIT 1;";

                using var reader = command.ExecuteReader();
                if (reader.Read()) return (reader.GetInt64(0), reader.GetInt32(1));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se pudo buscar el mapa con más NPC: {ex.Message}");
            }
            return (0, 0);
        }

        /// <summary>The subarea a map falls in, or zero. It is what the «PB» criterion asks.</summary>
        public static int SubAreaOfMap(long mapId)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var query = connection.CreateCommand();
                query.CommandText = "SELECT SubAreaId FROM MapSubareas WHERE MapId = $m LIMIT 1;";
                query.Parameters.AddWithValue("$m", mapId);
                var value = query.ExecuteScalar();
                return value == null || value is DBNull ? 0 : Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DatabaseManager] No se pudo leer la subárea del mapa {mapId}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>The maps of a subarea, in order. Empty when there are none.</summary>
        public static List<long> MapsOfSubArea(int subAreaId)
        {
            var fuera = new List<long>();
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var query = connection.CreateCommand();
                query.CommandText = "SELECT MapId FROM MapSubareas WHERE SubAreaId = $s ORDER BY MapId;";
                query.Parameters.AddWithValue("$s", subAreaId);
                using var reader = query.ExecuteReader();
                while (reader.Read()) fuera.Add(reader.GetInt64(0));
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DatabaseManager] No se pudieron leer los mapas de la subárea {subAreaId}: {ex.Message}");
            }
            return fuera;
        }

        /// <summary>
        /// A monster's aggression immunity criterion, as its template carries it. It is what governs the
        /// raids' light: the Sima's carry <c>(PB=1131&amp;RV!7,n1_worldlight,0)|…</c> and stop being immune
        /// in the dark.
        /// </summary>
        public static string MonsterAggressiveImmunity(int monsterTemplate)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();
                var query = connection.CreateCommand();
                query.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $m;";
                query.Parameters.AddWithValue("$m", monsterTemplate);
                if (query.ExecuteScalar() is not string data) return "";
                using var doc = System.Text.Json.JsonDocument.Parse(data);
                return doc.RootElement.TryGetProperty("aggressiveImmunityCriterion", out var criterion)
                    ? criterion.GetString() ?? ""
                    : "";
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DatabaseManager] No se pudo leer el criterio del monstruo {monsterTemplate}: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// A subarea's name, in the client's language.
        ///
        /// It goes in two hops, the same as spell names: SubAreaTemplates stores a JSON with a nameId inside,
        /// and that nameId is the Translations key. It comes out empty when there is no translation, and
        /// whoever calls will decide what to show in its place.
        /// </summary>
        public static string GetSubAreaName(int subAreaId)
        {
            try
            {
                using var connection = new SqliteConnection(WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM SubAreaTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", subAreaId);

                string? data = command.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(data)) return "";

                using var doc = System.Text.Json.JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("nameId", out var nameId)) return "";

                var translation = connection.CreateCommand();
                translation.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
                translation.Parameters.AddWithValue("$key", nameId.GetInt64().ToString());

                return translation.ExecuteScalar() as string ?? "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DatabaseManager] No se pudo leer el nombre de la subzona " +
                                  $"{subAreaId}: {ex.Message}");
                return "";
            }
        }
    }

    // =========================================================================
    // COMBAT DATA TRANSFER OBJECTS
    // =========================================================================

    public class MonsterGradeStats
    {
        /// <summary>
        /// The behaviour spell the monster starts with, as the client's data carries it: a
        /// <c>SpellLevels.Id</c>, NOT a <c>Spells.Id</c>. It is not its attack list and that is why it goes
        /// apart from <see cref="SpellIds"/>. Nobody reads it today.
        /// </summary>
        public int StartingSpellLevelId { get; set; }

        public int Level { get; set; } = 1;
        public int LifePoints { get; set; } = 50;
        public int ActionPoints { get; set; } = 6;
        public int MovementPoints { get; set; } = 3;
        public int Strength { get; set; }
        public int Intelligence { get; set; }
        public int Chance { get; set; }
        public int Agility { get; set; }
        public int Wisdom { get; set; }

        /// <summary>The grade's own AP and MP dodge ("paDodge", "pmDodge"), on top of what its wisdom gives.</summary>
        public int PaDodge { get; set; }
        public int PmDodge { get; set; }

        /// <summary>
        /// The grade's own escape and tackle ("bonusCharacteristics.tackleEvade" and
        /// "tackleBlock"), on top of the tenth of its agility both come from. 75 grades carry one.
        /// </summary>
        public int TackleEvadeBonus { get; set; }
        public int TackleBlockBonus { get; set; }

        public int NeutralResistance { get; set; }
        public int EarthResistance { get; set; }
        public int FireResistance { get; set; }
        public int WaterResistance { get; set; }
        public int AirResistance { get; set; }
        public int GradeXp { get; set; } = 100;
        public List<int> SpellIds { get; set; } = new List<int>();

        /// <summary>Grade (level) of each of the monster's spells, keyed by spell id.</summary>
        public Dictionary<int, int> SpellGrades { get; set; } = new Dictionary<int, int>();
    }

    public class MonsterDrop
    {
        public int ObjectId { get; set; }
        /// <summary>Drop chance, as a percentage, for the monster's grade.</summary>
        public double PercentDrop { get; set; }

        /// <summary>
        /// What the receiver has to satisfy to get it, in the client's own criterion language, or
        /// empty when anybody does.
        /// </summary>
        /// <remarks>
        /// Only the global table carries one. It is what keeps a raid's treasures inside the raid
        /// and the season's fragments inside the season.
        /// </remarks>
        public string ReceiverCriterion { get; set; } = "";
    }

    public class SpellCombatData
    {
        /// <summary>
        /// A weapon's damage lines, one per effect: the effect number, its element and its dice. Empty for a
        /// spell.
        ///
        /// A weapon hits once PER LINE, and the real server sends one hit for each with its own effect number.
        /// The loose fields down here carry the biggest line, which is what the AI needs to estimate.
        /// </summary>
        public List<(int Effect, int Element, int Min, int Max)> WeaponLines { get; } =
            new List<(int, int, int, int)>();

        public long SpellId { get; set; }
        public int SpellLevelId { get; set; }
        public int APCost { get; set; } = 3;
        public int MinRange { get; set; } = 1;
        public int MaxRange { get; set; } = 1;
        public int BaseDamageMin { get; set; } = 5;
        public int BaseDamageMax { get; set; } = 10;
        public int BaseDamage => (BaseDamageMin + BaseDamageMax) / 2;
        public int EffectUid { get; set; } = 41870;
        public int Element { get; set; } = 0; // 0=Neutral, 1=Earth, 2=Fire, 3=Water, 4=Air

        /// <summary>
        /// Whether the spell requires line of sight (castTestLos in the client's data).
        /// </summary>
        public bool NeedsLineOfSight { get; set; } = true;

        /// <summary>Can only be cast in a straight line.</summary>
        public bool CastInLine { get; set; }

        public int MaxCastPerTurn { get; set; }

        /// <summary>Casts allowed per turn on the SAME target. 0 = no limit.</summary>
        public int MaxCastPerTarget { get; set; }

        /// <summary>
        /// Base critical hit chance, as a percentage. The critical granted by the equipment is
        /// added on top: Frozen Arrow brings 10 and the Turquoise Dofus another 10, which is
        /// where the 20 % shown in the spell description comes from.
        /// </summary>
        public int CriticalHitProbability { get; set; }

        public int CriticalDamageMin { get; set; }
        public int CriticalDamageMax { get; set; }
        public bool HasCriticalDamage => CriticalDamageMin > 0 || CriticalDamageMax > 0;

        /// <summary>Cells of displacement: positive pushes, negative pulls. 0 = does not move.</summary>
        public int PushDistance { get; set; }

        /// <summary>
        /// Effects that modify a characteristic of the target (removing AP, removing range,
        /// bonuses...). Each one already carries the characteristic it maps to according to the
        /// client's effect catalogue.
        /// </summary>
        public List<SpellStatEffect> StatEffects { get; set; } = new List<SpellStatEffect>();

        /// <summary>Base damage bonuses that this spell leaves in place once cast.</summary>
        public List<SpellDamageBuff> DamageBuffs { get; set; } = new List<SpellDamageBuff>();
    }

    public class SpellDamageBuff
    {
        /// <summary>The spell whose base damage goes up.</summary>
        public int SpellId { get; set; }
        public int Bonus { get; set; }
        /// <summary>How many turns it lasts from the moment it is applied.</summary>
        public int Duration { get; set; } = 1;
    }

    public class SpellStatEffect
    {
        public int EffectId { get; set; }
        public int Characteristic { get; set; }
        public int Value { get; set; }
        public int Duration { get; set; }
    }
}
