using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Authoritative, criterion-free interactive routes from the pinned 3.6.10.10 client world
    /// graph.
    ///
    /// The client asset supplies the exact source map, element, cell, graphic, skill and target
    /// map. It does not supply the protocol interactive type used by <c>jss</c>, so an extracted
    /// route is declared as unknown (-1) unless the data row carries separately evidenced type
    /// information. World-pathfinding type 32 is never used as a protocol type.
    /// </summary>
    public static class WorldInteractiveTransitions
    {
        public const int UnknownProtocolInteractiveType = -1;
        public const string ExpectedClientVersion = "3.6.10.10";

        public sealed class Source
        {
            internal Source(int cellId, int? derivedArrivalCellId)
            {
                CellId = cellId;
                DerivedArrivalCellId = derivedArrivalCellId;
            }

            /// <summary>The source-map cell recorded by the world graph.</summary>
            public int CellId { get; }

            /// <summary>
            /// A candidate derived only from the exact reciprocal target-to-source edge. It is
            /// null when the reciprocal evidence is absent or ambiguous.
            /// </summary>
            public int? DerivedArrivalCellId { get; }
        }

        public sealed class Route
        {
            internal Route(long mapId, Interactives.Element element, int skillId, long targetMapId,
                           int protocolInteractiveType, IReadOnlyList<Source> sources)
            {
                MapId = mapId;
                Element = element;
                SkillId = skillId;
                TargetMapId = targetMapId;
                ProtocolInteractiveType = protocolInteractiveType;
                Sources = sources;
            }

            public long MapId { get; }
            public Interactives.Element Element { get; }
            public int SkillId { get; }
            public long TargetMapId { get; }
            public int ProtocolInteractiveType { get; }
            public IReadOnlyList<Source> Sources { get; }

            /// <summary>
            /// Resolve the graph source nearest the character. Exact matches win; one adjacent
            /// cell is tolerated because the clickable visual and the cell where the actor stops
            /// need not be the same cell. A remote click is never accepted.
            /// </summary>
            public bool TrySelectSource(int characterCellId, out Source source)
            {
                source = null!;
                if (!MapGeometry.IsValid(characterCellId)) return false;

                Source? nearest = null;
                int nearestDistance = int.MaxValue;
                foreach (var candidate in Sources)
                {
                    int distance = MapGeometry.Distance(characterCellId, candidate.CellId);
                    if (distance < nearestDistance ||
                        (distance == nearestDistance && nearest != null &&
                         candidate.CellId < nearest.CellId))
                    {
                        nearest = candidate;
                        nearestDistance = distance;
                    }
                }

                if (nearest == null || nearestDistance > 1) return false;
                source = nearest;
                return true;
            }
        }

        private sealed class Builder
        {
            public long MapId { get; init; }
            public Interactives.Element Element { get; init; }
            public int SkillId { get; init; }
            public long TargetMapId { get; init; }
            public int ProtocolInteractiveType { get; init; }
            public Dictionary<int, int?> Sources { get; } = new Dictionary<int, int?>();
        }

        private static readonly Dictionary<(long MapId, int ElementId), Route> _byElement =
            new Dictionary<(long, int), Route>();
        private static readonly Dictionary<long, List<Route>> _byMap =
            new Dictionary<long, List<Route>>();
        private static readonly List<Route> _all = new List<Route>();
        // Every live map element for which the graph contains an interactive edge, including
        // criterion-gated or ambiguous edges that are intentionally absent from safeRoutes.
        // Other feature detectors use this to avoid claiming a generic graph door merely because
        // it happens to reuse a house/zaap-looking graphic.
        private static readonly HashSet<(long MapId, int ElementId)> _allEvidenceElements =
            new HashSet<(long, int)>();

        public static int Count => _all.Count;
        public static int SourceCount { get; private set; }
        public static int RejectedCount { get; private set; }
        public static IReadOnlyList<Route> All => _all;

        public static void Initialize()
        {
            _byElement.Clear();
            _byMap.Clear();
            _all.Clear();
            _allEvidenceElements.Clear();
            SourceCount = 0;
            RejectedCount = 0;

            string path = Paths.WorldInteractiveTransitionsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[WorldTransitions] Falta {Path.GetFileName(path)}; no se " +
                                  "declaran puertas/escaleras del grafo mundial.");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = document.RootElement;
                string clientVersion = root.TryGetProperty("clientVersion", out var version)
                    ? version.GetString() ?? string.Empty
                    : string.Empty;
                if (!string.Equals(clientVersion, ExpectedClientVersion,
                                   StringComparison.Ordinal))
                {
                    Console.WriteLine($"[WorldTransitions] Catálogo {clientVersion} rechazado; " +
                                      $"el servidor está fijado a {ExpectedClientVersion}.");
                    return;
                }

                if (!root.TryGetProperty("safeRoutes", out var routes) ||
                    routes.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine("[WorldTransitions] El catálogo no contiene safeRoutes.");
                    return;
                }

                if (root.TryGetProperty("elements", out var evidenceElements) &&
                    evidenceElements.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in evidenceElements.EnumerateArray())
                    {
                        if (!element.TryGetProperty("mapId", out var map) ||
                            !element.TryGetProperty("elementId", out var id) ||
                            !element.TryGetProperty("routes", out var evidenceRoutes) ||
                            evidenceRoutes.ValueKind != JsonValueKind.Array)
                            continue;

                        bool hasInteractiveRoute = false;
                        foreach (var evidenceRoute in evidenceRoutes.EnumerateArray())
                        {
                            if (evidenceRoute.TryGetProperty("pathType", out var type) &&
                                type.ValueKind == JsonValueKind.Number && type.GetInt32() == 32)
                            {
                                hasInteractiveRoute = true;
                                break;
                            }
                        }
                        if (hasInteractiveRoute)
                            _allEvidenceElements.Add((map.GetInt64(), id.GetInt32()));
                    }
                }

                var builders = new Dictionary<(long, int), Builder>();
                var rejectedKeys = new HashSet<(long, int)>();
                foreach (var row in routes.EnumerateArray())
                {
                    long mapId;
                    int elementId;
                    int elementCellId;
                    int gfxId;
                    int skillId;
                    long targetMapId;
                    int sourceCellId;
                    int? derivedArrivalCellId;
                    int protocolType;
                    bool safe;
                    try
                    {
                        safe = TryReadSafeRow(row, out mapId, out elementId,
                            out elementCellId, out gfxId, out skillId, out targetMapId,
                            out sourceCellId, out derivedArrivalCellId, out protocolType);
                    }
                    catch (Exception)
                    {
                        safe = false;
                        mapId = targetMapId = 0;
                        elementId = elementCellId = gfxId = skillId = sourceCellId = 0;
                        derivedArrivalCellId = null;
                        protocolType = UnknownProtocolInteractiveType;
                    }

                    if (!safe)
                    {
                        RejectedCount++;
                        continue;
                    }

                    var key = (mapId, elementId);
                    if (rejectedKeys.Contains(key)) continue;

                    Interactives.Element live = Interactives.ByElementId(mapId, elementId);
                    if (live.Id != elementId || live.Cell != elementCellId || live.Gfx != gfxId ||
                        MapManager.GetMapInfo(mapId) == null ||
                        MapManager.GetMapInfo(targetMapId) == null)
                    {
                        RejectedCount++;
                        rejectedKeys.Add(key);
                        builders.Remove(key);
                        continue;
                    }

                    if (!builders.TryGetValue(key, out var builder))
                    {
                        builder = new Builder
                        {
                            MapId = mapId,
                            Element = live,
                            SkillId = skillId,
                            TargetMapId = targetMapId,
                            ProtocolInteractiveType = protocolType,
                        };
                        builders.Add(key, builder);
                    }
                    else if (builder.SkillId != skillId ||
                             builder.TargetMapId != targetMapId ||
                             builder.ProtocolInteractiveType != protocolType)
                    {
                        // The entire element becomes unsafe: selecting one of several actions or
                        // targets without a server criterion would be a guess.
                        RejectedCount++;
                        rejectedKeys.Add(key);
                        builders.Remove(key);
                        continue;
                    }

                    if (builder.Sources.TryGetValue(sourceCellId, out int? existingArrival) &&
                        existingArrival != derivedArrivalCellId)
                    {
                        RejectedCount++;
                        rejectedKeys.Add(key);
                        builders.Remove(key);
                        continue;
                    }
                    builder.Sources[sourceCellId] = derivedArrivalCellId;
                }

                var ordered = new List<Builder>(builders.Values);
                ordered.Sort((left, right) =>
                {
                    int byMap = left.MapId.CompareTo(right.MapId);
                    return byMap != 0 ? byMap : left.Element.Id.CompareTo(right.Element.Id);
                });

                foreach (var builder in ordered)
                {
                    var sources = new List<Source>();
                    foreach (var pair in builder.Sources)
                        sources.Add(new Source(pair.Key, pair.Value));
                    sources.Sort((left, right) => left.CellId.CompareTo(right.CellId));
                    if (sources.Count == 0) continue;

                    var route = new Route(builder.MapId, builder.Element, builder.SkillId,
                        builder.TargetMapId, builder.ProtocolInteractiveType, sources);
                    _all.Add(route);
                    _byElement.Add((route.MapId, route.Element.Id), route);
                    if (!_byMap.TryGetValue(route.MapId, out var mapRoutes))
                    {
                        mapRoutes = new List<Route>();
                        _byMap.Add(route.MapId, mapRoutes);
                    }
                    mapRoutes.Add(route);
                    SourceCount += sources.Count;
                }

                Console.WriteLine($"[WorldTransitions] {_all.Count} elementos seguros, " +
                                  $"{SourceCount} origen(es), {RejectedCount} fila(s) rechazada(s)." );
            }
            catch (Exception ex)
            {
                _byElement.Clear();
                _byMap.Clear();
                _all.Clear();
                _allEvidenceElements.Clear();
                SourceCount = 0;
                Console.WriteLine($"[WorldTransitions] No se pudo leer {Path.GetFileName(path)}: " +
                                  ex.Message);
            }
        }

        public static IReadOnlyList<Route> OnMap(long mapId)
            => _byMap.TryGetValue(mapId, out var routes)
                ? routes
                : (IReadOnlyList<Route>)Array.Empty<Route>();

        public static bool TryGet(long mapId, int elementId, out Route route)
            => _byElement.TryGetValue((mapId, elementId), out route!);

        public static bool HasGraphEvidence(long mapId, int elementId)
            => _allEvidenceElements.Contains((mapId, elementId));

        private static bool TryReadSafeRow(JsonElement row, out long mapId, out int elementId,
                                            out int elementCellId, out int gfxId, out int skillId,
                                            out long targetMapId, out int sourceCellId,
                                            out int? derivedArrivalCellId, out int protocolType)
        {
            mapId = targetMapId = 0;
            elementId = elementCellId = gfxId = skillId = sourceCellId = 0;
            derivedArrivalCellId = null;
            protocolType = UnknownProtocolInteractiveType;

            if (!row.TryGetProperty("pathType", out var pathType) || pathType.GetInt32() != 32 ||
                !row.TryGetProperty("criterion", out var criterion) ||
                !string.IsNullOrEmpty(criterion.GetString()) ||
                !row.TryGetProperty("targetCount", out var targetCount) ||
                targetCount.GetInt32() != 1 ||
                !row.TryGetProperty("ambiguous", out var ambiguous) || ambiguous.GetBoolean())
                return false;

            mapId = row.GetProperty("fromMapId").GetInt64();
            elementId = row.GetProperty("elementId").GetInt32();
            elementCellId = row.GetProperty("elementCellId").GetInt32();
            gfxId = row.GetProperty("gfxId").GetInt32();
            skillId = row.GetProperty("skillId").GetInt32();
            targetMapId = row.GetProperty("targetMapId").GetInt64();
            sourceCellId = row.GetProperty("sourceCellId").GetInt32();

            if (row.TryGetProperty("derivedArrivalCellId", out var arrival) &&
                arrival.ValueKind == JsonValueKind.Number)
                derivedArrivalCellId = arrival.GetInt32();

            if (row.TryGetProperty("protocolInteractiveTypeId", out var protocol) &&
                protocol.ValueKind == JsonValueKind.Number)
                protocolType = protocol.GetInt32();

            return mapId > 0 && targetMapId > 0 && targetMapId != mapId && elementId > 0 &&
                   skillId >= 0 && MapGeometry.IsValid(elementCellId) &&
                   MapGeometry.IsValid(sourceCellId) &&
                   (!derivedArrivalCellId.HasValue ||
                    MapGeometry.IsValid(derivedArrivalCellId.Value));
        }
    }
}
