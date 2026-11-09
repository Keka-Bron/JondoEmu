using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A class passive goes into a fight at the grade the character's level opens only when its
    /// grades are ranks spread over levels; when they all open at level one they are the spell's
    /// own hooks, and the passive is grade one.
    /// </summary>
    public class PassiveGradeTests
    {
        [Theory]
        [InlineData(12830, 200, 6)]   // La Jarrita de Pandawa: +50 power from level 187.
        [InlineData(12830, 100, 3)]
        [InlineData(24941, 200, 13)]  // The Xelor's Syncro.
        [InlineData(21978, 200, 1)]   // La Sombra de Sram: grade 2 is its "on a kill" hook.
        [InlineData(20488, 200, 1)]   // The Tymador's, a single grade.
        public void A_passive_is_at_its_level_grade_only_when_its_grades_are_ranks(int passive, int level, int grade)
            => Assert.Equal(grade, SpellTable.PassiveGrade(passive, level));
    }
}
