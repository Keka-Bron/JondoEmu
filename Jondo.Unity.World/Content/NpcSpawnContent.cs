using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.World.Content
{
    /// <summary>Where one NPC stands. The first domain to go through the content layers.</summary>
    public readonly struct NpcSpawn
    {
        public long MapId { get; init; }
        public int NpcId { get; init; }
        public int Cell { get; init; }
        public int Orientation { get; init; }

        public override string ToString() => $"npc {NpcId} on map {MapId}, cell {Cell}";
    }

    /// <summary>
    /// What identifies a placement: one NPC, on one map, on one cell.
    /// </summary>
    /// <remarks>
    /// The cell has to be in the key, and leaving it out was a real bug for about ten minutes: the
    /// same NPC can stand more than once on the same map. Measured over the 422 captured
    /// placements — 18 pairs of (map, npc) carry more than one row, worth 26 placements in total,
    /// and NPC 7629 stands three times on map 99090957, on cells 248, 263 and 277. Keyed by
    /// (map, npc) those 26 collapsed into their neighbours and vanished from the world; keyed with
    /// the cell there are exactly 422 distinct keys and no collisions at all.
    ///
    /// The cost is that moving an NPC is two authored rows — erase the old placement, add the new
    /// one — rather than one. That is the right trade: an ambiguous key silently loses data, and a
    /// wordier one only makes the editor write one more line.
    /// </remarks>
    public readonly struct NpcSpawnKey : IEquatable<NpcSpawnKey>
    {
        public NpcSpawnKey(long mapId, int npcId, int cell)
        {
            MapId = mapId;
            NpcId = npcId;
            Cell = cell;
        }

        public long MapId { get; }
        public int NpcId { get; }
        public int Cell { get; }

        public bool Equals(NpcSpawnKey other)
            => MapId == other.MapId && NpcId == other.NpcId && Cell == other.Cell;

        public override bool Equals(object? obj) => obj is NpcSpawnKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(MapId, NpcId, Cell);
        public override string ToString() => $"{MapId}/{NpcId}@{Cell}";
    }

    /// <summary>
    /// Reads NPC placements out of the layers and merges them.
    /// </summary>
    /// <remarks>
    /// Two files today:
    ///
    ///   measured   datos/npcs_reales.json     422 placements over 202 maps, read off the captures
    ///                                         by tools/extraer_npcs_reales.py. Regenerable.
    ///   authored   content/npcs/spawns.json   what a person decided. Never regenerated.
    ///
    /// The two do not share a spelling because they do not share a life: the measured file is
    /// written by a Python tool that has always used Spanish keys, and renaming them would break
    /// the tool for nothing. The authored file is new, so it is in English like the rest of the
    /// code from now on, and it is the only one anybody types into.
    /// </remarks>
    public static class NpcSpawnContent
    {
        /// <summary>The authored file, relative to the content root.</summary>
        public const string AuthoredFile = "npcs/spawns.json";

        /// <summary>Facing south-east, which is what a placement with no orientation gets.</summary>
        private const int DefaultOrientation = 1;

        public static ContentStore<NpcSpawnKey, NpcSpawn> Load(string? measuredPath, string? authoredPath,
                                                               Action<string>? report = null)
        {
            var store = new ContentStore<NpcSpawnKey, NpcSpawn>();

            // Lowest layer first only for readability: ContentStore does not care about the order,
            // precisely so that a change in startup order cannot silently undo an authored row.
            ReadMeasured(store, measuredPath, report);
            ReadAuthored(store, authoredPath, report);
            return store;
        }

        private static void ReadMeasured(ContentStore<NpcSpawnKey, NpcSpawn> store, string? path,
                                         Action<string>? report)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var from = Origin.Measured(Path.GetFileName(path));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("npcs", out var list)) return;

                foreach (var entry in list.EnumerateArray())
                {
                    long mapId = Long(entry, "mapa");
                    int npcId = (int)Long(entry, "npc");
                    int cell = (int)Long(entry, "casilla");
                    if (mapId == 0 || npcId == 0) continue;

                    store.Put(new NpcSpawnKey(mapId, npcId, cell), new NpcSpawn
                    {
                        MapId = mapId,
                        NpcId = npcId,
                        Cell = cell,
                        Orientation = entry.TryGetProperty("orientacion", out var o) && o.TryGetInt32(out int f)
                            ? f : DefaultOrientation,
                    }, from);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }

        private static void ReadAuthored(ContentStore<NpcSpawnKey, NpcSpawn> store, string? path,
                                         Action<string>? report)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var from = Origin.Authored(Path.GetFileName(path));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("spawns", out var list)) return;

                foreach (var entry in list.EnumerateArray())
                {
                    long mapId = Long(entry, "map");
                    int npcId = (int)Long(entry, "npc");
                    if (mapId == 0 || npcId == 0) continue;

                    if (!entry.TryGetProperty("cell", out var c) || !c.TryGetInt32(out int cell))
                    {
                        report?.Invoke($"[Content] npc {npcId} on map {mapId} has no cell; skipped. " +
                                       "The cell is part of what identifies a placement, because the " +
                                       "same NPC can stand several times on one map.");
                        continue;
                    }

                    var key = new NpcSpawnKey(mapId, npcId, cell);

                    // A tombstone: the row exists to say that Ankama's placement is not wanted. It
                    // has to be expressible without editing the generated file it came from,
                    // because that file is rewritten by a tool. Moving an NPC is one of these plus
                    // a plain row on the new cell.
                    if (entry.TryGetProperty("remove", out var gone) &&
                        gone.ValueKind == JsonValueKind.True)
                    {
                        store.Erase(key, from);
                        continue;
                    }

                    // Whatever the row leaves out is taken from the placement underneath, if there
                    // is one: a row that only turns an NPC around does not have to repeat the rest.
                    store.TryGet(key, out var below);
                    store.Put(key, new NpcSpawn
                    {
                        MapId = mapId,
                        NpcId = npcId,
                        Cell = cell,
                        Orientation = entry.TryGetProperty("orientation", out var o) && o.TryGetInt32(out int f)
                            ? f
                            : (below.Value.Orientation != 0 ? below.Value.Orientation : DefaultOrientation),
                    }, from);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }

        private static long Long(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
