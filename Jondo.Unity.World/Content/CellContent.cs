using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jondo.Unity.World.Content
{
    /// <summary>
    /// A change to one cell of one map. Anything left null is left alone.
    /// </summary>
    /// <remarks>
    /// Three layers, because the game really does keep three and none of them follows from the
    /// others: a cell can be walked on outside a fight and blocked inside one, and a cell can be
    /// seen through without being walkable at all.
    /// </remarks>
    public readonly struct CellPatch
    {
        public long MapId { get; init; }

        public int Cell { get; init; }

        /// <summary>Can be stood on outside a fight.</summary>
        public bool? Walkable { get; init; }

        /// <summary>Can be stood on during a fight. Not the same list.</summary>
        public bool? WalkableInFight { get; init; }

        /// <summary>Stops a spell being traced through.</summary>
        public bool? BlocksSight { get; init; }

        /// <summary>True when the patch says nothing at all, which should never be written.</summary>
        public bool Empty => Walkable == null && WalkableInFight == null && BlocksSight == null;

        public override string ToString() => $"{MapId}@{Cell}";
    }

    public readonly struct CellKey : IEquatable<CellKey>
    {
        public CellKey(long mapId, int cell)
        {
            MapId = mapId;
            Cell = cell;
        }

        public long MapId { get; }

        public int Cell { get; }

        public bool Equals(CellKey other) => MapId == other.MapId && Cell == other.Cell;
        public override bool Equals(object? obj) => obj is CellKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(MapId, Cell);
        public override string ToString() => $"{MapId}@{Cell}";
    }

    /// <summary>
    /// Hand-made changes to a map's cells.
    /// </summary>
    /// <remarks>
    /// <b>Deltas, never copies.</b> A map has 560 cells and this file holds the ones that were
    /// changed — three, usually. Writing all 560 would shadow the generated file for that map for
    /// ever, so the next time the extractor learned something about it nobody would ever see it,
    /// and nothing would say so. That is the failure this whole content layer exists to prevent,
    /// and cells are where it would be easiest to get wrong.
    ///
    /// The three generated files disagree on purpose and it is worth knowing which is which:
    /// <c>map_walkable_cells.json</c> <em>trims the map borders</em> so that monsters do not get
    /// placed in the outer ring during roleplay, while <c>map_fight_cells.json</c> keeps the whole
    /// map and is the one that carries the sight-blocking flag. So a cell missing from the first is
    /// not necessarily a wall.
    /// </remarks>
    public static class CellContent
    {
        /// <summary>The authored file, relative to the content root.</summary>
        public const string AuthoredFile = "maps/cells.json";

        public static ContentStore<CellKey, CellPatch> Load(string? authoredPath,
                                                            Action<string>? report = null)
        {
            var store = new ContentStore<CellKey, CellPatch>();
            if (string.IsNullOrEmpty(authoredPath) || !File.Exists(authoredPath)) return store;

            var from = Origin.Authored(Path.GetFileName(authoredPath));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authoredPath));
                if (!doc.RootElement.TryGetProperty("cells", out var list)) return store;

                foreach (var entry in list.EnumerateArray())
                {
                    long map = entry.TryGetProperty("map", out var m) && m.TryGetInt64(out long id) ? id : 0;
                    if (map == 0) continue;
                    if (!entry.TryGetProperty("cell", out var c) || !c.TryGetInt32(out int cell)) continue;

                    var key = new CellKey(map, cell);

                    if (entry.TryGetProperty("remove", out var gone) &&
                        gone.ValueKind == JsonValueKind.True)
                    {
                        store.Erase(key, from);
                        continue;
                    }

                    var patch = new CellPatch
                    {
                        MapId = map,
                        Cell = cell,
                        Walkable = Flag(entry, "walk"),
                        WalkableInFight = Flag(entry, "fight"),
                        BlocksSight = Flag(entry, "sight"),
                    };

                    // A row that says nothing is not a change; keeping it would only make the file
                    // grow every time somebody clicked a cell twice.
                    if (patch.Empty) continue;

                    store.Put(key, patch, from);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {Path.GetFileName(authoredPath)} is unreadable: {ex.Message}");
            }

            return store;
        }

        public static void Save(string path, IEnumerable<CellPatch> patches,
                                IEnumerable<string>? comment = null)
        {
            var ordered = new List<CellPatch>();
            foreach (var patch in patches)
            {
                if (!patch.Empty) ordered.Add(patch);
            }

            ordered.Sort((a, b) =>
            {
                int byMap = a.MapId.CompareTo(b.MapId);
                return byMap != 0 ? byMap : a.Cell.CompareTo(b.Cell);
            });

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("_comment");
                writer.WriteStartArray();
                foreach (string line in comment ?? DefaultComment) writer.WriteStringValue(line);
                writer.WriteEndArray();

                writer.WritePropertyName("cells");
                writer.WriteStartArray();

                foreach (var patch in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("map", patch.MapId);
                    writer.WriteNumber("cell", patch.Cell);
                    if (patch.Walkable.HasValue) writer.WriteBoolean("walk", patch.Walkable.Value);
                    if (patch.WalkableInFight.HasValue) writer.WriteBoolean("fight", patch.WalkableInFight.Value);
                    if (patch.BlocksSight.HasValue) writer.WriteBoolean("sight", patch.BlocksSight.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            File.WriteAllBytes(path, buffer.ToArray());
        }

        /// <summary>
        /// Lays the patches over the three generated sets, in place.
        /// </summary>
        /// <remarks>
        /// Written here rather than in the server and the editor separately, because the two have
        /// to agree about what a saved file means and the cheapest way to guarantee that is for
        /// there to be one implementation of it.
        /// </remarks>
        public static void Apply(IEnumerable<CellPatch> patches,
                                 IDictionary<long, HashSet<int>> walkable,
                                 IDictionary<long, HashSet<int>> inFight,
                                 IDictionary<long, HashSet<int>> blocksSight)
        {
            foreach (var patch in patches)
            {
                Set(walkable, patch.MapId, patch.Cell, patch.Walkable);
                Set(inFight, patch.MapId, patch.Cell, patch.WalkableInFight);
                Set(blocksSight, patch.MapId, patch.Cell, patch.BlocksSight);
            }
        }

        private static void Set(IDictionary<long, HashSet<int>> into, long mapId, int cell, bool? on)
        {
            if (on == null) return;

            if (!into.TryGetValue(mapId, out var set))
            {
                // A map with no generated entry can still be given one. Editing a map nobody
                // extracted is a legitimate thing to want.
                set = new HashSet<int>();
                into[mapId] = set;
            }

            if (on.Value) set.Add(cell);
            else set.Remove(cell);
        }

        private static bool? Flag(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        private static readonly string[] DefaultComment =
        {
            "Hand-made changes to map cells. Nothing regenerates this file.",
            "",
            "DELTAS, NOT COPIES. A map has 560 cells; this holds the ones that were changed.",
            "Writing all 560 would shadow the generated file for that map for ever.",
            "",
            "  { \"map\": 191106562, \"cell\": 250, \"walk\": true }        can be walked on",
            "  { \"map\": 191106562, \"cell\": 251, \"fight\": false }      blocked in a fight",
            "  { \"map\": 191106562, \"cell\": 252, \"sight\": true }       stops a spell",
            "  { \"map\": 191106562, \"cell\": 253, \"remove\": true }      drop the change",
            "",
            "Anything left out is left alone, so a row can change one layer and not the others.",
        };
    }
}
