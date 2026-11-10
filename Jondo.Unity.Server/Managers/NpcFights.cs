using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Fights a reply to an NPC starts, with who is in front, who fights beside the player and on
    /// which arena (content/fights/npc_fights.json).
    /// </summary>
    /// <remarks>
    /// The tutorial's last test: the celestial chafers of the combat room are NPCs, and «Empezar el
    /// combate contra los tres monstruos» (effect 911 beside it) puts the player on 241441797
    /// against a chafer explorer and two pikemen, with Master Darmas (monster 8018) on his side --
    /// fighter -1, 12 AP, 80 % resistances, in the jxg of the tutorial capture.
    /// </remarks>
    public static class NpcFights
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "fights/npc_fights.json";

        /// <summary>One fight a reply starts.</summary>
        public sealed class Fight
        {
            public int NpcId { get; init; }
            public long MapId { get; init; }
            public long Reply { get; init; }
            public long ArenaMapId { get; init; }
            public IReadOnlyList<(int Monster, int Grade)> Monsters { get; init; } = Array.Empty<(int, int)>();
            public IReadOnlyList<(int Monster, int Grade)> Allies { get; init; } = Array.Empty<(int, int)>();
        }

        private static List<Fight>? _fights;
        private static readonly object _lock = new();

        /// <summary>The fight this reply to this NPC starts here, or null.</summary>
        public static Fight? For(int npcId, long mapId, long reply)
        {
            Load();
            foreach (var fight in _fights!)
            {
                if (fight.NpcId == npcId && fight.Reply == reply && (fight.MapId == 0 || fight.MapId == mapId)) return fight;
            }
            return null;
        }

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _fights = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_fights != null) return;
                var fights = new List<Fight>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var row in doc.RootElement.GetProperty("fights").EnumerateArray())
                        {
                            fights.Add(new Fight
                            {
                                NpcId = (int)Long(row, "npc"),
                                MapId = Long(row, "map"),
                                Reply = Long(row, "reply"),
                                ArenaMapId = Long(row, "arena"),
                                Monsters = Members(row, "monsters"),
                                Allies = Members(row, "allies"),
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _fights = fights;
            }
        }

        private static List<(int, int)> Members(JsonElement row, string name)
        {
            var found = new List<(int, int)>();
            if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return found;
            foreach (var member in list.EnumerateArray())
            {
                int monster = (int)Long(member, "monster");
                if (monster != 0) found.Add((monster, (int)Long(member, "grade")));
            }
            return found;
        }

        private static long Long(JsonElement row, string name)
            => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
    }
}
