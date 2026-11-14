using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A dream's fight pays its intensity's experience, the XP percentage under the dream's name:
    /// 50 % in a Rêve I, 300 % in a Cauchemar III. It went as it was in every intensity.
    /// </summary>
    public class DreamExperienceTests
    {
        [Theory]
        [InlineData(1, 1000, 500)]     // Rêve I
        [InlineData(3, 1000, 1000)]    // Rêve III
        [InlineData(7, 1000, 1900)]    // Paradoxe IV
        [InlineData(10, 1000, 3000)]   // Cauchemar III
        public void A_dream_pays_its_intensitys_experience(int difficulty, long xp, long paid)
            => Assert.Equal(paid, FightHandler.DreamExperience(xp, difficulty));
    }
}
