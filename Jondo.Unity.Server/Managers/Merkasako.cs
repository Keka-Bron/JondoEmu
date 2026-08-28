using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// El merkasako, que es el havre-sac: el espacio propio al que se entra desde cualquier sitio.
    ///
    /// Sus mapas están todos en la subzona 851 y no tienen coordenadas —salen como (0,0) en
    /// MapPositions— porque no están en el mundo. Cada uno es un DECORADO, y la tabla
    /// HavenBagThemes del cliente da los 48 con su mapa: el tema 1 es el de Kerubim, el 4 el de
    /// Allister, y así.
    ///
    /// Casi todos llevan además un zaap del normal, con el mismo dibujo que los del mundo, un cofre
    /// (dibujo 12367, el mismo que en las casas) y la lotería (dibujo 51031, que solo está aquí).
    ///
    /// Lo que se habla con el cliente, de las capturas:
    ///
    ///   jbn { f2: de quién }        el botón y la tecla H
    ///   jbl { f1: tema }            cambiarse de decorado
    ///   jbv -> jbm                  abrir el modo de colocar muebles
    ///   jbg { f2 (rep): {f1: casilla, f2: mueble, f3: giro} }   guardar la habitación
    ///   jbu { f1 (rep): {f1: casilla, f2: mueble, f3: giro} }   lo que hay puesto
    /// </summary>
    public static class Merkasako
    {
        /// <summary>La subzona donde viven todos los mapas del merkasako.</summary>
        public const int SubArea = 851;

        /// <summary>El dibujo del cofre, el mismo que el de las casas.</summary>
        public const int ChestGfx = 12367;

        /// <summary>El tipo de elemento "Cofre", de la tabla de interactivos del cliente.</summary>
        public const int ChestType = 85;

        /// <summary>La habilidad que ofrece un cofre. En la captura de la casa el iwn lleva f4: 104.</summary>
        public const int ChestSkill = 104;

        /// <summary>El decorado con el que empieza uno, el de Kerubim.</summary>
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
                        // Un tema cuyo mapa no está en el mundo no sirve: llevaría a la nada.
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

        /// <summary>¿Existe ese mueble en el catálogo del cliente?</summary>
        public static bool IsFurniture(long typeId) => _furniture.Contains(typeId);

        /// <summary>El mapa de un decorado. Si el número no existe, el de siempre.</summary>
        public static long MapOfTheme(int theme)
        {
            if (_themes.TryGetValue(theme, out long mapId)) return mapId;
            if (_themes.TryGetValue(DefaultTheme, out long porDefecto)) return porDefecto;

            foreach (var cualquiera in _themes.Values) return cualquiera;
            return 0;
        }

        /// <summary>De qué decorado es este mapa.</summary>
        public static int ThemeOfMap(long mapId)
            => _themeOfMap.TryGetValue(mapId, out int theme) ? theme : DefaultTheme;

        /// <summary>
        /// El zaap de un mapa del merkasako, reconocido por el dibujo igual que los del mundo.
        ///
        /// No se puede usar <see cref="Interactives.ZaapOf"/> tal cual porque aquélla exige que el
        /// mapa esté en la tabla de zaaps del cliente, y estos no lo están: son destinos a los que
        /// no se viaja, sino desde los que se viaja.
        /// </summary>
        public static Interactives.Element ZaapOf(long mapId)
            => _maps.Contains(mapId) ? Interactives.ZaapByGfx(mapId) : default;

        /// <summary>El cofre de un mapa del merkasako.</summary>
        public static Interactives.Element ChestOf(long mapId)
            => _maps.Contains(mapId) ? Interactives.ElementByGfx(mapId, ChestGfx) : default;
    }
}
