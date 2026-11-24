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
    /// Dungeon 35's 301 and its 2028: a push of one cell on a ring (O1) round the aimed cell, mask
    /// "a,A". The ring leaves the aimed cell out, so aimed at the player it pushed the caster and
    /// the allies round him and never him. It is aimed round the enemy now, and pushes him.
    /// </summary>
    [Collection("MapManager")]
    public class RingPushAimTests
    {
        private const long Room = 107485184;
        private const int Dog = 301, RingPush = 2028;

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task The_ring_push_is_aimed_so_that_the_enemy_is_in_it(int distance)
        {
            if (SpellEffects.De(RingPush, 1).Count == 0) return;   // no spell data
            if (Jondo.Unity.Server.MapManager.Maps.Count == 0) Jondo.Unity.Server.MapManager.Initialize();
            var walkable = Jondo.Unity.Server.MapManager.GetFightWalkable(Room);
            if (walkable.Count == 0) return;
            int mine = walkable.OrderBy(c => c).Skip(walkable.Count / 2).First();
            int dogCell = walkable.Where(c => MapGeometry.Distance(c, mine) == distance).OrderBy(c => c).First();
            int allyCell = walkable.Where(c => c != dogCell && MapGeometry.Distance(c, mine) == 1).OrderBy(c => c).First();

            var fight = new FightInstance(9_999_035, Room, Room);
            var me = new Fighter
            {
                Id = 10, TeamId = 0, CellId = mine, MaxHP = 5000, CurrentHP = 5000, Level = 150,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var dog = new Fighter
            {
                Id = -1, TeamId = 1, CellId = dogCell, MaxHP = 500, CurrentHP = 500, Level = 60, IsMonster = true,
                MonsterId = Dog, MaxAP = 6, CurrentAP = 6, MaxMP = 0, CurrentMP = 0,
            };
            var ally = new Fighter
            {
                Id = -2, TeamId = 1, CellId = allyCell, MaxHP = 500, CurrentHP = 500, Level = 60, IsMonster = true,
                MonsterId = 299, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
            };
            dog.SpellIds.Add(RingPush); dog.SpellGrades[RingPush] = 1;
            fight.AddPlayer(me); fight.AddOpponent(dog); fight.AddOpponent(ally);

            var usable = FightHandler.TacticsOf(dog).Where(s => s.Id == RingPush).ToList();
            var plan = MonsterTactics.PlanTurn(FightHandler.BoardOf(fight), dog, usable);
            var cast = Assert.Single(plan, a => a.Spell?.Id == RingPush);
            Assert.NotEqual(mine, cast.TargetCell);

            await FightHandler.LanzarPorOrdenAsync(null, fight, dog, RingPush, 1, me, cast.TargetCell,
                                                   EffectEngine.AlLanzar, SpellEffects.De(RingPush, 1));
            // Pushed: moved off his cell, or into something and hurt by the crash.
            Assert.True(me.CellId != mine || me.CurrentHP < me.MaxHP);
            Assert.Equal(dogCell, dog.CellId);
        }
    }
}
