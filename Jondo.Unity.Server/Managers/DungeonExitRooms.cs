using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The rooms some dungeons leave their winners in after the boss, before the way out: a room
    /// with no fight, where an NPC offers to leave (content/world/dungeon_exits.json).
    /// </summary>
    /// <remarks>
    /// They are not the dungeon's rooms (DungeonRooms): they hold no fight and are no boss room.
    /// What makes one is the content file, so another dungeon with such a room is a row, not code.
    /// </remarks>
    public static class DungeonExitRooms
    {
        public const string File = "world/dungeon_exits.json";

        public sealed class ExitRoom
        {
            public int Dungeon { get; init; }
            public long BossRoom { get; init; }
            public long MapId { get; init; }
            public int Cell { get; init; }
            public int Npc { get; init; }
            public HashSet<long> Replies { get; init; } = new();
        }

        private static readonly Dictionary<long, ExitRoom> _afterBoss = new();
        private static readonly Dictionary<long, ExitRoom> _byRoom = new();

        /// <summary>Whether the file is read; volatile because <see cref="Ensure"/> reads it outside the lock, and raised last.</summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>Reads the file once, at boot, with its log line.</summary>
        public static void Load() => Ensure();

        /// <summary>The post-boss room a win in that boss room leads to; null for any other room.</summary>
        public static ExitRoom? AfterBoss(long bossRoomMapId)
        {
            Ensure();
            return _afterBoss.TryGetValue(bossRoomMapId, out var room) ? room : null;
        }

        /// <summary>The post-boss room that map is; null when it is none.</summary>
        public static ExitRoom? At(long mapId)
        {
            Ensure();
            return _byRoom.TryGetValue(mapId, out var room) ? room : null;
        }

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Read();
                }
                finally
                {
                    _loaded = true;   // a missing file counts as tried
                }
            }
        }

        private static void Read()
        {
            string path = Paths.ContentFile(File);
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine($"[Dungeons] {File} is not there: no dungeon has a room after its boss.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("exits", out var list) || list.ValueKind != JsonValueKind.Array) return;

                foreach (var row in list.EnumerateArray())
                {
                    var exit = new ExitRoom
                    {
                        Dungeon = (int)Number(row, "dungeon"),
                        BossRoom = Number(row, "bossRoom"),
                        MapId = Number(row, "room"),
                        Cell = (int)Number(row, "cell"),
                        Npc = (int)Number(row, "npc"),
                        Replies = row.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array
                            ? replies.EnumerateArray().Where(r => r.TryGetInt64(out _)).Select(r => r.GetInt64()).ToHashSet()
                            : new HashSet<long>(),
                    };
                    if (exit.Dungeon == 0 || exit.BossRoom == 0 || exit.MapId == 0) continue;
                    _afterBoss[exit.BossRoom] = exit;
                    _byRoom[exit.MapId] = exit;
                }
                Console.WriteLine($"[Dungeons] {_byRoom.Count} room(s) after a boss.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dungeons] Could not read {File}: {ex.Message}");
            }
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
