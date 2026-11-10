using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Where a capture has measured that a character lands on crossing into a map, which wins over
    /// the cell worked out from the border it left by.
    /// </summary>
    /// <remarks>
    /// The rule mirrors the cell it left from across the border, and it holds between maps that
    /// really touch. The guided tutorial's rooms do not: each is its own island, and the capture of
    /// the whole tutorial lands the character on cell 84 facing 1 on every one of them, whatever
    /// cell of the previous room's right edge it walked off from. The rule put it on 412.
    /// </remarks>
    public static class MeasuredArrivals
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "maps/arrivals.json";

        private static Dictionary<long, (int Cell, int Orientation)>? _arrivals;
        private static readonly object _lock = new();

        /// <summary>The measured landing on a map crossed into, if there is one.</summary>
        public static bool TryGet(long mapId, out int cell, out int orientation)
        {
            Load();
            if (_arrivals!.TryGetValue(mapId, out var arrival))
            {
                cell = arrival.Cell;
                orientation = arrival.Orientation;
                return true;
            }
            cell = -1;
            orientation = -1;
            return false;
        }

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _arrivals = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_arrivals != null) return;
                var arrivals = new Dictionary<long, (int, int)>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var entry in doc.RootElement.GetProperty("arrivals").EnumerateArray())
                        {
                            long map = entry.GetProperty("map").GetInt64();
                            int cell = entry.GetProperty("cell").GetInt32();
                            int orientation = entry.TryGetProperty("orientation", out var facing) ? facing.GetInt32() : -1;
                            if (map > 0 && cell >= 0 && cell < 560) arrivals[map] = (cell, orientation);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _arrivals = arrivals;
            }
        }
    }
}
