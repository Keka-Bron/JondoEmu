using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What an administrator puts on a map and takes off it: NPCs and groups of monsters, on the
    /// cell he stands on, seen at once by everybody on the map.
    /// </summary>
    /// <remarks>
    /// Until the server stops, like everything else that happens on a map while it runs: a group
    /// put here fights like any other and goes when beaten; an NPC put here talks like the ones of
    /// the world. What stands on a map for good is authored in content/npcs/spawns.json, by hand
    /// or with Jondo Studio.
    /// </remarks>
    public static class MapAdmin
    {
        /// <summary>An NPC on the map: who it is to the client, which NPC, and where.</summary>
        public sealed record NpcHere(long ContextualId, int NpcId, int Cell);

        /// <summary>A member of a group: the monster, its grade from 1, and its level.</summary>
        public sealed record MemberHere(int MonsterId, int Grade, int Level);

        /// <summary>A group of monsters on the map.</summary>
        public sealed record GroupHere(long MobId, int Cell, IReadOnlyList<MemberHere> Members);

        /// <summary>The most monsters one group can hold: a fight's eight.</summary>
        public const int MaxMembers = 8;

        /// <summary>What stands on this map: its NPCs and its groups of monsters.</summary>
        public static (IReadOnlyList<NpcHere> Npcs, IReadOnlyList<GroupHere> Groups) On(long mapId)
        {
            var npcs = Managers.Npcs.OnMap(mapId)
                .Select(s => new NpcHere(s.ContextualId, s.NpcId, s.Cell))
                .ToList();
            var groups = MobSpawnManager.GetMobsForMap(mapId)
                .Select(g => new GroupHere(g.MobId, g.CellId,
                    g.Members.Select(m => new MemberHere(m.Monster.Id, m.GradeIndex + 1, m.Level)).ToList()))
                .ToList();
            return (npcs, groups);
        }

        /// <summary>Puts an NPC on this map and cell, facing that way. Null when there is no such NPC.</summary>
        public static Npcs.Spawn? SpawnNpc(long mapId, int npcId, int cell, int orientation)
            => Npcs.PlaceAtRuntime(mapId, npcId, cell, Math.Clamp(orientation, 0, 7));

        /// <summary>
        /// Puts a group on this map and cell: each monster at its grade, counted from 1. Null when
        /// none of them is a monster the server knows, or there are more than a fight holds.
        /// </summary>
        public static MobSpawnManager.MobGroup? SpawnMonsters(long mapId, int cell, IReadOnlyList<(int Monster, int Grade)> members)
        {
            if (members.Count == 0 || members.Count > MaxMembers) return null;
            return MobSpawnManager.SpawnComposed(mapId, members.Select(m => (m.Monster, Math.Max(1, m.Grade) - 1)), cell);
        }

        /// <summary>Takes an NPC off this map. False when it was not there.</summary>
        public static bool RemoveNpc(long mapId, long contextualId) => Npcs.RemoveAtRuntime(mapId, contextualId);

        /// <summary>Takes a group off this map. False when it was not there.</summary>
        public static bool RemoveGroup(long mapId, long mobId) => MobSpawnManager.RemoveByHand(mapId, mobId);

        /// <summary>
        /// Everybody on the map sees it again as it now is: each one is sent the map's actors, as
        /// a quest does when it brings a monster out, and the end of the list after them.
        /// </summary>
        /// <remarks>
        /// The whole list rather than one actor added or taken away: it is the form the client is
        /// known to take mid-map (Quests.SpawnAsync), and the same list it is sent on arrival.
        /// </remarks>
        public static async Task<int> RefreshAsync(long mapId)
        {
            int told = 0;
            foreach (var viewer in SessionRegistry.OnMap(mapId))
            {
                if (viewer.Stream == null || viewer.State.IsInFight) continue;
                try
                {
                    using (SessionContext.Push(viewer))
                    {
                        var character = DatabaseManager.GetCharacterById(viewer.CharacterId);
                        if (character == null) continue;
                        await viewer.SendAsync(ConnectionProtocol.Push(Op.Jss, ConnectionProtocol.BuildMapActors(
                            mapId, character, viewer.State.CellId, viewer.State.Orientation, viewer.AccountId)));
                        await viewer.SendAsync(ConnectionProtocol.BuildActorsComplete());
                        told++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Mapa] No se ha podido repintar el mapa {mapId} a {viewer.State.CharacterName}: {ex.Message}");
                }
            }
            return told;
        }
    }
}
