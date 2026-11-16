using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Cerdo serrano's Flecha Dolorosa (353): for two turns, «6 a 10 de daños de aire» to
    /// whoever suffers air damage, and of every other element the same. That blow is air damage
    /// too: four of them on one character, one from each serrano of the Dragon Pig's Den's third
    /// room, answered each other down to the depth limit and dealt 134 blows in one Flecha
    /// Localizadora. Each one answers the blow it hooks on, and none answers another.
    /// </summary>
    public class FlechaDolorosaTests
    {
        private const int FlechaDolorosa = 353;

        private static Fighter Character(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 160,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Serrano(long id, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 1600, CurrentHP = 1600, Level = 92,
            MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4, IsMonster = true, MonsterId = 3304,
        };

        private static int Beside(int cell, int dx)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y);
        }

        [Fact]
        public async Task Four_painful_arrows_answer_one_air_blow_once_each()
        {
            var fight = new FightInstance(1, 1);
            var cra = Character(1, 300);
            fight.AddPlayer(cra);
            for (int i = 1; i <= 4; i++)
            {
                var serrano = Serrano(-i, Beside(300, 2 + i));
                fight.AddOpponent(serrano);
                cra.Buffs.ActiveSpells.Add(new Buffs.ActiveSpell
                {
                    Hechizo = FlechaDolorosa, Grado = 1, Lanzador = serrano.Id, CaducaEnRonda = -1,
                });
            }

            // One air blow of exactly 20, from the first serrano.
            var blow = new SpellEffect { EffectId = 98, EffectUid = 900098, DiceNum = 20, DiceSide = 20, Element = 4, TargetMask = "A" };
            await FightHandler.HurtAsync(null, fight, fight.Buscar(-1), 1, 1, cra, cra.CellId, tirada: new[] { blow });

            int lost = 5000 - cra.CurrentHP;
            // The blow and the four answers, 6 to 10 each (the -1 or so of erosion aside).
            Assert.InRange(lost, 20 + 4 * 6 - 4, 20 + 4 * 10);
            Assert.Empty(fight.HooksResolving);
        }
    }
}
