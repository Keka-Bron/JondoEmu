using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Ben el Ripata's Hamrack (2955) has Timonel (2618): cast on Ben (2877) beside it, it takes his
    /// invulnerability (952 56) and gives him 276, hurts the enemies round him, and kills the
    /// Hamrack. Weighed as a blow, it went to the character beside it, and Ben stayed invulnerable
    /// for the whole fight.
    /// </summary>
    public class NamedAllyStepTests
    {
        private const int Hamrack = 2955, Ben = 2877, Timonel = 2618;

        [Fact]
        public void The_hamrack_casts_timonel_on_ben_rather_than_on_a_character_beside_it()
        {
            if (!SpellEffects.De(Timonel, 1).Any(r => r.EffectId == 952)) return;   // no spell data

            int[] around = MapGeometry.GetNeighbors(300).ToArray();
            var fight = new FightInstance(1, 1);
            var hamrack = new Fighter
            {
                Id = -5, TeamId = 1, CellId = 300, MaxHP = 900, CurrentHP = 900, Level = 150, IsMonster = true,
                MonsterId = Hamrack, MaxAP = 12, CurrentAP = 12, MaxMP = 0, CurrentMP = 0,
            };
            hamrack.SpellIds.Add(Timonel);
            hamrack.SpellGrades[Timonel] = 1;
            var ben = new Fighter
            {
                Id = -1, TeamId = 1, CellId = around[0], MaxHP = 9000, CurrentHP = 9000, Level = 160, IsMonster = true,
                MonsterId = Ben, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            var character = new Fighter
            {
                Id = 10, TeamId = 0, CellId = around[1], MaxHP = 4000, CurrentHP = 4000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(character); fight.AddMonster(ben); fight.AddMonster(hamrack);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { hamrack, ben, character },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            };

            var spells = FightHandler.TacticsOf(hamrack);
            var timonel = Assert.Single(spells, s => s.Id == Timonel);
            Assert.True(timonel.NamedAllyStep);

            var action = MonsterTactics.Next(board, hamrack, spells);

            Assert.NotNull(action);
            Assert.Equal(Timonel, action!.Spell.Id);
            Assert.Same(ben, action.Target);
        }
    }
}
