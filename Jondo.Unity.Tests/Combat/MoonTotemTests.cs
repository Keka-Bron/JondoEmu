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
        private const int TotemCall = 28952, Glyph = 28954, SwapCall = 28956, FirstMoonState = 5243;

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
    
        [Fact]
        public void A_totem_on_its_glyph_gives_Moon_a_state()
        {
            if (SpellEffects.De(Glyph, 1).Count == 0) return;   // no data
            var (fight, mask) = Board();
            var moon = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 300, IsMonster = true, MonsterId = 7802, Level = 220,
                MaxHP = 20000, CurrentHP = 20000, MaxAP = 12, CurrentAP = 12,
            };
            var totem = new Fighter
            {
                Id = -6, TeamId = 1, CellId = 147, IsMonster = true, MonsterId = 7804, Level = 100,
                MaxHP = 500, CurrentHP = 500, MaxAP = 6, CurrentAP = 6, Invocador = -5,
            };
            fight.AddMonster(moon);
            fight.AddMonster(totem);

            // The red glyph's 28954-1 has its totem cast 28954-5, whose 792 has Moon cast 28955 on
            // himself: the first of his four stacked states.
            var cast = EffectEngine.Resolver(fight, mask, Glyph, 1, totem, EffectEngine.AlLanzar,
                                             fight.RoundNumber, celdaApuntada: totem.CellId);
            Assert.Contains(cast, o => o.Sobre == moon && o.Efecto.EffectId == 950 && o.Efecto.Value == FirstMoonState);
        }
    
        [Fact]
        public void At_the_end_of_his_turn_the_player_swaps_with_a_totem()
        {
            if (SpellEffects.De(SwapCall, 4).Count == 0) return;   // no data
            var (fight, mask) = Board();
            var totem = new Fighter
            {
                Id = -6, TeamId = 1, CellId = 147, IsMonster = true, MonsterId = 7804, Level = 100,
                MaxHP = 500, CurrentHP = 500, MaxAP = 6, CurrentAP = 6, Invocador = -5,
            };
            fight.AddMonster(totem);
            var player = fight.Todos.First(f => f.Id == 10);

            // Moon's 28956 hangs a "TE" 28956-4 on each player; it picks a totem, which casts the
            // swap back. The swap's other party is the totem, and the frame is sent in its name.
            var cast = EffectEngine.Resolver(fight, player, SwapCall, 4, player, EffectEngine.AlLanzar,
                                             fight.RoundNumber, celdaApuntada: player.CellId);
            var swap = Assert.Single(cast, o => o.Efecto.EffectId == 8);
            Assert.Same(player, swap.Sobre);
            Assert.Same(totem, swap.Tambien);
            Assert.Equal(145, totem.CellId);
            Assert.Equal(147, player.CellId);
        }
    }
}
