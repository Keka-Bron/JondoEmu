using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
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

        /// <summary>What went wrong while loading, if anything. Shown rather than swallowed.</summary>
        public List<string> Complaints { get; } = new List<string>();

        public IReadOnlyDictionary<long, MapCells> Maps => _maps;

        public int MapCount => _maps.Count;

        public static WorldData Load()
        {
            var data = new WorldData();
            data.LoadNpcPlacements();
            data.LoadCells();
            return data;
        }

        private void LoadNpcPlacements()
        {
            NpcPlacements = NpcSpawnContent.Load(
                Paths.WorldNpcsJson,
                Paths.ContentFile(NpcSpawnContent.AuthoredFile),
                Complaints.Add);
        }

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
