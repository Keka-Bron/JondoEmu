using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The four Tymador spells reported dead in one afternoon -- Fusil, Kabúm, Colado and
    /// Remisión -- each on the cells of the fight it was reported from or of its capture, and
    /// each fixed in the common engine: a zone shape, a mask letter, a trigger.
    /// </summary>
    public class ReportedTymadorSpellsTests
    {
        private const int Fusil = 13440;
        private const int Kabum = 13450;
        private const int Colado = 13443;
        private const int Remision = 13445;
        private const int RemisionPush = 13430;
        private const int WaterBombStorm = 13462;
        private const int Explobomba = 3112;
        private const int WaterBomb = 3114;

        private static Fighter Person(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 8, CurrentAP = 8,
        };

        private static Fighter Bomb(long id, Fighter owner, int cell, int template = Explobomba) => new()
        {
            Id = id, TeamId = owner.TeamId, CellId = cell, MaxHP = 900, CurrentHP = 900,
            IsMonster = true, MonsterId = template, GradeIndex = 3, Invocador = owner.Id,
            SummonCost = 0, JuegaTurno = false, Level = 3,
        };

        /// <summary>
        /// The reported cast: the Tymador on 261 fires at 259 with the Ocra on 203, at the end
        /// of the bar. The push goes "hacia los extremos": from the centre outwards along the
        /// bar, two cells, to (12,2).
        /// </summary>
        [Fact]
        public void Fusil_pushes_the_one_at_the_end_of_its_bar_outwards()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 261);
            var ocra = Person(20, 1, 203);
            fight.AddPlayer(me); fight.AddOpponent(ocra);

            var outcomes = EffectEngine.Resolver(fight, me, Fusil, 3, me, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 259);

            var push = Assert.Single(outcomes, o => o.Mueve && o.Sobre == ocra);
            Assert.Equal(MapGeometry.PointToCell(12, 2), push.CasillaHasta);
            Assert.Equal(push.CasillaHasta, ocra.CellId);
        }

        /// <summary>
        /// Kabúm at his own cell, as in its capture: the state lands on him -- an ally of his
        /// own -- for two turns, and every bomb of his in the cross climbs one rung.
        /// </summary>
        [Fact]
        public void Kabum_puts_its_state_on_the_caster_standing_in_his_own_zone()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 301);
            var enemy = Person(20, 1, 400);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var bomb = Bomb(-1, me, 274);
            fight.Invocar(bomb, me);
            bomb.Buffs.PonerEstado(2484);

            EffectEngine.Resolver(fight, me, Kabum, 2, me, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: 301);

            Assert.Contains(92, me.Buffs.Estados);
            var state = Assert.Single(me.Buffs.Puestos, b => b.Estado == 92);
            Assert.Equal(fight.RoundNumber + 2, state.CaducaEnRonda);
            Assert.Equal(2, Combo.LevelOf(bomb));
        }

        /// <summary>
        /// A water bomb going off two cells from a Tymador under Kabúm: instead of hurting him
        /// its storm reaches him through his own state -- Kabúm's rung on him, climbed once by
        /// the second 20752 (the capture drops the 2567 row and puts 2568), and the +1 AP of
        /// the water bomb for three turns with its state 3431 -- the way the capture's 23513,
        /// 20752 and 20748 do.
        /// </summary>
        [Fact]
        public void A_bomb_exploding_near_a_Tymador_under_Kabum_buffs_him_instead()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 301);
            var enemy = Person(20, 1, 400);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var bomb = Bomb(-18, me, 274, WaterBomb);
            fight.Invocar(bomb, me);
            me.Buffs.PonerEstado(92);

            EffectEngine.Resolver(fight, bomb, WaterBombStorm, 3, bomb, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: 274,
                                  bombasYaEstalladas: new HashSet<long> { bomb.Id });

            Assert.Contains(2568, me.Buffs.Estados);
            Assert.DoesNotContain(2567, me.Buffs.Estados);
            Assert.Contains(3431, me.Buffs.Estados);
            var ap = Assert.Single(me.Buffs.Puestos, b => b.Caracteristica == Fighter.CaracteristicaDePuntosDeAccion);
            Assert.Equal(1, ap.Cuanto);
            Assert.Equal(fight.RoundNumber + 3, ap.CaducaEnRonda);
        }

        /// <summary>
        /// Colado's ring: the bomb two cells from the centre goes to the cell two away on the
        /// other side, in the bomb's own name. The capture's first cast: 274 through 301 lands
        /// on 328.
        /// </summary>
        [Fact]
        public void Colado_mirrors_the_bomb_on_its_ring_through_the_centre()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 300);
            var enemy = Person(20, 1, 400);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var bomb = Bomb(-17, me, 274);
            fight.Invocar(bomb, me);

            var outcomes = EffectEngine.Resolver(fight, me, Colado, 2, me, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 301);

            var jump = Assert.Single(outcomes, o => o.Mueve && o.Sobre == bomb);
            Assert.Equal(328, jump.CasillaHasta);
            Assert.Equal(328, bomb.CellId);
        }

        /// <summary>
        /// The capture: Remisión on the enemy at 260, then the Tymador hits him in melee from
        /// 273. The melee trigger marks the enemy, casts the push spell at his cell, and the
        /// push -- "a,A,O,e3795" -- goes to the attacker and not to the marked bearer: six
        /// cells away from 260, from 273 to 354, with the script marker on 273 first.
        /// </summary>
        [Fact]
        public void Remision_throws_the_melee_attacker_six_cells_back()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 273);
            var enemy = Person(20, 1, 260);
            fight.AddPlayer(me); fight.AddOpponent(enemy);

            fight.TriggeringAttacker = me;
            var outcomes = EffectEngine.Resolver(fight, me, Remision, 3, enemy, EffectEngine.CuandoMePeganDeCerca,
                                                 fight.RoundNumber, celdaApuntada: 260);
            fight.TriggeringAttacker = null;

            Assert.Contains(outcomes, o => o.Buff != null && o.Buff.Estado == 3795 && o.Sobre == enemy);
            var marker = Assert.Single(outcomes, o => o.Marcador);
            Assert.Equal(me, marker.Sobre);
            Assert.Equal(RemisionPush, marker.HechizoOrigen);
            var push = Assert.Single(outcomes, o => o.Mueve);
            Assert.Equal(me, push.Sobre);
            Assert.Equal(354, push.CasillaHasta);
            Assert.DoesNotContain(outcomes, o => o.Mueve && o.Sobre == enemy);
        }

        /// <summary>
        /// Llamita, "daños en el peor elemento del lanzador": a hit in the element of his lowest
        /// characteristic, which went to the panel as a row nobody applied and hurt nobody.
        /// </summary>
        [Fact]
        public void Llamita_hits_in_the_casters_worst_element()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 300);
            me.Strength = 400; me.Intelligence = 30; me.Chance = 200; me.Agility = 100;
            var enemy = Person(20, 1, 301);
            fight.AddPlayer(me); fight.AddOpponent(enemy);

            var golpes = EffectEngine.Golpes(fight, me, 24006, 3, enemy, celdaApuntada: 301);

            var golpe = Assert.Single(golpes);
            Assert.Equal(enemy, golpe.Sobre);
            Assert.Equal(2, golpe.Elemento);   // fire: his weakest is intelligence
            Assert.True(EffectEngine.EsDeDano(2832));
        }

        /// <summary>
        /// Remisión on a bomb of his: the "-N de daños recibidos" under DR is a row on the bomb,
        /// hidden, with the cut in it -- 20 at grade 3, the capture's 23 being a critical cast --
        /// that a ranged blow reads and a melee one does not, for three rounds. Nothing of it
        /// on the enemy the same spell marks, and nothing fires on the blow's own trigger.
        /// </summary>
        [Fact]
        public void Remision_on_a_bomb_cuts_ranged_damage_and_not_melee()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 232);
            var enemy = Person(20, 1, 400);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var bomb = Bomb(-1, me, 218);
            fight.Invocar(bomb, me);

            var outcomes = EffectEngine.Resolver(fight, me, Remision, 3, bomb, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 218);

            var row = Assert.Single(outcomes, o => o.FilaEnganchada);
            Assert.Equal(bomb, row.Sobre);
            Assert.Equal(265, row.Efecto.EffectId);
            Assert.Equal("DR", row.Buff.Disparador);
            Assert.Equal(20, row.Buff.Cuanto);
            Assert.Equal(fight.RoundNumber + 3, row.Buff.CaducaEnRonda);
            Assert.Contains(2512, bomb.Buffs.Estados);

            Assert.Equal(20, bomb.Buffs.ReduccionDeDanoRecibido(fight.RoundNumber, new[] { "D", "DR" }));
            Assert.Equal(0, bomb.Buffs.ReduccionDeDanoRecibido(fight.RoundNumber, new[] { "D", "DM", "DCAC" }));
            Assert.Equal(0, enemy.Buffs.ReduccionDeDanoRecibido(fight.RoundNumber, new[] { "D", "DR" }));

            // The blow's trigger fires the hooked spell on the bomb: no second row comes of it.
            fight.TriggeringAttacker = enemy;
            EffectEngine.Resolver(fight, me, Remision, 3, bomb, EffectEngine.CuandoMePeganDeLejos,
                                  fight.RoundNumber, celdaApuntada: 218);
            fight.TriggeringAttacker = null;
            Assert.Single(bomb.Buffs.Puestos, b => b.EffectId == 265);
        }

        /// <summary>The bar and the ring are what the shapes say; nothing hits the caster's cell.</summary>
        [Fact]
        public void Fusil_leaves_the_caster_alone_and_hits_the_bar_only()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 261);
            var friend = Person(11, 0, 245);   // straight behind the aimed cell, off the bar
            var ocra = Person(20, 1, 203);
            fight.AddPlayer(me); fight.AddPlayer(friend); fight.AddOpponent(ocra);

            var outcomes = EffectEngine.Resolver(fight, me, Fusil, 3, me, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 259);

            Assert.All(outcomes.Where(o => o.Mueve), o => Assert.Equal(ocra, o.Sobre));
            Assert.Equal(261, me.CellId);
            Assert.Equal(245, friend.CellId);
        }
    }
}
