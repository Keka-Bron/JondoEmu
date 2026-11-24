using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// "P" between placed monsters reaches only one that summons the caster's kind (the Tengu and
    /// his Yomi), never two placed monsters of the same kind: two Dobles do not pull through each
    /// other with Sutileza, two Bakazakos do not share their blows. And a hook that outlives its
    /// caster stays with the state it switches off: Mizz Frizz dead first, her allies' glacial
    /// invulnerability can still be pushed off.
    /// </summary>
    public class PlacedCompanionTests
    {
        private const int Doble = 3748, Sutileza = 4862;
        private const int Bakazako = 6390, KopiKazaShare = 17325;
        private const int MizzFrizz = 3391, Barrilicoso = 3385, HerOpening = 3517, GlacialInvulnerability = 935;

        private static Fighter Monster(long id, int template, int cell) => new()
        {
            Id = id, CellId = cell, MaxHP = 3000, CurrentHP = 3000, Level = 170, IsMonster = true,
            MonsterId = template, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        private static (FightInstance Fight, Fighter Character) Board(params Fighter[] monsters)
        {
            var fight = new FightInstance(1, 1);
            var character = new Fighter
            {
                Id = 10, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(character);
            foreach (var monster in monsters) fight.AddMonster(monster);
            fight.StartFight();
            return (fight, character);
        }

        [Fact]
        public void The_tengu_summons_the_yomi_and_a_doble_no_doble()
        {
            if (!SpellEffects.De(2683, 1).Any()) return;   // no spell data
            Assert.True(EffectEngine.SummonsTheKind(2967, 2891));
            Assert.False(EffectEngine.SummonsTheKind(Doble, Doble));
            Assert.False(EffectEngine.SummonsTheKind(Bakazako, Bakazako));
        }

        /// <summary>
        /// The Kwoknan (2995) calls his four Bwaks at once from the first turn; a Bwak placed in his
        /// group is no stand-in for that wave, so its Kwapoussée state does not lock his Kwarmée
        /// Kwayal (29789, "HS!5511").
        /// </summary>
        [Fact]
        public void A_placed_bwak_does_not_lock_the_kwoknans_wave()
        {
            if (!SpellEffects.De(29789, 1).Any()) return;   // no spell data
            Assert.False(EffectEngine.SummonsTheKind(2995, 3284));
        }

        [Fact]
        public void A_placed_doble_does_not_make_another_placed_doble_cast_sutileza()
        {
            var row = SpellEffects.De(Sutileza, 1).FirstOrDefault(r => r.EffectId == 792);
            if (row == null) return;
            var first = Monster(-1, Doble, 400);
            var second = Monster(-2, Doble, 404);
            var (fight, character) = Board(first, second);

            var reached = EffectEngine.ReachOf(fight, first, row, first.CellId, character.CellId);

            Assert.DoesNotContain(second, reached);
        }

        [Fact]
        public void Two_placed_bakazakos_do_not_share_their_blows()
        {
            var row = SpellEffects.De(KopiKazaShare, 3).FirstOrDefault(r => r.EffectId == 1061);
            if (row == null) return;
            var first = Monster(-1, Bakazako, 400);
            var second = Monster(-2, Bakazako, 404);
            var (fight, _) = Board(first, second);

            var reached = EffectEngine.ReachOf(fight, first, row, first.CellId, first.CellId);

            Assert.DoesNotContain(second, reached);
        }

        [Fact]
        public async Task Mizz_frizz_dead_her_allies_keep_both_the_invulnerability_and_its_push_switch()
        {
            if (!SpellEffects.De(HerOpening, 1).Any(r => r.Value == GlacialInvulnerability)) return;
            var frizz = Monster(-1, MizzFrizz, 400);
            var barrel = Monster(-2, Barrilicoso, 404);
            var (fight, character) = Board(frizz, barrel);
            frizz.Conducta = (HerOpening, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, frizz);
            Assert.True(barrel.Buffs.TieneEstado(GlacialInvulnerability));
            int hooks = barrel.Buffs.ActiveSpells.Count(e => e.Lanzador == frizz.Id);
            Assert.True(hooks > 0);

            await FightHandler.TestKillAsync(null, fight, frizz, character);

            Assert.True(barrel.Buffs.TieneEstado(GlacialInvulnerability));
            Assert.Equal(hooks, barrel.Buffs.ActiveSpells.Count(e => e.Lanzador == frizz.Id));
        }
    }
}
