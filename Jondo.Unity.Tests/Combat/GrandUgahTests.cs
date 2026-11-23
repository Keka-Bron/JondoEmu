using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Grand Ugah summons one monster of his zone a turn (1265), up to six on the field.
    /// With no cap he piled them up as long as the fight lasted.
    /// </summary>
    [Collection("MapManager")]
    public class GrandUgahTests
    {
        [Fact]
        public void He_controls_six_summons_at_a_time()
        {
            if (!System.IO.File.Exists(Paths.ContentFile(MonsterSummonCaps.File))) return;
            MonsterSummonCaps.Reload();
            Assert.Equal(6, MonsterSummonCaps.For(1159));
        }
    }
}
