using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The monster spells a guide says are only used while an enemy carries a state
    /// (content/monsters/spell_gates.json).
    /// </summary>
    /// <remarks>
    /// The client's data puts such a spell in the monster's list from the start, and its level
    /// asks nothing of the enemy: the Waddicts' Twagón (12216) asks only that its caster not be
    /// Pesante (HS!7). Yet «Lorsqu'un joueur est dans l'état Chocomatose, tous les monstres (sauf
    /// le Boss) gagnent un nouveau sort» (Dofus pour les noobs): the monsters jumped onto a
    /// character from the first round. Each gate here is written from its guide.
    /// </remarks>
    public static class MonsterSpellGates
    {
        public const string File = "monsters/spell_gates.json";

        private static Dictionary<int, int> _gates;
        private static readonly object _lock = new object();

        /// <summary>The state an enemy must carry for a monster to cast this spell; 0 when none.</summary>
        public static int StateFor(int spell)
        {
            Ensure();
            return _gates.TryGetValue(spell, out int state) ? state : 0;
        }

        /// <summary>
        /// Whether the monster may cast the spell now: it has no gate, or a living enemy of his
        /// carries the state it asks for.
        /// </summary>
        public static bool Open(FightInstance fight, Fighter monster, int spell)
        {
            int state = StateFor(spell);
            if (state == 0 || fight == null) return true;
            return fight.Todos.Any(f => f != null && f.IsAlive && f.TeamId != monster.TeamId
                                        && f.Buffs.Estados.Contains(state));
        }

        /// <summary>Reads the file again, for a content reload.</summary>
        public static void Reload()
        {
            lock (_lock) _gates = null;
            Ensure();
        }

        private static void Ensure()
        {
            if (_gates != null) return;
            lock (_lock)
            {
                if (_gates != null) return;
                var gates = new Dictionary<int, int>();
                string path = Paths.ContentFile(File);
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        foreach (var (spell, state) in Parse(System.IO.File.ReadAllText(path))) gates[spell] = state;
                        Console.WriteLine($"[SpellGates] {gates.Count} monster spell(s) gated on an enemy's state.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SpellGates] Could not read {File}: {ex.Message}");
                }
                _gates = gates;
            }
        }

        /// <summary>The rows of the file's text; those without a spell or a state are left out.</summary>
        public static List<(int Spell, int State)> Parse(string json)
        {
            var rows = new List<(int, int)>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("gates", out var list) || list.ValueKind != JsonValueKind.Array) return rows;
            foreach (var row in list.EnumerateArray())
            {
                int spell = row.TryGetProperty("spell", out var s) && s.TryGetInt32(out int sv) ? sv : 0;
                int state = row.TryGetProperty("enemyState", out var e) && e.TryGetInt32(out int ev) ? ev : 0;
                if (spell > 0 && state > 0) rows.Add((spell, state));
            }
            return rows;
        }
    }
}
