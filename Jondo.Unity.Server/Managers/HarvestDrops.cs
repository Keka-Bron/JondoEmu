using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What a gathering hands over besides the resource, while a quest asks for it
    /// (content/quests/harvest_drops.json).
    /// </summary>
    /// <remarks>
    /// The monsters' quest loot is in the client's data, each drop with its criterion -- the
    /// contaminated meat of «Crisis sanitaria» falls off a pío only while objective 13820 is in hand
    /// («Qo=13820&amp;PO!19649&amp;PB=95&amp;Pj&gt;41,0&amp;CU&gt;0»). The other four samples of that
    /// objective come from the jobs (Dofus pour les noobs: «en fauchant du Blé dans les Champs
    /// d'Astrub», «en coupant un Frêne», «en cueillant un Ortie», «en pêchant du Goujon»), and the
    /// client has no row for them: the server decides. This file is those rows, read with the same
    /// criterion reader as the monsters' loot.
    /// </remarks>
    public static class HarvestDrops
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "quests/harvest_drops.json";

        /// <summary>One row: gathering <see cref="Resource"/> gives <see cref="Count"/> of <see cref="Item"/>.</summary>
        public sealed class Drop
        {
            public int Resource { get; init; }
            public int Item { get; init; }
            public int Count { get; init; } = 1;
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
                                    Resource = row.GetProperty("resource").GetInt32(),
                                    Item = row.GetProperty("item").GetInt32(),
                                    Count = row.TryGetProperty("count", out var count) ? Math.Max(1, count.GetInt32()) : 1,
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

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _drops = null;
        }

        /// <summary>
        /// A resource has been gathered on a map: what its rows give to whoever meets them, each
        /// item with the chat's line, as a purchase's (lqn 21).
        /// </summary>
        public static async Task OnGatheredAsync(NetworkStream stream, int resource, long mapId)
        {
            foreach (var drop in All)
            {
                if (drop.Resource != resource) continue;
                if (drop.Criterion.Length > 0 && !PlayerCriteria.Met(drop.Criterion, mapId)) continue;
                if (!await Equipment.GiveAsync(stream, drop.Item, drop.Count)) continue;
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        InfoMessages.ItemGained, drop.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        drop.Item.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine($"[Jobs] Gathering {resource} on {mapId} also gives {drop.Count}x{drop.Item}.");
            }
        }
    }
}
