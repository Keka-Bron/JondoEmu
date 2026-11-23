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
    /// The Kimbo (1045) has no invulnerable state: he stands on 400% in every element, so no blow
    /// hurts him. Hit in earth or water he takes the odd state and his Disciple (1088) lays the odd
    /// glyph (1078); if he starts his turn on it, he loses 400% earth and water and gains 800% air
    /// and fire for a turn -- the window his earth and water blows go through.
    /// </summary>
    [Collection("MapManager")]
    public class KimboGlyphResistanceTests
    {
        private const int Kimbo = 1045, Disciple = 1088, OddGlyph = 1078;

        [Fact]
        public async Task The_odd_glyph_opens_him_to_earth_and_water()
        {
            if (!SpellEffects.De(OddGlyph, 1).Any(row => row.EffectId == 216)) return;   // no spell data
            MobSpawnManager.EnsureMonsterData();
            var data = MobSpawnManager.GetMonsterData(Kimbo);
            if (data == null) return;

            var fight = new FightInstance(9_931_045, 1);
            fight.AddPlayer(new Fighter { Id = 7412, TeamId = 0, CellId = 200, MaxHP = 5000, CurrentHP = 5000, Level = 200 });
            var boss = FightHandler.BuildMonsterFighter(new MobSpawnManager.MobMember { Monster = data, GradeIndex = 1 }, -1, 300);
            var disciple = new Fighter { Id = -2, TeamId = 1, CellId = MapGeometry.GetNeighbors(300).First(), MaxHP = 190,
                                         CurrentHP = 190, IsMonster = true, MonsterId = Disciple, Level = 100 };
            fight.AddMonster(boss);
            fight.AddMonster(disciple);
            fight.StartFight();

            int round = fight.RoundNumber;
            Assert.True(FightHandler.ResistenciaDeLaVictima(boss, ElementType.Earth, round) >= 100);
            Assert.True(FightHandler.ResistenciaDeLaVictima(boss, ElementType.Water, round) >= 100);

            // What the glyph does to him when he starts his turn on it.
            await FightHandler.AplicarEfectosAsync(null, fight, disciple, OddGlyph, 1, boss, EffectEngine.AlLanzar, boss.CellId);

            Assert.True(FightHandler.ResistenciaDeLaVictima(boss, ElementType.Earth, round) < 100);
            Assert.True(FightHandler.ResistenciaDeLaVictima(boss, ElementType.Water, round) < 100);
            Assert.True(FightHandler.ResistenciaDeLaVictima(boss, ElementType.Air, round) >= 100);
            Assert.True(boss.IsAlive);
        }
    }
}
