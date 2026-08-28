using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>One interactive element standing on a map.</summary>
    public sealed class MapElement
    {
        public long MapId { get; init; }
        public long ElementId { get; init; }
        public int Cell { get; init; }
        public int GfxId { get; init; }

        /// <summary>The type measured off real captures for this drawing, or zero.</summary>
        public int MeasuredType { get; init; }

        /// <summary>Where it already leads, when it is already a passage.</summary>
        public Passage? Leads { get; set; }

        /// <summary>True when the passage came out of the catalogues rather than from a person.</summary>
        public bool Extracted { get; set; }

        public bool IsPassage => Leads.HasValue;

        public override string ToString() => $"{Cell}  ·  gfx {GfxId}";
    }

    /// <summary>
    /// The interactive elements of the world, and which of them are already doors.
    /// </summary>
    /// <remarks>
    /// Three sources, and they say different things:
    ///
    /// <code>
    ///   datos/interactive_elements.json   9,840 maps, 46,309 elements. What the CLIENT has.
    ///   world.db InteractiveTeleports     3,815 passages, extracted from two community
    ///                                     catalogues for older client versions. Regenerable.
    ///   content/interactives/teleports.json   what a person decided. Never regenerated.
    /// </code>
    ///
    /// The first is the important one and the one that constrains everything: <b>an element cannot
    /// be invented.</b> The client draws them from its own map data, so the editor can only ever
    /// offer what is already standing on the map. That is why this screen is a picker and not a
    /// painter.
    /// </remarks>
    public sealed class InteractiveCatalogue : IDisposable
    {
        private readonly SqliteConnection? _world;
        private readonly Dictionary<long, List<MapElement>> _byMap = new Dictionary<long, List<MapElement>>();
        private readonly Dictionary<int, int> _measuredTypes = new Dictionary<int, int>();

        /// <summary>The passages that came out of the catalogues, by the element they hang off.</summary>
        private readonly Dictionary<PassageKey, Passage> _extracted = new Dictionary<PassageKey, Passage>();

        public InteractiveCatalogue(Action<string>? report = null)
        {
            ReadMeasuredTypes(report);

            try
            {
                _world = new SqliteConnection(Paths.WorldConnectionString + ";Mode=ReadOnly");
                _world.Open();
                ReadExtracted();
            }
            catch (Exception ex)
            {
                report?.Invoke($"world.db could not be opened: {ex.Message}");
                _world = null;
            }

            ReadElements(report);
        }

        public bool Ready => _byMap.Count > 0;

        /// <summary>How many elements the client has, over how many maps.</summary>
        public int ElementCount { get; private set; }

        public int MapCount => _byMap.Count;

        /// <summary>How many passages came out of the catalogues.</summary>
        public int ExtractedCount => _extracted.Count;

        /// <summary>Every element on one map, in cell order. Empty when the map has none.</summary>
        public IReadOnlyList<MapElement> On(long mapId)
            => _byMap.TryGetValue(mapId, out var list) ? list : Array.Empty<MapElement>();

        /// <summary>The passage the catalogues give for one element, if any.</summary>
        public Passage? ExtractedFor(long mapId, long elementId)
            => _extracted.TryGetValue(new PassageKey(mapId, elementId), out var passage) ? passage : null;

        /// <summary>The type measured for a drawing, or zero when nobody has seen one used.</summary>
        public int TypeOf(int gfxId) => _measuredTypes.TryGetValue(gfxId, out int type) ? type : 0;

        private void ReadMeasuredTypes(Action<string>? report)
        {
            string path = Paths.InteractiveTypesJson;
            if (!File.Exists(path)) return;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int gfx)) continue;
                    if (!entry.Value.TryGetProperty("tipo", out var type)) continue;

                    // The file carries an unsigned sentinel for "seen but never identified", which
                    // does not fit in an int and is not a type anyway.
                    if (!type.TryGetInt32(out int measured) || measured < 0) continue;

                    _measuredTypes[gfx] = measured;
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"{Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }

        private void ReadExtracted()
        {
            if (_world == null) return;

            using var command = _world.CreateCommand();
            command.CommandText = @"
                SELECT SourceMapId, ElementId, SourceCellId, GfxId, InteractiveType, SkillId,
                       DestinationMapId, DestinationCellId
                FROM InteractiveTeleports
                WHERE Enabled = 1;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var passage = new Passage
                {
                    SourceMapId = reader.GetInt64(0),
                    ElementId = reader.GetInt64(1),
                    SourceCell = reader.GetInt32(2),
                    GfxId = reader.GetInt32(3),
                    InteractiveType = reader.GetInt32(4),
                    SkillId = reader.GetInt32(5),
                    DestinationMapId = reader.GetInt64(6),
                    DestinationCell = reader.GetInt32(7),
                };

                _extracted[new PassageKey(passage.SourceMapId, passage.ElementId)] = passage;
            }
        }

        private void ReadElements(Action<string>? report)
        {
            string path = Paths.InteractiveElementsJson;
            if (!File.Exists(path))
            {
                report?.Invoke($"{Path.GetFileName(path)} is not there; no map will show any element.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!long.TryParse(entry.Name, out long mapId)) continue;
                    if (entry.Value.ValueKind != JsonValueKind.Array) continue;

                    var here = new List<MapElement>();
                    foreach (var element in entry.Value.EnumerateArray())
                    {
                        long id = Long(element, "e");
                        if (id == 0) continue;

                        int gfx = (int)Long(element, "g");
                        var made = new MapElement
                        {
                            MapId = mapId,
                            ElementId = id,
                            Cell = (int)Long(element, "c"),
                            GfxId = gfx,
                            MeasuredType = TypeOf(gfx),
                        };

                        var already = ExtractedFor(mapId, id);
                        if (already.HasValue)
                        {
                            made.Leads = already;
                            made.Extracted = true;
                        }

                        here.Add(made);
                    }

                    here.Sort((a, b) => a.Cell.CompareTo(b.Cell));
                    _byMap[mapId] = here;
                    ElementCount += here.Count;
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"{Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }

        /// <summary>
        /// Lays the authored layer over the extracted one, so the editor shows one world.
        /// </summary>
        /// <remarks>
        /// Called again after every save. The extracted passage underneath is kept in
        /// <see cref="MapElement.Extracted"/> so a row can still say where it came from.
        /// </remarks>
        public void Apply(IEnumerable<KeyValuePair<PassageKey, Passage>> authored,
                          IEnumerable<PassageKey> removed)
        {
            foreach (var list in _byMap.Values)
            {
                foreach (var element in list)
                {
                    var extracted = ExtractedFor(element.MapId, element.ElementId);
                    element.Leads = extracted;
                    element.Extracted = extracted.HasValue;
                }
            }

            foreach (var key in removed)
            {
                var element = Find(key);
                if (element == null) continue;

                element.Leads = null;
                element.Extracted = false;
            }

            foreach (var pair in authored)
            {
                var element = Find(pair.Key);
                if (element == null) continue;

                element.Leads = pair.Value;
                element.Extracted = false;
            }
        }

        private MapElement? Find(PassageKey key)
        {
            if (!_byMap.TryGetValue(key.SourceMapId, out var list)) return null;

            foreach (var element in list)
            {
                if (element.ElementId == key.ElementId) return element;
            }

            return null;
        }

        private static long Long(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;

        public void Dispose() => _world?.Dispose();
    }
}
