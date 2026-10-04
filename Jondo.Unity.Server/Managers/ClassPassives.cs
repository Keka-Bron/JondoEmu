using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The spells a class carries into every fight without anybody casting them: its passive,
    /// and the initial spell of each spell the character has that comes with one.
    /// </summary>
    /// <remarks>
    /// The real server casts them on the player between the jxb and the first turn, each in a
    /// sequence of its own, and they stay hooked on him with their turn triggers: the Tymador's
    /// "La Astucia del Tymador" (20488) is what puts state 2483 on him at his turn start -- the
    /// state the bomb walls double their damage on -- and hands two combos to every bomb he has
    /// out, and takes the state away at his turn end. None of that is written here: the table of
    /// which class carries which lives in <c>content/fights/class_passives.json</c>, measured
    /// capture by capture, and what the spells do comes out of the spell data like anything else.
    ///
    /// The initial spells of the character's own choices are found by their ICON: the class's
    /// initial spell type holds an "Explobomba" (25200) drawn with the same icon as the bomb
    /// spell Explobomba (13444), and an "Explobomba Resiliente" (24847) with the variant's, and
    /// the Tymador who knows the one is sent the other at his grade of it. The icon and not the
    /// name because the names drift -- "Tornabombas" and "Bombas de agua" on the spells, singular
    /// on their initial spells -- while the icon is the same in all six pairs measured: the four
    /// bombs and their four resilient variants on eleven Tymador fights, and the Cra's Flecha de
    /// Redención and Ojo por Ojo in seven of fourteen.
    /// </remarks>
    public static class ClassPassives
    {
        public const string AuthoredFile = "fights/class_passives.json";

        private sealed class ClassEntry
        {
            public int Passive;
            public int InitialSpellType;
        }

        private static readonly object _lock = new();
        private static Dictionary<int, ClassEntry> _classes;
        private static Dictionary<int, List<(int Spell, int Icon)>> _initialSpellsByType;
        private static Dictionary<int, int> _icons;

        /// <summary>The class's passive, or zero when the class has none on record.</summary>
        public static int PassiveOf(int breed)
        {
            Load();
            return _classes.TryGetValue(breed, out var entry) ? entry.Passive : 0;
        }

        /// <summary>
        /// The initial spells a character carries into a fight, in the order the real server casts
        /// them: one per known spell that shares its icon with a spell of the class's initial spell
        /// type, at the character's grade of the known spell, and then the class passive.
        /// </summary>
        public static List<(int Spell, int Grade)> ForFight(int breed, IEnumerable<(int Spell, int Grade)> known)
        {
            Load();
            var fuera = new List<(int, int)>();
            if (!_classes.TryGetValue(breed, out var entry)) return fuera;

            if (entry.InitialSpellType != 0 && _initialSpellsByType.TryGetValue(entry.InitialSpellType, out var initials))
            {
                foreach (var (spell, grade) in known)
                {
                    if (!_icons.TryGetValue(spell, out int icon) || icon == 0) continue;
                    foreach (var (initial, initialIcon) in initials)
                    {
                        if (initial == entry.Passive || initial == spell) continue;
                        if (initialIcon != icon) continue;
                        if (fuera.Any(f => f.Item1 == initial)) continue;
                        fuera.Add((initial, Math.Max(1, grade)));
                    }
                }
            }

            if (entry.Passive != 0) fuera.Add((entry.Passive, 1));
            return fuera;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_classes != null) return;
                var classes = new Dictionary<int, ClassEntry>();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Paths.ContentFile(AuthoredFile)));
                    foreach (var property in doc.RootElement.GetProperty("classes").EnumerateObject())
                    {
                        classes[int.Parse(property.Name)] = new ClassEntry
                        {
                            Passive = property.Value.TryGetProperty("passive", out var p) ? p.GetInt32() : 0,
                            InitialSpellType = property.Value.TryGetProperty("initialSpellType", out var t) ? t.GetInt32() : 0,
                        };
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ClassPassives] Could not read {AuthoredFile}: {ex.Message}");
                }
                _classes = classes;
                var types = classes.Values.Select(c => c.InitialSpellType).Where(t => t != 0).ToHashSet();
                _icons = new Dictionary<int, int>();
                _initialSpellsByType = new Dictionary<int, List<(int, int)>>();
                ReadTemplates(types);
            }
        }

        /// <summary>
        /// Every spell's icon, and the spells of the initial spell types by type, straight from
        /// the templates.
        /// </summary>
        private static void ReadTemplates(HashSet<int> types)
        {
            try
            {
                using var conexion = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conexion.Open();
                var orden = conexion.CreateCommand();
                orden.CommandText = "SELECT Id, Data FROM SpellTemplates;";
                using var lector = orden.ExecuteReader();
                while (lector.Read())
                {
                    int id = lector.GetInt32(0);
                    using var doc = JsonDocument.Parse(lector.IsDBNull(1) ? "{}" : lector.GetString(1));
                    int icon = doc.RootElement.TryGetProperty("iconId", out var i) ? i.GetInt32() : 0;
                    _icons[id] = icon;
                    if (!doc.RootElement.TryGetProperty("typeId", out var t)) continue;
                    int type = t.GetInt32();
                    if (!types.Contains(type)) continue;
                    if (!_initialSpellsByType.TryGetValue(type, out var list)) _initialSpellsByType[type] = list = new List<(int, int)>();
                    list.Add((id, icon));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClassPassives] Could not read the spell templates: {ex.Message}");
            }
        }
    }
}
