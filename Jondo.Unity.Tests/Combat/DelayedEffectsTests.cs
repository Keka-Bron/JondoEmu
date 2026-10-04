using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What waits and what falls when: the delayed effects of the Ocra's Paso de Cacería and
    /// Baliza de Supervivencia, the points a turn start carries from the live buffs, and the
    /// caster's turn as the moment an expired row falls.
    /// </summary>
    public class DelayedEffectsTests
    {
        private const int PasoDeCaceria = 32464;
        private const int SurvivalBeaconOwnSpell = 32477;
        private const int SurvivalBeacon = 8348;

        private static Fighter Person(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3,
        };

        /// <summary>
        /// The capture, three casts out of three: the Ocra lands on the aimed cell, and the +1
        /// MP waits as a hidden row for the next round -- nothing on his points now.
        /// </summary>
        [Fact]
        public void Paso_de_Caceria_carries_the_caster_and_keeps_the_movement_point_for_next_turn()
        {
            var fight = new FightInstance(1, 1);
            var ocra = Person(10, 0, 344);
            var enemy = Person(20, 1, 400);
            fight.AddPlayer(ocra); fight.AddOpponent(enemy);

            var outcomes = EffectEngine.Resolver(fight, ocra, PasoDeCaceria, 3, ocra, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 329);

            var jump = Assert.Single(outcomes, o => o.Mueve);
            Assert.Equal(ocra, jump.Sobre);
            Assert.Equal(329, jump.CasillaHasta);
            Assert.Equal(329, ocra.CellId);

            Assert.DoesNotContain(outcomes, o => o.Caracteristica != 0);
            var waiting = ocra.Buffs.Puestos.Where(b => b.Pendiente).ToList();
            Assert.Equal(2, waiting.Count);
            Assert.All(waiting, b => Assert.Equal(fight.RoundNumber + 1, b.EmpiezaEnRonda));
            Assert.Contains(waiting, b => b.EffectId == 3793);
            var mp = Assert.Single(waiting, b => b.EffectId == 128);
            Assert.Equal(Fighter.CaracteristicaDePuntosDeMovimiento, mp.Caracteristica);
            Assert.Equal(1, mp.Cuanto);
            Assert.Equal(0, ocra.Buffs.De(Fighter.CaracteristicaDePuntosDeMovimiento, fight.RoundNumber));
        }

        /// <summary>
        /// The round after: the marker goes off, the +1 MP becomes a live row naming the
        /// waiting one and lasting one round, and the turn starts with one more step.
        /// </summary>
        [Fact]
        public void Next_round_the_waiting_movement_point_goes_live_and_the_turn_carries_it()
        {
            var fight = new FightInstance(1, 1);
            var ocra = Person(10, 0, 344);
            var enemy = Person(20, 1, 400);
            fight.AddPlayer(ocra); fight.AddOpponent(enemy);
            EffectEngine.Resolver(fight, ocra, PasoDeCaceria, 3, ocra, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: 329);
            var waitingMp = ocra.Buffs.Puestos.Single(b => b.Pendiente && b.EffectId == 128);

            Assert.Empty(EffectEngine.ActivateDuePending(fight, fight.RoundNumber));
            var due = EffectEngine.ActivateDuePending(fight, fight.RoundNumber + 1);

            Assert.Equal(2, due.Count);
            Assert.Contains(due, d => d.Marks);
            var live = Assert.Single(due, d => d.Live != null).Live;
            Assert.Equal(waitingMp.Numero, live.Padre);
            Assert.Equal(fight.RoundNumber + 2, live.CaducaEnRonda);
            Assert.DoesNotContain(ocra.Buffs.Puestos, b => b.Pendiente);
            Assert.Equal(1, ocra.Buffs.De(Fighter.CaracteristicaDePuntosDeMovimiento, fight.RoundNumber + 1));

            ocra.StartTurn(fight.RoundNumber + 1);
            Assert.Equal(4, ocra.CurrentMP);
            Assert.Equal(7, ocra.CurrentAP);
        }

        /// <summary>
        /// The beacon's own spell at her birth: its kill has a delay of two, so she is not
        /// killed but marked to die -- a waiting row for the round after next -- and the
        /// activation of that round is a kill. That is the two rounds of her sheet.
        /// </summary>
        [Fact]
        public void The_survival_beacon_dies_two_rounds_after_her_birth_and_not_at_it()
        {
            var fight = new FightInstance(1, 1);
            var ocra = Person(10, 0, 344);
            var enemy = Person(20, 1, 400);
            fight.AddPlayer(ocra); fight.AddOpponent(enemy);
            var beacon = new Fighter
            {
                Id = -3, TeamId = 0, CellId = 442, MaxHP = 1050, CurrentHP = 1050, IsMonster = true,
                MonsterId = SurvivalBeacon, GradeIndex = 3, Invocador = ocra.Id, SummonCost = 1, Level = 3,
            };
            fight.Invocar(beacon, ocra);

            var outcomes = EffectEngine.Resolver(fight, beacon, SurvivalBeaconOwnSpell, 1, beacon,
                                                 EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: 442);

            Assert.DoesNotContain(outcomes, o => o.Fulmina);
            Assert.True(beacon.IsAlive);
            var waiting = Assert.Single(beacon.Buffs.Puestos, b => b.Pendiente);
            Assert.Equal(EffectEngine.MataAlObjetivo, waiting.EffectId);
            Assert.Equal(fight.RoundNumber + 2, waiting.EmpiezaEnRonda);
            Assert.Equal(fight.RoundNumber + 2, waiting.CaducaEnRonda);

            Assert.Empty(EffectEngine.ActivateDuePending(fight, fight.RoundNumber + 1));
            var due = Assert.Single(EffectEngine.ActivateDuePending(fight, fight.RoundNumber + 2));
            Assert.True(due.Kills);
            Assert.Equal(beacon, due.Target);
            Assert.Null(due.Live);
        }

        /// <summary>
        /// A turn starts with the points the live rows say: "+1 PA durante 3 turnos" is one
        /// more on each of them, "-2 PA" put on somebody before his turn is two fewer.
        /// </summary>
        [Fact]
        public void A_turn_start_carries_the_live_point_buffs()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 300);
            me.Buffs.Poner(new Buff { EffectId = 111, Caracteristica = Fighter.CaracteristicaDePuntosDeAccion, Cuanto = 1,
                                      Quien = 10, EmpiezaEnRonda = 1, CaducaEnRonda = 4 }, fight.SiguienteEmbrujo);
            me.Buffs.Poner(new Buff { EffectId = 1079, Caracteristica = Fighter.CaracteristicaDePuntosDeAccion, Cuanto = -2,
                                      Quien = 20, EmpiezaEnRonda = 2, CaducaEnRonda = 3 }, fight.SiguienteEmbrujo);
            me.Buffs.Poner(new Buff { EffectId = 128, Caracteristica = Fighter.CaracteristicaDePuntosDeMovimiento, Cuanto = 1,
                                      Quien = 10, EmpiezaEnRonda = 3, CaducaEnRonda = 4, Pendiente = true }, fight.SiguienteEmbrujo);

            me.StartTurn(2);
            Assert.Equal(6, me.CurrentAP);
            Assert.Equal(3, me.CurrentMP);

            me.StartTurn(1);
            Assert.Equal(8, me.CurrentAP);
        }

        /// <summary>
        /// An expired row falls at ITS CASTER's turn: the enemy's "-2 PA" on me, put in round
        /// 2 for one round, still costs me the two at my round-3 turn when the enemy plays
        /// after me; it falls when his turn comes. Rows of a caster who is gone fall at once.
        /// </summary>
        [Fact]
        public void An_expired_row_falls_at_its_casters_turn_and_still_counts_until_then()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 300);
            var row = me.Buffs.Poner(new Buff { EffectId = 1079, Caracteristica = Fighter.CaracteristicaDePuntosDeAccion,
                                                Cuanto = -2, Quien = 20, EmpiezaEnRonda = 2, CaducaEnRonda = 3 },
                                     fight.SiguienteEmbrujo);
            var orphan = me.Buffs.Poner(new Buff { EffectId = 1079, Caracteristica = Fighter.CaracteristicaDePuntosDeAccion,
                                                   Cuanto = -1, Quien = 0, EmpiezaEnRonda = 2, CaducaEnRonda = 3 },
                                        fight.SiguienteEmbrujo);

            var atMyTurn = me.Buffs.Barrer(3, b => b.Quien == 0 || b.Quien == 10);
            Assert.Equal(new[] { orphan }, atMyTurn);
            Assert.True(row.Vivo(3));
            Assert.Equal(-2, me.Buffs.De(Fighter.CaracteristicaDePuntosDeAccion, 3));

            var atHisTurn = me.Buffs.Barrer(3, b => b.Quien == 0 || b.Quien == 20);
            Assert.Equal(new[] { row }, atHisTurn);
            Assert.Equal(0, me.Buffs.De(Fighter.CaracteristicaDePuntosDeAccion, 3));
        }
    }
}
