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
    /// Two kinds of spell a monster's tactics weighed at nothing, so they were never cast: the
    /// summon that brings back the last ally to fall, and a pull. Al Howin's Vegetable Patch plays
    /// both (Dofus pour les noobs, «Potager d'Halouine»).
    /// </summary>
    [Collection("MapManager")]
    public class MonsterReviveAndPullTests
    {
        private const int ZombiePlants = 3364, Rakeman = 3361;

        private static Fighter Monster(long id, int cell, int spell)
        {
            var m = new Fighter
            {
                Id = id, TeamId = 1, CellId = cell, MaxHP = 1500, CurrentHP = 1500, Level = 110,
                IsMonster = true, MonsterId = 3306, MaxAP = 8, CurrentAP = 8, MaxMP = 0, CurrentMP = 0,
            };
            m.SpellIds.Add(spell);
            m.SpellGrades[spell] = 1;
            return m;
        }

        private static Fighter Person(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 100,
            MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        private static MonsterTactics.Board BoardOf(FightInstance fight, params Fighter[] fighters) => new()
        {
            Fighters = fighters,
            Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            CanSummon = (c, t, g) => FightHandler.FitsTheSummonLimit(fight, c, t, g),
            LastFallen = caster => fighters.LastOrDefault(m => !m.IsAlive && m.TeamId == caster.TeamId
                                                                 && m.IsMonster && m != caster),
        };

        [Fact]
        public void A_monster_brings_back_its_fallen_ally()
        {
            if (SpellEffects.De(ZombiePlants, 1).Count == 0) return;   // no spell data
            Assert.Contains(SpellEffects.De(ZombiePlants, 1), r => r.EffectId is 780 or 1034);

            var fight = new FightInstance(1, 1);
            var boss = Monster(-1, 300, ZombiePlants);
            var fallen = Monster(-2, 330, 0);
            fallen.CurrentHP = 0;
            var me = Person(10, 160);
            fight.AddPlayer(me); fight.AddMonster(boss); fight.AddMonster(fallen);

            var options = MonsterTactics.Options(BoardOf(fight, boss, fallen, me), boss, FightHandler.TacticsOf(boss));
            Assert.Contains(options, o => o.Spell.Id == ZombiePlants && o.Score > 0);

            // Nobody fallen: nothing to bring back.
            fallen.CurrentHP = 1500;
            options = MonsterTactics.Options(BoardOf(fight, boss, fallen, me), boss, FightHandler.TacticsOf(boss));
            Assert.DoesNotContain(options, o => o.Spell.Id == ZombiePlants);
        }

        [Fact]
        public void A_monster_pulls_a_distant_enemy_in()
        {
            var rows = SpellEffects.De(Rakeman, 1);
            if (rows.Count == 0 || !rows.Any(r => r.EffectId == EffectSupport.Pull)) return;

            var fight = new FightInstance(1, 1);
            var rakeman = Monster(-1, 300, Rakeman);
            var (x, y) = MapGeometry.CellToPoint(300);
            var me = Person(10, MapGeometry.PointToCell(x + 3, y));      // three cells down a line from it
            fight.AddPlayer(me); fight.AddMonster(rakeman);

            var options = MonsterTactics.Options(BoardOf(fight, rakeman, me), rakeman, FightHandler.TacticsOf(rakeman));
            Assert.Contains(options, o => o.Spell.Id == Rakeman && o.Score > 0);
        }
    }
}
