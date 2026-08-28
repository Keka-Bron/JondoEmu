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
    /// <summary>Un paso instantáneo de un mapa a otro, colgado de un elemento del mapa.</summary>
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
    /// Importa, valida e indexa los pasos sacados de Giny 2.68.
    ///
    /// El json normalizado es la fuente que se versiona; SQLite es la copia de trabajo que el
    /// servidor consulta. Se reimporta en CADA arranque, así que tocar la tabla a mano no sirve
    /// de nada: lo que manda es el json.
    ///
    /// Las casas se rechazan aquí a propósito: su protocolo jqw y su estado de vuelta son de
    /// <see cref="Houses"/> y de HouseHandler, y meterlas por aquí las rompería.
    ///
    /// De 1.678 candidatas quedan 1.586 activas. Las que se caen lo hacen por lo que dice
    /// Validate: 55 son ambiguas —dos destinos para el mismo elemento— y 37 apuntan a un mapa
    /// que no existe en 3.6.10.10. Ese filtro es lo que hace utilizable un volcado de Dofus 2.
    /// </summary>
    public static class TeleportManager
    {
        public const int UseSkill = 114;
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
            // El índice por casilla cumple dos funciones conservadas por Jondo: impide dos rutas
            // activas sobre la misma celda y permite que WorldMoveHandler reutilice la ruta cuando
            // recibe el jqi de fin de movimiento. Sin coincidencia, jqi sigue por el flujo normal
            // jsq/jqk de los bordes de mapa.
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
        /// Junta los catálogos y los deja en la base.
        ///
        /// Son DOS y el orden importa: primero el de Giny, que trae la casilla de llegada medida,
        /// y después el del grafo de 2.73, que sólo la sabe aproximar. Cuando los dos hablan del
        /// mismo elemento gana el primero, y el segundo queda apagado con el motivo escrito.
        ///
        /// Todo lo que se descarta se guarda igual, con su ValidationStatus, para que una ruta que
        /// desaparece se pueda mirar en vez de adivinar por qué no está.
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

                // Dos destinos para el mismo elemento dentro del MISMO catálogo: no se puede elegir
                // por nosotros, así que no se activa ninguno.
                var ambiguous = rows
                    .Where(x => x.RequestedEnabled)
                    .GroupBy(x => (x.Route.SourceMapId, x.Route.ElementId, x.Route.SourceVersion))
                    .Where(x => x.Count() > 1)
                    .Select(x => (x.Key.SourceMapId, x.Key.ElementId))
                    .ToHashSet();

                // Lo que ya se ha activado, para que el segundo catálogo no pise al primero. Se
                // vigilan las dos claves: el elemento, y la casilla —dos pasos en la misma casilla
                // dejarían el índice por casilla sin saber a cuál ir—.
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
                // Igual que el «.sun» de Giny: el gráfico sigue siendo el del elemento del mapa
                // y la acción de teleport se declara siempre con el tipo genérico 0.
                //
                // OJO: esto PISA el tipo que trae el json, y 529 de las rutas activas lo traían
                // medido de una captura de 3.6. De rebote, la comprobación «unexpected-type» de
                // Validate no puede fallar nunca. Se deja tal cual porque es lo que da las 1.586
                // que están probadas; cambiarlo cambiaría la cuenta y habría que volver a medir.
                InteractiveType = GenericTeleportType,
                SkillId = entry.GetProperty("skillId").GetInt32(),
                DestinationMapId = destinationMapId,
                DestinationCellId = entry.GetProperty("destinationCellId").GetInt32(),
                SourceVersion = entry.TryGetProperty("sourceVersion", out var source) ? source.GetString() ?? "" : "",
                Confidence = entry.TryGetProperty("confidence", out var confidence) ? confidence.GetString() ?? "" : ""
            };
        }

        private static List<string> Validate(InteractiveTeleport route)
        {
            var errors = new List<string>();
            if (route.SourceMapId <= 0 || route.DestinationMapId <= 0) errors.Add("invalid-map");
            if (route.ElementId <= 0) errors.Add("invalid-element");
            if (route.DestinationCellId < 0 || route.DestinationCellId > 559) errors.Add("invalid-cell");
            if (route.InteractiveType != GenericTeleportType) errors.Add("unexpected-type");
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
        /// Un «Teleport» de los viejos de Giny puede ser en realidad un zaap, un zaapi o algún
        /// otro elemento cuyo protocolo de verdad ya conocemos. Ésos se quedan en su manager, que
        /// sabe hacerlo bien; aquí sólo entran los pasos genéricos.
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

            _byElement = byElement;
            _byMap = byMap.ToDictionary(x => x.Key, x => (IReadOnlyList<InteractiveTeleport>)x.Value);
            _byCell = byCell;
        }

        /// <summary>
        /// Los pasajes que ha decidido una persona, encima de los 3.815 extraidos.
        /// </summary>
        /// <remarks>
        /// Sin esto el editor escribe un fichero que nadie lee. La tabla InteractiveTeleports se
        /// reconstruye cada vez que se rehace world.db, asi que un pasaje anadido ahi desaparece
        /// en la siguiente regeneracion sin decir nada; por eso lo nuestro vive en
        /// content/interactives/teleports.json y se pone ENCIMA al arrancar.
        ///
        /// Se sustituye por elemento, no se suma: un elemento es una puerta y una puerta lleva a un
        /// sitio. Y si nuestra version cambia la casilla de origen, hay que quitar la entrada vieja
        /// del indice por casilla o quedan dos rutas para la misma casilla y el arranque revienta,
        /// que es justo lo que comprueba la excepcion de arriba.
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

                // Solo si la casilla sigue apuntando a ESTA ruta: dos elementos pueden compartir
                // casilla y borrar a ciegas se llevaria por delante la del otro.
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

                // Otro elemento en la misma casilla se queda sin su atajo por casilla, y es lo
                // correcto: el que manda es el que se ha decidido a mano.
                byCell[(ruta.SourceMapId, ruta.SourceCellId)] = ruta;
                puestos++;
            }

            if (puestos > 0 || quitados > 0)
            {
                Console.WriteLine($"[Teleports] {puestos} pasaje(s) puestos a mano y {quitados} quitado(s), " +
                                  "de content/interactives/teleports.json.");
            }
        }
    }
}
