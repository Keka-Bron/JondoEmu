using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    public static partial class ChallengeWatcher
    {
        private const int NanoimpatienceIIIState = 695;

        /// <summary>Challenge 1088 permits enemy kills only while the victim has Nanoimpatience III.</summary>
        private static async Task DazahkAndWarEnemyDiedAsync(NetworkStream stream, FightInstance fight,
                                                            Fighter victim, Fighter? killer)
        {
            if (RuleInPlay(fight, Challenges.BossRule.OnlyKillNanoimpatienceIII)
                && !victim.Buffs.TieneEstado(NanoimpatienceIIIState))
                await BreakRuleAsync(stream, fight, Challenges.BossRule.OnlyKillNanoimpatienceIII,
                                     $"{victim.Name} dies without Nanoimpatience III");

            if (killer?.TeamId == 0 && IsWarfareWeapon(victim)
                && RuleInPlay(fight, Challenges.BossRule.DoNotKillOrDisplaceWarfareWeapons))
                await BreakRuleAsync(stream, fight, Challenges.BossRule.DoNotKillOrDisplaceWarfareWeapons,
                                     $"{killer.Name} kills War's weapon {victim.Name}");
        }

        private static async Task WarWeaponDisplacedAsync(NetworkStream stream, FightInstance fight,
                                                          Fighter target, Fighter source)
        {
            if (target.TeamId != 1 || source.TeamId != 0
                || !IsWarfareWeapon(target)
                || !RuleInPlay(fight, Challenges.BossRule.DoNotKillOrDisplaceWarfareWeapons)) return;
            await BreakRuleAsync(stream, fight, Challenges.BossRule.DoNotKillOrDisplaceWarfareWeapons,
                                 $"{source.Name} displaces War's weapon {target.Name}");
        }

        private static bool IsWarfareWeapon(Fighter fighter)
            => fighter.MonsterId is 6010 or 6011 or 6012 or 6013;
    }
}
