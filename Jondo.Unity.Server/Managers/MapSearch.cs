using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Maps found from what somebody typed, for the administrator's teleport: part of an area's or
    /// a subarea's name ("bonta", "barrio de los herreros"), a coordinate ("4,-18"), or a map id.
    /// The same three Jondo Studio's map field takes (Studio/Data/MapCatalogue.Find), with the
    /// area named as well as the subarea.
    /// </summary>
    /// <remarks>
    /// The subareas' names are in world.db (SubAreaTemplates, then Translations); the areas' are
    /// not -- only their nameId, in the client's AreasDataRoot, which is what
    /// datos/areas_3.6.10.10.json keeps, because dofus3_data/ does not ship.
    /// </remarks>
    public static class MapSearch
    {
        /// <summary>One map found: where it is and what it is called.</summary>
        public sealed record Place(long MapId, int X, int Y, string Area, string SubArea, bool Outdoor);

        private static List<(Place Place, string Folded)>? _index;
        private static readonly object _gate = new();

        /// <summary>
        /// At most <paramref name="most"/> maps: outdoor ones first, then by area, subarea and
        /// coordinates, so that "bonta" reads like a walk through the city rather than a dump.
        /// </summary>
        public static IReadOnlyList<Place> Find(string? text, int most = 60)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return Array.Empty<Place>();
            var index = Index();

            IEnumerable<(Place Place, string Folded)> hits;
            if (TryCoordinates(text, out int x, out int y))
                hits = index.Where(e => e.Place.X == x && e.Place.Y == y);
            else if (long.TryParse(text, out long id))
                hits = index.Where(e => e.Place.MapId == id || e.Place.MapId.ToString(CultureInfo.InvariantCulture).Contains(text));
            else
            {
                var words = Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                hits = index.Where(e => words.All(w => e.Folded.Contains(w)));
            }

            return hits.Select(e => e.Place)
                .OrderByDescending(p => p.Outdoor)
                .ThenBy(p => p.Area, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p.SubArea, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.MapId)
                .Take(most)
                .ToList();
        }

        /// <summary>"4,-18", "4 -18" and "[4, -18]" all mean the same square.</summary>
        public static bool TryCoordinates(string text, out int x, out int y)
        {
            x = y = 0;
            string cleaned = text.Replace('[', ' ').Replace(']', ' ').Replace(',', ' ').Replace(';', ' ').Trim();
            string[] parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
        }

        /// <summary>Every map with a place in the world and its two names, read once.</summary>
        private static List<(Place Place, string Folded)> Index()
        {
            lock (_gate)
            {
                if (_index != null) return _index;
                var (subAreaNames, areaOfSubArea) = SubAreas();
                var areaNames = Areas();

                var index = new List<(Place Place, string Folded)>();
                foreach (var map in MapManager.Maps.Values)
                {
                    string sub = subAreaNames.TryGetValue(map.SubAreaId, out var s) ? s : "";
                    string area = areaOfSubArea.TryGetValue(map.SubAreaId, out int a) && areaNames.TryGetValue(a, out var n) ? n : "";
                    var place = new Place(map.MapId, map.PosX, map.PosY, area, sub, map.Outdoor);
                    index.Add((place, Fold(area + " " + sub)));
                }
                // Not kept while empty: built before the maps were read, it would stay empty for good.
                if (index.Count > 0) _index = index;
                return index;
            }
        }

        private static (Dictionary<int, string> Names, Dictionary<int, int> Area) SubAreas()
        {
            var names = new Dictionary<int, string>();
            var areas = new Dictionary<int, int>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var nameIds = new Dictionary<int, long>();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Data FROM SubAreaTemplates;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int id = reader.GetInt32(0);
                        using var doc = JsonDocument.Parse(reader.IsDBNull(1) ? "{}" : reader.GetString(1));
                        if (doc.RootElement.TryGetProperty("nameId", out var nameId)) nameIds[id] = nameId.GetInt64();
                        if (doc.RootElement.TryGetProperty("areaId", out var areaId)) areas[id] = areaId.GetInt32();
                    }
                }
                foreach (var (id, nameId) in nameIds)
                    names[id] = Translation(connection, nameId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mapas] No se han podido leer las subzonas: {ex.Message}");
            }
            return (names, areas);
        }

        private static Dictionary<int, string> Areas()
        {
            var names = new Dictionary<int, string>();
            try
            {
                string path = Paths.Resolve("areas_3.6.10.10.json");
                if (!File.Exists(path)) return names;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                foreach (var area in doc.RootElement.GetProperty("areas").EnumerateObject())
                    if (int.TryParse(area.Name, out int id)) names[id] = Translation(connection, area.Value.GetInt64());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mapas] No se han podido leer las áreas: {ex.Message}");
            }
            return names;
        }

        private static string Translation(SqliteConnection connection, long key)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
            command.Parameters.AddWithValue("$key", key.ToString(CultureInfo.InvariantCulture));
            return command.ExecuteScalar() as string ?? "";
        }

        /// <summary>Lower case and without accents: "Barrio de los artesanos" is found by "artesano".</summary>
        internal static string Fold(string text)
        {
            var folded = new StringBuilder(text.Length);
            foreach (char c in text.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    folded.Append(char.ToLowerInvariant(c));
            return folded.ToString();
        }
    }
}
