using System;
using System.Collections.Generic;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Cells that answer whoever stops on them, as a puzzle wants: a spot that hands something over
    /// (the lockpick «escondida bajo la rejilla de ventilación», 863) and the numbered tiles of a
    /// riddle, crossed by stepping on the right digit of each island in turn.
    /// </summary>
    /// <remarks>
    /// <para>A spot answers with the first of its <see cref="PuzzleCase"/>s that holds, nothing when
    /// none does.</para>
    /// <para>A riddle's tiles are grouped in steps, one per island: the right digit of a step takes the
    /// character to the next island (its <c>toCell</c>), a wrong one plays the riddle's
    /// <c>wrong</c> case («La losa activa una alarma...», 860, back to the prison). Which digit is
    /// right comes from the document the character was given to read: the riddle names that
    /// readable, and its answers are keyed by document.</para>
    /// </remarks>
    public static class FloorRiddles
    {
        public const string File = "interactives/floor_riddles.json";

        public sealed record Spot(long Map, int Cell, PuzzleCase[] Cases);

        public sealed record Step(IReadOnlyDictionary<int, char> Tiles, int ToCell);

        public sealed record Riddle(long Map, long ReadableMap, int ReadableElement,
                                    IReadOnlyDictionary<int, string> Answers, Step[] Steps, PuzzleCase Wrong);

        private sealed record Book(Dictionary<(long, int), Spot> Spots,
                                   Dictionary<(long, int), (Riddle Riddle, int Step, char Digit)> Tiles);

        private static volatile Book? _book;
        private static readonly object _lock = new();

        public static void Load() => Ensure();

        public static void Forget()
        {
            lock (_lock) _book = Read(Paths.ContentFile(File));
        }

        private static Book Ensure()
        {
            var book = _book;
            if (book != null) return book;
            lock (_lock) return _book ??= Read(Paths.ContentFile(File));
        }

        public static Spot? SpotOf(long map, int cell)
            => Ensure().Spots.TryGetValue((map, cell), out var spot) ? spot : null;

        public static (Riddle Riddle, int Step, char Digit)? TileOf(long map, int cell)
            => Ensure().Tiles.TryGetValue((map, cell), out var tile) ? tile : null;

        /// <summary>Whether <paramref name="digit"/> is the one step <paramref name="step"/> of <paramref name="answer"/> wants.</summary>
        public static bool Right(string answer, int step, char digit)
            => step >= 0 && step < answer.Length && answer[step] == digit;

        /// <summary>The file read, for the tests: a missing or broken one is an empty book, said.</summary>
        internal static (int Spots, int Tiles) Count(string path)
        {
            var book = Read(path);
            return (book.Spots.Count, book.Tiles.Count);
        }

        internal static void UseForTests(string path)
        {
            lock (_lock) _book = Read(path);
        }

        private static Book Read(string path)
        {
            var spots = new Dictionary<(long, int), Spot>();
            var tiles = new Dictionary<(long, int), (Riddle, int, char)>();
            var book = new Book(spots, tiles);
            if (!System.IO.File.Exists(path)) return book;
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("spots", out var spotList) && spotList.ValueKind == JsonValueKind.Array)
                    foreach (var row in spotList.EnumerateArray())
                    {
                        long map = Number(row, "map");
                        int cell = (int)Number(row, "cell");
                        var cases = PuzzleCase.Read(row);
                        if (map == 0 || cell < 0 || cell >= 560 || cases.Length == 0) continue;
                        spots[(map, cell)] = new Spot(map, cell, cases);
                    }
                if (root.TryGetProperty("riddles", out var riddleList) && riddleList.ValueKind == JsonValueKind.Array)
                    foreach (var row in riddleList.EnumerateArray())
                        AddRiddle(row, tiles);
                Console.WriteLine($"[FloorRiddles] {spots.Count} spot(s), {tiles.Count} riddle tile(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FloorRiddles] {File} is unreadable: {ex.Message}");
            }
            return book;
        }

        private static void AddRiddle(JsonElement row, Dictionary<(long, int), (Riddle, int, char)> tiles)
        {
            long map = Number(row, "map");
            if (map == 0 || !row.TryGetProperty("readable", out var readable)) return;
            var answers = new Dictionary<int, string>();
            if (row.TryGetProperty("answers", out var list) && list.ValueKind == JsonValueKind.Object)
                foreach (var answer in list.EnumerateObject())
                    if (int.TryParse(answer.Name, out int document) && answer.Value.ValueKind == JsonValueKind.String)
                        answers[document] = answer.Value.GetString() ?? "";
            var steps = new List<Step>();
            if (row.TryGetProperty("steps", out var stepList) && stepList.ValueKind == JsonValueKind.Array)
                foreach (var step in stepList.EnumerateArray())
                {
                    var digits = new Dictionary<int, char>();
                    if (step.TryGetProperty("tiles", out var t) && t.ValueKind == JsonValueKind.Object)
                        foreach (var tile in t.EnumerateObject())
                        {
                            string digit = tile.Value.ValueKind == JsonValueKind.Number ? tile.Value.GetRawText()
                                         : tile.Value.GetString() ?? "";
                            if (int.TryParse(tile.Name, out int cell) && digit.Length == 1) digits[cell] = digit[0];
                        }
                    steps.Add(new Step(digits, (int)Number(step, "toCell")));
                }
            var wrong = row.TryGetProperty("wrong", out var w) && w.ValueKind == JsonValueKind.Object
                ? PuzzleCase.One(w) : new PuzzleCase();
            var riddle = new Riddle(map, Number(readable, "map"), (int)Number(readable, "element"),
                                    answers, steps.ToArray(), wrong);
            for (int i = 0; i < steps.Count; i++)
                foreach (var (cell, digit) in steps[i].Tiles)
                    tiles[(map, cell)] = (riddle, i, digit);
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
