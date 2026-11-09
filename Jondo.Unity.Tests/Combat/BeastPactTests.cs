using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Pacto Bestial (31141) sacrifices every Osamodas creature on the board, and each one
    /// sacrificed casts its 32557 again: the state and the +2 AP "se prolonga", it does not pile
    /// up. Its +2 AP row has maxStack 1, so each new one replaces the last -- and the points in
    /// the Osamodas's hand must follow the row, not grow by two with every creature eaten.
    /// </summary>
    public class BeastPactTests
    {
        [Fact]
        public async Task The_creatures_sacrificed_give_the_ap_once()
        {
            var fight = new FightInstance(1, 1);
            var osamodas = new Fighter { Id = 1, TeamId = 0, CellId = 300, MaxHP = 3000, CurrentHP = 3000, Level = 200,
                                         MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3, Initiative = 1000 };
            var foe = new Fighter { Id = -1, TeamId = 1, CellId = 500, MaxHP = 5000, CurrentHP = 5000, Level = 50,
                                    MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494 };
            fight.AddPlayer(osamodas); fight.AddOpponent(foe);
            fight.StartFight();
            Assert.Same(osamodas, fight.CurrentFighter);

            // Four Tofus, the first creature Pacto Bestial names (F8070).
            foreach (int cell in new[] { 271, 272, 328, 329 })
                fight.Invocar(new Fighter { Id = fight.SiguienteIdDeInvocado(), TeamId = 0, CellId = cell, MaxHP = 500, CurrentHP = 500,
                                            Level = 200, IsMonster = true, MonsterId = 8070, GradeIndex = 3, JuegaTurno = true }, osamodas);

            await FightHandler.AplicarEfectosAsync(null, fight, osamodas, 31141, 1, osamodas, EffectEngine.AlLanzar, osamodas.CellId);

            Assert.True(fight.Todos.Where(f => f.EsInvocado).All(f => !f.IsAlive), "a creature outlived the pact");
            Assert.Equal(2, osamodas.Buffs.De(1, fight.RoundNumber));
            Assert.Equal(9, osamodas.CurrentAP);
        }

        /// <summary>
        /// The harness's clean board takes away what an earlier try left waiting on a trigger,
        /// as it takes its rows: left on, Pacto Bestial's "on summoning" sacrificed every
        /// creature the summons' tries brought out after it.
        /// </summary>
        [Fact]
        public async Task A_clean_board_takes_its_hook_away()
        {
            var fight = new FightInstance(1, 1);
            var osamodas = new Fighter { Id = 54000000071, TeamId = 0, CellId = 300, MaxHP = 3000, CurrentHP = 3000, Level = 200,
                                         MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3, Initiative = 1000 };
            var foe = new Fighter { Id = -1, TeamId = 1, CellId = 500, MaxHP = 5000, CurrentHP = 5000, Level = 50,
                                    MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494 };
            fight.AddPlayer(osamodas); fight.AddOpponent(foe);
            fight.StartFight();
            await using var wire = await PortalTests.Wire.Open(osamodas.Id);
            await FightHandler.FreshTurnAsync(wire.Session.Stream, fight, osamodas, keepBoard: false, withoutSummons: true);

            await FightHandler.AplicarEfectosAsync(null, fight, osamodas, 31141, 1, osamodas, EffectEngine.AlLanzar, osamodas.CellId);
            Assert.Contains(osamodas.Buffs.ActiveSpells, h => h.Hechizo == 32557);

            await FightHandler.FreshTurnAsync(wire.Session.Stream, fight, osamodas, keepBoard: false, withoutSummons: true);
            Assert.DoesNotContain(osamodas.Buffs.ActiveSpells, h => h.Hechizo == 32557);
            Assert.Empty(osamodas.Buffs.Puestos);
        }
    }
}
