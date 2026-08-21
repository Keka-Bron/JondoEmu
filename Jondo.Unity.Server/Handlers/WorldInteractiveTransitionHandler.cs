using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>Executes an evidence-backed interactive edge from the client world graph.</summary>
    public static class WorldInteractiveTransitionHandler
    {
        public static async Task UseAsync(NetworkStream stream, RegisteredInteractive interactive,
                                          InteractiveAction action)
        {
            long mapId = SessionContext.State.MapId;
            if (interactive.MapId != mapId ||
                !WorldInteractiveTransitions.TryGet(mapId, interactive.Element.Id, out var route) ||
                route.SkillId != action.SkillId || route.Element.Cell != interactive.Element.Cell ||
                route.Element.Gfx != interactive.Element.Gfx)
            {
                Console.WriteLine($"[WorldTransitions] Uso rechazado: la declaración " +
                                  $"{mapId}/{interactive.Element.Id}/{action.SkillId} ya no " +
                                  "coincide con el catálogo cargado.");
                return;
            }

            if (!route.TrySelectSource(SessionContext.State.CellId, out var source))
            {
                Console.WriteLine($"[WorldTransitions] Uso remoto rechazado en {mapId}/" +
                                  $"{interactive.Element.Id}: personaje en " +
                                  $"{SessionContext.State.CellId}, origen(es) " +
                                  SourceList(route.Sources) + ".");
                return;
            }

            int preferredCell = source.DerivedArrivalCellId ?? TeleportHandler.MapCentre;
            if (!TryNearestSafeWalkable(route.TargetMapId, preferredCell, out int arrivalCell))
            {
                Console.WriteLine($"[WorldTransitions] El mapa destino {route.TargetMapId} no " +
                                  "tiene ninguna casilla segura conocida; no se cambia el mapa.");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.InteractiveUsedMessage,
                    ConnectionProtocol.BuildElementInUse(interactive.Element.Id, action.SkillId,
                        SessionContext.State.CharacterId)));

            string arrivalEvidence = source.DerivedArrivalCellId.HasValue
                ? $"recíproco {source.DerivedArrivalCellId.Value}"
                : "centro seguro (el grafo no contiene llegada)";
            bool moved = await WorldMoveHandler.MoveToMapAsync(stream, route.TargetMapId, arrivalCell,
                $"interactivo {interactive.Element.Id}, skill {action.SkillId}, llegada {arrivalEvidence}");

            // A small number of indoor variants expose their return as a map-exit cell rather
            // than as a reverse world-graph edge.  Bind it to this exact entry only when the
            // version-pinned client-data row matches every identity field of the used route.
            if (moved && WorldInteractiveReturns.TryCreatePending(route, source, out var pending))
                SessionContext.State.PendingWorldInteractiveReturn = pending;
        }

        private static bool TryNearestSafeWalkable(long mapId, int preferredCell, out int result)
        {
            result = -1;
            IReadOnlyCollection<int>? safe = null;
            if (MapManager.WalkableCells.TryGetValue(mapId, out var roleplay) &&
                roleplay.Count > 0)
                safe = roleplay;
            else
            {
                var fight = MapManager.GetFightWalkable(mapId);
                if (fight != null && fight.Count > 0) safe = fight;
            }

            if (safe == null || safe.Count == 0) return false;
            if (safe.Contains(preferredCell))
            {
                result = preferredCell;
                return true;
            }

            int bestDistance = int.MaxValue;
            foreach (int candidate in safe)
            {
                int distance = MapGeometry.IsValid(preferredCell)
                    ? MapGeometry.Distance(preferredCell, candidate)
                    : Math.Abs(candidate - TeleportHandler.MapCentre);
                if (distance < bestDistance ||
                    (distance == bestDistance && (result < 0 || candidate < result)))
                {
                    result = candidate;
                    bestDistance = distance;
                }
            }
            return result >= 0;
        }

        private static string SourceList(IReadOnlyList<WorldInteractiveTransitions.Source> sources)
        {
            var values = new string[sources.Count];
            for (int i = 0; i < sources.Count; i++) values[i] = sources[i].CellId.ToString();
            return string.Join(",", values);
        }
    }
}
