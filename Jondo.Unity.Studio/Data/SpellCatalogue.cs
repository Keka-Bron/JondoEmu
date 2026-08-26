using System;
using System.Collections.Generic;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;
using Jondo.Unity.World.Combat;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>One line of what a spell does.</summary>
    public sealed class SpellEffectInfo
    {
        public int EffectId { get; init; }

        /// <summary>The catalogue's own words for this effect, in the language in use.</summary>
        public string Description { get; init; } = "";

        public int DiceNum { get; init; }
        public int DiceSide { get; init; }
        public int Value { get; init; }

        public int Duration { get; init; }

        /// <summary>Turns before it goes off. Non-zero is a trap or a bomb.</summary>
        public int Delay { get; init; }

        /// <summary>Who it can land on, as the client writes it: "a,A" and so on.</summary>
        public string TargetMask { get; init; } = "";

        /// <summary>The shape letter, as a character code. See <see cref="Jondo.Unity.World.Maps.Zone"/>.</summary>
        public int ZoneShape { get; init; }

        public int ZoneSize { get; init; }

        public bool Critical { get; init; }

        /// <summary>
        /// What the fight engine will do with it — the only thing on this screen that says whether
        /// the spell works at all.
        /// </summary>
        public EffectSupportKind Support { get; init; }

        /// <summary>True when nothing happens, however good the card looks.</summary>
        public bool DoesNothing => Support == EffectSupportKind.PanelOnly;

        /// <summary>The dice as they read on a spell card: "13 to 234", or a flat number.</summary>
        public string Roll
        {
            get
            {
                if (DiceNum == 0 && DiceSide == 0) return Value != 0 ? Value.ToString() : "";
                if (DiceSide == 0) return DiceNum.ToString();
                return $"{DiceNum} – {DiceNum * DiceSide}";
            }
        }
    }

    /// <summary>One grade of one spell: what it costs, how far it reaches, what it does.</summary>
    public sealed class SpellLevelInfo
    {
        public int Id { get; init; }
        public int SpellId { get; init; }
        public int Grade { get; init; }
        public int ApCost { get; init; }
        public int MinRange { get; init; }
        public int MaxRange { get; init; }
        public bool CastInLine { get; init; }
        public bool NeedsSight { get; init; }
        public int MaxPerTurn { get; init; }
        public int MaxPerTarget { get; init; }

        public IReadOnlyList<SpellEffectInfo> Effects { get; init; } = Array.Empty<SpellEffectInfo>();

        /// <summary>
        /// True when the range is zero to zero, which means it can only be cast on the caster's own
        /// cell — and is the reason 1,555 spells were never being cast at all.
        /// </summary>
        public bool OnSelfOnly => MinRange == 0 && MaxRange == 0;
    }

    /// <summary>A spell, by name.</summary>
    public sealed class SpellSummary
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public IReadOnlyList<int> Grades { get; init; } = Array.Empty<int>();

        public override string ToString()
            => Name.Length == 0 ? Id.ToString() : $"{Name}  ·  {Grades.Count} ×";
    }

    /// <summary>
    /// The spells, and which monster knows which.
    /// </summary>
    /// <remarks>
    /// The spellbook is read the way the <em>server</em> reads it, out of
    /// <c>MonsterTemplates.Data</c>, and that matters: the <c>Spells</c> column on the
    /// <c>Monsters</c> table is empty for all 5,134 of them — the importer never filled it in — so
    /// an editor that trusted it would have shown every monster in the game as having nothing to
    /// cast. Measured over <c>MonsterTemplates</c>: <b>4,763 monsters carry spells and 371 do
    /// not</b>, and those 371 are the content bug you cannot see by playing, because a monster with
    /// an empty spellbook takes its turn and does nothing.
    ///
    /// <c>spellGrades</c> sits alongside <c>spells</c> and says, per monster grade, which grade of
    /// that spell applies: <c>"1,16;1,17;..."</c> is one <c>grade,level</c> pair per monster grade.
    /// </remarks>
    public sealed class SpellCatalogue : IDisposable
    {
        private readonly SqliteConnection? _world;
        private readonly ClientText? _text;
        private readonly Dictionary<int, string> _effectNames = new Dictionary<int, string>();

        /// <summary>Effect id to what the engine does with it.</summary>
        private readonly Dictionary<int, EffectSupportKind> _support
            = new Dictionary<int, EffectSupportKind>();

        /// <summary>How many of the game's effects fall in each bucket. For the heading.</summary>
        public (int Direct, int Characteristic, int PanelOnly) Coverage { get; private set; }
        private List<SpellSummary>? _all;

        public SpellCatalogue(ClientText? text = null, Action<string>? report = null)
        {
            _text = text;

            try
            {
                _world = new SqliteConnection(Paths.WorldConnectionString + ";Mode=ReadOnly");
                _world.Open();
                ReadEffectNames();
            }
            catch (Exception ex)
            {
                report?.Invoke($"world.db could not be opened: {ex.Message}");
                _world = null;
            }
        }

        public bool Ready => _world != null;

        private void ReadEffectNames()
        {
            if (_world == null) return;

            using var command = _world.CreateCommand();
            command.CommandText =
                "SELECT Id, DescriptionId, Description, Characteristic, Category FROM Effects;";

            int direct = 0, characteristic = 0, panelOnly = 0;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                int id = reader.GetInt32(0);

                // The client's own table first, so the words follow the language; the one baked
                // into world.db is the fallback and speaks whichever language was extracted.
                string said = "";
                if (!reader.IsDBNull(1) && _text != null) said = _text.Of(reader.GetInt64(1));
                if (said.Length == 0 && !reader.IsDBNull(2)) said = reader.GetString(2);

                if (said.Length > 0) _effectNames[id] = said;

                // Classified by the same code the engine uses, so the two cannot drift.
                var kind = EffectSupport.Classify(id,
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4));

                _support[id] = kind;
                if (kind == EffectSupportKind.Direct) direct++;
                else if (kind == EffectSupportKind.Characteristic) characteristic++;
                else panelOnly++;
            }

            Coverage = (direct, characteristic, panelOnly);
        }

        /// <summary>An effect that does nothing, and how much of the game it takes with it.</summary>
        public sealed class DeadEffect
        {
            public int EffectId { get; init; }
            public string Description { get; init; } = "";

            /// <summary>How many spell levels carry it.</summary>
            public int Levels { get; init; }

            /// <summary>How many distinct spells carry it.</summary>
            public int Spells { get; init; }
        }

        private List<DeadEffect>? _dead;

        /// <summary>
        /// The effects the engine does nothing with, worst first.
        /// </summary>
        /// <remarks>
        /// This is a work list, not a curiosity. Measured over the 34,823 spell levels: 647 of the
        /// game's 872 effects do nothing, and <b>15,841 levels — 45% — carry at least one of
        /// them</b>. Sorted by how many levels each one breaks, the top of the list is where an
        /// afternoon of engine work buys the most game: effect 1160 alone appears on 6,709 levels.
        ///
        /// Effect 108, healing, is on 751 of them, and is the one that makes the point: the card
        /// says the spell heals and nobody's life goes up.
        ///
        /// It walks every level's JSON, so it is worked out once and kept.
        /// </remarks>
        public List<DeadEffect> Dead()
        {
            if (_dead != null) return _dead;

            _dead = new List<DeadEffect>();
            if (_world == null) return _dead;

            var levels = new Dictionary<int, int>();
            var spells = new Dictionary<int, HashSet<int>>();

            using (var command = _world.CreateCommand())
            {
                command.CommandText =
                    "SELECT SpellId, EffectsJson, CriticalEffectsJson FROM SpellLevels;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int spell = reader.GetInt32(0);

                    for (int column = 1; column <= 2; column++)
                    {
                        if (reader.IsDBNull(column)) continue;
                        Count(reader.GetString(column), spell, levels, spells);
                    }
                }
            }

            foreach (var pair in levels)
            {
                _dead.Add(new DeadEffect
                {
                    EffectId = pair.Key,
                    Description = _effectNames.TryGetValue(pair.Key, out string? said) ? said : "",
                    Levels = pair.Value,
                    Spells = spells.TryGetValue(pair.Key, out var which) ? which.Count : 0,
                });
            }

            _dead.Sort((a, b) => b.Levels.CompareTo(a.Levels));
            return _dead;
        }

        private void Count(string json, int spell, Dictionary<int, int> levels,
                           Dictionary<int, HashSet<int>> spells)
        {
            if (json.Length == 0) return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

                foreach (var effect in doc.RootElement.EnumerateArray())
                {
                    int id = Int(effect, "effectId");
                    if (id == 0) continue;

                    if (!_support.TryGetValue(id, out var kind)) kind = EffectSupport.Classify(id, 0, 0);
                    if (kind != EffectSupportKind.PanelOnly) continue;

                    levels.TryGetValue(id, out int already);
                    levels[id] = already + 1;

                    if (!spells.TryGetValue(id, out var which)) spells[id] = which = new HashSet<int>();
                    which.Add(spell);
                }
            }
            catch (JsonException)
            {
                // One unreadable level is one row missing from a work list, not a lost screen.
            }
        }

        /// <summary>Every spell, by name.</summary>
        public List<SpellSummary> All()
        {
            if (_all != null) return _all;

            _all = new List<SpellSummary>();
            if (_world == null) return _all;

            var grades = new Dictionary<int, List<int>>();
            using (var command = _world.CreateCommand())
            {
                command.CommandText = "SELECT SpellId, Grade FROM SpellLevels ORDER BY SpellId, Grade;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int spell = reader.GetInt32(0);
                    if (!grades.TryGetValue(spell, out var list))
                    {
                        list = new List<int>();
                        grades[spell] = list;
                    }

                    list.Add(reader.GetInt32(1));
                }
            }

            using (var command = _world.CreateCommand())
            {
                command.CommandText = @"
                    SELECT s.Id, s.NameId, t.Text
                    FROM Spells s
                    LEFT JOIN Translations t ON t.Key = CAST(s.NameId AS TEXT);";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string name = "";
                    if (!reader.IsDBNull(1) && _text != null) name = _text.Of(reader.GetInt64(1));
                    if (name.Length == 0 && !reader.IsDBNull(2)) name = reader.GetString(2);

                    _all.Add(new SpellSummary
                    {
                        Id = id,
                        Name = name,
                        Grades = grades.TryGetValue(id, out var has) ? has : Array.Empty<int>(),
                    });
                }
            }

            _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return _all;
        }

        /// <summary>
        /// What one monster can cast, as (spell, grade) pairs for its first grade.
        /// </summary>
        public List<(int SpellId, int Grade)> Of(int monsterId)
        {
            var known = new List<(int, int)>();
            if (_world == null) return known;

            using var command = _world.CreateCommand();
            command.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", monsterId);

            string? json = command.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(json)) return known;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("spells", out var spells)) return known;
                if (!spells.TryGetProperty("Array", out var ids)) return known;

                JsonElement.ArrayEnumerator? gradeRows = null;
                if (doc.RootElement.TryGetProperty("spellGrades", out var sg) &&
                    sg.TryGetProperty("Array", out var gradeArray))
                {
                    gradeRows = gradeArray.EnumerateArray();
                }

                var grades = new List<string>();
                if (gradeRows != null)
                {
                    foreach (var row in gradeRows) grades.Add(row.GetString() ?? "");
                }

                int at = 0;
                foreach (var id in ids.EnumerateArray())
                {
                    if (!id.TryGetInt32(out int spellId)) { at++; continue; }
                    known.Add((spellId, FirstGrade(at < grades.Count ? grades[at] : "")));
                    at++;
                }
            }
            catch (JsonException)
            {
                // One unreadable template is one monster with no spells on screen, not a crash.
            }

            return known;
        }

        /// <summary>
        /// The spell grade the monster's first grade uses, out of "1,16;1,17;…".
        /// </summary>
        /// <remarks>
        /// Each entry is <c>monsterGrade,spellGrade</c>. Taking the first is the right default here
        /// because the editor shows one grade at a time and the first is the one everybody meets.
        /// </remarks>
        private static int FirstGrade(string spellGrades)
        {
            if (spellGrades.Length == 0) return 1;

            int semicolon = spellGrades.IndexOf(';');
            string first = semicolon < 0 ? spellGrades : spellGrades[..semicolon];

            int comma = first.IndexOf(',');
            if (comma < 0 || comma + 1 >= first.Length) return 1;

            return int.TryParse(first[(comma + 1)..], out int grade) ? grade : 1;
        }

        /// <summary>One grade of one spell, with its effects read out.</summary>
        public SpellLevelInfo? Level(int spellId, int grade)
        {
            if (_world == null) return null;

            using var command = _world.CreateCommand();
            command.CommandText = @"
                SELECT Id, Grade, APCost, MinRange, MaxRange, CastInLine, CastTestLos,
                       MaxCastPerTurn, MaxCastPerTarget, EffectsJson, CriticalEffectsJson
                FROM SpellLevels
                WHERE SpellId = $spell AND Grade = $grade
                LIMIT 1;";
            command.Parameters.AddWithValue("$spell", spellId);
            command.Parameters.AddWithValue("$grade", grade);

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            var effects = new List<SpellEffectInfo>();
            Read(reader.IsDBNull(9) ? "" : reader.GetString(9), effects, critical: false);
            Read(reader.IsDBNull(10) ? "" : reader.GetString(10), effects, critical: true);

            return new SpellLevelInfo
            {
                Id = reader.GetInt32(0),
                SpellId = spellId,
                Grade = reader.GetInt32(1),
                ApCost = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                MinRange = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                MaxRange = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                CastInLine = !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
                NeedsSight = !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
                MaxPerTurn = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                MaxPerTarget = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                Effects = effects,
            };
        }

        private void Read(string json, List<SpellEffectInfo> into, bool critical)
        {
            if (json.Length == 0) return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

                foreach (var effect in doc.RootElement.EnumerateArray())
                {
                    int id = Int(effect, "effectId");
                    int shape = 0;
                    int size = 0;

                    if (effect.TryGetProperty("zoneDescr", out var zone))
                    {
                        shape = Int(zone, "shape");
                        size = Int(zone, "param1");
                    }

                    into.Add(new SpellEffectInfo
                    {
                        EffectId = id,
                        Description = _effectNames.TryGetValue(id, out string? said) ? said : "",
                        DiceNum = Int(effect, "diceNum"),
                        DiceSide = Int(effect, "diceSide"),
                        Value = Int(effect, "value"),
                        Duration = Int(effect, "duration"),
                        Delay = Int(effect, "delay"),
                        TargetMask = effect.TryGetProperty("targetMask", out var mask)
                            ? mask.GetString() ?? "" : "",
                        ZoneShape = shape,
                        ZoneSize = size,
                        Critical = critical,

                        // An effect the catalogue has never heard of is one the engine has never
                        // heard of either.
                        Support = _support.TryGetValue(id, out var kind)
                            ? kind : EffectSupport.Classify(id, 0, 0),
                    });
                }
            }
            catch (JsonException)
            {
                // One unreadable spell is one row missing, not a lost screen.
            }
        }

        private static int Int(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt32(out int number) ? number : 0;

        public void Dispose() => _world?.Dispose();
    }
}
