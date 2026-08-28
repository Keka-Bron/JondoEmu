using System;
using System.Collections.Generic;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>A monster, by name, with how many grades it has.</summary>
    public sealed class MonsterSummary
    {
        public int Id { get; init; }

        public string Name { get; init; } = "";

        /// <summary>
        /// Which drawing it uses. Several monsters share one, and it is what the picture is filed
        /// under - never the monster's own id.
        /// </summary>
        public int GfxId { get; init; }

        /// <summary>How many grades its template declares. The client accepts at most five.</summary>
        public int Grades { get; init; }

        /// <summary>The level of its first grade, which is what "how hard is it" usually means.</summary>
        public int Level { get; init; }

        /// <summary>True when nothing it knows can be cast. 401 monsters are in this state.</summary>
        public bool Toothless { get; init; }

        public override string ToString()
            => Name.Length == 0 ? Id.ToString() : $"{Name}  ·  level {Level}" + (Toothless ? "  ·  no spells" : "");
    }

    /// <summary>A group of monsters as Ankama placed it.</summary>
    public sealed class MeasuredGroup
    {
        public long MapId { get; init; }

        public long GroupId { get; init; }

        public int Cell { get; init; }

        public IReadOnlyList<(int Monster, int Grade)> Members { get; init; }
            = Array.Empty<(int, int)>();
    }

    /// <summary>
    /// The monsters and where Ankama put them.
    /// </summary>
    /// <remarks>
    /// Read a map at a time. There are 38,744 groups across 12,907 maps, and nothing anybody does
    /// on this screen needs more than one map's worth at once.
    ///
    /// <see cref="MonsterSummary.Toothless"/> is here because it is the content bug you cannot see
    /// by playing: a monster with no spells joins a fight, takes its turn and does nothing, and it
    /// looks exactly like a monster that decided not to attack. 401 of the 5,134 are in that state.
    /// Putting it on the picker means a group can be built without walking into it.
    /// </remarks>
    public sealed class MonsterCatalogue : IDisposable
    {
        private readonly SqliteConnection? _world;

        /// <summary>The client's own text table, so a monster is named in the language in use.</summary>
        private readonly ClientText? _text;

        public MonsterCatalogue(ClientText? text = null, Action<string>? report = null)
        {
            _text = text;

            try
            {
                _world = new SqliteConnection(Paths.WorldConnectionString + ";Mode=ReadOnly");
                _world.Open();
            }
            catch (Exception ex)
            {
                report?.Invoke($"world.db could not be opened: {ex.Message}");
                _world = null;
            }
        }

        public bool Ready => _world != null;

        /// <summary>Every monster, by name.</summary>
        public List<MonsterSummary> All()
        {
            var all = new List<MonsterSummary>();
            if (_world == null) return all;

            using var command = _world.CreateCommand();
            command.CommandText = @"
                SELECT m.Id, m.NameId, t.Text, m.Grades, x.Data
                FROM Monsters m
                LEFT JOIN Translations t ON t.Key = CAST(m.NameId AS TEXT)
                LEFT JOIN MonsterTemplates x ON x.Id = m.Id;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string grades = reader.IsDBNull(3) ? "" : reader.GetString(3);
                // NOT the Spells column on Monsters: it is empty for all 5,134 of them, so
                // reading it marked every monster in the game as having nothing to cast. The
                // spellbook the server actually uses is in MonsterTemplates.Data, and measured
                // there it is 4,763 monsters with spells and 371 without.
                string spells = reader.IsDBNull(4) ? "" : reader.GetString(4);

                all.Add(new MonsterSummary
                {
                    Id = reader.GetInt32(0),
                    Name = Named(reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                                 reader.IsDBNull(2) ? "" : reader.GetString(2)),
                    Grades = CountGrades(grades, out int level),
                    Level = level,
                    GfxId = GfxOf(spells),
                    Toothless = !HasSpells(spells),
                });
            }

            all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return all;
        }

        /// <summary>The groups Ankama put on one map.</summary>
        /// <summary>Monster id to the drawing it uses, so a group can be given a face.</summary>
        public Dictionary<int, int> GfxByMonster()
        {
            var map = new Dictionary<int, int>();
            foreach (var monster in All())
            {
                if (monster.GfxId > 0) map[monster.Id] = monster.GfxId;
            }

            return map;
        }

        public List<MeasuredGroup> GroupsOn(long mapId)
        {
            var groups = new List<MeasuredGroup>();
            if (_world == null) return groups;

            using var command = _world.CreateCommand();
            command.CommandText =
                "SELECT MobId, CellId, MembersJson FROM MapMobs WHERE MapId = $map ORDER BY MobId DESC;";
            command.Parameters.AddWithValue("$map", mapId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                groups.Add(new MeasuredGroup
                {
                    MapId = mapId,
                    GroupId = reader.GetInt64(0),
                    Cell = reader.GetInt32(1),
                    Members = ReadMembers(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                });
            }

            return groups;
        }

        /// <summary>The name in the language in use, falling back to the one baked into world.db.</summary>
        private string Named(long nameId, string fallback)
        {
            if (nameId != 0 && _text != null)
            {
                string said = _text.Of(nameId);
                if (said.Length > 0) return said;
            }

            return fallback;
        }

        /// <summary>The drawing a monster uses, out of its template.</summary>
        private static int GfxOf(string templateJson)
        {
            if (templateJson.Length == 0) return 0;

            try
            {
                using var doc = JsonDocument.Parse(templateJson);
                return doc.RootElement.TryGetProperty("gfxId", out var gfx) && gfx.TryGetInt32(out int id)
                    ? id : 0;
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        /// <summary>Whether a monster template declares any spell at all.</summary>
        private static bool HasSpells(string templateJson)
        {
            if (templateJson.Length == 0) return false;

            try
            {
                using var doc = JsonDocument.Parse(templateJson);
                if (!doc.RootElement.TryGetProperty("spells", out var spells)) return false;
                if (!spells.TryGetProperty("Array", out var array)) return false;
                return array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static List<(int Monster, int Grade)> ReadMembers(string json)
        {
            var members = new List<(int, int)>();
            if (json.Length == 0) return members;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return members;

                foreach (var member in doc.RootElement.EnumerateArray())
                {
                    int id = member.TryGetProperty("id", out var i) && i.TryGetInt32(out int monster) ? monster : 0;
                    int grade = member.TryGetProperty("grade", out var g) && g.TryGetInt32(out int which) ? which : 0;
                    if (id != 0) members.Add((id, grade));
                }
            }
            catch (JsonException)
            {
                // One unreadable group is one row missing, not a reason to lose the map.
            }

            return members;
        }

        private static int CountGrades(string json, out int level)
        {
            level = 0;
            if (json.Length == 0) return 0;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Array", out var array)) return 0;
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0) return 0;

                if (array[0].TryGetProperty("level", out var first) && first.TryGetInt32(out int value))
                {
                    level = value;
                }

                return array.GetArrayLength();
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        public void Dispose() => _world?.Dispose();
    }
}
