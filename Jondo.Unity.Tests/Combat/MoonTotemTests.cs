using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The expedition's Moon (7802) has 200% resistances in every element; they drop only on the turn
    /// he carries the fourth of his stacked states, which his coloured totems (7804-7807) give him from
    /// their glyphs. Cara Oculta's 28952 brings the four totems out with a 2960 over fixed cells.
    /// </summary>
    public class MoonTotemTests
    {
        private const long MoonRoom = 239601165;
        private const int TotemCall = 28952;

        private static (FightInstance fight, Fighter mask) Board()
        {
            var fight = new FightInstance(9_999_159, MoonRoom, MoonRoom);
            var mask = new Fighter
            {
                Id = -5, TeamId = 1, CellId = 228, IsMonster = true, MonsterId = 7803, Level = 100,
                MaxHP = 3700, CurrentHP = 3700, MaxAP = 12, CurrentAP = 12, Invocador = -1,
            };
            fight.AddMonster(mask);
            fight.AddPlayer(new Fighter
            {
                Id = 10, TeamId = 0, CellId = 145, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            });
            return (fight, mask);
        }

        [Fact]
        public void Each_totem_comes_out_on_its_own_empty_cell()
        {
            var rows = SpellEffects.De(TotemCall, 1).Where(e => e.EffectId == 2960).ToList();
            if (rows.Count == 0) return;   // no data
            var (fight, mask) = Board();

            var cast = EffectEngine.Resolver(fight, mask, TotemCall, 1, mask, EffectEngine.AlLanzar,
                                             fight.RoundNumber, celdaApuntada: mask.CellId);

            var totems = cast.Where(o => o.Invoca is >= 7804 and <= 7807).ToList();
            Assert.Equal(new[] { 7804, 7805, 7806, 7807 }, totems.Select(o => o.Invoca).OrderBy(i => i));
            Assert.Equal(rows.Select(r => r.CeldasFijas[0]).OrderBy(c => c),
                         totems.Select(o => o.CasillaDeLaInvocacion).OrderBy(c => c));
            Assert.DoesNotContain(totems, o => o.CasillaDeLaInvocacion == mask.CellId);
        }
    }
}
