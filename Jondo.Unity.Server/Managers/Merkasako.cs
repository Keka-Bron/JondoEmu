using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The merkasako, which is the haven bag: one's own space, entered from anywhere.
    ///
    /// Its maps are all in subzone 851 and have no coordinates —they come out as (0,0) in
    /// MapPositions— because they are not in the world. Each one is a THEME, and the client's
    /// HavenBagThemes table gives the 48 with their map: theme 1 is Kerubim's, 4 is
    /// Allister's, and so on.
    ///
    /// Almost all of them also carry a normal zaap, with the same drawing as the world's, a chest
    /// (drawing 12367, the same as in houses) and the lottery (drawing 51031, which is only here).
    ///
    /// What is spoken with the client, from the captures:
    ///
    ///   jbn { f2: whose }           the button and the H key
    ///   jbl { f1: theme }           switch theme
    ///   jbv -> jbm                  open furniture placing mode
    ///   jbg { f2 (rep): {f1: cell, f2: furniture, f3: rotation} }   store the room
    ///   jbu { f1 (rep): {f1: cell, f2: furniture, f3: rotation} }   what is placed
    /// </summary>
    public static class Merkasako
    {
        /// <summary>The subzone where all the haven bag maps live.</summary>
        public const int SubArea = 851;

        /// <summary>The chest's drawing, the same as the houses'.</summary>
        public const int ChestGfx = 12367;

        /// <summary>The "Cofre" element type, from the client's interactives table.</summary>
        public const int ChestType = 85;

        /// <summary>The skill a chest offers. In the house capture the iwn carries f4: 104.</summary>
        public const int ChestSkill = 104;

        /// <summary>The theme one starts with, Kerubim's.</summary>
        public const int DefaultTheme = 1;

        private static readonly Dictionary<int, long> _themes = new Dictionary<int, long>();
        private static readonly Dictionary<long, int> _themeOfMap = new Dictionary<long, int>();
        private static readonly HashSet<long> _maps = new HashSet<long>();
        private static readonly HashSet<long> _furniture = new HashSet<long>();

        public static int ThemeCount => _themes.Count;
        public static int FurnitureCount => _furniture.Count;

        public static void Initialize()
        {
            _themes.Clear();
            _themeOfMap.Clear();
            _maps.Clear();
            _furniture.Clear();

            LoadMaps();
            LoadThemes();

            int conZaap = 0, conCofre = 0;
            foreach (long mapId in _maps)
            {
                if (Interactives.ZaapByGfx(mapId).Id != 0) conZaap++;
                if (ChestOf(mapId).Id != 0) conCofre++;
            }

            Console.WriteLine($"[Merkasako] {_maps.Count} decorados ({conZaap} con zaap, {conCofre} " +
                              $"con cofre), {_themes.Count} temas, {_furniture.Count} muebles.");
        }

        private static void LoadMaps()
        {
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT MapId FROM MapPositions WHERE SubAreaId = $sub;";
                command.Parameters.AddWithValue("$sub", SubArea);

                using var reader = command.ExecuteReader();
                while (reader.Read()) _maps.Add(reader.GetInt64(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudo leer la subzona {SubArea}: {ex.Message}");
            }
        }

        private static void LoadThemes()
        {
            string path = Paths.HavenBagJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Merkasako] Falta {Path.GetFileName(path)}; sin él no hay temas. " +
                                  "Genéralo con tools/extract_merkasako.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));

                if (doc.RootElement.TryGetProperty("themes", out var temas))
                {
                    foreach (var entry in temas.EnumerateObject())
                    {
                        if (!int.TryParse(entry.Name, out int id)) continue;
                        long mapId = entry.Value.GetInt64();
                        // A theme whose map is not in the world is no use: it would lead to nothing.
                        if (!_maps.Contains(mapId)) continue;

                        _themes[id] = mapId;
                        _themeOfMap[mapId] = id;
                    }
                }

                if (doc.RootElement.TryGetProperty("furniture", out var muebles))
                {
                    foreach (var entry in muebles.EnumerateObject())
                    {
                        if (long.TryParse(entry.Name, out long tipo)) _furniture.Add(tipo);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Merkasako] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        public static bool IsHavenBag(long mapId) => _maps.Contains(mapId);

        /// <summary>Does that piece of furniture exist in the client's catalogue?</summary>
        public static bool IsFurniture(long typeId) => _furniture.Contains(typeId);

        /// <summary>A theme's map. If the number does not exist, the usual one.</summary>
        public static long MapOfTheme(int theme)
        {
            if (_themes.TryGetValue(theme, out long mapId)) return mapId;
            if (_themes.TryGetValue(DefaultTheme, out long porDefecto)) return porDefecto;

            foreach (var cualquiera in _themes.Values) return cualquiera;
            return 0;
        }

        /// <summary>Which theme this map belongs to.</summary>
        public static int ThemeOfMap(long mapId)
            => _themeOfMap.TryGetValue(mapId, out int theme) ? theme : DefaultTheme;

        /// <summary>
        /// The zaap of a haven bag map, recognised by the drawing just like the world's.
        ///
        /// <see cref="Interactives.ZaapOf"/> cannot be used as is because that one requires the
        /// map to be in the client's zaap table, and these are not: they are not destinations
        /// travelled to, but travelled from.
        /// </summary>
        public static Interactives.Element ZaapOf(long mapId)
            => _maps.Contains(mapId) ? Interactives.ZaapByGfx(mapId) : default;

        /// <summary>The chest of a haven bag map.</summary>
        public static Interactives.Element ChestOf(long mapId)
            => _maps.Contains(mapId) ? Interactives.ElementByGfx(mapId, ChestGfx) : default;
    }
}
