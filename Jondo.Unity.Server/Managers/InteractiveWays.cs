using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Elements that lead to more than one place, each way under its own name and its own
    /// condition (content/interactives/ways.json): the class temples' statues.
    /// </summary>
    /// <remarks>
    /// A statue offers «Ir a Astrub.» (the client's SkillNamesDataRoot 47) to whoever has read his
    /// temple's book and «Hacia el territorio de los dioses» (48) to whoever has quest 1938 in hand
    /// or done. Both the names and the conditions are the client's: the 2.73 world graph carries
    /// each statue's two transitions, type 32 with skill 184 and that criterion.
    /// A plain passage (TeleportManager) has one destination and no name, and offered the
    /// territory to everybody.
    /// </remarks>
    public static class InteractiveWays
    {
        public const string File = "interactives/ways.json";

        public sealed class Way
        {
            /// <summary>The skill's name, a row of the client's SkillNamesDataRoot.</summary>
            public int Name { get; init; }
            public long ToMap { get; init; }
            public int ToCell { get; init; }

            /// <summary>Who is offered it, in the quests' criterion language; empty for anybody.</summary>
            public string Criterion { get; init; } = "";
        }

        public sealed class WayElement
        {
            public long MapId { get; init; }
            public int ElementId { get; init; }
            public int Cell { get; init; }
            public int Gfx { get; init; }
            public int Type { get; init; }
            public int Skill { get; init; }
            public IReadOnlyList<Way> Ways { get; init; } = Array.Empty<Way>();
        }

        private static readonly Dictionary<(long Map, int Element), WayElement> _byElement = new();
        private static readonly object _lock = new();
        private static volatile bool _loaded;

        public static IReadOnlyCollection<WayElement> All
        {
            get { Ensure(); return _byElement.Values; }
        }

        public static WayElement? Of(long mapId, int elementId)
        {
            Ensure();
            return _byElement.TryGetValue((mapId, elementId), out var element) ? element : null;
        }

        public static bool Has(long mapId, int elementId) => Of(mapId, elementId) != null;

        /// <summary>The way an element offers under this name, or null.</summary>
        public static Way? WayOf(WayElement element, int name)
        {
            foreach (var way in element.Ways)
                if (way.Name == name) return way;
            return null;
        }

        public static void Load() => Ensure();

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try { Read(); }
                finally { _loaded = true; }
            }
        }

        private static void Read()
        {
            string path = Paths.ContentFile(File);
            if (!System.IO.File.Exists(path)) return;
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("elements", out var list) || list.ValueKind != JsonValueKind.Array) return;
                foreach (var row in list.EnumerateArray())
                {
                    var ways = new List<Way>();
                    if (row.TryGetProperty("ways", out var wayList) && wayList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var way in wayList.EnumerateArray())
                        {
                            ways.Add(new Way
                            {
                                Name = (int)Number(way, "name"),
                                ToMap = Number(way, "toMap"),
                                ToCell = (int)Number(way, "toCell"),
                                Criterion = way.TryGetProperty("criterion", out var c) && c.ValueKind == JsonValueKind.String
                                    ? c.GetString() ?? "" : "",
                            });
                        }
                    }
                    var element = new WayElement
                    {
                        MapId = Number(row, "map"),
                        ElementId = (int)Number(row, "element"),
                        Cell = (int)Number(row, "cell"),
                        Gfx = (int)Number(row, "gfx"),
                        Type = (int)Number(row, "type"),
                        Skill = (int)Number(row, "skill"),
                        Ways = ways,
                    };
                    if (element.MapId == 0 || element.ElementId == 0 || ways.Count == 0) continue;
                    _byElement[(element.MapId, element.ElementId)] = element;
                }
                Console.WriteLine($"[Ways] {_byElement.Count} element(s) with named ways.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ways] Could not read {File}: {ex.Message}");
            }
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
    }
}
