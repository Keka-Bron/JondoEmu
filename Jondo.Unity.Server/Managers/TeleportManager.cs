using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>Une route de téléportation instantanée attachée à un élément de map.</summary>
    public sealed class InteractiveTeleport
    {
        public long SourceMapId { get; init; }
        public int ElementId { get; init; }
        public int SourceCellId { get; init; }
        public int GfxId { get; init; }
        public int InteractiveType { get; init; }
        public int SkillId { get; init; }
        public long DestinationMapId { get; init; }
        public int DestinationCellId { get; init; }
        public string SourceVersion { get; init; } = "";
        public string Confidence { get; init; } = "";
    }

    /// <summary>
    /// Importe, valide et indexe les téléporteurs issus de Giny 2.68.
    ///
    /// Le JSON normalisé est la source versionnée. SQLite est la copie de travail interrogée par
    /// le serveur. Les maisons sont expressément refusées ici : leur protocole jqw et leur état de
    /// retour appartiennent à <see cref="Houses"/> et <c>HouseHandler</c>.
    /// </summary>
    public static class TeleportManager
    {
        public const int UseSkill = 114;
        public const int DefaultType = 0;

        private static IReadOnlyDictionary<(long MapId, int ElementId), InteractiveTeleport> _byElement =
            new Dictionary<(long, int), InteractiveTeleport>();
        private static IReadOnlyDictionary<long, IReadOnlyList<InteractiveTeleport>> _byMap =
            new Dictionary<long, IReadOnlyList<InteractiveTeleport>>();

        public static int Count => _byElement.Count;
        public static IEnumerable<InteractiveTeleport> All => _byElement.Values;

        public static void Initialize()
        {
            ImportIfAvailable();
            LoadFromDatabase();
            Console.WriteLine($"[Teleport] {_byElement.Count} rutas activas cargadas.");
        }

        public static bool TryGet(long mapId, int elementId, out InteractiveTeleport route)
            => _byElement.TryGetValue((mapId, elementId), out route!);

        public static IReadOnlyList<InteractiveTeleport> On(long mapId)
            => _byMap.TryGetValue(mapId, out var routes)
                ? routes
                : Array.Empty<InteractiveTeleport>();

        private sealed class ImportRow
        {
            public required InteractiveTeleport Route { get; init; }
            public bool RequestedEnabled { get; init; }
            public bool Enabled { get; set; }
            public string ValidationStatus { get; set; } = "pending";
        }

        private static void ImportIfAvailable()
        {
            string path = Paths.InteractiveTeleportsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Teleport] Falta {path}; se conserva el catálogo SQLite existente.");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
                    throw new InvalidOperationException("schemaVersion distinto de 1.");
                if (!root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("La propiedad routes no es una lista.");

                var rows = new List<ImportRow>();
                int housesSkipped = 0;
                foreach (var entry in routes.EnumerateArray())
                {
                    var route = Read(entry);
                    if (IsHouse(route.SourceMapId, route.ElementId))
                    {
                        housesSkipped++;
                        continue;
                    }
                    rows.Add(new ImportRow
                    {
                        Route = route,
                        RequestedEnabled = entry.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean()
                    });
                }

                var ambiguous = rows
                    .Where(x => x.RequestedEnabled)
                    .GroupBy(x => (x.Route.SourceMapId, x.Route.ElementId))
                    .Where(x => x.Count() > 1)
                    .Select(x => x.Key)
                    .ToHashSet();

                int enabledCount = 0;
                foreach (var row in rows)
                {
                    var errors = Validate(row.Route);
                    if (!row.RequestedEnabled &&
                        string.Equals(row.Route.Confidence, "ambiguous", StringComparison.OrdinalIgnoreCase))
                        errors.Add("ambiguous-source");
                    if (ambiguous.Contains((row.Route.SourceMapId, row.Route.ElementId)))
                        errors.Add("ambiguous-source");
                    row.Enabled = row.RequestedEnabled && errors.Count == 0;
                    row.ValidationStatus = errors.Count == 0 ? "ok" : string.Join(",", errors);
                    if (row.Enabled) enabledCount++;
                }

                ReplaceDatabase(rows);
                Console.WriteLine($"[Teleport] Import JSON: {rows.Count} rutas, {enabledCount} activas, " +
                                  $"{housesSkipped} casas ignoradas.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Teleport] Importación cancelada; se conserva SQLite: {ex.Message}");
            }
        }

        private static InteractiveTeleport Read(JsonElement entry)
            => new InteractiveTeleport
            {
                SourceMapId = entry.GetProperty("sourceMapId").GetInt64(),
                ElementId = entry.GetProperty("elementId").GetInt32(),
                SourceCellId = entry.GetProperty("sourceCellId").GetInt32(),
                GfxId = entry.GetProperty("gfxId").GetInt32(),
                InteractiveType = entry.GetProperty("interactiveType").GetInt32(),
                SkillId = entry.GetProperty("skillId").GetInt32(),
                DestinationMapId = entry.GetProperty("destinationMapId").GetInt64(),
                DestinationCellId = entry.GetProperty("destinationCellId").GetInt32(),
                SourceVersion = entry.TryGetProperty("sourceVersion", out var source) ? source.GetString() ?? "" : "",
                Confidence = entry.TryGetProperty("confidence", out var confidence) ? confidence.GetString() ?? "" : ""
            };

        private static List<string> Validate(InteractiveTeleport route)
        {
            var errors = new List<string>();
            if (route.SourceMapId <= 0 || route.DestinationMapId <= 0) errors.Add("invalid-map");
            if (route.ElementId <= 0) errors.Add("invalid-element");
            if (route.DestinationCellId < 0 || route.DestinationCellId > 559) errors.Add("invalid-cell");
            if (route.InteractiveType != DefaultType) errors.Add("unexpected-type");
            if (route.SkillId != UseSkill) errors.Add("unexpected-skill");
            if (IsReservedInteractive(route.SourceMapId, route.ElementId))
                errors.Add("reserved-interactive");

            var element = Interactives.ByElementId(route.SourceMapId, route.ElementId);
            if (element.Id == 0) errors.Add("missing-source-element");
            else
            {
                if (element.Cell != route.SourceCellId) errors.Add("source-cell-mismatch");
                if (element.Gfx != route.GfxId) errors.Add("gfx-mismatch");
            }
            if (MapManager.GetMapInfo(route.DestinationMapId) == null) errors.Add("missing-destination-map");
            return errors;
        }

        private static bool IsHouse(long mapId, int elementId)
        {
            if (Houses.TryGetDoor(mapId, elementId, out _)) return true;
            return Houses.TryGetExit(mapId, out var exit) && exit.ElementId == elementId;
        }

        /// <summary>
        /// Un vieux « Teleport » Giny peut être un zaap, un zaapi ou un autre élément dont Jondo
        /// connaît maintenant le vrai protocole. Ces éléments restent dans leur manager spécialisé.
        /// </summary>
        private static bool IsReservedInteractive(long mapId, int elementId)
        {
            if (IsHouse(mapId, elementId)) return true;
            foreach (var element in Interactives.ZaapElements(mapId))
                if (element.Id == elementId) return true;
            if (Merkasako.ChestOf(mapId).Id == elementId) return true;
            if (Lottery.Of(mapId).Id == elementId) return true;
            foreach (var element in Zaapis.ElementsOn(mapId))
                if (element.Id == elementId) return true;
            foreach (var element in Bins.On(mapId))
                if (element.Id == elementId) return true;
            return false;
        }

        private static void ReplaceDatabase(IReadOnlyList<ImportRow> rows)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM InteractiveTeleports;";
                clear.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO InteractiveTeleports
                    (SourceMapId,ElementId,SourceCellId,GfxId,InteractiveType,SkillId,
                     DestinationMapId,DestinationCellId,SourceVersion,Confidence,ValidationStatus,Enabled)
                VALUES
                    ($source,$element,$sourceCell,$gfx,$type,$skill,
                     $destination,$destinationCell,$version,$confidence,$status,$enabled);";
            foreach (string name in new[] { "$source", "$element", "$sourceCell", "$gfx", "$type", "$skill",
                                             "$destination", "$destinationCell", "$version", "$confidence",
                                             "$status", "$enabled" })
                insert.Parameters.Add(new SqliteParameter(name, null));

            foreach (var row in rows)
            {
                var route = row.Route;
                insert.Parameters["$source"].Value = route.SourceMapId;
                insert.Parameters["$element"].Value = route.ElementId;
                insert.Parameters["$sourceCell"].Value = route.SourceCellId;
                insert.Parameters["$gfx"].Value = route.GfxId;
                insert.Parameters["$type"].Value = route.InteractiveType;
                insert.Parameters["$skill"].Value = route.SkillId;
                insert.Parameters["$destination"].Value = route.DestinationMapId;
                insert.Parameters["$destinationCell"].Value = route.DestinationCellId;
                insert.Parameters["$version"].Value = route.SourceVersion;
                insert.Parameters["$confidence"].Value = route.Confidence;
                insert.Parameters["$status"].Value = row.ValidationStatus;
                insert.Parameters["$enabled"].Value = row.Enabled ? 1 : 0;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private static void LoadFromDatabase()
        {
            var byElement = new Dictionary<(long, int), InteractiveTeleport>();
            var byMap = new Dictionary<long, List<InteractiveTeleport>>();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT SourceMapId,ElementId,SourceCellId,GfxId,InteractiveType,SkillId,
                       DestinationMapId,DestinationCellId,SourceVersion,Confidence
                FROM InteractiveTeleports WHERE Enabled=1 ORDER BY SourceMapId,ElementId;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var route = new InteractiveTeleport
                {
                    SourceMapId = reader.GetInt64(0), ElementId = reader.GetInt32(1),
                    SourceCellId = reader.GetInt32(2), GfxId = reader.GetInt32(3),
                    InteractiveType = reader.GetInt32(4), SkillId = reader.GetInt32(5),
                    DestinationMapId = reader.GetInt64(6), DestinationCellId = reader.GetInt32(7),
                    SourceVersion = reader.GetString(8), Confidence = reader.GetString(9)
                };
                if (!byElement.TryAdd((route.SourceMapId, route.ElementId), route))
                    throw new InvalidOperationException(
                        $"Dos teletransportes activos para {route.SourceMapId}/{route.ElementId}.");
                if (!byMap.TryGetValue(route.SourceMapId, out var list))
                    byMap.Add(route.SourceMapId, list = new List<InteractiveTeleport>());
                list.Add(route);
            }

            _byElement = byElement;
            _byMap = byMap.ToDictionary(x => x.Key, x => (IReadOnlyList<InteractiveTeleport>)x.Value);
        }
    }
}
