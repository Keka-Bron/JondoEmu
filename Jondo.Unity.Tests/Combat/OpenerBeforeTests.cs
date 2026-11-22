using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Guerra (6014) fights Vulnerable (1569). Her Martirio (15276) asks for her not to be Brave
    /// (HS!1567), her Lynchage and Impact (15257, 15259) for her to be Brave, and only Bravoure
    /// (15249) makes her so. Weighed blow by blow, Martirio always won and Bravoure never came:
    /// a spell that opens the caster's locked blows goes first.
    /// </summary>
    public class OpenerBeforeTests
    {
        private const int Guerra = 6014, Bravoure = 15249, Vulnerable = 1569;
        private static readonly int[] Spells = { 15249, 15257, 15259, 15261, 15267, 15276 };

        [Fact]
        public void Guerra_casts_bravoure_before_the_blows_it_opens()
        {
            if (SpellEffects.De(Bravoure, 1).Count == 0 || SpellStates.Of(Vulnerable) == null) return;   // no spell data

            var fight = new FightInstance(1, 1);
            var guerra = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 300, MaxHP = 22000, CurrentHP = 22000, Level = 220, IsMonster = true,
                MonsterId = Guerra, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            foreach (int spell in Spells) { guerra.SpellIds.Add(spell); guerra.SpellGrades[spell] = 1; }
            var character = new Fighter
            {
                Id = 10, TeamId = 0, CellId = MapGeometry.GetNeighbors(300).First(), MaxHP = 6000, CurrentHP = 6000,
                Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(character); fight.AddMonster(guerra);
            guerra.Buffs.PonerEstado(Vulnerable);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { guerra, character },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            };

            var spells = FightHandler.TacticsOf(guerra);
            var usable = spells.FindAll(s => SpellCriteria.Allows(guerra, s.Id, s.Grade));
            var locked = spells.FindAll(s => !SpellCriteria.Allows(guerra, s.Id, s.Grade));
            Assert.Contains(usable, s => s.Id == Bravoure);

            var action = MonsterTactics.Next(board, guerra, usable, locked);

            Assert.NotNull(action);
            Assert.Equal(Bravoure, action!.Spell.Id);
        }
    }
}
