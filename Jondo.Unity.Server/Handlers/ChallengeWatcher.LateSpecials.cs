using System.Net.Sockets;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    public static partial class ChallengeWatcher
    {
        private const int PacifistState = 218;
        private const int NecronyxState = 571;
        private const int ChocomatosisState = 952;
        private const int NeverResurrectAnEnemyMoreThanTwice = 1083;
        private const int EnemyAllySwapBetweenTurns = 1081;
        private const int MalamebaDeadline = 1077;
        private const int MalamebaMonsterId = 3822;

        private sealed class LateSpecialState
        {
            internal readonly object Gate = new object();
            internal readonly System.Collections.Generic.Dictionary<long, int> EnemyResurrections = new();
            internal readonly System.Collections.Generic.Dictionary<long, SwapCycle> SwapCycles = new();
        }

        private sealed class SwapCycle
        {
            internal bool HasCompletedFirstTurn;
            internal bool InTurn;
            internal bool SwappedSinceLastTurn;
        }

        private static readonly ConditionalWeakTable<FightInstance, LateSpecialState> _lateSpecialStates = new();

        private static LateSpecialState LateState(FightInstance fight) => _lateSpecialStates.GetValue(fight, _ => new LateSpecialState());

        private static bool LateSpecialInPlay(FightInstance fight, int id)
        {
            foreach (var (fixedId, _) in fight.ChallengesFixed)
                if (fixedId == id && !fight.ChallengesBroken.Contains(id)) return true;
            return false;
        }

        /// <summary>Pacifist (state 218) may never be applied to an ally in Klim or Nileza.</summary>
        public static async Task LateSpecialStateAddedAsync(NetworkStream stream, FightInstance fight,
                                                             Fighter target, int state)
        {
            if (state == PacifistState && target.TeamId == 0)
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoPacifistAllies,
                                     $"{target.Name} enters Pacifist state");

            // Challenge spell 29357 removes the player's pending marker on EON952.
            if (state == ChocomatosisState && target.TeamId == 0
                && RuleInPlay(fight, Challenges.BossRule.EachAllyEntersChocomatosis))
                Notes(fight).AlliesWithoutChocomatosis?.Remove(target.Id);

            // Catalogue challenge spells 26011 and 26012 watch EON571 on non-boss enemies.
            if (state == NecronyxState && target.TeamId == 1)
            {
                foreach (int id in WithRule(fight, Challenges.BossRule.NoEarlyNecronyx))
                {
                    if (OtherThanLivingBoss(fight, target, id))
                        await BreakOneAsync(stream, fight, id, $"{target.Name} enters Necronyx before the boss dies");
                }
            }
        }

        private static async Task CheckDiseaseDamageAsync(NetworkStream stream, FightInstance fight,
                                                          Fighter source, Fighter target)
        {
            if (!RuleInPlay(fight, Challenges.BossRule.NoDiseasedAllyDamage)) return;

            // The original check (spell 26047) uses the seven disease states 1615–1621.
            if (source.Buffs.Estados.Any(state => state is >= 1615 and <= 1621))
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoDiseasedAllyDamage,
                                     $"{source.Name} damages {target.Name} while diseased");
        }

        /// <summary>Counts successful enemy resurrections; a third resurrection breaks challenge 1083.</summary>
        public static async Task LateSpecialEnemyResurrectedAsync(NetworkStream stream, FightInstance fight,
                                                                   Fighter revived)
        {
            if (revived.TeamId != 1 || revived.MonsterId == 4744
                || !LateSpecialInPlay(fight, NeverResurrectAnEnemyMoreThanTwice)) return;
            int count;
            var notes = LateState(fight);
            lock (notes.Gate)
            {
                notes.EnemyResurrections.TryGetValue(revived.Id, out count);
                count = notes.EnemyResurrections[revived.Id] = count + 1;
            }
            if (count > 2)
                await BreakOneAsync(stream, fight, NeverResurrectAnEnemyMoreThanTwice,
                                $"{revived.Name} is resurrected for the {count}th time");
        }

        /// <summary>
        /// At each enemy turn start after its first completed turn, challenge 1081 requires that enemy
        /// to have swapped cells with an ally during the interval since its previous turn.
        /// </summary>
        public static async Task LateSpecialEnemyTurnStartedAsync(NetworkStream stream, FightInstance fight,
                                                                    Fighter enemy)
        {
            if (enemy.TeamId == 0 || !enemy.IsAlive) return;
            if (enemy.MonsterId == MalamebaMonsterId && LateSpecialInPlay(fight, MalamebaDeadline))
                await BreakOneAsync(stream, fight, MalamebaDeadline,
                                    $"{enemy.Name} starts its next turn before being defeated");

            if (!LateSpecialInPlay(fight, EnemyAllySwapBetweenTurns)) return;
            var notes = LateState(fight);
            bool failed = false;
            lock (notes.Gate)
            {
                if (!notes.SwapCycles.TryGetValue(enemy.Id, out var cycle))
                    notes.SwapCycles[enemy.Id] = cycle = new SwapCycle();
                if (cycle.HasCompletedFirstTurn && !cycle.SwappedSinceLastTurn) failed = true;
                if (cycle.HasCompletedFirstTurn) cycle.SwappedSinceLastTurn = false;
                cycle.InTurn = true;
            }
            if (failed)
                await BreakOneAsync(stream, fight, EnemyAllySwapBetweenTurns,
                                $"{enemy.Name} starts its turn without swapping with an ally since its previous turn");
        }

        public static void LateSpecialEnemyTurnEnded(FightInstance fight, Fighter enemy)
        {
            if (enemy.TeamId == 0 || !LateSpecialInPlay(fight, EnemyAllySwapBetweenTurns)) return;
            var notes = LateState(fight);
            lock (notes.Gate)
            {
                if (!notes.SwapCycles.TryGetValue(enemy.Id, out var cycle))
                    notes.SwapCycles[enemy.Id] = cycle = new SwapCycle();
                cycle.HasCompletedFirstTurn = true;
                cycle.InTurn = false;
                cycle.SwappedSinceLastTurn = false;
            }
        }

        /// <summary>Call only for a completed position exchange, not a push or a pull.</summary>
        public static void LateSpecialPositionsSwapped(FightInstance fight, Fighter first, Fighter second)
        {
            if (first.TeamId == second.TeamId || !LateSpecialInPlay(fight, EnemyAllySwapBetweenTurns)) return;
            var enemy = first.TeamId == 1 ? first : second;
            var notes = LateState(fight);
            lock (notes.Gate)
            {
                if (notes.SwapCycles.TryGetValue(enemy.Id, out var cycle)
                    && cycle.HasCompletedFirstTurn && !cycle.InTurn)
                    cycle.SwappedSinceLastTurn = true;
            }
        }

    }
}
