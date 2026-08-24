using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Loads explicitly reviewed interior return actions whose usable actor cell is not fully
    /// described by the client world graph. Rows remain versioned client data; this class
    /// deliberately has no fallback heuristic based on graphics, map coordinates or a player's
    /// last map.
    /// </summary>
    public static class WorldInteractiveReturns
    {
        public readonly struct Destination
        {
            public Destination(long returnMapId, int returnCellId, int entryElementId)
            {
                ReturnMapId = returnMapId;
                ReturnCellId = returnCellId;
                EntryElementId = entryElementId;
            }

            public long ReturnMapId { get; }
            public int ReturnCellId { get; }
            public int EntryElementId { get; }
        }

        private sealed class Definition
        {
            public long EntryMapId { get; init; }
            public int EntryElementId { get; init; }
            public int EntryCellId { get; init; }
            public int EntryGfxId { get; init; }
            public int SkillId { get; init; }
            public long InteriorMapId { get; init; }
            public int ExitCellId { get; init; }
            public long ReturnMapId { get; init; }
            public int ReturnCellId { get; init; }
        }

        private static readonly Dictionary<(long MapId, int ElementId), Definition> _byEntry =
            new Dictionary<(long, int), Definition>();

        public static int Count => _byEntry.Count;

        public static void Initialize()
        {
            _byEntry.Clear();
            string path = Paths.WorldInteractiveReturnsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine("[WorldReturns] No hay retornos interiores adicionales.");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("clientVersion", out var version) ||
                    !string.Equals(version.GetString(), WorldInteractiveTransitions.ExpectedClientVersion,
                                   StringComparison.Ordinal) ||
                    !root.TryGetProperty("returns", out var rows) ||
                    rows.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine("[WorldReturns] Catálogo inválido o de otra versión; ignorado.");
                    return;
                }

                foreach (JsonElement row in rows.EnumerateArray())
                {
                    if (!TryRead(row, out var definition)) continue;
                    var key = (definition.EntryMapId, definition.EntryElementId);
                    if (_byEntry.ContainsKey(key))
                    {
                        Console.WriteLine($"[WorldReturns] Entrada duplicada {key.Item1}/{key.Item2}; ignorada.");
                        continue;
                    }
                    _byEntry.Add(key, definition);
                }

                Console.WriteLine($"[WorldReturns] {_byEntry.Count} retorno(s) interior(es) versionado(s).");
            }
            catch (Exception ex)
            {
                _byEntry.Clear();
                Console.WriteLine($"[WorldReturns] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        public static bool TryCreatePending(WorldInteractiveTransitions.Route route,
                                            WorldInteractiveTransitions.Source source,
                                            out PendingWorldInteractiveReturn pending)
        {
            pending = null!;
            if (!_byEntry.TryGetValue((route.MapId, route.Element.Id), out var definition) ||
                definition.EntryCellId != source.CellId ||
                definition.EntryGfxId != route.Element.Gfx ||
                definition.SkillId != route.SkillId ||
                definition.InteriorMapId != route.TargetMapId)
                return false;

            pending = new PendingWorldInteractiveReturn
            {
                InteriorMapId = definition.InteriorMapId,
                ExitCellId = definition.ExitCellId,
                ReturnMapId = definition.ReturnMapId,
                ReturnCellId = definition.ReturnCellId,
                EntryElementId = definition.EntryElementId,
            };
            return true;
        }

        public static bool TryTakeAtCurrentCell(out Destination destination)
        {
            destination = default;
            var pending = SessionContext.State.PendingWorldInteractiveReturn;
            if (pending == null || SessionContext.State.MapId != pending.InteriorMapId ||
                !MapGeometry.IsValid(pending.ExitCellId) ||
                SessionContext.State.CellId != pending.ExitCellId ||
                MapManager.GetMapInfo(pending.ReturnMapId) == null)
                return false;

            destination = new Destination(pending.ReturnMapId, pending.ReturnCellId,
                pending.EntryElementId);
            SessionContext.State.PendingWorldInteractiveReturn = null;
            return true;
        }

        /// <summary>
        /// Resolves a version-pinned interior exit when the server was restarted after the
        /// character entered the interior, so there is no session-local entry binding to consume.
        /// This deliberately requires the client to explicitly request a map exit at the exact
        /// authored exit cell; it is not a coordinate or graphic heuristic and cannot be used as
        /// an arbitrary teleport.
        /// </summary>
        public static bool TryResolveDeclaredExitAtCurrentCell(out Destination destination)
            => TryResolveDeclaredExit(SessionContext.State.MapId,
                                      SessionContext.State.CellId,
                                      out destination);

        /// <summary>
        /// Pure resolver used by the live handler and startup regression guards. Both the map and
        /// the actor's final action cell must match one reviewed row exactly.
        /// </summary>
        public static bool TryResolveDeclaredExit(long interiorMapId, int currentCellId,
                                                  out Destination destination)
        {
            destination = default;
            if (!MapGeometry.IsValid(currentCellId)) return false;

            Definition? match = null;
            foreach (Definition definition in _byEntry.Values)
            {
                if (definition.InteriorMapId != interiorMapId ||
                    definition.ExitCellId != currentCellId ||
                    MapManager.GetMapInfo(definition.ReturnMapId) == null)
                    continue;

                // More than one static definition at one map/cell would make a return ambiguous.
                if (match != null) return false;
                match = definition;
            }

            if (match == null) return false;
            destination = new Destination(match.ReturnMapId, match.ReturnCellId,
                match.EntryElementId);
            return true;
        }

        private static bool TryRead(JsonElement row, out Definition definition)
        {
            definition = null!;
            try
            {
                JsonElement entry = row.GetProperty("entry");
                JsonElement exit = row.GetProperty("exit");
                JsonElement back = row.GetProperty("return");
                var result = new Definition
                {
                    EntryMapId = entry.GetProperty("mapId").GetInt64(),
                    EntryElementId = entry.GetProperty("elementId").GetInt32(),
                    EntryCellId = entry.GetProperty("sourceCellId").GetInt32(),
                    EntryGfxId = entry.GetProperty("gfxId").GetInt32(),
                    SkillId = entry.GetProperty("skillId").GetInt32(),
                    InteriorMapId = exit.GetProperty("mapId").GetInt64(),
                    // Some drawings are anchored several cells away from the actor endpoint used
                    // by the client. Keep the authored element cell as evidence while accepting
                    // only the separately observed action cell when one is supplied.
                    ExitCellId = exit.TryGetProperty("actionCellId", out var actionCell)
                        ? actionCell.GetInt32()
                        : exit.GetProperty("cellId").GetInt32(),
                    ReturnMapId = back.GetProperty("mapId").GetInt64(),
                    ReturnCellId = back.GetProperty("cellId").GetInt32(),
                };
                if (result.EntryMapId <= 0 || result.EntryElementId <= 0 ||
                    result.InteriorMapId <= 0 || result.ReturnMapId <= 0 ||
                    result.EntryMapId != result.ReturnMapId || result.SkillId < 0 ||
                    !MapGeometry.IsValid(result.EntryCellId) ||
                    !MapGeometry.IsValid(result.ExitCellId) ||
                    !MapGeometry.IsValid(result.ReturnCellId))
                    return false;
                definition = result;
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
