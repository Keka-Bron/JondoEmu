using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A Fin du rêve's end screen leaves out the monsters of its earlier waves that fell: the
    /// client keeps none of them once the next wave comes, and each went on the screen as a
    /// «???» at level 0.
    /// </summary>
    public class DreamWavesEndScreenTests
    {
        private static Fighter Monster(int wave, bool alive)
            => new Fighter { Id = -1, IsMonster = true, Wave = wave, MaxHP = 100, CurrentHP = alive ? 100 : 0 };

        [Fact]
        public void A_fallen_monster_of_an_earlier_wave_is_left_out()
        {
            Assert.True(FightHandler.FellInAnEarlierWave(Monster(3, alive: false), lastWave: 18));
            Assert.True(FightHandler.FellInAnEarlierWave(Monster(0, alive: false), lastWave: 18));
        }

        [Fact]
        public void The_last_wave_and_whoever_stands_stay()
        {
            Assert.False(FightHandler.FellInAnEarlierWave(Monster(18, alive: false), lastWave: 18));
            Assert.False(FightHandler.FellInAnEarlierWave(Monster(5, alive: true), lastWave: 18));
        }

        [Fact]
        public void A_fight_without_waves_keeps_every_monster()
            => Assert.False(FightHandler.FellInAnEarlierWave(Monster(0, alive: false), lastWave: 0));
    }
}
