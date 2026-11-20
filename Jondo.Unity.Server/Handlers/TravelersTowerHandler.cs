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
    /// The Travelers' Tower's interactives (TravelersTower): the rooms' teleporters, the
    /// dimensional portal's mechanism and portal, and the dimensions' portals back.
    /// </summary>
    /// <remarks>
    /// Each use as the captures have it: the element in use (iwn) to the map, then its effect --
    /// the room's «TP» staging and the map change, or on the mechanism the portal redeclared to
    /// everybody, each with the skill his alterations give him (iwm), its stagings (hpm), and the
    /// mechanism redeclared two seconds later, as the capture's comes; through the portal, the
    /// dimension's entry; through a portal back, the tower's cell 315.
    /// </remarks>
    public static class TravelersTowerHandler
    {
        /// <summary>How long the mechanism stays in use before it is offered again: the capture's 2.1 s.</summary>
        private static readonly TimeSpan MechanismBusy = TimeSpan.FromSeconds(2);

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        /// <summary>
        /// What of these elements a character is offered, or null for an element that is not
        /// theirs: the mechanism's «Cerrar el portal» and his dimensions; the portal's way through
        /// only while it is open to one of them.
        /// </summary>
        public static IReadOnlyList<InteractiveAction> VisibleActions(RegisteredInteractive interactive, long characterId)
        {
            if (interactive.MapId != TravelersTower.PortalRoom) return null;
            if (interactive.Element.Id == TravelersTower.Creator)
            {
                var names = TravelersTower.CreatorNamesFor(characterId, Alterations.Has);
                return interactive.Actions.Where(a => names.Contains(a.SkillNameId)).ToList();
            }
            if (interactive.Element.Id == TravelersTower.Portal)
                return TravelersTower.LetsThrough(characterId, Alterations.Has) ? interactive.Actions : Array.Empty<InteractiveAction>();
            return null;
        }

        /// <summary>An element as a character sees it (iwm): f3 { f1 1, f4 each skill { uid, skill, name }, f5, f6 }.</summary>
        public static byte[] BuildElementFor(RegisteredInteractive interactive, long characterId)
        {
            var declaration = Pb.New().Var(1, 1);
            foreach (var action in VisibleActions(interactive, characterId) ?? interactive.Actions)
                declaration.Msg(4, Pb.New().Var(1, action.SkillInstanceId).Var(2, action.SkillId).VarIfNotZero(3, action.SkillNameId));
            declaration.Var(5, interactive.Element.Id).Var(6, interactive.Type);
            return Pb.New().Msg(3, declaration).Build();
        }

        /// <summary>The same-map move a room's own teleporter makes (jse): f1 the character, f2 the cell.</summary>
        public static byte[] BuildPutBack(long characterId, int cell) => Pb.New().Var(1, characterId).Var(2, cell).Build();

        private static RegisteredInteractive Element(long mapId, int elementId)
            => InteractiveRegistry.OnMap(mapId).FirstOrDefault(i => i.Element.Id == elementId);

        /// <summary>An element redeclared to everybody on its map, each with what he is offered.</summary>
        private static async Task RedeclareAsync(long mapId, int elementId)
        {
            var interactive = Element(mapId, elementId);
            if (interactive == null) return;
            foreach (var session in SessionRegistry.OnMap(mapId))
            {
                if (session.State.IsInFight) continue;
                try
                {
                    await session.SendAsync(ConnectionProtocol.Push(Op.Iwm, BuildElementFor(interactive, session.State.CharacterId)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Tower] An element could not reach {session.State.CharacterId}: {ex.Message}");
                }
            }
        }

        private static Task InUseAsync(long mapId, int elementId, int skillId, long who)
            => SessionRegistry.BroadcastToMapAsync(mapId, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(elementId, skillId, who)));

        private static Task PlayAsync(long mapId, int staging)
            => SessionRegistry.BroadcastToMapAsync(mapId, ConnectionProtocol.Push(Op.Hpm,
                GuildRaidStagings.BuildPlay(staging, GuildRaidStagings.FromStart)));

        // ─── The rooms ──────────────────────────────────────────────────────

        /// <summary>
        /// A room's teleporter: its «TP», then to the room its skill names -- or, being there, back
        /// onto its landing cell (jse), as the capture's «Sala del portal dimensional» from itself.
        /// </summary>
        public static async Task UseRoomAsync(NetworkStream stream, RegisteredInteractive interactive, InteractiveAction action)
        {
            var room = TravelersTower.RoomOf(action.SkillId);
            if (room == null) return;
            var state = SessionContext.State;
            long mapId = state.MapId;
            await InUseAsync(mapId, interactive.Element.Id, action.SkillId, state.CharacterId);
            await PlayAsync(mapId, TravelersTower.TeleporterStaging);
            if (room.MapId == mapId)
            {
                state.CellId = room.Cell;
                await SessionRegistry.BroadcastToMapAsync(mapId, ConnectionProtocol.Push(Op.Jse, BuildPutBack(state.CharacterId, room.Cell)));
                return;
            }
            Console.WriteLine($"[Tower] {state.CharacterName} goes to the {room.Name}.");
            await TeleportHandler.ToMapAsync(stream, room.MapId, room.Cell);
        }

        // ─── The dimensional portal ─────────────────────────────────────────

        /// <summary>
        /// The mechanism: the portal opened to one of his dimensions, or closed. Everybody on the
        /// map sees the portal change (each offered it as his alterations allow) and its stagings.
        /// </summary>
        public static async Task UseCreatorAsync(NetworkStream stream, RegisteredInteractive interactive, InteractiveAction action)
        {
            var state = SessionContext.State;
            TravelersTower.Dimension to = null;
            if (action.SkillNameId != TravelersTower.CloseName)
            {
                to = TravelersTower.DimensionOf(action.SkillNameId);
                if (to == null || !Alterations.Has(state.CharacterId, to.Alteration)) return;
            }
            long mapId = state.MapId;
            var stagings = TravelersTower.Switch(to);
            Console.WriteLine($"[Tower] {state.CharacterName} " + (to == null ? "closes the portal." : $"opens the portal to {to.Name}."));

            await InUseAsync(mapId, interactive.Element.Id, action.SkillId, state.CharacterId);
            await RedeclareAsync(mapId, TravelersTower.Portal);
            foreach (int staging in stagings) await PlayAsync(mapId, staging);
            _ = Task.Run(async () =>
            {
                await Task.Delay(MechanismBusy);
                await RedeclareAsync(mapId, TravelersTower.Creator);
            });
        }

        /// <summary>The portal: into the dimension it is open to, for whoever has its alteration.</summary>
        public static async Task UsePortalAsync(NetworkStream stream, RegisteredInteractive interactive, InteractiveAction action)
        {
            var state = SessionContext.State;
            var open = TravelersTower.Open;
            if (open == null || !Alterations.Has(state.CharacterId, open.Alteration)) return;
            await InUseAsync(state.MapId, interactive.Element.Id, action.SkillId, state.CharacterId);
            Console.WriteLine($"[Tower] {state.CharacterName} goes through the portal to {open.Name}.");
            await TeleportHandler.ToMapAsync(stream, open.EntryMap, open.EntryCell);
        }

        /// <summary>A dimension's portal back: «Salir de la dimensión» to the tower, «Permanecer» nothing.</summary>
        public static async Task UseWayBackAsync(NetworkStream stream, RegisteredInteractive interactive, InteractiveAction action)
        {
            if (action.SkillId != TravelersTower.LeaveSkill) return;
            var state = SessionContext.State;
            await InUseAsync(state.MapId, interactive.Element.Id, action.SkillId, state.CharacterId);
            Console.WriteLine($"[Tower] {state.CharacterName} leaves the dimension.");
            await TeleportHandler.ToMapAsync(stream, TravelersTower.PortalRoom, TravelersTower.BackCell);
        }

        /// <summary>The portal room loaded: the portal as it stands, its staging at its end (the capture's «hpm 100, 100, 4»).</summary>
        public static async Task OnMapLoadedAsync(GameSession session)
        {
            if (session.MapId != TravelersTower.PortalRoom) return;
            var open = TravelersTower.Open;
            if (open != null)
                await session.SendAsync(GuildRaidStagings.StandingEvent(open.Staging));
        }
    }
}
