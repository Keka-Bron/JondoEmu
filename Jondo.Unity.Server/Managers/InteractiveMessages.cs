using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The interactives that only say something in the chat when pressed: tombstones, statues, the
    /// mirror, the hourglass that also sends you back in time to the street.
    /// </summary>
    /// <remarks>
    /// Measured on the official server (packet captures, 2026-11-20/21): the element in use by the
    /// character with skill 184, and right after it an information message of type 0; the hourglass
    /// then changes the map. Nothing else.
    ///
    /// A quest comes first: the handler only asks here after <see cref="Quests"/> has said the
    /// element is none of its objectives for this character, so a tomb a quest sends you to (Gobalo's,
    /// Snuffles') does what the quest says while it is under way and says its epitaph otherwise.
    /// </remarks>
    public static class InteractiveMessages
    {
        public const string File = "interactives/messages.json";

        /// <summary>The skill they are declared and used with, «Usar»: every use measured says 184.</summary>
        public const int Skill = 184;

        /// <summary>
        /// What pressing it does: a message (zero for none), a map change, and another element of the
        /// map opened -- its state on its cell, and the cells it stood in the way of made walkable:
        /// the lever of [5,-17]'s secret passage puts 515292 on cell 274 in state 1 and frees cell 274.
        /// </summary>
        /// <remarks>
        /// <c>Blocks</c>: cells made unwalkable in the same answer (the bridge of 99090955 swings:
        /// it frees 298, 313, 312, 326 and blocks 272, 244, 258, 259). <c>ClosesAfter</c>: seconds
        /// until the opened element shuts again and its cells are blocked back (Amakna castle's piano:
        /// the door 471367 opened at 943.384 s and shut at 953.384 s, the capture's clock). <c>Skill</c>:
        /// the option it is declared with, «Usar» 184 or «Acionar» 179 (the levers, whose staging
        /// MapStagings plays). <c>Order</c>: its place in a row of levers pulled in turn, zero for
        /// none -- what it opens and where it sends only happen when the whole row is pulled in order
        /// (<see cref="Pull"/>).
        /// </remarks>
        public sealed record Said(int Message, long ToMap, int ToCell, int Opens, int OpensCell, int OpensState,
                                  int[] Frees, string[] Params, int[] Blocks, int ClosesAfter, int Skill, int Order = 0)
        {
            /// <summary>
            /// What pressing it puts in the bag, each item only while none is carried: the urn of
            /// Kanniball Village's exit room and its Kannibubble Scroll.
            /// </summary>
            public (int Item, int Count)[] Gives { get; init; } = Array.Empty<(int, int)>();

            /// <summary>
            /// An item criterion the hand-out waits on (<see cref="ItemCriteria"/>), empty for none:
            /// the scroll's own «PT!24015&amp;PT!24016», not to whoever already knows the spell.
            /// </summary>
            public string Criterion { get; init; } = "";

            /// <summary>
            /// The answers a puzzle's element picks from (<see cref="PuzzleCase"/>), the first that
            /// holds played instead of the row; none, or none that holds, and the row speaks.
            /// </summary>
            public PuzzleCase[] Cases { get; init; } = Array.Empty<PuzzleCase>();
        }

        /// <summary>
        /// The message's parameters, by name, as the official server filled them (2026-11-21):
        /// «name» the character's (the mirrors: «...se parece muito com <name>?»), «hour» and «minute»
        /// Paris's clock in two digits («O relógio indica 13:02.» at 09:02 in Brazil, the clocks).
        /// </summary>
        public static string[] Fill(Said said, string characterName, DateTimeOffset now)
        {
            if (said.Params.Length == 0) return Array.Empty<string>();
            var paris = OsavoraSeasons.InParis(now);
            var filled = new string[said.Params.Length];
            for (int i = 0; i < said.Params.Length; i++)
                filled[i] = said.Params[i] switch
                {
                    "name" => characterName,
                    "hour" => paris.Hour.ToString("00"),
                    "minute" => paris.Minute.ToString("00"),
                    _ => "",
                };
            return filled;
        }

        private static volatile Dictionary<(long Map, int Element), Said> _byElement = new();
        private static volatile bool _loaded;
        private static readonly object _lock = new();

        public static void Load() => Ensure();

        /// <summary>The content reload: the file read again.</summary>
        public static void Forget()
        {
            lock (_lock)
            {
                _byElement = Read();
                _loaded = true;
            }
        }

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try { _byElement = Read(); }
                finally { _loaded = true; }
            }
        }

        private static Dictionary<(long, int), Said> Read()
        {
            var fresh = new Dictionary<(long, int), Said>();
            string path = Paths.ContentFile(File);
            if (!System.IO.File.Exists(path)) return fresh;
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array)
                    return fresh;
                foreach (var row in list.EnumerateArray())
                {
                    long map = Number(row, "map");
                    int element = (int)Number(row, "element");
                    int message = (int)Number(row, "message");
                    int opens = (int)Number(row, "opens");
                    int skill = (int)Number(row, "skill");
                    var gives = Gifts(row);
                    var cases = PuzzleCase.Read(row);
                    if (map == 0 || element == 0
                        || (message == 0 && opens == 0 && skill == 0 && gives.Length == 0 && cases.Length == 0)) continue;
                    var frees = Cells(row, "frees");
                    var blocks = Cells(row, "blocks");
                    var names = new List<string>();
                    if (row.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                        foreach (var p in ps.EnumerateArray())
                            if (p.ValueKind == JsonValueKind.String) names.Add(p.GetString() ?? "");
                    fresh[(map, element)] = new Said(message, Number(row, "toMap"), (int)Number(row, "toCell"),
                                                     opens, (int)Number(row, "opensCell"), (int)Number(row, "opensState"),
                                                     frees.ToArray(), names.ToArray(), blocks.ToArray(),
                                                     (int)Number(row, "closesAfter"), skill == 0 ? Skill : skill,
                                                     (int)Number(row, "order"))
                    {
                        Gives = gives,
                        Cases = cases,
                        Criterion = row.TryGetProperty("criterion", out var criterion)
                                    && criterion.ValueKind == JsonValueKind.String ? criterion.GetString() ?? "" : "",
                    };
                }
                Console.WriteLine($"[InteractiveMessages] {fresh.Count} interactives that say something.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InteractiveMessages] {File} is unreadable: {ex.Message}");
            }
            return fresh;
        }

        public static Said? Of(long mapId, int elementId)
        {
            Ensure();
            return _byElement.TryGetValue((mapId, elementId), out var said) ? said : null;
        }

        /// <summary>
        /// What of <see cref="Said.Gives"/> goes in the bag of whoever holds <paramref name="held"/>
        /// of each item: only what he carries none of, so pressing the urn again with the scroll still
        /// in the bag gives no second one.
        /// </summary>
        public static (int Item, int Count)[] HandOut(Said said, Func<int, int> held)
        {
            var handed = new List<(int, int)>();
            foreach (var (item, count) in said.Gives)
                if (held(item) <= 0) handed.Add((item, count));
            return handed.ToArray();
        }

        private static readonly ConcurrentDictionary<(long Character, long Map), int> _pulled = new();

        /// <summary>
        /// One lever of an ordered row pulled by a character: true when it completes the row. The
        /// right next one moves him on, the first one starts him over, any other one undoes the row:
        /// the five levers of the third room of Dramak's Theatre, red, purple, orange, yellow and
        /// green, open the secret passage backstage (Dofus pour les noobs, «Théâtre de Dramak»).
        /// </summary>
        public static bool Pull(long characterId, long mapId, Said said)
        {
            if (said.Order <= 0) return true;
            Ensure();
            int last = 0;
            foreach (var ((map, _), other) in _byElement)
                if (map == mapId && other.Order > last) last = other.Order;
            var key = (characterId, mapId);
            _pulled.TryGetValue(key, out int done);
            done = said.Order == done + 1 ? said.Order : said.Order == 1 ? 1 : 0;
            if (done >= last)
            {
                _pulled.TryRemove(key, out _);
                return true;
            }
            _pulled[key] = done;
            return false;
        }

        public static IEnumerable<int> OnMap(long mapId)
        {
            Ensure();
            foreach (var ((map, element), _) in _byElement)
                if (map == mapId) yield return element;
        }

        private static List<int> Cells(JsonElement row, string name)
        {
            var cells = new List<int>();
            if (row.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var cell in list.EnumerateArray())
                    if (cell.TryGetInt32(out int c)) cells.Add(c);
            return cells;
        }

        /// <summary>A row's «gives»: { item, count } each, a count of one when it says none.</summary>
        private static (int Item, int Count)[] Gifts(JsonElement row)
        {
            var gifts = new List<(int, int)>();
            if (row.TryGetProperty("gives", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var gift in list.EnumerateArray())
                {
                    if (gift.ValueKind != JsonValueKind.Object) continue;
                    int item = (int)Number(gift, "item");
                    int count = (int)Number(gift, "count");
                    if (item > 0) gifts.Add((item, count > 0 ? count : 1));
                }
            return gifts.ToArray();
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
