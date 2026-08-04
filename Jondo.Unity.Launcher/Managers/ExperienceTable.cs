using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Tabla de experiencia del personaje: cuánta experiencia acumulada hace falta para alcanzar
    /// cada nivel. Sale de los bundles del cliente
    /// (data_assets_characterxpmappingsdataroot.asset.bundle) mediante extract_character_xp.py.
    ///
    /// Los primeros valores cuadran con lo que ya estaba escrito a mano en el kri —nivel 2 = 110
    /// y nivel 3 = 650— y con la captura oficial de fin de combate, que para un personaje de
    /// nivel 3 manda 650 como suelo del nivel y 1500 como umbral del siguiente.
    /// </summary>
    public static class ExperienceTable
    {
        private static readonly SortedDictionary<int, long> _floors = new SortedDictionary<int, long>();
        private static int _maxLevel = 1;

        public static bool IsLoaded => _floors.Count > 0;
        public static int MaxLevel => _maxLevel;

        public static void Initialize()
        {
            _floors.Clear();
            string path = Paths.CharacterXpJson;

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ExperienceTable] AVISO: no se encuentra {path}. " +
                                  "La experiencia no se podrá calcular; ejecuta extract_character_xp.py.");
                return;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out int level) && prop.Value.TryGetInt64(out long xp))
                    {
                        _floors[level] = xp;
                    }
                }
                _maxLevel = _floors.Count > 0 ? _floors.Keys.Max() : 1;
                Console.WriteLine($"[ExperienceTable] Tabla de experiencia cargada: {_floors.Count} niveles (hasta el {_maxLevel}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExperienceTable] Error leyendo {path}: {ex.Message}");
            }
        }

        /// <summary>Experiencia acumulada con la que empieza el nivel indicado.</summary>
        public static long LevelFloor(int level)
        {
            if (_floors.Count == 0) return 0;
            if (_floors.TryGetValue(level, out long xp)) return xp;
            return level <= 1 ? 0 : _floors[Math.Min(level, _maxLevel)];
        }

        /// <summary>Experiencia acumulada a la que se sube al nivel siguiente.</summary>
        public static long NextLevelFloor(int level)
        {
            if (_floors.Count == 0) return 0;
            int next = level + 1;
            if (next > _maxLevel) return LevelFloor(_maxLevel);
            return LevelFloor(next);
        }

        /// <summary>Nivel que corresponde a una experiencia acumulada.</summary>
        public static int LevelForXp(long experience)
        {
            if (_floors.Count == 0) return 1;
            int level = 1;
            foreach (var kv in _floors)
            {
                if (kv.Value > experience) break;
                level = kv.Key;
            }
            return level;
        }
    }
}
