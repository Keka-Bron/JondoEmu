using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Dungeon 93's boss behaviour (6466 -> 6278): the three summoned fleas each get their own
    /// starting state (334/335/336 by template) and the boss mirrors them through 6326/6327.
    /// </summary>
    [Collection("MapManager")]
    public class PiojoporojoFleaStatesTests
    {
        private const int Behaviour = 6466;
        private const int Room = 239614490;

        [Fact]
        public async Task Each_flea_gets_its_starting_state_after_the_behaviour()
        {
            if (SpellEffects.De(Behaviour, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, Room);
            var player = new Fighter
            {
                Id = 9_970_701, Name = "P", CellId = 383, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "B", IsMonster = true, CellId = 299, Level = 200, MaxHP = 26000,
                CurrentHP = 26000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = 4278,
            };
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            player.CellId = 383; boss.CellId = 299;
            fight.StartFight();
            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);

            foreach (var (id, state) in new[] { (4279, 334), (4280, 335), (4281, 336) })
            {
                var flea = fight.Todos.Single(f => f.MonsterId == id);
                Assert.True(flea.Buffs.TieneEstado(state), $"flea {id} lacks {state}");
                Assert.Single(new[] { 334, 335, 336 }.Where(flea.Buffs.TieneEstado));
            }
            // With the three fleas alive the boss carries all three of their states (6326), which
            // no row of 6327 reads as one or two fleas lost: no 339 or 340 yet.
            Assert.True(new[] { 334, 335, 336 }.All(boss.Buffs.TieneEstado));
            Assert.False(boss.Buffs.TieneEstado(339) || boss.Buffs.TieneEstado(340));
        }
    }
}
