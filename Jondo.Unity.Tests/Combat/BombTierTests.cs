using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What an air or water bomb takes when it goes off, by its combo, as its spell says:
    /// Tornabombas «Combo I a IV: -1 PM • Combo V a IX: -2 PM • Combo X a XV: -3 PM», Bombas de
    /// agua «Combo I a IV: -0 PA • Combo V a IX: -1 PA • Combo X a XV: -2 PA».
    /// </summary>
    /// <remarks>
    /// Not in the explosion itself: right behind it the bomb casts «Tornado Tymador» (25590) or
    /// «Tormenta Tymadora» (25589), one row per rung of the ladder, each on «the caster holds
    /// that rung's state», casting 20681 / 20680 at the tier's grade. What lands is rolled
    /// against dodge; what is asked is the tier.
    /// </remarks>
    public class BombTierTests
    {
        private const int TornadoTymador = 25590, TormentaTymadora = 25589;

        private static (FightInstance Fight, Fighter Bomb, Fighter Foe) Board(int template)
        {
            var fight = new FightInstance(54000000141, 1);
            var rogue = new Fighter { Id = 54000000141, Breed = 13, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, CellId = 250 };
            var foe = new Fighter { Id = 54000000142, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var bomb = new Fighter
            {
                Id = -2, TeamId = rogue.TeamId, CellId = 300, MaxHP = 90, CurrentHP = 90, IsMonster = true, MonsterId = template,
                GradeIndex = 3, Invocador = rogue.Id, SummonCost = 0, JuegaTurno = false,
            };
            fight.AddPlayer(rogue); fight.AddPlayer(bomb); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            foe.CellId = MapGeometry.PointToCell(x + 1, y);
            return (fight, bomb, foe);
        }

        private static int Asked(int template, int spell, int removal, int level)
        {
            var (fight, bomb, foe) = Board(template);
            while (Combo.LevelOf(bomb) < level)
                EffectEngine.Resolver(fight, bomb, Combo.LadderSpell, 1, bomb, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: bomb.CellId);
            Assert.Equal(level, Combo.LevelOf(bomb));
            var outcomes = EffectEngine.Resolver(fight, bomb, spell, 1, foe, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: bomb.CellId);
            var rows = outcomes.Where(o => o.Sobre == foe && o.Efecto.EffectId == removal).ToList();
            Assert.True(rows.Count <= 1, $"{rows.Count} removal rows on the foe at combo {level}");
            return rows.Count == 0 ? 0 : rows[0].Efecto.DiceNum;
        }

        [Theory]
        [InlineData(1, 1)] [InlineData(4, 1)]
        [InlineData(5, 2)] [InlineData(9, 2)]
        [InlineData(10, 3)] [InlineData(15, 3)]
        public void An_air_bomb_takes_movement_by_its_combo(int level, int mp)
            => Assert.Equal(mp, Asked(3113, TornadoTymador, 1080, level));

        [Theory]
        [InlineData(1, 0)] [InlineData(4, 0)]
        [InlineData(5, 1)] [InlineData(9, 1)]
        [InlineData(10, 2)] [InlineData(15, 2)]
        public void A_water_bomb_takes_action_points_by_its_combo(int level, int ap)
            => Assert.Equal(ap, Asked(3114, TormentaTymadora, 1079, level));
        /// <summary>
        /// The explosion's own «-2 PM» / «-2 PA» row is never cast: in every capture of an air or
        /// water bomb going off (detonador, estopín, impostura, tornabomba and bomba de agua
        /// resilientes) the only removal is the tier's -- one 20681 or 20680 per target, one point
        /// each at a low combo --, never a second one from 13467 or 13462 itself.
        /// </summary>
        [Theory]
        [InlineData(3113, 13467, 1080)]
        [InlineData(3114, 13462, 1079)]
        public void The_explosion_itself_takes_no_points(int template, int explosion, int removal)
        {
            var (fight, bomb, foe) = Board(template);
            EffectEngine.Resolver(fight, bomb, Combo.LadderSpell, 1, bomb, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: bomb.CellId);
            var outcomes = EffectEngine.Resolver(fight, bomb, explosion, 3, foe, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: bomb.CellId);
            var rows = outcomes.Where(o => o.Sobre == foe && o.Efecto.EffectId == removal).ToList();
            string seen = string.Join(", ", rows.Select(o => $"{o.Efecto.EffectId} x{o.Efecto.DiceNum} from {o.HechizoOrigen}"));
            int tier = template == 3113 ? 20681 : 20680;
            if (template == 3113) Assert.True(rows.Count == 1 && rows[0].HechizoOrigen == tier, seen);
            else Assert.True(rows.Count == 0, seen);   // Combo II: a water bomb takes nothing below V.
        }
    }
}
