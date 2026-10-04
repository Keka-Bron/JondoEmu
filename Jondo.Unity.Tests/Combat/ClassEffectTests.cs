using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The effects the class spells were waiting on, each in the one engine: best-element heal
    /// (3002), the grade of a spell taken off (1406), kill and replace (2796), rolls maximised and
    /// minimised (782, 781), damage by the MP left (1012-1016), and the triggers their sheets name.
    /// </summary>
    public class ClassEffectTests
    {
        private static Fighter Character(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 900, CurrentHP = 900, Level = 50,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494,
        };

        /// <summary>
        /// "Curas del mejor elemento" scale with the caster's highest characteristic, as the
        /// Aniripsa's sheet says ("o incluso en su mejor elemento"): 100 agility and nothing
        /// else doubles a heal of 20.
        /// </summary>
        [Fact]
        public void A_best_element_heal_scales_with_the_best_characteristic()
        {
            var fight = new FightInstance(1, 1);
            var me = Character(1, 0, 300);
            var ally = Character(2, 0, 301);
            me.Agility = 100;
            ally.CurrentHP = 1000;
            fight.AddPlayer(me); fight.AddPlayer(ally);

            var row = new SpellEffect { EffectId = 3002, DiceNum = 20, Element = 5, TargetMask = "a", Triggers = "I" };
            var outcomes = EffectEngine.ResolveEffects(fight, me, 25883, 1, ally, EffectEngine.AlLanzar, 1, new[] { row }, aimedCell: 301);

            Assert.Equal(40, Assert.Single(outcomes).Cura);
        }

        /// <summary>
        /// Aguja takes grade 6 of its poison 30842 off before laying it again, and leaves another
        /// grade alone; the fight says so as "jwe 1406 f33{f2=30842 f3=6 f4=-3}" -- frame 200 of
        /// the Aguja capture, byte for byte.
        /// </summary>
        [Fact]
        public void Aguja_takes_one_grade_of_its_poison_off()
        {
            var fight = new FightInstance(1, 1);
            var xelor = Character(53720973411, 0, 300);
            var enemy = Monster(-3, 301);
            fight.AddPlayer(xelor); fight.AddOpponent(enemy);
            int n = 0;
            enemy.Buffs.Poner(new Buff { EffectId = 950, Estado = 2328, HechizoOrigen = 30842, NivelOrigen = 6, CaducaEnRonda = 3 }, () => ++n);
            enemy.Buffs.PonerEstado(2328);
            enemy.Buffs.Poner(new Buff { EffectId = 128, Caracteristica = 23, Cuanto = 1, HechizoOrigen = 30842, NivelOrigen = 2, CaducaEnRonda = 3 }, () => ++n);

            var row = SpellEffects.De(13244, 2).Single(e => e.EffectId == EffectEngine.QuitaUnGradoDeUnHechizo);
            var outcomes = EffectEngine.ResolveEffects(fight, xelor, 13244, 2, enemy, EffectEngine.AlLanzar, 1, new[] { row }, aimedCell: 301);

            Assert.Single(Assert.Single(outcomes).BuffsQuitados);
            Assert.False(enemy.Buffs.TieneEstado(2328));
            Assert.Single(enemy.Buffs.Puestos);
            StealTests.SameFields(StealTests.Hex("18e3809490c80170fe0a8a021110faf001180620fdffffffffffffffff01"),
                                  FightProtocol.BuildSpellEffectsRemoved(xelor.Id, 30842, enemy.Id, grade: 6,
                                                                         effect: EffectEngine.QuitaUnGradoDeUnHechizo));
        }

        /// <summary>
        /// Potencia Silvestre's 2796 on an allied tree (state 256) kills it and brings template
        /// 5901 in its cell, as the Sacrógrito's 405 does.
        /// </summary>
        [Fact]
        public void Potencia_Silvestre_replaces_the_tree()
        {
            var fight = new FightInstance(1, 1);
            var sadida = Character(1, 0, 300);
            var tree = new Fighter { Id = -1, TeamId = 0, CellId = 301, MaxHP = 100, CurrentHP = 100, IsMonster = true, MonsterId = 282, Invocador = 1 };
            fight.AddPlayer(sadida); fight.Invocar(tree, sadida);
            tree.Buffs.PonerEstado(256);

            var outcomes = EffectEngine.Resolver(fight, sadida, 13530, 1, tree, EffectEngine.AlLanzar, 1, celdaApuntada: 301);

            var replaced = Assert.Single(outcomes, o => o.Fulmina);
            Assert.Equal((tree, 5901, true), (replaced.Sobre, replaced.Invoca, replaced.EnLaCasillaDelMuerto));
        }

        /// <summary>
        /// El Diablo's 782 on everybody turns a die rolled on them to its top; Mala Sombra's 781
        /// on the enemy turns the dice he rolls to their bottom.
        /// </summary>
        [Fact]
        public void Rolls_are_maximised_on_a_782_and_minimised_from_a_781()
        {
            var me = Character(1, 0, 300);
            var enemy = Monster(-1, 301);
            var blow = new SpellEffect { EffectId = 96, DiceNum = 21, DiceSide = 24 };
            int n = 0;

            Assert.Equal(22, EffectEngine.ConLosAzares(blow, me, enemy, 1, 22));
            enemy.Buffs.Poner(new Buff { EffectId = EffectEngine.MaximizaLosAzares, CaducaEnRonda = 2 }, () => ++n);
            Assert.Equal(24, EffectEngine.ConLosAzares(blow, me, enemy, 1, 22));
            Assert.Equal(22, EffectEngine.ConLosAzares(blow, enemy, me, 1, 22));      // his own blows are not

            enemy.Buffs.Vaciar();
            enemy.Buffs.Poner(new Buff { EffectId = EffectEngine.MinimizaLosAzares, CaducaEnRonda = 2 }, () => ++n);
            Assert.Equal(21, EffectEngine.ConLosAzares(blow, enemy, me, 1, 23));
            Assert.Equal(23, EffectEngine.ConLosAzares(blow, me, enemy, 1, 23));

            var fixedBlow = new SpellEffect { EffectId = 96, DiceNum = 30 };
            Assert.Equal(30, EffectEngine.ConLosAzares(fixedBlow, enemy, me, 1, 30));  // nothing to turn
        }

        /// <summary>
        /// Cénit's 1013 (52-58) hits with the whole die while the Yopuka keeps his three MP -- 108
        /// next to its 27-29's 56, frames 108 and 109 of "yopuka-cenit" -- and with nothing once
        /// he has walked them all, "jwe 1013" with no amount at frame 308.
        /// </summary>
        [Fact]
        public void Cenit_scales_with_the_MP_left()
        {
            var yopuka = Character(1, 0, 300);
            yopuka.MaxMP = 3; yopuka.CurrentMP = 3;
            var row = SpellEffects.De(13145, 1).Single(e => e.EffectId == 1013);

            Assert.True(EffectEngine.EsDeDano(1013));
            Assert.Equal(54, EffectEngine.ConLosPMRestantes(row, 54, yopuka, 1));
            yopuka.CurrentMP = 0;
            Assert.Equal(0, EffectEngine.ConLosPMRestantes(row, 54, yopuka, 1));
            yopuka.CurrentMP = 2;
            Assert.Equal(36, EffectEngine.ConLosPMRestantes(row, 54, yopuka, 1));
            Assert.Equal(54, EffectEngine.ConLosPMRestantes(new SpellEffect { EffectId = 98 }, 54, yopuka, 1));
        }

        /// <summary>
        /// The triggers the class sheets name and the fight now fires, and the rows that answer
        /// them: Barricada's 1160 under DIS, Buena Estrella's under CC, Toxinas' under DT,
        /// Imantación's under MA, Impostura's under MS, Osadía's under PO|CPD, Desprendimiento's
        /// under R, Palabra Maliciosa's under CMPARR, Pacto Bestial's under CI.
        /// </summary>
        [Theory]
        [InlineData(29050, 1, "DIS")]
        [InlineData(12876, 1, "CC")]
        [InlineData(28739, 1, "DT")]
        [InlineData(13437, 1, "MA")]
        [InlineData(13489, 1, "MS")]
        [InlineData(12882, 1, "PO")]
        [InlineData(12882, 1, "CPD")]
        [InlineData(29764, 1, "R")]
        [InlineData(25895, 1, "CMPARR")]
        [InlineData(32557, 2, "CI")]
        [InlineData(29242, 1, "DS")]
        public void The_named_triggers_have_their_rows(int spell, int grade, string trigger)
        {
            Assert.Contains(SpellEffects.De(spell, grade), e => e.Disparadores().Contains(trigger));
            var named = typeof(EffectEngine).GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue());
            Assert.Contains(trigger, named);
        }
    }
}
