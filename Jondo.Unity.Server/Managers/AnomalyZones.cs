using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The zones the world map's anomalies panel lists, read off an official server's answer
    /// (content/anomalies/bonus_zones.json): the panel fills itself with every one of them, and the
    /// window of the map would not open on a list holding the anomalies' zones alone.
    /// </summary>
    public static class AnomalyZones
    {
        public const string AuthoredFile = "anomalies/bonus_zones.json";

        private static IReadOnlyList<int>? _subAreas;
        private static readonly object _lock = new();

        public static IReadOnlyList<int> SubAreas
        {
            get
            {
                if (_subAreas != null) return _subAreas;
                lock (_lock)
                {
                    if (_subAreas != null) return _subAreas;
                    var list = new List<int>();
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(Paths.ContentFile(AuthoredFile)));
                        list.AddRange(doc.RootElement.GetProperty("subareas").EnumerateArray().Select(v => v.GetInt32()));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Anomalies] Could not read {AuthoredFile}: {ex.Message}");
                    }
                    _subAreas = list;
                    return _subAreas;
                }
            }
        }
    }
}
