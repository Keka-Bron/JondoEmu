using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Rogue's combo, read out of spell 20497 instead of written down.
    /// </summary>
    public class ComboTests
    {
        private static Fighter Bomba(int template = 3112) => new()
        {
            Id = -2, TeamId = 0, CellId = 270, MaxHP = 90, CurrentHP = 90,
            IsMonster = true, MonsterId = template, GradeIndex = 3, Invocador = 10,
            SummonCost = 0, JuegaTurno = false,
        };

        [Fact]
        public void The_ladder_runs_past_the_fifteen_the_sheet_names_and_flattens_at_the_top()
        {
            var escalera = Combo.Ladder();

            // The sheet says "de 1 a 15", and those are the fifteen seen. But the spell's
            // ladder has EIGHTEEN rungs: the top three (2751, 2752, 2753) exist and
            // can be reached, and all three pay the same as fifteen because spell 20500
            // runs out of grades. So the sheet's cap is real, but it is in the
            // percentage and not in the number of rungs.
            Assert.Equal(18, escalera.Count);
            Assert.Equal(2484, escalera[0]);
            Assert.Equal(new[] { 2751, 2752, 2753 }, escalera.Skip(15).ToArray());

            foreach (int estado in escalera.Skip(14))
            {
                var bomba = Bomba();
                bomba.Buffs.PonerEstado(estado);
                Assert.Equal(360, Combo.PercentOf(bomba));
            }
        }

        [Fact]
        public void The_percentages_are_the_ones_the_class_sheet_prints()
        {
            // I: 0%, II: 20%, III: 40%, IV: 60%, V: 80%, VI: 100%, VII: 120%, VIII: 140%,
            // IX: 160%, X: 190%, XI: 220%, XII: 250%, XIII: 280%, XIV: 320%, XV: 360%.
            int[] ficha = { 0, 20, 40, 60, 80, 100, 120, 140, 160, 190, 220, 250, 280, 320, 360 };
            var escalera = Combo.Ladder();

            for (int nivel = 1; nivel <= ficha.Length; nivel++)
            {
                var bomba = Bomba();
                bomba.Buffs.PonerEstado(escalera[nivel - 1]);
                Assert.Equal(nivel, Combo.LevelOf(bomba));
                Assert.Equal(ficha[nivel - 1], Combo.PercentOf(bomba));
            }
        }

        [Fact]
        public void A_bomb_with_no_state_carries_no_combo()
        {
            var bomba = Bomba();
            Assert.Equal(0, Combo.LevelOf(bomba));
            Assert.Equal(0, Combo.PercentOf(bomba));
            Assert.False(Combo.Carries(bomba));
        }

        [Fact]
        public void Casting_the_ladder_walks_one_rung_at_a_time()
        {
            var fight = new FightInstance(1, 1);
            var tymador = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500 };
            var bomba = Bomba();
            fight.AddPlayer(tymador);
            fight.AddPlayer(bomba);

            for (int esperado = 1; esperado <= 5; esperado++)
            {
                EffectEngine.Resolver(fight, bomba, Combo.LadderSpell, 1, bomba,
                                      EffectEngine.AlLanzar, fight.RoundNumber,
                                      celdaApuntada: bomba.CellId);
                Assert.Equal(esperado, Combo.LevelOf(bomba));
            }

            // And on rung five it hits 80% more, which is what the Combo V sheet says.
            Assert.Equal(80, Combo.PercentOf(bomba));
        }

        [Fact]
        public void The_ladder_never_leaves_two_states_on_the_same_bomb()
        {
            var fight = new FightInstance(1, 1);
            var tymador = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500 };
            var bomba = Bomba();
            fight.AddPlayer(tymador);
            fight.AddPlayer(bomba);

            var escalera = Combo.Ladder().ToHashSet();
            for (int vez = 0; vez < 6; vez++)
            {
                EffectEngine.Resolver(fight, bomba, Combo.LadderSpell, 1, bomba,
                                      EffectEngine.AlLanzar, fight.RoundNumber,
                                      celdaApuntada: bomba.CellId);
                Assert.Single(bomba.Buffs.Estados.Where(escalera.Contains));
            }
        }

        [Fact]
        public void Only_a_bomb_can_carry_a_combo()
        {
            // Polvora and Mosquete chain the combo spell with masks the engine does not know how
            // to narrow, and without this it fell on the caster: the Rogue came out with Combo IV on his panel
            // and the bomb stayed the same.
            var fight = new FightInstance(1, 1);
            var tymador = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500 };
            fight.AddPlayer(tymador);

            EffectEngine.Resolver(fight, tymador, Combo.LadderSpell, 1, tymador,
                                  EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: tymador.CellId);

            Assert.Equal(0, Combo.LevelOf(tymador));
            Assert.False(Combo.Carries(tymador));
        }

        [Fact]
        public void A_bomb_never_climbs_past_the_fifteenth_rung()
        {
            var fight = new FightInstance(1, 1);
            var tymador = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500 };
            var bomba = Bomba();
            fight.AddPlayer(tymador);
            fight.AddPlayer(bomba);

            for (int vez = 0; vez < 25; vez++)
            {
                EffectEngine.Resolver(fight, bomba, Combo.LadderSpell, 1, bomba,
                                      EffectEngine.AlLanzar, fight.RoundNumber,
                                      celdaApuntada: bomba.CellId);
            }

            Assert.Equal(Combo.Tope, Combo.LevelOf(bomba));
            Assert.Equal(360, Combo.PercentOf(bomba));
        }

        [Fact]
        public void A_bomb_holds_exactly_one_rung_at_a_time()
        {
            var fight = new FightInstance(1, 1);
            var tymador = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500 };
            var bomba = Bomba();
            fight.AddPlayer(tymador);
            fight.AddPlayer(bomba);

            var escalera = Combo.Ladder().ToHashSet();
            for (int vez = 1; vez <= 6; vez++)
            {
                EffectEngine.Resolver(fight, bomba, Combo.LadderSpell, 1, bomba,
                                      EffectEngine.AlLanzar, fight.RoundNumber,
                                      celdaApuntada: bomba.CellId);
                Assert.Single(bomba.Buffs.Estados.Where(escalera.Contains));
                Assert.Equal(vez, Combo.LevelOf(bomba));
            }
        }

        /// <summary>
        /// The bomb announcing its own combo cast, byte for byte against frame 262 of
        /// "tymador-explobomba resiliente.pcapng": the bomb -5, on cell 216, casting 20497 at
        /// level 54099, with itself and its Rogue in the affected list.
        /// </summary>
        /// <remarks>
        /// Two f4 and no f8 is what tells it apart from an ordinary cast, and why it does not go
        /// through CastAt: that one writes a single f4 and closes with f8 = 1.
        /// </remarks>
        [Fact]
        public void A_bomb_announces_the_combo_it_casts_on_itself()
        {
            byte[] paquete = FightProtocol.BuildComboCast(
                bomb: -5, owner: 53721497699, cell: 216, spell: 20497, levelId: 54099);

            Assert.Equal("18fbffffffffffffffff013a2e10fbffffffffffffffff01220b20fbffffffff" +
                         "ffffffff01220720e380b490c80130d8013a081091a00118d3a60370ac02",
                         Hex(paquete));
        }

        /// <summary>And the same with the grade of 20500 behind it: frame 264.</summary>
        [Fact]
        public void And_the_bonus_spell_right_behind_it()
        {
            byte[] paquete = FightProtocol.BuildComboCast(
                bomb: -5, owner: 53721497699, cell: 216, spell: 20500, levelId: 54103);

            Assert.Equal("18fbffffffffffffffff013a2e10fbffffffffffffffff01220b20fbffffffff" +
                         "ffffffff01220720e380b490c80130d8013a081094a00118d7a60370ac02",
                         Hex(paquete));
        }

        /// <summary>
        /// The look of a growing bomb, byte for byte against frame 259: bone 1562 at scale 105.
        /// </summary>
        [Fact]
        public void A_bomb_grows_by_the_scale_of_its_look()
        {
            byte[] aspecto = FightProtocol.WithScale(
                Pb.New().Var(2, 3).Var(3, 1562).Build(), 105);

            Assert.Equal("1003189a0c2a0169", Hex(aspecto));

            byte[] paquete = FightProtocol.BuildLookChanged(-5, aspecto);
            Assert.Equal("18fbffffffffffffffff01709501d2011508fbffffffffffffffff011a081003" +
                         "189a0c2a0169", Hex(paquete));
        }

        /// <summary>
        /// And the size is the sum of the 1060 buffs, which is why it jumps by fifteen and twenty
        /// and not by a fixed step.
        /// </summary>
        /// <remarks>
        /// Rung 4 in the capture is scale 125, and 125 is 100 + 10 + 15: the 1060 of grade 2 and
        /// the one of grade 3, both alive at once. Checked over every look change in the Rogue
        /// captures: 540 of them come out exactly this way.
        /// </remarks>
        [Fact]
        public void The_size_is_a_hundred_plus_the_live_1060_buffs()
        {
            var bomba = Bomba();
            Assert.Equal(100, Combo.SizeOf(bomba, 1));

            bomba.Buffs.Poner(new Buff { EffectId = 1060, Cuanto = 10, EffectUid = 1,
                                         CaducaEnRonda = -1 }, () => 1);
            bomba.Buffs.Poner(new Buff { EffectId = 1060, Cuanto = 15, EffectUid = 2,
                                         CaducaEnRonda = -1 }, () => 2);

            Assert.Equal(125, Combo.SizeOf(bomba, 1));

            // And never more than two alive: the third evicts the first.
            bomba.Buffs.Poner(new Buff { EffectId = 1060, Cuanto = 20, EffectUid = 3,
                                         CaducaEnRonda = -1 }, () => 3);
            Assert.Equal(135, Combo.SizeOf(bomba, 1));
        }

        /// <summary>The grade of 20500 that pays for each rung, read off the ladder.</summary>
        [Fact]
        public void Every_rung_names_the_grade_that_pays_for_it()
        {
            Assert.Equal(1, Combo.GradeOf(2));
            Assert.Equal(2, Combo.GradeOf(3));
            Assert.Equal(10, Combo.GradeOf(11));
        }

        private static string Hex(byte[] bytes)
            => string.Concat(bytes.Select(b => b.ToString("x2")));

        [Fact]
        public void Two_combos_a_turn_is_what_the_class_sheet_says()
        {
            Assert.Equal(2, FightHandler.CombosPorTurno);
        }

        /// <summary>
        /// No buff number repeats on a bomb, and the ones that go are all announced.
        /// </summary>
        /// <remarks>
        /// It is what left the bomb at Combo I. Setting again the rung it already carried fell into
        /// the «this one was already there» branch and returned the SAME number, so the client got
        /// buff 1 with state 2484 twice and a single removal; the leftover state
        /// stayed on and it is the state's name —«Combo I»— that the client draws.
        ///
        /// The real server does not repeat a number even once: in «tymador-explobomba resiliente» the
        /// same bomb carries 2484 in buff 19 and again in 23, and in frames 260 and
        /// 261 it removes both.
        /// </remarks>
        [Fact]
        public void A_bomb_never_gets_the_same_buff_number_twice()
        {
            var fight = new FightInstance(1, 1);
            var tymador = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500 };
            var bomba = Bomba();
            fight.AddPlayer(tymador);
            fight.AddPlayer(bomba);

            var puestos = new List<int>();
            var quitados = new List<int>();
            for (int vez = 0; vez < 8; vez++)
            {
                foreach (var c in EffectEngine.Resolver(fight, bomba, Combo.LadderSpell, 1, bomba,
                                                        EffectEngine.AlLanzar, fight.RoundNumber,
                                                        celdaApuntada: bomba.CellId))
                {
                    if (c.Buff != null && Combo.EsPeldano(c.Buff.Estado)) puestos.Add(c.Buff.Numero);
                    foreach (var ido in c.BuffsQuitados)
                    {
                        if (Combo.EsPeldano(ido.Estado)) quitados.Add(ido.Numero);
                    }
                }
            }

            Assert.Equal(puestos.Count, puestos.Distinct().Count());

            // And of all those set only the last is still alive: the rest were announced.
            Assert.Equal(puestos.Count - 1, quitados.Distinct().Count());
            Assert.DoesNotContain(puestos[^1], quitados);
        }
    }
}
