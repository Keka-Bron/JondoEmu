using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The spells a fight against a monster lends its enemies for the fight
    /// (content/monsters/lent_spells.json).
    /// </summary>
    /// <remarks>
    /// The client's data has the spell and nobody casting it: the Crujidor de hielo (3370) wraps
    /// himself in Cura Glacial (3443), Invulnerable for good, and the only spell that takes it off,
    /// Grieta (3449, 952 of state 56 on «A,F3370»), is in no monster's list and no class's. «Chaque
    /// personnage reçoit au début du combat le sort Craquelure» (JeuxOnLine, «Lumière sur
    /// l'ombre»): without it the Crujidor was hit for 29 rounds and never lost a point of life.
    /// Each row here is written from its guide.
    /// </remarks>
    public static class MonsterLentSpells
    {
        public const string File = "monsters/lent_spells.json";

        private static List<(int Monster, int Spell, int Grade)> _rows;
        private static readonly object _lock = new object();

        /// <summary>The spells lent for a fight against this monster, with their grade.</summary>
        public static IReadOnlyList<(int Spell, int Grade)> For(int monster)
        {
            Ensure();
            return _rows.Where(r => r.Monster == monster).Select(r => (r.Spell, r.Grade)).ToList();
        }

        /// <summary>
        /// At the start of the fight: every character facing one of these monsters gets its spells
        /// on his bar, as a spell given for the fight (<see cref="Fighter.GrantedSpells"/>). It runs
        /// before the opening bursts, which carry the bar.
        /// </summary>
        public static void OnFightStarts(FightInstance fight)
        {
            if (fight == null) return;
            Ensure();
            if (_rows.Count == 0) return;
            var fighters = fight.Todos.Where(f => f != null).ToList();
            foreach (var monster in fighters.Where(f => f.IsMonster && f.MonsterId != 0))
            {
                foreach (var (spell, grade) in For(monster.MonsterId))
                {
                    foreach (var character in fighters.Where(f => f.IsCharacter && f.TeamId != monster.TeamId))
                    {
                        character.GrantedSpells.RemoveAll(g => g.Spell == spell);
                        character.GrantedSpells.Add((spell, grade));
                        character.TakenSpells.Remove(spell);
                    }
                }
            }
        }

        /// <summary>Reads the file again, for a content reload.</summary>
        public static void Reload()
        {
            lock (_lock) _rows = null;
            Ensure();
        }

        private static void Ensure()
        {
            if (_rows != null) return;
            lock (_lock)
            {
                if (_rows != null) return;
                var rows = new List<(int, int, int)>();
                string path = Paths.ContentFile(File);
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        rows = Parse(System.IO.File.ReadAllText(path));
                        Console.WriteLine($"[LentSpells] {rows.Count} spell(s) lent for fights against a monster.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LentSpells] Could not read {File}: {ex.Message}");
                }
                _rows = rows;
            }
        }

        /// <summary>The rows of the file's text; those without a monster or a spell are left out.</summary>
        public static List<(int Monster, int Spell, int Grade)> Parse(string json)
        {
            var rows = new List<(int, int, int)>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lent", out var list) || list.ValueKind != JsonValueKind.Array) return rows;
            foreach (var row in list.EnumerateArray())
            {
                int monster = row.TryGetProperty("monster", out var m) && m.TryGetInt32(out int mv) ? mv : 0;
                int spell = row.TryGetProperty("spell", out var s) && s.TryGetInt32(out int sv) ? sv : 0;
                int grade = row.TryGetProperty("grade", out var g) && g.TryGetInt32(out int gv) ? gv : 1;
                if (monster > 0 && spell > 0) rows.Add((monster, spell, Math.Max(1, grade)));
            }
            return rows;
        }
    }
}
