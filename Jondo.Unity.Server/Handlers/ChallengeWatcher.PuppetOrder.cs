using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    public static partial class ChallengeWatcher
    {
        private const int PuppetMaster = 3476;
        private static readonly HashSet<int> PuppetTemplates = new HashSet<int>
            { 3477, 3478, 3479, 3480, 3481 };

        private sealed class PuppetOrder
        {
            internal readonly object Gate = new object();
            internal readonly List<long> PuppetSummonOrder = new List<long>();
        }

        private static readonly ConditionalWeakTable<FightInstance, PuppetOrder> PuppetOrders = new();

        /// <summary>
        /// Call after a successful summon has been added to the fight. The Puppeteer challenge
        /// orders its summoned puppets by the order in which they enter combat.
        /// </summary>
        internal static void PuppetSummoned(FightInstance fight, Fighter summoned, Fighter summoner)
        {
            if (summoned.TeamId != 1 || !summoned.EsInvocado || !PuppetTemplates.Contains(summoned.MonsterId)
                || summoner.MonsterId != PuppetMaster
                || !RuleInPlay(fight, Challenges.BossRule.PuppetsKilledInSummonOrder)) return;

            var state = PuppetOrders.GetValue(fight, static _ => new PuppetOrder());
            lock (state.Gate)
            {
                if (!state.PuppetSummonOrder.Contains(summoned.Id))
                    state.PuppetSummonOrder.Add(summoned.Id);
            }
        }

        /// <summary>Call before the summoned puppet is removed from the fight.</summary>
        private static async Task PuppetDiedAsync(NetworkStream stream, FightInstance fight, Fighter victim)
        {
            if (victim.TeamId != 1 || !victim.EsInvocado || !PuppetTemplates.Contains(victim.MonsterId)
                || !RuleInPlay(fight, Challenges.BossRule.PuppetsKilledInSummonOrder)
                || !PuppetOrders.TryGetValue(fight, out var state)) return;

            bool outOfOrder;
            lock (state.Gate)
            {
                int victimIndex = state.PuppetSummonOrder.IndexOf(victim.Id);
                if (victimIndex < 0) return;

                // A puppet killed earlier in the ordered list is no longer pending. Only a
                // still-living earlier puppet makes this death out of order; the victim is
                // already at zero HP when this hook runs.
                outOfOrder = state.PuppetSummonOrder.Take(victimIndex).Any(id =>
                    fight.Rojo.Any(enemy => enemy.Id == id && enemy.IsAlive));
                state.PuppetSummonOrder.RemoveAt(victimIndex);
            }

            if (outOfOrder)
                await BreakRuleAsync(stream, fight, Challenges.BossRule.PuppetsKilledInSummonOrder,
                    $"{victim.Name} dies before an older puppet summoned by the Puppeteer");
        }
    }
}
