using System;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The free entry the keyring gives: one per dungeon per week, reset on Tuesdays.
    /// </summary>
    /// <remarks>
    /// Not invented and not remembered — read out of the client's own help text, translation
    /// 1189621, which is the page the game shows when you ask it how dungeons work:
    ///
    /// <code>
    ///   "Para entrar en una mazmorra, hay que dar una llave de mazmorra al portero. Las llaves
    ///    las fabrican los manitas o se compran en el mercadillo de recursos. También es posible
    ///    usar el manojo de llaves para beneficiarse de una ENTRADA GRATIS EN CADA MAZMORRA, UNA
    ///    VEZ POR SEMANA, a excepción de las mazmorras de las dimensiones divinas. El manojo de
    ///    llaves SE REINICIA TODOS LOS MARTES."
    /// </code>
    ///
    /// Three things in that sentence that guesswork gets wrong, and each changes the code:
    ///
    /// <list type="bullet">
    /// <item><b>Weekly, not daily.</b> A daily cooldown would be seven times too generous.</item>
    /// <item><b>Per dungeon, not per keyring.</b> "una entrada gratis en cada mazmorra" — using it
    /// on one door does not close the others. So the row is keyed by both.</item>
    /// <item><b>A fixed weekday, not a rolling seven days.</b> Somebody who enters on Monday gets
    /// another free entry the next day; somebody who enters on Wednesday waits six. Storing "when
    /// it was used" and adding seven days would be a different game.</item>
    /// </list>
    ///
    /// The loose key has no cooldown at all and never did: it is an ordinary craftable item, so the
    /// limit on using it is owning one.
    ///
    /// The divine-dimension exception needs no code. Whether a dungeon takes the keyring is in the
    /// game's own data as <c>availableOnKeyring</c> — 107 of the 187 do — and
    /// <see cref="DungeonManager.Dungeon.OnKeyring"/> already carries it.
    /// </remarks>
    public static class DungeonKeyring
    {
        /// <summary>The weekday the keyring comes back.</summary>
        public const DayOfWeek ResetDay = DayOfWeek.Tuesday;

        /// <summary>
        /// The start of the keyring week <paramref name="now"/> falls in: the most recent Tuesday.
        /// </summary>
        /// <remarks>
        /// Tuesday itself belongs to the week it opens, so a Tuesday at 00:00 is already the new
        /// week and the entry used the Monday before does not count against it.
        /// </remarks>
        public static DateTime WeekOf(DateTime now)
        {
            int back = ((int)now.DayOfWeek - (int)ResetDay + 7) % 7;
            return now.Date.AddDays(-back);
        }

        /// <summary>How the week is written down. Sortable and readable in the database by hand.</summary>
        public static string WeekKey(DateTime now) => WeekOf(now).ToString("yyyy-MM-dd");

        /// <summary>
        /// Whether this character still has the keyring's free entry for this dungeon this week.
        /// </summary>
        /// <remarks>
        /// A database that cannot be read answers YES. The alternative is a player locked out of
        /// content by a logging failure, which is a worse outcome than a free extra entry.
        /// </remarks>
        public static bool FreeEntryLeft(long characterId, int dungeonId, DateTime now)
        {
            if (characterId <= 0 || dungeonId <= 0) return true;

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT Week FROM CharacterKeyring WHERE CharacterId = $c AND DungeonId = $d;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$d", dungeonId);

                object? used = command.ExecuteScalar();
                return used == null || (used as string) != WeekKey(now);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mazmorra] No se ha podido leer el manojo: {ex.Message}");
                return true;
            }
        }

        /// <summary>Writes down that the free entry has been used this week.</summary>
        public static void SpendFreeEntry(long characterId, int dungeonId, DateTime now)
        {
            if (characterId <= 0 || dungeonId <= 0) return;

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO CharacterKeyring (CharacterId, DungeonId, Week) VALUES ($c, $d, $w) " +
                    "ON CONFLICT (CharacterId, DungeonId) DO UPDATE SET Week = $w;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$d", dungeonId);
                command.Parameters.AddWithValue("$w", WeekKey(now));
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mazmorra] No se ha podido apuntar el manojo: {ex.Message}");
            }
        }

        /// <summary>When the free entry comes back, for telling the player.</summary>
        public static DateTime NextReset(DateTime now) => WeekOf(now).AddDays(7);
    }
}
