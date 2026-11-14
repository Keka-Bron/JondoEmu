using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Map borders a quest keeps shut until it says otherwise (content/maps/closed_borders.json).
    /// </summary>
    /// <remarks>
    /// The Pandala bridge: its first half starts off the right of the Astrub quarry, 193201153
    /// [12,-21], and Moktwa will not let anyone onto it before «El despertar de Pandala» (2149) --
    /// «il ne peut pas vous laisser traverser le pont car celui-ci est en trop mauvais état», and
    /// once the serum is in, «vous pouvez traverser le pont de Pandala» (Dofus pour les noobs).
    /// Neither the world graph nor MapScrolls carries that condition, so it is written here: a
    /// border with an «open» criterion in the client's syntax; whoever does not meet it stays on
    /// the map as at any border that leads nowhere.
    /// </remarks>
    public static class ClosedBorders
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "maps/closed_borders.json";

        private static Dictionary<(long Map, string Way), string>? _open;
        private static readonly object _lock = new();

        /// <summary>
        /// Whether crossing <paramref name="way"/> («right», «down», «left», «up») of
        /// <paramref name="map"/> is shut for whoever <paramref name="meets"/> judges.
        /// </summary>
        public static bool Shut(long map, string way, Func<string, bool> meets)
            => All().TryGetValue((map, way), out string? open) && !meets(open);

        /// <summary>Every closed border with the criterion that opens it.</summary>
        internal static Dictionary<(long Map, string Way), string> All()
        {
            lock (_lock)
            {
                if (_open != null) return _open;
                var open = new Dictionary<(long Map, string Way), string>();
                string path = Paths.ContentFile(AuthoredFile);
                try
                {
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var row in doc.RootElement.GetProperty("borders").EnumerateArray())
                        {
                            string criterion = row.GetProperty("open").GetString() ?? "";
                            if (criterion.Length == 0) continue;
                            open[(row.GetProperty("map").GetInt64(), row.GetProperty("way").GetString() ?? "")] = criterion;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Borders] {Path.GetFileName(path)} is unreadable: {ex.Message}");
                }
                _open = open;
                return _open;
            }
        }

        /// <summary>Read the file again (content reload, tests).</summary>
        internal static void Forget()
        {
            lock (_lock) _open = null;
        }
    }
}
