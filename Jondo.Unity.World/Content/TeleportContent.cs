using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jondo.Unity.World.Content
{
    /// <summary>
    /// One passage: an element you click on one map, and where it puts you on another.
    /// </summary>
    /// <remarks>
    /// <see cref="ElementId"/> is not ours to invent. The client draws interactive elements from
    /// its own map data, so a passage can only hang off an element that is already standing there —
    /// 46,309 of them across 9,840 maps. Writing a passage on a cell with no element gives a door
    /// nobody can see or click.
    /// </remarks>
    public readonly struct Passage
    {
        public long SourceMapId { get; init; }

        /// <summary>The element on the source map. It has to be one the map already has.</summary>
        public long ElementId { get; init; }

        public int SourceCell { get; init; }

        /// <summary>Which drawing the element uses. Kept so the editor can show what it looks like.</summary>
        public int GfxId { get; init; }

        public int InteractiveType { get; init; }

        public int SkillId { get; init; }

        public long DestinationMapId { get; init; }

        public int DestinationCell { get; init; }

        public override string ToString()
            => $"{SourceMapId}/{ElementId} → {DestinationMapId}@{DestinationCell}";
    }

    /// <summary>
    /// What identifies a passage: the element it hangs off.
    /// </summary>
    /// <remarks>
    /// The element and not the cell, because the element is what the player clicks and what the
    /// client sends back. Two elements can share a cell; one element is one door.
    /// </remarks>
    public readonly struct PassageKey : IEquatable<PassageKey>
    {
        public PassageKey(long sourceMapId, long elementId)
        {
            SourceMapId = sourceMapId;
            ElementId = elementId;
        }

        public long SourceMapId { get; }

        public long ElementId { get; }

        public bool Equals(PassageKey other)
            => SourceMapId == other.SourceMapId && ElementId == other.ElementId;

        public override bool Equals(object? obj) => obj is PassageKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SourceMapId, ElementId);
        public override string ToString() => $"{SourceMapId}/{ElementId}";
    }

    /// <summary>
    /// The passages somebody decided on, kept apart from the ones that were extracted.
    /// </summary>
    /// <remarks>
    /// The 3,815 passages in <c>world.db</c> came out of two community catalogues for older client
    /// versions, and the table is rebuilt whenever the database is. So a passage added there would
    /// vanish on the next regeneration, silently — which is the whole reason this layer exists.
    ///
    /// This is the piece the architecture document calls the highest value per hour in the project,
    /// and the reason is concrete: <b>a house with its own interior is two passages</b>, and there
    /// is no other way to make one.
    /// </remarks>
    public static class TeleportContent
    {
        /// <summary>The authored file, relative to the content root.</summary>
        public const string AuthoredFile = "interactives/teleports.json";

        public static ContentStore<PassageKey, Passage> Load(string? authoredPath,
                                                             Action<string>? report = null)
        {
            var store = new ContentStore<PassageKey, Passage>();
            if (string.IsNullOrEmpty(authoredPath) || !File.Exists(authoredPath)) return store;

            var from = Origin.Authored(Path.GetFileName(authoredPath));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authoredPath));
                if (!doc.RootElement.TryGetProperty("passages", out var list)) return store;

                foreach (var entry in list.EnumerateArray())
                {
                    long map = Long(entry, "map");
                    long element = Long(entry, "element");
                    if (map == 0 || element == 0) continue;

                    var key = new PassageKey(map, element);

                    if (entry.TryGetProperty("remove", out var gone) &&
                        gone.ValueKind == JsonValueKind.True)
                    {
                        store.Erase(key, from);
                        continue;
                    }

                    store.Put(key, new Passage
                    {
                        SourceMapId = map,
                        ElementId = element,
                        SourceCell = (int)Long(entry, "cell"),
                        GfxId = (int)Long(entry, "gfx"),
                        InteractiveType = (int)Long(entry, "type"),
                        SkillId = entry.TryGetProperty("skill", out var skill) && skill.TryGetInt32(out int id)
                            ? id : DefaultSkill,
                        DestinationMapId = Long(entry, "toMap"),
                        DestinationCell = (int)Long(entry, "toCell"),
                    }, from);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {Path.GetFileName(authoredPath)} is unreadable: {ex.Message}");
            }

            return store;
        }

        /// <summary>
        /// The skill a passage offers: <b>184</b>, and not the 114 every extracted row declares.
        /// </summary>
        /// <remarks>
        /// Measured three independent ways, and they agree:
        ///
        /// <code>
        ///   Ankama's own world graph   5,629 of 5,719 interactive transitions use 184.
        ///                              114 appears ZERO times.
        ///   401 real captures          184 on 420 elements over 2,456 occurrences.
        ///                              114 on 23, and every one of them is a zaap.
        ///   what the server then does  skill 184 is followed by a map change 178 times;
        ///                              114 is followed by the zaap list 14 times.
        /// </code>
        ///
        /// 114 is not a passage at all — it is <em>Utilizar</em> on a zaap, which is why the client
        /// answers it with the zaap window. Our own log shows us emitting the pair (type 0, skill
        /// 114) 84 times: a pair that occurs <b>zero</b> times in the captures and zero times in
        /// Ankama's graph.
        ///
        /// 339 and 361 are real and are not alternatives: they are signpost skills — "Indicar una
        /// salida", "Panneau directionnel" — that ride alongside 184 on the same element and are
        /// never answered with a map change.
        /// </remarks>
        public const int DefaultSkill = 184;

        /// <summary>
        /// The interactive type: <b>-1</b>, meaning the element has no special type.
        /// </summary>
        /// <remarks>
        /// Type 0 appears zero times in the 154 distinct types observed across the captures. The
        /// most common pair in the whole corpus is <c>(-1, 184)</c>, at 764 occurrences.
        /// </remarks>
        public const int DefaultType = -1;

        /// <summary>What every extracted row declares, which is what this replaces.</summary>
        public const int ExtractedSkill = 114;

        public static void Save(string path, IEnumerable<Passage> passages,
                                IEnumerable<PassageKey> removed, IEnumerable<string>? comment = null)
        {
            var ordered = new List<Passage>(passages);
            ordered.Sort((a, b) =>
            {
                int byMap = a.SourceMapId.CompareTo(b.SourceMapId);
                return byMap != 0 ? byMap : a.ElementId.CompareTo(b.ElementId);
            });

            var tombstones = new List<PassageKey>(removed);
            tombstones.Sort((a, b) =>
            {
                int byMap = a.SourceMapId.CompareTo(b.SourceMapId);
                return byMap != 0 ? byMap : a.ElementId.CompareTo(b.ElementId);
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

                writer.WritePropertyName("passages");
                writer.WriteStartArray();

                foreach (var passage in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("map", passage.SourceMapId);
                    writer.WriteNumber("element", passage.ElementId);
                    writer.WriteNumber("cell", passage.SourceCell);
                    if (passage.GfxId != 0) writer.WriteNumber("gfx", passage.GfxId);
                    writer.WriteNumber("type", passage.InteractiveType);
                    writer.WriteNumber("skill", passage.SkillId);
                    writer.WriteNumber("toMap", passage.DestinationMapId);
                    writer.WriteNumber("toCell", passage.DestinationCell);
                    writer.WriteEndObject();
                }

                foreach (var key in tombstones)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("map", key.SourceMapId);
                    writer.WriteNumber("element", key.ElementId);
                    writer.WriteBoolean("remove", true);
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
        /// What is wrong with a set of passages, in words.
        /// </summary>
        /// <remarks>
        /// Checked before saving rather than after, because a passage that leads nowhere is a
        /// player standing in a doorway that does nothing, and it would be found on somebody else's
        /// machine days later.
        /// </remarks>
        public static IEnumerable<string> Complaints(IEnumerable<Passage> passages)
        {
            var byKey = new Dictionary<PassageKey, Passage>();
            foreach (var passage in passages) byKey[new PassageKey(passage.SourceMapId, passage.ElementId)] = passage;

            foreach (var passage in byKey.Values)
            {
                if (passage.DestinationMapId == 0)
                {
                    yield return $"the passage on map {passage.SourceMapId} leads nowhere.";
                    continue;
                }

                // A passage whose two ends are on the same map is NOT a mistake: there are 12 of
                // them in the extracted set. Warning about it would be an editor refusing real
                // content because it looked odd.

                if (passage.DestinationCell < 0 || passage.DestinationCell >= 560)
                {
                    yield return $"the passage on map {passage.SourceMapId} lands on cell " +
                                 $"{passage.DestinationCell}, which is not a cell.";
                }
            }
        }

        private static long Long(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;

        private static readonly string[] DefaultComment =
        {
            "The authored layer for passages between maps. Nothing regenerates this file.",
            "",
            "A passage hangs off an interactive ELEMENT, and the element cannot be invented: the",
            "client draws them from its own map data, so a passage on a cell with no element is a",
            "door nobody can see. Pick from what the map already has.",
            "",
            "  { \"map\": 135169, \"element\": 499181, \"cell\": 224, \"type\": 0, \"skill\": 114,",
            "    \"toMap\": 135170, \"toCell\": 305 }        a passage",
            "  { \"map\": 135169, \"element\": 499181, \"remove\": true }   take one away",
            "",
            "The 3,815 passages in world.db came out of two community catalogues and are rebuilt",
            "with the database, so a passage added there would vanish on the next regeneration.",
            "That is what this file is for.",
        };
    }
}
