using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Bolgrot's spirit («Extinción de los fuegos», 1654) lays glyphs at the start of his turn and
    /// sets them off at the next (5704 grade 3: «Activa los glifos», then «Disipa los glifos»): each
    /// brings out a fire on its empty cell (5704 grade 4, a summon of 4043), and the astral shadow
    /// jumps on the fires to put them out. Set off only under their owner, as a class glyph is, the
    /// glyphs were dispelled turn after turn and no fire ever came out.
    /// </summary>
    [Collection("MapManager")]
    public class BolgrotFireTests
    {
        private const int Bolgrot = 4045, Fire = 4043, FireSpell = 5704, GlyphLayer = 5776;

        [Fact]
        public async Task A_monster_s_glyph_set_off_by_its_owner_goes_off_on_its_empty_cell()
        {
            if (SpellEffects.De(FireSpell, 3).Count == 0 || SpellEffects.De(FireSpell, 4).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var shadow = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 100, MaxHP = 40, CurrentHP = 40, Level = 160,
                MaxAP = 10, CurrentAP = 10,
            };
            var bolgrot = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 475, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Bolgrot,
            };
            fight.AddPlayer(shadow); fight.AddMonster(bolgrot);

            // Last turn's glyph, on a cell nobody stands on.
            var glyph = fight.Poner(new Glifo(bolgrot.Id, new[] { 303 }, FireSpell, 4, 0x8A0078,
                                              fight.RoundNumber + 2, "a,A", Disparo.AlEmpezarElTurno)
            {
                Tipo = 401, HechizoQueLoPuso = GlyphLayer, GradoQueLoPuso = 1,
            });

            await FightHandler.AplicarEfectosAsync(null, fight, bolgrot, FireSpell, 3, null,
                                                   EffectEngine.AlLanzar, bolgrot.CellId);

            var fire = Assert.Single(fight.Todos, f => f.MonsterId == Fire && f.IsAlive);
            Assert.Equal(303, fire.CellId);
            Assert.DoesNotContain(glyph, fight.Glifos);
            Assert.Equal(40, shadow.CurrentHP);
        }

        /// <summary>
        /// Salto Astral (5703) puts out the fire it is aimed at (141) and then teleports the shadow
        /// onto its cell. The rows of a cast are resolved before they are applied, and the fire,
        /// still counted alive, kept its cell: the shadow stayed beside it and the doomed fire's
        /// own rows killed her.
        /// </summary>
        [Fact]
        public async Task The_astral_jump_lands_on_the_fire_it_puts_out()
        {
            const int Jump = 5703;
            if (SpellEffects.De(Jump, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var shadow = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 40, CurrentHP = 40, Level = 160,
                MaxAP = 10, CurrentAP = 10,
            };
            var bolgrot = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 475, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Bolgrot,
            };
            var fire = new Fighter
            {
                Id = -2, TeamId = 1, CellId = 315, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Fire, Invocador = bolgrot.Id,
            };
            fight.AddPlayer(shadow); fight.AddMonster(bolgrot); fight.AddMonster(fire);

            await FightHandler.AplicarEfectosAsync(null, fight, shadow, Jump, 1, fire,
                                                   EffectEngine.AlLanzar, fire.CellId);

            Assert.False(fire.IsAlive);
            Assert.Equal(315, shadow.CellId);
            Assert.True(shadow.IsAlive);
            Assert.Empty(fight.StruckDownThisCast);
        }
    }
}
