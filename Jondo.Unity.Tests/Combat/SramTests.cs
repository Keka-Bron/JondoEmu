using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>The Sram's spells, against the class's captures (Clases/Sram).</summary>
    public class SramTests
    {
        private const int Doble = 12915;
        private const int DobleChild = 12966;
        private const int Arsenico = 12907;

        /// <summary>
        /// Doble runs 12966 twice: at grade 1 on the double -- its "cast 12964 at the end of the
        /// turn", which swaps it with the Sram and kills its caster -- and at grade 2 on the Sram,
        /// a state. Only the double is hooked with grade 1: in sram-doble.pcapng 12964 is cast by
        /// the double in its own second turn and it is the double that dies. Hooked on the Sram
        /// too, he cast it at himself at the end of his turn and died of it.
        /// </summary>
        [Fact]
        public void The_doubles_swap_is_hooked_on_the_double_and_not_on_the_sram()
        {
            var sram = new Fighter { Id = 13825565, TeamId = 0 };
            var doble = new Fighter { Id = -4, TeamId = 0, Invocador = sram.Id };
            var outcomes = new List<Outcome>
            {
                new Outcome { Sobre = doble, Caster = sram, HechizoOrigen = DobleChild, NivelOrigen = 1,
                              Buff = new Buff { EffectId = 950, CaducaEnRonda = -1 } },
                new Outcome { Sobre = sram, Caster = doble, HechizoOrigen = DobleChild, NivelOrigen = 2,
                              Buff = new Buff { EffectId = 950, CaducaEnRonda = -1 } },
            };

            FightHandler.EngancharLoPendiente(outcomes, Doble, 3, sram.Id, 1);

            Assert.Contains(doble.Buffs.ActiveSpells, a => a.Hechizo == DobleChild && a.Grado == 1);
            Assert.DoesNotContain(sram.Buffs.ActiveSpells, a => a.Hechizo == DobleChild && a.Grado == 1);
        }

        /// <summary>
        /// Arsénico's damage is a poison: "98 under TB". In sram-arsenico.pcapng the cast only
        /// hooks the row, and the air damage goes out at the start of each target's turn.
        /// </summary>
        [Fact]
        public void Arsenico_hits_at_the_start_of_the_targets_turn_and_not_at_the_cast()
        {
            var fight = new FightInstance(1, 1, 1);
            var sram = new Fighter { Id = 1, CellId = 200, MaxHP = 1000, CurrentHP = 1000, Level = 200 };
            var enemy = new Fighter { Id = -2, CellId = 202, MaxHP = 1000, CurrentHP = 1000, Level = 200 };
            fight.AddPlayer(sram);
            fight.AddOpponent(enemy);

            Assert.Empty(EffectEngine.Golpes(fight, sram, Arsenico, 3, enemy, enemy.CellId));
            var atTurnStart = EffectEngine.Golpes(fight, sram, Arsenico, 3, enemy, enemy.CellId,
                                                  disparador: EffectEngine.AlEmpezarElTurno);
            var blow = Assert.Single(atTurnStart);
            Assert.Equal((98, enemy), (blow.Efecto.EffectId, blow.Sobre));
        }
    }
}
