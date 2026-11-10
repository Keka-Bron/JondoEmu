using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.World.Maps
{
    /// <summary>
    /// Maps the quest catalogue still names that the world no longer has, and what stands for each
    /// now (content/maps/moved.json).
    /// </summary>
    /// <remarks>
    /// 3.6 rebuilt the guided tutorial on new maps, and quests 1629 and 1630 still send the player
    /// to the old celestial temple's rooms: «hablar con el maestro Curro» on 153093380, a map that
    /// is in no data any more, could never be done on 241440769, where he stands.
    /// </remarks>
    public static class MovedMaps
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "maps/moved.json";

        private static Dictionary<long, long>? _moved;
        private static readonly object _lock = new();

        /// <summary>The map that stands for this one now: itself when it did not move.</summary>
        public static long Now(long mapId)
        {
            if (mapId == 0) return 0;
            Load();
            return _moved!.TryGetValue(mapId, out long to) ? to : mapId;
        }

        /// <summary>For the tests: read the file again.</summary>
        public static void Forget()
        {
            lock (_lock) _moved = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_moved != null) return;
                var moved = new Dictionary<long, long>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var row in doc.RootElement.GetProperty("moved").EnumerateArray())
                        {
                            long from = row.GetProperty("from").GetInt64();
                            long to = row.GetProperty("to").GetInt64();
                            if (from > 0 && to > 0) moved[from] = to;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _moved = moved;
            }
        }
    }
}
