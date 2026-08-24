using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    /// Mutable databases live in <c>bases\</c>. Static gameplay is accepted only from a complete,
    /// versioned <c>client_data\</c> snapshot. The legacy resolver remains for migration and
    /// diagnostic fixtures; active gameplay readers use the strict methods below.
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
        public static string WorldZip => ServerData("world.zip");

        public static string WorldConnectionString => "Data Source=" + WorldDb.Replace('\\', '/');
        public static string AuthConnectionString => "Data Source=" + AuthDb.Replace('\\', '/');
        public static string PacketTelemetryConnectionString => "Data Source=" + PacketTelemetryDb.Replace('\\', '/');

        // ─── Game data ──────────────────────────────────────────────────────────
        public static string DataDir => CatalogsDir;
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
        public static string CatalogsDir => Path.Combine(ClientDataVersionDir, "catalogs");
        public static string WorldDataDir => Path.Combine(ClientDataVersionDir, "world");
        public static string MechanicsDir => Path.Combine(ClientDataVersionDir, "mechanics");
        public static string DofusDudeSnapshotDir => Path.Combine(ClientDataVersionDir, "dofusdude");
        public static string ServerClientDataDir => Path.Combine(ClientDataVersionDir, "server");
        public static string ProtocolDataDir => Path.Combine(ClientDataVersionDir, "protocol");
        public static string ProtocolPacketPolicyJson => Path.Combine(ProtocolDataDir, "packet-policy.json");
        private static bool? _serverClientDataValid;
        private static string _serverClientDataError = "not validated";
        /// <summary>
        /// Whether the executable accepted the version-pinned server snapshot.  This is exposed
        /// for startup diagnostics; callers must still resolve an individual filename because a
        /// snapshot intentionally contains only server-owned static inputs, not player state.
        /// </summary>
        public static bool UsingServerClientData => IsServerClientDataValid();
        public static string WalkableCellsJson => ServerData("map_walkable_cells.json");
        // FIGHT cells: walkable (mov=1, nonWalkableDuringFight=0) and opaque (los=0).
        // Generated by extract_fight_cells.py from the client bundles.
        public static string FightCellsJson => ServerData("map_fight_cells.json");
        // Accumulated experience required for each character level.
        // Generated by extract_character_xp.py from the client bundles.
        public static string CharacterXpJson => ServerData("character_xp.json");
        // Base look of each breed (bones, skins, scales and default colors).
        // Generated by extract_breed_looks.py from the client bundles.
        public static string BreedLooksJson => ServerData("breed_looks.json");
        // Character heads: the skin of each one and which is the default per breed and sex.
        // Generated by extract_heads.py from the client bundles.
        public static string HeadsJson => ServerData("heads.json");
        // What a point of each characteristic costs, per breed and per band.
        // Generated by extract_breed_stats.py from the client bundles.
        public static string BreedStatsJson => ServerData("breed_stats.json");
        // The sets, and what wearing several pieces of one is worth.
        // Generated by extract_item_sets.py from the dofusdude dump.
        public static string ItemSetsJson => ServerData("item_sets.json");
        // En qué campo del mensaje va el valor de cada efecto de objeto, aprendido de la captura.
        public static string EffectFieldsJson => ServerData("item_effect_fields.json");
        /// <summary>Reviewed runtime classifications that the numeric effect catalogue cannot express.</summary>
        public static string EffectRuntimeSemanticsJson => ServerData("effect_runtime_semantics.json");
        // The dungeons: their rooms, their entrance and their exit.
        // Generated by extract_dungeons.py from the client bundles.
        public static string DungeonsJson => ServerData("dungeons.json");
        // Lo que se puede clicar en cada mapa, con su casilla y su dibujo, y los zaaps con su
        // mapa y su subzona. Los genera extract_interactivos.py de los bundles del cliente.
        public static string InteractiveElementsJson
        {
            get
            {
                return ServerData("interactive_elements_3.6.10.10.json");
            }
        }
        public static string WorldInteractiveTransitionsJson
            => ServerData("world_interactive_transitions_3.6.10.10.json");
        /// <summary>
        /// Client-evidenced return cells for interior maps whose outward world-graph transition
        /// has no reciprocal edge.  This is static game content, never a player-position table.
        /// </summary>
        public static string WorldInteractiveReturnsJson
            => ServerData("world_interactive_returns_3.6.10.10.json");
        /// <summary>
        /// Server-ready profession, skill, and recipe catalogues. They are imported into query
        /// indexes but remain authoritative only in the active version snapshot.
        /// </summary>
        public static string DofusDudeJsonDir
        {
            get
            {
                string candidate = Path.Combine(ServerClientDataDir, "JsonFromDofusDude");
                _ = ServerData("JsonFromDofusDude/jobs.json");
                _ = ServerData("JsonFromDofusDude/skills.json");
                _ = ServerData("JsonFromDofusDude/recipes.json");
                return candidate;
            }
        }
        public static string JobsJson => Path.Combine(DofusDudeJsonDir, "jobs.json");
        public static string SkillsJson => Path.Combine(DofusDudeJsonDir, "skills.json");
        public static string RecipesJson => Path.Combine(DofusDudeJsonDir, "recipes.json");
        public static string WaypointsJson => ServerData("waypoints.json");
        public static string ZaapOverridesJson => ServerData("zaap_overrides.json");
        public static string HavenBagJson => ServerData("havenbag.json");
        public static string HousesJson => ServerData("houses.json");
        /// <summary>Official static house types extracted from HousesDataRoot (client 3.6.10.10).</summary>
        public static string HouseTemplatesJson => ServerData("house_templates_3.6.10.10.json");
        /// <summary>Server-owned exterior doors and their configured interior destinations.</summary>
        public static string HouseWorldJson => ServerData("casas_mundo_3.6.10.10.json");
        public static string TitlesOrnamentsJson => ServerData("titles_ornaments.json");
        public static string CosmeticsJson => ServerData("cosmetics.json");
        public static string CosmeticSkinsJson => ServerData("cosmetic_skins.json");
        // El aspecto de cada montura, indexado por el objeto que la da. Lo genera
        // extract_monturas.py de los bundles del cliente.
        public static string MountsJson => ServerData("mounts.json");
        // Qué vende cada NPC de tienda y a qué precio, medido del servidor de torneos con
        // tools/extraer_tiendas.py.
        public static string NpcShopsJson => ServerData("npc_shops.json");
        // Las parejas de hechizo base/variante, una por cada hueco de la barra. Del volcado del
        // cliente; el personaje lleva uno de cada pareja, no los dos.
        public static string SpellVariantsJson
        {
            get
            {
                return ServerData("spell_variants.json");
            }
        }

        // The three blocks of the world entry, taken from the 3.6.10.10 capture with
        // extraer_world.py. They are separate files because the real server does not push them in
        // one go: it sends a block, waits for the client to confirm, and only then carries on.
        public static string WorldStageAfterCharacter => ServerData("world_etapa1_tras_elegir_personaje.bin");
        public static string WorldStageAfterConfirm => ServerData("world_etapa2_tras_confirmar.bin");
        public static string WorldStageMap => ServerData("world_etapa3_mapa.bin");

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

        /// <summary>
        /// Resolves immutable server-ready content only from the active version snapshot.  Static
        /// gameplay readers must use this method instead of <see cref="Resolve"/> so a missing or
        /// corrupt snapshot cannot silently mix with an older datos/ directory.
        /// </summary>
        public static string ServerData(string relative)
        {
            ValidateSafeRelative(relative, "server data");
            if (!IsServerClientDataValid())
                throw new InvalidDataException($"Rejected client_data/{ActiveClientDataVersion}/server: {_serverClientDataError}");

            string path = Path.GetFullPath(Path.Combine(ServerClientDataDir, relative));
            string root = Path.GetFullPath(ServerClientDataDir) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException($"Static server data is not present in the active snapshot: {relative}", path);
            return path;
        }

        /// <summary>
        /// Returns a raw installed-client catalogue pinned by both the extraction manifest and
        /// the server-manifest-protected output integrity manifest.
        /// </summary>
        public static string Catalog(string filename)
        {
            ValidateSafeRelative(filename, "client catalogue");
            ValidateExtractedCatalogManifest();
            string relative = "catalogs/" + filename.Replace('\\', '/');
            if (!_catalogOutputs!.Contains(relative))
                throw new InvalidDataException($"The active extraction manifest does not list {relative}.");
            string path = Path.GetFullPath(Path.Combine(CatalogsDir, filename));
            string root = Path.GetFullPath(CatalogsDir) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException($"Required client catalogue is missing: {relative}", path);
            VerifyCatalogIntegrity(relative, path);
            return path;
        }

        private static HashSet<string>? _catalogOutputs;
        private static Dictionary<string, (long Bytes, string Hash)>? _catalogIntegrity;
        private static readonly HashSet<string> _verifiedCatalogs = new(StringComparer.OrdinalIgnoreCase);
        private static string? _staticContentFingerprint;
        private static readonly string[] RequiredGameplayCatalogs =
        {
            "areasdataroot.json", "subareasdataroot.json", "mapsinformationdataroot.json",
            "mapscoordinatesdataroot.json", "monstersdataroot.json", "spellsdataroot.json",
            "spelllevelsdataroot.json", "effectsdataroot.json", "itemsdataroot.json", "itemtypesdataroot.json",
            "dungeonsdataroot.json", "npcsdataroot.json", "npcmessagesdataroot.json",
            "questsdataroot.json", "queststepsdataroot.json", "questobjectivesdataroot.json",
            "queststeprewardsdataroot.json", "skillsdataroot.json", "recipesdataroot.json"
        };

        /// <summary>
        /// Fails startup when the static snapshot is incomplete, corrupt, or from another client
        /// version. Player/account databases are deliberately excluded: they are mutable state,
        /// whereas every file checked here is immutable game design input.
        /// </summary>
        public static void ValidateStaticContentOrThrow()
        {
            if (!IsServerClientDataValid())
                throw new InvalidDataException($"Server snapshot validation failed: {_serverClientDataError}");
            ValidateExtractedCatalogManifest();
            foreach (string catalog in RequiredGameplayCatalogs) _ = Catalog(catalog);
            foreach (string relative in _catalogOutputs!)
                _ = Catalog(relative["catalogs/".Length..]);
        }

        /// <summary>
        /// Stable identity of all immutable gameplay inputs. Generation timestamps are excluded;
        /// only manifest-listed content hashes participate.
        /// </summary>
        public static string StaticContentFingerprint
        {
            get
            {
                if (_staticContentFingerprint != null) return _staticContentFingerprint;
                ValidateStaticContentOrThrow();
                var canonical = new StringBuilder();
                foreach (var item in _catalogIntegrity!.OrderBy(x => x.Key, StringComparer.Ordinal))
                    canonical.Append("catalog:").Append(item.Key).Append(':').Append(item.Value.Hash).Append('\n');
                AppendManifestHashes(canonical, Path.Combine(ServerClientDataDir, "manifest.json"), "files", "path", "server");
                AppendManifestHashes(canonical, Path.Combine(MechanicsDir, "manifest.json"), "entries", "file", "mechanic");
                _staticContentFingerprint = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
                return _staticContentFingerprint;
            }
        }

        private static void ValidateExtractedCatalogManifest()
        {
            if (_catalogOutputs != null) return;
            string manifestPath = Path.Combine(ClientDataVersionDir, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("The active client extraction has no manifest.json.", manifestPath);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("clientVersion", out JsonElement version) || version.GetString() != ActiveClientDataVersion)
                throw new InvalidDataException("The client extraction manifest version does not match the active snapshot.");
            if (!root.TryGetProperty("worldExtracted", out JsonElement worldExtracted) || worldExtracted.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("The active client snapshot has no complete world extraction.");
            if (!root.TryGetProperty("catalogs", out JsonElement catalogs) || catalogs.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The client extraction manifest has no catalog list.");

            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement catalog in catalogs.EnumerateArray())
            {
                if (!catalog.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("A client catalogue manifest entry has no output path.");
                string relative = output.GetString() ?? "";
                ValidateSafeRelative(relative, "catalogue manifest");
                if (!outputs.Add(relative)) throw new InvalidDataException($"Duplicate client catalogue output: {relative}");
                string file = Path.Combine(ClientDataVersionDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) throw new FileNotFoundException($"Manifest-listed client catalogue is missing: {relative}", file);
            }
            string integrityPath = ServerData("catalog_integrity.json");
            using JsonDocument integrityDocument = JsonDocument.Parse(File.ReadAllText(integrityPath));
            JsonElement integrityRoot = integrityDocument.RootElement;
            if (!integrityRoot.TryGetProperty("clientVersion", out JsonElement integrityVersion) ||
                integrityVersion.GetString() != ActiveClientDataVersion ||
                !integrityRoot.TryGetProperty("catalogs", out JsonElement integrityEntries) ||
                integrityEntries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The catalogue integrity manifest is incompatible.");

            var integrity = new Dictionary<string, (long Bytes, string Hash)>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement entry in integrityEntries.EnumerateArray())
            {
                if (!entry.TryGetProperty("path", out JsonElement pathElement) || pathElement.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("bytes", out JsonElement bytesElement) || !bytesElement.TryGetInt64(out long bytes) || bytes < 0 ||
                    !entry.TryGetProperty("sha256", out JsonElement hashElement) || hashElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("A catalogue integrity entry is incomplete.");
                string relative = pathElement.GetString() ?? "";
                ValidateSafeRelative(relative, "catalogue integrity manifest");
                if (!outputs.Contains(relative))
                    throw new InvalidDataException($"Integrity manifest lists an unknown catalogue: {relative}");
                if (!integrity.TryAdd(relative, (bytes, hashElement.GetString() ?? "")))
                    throw new InvalidDataException($"Duplicate catalogue integrity entry: {relative}");
            }
            if (integrity.Count != outputs.Count)
                throw new InvalidDataException("The catalogue integrity manifest does not cover the full extraction.");
            _catalogOutputs = outputs;
            _catalogIntegrity = integrity;
        }

        private static void VerifyCatalogIntegrity(string relative, string path)
        {
            if (_verifiedCatalogs.Contains(relative)) return;
            if (_catalogIntegrity == null || !_catalogIntegrity.TryGetValue(relative, out var expected))
                throw new InvalidDataException($"No integrity record exists for {relative}.");
            var info = new FileInfo(path);
            if (info.Length != expected.Bytes)
                throw new InvalidDataException($"Catalogue size mismatch for {relative}: expected {expected.Bytes}, got {info.Length}.");
            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expected.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Catalogue SHA-256 mismatch for {relative}.");
            _verifiedCatalogs.Add(relative);
        }

        private static void AppendManifestHashes(StringBuilder canonical, string path,
            string entriesName, string pathName, string prefix)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(entriesName, out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{path} has no {entriesName} array.");
            var values = new List<(string Name, string Hash)>();
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty(pathName, out JsonElement name) || name.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("sha256", out JsonElement hash) || hash.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"{path} contains an incomplete hash entry.");
                values.Add((name.GetString() ?? "", hash.GetString() ?? ""));
            }
            foreach (var value in values.OrderBy(x => x.Name, StringComparer.Ordinal))
                canonical.Append(prefix).Append(':').Append(value.Name).Append(':').Append(value.Hash).Append('\n');
        }

        private static void ValidateSafeRelative(string relative, string kind)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Split('/', '\\').Any(part => part == ".."))
                throw new InvalidDataException($"Unsafe {kind} path: {relative}");
        }

        /// <summary>Only a complete manifest matching this executable's pinned client version may override datos/.</summary>
        private static bool IsServerClientDataValid()
        {
            if (_serverClientDataValid.HasValue) return _serverClientDataValid.Value;
            try
            {
                string manifestPath = Path.Combine(ServerClientDataDir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    _serverClientDataError = $"missing {manifestPath}";
                    return (_serverClientDataValid = false).Value;
                }
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!document.RootElement.TryGetProperty("clientVersion", out JsonElement version) ||
                    version.GetString() != ActiveClientDataVersion ||
                    !document.RootElement.TryGetProperty("serverProtocolVersion", out JsonElement protocolVersion) ||
                    protocolVersion.GetString() != ProtocolVersion ||
                    !document.RootElement.TryGetProperty("files", out JsonElement files) ||
                    files.ValueKind != JsonValueKind.Array)
                {
                    _serverClientDataError = "manifest version/protocol/files fields are invalid";
                    return (_serverClientDataValid = false).Value;
                }
                foreach (JsonElement file in files.EnumerateArray())
                {
                    if (!file.TryGetProperty("path", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
                        !file.TryGetProperty("bytes", out JsonElement bytes) || !bytes.TryGetInt64(out long expectedBytes) ||
                        !file.TryGetProperty("sha256", out JsonElement hash) || hash.ValueKind != JsonValueKind.String)
                    {
                        _serverClientDataError = "a manifest file entry is missing path, bytes, or sha256";
                        return (_serverClientDataValid = false).Value;
                    }
                    string relative = name.GetString() ?? "";
                    ValidateSafeRelative(relative, "server manifest");
                    string path = Path.Combine(ServerClientDataDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path))
                    {
                        _serverClientDataError = $"manifest-listed file is missing: {relative}";
                        return (_serverClientDataValid = false).Value;
                    }
                    var info = new FileInfo(path);
                    if (info.Length != expectedBytes)
                    {
                        _serverClientDataError = $"size mismatch for {relative}: expected {expectedBytes}, got {info.Length}";
                        return (_serverClientDataValid = false).Value;
                    }
                    using var stream = File.OpenRead(path);
                    string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    if (!string.Equals(actualHash, hash.GetString(), StringComparison.OrdinalIgnoreCase))
                    {
                        _serverClientDataError = $"SHA-256 mismatch for {relative}";
                        return (_serverClientDataValid = false).Value;
                    }
                }
                _serverClientDataError = "validated";
                return (_serverClientDataValid = true).Value;
            }
            catch (Exception ex)
            {
                _serverClientDataError = ex.Message;
                return (_serverClientDataValid = false).Value;
            }
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
            Console.WriteLine($"[Paths] raw catalogues    : {CatalogsDir}");
            Console.WriteLine($"[Paths] client data       : {ClientDataVersionDir}");
            Console.WriteLine($"[Paths] active data ver.  : {ActiveClientDataVersion}");
            Console.WriteLine($"[Paths] protocol version  : {ProtocolVersion}");
            Console.WriteLine($"[Paths] packet policy     : {ProtocolPacketPolicyJson}");
            Console.WriteLine($"[Paths] server snapshot   : {(UsingServerClientData ? "validated (strict)" : "invalid; startup will stop")}");
            if (UsingServerClientData)
                Console.WriteLine($"[Paths] snapshot manifest : {Path.Combine(ServerClientDataDir, "manifest.json")}");
            Console.WriteLine($"[Paths] mechanics         : {MechanicsDir}");
            Console.WriteLine($"[Paths] walkable cells    : {WalkableCellsJson}");
            Console.WriteLine($"[Paths] client            : {ClientDir}");
        }
    }
}
