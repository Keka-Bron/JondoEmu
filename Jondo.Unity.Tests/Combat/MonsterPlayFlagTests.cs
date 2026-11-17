using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// CanPlay (64) of a monster template: a creature without it takes no turn of its own, and
    /// what it does at turn start or end it does at its summoner's (FightHandler's turn start).
    /// </summary>
    public class MonsterPlayFlagTests
    {
        [Fact]
        public void Furyha_and_the_water_spirits_do_not_play()
        {
            // Furyha (4074), fought as a monster of her own, and the spirits Kelpi (4063), Caulerpo
            // (4066) and Nori (4067): m_flags without the bit.
            Assert.False(MonsterFlags.AllowsPlay(4074));
            Assert.False(MonsterFlags.AllowsPlay(4063));
            Assert.False(MonsterFlags.AllowsPlay(4066));
            Assert.False(MonsterFlags.AllowsPlay(4067));
        }

        [Fact]
        public void Ordinary_monsters_and_unknown_templates_play()
        {
            Assert.True(MonsterFlags.AllowsPlay(492));
            Assert.True(MonsterFlags.AllowsPlay(5192));
            // A template world.db does not have is taken as one that plays.
            Assert.True(MonsterFlags.AllowsPlay(0));
            Assert.True(MonsterFlags.AllowsPlay(-7));
        }
    }
}
