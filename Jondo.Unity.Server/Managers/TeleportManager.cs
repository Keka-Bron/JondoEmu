using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>An instant passage from one map to another, hanging from a map element.</summary>
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
    /// Imports, validates and indexes the passages taken from Giny 2.68.
    ///
    /// The normalised json is the source that is versioned; SQLite is the working copy the
    /// server queries. It is reimported on EVERY start, so touching the table by hand is of no
    /// use: what rules is the json.
    ///
    /// Houses are rejected here on purpose: their jqw protocol and their return state belong to
    /// <see cref="Houses"/> and HouseHandler, and putting them through here would break them.
    ///
    /// Of 1,678 candidates 1,586 remain active. The ones that drop do so for what
    /// Validate says: 55 are ambiguous —two destinations for the same element— and 37 point to a map
    /// that does not exist in 3.6.10.10. That filter is what makes a Dofus 2 dump usable.
    /// </summary>
    public static class TeleportManager
    {
        public const int UseSkill = 114;
        public const int ExitSkill = 339;
        /// <summary>Fallback type for routes whose source provides no measurement.</summary>
        public const int GenericTeleportType = 0;
        private static IReadOnlyDictionary<(long MapId, int ElementId), InteractiveTeleport> _byElement =
            new Dictionary<(long, int), InteractiveTeleport>();
        private static IReadOnlyDictionary<long, IReadOnlyList<InteractiveTeleport>> _byMap =
            new Dictionary<long, IReadOnlyList<InteractiveTeleport>>();
        private static IReadOnlyDictionary<(long MapId, int CellId), InteractiveTeleport> _byCell =
            new Dictionary<(long, int), InteractiveTeleport>();

        public static int Count => _byElement.Count;
        public static IEnumerable<InteractiveTeleport> All => _byElement.Values;

        public static void Initialize()
        {
            ImportIfAvailable();
            LoadFromDatabase();
            // The per-cell index detects duplicates and also serves the floor passages. Those
            // are triggered after the last jrw is confirmed, while still answering the
            // jqi: normal exits through the edge thus keep their jsq/jqk exchange.
            Console.WriteLine($"[Teleport] {_byElement.Count} rutas cargadas, en " +
                              $"{_byMap.Count} mapas.");
        }

        public static bool TryGet(long mapId, int elementId, out InteractiveTeleport route)
            => _byElement.TryGetValue((mapId, elementId), out route!);

        public static bool TryGetCellTrigger(long mapId, int cellId, out InteractiveTeleport route)
            => _byCell.TryGetValue((mapId, cellId), out route!);

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

        /// <summary>
        /// Merges the catalogues and leaves them in the base.
        ///
        /// They are TWO and the order matters: first Giny's, which brings the measured arrival cell,
        /// and then the 2.73 graph's, which can only approximate it. When both speak of the
        /// same element the first wins, and the second is left switched off with the reason written.
        ///
        /// Everything discarded is stored all the same, with its ValidationStatus, so that a route that
        /// disappears can be looked at instead of guessing why it is not there.
        /// </summary>
        private static void ImportIfAvailable()
        {
            var catalogos = new (string Ruta, string Nombre)[]
            {
                (Paths.InteractiveTeleportsJson, "Giny 2.68"),
                (Paths.WorldGraphTeleportsJson, "grafo 2.73"),
            };

            var rows = new List<ImportRow>();
            int housesSkipped = 0;

            try
            {
                foreach (var (ruta, nombre) in catalogos)
                {
                    if (!File.Exists(ruta))
                    {
                        Console.WriteLine($"[Teleport] Falta el catálogo de {nombre} ({ruta}).");
                        continue;
                    }

                    using var document = JsonDocument.Parse(File.ReadAllText(ruta));
                    JsonElement root = document.RootElement;
                    if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
                        throw new InvalidOperationException($"{nombre}: schemaVersion distinto de 1.");
                    if (!root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException($"{nombre}: la propiedad routes no es una lista.");

                    int leidas = 0;
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
                        leidas++;
                    }
                    Console.WriteLine($"[Teleport] Catálogo de {nombre}: {leidas} rutas leídas.");
                }

                if (rows.Count == 0)
                {
                    Console.WriteLine("[Teleport] Ningún catálogo; se conserva el que hay en SQLite.");
                    return;
                }

                // Two destinations for the same element within the SAME catalogue: it cannot be chosen
                // for us, so none is activated.
                var ambiguous = rows
                    .Where(x => x.RequestedEnabled)
                    .GroupBy(x => (x.Route.SourceMapId, x.Route.ElementId, x.Route.SourceVersion))
                    .Where(x => x.Count() > 1)
                    .Select(x => (x.Key.SourceMapId, x.Key.ElementId))
                    .ToHashSet();

                // What has already been activated, so that the second catalogue does not overwrite the first. Both
                // keys are watched: the element, and the cell —two passages on the same cell
                // would leave the per-cell index not knowing which to go to—.
                var elementoTomado = new HashSet<(long, int)>();
                var celdaTomada = new HashSet<(long, int)>();

                int enabledCount = 0;
                foreach (var row in rows)
                {
                    var errors = Validate(row.Route);

                    if (!row.RequestedEnabled &&
                        string.Equals(row.Route.Confidence, "ambiguous", StringComparison.OrdinalIgnoreCase))
                        errors.Add("ambiguous-source");
                    if (ambiguous.Contains((row.Route.SourceMapId, row.Route.ElementId)))
                        errors.Add("ambiguous-source");

                    var porElemento = (row.Route.SourceMapId, row.Route.ElementId);
                    var porCelda = (row.Route.SourceMapId, row.Route.SourceCellId);
                    if (row.RequestedEnabled && errors.Count == 0)
                    {
                        if (elementoTomado.Contains(porElemento)) errors.Add("already-covered");
                        else if (celdaTomada.Contains(porCelda)) errors.Add("duplicate-source-cell");
                    }

                    row.Enabled = row.RequestedEnabled && errors.Count == 0;
                    row.ValidationStatus = errors.Count == 0 ? "ok" : string.Join(",", errors);
                    if (row.Enabled)
                    {
                        elementoTomado.Add(porElemento);
                        celdaTomada.Add(porCelda);
                        enabledCount++;
                    }
                }

                ReplaceDatabase(rows);
                Console.WriteLine($"[Teleport] Importadas {rows.Count} rutas, {enabledCount} activas, " +
                                  $"{housesSkipped} casas ignoradas.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Teleport] Importación cancelada; se conserva SQLite: {ex.Message}");
            }
        }

        private static InteractiveTeleport Read(JsonElement entry)
        {
            long sourceMapId = entry.GetProperty("sourceMapId").GetInt64();
            long destinationMapId = entry.GetProperty("destinationMapId").GetInt64();
            return new InteractiveTeleport
            {
                SourceMapId = sourceMapId,
                ElementId = entry.GetProperty("elementId").GetInt32(),
                SourceCellId = entry.GetProperty("sourceCellId").GetInt32(),
                GfxId = entry.GetProperty("gfxId").GetInt32(),
                // The type is part of the element's identity on the client side. 538 Giny routes
                // carry a direct Dofus 3.6 measurement, notably gfx 3507 with type -1.
                // Overwriting it with zero does leave f11/f15 on the wire, but the client no longer
                // attaches the declaration to the exit drawing.
                InteractiveType = ReadInteractiveType(entry),
                SkillId = entry.GetProperty("skillId").GetInt32(),
                DestinationMapId = destinationMapId,
                DestinationCellId = entry.GetProperty("destinationCellId").GetInt32(),
                SourceVersion = entry.TryGetProperty("sourceVersion", out var source) ? source.GetString() ?? "" : "",
                Confidence = entry.TryGetProperty("confidence", out var confidence) ? confidence.GetString() ?? "" : ""
            };
        }

        internal static int ReadInteractiveType(JsonElement entry)
        {
            if (!entry.TryGetProperty("interactiveType", out var type) ||
                type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out int measured))
            {
                return GenericTeleportType;
            }
            return measured;
        }

        private static List<string> Validate(InteractiveTeleport route)
        {
            var errors = new List<string>();
            if (route.SourceMapId <= 0 || route.DestinationMapId <= 0) errors.Add("invalid-map");
            if (route.ElementId <= 0) errors.Add("invalid-element");
            if (route.DestinationCellId < 0 || route.DestinationCellId > 559) errors.Add("invalid-cell");
            // -1 is a valid, measured proto type: on the wire it becomes ulong.MaxValue.
            // The positive values also come from the captures (doors, transports, etc.).
            if (route.InteractiveType < -1) errors.Add("invalid-type");
            if (route.SkillId != UseSkill && route.SkillId != ExitSkill) errors.Add("unexpected-skill");
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
        /// One of Giny's old «Teleport» entries may really be a zaap, a zaapi or some
        /// other element whose real protocol we already know. Those stay in their manager, which
        /// knows how to do it right; only the generic passages come in here.
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
            var byCell = new Dictionary<(long, int), InteractiveTeleport>();
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
                if (!byCell.TryAdd((route.SourceMapId, route.SourceCellId), route))
                    throw new InvalidOperationException(
                        $"Dos rutas Teleport para {route.SourceMapId}/{route.SourceCellId}.");
            }

            AplicarLosNuestros(byElement, byMap, byCell);
            AddFloorPassages(byCell);

            _byElement = byElement;
            _byMap = byMap.ToDictionary(x => x.Key, x => (IReadOnlyList<InteractiveTeleport>)x.Value);
            _byCell = byCell;
        }

        /// <summary>
        /// The passages a person has decided, on top of the 3,815 extracted ones.
        /// </summary>
        /// <remarks>
        /// Without this the editor writes a file nobody reads. The InteractiveTeleports table is
        /// rebuilt every time world.db is remade, so a passage added there disappears
        /// on the next regeneration without a word; that is why ours lives in
        /// content/interactives/teleports.json and is laid ON TOP at start.
        ///
        /// It is replaced per element, not added: an element is a door and a door leads to one
        /// place. And if our version changes the origin cell, the old entry has to be removed
        /// from the per-cell index or two routes remain for the same cell and the start blows up,
        /// which is exactly what the exception above checks.
        /// </remarks>
        private static void AplicarLosNuestros(Dictionary<(long, int), InteractiveTeleport> byElement,
                                               Dictionary<long, List<InteractiveTeleport>> byMap,
                                               Dictionary<(long, int), InteractiveTeleport> byCell)
        {
            var nuestros = TeleportContent.Load(Paths.ContentFile(TeleportContent.AuthoredFile),
                                                mensaje => Console.WriteLine("[Teleports] " + mensaje));

            int puestos = 0;
            int quitados = 0;

            void Descolgar(long mapa, int elemento)
            {
                if (!byElement.TryGetValue((mapa, elemento), out var vieja)) return;

                byElement.Remove((mapa, elemento));
                if (byMap.TryGetValue(mapa, out var lista)) lista.RemoveAll(r => r.ElementId == elemento);

                // Only if the cell still points to THIS route: two elements can share a
                // cell and deleting blindly would wipe out the other's.
                if (byCell.TryGetValue((mapa, vieja.SourceCellId), out var enLaCasilla) &&
                    ReferenceEquals(enLaCasilla, vieja))
                {
                    byCell.Remove((mapa, vieja.SourceCellId));
                }
            }

            foreach (var key in nuestros.ErasedKeys)
            {
                Descolgar(key.SourceMapId, (int)key.ElementId);
                quitados++;
            }

            foreach (var fila in nuestros.Rows)
            {
                var passage = fila.Value.Value;
                int elemento = (int)passage.ElementId;

                Descolgar(passage.SourceMapId, elemento);

                var ruta = new InteractiveTeleport
                {
                    SourceMapId = passage.SourceMapId,
                    ElementId = elemento,
                    SourceCellId = passage.SourceCell,
                    GfxId = passage.GfxId,
                    InteractiveType = passage.InteractiveType,
                    SkillId = passage.SkillId,
                    DestinationMapId = passage.DestinationMapId,
                    DestinationCellId = passage.DestinationCell,
                    SourceVersion = "Jondo Studio",
                    Confidence = "authored",
                };

                byElement[(ruta.SourceMapId, ruta.ElementId)] = ruta;

                if (!byMap.TryGetValue(ruta.SourceMapId, out var lista))
                {
                    byMap.Add(ruta.SourceMapId, lista = new List<InteractiveTeleport>());
                }

                lista.Add(ruta);

                // Another element on the same cell is left without its per-cell shortcut, and that is
                // right: the one that rules is the one decided by hand.
                byCell[(ruta.SourceMapId, ruta.SourceCellId)] = ruta;
                puestos++;
            }

            if (puestos > 0 || quitados > 0)
            {
                Console.WriteLine($"[Teleports] {puestos} pasaje(s) puestos a mano y {quitados} quitado(s), " +
                                  "de content/interactives/teleports.json.");
            }
        }

        /// <summary>
        /// The floor passages (<see cref="FloorPassages"/>): cells that move whoever stops on
        /// them, with no element.
        /// </summary>
        /// <remarks>
        /// Only in the index by cell, which is what WorldMoveHandler asks when a walk ends; not by
        /// element nor by map, so nothing is declared to the client for them. A cell that already
        /// has an element's passage keeps it: the element is what the map shows. And one leading to
        /// a map the world does not have is left out, as Validate leaves out an element's.
        /// </remarks>
        private static void AddFloorPassages(Dictionary<(long, int), InteractiveTeleport> byCell)
        {
            int added = 0;
            foreach (var floor in FloorPassages.Load(Paths.ContentFile(FloorPassages.AuthoredFile),
                                                     message => Console.WriteLine("[Teleports] " + message)))
            {
                if (byCell.ContainsKey((floor.SourceMapId, floor.SourceCell)))
                {
                    Console.WriteLine($"[Teleports] Floor passage {floor}: the cell has an element's passage already; left out.");
                    continue;
                }
                if (MapManager.GetMapInfo(floor.DestinationMapId) == null)
                {
                    Console.WriteLine($"[Teleports] Floor passage {floor}: the world has no map {floor.DestinationMapId}; left out.");
                    continue;
                }

                byCell[(floor.SourceMapId, floor.SourceCell)] = new InteractiveTeleport
                {
                    SourceMapId = floor.SourceMapId,
                    SourceCellId = floor.SourceCell,
                    DestinationMapId = floor.DestinationMapId,
                    DestinationCellId = floor.DestinationCell,
                    SourceVersion = "floor",
                    Confidence = "authored",
                };
                added++;
            }

            if (added > 0)
                Console.WriteLine($"[Teleports] {added} floor passage(s), from content/interactives/floor_passages.json.");
        }
    }
}
