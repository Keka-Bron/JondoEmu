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
    /// A monster's own spell runs its rows in the order they are written. When a row before the
    /// blows moves the target, the blows land where he went, as they do after a teleport.
    /// </summary>
    /// <remarks>
    /// The Crucible's Berserkofre casts «Daemp» (4563): "push 2, -100 power, 26 to 30 fire". In a
    /// live fight the push and the power came off the target, and the fire hit the cell he had
    /// been pushed away from, with nobody on it.
    /// </remarks>
    [Collection("MapManager")]
    public class CrucibleBlowsTests
    {
        private const int Daemp = 4563;

        private static bool HasData => SpellEffects.De(Daemp, 1).Any(row => row.EffectId == 5)
                                       && SpellEffects.De(Daemp, 1).Any(row => row.EffectId == 99);

        [Fact]
        public async Task The_fire_after_the_push_lands_on_the_pushed_target()
        {
            if (!HasData) return;
            const int casterCell = 330;
            int targetCell = MapGeometry.GetNeighbors(casterCell).First();
            var (cx, cy) = MapGeometry.CellToPoint(casterCell);
            var (tx, ty) = MapGeometry.CellToPoint(targetCell);
            int landing = MapGeometry.PointToCell(tx + 2 * (tx - cx), ty + 2 * (ty - cy));
            Assert.True(landing >= 0, "the push stays on the board");

            var fight = new FightInstance(1, 1);
            var character = new Fighter
            {
                Id = 10, CellId = targetCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            var berserkofre = new Fighter
            {
                Id = -1, TeamId = 1, CellId = casterCell, MaxHP = 3000, CurrentHP = 3000, Level = 120,
                IsMonster = true, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
            };
            fight.AddPlayer(character);
            fight.AddMonster(berserkofre);
            fight.StartFight();

            await FightHandler.LanzarPorOrdenAsync(null, fight, berserkofre, Daemp, 1, character, targetCell,
                                                   EffectEngine.AlLanzar, SpellEffects.De(Daemp, 1).ToList());

            Assert.Equal(landing, character.CellId);
            Assert.True(character.CurrentHP < 5000, "the fire reached him where the push left him");
        }

        /// <summary>
        /// Missiz Frizz's forge's «Vientraumatismo» (3493): "swap places, water steal, Pesante on the
        /// target". After the swap the aimed cell holds the caster; the state is the target's still.
        /// </summary>
        [Fact]
        public async Task The_state_after_the_swap_lands_on_the_swapped_target()
        {
            const int Vientraumatismo = 3493;
            var rows = SpellEffects.De(Vientraumatismo, 1).ToList();
            var state = rows.FirstOrDefault(row => row.EffectId == 950);
            if (!rows.Any(row => row.EffectId == 8) || state == null) return;
            int stateId = state.Value > 0 ? state.Value : state.DiceSide;

            const int casterCell = 330;
            int targetCell = MapGeometry.GetNeighbors(MapGeometry.GetNeighbors(casterCell).First()).First(c => c != casterCell
                                                                                                    && !MapGeometry.GetNeighbors(casterCell).Contains(c));
            var fight = new FightInstance(1, 1);
            var character = new Fighter
            {
                Id = 10, CellId = targetCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            var monster = new Fighter
            {
                Id = -1, TeamId = 1, CellId = casterCell, MaxHP = 3000, CurrentHP = 3000, Level = 200,
                IsMonster = true, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
            };
            fight.AddPlayer(character);
            fight.AddMonster(monster);
            fight.StartFight();

            await FightHandler.LanzarPorOrdenAsync(null, fight, monster, Vientraumatismo, 1, character, targetCell,
                                                   EffectEngine.AlLanzar, rows);

            Assert.Equal(casterCell, character.CellId);
            Assert.Equal(targetCell, monster.CellId);
            Assert.True(character.Buffs.TieneEstado(stateId), $"state {stateId} on the swapped target");
            Assert.False(monster.Buffs.TieneEstado(stateId), "not on the caster now on the aimed cell");
        }
    }
}
