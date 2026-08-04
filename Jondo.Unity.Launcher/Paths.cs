using System;
using System.IO;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Resolución centralizada de rutas del emulador.
    ///
    /// Antes cada archivo llevaba rutas absolutas a C:\Jondo repartidas por todo el código
    /// (28 en total). Ahora todo lo que el emulador necesita vive dentro de la carpeta del
    /// emulador y la raíz se deduce en tiempo de ejecución del directorio del ensamblado.
    ///
    /// Para no romper instalaciones existentes, cada fichero se busca primero en la raíz nueva
    /// y, si no está, se cae a la antigua (C:\Jondo). Así una migración a medias sigue arrancando.
    /// </summary>
    public static class Paths
    {
        /// <summary>Ubicación histórica de los datos, usada como respaldo.</summary>
        public const string LegacyRoot = @"C:\Jondo";

        /// <summary>
        /// Raíz de datos del emulador: el directorio donde vive el ensamblado en ejecución.
        /// Con el despliegue actual eso es la carpeta "Jondo Unity Emulator".
        /// </summary>
        public static string Root { get; } = ResolveRoot();

        /// <summary>Carpeta del cliente de Dofus (queda fuera de la raíz del emulador).</summary>
        public static string ClientDir => Combine(LegacyRoot, "DofusClient");

        // ─── Bases de datos ─────────────────────────────────────────────────────
        public static string WorldDb => Resolve("world.db");
        public static string AuthDb => Resolve("auth.db");
        public static string WorldZip => Resolve("world.zip");

        public static string WorldConnectionString => "Data Source=" + WorldDb.Replace('\\', '/');
        public static string AuthConnectionString => "Data Source=" + AuthDb.Replace('\\', '/');

        // ─── Datos de juego ─────────────────────────────────────────────────────
        public static string DataDir => ResolveDir("dofus3_data");
        public static string WalkableCellsJson => Resolve("map_walkable_cells.json");
        // Celdas de COMBATE: transitables (mov=1, nonWalkableDuringFight=0) y opacas (los=0).
        // Lo genera extract_fight_cells.py desde los bundles del cliente.
        public static string FightCellsJson => Resolve("map_fight_cells.json");
        // Experiencia acumulada necesaria para cada nivel de personaje.
        // Lo genera extract_character_xp.py desde los bundles del cliente.
        public static string CharacterXpJson => Resolve("character_xp.json");
        public static string MapDumpCoordinates => Resolve("map_dump_coordinates.csv");
        public static string MapDumpScrolls => Resolve("map_dump_scrolls.csv");
        public static string MapDumpInfos => Resolve("map_dump_infos.csv");

        // ─── Registros ──────────────────────────────────────────────────────────
        // Los logs SIEMPRE se escriben en la raíz nueva: son de salida, no de entrada.
        public static string DebugLog => Combine(Root, "emulator_debug.log");
        public static string TrafficLog => Combine(Root, "gameserver_traffic.log");

        /// <summary>
        /// Fichero que marca cuál es la raíz de datos del emulador.
        ///
        /// Deliberadamente NO es world.db: existen copias de 0 bytes de world.db dentro de
        /// bin\Debug, bin\Release y de la carpeta del proyecto, y la búsqueda hacia arriba se
        /// habría detenido en ellas, dando por raíz una carpeta de compilación con una base de
        /// datos vacía. Este marcador solo existe en la raíz de verdad.
        /// </summary>
        private const string RootMarker = ".jondo-root";

        /// <summary>
        /// Localiza la raíz de datos subiendo desde el directorio del ensamblado hasta encontrar
        /// el fichero marcador. Así funciona igual ejecutando el despliegue
        /// (…\Jondo Unity Emulator\) que con `dotnet run` (…\bin\Debug\net10.0\), que de otro
        /// modo tomaría la carpeta de compilación como raíz y no encontraría los datos.
        /// </summary>
        private static string ResolveRoot()
        {
            foreach (string start in new[] { SafeBaseDirectory(), SafeCurrentDirectory() })
            {
                string found = SearchUpwards(start);
                if (found != null) return found;
            }

            // Sin marcador: nos quedamos con el directorio del ensamblado, y si ni eso, la ruta histórica.
            string fallback = SafeBaseDirectory();
            return string.IsNullOrEmpty(fallback) ? LegacyRoot : fallback;
        }

        private static string SearchUpwards(string start)
        {
            if (string.IsNullOrEmpty(start)) return null;
            try
            {
                var dir = new DirectoryInfo(start);
                // 6 niveles cubren de sobra bin\<config>\<tfw>\ y el despliegue.
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, RootMarker)))
                    {
                        return dir.FullName.TrimEnd(Path.DirectorySeparatorChar);
                    }
                }
            }
            catch { }
            return null;
        }

        private static string SafeBaseDirectory()
        {
            try
            {
                string d = AppContext.BaseDirectory;
                return Directory.Exists(d) ? d.TrimEnd(Path.DirectorySeparatorChar) : null;
            }
            catch { return null; }
        }

        private static string SafeCurrentDirectory()
        {
            try { return Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar); }
            catch { return null; }
        }

        /// <summary>Busca un fichero en la raíz nueva; si no está, devuelve la ruta antigua.</summary>
        public static string Resolve(string filename)
        {
            string preferred = Combine(Root, filename);
            if (File.Exists(preferred)) return preferred;

            string legacy = Combine(LegacyRoot, filename);
            if (File.Exists(legacy))
            {
                Console.WriteLine($"[Paths][AVISO] '{filename}' sigue en la ruta antigua ({LegacyRoot}). " +
                                  $"Muévelo a {Root} para completar la migración.");
                return legacy;
            }

            // No existe en ninguna parte: devolvemos la ruta nueva para que quien cree el fichero
            // lo haga ya en el sitio correcto.
            return preferred;
        }

        /// <summary>Igual que Resolve pero para directorios.</summary>
        public static string ResolveDir(string dirname)
        {
            string preferred = Combine(Root, dirname);
            if (Directory.Exists(preferred)) return preferred;

            string legacy = Combine(LegacyRoot, dirname);
            if (Directory.Exists(legacy))
            {
                Console.WriteLine($"[Paths][AVISO] El directorio '{dirname}' sigue en la ruta antigua ({LegacyRoot}).");
                return legacy;
            }
            return preferred;
        }

        private static string Combine(string a, string b) => Path.Combine(a, b);

        public static void LogResolvedPaths()
        {
            Console.WriteLine($"[Paths] Raíz del emulador : {Root}");
            Console.WriteLine($"[Paths] world.db          : {WorldDb}");
            Console.WriteLine($"[Paths] auth.db           : {AuthDb}");
            Console.WriteLine($"[Paths] dofus3_data       : {DataDir}");
            Console.WriteLine($"[Paths] celdas transitables: {WalkableCellsJson}");
            Console.WriteLine($"[Paths] cliente           : {ClientDir}");
        }
    }
}
