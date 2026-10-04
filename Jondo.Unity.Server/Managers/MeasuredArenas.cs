using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The arenas a capture has measured, map by map, which win over the resolver's rule.
    /// </summary>
    /// <remarks>
    /// <see cref="MapManager.ResolveArenaMapId"/> works an arena out from the map's subarea and a
    /// small id offset, and it is right where it was checked. It is wrong for the kanojedos: the
    /// Amakna one fights on a map 131,072 ids away, which no small-offset rule finds, so it fell
    /// back to an arena picked by hash. Where a capture says which arena a map uses, that is what
    /// goes, and the file says how many times it was seen.
    /// </remarks>
    public static class MeasuredArenas
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "fights/arenas.json";

        private static Dictionary<long, long>? _arenas;
        private static readonly object _lock = new();

        /// <summary>The measured arena of a roleplay map, or zero when none was measured.</summary>
        public static long Of(long roleplayMapId)
        {
            Load();
            return _arenas!.TryGetValue(roleplayMapId, out long arena) ? arena : 0;
        }

        /// <summary>How many pairs are written.</summary>
        public static int Count
        {
            get
            {
                Load();
                return _arenas!.Count;
            }
        }

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _arenas = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_arenas != null) return;
                var arenas = new Dictionary<long, long>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var entry in doc.RootElement.GetProperty("arenas").EnumerateArray())
                        {
                            long map = entry.GetProperty("map").GetInt64();
                            long arena = entry.GetProperty("arena").GetInt64();
                            if (map > 0 && arena > 0) arenas[map] = arena;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Arenas] Could not read {AuthoredFile}: {ex.Message}");
                }

                _arenas = arenas;
            }
        }
    }
}
