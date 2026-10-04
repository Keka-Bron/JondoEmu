using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Koliseo ladder: every character's rating in each mode, the league it puts him in, his
    /// placement fights, his wins and fights of the season and of the day, and the season itself.
    /// </summary>
    /// <remarks>
    /// ─── What the official site says (devblog "Devblog : les ligues", 24/08/2023) ─────────
    ///
    ///   * One rating per mode, independent of the others; the player no longer sees it, only
    ///     his league, which the rating alone decides (see <see cref="KoliseoLeagues"/>).
    ///   * About 3 to 4 wins to go up a division, and losses take a player down.
    ///   * Seasons of about three months; at the start of each, every rating goes back to a
    ///     starting value that depends on the character's level, and placement fights come first.
    ///   * The rating calculation is "strongly inspired by Glicko" (2.52), with no numbers given.
    ///
    /// ─── What the captures say ────────────────────────────────────────────────────────────
    ///
    /// The lty the real server sends at world entry and after a Koliseo fight: every mode starts
    /// with no league and 5 placement fights, and the 2v2 lost in "koliseo completo" leaves that
    /// mode with 4 left, 1 fight and 0 wins in the season and in the day. So 5 placement fights
    /// in every mode here, the capture's number and not the devblog's 10 for 3v3.
    ///
    /// ─── What is INFERRED, and where to change it ─────────────────────────────────────────
    ///
    ///   * The calculation: Elo on each side's average rating, K = <see cref="K"/>. An even fight
    ///     is worth 45 points and a division is 150 apart, so 3 to 4 wins, the devblog's figure.
    ///     Placement fights move twice as far (<see cref="PlacementK"/>) to find the level sooner.
    ///   * The starting rating: five points a level, 1000 at level 200 (<see cref="StartingRating"/>).
    ///   * The season: <see cref="SeasonLength"/> from the first one this server opened.
    ///   * The day of the day counters: the server's local date.
    ///   * Not modelled: levels gained outside the Koliseo raising the rating, a change of class
    ///     resetting it, and the season's ornament and title.
    /// </remarks>
    public static class KoliseoLadder
    {
        /// <summary>Placement fights in every mode: the capture's 5.</summary>
        public const int PlacementFights = 5;

        /// <summary>How far one fight moves a placed rating: 45 points for an even fight.</summary>
        public const int K = 90;

        /// <summary>How far a placement fight moves it.</summary>
        public const int PlacementK = 180;

        /// <summary>A season: "tous les 3 mois".</summary>
        public static readonly TimeSpan SeasonLength = TimeSpan.FromDays(91);

        /// <summary>The Koliseo's modes, the lsg of the protocol: 1v1, 2v2, 3v3 and the closed one.</summary>
        public static readonly int[] Modes = { 0, 1, 2, 3 };

        /// <summary>For tests: what "now" is.</summary>
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;

        /// <summary>One character's standing in one mode.</summary>
        public sealed class Standing
        {
            public long CharacterId { get; init; }
            public int Mode { get; init; }
            public int Season { get; set; }
            public int Rating { get; set; }
            public int League { get; set; } = KoliseoLeagues.None;
            public int BestLeague { get; set; } = KoliseoLeagues.None;
            public int PlacementLeft { get; set; } = PlacementFights;
            public int SeasonWins { get; set; }
            public int SeasonFights { get; set; }
            public int DayWins { get; set; }
            public int DayFights { get; set; }
            public string Day { get; set; } = "";

            public bool Placed => PlacementLeft <= 0;
        }

        /// <summary>A season: its number and when it started.</summary>
        public readonly record struct Season(int Number, DateTime StartUtc);

        private static readonly object Gate = new object();
        private static bool _schema;

        /// <summary>The rating a character starts a season with: five a level, 1000 at 200.</summary>
        public static int StartingRating(int level) => Math.Clamp(level * 5, 0, 1000);

        /// <summary>The chance the first side had, by the Elo curve.</summary>
        public static double Expected(double mine, double theirs) => 1.0 / (1.0 + Math.Pow(10, (theirs - mine) / 400.0));

        /// <summary>The season in force, opening a new one when the last is over.</summary>
        public static Season Current()
        {
            lock (Gate)
            {
                using var connection = Open();
                return CurrentSeason(connection, Clock());
            }
        }

        /// <summary>A character's standing in a mode, as it is now in the season in force.</summary>
        public static Standing Of(long characterId, int mode, int level)
        {
            lock (Gate)
            {
                using var connection = Open();
                var season = CurrentSeason(connection, Clock());
                return Read(connection, characterId, mode, level, season, Clock());
            }
        }

        /// <summary>A character's standing in every mode, in the protocol's order.</summary>
        public static IReadOnlyList<Standing> AllOf(long characterId, int level)
        {
            lock (Gate)
            {
                using var connection = Open();
                var season = CurrentSeason(connection, Clock());
                return Modes.Select(m => Read(connection, characterId, m, level, season, Clock())).ToList();
            }
        }

        /// <summary>
        /// A Koliseo fight is over: every player of both sides has his rating, league, placement
        /// and counters moved, and written. Returns the new standings.
        /// </summary>
        public static IReadOnlyList<Standing> Record(int mode, IReadOnlyList<(long Id, int Level)> winners,
                                                     IReadOnlyList<(long Id, int Level)> losers)
        {
            if (winners.Count == 0 || losers.Count == 0) return Array.Empty<Standing>();
            lock (Gate)
            {
                using var connection = Open();
                var now = Clock();
                var season = CurrentSeason(connection, now);
                var won = winners.Select(p => Read(connection, p.Id, mode, p.Level, season, now)).ToList();
                var lost = losers.Select(p => Read(connection, p.Id, mode, p.Level, season, now)).ToList();
                double wonAverage = won.Average(s => s.Rating);
                double lostAverage = lost.Average(s => s.Rating);

                var after = new List<Standing>();
                foreach (var s in won) after.Add(Apply(connection, s, true, Expected(wonAverage, lostAverage)));
                foreach (var s in lost) after.Add(Apply(connection, s, false, Expected(lostAverage, wonAverage)));
                return after;
            }
        }

        private static Standing Apply(SqliteConnection connection, Standing s, bool won, double expected)
        {
            int k = s.Placed ? K : PlacementK;
            s.Rating = Math.Max(0, (int)Math.Round(s.Rating + k * ((won ? 1.0 : 0.0) - expected), MidpointRounding.AwayFromZero));
            s.SeasonFights++;
            s.DayFights++;
            if (won)
            {
                s.SeasonWins++;
                s.DayWins++;
            }
            if (!s.Placed)
            {
                s.PlacementLeft--;
                if (s.Placed) s.League = KoliseoLeagues.Place(s.Rating);
            }
            else
            {
                s.League = KoliseoLeagues.Move(s.League, s.Rating);
            }
            if (s.League != KoliseoLeagues.None) s.BestLeague = KoliseoLeagues.Higher(s.BestLeague, s.League);
            Write(connection, s);
            return s;
        }

        // ─── The database ───────────────────────────────────────────────────────────────────

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            if (_schema) return connection;
            using var create = connection.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS KoliseoSeasons (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartedAt INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS KoliseoRatings (
                    CharacterId INTEGER NOT NULL,
                    Mode INTEGER NOT NULL,
                    Season INTEGER NOT NULL,
                    Rating INTEGER NOT NULL,
                    League INTEGER NOT NULL,
                    BestLeague INTEGER NOT NULL,
                    PlacementLeft INTEGER NOT NULL,
                    SeasonWins INTEGER NOT NULL,
                    SeasonFights INTEGER NOT NULL,
                    DayWins INTEGER NOT NULL,
                    DayFights INTEGER NOT NULL,
                    Day TEXT NOT NULL,
                    PRIMARY KEY (CharacterId, Mode)
                );";
            create.ExecuteNonQuery();
            _schema = true;
            return connection;
        }

        private static Season CurrentSeason(SqliteConnection connection, DateTime nowUtc)
        {
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT Id, StartedAt FROM KoliseoSeasons ORDER BY Id DESC LIMIT 1;";
                using var reader = read.ExecuteReader();
                if (reader.Read())
                {
                    var season = new Season(reader.GetInt32(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).UtcDateTime);
                    if (nowUtc < season.StartUtc + SeasonLength) return season;
                }
            }
            using var open = connection.CreateCommand();
            open.CommandText = "INSERT INTO KoliseoSeasons (StartedAt) VALUES ($t); SELECT last_insert_rowid();";
            var start = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
            open.Parameters.AddWithValue("$t", new DateTimeOffset(start).ToUnixTimeMilliseconds());
            int number = Convert.ToInt32(open.ExecuteScalar());
            Console.WriteLine($"[Koliseo] Season {number} opens: every rating back to its start, placement first.");
            return new Season(number, start);
        }

        private static string DayOf(DateTime nowUtc)
            => DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static Standing Read(SqliteConnection connection, long characterId, int mode, int level,
                                     Season season, DateTime nowUtc)
        {
            string today = DayOf(nowUtc);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Season, Rating, League, BestLeague, PlacementLeft, SeasonWins, SeasonFights, DayWins, " +
                "DayFights, Day FROM KoliseoRatings WHERE CharacterId = $c AND Mode = $m;";
            command.Parameters.AddWithValue("$c", characterId);
            command.Parameters.AddWithValue("$m", mode);
            using var reader = command.ExecuteReader();
            if (reader.Read() && reader.GetInt32(0) == season.Number)
            {
                bool sameDay = reader.GetString(9) == today;
                return new Standing
                {
                    CharacterId = characterId,
                    Mode = mode,
                    Season = season.Number,
                    Rating = reader.GetInt32(1),
                    League = reader.GetInt32(2),
                    BestLeague = reader.GetInt32(3),
                    PlacementLeft = reader.GetInt32(4),
                    SeasonWins = reader.GetInt32(5),
                    SeasonFights = reader.GetInt32(6),
                    DayWins = sameDay ? reader.GetInt32(7) : 0,
                    DayFights = sameDay ? reader.GetInt32(8) : 0,
                    Day = today,
                };
            }
            return new Standing
            {
                CharacterId = characterId,
                Mode = mode,
                Season = season.Number,
                Rating = StartingRating(level),
                Day = today,
            };
        }

        private static void Write(SqliteConnection connection, Standing s)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO KoliseoRatings (CharacterId, Mode, Season, Rating, League, BestLeague, " +
                "PlacementLeft, SeasonWins, SeasonFights, DayWins, DayFights, Day) VALUES ($c, $m, $s, $r, $l, $b, " +
                "$p, $sw, $sf, $dw, $df, $d);";
            command.Parameters.AddWithValue("$c", s.CharacterId);
            command.Parameters.AddWithValue("$m", s.Mode);
            command.Parameters.AddWithValue("$s", s.Season);
            command.Parameters.AddWithValue("$r", s.Rating);
            command.Parameters.AddWithValue("$l", s.League);
            command.Parameters.AddWithValue("$b", s.BestLeague);
            command.Parameters.AddWithValue("$p", Math.Max(0, s.PlacementLeft));
            command.Parameters.AddWithValue("$sw", s.SeasonWins);
            command.Parameters.AddWithValue("$sf", s.SeasonFights);
            command.Parameters.AddWithValue("$dw", s.DayWins);
            command.Parameters.AddWithValue("$df", s.DayFights);
            command.Parameters.AddWithValue("$d", s.Day);
            command.ExecuteNonQuery();
        }

        /// <summary>For tests: a character's standings gone.</summary>
        internal static void Erase(long characterId)
        {
            lock (Gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM KoliseoRatings WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>For tests: the number of the last season written, 0 for none, opening none.</summary>
        internal static int LastSeasonNumber()
        {
            lock (Gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM KoliseoSeasons;";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        /// <summary>For tests: the seasons after that one gone, so none stays in a real base.</summary>
        internal static void EraseSeasonsAfter(int number)
        {
            lock (Gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM KoliseoSeasons WHERE Id > $n;";
                command.Parameters.AddWithValue("$n", number);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>For tests: a season that started at <paramref name="startUtc"/>, in force.</summary>
        internal static Season StartSeason(DateTime startUtc)
        {
            lock (Gate)
            {
                using var connection = Open();
                using var open = connection.CreateCommand();
                open.CommandText = "INSERT INTO KoliseoSeasons (StartedAt) VALUES ($t); SELECT last_insert_rowid();";
                var start = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
                open.Parameters.AddWithValue("$t", new DateTimeOffset(start).ToUnixTimeMilliseconds());
                return new Season(Convert.ToInt32(open.ExecuteScalar()), start);
            }
        }
    }
}
