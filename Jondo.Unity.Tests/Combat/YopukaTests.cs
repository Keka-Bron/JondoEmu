using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Yopuka's spells against their captures, and the engine rules they pinned down:
    /// the rows for the client only, the one draw per cast, the stacking the level writes,
    /// the targets picked before anybody moves, the hooked decay of Furor, the kinds of
    /// fighter a mask letter names, the invulnerable state, the bar of the T.
    /// </summary>
    [Collection("removal dice")]
    public class YopukaTests
    {
        private const int Presion = 13106;
        private const int Salto = 13107;
        private const int EspadaDestructora = 13119;
        private const int Concentracion = 13123;
        private const int Furor = 13156;
        private const int FurorChain = 28604;
        private const int Friccion = 13113;
        private const int Influencia = 13141;
        private const int Virtud = 13142;
        private const int VirtudChain = 29723;
        private const int Vitalidad = 13120;
        private const int VitalidadChain = 25215;
        private const int Manticolmillo = 24011;
        private const int Tumulto = 13144;
        private const int EspadaDelJuicio = 13117;
        private const int BumeranPerfido = 364;

        private const int Incurable = 776;
        private const int BasicDamage = 293;
        private const int Dodge = 78;
        private const int Erosion = 75;
        private const int Power = 25;
        private const int MovementPoints = 23;

        private static Fighter Yopuka(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 3000, CurrentHP = 3000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Vitality = 1150,
        };

        private static Fighter Monster(long id, int cell, int template = 494) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 900, CurrentHP = 900, Level = 50, Vitality = 900,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = template,
        };

        private static List<Outcome> Cast(FightInstance fight, Fighter caster, int spell, int grade, Fighter target, int cell, int round = 1)
            => EffectEngine.Resolver(fight, caster, spell, grade, target, EffectEngine.AlLanzar, round, celdaApuntada: cell);

        // ─── The rows for the client only ───────────────────────────────────────

        /// <summary>
        /// The sheet's copies never reach the engine: Furor's "+20", Vitalidad's two "+N%",
        /// Manticolmillo's "+15 huida" and Virtud's shield and "-50" all carry the client's
        /// ForClientOnly bit next to the sub-cast that does the real thing.
        /// </summary>
        [Fact]
        public void The_sheets_copies_are_not_read()
        {
            Assert.DoesNotContain(SpellEffects.De(Furor, 2), e => e.EffectId == BasicDamage);
            Assert.Contains(SpellEffects.De(FurorChain, 3), e => e.EffectId == BasicDamage && e.Value == 20);
            Assert.DoesNotContain(SpellEffects.De(Vitalidad, 3), e => e.EffectId == EffectSupport.VitalityPercentBonus);
            Assert.Contains(SpellEffects.De(VitalidadChain, 5), e => e.EffectId == EffectSupport.VitalityPercentBonus && e.DiceNum == 20);
            Assert.DoesNotContain(SpellEffects.De(Manticolmillo, 3), e => e.EffectId == 752);
            Assert.DoesNotContain(SpellEffects.De(Virtud, 3), e => e.EffectId is 1020 or 186);
            Assert.All(SpellEffects.De(Virtud, 3), e => Assert.False(e.ForClientOnly));
        }

        // ─── One draw per cast ──────────────────────────────────────────────────

        /// <summary>
        /// Bumerán Pérfido is eight rows of 12.5 in four groups: every draw keeps exactly one
        /// life steal and the characteristic of the same element, and over enough draws all
        /// four elements come out.
        /// </summary>
        [Fact]
        public void Bumeran_Perfido_draws_one_element_and_its_characteristic()
        {
            var pairs = new Dictionary<int, int> { [92] = 118, [94] = 126, [91] = 123, [93] = 119 };
            var seen = new HashSet<int>();
            for (int i = 0; i < 400; i++)
            {
                var drawn = EffectEngine.EfectosSorteados(BumeranPerfido, 2, false);
                Assert.Equal(2, drawn.Count);
                var steal = Assert.Single(drawn, e => EffectEngine.EsRoboDeVida(e.EffectId));
                var boost = Assert.Single(drawn, e => !EffectEngine.EsRoboDeVida(e.EffectId));
                Assert.Equal(pairs[steal.EffectId], boost.EffectId);
                Assert.Equal(160, boost.DiceNum);
                seen.Add(steal.EffectId);
            }
            Assert.Equal(4, seen.Count);
        }

        /// <summary>The blows and the rows of one cast come out of the same draw.</summary>
        [Fact]
        public void The_blows_and_the_rows_of_a_cast_agree_on_the_draw()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var pairs = new Dictionary<int, int> { [92] = 118, [94] = 126, [91] = 123, [93] = 119 };
            for (int i = 0; i < 50; i++)
            {
                var draw = EffectEngine.EfectosSorteados(BumeranPerfido, 2, false);
                var blows = EffectEngine.Golpes(fight, me, BumeranPerfido, 2, puch, 301, false, draw);
                var blow = Assert.Single(blows);
                var rows = EffectEngine.Resolver(fight, me, BumeranPerfido, 2, puch, EffectEngine.AlLanzar, 1,
                                                 celdaApuntada: 301, efectosSorteados: draw);
                var boost = Assert.Single(rows, o => o.Buff != null);
                Assert.Equal(pairs[blow.Efecto.EffectId], boost.Efecto.EffectId);
                me.Buffs.Vaciar();
            }
        }

        // ─── What the level says about stacking ─────────────────────────────────

        /// <summary>Presión is "2": two casts on one target erode 20%, Espada Destructora's 26%.</summary>
        [Fact]
        public void Erosion_stacks_to_the_levels_cap()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            Cast(fight, me, Presion, 3, puch, 301);
            Cast(fight, me, Presion, 3, puch, 301);
            Assert.Equal(2, puch.Buffs.Puestos.Count(b => b.EffectId == Incurable));
            Assert.Equal(20, puch.Buffs.De(Erosion, 1));

            // A third cast pushes the oldest out: still 20, and the replaced row is told.
            var third = Cast(fight, me, Presion, 3, puch, 301);
            Assert.Equal(20, puch.Buffs.De(Erosion, 1));
            Assert.Single(third.Single(o => o.Buff != null && o.Efecto.EffectId == Incurable).Relevados);

            var other = Monster(-2, 302);
            fight.AddOpponent(other);
            Cast(fight, me, EspadaDestructora, 3, other, 302);
            Cast(fight, me, EspadaDestructora, 3, other, 302);
            Assert.Equal(26, other.Buffs.De(Erosion, 1));
        }

        /// <summary>
        /// Manticolmillo is "-1" on its 24012: one "+15 huida" per enemy in the cross, cast by
        /// the Yopuka on himself each time. Two enemies, thirty.
        /// </summary>
        [Fact]
        public void Manticolmillo_gives_dodge_per_enemy_hit()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var (x, y) = MapGeometry.CellToPoint(330);
            var one = Monster(-1, MapGeometry.PointToCell(x + 1, y));
            var two = Monster(-2, MapGeometry.PointToCell(x - 1, y));
            fight.AddPlayer(me); fight.AddOpponent(one); fight.AddOpponent(two);

            var outcomes = Cast(fight, me, Manticolmillo, 3, null, 330);
            Assert.Equal(2, me.Buffs.Puestos.Count(b => b.EffectId == 752));
            Assert.Equal(30, me.Buffs.De(Dodge, 1));
            Assert.Equal(2, outcomes.Count(o => o.Sobre == me && o.Efecto.EffectId == 752));
        }

        /// <summary>
        /// A spell that is "1" replaces its own row: Espada del Juicio cast on a second enemy
        /// drops the old shield row and puts a new one, the way the capture goes "jya 9, jwe
        /// 514, jxm 13" when the Yopuka turns from -2 to -4.
        /// </summary>
        [Fact]
        public void A_spell_that_does_not_stack_replaces_its_row()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            var other = Monster(-2, 302);
            fight.AddPlayer(me); fight.AddOpponent(puch); fight.AddOpponent(other);

            var first = Cast(fight, me, EspadaDelJuicio, 2, puch, 301);
            var shield = first.Single(o => o.Buff != null && o.Sobre == me && o.Efecto.EffectId == EffectEngine.ShieldPanelEffect);
            int number = shield.Buff.Numero;

            var second = Cast(fight, me, EspadaDelJuicio, 2, other, 302, round: 2);
            var again = second.Single(o => o.Buff != null && o.Sobre == me && o.Efecto.EffectId == EffectEngine.ShieldPanelEffect);
            Assert.NotEqual(number, again.Buff.Numero);
            Assert.Equal(number, Assert.Single(again.Relevados).Numero);
            Assert.Single(me.Buffs.Puestos, b => b.EffectId == EffectEngine.ShieldPanelEffect);
            // And the points of the row that went go with it: 100% of level 200, once.
            Assert.Equal(200, me.PuntosDeEscudo);
        }

        // ─── The targets are picked before anybody moves ────────────────────────

        /// <summary>
        /// Fricción pulls the enemy two cells and THEN puts its state on him: in the capture the
        /// pull carries -4 from 202 to 216 and the 950 still lands on -4. Read live, the aimed
        /// cell was empty by then.
        /// </summary>
        [Fact]
        public void Friccion_puts_its_state_on_the_enemy_it_just_pulled()
        {
            var fight = new FightInstance(1, 1);
            var (x, y) = MapGeometry.CellToPoint(300);
            var me = Yopuka(10, 300);
            int far = MapGeometry.PointToCell(x + 4, y);
            var puch = Monster(-1, far);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var outcomes = Cast(fight, me, Friccion, 3, puch, far);
            Assert.Contains(outcomes, o => o.Mueve && o.Sobre == puch);
            Assert.NotEqual(far, puch.CellId);
            Assert.True(puch.Buffs.TieneEstado(3643));
            Assert.Contains(outcomes, o => o.Buff != null && o.Buff.Estado == 3643 && o.Sobre == puch);
        }

        // ─── Furor ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Furor's rows are 28604's alone: Furor I and +20 the first time, Furor II and +40
        /// when cast again under Furor I, the old rows taken off by its 406. Never the +20 of
        /// 13156, which is the sheet's.
        /// </summary>
        [Fact]
        public void Furor_climbs_from_I_to_II_and_takes_its_old_rows_off()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var first = Cast(fight, me, Furor, 2, puch, 301, round: 19);
            Assert.True(me.Buffs.TieneEstado(609));
            Assert.Equal(20, me.Buffs.DelHechizo(Furor, SpellAspect.DanoBase, 19));
            Assert.All(me.Buffs.Puestos, b => Assert.Equal(FurorChain, b.HechizoOrigen));
            Assert.Contains(first, o => o.Buff != null && o.Buff.Estado == 609 && o.NivelOrigen == 3);

            var second = Cast(fight, me, Furor, 2, puch, 301, round: 20);
            Assert.True(me.Buffs.TieneEstado(5192));
            Assert.False(me.Buffs.TieneEstado(609));
            Assert.Equal(40, me.Buffs.DelHechizo(Furor, SpellAspect.DanoBase, 20));
            var removal = Assert.Single(second, o => o.Efecto.EffectId == EffectSupport.RemoveSpellEffects && o.BuffsQuitados.Count > 0);
            Assert.Equal(2, removal.BuffsQuitados.Count);
        }

        /// <summary>
        /// The decay is a "1160 under TE" with a delay of one: fired at the end of the turn of
        /// the cast it does nothing, at the end of the next one it casts grade 2, whose 406
        /// takes everything of 28604 away.
        /// </summary>
        [Fact]
        public void Furor_decays_at_the_end_of_the_turn_after_the_cast()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);
            Cast(fight, me, Furor, 2, puch, 301, round: 19);
            Assert.True(me.Buffs.TieneEstado(609));

            var sameTurn = EffectEngine.Resolver(fight, me, FurorChain, 3, me, EffectEngine.AlAcabarElTurno, 19,
                                                 celdaApuntada: me.CellId, rondaDelEnganche: 19);
            Assert.Empty(sameTurn);
            Assert.True(me.Buffs.TieneEstado(609));

            var nextTurn = EffectEngine.Resolver(fight, me, FurorChain, 3, me, EffectEngine.AlAcabarElTurno, 20,
                                                 celdaApuntada: me.CellId, rondaDelEnganche: 19);
            Assert.Contains(nextTurn, o => o.Efecto.EffectId == EffectSupport.RemoveSpellEffects && o.BuffsQuitados.Count == 2);
            Assert.False(me.Buffs.TieneEstado(609));
            Assert.Equal(0, me.Buffs.DelHechizo(Furor, SpellAspect.DanoBase, 20));
        }

        /// <summary>
        /// The cast hooks the chained spell, not the root: 28604 at grade 3 on the Yopuka, from
        /// round 19 to the fall of its rows at 21. 13156 has nothing to fire later.
        /// </summary>
        [Fact]
        public void Furor_hooks_its_chained_spell_on_the_caster()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var outcomes = Cast(fight, me, Furor, 2, puch, 301, round: 19);
            FightHandler.EngancharLoPendiente(outcomes, Furor, 2, me.Id, 19);

            var hook = Assert.Single(me.Buffs.ActiveSpells);
            Assert.Equal(FurorChain, hook.Hechizo);
            Assert.Equal(3, hook.Grado);
            Assert.Equal(19, hook.PuestoEnRonda);
            Assert.Equal(21, hook.CaducaEnRonda);
            Assert.Empty(puch.Buffs.ActiveSpells);
        }

        /// <summary>
        /// Fricción keeps pulling: hit by the Yopuka, the enemy under the state casts 13128
        /// back at his attacker (the O of the mask) and is pulled two cells towards him, as
        /// the capture goes when Tempestad de Potencia lands on -3.
        /// </summary>
        [Fact]
        public void Friccion_pulls_the_enemy_towards_whoever_hits_him()
        {
            var fight = new FightInstance(1, 1);
            var (x, y) = MapGeometry.CellToPoint(300);
            var me = Yopuka(10, 300);
            int far = MapGeometry.PointToCell(x + 5, y);
            var puch = Monster(-1, far);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var cast = Cast(fight, me, Friccion, 3, puch, far);
            FightHandler.EngancharLoPendiente(cast, Friccion, 3, me.Id, 1);
            var hook = Assert.Single(puch.Buffs.ActiveSpells);
            Assert.Equal(Friccion, hook.Hechizo);
            int pulled = puch.CellId;
            Assert.Equal(3, MapGeometry.Distance(300, pulled));

            // The blow: the Yopuka is the attacker at hand.
            fight.TriggeringAttacker = me;
            var hit = EffectEngine.Resolver(fight, me, Friccion, 3, puch, EffectEngine.CuandoMePegan, 1,
                                            celdaApuntada: puch.CellId, rondaDelEnganche: 1);
            fight.TriggeringAttacker = null;

            Assert.Contains(hit, o => o.Mueve && o.Sobre == puch && o.HechizoOrigen == 13128);
            Assert.Equal(1, MapGeometry.Distance(300, puch.CellId));
            Assert.False(puch.Buffs.TieneEstado(6048));
        }

        /// <summary>A 406 takes the hook of the spell away with its rows.</summary>
        [Fact]
        public void Removing_a_spells_effects_removes_its_hook()
        {
            var me = Yopuka(10, 300);
            me.Buffs.Enganchar(FurorChain, 3, 21, me.Id, 19);
            me.Buffs.Poner(new Buff { EffectId = 950, Estado = 609, HechizoOrigen = FurorChain, Quien = me.Id, CaducaEnRonda = 21 }, () => 1);
            me.Buffs.QuitarDelHechizo(FurorChain);
            Assert.Empty(me.Buffs.ActiveSpells);
            Assert.Empty(me.Buffs.Puestos);
        }

        // ─── The kinds a mask letter names ──────────────────────────────────────

        /// <summary>
        /// Concentración: 20-24 on players, monsters and the caster in the zone; 30-34 on the
        /// summons -- "los daños son mayores sobre las invocaciones".
        /// </summary>
        [Fact]
        public void Concentracion_hits_monsters_and_summons_with_different_dice()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            var summon = Monster(-2, 302);
            summon.Invocador = -1;
            fight.AddPlayer(me); fight.AddOpponent(puch); fight.AddOpponent(summon);

            var onMonster = Assert.Single(EffectEngine.Golpes(fight, me, Concentracion, 3, puch, 301));
            Assert.Equal(20, onMonster.Efecto.DiceNum);
            var onSummon = Assert.Single(EffectEngine.Golpes(fight, me, Concentracion, 3, summon, 302));
            Assert.Equal(30, onSummon.Efecto.DiceNum);
        }

        /// <summary>
        /// Vitalidad on oneself: the "c" row of 25215 at grade 5, 20% of the MAXIMUM LIFE as a
        /// flat "+N vitalidad" (125): +230 on the capture's naked 1,150, +330 on a Yopuka at
        /// 1,650 -- not the 120 that 20% of his 600 of vitality alone would be.
        /// </summary>
        [Fact]
        public void Vitalidad_on_oneself_is_twenty_percent_of_the_maximum_life()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            me.MaxHP = 1650; me.CurrentHP = 1650; me.Vitality = 600;
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var outcomes = Cast(fight, me, Vitalidad, 3, me, 300);
            var row = Assert.Single(outcomes, o => o.Buff != null && o.Sobre == me);
            Assert.Equal(EffectSupport.VitalityFlatBonus, row.Efecto.EffectId);
            Assert.Equal(330, row.Efecto.DiceNum);
            Assert.Equal(VitalidadChain, row.HechizoOrigen);
            Assert.Equal(1980, me.MaxHP);

            var naked = Yopuka(11, 303);
            naked.MaxHP = 1150; naked.CurrentHP = 1150; naked.Vitality = 100;
            fight.AddPlayer(naked);
            var capture = Assert.Single(Cast(fight, naked, Vitalidad, 3, naked, 303), o => o.Buff != null && o.Sobre == naked);
            Assert.Equal(230, capture.Efecto.DiceNum);

            // And on an enemy, the 10% row of grade 6: 90 of his 900.
            var onPuch = Cast(fight, me, Vitalidad, 3, puch, 301);
            var his = Assert.Single(onPuch, o => o.Buff != null && o.Sobre == puch);
            Assert.Equal(90, his.Efecto.DiceNum);
            Assert.Equal(6, his.NivelOrigen);
        }

        // ─── Influencia ─────────────────────────────────────────────────────────

        /// <summary>
        /// Influencia: the Invulnerable state and a row of "-100 PM" with the catalogue's own
        /// hundred, and the invulnerable takes no blow.
        /// </summary>
        [Fact]
        public void Influencia_makes_the_target_invulnerable_and_takes_all_its_MP()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var outcomes = Cast(fight, me, Influencia, 2, puch, 301);
            Assert.True(puch.Buffs.TieneEstado(269));
            var mp = Assert.Single(outcomes, o => o.Caracteristica == MovementPoints);
            Assert.Equal(-100, mp.Cuanto);
            Assert.Equal(0, mp.PuntosEsquivados);
            Assert.Equal(0, EffectEngine.PuntosQueCuentan(fight, puch, MovementPoints, 1));
            Assert.True(SpellStates.ShieldsFromBlow(puch, 1));
            Assert.True(SpellStates.ShieldsFromBlow(puch, 5));
            Assert.Equal("Invulnerable", SpellStates.Of(269).Name);
        }

        /// <summary>
        /// The client's catalogue: 103 flagged states, 97 among the pinned ones, and 678
        /// "Teleportal Imposible", whose effect 33 keeps its bearer out of the portals.
        /// </summary>
        [Fact]
        public void The_flagged_states_come_from_the_client()
        {
            Assert.Equal(104, SpellStates.Count);
            Assert.True(SpellStates.Of(678).CantUsePortals);
            Assert.True(SpellStates.Of(97).CantBeMoved);
            Assert.True(SpellStates.Of(6).CantBePushed);
            Assert.Null(SpellStates.Of(3643));
        }

        // ─── Salto ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Salto: the caster jumps, and the enemies around the arrival get "daños sufridos
        /// x115%" as a row under D, read by any blow of the round.
        /// </summary>
        [Fact]
        public void Salto_marks_the_enemies_around_the_arrival()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var (x, y) = MapGeometry.CellToPoint(330);
            var next = Monster(-1, MapGeometry.PointToCell(x + 1, y));
            var far = Monster(-2, MapGeometry.PointToCell(x + 3, y));
            fight.AddPlayer(me); fight.AddOpponent(next); fight.AddOpponent(far);

            var outcomes = Cast(fight, me, Salto, 3, null, 330);
            Assert.Equal(330, me.CellId);
            var mark = Assert.Single(outcomes, o => o.Buff != null && o.Efecto.EffectId == Buffs.DanoSufridoPorCiento);
            Assert.Same(next, mark.Sobre);
            Assert.True(mark.FilaEnganchada);
            Assert.Equal("D", mark.Buff.Disparador);
            Assert.Equal(115, next.Buffs.Multiplicador(Buffs.DanoSufridoPorCiento, 1, new[] { "D", "DM" }));
            Assert.Equal(100, far.Buffs.Multiplicador(Buffs.DanoSufridoPorCiento, 1, new[] { "D", "DM" }));
        }

        // ─── Virtud ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Virtud: the shield of 29723 on the caster and the ally next to him, and one "-50
        /// potencia" on the caster per ally in contact -- and nothing of 13142's own rows.
        /// </summary>
        [Fact]
        public void Virtud_shields_the_allies_around_and_costs_power_per_ally()
        {
            var fight = new FightInstance(1, 1);
            var (x, y) = MapGeometry.CellToPoint(300);
            var me = Yopuka(10, 300);
            var ally = Yopuka(11, MapGeometry.PointToCell(x + 1, y));
            var other = Yopuka(12, MapGeometry.PointToCell(x, y - 1));
            var puch = Monster(-1, MapGeometry.PointToCell(x - 1, y));
            fight.AddPlayer(me); fight.AddPlayer(ally); fight.AddPlayer(other); fight.AddOpponent(puch);

            var outcomes = Cast(fight, me, Virtud, 3, me, 300);
            Assert.Equal(1000, me.PuntosDeEscudo);
            Assert.Equal(1000, ally.PuntosDeEscudo);
            Assert.Equal(0, puch.PuntosDeEscudo);
            Assert.Equal(-100, me.Buffs.De(Power, 1));
            Assert.All(outcomes.Where(o => o.Buff != null), o => Assert.Equal(VirtudChain, o.HechizoOrigen));
            Assert.Single(me.Buffs.Puestos, b => b.EffectId == EffectEngine.ShieldPanelEffect);
        }

        // ─── Criticals ──────────────────────────────────────────────────────────

        /// <summary>
        /// A critical Virtud runs 29723 on its critical list: 550% of the level, 1,100 for a
        /// level 200, flagged as critical on the row (uid 383796, f9=1 in the capture), and
        /// nothing of 13142's own copies. Its sheet marker 666 is not a row at all.
        /// </summary>
        [Fact]
        public void A_critical_cast_runs_its_chain_on_the_critical_lists()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var outcomes = EffectEngine.Resolver(fight, me, Virtud, 3, me, EffectEngine.AlLanzar, 1,
                                                 celdaApuntada: 300, critico: true);
            var shield = Assert.Single(outcomes, o => o.Buff != null);
            Assert.Equal(1100, me.PuntosDeEscudo);
            Assert.Equal(VirtudChain, shield.HechizoOrigen);
            Assert.Equal(383796, shield.Efecto.EffectUid);
            Assert.True(shield.Critico);
            Assert.DoesNotContain(outcomes, o => o.Efecto.EffectId == 666);
            Assert.DoesNotContain(me.Buffs.Puestos, b => b.EffectId == 666);

            // And an ordinary cast: 500%, the ordinary entry, no flag.
            me.Buffs.Vaciar();
            var plain = Assert.Single(Cast(fight, me, Virtud, 3, me, 300), o => o.Buff != null);
            Assert.Equal(383778, plain.Efecto.EffectUid);
            Assert.False(plain.Critico);
        }

        /// <summary>
        /// A chained spell with no critical list runs its ordinary one, unflagged, even under
        /// a critical cast: Tumulto's critical cast puts 13154's plain "+20".
        /// </summary>
        [Fact]
        public void A_chained_spell_without_a_critical_list_is_not_flagged()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var (x, y) = MapGeometry.CellToPoint(330);
            var one = Monster(-1, MapGeometry.PointToCell(x + 1, y));
            fight.AddPlayer(me); fight.AddOpponent(one);

            var outcomes = EffectEngine.Resolver(fight, me, Tumulto, 1, null, EffectEngine.AlLanzar, 1,
                                                 celdaApuntada: 330, critico: true);
            var bonus = Assert.Single(outcomes, o => o.Buff != null && o.Efecto.EffectId == BasicDamage);
            Assert.Equal(13154, bonus.HechizoOrigen);
            Assert.False(bonus.Critico);
        }

        /// <summary>
        /// The decay hooks what it chains: fired off the grade-4 hook, 28604 at grade 3 goes on
        /// the caster with the round of the trigger, so that Furor I falls a turn later too.
        /// </summary>
        [Fact]
        public void The_decay_hooks_the_grade_it_falls_to()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var puch = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(puch);

            var first = Cast(fight, me, Furor, 2, puch, 301, round: 5);
            FightHandler.EngancharLoPendiente(first, Furor, 2, me.Id, 5);
            var second = Cast(fight, me, Furor, 2, puch, 301, round: 6);
            FightHandler.EngancharLoPendiente(second, Furor, 2, me.Id, 6);
            var hook = Assert.Single(me.Buffs.ActiveSpells);
            Assert.Equal(4, hook.Grado);
            Assert.Equal(6, hook.PuestoEnRonda);

            // End of the turn of round 7: the hook fires, Furor II falls to Furor I.
            var decay = EffectEngine.Resolver(fight, me, FurorChain, 4, me, EffectEngine.AlAcabarElTurno, 7,
                                              celdaApuntada: me.CellId, rondaDelEnganche: 6);
            FightHandler.EngancharLoPendiente(decay, FurorChain, 4, me.Id, 7, incluirElPropio: false);
            Assert.True(me.Buffs.TieneEstado(609));
            Assert.False(me.Buffs.TieneEstado(5192));
            var next = Assert.Single(me.Buffs.ActiveSpells);
            Assert.Equal(3, next.Grado);
            Assert.Equal(7, next.PuestoEnRonda);

            // End of the turn of round 8: grade 2, and nothing of 28604 is left.
            var gone = EffectEngine.Resolver(fight, me, FurorChain, 3, me, EffectEngine.AlAcabarElTurno, 8,
                                             celdaApuntada: me.CellId, rondaDelEnganche: 7);
            Assert.Contains(gone, o => o.Efecto.EffectId == EffectSupport.RemoveSpellEffects && o.BuffsQuitados.Count == 2);
            Assert.False(me.Buffs.TieneEstado(609));
            Assert.Empty(me.Buffs.ActiveSpells);
        }

        // ─── Tumulto ────────────────────────────────────────────────────────────

        /// <summary>Tumulto: one "+20 de daños básicos" per enemy hit, both kept ("-1").</summary>
        [Fact]
        public void Tumulto_stacks_one_bonus_per_enemy()
        {
            var fight = new FightInstance(1, 1);
            var me = Yopuka(10, 300);
            var (x, y) = MapGeometry.CellToPoint(330);
            var one = Monster(-1, MapGeometry.PointToCell(x + 1, y));
            var two = Monster(-2, MapGeometry.PointToCell(x - 1, y));
            fight.AddPlayer(me); fight.AddOpponent(one); fight.AddOpponent(two);

            Cast(fight, me, Tumulto, 1, null, 330);
            Assert.Equal(2, me.Buffs.Puestos.Count(b => b.EffectId == BasicDamage));
            Assert.Equal(40, me.Buffs.DelHechizo(Tumulto, SpellAspect.DanoBase, 1));
        }
    }
}
