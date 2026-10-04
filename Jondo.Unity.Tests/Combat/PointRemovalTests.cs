using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// AP and MP removal against dodge: one roll per point, the game's odds, what lands and
    /// what is dodged, and the points a target counts with outside his turn.
    /// </summary>
    [Collection("removal dice")]
    public class PointRemovalTests : System.IDisposable
    {
        public void Dispose() => EffectEngine.DadoDeRetiradaPorDefecto();

        private const int WaterBombStorm = 13462;   // its 25589 removes AP from everybody in a circle of two
        private const int PalabraJuguetona = 25877; // -2 PM (1080)

        private static Fighter Person(long id, int team, int cell, int wisdom = 0) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3,
            Otras = { [EffectEngine.RetiraPA] = wisdom / 10, [EffectEngine.RetiraPM] = wisdom / 10,
                      [EffectEngine.EsquivaPA] = wisdom / 10, [EffectEngine.EsquivaPM] = wisdom / 10 },
        };

        /// <summary>Feeds the removal dice a fixed sequence of draws.</summary>
        private static void Dice(params double[] draws)
        {
            var queue = new Queue<double>(draws);
            EffectEngine.DadoDeRetirada = () => queue.Count > 0 ? queue.Dequeue() : 0.999;
        }

        /// <summary>
        /// Equal removal and dodge, full points: one in two per point. The odds fall as the
        /// target's points do -- five left of seven is 5/7 of that -- and never leave 10 %–90 %.
        /// </summary>
        [Fact]
        public void The_odds_are_the_games_own()
        {
            var caster = Person(10, 0, 300, wisdom: 100);
            var target = Person(20, 1, 400, wisdom: 100);

            Dice(0.49, 0.51);
            Assert.Equal(1, EffectEngine.PuntosQuePierde(caster, target, 1, pedidos: 2, cuentan: 7, maximo: 7, ronda: 1));

            // Odds of 0.9 against a target with no dodge at all, still capped at 90 %.
            var helpless = Person(21, 1, 401, wisdom: 0);
            var strong = Person(11, 0, 301, wisdom: 500);
            Dice(0.89, 0.91);
            Assert.Equal(1, EffectEngine.PuntosQuePierde(strong, helpless, 1, pedidos: 2, cuentan: 7, maximo: 7, ronda: 1));

            // And no lower than 10 % against a wall of dodge.
            var wall = Person(22, 1, 402, wisdom: 2000);
            Dice(0.09, 0.11);
            Assert.Equal(1, EffectEngine.PuntosQuePierde(caster, wall, 1, pedidos: 2, cuentan: 7, maximo: 7, ronda: 1));
        }

        /// <summary>
        /// A "-2 PM" that lands one and loses one: the row says one MP lost, announced as the
        /// plain loss (169), and the other point is dodged.
        /// </summary>
        [Fact]
        public void A_partly_dodged_removal_lands_what_it_lands_and_says_what_was_dodged()
        {
            var fight = new FightInstance(1, 1);
            var eni = Person(10, 0, 300, wisdom: 100);
            var target = Person(20, 1, 301, wisdom: 100);
            fight.AddPlayer(eni); fight.AddOpponent(target);

            Dice(0.2, 0.8);
            var outcomes = EffectEngine.Resolver(fight, eni, PalabraJuguetona, 1, target, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 301);

            var loss = Assert.Single(outcomes, o => o.Caracteristica == Fighter.CaracteristicaDePuntosDeMovimiento);
            Assert.Equal(-1, loss.Cuanto);
            Assert.Equal(1, loss.PuntosEsquivados);
            Assert.Equal(169, loss.EfectoEnElCable);
            Assert.Equal(-1, target.Buffs.De(Fighter.CaracteristicaDePuntosDeMovimiento, fight.RoundNumber));
        }

        /// <summary>Dodged whole, there is nothing but the dodge to tell: no row, no points.</summary>
        [Fact]
        public void A_removal_dodged_whole_leaves_no_row()
        {
            var fight = new FightInstance(1, 1);
            var eni = Person(10, 0, 300, wisdom: 100);
            var target = Person(20, 1, 301, wisdom: 100);
            fight.AddPlayer(eni); fight.AddOpponent(target);

            Dice(0.9, 0.9);
            var outcomes = EffectEngine.Resolver(fight, eni, PalabraJuguetona, 1, target, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 301);

            var dodge = Assert.Single(outcomes, o => o.PuntosEsquivados > 0);
            Assert.Equal(2, dodge.PuntosEsquivados);
            Assert.Null(dodge.Buff);
            Assert.Equal(0, dodge.Caracteristica);
            Assert.Empty(target.Buffs.Puestos);
        }

        /// <summary>
        /// Outside his turn a target counts with the points his next turn starts with, not with
        /// what he had left: an Ocra who spent six of seven can still lose two.
        /// </summary>
        [Fact]
        public void Outside_his_turn_the_target_counts_with_his_next_turns_points()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 0, 300, wisdom: 100);
            var ocra = Person(20, 1, 301, wisdom: 100);
            ocra.CurrentAP = 1;
            fight.AddPlayer(me); fight.AddOpponent(ocra);
            var bomb = new Fighter
            {
                Id = -1, TeamId = 0, CellId = 302, MaxHP = 900, CurrentHP = 900, IsMonster = true,
                MonsterId = 3114, GradeIndex = 3, Invocador = 10, SummonCost = 0, JuegaTurno = false, Level = 3,
                MaxAP = 0, MaxMP = 0,
            };
            fight.Invocar(bomb, me);

            // The "-2 PA" written on the explosion itself is the sheet's copy (for the client
            // only); the removal the real server makes is 25589's, one 20680 per fighter in
            // the circle, at the grade the bomb's combo says: two points from Combo VI up
            // (state 2493), as the capture "tymador-bomba de agua y sismobomba resiliente"
            // shows with its jwe 308 after the 20680s.
            bomb.Buffs.PonerEstado(2493);

            Assert.Equal(7, EffectEngine.PuntosQueCuentan(fight, ocra, Fighter.CaracteristicaDePuntosDeAccion, fight.RoundNumber));

            Dice(0.05, 0.05);
            var outcomes = EffectEngine.Resolver(fight, bomb, WaterBombStorm, 3, bomb, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 302,
                                                 bombasYaEstalladas: new HashSet<long> { bomb.Id });

            var loss = Assert.Single(outcomes, o => o.Caracteristica == Fighter.CaracteristicaDePuntosDeAccion && o.Sobre == ocra);
            Assert.Equal(-2, loss.Cuanto);
            Assert.Equal(168, loss.EfectoEnElCable);
            ocra.StartTurn(fight.RoundNumber);
            Assert.Equal(5, ocra.CurrentAP);
        }

        /// <summary>The dodge frame, as the 401 of the captures: f3 who cast, f14 308/309, f28 { f1 how many, f3 who }.</summary>
        [Fact]
        public void The_dodge_frame_is_the_captures()
        {
            byte[] frame = FightProtocol.BuildPointsDodged(53721104483, 23, -5, 1);
            Assert.Equal("18e3809c90c80170b502e2010d080118fbffffffffffffffff01",
                         string.Concat(frame.Select(b => b.ToString("x2"))));
        }
    }
}
