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
        private const int MobLasponja = 928;
        private const int HungrySunflower = 799;
        private const int Lasoberana = 3996;
        private const int TregenariaEgg = 3997;
        private const int SilfElRasgabolMayor = 1071;
        private const int RasgabolMenor = 1084;
        private const int Minotauroro = 121;
        private const int TofuLance = 120;
        private const int Pikabeto = 1179;
        private const int Kepika = 849;
        private const int PapaNoes = 1194;

        private sealed class EarlySpecialState
        {
            public int LastLasponjaDisplacedRound;
            public readonly System.Collections.Generic.Dictionary<long, int> HungrySunflowerSummonTurnStarts = new();
            public readonly System.Collections.Generic.HashSet<long> EnemiesCompletedTurn = new();
            public readonly System.Collections.Generic.HashSet<long> EnemiesDamagedSinceTurn = new();
            public readonly System.Collections.Generic.HashSet<long> EnemiesHadTurnWithoutMp = new();
            public bool PapaNoesPuppetKilledByAlly;
        }

        private static readonly ConditionalWeakTable<FightInstance, EarlySpecialState> EarlySpecialStates = new();

        /// <summary>Call from DisplacedAsync with its effect type; swaps and carries do not count.</summary>
        public static void EarlySpecialDisplaced(FightInstance fight, Fighter target, bool pushed, bool pulled)
        {
            if ((!pushed && !pulled) || target.MonsterId != MobLasponja
                || !RuleInPlay(fight, Challenges.BossRule.PasarLaEsponjaEachRound)) return;
            var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
            lock (state) state.LastLasponjaDisplacedRound = fight.RoundNumber;
        }

        /// <summary>Call at the start of a new global round with the round that just ended.</summary>
        public static async Task EarlySpecialRoundStartedAsync(NetworkStream stream, FightInstance fight,
                                                                int previousRound)
        {
            if (!RuleInPlay(fight, Challenges.BossRule.PasarLaEsponjaEachRound) || previousRound < 2) return;
            var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
            int displacedRound;
            lock (state) displacedRound = state.LastLasponjaDisplacedRound;
            if (fight.Rojo.Exists(enemy => enemy.IsAlive && enemy.MonsterId == MobLasponja)
                && displacedRound != previousRound)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.PasarLaEsponjaEachRound,
                    $"Mob Lasponja was not pushed or pulled during global round {previousRound}");
            }
        }

        /// <summary>Call on death before the victim is removed from the fight.</summary>
        public static async Task EarlySpecialMonsterDiedAsync(NetworkStream stream, FightInstance fight,
                                                               Fighter victim)
        {
            if (victim.MonsterId != MobLasponja || fight.RoundNumber < 2
                || !RuleInPlay(fight, Challenges.BossRule.PasarLaEsponjaEachRound)) return;
            var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
            int displacedRound;
            lock (state) displacedRound = state.LastLasponjaDisplacedRound;
            if (displacedRound != fight.RoundNumber)
                await BreakRuleAsync(stream, fight, Challenges.BossRule.PasarLaEsponjaEachRound,
                    $"Mob Lasponja dies in global round {fight.RoundNumber} before being pushed or pulled");
        }

        /// <summary>Call from DiedAsync with the credited killer for Presas del encanto (987).</summary>
        public static async Task EarlySpecialDiedAsync(NetworkStream stream, FightInstance fight,
                                                        Fighter victim, Fighter? killer)
        {
            if (victim.TeamId == 1 && victim.MonsterId == PapaNoes
                && RuleInPlay(fight, Challenges.BossRule.PapaNoesPuppetBeforeBoss))
            {
                var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
                bool puppetKilled;
                lock (state) puppetKilled = state.PapaNoesPuppetKilledByAlly;
                if (!puppetKilled)
                    await BreakRuleAsync(stream, fight, Challenges.BossRule.PapaNoesPuppetBeforeBoss,
                        "Papa No-es dies before an ally kills one of his puppets");
            }
            // Spell 913 transforms a unit into one of the puppet templates watched by spell 28598.
            else if (victim.TeamId == 1 && victim.MonsterId is >= 1189 and <= 1193
                && killer?.TeamId == 0
                && RuleInPlay(fight, Challenges.BossRule.PapaNoesPuppetBeforeBoss))
            {
                var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
                lock (state) state.PapaNoesPuppetKilledByAlly = true;
            }

            if (victim.TeamId != 0
                && RuleInPlay(fight, Challenges.BossRule.EveryEnemyUsesNoMpTurnBeforeDeath))
            {
                var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
                bool hadTurn;
                lock (state) hadTurn = state.EnemiesHadTurnWithoutMp.Contains(victim.Id);
                if (!hadTurn)
                    await BreakRuleAsync(stream, fight, Challenges.BossRule.EveryEnemyUsesNoMpTurnBeforeDeath,
                        $"{victim.Name} dies without having played a turn that spent no MP");
            }

            if (victim.TeamId == 0 || killer == null || killer.TeamId != 0
                || !RuleInPlay(fight, Challenges.BossRule.IncurableFightersOnlyKill)) return;
            if (!SpellStates.IsIncurable(killer))
                await BreakRuleAsync(stream, fight, Challenges.BossRule.IncurableFightersOnlyKill,
                    $"{killer.Name} kills an enemy without being Incurable");
        }

        /// <summary>Call from TurnEndedAsync before it returns for an enemy turn.</summary>
        public static async Task EarlySpecialTurnEndedAsync(NetworkStream stream, FightInstance fight, Fighter fighter)
        {
            if (fighter.TeamId == 1 && fighter.IsAlive && fighter.MonsterId == Pikabeto
                && RuleInPlay(fight, Challenges.BossRule.PikabetoAtMostOneKepikaAtTurnEnd)
                && fight.Rojo.Count(monster => monster.IsAlive && monster.MonsterId == Kepika) > 1)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.PikabetoAtMostOneKepikaAtTurnEnd,
                    "more than one Kepika is alive at the end of Pikabeto's turn");
            }

            if (fighter.TeamId == 1 && RuleInPlay(fight, Challenges.BossRule.RataCortadaDamageBetweenTurns))
            {
                var notes = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
                lock (notes)
                {
                    notes.EnemiesCompletedTurn.Add(fighter.Id);
                    notes.EnemiesDamagedSinceTurn.Remove(fighter.Id);
                }
            }

            if (fighter.TeamId == 0 || !RuleInPlay(fight, Challenges.BossRule.EveryEnemyUsesNoMpTurnBeforeDeath))
                return;
            int spentMp = fight.TurnStartMp - fighter.CurrentMP - fight.TurnTackledMp;
            if (spentMp != 0) return;
            var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
            lock (state) state.EnemiesHadTurnWithoutMp.Add(fighter.Id);
        }

        /// <summary>Call from DamagedAsync after positive life damage to an enemy.</summary>
        public static void EarlySpecialEnemyDamaged(FightInstance fight, Fighter target, int lifeDamage)
        {
            if (lifeDamage <= 0 || target.TeamId == 0
                || !RuleInPlay(fight, Challenges.BossRule.RataCortadaDamageBetweenTurns)) return;
            var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
            lock (state) state.EnemiesDamagedSinceTurn.Add(target.Id);
        }

        /// <summary>
        /// Call at enemy turn start. Each turn after the first requires damage since the end of the preceding turn.
        /// </summary>
        public static async Task EarlySpecialEnemyTurnStartedAsync(NetworkStream stream, FightInstance fight,
                                                                    Fighter enemy)
        {
            if (enemy.MonsterId == SilfElRasgabolMayor && enemy.IsAlive
                && RuleInPlay(fight, Challenges.BossRule.RasgabolasMenoresMaxTwoAtBossTurn)
                && fight.Rojo.Count(monster => monster.IsAlive && monster.MonsterId == RasgabolMenor) > 2)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.RasgabolasMenoresMaxTwoAtBossTurn,
                    "more than two lesser Rasgabolas are alive when Silf starts its turn");
            }

            if (RuleInPlay(fight, Challenges.BossRule.MalaSemillaSummonSecondTurn)
                && enemy.TeamId == 1 && enemy.EsInvocado && enemy.IsAlive
                && IsSummonedBy(fight, enemy, HungrySunflower))
            {
                var state = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
                int starts;
                lock (state)
                {
                    starts = state.HungrySunflowerSummonTurnStarts.GetValueOrDefault(enemy.Id) + 1;
                    state.HungrySunflowerSummonTurnStarts[enemy.Id] = starts;
                }
                if (starts >= 2)
                    await BreakRuleAsync(stream, fight, Challenges.BossRule.MalaSemillaSummonSecondTurn,
                        $"a Hungry Sunflower summon {enemy.Name} begins its second turn");
            }

            if (enemy.MonsterId == TregenariaEgg && enemy.IsAlive && enemy.EsInvocado
                && IsSummonedBy(fight, enemy, Lasoberana)
                && RuleInPlay(fight, Challenges.BossRule.LasoberanaEggKilledBeforeNextTurn))
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.LasoberanaEggKilledBeforeNextTurn,
                    "a Tregenaria egg summoned by Lasoberaña begins its turn");
            }

            if (enemy.TeamId == 0 || !enemy.IsAlive
                || !RuleInPlay(fight, Challenges.BossRule.RataCortadaDamageBetweenTurns)) return;
            var enemyState = EarlySpecialStates.GetValue(fight, _ => new EarlySpecialState());
            bool playedBefore;
            bool damaged;
            lock (enemyState)
            {
                playedBefore = enemyState.EnemiesCompletedTurn.Contains(enemy.Id);
                damaged = enemyState.EnemiesDamagedSinceTurn.Remove(enemy.Id);
            }
            if (playedBefore && !damaged)
                await BreakRuleAsync(stream, fight, Challenges.BossRule.RataCortadaDamageBetweenTurns,
                    $"{enemy.Name} starts a turn without having taken damage since its previous turn");
        }

        /// <summary>Call after positive damage to Minotauroro, alongside other boss damage rules.</summary>
        public static async Task EarlySpecialBossDamagedAsync(NetworkStream stream, FightInstance fight,
                                                               Fighter target, int lifeDamage)
        {
            if (lifeDamage <= 0 || target.MonsterId != Minotauroro
                || !RuleInPlay(fight, Challenges.BossRule.MinotauroroUntouchedWhileTofuAlive)
                || !HasLivingSummonOwnedBy(fight, target.Id, TofuLance)) return;
            await BreakRuleAsync(stream, fight, Challenges.BossRule.MinotauroroUntouchedWhileTofuAlive,
                $"{target.Name} takes damage while one of its summoned tofus is alive");
        }

        public static async Task TotemLifeLostAsync(NetworkStream stream, FightInstance fight, Fighter target)
        {
            // Darkli Moon's four elemental totems, as referenced by challenge spell 26051.
            if (target.TeamId == 1 && target.MonsterId is >= 2815 and <= 2818)
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoLifeLossForDarkliMoonTotems,
                                     $"{target.Name} loses life");
        }

        private static bool HasLivingSummonOwnedBy(FightInstance fight, long bossId, int templateId)
        {
            return fight.Rojo.Exists(summon => summon.IsAlive && summon.EsInvocado
                && summon.Invocador == bossId && summon.MonsterId == templateId);
        }

        private static bool IsSummonedBy(FightInstance fight, Fighter summon, int bossMonsterId)
        {
            var boss = fight.Rojo.Find(enemy => enemy.MonsterId == bossMonsterId);
            return boss != null && summon.Invocador == boss.Id;
        }
    }
}
