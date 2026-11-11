using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The elements with several named ways (InteractiveWays): each viewer is offered the ways
    /// whose condition he meets, and the one he picks takes him to its map.
    /// </summary>
    public static class InteractiveWaysHandler
    {
        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        /// <summary>What of such an element the current character is offered, or null for any other element.</summary>
        public static IReadOnlyList<InteractiveAction>? VisibleActions(RegisteredInteractive interactive)
        {
            var ways = InteractiveWays.Of(interactive.MapId, interactive.Element.Id);
            if (ways == null) return null;
            return interactive.Actions
                .Where(a => a.Kind == InteractiveActionKind.Way
                            && InteractiveWays.WayOf(ways, a.SkillNameId) is { } way
                            && ItemCriteria.Holds(way.Criterion))
                .ToList();
        }

        /// <summary>An element as the current character sees it (iwm): f3 { f1 1, f4 each skill { uid, skill, name }, f5, f6 }.</summary>
        public static byte[] BuildElement(RegisteredInteractive interactive)
        {
            var declaration = Pb.New().Var(1, 1);
            foreach (var action in VisibleActions(interactive) ?? interactive.Actions)
                declaration.Msg(4, Pb.New().Var(1, action.SkillInstanceId).Var(2, action.SkillId).VarIfNotZero(3, action.SkillNameId));
            declaration.Var(5, interactive.Element.Id).Var(6, interactive.Type);
            return Pb.New().Msg(3, declaration).Build();
        }

        /// <summary>
        /// The map's elements with ways, declared again to the current character: what he is
        /// offered changes as he reads his temple's book, in the very room of the statue.
        /// </summary>
        public static async Task RefreshAsync(NetworkStream stream, long mapId)
        {
            foreach (var interactive in InteractiveRegistry.OnMap(mapId))
            {
                if (!InteractiveWays.Has(mapId, interactive.Element.Id)) continue;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Iwm, BuildElement(interactive)));
            }
        }

        /// <summary>A way taken: the element in use (iwn), then the map its name leads to.</summary>
        public static async Task UseAsync(NetworkStream stream, RegisteredInteractive interactive, InteractiveAction action)
        {
            var state = SessionContext.State;
            var ways = InteractiveWays.Of(interactive.MapId, interactive.Element.Id);
            var way = ways == null ? null : InteractiveWays.WayOf(ways, action.SkillNameId);
            if (way == null || way.ToMap == 0) return;
            if (!ItemCriteria.Holds(way.Criterion))
            {
                Console.WriteLine($"[Ways] Element {interactive.Element.Id} on {interactive.MapId}: «{way.Criterion}» not met.");
                return;
            }
            await SessionRegistry.BroadcastToMapAsync(state.MapId, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(interactive.Element.Id, action.SkillId, state.CharacterId)));
            Console.WriteLine($"[Ways] {state.CharacterName} takes way {way.Name} of element {interactive.Element.Id} to {way.ToMap}.");
            await TeleportHandler.ToMapAsync(stream, way.ToMap, way.ToCell);
        }
    }
}
