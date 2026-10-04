using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The modifiers of one spell -- the catalogue's category 3 -- kept as rows, honoured by the
    /// cast and told to the client with the hnd the captures show: the Uginak's beast form pinning
    /// his spells' range, the Pandawa's and the Osamodas' AP cost, the Aniripsa's base healing.
    /// </summary>
    public class SpellModifierTests
    {
        private const int Bestialidad = 13746;
        private const long Uginak = 54064250979;

        private static Fighter Player(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        /// <summary>
        /// The beast form, cast in round 2 in "molosse": nineteen "2905" rows pin the maximum range
        /// of the Uginak's spells to 2 until round 4, and 13791 is pinned to 0-0 by a 2905 and a
        /// 2906 of value 0 -- rows 243 and 258 of the capture, which the engine used to drop for
        /// their zero.
        /// </summary>
        [Fact]
        public void The_beast_form_pins_the_range_of_the_Uginaks_spells()
        {
            var fight = new FightInstance(1, 1);
            var me = Player(Uginak, 300);
            fight.AddPlayer(me);

            var outcomes = EffectEngine.Resolver(fight, me, Bestialidad, 1, me, EffectEngine.AlLanzar, 2, celdaApuntada: 300);

            var pinned = outcomes.Where(o => o.Buff?.Sobre == SpellAspect.MaxRangeSet).ToList();
            Assert.Equal(19, pinned.Count);
            var tibia = Assert.Single(pinned, o => o.Buff.HechizoAfectado == 13756);
            Assert.Equal((2, 4), (tibia.Buff.Cuanto, tibia.Buff.CaducaEnRonda));

            // Frame 243, the row of 13756, but for its number: family 4, value 2, until round 4.
            Assert.Equal(StealTests.Hex("0a470a3b08bc6b10e380ecb3c901182b2001320210043a014940eeaf185002621610ffffffffffffffffff0118ffffffffffffffffff0170b26b780480010210e380ecb3c90118d916"),
                         StealTests.AsItGoesOut(tibia, me, number: 43));

            // 13791: pinned to zero both ways, and a zero is a value.
            Assert.Equal(0, me.Buffs.FijadoDelHechizo(13791, SpellAspect.MaxRangeSet, 2));
            Assert.Equal(0, me.Buffs.FijadoDelHechizo(13791, SpellAspect.MinRangeSet, 2));
            Assert.Equal((0, 0), SpellModifiers.Range(me, 13791, 1, 6, 2));

            // The cast honours the pin whatever the range bonuses add up to.
            Assert.Equal((1, 2), SpellModifiers.Range(me, 13756, 1, 9, 2));
            me.Buffs.Barrer(4);                                               // the form is over
            Assert.Equal((1, 9), SpellModifiers.Range(me, 13756, 1, 9, 4));
        }

        /// <summary>
        /// The hnd of each: "f2=3 f3=2 f4=12 f5=13756" (frame 195), the zero pin with no f3 at all
        /// (frames 210 and 211, maximum and minimum of 13791), and the Pandawa's "+1 PA" on 12781
        /// as "f2=2 f3=1 f4=5" (frame 1288 of "resto hechizos pandawa-no variantes").
        /// </summary>
        [Fact]
        public void The_hnd_says_the_modifier_its_action_and_its_total()
        {
            Assert.Equal((12, SpellModifiers.Set), SpellModifiers.OnTheWire(SpellAspect.MaxRangeSet));
            Assert.Equal((13, SpellModifiers.Set), SpellModifiers.OnTheWire(SpellAspect.MinRangeSet));
            Assert.Equal((5, SpellModifiers.Subtract), SpellModifiers.OnTheWire(SpellAspect.ApCostUp));
            Assert.Equal((5, SpellModifiers.Add), SpellModifiers.OnTheWire(SpellAspect.ApCostDown));
            Assert.Equal((19, SpellModifiers.Add), SpellModifiers.OnTheWire(SpellAspect.BaseHeal));
            Assert.Equal((3, SpellModifiers.Add), SpellModifiers.OnTheWire(SpellAspect.DanoBase));

            Assert.Equal(StealTests.Hex("0a0910031802200c28bc6b10e380ecb3c901"),
                         FightProtocol.BuildSpellModifier(Uginak, 12, 13756, 2, SpellModifiers.Set));
            Assert.Equal(StealTests.Hex("0a071003200c28df6b10e380ecb3c901"),
                         FightProtocol.BuildSpellModifier(Uginak, 12, 13791, 0, SpellModifiers.Set));
            Assert.Equal(StealTests.Hex("0a071003200d28df6b10e380ecb3c901"),
                         FightProtocol.BuildSpellModifier(Uginak, 13, 13791, 0, SpellModifiers.Set));
            Assert.Equal(StealTests.Hex("0a0910021801200528ed6310e380b090c801"),
                         FightProtocol.BuildSpellModifier(53721432163, 5, 12781, 1, SpellModifiers.Subtract));
        }

        /// <summary>
        /// SÃ©quito Salvaje's grade 2 takes one AP off each of the Osamodas' summoning spells for
        /// two turns (285, "mod5 act1 val1" in his capture), and the Tymador's bombs add one to
        /// his (296 through Encendimiento): the cast pays the level's cost less the one plus the
        /// other.
        /// </summary>
        [Fact]
        public void The_AP_cost_is_the_levels_less_285_plus_296()
        {
            var fight = new FightInstance(1, 1);
            var me = Player(10, 300);
            fight.AddPlayer(me);

            EffectEngine.Resolver(fight, me, 32555, 2, me, EffectEngine.AlLanzar, 1, celdaApuntada: 300);
            Assert.Equal(2, SpellModifiers.ApCost(me, 31115, 3, 1));
            Assert.Equal(3, SpellModifiers.ApCost(me, 13756, 3, 1));   // not one of his

            int n = 0;
            me.Buffs.Poner(new Buff { EffectId = 296, Sobre = SpellAspect.ApCostUp, HechizoAfectado = 31115, Cuanto = 1, CaducaEnRonda = -1 }, () => ++n);
            me.Buffs.Poner(new Buff { EffectId = 296, EffectUid = 1, Sobre = SpellAspect.ApCostUp, HechizoAfectado = 31115, Cuanto = 1, CaducaEnRonda = -1 }, () => ++n);
            Assert.Equal(4, SpellModifiers.ApCost(me, 31115, 3, 1));
            Assert.Equal(2, SpellModifiers.Total(me, 31115, SpellAspect.ApCostUp, 1));
        }

        /// <summary>
        /// The rest of what a cast checks: casts per turn and per target (290, 291), the critical
        /// chance (287), the cell a spell wants (297 switches "occupied" off, 314 on, 299 "free"
        /// on) -- Karcham's 297 and 299 on 12787 are what let a carrying Pandawa throw on an empty
        /// cell.
        /// </summary>
        [Fact]
        public void Casts_critical_and_cells_follow_their_rows()
        {
            var fight = new FightInstance(1, 1);
            var me = Player(10, 300);
            fight.AddPlayer(me);
            int n = 0;
            void Row(SpellAspect aspect, int spell, int value)
                => me.Buffs.Poner(new Buff { EffectId = (int)aspect, EffectUid = ++n, Sobre = aspect, HechizoAfectado = spell,
                                             Cuanto = value, CaducaEnRonda = -1 }, () => n);

            Row(SpellAspect.CastsPerTurnUp, 25802, 1);
            Row(SpellAspect.CastsPerTargetUp, 25802, 2);
            Row(SpellAspect.CriticalUp, 12881, 15);
            Assert.Equal(3, SpellModifiers.CastsPerTurn(me, 25802, 2, 1));
            Assert.Equal(0, SpellModifiers.CastsPerTurn(me, 25802, 0, 1));   // no cap stays no cap
            Assert.Equal(3, SpellModifiers.CastsPerTarget(me, 25802, 1, 1));
            Assert.Equal(15, SpellModifiers.Critical(me, 12881, 1));

            Assert.Equal((false, true), SpellModifiers.Cells(me, 12787, false, true, 1));
            Row(SpellAspect.OccupiedCellOff, 12787, 1);
            Row(SpellAspect.FreeCellOn, 12787, 1);
            Assert.Equal((true, false), SpellModifiers.Cells(me, 12787, false, true, 1));
            Row(SpellAspect.OccupiedCellOn, 24219, 1);
            Assert.Equal((false, true), SpellModifiers.Cells(me, 24219, false, false, 1));
        }

        /// <summary>
        /// Coro Estridente's 2935 adds its +5 to the basic healing of 25861, as 293 adds to a
        /// blow: a heal of 10 at no intelligence heals 15.
        /// </summary>
        [Fact]
        public void Basic_healing_adds_to_the_heal_of_its_spell()
        {
            var fight = new FightInstance(1, 1);
            var me = Player(10, 300);
            var ally = Player(11, 301);
            ally.CurrentHP = 1000;
            fight.AddPlayer(me); fight.AddPlayer(ally);
            int n = 0;
            me.Buffs.Poner(new Buff { EffectId = 2935, Sobre = SpellAspect.BaseHeal, HechizoAfectado = 25861, Cuanto = 5, CaducaEnRonda = -1 }, () => ++n);

            var heal = new SpellEffect { EffectId = 108, DiceNum = 10, Element = 2, TargetMask = "a", Triggers = "I" };
            var outcomes = EffectEngine.ResolveEffects(fight, me, 25861, 1, ally, EffectEngine.AlLanzar, 1, new[] { heal }, aimedCell: 301);

            Assert.Equal(15, Assert.Single(outcomes).Cura);
        }
    }
}

