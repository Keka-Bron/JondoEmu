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

        /// <summary>
        /// What the authored file has to say so that the world comes out the way somebody wants it.
        /// </summary>
        /// <remarks>
        /// This is the rule that keeps the authored layer a set of deltas instead of a copy, and it
        /// is a rule rather than a convention because getting it wrong is invisible. An editor that
        /// wrote out every placement it was showing would produce a file with 422 rows in it, and
        /// from that moment the measured file would never reach the world again: re-running
        /// <c>tools/extraer_npcs_reales.py</c> would fix a cell, and the fix would be shadowed by a
        /// copy nobody remembered making.
        ///
        /// So only three things are written: rows that are not in the measured layer at all, rows
        /// that are there but different, and tombstones for rows that were there and are not
        /// wanted. Everything the two agree on is left to the measured file, where it belongs.
        /// </remarks>
        public static (List<NpcSpawn> Rows, List<NpcSpawnKey> Removed) Delta(
            IReadOnlyDictionary<NpcSpawnKey, NpcSpawn> measured,
            IReadOnlyDictionary<NpcSpawnKey, NpcSpawn> wanted)
        {
            var rows = new List<NpcSpawn>();
            var removed = new List<NpcSpawnKey>();

            foreach (var pair in wanted)
            {
                if (measured.TryGetValue(pair.Key, out var already) &&
                    already.Orientation == pair.Value.Orientation)
                {
                    continue;
                }

                rows.Add(pair.Value);
            }

            foreach (var pair in measured)
            {
                if (!wanted.ContainsKey(pair.Key)) removed.Add(pair.Key);
            }

            return (rows, removed);
        }

        /// <summary>
        /// Writes the authored file: the rows a person added or changed, and the tombstones.
        /// </summary>
        /// <remarks>
        /// Fixed order and a temporary file, for the same two reasons as every other authored file:
        /// a one-line change should give a one-line diff, and closing the editor mid-write must not
        /// leave half a JSON file where the server will look for one.
        /// </remarks>
        public static void Save(string path, IEnumerable<NpcSpawn> rows, IEnumerable<NpcSpawnKey> removed,
                                IEnumerable<string>? comment = null)
        {
            var ordered = new List<NpcSpawn>(rows);
            ordered.Sort((a, b) =>
            {
                int byMap = a.MapId.CompareTo(b.MapId);
                if (byMap != 0) return byMap;
                int byCell = a.Cell.CompareTo(b.Cell);
                return byCell != 0 ? byCell : a.NpcId.CompareTo(b.NpcId);
            });

            var tombstones = new List<NpcSpawnKey>(removed);
            tombstones.Sort((a, b) =>
            {
                int byMap = a.MapId.CompareTo(b.MapId);
                if (byMap != 0) return byMap;
                int byCell = a.Cell.CompareTo(b.Cell);
                return byCell != 0 ? byCell : a.NpcId.CompareTo(b.NpcId);
            });

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("_comment");
                writer.WriteStartArray();
                foreach (string line in comment ?? DefaultComment) writer.WriteStringValue(line);
                writer.WriteEndArray();

                writer.WritePropertyName("spawns");
                writer.WriteStartArray();

                foreach (var spawn in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("map", spawn.MapId);
                    writer.WriteNumber("npc", spawn.NpcId);
                    writer.WriteNumber("cell", spawn.Cell);
                    writer.WriteNumber("orientation", spawn.Orientation);
                    writer.WriteEndObject();
                }

                foreach (var key in tombstones)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("map", key.MapId);
                    writer.WriteNumber("npc", key.NpcId);
                    writer.WriteNumber("cell", key.Cell);
                    writer.WriteBoolean("remove", true);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            string temporary = path + ".writing";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }

        private static readonly string[] DefaultComment =
        {
            "The authored layer for NPC placements. Nothing regenerates this file: it is the only",
            "one a person edits by hand, and it wins over datos/npcs_reales.json, which a tool",
            "rewrites.",
            "",
            "One row per placement: map, npc AND cell. The cell identifies it because the same NPC",
            "can stand several times on one map - 18 of them do. Moving one is a remove plus a new",
            "row.",
            "",
            "  { \"map\": 241438721, \"npc\": 1088, \"cell\": 260, \"orientation\": 3 }   place or re-face",
            "  { \"map\": 241438721, \"npc\": 1088, \"cell\": 260, \"remove\": true }    take that one out",
            "",
            "Only what differs from the measured file is here. A copy of everything would mean the",
            "next regeneration never reached the world again, and nobody would notice.",
        };

        private static long Long(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
