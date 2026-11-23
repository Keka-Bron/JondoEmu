using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// XLII (3849) starts with his behaviour spell 5106: an Auroraire summoned, state 63 on him for
    /// the whole fight and "950 237 for a turn, under M, trigger duration 63" -- moved by anyone at
    /// any point of the fight, he is in state 237 for a turn, which Desajuste (5123, HS=237) needs.
    /// The row's own duration (1) is what it gives, not how long it listens: read that way, it
    /// went deaf after round 2 and a push later in the fight gave him nothing.
    /// </summary>
    [Collection("MapManager")]
    public class XliiMovedStateTests
    {
        private const int Xlii = 3849, Behaviour = 5106, Moved = 237, PushSpell = 32426;

        [Fact]
        public void A_behaviour_row_with_a_duration_and_no_limit_listens_for_good()
        {
            var row = new SpellEffect { EffectId = 950, Triggers = "M", Duration = 1, TriggerDuration = 63 };
            Assert.Equal(-1, EffectEngine.CaducidadDeLaFila(row, 1, true));
            Assert.Equal(2, EffectEngine.CaducidadDeLaFila(row, 1, false));   // a spell cast in the fight keeps its reading
        }

        [Fact]
        public async Task Pushed_late_in_the_fight_he_is_in_state_237()
        {
            if (!SpellEffects.De(Behaviour, 1).Any(row => row.EffectId == 950 && row.Value == Moved)) return;
            if (!SpellEffects.De(PushSpell, 1).Any(row => row.EffectId == 5)) return;   // no spell data

            int bossCell = 330;
            int attackerCell = MapGeometry.GetNeighbors(bossCell).First();
            var fight = new FightInstance(Xlii, 1);
            var character = new Fighter
            {
                Id = 10, CellId = attackerCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 9,
            };
            fight.AddPlayer(character);
            var boss = new Fighter
            {
                Id = -1, CellId = bossCell, MaxHP = 13000, CurrentHP = 13000, Level = 170, IsMonster = true,
                MonsterId = Xlii, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, TeamId = 1,
            };
            fight.AddMonster(boss);
            fight.StartFight();
            character.CellId = attackerCell; boss.CellId = bossCell;

            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            Assert.DoesNotContain(Moved, boss.Buffs.Estados);

            // Five rounds on, he is still listening.
            int round = fight.RoundNumber;
            while (fight.RoundNumber < round + 5) fight.NextTurn();
            boss.Buffs.BarrerEnganches(fight.RoundNumber);

            await FightHandler.AplicarEfectosAsync(null, fight, character, PushSpell, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            Assert.NotEqual(bossCell, boss.CellId);
            Assert.Contains(Moved, boss.Buffs.Estados);
        }
    }
}
