using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What any monster beaten drops while a quest asks for it (content/quests/fight_drops.json).
    /// </summary>
    /// <remarks>
    /// A monster's quest loot is usually in the client's data, on its own table with a criterion:
    /// the hazelnut Lotape of «Reír a base de palos» is on 138 dungeon bosses' tables as «Qa=1198».
    /// The other five flavours are not on any table, and yet «se dropent sur tous types de monstres
    /// si la quête est activée» (Dofus pour les noobs): the server hands them out on its own. This
    /// file is those rows, one roll per row and per monster beaten, read with the same criterion
    /// reader as the monsters' loot.
    /// </remarks>
    public static class QuestFightDrops
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "quests/fight_drops.json";

        /// <summary>One row: every monster beaten drops <see cref="Item"/> with <see cref="Percent"/> % chance.</summary>
        public sealed class Drop
        {
            public int Item { get; init; }
            public double Percent { get; init; }
            public string Criterion { get; init; } = "";
        }

        private static List<Drop>? _drops;
        private static readonly object _lock = new();

        /// <summary>The rows, read once.</summary>
        public static IReadOnlyList<Drop> All
        {
            get
            {
                lock (_lock)
                {
                    if (_drops != null) return _drops;
                    var drops = new List<Drop>();
                    try
                    {
                        string path = Paths.ContentFile(AuthoredFile);
                        if (File.Exists(path))
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(path));
                            foreach (var row in doc.RootElement.GetProperty("drops").EnumerateArray())
                            {
                                drops.Add(new Drop
                                {
                                    Item = row.GetProperty("item").GetInt32(),
                                    Percent = row.GetProperty("percent").GetDouble(),
                                    Criterion = row.TryGetProperty("criterion", out var criterion) ? criterion.GetString() ?? "" : "",
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                    }
                    _drops = drops;
                    return _drops;
                }
            }
        }

        /// <summary>For the tests and the harness's reload: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _drops = null;
        }
    }
}
