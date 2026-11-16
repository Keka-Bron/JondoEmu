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

        /// <summary>
        /// Every jump costs the shadow a point of vitality (153 on herself, forever), and the one
        /// that leaves her with none loses the fight. A loss of vitality only ever held her at
        /// one point, and she went on jumping with the client drawing her life below zero.
        /// </summary>
        [Fact]
        public async Task The_jump_that_takes_her_last_point_of_vitality_kills_the_shadow()
        {
            const int Jump = 5703;
            if (SpellEffects.De(Jump, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var shadow = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 1, CurrentHP = 1, Level = 160,
                MaxAP = 10, CurrentAP = 10,
            };
            var bolgrot = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 475, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Bolgrot,
            };
            fight.AddPlayer(shadow); fight.AddMonster(bolgrot);

            await FightHandler.AplicarEfectosAsync(null, fight, shadow, Jump, 1, null,
                                                   EffectEngine.AlLanzar, 315);

            Assert.False(shadow.IsAlive);
        }

        /// <summary>
        /// The client adds every row of vitality to the top of the bar by itself, so the life sheet
        /// tells the life against that top: after two jumps on empty cells, 38 of 38 is no life
        /// missing at all. Told against the bare 40, the client took the two points off twice.
        /// </summary>
        [Fact]
        public async Task The_life_sheet_does_not_take_the_lost_vitality_off_twice()
        {
            const int Jump = 5703;
            if (SpellEffects.De(Jump, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var shadow = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 40, CurrentHP = 40, Level = 160,
                MaxAP = 10, CurrentAP = 10, LifeTopTold = 40,
            };
            var bolgrot = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 475, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Bolgrot,
            };
            fight.AddPlayer(shadow); fight.AddMonster(bolgrot);

            await FightHandler.AplicarEfectosAsync(null, fight, shadow, Jump, 1, null, EffectEngine.AlLanzar, 315);
            await FightHandler.AplicarEfectosAsync(null, fight, shadow, Jump, 1, null, EffectEngine.AlLanzar, 300);

            Assert.Equal(38, shadow.CurrentHP);
            Assert.Equal(38, shadow.MaxHP);
            Assert.Equal(0, FightHandler.LifeToldAgainstTheTop(shadow));
        }

        /// <summary>
        /// A fire next to the one the shadow lands on is pushed off and drawn back beside her, and
        /// drawn in it takes state 272 (its 5705, under MA) -- before the jump's burn row asks for
        /// it. Marked only once the whole jump had been applied, it was judged without the state,
        /// and a Salto Doble, whose burn row every fire on the board casts, killed her.
        /// </summary>
        [Theory]
        [InlineData(5707, 300, false)]   // Salto Doble, two cells off
        [InlineData(5703, 315, false)]   // Salto Astral, next to the fire
        [InlineData(5707, 300, true)]    // the same, the fires armed as a fight arms them
        [InlineData(5703, 315, true)]
        public async Task A_fire_pushed_off_and_drawn_back_beside_her_does_not_burn_her(int jump, int from, bool asBehaviour)
        {
            if (SpellEffects.De(jump, 1).Count == 0 || SpellEffects.De(5705, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var shadow = new Fighter
            {
                Id = 10, TeamId = 0, CellId = from, MaxHP = 40, CurrentHP = 40, Level = 160,
                MaxAP = 10, CurrentAP = 10,
            };
            var bolgrot = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 475, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Bolgrot,
            };
            // The fire she lands on, and one beyond it, in line.
            var fire = new Fighter
            {
                Id = -2, TeamId = 1, CellId = 329, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Fire, Invocador = bolgrot.Id,
            };
            var beyond = new Fighter
            {
                Id = -3, TeamId = 1, CellId = 344, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Fire, Invocador = bolgrot.Id,
            };
            fight.AddPlayer(shadow); fight.AddMonster(bolgrot); fight.AddMonster(fire); fight.AddMonster(beyond);
            shadow.Buffs.PonerEstado(272);
            bolgrot.Buffs.PonerEstado(272);
            await GiveItsFireSpellAsync(fight, fire, asBehaviour);
            await GiveItsFireSpellAsync(fight, beyond, asBehaviour);

            await FightHandler.AplicarEfectosAsync(null, fight, shadow, jump, 1, fire,
                                                   EffectEngine.AlLanzar, fire.CellId);

            Assert.False(fire.IsAlive);
            Assert.Equal(329, shadow.CellId);
            Assert.True(beyond.IsAlive);
            Assert.Equal(344, beyond.CellId);
            Assert.True(shadow.IsAlive);
            // And the jump takes the mark off again: it is this jump's alone.
            Assert.False(beyond.Buffs.TieneEstado(272));
        }

        /// <summary>
        /// Round six of a real fight: Inmovilismo has drawn Bolgrot next to the fire the shadow
        /// then jumps on, and a fire beside it that nothing had moved (no 272) is pushed off and
        /// drawn back with Bolgrot. Drawn back, it is marked, and it must not burn her. In the
        /// fight the fires carry 5705 as their behaviour, cast as they come in and its row armed
        /// on themselves; only an attitude was looked at, and that fire burned her.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_fire_drawn_back_with_Bolgrot_beside_the_landing_does_not_burn_her(bool asBehaviour)
        {
            const int Jump = 5703;
            if (SpellEffects.De(Jump, 1).Count == 0 || SpellEffects.De(5705, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var shadow = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 270, MaxHP = 40, CurrentHP = 12, Level = 160,
                MaxAP = 10, CurrentAP = 4,
            };
            var bolgrot = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 256, MaxHP = 1, CurrentHP = 1, Level = 1,
                IsMonster = true, MonsterId = Bolgrot,
            };
            fight.AddPlayer(shadow); fight.AddMonster(bolgrot);
            var fires = new System.Collections.Generic.Dictionary<int, Fighter>();
            foreach (var (id, cell, marked) in new[] { (-18, 242, false), (-19, 257, true), (-20, 188, true),
                                                        (-21, 243, true), (-22, 284, true), (-24, 116, true),
                                                        (-25, 88, true) })
            {
                var f = new Fighter
                {
                    Id = id, TeamId = 1, CellId = cell, MaxHP = 1, CurrentHP = 1, Level = 1,
                    IsMonster = true, MonsterId = Fire, Invocador = bolgrot.Id,
                };
                fight.AddMonster(f);
                await GiveItsFireSpellAsync(fight, f, asBehaviour);
                if (marked) f.Buffs.PonerEstado(272);
                fires[id] = f;
            }
            shadow.Buffs.PonerEstado(272);
            bolgrot.Buffs.PonerEstado(272);

            await FightHandler.AplicarEfectosAsync(null, fight, shadow, Jump, 1, fires[-19],
                                                   EffectEngine.AlLanzar, 257);

            Assert.False(fires[-19].IsAlive);
            Assert.Equal(257, shadow.CellId);
            Assert.Equal(242, fires[-18].CellId);
            Assert.True(shadow.IsAlive);
        }

        /// <summary>
        /// The astral jump pushes whatever stands around its landing one cell away, but Bolgrot's
        /// spirit has no CanBePushed bit in its template's m_flags (4045: 33872, against the fires'
        /// 40720), so it stays where it is. It used to be driven about with the fires.
        /// </summary>
        [Fact]
        public async Task The_astral_jump_drives_a_fire_but_not_Bolgrot()
        {
            const int Jump = 5703;
            if (SpellEffects.De(Jump, 1).Count == 0 || MonsterFlags.Of(Bolgrot) < 0 || MonsterFlags.Of(Fire) < 0) return;

            async Task<int> CellAfterTheJumpAsync(int template)
            {
                var fight = new FightInstance(1, 1);
                var shadow = new Fighter
                {
                    Id = 10, TeamId = 0, CellId = 300, MaxHP = 40, CurrentHP = 40, Level = 160,
                    MaxAP = 10, CurrentAP = 10,
                };
                var beside = new Fighter
                {
                    Id = -1, TeamId = 1, CellId = 330, MaxHP = 1, CurrentHP = 1, Level = 1,
                    IsMonster = true, MonsterId = template,
                };
                fight.AddPlayer(shadow); fight.AddMonster(beside);
                beside.Buffs.PonerEstado(272);
                await FightHandler.AplicarEfectosAsync(null, fight, shadow, Jump, 1, null,
                                                       EffectEngine.AlLanzar, 315);
                Assert.Equal(315, shadow.CellId);
                return beside.CellId;
            }

            Assert.NotEqual(330, await CellAfterTheJumpAsync(Fire));
            Assert.Equal(330, await CellAfterTheJumpAsync(Bolgrot));
        }

        /// <summary>
        /// A fire's 5705, as an attitude or as a fight gives it: its behaviour spell, cast as it
        /// comes in, its MA row armed on itself.
        /// </summary>
        private static async Task GiveItsFireSpellAsync(FightInstance fight, Fighter fire, bool asBehaviour)
        {
            if (!asBehaviour)
            {
                fire.Buffs.PonerActitud(5705);
                return;
            }
            fire.Conducta = (5705, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, fire);
            Assert.Contains(fire.Buffs.ActiveSpells, hook => hook.Hechizo == 5705);
        }

        /// <summary>A share of the life taken (Último Aliento's -50%) still never kills as it lands.</summary>
        [Fact]
        public void A_share_of_the_life_taken_leaves_one_point()
        {
            var fighter = new Fighter { Id = 10, MaxHP = 100, CurrentHP = 20 };
            fighter.Buffs.Poner(new Buff
            {
                EffectId = 153, Caracteristica = 11, Cuanto = -50, CaducaEnRonda = 99, ShareOfLife = true,
            }, () => 1);

            Assert.Equal(1, fighter.CurrentHP);
            Assert.False(fighter.PendingDeath);
        }
    }
}
