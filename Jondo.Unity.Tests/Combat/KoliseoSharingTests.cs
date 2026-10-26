using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A won Koliseo pays each of its winners, planned for all of them before anybody sees the
    /// end: each end screen lists the partner's kamas, kolichas and experience too, as the
    /// capture's jyg does. Paid one by one, the partner came out with nothing.
    /// </summary>
    public class KoliseoSharingTests
    {
        [Fact]
        public void Both_winners_are_planned_and_the_losers_are_not()
        {
            const long first = 8_998_011, second = 8_998_012, third = 8_998_013, fourth = 8_998_014;
            var sessions = new[] { (first, 200), (second, 150), (third, 200), (fourth, 200) }
                .Select(p =>
                {
                    var session = GameSession.SinSocket();
                    session.BindAccount(p.Item1, 1);
                    session.State.CharacterId = p.Item1;
                    session.State.CharacterLevel = p.Item2;
                    session.EnterWorld();
                    Assert.True(SessionRegistry.Register(session));
                    return session;
                }).ToList();
            try
            {
                var fight = new FightInstance(8_998_011, 1) { Reglas = FightRules.Koliseo, KoliseoMode = 1 };
                fight.AddPlayer(new Fighter { Id = first, Level = 200, MaxHP = 100, CurrentHP = 100 });
                fight.AddPlayer(new Fighter { Id = second, Level = 150, MaxHP = 100, CurrentHP = 0 });
                fight.AddOpponent(new Fighter { Id = third, Level = 200, MaxHP = 100, CurrentHP = 0 });
                fight.AddOpponent(new Fighter { Id = fourth, Level = 200, MaxHP = 100, CurrentHP = 0 });

                FightHandler.PlanRewards(fight);

                foreach (var (winner, level) in new[] { (first, 200), (second, 150) })
                {
                    var reward = FightHandler.RewardOf(fight, winner);
                    Assert.NotNull(reward);
                    Assert.Equal(KoliseoRewards.KamasPorVictoria, reward!.Kamas);
                    Assert.Equal(KoliseoRewards.VitorichasPorVictoria, reward.Loot[KoliseoRewards.Vitoricha]);
                    Assert.True(reward.Loot[KoliseoRewards.Kolicha] >= KoliseoRewards.KolichasPorVictoria);
                    Assert.True(reward.Xp >= KoliseoRewards.Experiencia(level));
                }
                Assert.Null(FightHandler.RewardOf(fight, third));
                Assert.Null(FightHandler.RewardOf(fight, fourth));
            }
            finally
            {
                foreach (var session in sessions) SessionRegistry.Unregister(session);
            }
        }
    }
}
