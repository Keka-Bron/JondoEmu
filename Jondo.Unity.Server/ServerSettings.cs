using System;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// What the server's operator can change from its Settings window: rates, caps and modes that
    /// the game itself does not decide. Kept in the emulator's own folder (config\server_settings.json).
    /// </summary>
    /// <remarks>
    /// The server reads them ONCE, when it starts (<see cref="Current"/>), and every rule reads that
    /// snapshot: a change saved from the window applies at the next start, which the window offers
    /// to do at once, telling the players first. A setting at its default leaves the game as it is.
    /// </remarks>
    public sealed class ServerSettings
    {
        /// <summary>The fewest players a guild raid starts with; 0 keeps the client's data (eight).</summary>
        public int RaidMinPlayers { get; set; }

        /// <summary>The most action points a character can have; 0 means no cap (the official one is 12).</summary>
        public int MaxActionPoints { get; set; }

        /// <summary>The most movement points a character can have; 0 means no cap (the official one is 6).</summary>
        public int MaxMovementPoints { get; set; }

        /// <summary>Extra experience for everybody, in percent: what fights, quests and rewards give.</summary>
        public int ExperienceBonusPercent { get; set; }

        /// <summary>Extra chance of every item a monster drops, in percent.</summary>
        public int DropBonusPercent { get; set; }

        /// <summary>Extra kamas from fights, in percent.</summary>
        public int KamasBonusPercent { get; set; }

        /// <summary>
        /// Hardcore: every monster twice its size and with three times its life and characteristics.
        /// </summary>
        public bool Hardcore { get; set; }

        /// <summary>A lost fight costs no energy.</summary>
        public bool NoEnergyLoss { get; set; }

        /// <summary>
        /// Random loot: besides its own table, a monster can drop a handful of items picked at random
        /// around its level (<see cref="Managers.RandomLoot"/>).
        /// </summary>
        public bool RandomLoot { get; set; }

        /// <summary>The chance, in percent, that a monster drops a random handful when random loot is on.</summary>
        public double RandomLootChancePercent { get; set; } = 10;

        /// <summary>A line every player reads in the chat on coming into the world; empty, none.</summary>
        public string WelcomeMessage { get; set; } = "";

        /// <summary>The server window's language: es, en or fr.</summary>
        public string WindowLanguage { get; set; } = "es";

        /// <summary>How much bigger Hardcore makes monsters, and how much stronger.</summary>
        public const int HardcoreScale = 200;
        public const int HardcoreStatsFactor = 3;

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private static ServerSettings _current;

        /// <summary>The settings this run started with.</summary>
        public static ServerSettings Current => _current ??= Load();

        /// <summary>For the tests: the settings to run with, or null for the file's.</summary>
        internal static void UseForTests(ServerSettings settings) => _current = settings;

        /// <summary>The settings as the file has them now, or the defaults.</summary>
        public static ServerSettings Load()
        {
            try
            {
                string file = Paths.ServerSettingsFile;
                if (!File.Exists(file)) return new ServerSettings();
                return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(file), Json) ?? new ServerSettings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] {Paths.ServerSettingsFile} could not be read ({ex.Message}); the defaults stand.");
                return new ServerSettings();
            }
        }

        /// <summary>Writes these settings to the file, for the next start.</summary>
        public void Save()
        {
            string file = Paths.ServerSettingsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        }

        /// <summary>A copy, for the window to edit.</summary>
        public ServerSettings Copy() => (ServerSettings)MemberwiseClone();

        /// <summary>Whether two settings differ in anything that needs a restart.</summary>
        public bool DiffersFrom(ServerSettings other)
            => JsonSerializer.Serialize(WithoutLanguage()) != JsonSerializer.Serialize(other.WithoutLanguage());

        /// <summary>A copy without the window's language, which changes nothing in the game.</summary>
        private ServerSettings WithoutLanguage()
        {
            var copy = Copy();
            copy.WindowLanguage = "";
            return copy;
        }

        // ─── What the rules ask ────────────────────────────────────────────────

        /// <summary>An amount with a percent bonus on top.</summary>
        public static long WithBonus(long amount, int percent)
            => percent <= 0 || amount <= 0 ? amount : amount + amount * percent / 100;

        /// <summary>A chance with a percent bonus on top, never over certainty.</summary>
        public static double ChanceWithBonus(double percentChance, int percent)
            => percent <= 0 ? percentChance : Math.Min(100.0, percentChance * (100.0 + percent) / 100.0);

        /// <summary>A characteristic under a cap, where there is one.</summary>
        public static int Capped(int value, int cap) => cap > 0 ? Math.Min(value, cap) : value;
    }
}
