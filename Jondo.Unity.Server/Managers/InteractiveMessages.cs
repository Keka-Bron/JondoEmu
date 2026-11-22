using System;
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
        /// MapStagings plays).
        /// </remarks>
        public sealed record Said(int Message, long ToMap, int ToCell, int Opens, int OpensCell, int OpensState,
                                  int[] Frees, string[] Params, int[] Blocks, int ClosesAfter, int Skill);

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
                    if (map == 0 || element == 0 || (message == 0 && opens == 0 && skill == 0)) continue;
                    var frees = Cells(row, "frees");
                    var blocks = Cells(row, "blocks");
                    var names = new List<string>();
                    if (row.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                        foreach (var p in ps.EnumerateArray())
                            if (p.ValueKind == JsonValueKind.String) names.Add(p.GetString() ?? "");
                    fresh[(map, element)] = new Said(message, Number(row, "toMap"), (int)Number(row, "toCell"),
                                                     opens, (int)Number(row, "opensCell"), (int)Number(row, "opensState"),
                                                     frees.ToArray(), names.ToArray(), blocks.ToArray(),
                                                     (int)Number(row, "closesAfter"), skill == 0 ? Skill : skill);
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

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
