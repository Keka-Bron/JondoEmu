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
    /// Grohlum's Bravura (23884) holds "792 on g under X": when one of his allies falls, the fallen
    /// one casts its grade 2, a 141 on the enemies in a straight cross round it, and Grohlum
    /// (F2942) takes 2 MP and his damage bonuses. The watch on the others only answered state
    /// changes, and the attitude's own rows are never armed, so no death ever set it off.
    /// </summary>
    [Collection("MapManager")]
    public class GrohlumBravuraTests
    {
        private const int Bravura = 23884, Grohlum = 2942;

        private static Fighter Player(int id, int cell) => new()
        {
            Id = id, Name = "P", CellId = cell, Level = 200, MaxHP = 5000, CurrentHP = 5000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        private static Fighter Monster(long id, int monsterId, int cell) => new()
        {
            Id = id, Name = "M", IsMonster = true, CellId = cell, Level = 190, MaxHP = 20000,
            CurrentHP = 20000, TeamId = 1, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            MonsterId = monsterId,
        };

        [Fact]
        public async Task A_fallen_ally_takes_the_enemies_lined_up_with_it()
        {
            if (!SpellEffects.De(Bravura, 2).Any(row => row.EffectId == 141)) return;   // no spell data
            var (x, y) = MapGeometry.CellToPoint(300);
            int lined = MapGeometry.PointToCell(x + 3, y);
            int aside = MapGeometry.PointToCell(x + 2, y + 1);
            var inLine = Player(9_970_911, lined);
            var offLine = Player(9_970_912, aside);
            var grohlum = Monster(-1, Grohlum, 400);
            var ally = Monster(-2, 2941, 300);
            var fight = new FightInstance(Grohlum, 990_002_942);
            fight.AddPlayer(inLine);
            fight.AddPlayer(offLine);
            fight.AddMonster(grohlum);
            fight.AddMonster(ally);
            inLine.CellId = lined; offLine.CellId = aside; grohlum.CellId = 400; ally.CellId = 300;
            Assert.Equal(1, ExpeditionModifiers.Give(fight, new ExpeditionModifiers.Modifier(204, Grohlum, Bravura, "bravura"),
                                                     new[] { Grohlum }));
            fight.StartFight();
            await FightHandler.AplicarEfectosAsync(null, fight, grohlum, Bravura, 1, grohlum,
                                                   EffectEngine.AlLanzar, grohlum.CellId, armar: false);

            await FightHandler.TestKillAsync(null, fight, ally, inLine);
            Assert.False(ally.IsAlive);
            Assert.False(inLine.IsAlive);
            Assert.True(offLine.IsAlive);
            Assert.True(grohlum.IsAlive);
            Assert.Contains(grohlum.Buffs.Puestos, buff => buff.EffectId == 128);    // +2 MP
            Assert.Contains(grohlum.Buffs.Puestos, buff => buff.EffectId == 1171);   // damage bonus
        }
    }
}
