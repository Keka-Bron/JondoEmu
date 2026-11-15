using System;
using System.Collections.Generic;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// How many summons a monster controls at once, where a guide says so (content/monsters/summon_caps.json).
    /// </summary>
    /// <remarks>
    /// A monster has no summon capacity of its own in the client's data: its grades carry no
    /// characteristic 26 and its summon spells no state to check, so the fight lets it summon
    /// as often as its spells allow (<see cref="Handlers.FightHandler.SummonLimitFor"/>). Some
    /// bosses are held to one by Ankama's server all the same -- Master Cuerbok's crow, «Il ne
    /// peut y avoir qu'un seul Corbac invoqué sur le terrain» -- and nothing in the client says
    /// which: each cap here is written from its guide.
    /// </remarks>
    public static class MonsterSummonCaps
    {
        public const string File = "monsters/summon_caps.json";

        private static Dictionary<int, int> _caps;
        private static readonly object _gate = new object();

        /// <summary>The summon capacity of this monster template; 0 when nothing caps it.</summary>
        public static int For(int monster)
        {
            Ensure();
            return _caps.TryGetValue(monster, out int cap) ? cap : 0;
        }

        /// <summary>Reads the file again, for a content reload.</summary>
        public static void Reload()
        {
            lock (_gate) _caps = null;
            Ensure();
        }

        private static void Ensure()
        {
            if (_caps != null) return;
            lock (_gate)
            {
                if (_caps != null) return;
                var caps = new Dictionary<int, int>();
                string path = Paths.ContentFile(File);
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        foreach (var (monster, cap) in Parse(System.IO.File.ReadAllText(path))) caps[monster] = cap;
                        Console.WriteLine($"[SummonCaps] {caps.Count} monster(s) with a summon cap.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SummonCaps] Could not read {File}: {ex.Message}");
                }
                _caps = caps;
            }
        }

        /// <summary>The rows of the file's text; those without a monster or a positive cap are left out.</summary>
        public static List<(int Monster, int Cap)> Parse(string json)
        {
            var rows = new List<(int, int)>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("caps", out var list) || list.ValueKind != JsonValueKind.Array) return rows;
            foreach (var row in list.EnumerateArray())
            {
                int monster = row.TryGetProperty("monster", out var m) && m.TryGetInt32(out int mv) ? mv : 0;
                int cap = row.TryGetProperty("cap", out var c) && c.TryGetInt32(out int cv) ? cv : 0;
                if (monster > 0 && cap > 0) rows.Add((monster, cap));
            }
            return rows;
        }
    }
}
