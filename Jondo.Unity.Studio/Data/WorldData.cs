using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol.Wire;
using Jondo.Unity.World.Client;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>What one map's cells are: which can be stood on, which can be seen through.</summary>
    public sealed class MapCells
    {
        /// <summary>Cells anybody can stand on outside a fight.</summary>
        public HashSet<int> Walkable { get; } = new HashSet<int>();

        /// <summary>Cells that can be stood on during a fight. Not the same list.</summary>
        public HashSet<int> WalkableInFight { get; } = new HashSet<int>();

        /// <summary>Cells a spell cannot be traced through.</summary>
        public HashSet<int> SightBlockers { get; } = new HashSet<int>();
    }

    /// <summary>
    /// Everything the editor reads off disk, loaded once.
    /// </summary>
    /// <remarks>
    /// The editor reads the same files the server does, through the same Paths, and never asks a
    /// running server for anything: that is what lets it work with nothing else started. It does
    /// NOT go through MapManager or any of the server's managers — those live in the server
    /// assembly and carry its runtime state, and depending on them would drag the whole thing in.
    /// </remarks>
    public sealed class WorldData
    {
        private readonly Dictionary<long, MapCells> _maps = new Dictionary<long, MapCells>();

        /// <summary>NPC placements, merged across the content layers, with their provenance.</summary>
        public ContentStore<NpcSpawnKey, NpcSpawn> NpcPlacements { get; private set; }
            = new ContentStore<NpcSpawnKey, NpcSpawn>();

        /// <summary>What a person worked out about the traffic. The authored layer for packets.</summary>
        public ContentStore<PacketShapeKey, PacketNote> PacketNotes { get; private set; }
            = new ContentStore<PacketShapeKey, PacketNote>();

        /// <summary>
        /// Which reply leads to which line. Authored, and there is no layer beneath it: the client
        /// has never held that pairing, so there is nothing to measure or generate underneath.
        /// </summary>
        public ContentStore<NpcDialogueKey, NpcDialogue> NpcDialogues { get; private set; }
            = new ContentStore<NpcDialogueKey, NpcDialogue>();

        /// <summary>
        /// The protocol as the client declares it: 2,169 messages with real field numbers and types.
        /// </summary>
        /// <remarks>
        /// Loaded once and shared, because both the traffic view and the packet list read frames
        /// through it, and parsing 17,000 lines every time somebody clicks a section would be felt.
        /// It is allowed to be missing: a frame view with field numbers instead of names is still
        /// worth having on a machine that has never run the extraction tools.
        /// </remarks>
        public ProtoSchema Protocol { get; private set; } = ProtoSchema.Empty;

        /// <summary>
        /// The game's own words, in the language in use: names of NPCs, monsters and items, and
        /// every line of dialogue.
        /// </summary>
        /// <remarks>
        /// Read straight out of the client, which already ships five languages of it. Null when
        /// there is no client next to the emulator, and everything that uses it falls back to ids —
        /// worse to look at, but the editor still opens, which is the point.
        /// </remarks>
        public ClientText? Text { get; private set; }

        /// <summary>Which language the game's words are being read in.</summary>
        public GameLanguage Language { get; private set; } = GameLanguage.Spanish;

        /// <summary>
        /// Re-reads the game's words in another language.
        /// </summary>
        /// <remarks>
        /// The catalogues that resolved names against the old table are dropped rather than
        /// translated: they are rebuilt from the database on demand, and a half-translated list is
        /// worse than a slow one.
        /// </remarks>
        public void UseLanguage(GameLanguage language)
        {
            if (Language == language && Text != null) return;

            Language = language;
            Text = ClientText.Open(Paths.ClientTextFile(ClientText.TagOf(language)), language, Complaints.Add);
        }

        /// <summary>One of the game's words, or the id when there is no table to look it up in.</summary>
        public string Say(long key)
        {
            string text = Text?.Of(key) ?? "";
            return text.Length > 0 ? text : "";
        }

        /// <summary>What went wrong while loading, if anything. Shown rather than swallowed.</summary>
        public List<string> Complaints { get; } = new List<string>();

        public IReadOnlyDictionary<long, MapCells> Maps => _maps;

        public int MapCount => _maps.Count;

        public static WorldData Load()
        {
            var data = new WorldData();

            // Antes que nada: sin world.db no hay mapas, ni NPCs, ni monstruos, ni hechizos, y
            // quien acaba de clonar el repositorio solo tiene datos/world.zip.
            WorldArchive.Ensure(data.Complaints.Add);

            data.LoadNpcPlacements();
            data.LoadCells();
            data.LoadProtocol();
            data.ReloadPassages();
            data.ReloadCellPatches();
            data.UseLanguage(Ui.Words.Current);
            return data;
        }

        private void LoadNpcPlacements()
        {
            NpcPlacements = NpcSpawnContent.Load(
                Paths.WorldNpcsJson,
                Paths.ContentFile(NpcSpawnContent.AuthoredFile),
                Complaints.Add);
        }

        private void LoadProtocol()
        {
            Protocol = ProtoSchema.Load(Paths.ProtocolProto, Complaints.Add);
            PacketNotes = PacketShapeContent.Load(
                Paths.ContentFile(PacketShapeContent.AuthoredFile),
                Complaints.Add);

            NpcDialogues = NpcDialogueContent.Load(
                Paths.ContentFile(NpcDialogueContent.AuthoredFile),
                Complaints.Add);
        }

        /// <summary>Re-reads the authored packet notes after the editor has written them.</summary>
        public void ReloadPacketNotes()
            => PacketNotes = PacketShapeContent.Load(
                Paths.ContentFile(PacketShapeContent.AuthoredFile), Complaints.Add);

        /// <summary>
        /// How many NPCs stand on each map, out of the merged content layers.
        /// </summary>
        /// <remarks>
        /// Not out of the NpcSpawns table in world.db, which holds two maps' worth: the world's 422
        /// placements are measured into datos/npcs_reales.json and decided in content/, and this
        /// store is where the two meet.
        /// </remarks>
        public IReadOnlyDictionary<long, int> NpcsPerMap()
        {
            var counts = new Dictionary<long, int>();
            foreach (var pair in NpcPlacements.Rows)
            {
                counts.TryGetValue(pair.Key.MapId, out int already);
                counts[pair.Key.MapId] = already + 1;
            }

            return counts;
        }

        /// <summary>
        /// The passages a person decided on, over and above the 3,815 that were extracted.
        /// </summary>
        public ContentStore<PassageKey, Passage> Passages { get; private set; }
            = new ContentStore<PassageKey, Passage>();

        /// <summary>The hand-made cell changes, laid over the generated ones.</summary>
        public ContentStore<CellKey, CellPatch> CellPatches { get; private set; }
            = new ContentStore<CellKey, CellPatch>();

        /// <summary>Re-reads the cell changes after the editor has written them.</summary>
        public void ReloadCellPatches()
        {
            CellPatches = CellContent.Load(Paths.ContentFile(CellContent.AuthoredFile), Complaints.Add);
        }

        /// <summary>Re-reads the passages after the editor has written them.</summary>
        public void ReloadPassages()
        {
            Passages = TeleportContent.Load(Paths.ContentFile(TeleportContent.AuthoredFile),
                                            Complaints.Add);
        }

        /// <summary>Re-reads the NPC placements after the editor has written them.</summary>
        public void ReloadNpcPlacements() => LoadNpcPlacements();

        /// <summary>Re-reads the authored dialogues after the editor has written them.</summary>
        public void ReloadNpcDialogues()
            => NpcDialogues = NpcDialogueContent.Load(
                Paths.ContentFile(NpcDialogueContent.AuthoredFile), Complaints.Add);

        private void LoadCells()
        {
            // Two files, because they say two different things and one is not derivable from the
            // other: a cell can be walkable outside a fight and blocked inside one, and a cell can
            // be seen through without being walkable at all.
            Read(Paths.WalkableCellsJson, (cells, element) =>
            {
                foreach (var cell in element.EnumerateArray())
                {
                    if (cell.TryGetInt32(out int number)) cells.Walkable.Add(number);
                }
            });

            Read(Paths.FightCellsJson, (cells, element) =>
            {
                if (element.ValueKind != JsonValueKind.Object) return;

                if (element.TryGetProperty("f", out var walkable))
                {
                    foreach (var cell in walkable.EnumerateArray())
                    {
                        if (cell.TryGetInt32(out int number)) cells.WalkableInFight.Add(number);
                    }
                }

                if (element.TryGetProperty("b", out var blockers))
                {
                    foreach (var cell in blockers.EnumerateArray())
                    {
                        if (cell.TryGetInt32(out int number)) cells.SightBlockers.Add(number);
                    }
                }
            });
        }

        private void Read(string path, Action<MapCells, JsonElement> fill)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Complaints.Add($"{Path.GetFileName(path)} is not there; those cells will be missing.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!long.TryParse(entry.Name, out long mapId)) continue;

                    if (!_maps.TryGetValue(mapId, out var cells))
                    {
                        cells = new MapCells();
                        _maps[mapId] = cells;
                    }

                    fill(cells, entry.Value);
                }
            }
            catch (Exception ex)
            {
                Complaints.Add($"{Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }
    }
}
