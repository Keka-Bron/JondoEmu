using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Server-owned Incarnam bindings imported from reviewed public world data. Client-owned
    /// map graphics, skills and recipes remain in their exact 3.6.10.10 catalogues; this layer
    /// supplies only the relationships the native client does not contain and rejects a binding
    /// whenever it disagrees with those catalogues.
    /// </summary>
    public static class IncarnamServerContent
    {
        public sealed class WorkshopBinding
        {
            public int SourceRecordId { get; init; }
            public long MapId { get; init; }
            public Interactives.Element Element { get; init; }
            public int InteractiveTypeId { get; init; }
            public int SkillId { get; init; }
            public int RecipeCount { get; init; }
        }

        public sealed class NpcSpawnBinding
        {
            public int SourceRecordId { get; init; }
            public int NpcId { get; init; }
            public long MapId { get; init; }
            public int CellId { get; init; }
            public int Orientation { get; init; }
        }

        private static IReadOnlyList<WorkshopBinding> _workshops = Array.Empty<WorkshopBinding>();
        private static IReadOnlyList<NpcSpawnBinding> _npcSpawns = Array.Empty<NpcSpawnBinding>();

        // Exact accepted rows from Giny.NETCore e9a6d560, cross-checked against the pinned
        // 3.6.10.10 NPC catalogue. Source row 42 is deliberately absent: it names NPC 2897,
        // which this client does not contain. Replacing it with Ganymed (7581) would fabricate a
        // different server-owned spawn, so a refreshed manifest must not be enough to admit it.
        private static readonly Dictionary<int, (int NpcId, long MapId, int CellId, int Orientation)>
            ReviewedNpcSpawnRows = new()
            {
                [20] = (2907, 153881600, 384, 3),
                [21] = (2936, 152835072, 262, 3),
                [43] = (2892, 154010883, 357, 3),
                [44] = (2885, 153357316, 205, 3),
            };

        public static IReadOnlyList<WorkshopBinding> Workshops => _workshops;
        public static IReadOnlyList<NpcSpawnBinding> NpcSpawns => _npcSpawns;

        public static void Initialize()
        {
            _workshops = ReadWorkshops(Require("incarnam-workshops-3.6.10.10",
                                               "interactive-workshop-bindings"));
            _npcSpawns = ReadNpcSpawns(Require("incarnam-npc-spawns-3.6.10.10",
                                               "npc-spawn-bindings"));
            Console.WriteLine($"[Incarnam] {_workshops.Count} verified workshop station(s) and " +
                              $"{_npcSpawns.Count} current NPC placement(s) loaded.");
        }

        private static JsonElement Require(string id, string kind)
        {
            if (!MechanicCatalog.TryGet(id, out MechanicCatalog.Definition definition) ||
                !string.Equals(definition.Status, "Verified", StringComparison.Ordinal) ||
                !string.Equals(definition.Kind, kind, StringComparison.Ordinal))
                throw new InvalidOperationException($"Missing verified mechanics document {id} ({kind}).");
            return definition.Document;
        }

        private static IReadOnlyList<WorkshopBinding> ReadWorkshops(JsonElement document)
        {
            JsonElement records = RequiredArray(document, "records");
            var result = new List<WorkshopBinding>();
            var unique = new HashSet<(long MapId, int ElementId)>();
            foreach (JsonElement row in records.EnumerateArray())
            {
                int source = Positive(row, "sourceRecordId");
                long mapId = PositiveInt64(row, "mapId");
                int elementId = Positive(row, "elementId");
                int cellId = NonNegative(row, "cellId");
                int gfxId = Positive(row, "gfxId");
                int typeId = Positive(row, "interactiveTypeId");
                int skillId = Positive(row, "skillId");
                int expectedRecipes = Positive(row, "recipeCount");

                if (!MapManager.Maps.ContainsKey(mapId))
                    throw new InvalidOperationException($"Workshop source row {source}: unknown map {mapId}.");
                Interactives.Element element = Interactives.ByElementId(mapId, elementId);
                if (element.Id != elementId || element.Cell != cellId || element.Gfx != gfxId)
                    throw new InvalidOperationException(
                        $"Workshop source row {source}: element {elementId} disagrees with map {mapId}.");
                if (!SkillManager.TryGet(skillId, out SkillDefinition skill) || skill.IsGathering)
                    throw new InvalidOperationException($"Workshop source row {source}: skill {skillId} is not a craft skill.");
                int actualRecipes = RecipeManager.ForSkill(skillId).Count;
                if (actualRecipes != expectedRecipes)
                    throw new InvalidOperationException(
                        $"Workshop source row {source}: skill {skillId} expected {expectedRecipes} recipes, got {actualRecipes}.");
                if (!unique.Add((mapId, elementId)))
                    throw new InvalidOperationException($"Duplicate workshop element {elementId} on map {mapId}.");

                result.Add(new WorkshopBinding
                {
                    SourceRecordId = source,
                    MapId = mapId,
                    Element = element,
                    InteractiveTypeId = typeId,
                    SkillId = skillId,
                    RecipeCount = actualRecipes,
                });
            }
            if (result.Count == 0) throw new InvalidOperationException("The Incarnam workshop document is empty.");
            return result;
        }

        private static IReadOnlyList<NpcSpawnBinding> ReadNpcSpawns(JsonElement document)
        {
            JsonElement records = RequiredArray(document, "records");
            var result = new List<NpcSpawnBinding>();
            var unique = new HashSet<(long MapId, int NpcId)>();
            var sources = new HashSet<int>();
            foreach (JsonElement row in records.EnumerateArray())
            {
                int source = Positive(row, "sourceRecordId");
                int npcId = Positive(row, "npcId");
                long mapId = PositiveInt64(row, "mapId");
                int cellId = NonNegative(row, "cellId");
                int orientation = NonNegative(row, "orientation");
                if (!MapManager.Maps.ContainsKey(mapId))
                    throw new InvalidOperationException($"NPC source row {source}: unknown map {mapId}.");
                if (cellId > 559)
                    throw new InvalidOperationException($"NPC source row {source}: invalid cell {cellId}.");
                if (orientation > 7)
                    throw new InvalidOperationException($"NPC source row {source}: invalid orientation {orientation}.");
                if (!ReviewedNpcSpawnRows.TryGetValue(source, out var expected) ||
                    expected.NpcId != npcId || expected.MapId != mapId ||
                    expected.CellId != cellId || expected.Orientation != orientation)
                    throw new InvalidOperationException(
                        $"NPC source row {source}: not an exact reviewed 3.6.10.10 binding.");
                if (!sources.Add(source))
                    throw new InvalidOperationException($"Duplicate NPC source row {source}.");
                if (!unique.Add((mapId, npcId)))
                    throw new InvalidOperationException($"Duplicate NPC {npcId} on map {mapId}.");
                result.Add(new NpcSpawnBinding
                {
                    SourceRecordId = source,
                    NpcId = npcId,
                    MapId = mapId,
                    CellId = cellId,
                    Orientation = orientation,
                });
            }
            if (sources.Count != ReviewedNpcSpawnRows.Count)
                throw new InvalidOperationException(
                    $"Expected {ReviewedNpcSpawnRows.Count} exact Incarnam NPC source rows, got {sources.Count}.");
            return result;
        }

        private static JsonElement RequiredArray(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Missing array {name}.");
            return property;
        }

        private static int Positive(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement property) || !property.TryGetInt32(out int parsed) || parsed <= 0)
                throw new InvalidOperationException($"Missing positive integer {name}.");
            return parsed;
        }

        private static long PositiveInt64(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement property) || !property.TryGetInt64(out long parsed) || parsed <= 0)
                throw new InvalidOperationException($"Missing positive integer {name}.");
            return parsed;
        }

        private static int NonNegative(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement property) || !property.TryGetInt32(out int parsed) || parsed < 0)
                throw new InvalidOperationException($"Missing non-negative integer {name}.");
            return parsed;
        }
    }
}
