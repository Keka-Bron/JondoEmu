using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What a map puzzle remembers of each character while it is played: the padlock picked, the
    /// guardian come back to watch for a while. A mark either stays until it is cleared or runs
    /// out after its seconds, the guardian's «decide quedarse un rato para vigilarte» (two minutes,
    /// by the guides' «il faudra attendre 2 minutes qu'elle parte»).
    /// </summary>
    /// <remarks>
    /// Kept in memory: a puzzle is played in one sitting, and a server that restarts in the middle
    /// leaves it to be played again from its first step, never half solved.
    /// </remarks>
    public static class PuzzleMarks
    {
        private static readonly ConcurrentDictionary<(long Character, string Mark), DateTimeOffset> _marks = new();

        public static void Set(long character, string mark, int seconds, DateTimeOffset now)
            => _marks[(character, mark)] = seconds > 0 ? now.AddSeconds(seconds) : DateTimeOffset.MaxValue;

        public static void Clear(long character, string mark) => _marks.TryRemove((character, mark), out _);

        public static bool Has(long character, string mark, DateTimeOffset now)
        {
            if (!_marks.TryGetValue((character, mark), out var until)) return false;
            if (until > now) return true;
            _marks.TryRemove((character, mark), out _);
            return false;
        }

        /// <summary>
        /// Which of <paramref name="count"/> versions a character gets, the same one every time:
        /// the prison's riddle is «différente d'une personnage à l'autre, il en existe un total de 24».
        /// </summary>
        public static int Variant(long character, int count)
            => count <= 0 ? 0 : (int)(((character % count) + count) % count);
    }

    /// <summary>
    /// How many times each character has used an element whose cases count their uses
    /// (<see cref="PuzzleCase.Uses"/>), and until when it is locked: the Xelorium's clocks hand out
    /// their minutes but, used too much, lock for a few minutes (Dofus pour les noobs,
    /// «Énergie renouvelable»).
    /// </summary>
    /// <remarks>
    /// Kept in memory as <see cref="PuzzleMarks"/> are: a restart frees every clock.
    /// </remarks>
    public static class PuzzleUses
    {
        private static readonly ConcurrentDictionary<(long Character, string Key), (int Count, DateTimeOffset Until)> _uses = new();

        /// <summary>Whether <paramref name="key"/> is locked for this character now.</summary>
        public static bool Locked(long character, string key, DateTimeOffset now)
        {
            if (!_uses.TryGetValue((character, key), out var used) || used.Until == default) return false;
            if (used.Until > now) return true;
            _uses.TryRemove((character, key), out _);
            return false;
        }

        /// <summary>
        /// One more use of <paramref name="key"/>: the <paramref name="uses"/>-th locks it for
        /// <paramref name="lockFor"/> seconds and starts the count again. True when this use locked it.
        /// </summary>
        public static bool Use(long character, string key, int uses, int lockFor, DateTimeOffset now)
        {
            if (uses <= 0) return false;
            var slot = (character, key);
            int count = (_uses.TryGetValue(slot, out var used) && used.Until == default ? used.Count : 0) + 1;
            if (count < uses)
            {
                _uses[slot] = (count, default);
                return false;
            }
            if (lockFor > 0) _uses[slot] = (0, now.AddSeconds(lockFor));
            else _uses.TryRemove(slot, out _);
            return lockFor > 0;
        }

        /// <summary>For the tests: forget everything.</summary>
        internal static void Clear() => _uses.Clear();
    }

    /// <summary>
    /// One of the answers an element or a cell gives, chosen by what the character carries and what
    /// the puzzle remembers of him: the padlock without the lockpick rattles and brings the guardian
    /// back (866), with it opens without a sound (867) and is remembered as open.
    /// </summary>
    /// <remarks>
    /// The first case that holds is the one played: its criterion (<see cref="ItemCriteria"/>), the
    /// marks it <see cref="Needs"/> and the ones it <see cref="Lacks"/>. Playing it says its messages,
    /// takes and gives its items, clears and sets its marks (those it sets lasting <see cref="For"/>
    /// seconds, zero for good) and, last, moves the character.
    /// </remarks>
    public sealed record PuzzleCase
    {
        public string Criterion { get; init; } = "";
        public string[] Needs { get; init; } = Array.Empty<string>();
        public string[] Lacks { get; init; } = Array.Empty<string>();
        /// <summary>Said in turn: the bell's «¡DING, DING!» (869) and then the guardian come to watch (868).</summary>
        public int[] Messages { get; init; } = Array.Empty<int>();
        public (int Item, int Count)[] Gives { get; init; } = Array.Empty<(int, int)>();
        public (int Item, int Count)[] Takes { get; init; } = Array.Empty<(int, int)>();
        public string[] Mark { get; init; } = Array.Empty<string>();
        public string[] Unmark { get; init; } = Array.Empty<string>();
        public int For { get; init; }
        public long ToMap { get; init; }
        public int ToCell { get; init; }

        /// <summary>
        /// Whether the items add up («stacks»): its gives go in on every use, whatever is carried,
        /// and its takes take what there is up to their count. A Xelorium clock 7 minutes fast adds 7
        /// Minutes from Xelorium each time; one 8 minutes slow takes up to 8.
        /// </summary>
        public bool Stacks { get; init; }

        /// <summary>
        /// Uses before the element locks («uses»), zero for no limit; while locked the case does not
        /// hold and the next one is tried. Counted per element and character (<see cref="PuzzleUses"/>).
        /// </summary>
        public int Uses { get; init; }

        /// <summary>Seconds the element stays locked after its <see cref="Uses"/> («lockFor»).</summary>
        public int LockFor { get; init; }

        public bool Applies(Func<string, bool> holds, Func<string, bool> has)
            => (Criterion.Length == 0 || holds(Criterion)) && Needs.All(has) && !Lacks.Any(has);

        public static PuzzleCase? First(IEnumerable<PuzzleCase> cases, Func<string, bool> holds, Func<string, bool> has)
            => cases.FirstOrDefault(c => c.Applies(holds, has));

        /// <summary>
        /// The first case that holds and is not locked: a case with <see cref="Uses"/> is skipped
        /// while <paramref name="locked"/> says its element is.
        /// </summary>
        public static PuzzleCase? First(IEnumerable<PuzzleCase> cases, Func<string, bool> holds, Func<string, bool> has,
                                        Func<bool> locked)
            => cases.FirstOrDefault(c => c.Applies(holds, has) && (c.Uses <= 0 || !locked()));

        /// <summary>
        /// How many of a give go in for whoever carries <paramref name="held"/>: all of them when
        /// the case <see cref="Stacks"/>, otherwise only while none is carried.
        /// </summary>
        public int GiveNow(int held, int count) => Stacks || held <= 0 ? count : 0;

        /// <summary>
        /// How many of a take go for whoever carries <paramref name="held"/>: what there is up to the
        /// count when the case <see cref="Stacks"/>, otherwise the whole count or nothing.
        /// </summary>
        public int TakeNow(int held, int count) => Stacks ? Math.Min(Math.Max(0, held), count) : held >= count ? count : 0;

        /// <summary>A row's «cases», in their order; none when it has none.</summary>
        public static PuzzleCase[] Read(JsonElement row, string name = "cases")
        {
            var cases = new List<PuzzleCase>();
            if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return cases.ToArray();
            foreach (var c in list.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                cases.Add(One(c));
            }
            return cases.ToArray();
        }

        public static PuzzleCase One(JsonElement c) => new()
        {
            Criterion = c.TryGetProperty("criterion", out var cr) && cr.ValueKind == JsonValueKind.String ? cr.GetString() ?? "" : "",
            Needs = Words(c, "needs"),
            Lacks = Words(c, "lacks"),
            Messages = Numbers(c, "message"),
            Gives = Items(c, "gives"),
            Takes = Items(c, "takes"),
            Mark = Words(c, "mark"),
            Unmark = Words(c, "unmark"),
            For = (int)Number(c, "for"),
            ToMap = Number(c, "toMap"),
            ToCell = (int)Number(c, "toCell"),
            Stacks = c.TryGetProperty("stacks", out var stacks) && stacks.ValueKind == JsonValueKind.True,
            Uses = (int)Number(c, "uses"),
            LockFor = (int)Number(c, "lockFor"),
        };

        /// <summary>A list of words, or a single word for a list of one.</summary>
        private static string[] Words(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out var value)) return Array.Empty<string>();
            if (value.ValueKind == JsonValueKind.String) return new[] { value.GetString() ?? "" };
            if (value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            return value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToArray();
        }

        /// <summary>A number, or a list of them.</summary>
        private static int[] Numbers(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out var value)) return Array.Empty<int>();
            if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out int one) && one != 0 ? new[] { one } : Array.Empty<int>();
            if (value.ValueKind != JsonValueKind.Array) return Array.Empty<int>();
            return value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _))
                        .Select(v => v.GetInt32()).Where(v => v != 0).ToArray();
        }

        /// <summary>{ item, count } each, a count of one when it says none.</summary>
        internal static (int Item, int Count)[] Items(JsonElement row, string name)
        {
            var items = new List<(int, int)>();
            if (row.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var one in list.EnumerateArray())
                {
                    if (one.ValueKind != JsonValueKind.Object) continue;
                    int item = (int)Number(one, "item");
                    int count = (int)Number(one, "count");
                    if (item > 0) items.Add((item, count > 0 ? count : 1));
                }
            return items.ToArray();
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
