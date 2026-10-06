using System;
using System.IO;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Centralized resolution of the emulator's paths.
    ///
    /// Every file used to carry absolute paths to C:\Jondo scattered all over the code (28 of
    /// them in total). Now everything the emulator needs lives inside the emulator folder and the
    /// root is derived at run time from the assembly directory.
    ///
    /// The root keeps no loose files: whoever downloads the emulator has to see the .exe and little
    /// else, without doubting what to open. The data goes in <c>datos\</c> and the databases in <c>bases\</c>.
    /// The search looks in those folders first and the root afterwards, so a half-moved
    /// installation still starts the same; and if it is not there either, it falls back to the historical path.
    /// </summary>
    public static class Paths
    {
        /// <summary>Historical location of the data, used as a fallback.</summary>
        public const string LegacyRoot = @"C:\Jondo 3.6.10.10";

        /// <summary>Where each file is looked for, in this order.</summary>
        private static readonly string[] SubFolders = { "datos", "bases", "" };

        /// <summary>
        /// Data root of the emulator: the directory where the running assembly lives.
        /// With the current deployment that is the "Jondo Unity Emulator" folder.
        /// </summary>
        public static string Root { get; } = ResolveRoot();

        /// <summary>
        /// The client versions this emulator speaks to, FROM THE NEWEST TO THE
        /// OLDEST. The first one that exists is taken.
        ///
        /// It is a hand-written list on purpose, and not «the highest version folder
        /// there is»: the emulator is tied to a specific shape of the protocol, and if someone leaves
        /// alongside a client we do not yet know how to speak to, taking it on its own would be starting against an
        /// incompatible client without a word. Adding a version is one line.
        ///
        /// 3.6.10.11 is fine because it IS NOT ANOTHER PROTOCOL: its GameAssembly.dll and its
        /// global-metadata.dat are byte for byte the same as 3.6.10.10's —same SHA-256,
        /// both files— and the structural matcher confirms it on its own: 2,169
        /// messages against 2,169, identity mapping, zero doubts. That patch only moved data: 182
        /// bundles of Content/Data.
        /// </summary>
        private static readonly string[] ClientesQueValen =
        {
            "Cliente 3.6.10.11",
            "Cliente 3.6.10.10",
        };

        /// <summary>Dofus client folder (it lives outside the emulator root).</summary>
        public static string ClientDir
        {
            get
            {
                // Next to the emulator first, and the historical path after: a half-moved
                // installation still starts the same.
                foreach (string donde in new[]
                         {
                             Path.GetFullPath(Path.Combine(Root, "..")),
                             LegacyRoot,
                         })
                {
                    foreach (string version in ClientesQueValen)
                    {
                        string candidato = Path.Combine(donde, version);

                        // That the folder exists is not enough: it has to carry the executable inside.
                        // A half-downloaded folder exists and is of no use.
                        if (File.Exists(Path.Combine(candidato, "Dofus.exe"))) return candidato;
                    }
                }

                return Path.Combine(@"C:\Jondo", "DofusClient");
            }
        }

        /// <summary>
        /// The client's content folder: the texts, the icons, the maps.
        ///
        /// Everything the editor needs to show something presentable is already in there, and
        /// it is read from there instead of being copied: the client already takes up what it takes up on disk, and a
        /// copy of ours would go stale the day of the next patch without anyone noticing.
        /// </summary>
        public static string ClientContentDir
            => Path.Combine(ClientDir, "Dofus_Data", "StreamingAssets", "Content");

        /// <summary>
        /// The client's text table in one language, with the game's 339,342 texts inside.
        ///
        /// They are the names of the NPCs, the monsters, the items and the spells, and the
        /// dialogues' sentences. The language goes in the file name: es, en, fr, de, pt.
        /// </summary>
        public static string ClientTextFile(string language)
            => Path.Combine(ClientContentDir, "I18n", language + ".bin");

        /// <summary>The monsters' icons, 64 px, inside a Unity bundle.</summary>
        public static string MonsterIconsBundle
            => Path.Combine(ClientContentDir, "Picto", "Monsters", "monster_assets_1x.bundle");

        // ─── Databases ──────────────────────────────────────────────────────────
        // The databases create themselves the first time, so they go through ResolveWritable: if they do not
        // exist yet, the path that comes out is that of bases\ and not that of the root.
        public static string WorldDb => ResolveWritable("world.db", DatabaseFolder);
        public static string AuthDb => ResolveWritable("auth.db", DatabaseFolder);
        public static string WorldZip => Resolve("world.zip");
        public static string DatabaseBackupsDir => Path.Combine(Root, DatabaseFolder, "backups");

        /// <summary>
        /// The diagnostics database where the packets we do not know how to handle are recorded.
        ///
        /// It goes apart from world.db and auth.db on purpose: it carries nothing needed to play,
        /// it can be deleted to start from scratch and it can be copied to another machine to look at it without
        /// taking anyone's characters along.
        /// </summary>
        public static string PacketTelemetryDb => ResolveWritable("paquetes.db", DatabaseFolder);

        public static string WorldConnectionString => "Data Source=" + WorldDb.Replace('\\', '/');
        public static string AuthConnectionString => "Data Source=" + AuthDb.Replace('\\', '/');
        public static string PacketTelemetryConnectionString
            => "Data Source=" + PacketTelemetryDb.Replace('\\', '/');

        // ─── Game data ──────────────────────────────────────────────────────────
        public static string DataDir => ResolveDir("dofus3_data");
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
        // Which field of the message each item effect's value goes in, learnt from the capture.
        public static string EffectFieldsJson => Resolve("item_effect_fields.json");
        // The dungeons: their rooms, their entrance and their exit.
        // Generated by extract_dungeons.py from the client bundles.
        public static string DungeonsJson => Resolve("dungeons.json");
        // The spell states that change the rules of a fight: invulnerable, cannot be moved,
        // incurable... Only the flagged ones, 103 of the client's 6,375.
        // Generated by extract_spell_states.py from the client bundles.
        public static string SpellStatesJson => Resolve("spell_states.json");
        // Which world graphic is a workshop station, with its type and its craft skills.
        // Generated by extract_workshops.py from the captures and the client bundles.
        public static string WorkshopsJson => Resolve("talleres_3.6.10.10.json");
        // What smithmagic needs of every effect: its weight (effectPowerRate), its category, whether
        // it rolls, and its opposite. Generated by extract_effect_weights.py from the client bundles.
        public static string EffectWeightsJson => Resolve("effect_weights.json");
        // What can be clicked on each map, with its cell and its drawing, and the zaaps with their
        // map and their subzone. extract_interactivos.py generates them from the client's bundles.
        /// <summary>Which map is on each side of each map. 17,353 rows.</summary>
        public static string MapNeighboursJson => Resolve("map_neighbours.json");

        public static string InteractiveElementsJson => Resolve("interactive_elements.json");

        /// <summary>
        /// The interactive type really seen for each drawing, measured from the
        /// captures: 415 gfx, of which 20 do not match what we declare.
        /// </summary>
        public static string InteractiveTypesJson => Resolve("tipos_interactivos_3.6.10.10.json");

        /// <summary>
        /// The passages between maps, normalised from Giny 2.68's interactive_skills table. The
        /// server validates them against its own data and only imports the ones that survive.
        /// </summary>
        public static string InteractiveTeleportsJson => Resolve("interactive_teleports_giny_2.68.json");

        /// <summary>
        /// The passages taken from the navigation graph of the Dofus 2.73 client, generated by
        /// tools/extraer_world_graph.py. They fill in the gaps Giny's catalogue does not cover.
        ///
        /// They go in a separate file and NOT mixed with Giny's on purpose: these bring an
        /// approximate arrival cell —the graph does not carry it— and that has to be distinguishable
        /// at a glance, both here and in the base's Confidence column.
        /// </summary>
        public static string WorldGraphTeleportsJson
            => Resolve("interactive_teleports_worldgraph_2.73.json");
        /// <summary>
        /// The raw catalogues of professions, skills and recipes. They are left out of
        /// <c>datos</c> on purpose: the server imports them into world.db and never serves them.
        ///
        /// <c>dofus3_data</c> is searched BEFORE <c>JsonFromDofusDude</c>, which is where whoever
        /// wrote this put them. The three files come from the client dump and already lived in
        /// dofus3_data from before, so looking only in the new folder they were not found and the
        /// import skipped itself without anyone noticing: the tables were left created and
        /// empty. The two earlier folders are still looked in, because whoever has them there need not
        /// move them.
        ///
        /// The check is per FILE and not per folder: <c>dofus3_data</c> exists in any
        /// installation, so asking whether the folder exists would always have chosen it, whether it had
        /// the catalogues inside or not.
        /// </summary>
        public static string DofusDudeJsonDir
        {
            get
            {
                foreach (string candidate in new[]
                {
                    DataDir,
                    Path.Combine(Root, "JsonFromDofusDude"),
                    Path.GetFullPath(Path.Combine(Root, "..", "JsonFromDofusDude")),
                })
                {
                    if (File.Exists(Path.Combine(candidate, "jobs.json"))) return candidate;
                }

                return Path.Combine(Root, "JsonFromDofusDude");
            }
        }
        public static string JobsJson => Path.Combine(DofusDudeJsonDir, "jobs.json");
        public static string SkillsJson => Path.Combine(DofusDudeJsonDir, "skills.json");
        public static string RecipesJson => Path.Combine(DofusDudeJsonDir, "recipes.json");
        public static string WaypointsJson => Resolve("waypoints.json");
        public static string ZaapOverridesJson => Resolve("zaap_overrides.json");
        public static string HavenBagJson => Resolve("havenbag.json");
        public static string TitlesOrnamentsJson => Resolve("titles_ornaments.json");
        public static string CosmeticsJson => Resolve("cosmetics.json");
        public static string CosmeticSkinsJson => Resolve("cosmetic_skins.json");
        // The look of the REAL EQUIPMENT (not the appearance garments), measured on the
        // tournament server's captures with tools/extraer_equipo_real.py.
        public static string EquipmentSkinsJson => Resolve("equipment_skins.json");

        // The koliseo arenas with their placement cells per side, taken from the client
        // with tools/extraer_mapas_koliseo.py. The three koliseo subareas and nothing else.
        public static string KoliseoMapsJson => Resolve("koliseo_mapas.json");
        // Each mount's look, indexed by the item that gives it. Generated by
        // extract_monturas.py from the client's bundles.
        public static string MountsJson => Resolve("mounts.json");
        // What each shop NPC sells and at what price, measured from the tournament server with
        // tools/extraer_tiendas.py.
        public static string NpcShopsJson => Resolve("npc_shops.json");

        /// <summary>
        /// The shops that charge in tokens instead of kamas. By hand, not generated: what a token shop
        /// sells and for how much is Jondo's content and does not come from any capture.
        /// </summary>
        public static string TokenShopsJson => Resolve("tiendas_en_fichas.json");

        /// <summary>
        /// Which sellers Jondo merges into one and what they are called. Also by hand, and the
        /// client mod also reads it so that the name and the catalogue do not drift apart.
        /// </summary>
        public static string JondoVendorsJson => Resolve("vendedores_jondo.json");


        /// <summary>
        /// Where each NPC of the world is, taken from the captures of Ankama's server: 422 on
        /// 202 maps. Generated by tools/extraer_npcs_reales.py.
        /// </summary>
        public static string WorldNpcsJson => Resolve("npcs_reales.json");

        /// <summary>
        /// NPC placements worked out from the quest catalogue, for the NPCs no capture covers.
        /// </summary>
        /// <remarks>
        /// The captures know where 327 NPCs stand, out of 6,468 templates. The client's own quest
        /// data names 2,098 of them on a map — whoever hands a quest out, and whoever an objective
        /// sends you to — and that is where these come from: 1,692 with a map id, plus 317 more
        /// resolved from a coordinate hint. The map is the client's; <b>the cell is a guess</b>,
        /// the walkable cell nearest the middle of the map, because the cell only ever appears in
        /// the packet that spawns the actor.
        ///
        /// That is why it loads as the Base layer and not as Measured: a captured placement beats
        /// it outright, and dragging one in the Studio beats them both. Generated by
        /// tools/derive_npc_spawns.py.
        /// </remarks>
        public static string WorldNpcsDerivedJson => Resolve("npc_spawns_derived.json");

        /// <summary>
        /// The quest catalogue: 1,976 quests, 2,225 steps, 15,547 objectives, 6,707 rewards.
        /// </summary>
        /// <remarks>
        /// Flattened by tools/extract_quests.py out of the six files the client keeps quests in,
        /// which are 11.4 MB of Unity serialisation scaffolding in a folder the repository does
        /// not carry. Text is left as translation keys so the editor can show it in any of the
        /// three languages.
        /// </remarks>
        public static string QuestsJson => Resolve("quests_3.6.10.10.json");

        /// <summary>
        /// The achievement catalogue: 2,780 of them, 8,946 objectives, 6,394 rewards.
        /// </summary>
        /// <remarks>
        /// Flattened by tools/extract_achievements.py from four files the client keeps them in.
        /// 259 of the achievements are earned by finishing quests, which is what ties this to the
        /// quest engine; the rest are exploring, fighting and job levels, and this emulator can
        /// only judge some of those.
        /// </remarks>
        public static string AchievementsJson => Resolve("achievements_3.6.10.10.json");

        /// <summary>
        /// The 272 achievement objectives the client names but does not describe, tied to what
        /// finishes them: a subarea to enter, a level, a job level, a quest, a monster.
        /// </summary>
        /// <remarks>
        /// Generated by tools/extract_achievement_links.py from each achievement's own name and
        /// description. The exploration ones are measured: the 17 the captures earn all fire on
        /// entering the subarea the achievement is named after.
        /// </remarks>
        public static string AchievementLinksJson => Resolve("achievement_links_3.6.10.10.json");

        /// <summary>The 324 emotes: animation, cooldown, mount rule and criterion.</summary>
        /// <remarks>Generated by tools/extract_emotes.py from the client's emoticon table.</remarks>
        public static string EmotesJson => Resolve("emotes_3.6.10.10.json");

        /// <summary>The Almanax calendar: 376 days, each with its saint, its offering quest and its bonuses.</summary>
        /// <remarks>Generated by tools/extract_almanax.py from the client's calendar table.</remarks>
        public static string AlmanaxJson => Resolve("almanax_3.6.10.10.json");

        /// <summary>
        /// The authored content layer: the only files a person edits by hand.
        ///
        /// It sits next to the emulator and it is versioned in git, unlike <c>datos/</c> and
        /// <c>bases/</c>, which are generated. That is the whole point of it being text and being
        /// small: a diff anybody can review, and two people can edit different maps without
        /// colliding. Nothing regenerates this folder, so nothing can quietly wipe it.
        /// </summary>
        public static string ContentDir => Path.Combine(Root, "content");

        /// <summary>
        /// The missing hop to read an NPC dialogue: message id -> translation key.
        ///
        /// An NPC's template brings in dialogData a messageId that is NOT a translation, but an
        /// NpcMessageData id; one has to go through NpcMessagesDataRoot, which is 16.8 MB of the client
        /// dump, to get to the real key. This file is that hop distilled into one
        /// number per entry. Generated by tools/extraer_dialogos_npc.py.
        ///
        /// The replies do not need it: dialogReplies already brings the key next to the id.
        /// </summary>
        public static string NpcDialoguesJson => Resolve("npc_dialogos_3.6.10.10.json");

        /// <summary>
        /// The 513 message names the client still carries inside.
        ///
        /// The obfuscator renames the classes to three letters but leaves these strings in
        /// global-metadata.dat. They are orphaned -nobody references them- but they are the CLOSED list of
        /// valid names of this version, so naming an opcode stops being inventing and becomes
        /// choosing from a list.
        /// </summary>
        public static string RealNamesTsv => Resolve("nombres_reales_3.6.10.10.tsv");

        /// <summary>One file of the authored layer, by its path relative to the content root.</summary>
        public static string ContentFile(string relative)
            => Path.Combine(ContentDir, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// The whole protocol as the client declares it, rebuilt from its own classes by
        /// <c>protocolbuilder</c>: 2,169 messages and 550 enums with real field numbers and types.
        ///
        /// The version is in the filename, so this looks for the one that matches and then falls
        /// back to whichever game protocol is there. The fallback is on purpose: this file only
        /// ever feeds the frame view in the editor, and reading a frame against a protocol one
        /// patch out of date is worth far more than reading it against nothing. The connection
        /// protocol is a different, much smaller file and is deliberately not picked up here.
        /// </summary>
        public static string ProtocolProto
        {
            get
            {
                string preferred = Resolve("protocolo_3.6.10.10.proto");
                if (File.Exists(preferred)) return preferred;

                try
                {
                    string folder = Path.GetDirectoryName(preferred) ?? Root;
                    var candidates = Directory.GetFiles(folder, "protocolo_*.proto");
                    Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
                    for (int i = candidates.Length - 1; i >= 0; i--)
                    {
                        string name = Path.GetFileName(candidates[i]);
                        if (!name.StartsWith("protocolo_conexion", StringComparison.OrdinalIgnoreCase))
                        {
                            return candidates[i];
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                return preferred;
            }
        }
        // The base/variant spell pairs, one for each bar slot. From the client
        // dump; the character carries one of each pair, not both.
        public static string SpellVariantsJson
        {
            get
            {
                string inData = Path.Combine(DataDir, "spell_variants.json");
                return File.Exists(inData) ? inData : Resolve("spell_variants.json");
            }
        }

        // The 120 characteristics with the slot each one gets inside the kub, taken
        // from the 672 real kub of the captures by tools/extraer_caracteristicas_kub.py. It
        // was opened by relative path, and without it the sheet drops from 120 entries to 25 without a word.
        public static string CharacteristicFieldsJson => Resolve("caracteristicas_kub.json");

        /// <summary>
        /// What each bomb does: its explosion, what it casts at the target and its chain. It comes from the
        /// client with tools/extract_bomb_spells.py, from the SpellBombData class.
        /// </summary>
        public static string BombsJson => Resolve("bombas.json");

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
        public static string ActivityLog => Path.Combine(LogsDir, "activity.jsonl");

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
        /// Looks for a file in datos\, in bases\ and in the root; if it is in none, in the historical
        /// path. When it does not show up anywhere it returns the <c>datos\</c> path, which is
        /// where it must be created.
        /// </summary>
        public static string Resolve(string filename)
        {
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

        /// <summary>Data folder. Whatever does not exist will be created here.</summary>
        public const string DataFolder = "datos";

        /// <summary>Folder of the databases, which are the ones the emulator writes.</summary>
        public const string DatabaseFolder = "bases";

        /// <summary>
        /// Like Resolve but for files the emulator CREATES: if it does not exist yet, the
        /// path it returns is that of the folder it belongs in, not that of the root.
        /// </summary>
        /// <summary>
        /// A database: it is looked for FIRST in its folder, bases\, and only then in the rest.
        ///
        /// Before, it used the same order as the data —datos\ first— and that cost an afternoon. If for
        /// whatever reason a world.db shows up in datos\, the server starts writing there and stops
        /// seeing the one in bases\: the character comes out where you did not leave it, the professions go back to level 1,
        /// and there is no code bug to find because each half is consistent with
        /// itself. A database is not game data: datos\ is read-only and comes from the client,
        /// bases\ is what the server writes.
        ///
        /// If a loose copy is left somewhere else a warning goes to the console, because it is exactly the
        /// kind of thing that is not noticed until a game has been lost.
        /// </summary>
        private static string ResolveWritable(string filename, string folder)
        {
            string propia = Combine(Root, Path.Combine(folder, filename));
            if (File.Exists(propia))
            {
                AvisarDeCopias(filename, propia);
                return propia;
            }

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

        /// <summary>Warns if the same database exists in two places. See ResolveWritable.</summary>
        private static void AvisarDeCopias(string filename, string usada)
        {
            foreach (string sub in SubFolders)
            {
                string otra = Combine(Root, Path.Combine(sub, filename));
                if (!File.Exists(otra)) continue;
                if (string.Equals(otra, usada, StringComparison.OrdinalIgnoreCase)) continue;

                Console.WriteLine($"[Paths] OJO: hay otro {filename} en {otra}. Se usa {usada} " +
                                  "y ese otro se queda sin tocar; borra el que sobre.");
            }
        }

        private static string Combine(string a, string b) => Path.Combine(a, b);

        public static void LogResolvedPaths()
        {
            Console.WriteLine($"[Paths] emulator root     : {Root}");
            Console.WriteLine($"[Paths] world.db          : {WorldDb}");
            Console.WriteLine($"[Paths] auth.db           : {AuthDb}");
            Console.WriteLine($"[Paths] dofus3_data       : {DataDir}");
            Console.WriteLine($"[Paths] walkable cells    : {WalkableCellsJson}");
            Console.WriteLine($"[Paths] client            : {ClientDir}");
        }
    }
}
