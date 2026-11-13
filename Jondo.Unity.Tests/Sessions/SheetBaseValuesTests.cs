using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// The out-of-fight sheet's bases for what a fight also counts, measured on the real sheets:
    /// a new character has 6 action points and a level 200 has 7, and every character has a base
    /// of one summon. The sheet sent 6 and 0, one less of each than the fight then gave.
    /// </summary>
    public sealed class SheetBaseValuesTests
    {
        [Theory]
        [InlineData(1, 6)]
        [InlineData(99, 6)]
        [InlineData(100, 7)]
        [InlineData(200, 7)]
        public void The_seventh_action_point_comes_at_level_100(int level, int actionPoints)
        {
            Assert.Equal(actionPoints, ConnectionProtocol.ValueOf(ConnectionProtocol.Stat.ActionPoints, level));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(200)]
        public void Every_character_has_one_summon(int level)
        {
            Assert.Equal(1, ConnectionProtocol.ValueOf(ConnectionProtocol.Stat.Summons, level));
        }
    }
}
