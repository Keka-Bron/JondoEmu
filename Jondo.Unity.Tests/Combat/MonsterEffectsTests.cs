using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The effects the monsters use and the classes hardly do: zones of fixed cells, glyphs
    /// dispelled, buffs dispelled, the forced push.
    /// </summary>
    public class MonsterEffectsTests
    {
        private static Fighter Fighter(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 1000, CurrentHP = 1000, Level = 100,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
        };

        private static List<Outcome> Resolve(FightInstance fight, Fighter caster, Fighter target, SpellEffect row, int aimed)
            => EffectEngine.ResolveEffects(fight, caster, 1, 1, target, EffectEngine.AlLanzar, fight.RoundNumber,
                                           new[] { row }, aimedCell: aimed);

        /// <summary>
        /// A ';' zone names its cells outright: spell 1514's summons go on cells 510, 496, 483...
        /// one each, whatever cell the cast was aimed at.
        /// </summary>
        [Fact]
        public void A_zone_of_fixed_cells_is_its_cells()
        {
            var rows = SpellEffects.De(1514, 1).Where(r => r.EffectId == 181).ToList();

            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(';', r.Forma));
            Assert.Contains(rows, r => r.CeldasFijas.SequenceEqual(new[] { 510 }));
            Assert.Equal(new[] { 510 }, EffectEngine.CasillasDelEfecto(rows.First(r => r.CeldasFijas[0] == 510), 300, 42));
        }

        /// <summary>"Disipa los glifos" on the caster takes his glyphs -- only the named spell's when it names one.</summary>
        [Fact]
        public void Dispel_glyphs_takes_the_caster_s_glyphs_of_the_named_spell()
        {
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 0, 300);
            fight.AddPlayer(caster);
            var mine = fight.Poner(new Glifo(1, new[] { 310 }, 99, 1, 0, 0, "", Disparo.AlEmpezarElTurno) { HechizoQueLoPuso = 29575 });
            var other = fight.Poner(new Glifo(1, new[] { 320 }, 98, 1, 0, 0, "", Disparo.AlEmpezarElTurno) { HechizoQueLoPuso = 1 });
            var someoneElses = fight.Poner(new Glifo(2, new[] { 330 }, 97, 1, 0, 0, "", Disparo.AlEmpezarElTurno) { HechizoQueLoPuso = 29575 });

            var outcomes = Resolve(fight, caster, caster,
                new SpellEffect { EffectId = 2018, TargetMask = "C", DiceNum = 29575, Triggers = "I" }, caster.CellId);

            var gone = Assert.Single(outcomes).GlifosQuitados;
            Assert.Equal(new[] { mine }, gone);
            Assert.Contains(other, fight.Glifos);
            Assert.Contains(someoneElses, fight.Glifos);
        }

        /// <summary>"Retira los embrujos" takes the dispellable rows and leaves the rest.</summary>
        [Fact]
        public void Dispel_takes_only_what_can_be_dispelled()
        {
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 0, 300);
            var target = Fighter(2, 1, 301);
            fight.AddPlayer(caster); fight.AddOpponent(target);
            int n = 0;
            target.Buffs.PonerEstado(56);
            target.Buffs.Poner(new Buff { EffectId = 950, Estado = 56, Dispellable = 1, CaducaEnRonda = -1 }, () => ++n);
            target.Buffs.Poner(new Buff { EffectId = 128, Caracteristica = 23, Cuanto = 2, Dispellable = 3, CaducaEnRonda = -1 }, () => ++n);

            Resolve(fight, caster, target, new SpellEffect { EffectId = 132, TargetMask = "A", Triggers = "I" }, target.CellId);

            Assert.False(target.Buffs.TieneEstado(56));
            Assert.Single(target.Buffs.Puestos);
            Assert.Equal(128, target.Buffs.Puestos[0].EffectId);
        }

        /// <summary>A threshold is a row on the bearer holding its percentage and the spell that put it.</summary>
        [Fact]
        public void A_threshold_is_held_on_the_bearer()
        {
            var fight = new FightInstance(1, 1);
            var boss = Fighter(-1, 1, 300);
            fight.AddOpponent(boss);

            var outcomes = EffectEngine.ResolveEffects(fight, boss, 15280, 1, boss, EffectEngine.AlLanzar, fight.RoundNumber,
                new[] { new SpellEffect { EffectId = EffectEngine.Umbral, TargetMask = "C", DiceNum = 80, Duration = -1, Triggers = "I" } },
                aimedCell: boss.CellId);

            var row = Assert.Single(outcomes).Buff;
            Assert.Equal(EffectEngine.Umbral, row.EffectId);
            Assert.Equal(80, row.Cuanto);
            Assert.Equal(15280, row.HechizoOrigen);
            Assert.Equal("TR15280", EffectEngine.AlCruzarElUmbral(15280));
        }

        /// <summary>"Invoca al último aliado muerto" is the caster's, with the life its die says, where it was aimed.</summary>
        [Fact]
        public void Reviving_is_asked_of_the_fight()
        {
            var fight = new FightInstance(1, 1);
            var caster = Fighter(-1, 1, 300);
            fight.AddOpponent(caster);

            var outcomes = Resolve(fight, caster, caster,
                new SpellEffect { EffectId = 1034, TargetMask = "a,A", DiceNum = 13, Triggers = "I" }, 320);

            var revive = Assert.Single(outcomes);
            Assert.Equal(13, revive.Revive);
            Assert.Equal(320, revive.CasillaDeLaInvocacion);
            Assert.Same(caster, revive.Caster);
        }

        /// <summary>The masks the triggers carry, read from the one waiting.</summary>
        [Theory]
        [InlineData("a,F2992", 1, 2992, true)]
        [InlineData("a,F2992", 0, 2992, false)]
        [InlineData("A", 0, 0, true)]
        [InlineData("g", 1, 5, true)]
        [InlineData("H,M,D", 0, 0, true)]
        [InlineData("h,m,d", 0, 0, false)]
        public void A_trigger_s_mask_names_who_it_should(string mask, int team, int template, bool names)
        {
            var waiting = new Fighter { Id = -1, TeamId = 1, IsMonster = true, MonsterId = 1, MaxHP = 10, CurrentHP = 10 };
            var other = new Fighter { Id = -2, TeamId = team, IsMonster = template != 0, MonsterId = template, MaxHP = 10, CurrentHP = 10 };
            Assert.Equal(names, EffectEngine.CumpleLaMascara(waiting, other, mask));
        }

        /// <summary>
        /// The blows whose number is not the die grown by the caster: a share of the caster's missing
        /// life, a fixed amount, a share of the target's life, of the blow that set them off, so
        /// much per point the target spent.
        /// </summary>
        [Theory]
        [InlineData(275, 10, 1000)]   // 10% of the 10,000 the caster is missing
        [InlineData(1063, 70, 70)]    // fixed
        [InlineData(1070, 10, 300)]   // 10% of the target's 3,000
        [InlineData(1123, 50, 200)]   // 50% of the 400 that set it off
        public void Blows_out_of_something_else_than_the_die(int effect, int die, int expected)
        {
            var fight = new FightInstance(1, 1) { DanoDelDisparo = 400 };
            var caster = new Fighter { MaxHP = 20000, CurrentHP = 10000 };
            var target = new Fighter { MaxHP = 5000, CurrentHP = 3000 };

            Assert.NotEqual(EffectEngine.ModoDeDano.Normal, EffectEngine.ModoDe(effect));
            Assert.Equal(expected, EffectEngine.BaseDelModo(new SpellEffect { EffectId = effect }, die, caster, target, fight));
        }

        /// <summary>"#2 de daños por #1 PA utilizado": 100 per 2 AP, the target having spent 6 of his 10.</summary>
        [Fact]
        public void So_much_per_point_spent()
        {
            var target = new Fighter { MaxAP = 10, CurrentAP = 4, MaxMP = 5, CurrentMP = 5 };
            Assert.Equal(300, EffectEngine.BaseDelModo(new SpellEffect { EffectId = 1131, DiceNum = 2, DiceSide = 100 }, 0,
                                                       new Fighter(), target, new FightInstance(1, 1)));
        }

        /// <summary>The forced push moves the Indesplazable, which the ordinary one does not.</summary>
        [Fact]
        public void The_forced_push_moves_the_unmovable()
        {
            var fight = new FightInstance(1, 1);
            var caster = Fighter(1, 0, 300);
            var target = Fighter(2, 1, 301);
            fight.AddPlayer(caster); fight.AddOpponent(target);
            target.Buffs.PonerEstado(97);

            Resolve(fight, caster, target, new SpellEffect { EffectId = 5, TargetMask = "A", DiceNum = 2, Triggers = "I" }, target.CellId);
            Assert.Equal(301, target.CellId);

            Resolve(fight, caster, target, new SpellEffect { EffectId = 1021, TargetMask = "A", DiceNum = 2, Triggers = "I" }, target.CellId);
            Assert.NotEqual(301, target.CellId);
        }
    }
}
