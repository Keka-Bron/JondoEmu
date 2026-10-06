using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// How big the character is, as a percentage of how big his breed is.
    ///
    /// The scale travels in the look block's f5, packed, and it is a MULTIPLIER: in the client's own
    /// notation -- the one NpcSpawns.Look stores, "{4907|||130}" -- the last number is exactly that.
    /// NPCs are nearly all at 100 and a dragoturkey at 120, which is 20 % bigger.
    ///
    /// Careful with the starting number: breeds are NOT worth 100. breed_looks.json, which comes from
    /// the client's bundle, gives them between 43 and 55 depending on breed and sex (a male Feca 53, a
    /// female Feca 52). That is why the command does not send the number the player types as is: it
    /// stores a percentage and multiplies the scale the breed declares by it. That way 100 is THAT
    /// character's normal size -- whether it is 43 or 55 inside -- and 200 is double, which is what was
    /// asked.
    ///
    /// It lives here and not in GameState because the look is also built for characters not being
    /// played -- the selection screen draws them all -- and each has his own.
    /// </summary>
    public static class CharacterSize
    {
        /// <summary>The usual size: the one the breed declares, untouched.</summary>
        public const int Normal = 100;

        /// <summary>
        /// The limits. They are not protocol's, they are common sense's: below 5 the figure disappears from
        /// the screen and above 1000 it covers the whole map, and in both cases the player can no longer see
        /// himself to fix it.
        /// </summary>
        public const int Minimum = 5;
        public const int Maximum = 1000;

        private static readonly Dictionary<long, int> _cache = new Dictionary<long, int>();
        private static readonly object _lock = new object();

        /// <summary>A character's size. One who has never touched it is normal size.</summary>
        public static int Of(long characterId)
        {
            if (characterId <= 0) return Normal;

            lock (_lock)
            {
                if (_cache.TryGetValue(characterId, out int cached)) return cached;
            }

            int size = Read(characterId);

            lock (_lock)
            {
                _cache[characterId] = size;
            }
            return size;
        }

        /// <summary>
        /// Changes the size and writes it down. Returns the one that ended up set, which may not be the one
        /// asked for if it came outside the limits.
        /// </summary>
        public static int Set(long characterId, int percent)
        {
            int size = Math.Clamp(percent, Minimum, Maximum);
            if (characterId <= 0) return size;

            lock (_lock)
            {
                _cache[characterId] = size;
            }

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE Characters SET Size = $size WHERE Id = $id;";
                command.Parameters.AddWithValue("$size", size);
                command.Parameters.AddWithValue("$id", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // Failing to save it cannot prevent it from being seen: the look already carries the new
                // size in memory and what is lost is it surviving the game being closed.
                Console.WriteLine($"[Tamaño] No se pudo guardar el tamaño de {characterId}: {ex.Message}");
            }

            return size;
        }

        /// <summary>
        /// The breed's scales already multiplied by the character's size.
        ///
        /// It is rounded up with a floor of 1: a small size on a small scale gives zero when rounding, and a
        /// zero in f5 is "no scale", which the client draws at the default size. That is, shrinking too much
        /// sent the figure back to its normal size.
        /// </summary>
        public static List<long> Applied(IReadOnlyList<long> scales, long characterId)
        {
            var salida = new List<long>();
            if (scales == null) return salida;

            int size = Of(characterId);
            foreach (long scale in scales)
            {
                if (size == Normal) { salida.Add(scale); continue; }
                salida.Add(Math.Max(1, (long)Math.Round(scale * size / 100.0)));
            }
            return salida;
        }

        private static int Read(long characterId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(Size, $normal) FROM Characters WHERE Id = $id;";
                command.Parameters.AddWithValue("$normal", Normal);
                command.Parameters.AddWithValue("$id", characterId);

                var result = command.ExecuteScalar();
                if (result != null && result != DBNull.Value &&
                    int.TryParse(result.ToString(), out int size) && size > 0)
                {
                    return Math.Clamp(size, Minimum, Maximum);
                }
            }
            catch (Exception ex)
            {
                // A database without the column yet -- or a character who is not there -- cannot leave
                // the figure undrawn: he is measured like everybody else.
                Console.WriteLine($"[Tamaño] No se pudo leer el tamaño de {characterId}: {ex.Message}");
            }
            return Normal;
        }
    }
}
