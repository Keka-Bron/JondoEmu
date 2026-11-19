using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// JondoFix's monster group tooltip (MonsterGroupTooltip.cs): what a group on the map drops
    /// and which achievements beating it would earn, for the account's character in the world.
    /// </summary>
    /// <remarks>
    /// The client's tooltip does not know the group's id, only what is in it, so the mod sends
    /// the monsters -- <c>{"monsters": [[template, grade], ...]}</c> -- and two groups alike get
    /// the same answer, as they would the same loot. Every rule is <see cref="GroupPreview"/>'s.
    /// </remarks>
    public static partial class ControlApi
    {
        /// <summary>A group bigger than this is no group of the map's.</summary>
        private const int MostMonstersAsked = 16;

        private static Respuesta MonsterTooltip(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");

            var group = new List<GroupPreview.Member>();
            using (var doc = JsonDocument.Parse(cuerpo))
            {
                if (doc.RootElement.TryGetProperty("monsters", out var monsters) && monsters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var one in monsters.EnumerateArray())
                    {
                        if (one.ValueKind != JsonValueKind.Array || one.GetArrayLength() < 2) continue;
                        if (!one[0].TryGetInt32(out int monster) || !one[1].TryGetInt32(out int grade)) continue;
                        group.Add(new GroupPreview.Member(monster, Math.Clamp(grade, 1, 5)));
                        if (group.Count == MostMonstersAsked) break;
                    }
                }
            }
            if (group.Count == 0) return Mal(400, "monsters");

            using (SessionContext.Push(me))
            {
                int prospecting = FightHandler.ProspectingNow();
                var drops = GroupPreview.Drops(group, prospecting, me.State.MapId);
                var unlocks = GroupPreview.Unlocks(group, Achievements.Book, me.State.Achievements);
                return Bien(new
                {
                    prospecting,
                    achievements = unlocks.Select(u => new { id = u.Achievement, points = u.Points, monster = u.Monster }),
                    drops = drops.Select(d => new { item = d.Item, percent = Math.Round(d.Percent, 3) }),
                });
            }
        }
    }
}
