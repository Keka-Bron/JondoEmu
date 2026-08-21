using System;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Centralized resolution of the emulator's paths.
    ///
    /// Every file used to carry absolute paths to C:\Jondo scattered all over the code (28 of
    /// them in total). Now everything the emulator needs lives inside the emulator folder and the
    /// root is derived at run time from the assembly directory.
    ///
    /// La raíz no guarda ficheros sueltos: quien se baje el emulador tiene que ver el .exe y poco
    /// más, sin dudar de qué abrir. Los datos van en <c>datos\</c> y las bases en <c>bases\</c>.
    /// La búsqueda mira esas carpetas primero y la raíz después, así que una instalación a medio
    /// mover sigue arrancando igual; y si tampoco está ahí, se cae a la ruta histórica.
    /// </summary>
    public static class Paths
    {
        /// <summary>Historical location of the data, used as a fallback.</summary>
        public const string LegacyRoot = @"C:\Jondo 3.6.10.10";

        /// <summary>Dónde se busca cada fichero, en este orden.</summary>
        private static readonly string[] SubFolders = { "datos", "bases", "" };

        /// <summary>
        /// Data root of the emulator: the directory where the running assembly lives.
        /// With the current deployment that is the "Jondo Unity Emulator" folder.
        /// </summary>
        public static string Root { get; } = ResolveRoot();

        /// <summary>Dofus client folder (it lives outside the emulator root).</summary>
        public static string ClientDir
        {
            get
            {
                string candidate3610 = Path.GetFullPath(Path.Combine(Root, "..", "Cliente 3.6.10.10"));
                if (Directory.Exists(candidate3610)) return candidate3610;
                string fallback = Path.Combine(LegacyRoot, "Cliente 3.6.10.10");
                if (Directory.Exists(fallback)) return fallback;
                return Path.Combine(@"C:\Jondo", "DofusClient");
            }
        }

        // ─── Databases ──────────────────────────────────────────────────────────
        // Las bases se crean solas la primera vez, así que van por ResolveWritable: si todavía no
        // existen, la ruta que sale es la de bases\ y no la de la raíz.
        public static string WorldDb => ResolveWritable("world.db", DatabaseFolder);
        public static string AuthDb => ResolveWritable("auth.db", DatabaseFolder);
        /// <summary>
        /// Protocol telemetry is deliberately isolated from account and world state.  It may
        /// contain a replay sample of an unsupported client frame, so it is a diagnostic
        /// artefact that can be archived or deleted without touching player data.
        /// </summary>
        public static string PacketTelemetryDb => ResolveWritable("packet_telemetry.db", DatabaseFolder);
        public static string WorldZip => Resolve("world.zip");

        public static string WorldConnectionString => "Data Source=" + WorldDb.Replace('\\', '/');
        public static string AuthConnectionString => "Data Source=" + AuthDb.Replace('\\', '/');
        public static string PacketTelemetryConnectionString => "Data Source=" + PacketTelemetryDb.Replace('\\', '/');

        // ─── Game data ──────────────────────────────────────────────────────────
        public static string DataDir => ResolveDir("dofus3_data");
        /// <summary>
        /// Version-pinned, read-only game-content snapshots. Unlike <see cref="WorldDb"/>, this
        /// directory is never player state and can be refreshed only after its manifest matches
        /// the installed client version.
        /// </summary>
        public static string ClientDataRoot => Path.Combine(Root, "client_data");
        /// <summary>The wire protocol this server binary was compiled and verified against.</summary>
        public const string ProtocolVersion = "3.6.10.10";
        // Kept as an alias for existing version-pinned game-data readers.
        public const string PinnedClientVersion = ProtocolVersion;
        private static string? _activeClientDataVersion;
        /// <summary>
        /// The newest snapshot whose manifest explicitly declares compatibility with this binary's
        /// wire protocol, or JONDO_CLIENT_DATA_VERSION when it passes the same check.  A newer
        /// Dofus extraction is retained on disk but cannot silently change a compiled protocol.
        /// </summary>
        public static string ActiveClientDataVersion => _activeClientDataVersion ??= ResolveCompatibleClientDataVersion();
        public static string ClientDataVersionDir => Path.Combine(ClientDataRoot, ActiveClientDataVersion);
        public static string MechanicsDir => Path.Combine(ClientDataVersionDir, "mechanics");
        public static string DofusDudeSnapshotDir => Path.Combine(ClientDataVersionDir, "dofusdude");
        public static string ServerClientDataDir => Path.Combine(ClientDataVersionDir, "server");
        public static string ProtocolDataDir => Path.Combine(ClientDataVersionDir, "protocol");
        public static string ProtocolPacketPolicyJson => Path.Combine(ProtocolDataDir, "packet-policy.json");
        private static bool? _serverClientDataValid;
        /// <summary>
        /// Whether the executable accepted the version-pinned server snapshot.  This is exposed
        /// for startup diagnostics; callers must still resolve an individual filename because a
        /// snapshot intentionally contains only server-owned static inputs, not player state.
        /// </summary>
        public static bool UsingServerClientData => IsServerClientDataValid();
        public static string WalkableCellsJson => Resolve("map_walkable_cells.json");
        // FIGHT cells: walkable (mov=1, nonWalkableDuringFight=0) and opaque (los=0).
        // Generated by extract_fight_cells.py from the client bundles.
        public static string FightCellsJson => Resolve("map_fight_cells.json");
        // Accumulated experience required for each character level.
        // Generated by extract_character_xp.py from the client bundles.
        public static string CharacterXpJson => Resolve("character_xp.json");
        // Base look of each breed (bones, skins, scales and default colors).
        // Generated by extract_breed_looks.py from the client bundles.
        public static string BreedLooksJson => Resolve("breed_looks.json");
        // Character heads: the skin of each one and which is the default per breed and sex.
        // Generated by extract_heads.py from the client bundles.
        public static string HeadsJson => Resolve("heads.json");
        // What a point of each characteristic costs, per breed and per band.
        // Generated by extract_breed_stats.py from the client bundles.
        public static string BreedStatsJson => Resolve("breed_stats.json");
        // The sets, and what wearing several pieces of one is worth.
        // Generated by extract_item_sets.py from the dofusdude dump.
        public static string ItemSetsJson => Resolve("item_sets.json");
        // En qué campo del mensaje va el valor de cada efecto de objeto, aprendido de la captura.
        public static string EffectFieldsJson => Resolve("item_effect_fields.json");
        // The dungeons: their rooms, their entrance and their exit.
        // Generated by extract_dungeons.py from the client bundles.
        public static string DungeonsJson => Resolve("dungeons.json");
        // Lo que se puede clicar en cada mapa, con su casilla y su dibujo, y los zaaps con su
        // mapa y su subzona. Los genera extract_interactivos.py de los bundles del cliente.
        public static string InteractiveElementsJson
        {
            get
            {
                string pinned = Resolve("interactive_elements_3.6.10.10.json");
                return File.Exists(pinned) ? pinned : Resolve("interactive_elements.json");
            }
        }
        public static string WorldInteractiveTransitionsJson
            => Resolve("world_interactive_transitions_3.6.10.10.json");
        /// <summary>
        /// Client-evidenced return cells for interior maps whose outward world-graph transition
        /// has no reciprocal edge.  This is static game content, never a player-position table.
        /// </summary>
        public static string WorldInteractiveReturnsJson
            => Resolve("world_interactive_returns_3.6.10.10.json");
        /// <summary>
        /// Los catálogos de oficios, habilidades y recetas en crudo. Se quedan fuera de
        /// <c>datos</c> a propósito: el servidor los importa a world.db y nunca los sirve.
        ///
        /// Se busca en <c>dofus3_data</c> ANTES que en <c>JsonFromDofusDude</c>, que es donde los
        /// puso quien escribió esto. Los tres ficheros salen del volcado del cliente y ya vivían en
        /// dofus3_data desde antes, así que mirando sólo en la carpeta nueva no se encontraban y la
        /// importación se saltaba sola sin que nadie se enterara: las tablas quedaban creadas y
        /// vacías. Se sigue mirando en las dos carpetas de antes, porque quien los tenga allí no
        /// tiene por qué moverlos.
        ///
        /// La comprobación es por FICHERO y no por carpeta: <c>dofus3_data</c> existe en cualquier
        /// instalación, así que preguntar si existe la carpeta la habría elegido siempre, tuviera
        /// dentro los catálogos o no.
        /// </summary>
        public static string DofusDudeJsonDir
        {
            get
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(ServerClientDataDir, "JsonFromDofusDude"),
                    // datos/JsonFromDofusDude es donde los deja tools/extraer_oficios.py al
                    // sacarlos del cliente instalado: los datos del emulador viven en datos/,
                    // como todo lo demás que lee. Las otras dos rutas son las históricas, para
                    // quien ya tuviera los dumps de dofusdude descargados ahí.
                    Path.Combine(Root, "datos", "JsonFromDofusDude"),
                    DataDir,
                    Path.Combine(Root, "JsonFromDofusDude"),
                    Path.GetFullPath(Path.Combine(Root, "..", "JsonFromDofusDude")),
                })
                {
                    if (File.Exists(Path.Combine(candidate, "jobs.json"))) return candidate;
                }

                return Path.Combine(Root, "datos", "JsonFromDofusDude");
            }
        }
        public static string JobsJson => Path.Combine(DofusDudeJsonDir, "jobs.json");
        public static string SkillsJson => Path.Combine(DofusDudeJsonDir, "skills.json");
        public static string RecipesJson => Path.Combine(DofusDudeJsonDir, "recipes.json");
        public static string WaypointsJson => Resolve("waypoints.json");
        public static string ZaapOverridesJson => Resolve("zaap_overrides.json");
        public static string HavenBagJson => Resolve("havenbag.json");
        public static string HousesJson => Resolve("houses.json");
        /// <summary>Official static house types extracted from HousesDataRoot (client 3.6.10.10).</summary>
        public static string HouseTemplatesJson => Resolve("house_templates_3.6.10.10.json");
        /// <summary>Server-owned exterior doors and their configured interior destinations.</summary>
        public static string HouseWorldJson => Resolve("casas_mundo_3.6.10.10.json");
        public static string TitlesOrnamentsJson => Resolve("titles_ornaments.json");
        public static string CosmeticsJson => Resolve("cosmetics.json");
        public static string CosmeticSkinsJson => Resolve("cosmetic_skins.json");
        // El aspecto de cada montura, indexado por el objeto que la da. Lo genera
        // extract_monturas.py de los bundles del cliente.
        public static string MountsJson => Resolve("mounts.json");
        // Qué vende cada NPC de tienda y a qué precio, medido del servidor de torneos con
        // tools/extraer_tiendas.py.
        public static string NpcShopsJson => Resolve("npc_shops.json");
        // Las parejas de hechizo base/variante, una por cada hueco de la barra. Del volcado del
        // cliente; el personaje lleva uno de cada pareja, no los dos.
        public static string SpellVariantsJson
        {
            get
            {
                string inData = Path.Combine(DataDir, "spell_variants.json");
                return File.Exists(inData) ? inData : Resolve("spell_variants.json");
            }
        }

        // The three blocks of the world entry, taken from the 3.6.10.10 capture with
        // extraer_world.py. They are separate files because the real server does not push them in
        // one go: it sends a block, waits for the client to confirm, and only then carries on.
        public static string WorldStageAfterCharacter => Resolve("world_etapa1_tras_elegir_personaje.bin");
        public static string WorldStageAfterConfirm => Resolve("world_etapa2_tras_confirmar.bin");
        public static string WorldStageMap => Resolve("world_etapa3_mapa.bin");

        public static string LogsDir
        {
            get
            {
                string dir = Path.Combine(Root, "logs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string MapDumpCoordinates => Path.Combine(LogsDir, "map_dump_coordinates.csv");
        public static string MapDumpScrolls => Path.Combine(LogsDir, "map_dump_scrolls.csv");
        public static string MapDumpInfos => Path.Combine(LogsDir, "map_dump_infos.csv");

        // ─── Logs ───────────────────────────────────────────────────────────────
        // Logs are ALWAYS written inside the logs/ folder to keep the root clean.
        public static string DebugLog => Path.Combine(LogsDir, "emulator_debug.log");
        public static string TrafficLog => Path.Combine(LogsDir, "gameserver_traffic.log");

        /// <summary>
        /// File that marks which folder is the emulator's data root.
        ///
        /// Deliberately NOT world.db: there are 0-byte copies of world.db inside bin\Debug,
        /// bin\Release and the project folder, and the upward search would have stopped at them,
        /// taking a build folder with an empty database as the root. This marker only exists in
        /// the real root.
        /// </summary>
        private const string RootMarker = ".jondo-root";

        /// <summary>
        /// Locates the data root by walking up from the assembly directory until the marker file
        /// shows up. That way it works the same running the deployment
        /// (...\Jondo Unity Emulator\) as with `dotnet run` (...\bin\Debug\net10.0\), which would
        /// otherwise take the build folder as the root and never find the data.
        /// </summary>
        private static string ResolveRoot()
        {
            foreach (string start in new[] { SafeBaseDirectory(), SafeCurrentDirectory() })
            {
                string found = SearchUpwards(start);
                if (found != null) return found;
            }

            // No marker: fall back to the assembly directory, and failing that, to the historical path.
            string fallback = SafeBaseDirectory();
            return string.IsNullOrEmpty(fallback) ? LegacyRoot : fallback;
        }

        private static string SearchUpwards(string start)
        {
            if (string.IsNullOrEmpty(start)) return null;
            try
            {
                var dir = new DirectoryInfo(start);
                // 6 levels are more than enough to cover bin\<config>\<tfw>\ and the deployment.
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

        /// <summary>
        /// Busca un fichero en datos\, en bases\ y en la raíz; si no está en ninguna, en la ruta
        /// histórica. Cuando no aparece por ningún lado devuelve la ruta de <c>datos\</c>, que es
        /// donde debe crearse.
        /// </summary>
        public static string Resolve(string filename)
        {
            if (IsServerClientDataValid())
            {
                string snapshot = Path.Combine(ServerClientDataDir, filename);
                if (File.Exists(snapshot)) return snapshot;
            }
            foreach (string sub in SubFolders)
            {
                string candidate = Combine(Root, Path.Combine(sub, filename));
                if (File.Exists(candidate)) return candidate;
            }

            string legacy = Combine(LegacyRoot, filename);
            if (File.Exists(legacy))
            {
                Console.WriteLine($"[Paths][WARNING] '{filename}' is still at the old path ({LegacyRoot}). " +
                                  $"Move it to {Root} to complete the migration.");
                return legacy;
            }

            return Combine(Root, Path.Combine(DataFolder, filename));
        }

        /// <summary>Only a complete manifest matching this executable's pinned client version may override datos/.</summary>
        private static bool IsServerClientDataValid()
        {
            if (_serverClientDataValid.HasValue) return _serverClientDataValid.Value;
            try
            {
                string manifestPath = Path.Combine(ServerClientDataDir, "manifest.json");
                if (!File.Exists(manifestPath)) return (_serverClientDataValid = false).Value;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!document.RootElement.TryGetProperty("clientVersion", out JsonElement version) ||
                    version.GetString() != ActiveClientDataVersion ||
                    !document.RootElement.TryGetProperty("serverProtocolVersion", out JsonElement protocolVersion) ||
                    protocolVersion.GetString() != ProtocolVersion ||
                    !document.RootElement.TryGetProperty("files", out JsonElement files) ||
                    files.ValueKind != JsonValueKind.Array)
                    return (_serverClientDataValid = false).Value;
                foreach (JsonElement file in files.EnumerateArray())
                {
                    if (!file.TryGetProperty("path", out JsonElement name) || name.ValueKind != JsonValueKind.String) return (_serverClientDataValid = false).Value;
                    string relative = name.GetString() ?? "";
                    if (Path.IsPathRooted(relative) || relative.Contains("..")) return (_serverClientDataValid = false).Value;
                    if (!File.Exists(Path.Combine(ServerClientDataDir, relative))) return (_serverClientDataValid = false).Value;
                }
                return (_serverClientDataValid = true).Value;
            }
            catch { return (_serverClientDataValid = false).Value; }
        }

        private static string ResolveCompatibleClientDataVersion()
        {
            string? requested = Environment.GetEnvironmentVariable("JONDO_CLIENT_DATA_VERSION");
            if (!string.IsNullOrWhiteSpace(requested) && IsCompatibleClientDataVersion(requested)) return requested;

            string best = ProtocolVersion;
            try
            {
                if (!Directory.Exists(ClientDataRoot)) return best;
                foreach (string candidate in Directory.EnumerateDirectories(ClientDataRoot).Select(Path.GetFileName))
                {
                    if (string.IsNullOrWhiteSpace(candidate) || !Version.TryParse(candidate, out Version? parsed) ||
                        !IsCompatibleClientDataVersion(candidate)) continue;
                    if (!Version.TryParse(best, out Version? current) || parsed > current) best = candidate;
                }
            }
            catch { }
            return best;
        }

        private static bool IsCompatibleClientDataVersion(string version)
        {
            try
            {
                if (Path.IsPathRooted(version) || version.Contains("..")) return false;
                string manifest = Path.Combine(ClientDataRoot, version, "server", "manifest.json");
                if (!File.Exists(manifest)) return false;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                return document.RootElement.TryGetProperty("clientVersion", out JsonElement dataVersion) &&
                       dataVersion.GetString() == version &&
                       document.RootElement.TryGetProperty("serverProtocolVersion", out JsonElement protocolVersion) &&
                       protocolVersion.GetString() == ProtocolVersion;
            }
            catch { return false; }
        }

        /// <summary>Same as Resolve but for directories.</summary>
        public static string ResolveDir(string dirname)
        {
            foreach (string sub in SubFolders)
            {
                string candidate = Combine(Root, Path.Combine(sub, dirname));
                if (Directory.Exists(candidate)) return candidate;
            }

            string legacy = Combine(LegacyRoot, dirname);
            if (Directory.Exists(legacy))
            {
                Console.WriteLine($"[Paths][WARNING] Directory '{dirname}' is still at the old path ({LegacyRoot}).");
                return legacy;
            }
            return Combine(Root, Path.Combine(DataFolder, dirname));
        }

        /// <summary>Carpeta de datos. Lo que no exista se creará aquí.</summary>
        public const string DataFolder = "datos";

        /// <summary>Carpeta de las bases de datos, que son las que el emulador escribe.</summary>
        public const string DatabaseFolder = "bases";

        /// <summary>
        /// Igual que Resolve pero para ficheros que el emulador CREA: si no existe todavía, la
        /// ruta que devuelve es la de la carpeta que le toca, no la de la raíz.
        /// </summary>
        private static string ResolveWritable(string filename, string folder)
        {
            foreach (string sub in SubFolders)
            {
                string candidate = Combine(Root, Path.Combine(sub, filename));
                if (File.Exists(candidate)) return candidate;
            }

            string legacy = Combine(LegacyRoot, filename);
            if (File.Exists(legacy)) return legacy;

            string destino = Combine(Root, folder);
            try { Directory.CreateDirectory(destino); } catch { }
            return Path.Combine(destino, filename);
        }

        private static string Combine(string a, string b) => Path.Combine(a, b);

        public static void LogResolvedPaths()
        {
            Console.WriteLine($"[Paths] emulator root     : {Root}");
            Console.WriteLine($"[Paths] world.db          : {WorldDb}");
            Console.WriteLine($"[Paths] auth.db           : {AuthDb}");
            Console.WriteLine($"[Paths] packet telemetry  : {PacketTelemetryDb}");
            Console.WriteLine($"[Paths] dofus3_data       : {DataDir}");
            Console.WriteLine($"[Paths] client data       : {ClientDataVersionDir}");
            Console.WriteLine($"[Paths] active data ver.  : {ActiveClientDataVersion}");
            Console.WriteLine($"[Paths] protocol version  : {ProtocolVersion}");
            Console.WriteLine($"[Paths] packet policy     : {ProtocolPacketPolicyJson}");
            Console.WriteLine($"[Paths] server snapshot   : {(UsingServerClientData ? "validated and preferred" : "not valid; using legacy datos/")}");
            if (UsingServerClientData)
                Console.WriteLine($"[Paths] snapshot manifest : {Path.Combine(ServerClientDataDir, "manifest.json")}");
            Console.WriteLine($"[Paths] mechanics         : {MechanicsDir}");
            Console.WriteLine($"[Paths] walkable cells    : {WalkableCellsJson}");
            Console.WriteLine($"[Paths] client            : {ClientDir}");
        }
    }
}
