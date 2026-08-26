using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The characteristic sheet carries the same characteristics, in the same order, in the same
    /// slots as the real server's.
    /// </summary>
    /// <remarks>
    /// The expected list is not a design choice: it is the 53 entries of the jxb from
    /// "combate contra 4 poutchs nivel 25", in their exact order. The slot matters as much as the
    /// number, because the client reads each characteristic from one particular place and reads
    /// zero when it is put in another.
    ///
    /// This caught two things at once. We were sending characteristic 84 — push damage — which the
    /// real server sends in none of its 53 entries, and sending it right where the elemental
    /// damages begin; and the five resistances were going in the slot for spent points when the
    /// real server puts them in the gear slot.
    ///
    /// Both are the kind that raise no error at all: the fight goes on, the panel shows numbers,
    /// and the only visible symptom is a damage preview reading a tenth of the real figure.
    /// </remarks>
    public class FightSheetTests
    {
        private static readonly int[] FromTheRealServer =
        {
            1, 23, 37, 33, 35, 36, 34, 58, 54, 56, 57, 55, 85, 87, 101, 27, 28, 93, 79, 78,
            0, 10, 11, 13, 14, 15, 16, 18, 19, 25, 26, 50, 75, 88, 89, 90, 91, 92, 95, 96,
            97, 102, 107, 150, 120, 121, 122, 123, 124, 125, 141, 142, 143,
        };

        [Fact]
        public void The_sheet_has_the_same_53_entries_in_the_same_order()
        {
            var ours = FightHandler.FichaParaLaGuardia(new Fighter())
                                   .Select(entry => entry.Characteristic)
                                   .ToArray();

            Assert.Equal(FromTheRealServer, ours);
        }

        [Fact]
        public void The_resistances_go_in_the_gear_slot_and_not_the_spent_points_one()
        {
            // With a brand new fighter every value is zero and both slots come out empty, so the
            // fighter is given a value first: what is being checked is which of the two it lands in.
            var fighter = new Fighter { EarthResPct = 37 };

            var entry = FightHandler.FichaParaLaGuardia(fighter).Find(e => e.Characteristic == 33);

            Assert.Equal(0, entry.Base);
            Assert.Equal(37, entry.Gear);
        }

        [Fact]
        public void Push_damage_is_not_in_the_sheet()
        {
            // Characteristic 84 appears in none of the real server's 53 entries. It is measured and
            // used — it feeds the collision formula — but it never travels in the full sheet, and
            // putting it back would shift every entry after it by one.
            var ours = FightHandler.FichaParaLaGuardia(new Fighter())
                                   .Select(entry => entry.Characteristic);

            Assert.DoesNotContain(84, ours);
        }

        [Fact]
        public void The_damage_multipliers_all_ship_at_a_hundred()
        {
            // The client estimates a hit by multiplying through these eleven. Anything that is not
            // 100 scales the whole preview, and a zero makes it vanish: shipping 10 where 100
            // belongs is exactly what made the preview read a tenth of the real number.
            int[] multipliers = { 107, 150, 120, 121, 122, 123, 124, 125, 141, 142, 143 };
            var sheet = FightHandler.FichaParaLaGuardia(new Fighter());

            foreach (int which in multipliers)
            {
                var entry = sheet.Find(e => e.Characteristic == which);
                Assert.Equal(100, entry.Base);
            }
        }
    }
}
