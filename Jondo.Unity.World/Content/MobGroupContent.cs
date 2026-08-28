using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jondo.Unity.World.Content
{
    /// <summary>One monster of a group: which it is, and how hard.</summary>
    public sealed class MobMemberSpec
    {
        public int MonsterId { get; init; }

        /// <summary>Which of its grades, from 0. The client accepts five and no more.</summary>
        public int Grade { get; init; }

        public override string ToString() => $"{MonsterId} grade {Grade}";
    }

    /// <summary>A group of monsters standing on a cell.</summary>
    public sealed class MobGroupSpawn
    {
        public long MapId { get; init; }

        /// <summary>Its id. Stable, because a tombstone has to be able to name it next time.</summary>
        public long GroupId { get; init; }

        public int Cell { get; init; }

        public IReadOnlyList<MobMemberSpec> Members { get; init; } = Array.Empty<MobMemberSpec>();

        public MobGroupKey Key => new MobGroupKey(MapId, GroupId);

        public override string ToString()
            => $"{Members.Count} monster(s) on map {MapId}, cell {Cell}";
    }

    /// <summary>Which group this is: one map, one id.</summary>
    public readonly struct MobGroupKey : IEquatable<MobGroupKey>
    {
        public MobGroupKey(long mapId, long groupId)
        {
            MapId = mapId;
            GroupId = groupId;
        }

        public long MapId { get; }

        public long GroupId { get; }

        public bool Equals(MobGroupKey other) => MapId == other.MapId && GroupId == other.GroupId;
        public override bool Equals(object? obj) => obj is MobGroupKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(MapId, GroupId);
        public override string ToString() => $"{MapId}/{GroupId}";
    }

    /// <summary>
    /// Monster groups put somewhere on purpose, and Ankama's own taken away.
    /// </summary>
    /// <remarks>
    /// The 38,744 groups in <c>world.db</c> are Ankama's placement and are regenerated with it, so
    /// neither adding to them nor removing from them can be done in that file. Both are wanted:
    /// custom content needs groups where the client's data has none, and the veto that keeps
    /// monsters out of houses and off zaaps is a rule, not a judgement about any particular group.
    ///
    /// The level of each member is deliberately <em>not</em> stored. It follows from the monster
    /// and the grade, the server already works it out, and writing it down would be a second copy
    /// of a derived number that goes stale the day a grade table changes.
    /// </remarks>
    public static class MobGroupContent
    {
        /// <summary>The authored file, relative to the content root.</summary>
        public const string AuthoredFile = "monsters/groups.json";

        /// <summary>
        /// Where ids for groups placed by hand start, going down.
        /// </summary>
        /// <remarks>
        /// The measured groups run from -1,000,000 to -1,038,743, and the runtime hands out more
        /// from the same band as maps are generated. Starting here leaves room for nearly a million
        /// more of Ankama's before the two could ever meet, and it means a glance at an id says
        /// which kind it is.
        /// </remarks>
        public const long FirstAuthoredId = -2_000_000;

        /// <summary>How many grades the client accepts. Anything past this is clamped by the server.</summary>
        public const int MaxGrade = 4;

        public static ContentStore<MobGroupKey, MobGroupSpawn> Load(string? authoredPath,
                                                                    Action<string>? report = null)
        {
            var store = new ContentStore<MobGroupKey, MobGroupSpawn>();
            if (string.IsNullOrEmpty(authoredPath) || !File.Exists(authoredPath)) return store;

            var from = Origin.Authored(Path.GetFileName(authoredPath));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authoredPath));
                if (!doc.RootElement.TryGetProperty("groups", out var list)) return store;

                foreach (var entry in list.EnumerateArray())
                {
                    long mapId = Number(entry, "map");
                    long groupId = Number(entry, "group");
                    if (mapId == 0 || groupId == 0) continue;

                    var key = new MobGroupKey(mapId, groupId);

                    if (entry.TryGetProperty("remove", out var gone) && gone.ValueKind == JsonValueKind.True)
                    {
                        store.Erase(key, from);
                        continue;
                    }

                    var members = new List<MobMemberSpec>();
                    if (entry.TryGetProperty("members", out var array) && array.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var member in array.EnumerateArray())
                        {
                            int monsterId = (int)Number(member, "monster");
                            if (monsterId == 0) continue;

                            members.Add(new MobMemberSpec
                            {
                                MonsterId = monsterId,
                                Grade = Math.Clamp((int)Number(member, "grade"), 0, MaxGrade),
                            });
                        }
                    }

                    if (members.Count == 0)
                    {
                        report?.Invoke($"[Content] the group {groupId} on map {mapId} has no monsters " +
                                       "in it; skipped.");
                        continue;
                    }

                    store.Put(key, new MobGroupSpawn
                    {
                        MapId = mapId,
                        GroupId = groupId,
                        Cell = (int)Number(entry, "cell"),
                        Members = members,
                    }, from);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {Path.GetFileName(authoredPath)} is unreadable: {ex.Message}");
            }

            return store;
        }

        /// <summary>An id no group in this set is using yet.</summary>
        public static long NextId(IEnumerable<MobGroupSpawn> already)
        {
            long lowest = FirstAuthoredId + 1;
            foreach (var group in already)
            {
                if (group.GroupId < lowest) lowest = group.GroupId;
            }

            return lowest - 1;
        }

        public static void Save(string path, IEnumerable<MobGroupSpawn> groups,
                                IEnumerable<MobGroupKey> removed, IEnumerable<string>? comment = null)
        {
            var ordered = new List<MobGroupSpawn>(groups);
            ordered.Sort((a, b) =>
            {
                int byMap = a.MapId.CompareTo(b.MapId);
                return byMap != 0 ? byMap : b.GroupId.CompareTo(a.GroupId);
            });

            var tombstones = new List<MobGroupKey>(removed);
            tombstones.Sort((a, b) =>
            {
                int byMap = a.MapId.CompareTo(b.MapId);
                return byMap != 0 ? byMap : b.GroupId.CompareTo(a.GroupId);
            });

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("_comment");
                writer.WriteStartArray();
                foreach (string line in comment ?? DefaultComment) writer.WriteStringValue(line);
                writer.WriteEndArray();

                writer.WritePropertyName("groups");
                writer.WriteStartArray();

                foreach (var group in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("map", group.MapId);
                    writer.WriteNumber("group", group.GroupId);
                    writer.WriteNumber("cell", group.Cell);

                    writer.WritePropertyName("members");
                    writer.WriteStartArray();
                    foreach (var member in group.Members)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("monster", member.MonsterId);
                        writer.WriteNumber("grade", member.Grade);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                foreach (var key in tombstones)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("map", key.MapId);
                    writer.WriteNumber("group", key.GroupId);
                    writer.WriteBoolean("remove", true);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            string temporary = path + ".writing";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }

        private static long Number(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;

        private static readonly string[] DefaultComment =
        {
            "Monster groups put somewhere on purpose, and Ankama's own taken away.",
            "",
            "The 38,744 groups in world.db are Ankama's placement and are regenerated with it, so",
            "neither adding to them nor removing from them can be done in that file.",
            "",
            "  { \"map\": 241438721, \"group\": -2000000, \"cell\": 327,",
            "    \"members\": [ { \"monster\": 2549, \"grade\": 0 } ] }",
            "  { \"map\": 241438721, \"group\": -1000000, \"remove\": true }",
            "",
            "Ids for groups placed by hand start at -2000000 and go down. The measured ones run",
            "from -1000000 to -1038743, so the two bands cannot meet and an id says which kind it",
            "is at a glance.",
            "",
            "The level of a member is not written down. It follows from the monster and the grade,",
            "the server works it out, and a second copy of a derived number goes stale the day a",
            "grade table changes.",
        };
    }
}
