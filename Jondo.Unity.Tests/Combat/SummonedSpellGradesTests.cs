using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A summoned monster casts its spells at the grade its own grade gives them: each entry of
    /// «spellGrades» is «spell grade, monster level», one per monster grade, in the order of its
    /// grades. Read as «spell grade, monster grade», no Blop's level (52 to 60) was ever a grade and
    /// every summon fell to grade 1: a Blop Griotte called by a Royal Blop invoked Coco biblops.
    /// </summary>
    public class SummonedSpellGradesTests
    {
        private const int BlopGriotte = 275, Biblopicion = 471, Biblopetalo = 473, OcultaBiblop = 472,
                          Blopicion = 475, Blifo = 476;

        [Fact]
        public void A_summoned_Blop_Griotte_casts_its_own_element_s_grades()
        {
            var top = Summons.De(BlopGriotte, 5);
            if (top?.Plantilla != BlopGriotte) return;   // no monster data

            var spells = top.Hechizos.ToDictionary(s => s.Spell, s => s.Grade);
            Assert.Equal(2, spells[Biblopicion]);   // a Griotte biblop, 279, not a Coco one
            Assert.Equal(2, spells[Biblopetalo]);   // fire, not air
            Assert.Equal(2, spells[Blifo]);
            Assert.Equal(2, spells[Blopicion]);
            Assert.Equal(5, spells[OcultaBiblop]);  // the one that climbs with the grade
        }

        /// <summary>«0,52»: at its first grade the Blop has no Blopición at all.</summary>
        [Fact]
        public void A_spell_whose_grade_is_zero_is_not_had()
        {
            var first = Summons.De(BlopGriotte, 1);
            if (first?.Plantilla != BlopGriotte) return;

            var spells = first.Hechizos.ToDictionary(s => s.Spell, s => s.Grade);
            Assert.False(spells.ContainsKey(Blopicion));
            Assert.Equal(1, spells[OcultaBiblop]);
            Assert.Equal(2, spells[Biblopicion]);
        }
    }
}
