using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.World.Content
{
    /// <summary>
    /// A cell that moves whoever stops on it to another map: a passage with no element.
    /// </summary>
    public readonly struct FloorPassage
    {
        public long SourceMapId { get; init; }
        public int SourceCell { get; init; }
        public long DestinationMapId { get; init; }
        public int DestinationCell { get; init; }

        public override string ToString()
            => $"{SourceMapId}@{SourceCell} → {DestinationMapId}@{DestinationCell}";
    }

    /// <summary>
    /// The floor passages somebody decided on, in <c>content/interactives/floor_passages.json</c>.
    /// </summary>
    /// <remarks>
    /// For the maps that need a way out and have no element to hang a passage off. A passage is
    /// an element the player clicks (<see cref="TeleportContent"/>), and the client only lets him
    /// click what its own map data has: the GM prison's dungeon, 105120002, has nothing at all.
    /// What is left is the cell -- walking onto it moves him, as a map's border does. Nothing is
    /// declared to the client for it, so the cell looks like any other.
    ///
    /// A file of its own and not a second list in teleports.json: Jondo Studio's passage editor
    /// rewrites that file whole (<see cref="TeleportContent.Save"/>), with only the passages it
    /// knows.
    /// </remarks>
    public static class FloorPassages
    {
        /// <summary>The authored file, relative to the content root.</summary>
        public const string AuthoredFile = "interactives/floor_passages.json";

        /// <summary>
        /// The file's passages; one that names no map or a cell off the board is left out and
        /// said, and a missing file is simply none.
        /// </summary>
        public static IReadOnlyList<FloorPassage> Load(string? path, Action<string>? report = null)
        {
            var passages = new List<FloorPassage>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return passages;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("passages", out var list)) return passages;

                foreach (var entry in list.EnumerateArray())
                {
                    var passage = new FloorPassage
                    {
                        SourceMapId = Long(entry, "map"),
                        SourceCell = (int)Long(entry, "cell"),
                        DestinationMapId = Long(entry, "toMap"),
                        DestinationCell = (int)Long(entry, "toCell"),
                    };
                    if (passage.SourceMapId <= 0 || passage.DestinationMapId <= 0 ||
                        !OnTheBoard(passage.SourceCell) || !OnTheBoard(passage.DestinationCell))
                    {
                        report?.Invoke($"[Content] {Path.GetFileName(path)}: {passage} is not a passage; left out.");
                        continue;
                    }
                    passages.Add(passage);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
            return passages;
        }

        private static bool OnTheBoard(int cell) => cell >= 0 && cell < 560;

        /// <summary>The number, or -1 when it is not there: cell 0 is a cell.</summary>
        private static long Long(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : -1;
    }
}
