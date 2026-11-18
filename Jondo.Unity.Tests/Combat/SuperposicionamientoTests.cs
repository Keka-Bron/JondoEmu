using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Superposicionamiento, the admins' placing spell, in two casts: the first marks a fighter
    /// (state 262), the second, aimed at a cell, has him cast 30196 there -- "teleport to the aimed
    /// cell", mode 63. Read as Rempotage's 63, which never takes the aimed cell itself but the first
    /// free one on the line back to the one moving, he landed a cell short of it, or not at all
    /// when it was next to him.
    /// </summary>
    [Collection("MapManager")]
    public class SuperposicionamientoTests
    {
        private const int Superposicionamiento = 30044, Marked = 262;

        [Theory]
        [InlineData(356)]   // far away
        [InlineData(314)]   // next to him
        public async Task The_marked_fighter_lands_on_the_cell_aimed_at(int aimed)
        {
            var admin = new Fighter
            {
                Id = 9_970_302, Name = "A", CellId = 250, Level = 200, MaxHP = 3000, CurrentHP = 3000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 5,
            };
            var monster = new Fighter
            {
                Id = -1, Name = "M", IsMonster = true, CellId = 300, Level = 50, MaxHP = 900, CurrentHP = 900,
                MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, MonsterId = 494,
            };
            // A board of its own, with no ground data.
            var fight = new FightInstance(4107, 990_004_107);
            fight.AddPlayer(admin); admin.CellId = 250;
            fight.AddMonster(monster); monster.CellId = 300;
            Assert.Equal(1, Jondo.Unity.World.Maps.MapGeometry.Distance(300, 314));

            await FightHandler.CastEffectsAsync(null, fight, admin, Superposicionamiento, 1, monster, 300, false,
                                                EffectEngine.EfectosSorteados(Superposicionamiento, 1, false));
            Assert.True(monster.Buffs.TieneEstado(Marked));

            await FightHandler.CastEffectsAsync(null, fight, admin, Superposicionamiento, 1, null, aimed, false,
                                                EffectEngine.EfectosSorteados(Superposicionamiento, 1, false));

            Assert.Equal(aimed, monster.CellId);
            Assert.False(monster.Buffs.TieneEstado(Marked));
            Assert.Equal(250, admin.CellId);
        }
    }
}
