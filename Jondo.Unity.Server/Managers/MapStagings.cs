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
    /// The stagings a map shows (hpm): played out on entering it, or played on using one of its
    /// elements. The ones a conversation plays are the replies' «stagings»
    /// (content/npcs/dialogues.json), and the ones a step plays, its push's.
    /// </summary>
    /// <remarks>
    /// The guided tutorial is built on them. Its capture plays twenty: staging 11 standing as the
    /// character -- a Sacrier, breed 11 -- wakes before his god's statue (each god's is its breed's
    /// number: 2 raises Osamodas' beast; the entry's «breed»), 902 on «Desplazarte.», 912 and 302 once he has
    /// walked, 901 and 102 on «Ver cómo se materializa un camino» -- the path to the next island,
    /// which stands built (102 at its end, hpm f1 100) on every later arrival in Ganymed's room --
    /// and on, to the 9071-9075 of the five resources picked in Master Hoboulo's room. Without
    /// them the islands stay apart and the statue is not there.
    /// </remarks>
    public static class MapStagings
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "maps/stagings.json";

        private sealed record Document(Dictionary<long, List<(int[] Ids, bool Breed, string Criterion)>> Arrivals,
                                       Dictionary<(long Map, int Element), int[]> Elements);

        private static Document? _document;
        private static readonly object _lock = new();

        /// <summary>
        /// The stagings standing played out on a map for the character entering it: each entry's,
        /// when its «criterion» holds for him (Managers.PlayerCriteria) -- the tutorial's first path
        /// stands built once he has walked the steps the voice asked for.
        /// </summary>
        public static IReadOnlyList<int> OnArrival(long mapId)
        {
            if (!Load().Arrivals.TryGetValue(mapId, out var entries)) return Array.Empty<int>();
            var ids = new List<int>();
            foreach (var (entryIds, breed, criterion) in entries)
            {
                if (criterion.Length > 0 && !PlayerCriteria.Met(criterion, mapId)) continue;
                ids.AddRange(entryIds);
                if (breed && SessionContext.State.Breed > 0) ids.Add(SessionContext.State.Breed);
            }
            return ids;
        }

        /// <summary>The stagings using an element of a map plays.</summary>
        public static IReadOnlyList<int> OfElement(long mapId, int elementId)
            => Load().Elements.TryGetValue((mapId, elementId), out var ids) ? ids : Array.Empty<int>();

        /// <summary>Plays stagings from their start, in order.</summary>
        public static Task PlayAsync(NetworkStream stream, IEnumerable<int> ids)
            => SendAsync(stream, ids, GuildRaidStagings.FromStart);

        /// <summary>Shows stagings already played out, as a map that has seen them stands.</summary>
        public static Task ShowAsync(NetworkStream stream, IEnumerable<int> ids)
            => SendAsync(stream, ids, GuildRaidStagings.AtEnd);

        private static async Task SendAsync(NetworkStream stream, IEnumerable<int> ids, int progression)
        {
            if (stream == null || ids == null) return;
            foreach (int id in ids)
            {
                if (id <= 0) continue;
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Hpm, GuildRaidStagings.BuildPlay(id, progression)));
            }
        }

        /// <summary>For a content reload: read the file again.</summary>
        public static void Forget()
        {
            lock (_lock) _document = null;
        }

        private static Document Load()
        {
            lock (_lock)
            {
                if (_document != null) return _document;
                var arrivals = new Dictionary<long, List<(int[], bool, string)>>();
                var elements = new Dictionary<(long, int), int[]>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        if (doc.RootElement.TryGetProperty("arrivals", out var list))
                        {
                            foreach (var entry in list.EnumerateArray())
                            {
                                long map = entry.GetProperty("map").GetInt64();
                                var ids = Ids(entry);
                                string criterion = entry.TryGetProperty("criterion", out var c) && c.ValueKind == JsonValueKind.String
                                    ? c.GetString() ?? "" : "";
                                bool breed = entry.TryGetProperty("breed", out var b) && b.ValueKind == JsonValueKind.True;
                                if (map <= 0 || (ids.Length == 0 && !breed)) continue;
                                if (!arrivals.TryGetValue(map, out var list2)) arrivals[map] = list2 = new List<(int[], bool, string)>();
                                list2.Add((ids, breed, criterion));
                            }
                        }
                        if (doc.RootElement.TryGetProperty("elements", out var used))
                        {
                            foreach (var entry in used.EnumerateArray())
                            {
                                long map = entry.GetProperty("map").GetInt64();
                                int element = entry.GetProperty("element").GetInt32();
                                var ids = Ids(entry);
                                if (map > 0 && element > 0 && ids.Length > 0) elements[(map, element)] = ids;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _document = new Document(arrivals, elements);
                return _document;
            }
        }

        private static int[] Ids(JsonElement entry)
        {
            if (!entry.TryGetProperty("stagings", out var ids) || ids.ValueKind != JsonValueKind.Array)
                return Array.Empty<int>();
            var list = new List<int>();
            foreach (var id in ids.EnumerateArray())
                if (id.TryGetInt32(out int value) && value > 0) list.Add(value);
            return list.ToArray();
        }
    }
}
